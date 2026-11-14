// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Lintel.App.Cli;
using Lintel.App.Update;
using Lintel.Core.Agents;

namespace Lintel.App.Tests;

public class UpdateInstallerTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void FindChecksum_FindsEntryCaseInsensitivelyAndLowercases()
    {
        var sums = $"{new string('B', 64)}  Lintel-win-x64.zip\n{Hash} *install.ps1\r\nnot a checksum\n";
        Assert.Equal(new string('b', 64), UpdateInstaller.FindChecksum(sums, "lintel-win-x64.zip"));
        Assert.Equal(Hash, UpdateInstaller.FindChecksum(sums, "install.ps1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123  install.ps1")]
    public void FindChecksum_RejectsMalformedLines(string sums) => Assert.Null(UpdateInstaller.FindChecksum(sums, "install.ps1"));

    [Fact]
    public void FindChecksum_UnknownFileIsNull() => Assert.Null(UpdateInstaller.FindChecksum($"{Hash}  a.zip", "b.zip"));

    [Fact]
    public void AssetUrl_IsVersionedGitHubDownload() =>
        Assert.Equal("https://github.com/nio808/Lintel/releases/download/v1.2.3/install.ps1",
            UpdateInstaller.AssetUrl("nio808", "Lintel", "v1.2.3", "install.ps1"));

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2", true)]
    [InlineData("v1.2.3-rc.1", false)]
    [InlineData("v1.2.3; calc", false)]
    [InlineData("\"x", false)]
    public void IsSafeTag_OnlyPlainVersions(string tag, bool ok) => Assert.Equal(ok, UpdateInstaller.IsSafeTag(tag));
}

public class CliCommandsTests
{
    [Fact]
    public void LimitsJson_EmptySnapshotIsEmptyArray() =>
        Assert.Equal(0, JsonDocument.Parse(CliCommands.LimitsJson(AgentUsageSnapshot.Empty)).RootElement.GetArrayLength());

    [Fact]
    public void LimitsJson_WritesWindowShape()
    {
        var reset = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var limits = new AgentLimits("claude:default", AgentProvider.Claude,
            [new AgentLimitWindow("s", LimitKind.Session, 300, null, "5h", 37.46, reset)],
            reset, LimitSource.Account, new AgentPlan("Pro", null), null, false, false, null, null);
        var json = CliCommands.LimitsJson(AgentUsageSnapshot.Empty with { Limits = [limits] });

        var item = JsonDocument.Parse(json).RootElement[0];
        Assert.Equal("claude", item.GetProperty("provider").GetString());
        Assert.Equal("Pro", item.GetProperty("plan").GetString());
        var w = item.GetProperty("windows")[0];
        Assert.Equal("session", w.GetProperty("kind").GetString());
        Assert.Equal(300, w.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(62.5, w.GetProperty("remainingPercent").GetDouble());
    }
}
