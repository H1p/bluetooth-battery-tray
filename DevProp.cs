using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MouseBattery;

/// <summary>一只 BLE 设备在系统里的快照。</summary>
internal sealed record BleDevice(
    string InstanceId,
    string Name,
    string Address,
    ulong AddressValue,
    int? Battery,
    DateTime? Stamp,
    DeviceKind Kind)
{
    public string DisplayAddress => DevProp.FormatAddress(Address);
}

/// <summary>
/// 一台经典蓝牙（BR/EDR）设备的快照。电量只存在于 HFP 免手操作音频网关子节点上，
/// 系统「设置 → 蓝牙」里给耳机显示的就是这个值。
/// </summary>
internal sealed record ClassicDevice(
    string Address,
    string Name,
    int? Battery,
    DateTime? Stamp,
    DeviceKind Kind)
{
    public string DisplayAddress => DevProp.FormatAddress(Address);
}

/// <summary>
/// 通过 SetupAPI 直接读 Windows 设备属性树。
/// 这是系统「设置 → 蓝牙和设备」里显示电量的同一个数据源，零依赖、无需管理员权限。
/// </summary>
internal static class DevProp
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_ALLCLASSES = 0x04;

    private static readonly Guid GUID_DEVCLASS_BLUETOOTH = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    private static readonly Guid GUID_BTH_BATTERY = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private static readonly Guid GUID_DEVICE = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid GUID_DEVICE_CATEGORY = new("78c34fc8-104a-4aca-9ea4-524d52996e57");

    // DEVPKEY_Bluetooth_Battery  = {104EA319-…}, PID 2  (BYTE)
    // DEVPKEY_Bluetooth_BatteryStamp = {104EA319-…}, PID 7 (FILETIME，最近一次上报时间)
    // DEVPKEY_Device_FriendlyName = {a45c254e-…}, PID 14 (STRING)
    // DEVPKEY_DeviceDisplay_Category = {78c34fc8-…}, PID 90 (STRING_LIST，如 Communication.Headset.Bluetooth)
    private static readonly DevPropKey KeyBattery = new(GUID_BTH_BATTERY, 2);
    private static readonly DevPropKey KeyStamp = new(GUID_BTH_BATTERY, 7);
    private static readonly DevPropKey KeyFriendlyName = new(GUID_DEVICE, 14);
    private static readonly DevPropKey KeyCategory = new(GUID_DEVICE_CATEGORY, 90);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid fmtid;
        public int pid;

        public DevPropKey(Guid fmtid, int pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, ref DevPropKey propertyKey,
        out uint propertyType, IntPtr propertyBuffer, int propertyBufferSize,
        out int requiredSize, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>不带设备类过滤的版本：经典蓝牙节点不在 Bluetooth 类里也能枚举到。</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    /// <summary>实例 ID 里的设备 MAC：取最后一个 <c>&amp;MAC</c> 段。
    /// ⚠️ 不能全局找 12 位十六进制——服务 UUID 段里的 <c>00805F9B34FB</c> 会先被匹配。</summary>
    private static readonly Regex MacTail = new(
        @"&(?:BLUETOOTHDEVICE_)?([0-9A-Fa-f]{12})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>子节点实例 ID 前缀里的服务 UUID：<c>BTHENUM\{0000111E-…}</c>。</summary>
    private static readonly Regex ServiceUuid = new(
        @"^BTHENUM\\\{([0-9A-Fa-f]{8})-", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>音频相关服务：A2DP / HFP / HSP / AVRCP。有其一就说明是耳机或音箱。</summary>
    private static readonly HashSet<string> AudioServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "00001108", // Headset
        "00001112", // Headset AG
        "0000110a", // Audio Sink
        "0000110b", // Audio Source
        "0000110c", // A/V Remote Control Target
        "0000110e", // A/V Remote Control
        "0000111e", // Handsfree
        "0000111f", // Handsfree AG
    };

    /// <summary>子节点名称后缀，取名字时要剥掉，否则会显示成「闹钟音箱 Hands-Free AG」。</summary>
    private static readonly string[] ChildSuffixes =
    {
        " Hands-Free AG", " Hands-Free", " Avrcp 传输", " A2DP", " HFP", " 免手操作音频网关",
    };

    /// <summary>12 位 MAC 格式化成 AA:BB:CC:DD:EE:FF；不是 12 位就原样返回。</summary>
    public static string FormatAddress(string address) =>
        address.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => address.Substring(i * 2, 2).ToUpperInvariant()))
            : address;

    /// <summary>枚举当前在线的全部 BLE 设备（BTHLE\DEV_*）。</summary>
    public static List<BleDevice> EnumBleDevices()
    {
        var result = new List<BleDevice>();
        Guid classGuid = GUID_DEVCLASS_BLUETOOTH;
        IntPtr set = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var data = new SpDevInfoData();
            data.cbSize = Marshal.SizeOf<SpDevInfoData>();

            for (int i = 0; ; i++)
            {
                data.cbSize = Marshal.SizeOf<SpDevInfoData>();
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                var sb = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _)) continue;
                string instanceId = sb.ToString();

                if (!instanceId.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)) continue;

                string address = ParseAddress(instanceId);
                if (address.Length != 12) continue;

                string name = ReadString(set, ref data, KeyFriendlyName) ?? "蓝牙设备";
                int? battery = ReadByte(set, ref data, KeyBattery);
                DateTime? stamp = ReadFileTime(set, ref data, KeyStamp);

                result.Add(new BleDevice(
                    instanceId, name, address,
                    Convert.ToUInt64(address, 16),
                    battery, stamp,
                    DeviceIcons.Detect(name)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    /// <summary>
    /// 枚举经典蓝牙（BR/EDR）设备，按设备 MAC 归并 <c>BTHENUM\*</c> 多个节点，取出电量。
    ///
    /// 电量只挂在 HFP 免手操作音频网关子节点上（<c>{0000111E-…}</c>），
    /// 系统「设置 → 蓝牙」里给耳机/音箱显示的就是它。全程只读属性表：
    /// **不打开设备、不建立连接、不写任何报文**，整棵设备树枚举一次几毫秒。
    /// </summary>
    public static List<ClassicDevice> EnumClassicDevices()
    {
        var map = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);

        IntPtr set = SetupDiGetClassDevsAll(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new List<ClassicDevice>();

        try
        {
            var data = new SpDevInfoData();
            data.cbSize = Marshal.SizeOf<SpDevInfoData>();

            for (int i = 0; ; i++)
            {
                data.cbSize = Marshal.SizeOf<SpDevInfoData>();
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                var sb = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _)) continue;
                string instanceId = sb.ToString();

                // 只认经典蓝牙，排除 BLE（BTHLE）节点，避免和 GATT 通道重复上报
                if (!instanceId.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)) continue;

                string? mac = ParseClassicAddress(instanceId);
                if (mac == null) continue;

                if (!map.TryGetValue(mac, out Agg? agg))
                    map[mac] = agg = new Agg();

                bool isParent = instanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);

                string? name = ReadString(set, ref data, KeyFriendlyName);
                if (isParent)
                {
                    // 父节点是干净的设备名；子节点会带「Hands-Free AG」这类后缀
                    if (!string.IsNullOrWhiteSpace(name)) agg.ParentName = name!.Trim();

                    string[] categories = ReadStringList(set, ref data, KeyCategory);
                    if (categories.Length > 0) agg.Categories = categories;
                }
                else
                {
                    if (agg.ChildName == null && !string.IsNullOrWhiteSpace(name)) agg.ChildName = name!.Trim();

                    Match svc = ServiceUuid.Match(instanceId);
                    if (svc.Success) agg.Services.Add(svc.Groups[1].Value);
                }

                // 电量取第一个有效值：只有音频网关节点的这个属性会带数
                if (agg.Battery == null)
                {
                    int? percent = ReadNumber(set, ref data, KeyBattery);
                    if (percent is > 0 and <= 100)
                    {
                        agg.Battery = percent;
                        agg.Stamp = ReadFileTime(set, ref data, KeyStamp);
                    }
                }
            }
        }
        catch
        {
            // 枚举失败按「没读到」处理，绝不影响 BLE 与 2.4G 两条通道
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        var result = new List<ClassicDevice>(map.Count);
        foreach (KeyValuePair<string, Agg> pair in map)
        {
            Agg agg = pair.Value;
            string name = agg.ParentName ?? CleanChildName(agg.ChildName) ?? "蓝牙音频设备";

            result.Add(new ClassicDevice(
                pair.Key,
                name,
                agg.Battery,
                agg.Stamp,
                ResolveClassicKind(name, agg.Categories, agg.Services)));
        }

        return result;
    }

    private sealed class Agg
    {
        public string? ParentName;
        public string? ChildName;
        public int? Battery;
        public DateTime? Stamp;
        public string[] Categories = Array.Empty<string>();
        public readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>设备类型：系统类别最权威，其次音频服务，最后才猜名字。
    /// 猜不出必须是 Generic——绝不能退回 Headset（否则蓝牙鼠标会显示成耳机）。</summary>
    private static DeviceKind ResolveClassicKind(string name, string[] categories, HashSet<string> services)
    {
        foreach (string category in categories)
        {
            if (category.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
            if (category.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Mouse;
            if (category.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Controller", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Joystick", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Gamepad;
            if (category.Contains("Headset", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Speaker", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Headset;
        }

        if (services.Overlaps(AudioServices)) return DeviceKind.Headset;

        return DeviceIcons.Detect(name);
    }

    /// <summary>从 BTHENUM 节点的实例 ID 里取设备 MAC（12 位，小写）。</summary>
    private static string? ParseClassicAddress(string instanceId)
    {
        MatchCollection matches = MacTail.Matches(instanceId);
        if (matches.Count > 0) return matches[^1].Groups[1].Value.ToLowerInvariant();

        // 只有父节点、没有子节点时形如 BTHENUM\DEV_<12位>
        string[] parts = instanceId.Split('\\');
        if (parts.Length >= 2 && parts[1].StartsWith("DEV_", StringComparison.OrdinalIgnoreCase))
        {
            string hex = parts[1][4..];
            if (hex.Length == 12 && hex.All(Uri.IsHexDigit)) return hex.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>剥掉子节点名称里的「Hands-Free AG」这类尾巴。</summary>
    private static string? CleanChildName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string trimmed = name.Trim();
        foreach (string suffix in ChildSuffixes)
        {
            int index = trimmed.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                trimmed = trimmed[..index].Trim();
                break;
            }
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>电量属性可能是 BYTE / UINT16 / UINT32，按类型取值。</summary>
    private static int? ReadNumber(IntPtr set, ref SpDevInfoData data, DevPropKey key)
    {
        IntPtr buffer = AllocAndGet(set, ref data, key, out uint type, out _);
        if (buffer == IntPtr.Zero) return null;
        try
        {
            return type switch
            {
                0x03 => Marshal.ReadByte(buffer),          // DEVPROP_TYPE_BYTE
                0x05 => (ushort)Marshal.ReadInt16(buffer), // DEVPROP_TYPE_UINT16
                0x07 => Marshal.ReadInt32(buffer),         // DEVPROP_TYPE_UINT32
                _ => null,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读 UTF-16 多字符串（<c>\0</c> 分隔、双 <c>\0</c> 结尾）。</summary>
    private static string[] ReadStringList(IntPtr set, ref SpDevInfoData data, DevPropKey key)
    {
        IntPtr buffer = AllocAndGet(set, ref data, key, out uint type, out _);
        if (buffer == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            if (type != 0x2012) return Array.Empty<string>(); // DEVPROP_TYPE_STRING_LIST

            var list = new List<string>();
            int offset = 0;
            while (offset < 2040)
            {
                string? item = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset));
                if (string.IsNullOrEmpty(item)) break;
                list.Add(item);
                offset += (item.Length + 1) * 2;
            }
            return list.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>从 BTHLE\DEV_CAB3812135D4\7&amp;… 中取出 12 位十六进制 MAC。</summary>
    private static string ParseAddress(string instanceId)
    {
        string[] parts = instanceId.Split('\\');
        if (parts.Length < 2) return string.Empty;

        string second = parts[1];
        if (!second.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        string hex = second[4..];
        return hex.Length == 12 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : string.Empty;
    }

    private static IntPtr AllocAndGet(IntPtr set, ref SpDevInfoData data, DevPropKey key, out uint type, out int required)
    {
        type = 0;
        required = 0;
        IntPtr buffer = Marshal.AllocHGlobal(2048);
        bool ok = SetupDiGetDeviceProperty(set, ref data, ref key, out type, buffer, 2048, out required, 0);
        if (!ok)
        {
            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }
        return buffer;
    }

    private static string? ReadString(IntPtr set, ref SpDevInfoData data, DevPropKey key)
    {
        IntPtr buffer = AllocAndGet(set, ref data, key, out uint type, out _);
        if (buffer == IntPtr.Zero) return null;
        try
        {
            // DEVPROP_TYPE_STRING = 0x12
            return type == 0x12 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ReadByte(IntPtr set, ref SpDevInfoData data, DevPropKey key)
    {
        IntPtr buffer = AllocAndGet(set, ref data, key, out uint type, out _);
        if (buffer == IntPtr.Zero) return null;
        try
        {
            if (type != 0x03) return null; // DEVPROP_TYPE_BYTE
            byte value = Marshal.ReadByte(buffer);

            // 0 一律当无效：设备休眠前会推 0/1 这类伪读数，
            // 而真实的 0% 在关机前早就触发过低电量提醒了，不会漏。
            return value is > 0 and <= 100 ? value : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DateTime? ReadFileTime(IntPtr set, ref SpDevInfoData data, DevPropKey key)
    {
        IntPtr buffer = AllocAndGet(set, ref data, key, out uint type, out _);
        if (buffer == IntPtr.Zero) return null;
        try
        {
            if (type != 0x10) return null; // DEVPROP_TYPE_FILETIME
            long ft = Marshal.ReadInt64(buffer);
            if (ft <= 0) return null;
            DateTime utc = DateTime.FromFileTimeUtc(ft);
            if (utc.Year < 2000) return null;
            return utc.ToLocalTime();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
