using System.Runtime.InteropServices;
using System.Text;

namespace MouseBattery;

/// <summary>
/// 2.4G 接收器的电量采集。
/// 走厂商私有 HID 集合（usagePage 0xFF1C，64 字节收发），不是标准电池服务——
/// 系统属性里这类设备没有电量，只能自己发查询帧。
///
/// 实测要点（VGN V98 接收器 VID 320F:PID 5088）：
///   1) 请求帧必须是 64 字节整包，且必须用 WriteFile 写；HidD_SetOutputReport 会被设备拒绝（回 0xAA）。
///   2) 帧内容：reportId 0x04 + 00 00 1A 06 00 00 00 + 补零。
///   3) 响应：reportId 0x04 + 00 00 1A 06 00 00 00 + 电量（第 9 字节，含 reportId 计）。
/// 协议来自 drive.vgnlab.com.cn 的 bytech 库 + 抓包比对，非猜测。
/// </summary>
internal static class Wireless24G
{
    private const ushort VendorUsagePage = 0xFF1C;
    private const byte ReportId = 0x04;
    private const int ReportSize = 64;
    private const int TimeoutMs = 500;

    /// <summary>查询帧：固定 7 字节，其余补零。</summary>
    private static readonly byte[] QueryTail = { 0x00, 0x00, 0x1A, 0x06, 0x00, 0x00, 0x00 };

    internal sealed record Reading(string Address, string Name, DeviceKind Kind, int Percent, DateTime Stamp);

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(IntPtr h, StringBuilder buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEvent(IntPtr attrs, bool manual, bool initial, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED ov, out int transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIo(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr overlapped);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE = 0x03;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED { public IntPtr Internal; public IntPtr InternalHigh; public int Offset; public int OffsetHigh; public IntPtr hEvent; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
    }

    /// <summary>轮询所有 2.4G 接收器。没有就返回空表，不抛异常。</summary>
    public static List<Reading> Poll()
    {
        var result = new List<Reading>();
        var paths = new List<string>();

        try
        {
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

            var did = new SP_DEVICE_INTERFACE_DATA();
            try
            {
                for (int i = 0; ; i++)
                {
                    did.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did)) break;

                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                    if (required <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, 0, IntPtr.Size == 8 ? 8 : 5);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, required, out _, IntPtr.Zero)) continue;

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? string.Empty;
                        if (path.Length > 0) paths.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch
        {
            return result;
        }

        foreach (string path in paths)
        {
            try
            {
                Reading? r = Query(path);
                if (r != null) result.Add(r);
            }
            catch
            {
                // 单个设备失败不影响其他设备
            }
        }

        return result;
    }

    private static Reading? Query(string path)
    {
        // 读写必须分开两个句柄：读要 overlapped（异步挂起），写不能带 overlapped
        // （带了标志又传 null OVERLAPPED 会直接 ERROR_INVALID_PARAMETER）
        IntPtr hRead = CreateFile(path, GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (hRead == new IntPtr(-1)) return null;

        IntPtr hWrite = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hWrite == new IntPtr(-1)) { CloseHandle(hRead); return null; }

        IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);

        try
        {
            if (!HidD_GetPreparsedData(hRead, out IntPtr pre)) return null;

            HIDP_CAPS caps;
            try
            {
                if (HidP_GetCaps(pre, out caps) != 0x00110000) return null;
            }
            finally { HidD_FreePreparsedData(pre); }
            if (caps.UsagePage != VendorUsagePage || caps.OutputReportByteLength != ReportSize || caps.InputReportByteLength != ReportSize)
                return null;

            var name = new StringBuilder(128);
            HidD_GetProductString(hRead, name, name.Capacity);
            string product = name.ToString().Trim();
            if (product.Length == 0) product = "2.4G 设备";

            // 先挂一个读，再把查询帧写出去，避免漏掉很快返回的响应
            byte[] inbound = new byte[ReportSize];
            ResetEvent(ev);
            var ov = new OVERLAPPED { hEvent = ev };
            ReadFile(hRead, inbound, inbound.Length, out _, ref ov);

            byte[] frame = new byte[ReportSize];
            frame[0] = ReportId;
            Array.Copy(QueryTail, 0, frame, 1, QueryTail.Length);

            bool wrote = WriteFile(hWrite, frame, frame.Length, out _, IntPtr.Zero);
            if (!wrote)
            {
                CancelIo(hRead);
                GetOverlappedResult(hRead, ref ov, out _, true);
                return null;
            }

            if (WaitForSingleObject(ev, TimeoutMs) != 0)
            {
                CancelIo(hRead);
                GetOverlappedResult(hRead, ref ov, out _, true);
                return null;
            }

            if (!GetOverlappedResult(hRead, ref ov, out int n, false) || n < 10) return null;

            // 响应校验：reportId 0x04 + 00 00 1A 06
            if (inbound[0] != ReportId || inbound[3] != 0x1A || inbound[4] != 0x06) return null;

            int percent = inbound[8];
            if (percent is <= 0 or > 100) return null;

            DeviceKind kind = product.Contains("Mouse", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Mouse
                : product.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Keyboard
                : DeviceKind.Generic;

            // 2.4G 设备没有 MAC，用设备路径的稳定片段当标识
            string address = StableId(path);
            return new Reading(address, product, kind, percent, DateTime.Now);
        }
        finally
        {
            CloseHandle(ev);
            CloseHandle(hRead);
            CloseHandle(hWrite);
        }
    }

    /// <summary>设备路径里取出一段稳定标识，用于别名键和去重。</summary>
    private static string StableId(string path)
    {
        int hash = 17;
        foreach (char c in path.ToLowerInvariant()) hash = hash * 31 + c;
        return "24g" + (hash & 0x7FFFFFFF).ToString("x8");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, ref OVERLAPPED ov);
}
