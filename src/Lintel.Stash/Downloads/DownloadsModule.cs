// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Text;

namespace Lintel.Stash;

/// <summary>Downloads: shows in-progress browser downloads (bytes so far + speed) and says when one finishes.</summary>
internal sealed class DownloadsModule : StashModule
{
    public static readonly ModuleManifest Info = new()
    {
        Id = "downloads",
        Name = "Downloads",
        Summary = "Shows in-progress browser downloads and tells you when one finishes.",
        Glyph = Glyphs.Download,
        Category = ModuleCategory.Productivity,
        RequiredPermissions = [PermissionIds.DownloadsFolder],
        Order = 90,
    };

    private DownloadTracker tracker = null!;
    private DownloadsWatcher? watcher;
    private DownloadsActivity? activity;
    private DownloadsSectionView? view;
    private IReadOnlyList<NotchSection> sections = [];
    private IReadOnlyList<CompactActivity> activities = [];

    public override ModuleManifest Manifest => Info;

    public override IReadOnlyList<NotchSection> Sections => sections;

    public override IReadOnlyList<CompactActivity> Activities => activities;

    protected override void OnStart()
    {
        tracker = new DownloadTracker();
        activity = new DownloadsActivity(tracker);
        activities = [activity];
        sections =
        [
            new NotchSection
            {
                Id = "downloads",
                Title = "Downloads",
                Glyph = Glyphs.Download,
                CreateView = () => view ??= new DownloadsSectionView(tracker, Context.Time),
                PreferredSize = new Size(460, 180),
                Order = 90,
            },
        ];

        if (Context.IsDemo)
        {
            SeedDemo();
            return;
        }
        watcher = new DownloadsWatcher(Native.DownloadsFolder(), tracker, Context.Ui, Context.Time, OnFinished);
        if (!watcher.Start())
            Context.Log.Warn("downloads: Downloads folder not found; nothing to watch");
    }

    public override void Stop()
    {
        watcher?.Dispose();
        watcher = null;
        activity?.Detach();
    }

    private void OnFinished(FinishedDownload done)
    {
        Context.Notch.ShowNotice(new NotchNotice
        {
            Id = "downloads.done." + done.Path,
            Title = "Download finished",
            Message = $"{done.Name} · {Format.Bytes(done.Bytes)}",
            Glyph = Glyphs.Download,
            Tone = NoticeTone.Success,
            TargetSectionId = "downloads",
            OnClick = () => ShellActions.Reveal(done.Path),
        });
    }

    private void SeedDemo()
    {
        var now = Context.Time.GetUtcNow();
        void Active(string name, long bytes, double perSecond)
        {
            var path = @"C:\Users\you\Downloads\" + name + ".crdownload";
            tracker.TempSeen(path, Math.Max(0, bytes - (long)(perSecond * 4)), now - TimeSpan.FromSeconds(4));
            tracker.TempSeen(path, bytes, now);
        }
        Active("Visual-Studio-Installer.exe", 412L * 1024 * 1024, 5.8 * 1024 * 1024);
        Active("holiday-photos-2026.zip", 86L * 1024 * 1024, 1.4 * 1024 * 1024);
        tracker.SeedHistory(new FinishedDownload(@"C:\Users\you\Downloads\Quarterly report.pdf", "Quarterly report.pdf", 3_400_000, now - TimeSpan.FromMinutes(4)));
        tracker.SeedHistory(new FinishedDownload(@"C:\Users\you\Downloads\wallpaper-4k.png", "wallpaper-4k.png", 18_900_000, now - TimeSpan.FromMinutes(31)));
        tracker.SeedHistory(new FinishedDownload(@"C:\Users\you\Downloads\notes.txt", "notes.txt", 2_100, now - TimeSpan.FromHours(2)));
    }
}

/// <summary>Download glyph, an indeterminate meter and the total bytes so far while anything is downloading (priority 50).</summary>
internal sealed class DownloadsActivity : CompactActivity
{
    private readonly DownloadTracker tracker;
    private TextBlock? bytes;

    public DownloadsActivity(DownloadTracker tracker) : base("downloads.active", 50, "downloads")
    {
        this.tracker = tracker;
        tracker.Changed += OnChanged;
        OnChanged(null, EventArgs.Empty);
    }

    public void Detach() => tracker.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        var active = tracker.Active;
        IsActive = active.Count > 0;
        if (bytes is not null)
            bytes.Text = Format.Bytes(active.Sum(a => a.Bytes));
    }

    public override FrameworkElement CreateLeading()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock { Text = Glyphs.Download, Margin = new Thickness(0, 0, 4, 0) };
        glyph.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Icon");
        var meter = new MeterBar { Width = 20, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        meter.SetResourceReference(MeterBar.FillProperty, "Notch.Accent");
        bytes = new TextBlock { Text = Format.Bytes(tracker.Active.Sum(a => a.Bytes)), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        bytes.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Caption");
        bytes.SetResourceReference(TextBlock.ForegroundProperty, "Notch.Text");
        panel.Children.Add(glyph);
        panel.Children.Add(meter);
        panel.Children.Add(bytes);
        return panel;
    }
}
