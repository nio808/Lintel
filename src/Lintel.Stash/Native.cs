// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Lintel.Stash;

internal static class Native
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr mem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint Type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public MOUSEINPUT Mi;   // makes the union the size Windows expects
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    public const ushort VK_CONTROL = 0x11, VK_V = 0x56;

    /// <summary>Synthetic Ctrl+V to whichever window has focus.</summary>
    public static void SendCtrlV()
    {
        INPUT Key(ushort vk, bool up) => new() { Type = INPUT_KEYBOARD, U = new INPUTUNION { Ki = new KEYBDINPUT { Vk = vk, Flags = up ? KEYEVENTF_KEYUP : 0 } } };
        var inputs = new[] { Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // --- known folders ---
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p)!; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // --- shell icons ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Icon for a file type by name/extension only (never touches the disk). Frozen; null on failure.</summary>
    public static System.Windows.Media.ImageSource? ShellIcon(string nameOrExtension, bool folder)
    {
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0, SHGFI_USEFILEATTRIBUTES = 0x10;
        var info = new SHFILEINFO();
        var attrs = folder ? 0x10u : 0x80u;
        if (SHGetFileInfo(nameOrExtension, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
