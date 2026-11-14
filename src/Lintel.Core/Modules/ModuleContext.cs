// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Core.Modules;

/// <summary>The service bag every module receives. <paramref name="dataRoot"/> overrides %LOCALAPPDATA%\Lintel (Preview, tests, --screenshot).</summary>
public sealed class ModuleContext(
    AppInfo app, bool isDemo, ISettingsStore settings, IConsentService consent, INotchHost notch,
    IHotkeyService hotkeys, IAppLog log, Dispatcher ui, HttpClient http, TimeProvider time, string? dataRoot = null) : IModuleContext
{
    public AppInfo App { get; } = app;
    public bool IsDemo { get; } = isDemo;
    public ISettingsStore Settings { get; } = settings;
    public IConsentService Consent { get; } = consent;
    public INotchHost Notch { get; } = notch;
    public IHotkeyService Hotkeys { get; } = hotkeys;
    public IAppLog Log { get; } = log;
    public Dispatcher Ui { get; } = ui;
    public HttpClient Http { get; } = http;
    public TimeProvider Time { get; } = time;

    public string GetDataDirectory(string moduleId)
    {
        var path = dataRoot is null ? AppPaths.Data(moduleId) : Path.Combine(dataRoot, "data", moduleId);
        Directory.CreateDirectory(path);
        return path;
    }
}
