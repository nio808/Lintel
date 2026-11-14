// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lintel.Core.Agents;
using Lintel.Core.Text;

namespace Lintel.Agents.UI;

// View-models only raise PropertyChanged when a value really changes (SetProperty), and collections are synced in place,
// so a snapshot that changes one number re-renders one TextBlock — not the card.

internal abstract class CardVm : ObservableObject
{
    public abstract string Key { get; }
    public virtual bool Wide => false;
}

internal sealed class LimitRowVm(string id) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; set => SetProperty(ref field, value); } = "";
    public string Countdown { get; set => SetProperty(ref field, value); } = "";
    public string Value { get; set => SetProperty(ref field, value); } = "";
    public double Percent { get; set => SetProperty(ref field, value); }
    public double PacePercent { get; set => SetProperty(ref field, value); } = double.NaN;
    public Brush Fill { get; set => SetProperty(ref field, value); } = Brushes.White;
    public Brush ValueBrush { get; set => SetProperty(ref field, value); } = Brushes.White;
    public double Dim { get; set => SetProperty(ref field, value); } = 1;
    public string Tip { get; set => SetProperty(ref field, value); } = "";
}

internal sealed class LimitsCardVm : CardVm
{
    private readonly AgentsUiState state;

    public LimitsCardVm(AgentsUiState state, string accountId)
    {
        this.state = state;
        AccountId = accountId;
        RefreshCommand = new AsyncRelayCommand(() => state.Service.RefreshLimitsAsync(AccountId), AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    public string AccountId { get; }
    public override string Key => "limits:" + AccountId;
    public IAsyncRelayCommand RefreshCommand { get; }
    public ObservableCollection<LimitRowVm> Rows { get; } = [];

    public AgentProvider Provider
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(Brush));
                OnPropertyChanged(nameof(SoftBrush));
            }
        }
    }

    public Brush Brush => AgentDisplay.Brush(Provider);
    public Brush SoftBrush => Soft.For(Provider);
    public string Title { get; set => SetProperty(ref field, value); } = "";
    public string? Plan { get; set => SetProperty(ref field, value); }
    public bool IsStale { get; set => SetProperty(ref field, value); }
    public bool IsBlocked { get; set => SetProperty(ref field, value); }
    public bool IsWorking { get; set => SetProperty(ref field, value); }
    public string? Message { get; set => SetProperty(ref field, value); }
    public string? Footer { get; set => SetProperty(ref field, value); }
    public Brush FooterBrush { get; set => SetProperty(ref field, value); } = Brushes.Gray;
    public string RefreshTip { get; set => SetProperty(ref field, value); } = "";

    public void Update(AgentAccount? account, AgentProvider provider, AgentLimits? limits, DateTimeOffset now)
    {
        var used = state.ShowUsed;
        Provider = provider;
        Title = AgentDisplay.AccountTitle(provider, account);
        Plan = limits?.Plan?.Name;
        IsStale = limits?.IsStale == true;
        IsBlocked = limits?.Blocked == true;
        IsWorking = state.Live.Any(s => s.Provider == provider) && (account?.IsDefault ?? true);
        var windows = (limits?.Windows ?? [])
            .OrderBy(w => w.Kind).ThenBy(w => w.Scope is null ? 0 : 1).ThenBy(w => w.Minutes ?? int.MaxValue).ToList();
        // Blocked paints the exhausted window(s) red; only when none is exhausted (e.g. a spend cap) does the whole card go red.
        var blockAll = IsBlocked && !windows.Any(w => w.RemainingPercent <= 0);
        Sync.Collection(Rows, windows, (row, w) => row.Id == w.Id, w => new LimitRowVm(w.Id), (row, w) =>
        {
            var due = w.ResetDue(now);
            var tone = blockAll ? LimitTone.Critical : AgentDisplay.Tone(w.RemainingPercent);
            var shown = AgentDisplay.Shown(w, used);
            var elapsed = AgentDisplay.Elapsed(w, now);
            row.Label = AgentDisplay.Label(w);
            row.Countdown = AgentDisplay.ShortCountdown(w.ResetsAt, now);
            row.Value = Format.Percent(shown);
            row.Percent = shown;
            row.PacePercent = elapsed is { } e ? 100 * (used ? e : 1 - e) : double.NaN;
            row.Fill = AgentDisplay.ToneBrush(tone, provider);
            row.ValueBrush = tone == LimitTone.Normal ? AgentDisplay.Text : AgentDisplay.ToneBrush(tone, provider);
            row.Dim = due ? 0.45 : IsStale ? 0.6 : 1;
            row.Tip = $"{row.Label} · {Format.Percent(w.UsedPercent)} used · {Format.Percent(w.RemainingPercent)} left"
                + Environment.NewLine + Format.Countdown(w.ResetsAt, now)
                + (w.ResetsAt is { } r ? $" · {r.ToLocalTime():ddd d MMM, HH:mm}" : "")
                + (elapsed is not null ? Environment.NewLine + "The tick marks where an even pace would be" : "");
        });

        Message = windows.Count > 0 ? null
            : limits is null ? "Checking limits…"
            : limits.Error ?? "No plan limits reported for this account";
        (Footer, FooterBrush) = FooterFor(limits, windows.Count > 0, used, now);
        RefreshTip = limits?.NextAttempt is { } next && next > now
            ? $"Refresh limits · next check {AgentDisplay.In(next, now)}"
            : "Refresh limits";
    }

    private static (string?, Brush) FooterFor(AgentLimits? limits, bool hasWindows, bool used, DateTimeOffset now)
    {
        if (limits is null)
            return (null, Brushes.Gray);
        if (hasWindows && (limits.IsStale || limits.Error is not null))
        {
            var parts = new List<string> { $"Last known · {AgentDisplay.Ago(limits.ObservedAt, now)}" };
            if (limits.Error is { } error)
                parts.Add(error);
            if (limits.NextAttempt is { } next && next > now)
                parts.Add("retry " + AgentDisplay.In(next, now));
            return (string.Join(" · ", parts), AgentDisplay.Warning);
        }
        if (!hasWindows)
            return (null, Brushes.Gray);
        var source = limits.Source switch
        {
            LimitSource.ClaudeApp => "From the Claude app",
            LimitSource.SessionLog => "From session logs",
            _ => "Updated",
        };
        var line = $"{source} {AgentDisplay.Ago(limits.ObservedAt, now)} · % {(used ? "used" : "left")}";
        return (limits.Credits is { Length: > 0 } credits ? $"{credits}  ·  {line}" : line, Soft.Tertiary);
    }
}

