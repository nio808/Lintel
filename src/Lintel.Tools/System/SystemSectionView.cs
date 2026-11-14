// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Text;
using Lintel.Tools.KeepAwake;
using Lintel.Tools.Ui;

namespace Lintel.Tools.SystemStats;

/// <summary>460 × 200: CPU and network sparkline cards on top; memory, disk, battery and uptime tiles below. Samples at 1 Hz only while shown.</summary>
internal sealed class SystemSectionView : Grid, ISectionLifecycle
{
    private readonly SystemModule module;
    private readonly SystemSampler sampler;
    private readonly DispatcherTimer timer;

    private readonly TextBlock cpuValue = Kit.Text("—", "Notch.Numeric");
    private readonly Sparkline cpuLine = new() { FixedMax = 100 };
    private readonly TextBlock downValue = Kit.Text("—", "Notch.Numeric");
    private readonly TextBlock upValue = Kit.Text("—", "Notch.Numeric");
    private readonly Sparkline netLine = new() { MinScale = 100 * 1024 };
    private readonly TextBlock memValue = Kit.Text("—", "Notch.Numeric");
    private readonly TextBlock memDetail = Kit.Text("", "Notch.Caption");
    private readonly MeterBar memBar = new();
    private readonly TextBlock diskValue = Kit.Text("—", "Notch.Numeric");
    private readonly TextBlock diskDetail = Kit.Text("", "Notch.Caption");
    private readonly MeterBar diskBar = new();
    private readonly TextBlock batValue = Kit.Text("—", "Notch.Numeric");
    private readonly TextBlock batDetail = Kit.Text("", "Notch.Caption");
    private readonly MeterBar batBar = new();
    private readonly TextBlock upTime = Kit.Text("—", "Notch.Numeric");

    public SystemSectionView(SystemModule module)
    {
        this.module = module;
        sampler = module.CreateSampler();
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Update(sampler.Sample());

        Margin = new Thickness(0, 2, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < 4; i++)
            ColumnDefinitions.Add(new ColumnDefinition());

        cpuLine.SetResourceReference(Sparkline.StrokeProperty, "Notch.Accent");
        netLine.SetResourceReference(Sparkline.StrokeProperty, "Tone.Success");
        netLine.SetResourceReference(Sparkline.SecondStrokeProperty, "Agent.Claude");
        memBar.SetResourceReference(MeterBar.FillProperty, "Notch.Accent");
        diskBar.SetResourceReference(MeterBar.FillProperty, "Agent.Codex");
        batBar.SetResourceReference(MeterBar.FillProperty, "Tone.Success");

        Place(Card("CPU", cpuValue, null, cpuLine), 0, 0, 2);
        var net = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        net.Children.Add(Kit.Text("↓ ", "Notch.Caption", "Tone.Success"));
        net.Children.Add(downValue);
        net.Children.Add(Kit.Text("   ↑ ", "Notch.Caption", "Agent.Claude"));
        net.Children.Add(upValue);
        Place(Card("Network", net, null, netLine), 0, 2, 2);

        Place(Tile("Memory", memValue, memDetail, memBar), 1, 0, 1);
        Place(Tile("Disk", diskValue, diskDetail, diskBar), 1, 1, 1);
        Place(Tile("Battery", batValue, batDetail, batBar), 1, 2, 1);
        Place(Tile("Uptime", upTime, null, null), 1, 3, 1);

        if (module.Context.IsDemo)
            Seed();
    }

    public void OnShown()
    {
        module.PowerChanged += UpdatePower;
        UpdatePower();
        sampler.Prime();
        timer.Start();
    }

    public void OnHidden()
    {
        timer.Stop();
        module.PowerChanged -= UpdatePower;
    }

    private void Place(UIElement element, int row, int column, int span)
    {
        SetRow((FrameworkElement)element, row);
        SetColumn((FrameworkElement)element, column);
        SetColumnSpan((FrameworkElement)element, span);
        ((FrameworkElement)element).Margin = new Thickness(column == 0 ? 0 : 3, row == 0 ? 0 : 6, column + span == 4 ? 0 : 3, 0);
        Children.Add(element);
    }

    private static Border Card(string title, UIElement value, UIElement? unused, Sparkline line)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel();
        var name = Kit.Text(title, "Notch.Caption");
        header.Children.Add(name);
        if (value is FrameworkElement fe)
            fe.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(value);
        line.Margin = new Thickness(0, 4, 0, 0);
        SetRow(line, 1);
        grid.Children.Add(header);
        grid.Children.Add(line);
        return Kit.Card(grid);
    }

    private static Border Tile(string title, TextBlock value, TextBlock? detail, MeterBar? bar)
    {
        var stack = new StackPanel();
        stack.Children.Add(Kit.Text(title, "Notch.Caption"));
        value.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(value);
        if (detail is not null)
        {
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(detail);
        }
        if (bar is not null)
        {
            bar.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(bar);
        }
        return Kit.Card(stack, new Thickness(10, 8, 10, 8));
    }

    private void Update(SystemSample s)
    {
        cpuValue.Text = Format.Percent(s.Cpu);
        cpuLine.Push(s.Cpu);
        downValue.Text = Rate(s.NetDown);
        upValue.Text = Rate(s.NetUp);
        netLine.Push(s.NetDown, s.NetUp);
        memValue.Text = Format.Percent(s.MemPercent);
        memDetail.Text = $"{Format.Bytes(s.MemUsed)} of {Format.Bytes(s.MemTotal)}";
        memBar.Value = s.MemPercent / 100;
        if (s.DiskTotal > 0)
        {
            diskValue.Text = Format.Bytes(s.DiskFree);
            diskDetail.Text = $"free of {Format.Bytes(s.DiskTotal)}";
            diskBar.Value = 1 - (double)s.DiskFree / s.DiskTotal;
        }
        upTime.Text = Format.Duration(s.Uptime);
    }

    private void UpdatePower()
    {
        var p = module.LastPower;
        if (!p.HasBattery || p.Percent is not { } percent)
        {
            batValue.Text = "None";
            batDetail.Text = "On AC power";
            batBar.Value = 0;
            return;
        }
        batValue.Text = Format.Percent(percent);
        batDetail.Text = p.Charging ? "Charging" : p.OnBattery ? (p.Remaining is { } left ? $"{Format.Duration(left)} left" : "On battery") : "Plugged in";
        batBar.Value = percent / 100.0;
        batBar.SetResourceReference(MeterBar.FillProperty, BatteryActivity.TintKey(p) is "Notch.Text" ? "Tone.Success" : BatteryActivity.TintKey(p));
    }

    private static string Rate(double bytesPerSecond) => Format.Bytes((long)bytesPerSecond) + "/s";

    /// <summary>--demo: fill the sparklines so screenshots are not empty.</summary>
    private void Seed()
    {
        for (var i = 0; i < Sparkline.Capacity; i++)
        {
            cpuLine.Push(35 + 25 * Math.Sin(i / 5.0) + 8 * Math.Sin(i * 1.7));
            netLine.Push(900_000 + 700_000 * Math.Sin(i / 4.0) + 400_000 * Math.Sin(i * 2.3), 120_000 + 90_000 * Math.Sin(i / 3.0 + 1));
        }
    }
}
