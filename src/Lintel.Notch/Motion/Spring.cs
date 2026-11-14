// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Notch.Motion;

/// <summary>Spring defined like SwiftUI: response = undamped period (s), damping = ratio ζ (1 = critical).</summary>
public readonly record struct SpringParams(double Response, double Damping)
{
    public double Omega => 2 * Math.PI / Response;
}

/// <summary>Closed-form damped harmonic oscillator. Exact for any time step, so frame drops never change the curve.</summary>
public static class Spring
{
    /// <summary>Displacement from target and velocity after <paramref name="t"/> s, starting at displacement y0 with velocity v0.</summary>
    public static (double Y, double V) Evaluate(SpringParams p, double y0, double v0, double t)
    {
        var w0 = p.Omega;
        var z = p.Damping;
        if (Math.Abs(z - 1) < 1e-6)
        {
            var e = Math.Exp(-w0 * t);
            var b = v0 + w0 * y0;
            return (e * (y0 + b * t), e * (b - w0 * (y0 + b * t)));
        }
        if (z < 1)
        {
            var wd = w0 * Math.Sqrt(1 - z * z);
            var e = Math.Exp(-z * w0 * t);
            var a = y0;
            var b = (v0 + z * w0 * y0) / wd;
            var c = Math.Cos(wd * t);
            var s = Math.Sin(wd * t);
            return (e * (a * c + b * s), e * ((b * wd - z * w0 * a) * c - (a * wd + z * w0 * b) * s));
        }
        var root = Math.Sqrt(z * z - 1);
        var r1 = -w0 * (z - root);
        var r2 = -w0 * (z + root);
        var c2 = (v0 - r1 * y0) / (r2 - r1);
        var c1 = y0 - c2;
        var e1 = Math.Exp(r1 * t);
        var e2 = Math.Exp(r2 * t);
        return (c1 * e1 + c2 * e2, c1 * r1 * e1 + c2 * r2 * e2);
    }

    /// <summary>Normalised step response 0 → 1 from rest.</summary>
    public static double Value(SpringParams p, double t) => 1 + Evaluate(p, -1, 0, t).Y;

    /// <summary>Time after which the step response stays within <paramref name="epsilon"/> of 1.</summary>
    public static double SettleTime(SpringParams p, double epsilon = 0.001)
    {
        // 1 ms scan over 10 s (10k evaluations); called by tests and tokens, never per frame.
        var last = 0.0;
        for (var ms = 0; ms <= 10_000; ms++)
        {
            var t = ms / 1000.0;
            if (Math.Abs(Value(p, t) - 1) >= epsilon)
                last = t;
        }
        return last;
    }
}

/// <summary>A value that chases <see cref="Target"/> on a spring. Retargeting keeps position and velocity (no jumps).</summary>
internal sealed class SpringValue(double value, double epsilon = 0.01) : IStepper
{
    public double Value { get; private set; } = value;
    public double Velocity { get; private set; }
    public double Target { get; private set; } = value;
    public SpringParams Params { get; private set; } = MotionTokens.Expand;
    public bool IsSettled => Value == Target && Velocity == 0;

    /// <summary>True when the target or params changed and the caller should start stepping.</summary>
    public bool SetTarget(double target, SpringParams p)
    {
        Params = p;
        if (target == Target && IsSettled)
            return false;
        Target = target;
        return true;
    }

    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    public bool Step(double dt)
    {
        if (IsSettled)
            return false;
        var (y, v) = Spring.Evaluate(Params, Value - Target, Velocity, dt);
        Value = Target + y;
        Velocity = v;
        if (Math.Abs(y) < epsilon && Math.Abs(v) < epsilon * 10)
            Snap(Target);
        return !IsSettled;
    }
}

/// <summary>
/// Motion: quick, quiet springs (≈ 0.2–0.26 s, at most a hint of overshoot), gentle cross-fades, short travel and no
/// scale-ups.
/// </summary>
public static class MotionTokens
{
    public static readonly SpringParams Expand = new(0.26, 0.88);    // ≈ 0.3 % overshoot
    public static readonly SpringParams Collapse = new(0.22, 1.0);   // no bounce
    public static readonly SpringParams Notice = new(0.24, 0.92);

    public static readonly TimeSpan CompactFadeOut = TimeSpan.FromMilliseconds(70);
    public static readonly TimeSpan ContentFadeIn = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan ContentFadeInDelay = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan ActivityCrossfade = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan SectionSwitch = TimeSpan.FromMilliseconds(180);
    public const double SectionSlide = 10;
    public static readonly TimeSpan ReducedFade = TimeSpan.FromMilliseconds(120);
}
