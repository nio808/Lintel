// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lintel.Core.Controls;

namespace Lintel.Media.Views;

/// <summary>Small code-only helpers; styles and brushes are looked up by key so the host theme (or Preview) supplies them.</summary>
internal static class Ui
{
    public static TextBlock Text(string style, string text = "", string? brush = null, double? size = null)
    {
        var block = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (brush is not null)
            block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (size is { } s)
            block.FontSize = s;
        return block;
    }

    public static Button IconButton(string glyph, string tooltip, Action click)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.IconButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static ImageBrush ArtBrush(ImageSource art) => Freeze(new ImageBrush(art) { Stretch = Stretch.UniformToFill });

    public static SolidColorBrush Solid(Color color) => Freeze(new SolidColorBrush(color));

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Rounded glyph placeholder shown where artwork or an app icon is missing.</summary>
    public static Border GlyphTile(string glyph, double size, double radius, double glyphSize)
    {
        var icon = new TextBlock { Text = glyph, FontSize = glyphSize, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Notch.TextTertiary");
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = icon };
        tile.SetResourceReference(Border.BackgroundProperty, "Notch.Surface");
        return tile;
    }
}
