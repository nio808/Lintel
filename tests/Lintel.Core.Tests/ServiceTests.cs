// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Windows.Input;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;
using Lintel.Core.Text;

namespace Lintel.Core.Tests;

public sealed class ConsentTests : IDisposable
{
    private readonly Harness h = new();

    public void Dispose() => h.Dispose();

    [Fact]
    public void GrantAndRevokeRaiseChangedOnlyOnChange()
    {
        var events = new List<string>();
        h.Consent.Changed += events.Add;
        h.Consent.Grant(PermissionIds.ClipboardRead);
        h.Consent.Grant(PermissionIds.ClipboardRead);
        Assert.True(h.Consent.IsGranted(PermissionIds.ClipboardRead));
        Assert.True(h.Settings.Get("consent.clipboard.read", false));
        h.Consent.Revoke(PermissionIds.ClipboardRead);
        h.Consent.Revoke(PermissionIds.ClipboardRead);
        Assert.False(h.Consent.IsGranted(PermissionIds.ClipboardRead));
        Assert.Equal([PermissionIds.ClipboardRead, PermissionIds.ClipboardRead], events);
    }

    [Fact]
    public void UnusedListsGrantedPermissionsNoInstalledModuleNeeds()
    {
        foreach (var id in new[] { PermissionIds.ClipboardRead, PermissionIds.InputPaste, PermissionIds.KeepAwake, PermissionIds.NetworkUpdates, PermissionIds.Startup })
            h.Consent.Grant(id);
        var clipboard = new ModuleManifest
        {
            Id = "clipboard", Name = "Clipboard", Summary = "", Glyph = "", Category = ModuleCategory.Productivity,
            RequiredPermissions = [PermissionIds.ClipboardRead], OptionalPermissions = [PermissionIds.InputPaste],
        };
        Assert.Equal([PermissionIds.KeepAwake], h.Consent.Unused([clipboard]));   // app-level ones are never "unused"
    }

    [Fact]
    public void CatalogCoversEveryPermissionId()
    {
        var ids = typeof(PermissionIds).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();
        Assert.Equal(ids.Count, PermissionCatalog.All.Count);
        Assert.All(PermissionCatalog.All, p => Assert.Contains(p.Id, ids));
    }
}

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+N", ModifierKeys.Control | ModifierKeys.Alt, Key.N, "Ctrl+Alt+N")]
    [InlineData("shift+win+f5", ModifierKeys.Shift | ModifierKeys.Windows, Key.F5, "Shift+Win+F5")]
    [InlineData("Win+Ctrl+V", ModifierKeys.Control | ModifierKeys.Windows, Key.V, "Ctrl+Win+V")]
    [InlineData("Control + Shift + D1", ModifierKeys.Control | ModifierKeys.Shift, Key.D1, "Ctrl+Shift+D1")]
    [InlineData("F13", ModifierKeys.None, Key.F13, "F13")]
    public void ParsesAndFormats(string text, ModifierKeys modifiers, Key key, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var m, out var k));
        Assert.Equal(modifiers, m);
        Assert.Equal(key, k);
        Assert.Equal(canonical, HotkeyGesture.Format(m, k));
        Assert.Equal(canonical, HotkeyGesture.Normalize(text));
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+N")]
    [InlineData("Hyper+N")]
    [InlineData("Ctrl+LeftShift")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+42")]
    [InlineData("Ctrl+A,B")]
    public void RejectsInvalidGestures(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out _));
        Assert.Null(HotkeyGesture.Normalize(text));
    }

    [Fact]
    public void BlankNormalizesToUnbound() => Assert.Equal("", HotkeyGesture.Normalize("  "));
}

public sealed class AppArgsTests
{
    [Fact]
    public void ParsesEverySwitch()
    {
        var a = AppArgs.Parse(["--autostart", "--SETTINGS", "--quit", "--demo", "--layered", "--version",
            "--screenshot", @"C:\shots", "--read-usage", "out.json", "--unknown"]);
        Assert.Equal(new AppArgs(true, true, true, true, true, true, @"C:\shots", "out.json"), a);
    }

    [Fact]
    public void EmptyAndMissingValues()
    {
        Assert.Equal(new AppArgs(false, false, false, false, false, false, null, null), AppArgs.Parse([]));
        var a = AppArgs.Parse(["--screenshot", "--demo"]);
        Assert.Null(a.ScreenshotDir);
        Assert.True(a.Demo);
    }
}

public sealed class UpdateCheckerTests : IDisposable
{
    private readonly Harness h = new();

    public void Dispose() => h.Dispose();

    private static string Release(string tag, bool prerelease = false, string assets = """
        [ { "name": "Lintel-win-x64.zip", "browser_download_url": "https://github.com/o/r/releases/download/v1.2.0/Lintel-win-x64.zip" },
          { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/o/r/releases/download/v1.2.0/SHA256SUMS.txt" } ]
        """) =>
        $$"""{ "tag_name": "{{tag}}", "prerelease": {{(prerelease ? "true" : "false")}}, "draft": false, "html_url": "https://github.com/o/r/releases/tag/{{tag}}", "assets": {{assets}} }""";

