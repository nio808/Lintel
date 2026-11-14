// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lintel.Tools.Toggles;

public enum ToggleKind
{
    /// <summary>Has an on/off state Windows exposes.</summary>
    Switch,
    /// <summary>Does something once, no state.</summary>
    Action,
    /// <summary>Does something destructive: the first click arms it, a second click within 3 s runs it.</summary>
    Confirm,
    /// <summary>Opens a Windows Settings page.</summary>
    Link,
}

/// <summary>One thing a tile can do. Real implementations are in WindowsToggles; tests use fakes.</summary>
public interface IToggleAction
{
    string Id { get; }
    string Title { get; }
    string Glyph { get; }
    ToggleKind Kind { get; }
    /// <summary>Current state for Switch tiles; null = unknown or not applicable.</summary>
    bool? ReadState();
    void Execute();
}

/// <summary>Tile state + click handling (arm/confirm window, refresh after run).</summary>
public sealed partial class ToggleTile(IToggleAction action, TimeProvider time) : ObservableObject
{
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);
    private DateTimeOffset armedUntil;

    public IToggleAction Action { get; } = action;

    /// <summary>True/false for a Switch, null otherwise or when Windows does not say.</summary>
    [ObservableProperty] private bool? state;
    [ObservableProperty] private bool isArmed;
    [ObservableProperty] private string? error;

    public bool IsAvailable => Action.Kind != ToggleKind.Switch || State is not null;

    public void Refresh()
    {
        State = Action.Kind == ToggleKind.Switch ? SafeRead() : null;
        OnPropertyChanged(nameof(IsAvailable));
    }

    /// <summary>Returns true when the action ran.</summary>
    public bool Click()
    {
        var now = time.GetUtcNow();
        if (Action.Kind == ToggleKind.Confirm && !(IsArmed && now <= armedUntil))
        {
            IsArmed = true;
            armedUntil = now + ConfirmWindow;
            return false;
        }
        IsArmed = false;
        try
        {
            Action.Execute();
            Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return false;
        }
        Refresh();
        return true;
    }

    /// <summary>Called by the view's one-shot timer when the confirm window passes.</summary>
    public void Disarm() => IsArmed = false;

    private bool? SafeRead()
    {
        try { return Action.ReadState(); }
        catch (Exception) { return null; }
    }
}
