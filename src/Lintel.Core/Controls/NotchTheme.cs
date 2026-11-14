// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Lintel.Core.Controls;

/// <summary>Themes/NotchTheme.xaml: merge into the notch window's (or Preview's) resources, never into App resources.</summary>
public static class NotchTheme
{
    public static Uri Source { get; } = new("/Lintel.Core;component/Themes/NotchTheme.xaml", UriKind.Relative);

    public static ResourceDictionary Create() => new() { Source = Source };
}
