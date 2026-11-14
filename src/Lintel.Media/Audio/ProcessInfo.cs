// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lintel.Media.Audio;

/// <summary>Executable path, description and icon of a process, tolerant of access-denied and exited processes.</summary>
internal static class ProcessInfo
{
    private const uint QueryLimited = 0x1000;
    private const uint IconLarge = 0x100, UseFileAttributes = 0x10, Icon = 0x100;
    private static readonly Dictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static string? ImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimited, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? Description(string path)
    {
        if (Descriptions.TryGetValue(path, out var cached))
            return cached;
        string? text;
        try { text = FileVersionInfo.GetVersionInfo(path).FileDescription; }
        catch (Exception) { text = null; }
        return Descriptions[path] = text;
    }

    /// <summary>Frozen icon of the exe (cached); callable from any thread.</summary>
    public static ImageSource? IconOf(string path)
    {
        if (Icons.TryGetValue(path, out var cached))
            return cached;
        ImageSource? image = null;
        try
        {
            var info = new ShFileInfo();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), Icon | IconLarge) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    image = bmp;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch (Exception)
        {
            image = null;
        }
        return Icons[path] = image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
