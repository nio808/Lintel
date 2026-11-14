// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lintel.Core.Permissions;
using Lintel.Core.Settings;

namespace Lintel.Core.Services;

public sealed record ReleaseInfo(string Tag, Version Version, string HtmlUrl);

/// <summary>GET https://api.github.com/repos/{owner}/{repo}/releases/latest.</summary>
public sealed class UpdateChecker(HttpClient http, AppInfo app, ISettingsStore settings, IConsentService consent, TimeProvider time)
{
    /// <summary>
    /// Null = up to date / skipped / not consented / &lt; 24 h since last. Background checks (userInitiated=false) also honour
    /// app.updates.check and app.updates.skipVersion; user checks ignore both and the 24 h rule but still need consent.
    /// Network failures and non-404 HTTP errors throw (HttpRequestException / TaskCanceledException).
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(bool userInitiated, CancellationToken ct)
    {
        if (!consent.IsGranted(PermissionIds.NetworkUpdates))
            return null;
        var now = time.GetUtcNow();
        if (!userInitiated)
        {
            if (!settings.Get(SettingKeys.AppUpdatesCheck, true))
                return null;
            if (DateTimeOffset.TryParse(settings.Get(SettingKeys.AppUpdatesLastCheckUtc, ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var last) && now - last < TimeSpan.FromHours(24))
                return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{app.RepositoryOwner}/{app.RepositoryName}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, ct);
        settings.Set(SettingKeys.AppUpdatesLastCheckUtc, now.ToString("o", CultureInfo.InvariantCulture));
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;   // no release published yet
        response.EnsureSuccessStatusCode();

        var current = TryParseVersion(app.Version, out var version) ? version : new Version(0, 0, 0);
        var release = Parse(await response.Content.ReadAsStringAsync(ct), current);
        if (release is null)
            return null;
        var skip = settings.Get(SettingKeys.AppUpdatesSkipVersion, "");
        if (!userInitiated && skip.Length > 0 && (skip == release.Tag || skip == release.Version.ToString()))
            return null;
        return release;
    }

    /// <summary>Null when not newer than <paramref name="current"/>, a prerelease/draft, or unreadable. Asset URLs are built by UpdateInstaller from the tag.</summary>
    public static ReleaseInfo? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Flag(root, "prerelease") || Flag(root, "draft"))
                return null;
            var tag = Text(root, "tag_name");
            if (tag is null || !TryParseVersion(tag, out var version) || version <= Normalize(current))
                return null;

            return new ReleaseInfo(tag, version, Text(root, "html_url") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.2.3" / "1.2" → 1.2.3 / 1.2.0; prerelease tags ("1.2.0-beta") are refused.</summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        var core = text.Trim().TrimStart('v', 'V').Split('+')[0];
        if (core.Contains('-') || !Version.TryParse(core, out var parsed))
            return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
