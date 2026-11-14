// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Lintel.Core.Settings;

/// <summary>%LOCALAPPDATA%\Lintel\ layout. Nothing here creates directories.</summary>
public static class AppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Root { get; } = Path.Combine(LocalAppData, "Lintel");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Data(string moduleId) => Path.Combine(Root, "data", moduleId);
}
