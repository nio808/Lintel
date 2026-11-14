// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Lintel.Agents.Accounts;
using Lintel.Agents.Limits;
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Agents.Usage;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;

namespace Lintel.Agents;

/// <summary>
/// The real data service. One background worker (BelowNormal) owns log tailing and the store; limit reads run as tasks;
/// snapshots are built at most once per second and posted to the UI thread. Nothing here logs or persists tokens,
/// e-mail addresses, account ids, response bodies or log-line contents.
/// </summary>
internal sealed class AgentUsageService : IAgentUsageService
{
    private static readonly TimeSpan DiscoverEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TailEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PriceEvery = TimeSpan.FromHours(24);

    private readonly IModuleContext ctx;
    private readonly Action<Action> post;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly object stateLock = new();
    private readonly ManualResetEventSlim wake = new(false);
    private readonly SemaphoreSlim limitsNudge = new(0);
    private readonly ConcurrentDictionary<string, AgentLimits> fallback = new();
    private readonly AlertTracker alerts = new();
    private readonly object alertLock = new();

    private PriceSource prices = null!;
    private UsageStore store = null!;
    private TurnTracker turns = null!;
    private volatile LogIndexer? indexer;   // created on the worker: loading the cache must not delay the UI thread
    private LimitsCoordinator limits = null!;
    private CodexAppServerClient codex = null!;
    private ClaudeOAuthReader claude = null!;
    private CancellationTokenSource? cts;
    private Thread? worker;
    private Task? limitsLoop;
    private Timer? publisher;
    private volatile AgentUsageSnapshot current = AgentUsageSnapshot.Empty;
    private IReadOnlyList<AgentAccount> accounts = [];
    private string liveSignature = "";
    private volatile bool dirty, visible, loaded, discoverNow, started, stopped;
    private DateTime appHistoryStamp;

    public AgentUsageService(IModuleContext context, Action<Action>? poster = null)
    {
        ctx = context;
        time = context.Time;
        post = poster ?? (a => context.Ui.BeginInvoke(a));
    }

    public AgentUsageSnapshot Current => current;

    public event EventHandler<AgentUsageSnapshot>? SnapshotChanged;
    public event EventHandler<AgentUsageEvent>? EventRaised;

    // ---- lifecycle ---------------------------------------------------------------------------------------------

