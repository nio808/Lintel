// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lintel.App.Settings;

internal enum Tone { Neutral, Accent, Success, Caution, Critical }

/// <summary>Tile colours: one hue per feature / permission, white glyph on top.</summary>
internal static class Palette
{
    public static readonly Color Blue = Rgb(0x34, 0x78, 0xF6), Indigo = Rgb(0x58, 0x56, 0xD6), Purple = Rgb(0xA8, 0x55, 0xD8),
        Pink = Rgb(0xE2, 0x4A, 0x7E), Red = Rgb(0xE0, 0x4E, 0x4E), Orange = Rgb(0xEE, 0x8A, 0x2A), Amber = Rgb(0xD4, 0x9A, 0x0C),
        Green = Rgb(0x2E, 0xA8, 0x68), Teal = Rgb(0x1E, 0x9E, 0xB2), Coral = Rgb(0xD5, 0x76, 0x58), Gray = Rgb(0x76, 0x7F, 0x8C),
        Slate = Rgb(0x55, 0x66, 0x84);

    public static Color ForModule(string id) => id switch
    {
        "agents" => Coral, "media" => Pink, "mixer" => Purple, "timer" => Orange, "clipboard" => Blue, "system" => Green,
        "shelf" => Teal, "scratchpad" => Amber, "downloads" => Indigo, "keepawake" => Slate, "toggles" => Gray, _ => Blue,
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

/// <summary>
/// Dense Fluent building blocks: 11 px body, 10 px secondary text, 32 DIP rows, grouped cards with hairline dividers, small
/// coloured icon tiles, controls on the right. Brushes are resource references so light/dark switches apply live.
/// </summary>
internal static class Ui
{
    public const double Body = 11, Small = 10, RowHeight = 32, TileSize = 22;

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    [ThreadStatic]
    private static bool quiet;

    public static TextBlock Text(string text, double size = Body, string brush = "TextFillColorPrimaryBrush", FontWeight? weight = null, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static TextBlock Strong(string text, double size = Body) => Text(text, size, weight: FontWeights.SemiBold);

    public static TextBlock Secondary(string text, double size = Small) => Text(text, size, "TextFillColorSecondaryBrush");

    public static TextBlock Tertiary(string text, double size = Small) => Text(text, size, "TextFillColorTertiaryBrush");

    public static void SetColor(TextBlock block, string brush) => block.SetResourceReference(TextBlock.ForegroundProperty, brush);

    public static FontIcon Glyph(string glyph, double size = 12) => new() { Glyph = glyph, FontFamily = IconFont, FontSize = size };

    public static SymbolIcon Icon(SymbolRegular symbol, double size = 14) => new() { Symbol = symbol, FontSize = size };

    /// <summary>Solid rounded tile with a white icon (SymbolRegular or a Segoe Fluent glyph string).</summary>
    public static Border Tile(object icon, Color color, double size = TileSize, bool muted = false)
    {
        IconElement element = icon is SymbolRegular symbol ? Icon(symbol, Math.Round(size * 0.56)) : Glyph((string)icon, Math.Round(size * 0.5));
        element.Foreground = Brushes.White;
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        var fill = new LinearGradientBrush(Lighten(color, 0.12), color, 90);
        fill.Freeze();
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Round(size * 0.27)),
            Background = fill,
            Child = element,
            Opacity = muted ? 0.45 : 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>A titled card whose rows are separated by inset hairlines.</summary>
    public static StackPanel Group(string? title, IEnumerable<UIElement> rows, UIElement? headerTrailing = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (title is not null || headerTrailing is not null)
            panel.Children.Add(Header(title, headerTrailing));
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0)
                stack.Children.Add(Divider());
            stack.Children.Add(row);
        }
        panel.Children.Add(Card(stack, new Thickness(0)));
        return panel;
    }

    /// <summary>Section label above a card: 10.5 px semibold, secondary.</summary>
    public static TextBlock Heading(string text) => Text(text, 10.5, "TextFillColorSecondaryBrush", FontWeights.SemiBold, wrap: false);

    /// <summary>Section label with optional quiet text on the right (a count, a hint).</summary>
    public static Grid Header(string? title, UIElement? trailing = null)
    {
        var header = new Grid { Margin = new Thickness(2, 0, 2, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (title is not null)
            header.Children.Add(Heading(title));
        if (trailing is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(element, 1);
            header.Children.Add(element);
        }
        return header;
    }

    /// <summary>Equal-width columns of cards; cards in one row share its height (the gap is split across the cells' margins).</summary>
    public static Grid Columns(int count, IEnumerable<UIElement> cells, double gap = 8)
    {
        var grid = new Grid { Margin = new Thickness(-gap / 2, -gap / 2, -gap / 2, 14 - gap / 2) };
        for (var c = 0; c < count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        var i = 0;
        foreach (var cell in cells)
        {
            if (i % count == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (cell is FrameworkElement element)
                element.Margin = new Thickness(gap / 2);
            Grid.SetRow(cell, i / count);
            Grid.SetColumn(cell, i % count);
            grid.Children.Add(cell);
            i++;
        }
        return grid;
    }

    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = new Border
        {
            Child = content,
            Padding = padding ?? new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    public static Border Divider(double inset = 44)
    {
        var line = new Border { Height = 1, Margin = new Thickness(inset, 0, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    /// <summary>Tile · title + one-line secondary text · control on the right; 32 DIP plus a 6 DIP inset top and bottom.</summary>
    public static Grid Row(Border? tile, string title, string? subtitle, UIElement? trailing, UIElement? below = null)
    {
        var grid = new Grid { MinHeight = RowHeight, Margin = new Thickness(11, 6, 11, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (tile is not null)
        {
            tile.Margin = new Thickness(0, below is null ? 0 : 1, 11, 0);
            tile.VerticalAlignment = below is null ? VerticalAlignment.Center : VerticalAlignment.Top;
            grid.Children.Add(tile);
        }
        var text = new StackPanel { VerticalAlignment = below is null ? VerticalAlignment.Center : VerticalAlignment.Top };
        text.Children.Add(Text(title, Body, weight: FontWeights.Medium));
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(Secondary(subtitle));
        if (below is not null)
            text.Children.Add(below);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (trailing is FrameworkElement control)
        {
            control.Margin = new Thickness(12, 0, 0, 0);
            control.VerticalAlignment = below is null ? VerticalAlignment.Center : VerticalAlignment.Top;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        return grid;
    }

    /// <summary>Raises <paramref name="changed"/> when the user flips it (mouse, keyboard or UI Automation), not for <see cref="Set"/>.</summary>
    public static ToggleSwitch Toggle(bool on, Action<bool> changed, string? name = null)
    {
        var toggle = new ToggleSwitch { IsChecked = on };
        if (name is not null)
            System.Windows.Automation.AutomationProperties.SetName(toggle, name);
        toggle.Checked += (_, _) =>
        {
            if (!quiet)
                changed(true);
        };
        toggle.Unchecked += (_, _) =>
        {
            if (!quiet)
                changed(false);
        };
        return toggle;
    }

    /// <summary>Shows a state read from the services without treating it as a user change.</summary>
    public static void Set(ToggleButton toggle, bool on)
    {
        quiet = true;
        try
        {
            toggle.IsChecked = on;
        }
        finally
        {
            quiet = false;
        }
    }

    public static ComboBox Choice(IReadOnlyList<(string Value, string Label)> options, string current, Action<string> changed, double minWidth = 150)
    {
        var box = new ComboBox { MinWidth = minWidth, MaxWidth = 260 };
        foreach (var (value, label) in options)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var index = options.Select(o => o.Value).ToList().IndexOf(current);
        box.SelectedIndex = index < 0 ? 0 : index;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string value })
                changed(value);
        };
        return box;
    }

    public static Button Button(string text, Action click, bool primary = false, SymbolRegular? icon = null)
    {
        var button = new Button
        {
            Content = text,
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
            Padding = new Thickness(10, 3, 10, 4),
            FontSize = Body,
            MinHeight = 26,
        };
        if (icon is { } symbol)
            button.Icon = Icon(symbol, 12);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button IconButton(SymbolRegular icon, string tip, Action click)
    {
        var button = new Button
        {
            Icon = Icon(icon, 12),
            Appearance = ControlAppearance.Transparent,
            ToolTip = tip,
            Padding = new Thickness(5),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    public static HyperlinkButton Link(string text, string uri) =>
        new() { Content = text, NavigateUri = uri, Padding = new Thickness(6, 2, 6, 3), FontSize = Body };

    /// <summary>Status pill: optional dot + text on a tinted background.</summary>
    public static Border Pill(string text, Tone tone, bool dot = false)
    {
        var (background, foreground) = tone switch
        {
            Tone.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            Tone.Caution => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            Tone.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            Tone.Accent => ("SubtleFillColorSecondaryBrush", "AccentTextFillColorPrimaryBrush"),
            _ => ("SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot)
        {
            var ellipse = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            ellipse.SetResourceReference(Shape.FillProperty, foreground);
            row.Children.Add(ellipse);
        }
        var label = Text(text, 9.5, foreground, FontWeights.SemiBold, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var border = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 0, 6, 1),
            Child = row,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        border.SetResourceReference(Border.BackgroundProperty, background);
        return border;
    }

    /// <summary>Small outlined chip (permission names, "used by").</summary>
    public static Border Chip(string text, string? tooltip = null, bool muted = false)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(0, 3, 3, 0),
            Child = Text(text, 9.5, muted ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush", wrap: false),
            ToolTip = tooltip,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush");
        return border;
    }

    /// <summary>Small glyph + caption, e.g. "Stays on this PC".</summary>
    public static Grid Fact(string text, string brushKey, string glyph, double size = Small)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Glyph(glyph, size);
        icon.Margin = new Thickness(0, 1, 4, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.SetResourceReference(IconElement.ForegroundProperty, brushKey);
        row.Children.Add(icon);
        var label = Text(text, size, "TextFillColorSecondaryBrush");
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    /// <summary>Slim tinted banner (success / caution) with an optional action on the right.</summary>
    public static Border Banner(string text, Tone tone, UIElement? action = null)
    {
        var (background, foreground, glyph) = tone == Tone.Success
            ? ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush", "")
            : ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush", "");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph(glyph, 12);
        icon.SetResourceReference(IconElement.ForegroundProperty, foreground);
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var label = Text(text, Body);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (action is FrameworkElement control)
        {
            control.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        var border = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 8, 6), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12),
        };
        border.SetResourceReference(Border.BackgroundProperty, background);
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    public static void Open(string target, Core.Services.IAppLog? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Warn($"Could not open {target}", e);
        }
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}
