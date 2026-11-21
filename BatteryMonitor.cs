using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace MouseBattery;

/// <summary>设备当前的连接方式。</summary>
internal enum Transport
{
    Bluetooth,
    Wireless24G,
}

internal sealed class BatteryReading
{
    public int? Percent { get; init; }
    /// <summary>显示名：有自定义名称时用别名，否则等于 OriginalName。</summary>
    public string DeviceName { get; init; } = "未找到设备";
    /// <summary>设备在系统里的原始名称。</summary>
    public string OriginalName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public DateTime? Stamp { get; init; }
    public string Source { get; init; } = "-";
    public bool Connected { get; init; }
    public bool Subscribed { get; init; }
    /// <summary>设备是否在上报充电中。只有 2.4G 协议能读到，其它通道一律 false。</summary>
    public bool Charging { get; init; }
    public DeviceKind Kind { get; init; } = DeviceKind.Generic;
    public Transport Transport { get; init; } = Transport.Bluetooth;
    public string? Error { get; init; }

    public double? AgeMinutes => Stamp is null ? null : (DateTime.Now - Stamp.Value).TotalMinutes;
}

/// <summary>
/// 电量采集器，多设备版。每台设备各自维护一条 GATT 通道：
///   1) BLE GATT 0x180F/0x2A19 —— 主动读取 + Notify 订阅，能拿到最快的一次上报；
///   2) Windows 设备属性 DEVPKEY_Bluetooth_Battery —— 系统自身缓存，GATT 不可用时的兜底。
/// 只上报"有电量"的设备：既没有系统缓存值、也没有 GATT 值的设备不会出现在结果里。
/// </summary>
internal sealed class BatteryMonitor : IDisposable
{
    private static readonly Guid BatteryServiceUuid = new("0000180F-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BatteryLevelUuid = new("00002A19-0000-1000-8000-00805F9B34FB");

    private const int GattTimeoutMs = 9000;

    /// <summary>单台设备的 GATT 通道状态。</summary>
    private sealed class Channel
    {
        public BluetoothLEDevice? Device;
        public GattCharacteristic? Characteristic;
        public TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? Handler;
        public bool Subscribed;
        public int? Percent;
        public DateTime? Stamp;
        public int FailCount;
        public DateTime NextAttempt = DateTime.MinValue;
    }

    private readonly AppSettings _settings;
    private readonly SynchronizationContext? _ui;
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);

    public BatteryMonitor(AppSettings settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current;

        // 先恢复上次的 2.4G 读数：开机自启时键盘往往还在休眠，读不到新值
        LoadWirelessCache();
    }

    /// <summary>某台设备的电量发生变化时触发（已切回 UI 线程）。</summary>
    public event Action<BatteryReading>? DeviceUpdated;

    public string LogPath { get; } = Path.Combine(AppContext.BaseDirectory, "MouseBattery.log");

