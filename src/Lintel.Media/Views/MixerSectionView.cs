// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Media.Audio;
using Lintel.Media.Logic;

namespace Lintel.Media.Views;

/// <summary>Mixer section (460 x 220): master row, then one row per app (icon, name, slider with peak meter, mute).</summary>
internal sealed class MixerSectionView : Grid, ISectionLifecycle
{
    private sealed class Row
    {
        public required Grid Root;
        public required Slider Slider;
        public required TextBlock Percent;
        public required Button Mute;
        public required MeterBar Meter;
        public required Border Icon;
        public required TextBlock Name;
        public readonly PeakSmoother Smoother = new(0.6, 0.25);
        public bool Updating;
    }

    private readonly IMixerService mixer;
    private readonly Slider masterSlider = new() { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5 };
    private readonly TextBlock masterPercent = Ui.Text("Notch.Numeric");
    private readonly TextBlock deviceName = Ui.Text("Notch.Caption");
    private readonly Button masterMute;
    private readonly StackPanel list = new();
    private readonly TextBlock emptyText = Ui.Text("Notch.Caption", "No apps are playing audio.");
    private readonly Dictionary<string, Row> rows = [];
    private bool masterUpdating;
    private bool lastMasterMuted;

    public MixerSectionView(IMixerService mixer)
    {
        this.mixer = mixer;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        masterMute = Ui.IconButton(Glyphs.Volume, "Mute", () => mixer.SetMasterMute(!lastMasterMuted));
        var master = new Border { Margin = new Thickness(0, 0, 0, 6) };
        master.SetResourceReference(StyleProperty, "Notch.Card");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        masterMute.Margin = new Thickness(0, 0, 6, 0);
        masterMute.VerticalAlignment = VerticalAlignment.Center;
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = new Grid();
        label.Children.Add(Ui.Text("Notch.Title", "Master"));
        deviceName.HorizontalAlignment = HorizontalAlignment.Right;
        deviceName.MaxWidth = 240;
        label.Children.Add(deviceName);
        middle.Children.Add(label);
        middle.Children.Add(masterSlider);
        Grid.SetColumn(middle, 1);
        masterPercent.TextAlignment = TextAlignment.Right;
        masterPercent.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(masterPercent, 2);
        grid.Children.Add(masterMute);
        grid.Children.Add(middle);
        grid.Children.Add(masterPercent);
        master.Child = grid;
        Children.Add(master);

        masterSlider.ValueChanged += (_, _) =>
        {
            masterPercent.Text = $"{masterSlider.Value:0}%";
            if (!masterUpdating)
                mixer.SetMaster(masterSlider.Value / 100);
        };

        emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        emptyText.Margin = new Thickness(0, 16, 0, 0);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        mixer.Changed += Sync;
        Sync();
    }

    public void OnShown() => mixer.SetVisible(true);

    public void OnHidden() => mixer.SetVisible(false);

    private void Sync()
    {
        var state = mixer.Current;
        deviceName.Text = state.DeviceName;
        lastMasterMuted = state.MasterMuted;
        masterMute.Content = state.MasterMuted || state.Master <= 0 ? Glyphs.Mute : Glyphs.Volume;
        masterSlider.IsEnabled = state.HasDevice;
        masterMute.IsEnabled = state.HasDevice;
        if (!masterSlider.IsMouseCaptureWithin && Math.Abs(masterSlider.Value - state.Master * 100) > 0.5)
        {
            masterUpdating = true;
            masterSlider.Value = state.Master * 100;
            masterUpdating = false;
        }
        masterPercent.Text = $"{state.Master * 100:0}%";
        masterPercent.Opacity = masterSlider.Opacity = state.MasterMuted ? 0.45 : 1;

        // Rows are keyed so a slider being dragged is never rebuilt; only the order and the set of rows change.
        var seen = new HashSet<string>();
        for (var i = 0; i < state.Apps.Count; i++)
        {
            var app = state.Apps[i];
            seen.Add(app.Key);
            if (!rows.TryGetValue(app.Key, out var row))
            {
                row = CreateRow(app);
                rows[app.Key] = row;
            }
            Update(row, app);
            if (i >= list.Children.Count || !ReferenceEquals(list.Children[i], row.Root))
            {
                list.Children.Remove(row.Root);
                list.Children.Insert(i, row.Root);
            }
        }
        foreach (var gone in rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            list.Children.Remove(rows[gone].Root);
            rows.Remove(gone);
        }
        if (state.Apps.Count == 0 && !list.Children.Contains(emptyText))
            list.Children.Add(emptyText);
        else if (state.Apps.Count > 0)
            list.Children.Remove(emptyText);
    }

    private Row CreateRow(AppRow app)
    {
        var root = new Grid { Margin = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Height = 30 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5) };
        var name = Ui.Text("Notch.Body");
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(name, 1);

        var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5 };
        var meter = new MeterBar { Margin = new Thickness(0, -4, 6, 0) };
        meter.SetResourceReference(MeterBar.FillProperty, "Notch.Accent");
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(slider);
        middle.Children.Add(meter);
        Grid.SetColumn(middle, 2);

        var percent = Ui.Text("Notch.Numeric");
        percent.TextAlignment = TextAlignment.Right;
        percent.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(percent, 3);

        var key = app.Key;
        Row? self = null;
        var mute = Ui.IconButton(Glyphs.Volume, "Mute", () => mixer.SetAppMute(key, !(self!.Mute.Tag is true)));
        mute.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(mute, 4);

        foreach (var element in new UIElement[] { icon, name, middle, percent, mute })
            root.Children.Add(element);
        self = new Row { Root = root, Slider = slider, Percent = percent, Mute = mute, Meter = meter, Icon = icon, Name = name };
        slider.ValueChanged += (_, _) =>
        {
            percent.Text = $"{slider.Value:0}%";
            if (!self.Updating)
                mixer.SetApp(key, slider.Value / 100);
        };
        return self;
    }

    private static void Update(Row row, AppRow app)
    {
        row.Name.Text = app.Name;
        row.Name.ToolTip = app.Name;
        row.Name.Opacity = app.IsActive ? 1 : 0.6;
        if (app.Icon is { } image)
        {
            if (row.Icon.Background is not ImageBrush brush || !ReferenceEquals(brush.ImageSource, image))
            {
                row.Icon.Background = Ui.ArtBrush(image);
                row.Icon.Child = null;
            }
        }
        else if (row.Icon.Child is null)
        {
            row.Icon.Background = null;
            var letter = Ui.Text("Notch.Caption", app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?");
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            letter.VerticalAlignment = VerticalAlignment.Center;
            letter.FontWeight = FontWeights.SemiBold;
            row.Icon.Child = letter;
            row.Icon.SetResourceReference(Border.BackgroundProperty, "Notch.SurfaceHover");
        }

        var percent = app.Volume * 100;
        if (!row.Slider.IsMouseCaptureWithin && Math.Abs(row.Slider.Value - percent) > 0.5)
        {
            row.Updating = true;
            row.Slider.Value = percent;
            row.Updating = false;
        }
        row.Percent.Text = $"{percent:0}%";
        row.Mute.Content = app.Muted ? Glyphs.Mute : Glyphs.Volume;
        row.Mute.Tag = app.Muted;
        row.Slider.Opacity = row.Percent.Opacity = row.Meter.Opacity = app.Muted ? 0.45 : 1;
        row.Meter.Value = row.Smoother.Next(app.Muted ? 0 : MediaMath.PeakToLevel(app.Peak));
    }
}
