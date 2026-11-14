// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Text;
using Lintel.Tools.Ui;

namespace Lintel.Tools.Timer;

public sealed class TimerModule : INotchModule
{
    public const string SoundKey = "timer.sound";
    private const string ModeKey = "timer.mode", StateKey = "timer.state", EndKey = "timer.endUtc", RemainingKey = "timer.remainingMs",
        TotalKey = "timer.totalMs", PhaseKey = "timer.phase", RoundKey = "timer.round";

    private IModuleContext? context;
    private TimerEngine? engine;
    private DispatcherTimer? ticker;
    private TimerActivity? activity;
    private IReadOnlyList<NotchSection> sections = [];
    private IReadOnlyList<CompactActivity> activities = [];
    private bool started;
    private MediaPlayer? player;

    public ModuleManifest Manifest { get; } = new()
    {
        Id = "timer",
        Name = "Timer",
        Summary = "Countdown, stopwatch and Pomodoro with an alarm.",
        Glyph = Glyphs.Timer,
        Category = ModuleCategory.Productivity,
        Recommended = true,
        Order = 40,
    };

    public IReadOnlyList<NotchSection> Sections => sections;
    public IReadOnlyList<CompactActivity> Activities => activities;

    internal TimerEngine Engine => engine ?? throw new InvalidOperationException("Timer module not started");
    internal IModuleContext Context => context!;

    /// <summary>Raised every second while a timer runs, and whenever the engine changes.</summary>
    internal event Action? Updated;

    /// <summary>Mode shown while idle (remembered across restarts).</summary>
    internal TimerMode SelectedMode
    {
        get => Engine.State is TimerState.Running or TimerState.Paused ? Engine.Mode
            : Enum.TryParse(context!.Settings.Get(ModeKey, nameof(TimerMode.Countdown)), out TimerMode mode) ? mode : TimerMode.Countdown;
        set
        {
            context!.Settings.Set(ModeKey, value.ToString());
            Updated?.Invoke();
        }
    }

    public void Start(IModuleContext ctx)
    {
        if (started)
            return;
        started = true;
        context = ctx;
        engine = new TimerEngine(ctx.Time);
        engine.Changed += OnChanged;
        engine.Completed += c => ctx.Ui.InvokeAsync(() => OnCompleted(c));
        ticker = new DispatcherTimer(DispatcherPriority.Normal, ctx.Ui) { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) =>
        {
            engine.Tick();
            Refresh();
        };
        activity = new TimerActivity();
        activities = [activity];
        sections =
        [
            new NotchSection
            {
                Id = "timer", Title = "Timer", Glyph = Glyphs.Timer, Order = 40,
                PreferredSize = new Size(460, 200),
                CreateView = () => new TimerSectionView(this),
            },
        ];

        if (ctx.IsDemo)
        {
            var now = ctx.Time.GetUtcNow();
            engine.Restore(new TimerSnapshot(TimerMode.Countdown, TimerState.Running, now + new TimeSpan(0, 14, 32), TimeSpan.Zero, TimeSpan.FromMinutes(25), PomodoroPhase.Focus, 1));
        }
        else
        {
            Restore();
        }
        ctx.Hotkeys.Register("timer.toggle", "", "Pause or resume the timer", () => ctx.Ui.InvokeAsync(() => engine.Toggle()));
        OnChanged();
    }

    public void Stop()
    {
        if (!started)
            return;
        started = false;
        ticker?.Stop();
        context!.Hotkeys.Unregister("timer.toggle");
        player?.Close();
        player = null;
        if (activity is not null)
            activity.IsActive = false;
        context.Settings.Flush();
    }

    public FrameworkElement? CreateSettingsView()
    {
        var sound = new CheckBox { Content = "Play a sound when the timer ends", Margin = new Thickness(0, 2, 0, 2) };
        // Settings may be opened before Start (module stopped), so read through the store lazily.
        sound.Loaded += (_, _) =>
        {
            if (context is null)
                return;
            sound.IsChecked = context.Settings.Get(SoundKey, true);
            sound.Checked += (_, _) => context.Settings.Set(SoundKey, true);
            sound.Unchecked += (_, _) => context.Settings.Set(SoundKey, false);
        };
        return sound;
    }

    public void DeleteData() => context?.Settings.RemovePrefix("timer.");

    private void OnChanged()
    {
        if (engine is null || context is null)
            return;
        if (!context.IsDemo)
            Persist();
        var running = engine.State == TimerState.Running;
        if (ticker is not null && ticker.IsEnabled != running)
        {
            if (running) ticker.Start();
            else ticker.Stop();
        }
        Refresh();
    }

    private void Refresh()
    {
        if (engine is null || activity is null)
            return;
        activity.IsActive = engine.State is TimerState.Running or TimerState.Paused;
        activity.Update(engine);
        Updated?.Invoke();
    }

