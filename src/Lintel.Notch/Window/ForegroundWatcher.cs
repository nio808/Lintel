// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Lintel.Notch.Layout;
using static Lintel.Notch.Window.NativeMethods;

namespace Lintel.Notch.Window;

/// <summary>What the fullscreen rule needs to know about a window.</summary>
public readonly record struct ForegroundInfo(string ClassName, bool Zoomed, bool Visible, PixelRect Rect, bool OnNotchDisplay, int NotificationState);

/// <summary>Fullscreen heuristics. Pure.</summary>
public static class FullscreenRules
{
    public const int QunsBusy = 2, QunsD3DFullscreen = 3, QunsPresentation = 4;

    /// <summary>Shell surfaces that cover a monitor without being "an app in fullscreen".</summary>
    public static readonly IReadOnlySet<string> IgnoredClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "ForegroundStaging", "LockScreenControllerProxyWindow",
    };

    /// <summary>
    /// Exclusive D3D / presentation mode on the notch's display, or a non-maximized window that covers the whole monitor
    /// (borderless games, F11 browsers, fullscreen video). Maximized windows and the desktop never count.
    /// </summary>
    public static bool IsFullscreen(ForegroundInfo w, PixelRect monitor)
    {
        if (!w.OnNotchDisplay || !w.Visible || IgnoredClasses.Contains(w.ClassName))
            return false;
        if (w.NotificationState is QunsD3DFullscreen or QunsPresentation)
            return true;
        return !w.Zoomed && w.Rect.Covers(monitor);
    }
}

/// <summary>
/// EVENT_SYSTEM_FOREGROUND hook (out of context, UI thread) + a 2 s re-check, for fullscreen detection and for
/// re-asserting HWND_TOPMOST when another window (e.g. the taskbar) is activated.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventProc proc;   // kept alive while hooked
    private readonly DispatcherTimer recheck;
    private readonly Func<IntPtr> ownWindow;
    private IntPtr hook;
    private IntPtr fullscreenWindow;

    public ForegroundWatcher(Func<IntPtr> ownWindow)
    {
        this.ownWindow = ownWindow;
        proc = (_, _, _, _, _, _, _) => Changed?.Invoke();
        recheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        recheck.Tick += (_, _) => Changed?.Invoke();
    }

    /// <summary>Foreground changed (or the periodic re-check ticked).</summary>
    public event Action? Changed;

    public void Start(bool periodic)
    {
        if (hook == IntPtr.Zero)
            hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        recheck.IsEnabled = periodic;
    }

    /// <summary>True when a fullscreen window is on <paramref name="display"/>. Keeps a known fullscreen window while focus is on another display.</summary>
    public bool Evaluate(DisplayInfo display)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownWindow())
            return Still(display);
        var info = Describe(foreground, display);
        if (info.OnNotchDisplay)
        {
            fullscreenWindow = FullscreenRules.IsFullscreen(info, display.Bounds) ? foreground : IntPtr.Zero;
            return fullscreenWindow != IntPtr.Zero;
        }
        return Still(display);
    }

    public void Dispose()
    {
        recheck.Stop();
        if (hook != IntPtr.Zero)
            UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }

    private bool Still(DisplayInfo display)
    {
        if (fullscreenWindow == IntPtr.Zero)
            return false;
        if (!IsWindow(fullscreenWindow) || IsIconic(fullscreenWindow) || !FullscreenRules.IsFullscreen(Describe(fullscreenWindow, display) with { NotificationState = 0 }, display.Bounds))
            fullscreenWindow = IntPtr.Zero;
        return fullscreenWindow != IntPtr.Zero;
    }

    private static ForegroundInfo Describe(IntPtr hwnd, DisplayInfo display)
    {
        GetWindowRect(hwnd, out var rect);
        SHQueryUserNotificationState(out var quns);
        return new(ClassName(hwnd), IsZoomed(hwnd), IsWindowVisible(hwnd), Displays.Rect(rect),
            MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) == display.Handle, quns);
    }
}
