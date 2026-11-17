// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;

namespace Lintel.Printers;

/// <summary>Entry point of the Printers module.</summary>
public static class PrintersCatalog
{
    public static IReadOnlyList<ModuleDescriptor> Modules { get; } = [Describe(() => new PrintersModule())];

    private static ModuleDescriptor Describe(Func<INotchModule> factory) => new(factory().Manifest, factory);
}
