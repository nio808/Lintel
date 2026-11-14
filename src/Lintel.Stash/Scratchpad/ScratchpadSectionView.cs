// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Notch;

namespace Lintel.Stash;

/// <summary>Tab strip + monospace editor. All state lives in <see cref="ScratchpadStore"/>; this only renders and forwards edits.</summary>
internal sealed class ScratchpadSectionView : Grid, ISectionLifecycle
{
    private readonly ScratchpadStore store;
    private readonly string? folder;
    private readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox editor = new();
    private readonly ToggleButton wrap = new();
    private readonly DispatcherTimer disarm = new() { Interval = TimeSpan.FromSeconds(3) };
    private string selected = "";
    private string? armed;          // tab whose close button asked "Delete?"
    private bool loading;

    public ScratchpadSectionView(ScratchpadStore store, string? folder)
    {
        this.store = store;
        this.folder = folder;
        Margin = new Thickness(0, 2, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        wrap.Content = "Wrap";
        wrap.IsChecked = true;
        Skin(wrap, "Notch.PillButton");
        wrap.Padding = new Thickness(8, 2, 8, 2);
        wrap.MinHeight = 20;
        wrap.Click += (_, _) => ApplyWrap();
        var open = new Button { Content = Glyphs.Folder, ToolTip = "Open notes folder", Margin = new Thickness(4, 0, 0, 0) };
        Skin(open, "Notch.IconButton");
        open.Click += (_, _) => OpenFolder();
        right.Children.Add(wrap);
        right.Children.Add(open);
        bar.Children.Add(right);
        bar.Children.Add(tabStrip);
        SetRow(bar, 0);
        Children.Add(bar);

        editor.AcceptsReturn = true;
        editor.AcceptsTab = true;
        editor.VerticalContentAlignment = VerticalAlignment.Top;
        editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        editor.SetResourceReference(TextBox.FontFamilyProperty, "Notch.MonoFont");
        editor.FontSize = 11;   // mono body, one step above Notch.Body for legibility
        editor.Padding = new Thickness(8, 6, 8, 6);
        editor.SpellCheck.IsEnabled = false;
        editor.TextChanged += (_, _) => { if (!loading) store.SetText(selected, editor.Text); };
        SetRow(editor, 1);
        Children.Add(editor);
        ApplyWrap();

        disarm.Tick += (_, _) => { disarm.Stop(); armed = null; BuildTabs(); };
        selected = store.Tabs[0].Id;
        Load(selected);
        BuildTabs();
    }

    public void OnShown() => editor.Focus();

    public void OnHidden() => store.Flush();

    private static void Skin(FrameworkElement element, string key) => element.SetResourceReference(StyleProperty, key);

    private void ApplyWrap() => editor.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private void Load(string id)
    {
        loading = true;
        selected = id;
        editor.Text = store.GetText(id);
        editor.CaretIndex = editor.Text.Length;
        loading = false;
    }

    private void Select(string id)
    {
        if (id == selected)
            return;
        Load(id);
        BuildTabs();
        editor.Focus();
    }

    private void BuildTabs()
    {
        tabStrip.Children.Clear();
        var tabs = store.Tabs;
        foreach (var tab in tabs)
            tabStrip.Children.Add(BuildTab(tab, tabs.Count > 1));
        if (tabs.Count < ScratchpadStore.MaxTabs)
        {
            var add = new Button { Content = Glyphs.Add, ToolTip = "New note", Width = 22, Height = 22 };
            Skin(add, "Notch.IconButton");
            add.Click += (_, _) =>
            {
                if (store.AddTab() is { } created)
                {
                    Load(created.Id);
                    BuildTabs();
                    editor.Focus();
                }
            };
            tabStrip.Children.Add(add);
        }
    }

    private UIElement BuildTab(ScratchTab tab, bool canClose)
    {
        var isSelected = tab.Id == selected;
        var title = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis };
        Skin(title, "Notch.Body");
        title.SetResourceReference(TextBlock.ForegroundProperty, isSelected ? "Notch.Text" : "Notch.TextSecondary");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(title);

        var close = new Button { Width = 16, Height = 16, Margin = new Thickness(4, 0, -4, 0), Visibility = canClose ? Visibility.Visible : Visibility.Collapsed, ToolTip = "Close note" };
        Skin(close, "Notch.IconButton");
        close.FontSize = 9;   // keeps the close glyph small inside a 16 px button
        var needsConfirm = !string.IsNullOrWhiteSpace(store.GetText(tab.Id));
        if (armed == tab.Id)
        {
            close.Content = "Delete?";
            close.Width = double.NaN;
            close.Padding = new Thickness(6, 0, 6, 0);
            close.FontFamily = new FontFamily("Segoe UI");
            close.SetResourceReference(Control.ForegroundProperty, "Tone.Critical");
        }
        else
        {
            close.Content = Glyphs.Close;
        }
        close.Click += (_, e) =>
        {
            e.Handled = true;
            if (needsConfirm && armed != tab.Id)
            {
                armed = tab.Id;
                disarm.Stop();
                disarm.Start();
                BuildTabs();
                return;
            }
            CloseTab(tab.Id);
        };
        panel.Children.Add(close);

        var border = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0), Child = panel, Cursor = Cursors.Hand, ToolTip = "Double-click to rename" };
        border.SetResourceReference(Border.BackgroundProperty, isSelected ? "Notch.SurfaceHover" : "Notch.Surface");
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside<ButtonBase>(d))
                return;
            if (e.ClickCount == 2)
            {
                Select(tab.Id);
                Rename(border, tab);
            }
            else
            {
                Select(tab.Id);
            }
        };
        border.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
                CloseTab(tab.Id);
        };
        return border;
    }

    private void CloseTab(string id)
    {
        armed = null;
        disarm.Stop();
        var next = store.RemoveTab(id);
        if (next is null)
            return;
        Load(selected == id ? next : selected);
        BuildTabs();
    }

    private void Rename(Border host, ScratchTab tab)
    {
        var box = new TextBox { Text = tab.Title, MinWidth = 80, Padding = new Thickness(4, 0, 4, 0) };
        host.Child = box;
        box.Focus();
        box.SelectAll();
        var done = false;
        void Commit()
        {
            if (done)
                return;
            done = true;
            store.Rename(tab.Id, box.Text);
            BuildTabs();
            editor.Focus();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                if (e.Key == Key.Escape)
                    box.Text = tab.Title;
                Commit();
                e.Handled = true;
            }
        };
    }

    private void OpenFolder()
    {
        if (folder is null)
            return;
        store.Flush();
        ShellActions.OpenFolder(folder);
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T)
                return true;
        return false;
    }
}
