// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;
using Lintel.Tools.KeepAwake;
using Lintel.Tools.SystemStats;
using Lintel.Tools.Timer;
using Lintel.Tools.Toggles;

namespace Lintel.Tools;

/// <summary>Entry point of the Tools modules: timer, keep awake, system monitor, quick toggles.</summary>
public static class ToolsCatalog
{
    public static IReadOnlyList<ModuleDescriptor> Modules { get; } = Describe(
        () => new TimerModule(), () => new SystemModule(), () => new KeepAwakeModule(), () => new TogglesModule());

    private static IReadOnlyList<ModuleDescriptor> Describe(params Func<INotchModule>[] factories) =>
        factories.Select(f => new ModuleDescriptor(f().Manifest, f)).ToList();
}