internal sealed record LegendItem(Brush Brush, string Name, string Value);

internal sealed record ChipItem(string Text, Brush Brush, Brush Background, string Tip);

internal sealed class SpendCardVm : CardVm
{
    public override string Key => "spend";
    public string Value { get; set => SetProperty(ref field, value); } = "";
    public string ValueTip { get; set => SetProperty(ref field, value); } = "";
    public string PeriodText { get; set => SetProperty(ref field, value); } = "";
    public IReadOnlyList<SplitSegment> Segments { get; set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<LegendItem> Legend { get; set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<ChipItem> Chips { get; set => SetProperty(ref field, value); } = [];
    public string Tokens { get; set => SetProperty(ref field, value); } = "";
    public string Requests { get; set => SetProperty(ref field, value); } = "";
    public string CacheHit { get; set => SetProperty(ref field, value); } = "";
    public string Saved { get; set => SetProperty(ref field, value); } = "";
    public bool IsLoading { get; set => SetProperty(ref field, value); }

    public void Update(AgentsUiState state, AgentPeriod period)
    {
        var snapshot = state.Snapshot;
        var usage = snapshot.Periods.TryGetValue(period, out var u) ? u : null;
        var providers = AgentDisplay.Providers.Where(state.Enabled).ToList();
        var by = providers.Select(p => (Provider: p, Totals: usage?.ByProvider.GetValueOrDefault(p) ?? AgentTotals.Zero)).ToList();
        var total = usage is null ? AgentTotals.Zero
            : usage.ByProvider.Count == 0 ? usage.Total
            : by.Aggregate(AgentTotals.Zero, (sum, x) => sum + x.Totals);
        var byCost = total.Cost > 0;
        IsLoading = !snapshot.Loaded;
        PeriodText = Periods.Long(period);
        Value = total.Cost <= 0 && total.Unpriced > 0 ? "—" : (total.Unpriced > 0 ? "≥ " : "") + Format.Money(total.Cost);
        ValueTip = total.Unpriced > 0
            ? $"What these tokens would cost at public API prices. {AgentDisplay.Count(total.Unpriced)} requests used models without a price."
            : "What these tokens would cost at public API list prices";
        Segments = Same(Segments, by.Where(x => Weight(x.Totals, byCost) > 0).Select(x => new SplitSegment(AgentDisplay.Brush(x.Provider), Weight(x.Totals, byCost))).ToList());
        Legend = Same(Legend, by.Where(x => x.Totals.Tokens.Total > 0 || x.Totals.Cost > 0)
            .Select(x => new LegendItem(AgentDisplay.Brush(x.Provider), AgentDisplay.Name(x.Provider), byCost ? Format.Money(x.Totals.Cost) : Format.Tokens(x.Totals.Tokens.Total))).ToList());
        Tokens = Format.Tokens(total.Tokens.Total);
        Requests = AgentDisplay.Count(total.Requests);
        CacheHit = total.Tokens.CacheHitRate is { } rate ? Format.Percent(rate * 100) : "—";
        Saved = Format.Money(total.Savings);
        Chips = Same(Chips, period == AgentPeriod.Month ? PlanChips(state, by) : []);
    }

    private static List<ChipItem> PlanChips(AgentsUiState state, List<(AgentProvider Provider, AgentTotals Totals)> by)
    {
        var chips = new List<ChipItem>();
        foreach (var (provider, totals) in by)
        {
            var plan = state.Limits.Where(l => l.Provider == provider).Select(l => l.Plan).FirstOrDefault(p => p?.MonthlyPrice > 0);
            if (plan?.MonthlyPrice is not { } price || totals.Cost <= 0)
                continue;
            var multiple = totals.Cost / price;
            var text = multiple.ToString(multiple < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "×";
            chips.Add(new ChipItem($"{text} {plan.Name}", AgentDisplay.Brush(provider), Soft.For(provider),
                $"30 days of {AgentDisplay.Name(provider)} API value is {text} the {Format.Money(price)}/mo {plan.Name} plan"));
        }
        return chips;
    }

    private static double Weight(AgentTotals totals, bool byCost) => byCost ? totals.Cost : totals.Tokens.Total;

    private static IReadOnlyList<T> Same<T>(IReadOnlyList<T> current, IReadOnlyList<T> next) => current.SequenceEqual(next) ? current : next;
}

internal sealed class LiveRowVm(string id) : ObservableObject
{
    public string Id { get; } = id;
    public AgentProvider Provider { get; set => SetProperty(ref field, value); }
    public Brush Brush { get; set => SetProperty(ref field, value); } = Brushes.White;
    public string Project { get; set => SetProperty(ref field, value); } = "";
    public string Detail { get; set => SetProperty(ref field, value); } = "";
    public string Elapsed { get; set => SetProperty(ref field, value); } = "";
    public DateTimeOffset Started { get; set; }
    public string Tip { get; set => SetProperty(ref field, value); } = "";
}

internal sealed class LiveCardVm : CardVm
{
    private const int MaxRows = 3;

    public override string Key => "live";
    public ObservableCollection<LiveRowVm> Rows { get; } = [];
    public bool IsEmpty { get; set => SetProperty(ref field, value); } = true;
    public string CountText { get; set => SetProperty(ref field, value); } = "";
    public string? More { get; set => SetProperty(ref field, value); }

    public void Update(AgentsUiState state, DateTimeOffset now)
    {
        var live = state.Live.OrderBy(s => s.Started).ToList();
        IsEmpty = live.Count == 0;
        CountText = live.Count switch { 0 => "", 1 => "1 session", _ => $"{live.Count} sessions" };
        More = live.Count > MaxRows ? $"+{live.Count - MaxRows} more" : null;
        Sync.Collection(Rows, live.Take(MaxRows).ToList(), (row, s) => row.Id == s.Id, s => new LiveRowVm(s.Id), (row, s) =>
        {
            row.Provider = s.Provider;
            row.Brush = AgentDisplay.Brush(s.Provider);
            row.Project = string.IsNullOrWhiteSpace(s.Project) ? AgentDisplay.Name(s.Provider) : s.Project;
            row.Detail = string.Join(" · ", AgentDisplay.ModelName(s.Model), Format.Tokens(s.Tokens.Total) + " tokens", Format.Money(s.Cost));
            row.Started = s.Started;
            row.Elapsed = Format.Clock(now - s.Started);
            row.Tip = $"{AgentDisplay.Name(s.Provider)} · started {s.Started.ToLocalTime():HH:mm} · last activity {AgentDisplay.Ago(s.LastActivity, now)}"
                + (s.Tokens.CacheHitRate is { } rate ? $" · {Format.Percent(rate * 100)} cached" : "");
        });
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var row in Rows)
            row.Elapsed = Format.Clock(now - row.Started);
    }
}

internal sealed class TrendCardVm : CardVm
{
    private string defaultCaption = "";

    public override string Key => "trend";
    public override bool Wide => true;
    public IReadOnlyList<TrendBar> Bars { get; set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<Brush> PartBrushes { get; set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<LegendItem> Legend { get; set => SetProperty(ref field, value); } = [];
    public string Caption { get; set => SetProperty(ref field, value); } = "";
    public string AxisStart { get; set => SetProperty(ref field, value); } = "";
    public string AxisMiddle { get; set => SetProperty(ref field, value); } = "";
    public string AxisEnd { get; set => SetProperty(ref field, value); } = "";
    public bool IsLoading { get; set => SetProperty(ref field, value); }

    public int HoveredIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Caption = value >= 0 && value < Bars.Count ? Bars[value].Detail : defaultCaption;
        }
    } = -1;

    public void Update(AgentsUiState state, AgentPeriod period)
    {
        var snapshot = state.Snapshot;
        IsLoading = !snapshot.Loaded;
        var buckets = period switch
        {
            AgentPeriod.Today => snapshot.Hours.TakeLast(24).ToList(),
            AgentPeriod.Week => snapshot.Days.TakeLast(7).ToList(),
            _ => snapshot.Days.TakeLast(30).ToList(),
        };
        var providers = AgentDisplay.Providers.Where(p => state.Enabled(p) && buckets.Any(b => b.ByProvider.TryGetValue(p, out var t) && (t.Cost > 0 || t.Tokens.Total > 0))).ToList();
        var usage = snapshot.Periods.TryGetValue(period, out var u) ? u.Total : AgentTotals.Zero;
        var byCost = usage.Cost > 0 && usage.Unpriced == 0;
        string Value(AgentTotals t) => byCost ? Format.Money(t.Cost) : Format.Tokens(t.Tokens.Total) + " tokens";
        string Label(DateTimeOffset start, bool full) => period switch
        {
            AgentPeriod.Today => start.ToLocalTime().ToString("HH:00", CultureInfo.InvariantCulture),
            AgentPeriod.Week when !full => start.ToLocalTime().ToString("ddd", CultureInfo.InvariantCulture),
            _ => start.ToLocalTime().ToString(full ? "ddd d MMM" : "d MMM", CultureInfo.InvariantCulture),
        };
        var bars = buckets.Select(b =>
        {
            var totals = providers.Select(p => b.ByProvider.GetValueOrDefault(p) ?? AgentTotals.Zero).ToList();
            var sum = totals.Aggregate(AgentTotals.Zero, (a, t) => a + t);
            var detail = $"{Label(b.Start, true)} · {Value(sum)}" + (byCost && sum.Tokens.Total > 0 ? $" · {Format.Tokens(sum.Tokens.Total)} tokens" : "");
            return new TrendBar(totals.Select(t => byCost ? t.Cost : t.Tokens.Total).ToArray(), Label(b.Start, false), detail);
        }).ToList();
        if (bars.Count != Bars.Count || !bars.Zip(Bars).All(p => p.First.SameAs(p.Second)))
            Bars = bars;
        var brushes = providers.Select(AgentDisplay.Brush).ToList();
        if (!brushes.SequenceEqual(PartBrushes))
            PartBrushes = brushes;
        var legend = providers.Select(p => new LegendItem(AgentDisplay.Brush(p), AgentDisplay.Name(p), "")).ToList();
        if (!legend.SequenceEqual(Legend))
            Legend = legend;
        AxisStart = bars.Count > 0 ? bars[0].Label : "";
        AxisMiddle = bars.Count > 2 ? bars[bars.Count / 2].Label : "";
        AxisEnd = bars.Count > 1 ? (period == AgentPeriod.Today ? "Now" : "Today") : "";
        defaultCaption = $"{Periods.Long(period)} · {Value(usage)}";
        Caption = HoveredIndex >= 0 && HoveredIndex < Bars.Count ? Bars[HoveredIndex].Detail : defaultCaption;
    }
}

internal sealed class ShareRowVm(string name) : ObservableObject
{
    public string Name { get; } = name;
    public string Display { get; set => SetProperty(ref field, value); } = "";
    public string Value { get; set => SetProperty(ref field, value); } = "";
    public double BarWidth { get; set => SetProperty(ref field, value); }
    public Brush Brush { get; set => SetProperty(ref field, value); } = Brushes.White;
    public string Tip { get; set => SetProperty(ref field, value); } = "";
}

internal sealed class ShareCardVm(string key, string title, bool models) : CardVm
{
    public const double BarRoom = 52;
    private const int MaxRows = 5;

    public override string Key => key;
    public string Title { get; } = title;
    public ObservableCollection<ShareRowVm> Rows { get; } = [];
    public bool IsEmpty { get; set => SetProperty(ref field, value); } = true;
    public string PeriodText { get; set => SetProperty(ref field, value); } = "";
    public string EmptyText { get; set => SetProperty(ref field, value); } = "";

    public void Update(AgentsUiState state, AgentPeriod period)
    {
        var usage = state.Snapshot.Periods.TryGetValue(period, out var u) ? u : null;
        var shares = (models ? usage?.Models : usage?.Projects) ?? [];
        var shown = shares.Where(s => s.Provider is not { } p || state.Enabled(p)).Take(MaxRows).ToList();
        var byCost = shown.Any(s => s.Totals.Cost > 0);
        double Weight(AgentShare s) => byCost ? s.Totals.Cost : s.Totals.Tokens.Total;
        var peak = Math.Max(shown.Select(Weight).DefaultIfEmpty(0).Max(), double.Epsilon);
        IsEmpty = shown.Count == 0;
        PeriodText = Periods.Long(period);
        EmptyText = state.Snapshot.Loaded ? $"No activity {Periods.Phrase(period)}" : "Reading logs…";
        Sync.Collection(Rows, shown, (row, s) => row.Name == s.Name, s => new ShareRowVm(s.Name), (row, s) =>
        {
            row.Display = models ? AgentDisplay.ModelName(s.Name) : s.Name;
            row.Value = byCost ? Format.Money(s.Totals.Cost) : Format.Tokens(s.Totals.Tokens.Total);
            row.BarWidth = Math.Max(3, BarRoom * Weight(s) / peak);
            row.Brush = s.Provider is { } p ? AgentDisplay.Brush(p) : Soft.Neutral;
            row.Tip = $"{row.Display} · {Format.Money(s.Totals.Cost)} · {Format.Tokens(s.Totals.Tokens.Total)} tokens · {AgentDisplay.Count(s.Totals.Requests)} requests";
        });
    }
}

/// <summary>The AI section: header status, period, refresh, the card tiles and the empty/loading states.</summary>
internal sealed class AgentsSectionVm : ObservableObject
{
    private readonly AgentsUiState state;
    private readonly Dictionary<string, LimitsCardVm> limitCards = [];
    private readonly SpendCardVm spend = new();
    private readonly LiveCardVm live = new();
    private readonly TrendCardVm trend = new();
    private readonly ShareCardVm models = new("models", "Top models", true);
    private readonly ShareCardVm projects = new("projects", "Top projects", false);
    private readonly DispatcherTimer tick;
    private string tileKey = "";
    private bool visible;
    private int ticks;

    public AgentsSectionVm(AgentsUiState state)
    {
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => state.Service.RefreshLimitsAsync(), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        tick = new DispatcherTimer(DispatcherPriority.Background, state.Context.Ui) { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => Tick();
        state.Changed += Invalidate;
        state.SettingChanged += _ => Invalidate();
        PeriodIndex = (int)state.Period;
    }

    public event Action? TilesChanged;

    public IReadOnlyList<CardVm> Tiles { get; private set; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public string Mode { get; private set => SetProperty(ref field, value); } = "loading";
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public string Detail { get; private set => SetProperty(ref field, value); } = "";
    public Brush StatusBrush { get; private set => SetProperty(ref field, value); } = AgentDisplay.Accent;
    public bool IsWorking { get; private set => SetProperty(ref field, value); }
    public string RefreshTip { get; private set => SetProperty(ref field, value); } = "Refresh limits";
    public IReadOnlyList<string> EmptyLines { get; private set => SetProperty(ref field, value); } = [];

    public int PeriodIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 0, 2)) && state.Period != (AgentPeriod)field)
                state.Period = (AgentPeriod)field;
        }
    }

    public void Show()
    {
        visible = true;
        state.Service.SetSectionVisible(true);
        Apply();
        tick.Start();
    }

    public void Hide()
    {
        visible = false;
        tick.Stop();
        state.Service.SetSectionVisible(false);
    }

    /// <summary>A hidden section does no work; Show applies the latest snapshot.</summary>
    private void Invalidate()
    {
        if (visible)
            Apply();
    }

    private void Tick()
    {
        var now = state.Now;
        live.Tick(now);
        // Countdowns and "x min ago" move slowly; a full pass every 15 s keeps them honest.
        if (++ticks % 15 == 0)
            Apply();
    }

    public void Apply()
    {
        var snapshot = state.Snapshot;
        var now = state.Now;
        var period = state.Period;
        PeriodIndex = (int)period;
        var limits = state.Limits.ToList();
        var accounts = snapshot.Accounts.Where(a => state.Enabled(a.Provider)).ToList();
        var anySeen = snapshot.Seen.Any(state.Enabled);
        Mode = !snapshot.Loaded && limits.Count == 0 && accounts.Count == 0 ? "loading"
            : snapshot.Loaded && limits.Count == 0 && accounts.Count == 0 && !anySeen ? "empty"
            : "cards";
        UpdateHeader(snapshot, limits, now);
        if (Mode == "empty")
            EmptyLines = Same(EmptyLines, EmptyGuidance(limits));

        var tiles = new List<CardVm>();
        foreach (var card in state.Cards)
        {
            switch (card)
            {
                case "limits":
                    foreach (var (id, provider, account) in LimitAccounts(accounts, limits))
                    {
                        if (!limitCards.TryGetValue(id, out var vm))
                            limitCards[id] = vm = new LimitsCardVm(state, id);
                        vm.Update(account, provider, limits.FirstOrDefault(l => l.AccountId == id), now);
                        tiles.Add(vm);
                    }
                    break;
                case "spend":
                    spend.Update(state, period);
                    tiles.Add(spend);
                    break;
                case "live":
                    live.Update(state, now);
                    tiles.Add(live);
                    break;
                case "trend":
                    trend.Update(state, period);
                    tiles.Add(trend);
                    break;
                case "models":
                    models.Update(state, period);
                    tiles.Add(models);
                    break;
                case "projects":
                    projects.Update(state, period);
                    tiles.Add(projects);
                    break;
            }
        }
        var key = string.Join("|", tiles.Select(t => t.Key));
        if (key != tileKey)
        {
            tileKey = key;
            Tiles = tiles;
            TilesChanged?.Invoke();
        }
    }

    /// <summary>Accounts in the service's order, then any limits whose account is not listed.</summary>
    private static IEnumerable<(string Id, AgentProvider Provider, AgentAccount? Account)> LimitAccounts(List<AgentAccount> accounts, List<AgentLimits> limits)
    {
        foreach (var account in accounts.Where(a => a.Provider is AgentProvider.Claude or AgentProvider.Codex || limits.Any(l => l.AccountId == a.Id)))
            yield return (account.Id, account.Provider, account);
        foreach (var orphan in limits.Where(l => accounts.All(a => a.Id != l.AccountId)))
            yield return (orphan.AccountId, orphan.Provider, null);
    }

    private void UpdateHeader(AgentUsageSnapshot snapshot, List<AgentLimits> limits, DateTimeOffset now)
    {
        var working = state.Live.ToList();
        IsWorking = false;
        string Title(AgentLimits l) => AgentDisplay.AccountTitle(l.Provider, snapshot.Accounts.FirstOrDefault(a => a.Id == l.AccountId));
        if (Mode == "loading")
        {
            (Status, Detail, StatusBrush) = ("Reading your agent logs…", "The first pass runs in the background", AgentDisplay.Accent);
        }
        else if (Mode == "empty")
        {
            (Status, Detail, StatusBrush) = ("Nothing to show yet", "Waiting for a coding agent on this PC", Soft.Neutral);
        }
        else if (limits.FirstOrDefault(l => l.Blocked) is { } blocked)
        {
            var soonest = blocked.Windows.Where(w => w.ResetsAt > now).MinBy(w => w.ResetsAt);
            (Status, Detail, StatusBrush) = ($"{Title(blocked)} limit reached", soonest is null ? "Waiting for the next reset" : Format.Countdown(soonest.ResetsAt, now), AgentDisplay.Critical);
        }
        else if (AgentDisplay.Focus(limits, "mostUsed", now) is { } low && AgentDisplay.Tone(low.Window.RemainingPercent) != LimitTone.Normal)
        {
            var tone = AgentDisplay.Tone(low.Window.RemainingPercent);
            (Status, Detail, StatusBrush) = ($"{AgentDisplay.Label(low.Window)} limit {(tone == LimitTone.Critical ? "almost used up" : "running low")}",
                $"{Title(low.Limits)} · {Format.Percent(low.Window.RemainingPercent)} left · {Format.Countdown(low.Window.ResetsAt, now)}",
                AgentDisplay.ToneBrush(tone, low.Limits.Provider));
        }
        else if (working.Count > 0)
        {
            IsWorking = true;
            var names = working.Select(s => AgentDisplay.Name(s.Provider)).Distinct().ToList();
            (Status, Detail, StatusBrush) = (names.Count == 1 && working.Count == 1 ? $"{names[0]} is working" : $"{working.Count} agents working",
                string.Join(" · ", working.Select(s => string.IsNullOrWhiteSpace(s.Project) ? AgentDisplay.Name(s.Provider) : s.Project).Distinct()),
                AgentDisplay.Brush(working[0].Provider));
        }
        else if (limits.FirstOrDefault(l => l.IsStale) is { } stale)
        {
            (Status, Detail, StatusBrush) = ("Showing last known limits", stale.Error ?? $"{Title(stale)} could not be refreshed", AgentDisplay.Warning);
        }
        else if (AgentDisplay.Focus(limits, "mostUsed", now) is { } lowest)
        {
            (Status, Detail, StatusBrush) = ("Plenty of room", $"Lowest: {Title(lowest.Limits)} {AgentDisplay.Label(lowest.Window)} · {Format.Percent(lowest.Window.RemainingPercent)} left", AgentDisplay.Success);
        }
        else
        {
            var today = snapshot.Periods.TryGetValue(AgentPeriod.Today, out var u) ? u.Total : AgentTotals.Zero;
            (Status, Detail, StatusBrush) = ("Tracking usage", $"{Format.Money(today.Cost)} of API value today", AgentDisplay.Accent);
        }

        var next = limits.Select(l => l.NextAttempt).Where(t => t > now).Min();
        var observed = limits.Where(l => l.Windows.Count > 0).Select(l => (DateTimeOffset?)l.ObservedAt).Max();
        RefreshTip = string.Join(" · ", new[]
        {
            "Refresh limits",
            observed is { } o ? "updated " + AgentDisplay.Ago(o, now) : null,
            next is { } n ? "next check " + AgentDisplay.In(n, now) : null,
        }.Where(p => p is not null));
    }

    private static List<string> EmptyGuidance(List<AgentLimits> limits)
    {
        var lines = limits.Select(l => l.Error).OfType<string>().Distinct().ToList();
        if (lines.Count == 0)
        {
            lines.Add("Claude — sign in with `claude auth login`");
            lines.Add("Codex — install Codex and run `codex login`");
        }
        return lines;
    }

    private static IReadOnlyList<T> Same<T>(IReadOnlyList<T> current, IReadOnlyList<T> next) => current.SequenceEqual(next) ? current : next;
}

