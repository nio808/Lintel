// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Tools.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void CatalogHasTheFourToolsModulesWithPlannedIds()
    {
        var m = ToolsCatalog.Modules.ToDictionary(d => d.Manifest.Id, d => d.Manifest);
        Assert.Equal(["timer", "system", "keepawake", "toggles"], ToolsCatalog.Modules.Select(d => d.Manifest.Id));
        Assert.Equal(40, m["timer"].Order);
        Assert.Equal(60, m["system"].Order);
        Assert.Equal(100, m["keepawake"].Order);
        Assert.Equal(110, m["toggles"].Order);
        Assert.Equal(["power.keep-awake"], m["keepawake"].RequiredPermissions);
        Assert.Equal(["system.stats"], m["system"].RequiredPermissions);
        Assert.Equal(["system.toggles"], m["toggles"].RequiredPermissions);
        Assert.Empty(m["timer"].RequiredPermissions);
        Assert.True(m["timer"].Recommended && m["system"].Recommended);
        Assert.False(m["keepawake"].Recommended || m["toggles"].Recommended);
    }
}
