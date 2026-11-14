// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lintel.Core.Notch;
using Lintel.Notch.Layout;

namespace Lintel.Notch.Views;

/// <summary>One line inside the widened pill: tinted glyph, title, message.</summary>
internal sealed class NoticeView : Border
{
    private const double GlyphWidth = 14, Gap = 6;

    private readonly TextBlock glyph = new() { Width = GlyphWidth, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock message = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

    public NoticeView()
    {
        Height = NotchMetrics.NoticeHeight;
        Padding = new Thickness(NotchMetrics.NoticePadding, 0, NotchMetrics.NoticePadding, 0);
        Background = Brushes.Transparent;
        glyph.SetResourceReference(StyleProperty, "Notch.Icon");
        glyph.FontSize = 12;
        title.SetResourceReference(TextBlock.ForegroundProperty, "Notch.Text");
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Notch.Font");
        message.SetResourceReference(TextBlock.ForegroundProperty, "Notch.TextSecondary");
        message.SetResourceReference(TextBlock.FontFamilyProperty, "Notch.Font");
        message.Margin = new Thickness(Gap, 0, 0, 0);
        title.Margin = new Thickness(Gap, 0, 0, 0);
        row.Children.Add(glyph);
        row.Children.Add(title);
        row.Children.Add(message);
        Child = row;
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    public NotchNotice? Notice { get; private set; }

    /// <summary>Natural content width (before the 380 DIP clamp), excluding padding.</summary>
    public double NaturalWidth { get; private set; }

    public void Set(NotchNotice notice)
    {
        Notice = notice;
        glyph.Text = notice.Glyph;
        if (notice.Accent is { } accent)
            glyph.Foreground = accent;
        else
            glyph.SetResourceReference(TextBlock.ForegroundProperty, notice.Tone switch
            {
                NoticeTone.Success => "Tone.Success",
                NoticeTone.Warning => "Tone.Warning",
                NoticeTone.Critical => "Tone.Critical",
                _ => "Notch.Accent",
            });
        title.Text = notice.Title;
        message.Text = notice.Message ?? "";
        message.Visibility = string.IsNullOrEmpty(notice.Message) ? Visibility.Collapsed : Visibility.Visible;
        AutomationPropertiesName(notice);

        var room = NotchMetrics.NoticeMaxWidth - 2 * NotchMetrics.NoticePadding - GlyphWidth - Gap;
        title.MaxWidth = room;
        message.MaxWidth = double.PositiveInfinity;
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        message.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var titleWidth = Math.Min(title.DesiredSize.Width, room);
        var messageWidth = message.Visibility == Visibility.Visible ? message.DesiredSize.Width : 0;
        message.MaxWidth = Math.Max(0, room - titleWidth - Gap);
        NaturalWidth = GlyphWidth + titleWidth + (messageWidth > 0 ? Math.Min(messageWidth, message.MaxWidth) + Gap : 0);
    }

    private void AutomationPropertiesName(NotchNotice notice) =>
        System.Windows.Automation.AutomationProperties.SetName(this, notice.Message is null ? notice.Title : $"{notice.Title}. {notice.Message}");
}
