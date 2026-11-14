// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Lintel.Core.Controls;

namespace Lintel.Tools.Toggles;

/// <summary>DWORD registry values (HKCU) behind an interface so the theme mapping can be tested.</summary>
public interface IRegistryDword
{
    int? Get(string subKey, string name);
    void Set(string subKey, string name, int value);
}

public sealed class CurrentUserRegistry : IRegistryDword
{
    public int? Get(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as int?;
    }

    public void Set(string subKey, string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}

/// <summary>Dark/light mode: AppsUseLightTheme (apps) and SystemUsesLightTheme (taskbar, Start); 0 = dark.</summary>
public sealed class DarkModeToggle(IRegistryDword registry, Action broadcast) : IToggleAction
{
    public const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public string Id => "darkmode";
    public string Title => "Dark";
    public string Glyph => Glyphs.Moon;
    public ToggleKind Kind => ToggleKind.Switch;

    public static bool? IsDark(int? appsUseLightTheme) => appsUseLightTheme is { } v ? v == 0 : null;

    public bool? ReadState() => IsDark(registry.Get(Key, "AppsUseLightTheme"));

    public void Execute()
    {
        var light = ReadState() == true ? 1 : 0;   // dark → light, anything else → dark
        registry.Set(Key, "AppsUseLightTheme", light);
        registry.Set(Key, "SystemUsesLightTheme", light);
        broadcast();
    }

    public static void BroadcastThemeChange() =>
        Native.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x2, 200, out _);   // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
}

/// <summary>Show/hide desktop icons by sending Explorer's own "Show desktop icons" command (0x7402) to the desktop view.</summary>
public sealed class DesktopIconsToggle : IToggleAction
{
    public string Id => "desktopicons";
    public string Title => "Icons";
    public string Glyph => Glyphs.Desktop;
    public ToggleKind Kind => ToggleKind.Switch;

    public bool? ReadState()
    {
        var view = FindDefView();
        if (view == IntPtr.Zero)
            return null;
        var list = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        return list == IntPtr.Zero ? null : Native.IsWindowVisible(list);
    }

    public void Execute()
    {
        var view = FindDefView();
        if (view != IntPtr.Zero)
            Native.SendMessage(view, 0x0111, new IntPtr(0x7402), IntPtr.Zero);   // WM_COMMAND
    }

    private static IntPtr FindDefView()
    {
        var view = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero)
            return view;
        // With a wallpaper slideshow / Win11 the view lives under a WorkerW sibling.
        Native.EnumWindows((hwnd, _) =>
        {
            var child = Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero)
                view = child;
            return child == IntPtr.Zero;
        }, IntPtr.Zero);
        return view;
    }
}

/// <summary>Mutes/unmutes the default recording device.</summary>
public sealed class MicrophoneToggle : IToggleAction
{
    public string Id => "mic";
    public string Title => "Mic";
    public string Glyph => Glyphs.Mic;
    public ToggleKind Kind => ToggleKind.Switch;

    /// <summary>"On" = the microphone is live (not muted).</summary>
    public bool? ReadState()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return !device.AudioEndpointVolume.Mute;
    }

    public void Execute()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            return;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }
}

public sealed class LockToggle : IToggleAction
{
    public string Id => "lock";
    public string Title => "Lock";
    public string Glyph => Glyphs.Lock;
    public ToggleKind Kind => ToggleKind.Action;
    public bool? ReadState() => null;
    public void Execute() => Native.LockWorkStation();
}

public sealed class UriToggle(string id, string title, string glyph, string uri, ToggleKind kind = ToggleKind.Link) : IToggleAction
{
    public string Id => id;
    public string Title => title;
    public string Glyph => glyph;
    public ToggleKind Kind => kind;
    public bool? ReadState() => null;
    public void Execute() => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
}

public sealed class RecycleBinToggle : IToggleAction
{
    public string Id => "recyclebin";
    public string Title => "Bin";
    public string Glyph => Glyphs.Trash;
    public ToggleKind Kind => ToggleKind.Confirm;
    public bool? ReadState() => null;

    public void Execute()
    {
        // NOCONFIRMATION | NOPROGRESSUI | NOSOUND: the second click was the confirmation. 0x8000_000A (S_FALSE on an already empty bin) is fine.
        Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
    }
}

public static class WindowsToggles
{
    public static IReadOnlyList<IToggleAction> Create() =>
    [
        new DarkModeToggle(new CurrentUserRegistry(), DarkModeToggle.BroadcastThemeChange),
        new DesktopIconsToggle(),
        new MicrophoneToggle(),
        new LockToggle(),
        new UriToggle("snip", "Snip", Glyphs.Camera, "ms-screenclip:", ToggleKind.Action),
        new RecycleBinToggle(),
        new UriToggle("nightlight", "Night", Glyphs.Sun, "ms-settings:nightlight"),
        new UriToggle("bluetooth", "Bluetooth", Glyphs.Bluetooth, "ms-settings:bluetooth"),
        new UriToggle("focus", "Focus", Glyphs.Bell, "ms-settings:quiethours"),
        new UriToggle("display", "Display", Glyphs.Settings, "ms-settings:display"),
    ];
}
