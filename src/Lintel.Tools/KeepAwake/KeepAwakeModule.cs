// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Text;
using Lintel.Tools.SystemStats;
using Lintel.Tools.Ui;

namespace Lintel.Tools.KeepAwake;

public sealed class KeepAwakeModule : INotchModule, IToolsPanel
{
    public const string DisplayKey = "keepawake.display", AutoOffKey = "keepawake.autoOff";
    private readonly Func<IAwakeBackend> backendFactory;
    private readonly Func<PowerReading> readPower;
    private readonly Func<string, bool> isRunning;
    private IModuleContext? context;
    private IAwakeBackend? backend;
    private DispatcherTimer? ticker;
    private KeepAwakeActivity? activity;
    private int tickCount;
    private bool appRunning = true;
    private bool started;

    public KeepAwakeModule() : this(() => new ExecutionStateThread(), new WindowsSystemSource().Power, ProcessIsRunning) { }

    /// <summary>For tests and demo: swap the OS-facing pieces.</summary>
    internal KeepAwakeModule(Func<IAwakeBackend> backend, Func<PowerReading> power, Func<string, bool> processRunning)
    {
        backendFactory = backend;
        readPower = power;
        isRunning = processRunning;
    }

    public ModuleManifest Manifest { get; } = new()
    {
        Id = "keepawake",
        Name = "Keep Awake",
        Summary = "Stop the PC or display from sleeping for a while.",
        Glyph = Glyphs.Cup,
        Category = ModuleCategory.System,
        RequiredPermissions = [PermissionIds.KeepAwake],
        Order = 100,
    };

    public IReadOnlyList<NotchSection> Sections { get; private set; } = [];
    public IReadOnlyList<CompactActivity> Activities { get; private set; } = [];

    int IToolsPanel.Order => 0;
    double? IToolsPanel.Width => 190;
    FrameworkElement IToolsPanel.Create() => new KeepAwakePanel(this);

    internal IModuleContext Context => context!;
    internal KeepAwakeSession? Session { get; private set; }
    /// <summary>Minutes of the running timed session (for chip highlighting), null otherwise.</summary>
    internal int? Minutes { get; private set; }
    internal event Action? Updated;

    internal bool KeepDisplay
    {
        get => context!.Settings.Get(DisplayKey, true);
        set { context!.Settings.Set(DisplayKey, value); Reapply(); }
    }

    internal bool AutoOff
    {
        get => context!.Settings.Get(AutoOffKey, true);
        set { context!.Settings.Set(AutoOffKey, value); Reapply(); }
    }

    internal TimeSpan? Remaining => Session?.Remaining(context!.Time.GetUtcNow());

