// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Settings;
using Lintel.Media.Audio;
using Lintel.Media.Logic;
using Lintel.Media.Smtc;

namespace Lintel.Media.Views;

/// <summary>Pill glance: rounded artwork on the left wing, tinted equalizer bars on the right. Active while playing and shortly after pausing.</summary>
internal sealed class NowPlayingActivity : CompactActivity
{
    private readonly IMediaService media;
    private readonly LevelMeter meter;
    private readonly ISettingsStore settings;
    private readonly TimeProvider time;
    private readonly DispatcherTimer expiry;
    private readonly Border art = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), ClipToBounds = true };
    private readonly EqualizerBars bars = new() { Width = 16, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border barsHost = new() { Width = 16, Height = 18 };
    private IDisposable? lease;
    private DateTimeOffset? pausedSince;
    private bool wasPlaying;

    public NowPlayingActivity(IMediaService media, LevelMeter meter, ISettingsStore settings, TimeProvider time)
        : base("media.nowplaying", 60, "music")
    {
        this.media = media;
        this.meter = meter;
        this.settings = settings;
        this.time = time;
        expiry = new DispatcherTimer(DispatcherPriority.Background);
        expiry.Tick += (_, _) => Recompute();
        barsHost.Child = bars;
        bars.IsVisibleChanged += (_, _) => UpdateLease();
        meter.LevelChanged += level => bars.Level = level;
        settings.Changed += key =>
        {
            if (key is MediaModule.EqualizerKey or MediaModule.HideAfterKey)
                bars.Dispatcher.BeginInvoke(Apply);
        };
        media.Changed += Apply;
        Apply();
    }

    public override FrameworkElement CreateLeading() => art;

    public override FrameworkElement? CreateTrailing() => barsHost;

    private bool EqualizerOn => settings.Get(MediaModule.EqualizerKey, true);

    private void Apply()
    {
        var now = media.Current;
        var playing = now?.IsPlaying == true;
        if (wasPlaying && !playing)
            pausedSince = time.GetUtcNow();
        if (playing)
            pausedSince = null;
        wasPlaying = playing;

        if (now?.Art is { } image)
        {
            art.Background = Ui.ArtBrush(image);
            art.Child = null;
        }
        else
        {
            art.Background = null;
            art.Child ??= Ui.GlyphTile(Glyphs.Music, 18, 5, 10);
        }
        var tint = Ui.Solid(now?.Tint ?? Colors.White);
        bars.Tint = tint;
        bars.IsActive = playing && EqualizerOn;
        barsHost.Visibility = EqualizerOn ? Visibility.Visible : Visibility.Collapsed;
        meter.SetPlaying(playing && EqualizerOn);
        UpdateLease();
        Recompute();
    }

    private void Recompute()
    {
        expiry.Stop();
        var hideAfter = TimeSpan.FromSeconds(settings.Get(MediaModule.HideAfterKey, 30));
        var now = time.GetUtcNow();
        var current = media.Current;
        IsActive = MediaMath.IsActivityActive(current is not null, current?.IsPlaying == true, pausedSince, now, hideAfter);
        if (IsActive && current?.IsPlaying != true && hideAfter > TimeSpan.Zero && pausedSince is { } since)
        {
            expiry.Interval = since + hideAfter - now + TimeSpan.FromMilliseconds(50);
            expiry.Start();
        }
    }

    private void UpdateLease()
    {
        var want = bars.IsVisible && EqualizerOn;
        if (want && lease is null)
            lease = meter.Acquire();
        else if (!want && lease is not null)
        {
            lease.Dispose();
            lease = null;
        }
    }
}
