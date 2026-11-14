// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;

namespace Lintel.Media;

/// <summary>Entry point of the Media modules: Now Playing and Volume Mixer.</summary>
public static class MediaCatalog
{
    public static IReadOnlyList<ModuleDescriptor> Modules { get; } =
    [
        new(MediaModule.ManifestInfo, () => new MediaModule()),
        new(MixerModule.ManifestInfo, () => new MixerModule()),
    ];
}
