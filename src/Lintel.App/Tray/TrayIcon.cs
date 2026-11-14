// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Lintel.App.Tray;

/// <summary>
/// Notification-area icon (Shell_NotifyIcon, version 4) on a hidden top-level HwndSource. UI thread only.
/// Left click → <c>click</c>; right click / menu key → a WPF ContextMenu (Fluent-styled by the app resources).
/// Re-adds itself when Explorer restarts (TaskbarCreated) and reports system light/dark changes.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    private const int WmContextMenu = 0x007B, WmSettingChange = 0x001A, NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifInfo = 0x1, NiifLargeIcon = 0x20;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource source;
    private readonly Func<ContextMenu> buildMenu;
    private readonly Action click;
    private readonly IntPtr normalIcon;
    private readonly IntPtr badgeIcon;
    private string tooltip;
    private bool badge;

    public TrayIcon(string tooltip, Func<ContextMenu> buildMenu, Action click)
    {
        this.tooltip = tooltip;
        this.buildMenu = buildMenu;
        this.click = click;
        source = new HwndSource(new HwndSourceParameters("Lintel.Tray") { WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook(WndProc);
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        normalIcon = LoadIcon("tray.ico", size);
        badgeIcon = LoadIcon("tray-update.ico", size);
        Add();
    }

    /// <summary>Windows switched between light and dark.</summary>
    public event Action? ThemeChanged;

    public void SetBadge(bool on, string newTooltip)
    {
        badge = on;
        tooltip = newTooltip;
        var data = Data(NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>Windows shows it as a toast from this app.</summary>
    public void ShowBalloon(string title, string message)
    {
        var data = Data(NifInfo);
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(string.IsNullOrWhiteSpace(message) ? title : message, 255);
        data.dwInfoFlags = NiifInfo | NiifLargeIcon;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        source.RemoveHook(WndProc);
        source.Dispose();
        DestroyIcon(normalIcon);
        DestroyIcon(badgeIcon);
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimAdd, ref data);
        data.uVersion = 4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = badge ? badgeIcon : normalIcon,
        szTip = Clip(tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case NinSelect or NinKeySelect:
                    click();
                    break;
                case WmContextMenu:
                    ShowMenu();
                    break;
            }
        }
        else if (msg == TaskbarCreated)
        {
            Add();
        }
        else if (msg == WmSettingChange && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ThemeChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = buildMenu();
        menu.Placement = PlacementMode.MousePoint;
        // Without foreground the menu would not close when the user clicks elsewhere.
        SetForegroundWindow(source.Handle);
        menu.IsOpen = true;
    }

    /// <summary>Picks the smallest frame ≥ size (else the largest) from an embedded .ico and makes an HICON (PNG frames are fine).</summary>
    private static IntPtr LoadIcon(string name, int size)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
        if (info is null)
            return IntPtr.Zero;
        using var memory = new MemoryStream();
        using (info.Stream)
            info.Stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var frames = Enumerable.Range(0, BitConverter.ToUInt16(bytes, 4))
            .Select(i => (Entry: 6 + 16 * i, Size: bytes[6 + 16 * i] == 0 ? 256 : bytes[6 + 16 * i]))
            .OrderBy(f => f.Size).ToList();
        var (entry, _) = frames.FirstOrDefault(f => f.Size >= size, frames[^1]);
        var length = BitConverter.ToInt32(bytes, entry + 8);
        var offset = BitConverter.ToInt32(bytes, entry + 12);
        return CreateIconFromResourceEx(bytes[offset..(offset + length)], (uint)length, true, 0x00030000, size, size, 0);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
