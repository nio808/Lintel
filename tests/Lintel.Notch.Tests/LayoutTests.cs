// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Lintel.Core.Notch;
using Lintel.Notch.Layout;
using Lintel.Notch.Window;

namespace Lintel.Notch.Tests;

public sealed class LayoutTests
{
    [Theory]
    [InlineData(0, 0, 120)]       // idle pill: minimum
    [InlineData(22, 22, 120)]     // 100 → minimum
    [InlineData(40, 40, 136)]     // 24 + 40 + 32 + 40
    [InlineData(96, 96, 248)]
    [InlineData(200, 200, 248)]   // wings cap at 96
    public void CollapsedWidthFromWings(double leading, double trailing, double expected) =>
        Assert.Equal(expected, NotchMetrics.CollapsedWidth(leading, trailing));

    [Fact]
    public void SectionContentIsClampedAndPanelAddsCompactChrome()
    {
        Assert.Equal(new Size(300, 100), NotchMetrics.ClampContent(new Size(100, 50)));
        Assert.Equal(new Size(680, 420), NotchMetrics.ClampContent(new Size(2000, 2000)));
        Assert.Equal(new Size(560, 260), NotchMetrics.ClampContent(new Size(double.NaN, 0)));
        Assert.Equal(new Size(704, 440), NotchMetrics.PanelSize(new Size(680, 400)));   // 12 side, 30 header, 10 bottom
        Assert.Equal(new Size(324, 140), NotchMetrics.PanelSize(NotchMetrics.MinContent));
        Assert.Equal(new Size(704, 460), NotchMetrics.PanelSize(NotchMetrics.MaxContent));
    }

    [Fact]
    public void LargestPanelAndShadowFitTheFixedCanvas()
    {
        var hit = NotchMetrics.HitRect(NotchState.Expanded, NotchMetrics.Shape(NotchState.Expanded, 0, 0, NotchMetrics.PanelSize(NotchMetrics.MaxContent)));
        Assert.True(hit.Left >= 0 && hit.Right <= NotchMetrics.CanvasWidth && hit.Bottom <= NotchMetrics.CanvasHeight);
    }

    [Fact]
    public void NoticeWidthIsClamped()
    {
        Assert.Equal(340, NotchMetrics.NoticeWidth(1000, 120));
        Assert.Equal(200, NotchMetrics.NoticeWidth(10, 200));   // never narrower than the pill it grew from
        Assert.Equal(224, NotchMetrics.NoticeWidth(200, 120));
    }

    [Theory]
    [InlineData(NotchState.Collapsed, 180, 28)]
    [InlineData(NotchState.Notice, 300, 30)]
    [InlineData(NotchState.Sliver, 56, 3)]
    [InlineData(NotchState.Hidden, 180, 0)]
    [InlineData(NotchState.Expanded, 704, 440)]
    public void ShapesPerState(NotchState state, double width, double height)
    {
        var shape = NotchMetrics.Shape(state, 180, 300, new Size(704, 440));
        Assert.Equal(width, shape.Width);
        Assert.Equal(height, shape.Height);
        Assert.Equal(0, shape.Bounds.Top);
        Assert.Equal(NotchMetrics.CanvasWidth / 2, shape.Left + shape.Width / 2, 9);
    }

