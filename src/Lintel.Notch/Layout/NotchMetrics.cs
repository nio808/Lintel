// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Lintel.Core.Notch;

namespace Lintel.Notch.Layout;

/// <summary>Visible pill/panel outline in canvas DIP: flush with the top edge, centred horizontally on the canvas.</summary>
public readonly record struct NotchShape(double Width, double Height, double Radius)
{
    public double Left => (NotchMetrics.CanvasWidth - Width) / 2;
    public Rect Bounds => new(Left, 0, Math.Max(0, Width), Math.Max(0, Height));
}

/// <summary>Device-pixel rectangle (window-relative or screen).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public bool Covers(PixelRect other) => Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
}

/// <summary>
/// Fixed notch geometry (no user-facing look options), in DIP. Compact by design: a 28 DIP pill, a 30 DIP header,
/// 12 DIP padding, 20 DIP corners. Pure.
/// </summary>
public static class NotchMetrics
{
    /// <summary>The window never resizes: a fixed canvas, top-centre of the display.</summary>
    public const double CanvasWidth = 840, CanvasHeight = 600;

    public const double CollapsedHeight = 28, CollapsedRadius = 14;
    public const double WingPadding = 12, CenterGap = 32, WingMaxWidth = 96, WingMaxHeight = 22;
    public const double MinCollapsedWidth = 120, MaxCollapsedWidth = 280;
    public const double NoticeHeight = 30, NoticeMaxWidth = 340, NoticePadding = 12;
    public const double HeaderHeight = 30, SidePadding = 12, BottomPadding = 10, ExpandedRadius = 20;
    public const double SliverWidth = 56, SliverHeight = 3, SliverHitHeight = 8;
    public const double HoverSlop = 4, ShadowMargin = 20;

    /// <summary>Section content clamp: compact sections are welcome, nothing grows past 680 × 420.</summary>
    public static readonly Size MinContent = new(300, 100);
    public static readonly Size MaxContent = new(680, 420);

    public static double CollapsedWidth(double leading, double trailing) =>
        Math.Clamp(2 * WingPadding + Math.Min(leading, WingMaxWidth) + CenterGap + Math.Min(trailing, WingMaxWidth), MinCollapsedWidth, MaxCollapsedWidth);

    public static Size ClampContent(Size preferred) =>
        new(Math.Clamp(Finite(preferred.Width, 560), MinContent.Width, MaxContent.Width),
            Math.Clamp(Finite(preferred.Height, 260), MinContent.Height, MaxContent.Height));

    public static Size PanelSize(Size content) => new(content.Width + 2 * SidePadding, HeaderHeight + content.Height + BottomPadding);

    public static double NoticeWidth(double contentWidth, double collapsedWidth) =>
        Math.Clamp(contentWidth + 2 * NoticePadding, Math.Min(Math.Max(collapsedWidth, MinCollapsedWidth), NoticeMaxWidth), NoticeMaxWidth);

    /// <summary>Target outline for a state. Hidden tucks the pill up into the screen edge.</summary>
    public static NotchShape Shape(NotchState state, double collapsedWidth, double noticeWidth, Size panel) => state switch
    {
        NotchState.Hidden => new(collapsedWidth, 0, CollapsedRadius),
        NotchState.Sliver => new(SliverWidth, SliverHeight, SliverHeight / 2),
        NotchState.Notice => new(noticeWidth, NoticeHeight, NoticeHeight / 2),
        NotchState.Expanded => new(panel.Width, panel.Height, ExpandedRadius),
        _ => new(collapsedWidth, CollapsedHeight, CollapsedRadius),
    };

    /// <summary>
    /// Click/hover region in canvas DIP. Collapsed/notice: the pill + 4 DIP at left/right/bottom; expanded: the panel +
    /// the shadow margin; sliver: 56 × 8; hidden: empty. Always reaches the top edge.
    /// </summary>
    public static Rect HitRect(NotchState state, NotchShape shape)
    {
        if (state == NotchState.Hidden)
            return Rect.Empty;
        if (state == NotchState.Sliver)
            return new((CanvasWidth - SliverWidth) / 2, 0, SliverWidth, SliverHitHeight);
        var margin = state == NotchState.Expanded ? ShadowMargin : HoverSlop;
        var width = shape.Width + 2 * margin;
        return new((CanvasWidth - width) / 2, 0, width, shape.Height + margin);
    }

    /// <summary>DIP rect → device pixels, growing outward to whole pixels. <paramref name="dpiScale"/> = device pixels per DIP.</summary>
    public static PixelRect ToPixels(Rect dip, double dpiScale) =>
        dip.IsEmpty ? default
        : new((int)Math.Floor(dip.Left * dpiScale + 1e-6), (int)Math.Floor(dip.Top * dpiScale + 1e-6),
              (int)Math.Ceiling(dip.Right * dpiScale - 1e-6), (int)Math.Ceiling(dip.Bottom * dpiScale - 1e-6));

    /// <summary>Round a DIP coordinate to the nearest device pixel.</summary>
    public static double Snap(double dip, double dpiScale) => dpiScale <= 0 ? dip : Math.Round(dip * dpiScale) / dpiScale;

    /// <summary>Window size in device pixels; width is even so the canvas centre lands on a whole pixel.</summary>
    public static (int Width, int Height) WindowPixels(double dpiScale) =>
        (2 * (int)Math.Round(CanvasWidth * dpiScale / 2), (int)Math.Round(CanvasHeight * dpiScale));

    private static double Finite(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}
