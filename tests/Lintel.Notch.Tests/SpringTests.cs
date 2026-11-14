// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Notch.Motion;

namespace Lintel.Notch.Tests;

public sealed class SpringTests
{
    [Theory]
    [InlineData(0.26, 0.88)]
    [InlineData(0.22, 1.0)]
    [InlineData(0.38, 0.72)]
    [InlineData(0.5, 1.6)]
    public void StartsAtZeroAndEndsAtOne(double response, double damping)
    {
        var p = new SpringParams(response, damping);
        Assert.Equal(0, Spring.Value(p, 0), 9);
        Assert.Equal(1, Spring.Value(p, 5), 6);
    }

    [Fact]
    public void UnderdampedOvershootMatchesTheory()
    {
        // e^(−πζ/√(1−ζ²)): ζ 0.72 → ≈ 3.8 %; the notch's own springs stay under 1 % (quiet motion).
        var peak = Enumerable.Range(0, 2000).Max(ms => Spring.Value(new SpringParams(0.38, 0.72), ms / 1000.0));
        Assert.InRange(peak - 1, 0.035, 0.045);
        foreach (var p in new[] { MotionTokens.Expand, MotionTokens.Notice })
            Assert.InRange(Enumerable.Range(0, 2000).Max(ms => Spring.Value(p, ms / 1000.0)) - 1, 0, 0.01);
    }

    [Fact]
    public void CriticalDampingIsMonotonic()
    {
        var previous = 0.0;
        for (var ms = 1; ms < 3000; ms++)
        {
            var value = Spring.Value(MotionTokens.Collapse, ms / 1000.0);
            Assert.True(value >= previous - 1e-12, $"dropped at {ms} ms");
            Assert.True(value <= 1 + 1e-9);
            previous = value;
        }
    }

    [Fact]
    public void SettleTimesMatchTheSpec()
    {
        // Quick, quiet motion: everything is within 0.1 % of its target in ≈ 0.3 s.
        Assert.InRange(Spring.SettleTime(MotionTokens.Expand), 0.2, 0.4);
        Assert.InRange(Spring.SettleTime(MotionTokens.Collapse), 0.2, 0.4);
        Assert.InRange(Spring.SettleTime(MotionTokens.Notice), 0.2, 0.4);
        Assert.InRange(Spring.SettleTime(new SpringParams(0.38, 0.72)), 0.5, 0.7);
    }

    [Fact]
    public void VelocityIsTheDerivative()
    {
        var p = MotionTokens.Notice;
        const double h = 1e-6;
        foreach (var t in new[] { 0.01, 0.05, 0.2, 0.4 })
        {
            var (_, v) = Spring.Evaluate(p, -100, 30, t);
            var (y1, _) = Spring.Evaluate(p, -100, 30, t - h);
            var (y2, _) = Spring.Evaluate(p, -100, 30, t + h);
            Assert.Equal(v, (y2 - y1) / (2 * h), 3);
        }
        Assert.Equal(30, Spring.Evaluate(p, -100, 30, 0).V, 9);
    }

    [Fact]
    public void SteppingInFramesMatchesTheClosedForm()
    {
        var spring = new SpringValue(0, 1e-9);
        spring.SetTarget(100, MotionTokens.Expand);
        for (var i = 0; i < 30; i++)
            spring.Step(1 / 144.0);
        Assert.Equal(100 * Spring.Value(MotionTokens.Expand, 30 / 144.0), spring.Value, 6);
    }

    [Fact]
    public void RetargetKeepsPositionAndVelocity()
    {
        var spring = new SpringValue(0, 0.01);
        spring.SetTarget(300, MotionTokens.Expand);
        for (var i = 0; i < 10; i++)
            spring.Step(1 / 60.0);
        var (value, velocity) = (spring.Value, spring.Velocity);
        spring.SetTarget(0, MotionTokens.Collapse);
        Assert.Equal(value, spring.Value);
        Assert.Equal(velocity, spring.Velocity);
        while (spring.Step(1 / 60.0)) { }
        Assert.True(spring.IsSettled);
        Assert.Equal(0, spring.Value);
    }

    [Fact]
    public void TweenEasesAndCompletes()
    {
        var applied = new List<double>();
        var done = 0;
        var tween = new Tween(0, applied.Add);
        Assert.True(tween.Start(1, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), Easing.CubicOut, () => done++));
        Assert.True(tween.Step(0.05));
        Assert.Equal(0, tween.Value, 9);   // still in the delay
        Assert.True(tween.Step(0.05));
        Assert.Equal(Easing.CubicOut(0.5), tween.Value, 9);
        Assert.False(tween.Step(0.1));
        Assert.Equal(1, tween.Value);
        Assert.Equal(1, done);
    }
}
