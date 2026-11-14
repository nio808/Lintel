// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;

namespace Lintel.Stash;

/// <summary>Entry point of the Stash modules: clipboard history, shelf, scratchpad, downloads.</summary>
public static class StashCatalog
{
    public static IReadOnlyList<ModuleDescriptor> Modules { get; } =
    [
        new(ClipboardModule.Info, () => new ClipboardModule()),
        new(ShelfModule.Info, () => new ShelfModule()),
        new(ScratchpadModule.Info, () => new ScratchpadModule()),
        new(DownloadsModule.Info, () => new DownloadsModule()),
    ];
}
