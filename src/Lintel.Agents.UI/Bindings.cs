// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lintel.Agents.UI;

/// <summary>
/// Visible when the value is "something": true, a non-empty string/list, a non-zero number, any other object.
/// A string parameter compares instead (Mode="cards"); a leading "!" inverts either test.
/// </summary>
internal sealed class Show : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = parameter as string ?? "";
        var invert = text.StartsWith('!');
        if (invert)
            text = text[1..];
        var visible = text.Length > 0
            ? string.Equals(value?.ToString(), text, StringComparison.Ordinal)
            : value switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                ICollection c => c.Count > 0,
                double d => d != 0 && !double.IsNaN(d),
                int i => i != 0,
                _ => true,
            };
        return visible != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>TextBlock text where `backticked` parts render as code (guidance like "run `codex login`").</summary>
internal static class Markup
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Markup), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        block.Inlines.Clear();
        var parts = ((string?)e.NewValue ?? "").Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;
            var run = new Run(parts[i]);
            if (i % 2 == 1)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "Notch.MonoFont");
                run.SetResourceReference(TextElement.ForegroundProperty, "Notch.Text");
                run.Background = CodeBackground;
            }
            block.Inlines.Add(run);
        }
    }

    private static readonly Brush CodeBackground = AgentDisplay.Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
}
