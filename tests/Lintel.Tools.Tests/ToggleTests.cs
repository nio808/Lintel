// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Tools.Toggles;

namespace Lintel.Tools.Tests;

public sealed class ToggleTests
{
    private sealed class Fake(ToggleKind kind, bool? state = null) : IToggleAction
    {
        public string Id => "fake";
        public string Title => "Fake";
        public string Glyph => "";
        public ToggleKind Kind => kind;
        public bool? State { get; set; } = state;
        public int Runs { get; private set; }
        public bool Throw { get; set; }
        public bool? ReadState() => State;
        public void Execute()
        {
            if (Throw)
                throw new InvalidOperationException("nope");
            Runs++;
            if (Kind == ToggleKind.Switch)
                State = !State;
        }
    }

    private sealed class Registry : IRegistryDword
    {
        public Dictionary<string, int> Values { get; } = [];
        public int? Get(string subKey, string name) => Values.TryGetValue(name, out var v) ? v : null;
        public void Set(string subKey, string name, int value) => Values[name] = value;
    }

    private readonly FakeTime time = new();

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, null)]
    public void DarkModeMapsAppsUseLightTheme(int? raw, bool? dark) => Assert.Equal(dark, DarkModeToggle.IsDark(raw));

    [Fact]
    public void DarkModeFlipsBothValuesAndBroadcasts()
    {
        var reg = new Registry();
        reg.Values["AppsUseLightTheme"] = 1;
        reg.Values["SystemUsesLightTheme"] = 1;
        var broadcasts = 0;
        var toggle = new DarkModeToggle(reg, () => broadcasts++);
        Assert.False(toggle.ReadState());
        toggle.Execute();
        Assert.Equal(0, reg.Values["AppsUseLightTheme"]);
        Assert.Equal(0, reg.Values["SystemUsesLightTheme"]);
        Assert.True(toggle.ReadState());
        toggle.Execute();
        Assert.Equal(1, reg.Values["AppsUseLightTheme"]);
        Assert.Equal(2, broadcasts);
    }

    [Fact]
    public void DarkModeWithMissingValueBecomesDark()
    {
        var reg = new Registry();
        new DarkModeToggle(reg, () => { }).Execute();
        Assert.Equal(0, reg.Values["AppsUseLightTheme"]);
    }

    [Fact]
    public void SwitchTileMirrorsStateAndRefreshesAfterClick()
    {
        var fake = new Fake(ToggleKind.Switch, false);
        var tile = new ToggleTile(fake, time);
        tile.Refresh();
        Assert.False(tile.State);
        Assert.True(tile.Click());
        Assert.True(tile.State);
        Assert.Equal(1, fake.Runs);
    }

    [Fact]
    public void SwitchWithUnknownStateIsUnavailable()
    {
        var tile = new ToggleTile(new Fake(ToggleKind.Switch, null), time);
        tile.Refresh();
        Assert.False(tile.IsAvailable);
        Assert.True(new ToggleTile(new Fake(ToggleKind.Action), time).IsAvailable);
    }

    [Fact]
    public void ConfirmTileNeedsSecondClickWithinThreeSeconds()
    {
        var fake = new Fake(ToggleKind.Confirm);
        var tile = new ToggleTile(fake, time);
        Assert.False(tile.Click());
        Assert.True(tile.IsArmed);
        Assert.Equal(0, fake.Runs);
        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.True(tile.Click());
        Assert.False(tile.IsArmed);
        Assert.Equal(1, fake.Runs);
    }

    [Fact]
    public void ConfirmTileRearmsWhenWindowPassed()
    {
        var fake = new Fake(ToggleKind.Confirm);
        var tile = new ToggleTile(fake, time);
        tile.Click();
        time.Advance(TimeSpan.FromSeconds(3.5));
        Assert.False(tile.Click());   // too late: arms again instead of running
        Assert.Equal(0, fake.Runs);
        Assert.True(tile.IsArmed);
        tile.Disarm();
        Assert.False(tile.IsArmed);
    }

    [Fact]
    public void FailureIsReportedNotThrown()
    {
        var fake = new Fake(ToggleKind.Action) { Throw = true };
        var tile = new ToggleTile(fake, time);
        Assert.False(tile.Click());
        Assert.Equal("nope", tile.Error);
    }

    [Fact]
    public void CatalogListsEveryPlannedTile()
    {
        var ids = WindowsToggles.Create().Select(t => t.Id).ToArray();
        Assert.Equal(["darkmode", "desktopicons", "mic", "lock", "snip", "recyclebin", "nightlight", "bluetooth", "focus", "display"], ids);
    }
}