    public void Start(IModuleContext ctx)
    {
        if (started)
            return;
        started = true;
        context = ctx;
        backend = ctx.IsDemo ? new NullBackend() : backendFactory();
        ticker = new DispatcherTimer(DispatcherPriority.Normal, ctx.Ui) { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => Tick();
        activity = new KeepAwakeActivity();
        Activities = [activity];
        Sections = [ToolsSection.Create()];
        ToolsSection.Register(this);
        ctx.Hotkeys.Register("keepawake.toggle", "", "Turn keep awake on or off", () => ctx.Ui.InvokeAsync(() => Toggle()));
        if (ctx.IsDemo)
            Begin(KeepAwakeSession.Timed(ctx.Time.GetUtcNow(), new TimeSpan(0, 47, 12), true, true), 60);
    }

    public void Stop()
    {
        if (!started)
            return;
        started = false;
        ToolsSection.Unregister(this);
        context!.Hotkeys.Unregister("keepawake.toggle");
        End(null);
        backend?.Dispose();
        backend = null;
    }

    public FrameworkElement? CreateSettingsView()
    {
        var panel = new StackPanel();
        panel.Children.Add(Option("Keep the display on too", DisplayKey));
        panel.Children.Add(Option("Turn off below 20% battery", AutoOffKey));
        return panel;

        CheckBox Option(string text, string key)
        {
            var box = new CheckBox { Content = text, Margin = new Thickness(0, 2, 0, 2) };
            box.Loaded += (_, _) =>
            {
                if (context is null)
                    return;
                box.IsChecked = context.Settings.Get(key, true);
                box.Checked += (_, _) => context.Settings.Set(key, true);
                box.Unchecked += (_, _) => context.Settings.Set(key, false);
            };
            return box;
        }
    }

    public void DeleteData() => context?.Settings.RemovePrefix("keepawake.");

    internal void StartTimed(int minutes) =>
        Begin(KeepAwakeSession.Timed(context!.Time.GetUtcNow(), TimeSpan.FromMinutes(minutes), KeepDisplay, AutoOff), minutes);

    internal void StartIndefinite() => Begin(KeepAwakeSession.Indefinite(KeepDisplay, AutoOff), null);

    internal void StartWhileApp(string processName) => Begin(KeepAwakeSession.WhileApp(processName, KeepDisplay, AutoOff), null);

    internal void Toggle()
    {
        if (Session is null) StartIndefinite();
        else End(null);
    }

    /// <summary>User turned it off (reason null: quiet) or a rule ended it.</summary>
    internal void End(KeepAwakeEnd? reason)
    {
        var ended = Session;
        Session = null;
        Minutes = null;
        ticker?.Stop();
        backend?.Clear();
        if (activity is not null)
            activity.IsActive = false;
        Updated?.Invoke();
        if (reason is { } why && ended is not null)
            Notify(why, ended);
    }

    private void Begin(KeepAwakeSession session, int? minutes)
    {
        Session = session;
        Minutes = minutes;
        tickCount = 0;
        appRunning = true;
        backend!.Set(session.KeepDisplay);
        ticker!.Start();
        UpdateActivity();
        Updated?.Invoke();
    }

    private void Reapply()
    {
        if (Session is { } s)
        {
            Session = s.WithOptions(KeepDisplay, AutoOff);
            backend!.Set(Session.KeepDisplay);
        }
        Updated?.Invoke();
    }

    private void Tick()
    {
        if (Session is not { } session)
            return;
        if (session.Kind == KeepAwakeKind.WhileApp && tickCount++ % 5 == 0)
            appRunning = isRunning(session.AppName!);
        var power = session.AutoOffOnLowBattery ? readPower() : default;
        if (session.Check(context!.Time.GetUtcNow(), power, appRunning) is { } reason)
        {
            End(reason);
            return;
        }
        UpdateActivity();
        Updated?.Invoke();
    }

    private void UpdateActivity()
    {
        if (activity is null || Session is not { } s)
            return;
        activity.IsActive = true;
        activity.SetText(s.Remaining(context!.Time.GetUtcNow()) is { } left ? Format.Clock(TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds)))
            : s.Kind == KeepAwakeKind.WhileApp ? "app" : "on");
    }

    private void Notify(KeepAwakeEnd reason, KeepAwakeSession session) =>
        context!.Notch.ShowNotice(new NotchNotice
        {
            Id = "keepawake.ended",
            Title = "Keep awake ended",
            Message = reason switch
            {
                KeepAwakeEnd.LowBattery => "Battery is below 20%, so your PC can sleep again.",
                KeepAwakeEnd.AppExited => $"{session.AppName} closed. Your PC can sleep again.",
                _ => "Your PC can sleep again.",
            },
            Glyph = Glyphs.Cup,
            Tone = reason == KeepAwakeEnd.LowBattery ? NoticeTone.Warning : NoticeTone.Info,
            TargetSectionId = "tools",
        });

    private static bool ProcessIsRunning(string name)
    {
        var found = Process.GetProcessesByName(name);
        try { return found.Length > 0; }
        finally { foreach (var p in found) p.Dispose(); }
    }

    private sealed class NullBackend : IAwakeBackend
    {
        public void Set(bool keepDisplay) { }
        public void Clear() { }
        public void Dispose() { }
    }
}

/// <summary>Pill: cup glyph on the left, time left on the right.</summary>
internal sealed class KeepAwakeActivity : CompactActivity
{
    private TextBlock? text;
    private string value = "";

    public KeepAwakeActivity() : base("keepawake.on", 30, "tools") { }

    public override FrameworkElement CreateLeading() => Kit.Icon(Glyphs.Cup, 12, "Notch.Accent");

    public override FrameworkElement? CreateTrailing()
    {
        text = Kit.Text(value, "Notch.Numeric");
        text.HorizontalAlignment = HorizontalAlignment.Right;
        return text;
    }

    public void SetText(string newValue)
    {
        value = newValue;
        if (text is not null)
            text.Text = newValue;
    }
}
