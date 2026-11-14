// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Lintel.Core.Notch;

namespace Lintel.Agents.UI;

/// <summary>Root of the AI section: lays the cards out in two columns (wide cards span both) and forwards visibility to the VM.</summary>
internal sealed partial class AgentsSectionView : UserControl, ISectionLifecycle
{
    private const double Gap = 6;
    private const double ScrollRoom = 9;
    private readonly AgentsSectionVm vm;

    public AgentsSectionView(AgentsSectionVm vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        for (var i = 0; i < 24; i++)
        {
            // Skeleton bars: a calm wave, not data.
            var height = 8 + 40 * (0.5 + 0.5 * Math.Sin(i * 0.55));
            BoneBars.Children.Add(new Border { Height = height, Margin = new Thickness(1.5, 0, 1.5, 0), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Resources["Bone"] });
        }
        Skeleton.IsVisibleChanged += (_, _) => Breathe(Skeleton.IsVisible);
        Scroller.ScrollChanged += (_, _) => UpdateScrollChrome();
        vm.TilesChanged += BuildGrid;
        vm.Apply();   // laid out once at its final size before the expand reveals it
        BuildGrid();
    }

    public void OnShown() => vm.Show();

    public void OnHidden() => vm.Hide();

    private void BuildGrid()
    {
        CardGrid.Children.Clear();
        CardGrid.RowDefinitions.Clear();
        CardVm? pending = null;
        foreach (var tile in vm.Tiles)
        {
            if (tile.Wide)
            {
                if (pending is not null)
                    AddRow(pending, null);
                pending = null;
                AddRow(tile, null);
            }
            else if (pending is null)
            {
                pending = tile;
            }
            else
            {
                AddRow(pending, tile);
                pending = null;
            }
        }
        if (pending is not null)
            AddRow(pending, null);
        // No gap under the last row.
        var last = CardGrid.RowDefinitions.Count - 1;
        foreach (FrameworkElement child in CardGrid.Children)
        {
            if (Grid.GetRow(child) == last)
                child.Margin = new Thickness(0);
        }
    }

    /// <summary>A pair shares a row (equal heights); a lone or wide card spans both columns.</summary>
    private void AddRow(CardVm first, CardVm? second)
    {
        var row = CardGrid.RowDefinitions.Count;
        CardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(first, row, 0, second is null ? 3 : 1);
        if (second is not null)
            Add(second, row, 2, 1);
    }

    private void Add(CardVm card, int row, int column, int span)
    {
        var host = new ContentPresenter { Content = card, Margin = new Thickness(0, 0, 0, Gap) };
        Grid.SetRow(host, row);
        Grid.SetColumn(host, column);
        Grid.SetColumnSpan(host, span);
        CardGrid.Children.Add(host);
    }

    private void UpdateScrollChrome()
    {
        // Room for the overlay scrollbar only when there is something to scroll; the hysteresis avoids a wrap/unwrap loop.
        var scrollable = Scroller.ScrollableHeight;
        var right = CardGrid.Margin.Right;
        if (scrollable > ScrollRoom * 2 && right == 0)
            CardGrid.Margin = new Thickness(0, 0, ScrollRoom, 0);
        else if (scrollable <= 0 && right > 0)
            CardGrid.Margin = new Thickness(0);
        BottomFade.Visibility = Scroller.IsVisible && scrollable - Scroller.VerticalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Breathe(bool on)
    {
        if (!on || !SystemParameters.ClientAreaAnimation)
        {
            Skeleton.BeginAnimation(OpacityProperty, null);
            return;
        }
        var breathe = new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        Timeline.SetDesiredFrameRate(breathe, 30);
        Skeleton.BeginAnimation(OpacityProperty, breathe);
    }
}
