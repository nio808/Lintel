// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lintel.Core.Controls;
using Lintel.Core.Notch;

namespace Lintel.Stash;

public sealed class ShelfTile(ShelfItem item, bool missing, ImageSource? icon)
{
    public ShelfItem Item { get; } = item;
    public bool Missing { get; } = missing;
    public ImageSource? Icon { get; } = icon;
    public string Name => Item.Name;
    public double Opacity => Missing ? 0.4 : 1;
    public string Tooltip => Missing ? Item.Value + " (missing)" : Item.Kind == ShelfKind.Text ? Item.Value.Length > 300 ? Item.Value[..300] + "…" : Item.Value : Item.Value;
    public string Glyph => Item.Kind == ShelfKind.Link ? Glyphs.Globe : Glyphs.Document;
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class ShelfSectionView : UserControl, ISectionLifecycle
{
    private readonly ShelfStore store;
    private readonly Dictionary<string, ImageSource?> icons = [];
    private Point dragStart;
    private bool dragging;

    internal ShelfSectionView(ShelfStore store)
    {
        this.store = store;
        InitializeComponent();
        store.Changed += (_, _) => Refresh();
        Refresh();
    }

    public void OnShown() => Refresh();     // re-check which files still exist

    public void OnHidden() { }

    /// <summary>NotchSection.AcceptsDrop: files, text or links.</summary>
    public static bool Accepts(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);

    private void Refresh()
    {
        var tiles = store.Items.Select(i => new ShelfTile(i, store.IsMissing(i), IconFor(i))).ToList();
        Tiles.ItemsSource = tiles;
        Summary.Text = tiles.Count == 0 ? "" : $"{tiles.Count} item{(tiles.Count == 1 ? "" : "s")} · drag out to use";
        ClearButton.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        if (tiles.Count == 0)
            EmptyState.Visibility = Visibility.Visible;
        else
            ClearButton.Visibility = Visibility.Visible;
    }

    private ImageSource? IconFor(ShelfItem item)
    {
        if (item.Kind != ShelfKind.File)
            return null;
        var key = item.IsFolder ? "<folder>" : Path.GetExtension(item.Value).ToLowerInvariant();
        if (!icons.TryGetValue(key, out var icon))
            icons[key] = icon = Native.ShellIcon(item.IsFolder ? "folder" : "file" + (key.Length == 0 ? ".txt" : key), item.IsFolder);
        return icon;
    }

    // --- drop onto the shelf ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = !dragging && Accepts(e.Data);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (dragging)
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Reverse())
                store.Add(ShelfKind.File, file, Directory.Exists(file));
        }
        else if ((e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) is string text && !string.IsNullOrWhiteSpace(text))
        {
            text = text.Trim();
            var isLink = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" && !text.Contains(' ') && !text.Contains('\n');
            store.Add(isLink ? ShelfKind.Link : ShelfKind.Text, text);
        }
    }

    // --- drag out of the shelf / click to open ---

    private void OnTileDown(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(this);

    private void OnTileMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragging || sender is not FrameworkElement { DataContext: ShelfTile tile })
            return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (tile.Missing)
            return;
        var data = new DataObject();
        if (tile.Item.Kind == ShelfKind.File)
            data.SetData(DataFormats.FileDrop, new[] { tile.Item.Value });
        else
            data.SetText(tile.Item.Value);
        dragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            dragging = false;
            Refresh();       // a Move leaves the original missing
        }
    }

    private void OnTileUp(object sender, MouseButtonEventArgs e)
    {
        // A click without a drag: open (files/links) or copy (text).
        if (dragging || e.OriginalSource is DependencyObject d && IsInside<Button>(d) || sender is not FrameworkElement { DataContext: ShelfTile tile })
            return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - dragStart.X) > 4 || Math.Abs(p.Y - dragStart.Y) > 4)
            return;
        Open(tile);
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T)
                return true;
        return false;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ShelfTile tile)
            store.Remove(tile.Item.Id);
        e.Handled = true;
    }

    private void OnClear(object sender, RoutedEventArgs e) => store.Clear();

    private void OnTileMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShelfTile tile } element)
            return;
        var menu = new ContextMenu { PlacementTarget = element };
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }
        var kind = tile.Item.Kind;
        menu.Items.Add(Item(kind == ShelfKind.Text ? "Copy text" : "Open", () => Open(tile), !tile.Missing));
        if (kind == ShelfKind.File)
            menu.Items.Add(Item("Show in folder", () => ShellActions.Reveal(tile.Item.Value), !tile.Missing));
        if (kind == ShelfKind.Link)
            menu.Items.Add(Item("Copy link", () => TryCopy(tile.Item.Value)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () => store.Remove(tile.Item.Id)));
        element.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void Open(ShelfTile tile)
    {
        if (tile.Missing)
            return;
        switch (tile.Item.Kind)
        {
            case ShelfKind.Text:
                TryCopy(tile.Item.Value);
                break;
            default:
                ShellActions.Open(tile.Item.Value);
                break;
        }
    }

    private static void TryCopy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
}
