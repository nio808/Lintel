// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using Lintel.Core.Agents;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Text;

namespace Lintel.Agents.UI;

/// <summary>The AI Agents module: pill activities, the AI section, notices and the settings page.</summary>
public sealed class AgentsModule(Func<IModuleContext, IAgentUsageService> serviceFactory) : INotchModule
{
    public const string Id = "agents";
    private const string RefreshHotkey = "agents.refresh";

    private IModuleContext? context;
    private AgentsUiState? state;
    private IReadOnlyList<NotchSection> sections = [];
    private IReadOnlyList<CompactActivity> activities = [];

    public static ModuleManifest Info { get; } = new()
    {
        Id = Id,
        Name = "AI Agents",
        Summary = "Plan limits, tokens, API value and live work for Claude, Codex, Copilot and OpenCode",
        Glyph = AgentDisplay.SectionGlyph,
        Category = ModuleCategory.Agents,
        RequiredPermissions = [PermissionIds.AgentLogs],
        OptionalPermissions = [PermissionIds.ClaudeCredentials, PermissionIds.ClaudeAppHistory, PermissionIds.CodexCli, PermissionIds.NetworkPrices],
        Recommended = true,
        Order = 10,
    };

    public ModuleManifest Manifest => Info;

    public IReadOnlyList<NotchSection> Sections => sections;

    public IReadOnlyList<CompactActivity> Activities => activities;

    public void Start(IModuleContext context)
    {
        if (state is not null)
            return;
        this.context = context;
        var service = serviceFactory(context);
        state = new AgentsUiState(context, service);
        activities = [new LiveActivity(state), new QuotaActivity(state)];
        var section = new Lazy<AgentsSectionVm>(() => new AgentsSectionVm(state));
        sections =
        [
            new NotchSection
            {
                Id = Id,
                Title = "AI",
                Glyph = AgentDisplay.SectionGlyph,
                CreateView = () => new AgentsSectionView(section.Value),
                PreferredSize = new Size(560, 332),
                Order = Info.Order,
            },
        ];
        service.SnapshotChanged += OnSnapshot;
        service.EventRaised += OnEvent;
        context.Settings.Changed += OnSettingChanged;
        context.Hotkeys.Register(RefreshHotkey, "", "Refresh AI limits", () => _ = service.RefreshLimitsAsync());
        service.Start();
        state.Publish(service.Current);
    }

    public void Stop()
    {
        if (state is null || context is null)
            return;
        context.Hotkeys.Unregister(RefreshHotkey);
        context.Settings.Changed -= OnSettingChanged;
        state.Service.SnapshotChanged -= OnSnapshot;
        state.Service.EventRaised -= OnEvent;
        foreach (var activity in activities)
            activity.IsActive = false;
        state.Service.Stop();
        state.Service.Dispose();
        state = null;
    }

    public FrameworkElement? CreateSettingsView() => context is null ? null : new AgentsSettingsView(context, state?.Service);

