// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Text;
using Lintel.Tools.Ui;

namespace Lintel.Tools.Timer;

/// <summary>460 × 200: ring + clock on the left; mode, presets / controls on the right.</summary>
internal sealed class TimerSectionView : Grid, ISectionLifecycle
{
    private static readonly int[] Presets = [1, 5, 10, 15, 25, 45, 60];

    private readonly TimerModule module;
    private readonly RingGauge ring = new() { Width = 104, Height = 104, Thickness = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock clock = Kit.Text("0:00", "Notch.Title");
    private readonly TextBlock caption = Kit.Text("", "Notch.Caption");
    private readonly ListBox modes = new();
    private readonly StackPanel idlePane = new();
    private readonly StackPanel runPane = new();
    private readonly TextBox custom = new() { Width = 76, MinHeight = 24, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Minutes, mm:ss or h:mm:ss (up to 23:59:59)" };
    private readonly TextBlock customError = Kit.Text("Use minutes, mm:ss or h:mm:ss, up to 23:59:59.", "Notch.Caption", "Tone.Warning");
    private readonly Button startButton;
    private readonly TextBlock idleHint = Kit.Text("", "Notch.Caption");
    private readonly WrapPanel presetPanel = new();
    private readonly StackPanel customRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button pauseButton;
    private readonly Button plusButton;
    private readonly Button lapButton;
    private readonly TextBlock lapsText = Kit.Text("", "Notch.Caption");
    private readonly TextBlock phaseDots = Kit.Text("", "Notch.Caption");
    private bool shown;
    private bool updatingModes;

    public TimerSectionView(TimerModule module)
    {
        this.module = module;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Margin = new Thickness(0, 2, 0, 0);

        // Left: ring with the clock inside.
        ring.SetResourceReference(RingGauge.StrokeProperty, "Notch.Accent");
        clock.FontSize = 20;
        clock.FontWeight = FontWeights.SemiBold;
        clock.HorizontalAlignment = HorizontalAlignment.Center;
        clock.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        inner.Children.Add(clock);
        inner.Children.Add(caption);
        var left = new Grid();
        left.Children.Add(ring);
        left.Children.Add(inner);
        Children.Add(left);

        // Right column.
        var right = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(right, 1);
        Children.Add(right);

        modes.SetResourceReference(StyleProperty, "Notch.Segmented");
        modes.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (var name in new[] { "Countdown", "Stopwatch", "Pomodoro" })
            modes.Items.Add(name);
        modes.SelectionChanged += (_, _) =>
        {
            if (updatingModes || modes.SelectedIndex < 0)
                return;
            module.SelectedMode = (TimerMode)modes.SelectedIndex;
        };
        right.Children.Add(modes);

        // Idle pane: presets + custom, or a single Start.
        foreach (var minutes in Presets)
        {
            var chip = Kit.PillButton(minutes + "m", () => StartCountdown(TimeSpan.FromMinutes(minutes)));
            chip.Padding = new Thickness(8, 2, 8, 2);
            chip.Margin = new Thickness(0, 0, 4, 4);
            presetPanel.Children.Add(chip);
        }
        idleHint.TextWrapping = TextWrapping.Wrap;
        idleHint.Margin = new Thickness(0, 0, 0, 6);
        startButton = Kit.PillButton("Start", StartFromIdle, accent: true);
        startButton.HorizontalAlignment = HorizontalAlignment.Left;
        custom.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                StartCustom();
        };
        custom.TextChanged += (_, _) => customError.Visibility = Visibility.Collapsed;
        var customStart = Kit.PillButton("Start", StartCustom, accent: true);
        customStart.Margin = new Thickness(6, 0, 0, 0);
        customRow.Children.Add(Kit.Text("Custom", "Notch.Caption"));
        ((FrameworkElement)customRow.Children[0]).Margin = new Thickness(0, 0, 6, 0);
        customRow.Children.Add(custom);
        customRow.Children.Add(customStart);
        customError.Visibility = Visibility.Collapsed;
        customError.Margin = new Thickness(0, 4, 0, 0);
        idlePane.Margin = new Thickness(0, 8, 0, 0);
        idlePane.Children.Add(idleHint);
        idlePane.Children.Add(presetPanel);
        idlePane.Children.Add(startButton);
        idlePane.Children.Add(customRow);
        idlePane.Children.Add(customError);
        right.Children.Add(idlePane);

        // Run pane: pause/resume, +1 min, lap, cancel.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        pauseButton = Kit.PillButton("Pause", () => module.Engine.Toggle(), accent: true);
        pauseButton.MinWidth = 64;
        plusButton = Kit.PillButton("+1 min", () => module.Engine.AddTime(TimeSpan.FromMinutes(1)));
        plusButton.Margin = new Thickness(4, 0, 0, 0);
        lapButton = Kit.PillButton("Lap", () => module.Engine.Lap());
        lapButton.Margin = new Thickness(4, 0, 0, 0);
        var cancel = Kit.IconButton(Glyphs.Close, "Cancel", () => module.Engine.Cancel());
        cancel.Margin = new Thickness(4, 0, 0, 0);
        buttons.Children.Add(pauseButton);
        buttons.Children.Add(plusButton);
        buttons.Children.Add(lapButton);
        buttons.Children.Add(cancel);
        phaseDots.Margin = new Thickness(0, 8, 0, 0);
        lapsText.Margin = new Thickness(0, 6, 0, 0);
        runPane.Margin = new Thickness(0, 10, 0, 0);
        runPane.Children.Add(buttons);
        runPane.Children.Add(phaseDots);
        runPane.Children.Add(lapsText);
        right.Children.Add(runPane);
    }

    /// <summary>mm:ss clock for the engine's current state (shared with the pill).</summary>
    public static string ClockText(TimerEngine engine) => engine.Mode == TimerMode.Stopwatch
        ? Format.Clock(TimeSpan.FromSeconds(Math.Floor(engine.Elapsed.TotalSeconds)))
        : Format.Clock(TimeSpan.FromSeconds(Math.Ceiling(engine.Remaining.TotalSeconds)));

    public void OnShown()
    {
        shown = true;
        module.Updated += Refresh;
        Refresh();
    }

    public void OnHidden()
    {
        shown = false;
        module.Updated -= Refresh;
    }

    private void StartFromIdle()
    {
        switch (module.SelectedMode)
        {
            case TimerMode.Stopwatch: module.Engine.StartStopwatch(); break;
            case TimerMode.Pomodoro: module.Engine.StartPomodoro(); break;
        }
    }

    private void StartCountdown(TimeSpan duration) => module.Engine.StartCountdown(duration);

    private void StartCustom()
    {
        if (TimerEngine.ParseDuration(custom.Text) is { } duration)
        {
            custom.Clear();
            StartCountdown(duration);
        }
        else
        {
            customError.Visibility = Visibility.Visible;
        }
    }

    private void Refresh()
    {
        if (!shown)
            return;
        var engine = module.Engine;
        var active = engine.State is TimerState.Running or TimerState.Paused;
        var mode = module.SelectedMode;

        updatingModes = true;
        modes.SelectedIndex = (int)mode;
        updatingModes = false;
        modes.IsEnabled = !active;

        idlePane.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        runPane.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        presetPanel.Visibility = customRow.Visibility = mode == TimerMode.Countdown ? Visibility.Visible : Visibility.Collapsed;
        startButton.Visibility = mode == TimerMode.Countdown ? Visibility.Collapsed : Visibility.Visible;
        idleHint.Text = mode switch
        {
            TimerMode.Countdown => "Pick a duration.",
            TimerMode.Stopwatch => "Counts up, with laps.",
            _ => "25 focus, 5 break, x4, then 15 off",
        };

        var dim = engine.State == TimerState.Idle || engine.State == TimerState.Finished;
        clock.Text = dim && !active
            ? Format.Clock(mode == TimerMode.Pomodoro ? TimerEngine.PhaseLength(PomodoroPhase.Focus) : TimeSpan.Zero)
            : ClockText(engine);
        clock.SetResourceReference(TextBlock.ForegroundProperty, engine.State == TimerState.Paused ? "Notch.TextSecondary" : dim && engine.State == TimerState.Idle ? "Notch.TextTertiary" : "Notch.Text");
        ring.Value = active || engine.State == TimerState.Finished ? (engine.State == TimerState.Finished ? 100 : engine.Progress * 100) : 0;
        var breakPhase = engine.Mode == TimerMode.Pomodoro && engine.Phase != PomodoroPhase.Focus && active;
        ring.SetResourceReference(RingGauge.StrokeProperty, engine.State == TimerState.Paused ? "Notch.TextTertiary" : breakPhase || engine.State == TimerState.Finished ? "Tone.Success" : "Notch.Accent");

        caption.Text = engine.State switch
        {
            TimerState.Paused => "Paused",
            TimerState.Finished => "Finished",
            TimerState.Idle => mode.ToString(),
            _ => engine.Mode switch
            {
                TimerMode.Pomodoro => engine.Phase switch { PomodoroPhase.Focus => $"Focus {engine.Round} of 4", PomodoroPhase.ShortBreak => "Short break", _ => "Long break" },
                TimerMode.Stopwatch => "Stopwatch",
                _ => $"of {Format.Duration(engine.Total)}",
            },
        };

        pauseButton.Content = engine.State == TimerState.Paused ? "Resume" : "Pause";
        plusButton.Visibility = engine.Mode == TimerMode.Stopwatch ? Visibility.Collapsed : Visibility.Visible;
        lapButton.Visibility = engine.Mode == TimerMode.Stopwatch ? Visibility.Visible : Visibility.Collapsed;
        lapButton.IsEnabled = engine.State == TimerState.Running;
        phaseDots.Visibility = engine.Mode == TimerMode.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
        phaseDots.Text = string.Concat(Enumerable.Range(1, 4).Select(i => i < engine.Round || (i == engine.Round && engine.Phase != PomodoroPhase.Focus) ? "● " : "○ ")).TrimEnd();
        var laps = engine.Laps;
        lapsText.Visibility = laps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        lapsText.Text = string.Join("\n", laps.Select((l, i) => $"Lap {i + 1}   {Format.Clock(l)}").TakeLast(3));
    }
}
