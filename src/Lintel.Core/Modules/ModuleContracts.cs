// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Core.Modules;

public enum ModuleCategory { Agents, Media, Productivity, System }

public enum ModuleStatus { NotInstalled, Running, NeedsPermission, Failed }

public sealed record ModuleManifest
{
    public required string Id { get; init; }              // "agents", "media", … (lowercase, stable)
    public required string Name { get; init; }            // "AI Agents"
    public required string Summary { get; init; }         // one line for the Features hub
    public required string Glyph { get; init; }           // a Glyphs.* constant
    public required ModuleCategory Category { get; init; }
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];
    public IReadOnlyList<string> OptionalPermissions { get; init; } = [];
    public bool Recommended { get; init; }                // part of the "Recommended" preset
    public int Order { get; init; }                       // hub and default section order
}

public interface INotchModule
{
    ModuleManifest Manifest { get; }

    /// <summary>UI thread. Return within 50 ms; start background work with Task.Run. Called once per instance.</summary>
    void Start(IModuleContext context);

    /// <summary>UI thread. Stop timers, threads, child processes, hooks; flush data. Idempotent.</summary>
    void Stop();

    /// <summary>Fixed after Start. Sections with an Id already shown by another module are shown once.</summary>
    IReadOnlyList<NotchSection> Sections { get; }

    /// <summary>Fixed after Start; each toggles its own IsActive.</summary>
    IReadOnlyList<CompactActivity> Activities { get; }

    /// <summary>Plain WPF controls only (the Settings window styles them). Null = no settings page.</summary>
    FrameworkElement? CreateSettingsView();

    /// <summary>Called after Stop when the user chose "Delete data": remove context.DataDirectory contents and own setting keys.</summary>
    void DeleteData();
}

public sealed record ModuleDescriptor(ModuleManifest Manifest, Func<INotchModule> Create);

public interface IModuleManager
{
    IReadOnlyList<ModuleDescriptor> Catalog { get; }
    IReadOnlyList<INotchModule> Running { get; }
    ModuleStatus GetStatus(string moduleId);
    string? GetFailure(string moduleId);

    /// <summary>Sets modules.&lt;id&gt;.installed=true and starts it if all RequiredPermissions are granted.</summary>
    void Install(string moduleId);
    void Uninstall(string moduleId, bool deleteData);
    void StartInstalled();          // app start
    void StopAll();                 // app exit
    event EventHandler? Changed;    // UI thread; Running or any status changed
}

public interface IModuleContext
{
    AppInfo App { get; }
    bool IsDemo { get; }                    // --demo: show canned data, touch no accounts
    ISettingsStore Settings { get; }
    IConsentService Consent { get; }
    INotchHost Notch { get; }
    IHotkeyService Hotkeys { get; }
    IAppLog Log { get; }
    Dispatcher Ui { get; }
    HttpClient Http { get; }                // shared: no redirects, no cookies, 20 s timeout, UA "Lintel/<version>"
    TimeProvider Time { get; }
    string GetDataDirectory(string moduleId);   // %LOCALAPPDATA%\Lintel\data\<moduleId>, created
}
