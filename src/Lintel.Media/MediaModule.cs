// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Media.Audio;
using Lintel.Media.Smtc;
using Lintel.Media.Views;

namespace Lintel.Media;

/// <summary>Now Playing: SMTC session, pill artwork + equalizer, Music section.</summary>
public sealed class MediaModule : INotchModule
{
    public const string EqualizerKey = "media.equalizer";        // bool, default true
    public const string HideAfterKey = "media.hideAfterSeconds"; // int, default 30; 0 = while any media exists

    public static ModuleManifest ManifestInfo { get; } = new()
    {
        Id = "media",
        Name = "Now Playing",
        Summary = "Artwork, title and transport for whatever Windows is playing, with an equalizer in the pill.",
        Glyph = Glyphs.Music,
        Category = ModuleCategory.Media,
        RequiredPermissions = [PermissionIds.MediaSession],
        OptionalPermissions = [PermissionIds.AudioSessions],
        Recommended = true,
        Order = 20,
    };

    private IModuleContext? context;
    private IMediaService? media;
    private LevelMeter? meter;
    private NowPlayingActivity? activity;
    private IReadOnlyList<NotchSection> sections = [];
    private IReadOnlyList<CompactActivity> activities = [];

    public ModuleManifest Manifest => ManifestInfo;
    public IReadOnlyList<NotchSection> Sections => sections;
    public IReadOnlyList<CompactActivity> Activities => activities;

    public void Start(IModuleContext ctx)
    {
        context = ctx;
        var service = ctx.IsDemo ? new DemoMediaService(ctx.Time) : (IMediaService)new MediaSessionService(ctx.Ui, ctx.Settings, ctx.Log);
        var levels = new LevelMeter(ctx.Ui, () => CreatePeakSource(ctx), () => service.Current?.SourceAppId, ctx.Log);
        media = service;
        meter = levels;
        activity = new NowPlayingActivity(service, levels, ctx.Settings, ctx.Time);
        activities = [activity];
        sections =
        [
            new NotchSection
            {
                Id = "music",
                Title = "Music",
                Glyph = Glyphs.Music,
                Order = 20,
                PreferredSize = new Size(460, 150),
                CreateView = () => new MusicSectionView(service, levels, ctx.Settings, ctx.Time),
            },
        ];
        service.Start();
    }

    public void Stop()
    {
        meter?.Dispose();
        media?.Dispose();
        meter = null;
        media = null;
    }

    public FrameworkElement? CreateSettingsView()
    {
        var settings = context?.Settings;
        if (settings is null)
            return null;
        var panel = new StackPanel();
        var equalizer = new CheckBox { Content = "Animate equalizer bars with the app's volume", IsChecked = settings.Get(EqualizerKey, true) };
        equalizer.Click += (_, _) => settings.Set(EqualizerKey, equalizer.IsChecked == true);
        panel.Children.Add(equalizer);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Keep in the pill after pausing for", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var delays = new (string Label, int Seconds)[] { ("10 seconds", 10), ("30 seconds", 30), ("1 minute", 60), ("5 minutes", 300), ("As long as media exists", 0) };
        var combo = new ComboBox { Width = 160, ItemsSource = delays.Select(d => d.Label).ToList() };
        var current = settings.Get(HideAfterKey, 30);
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(delays, d => d.Seconds == current));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                settings.Set(HideAfterKey, delays[combo.SelectedIndex].Seconds);
        };
        row.Children.Add(combo);
        panel.Children.Add(row);
        return panel;
    }

    public void DeleteData() => context?.Settings.RemovePrefix("media.");

    private static IPeakSource? CreatePeakSource(IModuleContext ctx)
    {
        if (ctx.IsDemo || !ctx.Consent.IsGranted(PermissionIds.AudioSessions))
            return null;
        try { return new WasapiPeakSource(); }
        catch (Exception e)
        {
            ctx.Log.Warn("Audio peak meter unavailable; using the gentle animation", e);
            return null;
        }
    }
}