    public void DeleteData()
    {
        if (context is null)
            return;
        context.Settings.RemovePrefix(Id + ".");
        var directory = context.GetDataDirectory(Id);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo folder)
                    folder.Delete(recursive: true);
                else
                    entry.Delete();
            }
            catch (IOException e)
            {
                context.Log.Warn($"agents: could not delete {entry.Name}", e);
            }
        }
    }

    private void OnSnapshot(object? sender, AgentUsageSnapshot snapshot) => state?.Publish(snapshot);

    private void OnSettingChanged(string key)
    {
        if (state is null || context is null || !key.StartsWith(Id + ".", StringComparison.Ordinal))
            return;
        if (context.Ui.CheckAccess())
            state.RaiseSetting(key);
        else
            context.Ui.BeginInvoke(() => state?.RaiseSetting(key));
    }


    private void OnEvent(object? sender, AgentUsageEvent e)
    {
        if (state is null || context is null)
            return;
        var notice = e switch
        {
            AgentUsageEvent.Finished f => new NotchNotice
            {
                Id = $"agents.finished.{f.Provider}.{f.Project}",
                Title = $"{AgentDisplay.Name(f.Provider)} finished",
                Message = string.Join(" · ", new[] { f.Project, Format.Duration(f.Duration), f.Cost is > 0 ? Format.Money(f.Cost) : null }
                    .Where(p => !string.IsNullOrWhiteSpace(p))),
                Glyph = Glyphs.Check,
                Accent = AgentDisplay.Brush(f.Provider),
                Tone = NoticeTone.Success,
                TargetSectionId = Id,
            },
            AgentUsageEvent.LimitWarning w => Warning(w),
            AgentUsageEvent.LimitReset r => new NotchNotice
            {
                Id = $"agents.limit.{r.Limits.AccountId}.{r.Window.Id}",
                Title = $"{Title(r.Limits)} {AgentDisplay.Label(r.Window)} reset",
                Message = "Fresh allowance available",
                Glyph = Glyphs.Refresh,
                Accent = AgentDisplay.Brush(r.Limits.Provider),
                Tone = NoticeTone.Info,
                TargetSectionId = Id,
            },
            _ => null,
        };
        if (notice is not null)
            context.Notch.ShowNotice(notice);
    }

    private NotchNotice Warning(AgentUsageEvent.LimitWarning w)
    {
        var remaining = w.Window.RemainingPercent;
        var critical = w.Limits.Blocked || remaining <= 5 || w.ThresholdRemaining <= 5;
        var title = w.Limits.Blocked || remaining <= 0
            ? $"{Title(w.Limits)} {AgentDisplay.Label(w.Window)} limit reached"
            : $"{Title(w.Limits)} {AgentDisplay.Label(w.Window)} limit at {Format.Percent(remaining)}";
        return new NotchNotice
        {
            Id = $"agents.limit.{w.Limits.AccountId}.{w.Window.Id}",
            Title = title,
            Message = Format.Countdown(w.Window.ResetsAt, state!.Now),
            Glyph = Glyphs.Warning,
            Accent = critical ? AgentDisplay.Critical : AgentDisplay.Warning,
            Tone = critical ? NoticeTone.Critical : NoticeTone.Warning,
            TargetSectionId = Id,
            Sound = critical,
            BalloonIfHidden = true,
        };
    }

    private string Title(AgentLimits limits) =>
        AgentDisplay.AccountTitle(limits.Provider, state?.Snapshot.Accounts.FirstOrDefault(a => a.Id == limits.AccountId));
}

/// <summary>What the activities, the section and the settings page share: the latest snapshot and the module's settings.</summary>
internal sealed class AgentsUiState(IModuleContext context, IAgentUsageService service)
{
    public IModuleContext Context { get; } = context;
    public IAgentUsageService Service { get; } = service;
    public AgentUsageSnapshot Snapshot { get; private set; } = AgentUsageSnapshot.Empty;

    public event Action? Changed;
    public event Action<string>? SettingChanged;

    public DateTimeOffset Now => Context.Time.GetLocalNow();

    public bool ShowUsed => Context.Settings.Get(UiKeys.PillDisplay, "remaining") == "used";
    public string Focus => Context.Settings.Get(UiKeys.PillFocus, "mostUsed");
    public string Readout => Context.Settings.Get(UiKeys.PillReadout, "elapsed");

    public AgentPeriod Period
    {
        get => Context.Settings.Get(UiKeys.Period, "today") switch { "week" => AgentPeriod.Week, "month" => AgentPeriod.Month, _ => AgentPeriod.Today };
        set => Context.Settings.Set(UiKeys.Period, value switch { AgentPeriod.Week => "week", AgentPeriod.Month => "month", _ => "today" });
    }

    /// <summary>Visible cards in the user's order; ids missing from a saved order are appended so new cards are never lost.</summary>
    public IReadOnlyList<string> Cards
    {
        get
        {
            var order = Context.Settings.Get(UiKeys.CardsOrder, UiKeys.DefaultCards) ?? UiKeys.DefaultCards;
            var hidden = Context.Settings.Get(UiKeys.CardsHidden, Array.Empty<string>()) ?? [];
            return order.Where(UiKeys.DefaultCards.Contains).Concat(UiKeys.DefaultCards).Distinct().Where(c => !hidden.Contains(c)).ToList();
        }
    }

    public bool Enabled(AgentProvider provider) => Context.Settings.Get(UiKeys.Provider(provider), true);

    public IEnumerable<AgentLimits> Limits => Snapshot.Limits.Where(l => Enabled(l.Provider));

    public IEnumerable<AgentLiveSession> Live => Snapshot.Live.Where(s => Enabled(s.Provider));

    public void Publish(AgentUsageSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke();
    }

    public void RaiseSetting(string key) => SettingChanged?.Invoke(key);
}
