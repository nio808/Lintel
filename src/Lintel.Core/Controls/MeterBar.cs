// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lintel.Core.Controls;

/// <summary>4 DIP high rounded bar. Value 0–1. IsIndeterminate slides a segment (30 fps, only while visible).</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(MeterBar), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(MeterBar), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MeterBar)d).UpdateAnimation()));

    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private bool animating;

    public MeterBar()
    {
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 4);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;
        var radius = height / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), radius, radius);
        Rect fill;
        if (IsIndeterminate)
        {
            var segment = width * 0.3;
            var x = (double)GetValue(PhaseProperty) * (width + segment) - segment;
            fill = new Rect(Math.Max(0, x), 0, Math.Max(0, Math.Min(width, x + segment) - Math.Max(0, x)), height);
        }
        else
        {
            fill = new Rect(0, 0, width * Math.Clamp(Value, 0, 1), height);
        }
        if (fill.Width > 0)
            dc.DrawRoundedRectangle(Fill, null, fill, radius, radius);
    }

    private void UpdateAnimation()
    {
        var want = IsIndeterminate && IsVisible;
        if (want == animating)
            return;
        animating = want;
        if (!want)
        {
            BeginAnimation(PhaseProperty, null);
            return;
        }
        var sweep = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(sweep, 30);
        BeginAnimation(PhaseProperty, sweep);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
