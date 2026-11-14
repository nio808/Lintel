// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Notch;

namespace Lintel.Notch;

/// <summary>Entry point of the notch window. Call on the UI thread.</summary>
public static class NotchBootstrap
{
    public static INotchShell Create(NotchDependencies deps) => new NotchController(deps);
}
