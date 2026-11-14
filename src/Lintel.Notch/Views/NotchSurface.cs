// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using Lintel.Notch.Layout;

namespace Lintel.Notch.Views;

/// <summary>
/// Draws the island: a soft pre-drawn shadow (gradient-filled edge rects + radial corners: no BlurEffect, no stroke
/// tessellation), the black shape and a 1 DIP inner stroke. Redrawn per animation frame from plain numbers; no layout.
/// </summary>
internal sealed class NotchSurface : FrameworkElement
{
    private const double Spread = 16, ShadowOffset = 3, ShadowAlpha = 0.2;

    private static readonly Brush Fill = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Pen Stroke = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly (double Offset, double Strength)[] Falloff = [(0, 1), (0.32, 0.42), (0.68, 0.12), (1, 0)];
    private static readonly GradientStopCollection Stops = Freeze(new GradientStopCollection(Falloff.Select(f => new GradientStop(Shade(f.Strength), f.Offset))));
    private static readonly Brush ShadeSolid = Freeze(new SolidColorBrush(Shade(1)));
    private static readonly Brush ShadeDown = Freeze(new LinearGradientBrush(Stops, new Point(0, 0), new Point(0, 1)));
    private static readonly Brush ShadeLeft = Freeze(new LinearGradientBrush(Stops, new Point(1, 0), new Point(0, 0)));
    private static readonly Brush ShadeRight = Freeze(new LinearGradientBrush(Stops, new Point(0, 0), new Point(1, 0)));

    /// <summary>Corner brushes keyed by whole-DIP radius; reused so the render thread realizes each gradient once.</summary>
    private static readonly Dictionary<int, Brush> Corners = [];

    private NotchShape shape;
    private double decoration;   // 0 collapsed … 1 expanded: shadow + stroke
    private double snap;          // device pixels per DIP when at rest, 0 while moving

    /// <summary>The current outline (black fill).</summary>
    public Geometry ShapeGeometry { get; private set; } = Geometry.Empty;

    public void Update(NotchShape value, double decorationOpacity, double snapFactor)
    {
        shape = value;
        decoration = Math.Clamp(decorationOpacity, 0, 1);
        snap = snapFactor;
        ShapeGeometry = Outline(shape, snap, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (shape.Height <= 0.01 || shape.Width <= 0.01)
            return;
        if (decoration > 0.01)
        {
            dc.PushOpacity(decoration);
            DrawShadow(dc);
            dc.Pop();
        }
        dc.DrawGeometry(Fill, null, ShapeGeometry);
        if (decoration > 0.01)
        {
            dc.PushOpacity(decoration);
            dc.DrawGeometry(null, Stroke, Outline(shape, snap, 0.5, closed: false));
            dc.Pop();
        }
    }

    /// <summary>
    /// Flush with the top edge, square top corners, rounded bottom. <paramref name="inset"/> shrinks the sides and bottom
    /// (stroke). Open outlines skip the top edge: a line along the screen edge would read as a gap.
    /// </summary>
    internal static Geometry Outline(NotchShape s, double snap, double inset, bool closed = true)
    {
        double l = s.Left, r = s.Left + s.Width, b = s.Height;
        if (snap > 0)
        {
            l = NotchMetrics.Snap(l, snap);
            r = NotchMetrics.Snap(r, snap);
            b = NotchMetrics.Snap(b, snap);
        }
        l += inset;
        r -= inset;
        b -= inset;
        if (r - l <= 0 || b <= 0)
            return Geometry.Empty;
        var radius = Math.Max(0, Math.Min(s.Radius - inset, Math.Min((r - l) / 2, b)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(l, 0), closed, closed);
            c.LineTo(new Point(l, b - radius), true, false);
            c.ArcTo(new Point(l + radius, b), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(r - radius, b), true, false);
            c.ArcTo(new Point(r, b - radius), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(r, 0), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Soft shadow cast 3 DIP down; the sides start above the screen edge (clipped by the canvas).</summary>
    private void DrawShadow(DrawingContext dc)
    {
        double l = shape.Left, r = shape.Left + shape.Width, b = shape.Height + ShadowOffset;
        var radius = Math.Round(Math.Max(0, Math.Min(shape.Radius, Math.Min(shape.Width / 2, shape.Height))));
        var sideBottom = b - radius;
        if (sideBottom > -40)
        {
            dc.DrawRectangle(ShadeLeft, null, new Rect(l - Spread, -40, Spread, sideBottom + 40));
            dc.DrawRectangle(ShadeRight, null, new Rect(r, -40, Spread, sideBottom + 40));
        }
        if (r - l > 2 * radius)
        {
            // The offset shadow's solid body shows in the strip just below the shape, then fades out.
            dc.DrawRectangle(ShadeSolid, null, new Rect(l + radius, shape.Height, r - l - 2 * radius, ShadowOffset));
            dc.DrawRectangle(ShadeDown, null, new Rect(l + radius, b, r - l - 2 * radius, Spread));
        }
        var corner = CornerBrush((int)radius);
        var size = 2 * (radius + Spread);
        // Each corner draws a quarter of the radial disc: clip to the quadrant, fill the whole disc box.
        dc.PushClip(Freeze(new RectangleGeometry(new Rect(l - Spread, sideBottom, radius + Spread, radius + Spread))));
        dc.DrawRectangle(corner, null, new Rect(l + radius - size / 2, sideBottom - size / 2, size, size));
        dc.Pop();
        dc.PushClip(Freeze(new RectangleGeometry(new Rect(r - radius, sideBottom, radius + Spread, radius + Spread))));
        dc.DrawRectangle(corner, null, new Rect(r - radius - size / 2, sideBottom - size / 2, size, size));
        dc.Pop();
    }

    private static Brush CornerBrush(int radius)
    {
        if (Corners.TryGetValue(radius, out var brush))
            return brush;
        var k = radius / (radius + Spread);
        var stops = new GradientStopCollection { new(Shade(1), 0) };
        foreach (var (offset, strength) in Falloff)
            stops.Add(new GradientStop(Shade(strength), k + (1 - k) * offset));
        return Corners[radius] = Freeze(new RadialGradientBrush(stops));   // relative: centred in the disc box
    }

    private static Color Shade(double strength) => Color.FromArgb((byte)Math.Round(255 * ShadowAlpha * strength), 0, 0, 0);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
