// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Notch;

namespace Lintel.Stash;

/// <summary>One row of the history list (display model for a <see cref="ClipEntry"/>).</summary>
public sealed class ClipRow(ClipEntry entry, string age, ImageSource? thumbnail)
{
    public ClipEntry Entry { get; } = entry;
    public string Age { get; } = age;
    public ImageSource? Thumbnail { get; } = thumbnail;
    public string Preview => Entry.Preview;
    public string KindGlyph => Entry.Kind switch { ClipKind.Files => Glyphs.Folder, ClipKind.Image => Glyphs.Camera, _ => Glyphs.Document };
    public Visibility TextVisibility => Entry.Kind == ClipKind.Image && Thumbnail is not null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ImageVisibility => Entry.Kind == ClipKind.Image && Thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinnedVisibility => Entry.Pinned ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class ClipboardSectionView : UserControl, ISectionLifecycle
{
    private readonly ClipboardModule module;
    private readonly Dictionary<Guid, ImageSource> thumbnails = [];
    private readonly DispatcherTimer copiedTimer;
    private bool shown;

    internal ClipboardSectionView(ClipboardModule module)
    {
        this.module = module;
        InitializeComponent();
        copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        copiedTimer.Tick += (_, _) => { copiedTimer.Stop(); CopiedText.Visibility = Visibility.Collapsed; };
        module.Store.Changed += (_, _) => { if (shown) Refresh(); };
        RefreshState();
        Refresh();
    }

    public void OnShown()
    {
        shown = true;
        Refresh();
        Search.Focus();
    }

    public void OnHidden()
    {
        shown = false;
        thumbnails.Clear();
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ClearButton.Visibility = Visibility.Visible;
    }

    /// <summary>Pause toggle follows the setting (it can also change in Settings).</summary>
    public void RefreshState()
    {
        PauseButton.IsChecked = module.Policy.Paused;
        PauseButton.ToolTip = module.Policy.Paused ? "Capture paused: click to resume" : "Pause capture";
        PauseButton.Content = module.Policy.Paused ? Glyphs.Play : Glyphs.Pause;
        UpdateEmpty();
    }

    private void Refresh()
    {
        var now = module.Time.GetUtcNow();
        var offset = ScrollOffset();
        var keep = (List.SelectedItem as ClipRow)?.Entry.Id;
        var rows = module.Store.Search(Search.Text).Select(e => new ClipRow(e, StashText.Ago(e.Created, now), Thumbnail(e))).ToList();
        List.ItemsSource = rows;
        if (keep is { } id && rows.FirstOrDefault(r => r.Entry.Id == id) is { } again)
            List.SelectedItem = again;
        UpdateEmpty();
        if (offset > 0)
            List.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindScroll()?.ScrollToVerticalOffset(offset));
    }

    private void UpdateEmpty()
    {
        var empty = List.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = module.Policy.Paused ? "Capture is paused" : Search.Text.Length > 0 ? "No matches" : "Nothing copied yet";
    }

    private ImageSource? Thumbnail(ClipEntry entry)
    {
        if (entry.Kind != ClipKind.Image || entry.Png is null)
            return null;
        if (thumbnails.TryGetValue(entry.Id, out var cached))
            return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 96;
            bmp.StreamSource = new MemoryStream(entry.Png);
            bmp.EndInit();
            bmp.Freeze();
            return thumbnails[entry.Id] = bmp;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private ScrollViewer? FindScroll() => FindChild<ScrollViewer>(List);

    private double ScrollOffset() => FindScroll()?.VerticalOffset ?? 0;

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded)
            Refresh();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Max(0, List.SelectedIndex);
            (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && List.Items.Count > 0)
        {
            Activate((List.SelectedItem ?? List.Items[0]) as ClipRow);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Activate(List.SelectedItem as ClipRow);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && List.SelectedItem is ClipRow row)
        {
            module.Store.Remove(row.Entry.Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && List.SelectedIndex <= 0)
        {
            Search.Focus();
            e.Handled = true;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        // Pin / delete buttons carry a Tag; anything else on the row copies it.
        for (var d = source; d is not null && d != List; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is Button { Tag: string action } button && button.DataContext is ClipRow target)
            {
                if (action == "pin") module.Store.Pin(target.Entry.Id, !target.Entry.Pinned);
                else module.Store.Remove(target.Entry.Id);
                e.Handled = true;
                return;
            }
            if (d is ListBoxItem { DataContext: ClipRow clicked })
            {
                Activate(clicked);
                return;
            }
        }
    }

    private void Activate(ClipRow? row)
    {
        if (row is null)
            return;
        module.Activate(row.Entry);
        CopiedText.Visibility = Visibility.Visible;
        copiedTimer.Stop();
        copiedTimer.Start();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => module.SetPaused(PauseButton.IsChecked == true);

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ClearButton.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
    }

    private void OnClearConfirm(object sender, RoutedEventArgs e)
    {
        module.Store.Clear(keepPinned: true);
        OnClearCancel(sender, e);
    }

    private void OnClearCancel(object sender, RoutedEventArgs e)
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ClearButton.Visibility = Visibility.Visible;
    }
}
