// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http;
using System.Windows.Threading;
using Lintel.Agents.Accounts;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Agents.Tests;

public sealed class CodexAccountsTests : IDisposable
{
    private readonly string dir = T.TempDir();
    private readonly JsonSettingsStore settings;

    public CodexAccountsTests() => settings = new JsonSettingsStore(Path.Combine(dir, "settings.json"));

    public void Dispose() => Directory.Delete(dir, true);

    [Theory]
    [InlineData("Work", "codex:work")]
    [InlineData("  My Work  Account! ", "codex:my-work-account")]
    [InlineData("Ünïcode", "codex:n-code")]
    [InlineData("!!!", "codex:home")]
    [InlineData("default", "codex:default-2")]
    public void SlugsAreLowercaseAsciiWithDashes(string name, string expected) =>
        Assert.Equal(expected, CodexAccounts.Slug(name, []));

    [Fact]
    public void SlugsAreUniqueAmongExistingIds()
    {
        Assert.Equal("codex:work-2", CodexAccounts.Slug("Work", ["codex:work"]));
        Assert.Equal("codex:work-3", CodexAccounts.Slug("Work", ["codex:work", "CODEX:WORK-2"]));
    }

    [Fact]
    public void AddPersistsTheHomeInTheDocumentedShapeAndReturnsTheAccount()
    {
        var account = CodexAccounts.Add(settings, @"D:\codex-work", "Work");
        Assert.Equal(new AgentAccount("codex:work", AgentProvider.Codex, "Work", @"D:\codex-work", false), account);
        var stored = settings.Get(AgentSettings.CodexHomes, new List<CodexHome>());
        Assert.Equal([new CodexHome("codex:work", "Work", @"D:\codex-work")], stored);
        settings.Flush();
        Assert.Contains("\"agents.codex.homes\"", File.ReadAllText(Path.Combine(dir, "settings.json")));
        Assert.Contains("\"path\"", File.ReadAllText(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void AddingTheSameFolderAgainRenamesAndTwoFoldersGetDistinctIds()
    {
        CodexAccounts.Add(settings, @"D:\a", "Work");
        var renamed = CodexAccounts.Add(settings, @"d:\A", "Client");
        Assert.Equal("Client", renamed.DisplayName);
        Assert.Single(CodexAccounts.Load(settings));

        var second = CodexAccounts.Add(settings, @"D:\b", "Client");
        Assert.NotEqual(renamed.Id, second.Id);
        Assert.Equal(2, CodexAccounts.Load(settings).Count);
    }

    [Fact]
    public void BlankNameFallsBackToTheFolderName() =>
        Assert.Equal("projects", CodexAccounts.Add(settings, @"D:\data\projects\", "  ").DisplayName);

    [Fact]
    public void RemoveOnlyTouchesKnownExtraHomes()
    {
        CodexAccounts.Add(settings, @"D:\a", "A");
        Assert.False(CodexAccounts.Remove(settings, "codex:default"));
        Assert.False(CodexAccounts.Remove(settings, "codex:nope"));
        Assert.True(CodexAccounts.Remove(settings, "CODEX:A"));
        Assert.Empty(CodexAccounts.Load(settings));
    }

    [Fact]
    public void BrokenEntriesAreSkippedOnLoad()
    {
        settings.Set(AgentSettings.CodexHomes, new List<CodexHome> { new("codex:ok", "Ok", @"D:\ok"), new("", "NoId", @"D:\x"), new("codex:nopath", "NoPath", " ") });
        Assert.Equal(["codex:ok"], CodexAccounts.Load(settings).Select(h => h.Id));
    }

    [Fact]
    public void ProviderKeysFollowThePlanNames()
    {
        Assert.Equal("agents.providers.claude", AgentSettings.Provider(AgentProvider.Claude));
        Assert.Equal("agents.providers.opencode", AgentSettings.Provider(AgentProvider.OpenCode));
    }
}

/// <summary>Service-level checks that never touch real data: every provider and source is switched off or the context is demo.</summary>
public sealed class AgentUsageServiceTests : IDisposable
{
    private readonly string dir = T.TempDir();
    private readonly JsonSettingsStore settings;
    private readonly TestConsent consent = new();

    public AgentUsageServiceTests() => settings = new JsonSettingsStore(Path.Combine(dir, "settings.json"));

    public void Dispose() => Directory.Delete(dir, true);

    private sealed class TestConsent : IConsentService
    {
        public HashSet<string> Granted { get; } = [];
        public bool IsGranted(string id) => Granted.Contains(id);
        public void Grant(string id) => Granted.Add(id);
        public void Revoke(string id) => Granted.Remove(id);
        public event Action<string>? Changed { add { } remove { } }
        public IReadOnlyList<string> Unused(IReadOnlyList<ModuleManifest> installed) => [];
    }

    private sealed class NoLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message, Exception? error = null) { }
        public void Error(string message, Exception? error = null) { }
    }

    private ModuleContext Context(bool demo) => new(
        new AppInfo("1.0.0", "OWNER", "Lintel", "x.exe"), demo, settings, consent, new NullNotchShell(), null!, new NoLog(),
        Dispatcher.CurrentDispatcher, new HttpClient(), TimeProvider.System, dir);

    [Fact]
    public void FactoryReturnsTheDemoServiceForDemoContexts()
    {
        using var service = AgentsData.CreateService(Context(demo: true));
        Assert.IsType<DemoAgentUsageService>(service);
        var snapshot = service.Current;
        Assert.False(snapshot.Loaded);   // until Start
    }

    [Fact]
    public void DemoSnapshotIsRichAndDeterministic()
    {
        using var service = new DemoAgentUsageService(Context(demo: true));
        var raised = new List<AgentUsageSnapshot>();
        service.SnapshotChanged += (_, s) => raised.Add(s);
        service.Start();

        var s = service.Current;
        Assert.True(s.Loaded);
        Assert.Equal(["claude:default", "codex:default"], s.Accounts.Select(a => a.Id));
        Assert.Equal(2, s.Limits.Count);
        Assert.All(s.Limits, l => Assert.NotEmpty(l.Windows));
        Assert.Equal(30, s.Days.Count);
        Assert.Equal(24, s.Hours.Count);
        Assert.Equal(3, s.Periods.Count);
        Assert.True(s.Periods[AgentPeriod.Month].Total.Cost > s.Periods[AgentPeriod.Today].Total.Cost);
        Assert.NotEmpty(s.Periods[AgentPeriod.Month].Models);
        Assert.NotEmpty(s.Periods[AgentPeriod.Month].Projects);
        var live = Assert.Single(s.Live);
        Assert.True(live.Tokens.Total > 0);
        Assert.Equal([AgentProvider.Claude, AgentProvider.Codex], s.Seen);
        Assert.NotEmpty(raised);
        service.Stop();
        service.Stop();   // idempotent
    }

    [Fact]
    public async Task WithoutLogConsentAndWithProvidersOffTheServiceStartsQuietlyAndStopsCleanly()
    {
        foreach (var p in Enum.GetValues<AgentProvider>()) settings.Set(AgentSettings.Provider(p), false);
        var posted = new List<Action>();
        using var service = new AgentUsageService(Context(demo: false), posted.Add);
        service.Start();
        Assert.Empty(service.Current.Accounts);
        service.SetSectionVisible(true);
        await service.RefreshLimitsAsync();
        service.Stop();
        service.Stop();
        service.Dispose();
        Assert.Empty(service.Current.Limits);
        Assert.True(File.Exists(Path.Combine(dir, "data", "agents", "usage-cache.json")));   // flushed on Stop
    }

    [Fact]
    public void RemoveAndAddCodexHomeUpdateAccountsWithoutTouchingTheDefault()
    {
        foreach (var p in Enum.GetValues<AgentProvider>()) settings.Set(AgentSettings.Provider(p), p == AgentProvider.Codex);
        using var service = new AgentUsageService(Context(demo: false), a => a());
        service.Start();
        var home = Path.Combine(dir, "extra-home");
        var account = service.AddCodexHome(home, "Extra");
        Assert.Equal("codex:extra", account.Id);
        Assert.Equal(home, account.HomePath);
        Assert.Contains(service.Current.Accounts, a => a.Id == "codex:extra");   // visible immediately
        Assert.Contains(settings.Get(AgentSettings.CodexHomes, new List<CodexHome>()), h => h.Id == "codex:extra");
        service.RemoveAccount("codex:extra");
        Assert.DoesNotContain(service.Current.Accounts, a => a.Id == "codex:extra");
        Assert.Empty(settings.Get(AgentSettings.CodexHomes, new List<CodexHome>()));
        service.RemoveAccount("codex:default");   // the default account cannot be removed
        service.Stop();
    }
}
