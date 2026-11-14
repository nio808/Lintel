// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.App.Update;

/// <summary>
/// Background update check (30 s after start, then every 6 h; <see cref="UpdateChecker"/> enforces the 24 h rule, the
/// app.updates.check switch and consent network.updates) and the in-app install: download the release's install.ps1 into
/// %TEMP%\Lintel-update\, verify its SHA-256 against SHA256SUMS.txt, start it hidden (it checks the zip hash itself, quits
/// the app, upgrades in place and relaunches), then quit. UI thread only.
/// </summary>
internal sealed partial class UpdateInstaller(UpdateChecker checker, AppInfo app, ISettingsStore settings, IConsentService consent,
    IAppLog log, Action quit) : IDisposable
{
    private readonly CancellationTokenSource cancel = new();
    private DispatcherTimer? timer;

    /// <summary>Raised on the UI thread whenever any property below changes.</summary>
    public event Action? Changed;

    /// <summary>A background check found a version that was not announced in this session yet.</summary>
    public event Action<ReleaseInfo>? Found;

    public ReleaseInfo? Available { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>0–1 while downloading, else null.</summary>
    public double? Progress { get; private set; }

    /// <summary>User-facing result of the last user action (null = nothing to say).</summary>
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public bool Allowed => consent.IsGranted(PermissionIds.NetworkUpdates);

    public DateTimeOffset? LastChecked =>
        DateTimeOffset.TryParse(settings.Get(SettingKeys.AppUpdatesLastCheckUtc, ""), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var last) ? last : null;

    public void StartBackground()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            await CheckAsync(userInitiated: false);
        };
        timer.Start();
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (Busy)
            return;
        if (!Allowed)
        {
            if (userInitiated)
                Say("Update checks are off. Allow them to look for a new version.", error: false);
            return;
        }
        Busy = true;
        if (userInitiated)
            Say("Checking for updates…", error: false);
        try
        {
            var release = await checker.CheckAsync(userInitiated, cancel.Token);
            if (release is not null)
            {
                var announce = !userInitiated && Available?.Version != release.Version;
                Available = release;
                Message = null;
                if (announce)
                    Found?.Invoke(release);
            }
            else if (userInitiated)
            {
                Available = null;
                Message = "You're up to date.";
                MessageIsError = false;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            log.Warn($"Update check failed: {e.GetType().Name}");
            if (userInitiated)
                Say("Couldn't reach GitHub. Check your connection and try again.", error: true);
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    public void Skip()
    {
        if (Available is not { } release)
            return;
        settings.Set(SettingKeys.AppUpdatesSkipVersion, release.Tag);
        Available = null;
        Say($"Skipped {release.Tag}. You'll hear about the next version.", error: false);
    }

    public async Task InstallAsync()
    {
        if (Available is not { } release || Busy)
            return;
        if (!TagPattern().IsMatch(release.Tag))
        {
            Say("This release has an unexpected version tag. Open the release page to install it.", error: true);
            return;
        }
        Busy = true;
        Progress = 0;
        Say("Downloading the update…", error: false);
        try
        {
            var script = await Task.Run(() => DownloadAndVerifyAsync(release.Tag, cancel.Token));
            log.Info($"Update {release.Tag}: install.ps1 verified; starting it");
            Launch(script, release.Tag.TrimStart('v', 'V'));
            Say("Installing… Lintel restarts when it is done.", error: false);
            quit();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or Win32Exception)
        {
            log.Warn($"Update install failed: {e.GetType().Name}: {(e is InvalidDataException ? e.Message : "download error")}");
            Say(e is InvalidDataException
                ? e.Message
                : "The update couldn't be downloaded. Try again later, or download it from the release page.", error: true);
        }
        finally
        {
            Busy = false;
            Progress = null;
            Changed?.Invoke();
        }
    }
    public void Dispose()
    {
        timer?.Stop();
        cancel.Cancel();
    }

    /// <summary>The lowercase hex digest listed for <paramref name="fileName"/> in a SHA256SUMS.txt ("&lt;hex&gt;  &lt;name&gt;"), else null.</summary>
    internal static string? FindChecksum(string sums, string fileName) =>
        sums.Split('\n')
            .Select(line => ChecksumLine().Match(line.Trim()))
            .Where(m => m.Success && m.Groups[2].Value.Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .FirstOrDefault();

    /// <summary>https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}</summary>
    internal static string AssetUrl(string owner, string repo, string tag, string asset) =>
        $"https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}";

    /// <summary>Tags that are safe to put on a command line: v1.2.3, 1.2.3, v1.2.</summary>
    internal static bool IsSafeTag(string tag) => TagPattern().IsMatch(tag);

    /// <summary>Downloads the release's install.ps1 + SHA256SUMS.txt into %TEMP%\Lintel-update\ and returns the script path once its SHA-256 matches.</summary>
    private async Task<string> DownloadAndVerifyAsync(string tag, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Lintel-update");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        // GitHub answers asset URLs with a redirect to objects.githubusercontent.com, so this client (unlike the shared one) follows redirects.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd($"Lintel/{app.Version}");

        string sums;
        using (var response = await GetAsync(http, AssetUrl(app.RepositoryOwner, app.RepositoryName, tag, "SHA256SUMS.txt"), ct))
            sums = await response.Content.ReadAsStringAsync(ct);
        var expected = FindChecksum(sums, "install.ps1")
            ?? throw new InvalidDataException("SHA256SUMS.txt doesn't list install.ps1, so the update was not run.");

        var script = Path.Combine(folder, "install.ps1");
        using (var response = await GetAsync(http, AssetUrl(app.RepositoryOwner, app.RepositoryName, tag, "install.ps1"), ct))
            await File.WriteAllBytesAsync(script, await response.Content.ReadAsByteArrayAsync(ct), ct);

        string actual;
        await using (var stream = File.OpenRead(script))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(script);
            throw new InvalidDataException("The download doesn't match its published SHA-256 checksum, so it was deleted and not run.");
        }
        return script;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var host = response.RequestMessage?.RequestUri?.Host ?? "";
        if (!(host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
              || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            response.Dispose();
            throw new InvalidDataException("The download was redirected to an unexpected host, so it was stopped.");
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>Starts install.ps1 hidden; it verifies the zip's SHA-256 itself, quits this app, upgrades in place and relaunches.</summary>
    private static void Launch(string script, string version)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-Version", version })
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
    private void Say(string message, bool error)
    {
        Message = message;
        MessageIsError = error;
        Changed?.Invoke();
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
    private static partial Regex ChecksumLine();

    [GeneratedRegex(@"^[vV]?\d+\.\d+(\.\d+)?$")]
    private static partial Regex TagPattern();
}
