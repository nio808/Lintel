// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;

namespace Lintel.Notch.Motion;

internal interface IStepper
{
    /// <summary>Advance by dt seconds; false when finished.</summary>
    bool Step(double dt);
}

/// <summary>
/// One frame clock for every notch animation, on CompositionTarget.Rendering (fires at the display refresh rate).
/// Hooked only while something moves, so an idle notch costs no CPU.
/// </summary>
internal sealed class Animator
{
    private readonly List<IStepper> active = [];
    private TimeSpan last;
    private bool hooked;
    private bool first;

    /// <summary>After all steppers advanced in a frame.</summary>
    public event Action? Frame;

    /// <summary>When the last stepper finished.</summary>
    public event Action? Idle;

    public bool IsRunning => hooked;

    public void Run(IStepper stepper)
    {
        if (!active.Contains(stepper))
            active.Add(stepper);
        if (hooked)
            return;
        hooked = true;
        first = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Cancel(IStepper stepper) => active.Remove(stepper);

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (!first && now == last)
            return;   // Rendering can fire more than once per frame
        // First frame moves by one nominal frame so the first visible frame already shows motion.
        var dt = first ? 1 / 100.0 : Math.Clamp((now - last).TotalSeconds, 0, 1 / 20.0);
        first = false;
        last = now;
        for (var i = active.Count - 1; i >= 0; i--)
        {
            if (i < active.Count && !active[i].Step(dt))
                active.RemoveAt(i);
        }
        Frame?.Invoke();
        if (active.Count > 0)
            return;
        CompositionTarget.Rendering -= OnRendering;
        hooked = false;
        Idle?.Invoke();
    }
}

/// <summary>Time-based tween (fades, slides). Value is readable at any time; Apply runs on each step.</summary>
internal sealed class Tween(double value, Action<double>? apply = null) : IStepper
{
    private double from;
    private double to;
    private double duration;
    private double delay;
    private double elapsed;
    private Func<double, double> ease = Easing.Linear;
    private Action? completed;

    public double Value { get; private set; } = value;
    public bool IsRunning { get; private set; }

    /// <summary>True when the caller should start stepping.</summary>
    public bool Start(double target, TimeSpan time, TimeSpan delayBy = default, Func<double, double>? easing = null, Action? done = null)
    {
        completed = done;
        if (target == Value && !IsRunning)
        {
            done?.Invoke();
            return false;
        }
        from = Value;
        to = target;
        duration = Math.Max(time.TotalSeconds, 1e-6);
        delay = delayBy.TotalSeconds;
        elapsed = 0;
        ease = easing ?? Easing.Linear;
        IsRunning = true;
        return true;
    }

    /// <summary>Jump to <paramref name="value"/>; a pending completion is dropped (the tween was superseded).</summary>
    public void Snap(double value)
    {
        IsRunning = false;
        completed = null;
        Value = value;
        apply?.Invoke(value);
    }

    public bool Step(double dt)
    {
        if (!IsRunning)
            return false;
        elapsed += dt;
        var t = Math.Clamp((elapsed - delay) / duration, 0, 1);
        Value = from + (to - from) * ease(t);
        apply?.Invoke(Value);
        if (t < 1)
            return true;
        IsRunning = false;
        var done = completed;
        completed = null;
        done?.Invoke();
        return false;
    }
}

internal static class Easing
{
    public static double Linear(double t) => t;

    public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);
}