internal static class Periods
{
    public static string Long(AgentPeriod period) => period switch
    {
        AgentPeriod.Today => "Today",
        AgentPeriod.Week => "Last 7 days",
        _ => "Last 30 days",
    };

    public static string Phrase(AgentPeriod period) => period switch
    {
        AgentPeriod.Today => "today",
        AgentPeriod.Week => "in the last 7 days",
        _ => "in the last 30 days",
    };
}

/// <summary>Translucent agent tints for badges and chips, and the neutral greys used from code.</summary>
internal static class Soft
{
    private static readonly Brush[] Tints = [.. AgentDisplay.Providers.Select(p => Alpha(AgentDisplay.Brush(p), 0x2E))];
    public static readonly Brush Tertiary = AgentDisplay.Frozen(Color.FromRgb(0x68, 0x70, 0x80));
    public static readonly Brush Neutral = AgentDisplay.Frozen(Color.FromRgb(0xA6, 0xAD, 0xBB));

    public static Brush For(AgentProvider provider) => Tints[(int)provider];

    private static Brush Alpha(Brush brush, byte alpha)
    {
        var color = ((SolidColorBrush)brush).Color;
        return AgentDisplay.Frozen(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}

internal static class Sync
{
    /// <summary>Makes <paramref name="target"/> match <paramref name="source"/> with moves/inserts/removes, reusing matching items.</summary>
    public static void Collection<TVm, TSrc>(ObservableCollection<TVm> target, IReadOnlyList<TSrc> source,
        Func<TVm, TSrc, bool> matches, Func<TSrc, TVm> create, Action<TVm, TSrc> update)
    {
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var found = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (matches(target[j], item))
                {
                    found = j;
                    break;
                }
            }
            if (found < 0)
                target.Insert(i, create(item));
            else if (found != i)
                target.Move(found, i);
            update(target[i], item);
        }
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }
}
