using Windows.Devices.Bluetooth;
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
        await Task.Run(() => MergeWireless(list));

        return list
            .OrderBy(r => (int)r.Kind)
            .ThenBy(r => r.DeviceName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 把 2.4G 读数并进来。同一台设备（按类型判断）在两种连接方式下只保留一条：
    /// 谁的数据更新用谁的电量，名字沿用已有的那条，避免切换模式时列表里出现两台。
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
            int index = list.FindIndex(r => r.Kind == w.Kind);

            if (index < 0)
            {
                list.Add(new BatteryReading
                {
                    Percent = w.Percent,
                    DeviceName = w.Name,
                    OriginalName = w.Name,
                    Address = w.Address,
                    Stamp = w.Stamp,
                    Source = "2.4G 私有协议",
                    Connected = true,
                    Kind = w.Kind,
                    Transport = Transport.Wireless24G,
                });
                continue;
            }

            BatteryReading bt = list[index];
            // 名字和别名沿用蓝牙那条（"VGN V98 BT1" 比 "Wireless Gaming Keyboard" 好认），
            // 但电量和时间用更新的那份
            bool wirelessFresher = bt.Stamp is null || w.Stamp >= bt.Stamp;

            list[index] = new BatteryReading
            {
                Percent = wirelessFresher ? w.Percent : bt.Percent,
                DeviceName = bt.DeviceName,
                OriginalName = bt.OriginalName,
                Address = bt.Address,
                Stamp = wirelessFresher ? w.Stamp : bt.Stamp,
                Source = wirelessFresher ? "2.4G 私有协议" : bt.Source,
                Connected = true,
                Subscribed = bt.Subscribed,
                Kind = bt.Kind,
                Transport = wirelessFresher ? Transport.Wireless24G : bt.Transport,
            };

            Log($"[2.4G] {bt.DeviceName}: {w.Percent}% (蓝牙 {bt.Percent?.ToString() ?? "无"}%)");
        }
    }

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

            GattCharacteristic characteristic = chResult.Characteristics[0];
            ch.Characteristic = characteristic;

            // 主动读一次，拿到当前真实值
            var readResult = await WithTimeout(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), GattTimeoutMs);
            if (readResult?.Status == GattCommunicationStatus.Success)
            {
                byte value = ReadByte(readResult.Value);
                if (value <= 100)
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
            if (value > 100) return;

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
