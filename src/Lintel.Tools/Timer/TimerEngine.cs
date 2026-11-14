// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Tools.Timer;

public enum TimerMode { Countdown, Stopwatch, Pomodoro }
public enum TimerState { Idle, Running, Paused, Finished }
public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

/// <summary>Raised when a countdown ends or a Pomodoro phase ends. For Pomodoro, Next is the phase now running (null when the cycle is done).</summary>
public sealed record TimerCompletion(TimerMode Mode, PomodoroPhase Phase, int Round, PomodoroPhase? Next, TimeSpan Length, bool Overdue);

/// <summary>Everything needed to resume after a restart.</summary>
public sealed record TimerSnapshot(TimerMode Mode, TimerState State, DateTimeOffset? EndUtc, TimeSpan Remaining, TimeSpan Total, PomodoroPhase Phase, int Round);

/// <summary>Pure timer state machine driven by a TimeProvider; the owner calls Tick().</summary>
public sealed class TimerEngine(TimeProvider time)
{
    public static readonly TimeSpan MaxCustom = new(23, 59, 59);
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromSeconds(2);
    private readonly List<TimeSpan> laps = [];
    private DateTimeOffset anchor;      // Running: end (countdown/pomodoro) or start (stopwatch)
    private TimeSpan held;              // Paused: remaining (countdown/pomodoro) or elapsed (stopwatch)

    public TimerMode Mode { get; private set; }
    public TimerState State { get; private set; }
    public PomodoroPhase Phase { get; private set; }
    public int Round { get; private set; } = 1;
    public TimeSpan Total { get; private set; }
    public IReadOnlyList<TimeSpan> Laps => laps;
    public event Action<TimerCompletion>? Completed;
    public event Action? Changed;

    public static TimeSpan PhaseLength(PomodoroPhase phase) =>
        phase switch { PomodoroPhase.Focus => TimeSpan.FromMinutes(25), PomodoroPhase.ShortBreak => TimeSpan.FromMinutes(5), _ => TimeSpan.FromMinutes(15) };

    /// <summary>Focus n, short break, focus n+1 ... long break after round 4; after the long break the cycle is done (null).</summary>
    public static PomodoroPhase? NextPhase(PomodoroPhase phase, int round) =>
        phase switch { PomodoroPhase.Focus => round >= 4 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak, PomodoroPhase.ShortBreak => PomodoroPhase.Focus, _ => null };