    public void Start()
    {
        lock (stateLock)
        {
            if (started || stopped) return;
            started = true;
        }
        var dir = ctx.GetDataDirectory("agents");
        prices = new PriceSource(Path.Combine(dir, "agent-prices.json"));
        store = new UsageStore(new AgentPricing(prices.Current));
        turns = new TurnTracker();
        codex = new CodexAppServerClient(() => prices.Current, time);
        claude = new ClaudeOAuthReader(ctx.Http, time, () => prices.Current);
        limits = new LimitsCoordinator(time, ReadAccountAsync, CanReadAccount, a => fallback.TryGetValue(a.Id, out var l) ? l : null);
        limits.Changed += OnLimitsChanged;
        ctx.Settings.Changed += OnSettingChanged;
        ctx.Consent.Changed += OnConsentChanged;

        cts = new CancellationTokenSource();
        var ct = cts.Token;
        worker = new Thread(() => Work(ct, dir)) { IsBackground = true, Name = "Lintel.Agents.Index", Priority = ThreadPriority.BelowNormal };
        worker.Start();
        limitsLoop = Task.Run(() => LimitsLoop(ct));
        publisher = new Timer(_ => PublishTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        lock (stateLock)
        {
            if (!started || stopped) return;
            stopped = true;
        }
        ctx.Settings.Changed -= OnSettingChanged;
        ctx.Consent.Changed -= OnConsentChanged;
        publisher?.Dispose();
        cts?.Cancel();
        wake.Set();
        limitsNudge.Release();
        codex.KillAll();
        try { worker?.Join(3000); } catch (ThreadStateException) { }
        try { limitsLoop?.Wait(2000); } catch (AggregateException) { }
        codex.Dispose();
        try { indexer?.Save(); } catch (Exception e) { ctx.Log.Warn("Could not save the usage cache.", e); }
    }

    public void Dispose() => Stop();

    // ---- public operations -------------------------------------------------------------------------------------

    public void SetSectionVisible(bool value)
    {
        visible = value;
        if (value && started && !stopped) limitsNudge.Release();
    }

    public async Task RefreshLimitsAsync(string? accountId = null)
    {
        if (!started || stopped || cts is null) return;
        ReadAppHistory(force: true);
        try { await limits.RefreshAsync(accountId, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        dirty = true;
    }

    public AgentAccount AddCodexHome(string folder, string displayName)
    {
        var account = CodexAccounts.Add(ctx.Settings, folder, displayName);
        AccountsChanged();
        PublishNow();
        return account;
    }

    public void RemoveAccount(string accountId)
    {
        if (!CodexAccounts.Remove(ctx.Settings, accountId)) return;
        lock (alertLock) alerts.Forget(accountId);
        fallback.TryRemove(accountId, out _);
        lock (gate) indexer?.CodexLimits.Remove(accountId);
        AccountsChanged();
        PublishNow();
    }

    // ---- settings & consent ------------------------------------------------------------------------------------

    private bool ProviderOn(AgentProvider p) => ctx.Settings.Get(AgentSettings.Provider(p), true);

    private bool LogsOn(AgentProvider p) => ProviderOn(p) && ctx.Consent.IsGranted(PermissionIds.AgentLogs);

    private bool CanReadAccount(AgentAccount a) => a.Provider switch
    {
        AgentProvider.Claude => ProviderOn(AgentProvider.Claude) && ctx.Consent.IsGranted(PermissionIds.ClaudeCredentials),
        AgentProvider.Codex => ProviderOn(AgentProvider.Codex) && ctx.Consent.IsGranted(PermissionIds.CodexCli),
        _ => false,
    };

    private Task<AgentLimits> ReadAccountAsync(AgentAccount a, CancellationToken ct) =>
        a.Provider == AgentProvider.Claude ? claude.ReadAsync(ct) : codex.ReadAsync(a, ct);

    private void OnSettingChanged(string key)
    {
        if (!key.StartsWith("agents.", StringComparison.Ordinal)) return;
        AccountsChanged();
    }

    private void OnConsentChanged(string id)
    {
        if (id is PermissionIds.AgentLogs or PermissionIds.ClaudeCredentials or PermissionIds.ClaudeAppHistory or PermissionIds.CodexCli or PermissionIds.NetworkPrices)
            AccountsChanged();
    }

    private void AccountsChanged()
    {
        if (!started || stopped) return;
        RebuildAccounts();
        discoverNow = true;
        wake.Set();
        limitsNudge.Release();
        dirty = true;
    }

    /// <summary>Claude: when its folder or sign-in exists. Codex: when its home or binary exists, plus every added home.</summary>
    private void RebuildAccounts()
    {
        var list = new List<AgentAccount>();
        if (ProviderOn(AgentProvider.Claude)
            && (ClaudeOAuthReader.CredentialsExist() || Directory.Exists(LogRoots.FromEnvironment([]).ClaudeConfigDir)))
            list.Add(new AgentAccount("claude:default", AgentProvider.Claude, "Claude", null, true));
        if (ProviderOn(AgentProvider.Codex))
        {
            if (Directory.Exists(LogRoots.DefaultCodexHome()) || CodexLocator.Find() is not null)
                list.Add(new AgentAccount(CodexAccounts.DefaultId, AgentProvider.Codex, "Codex", null, true));
            list.AddRange(CodexAccounts.Load(ctx.Settings).Select(CodexAccounts.ToAccount));
        }
        accounts = list;
        limits.SetAccounts(list);
        if (!ctx.Consent.IsGranted(PermissionIds.ClaudeAppHistory)) fallback.TryRemove("claude:default", out _);
        limits.Recompose();
    }

    // ---- worker: logs --------------------------------------------------------------------------------------------

    private void Work(CancellationToken ct, string dataDirectory)
    {
        try
        {
            // Heavy start-up (usage cache, account discovery) happens here, never on the UI thread.
            RebuildAccounts();
            limitsNudge.Release();
            lock (gate)
                indexer = new LogIndexer(() => LogRoots.FromEnvironment(CodexAccounts.Load(ctx.Settings).Select(h => (h.Id, h.Path))),
                    store, turns, time, LogsOn, Path.Combine(dataDirectory, "usage-cache.json"), m => ctx.Log.Warn(m), gate)
                {
                    OnEvent = OnIndexerEvent,
                };
        }
        catch (Exception e)
        {
            ctx.Log.Warn("Usage indexer could not start.", e);
            return;
        }
        var index = indexer!;
        var lastDiscover = DateTimeOffset.MinValue;
        var lastSave = time.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = time.GetUtcNow();
                var discover = discoverNow || now - lastDiscover >= DiscoverEvery;
                if (discover) { discoverNow = false; lastDiscover = now; }
                var changed = ctx.Consent.IsGranted(PermissionIds.AgentLogs) && index.Pass(discover, ct);
                if (discover) { ReadAppHistory(force: false); }
                if (CopyLogLimits() || changed) dirty = true;
                if (!loaded && !ct.IsCancellationRequested)
                {
                    lock (gate) turns.ReportsTransitions = true;
                    loaded = true;
                    dirty = true;
                }
                if (now - lastSave >= SaveEvery) { lastSave = now; index.Save(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                ctx.Log.Warn("Usage indexing pass failed.", e);   // exception text only; never log lines or paths of content
            }
            try
            {
                wake.Wait(TailEvery, ct);
                wake.Reset();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Codex readings copied into session logs become the fallback source of each account.</summary>
    private bool CopyLogLimits()
    {
        bool any = false;
        if (indexer is not { } index) return false;
        lock (gate)
            foreach (var (id, (windows, observed)) in index.CodexLimits)
            {
                var plan = index.CodexPlans.TryGetValue(id, out var p) ? AgentPlans.Codex(p.Name, prices.Current) : null;
                var reading = new AgentLimits(id, AgentProvider.Codex, windows, observed, LimitSource.SessionLog, plan, null,
                    windows.Any(w => w.UsedPercent >= 100), false, null, null);
                if (!fallback.TryGetValue(id, out var old) || old.ObservedAt != observed || old.Plan != plan)
                {
                    fallback[id] = reading;
                    any = true;
                }
            }
        if (any) limits.Recompose();
        return any;
    }

    private void ReadAppHistory(bool force)
    {
        if (!ctx.Consent.IsGranted(PermissionIds.ClaudeAppHistory) || !ProviderOn(AgentProvider.Claude)) return;
        var path = ClaudeAppHistory.HistoryPath();
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (!force && stamp == appHistoryStamp) return;
            appHistoryStamp = stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (ClaudeAppHistory.Read(path, time.GetUtcNow()) is { } reading) fallback["claude:default"] = reading;
        else fallback.TryRemove("claude:default", out _);
        limits.Recompose();
    }

    private void OnIndexerEvent(AgentUsageEvent e)
    {
        if (e is AgentUsageEvent.Finished f)
        {
            if (!ctx.Settings.Get(AgentSettings.FinishEnabled, true)) return;
            if (f.Duration.TotalSeconds < ctx.Settings.Get(AgentSettings.FinishMinSeconds, 60)) return;
        }
        Raise(e);
    }

    // ---- limits ----------------------------------------------------------------------------------------------------

    private async Task LimitsLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshPricesIfDueAsync(ct).ConfigureAwait(false);
                await limits.TickAsync(visible, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { ctx.Log.Warn("Limits check failed.", e); }
            try { await limitsNudge.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnLimitsChanged(AgentLimits reading)
    {
        dirty = true;
        if (reading.IsStale || reading.Windows.Count == 0 || !ctx.Settings.Get(AgentSettings.AlertsEnabled, true)) return;
        var thresholds = ctx.Settings.Get(AgentSettings.AlertsThresholds, new[] { 20d, 5d });
        List<AgentUsageEvent> events;
        lock (alertLock)
            events = alerts.Evaluate(reading, thresholds, warn: true, onReset: ctx.Settings.Get(AgentSettings.AlertsOnReset, true));
        foreach (var e in events) Raise(e);
    }

    private async Task RefreshPricesIfDueAsync(CancellationToken ct)
    {
        if (!ctx.Consent.IsGranted(PermissionIds.NetworkPrices) || ctx.App.RepositoryOwner == "OWNER") return;
        var checkedAt = DateTimeOffset.TryParse(ctx.Settings.Get(AgentSettings.PricesChecked, ""), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
        var now = time.GetUtcNow();
        if (now - checkedAt < PriceEvery) return;
        ctx.Settings.Set(AgentSettings.PricesChecked, now.ToString("o", CultureInfo.InvariantCulture));
        try
        {
            if (await prices.RefreshAsync(ctx.Http, PriceSource.Url(ctx.App.RepositoryOwner, ctx.App.RepositoryName), ct).ConfigureAwait(false))
                lock (gate) store.Pricing = new AgentPricing(prices.Current);
        }
        catch (Exception e) when (e is not OperationCanceledException) { ctx.Log.Warn("Price list refresh failed.", e); }
    }

    // ---- publishing ----------------------------------------------------------------------------------------------

    private void Raise(AgentUsageEvent e) => post(() => EventRaised?.Invoke(this, e));

    private void PublishTick()
    {
        if (stopped || indexer is null) return;
        try
        {
            var now = time.GetUtcNow();
            string signature;
            lock (gate) signature = string.Join(',', turns.Live(now).Select(s => s.Id));
            if (signature != liveSignature) { liveSignature = signature; dirty = true; }
            else if (signature.Length > 0) dirty = true;   // live sessions' tokens/cost tick
            if (!dirty) return;
            dirty = false;
            var snapshot = Build(now);
            current = snapshot;
            post(() => SnapshotChanged?.Invoke(this, snapshot));
        }
        catch (Exception e) { ctx.Log.Warn("Publishing the usage snapshot failed.", e); }
    }

    /// <summary>Builds and publishes immediately (Current is updated before this returns).</summary>
    private void PublishNow()
    {
        if (!started || stopped) return;
        try
        {
            dirty = false;
            var snapshot = Build(time.GetUtcNow());
            current = snapshot;
            post(() => SnapshotChanged?.Invoke(this, snapshot));
        }
        catch (Exception e) { ctx.Log.Warn("Publishing the usage snapshot failed.", e); }
    }

    private AgentUsageSnapshot Build(DateTimeOffset now)
    {
        var reading = limits.Current;   // before the gate: LimitsCoordinator never holds its lock while taking ours
        var on = Enum.GetValues<AgentProvider>().Where(ProviderOn).ToHashSet();   // one settings read per provider, not per bucket
        UsageView view;
        List<AgentLiveSession> live;
        List<AgentProvider> present;
        lock (gate)
        {
            present = (indexer?.Present ?? []).Where(on.Contains).ToList();
            view = UsageSummary.Build(store.Buckets, now, TimeZoneInfo.Local, on.Contains);
            live = turns.Live(now).Where(s => on.Contains(s.Provider)).ToList();
        }
        var seen = view.Seen.Union(present).Distinct().OrderBy(p => p).ToList();
        return new AgentUsageSnapshot
        {
            Loaded = loaded,
            Now = now,
            Accounts = accounts,
            Limits = reading,
            Periods = view.Periods,
            Days = view.Days,
            Hours = view.Hours,
            Live = live,
            Seen = seen,
            PricesUpdated = prices.Current.Updated == DateOnly.MinValue ? null : prices.Current.Updated,
        };
    }
}
