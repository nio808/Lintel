// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lintel.Notch.Layout;
using Lintel.Notch.Motion;

namespace Lintel.Notch.Views;

/// <summary>The collapsed pill's two wings. Changes cross-fade in 180 ms; a click opens the wing's target section.</summary>
internal sealed class CompactBar : Grid
{
    private readonly Wing left;
    private readonly Wing right;

    public CompactBar(Animator animator)
    {
        Height = NotchMetrics.CollapsedHeight;
        left = new Wing(animator, HorizontalAlignment.Left, this);
        right = new Wing(animator, HorizontalAlignment.Right, this);
        Children.Add(left);
        Children.Add(right);
        ClipToBounds = false;
    }

    /// <summary>A wing's content size changed (new activity or its own content grew).</summary>
    public event Action? ContentSizeChanged;

    /// <summary>Target section of the clicked wing (null = no target).</summary>
    public event Action<string?>? WingClicked;

    public double LeadingWidth => left.ContentWidth;
    public double TrailingWidth => right.ContentWidth;

    /// <summary>Target section of the wing under the pointer (hover-expand opens it), or null.</summary>
    public string? HoveredTarget => left.IsMouseOver ? left.Target : right.IsMouseOver ? right.Target : null;

    public void Show(FrameworkElement? leading, string? leadingTarget, FrameworkElement? trailing, string? trailingTarget, bool animate)
    {
        left.Set(leading, leadingTarget, animate);
        right.Set(trailing, trailingTarget, animate);
    }

    private sealed class Wing : Grid
    {
        private readonly Animator animator;
        private readonly CompactBar owner;
        private Border? current;
        private string? target;

        public Wing(Animator animator, HorizontalAlignment side, CompactBar owner)
        {
            this.animator = animator;
            this.owner = owner;
            HorizontalAlignment = side;
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(NotchMetrics.WingPadding, 0, NotchMetrics.WingPadding, 0);
            MaxWidth = NotchMetrics.WingMaxWidth;
            MaxHeight = NotchMetrics.WingMaxHeight;
            Background = System.Windows.Media.Brushes.Transparent;   // whole wing is clickable
            MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled || current is null)
                    return;
                e.Handled = true;
                owner.WingClicked?.Invoke(target);
            };
            Cursor = Cursors.Arrow;
        }

        public double ContentWidth => current?.Child is null ? 0 : current.DesiredSize.Width;

        public string? Target => current is null ? null : target;

        public void Set(FrameworkElement? element, string? sectionTarget, bool animate)
        {
            target = sectionTarget;
            if (current?.Child == element && element is not null)
                return;
            if (current is null && element is null)
                return;
            var old = current;
            current = null;
            if (old is not null)
                Retire(old, animate);
            if (element is not null)
            {
                if (element.Parent is Border previousHost)
                    previousHost.Child = null;   // moving from the other wing: no cross-fade for that copy
                var host = new Border { Child = element, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment };
                host.SizeChanged += (_, _) => owner.ContentSizeChanged?.Invoke();
                Children.Add(host);
                host.Measure(new Size(NotchMetrics.WingMaxWidth, NotchMetrics.WingMaxHeight));
                current = host;
                Fade(host, 0, 1, animate, null);
            }
            owner.ContentSizeChanged?.Invoke();
        }

        private void Retire(Border host, bool animate)
        {
            Fade(host, host.Opacity, 0, animate, () =>
            {
                host.Child = null;
                Children.Remove(host);
            });
        }

        private void Fade(Border host, double from, double to, bool animate, Action? done)
        {
            if (!animate)
            {
                host.Opacity = to;
                done?.Invoke();
                return;
            }
            host.Opacity = from;
            var tween = new Tween(from, v => host.Opacity = v);
            if (tween.Start(to, MotionTokens.ActivityCrossfade, default, Easing.CubicOut, done))
                animator.Run(tween);
        }
    }
}
