// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Tools.KeepAwake;
using Lintel.Tools.Ui;

namespace Lintel.Tools.SystemStats;

public sealed class SystemModule : INotchModule
{
    public const string ShowBatteryKey = "system.showBattery";
    private readonly Func<IModuleContext, ISystemSource>? sourceFactory;
    private IModuleContext? context;
    private ISystemSource? source;
    private DispatcherTimer? batteryTimer;
    private DispatcherTimer? plugTimer;
    private BatteryActivity? activity;
    private readonly BatteryWatcher watcher = new();
    private bool started;

    public SystemModule() { }

    internal SystemModule(Func<IModuleContext, ISystemSource> source) => sourceFactory = source;

    public ModuleManifest Manifest { get; } = new()
    {
        Id = "system",
        Name = "System",
        Summary = "CPU, memory, network, disk and battery at a glance.",
        Glyph = Glyphs.Chip,
        Category = ModuleCategory.System,
        RequiredPermissions = [PermissionIds.SystemStats],
        Recommended = true,
        Order = 60,
    };

    public IReadOnlyList<NotchSection> Sections { get; private set; } = [];
    public IReadOnlyList<CompactActivity> Activities { get; private set; } = [];

    internal IModuleContext Context => context!;
    internal SystemSampler CreateSampler() => new(source!, context!.Time);
    internal PowerReading LastPower { get; private set; }
    internal event Action? PowerChanged;

    public void Start(IModuleContext ctx)
    {
        if (started)
            return;
        started = true;
        context = ctx;
        source = sourceFactory?.Invoke(ctx) ?? (ctx.IsDemo ? new DemoSystemSource() : new WindowsSystemSource());
        activity = new BatteryActivity();
        Activities = [activity];
        Sections =
        [
            new NotchSection
            {
                Id = "system", Title = "System", Glyph = Glyphs.Chip, Order = 60,
                PreferredSize = new Size(460, 200),
                CreateView = () => new SystemSectionView(this),
            },
        ];

        // Battery: every 60 s always, plus an immediate read when Windows reports a power-source change.
        batteryTimer = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui) { Interval = TimeSpan.FromSeconds(60) };
        batteryTimer.Tick += (_, _) => ReadBattery();
        batteryTimer.Start();
        SystemEvents.PowerModeChanged += OnPowerMode;
        context.Settings.Changed += OnSetting;
        ReadBattery();
    }

    public void Stop()
    {
        if (!started)
            return;
        started = false;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        context!.Settings.Changed -= OnSetting;
        batteryTimer?.Stop();
        plugTimer?.Stop();
        if (activity is not null)
            activity.IsActive = false;
    }

    public FrameworkElement? CreateSettingsView()
    {
        var box = new CheckBox { Content = "Always show the battery in the pill", Margin = new Thickness(0, 2, 0, 2) };
        box.Loaded += (_, _) =>
        {
            if (context is null)
                return;
            box.IsChecked = context.Settings.Get(ShowBatteryKey, false);
            box.Checked += (_, _) => context.Settings.Set(ShowBatteryKey, true);
            box.Unchecked += (_, _) => context.Settings.Set(ShowBatteryKey, false);
        };
        return box;
    }

    public void DeleteData() => context?.Settings.RemovePrefix("system.");

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
            context?.Ui.InvokeAsync(ReadBattery);
    }

    private void OnSetting(string key)
    {
        if (key == ShowBatteryKey)
            context?.Ui.InvokeAsync(UpdateActivity);
    }

    private void ReadBattery()
    {
        if (!started)
            return;
        var power = source!.Power();
        var alert = watcher.Update(power);
        LastPower = power;
        if (watcher.PlugChanged && power.HasBattery)
        {
            // Show the battery in the pill for 5 s after plugging in or unplugging.
            plugTimer?.Stop();
            plugTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                plugTimer = null;
                UpdateActivity();
            }, context!.Ui);
        }
        if (alert != BatteryAlert.None)
        {
            var critical = alert == BatteryAlert.Low10;
            context!.Notch.ShowNotice(new NotchNotice
            {
                Id = "system.battery",
                Title = critical ? "Battery critically low" : "Battery low",
                Message = $"{power.Percent}% left. Plug in soon.",
                Glyph = Glyphs.Battery,
                Tone = critical ? NoticeTone.Critical : NoticeTone.Warning,
                TargetSectionId = "system",
                BalloonIfHidden = critical,
            });
        }
        UpdateActivity();
        PowerChanged?.Invoke();
    }

    private void UpdateActivity()
    {
        if (activity is null || !started)
            return;
        var power = LastPower;
        activity.Update(power);
        activity.IsActive = power.HasBattery && power.Percent is not null
            && (context!.Settings.Get(ShowBatteryKey, false) || plugTimer is { IsEnabled: true });
    }
}

/// <summary>Pill: battery glyph + percent, tinted by state.</summary>
internal sealed class BatteryActivity : CompactActivity
{
    private readonly TextBlock glyph = Kit.Icon(Glyphs.Battery);
    private readonly TextBlock text = Kit.Text("", "Notch.Caption", "Notch.Text");

    public BatteryActivity() : base("system.battery", 10, "system") { }

    public override FrameworkElement CreateLeading()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        text.Margin = new Thickness(4, 0, 0, 0);
        text.FontWeight = FontWeights.SemiBold;
        row.Children.Add(glyph);
        row.Children.Add(text);
        return row;
    }

    public void Update(PowerReading power)
    {
        text.Text = power.Percent is { } p ? p + "%" : "";
        glyph.SetResourceReference(TextBlock.ForegroundProperty, TintKey(power));
    }

    public static string TintKey(PowerReading power) =>
        power.Charging ? "Tone.Success" : power is { OnBattery: true, Percent: <= 10 } ? "Tone.Critical" : power is { OnBattery: true, Percent: <= 20 } ? "Tone.Warning" : "Notch.Text";
}