    private void Persist()
    {
        var s = context!.Settings;
        var snap = engine!.Snapshot();
        s.Set(ModeKey, snap.Mode.ToString());
        if (snap.Mode == TimerMode.Stopwatch || snap.State is TimerState.Idle or TimerState.Finished)
        {
            foreach (var key in new[] { StateKey, EndKey, RemainingKey, TotalKey, PhaseKey, RoundKey })
                s.Remove(key);
            return;
        }
        s.Set(StateKey, snap.State.ToString());
        if (snap.EndUtc is { } end) s.Set(EndKey, end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        else s.Remove(EndKey);
        s.Set(RemainingKey, (long)snap.Remaining.TotalMilliseconds);
        s.Set(TotalKey, (long)snap.Total.TotalMilliseconds);
        s.Set(PhaseKey, snap.Phase.ToString());
        s.Set(RoundKey, snap.Round);
    }

    private void Restore()
    {
        var s = context!.Settings;
        if (!Enum.TryParse(s.Get(StateKey, ""), out TimerState state) || state is not (TimerState.Running or TimerState.Paused)
            || !Enum.TryParse(s.Get(ModeKey, ""), out TimerMode mode) || mode == TimerMode.Stopwatch)
            return;
        DateTimeOffset? end = DateTimeOffset.TryParse(s.Get(EndKey, ""), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        Enum.TryParse(s.Get(PhaseKey, ""), out PomodoroPhase phase);
        engine!.Restore(new TimerSnapshot(mode, state, end, TimeSpan.FromMilliseconds(s.Get(RemainingKey, 0L)),
            TimeSpan.FromMilliseconds(s.Get(TotalKey, 0L)), phase, s.Get(RoundKey, 1)));
    }

    private void OnCompleted(TimerCompletion c)
    {
        var notch = context!.Notch;
        NotchNotice notice;
        if (c.Mode == TimerMode.Pomodoro && c.Next is { } next)
        {
            var (title, message) = next == PomodoroPhase.Focus
                ? ("Break over", $"Focus {engine!.Round} of 4 starts now")
                : ("Focus complete", $"Take a {Format.Duration(TimerEngine.PhaseLength(next))} break");
            notice = new NotchNotice
            {
                Id = "timer.finished", Title = title, Message = message, Glyph = Glyphs.Timer, Tone = NoticeTone.Success,
                Duration = TimeSpan.FromSeconds(6), TargetSectionId = "timer", Sound = true, BalloonIfHidden = true,
            };
        }
        else
        {
            notice = new NotchNotice
            {
                Id = "timer.finished",
                Title = c.Mode == TimerMode.Pomodoro ? "Pomodoro cycle complete" : "Timer finished",
                Message = c.Mode == TimerMode.Pomodoro ? "Four focus sessions done. Nice work." : $"{Format.Duration(c.Length)} are up",
                Glyph = Glyphs.Timer, Tone = NoticeTone.Critical, Duration = TimeSpan.Zero, TargetSectionId = "timer", Sound = true, BalloonIfHidden = true,
            };
        }
        notch.ShowNotice(notice);
        if (context.Settings.Get(SoundKey, true))
            PlayAlarm();
    }

    private void PlayAlarm()
    {
        const string wav = @"C:\Windows\Media\Alarm01.wav";
        try
        {
            if (!File.Exists(wav))
                throw new FileNotFoundException(wav);
            player?.Close();
            player = new MediaPlayer();
            player.MediaFailed += (_, _) => SystemSounds.Exclamation.Play();
            player.MediaEnded += (_, _) =>
            {
                player?.Close();
                player = null;
            };
            player.Open(new Uri(wav));
            player.Play();
        }
        catch (Exception e)
        {
            context?.Log.Warn("Alarm sound failed; using the system sound", e);
            SystemSounds.Exclamation.Play();
        }
    }
}

/// <summary>Pill: a progress ring on the left wing, mm:ss on the right.</summary>
internal sealed class TimerActivity : CompactActivity
{
    private RingGauge? ring;
    private TextBlock? clock;

    public TimerActivity() : base("timer.running", 80, "timer") { }

    public override FrameworkElement CreateLeading()
    {
        ring = new RingGauge { Width = 14, Height = 14, Thickness = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        ring.SetResourceReference(RingGauge.StrokeProperty, "Notch.Accent");
        return ring;
    }

    public override FrameworkElement? CreateTrailing()
    {
        clock = Kit.Text("0:00", "Notch.Caption", "Notch.Text");
        clock.FontWeight = FontWeights.SemiBold;
        clock.HorizontalAlignment = HorizontalAlignment.Right;
        return clock;
    }

    public void Update(TimerEngine engine)
    {
        var paused = engine.State == TimerState.Paused;
        if (ring is not null)
        {
            ring.Value = engine.Progress * 100;
            ring.SetResourceReference(RingGauge.StrokeProperty, paused ? "Notch.TextTertiary"
                : engine.Mode == TimerMode.Pomodoro && engine.Phase != PomodoroPhase.Focus ? "Tone.Success" : "Notch.Accent");
        }
        if (clock is not null)
        {
            clock.Text = TimerSectionView.ClockText(engine);
            clock.Opacity = paused ? 0.6 : 1;
        }
    }
}