    [Fact]
    public void HitRectsFollowPlan()
    {
        var collapsed = NotchMetrics.Shape(NotchState.Collapsed, 200, 0, default);
        var hit = NotchMetrics.HitRect(NotchState.Collapsed, collapsed);
        Assert.Equal(new Rect(316, 0, 208, 32), hit);   // pill + 4 DIP slop left/right/bottom, from the top edge

        var panel = NotchMetrics.Shape(NotchState.Expanded, 200, 0, new Size(704, 440));
        hit = NotchMetrics.HitRect(NotchState.Expanded, panel);
        Assert.Equal(704 + 40, hit.Width);   // shadow margin
        Assert.Equal(440 + 20, hit.Bottom);

        hit = NotchMetrics.HitRect(NotchState.Sliver, NotchMetrics.Shape(NotchState.Sliver, 200, 0, default));
        Assert.Equal(new Rect(392, 0, 56, 8), hit);
        Assert.True(NotchMetrics.HitRect(NotchState.Hidden, collapsed).IsEmpty);
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    [InlineData(2.00)]
    public void PixelRoundingGrowsOutwardAndSnapsToWholePixels(double dpi)
    {
        var rect = new Rect(321.3, 0, 197.4, 34.05);
        var px = NotchMetrics.ToPixels(rect, dpi);
        Assert.True(px.Left <= rect.Left * dpi && px.Right >= rect.Right * dpi);
        Assert.True(px.Bottom >= rect.Bottom * dpi);
        Assert.True(px.Width - rect.Width * dpi < 2);
        var snapped = NotchMetrics.Snap(321.3, dpi);
        Assert.Equal(Math.Round(snapped * dpi), snapped * dpi, 9);
        Assert.True(Math.Abs(snapped - 321.3) <= 0.5 / dpi + 1e-9);
    }

    [Theory]
    [InlineData(1.00, 840, 600)]
    [InlineData(1.25, 1050, 750)]
    [InlineData(1.50, 1260, 900)]
    [InlineData(1.75, 1470, 1050)]
    [InlineData(2.00, 1680, 1200)]
    public void WindowPixelsFollowDpiAndStayEven(double dpi, int width, int height)
    {
        var (w, h) = NotchMetrics.WindowPixels(dpi);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
        Assert.Equal(0, w % 2);
    }

    [Fact]
    public void ActivitySelectorPicksByPriority()
    {
        var timer = new TestActivity("timer", 80);
        var media = new TestActivity("media", 60);
        var quota = new TestActivity("quota", 20);
        CompactActivity[] all = [quota, media, timer];

        Assert.Equal((null, null), ActivitySelector.Select(all, _ => true));

        quota.IsActive = true;
        var (left, right) = ActivitySelector.Select(all, a => a == quota);
        Assert.Equal(new WingSlot(quota, false), left);
        Assert.Equal(new WingSlot(quota, true), right);
        Assert.Null(ActivitySelector.Select(all, _ => false).Right);

        media.IsActive = timer.IsActive = true;
        (left, right) = ActivitySelector.Select(all, _ => true);
        Assert.Equal(new WingSlot(timer, false), left);
        Assert.Equal(new WingSlot(media, false), right);
    }

    [Fact]
    public void EqualPrioritiesKeepRegistrationOrder()
    {
        var a = new TestActivity("a", 50) { IsActive = true };
        var b = new TestActivity("b", 50) { IsActive = true };
        var (left, right) = ActivitySelector.Select([a, b], _ => false);
        Assert.Same(a, left?.Activity);
        Assert.Same(b, right?.Activity);
    }

    [Fact]
    public void DisplayChoice()
    {
        var primary = Display(@"\\.\DISPLAY1", 0, true);
        var second = Display(@"\\.\DISPLAY2", 1920, false);
        DisplayInfo[] all = [second, primary];
        Assert.Same(primary, Displays.Choose(all, "primary", null));
        Assert.Same(primary, Displays.Choose(all, null, null));
        Assert.Same(second, Displays.Choose(all, @"\\.\display2", null));
        Assert.Same(primary, Displays.Choose(all, @"\\.\DISPLAY9", null));   // unplugged → primary
        Assert.Same(second, Displays.Choose(all, "pointer", (2500, 300)));
        Assert.Same(primary, Displays.Choose(all, "pointer", (-5000, 0)));
        Assert.Null(Displays.Choose([], "primary", null));
    }

    [Fact]
    public void WindowIsCentredAndAnchoredBelowATopTaskbar()
    {
        var bottomTaskbar = Display("d", 1920, false, workTop: 0);
        var rect = Displays.WindowRect(bottomTaskbar with { DpiScale = 1.25 });
        Assert.Equal(1920 + (1920 - 1050) / 2, rect.Left);
        Assert.Equal(0, rect.Top);
        Assert.Equal(1050, rect.Width);
        Assert.Equal(rect.Left - 1920, 1920 - (rect.Right - 1920));   // symmetric

        var topTaskbar = Display("d", 0, true, workTop: 48);
        Assert.Equal(48, Displays.WindowRect(topTaskbar).Top);
    }

    private static DisplayInfo Display(string name, int left, bool primary, int workTop = 0) =>
        new(name, new PixelRect(left, 0, left + 1920, 1080), new PixelRect(left, workTop, left + 1920, 1032), primary, 1.0, new IntPtr(left + 1));

    private sealed class TestActivity(string id, int priority) : CompactActivity(id, priority, id)
    {
        public override FrameworkElement CreateLeading() => throw new NotSupportedException();
    }
}
