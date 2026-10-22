using System.Runtime.InteropServices;
using System.Text;

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
    public string DisplayAddress =>
        Address.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => Address.Substring(i * 2, 2).ToUpperInvariant()))
            : Address;
}

/// <summary>
/// 通过 SetupAPI 直接读 Windows 设备属性树。
/// 这是系统「设置 → 蓝牙和设备」里显示电量的同一个数据源，零依赖、无需管理员权限。
/// </summary>
internal static class DevProp
{
    private const int DIGCF_PRESENT = 0x02;

    private static readonly Guid GUID_DEVCLASS_BLUETOOTH = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    private static readonly Guid GUID_BTH_BATTERY = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private static readonly Guid GUID_DEVICE = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    // DEVPKEY_Bluetooth_Battery  = {104EA319-…}, PID 2  (BYTE)
    // DEVPKEY_Bluetooth_BatteryStamp = {104EA319-…}, PID 7 (FILETIME，最近一次上报时间)
    // DEVPKEY_Device_FriendlyName = {a45c254e-…}, PID 14 (STRING)
    private static readonly DevPropKey KeyBattery = new(GUID_BTH_BATTERY, 2);
    private static readonly DevPropKey KeyStamp = new(GUID_BTH_BATTERY, 7);
    private static readonly DevPropKey KeyFriendlyName = new(GUID_DEVICE, 14);

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
            return value <= 100 ? value : null;
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
