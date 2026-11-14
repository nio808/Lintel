// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Lintel.Core.Settings;

namespace Lintel.Core.Services;

public sealed record HotkeyBinding(string Id, string Description, string Gesture, bool IsRegistered);

public interface IHotkeyService : IDisposable
{
    /// <summary>Uses hotkeys.&lt;id&gt; if set, else defaultGesture ("" = unbound). False if Windows refused it.</summary>
    bool Register(string id, string defaultGesture, string description, Action callback);
    void Unregister(string id);

    /// <summary>Persists; "" clears.</summary>
    bool Rebind(string id, string gesture);
    IReadOnlyList<HotkeyBinding> Bindings { get; }
    event EventHandler? Changed;
}

/// <summary>Gesture grammar: [Ctrl+][Alt+][Shift+][Win+]&lt;Key&gt; where Key is a System.Windows.Input.Key name.</summary>
public static class HotkeyGesture
{
    public static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
            if (modifier == ModifierKeys.None || modifiers.HasFlag(modifier))
                return false;
            modifiers |= modifier;
        }
        var name = parts[^1];
        if (name.Length == 0 || char.IsAsciiDigit(name[0]) || name.Contains(',') || !Enum.TryParse(name, ignoreCase: true, out key)
            || !Enum.IsDefined(key) || IsModifierKey(key))
        {
            modifiers = ModifierKeys.None;
            key = Key.None;
            return false;
        }
        return true;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }

    /// <summary>Canonical spelling, "" for blank, null when invalid.</summary>
    public static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : TryParse(text, out var modifiers, out var key) ? Format(modifiers, key) : null;

    private static bool IsModifierKey(Key key) => key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}

/// <summary>RegisterHotKey on a message-only window. Create, use and dispose on the UI thread; callbacks run there.</summary>
public sealed class HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly ISettingsStore settings;
    private readonly IAppLog log;
    private readonly HwndSource source;
    private readonly Dictionary<string, Entry> entries = [];
    private int nextAtom = 1;

    public HotkeyService(ISettingsStore settings, IAppLog log)
    {
        this.settings = settings;
        this.log = log;
        source = new HwndSource(new HwndSourceParameters("Lintel.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HotkeyBinding> Bindings =>
        entries.Select(p => new HotkeyBinding(p.Key, p.Value.Description, p.Value.Gesture, p.Value.Registered)).ToList();

    public bool Register(string id, string defaultGesture, string description, Action callback)
    {
        if (entries.Remove(id, out var old))
            Unbind(old);
        var entry = new Entry(description, callback, nextAtom++);
        entries[id] = entry;
        var ok = Bind(id, entry, settings.Get(SettingKeys.Hotkey(id), defaultGesture));
        Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public void Unregister(string id)
    {
        if (!entries.Remove(id, out var entry))
            return;
        Unbind(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Rebind(string id, string gesture)
    {
        var normalized = HotkeyGesture.Normalize(gesture);
        if (normalized is null)
            return false;
        settings.Set(SettingKeys.Hotkey(id), normalized);
        if (!entries.TryGetValue(id, out var entry))
            return false;
        Unbind(entry);
        var ok = Bind(id, entry, normalized);
        Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values)
            Unbind(entry);
        entries.Clear();
        source.RemoveHook(WndProc);
        source.Dispose();
    }

    private bool Bind(string id, Entry entry, string gesture)
    {
        entry.Gesture = gesture;
        entry.Registered = false;
        if (gesture.Length == 0)
            return true;
        if (!HotkeyGesture.TryParse(gesture, out var modifiers, out var key))
        {
            log.Warn($"Hotkey {id}: invalid gesture '{gesture}'");
            return false;
        }
        uint native = ModNoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Alt)) native |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) native |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) native |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) native |= ModWin;
        entry.Registered = RegisterHotKey(source.Handle, entry.Atom, native, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!entry.Registered)
            log.Warn($"Hotkey {id}: {gesture} is in use by another app");
        return entry.Registered;
    }

    private void Unbind(Entry entry)
    {
        if (entry.Registered)
            UnregisterHotKey(source.Handle, entry.Atom);
        entry.Registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
            return IntPtr.Zero;
        var atom = wParam.ToInt32();
        var entry = entries.Values.FirstOrDefault(e => e.Atom == atom && e.Registered);
        if (entry is null)
            return IntPtr.Zero;
        handled = true;
        try
        {
            entry.Callback();
        }
        catch (Exception e)
        {
            log.Error("Hotkey callback failed", e);
        }
        return IntPtr.Zero;
    }

    private sealed class Entry(string description, Action callback, int atom)
    {
        public string Description { get; } = description;
        public Action Callback { get; } = callback;
        public int Atom { get; } = atom;
        public string Gesture { get; set; } = "";
        public bool Registered { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