    /// <summary>"25" = 25 min, "1:30" = 1 min 30 s, "1:02:03" = 1 h 2 m 3 s. Null when empty, malformed, zero or over 23:59:59.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length is 0 or > 5 || !p.All(char.IsAsciiDigit)))
            return null;
        var n = parts.Select(int.Parse).ToArray();
        if (n.Length > 1 && n.Skip(1).Any(x => x > 59))
            return null;
        var value = n.Length switch
        {
            1 => TimeSpan.FromMinutes(n[0]),
            2 => new TimeSpan(0, n[0], n[1]),
            _ => new TimeSpan(n[0], n[1], n[2]),
        };
        return value > TimeSpan.Zero && value <= MaxCustom ? value : null;
    }

    public TimeSpan Remaining => Mode == TimerMode.Stopwatch ? TimeSpan.Zero : State switch
    {
        TimerState.Running => Max0(anchor - time.GetUtcNow()),
        TimerState.Paused => held,
        _ => TimeSpan.Zero,
    };

    public TimeSpan Elapsed => Mode == TimerMode.Stopwatch
        ? State switch { TimerState.Running => Max0(time.GetUtcNow() - anchor), TimerState.Paused => held, _ => TimeSpan.Zero }
        : State == TimerState.Idle ? TimeSpan.Zero : Max0(Total - Remaining);

    /// <summary>0–1 for countdown/Pomodoro; position within the current minute for the stopwatch.</summary>
    public double Progress => Mode == TimerMode.Stopwatch
        ? Elapsed.TotalSeconds % 60 / 60
        : Total > TimeSpan.Zero && State != TimerState.Idle ? Math.Clamp(Elapsed / Total, 0, 1) : 0;

    public void StartCountdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaxCustom)
            throw new ArgumentOutOfRangeException(nameof(duration));
        Begin(TimerMode.Countdown, duration);
    }

    public void StartPomodoro()
    {
        Phase = PomodoroPhase.Focus;
        Round = 1;
        Begin(TimerMode.Pomodoro, PhaseLength(Phase));
    }

    public void StartStopwatch()
    {
        laps.Clear();
        Mode = TimerMode.Stopwatch;
        Total = TimeSpan.Zero;
        anchor = time.GetUtcNow();
        State = TimerState.Running;
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (State != TimerState.Running)
            return;
        var now = time.GetUtcNow();
        held = Mode == TimerMode.Stopwatch ? Max0(now - anchor) : Max0(anchor - now);
        State = TimerState.Paused;
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (State != TimerState.Paused)
            return;
        var now = time.GetUtcNow();
        anchor = Mode == TimerMode.Stopwatch ? now - held : now + held;
        State = TimerState.Running;
        Changed?.Invoke();
    }

    /// <summary>Pause when running, resume when paused, otherwise nothing.</summary>
    public void Toggle()
    {
        if (State == TimerState.Running) Pause();
        else Resume();
    }

    /// <summary>+1 min. After a countdown finished it starts a new countdown of that length.</summary>
    public void AddTime(TimeSpan extra)
    {
        if (Mode == TimerMode.Stopwatch || extra <= TimeSpan.Zero)
            return;
        switch (State)
        {
            case TimerState.Running:
                anchor += extra;
                Total += extra;
                break;
            case TimerState.Paused:
                held += extra;
                Total += extra;
                break;
            case TimerState.Finished when Mode == TimerMode.Countdown:
                StartCountdown(extra);
                return;
            default:
                return;
        }
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (State == TimerState.Idle)
            return;
        State = TimerState.Idle;
        laps.Clear();
        Changed?.Invoke();
    }

    public void Lap()
    {
        if (Mode == TimerMode.Stopwatch && State == TimerState.Running)
        {
            laps.Add(Elapsed);
            Changed?.Invoke();
        }
    }

    /// <summary>Call about once a second while running.</summary>
    public void Tick()
    {
        if (State != TimerState.Running || Mode == TimerMode.Stopwatch)
            return;
        var now = time.GetUtcNow();
        if (anchor > now)
            return;
        var end = anchor;
        var overdue = now - end > OverdueAfter;
        var finished = new TimerCompletion(Mode, Phase, Round, null, Total, overdue);
        if (Mode == TimerMode.Pomodoro && NextPhase(Phase, Round) is { } next)
        {
            if (Phase == PomodoroPhase.ShortBreak)
                Round++;
            Phase = next;
            Total = PhaseLength(next);
            anchor = (overdue ? now : end) + Total;   // no drift when on time; a restore after a long gap starts fresh
            finished = finished with { Next = next };
        }
        else
        {
            State = TimerState.Finished;
        }
        Changed?.Invoke();
        Completed?.Invoke(finished);
    }

    public TimerSnapshot Snapshot() => new(Mode, State, State == TimerState.Running ? anchor : null, held, Total, Phase, Round);

    /// <summary>Resumes a saved timer; a running one whose end has passed fires Completed straight away.</summary>
    public void Restore(TimerSnapshot s)
    {
        Mode = s.Mode;
        Phase = s.Phase;
        Round = Math.Clamp(s.Round, 1, 4);
        Total = s.Total;
        laps.Clear();
        State = s.State;
        held = s.Remaining;
        anchor = s.EndUtc ?? default;
        if (State == TimerState.Running && s.EndUtc is null)
            State = TimerState.Idle;
        Changed?.Invoke();
        Tick();
    }

    private void Begin(TimerMode mode, TimeSpan duration)
    {
        laps.Clear();
        Mode = mode;
        Total = duration;
        anchor = time.GetUtcNow() + duration;
        State = TimerState.Running;
        Changed?.Invoke();
    }

    private static TimeSpan Max0(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
