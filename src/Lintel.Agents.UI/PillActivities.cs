// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lintel.Core.Agents;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Text;

namespace Lintel.Agents.UI;

/// <summary>
/// Idle pill (priority 20): concentric rings, outer Claude then Codex… (up to 3), plus an amber dot when a reading is
/// stale/blocked; trailing = the focus window as "5h 59%". Without any limits it falls back to a mark + today's API value.
/// </summary>
internal sealed class QuotaActivity : CompactActivity
{
    private const double Size = 20;
    private readonly AgentsUiState state;
    private readonly RingGauge[] rings = new RingGauge[3];
    private Grid? leading;
    private Ellipse? dot;
    private ProviderMark? mark;
    private TextBlock? label;
    private TextBlock? value;

    public QuotaActivity(AgentsUiState state) : base("agents.quota", 20, AgentsModule.Id)
    {
        this.state = state;
        state.Changed += Update;
        state.SettingChanged += _ => Update();
        Update();
    }

    public override FrameworkElement CreateLeading()
    {
        leading = new Grid { Width = Size, Height = Size, SnapsToDevicePixels = true };
        for (var i = 0; i < rings.Length; i++)
        {
            rings[i] = new RingGauge { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            leading.Children.Add(rings[i]);
        }
        mark = new ProviderMark { Width = 9, Height = 9, Visibility = Visibility.Collapsed };
        leading.Children.Add(mark);
        dot = new Ellipse
        {
            Width = 7, Height = 7, Stroke = Brushes.Black, StrokeThickness = 1.5, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1.5, -2.5, 0),
        };
        leading.Children.Add(dot);
        Update();
        return leading;
    }

    public override FrameworkElement? CreateTrailing()
    {
        label = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Notch.Font");
        value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 28, TextAlignment = TextAlignment.Right };
        value.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Numeric");
        value.FontSize = 12;   // the pill reads at a glance; 14 px is for panel numbers
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(label);
        panel.Children.Add(value);
        Update();
        return panel;
    }

    private void Update()
    {
        var now = state.Now;
        var used = state.ShowUsed;
        var limits = state.Limits.ToList();
        var perProvider = AgentDisplay.Providers
            .Select(p => AgentDisplay.Focus(limits.Where(l => l.Provider == p), state.Focus, now))
            .OfType<FocusReading>().Take(rings.Length).ToList();
        var focus = AgentDisplay.Focus(limits, state.Focus, now);
        var today = state.Snapshot.Periods.TryGetValue(AgentPeriod.Today, out var usage) ? usage : null;
        var seen = state.Snapshot.Seen.FirstOrDefault(state.Enabled);
        var fallback = focus is null && state.Snapshot.Seen.Any(state.Enabled);
        IsActive = focus is not null || fallback;

        if (leading is not null)
        {
            var thickness = perProvider.Count >= 3 ? 2 : 2.5;
            var gap = 1d;
            for (var i = 0; i < rings.Length; i++)
            {
                var ring = rings[i];
                if (i >= perProvider.Count)
                {
                    ring.Visibility = Visibility.Collapsed;
                    continue;
                }
                var reading = perProvider[i];
                var diameter = Size - i * 2 * (thickness + gap);
                ring.Width = ring.Height = diameter;
                ring.Thickness = thickness;
                ring.Stroke = AgentDisplay.ToneBrush(reading.Limits.Blocked ? LimitTone.Critical : AgentDisplay.Tone(reading.Window.RemainingPercent), reading.Limits.Provider);
                ring.Value = reading.Window.ResetDue(now) ? 0 : AgentDisplay.Shown(reading.Window, used);
                ring.Visibility = Visibility.Visible;
            }
            // One ring leaves room for the agent's mark in its middle; a fallback shows the mark alone.
            mark!.Visibility = perProvider.Count == 1 || fallback ? Visibility.Visible : Visibility.Collapsed;
            mark.Provider = perProvider.Count == 1 ? perProvider[0].Limits.Provider : seen;
            mark.Tint = AgentDisplay.Brush(mark.Provider);
            mark.Width = mark.Height = fallback ? 12 : 8;
            // one amber dot for "a reading needs attention" (stale or blocked); the ring itself shows how bad.
            dot!.Visibility = limits.Any(l => l.Blocked || l.IsStale) ? Visibility.Visible : Visibility.Collapsed;
            dot.Fill = AgentDisplay.Warning;
            leading.ToolTip = perProvider.Count == 0 ? null : string.Join(Environment.NewLine, perProvider.Select(r =>
                $"{AgentDisplay.AccountTitle(r.Limits.Provider, null)} {AgentDisplay.Label(r.Window)} · {Format.Percent(AgentDisplay.Shown(r.Window, used))} {(used ? "used" : "left")}"));
        }

        if (label is not null && value is not null)
        {
            if (focus is not null)
            {
                var tone = focus.Limits.Blocked ? LimitTone.Critical : AgentDisplay.Tone(focus.Window.RemainingPercent);
                label.Text = AgentDisplay.ShortLabel(focus.Window);
                label.Foreground = AgentDisplay.Brush(focus.Limits.Provider);
                label.Visibility = Visibility.Visible;
                value.Text = focus.Window.ResetDue(now) ? "—" : Format.Percent(AgentDisplay.Shown(focus.Window, used));
                value.Foreground = tone == LimitTone.Normal ? AgentDisplay.Text : AgentDisplay.ToneBrush(tone, focus.Limits.Provider);
            }
            else
            {
                label.Visibility = Visibility.Collapsed;
                value.Text = Format.Money(today?.Total.Cost ?? 0);
                value.Foreground = AgentDisplay.Text;
            }
        }
    }
}

