// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Lintel.Notch.Layout;
using static Lintel.Notch.Window.NativeMethods;

namespace Lintel.Notch.Window;

/// <summary>
/// The fixed-size, never-activating, topmost canvas. DWM mode = non-layered window with per-pixel alpha composed
/// by DWM; layered mode = AllowsTransparency. Clicks reach the window only inside the region set with <see cref="SetRegion"/>.
/// </summary>
internal sealed class NotchWindow : System.Windows.Window
{
    private PixelRect region = new(-1, -1, -1, -1);
    private PixelRect placement;
    private IntPtr previousForeground;

    public NotchWindow(bool layered)
    {
        Layered = layered;
        Title = "Lintel";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = layered;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        AllowDrop = true;
        Left = -32000;   // real placement happens in pixels via Place()
        Top = -32000;
        Width = NotchMetrics.CanvasWidth;
        Height = NotchMetrics.CanvasHeight;
        SourceInitialized += (_, _) => Initialize();
        // WPF makes the first window Application.MainWindow, and app-wide theme managers (WPF-UI's
        // ApplicationThemeManager) restyle MainWindow with an opaque background. The notch is never the main window.
        if (Application.Current is { } app && app.MainWindow == this)
            app.MainWindow = null;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Something outside the notch replaced the background: put the transparent composition back.
        if (e.Property == BackgroundProperty && !ReferenceEquals(e.NewValue, Brushes.Transparent))
        {
            Background = Brushes.Transparent;
            if (Handle != IntPtr.Zero)
                ApplyComposition();
        }
    }

    public bool Layered { get; }
    public IntPtr Handle { get; private set; }
    public bool IsKeyboardMode { get; private set; }

    /// <summary>Display, work area (taskbar) or DPI changed.</summary>
    public event Action? DisplayChanged;

    /// <summary>WM_MOUSEHWHEEL delta (positive = right).</summary>
    public event Action<int>? HorizontalWheel;

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public PixelRect Placement => placement;

    /// <summary>Move/size in physical pixels; never activates.</summary>
    public void Place(PixelRect rect)
    {
        placement = rect;
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Window-relative device pixels. Everything outside is not part of the window, so clicks go to the app below.</summary>
    public void SetRegion(PixelRect rect)
    {
        if (rect == region || Handle == IntPtr.Zero)
            return;
        region = rect;
        // The system owns the region handle after SetWindowRgn.
        SetWindowRgn(Handle, rect.IsEmpty ? CreateRectRgn(0, 0, 0, 0) : CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom), true);
    }

    public void ReassertTopmost()
    {
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void ShowQuietly()
    {
        if (!IsShown)
        {
            Show();             // ShowActivated=false → SW_SHOWNA
            Place(placement);   // WPF's first Show applies Left/Top/Width/Height; ours are in pixels
        }
        ReassertTopmost();
    }

    public void HideQuietly()
    {
        if (IsShown)
            Hide();
    }

    public bool IsShown => Handle != IntPtr.Zero && IsWindowVisible(Handle);

    /// <summary>Keyboard mode: become activatable and foreground (allowed: we just received the hotkey or the click).</summary>
    public void EnterKeyboardMode()
    {
        if (Handle == IntPtr.Zero || IsKeyboardMode)
            return;
        var foreground = GetForegroundWindow();
        previousForeground = foreground == Handle ? IntPtr.Zero : foreground;
        IsKeyboardMode = true;
        SetExStyle(Handle, ExStyle(Handle) & ~WS_EX_NOACTIVATE);
        SetForegroundWindow(Handle);
        Activate();
    }

    /// <summary>Leave keyboard mode; give focus back to the window that had it unless the user clicked elsewhere.</summary>
    public void ExitKeyboardMode(bool restoreFocus)
    {
        if (Handle == IntPtr.Zero || !IsKeyboardMode)
            return;
        IsKeyboardMode = false;
        SetExStyle(Handle, ExStyle(Handle) | WS_EX_NOACTIVATE);
        var previous = previousForeground;
        previousForeground = IntPtr.Zero;
        if (restoreFocus && previous != IntPtr.Zero && IsWindow(previous) && GetForegroundWindow() == Handle)
            SetForegroundWindow(previous);
    }

    private void Initialize()
    {
        Handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(Handle)!;
        ApplyComposition();
        DwmAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        DwmAttribute(Handle, DWMWA_EXCLUDED_FROM_PEEK, 1);
        SetExStyle(Handle, (ExStyle(Handle) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST) & ~WS_EX_APPWINDOW);
        source.AddHook(WndProc);
        SetRegion(default);   // nothing clickable until the first state is applied
        if (!placement.IsEmpty)
            Place(placement);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => DisplayChanged?.Invoke());
    }

    /// <summary>DWM mode: per-pixel alpha composed by DWM on the GPU (transparent WPF clear colour + frame extended over the client).</summary>
    private void ApplyComposition()
    {
        if (Layered || HwndSource.FromHwnd(Handle) is not { CompositionTarget: { } target })
            return;
        target.BackgroundColor = Colors.Transparent;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref margins);
        DwmAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, 1);   // do not round (Win11; ignored elsewhere)
        DwmAttribute(Handle, DWMWA_BORDER_COLOR, unchecked((int)0xFFFFFFFE));   // no border
        DwmAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, 1);   // no backdrop
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != IntPtr.Zero && !Layered:
                handled = true;   // client area = window area
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(() => DisplayChanged?.Invoke());
                break;
            case WM_SETTINGCHANGE when wParam.ToInt32() == SPI_SETWORKAREA:
                Dispatcher.BeginInvoke(() => DisplayChanged?.Invoke());
                break;
            case WM_MOUSEHWHEEL:
                HorizontalWheel?.Invoke((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }
}
