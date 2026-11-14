// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lintel.Core.Notch;
using Lintel.Core.Services;
using Lintel.Notch.Layout;
using Lintel.Notch.Motion;

namespace Lintel.Notch.Views;

/// <summary>
/// One section at a time. Views are created lazily (once), laid out at their clamped PreferredSize, and told via
/// <see cref="ISectionLifecycle"/> when they become visible or hidden. Switching slides 24 DIP and cross-fades (200 ms).
/// </summary>
internal sealed class SectionHost : Grid
{
    private readonly Animator animator;
    private readonly IAppLog log;
    private readonly Dictionary<string, Border> views = [];
    private Border? current;
    private string? shownId;
    private bool visible;

    public SectionHost(Animator animator, IAppLog log)
    {
        this.animator = animator;
        this.log = log;
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public string? CurrentId { get; private set; }

    public FrameworkElement? CurrentView => current?.Child as FrameworkElement;

    /// <summary>Make <paramref name="section"/> current. <paramref name="direction"/>: +1 slides in from the right, −1 from the left, 0 no slide.</summary>
    public void Show(NotchSection? section, Size content, int direction, bool animate)
    {
        Width = content.Width;
        Height = content.Height;
        if (section?.Id == CurrentId && current is not null)
            return;
        var wasShown = visible;
        if (wasShown)
            NotifyHidden();
        // Finish any half-done transition: only the outgoing view may remain.
        foreach (var stale in Children.OfType<Border>().Where(b => b != current).ToList())
            Children.Remove(stale);

        var old = current;
        CurrentId = section?.Id;
        current = section is null ? null : Wrapper(section, content);
        if (current is not null && !Children.Contains(current))
            Children.Add(current);

        animate &= direction != 0 && old is not null && current is not null;
        if (old is not null)
        {
            if (animate)
                Slide(old, 0, -direction * MotionTokens.SectionSlide, 1, 0, () => Children.Remove(old));
            else
                Children.Remove(old);
        }
        if (current is not null)
        {
            if (animate)
                Slide(current, direction * MotionTokens.SectionSlide, 0, 0, 1, null);
            else
                Reset(current);
        }
        if (wasShown)
            NotifyShown();
    }

    /// <summary>The panel opened (true) or closed (false): OnShown / OnHidden for the current view.</summary>
    public void SetVisible(bool value)
    {
        if (visible == value)
            return;
        visible = value;
        if (value)
            NotifyShown();
        else
            NotifyHidden();
    }

    /// <summary>Drop views whose sections are gone (module stopped). Hides the current one first if needed.</summary>
    public void Retain(IReadOnlySet<string> ids)
    {
        foreach (var id in views.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            if (id == shownId)
                NotifyHidden();
            var wrapper = views[id];
            Children.Remove(wrapper);
            wrapper.Child = null;
            views.Remove(id);
            if (id == CurrentId)
            {
                CurrentId = null;
                current = null;
            }
        }
    }

    /// <summary>Creates (once) and returns a section's view for capture.</summary>
    public FrameworkElement? EnsureView(NotchSection section, Size content) => Wrapper(section, content).Child as FrameworkElement;

    private Border Wrapper(NotchSection section, Size content)
    {
        if (!views.TryGetValue(section.Id, out var wrapper))
        {
            FrameworkElement view;
            try
            {
                view = section.CreateView();
            }
            catch (Exception e)
            {
                log.Error($"Section {section.Id}: CreateView failed", e);
                view = new TextBlock { Text = $"{section.Title} could not be shown.", Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
                view.SetResourceReference(StyleProperty, "Notch.Caption");
            }
            wrapper = new Border { Child = view, ClipToBounds = true, RenderTransform = new TranslateTransform(), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            views[section.Id] = wrapper;
        }
        wrapper.Width = content.Width;
        wrapper.Height = content.Height;
        return wrapper;
    }

    private void NotifyShown()
    {
        if (CurrentId is null || shownId == CurrentId || current?.Child is not FrameworkElement view)
            return;
        shownId = CurrentId;
        // Laid out and visible before OnShown, so a view can focus its search box there.
        UpdateLayout();
        if (view is ISectionLifecycle lifecycle)
            Guard(lifecycle.OnShown, "OnShown");
    }

    private void NotifyHidden()
    {
        if (shownId is null)
            return;
        var id = shownId;
        shownId = null;
        if (views.TryGetValue(id, out var wrapper) && wrapper.Child is ISectionLifecycle lifecycle)
            Guard(lifecycle.OnHidden, "OnHidden");
    }

    private void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            log.Warn($"Section {CurrentId}: {what} failed", e);
        }
    }

    private void Slide(Border wrapper, double fromX, double toX, double fromOpacity, double toOpacity, Action? done)
    {
        // One tween pair per wrapper: a quick A → B → A retargets instead of two tweens fighting.
        var (x, opacity) = Tweens(wrapper);
        x.Snap(fromX);
        opacity.Snap(fromOpacity);
        if (x.Start(toX, MotionTokens.SectionSwitch, default, Easing.CubicOut))
            animator.Run(x);
        if (opacity.Start(toOpacity, MotionTokens.SectionSwitch, default, Easing.CubicOut, done))
            animator.Run(opacity);
    }

    private static void Reset(Border wrapper)
    {
        var (x, opacity) = Tweens(wrapper);
        x.Snap(0);
        opacity.Snap(1);
    }

    private static (Tween X, Tween Opacity) Tweens(Border wrapper)
    {
        if (wrapper.Tag is (Tween x, Tween o))
            return (x, o);
        var translate = (TranslateTransform)wrapper.RenderTransform;
        var pair = (new Tween(translate.X, v => translate.X = v), new Tween(wrapper.Opacity, v => wrapper.Opacity = v));
        wrapper.Tag = pair;
        return pair;
    }
}
