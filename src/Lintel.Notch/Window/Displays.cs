// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Notch.Layout;
using static Lintel.Notch.Window.NativeMethods;

namespace Lintel.Notch.Window;

/// <summary>A display: rcMonitor/rcWork in physical pixels, DPI scale (1.25 = 125%).</summary>
public sealed record DisplayInfo(string Device, PixelRect Bounds, PixelRect Work, bool Primary, double DpiScale, IntPtr Handle = default);

/// <summary>Display enumeration (Win32) and the pure choice/placement rules.</summary>
public static class Displays
{
    /// <summary>"primary" | "pointer" | a device name such as \\.\DISPLAY2 (missing → primary).</summary>
    public static DisplayInfo? Choose(IReadOnlyList<DisplayInfo> displays, string? setting, (int X, int Y)? pointer)
    {
        if (displays.Count == 0)
            return null;
        var primary = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        if (string.Equals(setting, "pointer", StringComparison.OrdinalIgnoreCase))
            return pointer is { } p ? displays.FirstOrDefault(d => d.Bounds.Contains(p.X, p.Y)) ?? primary : primary;
        if (string.IsNullOrWhiteSpace(setting) || string.Equals(setting, "primary", StringComparison.OrdinalIgnoreCase))
            return primary;
        return displays.FirstOrDefault(d => string.Equals(d.Device, setting, StringComparison.OrdinalIgnoreCase)) ?? primary;
    }

    /// <summary>Window rect in screen pixels: centred, top at rcMonitor.top, or rcWork.top when an app bar is docked at the top.</summary>
    public static PixelRect WindowRect(DisplayInfo display)
    {
        var (width, height) = NotchMetrics.WindowPixels(display.DpiScale);
        var left = display.Bounds.Left + (display.Bounds.Width - width) / 2;
        var top = display.Work.Top > display.Bounds.Top ? display.Work.Top : display.Bounds.Top;
        return new(left, top, left + width, top + height);
    }

    public static List<DisplayInfo> Enumerate()
    {
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (Describe(monitor) is { } info)
                list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    internal static DisplayInfo? Describe(IntPtr monitor)
    {
        var info = new MONITORINFOEX { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(), Device = "" };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return null;
        var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return new(info.Device, Rect(info.Monitor), Rect(info.Work), (info.Flags & MONITORINFOF_PRIMARY) != 0, dpi, monitor);
    }

    public static (int X, int Y)? Pointer() => GetCursorPos(out var p) ? (p.X, p.Y) : null;

    internal static PixelRect Rect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
