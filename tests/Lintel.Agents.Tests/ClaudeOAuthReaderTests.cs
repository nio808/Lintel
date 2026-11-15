// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Lintel.Agents.Limits;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class ClaudeOAuthReaderTests : IDisposable
{
    private const string Token = "test-token-not-a-real-credential-0000";
    private readonly string dir = T.TempDir();
    private readonly FakeTime time = new(T.Now);

    public void Dispose() => Directory.Delete(dir, true);

    private string Credentials => Path.Combine(dir, ".credentials.json");

    private void WriteCredentials(string? json = null) =>
        File.WriteAllText(Credentials, json ?? $$"""{"claudeAiOauth":{"accessToken":"{{Token}}","refreshToken":"test-refresh-not-real","subscriptionType":"max","rateLimitTier":"default_claude_max_20x"} }""");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Beta, string Accept, bool HasCookie, string? Body)> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("anthropic-beta", out var b) ? string.Join(",", b) : null,
                request.Headers.Accept.ToString(), request.Headers.Contains("Cookie"), request.Content?.ReadAsStringAsync(ct).Result));
            return Task.FromResult(respond(request));
        }
    }

    // Tests never start the real CLI: renewal is a no-op unless a test supplies one.
    private ClaudeOAuthReader Reader(Handler h, Func<CancellationToken, Task>? renew = null) =>
        new(new HttpClient(h), time, () => PriceSource.Embedded(), dir, renew: renew ?? (_ => Task.CompletedTask));

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    private async Task<LimitReadException> Fail(Handler h)
    {
        var e = await Assert.ThrowsAsync<LimitReadException>(() => Reader(h).ReadAsync(CancellationToken.None));
        Assert.DoesNotContain(Token, e.Message);
        return e;
    }

    [Fact]
    public async Task SendsTheExpectedRequestAndParsesTheReply()
    {
        WriteCredentials();
        var h = new Handler(_ => Json(T.Fixture("claude-usage.json")));
        var limits = await Reader(h).ReadAsync(CancellationToken.None);

        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", r.Url);
        Assert.Equal("Bearer " + Token, r.Auth);
        Assert.Equal("oauth-2025-04-20", r.Beta);
        Assert.Contains("application/json", r.Accept);
        Assert.False(r.HasCookie);
        Assert.Null(r.Body);

        Assert.Equal(4, limits.Windows.Count);
        Assert.Equal("Ultra", limits.Plan?.Name);          // rateLimitTier "…max_20x" beats the generic subscription type
        Assert.Equal(200, limits.Plan?.MonthlyPrice);
        Assert.Equal(LimitSource.Account, limits.Source);
        Assert.Equal(T.Now, limits.ObservedAt);
        Assert.Equal("claude:default", limits.AccountId);
    }

    [Fact]
    public async Task TheCredentialsFileIsOnlyEverReadNeverWritten()
    {
        WriteCredentials();
        File.SetLastWriteTimeUtc(Credentials, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var hash = SHA256.HashData(File.ReadAllBytes(Credentials));
        var stamp = File.GetLastWriteTimeUtc(Credentials);

        await Reader(new Handler(_ => Json(T.Fixture("claude-usage.json")))).ReadAsync(CancellationToken.None);
        await Fail(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));   // Lintel itself never writes it, not even on a 401

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Credentials)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Credentials));
    }

    [Fact]
    public async Task TheTokenIsReReadOnEveryCheck()
    {
        WriteCredentials();
        var h = new Handler(_ => Json(T.Fixture("claude-usage.json")));
        var reader = Reader(h);
        await reader.ReadAsync(CancellationToken.None);
        WriteCredentials(WriteSwapped("renewed-token-1111"));
        await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(["Bearer " + Token, "Bearer renewed-token-1111"], h.Requests.Select(r => r.Auth));

        static string WriteSwapped(string token) => $$"""{"claudeAiOauth":{"accessToken":"{{token}}"} }""";
    }

    [Fact]
    public async Task AnExpiredTokenIsRenewedByTheCliAndRetriedOnce()
    {
        WriteCredentials();
        var h = new Handler(r => r.Headers.Authorization?.Parameter == Token
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json(T.Fixture("claude-usage.json")));
        var renewals = 0;
        var limits = await Reader(h, _ =>
        {
            renewals++;
            File.WriteAllText(Credentials, """{"claudeAiOauth":{"accessToken":"renewed-by-cli-2222","subscriptionType":"max"} }""");
            return Task.CompletedTask;
        }).ReadAsync(CancellationToken.None);

        Assert.Equal(1, renewals);
        Assert.Equal(["Bearer " + Token, "Bearer renewed-by-cli-2222"], h.Requests.Select(r => r.Auth));
        Assert.Equal(4, limits.Windows.Count);
    }

    [Fact]
    public async Task ARenewalThatChangesNothingDoesNotRetry()
    {
        WriteCredentials();
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var e = await Assert.ThrowsAsync<LimitReadException>(() => Reader(h).ReadAsync(CancellationToken.None));
        Assert.Single(h.Requests);
        Assert.Equal(ClaudeCli.SignInMessage, e.Message);
    }

    [Fact]
    public void TheCliRunsWithoutTheVariablesOfTheSessionThatStartedLintel()
    {
        var info = ClaudeCli.StartInfo(@"C:\x\claude.cmd", @"D:\cfg", "auth", "status");
        Assert.EndsWith("cmd.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/d", "/c", @"C:\x\claude.cmd", "auth", "status"], info.ArgumentList);
        Assert.Equal(@"D:\cfg", info.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.DoesNotContain(info.Environment.Keys, k => k.StartsWith("ANTHROPIC_") || k.StartsWith("CLAUDE_CODE_") || k == "CLAUDECODE");
    }

    [Fact]
    public async Task ReadsWhileClaudeCodeHoldsTheFileOpen()
    {
        WriteCredentials();
        using var holder = new FileStream(Credentials, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        await Reader(new Handler(_ => Json(T.Fixture("claude-usage.json")))).ReadAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfterAsDeltaOrDate()
    {
        WriteCredentials();
        var delta = await Fail(new Handler(_ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return r;
        }));
        Assert.Equal(LimitFailure.RateLimited, delta.Kind);
        Assert.Equal(TimeSpan.FromMinutes(20), delta.RetryAfter);
        Assert.Equal("Claude is limiting usage checks. Waiting before trying again.", delta.Message);

        var date = await Fail(new Handler(_ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(T.Now.AddMinutes(45));
            return r;
        }));
        Assert.Equal(TimeSpan.FromMinutes(45), date.RetryAfter);

        var none = await Fail(new Handler(_ => new HttpResponseMessage((HttpStatusCode)429)));
        Assert.Null(none.RetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(15), BackoffPolicy.Delay(none.Kind, 1, none.RetryAfter));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ClaudeCli.SignInMessage)]
    [InlineData(HttpStatusCode.Forbidden, "Claude denied the usage read. Check your subscription login.")]
    public async Task AuthFailuresUseFixedWordingAndTheFifteenMinuteBackoff(HttpStatusCode code, string message)
    {
        WriteCredentials();
        var e = await Fail(new Handler(_ => new HttpResponseMessage(code)));
        Assert.Equal(LimitFailure.Auth, e.Kind);
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public async Task OtherHttpErrorsAreReportedWithTheirStatus()
    {
        WriteCredentials();
        var e = await Fail(new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("secret server text") }));
        Assert.Equal(LimitFailure.Other, e.Kind);
        Assert.Equal("Claude usage is temporarily unavailable (HTTP 500).", e.Message);
        Assert.DoesNotContain("secret server text", e.Message);   // response bodies are never surfaced
    }

    [Fact]
    public async Task MissingOrUnusableCredentialsGiveActionableMessages()
    {
        var missing = await Fail(new Handler(_ => throw new InvalidOperationException("must not call the network")));
        Assert.Equal("Sign in with `claude auth login`, then refresh.", missing.Message);

        WriteCredentials("""{"claudeAiOauth":{"subscriptionType":"pro"}}""");
        Assert.Equal("Claude subscription login not found. Run `claude auth login`.", (await Fail(new Handler(_ => throw new InvalidOperationException()))).Message);

        WriteCredentials("{ this is not json");
        Assert.Equal("Could not read Claude login. Try again after the CLI finishes signing in.", (await Fail(new Handler(_ => throw new InvalidOperationException()))).Message);

        WriteCredentials("{}");
        Assert.Equal("Claude subscription login not found. Run `claude auth login`.", (await Fail(new Handler(_ => throw new InvalidOperationException()))).Message);
    }

    [Fact]
    public async Task NetworkAndPayloadFailuresMapToFriendlyText()
    {
        WriteCredentials();
        var offline = await Record.ExceptionAsync(() => Reader(new Handler(_ => throw new HttpRequestException("Bearer " + Token))).ReadAsync(CancellationToken.None));
        Assert.IsType<HttpRequestException>(offline);
        Assert.DoesNotContain(Token, BackoffPolicy.Friendly(offline!));

        var empty = await Record.ExceptionAsync(() => Reader(new Handler(_ => Json("""{"five_hour":null}"""))).ReadAsync(CancellationToken.None));
        Assert.Equal("The service returned no readable subscription quotas. Try your CLI's usage screen.", BackoffPolicy.Friendly(empty!));
        var garbage = await Record.ExceptionAsync(() => Reader(new Handler(_ => Json("<html>"))).ReadAsync(CancellationToken.None));
        Assert.Equal("The service returned no readable subscription quotas. Try your CLI's usage screen.", BackoffPolicy.Friendly(garbage!));
    }

    [Fact]
    public void CredentialsPathHonoursTheConfigDirOverride()
    {
        Assert.Equal(Path.Combine(@"D:\cfg", ".credentials.json"), ClaudeOAuthReader.CredentialsPath(@"D:\cfg"));
        Assert.EndsWith(Path.Combine(".claude", ".credentials.json"), ClaudeOAuthReader.CredentialsPath(null), StringComparison.OrdinalIgnoreCase);
    }
}