/// <summary>
/// Working pill (priority 70): the working agents' marks + a pulsing dot; trailing = elapsed / tokens / cost / limit,
/// ticking once a second only while active and on screen.
/// </summary>
internal sealed class LiveActivity : CompactActivity
{
    private readonly AgentsUiState state;
    private readonly DispatcherTimer timer;
    private readonly ProviderMark[] marks = new ProviderMark[4];
    private PulseDot? pulse;
    private TextBlock? readout;
    private bool built;

    public LiveActivity(AgentsUiState state) : base("agents.live", 70, AgentsModule.Id)
    {
        this.state = state;
        timer = new DispatcherTimer(DispatcherPriority.Background, state.Context.Ui) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateReadout();
        state.Changed += Update;
        state.SettingChanged += _ => Update();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsActive))
                UpdateTimer();
        };
        Update();
    }

    public override FrameworkElement CreateLeading()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 20 };
        for (var i = 0; i < marks.Length; i++)
        {
            marks[i] = new ProviderMark { Provider = (AgentProvider)i, Tint = AgentDisplay.Brush((AgentProvider)i), Width = 12, Height = 12, Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed };
            panel.Children.Add(marks[i]);
        }
        pulse = new PulseDot { Width = 14, Height = 14, DotSize = 5, IsPulsing = true, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(pulse);
        built = true;
        Update();
        return panel;
    }

    public override FrameworkElement? CreateTrailing()
    {
        readout = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, MinWidth = 32 };
        readout.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Numeric");
        readout.FontSize = 12;
        readout.IsVisibleChanged += (_, _) => UpdateTimer();
        Update();
        return readout;
    }

    private void Update()
    {
        var working = state.Live.Select(s => s.Provider).Distinct().Order().ToList();
        IsActive = working.Count > 0;
        if (built)
        {
            for (var i = 0; i < marks.Length; i++)
                marks[i].Visibility = working.Contains((AgentProvider)i) && working.IndexOf((AgentProvider)i) < 2 ? Visibility.Visible : Visibility.Collapsed;
            pulse!.Fill = working.Count > 0 ? AgentDisplay.Brush(working[0]) : AgentDisplay.Accent;
        }
        UpdateReadout();
        UpdateTimer();
    }

    private void UpdateReadout()
    {
        if (readout is null)
            return;
        var first = state.Live.OrderBy(s => s.Started).FirstOrDefault();
        readout.Foreground = first is null ? AgentDisplay.Text : AgentDisplay.Brush(first.Provider);
        readout.Text = AgentDisplay.Readout(state.Snapshot with { Live = state.Live.ToList() }, state.Readout, state.ShowUsed, state.Focus, state.Now);
    }

    private void UpdateTimer()
    {
        var want = IsActive && readout is { IsVisible: true } && state.Readout == "elapsed";
        if (want != timer.IsEnabled)
            timer.IsEnabled = want;
    }
}
