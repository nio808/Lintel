// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Settings;
using Lintel.Core.Text;
using Lintel.Media.Audio;
using Lintel.Media.Logic;
using Lintel.Media.Smtc;

namespace Lintel.Media.Views;

/// <summary>Music section (460 x 150): artwork with tint glow, title block, scrubber, transport, source chip.</summary>
internal sealed class MusicSectionView : Grid, ISectionLifecycle
{
    private readonly IMediaService media;
    private readonly LevelMeter meter;
    private readonly ISettingsStore settings;
    private readonly TimeProvider time;
    private readonly DispatcherTimer tick;

    private readonly Rectangle glow = new() { Width = 190, Height = 190, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-48, 0, 0, 0) };
    private readonly Border art = new() { Width = 92, Height = 92, CornerRadius = new CornerRadius(12), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = Ui.Text("Notch.Title", size: 14);
    private readonly TextBlock artist = Ui.Text("Notch.Body", brush: "Notch.TextSecondary");
    private readonly TextBlock album = Ui.Text("Notch.Caption", brush: "Notch.TextTertiary");
    private readonly TextBlock elapsed = Ui.Text("Notch.Caption");
    private readonly TextBlock remaining = Ui.Text("Notch.Caption");
    private readonly Slider scrub = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button play = new();
    private readonly Button prev, next;
    private readonly ToggleButton shuffle = new(), repeat = new();
    private readonly Button source = new();
    private readonly EqualizerBars bars = new() { Width = 16, Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Grid content = new();
    private readonly StackPanel empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private IDisposable? lease;
    private bool dragging, updating, shown;
    private Brush accent = Brushes.White;

    public MusicSectionView(IMediaService media, LevelMeter meter, ISettingsStore settings, TimeProvider time)
    {
        this.media = media;
        this.meter = meter;
        this.settings = settings;
        this.time = time;
        ClipToBounds = true;
        prev = Ui.IconButton(Glyphs.Previous, "Previous", media.Previous);
        next = Ui.IconButton(Glyphs.Next, "Next", media.Next);
        tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        tick.Stop();

        glow.Fill = Ui.Freeze(new RadialGradientBrush(Color.FromArgb(0x4D, 0x7C, 0x9C, 0xFF), Colors.Transparent) { RadiusX = 0.5, RadiusY = 0.5 });
        art.SetResourceReference(Border.BackgroundProperty, "Notch.Surface");

        BuildTransport();
        BuildContent();
        BuildEmpty();
        Children.Add(glow);
        Children.Add(content);
        Children.Add(empty);

        meter.LevelChanged += level => bars.Level = level;
        media.Changed += Refresh;
        settings.Changed += key =>
        {
            if (key == MediaModule.EqualizerKey)
                Dispatcher.BeginInvoke(UpdateBars);
        };
        Refresh();
    }

    public void OnShown()
    {
        shown = true;
        UpdateBars();
        Tick();
        tick.Start();
    }

    public void OnHidden()
    {
        shown = false;
        tick.Stop();
        lease?.Dispose();
        lease = null;
    }

    private void BuildTransport()
    {
        play.SetResourceReference(StyleProperty, "Notch.IconButton");
        play.Width = play.Height = 32;
        play.FontSize = 14;
        play.Resources["Notch.ButtonRadius"] = new CornerRadius(16);
        play.SetResourceReference(Control.BackgroundProperty, "Notch.Text");
        play.SetResourceReference(Control.ForegroundProperty, "Notch.Background");
        play.Click += (_, _) => media.TogglePlayPause();

        foreach (var toggle in new[] { shuffle, repeat })
        {
            toggle.SetResourceReference(StyleProperty, "Notch.IconButton");
        }
        shuffle.Content = Glyphs.Shuffle;
        shuffle.ToolTip = "Shuffle";
        shuffle.Click += (_, _) => media.SetShuffle(shuffle.IsChecked == true);
        repeat.ToolTip = "Repeat";
        repeat.Click += (_, _) =>
        {
            var current = media.Current?.Repeat ?? RepeatMode.None;
            media.SetRepeat(current switch { RepeatMode.None => RepeatMode.List, RepeatMode.List => RepeatMode.Track, _ => RepeatMode.None });
        };

        source.SetResourceReference(StyleProperty, "Notch.PillButton");
        source.Padding = new Thickness(8, 2, 8, 2);
        source.MinHeight = 20;
        source.MaxWidth = 150;
        source.Click += (_, _) => OpenSourceMenu();
    }

    private void BuildContent()
    {
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        art.Margin = new Thickness(0, 0, 14, 0);
        content.Children.Add(art);

        var right = new Grid { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 1);
        for (var i = 0; i < 4; i++)
            right.RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        top.Children.Add(bars);
        top.Children.Add(source);
        right.Children.Add(top);

        var text = new StackPanel { Margin = new Thickness(0, 4, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(artist);
        text.Children.Add(album);
        Grid.SetRow(text, 1);
        right.Children.Add(text);

        var line = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        elapsed.VerticalAlignment = remaining.VerticalAlignment = VerticalAlignment.Center;
        remaining.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(scrub, 1);
        Grid.SetColumn(remaining, 2);
        line.Children.Add(elapsed);
        line.Children.Add(scrub);
        line.Children.Add(remaining);
        Grid.SetRow(line, 2);
        right.Children.Add(line);
        scrub.ValueChanged += (_, _) =>
        {
            if (!updating)
                elapsed.Text = Format.Clock(TimeSpan.FromSeconds(scrub.Value));
        };
        scrub.PreviewMouseLeftButtonDown += (_, _) => dragging = true;
        scrub.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (!dragging)
                return;
            dragging = false;
            media.Seek(TimeSpan.FromSeconds(scrub.Value));
        }), true);

        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var element in new UIElement[] { shuffle, prev, play, next, repeat })
        {
            ((FrameworkElement)element).Margin = new Thickness(2, 0, 2, 0);
            ((FrameworkElement)element).VerticalAlignment = VerticalAlignment.Center;
            transport.Children.Add(element);
        }
        Grid.SetRow(transport, 3);
        right.Children.Add(transport);
        content.Children.Add(right);
    }

    private void BuildEmpty()
    {
        var tile = Ui.GlyphTile(Glyphs.Music, 40, 10, 18);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(tile);
        var heading = Ui.Text("Notch.Title", "Nothing playing");
        heading.Margin = new Thickness(0, 8, 0, 2);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(heading);
        var hint = Ui.Text("Notch.Caption", "Play something and it shows up here.");
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(hint);
    }

    private void Refresh()
    {
        var now = media.Current;
        content.Visibility = now is null ? Visibility.Collapsed : Visibility.Visible;
        glow.Visibility = now is null ? Visibility.Collapsed : Visibility.Visible;
        empty.Visibility = now is null ? Visibility.Visible : Visibility.Collapsed;
        if (now is null)
        {
            UpdateBars();
            return;
        }

        title.Text = string.IsNullOrWhiteSpace(now.Title) ? "Unknown title" : now.Title;
        artist.Text = now.Artist;
        artist.Visibility = now.Artist.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        album.Text = now.Album;
        album.Visibility = now.Album.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (now.Art is { } image)
        {
            art.Background = Ui.ArtBrush(image);
            art.Child = null;
        }
        else
        {
            art.SetResourceReference(Border.BackgroundProperty, "Notch.Surface");
            art.Child = Ui.GlyphTile(Glyphs.Music, 92, 12, 28);
        }

        var tint = now.Tint ?? Color.FromRgb(0x7C, 0x9C, 0xFF);
        accent = Ui.Solid(tint);
        glow.Fill = Ui.Freeze(Glow(tint));
        scrub.Foreground = accent;
        bars.Tint = accent;

        play.Content = now.IsPlaying ? Glyphs.Pause : Glyphs.Play;
        play.ToolTip = now.IsPlaying ? "Pause" : "Play";
        prev.IsEnabled = now.CanPrev;
        next.IsEnabled = now.CanNext;
        shuffle.IsEnabled = now.CanShuffle;
        shuffle.IsChecked = now.Shuffle == true;
        repeat.IsEnabled = now.CanRepeat;
        repeat.IsChecked = now.Repeat is RepeatMode.List or RepeatMode.Track;
        repeat.Content = now.Repeat == RepeatMode.Track ? RepeatOneGlyph() : Glyphs.Repeat;
        repeat.ToolTip = now.Repeat switch { RepeatMode.List => "Repeat all", RepeatMode.Track => "Repeat one", _ => "Repeat" };

        scrub.IsEnabled = now.CanSeek;
        updating = true;
        scrub.Maximum = Math.Max(1, now.Duration.TotalSeconds);
        updating = false;
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var sourceText = Ui.Text("Notch.Caption", now.SourceName, "Notch.Text");
        sourceText.MaxWidth = 110;
        label.Children.Add(sourceText);
        if (media.Sources.Count > 1)
        {
            var chevron = new TextBlock { Text = Glyphs.ChevronDown, Margin = new Thickness(4, 1, 0, 0) };
            chevron.SetResourceReference(StyleProperty, "Notch.Icon");
            chevron.FontSize = 8;
            label.Children.Add(chevron);
        }
        source.Content = label;
        source.IsEnabled = media.Sources.Count > 1;
        UpdateBars();
        Tick();
    }

    private static Brush Glow(Color tint) => Ui.Freeze(new RadialGradientBrush(
    [
        new GradientStop(Color.FromArgb(0x55, tint.R, tint.G, tint.B), 0),
        new GradientStop(Color.FromArgb(0x1C, tint.R, tint.G, tint.B), 0.55),
        new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1),
    ]));

    private static UIElement RepeatOneGlyph()
    {
        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = Glyphs.Repeat });
        grid.Children.Add(new TextBlock { Text = "1", FontFamily = new FontFamily("Segoe UI"), FontSize = 7, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }

    private void UpdateBars()
    {
        var on = settings.Get(MediaModule.EqualizerKey, true) && media.Current is not null;
        bars.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        bars.IsActive = on && media.Current?.IsPlaying == true;
        var want = shown && on;
        if (want && lease is null)
            lease = meter.Acquire();
        else if (!want && lease is not null)
        {
            lease.Dispose();
            lease = null;
        }
    }

    /// <summary>Position is interpolated locally between system updates.</summary>
    private void Tick()
    {
        if (media.Current is not { } now || dragging)
            return;
        var position = now.PositionAtTime(time.GetUtcNow());
        updating = true;
        scrub.Value = Math.Min(position.TotalSeconds, scrub.Maximum);
        updating = false;
        elapsed.Text = Format.Clock(position);
        remaining.Text = now.Duration > TimeSpan.Zero ? "-" + Format.Clock(now.Duration - position) : "";
    }

    private void OpenSourceMenu()
    {
        var menu = new ContextMenu { PlacementTarget = source, Placement = PlacementMode.Bottom };
        foreach (var item in media.Sources)
        {
            var entry = new MenuItem { Header = item.Name, IsCheckable = true, IsChecked = item.IsCurrent };
            var id = item.Id;
            entry.Click += (_, _) => media.Select(id);
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        var auto = new MenuItem { Header = "Automatic" };
        auto.Click += (_, _) => media.Select(null);
        menu.Items.Add(auto);
        menu.IsOpen = true;
    }
}
