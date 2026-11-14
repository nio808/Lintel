// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Notch;
using Lintel.Notch.State;
using static Lintel.Notch.State.NotchEffects;
using static Lintel.Notch.State.NotchInput;

namespace Lintel.Notch.Tests;

public sealed class StateMachineTests
{
    private readonly NotchStateMachine m = new();

    [Fact]
    public void HoverDwellExpandsAndLeaveGraceCollapses()
    {
        Assert.Equal(NotchState.Collapsed, m.State);
        Assert.True(m.Fire(PointerEnter).HasFlag(StartDwell));
        Assert.Equal(NotchState.Collapsed, m.State);
        m.Fire(DwellElapsed);
        Assert.Equal(NotchState.Expanded, m.State);
        Assert.True(m.Fire(PointerLeave).HasFlag(StartGrace));
        Assert.Equal(NotchState.Expanded, m.State);
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void LeavingBeforeTheDwellCancelsIt()
    {
        m.Fire(PointerEnter);
        Assert.True(m.Fire(PointerLeave).HasFlag(CancelDwell));
        m.Fire(DwellElapsed);   // a stale tick changes nothing
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void ReenteringCancelsTheGrace()
    {
        Expand();
        m.Fire(PointerLeave);
        Assert.True(m.Fire(PointerEnter).HasFlag(CancelGrace));
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Expanded, m.State);
    }

    [Fact]
    public void ClickModeIgnoresHover()
    {
        m.ExpandOnHover = false;
        Assert.Equal(None, m.Fire(PointerEnter));
        m.Fire(DwellElapsed);
        Assert.Equal(NotchState.Collapsed, m.State);
        m.Fire(Click);
        Assert.Equal(NotchState.Expanded, m.State);
    }

    [Fact]
    public void PinHoldsTheNotchOpen()
    {
        Expand();
        m.Fire(PinToggle);
        Assert.Equal(None, m.Fire(PointerLeave) & StartGrace);
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Expanded, m.State);
        Assert.True(m.Fire(PinToggle).HasFlag(StartGrace));   // unpinned while away → close after the grace
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void BlockersRearmTheGrace()
    {
        Expand();
        m.Fire(PointerLeave);
        m.Blocked = true;   // slider drag / popup
        Assert.Equal(StartGrace, m.Fire(GraceElapsed));
        Assert.Equal(NotchState.Expanded, m.State);
        m.Blocked = false;
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void DragOverExpandsAfterItsDwellAndHoldsOpen()
    {
        Assert.True(m.Fire(DragEnter).HasFlag(StartDragDwell));
        m.Fire(DragDwellElapsed);
        Assert.Equal(NotchState.Expanded, m.State);
        m.Fire(PointerLeave);
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Expanded, m.State);   // dragging holds it open
        Assert.True(m.Fire(DragLeave).HasFlag(StartGrace));
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void DragLeavingBeforeTheDwellDoesNotExpand()
    {
        m.Fire(DragEnter);
        Assert.True(m.Fire(DragLeave).HasFlag(CancelDragDwell));
        m.Fire(DragDwellElapsed);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void HotkeyEntersAndLeavesKeyboardMode()
    {
        var effects = m.Fire(HotkeyToggle);
        Assert.True(effects.HasFlag(EnterKeyboard));
        Assert.True(m.IsKeyboard);
        Assert.Equal(NotchState.Expanded, m.State);
        m.Fire(PointerLeave);
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Expanded, m.State);   // keyboard mode pins
        Assert.True(m.Fire(HotkeyToggle).HasFlag(ExitKeyboard));
        Assert.False(m.IsKeyboard);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void TextBoxClickEntersKeyboardModeAndEscapeCollapses()
    {
        Expand();
        Assert.True(m.Fire(KeyboardEnter).HasFlag(EnterKeyboard));
        Assert.True(m.Fire(KeyboardExit).HasFlag(ExitKeyboard));
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void CollapseRequestLeavesKeyboardMode()
    {
        m.Fire(ExpandKeyboardRequested);
        Assert.True(m.IsKeyboard);
        var effects = m.Fire(CollapseRequested);
        Assert.True(effects.HasFlag(ExitKeyboard));   // the host gives focus back synchronously
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void ExpandFromAfarClosesUnlessThePointerArrives()
    {
        Assert.True(m.Fire(ExpandRequested).HasFlag(StartGrace));
        m.Fire(PointerEnter);
        m.Fire(GraceElapsed);
        Assert.Equal(NotchState.Expanded, m.State);
    }

    [Theory]
    [InlineData(FullscreenPolicy.Hide, NotchState.Hidden)]
    [InlineData(FullscreenPolicy.Shrink, NotchState.Sliver)]
    [InlineData(FullscreenPolicy.Stay, NotchState.Collapsed)]
    public void FullscreenFollowsThePolicy(FullscreenPolicy policy, NotchState expected)
    {
        m.Policy = policy;
        m.Fullscreen = true;
        m.Fire(RestChanged);
        Assert.Equal(expected, m.State);
        m.Fullscreen = false;
        m.Fire(RestChanged);
        Assert.Equal(NotchState.Collapsed, m.State);
    }

    [Fact]
    public void GoingFullscreenWhileOpenCollapsesAndLeavesKeyboardMode()
    {
        m.Fire(HotkeyToggle);
        m.Fullscreen = true;
        Assert.True(m.Fire(RestChanged).HasFlag(ExitKeyboard));
        Assert.Equal(NotchState.Hidden, m.State);
    }

    [Fact]
    public void HiddenNotchIgnoresHoverButTheHotkeyStillOpensIt()
    {
        m.UserHidden = true;
        m.Fire(RestChanged);
        Assert.Equal(NotchState.Hidden, m.State);
        Assert.Equal(None, m.Fire(PointerEnter));
        m.Fire(HotkeyToggle);
        Assert.Equal(NotchState.Expanded, m.State);
        m.Fire(HotkeyToggle);
        Assert.Equal(NotchState.Hidden, m.State);
    }

    [Fact]
    public void NoticeOverlaysRestButNotHiddenOrExpanded()
    {
        m.Policy = FullscreenPolicy.Shrink;
        m.Fullscreen = true;
        m.Fire(RestChanged);
        m.Fire(NoticeShown);
        Assert.Equal(NotchState.Notice, m.State);
        Expand();
        Assert.Equal(NotchState.Expanded, m.State);
        m.Fire(CollapseRequested);
        Assert.Equal(NotchState.Notice, m.State);
        m.UserHidden = true;
        m.Fire(RestChanged);
        Assert.Equal(NotchState.Hidden, m.State);
        m.UserHidden = false;
        m.Fire(RestChanged);
        m.Fire(NoticeEnded);
        Assert.Equal(NotchState.Sliver, m.State);
    }

    private void Expand()
    {
        m.Fire(PointerEnter);
        m.Fire(DwellElapsed);
        Assert.Equal(NotchState.Expanded, m.State);
    }
}
