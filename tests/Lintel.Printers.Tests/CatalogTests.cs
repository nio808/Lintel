// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Permissions;
using Lintel.Printers.Spooler;
using Xunit.Abstractions;

namespace Lintel.Printers.Tests;

public sealed class CatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void ManifestDeclaresItsPermissions()
    {
        var m = Assert.Single(PrintersCatalog.Modules).Manifest;
        Assert.Equal("printers", m.Id);
        Assert.Equal([PermissionIds.PrinterQueue], m.RequiredPermissions);
        Assert.Equal([PermissionIds.PrinterMaintenance, PermissionIds.PrinterPadReset], m.OptionalPermissions);
        Assert.False(m.Recommended);
        foreach (var id in m.RequiredPermissions.Concat(m.OptionalPermissions))
            Assert.NotNull(PermissionCatalog.Find(id));
    }

    [Fact]
    public void PadResetHasItsOwnPermissionSeparateFromMaintenance()
    {
        Assert.NotEqual(PermissionIds.PrinterMaintenance, PermissionIds.PrinterPadReset);
        var info = PermissionCatalog.Find(PermissionIds.PrinterPadReset)!;
        Assert.Contains("ink pad", info.PlainWords + info.Touches, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read-only: enumerates the machine's printers and queues. Passes on a machine without printers.</summary>
    [Fact]
    public void RealSpoolerCanBeReadWithoutSideEffects()
    {
        var snap = WinSpoolerSource.ReadSnapshot();
        foreach (var p in PrinterRules.Physical(snap.Printers))
            output.WriteLine($"{p.Name} | {p.Driver} | {p.Port} | {p.Kind} | {p.State} | default={p.IsDefault} | jobs={p.JobCount}");
        output.WriteLine($"{snap.Printers.Count} printers in total, {snap.Jobs.Count} jobs");
    }
}
