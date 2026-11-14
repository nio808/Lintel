// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lintel.Core.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace Lintel.App.Settings.Pages;

/// <summary>Global shortcuts: record, clear, reset; "in use by another app" and conflicts with our own bindings.</summary>
internal sealed class ShortcutsPage : SettingsPage
{
    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["notch.toggle"] = "Ctrl+Alt+N",
        ["notch.hide"] = "Ctrl+Alt+H",
        ["clipboard.open"] = "Ctrl+Alt+V",
    };

    private static readonly string[] Order = ["notch.toggle", "notch.hide", "clipboard.open", "timer.toggle", "keepawake.toggle", "agents.refresh"];

    private readonly StackPanel list = new();
    private readonly Dictionary<string, string> errors = [];
    private Button? recording;

    public ShortcutsPage(SettingsWindow host, AppServices s)
        : base(host, s, "Shortcuts", "Work from anywhere in Windows. Click a shortcut to record a new one.")
    {
        Body.Children.Add(list);
        Body.Children.Add(Ui.Header("While the notch has the keyboard"));
        Body.Children.Add(Ui.Columns(2,
        [
            Help(["Esc"], "Close, back to your app"),
            Help(["Ctrl", "Tab"], "Next section · Shift: previous"),
            Help(["Ctrl", "1…9"], "Jump to a section"),
            Help(["Ctrl", ","], "Open Settings"),
        ], 6));
        Refresh();
    }

    public override void Refresh()
    {
        if (recording is not null)
            return;   // don't yank the control the user is typing into
        list.Children.Clear();
        var bindings = S.Hotkeys.Bindings
            .OrderBy(b => Array.IndexOf(Order, b.Id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(b => b.Description, StringComparer.CurrentCulture)
            .ToList();
        if (bindings.Count == 0)
        {
            list.Children.Add(Ui.Group("Global", [Ui.Row(null, "No shortcuts yet", "They appear once the notch and your features are running", null)]));
            return;
        }
        list.Children.Add(Ui.Group("Global", bindings.Select(b => (UIElement)Row(b, bindings)),
            Ui.Tertiary("Features you install later add theirs here")));
    }

    private Grid Row(HotkeyBinding binding, List<HotkeyBinding> all)
    {
        // Our own conflict message wins; then Windows' refusal; otherwise the id as a quiet hint.
        var warning = errors.GetValueOrDefault(binding.Id)
            ?? (binding.Gesture.Length > 0 && !binding.IsRegistered ? "In use by another app — pick a different shortcut" : null);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(GestureBox(binding, all));
        var clear = Ui.IconButton(SymbolRegular.Dismiss16, "Clear", () => Apply(binding, "", all));
        clear.Visibility = binding.Gesture.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        clear.Margin = new Thickness(4, 0, 0, 0);
        controls.Children.Add(clear);
        if (Defaults.TryGetValue(binding.Id, out var fallback) && fallback != binding.Gesture)
            controls.Children.Add(Ui.IconButton(SymbolRegular.ArrowCounterclockwise20, $"Reset to {fallback}", () => Apply(binding, fallback, all)));
        var row = Ui.Row(Ui.Tile(SymbolRegular.Keyboard24, warning is null ? Palette.Indigo : Palette.Orange), binding.Description,
            warning, controls);
        if (warning is not null && row.Children[1] is StackPanel text && text.Children.Count > 1 && text.Children[1] is System.Windows.Controls.TextBlock line)
            Ui.SetColor(line, "SystemFillColorCautionBrush");
        return row;
    }

    private Button GestureBox(HotkeyBinding binding, List<HotkeyBinding> all)
    {
        var box = new Button { MinWidth = 112, MinHeight = 24, Padding = new Thickness(6, 2, 6, 2), Content = Keys(binding.Gesture) };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{binding.Description}: {(binding.Gesture.Length == 0 ? "not set" : binding.Gesture)}. Press to record.");
        box.Click += (_, _) =>
        {
            if (recording == box)
                return;
            recording = box;
            box.Content = Ui.Text("Press keys…", Ui.Body, "AccentTextFillColorPrimaryBrush", wrap: false);
            box.Focus();
            Keyboard.Focus(box);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (recording != box)
                return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                box.Content = Keys(HotkeyGesture.Format(modifiers, Key.None).Replace("+None", "").Replace("None", "") + "+…");
                return;
            }
            if (modifiers == ModifierKeys.None && key == Key.Escape)
            {
                Stop();
                return;
            }
            if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
            {
                Stop();
                Apply(binding, "", all);
                return;
            }
            var isFunctionKey = key >= Key.F1 && key <= Key.F24;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0 && !isFunctionKey)
            {
                box.Content = Ui.Text("Add Ctrl, Alt or Win", Ui.Body, "SystemFillColorCautionBrush", wrap: false);
                return;
            }
            Stop();
            Apply(binding, HotkeyGesture.Format(modifiers, key), all);
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (recording == box)
                Stop();
        };
        return box;

        void Stop()
        {
            recording = null;
            Refresh();
        }
    }

    private void Apply(HotkeyBinding binding, string gesture, List<HotkeyBinding> all)
    {
        errors.Remove(binding.Id);
        var other = gesture.Length == 0 ? null : all.FirstOrDefault(b => b.Id != binding.Id && b.Gesture.Equals(gesture, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            errors[binding.Id] = $"{gesture.Replace("+", " + ")} is already used by \"{other.Description}\"";
            Refresh();
            return;
        }
        S.Hotkeys.Rebind(binding.Id, gesture);   // a refusal shows as "in use by another app" from the binding itself
        Refresh();
    }

    private static FrameworkElement Keys(string gesture)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (gesture.Length == 0)
        {
            row.Children.Add(Ui.Tertiary("Not set", Ui.Body));
            return row;
        }
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries))
            row.Children.Add(KeyCap(part));
        return row;
    }

    public static Border KeyCap(string text)
    {
        var cap = new Border
        {
            Child = Ui.Text(text, 10, weight: FontWeights.SemiBold, wrap: false),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1, 1, 1, 2),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(2, 0, 0, 0),
            MinWidth = 20,
        };
        ((System.Windows.Controls.TextBlock)cap.Child).HorizontalAlignment = HorizontalAlignment.Center;
        cap.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        cap.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        return cap;
    }

    private static Border Help(string[] keys, string what)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in keys)
            caps.Children.Add(KeyCap(k));
        var row = Ui.Row(null, what, null, caps);
        row.MinHeight = 24;
        return Ui.Card(row, new Thickness(0));
    }
}
