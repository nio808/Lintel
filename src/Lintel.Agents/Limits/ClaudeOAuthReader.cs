// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Limits;

/// <summary>
/// Claude plan limits through the private OAuth usage endpoint. The access token is read from
/// <c>.credentials.json</c> on every check (read-only, full sharing), sent only to api.anthropic.com, never written by
/// Lintel, never logged or kept: it lives in one local variable for the duration of the request. The token lasts hours and
/// the CLI only renews it when it runs, so on a 401 Lintel runs <c>claude auth status</c> (which renews and saves it) and
/// retries once.
/// </summary>
internal sealed class ClaudeOAuthReader(HttpClient http, TimeProvider time, Func<PriceList> prices, string? configDir = null, string? endpoint = null,
                                        Func<CancellationToken, Task>? renew = null)
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    public static string CredentialsPath(string? configDir = null)
    {
        var root = configDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(root, ".credentials.json");
    }

    public static bool CredentialsExist(string? configDir = null) => File.Exists(CredentialsPath(configDir));

    public async Task<AgentLimits> ReadAsync(CancellationToken ct)
    {
        var path = CredentialsPath(configDir);
        if (!File.Exists(path)) throw new LimitReadException("Sign in with `claude auth login`, then refresh.");
        string? token, subscription, tier;
        try
        {
            (token, subscription, tier) = ReadCredentials(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            throw new LimitReadException("Could not read Claude login. Try again after the CLI finishes signing in.");
        }
        if (string.IsNullOrWhiteSpace(token)) throw new LimitReadException("Claude subscription login not found. Run `claude auth login`.");

        var response = await SendAsync(token, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && await RenewAsync(path, token, ct).ConfigureAwait(false) is { } renewed)
        {
            response.Dispose();
            response = await SendAsync(renewed, ct).ConfigureAwait(false);
        }
        using var _ = response;
        var code = response.StatusCode;
        if ((int)code == 429)
        {
            var retry = response.Headers.RetryAfter;
            TimeSpan? wait = retry is null ? null : retry.Delta ?? (retry.Date is { } d ? d - time.GetUtcNow() : null);
            throw new LimitReadException("Claude is limiting usage checks. Waiting before trying again.", LimitFailure.RateLimited, wait);
        }
        if (code == HttpStatusCode.Unauthorized)
            throw new LimitReadException(ClaudeCli.SignInMessage, LimitFailure.Auth);
        if (code == HttpStatusCode.Forbidden)
            throw new LimitReadException("Claude denied the usage read. Check your subscription login.", LimitFailure.Auth);
        if (!response.IsSuccessStatusCode)
            throw new LimitReadException($"Claude usage is temporarily unavailable (HTTP {(int)code}).");
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var list = prices();
        return ClaudeUsageParser.Parse(body, AgentPlans.Claude(subscription, tier, list), time.GetUtcNow());
    }

    private async Task<HttpResponseMessage> SendAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint ?? Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>The renewed token, or null when the CLI did not change it.</summary>
    private async Task<string?> RenewAsync(string path, string old, CancellationToken ct)
    {
        await (renew ?? RenewWithCliAsync)(ct).ConfigureAwait(false);
        try
        {
            var token = ReadCredentials(path).Token;
            return string.IsNullOrWhiteSpace(token) || token == old ? null : token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { return null; }
    }

    /// <summary>Hidden <c>claude auth status</c>: the CLI renews an expired token with its refresh token and saves it. 30 s cap.</summary>
    private async Task RenewWithCliAsync(CancellationToken ct)
    {
        if (ClaudeCli.Find() is not { } binary) return;
        var info = ClaudeCli.StartInfo(binary, configDir, "auth", "status");
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { return; }
        if (process is null) return;
        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>(accessToken, subscriptionType, rateLimitTier) from claudeAiOauth. Opened read-only with full sharing.</summary>
    internal static (string? Token, string? Subscription, string? Tier) ReadCredentials(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(stream);
        JsonElement? auth = doc.RootElement.Prop("claudeAiOauth");
        return (auth.Str("accessToken"), auth.Str("subscriptionType"), auth.Str("rateLimitTier"));
    }
}
