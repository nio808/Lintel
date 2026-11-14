// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;
using Lintel.Core.Settings;

namespace Lintel.Core.Permissions;

/// <summary>Consents live in settings as consent.&lt;permissionId&gt; = bool. Changed fires on the caller's (UI) thread, only on a real change.</summary>
public sealed class ConsentService(ISettingsStore settings) : IConsentService
{
    /// <summary>Used by the app itself, not by a module, so never reported by <see cref="Unused"/>.</summary>
    public static IReadOnlyList<string> AppLevel { get; } = [PermissionIds.Startup, PermissionIds.NetworkUpdates];

    public event Action<string>? Changed;

    public bool IsGranted(string permissionId) => settings.Get(SettingKeys.Consent(permissionId), false);

    public void Grant(string permissionId) => Apply(permissionId, true);

    public void Revoke(string permissionId) => Apply(permissionId, false);

    public IReadOnlyList<string> Unused(IReadOnlyList<ModuleManifest> installed)
    {
        var used = installed.SelectMany(m => m.RequiredPermissions.Concat(m.OptionalPermissions)).Concat(AppLevel).ToHashSet();
        return PermissionCatalog.All.Select(p => p.Id).Where(id => IsGranted(id) && !used.Contains(id)).ToList();
    }

    private void Apply(string permissionId, bool granted)
    {
        if (IsGranted(permissionId) == granted)
            return;
        settings.Set(SettingKeys.Consent(permissionId), granted);
        Changed?.Invoke(permissionId);
    }
}
