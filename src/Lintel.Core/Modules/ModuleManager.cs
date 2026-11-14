// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Lintel.Core.Settings;

namespace Lintel.Core.Modules;

/// <summary>
/// Installs, starts and stops modules. All calls on the UI thread. A module is constructed only when it starts.
/// Status: not installed → NotInstalled; running → Running; Start threw → Failed (until reinstalled);
/// installed but a required permission is missing → NeedsPermission.
/// </summary>
public sealed class ModuleManager : IModuleManager
{
    private readonly IModuleContext context;
    private readonly Dictionary<string, INotchModule> running = [];
    private readonly Dictionary<string, string> failures = [];
    private bool active;   // between StartInstalled and StopAll

    public ModuleManager(IReadOnlyList<ModuleDescriptor> catalog, IModuleContext context)
    {
        Catalog = catalog;
        this.context = context;
        context.Consent.Changed += OnConsentChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ModuleDescriptor> Catalog { get; }

    public IReadOnlyList<INotchModule> Running =>
        Catalog.Where(d => running.ContainsKey(d.Manifest.Id)).Select(d => running[d.Manifest.Id]).ToList();

    public ModuleStatus GetStatus(string moduleId)
    {
        var descriptor = Find(moduleId);
        if (descriptor is null || !IsInstalled(moduleId))
            return ModuleStatus.NotInstalled;
        if (running.ContainsKey(moduleId))
            return ModuleStatus.Running;
        if (failures.ContainsKey(moduleId))
            return ModuleStatus.Failed;
        // installed + permitted but not started only happens before StartInstalled / after StopAll; reported as NotInstalled.
        return HasRequired(descriptor.Manifest) ? ModuleStatus.NotInstalled : ModuleStatus.NeedsPermission;
    }

    public string? GetFailure(string moduleId) => failures.GetValueOrDefault(moduleId);

    public void Install(string moduleId)
    {
        var descriptor = Find(moduleId) ?? throw new ArgumentException($"Unknown module '{moduleId}'", nameof(moduleId));
        context.Settings.Set(SettingKeys.ModuleInstalled(moduleId), true);
        failures.Remove(moduleId);
        if (!running.ContainsKey(moduleId) && HasRequired(descriptor.Manifest))
            Start(descriptor);
        RaiseChanged();
    }

    public void Uninstall(string moduleId, bool deleteData)
    {
        _ = Find(moduleId) ?? throw new ArgumentException($"Unknown module '{moduleId}'", nameof(moduleId));
        var module = Stop(moduleId);
        context.Settings.Set(SettingKeys.ModuleInstalled(moduleId), false);
        failures.Remove(moduleId);
        if (deleteData)
        {
            if (module is not null)
                Guard(moduleId, "DeleteData", module.DeleteData);
            // Also covers modules that never started (no instance to ask).
            context.Settings.RemovePrefix(moduleId + ".");
            Guard(moduleId, "delete data directory", () => Directory.Delete(context.GetDataDirectory(moduleId), recursive: true));
        }
        RaiseChanged();
    }

    public void StartInstalled()
    {
        active = true;
        foreach (var descriptor in Catalog)
        {
            var id = descriptor.Manifest.Id;
            if (IsInstalled(id) && !running.ContainsKey(id) && HasRequired(descriptor.Manifest))
                Start(descriptor);
        }
        RaiseChanged();
    }

    public void StopAll()
    {
        active = false;
        foreach (var descriptor in Catalog.Reverse())
            Stop(descriptor.Manifest.Id);
        RaiseChanged();
    }

    private void OnConsentChanged(string permissionId)
    {
        if (!active)
            return;
        var changed = false;
        foreach (var descriptor in Catalog)
        {
            var id = descriptor.Manifest.Id;
            if (!IsInstalled(id))
                continue;
            var permitted = HasRequired(descriptor.Manifest);
            if (running.ContainsKey(id) && !permitted)
            {
                Stop(id);
                changed = true;
            }
            else if (!running.ContainsKey(id) && permitted && !failures.ContainsKey(id))
            {
                Start(descriptor);
                changed = true;
            }
        }
        if (changed)
            RaiseChanged();
    }

    private void Start(ModuleDescriptor descriptor)
    {
        var id = descriptor.Manifest.Id;
        INotchModule? module = null;
        try
        {
            module = descriptor.Create();
            module.Start(context);
            running[id] = module;
            failures.Remove(id);
            context.Log.Info($"Module {id} started");
        }
        catch (Exception e)
        {
            failures[id] = e.Message;
            context.Log.Error($"Module {id} failed to start", e);
            if (module is not null)
                Guard(id, "Stop after failed Start", module.Stop);
        }
    }

    private INotchModule? Stop(string moduleId)
    {
        if (!running.Remove(moduleId, out var module))
            return null;
        Guard(moduleId, "Stop", module.Stop);
        context.Log.Info($"Module {moduleId} stopped");
        return module;
    }

    private void Guard(string moduleId, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            context.Log.Warn($"Module {moduleId}: {what} failed", e);
        }
    }

    private ModuleDescriptor? Find(string moduleId) => Catalog.FirstOrDefault(d => d.Manifest.Id == moduleId);

    private bool IsInstalled(string moduleId) => context.Settings.Get(SettingKeys.ModuleInstalled(moduleId), false);

    private bool HasRequired(ModuleManifest manifest) => manifest.RequiredPermissions.All(context.Consent.IsGranted);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
