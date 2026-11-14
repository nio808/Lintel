// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Services;
using Lintel.Notch.Layout;
using Lintel.Notch.Motion;

namespace Lintel.Notch.Views;

/// <summary>
/// The whole island on the fixed 840 × 600 canvas: hit pad, surface (shape/shadow/stroke) and a content layer clipped to
/// the outline holding the compact wings, the notice line and the expanded panel. Shape = springs on width/height/radius;
/// content = fades. Nothing re-measures per frame.
/// </summary>
internal sealed class NotchView : Canvas
{
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private readonly Animator animator;
    private readonly NotchSurface surface = new();
    private readonly Canvas content = new() { Width = NotchMetrics.CanvasWidth, Height = NotchMetrics.CanvasHeight };
    private readonly Rectangle hitPad = new() { Fill = HitBrush };
    private readonly SpringValue width = new(NotchMetrics.MinCollapsedWidth, 0.1);
    private readonly SpringValue height = new(0, 0.1);
    private readonly SpringValue radius = new(NotchMetrics.CollapsedRadius, 0.1);
    private readonly SpringValue open = new(0, 0.002);
    private readonly Tween compactOpacity;
    private readonly Tween noticeOpacity;
    private readonly Tween panelOpacity;

    public NotchView(Animator animator, IAppLog log)
    {
        this.animator = animator;
        Width = NotchMetrics.CanvasWidth;
        Height = NotchMetrics.CanvasHeight;
        Resources.MergedDictionaries.Add(NotchTheme.Create());
        SetResourceReference(TextElement.FontFamilyProperty, "Notch.Font");
        SetResourceReference(TextElement.ForegroundProperty, "Notch.Text");
        TextElement.SetFontSize(this, 11);
        // Grayscale AA: crisp on black and correct over per-pixel alpha (ClearType would fringe).
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ClipToBounds = true;

        // Hidden (not Collapsed) when faded out: layout stays warm for the next open, and IsVisible is false
        // so modules stop sampling (e.g. the equalizer) while the notch is hidden, a sliver or open.
        Compact = new CompactBar(animator) { Opacity = 0, Visibility = Visibility.Hidden };
        Notice = new NoticeView { Opacity = 0, Visibility = Visibility.Hidden };
        Panel = new ExpandedPanel(animator, log) { Opacity = 0, Visibility = Visibility.Hidden };
        compactOpacity = new Tween(0, v => Compact.Opacity = v);
        noticeOpacity = new Tween(0, v => Notice.Opacity = v);
        panelOpacity = new Tween(0, v => Panel.Opacity = v);

        content.Children.Add(Compact);
        content.Children.Add(Notice);
        content.Children.Add(Panel);
        Children.Add(hitPad);
        Children.Add(surface);
        Children.Add(content);
        surface.Width = NotchMetrics.CanvasWidth;
        surface.Height = NotchMetrics.CanvasHeight;

        animator.Frame += ApplyFrame;
        animator.Idle += OnIdle;
        Panel.Show(null, 0, false);
        ApplyFrame();
    }

    public CompactBar Compact { get; }
    public NoticeView Notice { get; }
    public ExpandedPanel Panel { get; }

    public NotchState State { get; private set; } = NotchState.Hidden;
    public NotchShape Target { get; private set; }
    public bool ReducedMotion { get; set; }

    /// <summary>Device pixels per DIP (for snapping edges at rest).</summary>
    public double PixelFactor { get; set; } = 1;

    /// <summary>Clickable area in canvas DIP: union of the old and new state while moving, the new one at rest.</summary>
    public Rect HitRect { get; private set; } = Rect.Empty;

    public bool IsAnimating => animator.IsRunning;

    public event Action? HitRectChanged;

    /// <summary>All motion finished.</summary>
    public event Action? Settled;

    public double CollapsedWidth => NotchMetrics.CollapsedWidth(Compact.LeadingWidth, Compact.TrailingWidth);

    public double NoticeWidth => NotchMetrics.NoticeWidth(Notice.NaturalWidth, CollapsedWidth);

    /// <summary>Move to <paramref name="state"/>. Re-rendering the same state only re-targets sizes (wing/notice/section changed).</summary>
    public void Render(NotchState state, bool animate)
    {
        var previous = State;
        var changed = previous != state;
        State = state;
        var collapsedWidth = CollapsedWidth;
        var noticeWidth = NoticeWidth;
        PlaceChildren(collapsedWidth, noticeWidth);
        Target = NotchMetrics.Shape(state, collapsedWidth, noticeWidth, Panel.Size);

        var shapeSpring = state == NotchState.Expanded ? MotionTokens.Expand
            : previous == NotchState.Expanded || state is NotchState.Hidden or NotchState.Sliver ? MotionTokens.Collapse
            : MotionTokens.Notice;
        var springs = !ReducedMotion && animate;
        Retarget(width, Target.Width, shapeSpring, springs);
        Retarget(height, Target.Height, shapeSpring, springs);
        Retarget(radius, Target.Radius, shapeSpring, springs);
        Retarget(open, state == NotchState.Expanded ? 1 : 0, state == NotchState.Expanded ? MotionTokens.Expand : MotionTokens.Collapse, springs);

        if (changed)
            FadeContent(previous, state, animate);
        if (animate && changed && (state == NotchState.Expanded || previous == NotchState.Expanded))
            Panel.CacheMode = new BitmapCache { SnapsToDevicePixels = true };   // fades from a texture, not a re-render

        var targetHit = NotchMetrics.HitRect(state, Target);
        SetHit(animate ? Rect.Union(HitRect, targetHit) : targetHit);

        foreach (var spring in new[] { width, height, radius, open })
            if (!spring.IsSettled)
                animator.Run(spring);
        if (!animator.IsRunning)
            OnIdle();
        else
            ApplyFrame();
    }

