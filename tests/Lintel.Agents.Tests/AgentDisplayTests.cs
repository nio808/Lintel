// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Agents.UI;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class AgentDisplayTests
{
    [Theory]
    [InlineData(43200, "30d", "30-day")]
    [InlineData(10080, "7d", "7-day")]
    [InlineData(300, "5h", "5-hour")]
    [InlineData(1440, "24h", "24-hour")]
    [InlineData(2880, "2d", "2-day")]
    [InlineData(2940, "49h", "49-hour")]
    [InlineData(90, "1.5h", "1.5-hour")]
    public void WindowDurationLabels(int minutes, string shortLabel, string label)
    {
        var window = new AgentLimitWindow("w", LimitKind.Session, minutes, null, "", 0, null);
        Assert.Equal(shortLabel, AgentDisplay.ShortLabel(window));
        Assert.Equal(label, AgentDisplay.Label(window));
        Assert.Equal(shortLabel, AgentDisplay.ShortLabel(window with { Kind = LimitKind.Other }));
    }

    [Fact]
    public void SuppliedAndWeeklyLabelsStayUnchanged()
    {
        var window = new AgentLimitWindow("w", LimitKind.Weekly, 10080, null, "Weekly", 0, null);
        Assert.Equal("wk", AgentDisplay.ShortLabel(window));
        Assert.Equal("Weekly", AgentDisplay.Label(window));
        Assert.Equal("Custom", AgentDisplay.Label(window with { Kind = LimitKind.Session, Label = "Custom" }));
    }
}
