// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lintel.Stash;

/// <summary>
/// Message-only window with AddClipboardFormatListener. Debounces bursts (150 ms) and raises <see cref="Changed"/> with what the
/// policy needs (sequence, sensitive-format flags, owner process name). Contents are read separately by <see cref="ClipboardReader"/>.
/// </summary>
internal sealed class ClipboardListener : IDisposable
{
    private readonly Dispatcher ui;
    private readonly DispatcherTimer debounce;
    private HwndSource? source;
    private string? owner;

    public ClipboardListener(Dispatcher ui)
    {
        this.ui = ui;
        debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(150) };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            var change = await ClipboardReader.DescribeAsync(owner);
            if (source is not null && change is not null)
                Changed?.Invoke(change);
        };
    }

    public event Action<ClipboardChange>? Changed;

    public void Start()
    {
        var p = new HwndSourceParameters("Lintel.ClipboardListener") { ParentWindow = Native.HWND_MESSAGE, Width = 0, Height = 0, WindowStyle = 0 };
        source = new HwndSource(p);
        source.AddHook(WndProc);
        Native.AddClipboardFormatListener(source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            owner = OwnerName();          // the owner can be gone by the time the debounce fires
            debounce.Stop();
            debounce.Start();
        }
        return IntPtr.Zero;
    }

    private static string? OwnerName()
    {
        try
        {
            var hwnd = Native.GetClipboardOwner();
            if (hwnd == IntPtr.Zero || Native.GetWindowThreadProcessId(hwnd, out var pid) == 0)
                return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        debounce.Stop();
        if (source is { } s)
        {
            source = null;
            Native.RemoveClipboardFormatListener(s.Handle);
            s.RemoveHook(WndProc);
            s.Dispose();
        }
    }
}

/// <summary>Reads and writes the real clipboard (UI thread, retries while another app holds it open).</summary>
internal static class ClipboardReader
{
    private const int Attempts = 6;

    public static async Task<ClipboardChange?> DescribeAsync(string? owner)
    {
        var sequence = Native.GetClipboardSequenceNumber();
        var exclude = IsFormat(ClipboardPolicy.ExcludeFormat) || IsFormat(ClipboardPolicy.LegacyIgnoreFormat);
        int? can = null;
        if (IsFormat(ClipboardPolicy.CanIncludeFormat))
        {
            // Present but unreadable counts as 0: when in doubt, record nothing.
            can = 0;
            for (var i = 0; i < Attempts; i++)
            {
                if (TryReadDword(ClipboardPolicy.CanIncludeFormat, out var value))
                {
                    can = value;
                    break;
                }
                await Task.Delay(40);
            }
        }
        return new ClipboardChange(sequence, exclude, can, owner);
    }

    /// <summary>Text, then files, then image. Null when nothing usable (or still locked after the retries).</summary>
    public static async Task<ClipEntry?> ReadAsync(DateTimeOffset now)
    {
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                    return files.Length == 0 ? null : ClipEntry.FromFiles(files, now);
                }
                if (Clipboard.ContainsText())
                    return ClipEntry.FromText(Clipboard.GetText(), now);
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    if ((long)image.PixelWidth * image.PixelHeight > 16_000_000)
                        return null;       // would blow the 4 MB cap anyway; skip the encode
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    return ClipEntry.FromImage(ms.ToArray(), image.PixelWidth, image.PixelHeight, now);
                }
                return null;
            }
            catch (COMException e) when (e.HResult == Native.CLIPBRD_E_CANT_OPEN)
            {
                await Task.Delay(40);
            }
            catch (ExternalException)
            {
                await Task.Delay(40);
            }
        }
        return null;
    }

    /// <summary>Puts the entry on the clipboard and tells the policy so the resulting change is ignored. False if it could not be written.</summary>
    public static bool Write(ClipEntry entry, ClipboardPolicy policy)
    {
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                switch (entry.Kind)
                {
                    case ClipKind.Text:
                        Clipboard.SetText(entry.Text ?? "");
                        break;
                    case ClipKind.Files:
                        var list = new System.Collections.Specialized.StringCollection();
                        list.AddRange([.. entry.Files]);
                        Clipboard.SetFileDropList(list);
                        break;
                    default:
                        if (entry.Png is null)
                            return false;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(entry.Png);
                        bmp.EndInit();
                        Clipboard.SetImage(bmp);
                        break;
                }
                policy.NoteOwnWrite(Native.GetClipboardSequenceNumber());
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }

    private static bool IsFormat(string name)
    {
        var id = Native.RegisterClipboardFormat(name);
        return id != 0 && Native.IsClipboardFormatAvailable(id);
    }

    private static bool TryReadDword(string formatName, out int value)
    {
        value = 0;
        var id = Native.RegisterClipboardFormat(formatName);
        if (id == 0 || !Native.OpenClipboard(IntPtr.Zero))
            return false;
        try
        {
            var handle = Native.GetClipboardData(id);
            if (handle == IntPtr.Zero || Native.GlobalSize(handle) < 4)
                return handle != IntPtr.Zero;
            var ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
                return false;
            try { value = Marshal.ReadInt32(ptr); }
            finally { Native.GlobalUnlock(handle); }
            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }
}
