// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Lintel.App.Settings;

/// <summary>A connected display: GDI device name (the notch.display value), number, friendly monitor name, pixels.</summary>
internal sealed record DisplayInfo(string DeviceName, int Number, string? FriendlyName, int Width, int Height, bool IsPrimary, double RefreshHz)
{
    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(FriendlyName) ? "" : $" · {FriendlyName}";
            return $"Display {Number}{name} · {Width} × {Height}{(IsPrimary ? " · main" : "")}";
        }
    }
}

/// <summary>EnumDisplayMonitors for bounds/primary + QueryDisplayConfig for friendly names and refresh rates.</summary>
internal static class Displays
{
    public static IReadOnlyList<DisplayInfo> All()
    {
        var names = new Dictionary<string, (string? Name, double Hz)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (device, friendly, hz) in Targets())
                names[device] = (friendly, hz);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Very old Windows: no friendly names.
        }

        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var digits = new string(info.szDevice.Where(char.IsAsciiDigit).ToArray());
                names.TryGetValue(info.szDevice, out var extra);
                result.Add(new DisplayInfo(info.szDevice, int.TryParse(digits, out var n) ? n : result.Count + 1, extra.Name,
                    info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & 1) != 0, extra.Hz));
            }
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(d => d.Number).ToList();
    }

    /// <summary>Highest refresh rate among active displays, null when unknown.</summary>
    public static int? MaxRefreshRate()
    {
        try
        {
            var max = Targets().Select(t => t.Hz).DefaultIfEmpty(0).Max();
            return max >= 1 ? (int)Math.Round(max) : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static List<(string Device, string? Friendly, double Hz)> Targets()
    {
        var list = new List<(string, string?, double)>();
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            return list;
        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return list;
        foreach (var path in paths.Take((int)pathCount))
        {
            var source = new SourceDeviceName
            {
                header = new DeviceInfoHeader { type = 1, size = Marshal.SizeOf<SourceDeviceName>(), adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref source) != 0)
                continue;
            var target = new TargetDeviceName
            {
                header = new DeviceInfoHeader { type = 2, size = Marshal.SizeOf<TargetDeviceName>(), adapterId = path.targetInfo.adapterId, id = path.targetInfo.id },
            };
            var friendly = DisplayConfigGetDeviceInfo(ref target) == 0 ? target.monitorFriendlyDeviceName : null;
            var rate = path.targetInfo.refreshRate;
            list.Add((source.viewGdiDeviceName, friendly, rate.Denominator == 0 ? 0 : (double)rate.Numerator / rate.Denominator));
        }
        return list;
    }

    private const uint QdcOnlyActivePaths = 2;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { public uint infoType; public uint id; public Luid adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader { public uint type; public int size; public Luid adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount,
        [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
}
