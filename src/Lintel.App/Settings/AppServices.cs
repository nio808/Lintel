// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security;
using Lintel.App.Update;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.App.Settings;

/// <summary>What the Settings window, onboarding and tray act on; built once by the composition root.</summary>
internal sealed record AppServices(
    ISettingsStore Settings, IConsentService Consent, IModuleManager Modules, IHotkeyService Hotkeys, INotchShell Notch,
    AppInfo App, UpdateInstaller Updates, IAppLog Log)
{
    public bool IsInstalled(string moduleId) => Settings.Get(SettingKeys.ModuleInstalled(moduleId), false);

    public IReadOnlyList<ModuleManifest> InstalledManifests =>
        Modules.Catalog.Select(d => d.Manifest).Where(m => IsInstalled(m.Id)).ToList();

    public IReadOnlyList<string> UnusedPermissions => Consent.Unused(InstalledManifests);

    /// <summary>Startup is the Run value (also honouring Task Manager's switch), not just the consent flag.</summary>
    public bool StartupEnabled
    {
        get
        {
            try
            {
                return StartupRegistration.IsEnabled();
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public void SetStartup(bool on)
    {
        try
        {
            StartupRegistration.Set(on, App.ExePath);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("Could not change the startup registration", e);
        }
        if (on)
            Consent.Grant(PermissionIds.Startup);
        else
            Consent.Revoke(PermissionIds.Startup);
    }

    public bool UpdateChecksOn => Settings.Get(SettingKeys.AppUpdatesCheck, true) && Consent.IsGranted(PermissionIds.NetworkUpdates);

    public void SetUpdateChecks(bool on)
    {
        Settings.Set(SettingKeys.AppUpdatesCheck, on);
        if (on)
            Consent.Grant(PermissionIds.NetworkUpdates);
        else
            Consent.Revoke(PermissionIds.NetworkUpdates);
    }

    /// <summary>Gesture currently bound to a hotkey id, or null.</summary>
    public string? Gesture(string hotkeyId) =>
        Hotkeys.Bindings.FirstOrDefault(b => b.Id == hotkeyId && b.Gesture.Length > 0)?.Gesture;
}
