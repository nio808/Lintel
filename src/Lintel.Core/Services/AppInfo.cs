// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace Lintel.Core.Services;

public sealed record AppInfo(string Version, string RepositoryOwner, string RepositoryName, string ExePath)
{
    /// <summary>Version = informational version without "+build" metadata; repository from the AssemblyMetadata that Directory.Build.props emits.</summary>
    public static AppInfo FromAssembly(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        string Meta(string key, string fallback) =>
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? fallback;
        return new(version.Split('+')[0], Meta("RepositoryOwner", "OWNER"), Meta("RepositoryName", "Lintel"),
            Environment.ProcessPath ?? assembly.Location);
    }
}

public sealed record AppArgs(bool Autostart, bool Settings, bool Quit, bool Demo, bool Layered,
                             bool ShowVersion, string? ScreenshotDir, string? ReadUsageFile)
{
    /// <summary>Case-insensitive; unknown arguments are ignored; a value-taking switch without a value yields null.</summary>
    public static AppArgs Parse(string[] args)
    {
        bool autostart = false, settings = false, quit = false, demo = false, layered = false, version = false;
        string? screenshot = null, readUsage = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--autostart": autostart = true; break;
                case "--settings": settings = true; break;
                case "--quit": quit = true; break;
                case "--demo": demo = true; break;
                case "--layered": layered = true; break;
                case "--version": version = true; break;
                case "--screenshot": screenshot = Value(args, ref i); break;
                case "--read-usage": readUsage = Value(args, ref i); break;
            }
        }
        return new(autostart, settings, quit, demo, layered, version, screenshot, readUsage);
    }

    private static string? Value(string[] args, ref int i) =>
        i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
}

/// <summary>Builds the shared IModuleContext.Http client.</summary>
public static class AppHttp
{
    /// <summary>No redirects, no cookies, 20 s timeout, User-Agent "Lintel/&lt;version&gt;".</summary>
    public static HttpClient Create(AppInfo app)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd($"Lintel/{app.Version}");
        return http;
    }
}