    public void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败无关紧要
        }
    }

    // ---------------------------------------------------------------- 采集

    /// <summary>轮询全部设备，返回有电量的每一台。</summary>
    /// <param name="forceGatt">用户主动刷新：忽略失败退避，立刻重试 GATT。</param>
    /// <param name="allowGatt">false 时只读系统缓存，用于启动时先把图标点亮。</param>
    public async Task<List<BatteryReading>> PollAllAsync(bool forceGatt, bool allowGatt = true)
    {
        List<BleDevice> all = DevProp.EnumBleDevices();
        PruneChannels(all);

        List<BleDevice> visible = SelectDevices(all);

        // 并行探测：设备离线时单台要等满 9 秒超时，串行会把界面拖住
        Task<BatteryReading>[] tasks = visible.Select(d => BuildReadingAsync(d, forceGatt, allowGatt)).ToArray();
        BatteryReading[] results = await Task.WhenAll(tasks);

        // 2.4G 接收器走私有 HID，和蓝牙是两条独立来源，这里合并成一份
        List<BatteryReading> list = results.ToList();

        // 经典蓝牙（BR/EDR）不在 BLE 枚举里，电量要从 PnP 属性表补一条
        await AppendClassicAsync(list);

        await Task.Run(() => MergeWireless(list));

        return list
            .OrderBy(r => (int)r.Kind)
            .ThenBy(r => r.DeviceName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>上一次经典蓝牙的读取台数，用来避免每轮都写同一条日志。</summary>
    private int _classicCount = -1;

    /// <summary>
    /// 补上经典蓝牙设备。
    /// 这类设备（耳机、音箱）没有 GATT 电池服务，电量以 PnP 属性形式挂在 HFP 网关节点上，
    /// 读它不需要打开设备、不需要建立连接，属于零打扰的被动读取。
    ///
    /// 必须过滤连接状态：BTHENUM 设备树会把**所有配对过的设备永久保留**，
    /// 不过滤就会把几个月前配对、早已关机的耳机当成在线设备列出来（还会拿它的旧电量去算最低值）。
    /// 同一台设备如果 BLE 侧已经上报，以 BLE 为准（那边是实时值）。
    /// </summary>
    private async Task AppendClassicAsync(List<BatteryReading> list)
    {
        List<ClassicDevice> classic;
        try
        {
            classic = DevProp.EnumClassicDevices();
        }
        catch (Exception ex)
        {
            Log($"[经典蓝牙] 枚举失败: {ex.GetType().Name} {ex.Message}");
            return;
        }

        ClassicDevice[] candidates = classic.Where(d => d.Battery.HasValue).ToArray();
        if (candidates.Length == 0) return;

        // 一次问系统「现在有哪些经典蓝牙是连着的」，比逐台查 ConnectionStatus 可靠
        HashSet<string> connected = await ConnectedClassicAsync();

        var known = new HashSet<string>(list.Select(r => NormalizeAddress(r.Address)), StringComparer.OrdinalIgnoreCase);
        int added = 0;

        for (int i = 0; i < candidates.Length; i++)
        {
            ClassicDevice device = candidates[i];

            // 双重判据：系统说在线，或它刚刚上报过电量。
            // 后者是兜底——个别音频设备的连接状态查不到，
            // 但属性时间戳不会骗人：只有设备真的在连着，系统才会更新它。
            bool fresh = device.Stamp is DateTime stamp && (DateTime.Now - stamp).TotalMinutes < 10;
            bool online = connected.Contains(device.Address) || fresh;
            if (!online) continue;

            if (!string.IsNullOrEmpty(_settings.DeviceAddress)
                && !string.Equals(device.Address, _settings.DeviceAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!known.Add(NormalizeAddress(device.Address))) continue;

            string name = _settings.DeviceAliases.TryGetValue(device.Address, out string? alias)
                          && !string.IsNullOrWhiteSpace(alias)
                ? alias.Trim()
                : device.Name;

            list.Add(new BatteryReading
            {
                Percent = device.Battery,
                DeviceName = name,
                OriginalName = device.Name,
                Address = device.DisplayAddress,
                Stamp = device.Stamp,
                Source = "经典蓝牙属性",
                Connected = true,
                Kind = device.Kind,
                Transport = Transport.Bluetooth,
            });

            added++;
        }

        if (added != _classicCount)
        {
            _classicCount = added;

            // 只在台数变化时打明细，避免每分钟刷同样的行
            foreach (ClassicDevice d in candidates)
            {
                bool isOnline = connected.Contains(d.Address)
                                || (d.Stamp is DateTime s && (DateTime.Now - s).TotalMinutes < 10);
                Log($"[经典蓝牙] {d.Name}: {d.Battery}% 在线={isOnline}");
            }

            Log($"[经典蓝牙] 在线 {added} 台（系统报告已连接 {connected.Count} 台，枚举到 {candidates.Length} 台有电量属性）");
        }
    }

    /// <summary>
    /// 当前处于已连接状态的经典蓝牙设备地址（12 位、大写）。
    /// 用的是系统自己的连接状态集合，和「设置 → 蓝牙」里显示的一致；
    /// 只做枚举，不做服务发现、不建立 GATT 会话，因此不会打扰设备。
    /// 2.5 秒拿不到结果就按「都没连」处理——宁可少显示，也不把关机设备的旧电量当成实时值。
    /// </summary>
    private async Task<HashSet<string>> ConnectedClassicAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var op = DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));

            Windows.Devices.Enumeration.DeviceInformationCollection? all = await WithTimeout(op, 2500);
            if (all == null) return set;

            foreach (DeviceInformation info in all)
            {
                // AEP 的 Id 形如 Bluetooth#Bluetooth<适配器 MAC>-<设备 MAC>，
                // 取**最后一个**才是设备本身；取第一个会得到适配器地址，永远匹配不上。
                MatchCollection macs = Regex.Matches(info.Id, "([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}");
                if (macs.Count == 0) continue;

                set.Add(macs[^1].Value.Replace(":", string.Empty).ToUpperInvariant());
            }
        }
        catch (Exception ex)
        {
            // 拿不到就当没有，不影响其它通道
            Log($"[经典蓝牙] 连接状态查询失败: {ex.GetType().Name} {ex.Message}");
        }

        return set;
    }

    /// <summary>地址比较前统一去掉冒号：BLE 侧存 12 位，显示用带冒号的格式。</summary>
    private static string NormalizeAddress(string address) =>
        address.Replace(":", string.Empty).Replace("-", string.Empty).Trim();

    /// <summary>上次成功读到的 2.4G 电量。查询偶发失败时用它兜底，避免界面跳回蓝牙的旧缓存。</summary>
    private readonly Dictionary<DeviceKind, (int Percent, DateTime Stamp)> _wirelessLast = new();

    /// <summary>
    /// 2.4G 读数缓存文件。键盘空闲后会进入休眠、不再应答查询，
    /// 而程序常常是在开机时先启动的——那时缓存为空，界面就只能显示蓝牙那条一天前的旧值。
    /// 所以把上次读到的值与时间落盘，重启后接着用。
    /// </summary>
    private string WirelessCachePath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath) ?? AppContext.BaseDirectory, "wireless-cache.json");

    private sealed class CachedSample
    {
        [System.Text.Json.Serialization.JsonPropertyName("p")] public int Percent { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("t")] public DateTime Stamp { get; set; }
    }

    private void LoadWirelessCache()
    {
        try
        {
            if (!File.Exists(WirelessCachePath)) return;

            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CachedSample>>(File.ReadAllText(WirelessCachePath));
            if (saved == null) return;

            foreach (KeyValuePair<string, CachedSample> pair in saved)
            {
                if (!Enum.TryParse(pair.Key, out DeviceKind kind)) continue;
                if (pair.Value.Percent is <= 0 or > 100) continue;
                _wirelessLast[kind] = (pair.Value.Percent, pair.Value.Stamp);
            }
        }
        catch
        {
            // 缓存坏了就当作没有，不影响启动
        }
    }

    private void SaveWirelessCache()
    {
        try
        {
            var payload = new Dictionary<string, CachedSample>();
            foreach (KeyValuePair<DeviceKind, (int Percent, DateTime Stamp)> pair in _wirelessLast)
            {
                payload[pair.Key.ToString()] = new CachedSample { Percent = pair.Value.Percent, Stamp = pair.Value.Stamp };
            }

            string path = WirelessCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 把 2.4G 读数并进来。同一台设备（按类型判断）在两种连接方式下只保留一条。
    ///
    /// 关键点：**接收器存在就说明设备在 2.4G 上**。查询失败只是这一次没读到，
    /// 应该沿用上次的读数（连带它的时间戳），而不是退回蓝牙那条可能已经放置一天的缓存。
    /// 只有当蓝牙侧的数据比 2.4G 缓存更新时，才认为设备真的切回蓝牙了。
    /// </summary>
    private void MergeWireless(List<BatteryReading> list)
    {
        List<Wireless24G.Reading> wireless;
        try
        {
            wireless = Wireless24G.Poll();
        }
        catch
        {
            return;
        }

        foreach (Wireless24G.Reading w in wireless)
        {
            if (w.Percent is int fresh)
            {
                bool changed = !_wirelessLast.TryGetValue(w.Kind, out (int Percent, DateTime Stamp) previous) || previous.Percent != fresh;
                _wirelessLast[w.Kind] = (fresh, w.Stamp ?? DateTime.Now);
                if (changed) SaveWirelessCache();
            }

            int index = list.FindIndex(r => r.Kind == w.Kind);
            BatteryReading? bt = index >= 0 ? list[index] : null;

            bool has2G = w.Percent.HasValue || _wirelessLast.ContainsKey(w.Kind);
            if (!has2G)
            {
                // 接收器在，但一次都没读到过（刚启动、设备还在唤醒）。
                // 这时不做任何加工：沿用蓝牙那条原值，角标也保持蓝色，
                // 免得出现"标着 2.4G、数值其实是蓝牙旧值"的假象。
                continue;
            }

            (int percent, DateTime stamp) = w.Percent is int value
                ? (value, w.Stamp ?? DateTime.Now)
                : _wirelessLast[w.Kind];

            if (bt == null)
            {
                list.Add(new BatteryReading
                {
                    Percent = percent,
                    DeviceName = w.Name,
                    OriginalName = w.Name,
                    Address = w.Address,
                    Stamp = stamp,
                    Source = "2.4G 私有协议",
                    Connected = true,
                    Charging = w.Charging,
                    Kind = w.Kind,
                    Transport = Transport.Wireless24G,
                });
                continue;
            }

            // 蓝牙侧的数据如果比 2.4G 缓存还新，说明设备现在确实连的是蓝牙
            bool bluetoothIsLive = bt.Stamp is DateTime btStamp && btStamp > stamp;
            if (!w.Percent.HasValue && bluetoothIsLive)
            {
                list[index] = WithTransport(bt, Transport.Bluetooth);
                Log($"[2.4G] {bt.DeviceName}: 读取失败，蓝牙侧数据更新，按蓝牙处理");
                continue;
            }

            list[index] = new BatteryReading
            {
                Percent = percent,
                DeviceName = bt.DeviceName,     // 名字沿用蓝牙那条，切换模式时列表不跳名称
                OriginalName = bt.OriginalName,
                Address = bt.Address,
                Stamp = stamp,
                Source = w.Percent.HasValue ? "2.4G 私有协议" : "2.4G 私有协议（上次读数）",
                Connected = true,
                Subscribed = bt.Subscribed,
                Charging = w.Percent.HasValue && w.Charging,
                Kind = bt.Kind,
                Transport = Transport.Wireless24G,
            };

            Log($"[2.4G] {bt.DeviceName}: {percent}%{(w.Charging ? " 充电中" : "")}{(w.Percent.HasValue ? "" : "（本轮未响应，沿用上次）")} (蓝牙 {bt.Percent?.ToString() ?? "无"}%)");
        }
    }

    private static BatteryReading WithTransport(BatteryReading source, Transport transport) => new()
    {
        Percent = source.Percent,
        DeviceName = source.DeviceName,
        OriginalName = source.OriginalName,
        Address = source.Address,
        Stamp = source.Stamp,
        Source = source.Source,
        Connected = source.Connected,
        Subscribed = source.Subscribed,
        Charging = source.Charging,
        Kind = source.Kind,
        Transport = transport,
    };

    /// <summary>过滤出要显示的设备：有系统缓存电量，或已有 GATT 读数。</summary>
    private List<BleDevice> SelectDevices(List<BleDevice> all)
    {
        IEnumerable<BleDevice> q = all;

        // 配置里锁定过 MAC 就只认它（留空 = 全部显示）
        if (!string.IsNullOrEmpty(_settings.DeviceAddress))
        {
            q = q.Where(d => string.Equals(d.Address, _settings.DeviceAddress, StringComparison.OrdinalIgnoreCase));
        }

        var list = new List<BleDevice>();
        foreach (BleDevice d in q)
        {
            if (d.Battery.HasValue || HasGattValue(d.Address)) list.Add(d);
        }
        return list;
    }

    private bool HasGattValue(string address)
    {
        lock (_gate)
        {
            return _channels.TryGetValue(address, out Channel? ch) && ch.Percent.HasValue;
        }
    }

    private async Task<BatteryReading> BuildReadingAsync(BleDevice target, bool forceGatt, bool allowGatt)
    {
        Channel ch = GetOrCreateChannel(target.Address);

        // 没有订阅成功过、且退避时间已到，才去连 GATT。设备休眠时反复连会白等 9 秒超时，
        // 所以失败后按 20s → 40s → 80s … 退避，最多 5 分钟。
        bool wantGatt = allowGatt && _settings.UseGatt && (forceGatt || !ch.Subscribed);
        bool timeReached = forceGatt || DateTime.Now >= ch.NextAttempt;

        if (wantGatt && timeReached)
        {
            bool ok = await TryGattAsync(target, ch);
            if (ok)
            {
                ch.FailCount = 0;
                ch.NextAttempt = DateTime.MinValue;
            }
            else
            {
                ch.FailCount++;
                int seconds = Math.Min(300, 20 * (int)Math.Pow(2, Math.Min(ch.FailCount - 1, 4)));
                ch.NextAttempt = DateTime.Now.AddSeconds(seconds);
            }
        }

        int? percent;
        string source;
        DateTime? stamp;

        lock (_gate)
        {
            if (ch.Percent is int gp && ch.Stamp is DateTime gt && (DateTime.Now - gt).TotalHours < 6)
            {
                percent = gp;
                source = ch.Subscribed ? "GATT 实时订阅" : "GATT 主动读取";
                stamp = gt;
            }
            else
            {
                percent = target.Battery;
                source = "系统属性";
                stamp = target.Stamp;
            }
        }

        bool connected = ch.Device?.ConnectionStatus == BluetoothConnectionStatus.Connected
                         || target.Battery.HasValue;

        return new BatteryReading
        {
            Percent = percent,
            DeviceName = DisplayNameOf(target),
            OriginalName = target.Name,
            Address = target.DisplayAddress,
            Stamp = stamp,
            Source = source,
            Connected = connected,
            Subscribed = ch.Subscribed,
            Kind = target.Kind,
            Error = percent == null ? "设备未上报电量" : null,
        };
    }

    /// <summary>有自定义名称就用别名，否则用系统里的名字。</summary>
    private string DisplayNameOf(BleDevice device)
    {
        if (_settings.DeviceAliases.TryGetValue(device.Address, out string? alias)
            && !string.IsNullOrWhiteSpace(alias))
        {
            return alias.Trim();
        }
        return device.Name;
    }

    private Channel GetOrCreateChannel(string address)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(address, out Channel? ch))
            {
                ch = new Channel();
                _channels[address] = ch;
            }
            return ch;
        }
    }

    /// <summary>设备从系统里消失后，回收它的 GATT 连接。</summary>
    private void PruneChannels(List<BleDevice> present)
    {
        var alive = new HashSet<string>(present.Select(d => d.Address), StringComparer.OrdinalIgnoreCase);
        var dead = new List<Channel>();

        lock (_gate)
        {
            foreach (string key in _channels.Keys.ToList())
            {
                if (alive.Contains(key)) continue;
                dead.Add(_channels[key]);
                _channels.Remove(key);
            }
        }

        foreach (Channel ch in dead) ReleaseChannel(ch);
    }

    // ---------------------------------------------------------------- GATT

    private async Task<bool> TryGattAsync(BleDevice target, Channel ch)
    {
        try
        {
            if (ch.Device == null || ch.Device.BluetoothAddress != target.AddressValue)
            {
                ReleaseChannel(ch);
                var op = BluetoothLEDevice.FromBluetoothAddressAsync(target.AddressValue);
                ch.Device = await WithTimeout(op, GattTimeoutMs);
            }

            if (ch.Device == null)
            {
                Log($"[GATT] {Label(target)}: FromBluetoothAddressAsync 返回 null，设备可能已休眠");
                return false;
            }

            GattCharacteristic? characteristic = ch.Characteristic;

            // 已经拿到特征就跳过服务发现。每轮都做一次 Uncached 发现会在链路上来回跑，
            // 把设备从省电状态拽起来——蓝牙鼠标会表现出"周期性断联一下"
            if (characteristic == null)
            {
                var svcResult = await WithTimeout(
                    ch.Device.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached),
                    GattTimeoutMs);

                if (svcResult?.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                {
                    Log($"[GATT] {Label(target)}: 电池服务不可用: {svcResult?.Status}");
                    return false;
                }

                var service = svcResult.Services[0];
                var chResult = await WithTimeout(
                    service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Uncached),
                    GattTimeoutMs);

                if (chResult?.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
                {
                    Log($"[GATT] {Label(target)}: 电量特征不可用: {chResult?.Status}");
                    return false;
                }

                characteristic = chResult.Characteristics[0];
                ch.Characteristic = characteristic;
            }

            // 已经订阅上就靠推送拿值，不再主动读——主动读同样会打扰设备。
            // 只有没订阅、或数据已经 10 分钟没动过时，才补一次主动读。
            bool stale = ch.Stamp is null || (DateTime.Now - ch.Stamp.Value).TotalMinutes > 10;
            if (!ch.Subscribed || stale)
            {
                var readResult = await WithTimeout(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), GattTimeoutMs);
                if (readResult?.Status == GattCommunicationStatus.Success)
                {
                    byte value = ReadByte(readResult.Value);
                    // 0 当无效：休眠前的伪读数，真实 0% 早该报过低电量了
                    if (value is > 0 and <= 100)
                    {
                        lock (_gate)
                        {
                            ch.Percent = value;
                            ch.Stamp = DateTime.Now;
                        }
                        Log($"[GATT] {Label(target)}: 主动读取 = {value}%");
                    }
                }
                else
                {
                    Log($"[GATT] {Label(target)}: 读取失败: {readResult?.Status}");
                }
            }

            // 订阅推送，这是「实时」的来源
            if (!ch.Subscribed)
            {
                var status = await WithTimeout(
                    characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify),
                    GattTimeoutMs);

                if (status == GattCommunicationStatus.Success)
                {
                    TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
                        (_, args) => OnValueChanged(target, ch, args);
                    characteristic.ValueChanged += handler;

                    ch.Handler = handler;
                    ch.Subscribed = true;
                    Log($"[GATT] {Label(target)}: 已订阅 Notify 推送");
                }
                else
                {
                    Log($"[GATT] {Label(target)}: 订阅失败: {status}（降级为轮询）");
                }
            }

            return ch.Percent.HasValue || ch.Subscribed;
        }
        catch (Exception ex)
        {
            Log($"[GATT] {Label(target)}: 异常 {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>日志里用名字 + 地址后四位，避免两只同名鼠标分不清。</summary>
    private static string Label(BleDevice device) =>
        device.Address.Length >= 4
            ? $"{device.Name}[{device.Address[^4..].ToUpperInvariant()}]"
            : device.Name;

    private void OnValueChanged(BleDevice target, Channel ch, GattValueChangedEventArgs args)
    {
        try
        {
            byte value = ReadByte(args.CharacteristicValue);
            if (value is <= 0 or > 100) return;

            lock (_gate)
            {
                if (ch.Percent == value) return;
                ch.Percent = value;
                ch.Stamp = DateTime.Now;
            }

            Log($"[GATT] {Label(target)}: 推送更新 = {value}%");

            Publish(new BatteryReading
            {
                Percent = value,
                DeviceName = DisplayNameOf(target),
                OriginalName = target.Name,
                Address = target.DisplayAddress,
                Stamp = DateTime.Now,
                Source = "GATT 实时订阅",
                Connected = true,
                Subscribed = true,
                Kind = target.Kind,
            });
        }
        catch (Exception ex)
        {
            Log($"[GATT] {target.Name}: 推送处理异常 {ex.Message}");
        }
    }

    private static byte ReadByte(IBuffer buffer)
    {
        if (buffer.Length < 1) return 255;
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes[0];
    }

    private static async Task<T?> WithTimeout<T>(Windows.Foundation.IAsyncOperation<T> operation, int milliseconds)
    {
        Task<T> task = operation.AsTask();
        Task finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (finished != task) return default;
        return await task.ConfigureAwait(false);
    }

    private void ReleaseChannel(Channel ch)
    {
        try
        {
            if (ch.Characteristic != null && ch.Handler != null)
            {
                ch.Characteristic.ValueChanged -= ch.Handler;
            }
        }
        catch
        {
            // ignore
        }

        ch.Characteristic = null;
        ch.Handler = null;
        ch.Subscribed = false;

        try { ch.Device?.Dispose(); } catch { /* ignore */ }
        ch.Device = null;
    }

    private void Publish(BatteryReading reading)
    {
        if (_ui != null) _ui.Post(_ => DeviceUpdated?.Invoke(reading), null);
        else DeviceUpdated?.Invoke(reading);
    }

    public void Dispose()
    {
        List<Channel> all;
        lock (_gate)
        {
            all = _channels.Values.ToList();
            _channels.Clear();
        }
        foreach (Channel ch in all) ReleaseChannel(ch);
    }
}
