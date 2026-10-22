using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MouseBattery;

internal sealed class BatteryReading
{
    public int? Percent { get; init; }
    public string DeviceName { get; init; } = "未找到设备";
    public string Address { get; init; } = string.Empty;
    public DateTime? Stamp { get; init; }
    public DateTime ReadAt { get; init; } = DateTime.Now;
    public string Source { get; init; } = "-";
    public bool Connected { get; init; }
    public bool Subscribed { get; init; }
    public DeviceKind Kind { get; init; } = DeviceKind.Generic;
    public string? Error { get; init; }

    public double? AgeMinutes => Stamp is null ? null : (DateTime.Now - Stamp.Value).TotalMinutes;
}

/// <summary>
/// 电量采集器。两条腿走路：
///   1) BLE GATT 0x180F/0x2A19 —— 主动读取 + Notify 订阅，能拿到最快的一次上报；
///   2) Windows 设备属性 DEVPKEY_Bluetooth_Battery —— 系统自身缓存，GATT 不可用时的兜底。
/// </summary>
internal sealed class BatteryMonitor : IDisposable
{
    private static readonly Guid BatteryServiceUuid = new("0000180F-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BatteryLevelUuid = new("00002A19-0000-1000-8000-00805F9B34FB");

    private const int GattTimeoutMs = 9000;

    private readonly AppSettings _settings;
    private readonly SynchronizationContext? _ui;
    private readonly object _gate = new();

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _batteryChar;
    private bool _subscribed;
    private int? _gattPercent;
    private DateTime? _gattStamp;
    private DateTime _gattAttemptAt = DateTime.MinValue;
    private bool _gattBroken;

    public BatteryMonitor(AppSettings settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current;
    }

    /// <summary>电量发生变化时触发（已切回 UI 线程）。</summary>
    public event Action<BatteryReading>? Updated;

    public BleDevice? Target { get; private set; }

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

    public async Task<BatteryReading> PollAsync(bool forceGatt)
    {
        // 1) 发现/刷新设备身份与系统侧电量
        var devices = DevProp.EnumBleDevices();
        BleDevice? target = null;

        lock (_gate)
        {
            target = PickTarget(devices);
            if (target != null)
            {
                bool switched = Target == null || !string.Equals(Target.Address, target.Address, StringComparison.OrdinalIgnoreCase);
                Target = target;
                if (switched) ResetGatt();
            }
            else
            {
                Target = null;
                ResetGatt();
            }
        }

        if (target == null)
        {
            return Publish(new BatteryReading
            {
                Percent = null,
                DeviceName = "未找到蓝牙鼠标",
                Connected = false,
                Kind = DeviceKind.Mouse,
                Error = "系统里没有在线且带电量属性的 BLE 设备",
                Source = "-",
            });
        }

        // 2) GATT 主动读（首次 / 手动刷新 / 订阅掉线后重试）
        bool gattUsable = _settings.UseGatt && !_gattBroken;
        bool needGatt = gattUsable && (forceGatt || !_subscribed);

        if (needGatt && (forceGatt || (DateTime.Now - _gattAttemptAt).TotalSeconds > 20))
        {
            _gattAttemptAt = DateTime.Now;
            await TryGattAsync(target);
        }

        // 3) 合并两个来源
        int? percent;
        string source;
        DateTime? stamp;

        lock (_gate)
        {
            if (_gattPercent is int gp && _gattStamp is DateTime gt && (DateTime.Now - gt).TotalHours < 6)
            {
                percent = gp;
                source = _subscribed ? "GATT 实时订阅" : "GATT 主动读取";
                stamp = gt;
            }
            else
            {
                percent = target.Battery;
                source = "系统属性";
                stamp = target.Stamp;
            }
        }

        bool connected = _device?.ConnectionStatus == BluetoothConnectionStatus.Connected
                         || target.Battery.HasValue;

        return Publish(new BatteryReading
        {
            Percent = percent,
            DeviceName = target.Name,
            Address = target.DisplayAddress,
            Stamp = stamp,
            Source = source,
            Connected = connected,
            Subscribed = _subscribed,
            Kind = target.Kind,
            Error = percent == null ? "设备未上报电量" : null,
        });
    }

    private BleDevice? PickTarget(List<BleDevice> devices)
    {
        if (devices.Count == 0) return null;

        // 用户锁定过某个 MAC：只认它
        if (!string.IsNullOrEmpty(_settings.DeviceAddress))
        {
            var exact = devices.FirstOrDefault(d =>
                string.Equals(d.Address, _settings.DeviceAddress, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // 锁定的设备不在线，不要去乱选别的设备
            return null;
        }

        // 自动挑选：优先名字像鼠标的，其次有电量值的
        var mouseLike = devices.FirstOrDefault(d =>
            d.Name.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("鼠标", StringComparison.Ordinal));
        if (mouseLike != null) return mouseLike;

        var withBattery = devices.FirstOrDefault(d => d.Battery.HasValue);
        return withBattery ?? devices[0];
    }

    // ---------------------------------------------------------------- GATT

    private async Task TryGattAsync(BleDevice target)
    {
        try
        {
            if (_device == null || _device.BluetoothAddress != target.AddressValue)
            {
                ResetGatt();
                var op = BluetoothLEDevice.FromBluetoothAddressAsync(target.AddressValue);
                _device = await WithTimeout(op, GattTimeoutMs);
            }

            if (_device == null)
            {
                Log("[GATT] FromBluetoothAddressAsync 返回 null，设备可能已休眠");
                return;
            }

            var svcResult = await WithTimeout(
                _device.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached),
                GattTimeoutMs);

            if (svcResult?.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                Log($"[GATT] 电池服务不可用: {svcResult?.Status}");
                return;
            }

            var service = svcResult.Services[0];
            var chResult = await WithTimeout(
                service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Uncached),
                GattTimeoutMs);

            if (chResult?.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
            {
                Log($"[GATT] 电量特征不可用: {chResult?.Status}");
                return;
            }

            GattCharacteristic ch = chResult.Characteristics[0];

            // 主动读一次，拿到当前真实值
            var readResult = await WithTimeout(ch.ReadValueAsync(BluetoothCacheMode.Uncached), GattTimeoutMs);
            if (readResult?.Status == GattCommunicationStatus.Success)
            {
                byte value = ReadByte(readResult.Value);
                if (value <= 100)
                {
                    lock (_gate)
                    {
                        _gattPercent = value;
                        _gattStamp = DateTime.Now;
                    }
                    Log($"[GATT] 主动读取 = {value}%");
                }
            }
            else
            {
                Log($"[GATT] 读取失败: {readResult?.Status}");
            }

            // 订阅推送，这是「实时」的来源
            lock (_gate) { _batteryChar = ch; }

            if (!_subscribed)
            {
                var status = await WithTimeout(
                    ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify),
                    GattTimeoutMs);

                if (status == GattCommunicationStatus.Success)
                {
                    ch.ValueChanged += OnValueChanged;
                    _subscribed = true;
                    Log("[GATT] 已订阅 Notify 推送");
                }
                else
                {
                    Log($"[GATT] 订阅失败: {status}（降级为轮询）");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[GATT] 异常: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            byte value = ReadByte(args.CharacteristicValue);
            if (value > 100) return;

            lock (_gate)
            {
                if (_gattPercent == value) return;
                _gattPercent = value;
                _gattStamp = DateTime.Now;
            }

            Log($"[GATT] 推送更新 = {value}%");

            var target = Target;
            if (target == null) return;

            Publish(new BatteryReading
            {
                Percent = value,
                DeviceName = target.Name,
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
            Log($"[GATT] 推送处理异常: {ex.Message}");
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

    private void ResetGatt()
    {
        try
        {
            if (_batteryChar != null && _subscribed)
            {
                _batteryChar.ValueChanged -= OnValueChanged;
            }
        }
        catch
        {
            // ignore
        }

        _batteryChar = null;
        _subscribed = false;
        _device?.Dispose();
        _device = null;
    }

    /// <summary>设备长时间读不到时，允许重新尝试 GATT。</summary>
    public void ClearGattFailure() => _gattBroken = false;

    private BatteryReading Publish(BatteryReading reading)
    {
        if (_ui != null) _ui.Post(_ => Updated?.Invoke(reading), null);
        else Updated?.Invoke(reading);
        return reading;
    }

    public void Dispose() => ResetGatt();
}
