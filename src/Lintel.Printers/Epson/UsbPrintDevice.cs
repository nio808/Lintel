// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lintel.Printers.Epson;

/// <summary>
/// The USB print class device interface that Windows' own usbprint.sys exposes for every USB printer. Opening it needs no driver
/// swap and no elevation. Approach and path discovery follow epson-usb's usbprint_win backend (MIT), see SOURCES.md.
/// </summary>
internal sealed class UsbPrintDevice : IEpsonTransport
{
    private static readonly Guid UsbPrintGuid = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");
    private const uint IoctlGet1284Id = 0x220034;   // CTL_CODE(FILE_DEVICE_UNKNOWN, 13, METHOD_BUFFERED, FILE_ANY_ACCESS)

    public string Path { get; }
    private readonly SafeFileHandle _h;

    private UsbPrintDevice(string path, SafeFileHandle h) { Path = path; _h = h; }

    // ---- enumeration -------------------------------------------------------------------------------------------------------

    /// <summary>Device paths of every present usbprint interface from Epson (vendor id 04B8).</summary>
    public static IReadOnlyList<string> EnumerateEpsonPaths()
    {
        var result = new List<string>();
        var g = UsbPrintGuid;
        var set = SetupDiGetClassDevsW(ref g, null, IntPtr.Zero, 0x02 | 0x10);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (uint i = 0; ; i++)
            {
                var d = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out var need, IntPtr.Zero);
                if (need == 0) continue;
                var buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, need, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4);
                    if (path is not null && path.Contains("vid_04b8", StringComparison.OrdinalIgnoreCase)) result.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    /// <summary>Opens a path, shared read/write so Windows' own components keep their handle. Throws <see cref="Win32Exception"/>.</summary>
    public static UsbPrintDevice Open(string path)
    {
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);   // R/W, share R/W, OPEN_EXISTING, OVERLAPPED
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new UsbPrintDevice(path, h);
    }

    /// <summary>IEEE-1284 device ID such as "MFG:EPSON;CMD:...;MDL:L3250 Series;..." via a read-only IOCTL. Null if unavailable.</summary>
    public string? ReadDeviceId()
    {
        var buf = new byte[1024];
        using var ev = new ManualResetEvent(false);
        var ov = new Overlapped { hEvent = ev.SafeWaitHandle.DangerousGetHandle() };
        if (!DeviceIoControl(_h, IoctlGet1284Id, IntPtr.Zero, 0, buf, buf.Length, out var n, ref ov))
        {
            if (Marshal.GetLastWin32Error() != 997) return null;
            if (!ev.WaitOne(3000)) { CancelIoEx(_h, ref ov); GetOverlappedResult(_h, ref ov, out _, true); return null; }
            if (!GetOverlappedResult(_h, ref ov, out n, false)) return null;
        }
        if (n < 3) return null;
        // First two bytes are a length prefix; the text follows.
        return Encoding.ASCII.GetString(buf, 2, (int)n - 2).TrimEnd('\0', ' ', '\r', '\n');
    }

    public static string? Field(string? id, string key)
    {
        if (id is null) return null;
        foreach (var part in id.Split(';'))
        {
            var c = part.IndexOf(':');
            if (c > 0 && part[..c].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return part[(c + 1)..].Trim();
        }
        return null;
    }

    // ---- IEpsonTransport ---------------------------------------------------------------------------------------------------

    public void Write(byte[] data, int timeoutMs, CancellationToken ct)
    {
        if (Io(data, data.Length, timeoutMs, ct, write: true) < 0) throw new TimeoutException("usbprint write timed out");
    }

    public byte[] Read(int max, int timeoutMs, CancellationToken ct)
    {
        var buf = new byte[max];
        var n = Io(buf, max, timeoutMs, ct, write: false);
        return n <= 0 ? [] : buf[..n];
    }

    // Overlapped I/O with timeout and cancellation. The buffer stays pinned until the kernel is done, even after a cancel.
    private int Io(byte[] buf, int len, int timeoutMs, CancellationToken ct, bool write)
    {
        ct.ThrowIfCancellationRequested();
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        using var ev = new ManualResetEvent(false);
        var ov = new Overlapped { hEvent = ev.SafeWaitHandle.DangerousGetHandle() };
        try
        {
            var ptr = pin.AddrOfPinnedObject();
            var ok = write ? WriteFile(_h, ptr, (uint)len, out var done, ref ov) : ReadFile(_h, ptr, (uint)len, out done, ref ov);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 997) throw new Win32Exception(err);
                var idx = WaitHandle.WaitAny([ev, ct.WaitHandle], timeoutMs);
                if (idx != 0)
                {
                    CancelIoEx(_h, ref ov);
                    GetOverlappedResult(_h, ref ov, out _, true);
                    ct.ThrowIfCancellationRequested();
                    return -1;
                }
                if (!GetOverlappedResult(_h, ref ov, out done, false)) return -1;
            }
            return (int)done;
        }
        finally { pin.Free(); }
    }

    public void Dispose() => _h.Dispose();

    // ---- P/Invoke ----------------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int cbSize; public Guid Guid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr hEvent; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, string? enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SpDeviceInterfaceData d);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData d, IntPtr detail, uint size, out uint required, IntPtr info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, IntPtr buf, uint n, out uint done, ref Overlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint n, out uint done, ref Overlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inLen, byte[] outBuf, int outLen, out uint returned, ref Overlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle h, ref Overlapped ov, out uint done, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, ref Overlapped ov);
}
