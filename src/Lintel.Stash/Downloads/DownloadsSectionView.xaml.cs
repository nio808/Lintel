// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Text;

namespace Lintel.Stash;

public sealed record DownloadRow(string Name, string Detail, string Path);

public sealed partial class DownloadsSectionView : UserControl, ISectionLifecycle
{
    private readonly DownloadTracker tracker;
    private readonly TimeProvider time;
    private bool shown;

    internal DownloadsSectionView(DownloadTracker tracker, TimeProvider time)
    {
        this.tracker = tracker;
        this.time = time;
        InitializeComponent();
        tracker.Changed += (_, _) => { if (shown) Refresh(); };
        Refresh();
    }

    public void OnShown()
    {
        shown = true;
        Refresh();
    }

    public void OnHidden() => shown = false;

    private void Refresh()
    {
        var now = time.GetUtcNow();
        var active = tracker.Active.Select(a =>
        {
            var speed = StashText.Speed(a.BytesPerSecond);
            return new DownloadRow(a.Name, Format.Bytes(a.Bytes) + (speed.Length > 0 ? " · " + speed : ""), a.Path);
        }).ToList();
        var recent = tracker.History.Select(h => new DownloadRow(h.Name, $"{Format.Bytes(h.Bytes)} · {StashText.Ago(h.Finished, now)}", h.Path)).ToList();
        ActiveList.ItemsSource = active;
        RecentList.ItemsSource = recent;
        ActiveHeader.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Visibility = recent.Count > 0 && active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = active.Count == 0 && recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string path)
            ShellActions.Reveal(path);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string path)
            ShellActions.Open(path);
    }
}