    /// <summary>Render the current (settled) visual to a bitmap, cropped to <paramref name="crop"/> canvas DIP.</summary>
    public BitmapSource Snapshot(Rect crop, double pixelScale, Brush backdrop)
    {
        var size = new Size(NotchMetrics.CanvasWidth, NotchMetrics.CanvasHeight);
        Measure(size);
        Arrange(new Rect(size));
        UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var target = new Rect(0, 0, crop.Width, crop.Height);
            dc.DrawRectangle(backdrop, null, target);
            dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = crop }, null, target);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(crop.Width * pixelScale), (int)Math.Ceiling(crop.Height * pixelScale),
            96 * pixelScale, 96 * pixelScale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void FadeContent(NotchState previous, NotchState state, bool animate)
    {
        var quick = MotionTokens.CompactFadeOut;
        var arrive = TimeSpan.FromMilliseconds(130);
        switch (state)
        {
            case NotchState.Expanded:
                Fade(compactOpacity, Compact, 0, quick, default, animate);
                Fade(noticeOpacity, Notice, 0, quick, default, animate);
                Fade(panelOpacity, Panel, 1, MotionTokens.ContentFadeIn, MotionTokens.ContentFadeInDelay, animate);
                break;
            case NotchState.Collapsed:
                Fade(panelOpacity, Panel, 0, quick, default, animate);
                Fade(noticeOpacity, Notice, 0, quick, default, animate);
                // Wings return as the shape lands.
                Fade(compactOpacity, Compact, 1, arrive, TimeSpan.FromMilliseconds(previous == NotchState.Expanded ? 110 : 60), animate);
                break;
            case NotchState.Notice:
                Fade(panelOpacity, Panel, 0, quick, default, animate);
                Fade(compactOpacity, Compact, 0, quick, default, animate);
                Fade(noticeOpacity, Notice, 1, arrive, TimeSpan.FromMilliseconds(previous == NotchState.Expanded ? 110 : 60), animate);
                break;
            default:   // Hidden, Sliver: nothing inside (wings must stop being visible: IsVisible drives module sampling)
                Fade(panelOpacity, Panel, 0, quick, default, animate);
                Fade(noticeOpacity, Notice, 0, quick, default, animate);
                Fade(compactOpacity, Compact, 0, quick, default, animate);
                break;
        }
    }

    private void Fade(Tween tween, UIElement element, double to, TimeSpan duration, TimeSpan delay, bool animate)
    {
        if (to > 0)
            element.Visibility = Visibility.Visible;
        if (!animate)
        {
            tween.Snap(to);
            element.Visibility = to > 0 ? Visibility.Visible : Visibility.Hidden;
            return;
        }
        if (ReducedMotion)
        {
            duration = MotionTokens.ReducedFade;
            delay = default;
        }
        if (tween.Start(to, duration, delay, null, () =>
            {
                if (tween.Value <= 0)
                    element.Visibility = Visibility.Hidden;
            }))
            animator.Run(tween);
    }

    private static void Retarget(SpringValue spring, double target, SpringParams p, bool animate)
    {
        spring.SetTarget(target, p);
        if (!animate)
            spring.Snap(target);
    }

    private void PlaceChildren(double collapsedWidth, double noticeWidth)
    {
        Compact.Width = collapsedWidth;
        SetLeft(Compact, (NotchMetrics.CanvasWidth - collapsedWidth) / 2);
        Notice.Width = noticeWidth;
        SetLeft(Notice, (NotchMetrics.CanvasWidth - noticeWidth) / 2);
        SetLeft(Panel, (NotchMetrics.CanvasWidth - Panel.Size.Width) / 2);
    }

    private void ApplyFrame()
    {
        var resting = width.IsSettled && height.IsSettled && radius.IsSettled;
        var shape = new NotchShape(width.Value, height.Value, radius.Value);
        surface.Update(shape, open.Value, resting ? PixelFactor : 0);
        // Rectangular clip = GPU scissor (a rounded geometry clip would need an offscreen mask layer every frame).
        // Content padding keeps it inside the rounded corners at rest; while moving the difference is invisible.
        content.Clip = Frozen(new RectangleGeometry(shape.Bounds));
    }

    private void OnIdle()
    {
        Panel.CacheMode = null;
        ApplyFrame();
        SetHit(NotchMetrics.HitRect(State, Target));
        Settled?.Invoke();
    }

    private void SetHit(Rect rect)
    {
        if (rect == HitRect)
            return;
        HitRect = rect;
        hitPad.Visibility = rect.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!rect.IsEmpty)
        {
            SetLeft(hitPad, rect.X);
            SetTop(hitPad, rect.Y);
            hitPad.Width = rect.Width;
            hitPad.Height = rect.Height;
        }
        HitRectChanged?.Invoke();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
