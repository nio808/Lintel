// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Notch;

namespace Lintel.Notch.State;

public enum FullscreenPolicy { Hide, Shrink, Stay }

public enum NotchInput
{
    PointerEnter, PointerLeave, Click, DwellElapsed, GraceElapsed,
    DragEnter, DragLeave, DragDwellElapsed,
    HotkeyToggle, ExpandRequested, ExpandKeyboardRequested, CollapseRequested,
    KeyboardEnter, KeyboardExit, PinToggle,
    NoticeShown, NoticeEnded,
    RestChanged,   // UserHidden / Fullscreen / Policy changed
}

/// <summary>Timers and focus changes the host must perform after an input.</summary>
[Flags]
public enum NotchEffects
{
    None = 0,
    StartDwell = 1, CancelDwell = 2,
    StartGrace = 4, CancelGrace = 8,
    StartDragDwell = 16, CancelDragDwell = 32,
    EnterKeyboard = 64, ExitKeyboard = 128,
}

/// <summary>
/// Pure notch state machine: inputs in, state + timer/focus effects out. Timers live in the host.
/// Expanded overlays everything; a notice overlays the rest state unless the notch is hidden.
/// </summary>
public sealed class NotchStateMachine
{
    private NotchState lastRest = NotchState.Collapsed;

    public bool ExpandOnHover { get; set; } = true;
    public bool UserHidden { get; set; }
    public bool Fullscreen { get; set; }
    public FullscreenPolicy Policy { get; set; } = FullscreenPolicy.Hide;

    /// <summary>Mouse button down / popup open: checked when the grace timer elapses (re-armed while true).</summary>
    public bool Blocked { get; set; }

    public bool IsExpanded { get; private set; }
    public bool IsKeyboard { get; private set; }
    public bool IsPinned { get; private set; }
    public bool IsHovering { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsNoticeShowing { get; private set; }

    public NotchState Rest =>
        UserHidden || (Fullscreen && Policy == FullscreenPolicy.Hide) ? NotchState.Hidden
        : Fullscreen && Policy == FullscreenPolicy.Shrink ? NotchState.Sliver
        : NotchState.Collapsed;

    public NotchState State =>
        IsExpanded ? NotchState.Expanded
        : Rest == NotchState.Hidden ? NotchState.Hidden
        : IsNoticeShowing ? NotchState.Notice
        : Rest;

    private bool HoldsOpen => IsPinned || IsKeyboard || IsDragging;

    public NotchEffects Fire(NotchInput input)
    {
        switch (input)
        {
            case NotchInput.PointerEnter:
                IsHovering = true;
                if (IsExpanded)
                    return NotchEffects.CancelGrace;
                return ExpandOnHover && Rest != NotchState.Hidden ? NotchEffects.StartDwell : NotchEffects.None;

            case NotchInput.PointerLeave:
                IsHovering = false;
                return NotchEffects.CancelDwell | (IsExpanded && !HoldsOpen ? NotchEffects.StartGrace : NotchEffects.None);

            case NotchInput.DwellElapsed:
                return ExpandOnHover && IsHovering && !IsExpanded && Rest != NotchState.Hidden ? Open(false) : NotchEffects.None;

            case NotchInput.Click:
                return !IsExpanded && Rest != NotchState.Hidden ? Open(false) : NotchEffects.None;

            case NotchInput.GraceElapsed:
                if (!IsExpanded || IsHovering || HoldsOpen)
                    return NotchEffects.None;
                return Blocked ? NotchEffects.StartGrace : Close();

            case NotchInput.DragEnter:
                IsDragging = true;
                return IsExpanded ? NotchEffects.CancelGrace : NotchEffects.StartDragDwell;

            case NotchInput.DragLeave:
                IsDragging = false;
                return NotchEffects.CancelDragDwell | (IsExpanded && !IsHovering && !HoldsOpen ? NotchEffects.StartGrace : NotchEffects.None);

            case NotchInput.DragDwellElapsed:
                return IsDragging && !IsExpanded && Rest != NotchState.Hidden ? Open(false) : NotchEffects.None;

            case NotchInput.HotkeyToggle:
                return IsExpanded ? Close() : Open(true);

            case NotchInput.ExpandRequested:
                return IsExpanded ? NotchEffects.None : Open(false);

            case NotchInput.ExpandKeyboardRequested:
                return !IsExpanded ? Open(true) : IsKeyboard ? NotchEffects.None : EnterKeyboard();

            case NotchInput.CollapseRequested:
                return IsExpanded ? Close() : NotchEffects.None;

            case NotchInput.KeyboardEnter:
                return IsExpanded && !IsKeyboard ? EnterKeyboard() : NotchEffects.None;

            case NotchInput.KeyboardExit:   // Esc, focus lost or hotkey: leave keyboard mode and collapse
                return IsKeyboard ? Close() : NotchEffects.None;

            case NotchInput.PinToggle:
                IsPinned = !IsPinned;
                if (IsPinned)
                    return NotchEffects.CancelGrace;
                return IsExpanded && !IsHovering && !HoldsOpen ? NotchEffects.StartGrace : NotchEffects.None;

            case NotchInput.NoticeShown:
                IsNoticeShowing = true;
                return NotchEffects.None;

            case NotchInput.NoticeEnded:
                IsNoticeShowing = false;
                return NotchEffects.None;

            case NotchInput.RestChanged:
                var was = lastRest;
                lastRest = Rest;
                // Hidden by the user or a fullscreen app while open → close. (Opening while already hidden is allowed: hotkey.)
                return IsExpanded && lastRest == NotchState.Hidden && was != NotchState.Hidden ? Close() : NotchEffects.None;

            default:
                return NotchEffects.None;
        }
    }

    private NotchEffects Open(bool keyboard)
    {
        IsExpanded = true;
        var effects = NotchEffects.CancelDwell | NotchEffects.CancelDragDwell;
        if (keyboard)
            return effects | EnterKeyboard();
        // Opened from afar (API): close again unless the pointer arrives.
        return effects | (IsHovering || HoldsOpen ? NotchEffects.CancelGrace : NotchEffects.StartGrace);
    }

    private NotchEffects EnterKeyboard()
    {
        IsKeyboard = true;
        return NotchEffects.EnterKeyboard | NotchEffects.CancelGrace;
    }

    private NotchEffects Close()
    {
        IsExpanded = false;
        var effects = NotchEffects.CancelGrace;
        if (IsKeyboard)
        {
            IsKeyboard = false;
            effects |= NotchEffects.ExitKeyboard;
        }
        return effects;
    }
}
