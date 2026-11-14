// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Core.Notch;

public enum NotchState { Hidden, Sliver, Collapsed, Notice, Expanded }

public interface INotchHost
{
    NotchState State { get; }
    bool IsKeyboardMode { get; }
    string? VisibleSectionId { get; }                 // non-null only while Expanded
    event EventHandler? StateChanged;                 // UI thread
    void Expand(string? sectionId = null, bool keyboard = false);
    void Collapse();
    void ShowNotice(NotchNotice notice);              // any thread
    void DismissNotice(string id);
}

public interface INotchShell : INotchHost
{
    void Attach(IModuleManager modules);              // subscribe to Changed; build tabs + activities
    void Show();
    bool IsUserHidden { get; }
    void SetUserHidden(bool hidden);                  // persists notch.userHidden
    Task CaptureAsync(string directory);              // collapsed.png + section-<id>.png for each section
    void Shutdown();
}

public sealed record NotchDependencies(
    ISettingsStore Settings, IAppLog Log, IHotkeyService Hotkeys, AppArgs Args,
    Action OpenSettings, Action Quit, Action<string, string> ShowBalloon);

public sealed record NotchSection
{
    public required string Id { get; init; }                  // "agents", "music", "mixer", "timer", "tools", …
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required Func<FrameworkElement> CreateView { get; init; }   // called once, lazily, UI thread
    public Size PreferredSize { get; init; } = new(560, 260); // content DIP; host clamps to 388–728 × 140–440
    public int Order { get; init; }
    public bool WantsKeyboard { get; init; }                  // enter keyboard mode when opened by hotkey
    public Func<IDataObject, bool>? AcceptsDrop { get; init; } // drag-over routing; the view handles the Drop itself
}

/// <summary>Optional, implemented by a section's root view.</summary>
public interface ISectionLifecycle
{
    void OnShown();
    void OnHidden();
}

public abstract class CompactActivity : ObservableObject
{
    private bool isActive;

    protected CompactActivity(string id, int priority, string? targetSectionId)
    {
        Id = id;
        Priority = priority;
        TargetSectionId = targetSectionId;
    }

    public string Id { get; }
    public int Priority { get; }                              // see ActivitySelector
    public string? TargetSectionId { get; }

    /// <summary>Observable; set on the UI thread.</summary>
    public bool IsActive
    {
        get => isActive;
        set => SetProperty(ref isActive, value);
    }

    /// <summary>≤ 96 × 22 DIP, called once.</summary>
    public abstract FrameworkElement CreateLeading();

    public virtual FrameworkElement? CreateTrailing() => null;
}

public enum NoticeTone { Info, Success, Warning, Critical }

public sealed record NotchNotice
{
    public required string Id { get; init; }                  // same Id replaces a queued/showing notice
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string Glyph { get; init; } = Glyphs.Info;
    public Brush? Accent { get; init; }                       // frozen
    public NoticeTone Tone { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);   // Zero = sticky until clicked
    public string? TargetSectionId { get; init; }             // click expands here
    public Action? OnClick { get; init; }
    public bool Sound { get; init; }
    public bool BalloonIfHidden { get; init; }                // tray balloon when State == Hidden
}
