// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;

namespace Lintel.Core.Tests;

public sealed class ModuleManagerTests : IDisposable
{
    private readonly Harness h = new();
    private readonly List<FakeModule> created = [];

    public void Dispose() => h.Dispose();

    private static ModuleManifest Manifest(string id, params string[] required) => new()
    {
        Id = id, Name = id, Summary = id, Glyph = "x", Category = ModuleCategory.Productivity, RequiredPermissions = required,
    };

    private ModuleDescriptor Descriptor(ModuleManifest manifest, Action<IModuleContext>? onStart = null) =>
        new(manifest, () =>
        {
            var module = new FakeModule(manifest, onStart);
            created.Add(module);
            return module;
        });

    [Fact]
    public void InstallStartsModuleWhenPermissionsAreGranted()
    {
        h.Consent.Grant(PermissionIds.ClipboardRead);
        var manager = new ModuleManager([Descriptor(Manifest("clipboard", PermissionIds.ClipboardRead))], h.Context);
        var changes = 0;
        manager.Changed += (_, _) => changes++;

        Assert.Equal(ModuleStatus.NotInstalled, manager.GetStatus("clipboard"));
        Assert.Empty(created);   // not installed = not constructed
        manager.Install("clipboard");

        Assert.Equal(ModuleStatus.Running, manager.GetStatus("clipboard"));
        Assert.True(h.Settings.Get("modules.clipboard.installed", false));
        Assert.Single(manager.Running);
        Assert.Equal(1, created.Single().Starts);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void MissingPermissionMeansNeedsPermissionAndNoInstance()
    {
        var manager = new ModuleManager([Descriptor(Manifest("clipboard", PermissionIds.ClipboardRead))], h.Context);
        manager.StartInstalled();
        manager.Install("clipboard");
        Assert.Equal(ModuleStatus.NeedsPermission, manager.GetStatus("clipboard"));
        Assert.Empty(created);
        Assert.Empty(manager.Running);
    }

    [Fact]
    public void StartThatThrowsIsFailedWithMessage()
    {
        var manager = new ModuleManager([Descriptor(Manifest("timer"), _ => throw new InvalidOperationException("boom"))], h.Context);
        manager.Install("timer");
        Assert.Equal(ModuleStatus.Failed, manager.GetStatus("timer"));
        Assert.Equal("boom", manager.GetFailure("timer"));
        Assert.Empty(manager.Running);
        Assert.Equal(1, created.Single().Stops);   // cleaned up after the failed Start

        manager.Install("timer");                  // reinstall retries with a new instance
        Assert.Equal(2, created.Count);
    }

    [Fact]
    public void RevokingARequiredPermissionStopsAndGrantingRestarts()
    {
        h.Consent.Grant(PermissionIds.MediaSession);
        h.Settings.Set("modules.media.installed", true);
        var manager = new ModuleManager([Descriptor(Manifest("media", PermissionIds.MediaSession))], h.Context);
        manager.StartInstalled();
        Assert.Equal(ModuleStatus.Running, manager.GetStatus("media"));

        h.Consent.Revoke(PermissionIds.MediaSession);
        Assert.Equal(ModuleStatus.NeedsPermission, manager.GetStatus("media"));
        Assert.Equal(1, created[0].Stops);
        Assert.Empty(manager.Running);

        h.Consent.Grant(PermissionIds.MediaSession);
        Assert.Equal(ModuleStatus.Running, manager.GetStatus("media"));
        Assert.Equal(2, created.Count);            // a fresh instance; Start is once per instance
    }

    [Fact]
    public void UninstallWithDeleteDataStopsAndClearsData()
    {
        var manager = new ModuleManager([Descriptor(Manifest("scratchpad"))], h.Context);
        manager.StartInstalled();
        manager.Install("scratchpad");
        h.Settings.Set("scratchpad.tabs", 3);
        var data = h.Context.GetDataDirectory("scratchpad");
        File.WriteAllText(Path.Combine(data, "1.txt"), "note");

        manager.Uninstall("scratchpad", deleteData: true);

        var module = created.Single();
        Assert.Equal(1, module.Stops);
        Assert.Equal(1, module.Deletes);
        Assert.Equal(ModuleStatus.NotInstalled, manager.GetStatus("scratchpad"));
        Assert.False(h.Settings.Get("modules.scratchpad.installed", true));
        Assert.False(h.Settings.Contains("scratchpad.tabs"));
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public void UninstallKeepingDataLeavesSettings()
    {
        var manager = new ModuleManager([Descriptor(Manifest("scratchpad"))], h.Context);
        manager.Install("scratchpad");
        h.Settings.Set("scratchpad.tabs", 3);
        manager.Uninstall("scratchpad", deleteData: false);
        Assert.Equal(0, created.Single().Deletes);
        Assert.True(h.Settings.Contains("scratchpad.tabs"));
    }

    [Fact]
    public void StopAllStopsEverythingAndIgnoresLaterConsentChanges()
    {
        h.Settings.Set("modules.timer.installed", true);
        h.Settings.Set("modules.system.installed", true);
        h.Consent.Grant(PermissionIds.SystemStats);
        var manager = new ModuleManager([Descriptor(Manifest("timer")), Descriptor(Manifest("system", PermissionIds.SystemStats))], h.Context);
        manager.StartInstalled();
        Assert.Equal(2, manager.Running.Count);

        manager.StopAll();
        Assert.Empty(manager.Running);
        Assert.All(created, m => Assert.Equal(1, m.Stops));

        h.Consent.Revoke(PermissionIds.SystemStats);
        h.Consent.Grant(PermissionIds.SystemStats);
        Assert.Equal(2, created.Count);
    }
}
