// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;

namespace Lintel.App.Settings.Pages;

/// <summary>Fixed title + scrolling body. Pages build their controls once and update them in <see cref="Refresh"/>.</summary>
internal abstract class SettingsPage : UserControl
{
    private readonly TextBlock title;
    private readonly TextBlock subtitle;

    protected SettingsPage(SettingsWindow host, AppServices services, string title, string? subtitle = null)
    {
        Host = host;
        S = services;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var header = new StackPanel { Margin = new Thickness(22, 2, 22, 12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        this.title = Ui.Text(title, 15, weight: FontWeights.SemiBold);
        this.subtitle = Ui.Secondary(subtitle ?? "", 10.5);
        this.subtitle.Margin = new Thickness(0, 1, 0, 0);
        this.subtitle.Visibility = subtitle is null ? Visibility.Collapsed : Visibility.Visible;
        header.Children.Add(this.title);
        header.Children.Add(this.subtitle);
        grid.Children.Add(header);
        Body = new StackPanel { Margin = new Thickness(22, 0, 22, 18), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Fills the width up to 804 DIP, then stays left-aligned (a star column with a maximum leaves the rest empty).
        var column = new Grid();
        column.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 804 });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        column.Children.Add(Body);
        var scroll = new ScrollViewer
        {
            Content = column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
    }

    protected SettingsWindow Host { get; }
    protected AppServices S { get; }
    protected StackPanel Body { get; }

    /// <summary>The whole scrolling body (for --screenshot of pages taller than the window).</summary>
    public FrameworkElement ScrollContent => Body;

    /// <summary>Re-read state from the services (called after module, consent, hotkey or update changes).</summary>
    public virtual void Refresh()
    {
    }

    protected void SetTitle(string text, string? sub)
    {
        title.Text = text;
        subtitle.Text = sub ?? "";
        subtitle.Visibility = sub is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A fixed bar under the scrolling body (onboarding buttons).</summary>
    protected void SetFooter(UIElement footer)
    {
        var grid = (Grid)Content;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new Border { Child = footer, Padding = new Thickness(22, 8, 22, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
        bar.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        bar.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        Grid.SetRow(bar, 2);
        grid.Children.Add(bar);
    }

    /// <summary>A row that navigates elsewhere in Settings.</summary>
    protected Grid LinkRow(Border tile, string text, string description, string target)
    {
        var row = Ui.Row(tile, text, description, Ui.Icon(Wpf.Ui.Controls.SymbolRegular.ChevronRight20, 10));
        row.Background = System.Windows.Media.Brushes.Transparent;
        row.Cursor = System.Windows.Input.Cursors.Hand;
        row.MouseLeftButtonUp += (_, _) => Host.Navigate(target);
        return row;
    }
}
