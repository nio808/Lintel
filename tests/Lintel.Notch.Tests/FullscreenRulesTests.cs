// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Notch.Layout;
using Lintel.Notch.Window;

namespace Lintel.Notch.Tests;

public sealed class FullscreenRulesTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void BorderlessWindowCoveringTheMonitorIsFullscreen() =>
        Assert.True(FullscreenRules.IsFullscreen(Window("Chrome_WidgetWin_1", Monitor), Monitor));

    [Fact]
    public void MaximizedWindowIsNot() =>
        // A maximized window's rect overhangs the monitor by its frame, but it is zoomed.
        Assert.False(FullscreenRules.IsFullscreen(Window("Notepad", new PixelRect(-8, -8, 1928, 1040), zoomed: true), Monitor));

    [Fact]
    public void MaximizedWithAutoHideTaskbarIsNot() =>
        Assert.False(FullscreenRules.IsFullscreen(Window("Chrome_WidgetWin_1", new PixelRect(-8, -8, 1928, 1088), zoomed: true), Monitor));

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("XamlExplorerHostIslandWindow")]
    public void DesktopAndShellSurfacesAreNot(string className) =>
        Assert.False(FullscreenRules.IsFullscreen(Window(className, Monitor), Monitor));

    [Fact]
    public void SmallerWindowIsNot() =>
        Assert.False(FullscreenRules.IsFullscreen(Window("Game", new PixelRect(0, 0, 1920, 1079)), Monitor));

    [Fact]
    public void FullscreenOnAnotherDisplayIsNot() =>
        Assert.False(FullscreenRules.IsFullscreen(Window("Game", Monitor) with { OnNotchDisplay = false }, Monitor));

    [Theory]
    [InlineData(FullscreenRules.QunsD3DFullscreen)]
    [InlineData(FullscreenRules.QunsPresentation)]
    public void ExclusiveD3DAndPresentationModeCount(int state) =>
        Assert.True(FullscreenRules.IsFullscreen(Window("Game", new PixelRect(0, 0, 800, 600)) with { NotificationState = state }, Monitor));

    [Fact]
    public void HiddenWindowIsNot() =>
        Assert.False(FullscreenRules.IsFullscreen(Window("Game", Monitor) with { Visible = false }, Monitor));

    private static ForegroundInfo Window(string className, PixelRect rect, bool zoomed = false) =>
        new(className, zoomed, true, rect, true, 5 /* QUNS_ACCEPTS_NOTIFICATIONS */);
}
