// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;

namespace Lintel.Core.Controls;

/// <summary>
/// N vertical bars driven by a peak Level (0–1) the owner pushes. Each bar follows Level × its own slow wobble with
/// fast attack / slower decay. Frames run on CompositionTarget.Rendering only while active (or settling) and visible.
/// </summary>
public sealed class EqualizerBars : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(EqualizerBars), new PropertyMetadata(0d));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(EqualizerBars), new PropertyMetadata(false, (d, _) => ((EqualizerBars)d).UpdateHook()));

    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(EqualizerBars),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((EqualizerBars)d).ResetBars()),
        v => v is int n && n is > 0 and <= 32);

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(EqualizerBars), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Rest = 0.15;          // idle bar height (fraction)
    private const double AttackSeconds = 0.04;
    private const double DecaySeconds = 0.18;

    private double[] heights = [];
    private TimeSpan lastFrame;
    private bool hooked;

    public EqualizerBars()
    {
        ResetBars();
        IsVisibleChanged += (_, _) => UpdateHook();
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var count = heights.Length;
        var width = ActualWidth;
        var height = ActualHeight;
        if (count == 0 || width <= 0 || height <= 0)
            return;
        var bar = width / (count * 1.6 - 0.6);   // gap = 0.6 × bar width
        var step = bar * 1.6;
        for (var i = 0; i < count; i++)
        {
            var h = Math.Max(bar, heights[i] * height);
            dc.DrawRoundedRectangle(Tint, null, new Rect(i * step, (height - h) / 2, bar, h), bar / 2, bar / 2);
        }
    }

    private void ResetBars()
    {
        heights = Enumerable.Repeat(Rest, BarCount).ToArray();
        InvalidateVisual();
    }

    private void UpdateHook()
    {
        var want = IsVisible && (IsActive || heights.Any(h => Math.Abs(h - Rest) > 0.002));
        if (want == hooked)
            return;
        hooked = want;
        lastFrame = TimeSpan.Zero;
        if (want)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame)
            return;
        var dt = lastFrame == TimeSpan.Zero ? 1 / 60d : Math.Clamp((now - lastFrame).TotalSeconds, 0, 0.1);
        lastFrame = now;
        var t = now.TotalSeconds;
        var level = IsActive ? Math.Clamp(Level, 0, 1) : 0;
        for (var i = 0; i < heights.Length; i++)
        {
            // Two incommensurate sines per bar so neighbours never move in lockstep.
            var wobble = 0.6 + 0.25 * Math.Sin(t * (5.1 + i * 1.7) + i * 2.3) + 0.15 * Math.Sin(t * (11.3 - i * 0.9) + i);
            var target = Rest + (1 - Rest) * Math.Clamp(level * wobble, 0, 1);
            var tau = target > heights[i] ? AttackSeconds : DecaySeconds;
            heights[i] += (target - heights[i]) * (1 - Math.Exp(-dt / tau));
        }
        InvalidateVisual();
        if (!IsActive)
            UpdateHook();   // unhooks once every bar has settled
    }
}
