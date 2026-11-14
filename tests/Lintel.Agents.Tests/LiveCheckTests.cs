// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;
using Xunit.Abstractions;

namespace Lintel.Agents.Tests;

/// <summary>
/// Opt-in, read-only check against the real accounts on this PC: set LINTEL_LIVE=1. It prints only normalised
/// numbers (percentages, windows, counts) and asserts the privacy promises (no tokens or e-mail addresses written,
/// credentials untouched, no child process left behind). Without the variable it does nothing.
/// </summary>
public sealed class LiveCheckTests(ITestOutputHelper output)
{
    private sealed class AllConsent : IConsentService
    {
        public bool IsGranted(string id) => true;
        public void Grant(string id) { }
        public void Revoke(string id) { }
        public event Action<string>? Changed { add { } remove { } }
        public IReadOnlyList<string> Unused(IReadOnlyList<ModuleManifest> installed) => [];
    }

    private sealed class QuietLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message, Exception? error = null) => output_?.WriteLine("[warn] " + message + (error is null ? "" : " (" + error.GetType().Name + ")"));
        public void Error(string message, Exception? error = null) => output_?.WriteLine("[error] " + message);
        public ITestOutputHelper? output_;
    }

    private static HashSet<int> CodexPids() => Process.GetProcessesByName("codex").Select(p => p.Id).ToHashSet();

    [Fact]
    public async Task LiveReadOnlyCheck()
    {
        if (Environment.GetEnvironmentVariable("LINTEL_LIVE") != "1") return;

        var root = Path.Combine(Path.GetTempPath(), "lintel-agents-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var credentials = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        var hash = File.Exists(credentials) ? SHA256.HashData(File.ReadAllBytes(credentials)) : null;
        var stamp = File.Exists(credentials) ? File.GetLastWriteTimeUtc(credentials) : default;
        var before = CodexPids();

        var log = new QuietLog { output_ = output };
        using var settings = new JsonSettingsStore(Path.Combine(root, "settings.json"));
        using var http = AppHttp.Create(new AppInfo("1.0.0", "OWNER", "Lintel", "x.exe"));
        var context = new ModuleContext(new AppInfo("1.0.0", "OWNER", "Lintel", "x.exe"), false, settings, new AllConsent(),
            new NullNotchShell(), null!, log, Dispatcher.CurrentDispatcher, http, TimeProvider.System, root);

        var raised = new List<string>();
        var watch = Stopwatch.StartNew();
        AgentUsageSnapshot snapshot;
        using (var service = new AgentUsageService(context, a => a()))
        {
            service.EventRaised += (_, e) => raised.Add(e.GetType().Name);
            service.Start();
            service.SetSectionVisible(true);
            output.WriteLine($"start -> {watch.Elapsed.TotalMilliseconds:0} ms");

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                var s = service.Current;
                var wanted = s.Accounts.Count(a => a.Provider is AgentProvider.Claude or AgentProvider.Codex);
                if (s.Loaded && s.Limits.Count >= wanted && wanted > 0 && s.Limits.All(l => l.Windows.Count > 0 || l.Error is not null)) break;
                await Task.Delay(500);
            }
            output.WriteLine($"first snapshot with limits + loaded -> {watch.Elapsed.TotalSeconds:0.0} s");
            snapshot = service.Current;

            // A manual refresh right after a success must be refused by the 30 s rule (no second call).
            await service.RefreshLimitsAsync();
        }

        output.WriteLine($"loaded={snapshot.Loaded} accounts=[{string.Join(", ", snapshot.Accounts.Select(a => a.Id))}] seen=[{string.Join(", ", snapshot.Seen)}] prices={snapshot.PricesUpdated}");
        foreach (var l in snapshot.Limits)
        {
            output.WriteLine($"LIMITS {l.AccountId} source={l.Source} stale={l.IsStale} blocked={l.Blocked} plan={l.Plan?.Name}/{l.Plan?.MonthlyPrice} credits='{l.Credits}' error='{l.Error}' next={(l.NextAttempt is { } n ? (n - DateTimeOffset.UtcNow).TotalMinutes.ToString("0.0") + " min" : "none")}");
            foreach (var w in l.Windows)
                output.WriteLine($"   {w.Id,-28} {w.Label,-16} kind={w.Kind,-7} min={w.Minutes} used={w.UsedPercent:0.#}% remaining={w.RemainingPercent:0.#}% resets_in={(w.ResetsAt is { } r ? (r - DateTimeOffset.UtcNow).TotalHours.ToString("0.0") + " h" : "not reported")}");
        }
        foreach (var (period, usage) in snapshot.Periods)
        {
            output.WriteLine($"{period,-6} tokens={usage.Total.Tokens.Total} requests={usage.Total.Requests} apiValue=${usage.Total.Cost:0.00} savings=${usage.Total.Savings:0.00} unpriced={usage.Total.Unpriced}" +
                $" cacheHit={(usage.Total.Tokens.CacheHitRate is { } c ? (c * 100).ToString("0") + "%" : "-")} models={usage.Models.Count} projects={usage.Projects.Count}");
            foreach (var (provider, t) in usage.ByProvider)
                output.WriteLine($"   {provider,-9} tokens={t.Tokens.Total} requests={t.Requests} apiValue=${t.Cost:0.00}");
        }
        output.WriteLine($"top models (month): {string.Join(", ", snapshot.Periods[AgentPeriod.Month].Models.Take(5).Select(m => $"{m.Name} ${m.Totals.Cost:0.00}"))}");
        output.WriteLine($"days={snapshot.Days.Count} nonEmptyDays={snapshot.Days.Count(d => d.ByProvider.Values.Any(t => t.Requests > 0))} hours={snapshot.Hours.Count} live={snapshot.Live.Count} events={raised.Count}");

        // Privacy promises.
        var written = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        output.WriteLine($"files written under the data root: {written.Count} ({string.Join(", ", written.Select(Path.GetFileName))})");
        var email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}");
        foreach (var file in written.Where(f => !f.EndsWith("agent-prices.json")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(email, text);
            Assert.DoesNotContain("sk-ant", text);
            Assert.DoesNotContain("eyJ", text);
            Assert.DoesNotContain("Bearer", text);
        }
        if (hash is not null)
        {
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(credentials)));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(credentials));
            output.WriteLine("credentials file: sha256 and mtime unchanged");
        }
        var after = CodexPids();
        after.ExceptWith(before);
        output.WriteLine($"codex processes started by us and still running: {after.Count}");
        Assert.Empty(after);

        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    /// <summary>Opt-in (LINTEL_LIVE=1): reads only the real Codex session logs (plain and .zst), no network, no processes; prints counts only.</summary>
    [Fact]
    public void LiveCodexLogsCheck()
    {
        if (Environment.GetEnvironmentVariable("LINTEL_LIVE") != "1") return;
        var store = new Usage.UsageStore(new Pricing.AgentPricing(Pricing.PriceSource.Embedded()));
        var warnings = new List<string>();
        var roots = Logs.LogRoots.FromEnvironment([]);
        var indexer = new Usage.LogIndexer(() => roots, store, new Usage.TurnTracker(), TimeProvider.System, p => p == AgentProvider.Codex, null, warnings.Add);
        indexer.Pass(true);
        var files = roots.Discover(DateTime.UtcNow - Usage.UsageStore.Retention, p => p == AgentProvider.Codex);
        output.WriteLine($"codex files: {files.Count(f => f.Path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))} zst, {files.Count(f => !f.Path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))} plain, tracked={indexer.TrackedFiles}, warnings={warnings.Count}");
        var view = Usage.UsageSummary.Build(store.Buckets, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        foreach (var (period, usage) in view.Periods)
        {
            if (usage.ByProvider.GetValueOrDefault(AgentProvider.Codex) is not { } t) { output.WriteLine($"{period,-6} codex none"); continue; }
            output.WriteLine($"{period,-6} codex requests={t.Requests} tokens={t.Tokens.Total} apiValue=${t.Cost:0.00} unpriced={t.Unpriced}");
        }
        output.WriteLine($"codex models (month): {string.Join(", ", view.Periods[AgentPeriod.Month].Models.Where(m => m.Provider == AgentProvider.Codex).Take(6).Select(m => m.Name))}; projects={view.Periods[AgentPeriod.Month].Projects.Count}");
    }
}