    [Fact]
    public void NewerReleaseIsReturned()
    {
        var r = UpdateChecker.Parse(Release("v1.2.0"), new Version(1, 0, 0));
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 2, 0), r.Version);
        Assert.Equal("v1.2.0", r.Tag);
        Assert.Equal("https://github.com/o/r/releases/tag/v1.2.0", r.HtmlUrl);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("v0.9.9")]
    public void SameOrOlderIsNull(string tag) => Assert.Null(UpdateChecker.Parse(Release(tag), new Version(1, 0, 0)));

    [Fact]
    public void PrereleaseIsIgnored()
    {
        Assert.Null(UpdateChecker.Parse(Release("v2.0.0", prerelease: true), new Version(1, 0, 0)));
        Assert.Null(UpdateChecker.Parse(Release("v2.0.0-beta.1"), new Version(1, 0, 0)));
    }

    [Fact]
    public void GarbageIsNull()
    {
        Assert.Null(UpdateChecker.Parse("not json", new Version(1, 0, 0)));
        Assert.Null(UpdateChecker.Parse("""{ "message": "Not Found" }""", new Version(1, 0, 0)));
    }

    [Fact]
    public async Task CheckAsyncNeedsConsentAndRespectsTheDailyRule()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(() => { calls++; return Release("v1.2.0"); }));
        var time = new FixedTime(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var checker = new UpdateChecker(http, new AppInfo("1.0.0", "o", "r", "x.exe"), h.Settings, h.Consent, time);

        Assert.Null(await checker.CheckAsync(userInitiated: true, CancellationToken.None));
        Assert.Equal(0, calls);

        h.Consent.Grant(PermissionIds.NetworkUpdates);
        Assert.NotNull(await checker.CheckAsync(userInitiated: false, CancellationToken.None));
        Assert.Null(await checker.CheckAsync(userInitiated: false, CancellationToken.None));   // < 24 h
        Assert.Equal(1, calls);

        h.Settings.Set(SettingKeys.AppUpdatesSkipVersion, "v1.2.0");
        time.Now = time.Now.AddHours(25);
        Assert.Null(await checker.CheckAsync(userInitiated: false, CancellationToken.None));   // skipped version
        Assert.NotNull(await checker.CheckAsync(userInitiated: true, CancellationToken.None)); // user check shows it
        Assert.Equal(3, calls);
    }

    private sealed class StubHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/repos/o/r/releases/latest", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) });
        }
    }
}

public sealed class FormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CountdownUsesFriendlyWording()
    {
        Assert.Equal("Reset time not reported", Format.Countdown(null, Now));
        Assert.Equal("Reset due · awaiting update", Format.Countdown(Now, Now));
        Assert.Equal("Resets in 3d 18h", Format.Countdown(Now.AddDays(3).AddHours(18), Now));
        Assert.Equal("Resets in 2h 14m", Format.Countdown(Now.AddHours(2).AddMinutes(14), Now));
        Assert.Equal("Resets in 5m", Format.Countdown(Now.AddMinutes(4).AddSeconds(10), Now));
        Assert.Equal("Resets in 1m", Format.Countdown(Now.AddSeconds(5), Now));
    }

    [Fact]
    public void DurationsAndClocks()
    {
        Assert.Equal("42s", Format.Duration(TimeSpan.FromSeconds(42)));
        Assert.Equal("4m 12s", Format.Duration(TimeSpan.FromSeconds(252)));
        Assert.Equal("1h 5m", Format.Duration(new TimeSpan(1, 5, 9)));
        Assert.Equal("2d 3h", Format.Duration(new TimeSpan(2, 3, 0, 0)));
        Assert.Equal("0s", Format.Duration(TimeSpan.FromSeconds(-3)));
        Assert.Equal("0:05", Format.Clock(TimeSpan.FromSeconds(5)));
        Assert.Equal("12:34", Format.Clock(new TimeSpan(0, 12, 34)));
        Assert.Equal("1:02:03", Format.Clock(new TimeSpan(1, 2, 3)));
        Assert.Equal("0:00", Format.Clock(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(842, "842")]
    [InlineData(1000, "1K")]
    [InlineData(1234, "1.2K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(123_456, "123K")]
    [InlineData(999_950, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(4_500_000_000, "4.5B")]
    public void Tokens(long value, string expected) => Assert.Equal(expected, Format.Tokens(value));

    [Fact]
    public void MoneyBytesPercent()
    {
        Assert.Equal("$0.84", Format.Money(0.84));
        Assert.Equal("$1,234.50", Format.Money(1234.5));
        Assert.Equal("—", Format.Money(null));
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.Equal("1.5 KB", Format.Bytes(1536));
        Assert.Equal("120 MB", Format.Bytes(120L * 1024 * 1024));
        Assert.Equal("42%", Format.Percent(42.4));
        Assert.Equal("100%", Format.Percent(99.6));
    }
}

public sealed class AgentModelTests
{
    [Fact]
    public void TokenMathClampsAndMaxes()
    {
        var a = new AgentTokens(10, 5, 100, 40, 8);
        var b = new AgentTokens(4, 9, 50, 50, 2);
        Assert.Equal(new AgentTokens(14, 14, 150, 90, 10), a + b);
        Assert.Equal(new AgentTokens(6, 0, 50, 0, 6), a - b);
        Assert.Equal(new AgentTokens(10, 9, 100, 50, 8), a.Max(b));
        Assert.Equal(155, a.Total);
        Assert.Equal(115, a.Prompt);
        Assert.Equal(100d / 115, a.CacheHitRate);
        Assert.Null(default(AgentTokens).CacheHitRate);
    }

    [Fact]
    public void WindowsAndEmptySnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var w = new AgentLimitWindow("five_hour", LimitKind.Session, 300, null, "5 hours", 130, now);
        Assert.Equal(0, w.RemainingPercent);
        Assert.True(w.ResetDue(now));
        Assert.False(w with { ResetsAt = null } is var x && x.ResetDue(now));
        Assert.Equal(3, AgentUsageSnapshot.Empty.Periods.Count);
        Assert.Equal(AgentTotals.Zero, AgentUsageSnapshot.Empty.Periods[AgentPeriod.Month].Total);
    }
}
