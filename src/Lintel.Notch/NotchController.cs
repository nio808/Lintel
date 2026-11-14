// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Settings;
using Lintel.Notch.Layout;
using Lintel.Notch.Motion;
using Lintel.Notch.State;
using Lintel.Notch.Views;
using Lintel.Notch.Window;

namespace Lintel.Notch;

/// <summary>
/// INotchShell: owns the window, the state machine, the notice queue, sections/activities from running modules,
/// hotkeys notch.toggle / notch.hide, display/fullscreen tracking and settings. Create and use on the UI thread
/// (ShowNotice/Expand/Collapse/DismissNotice may be called from any thread).
/// </summary>
internal sealed class NotchController : INotchShell
{
    private const string ToggleHotkey = "notch.toggle", HideHotkey = "notch.hide";
    private static readonly TimeSpan RemoteGrace = TimeSpan.FromSeconds(4);

    private readonly NotchDependencies deps;
    private readonly ISettingsStore settings;
    private readonly Dispatcher ui;
    private readonly NotchStateMachine machine = new();
    private readonly NoticeQueue notices = new();
    private readonly Animator animator = new();
    private readonly NotchView view;
    private readonly DispatcherTimer dwell, grace, dragDwell, poll, noticeTimer, pointerDisplay;
    private readonly List<CompactActivity> activities = [];
    private readonly Dictionary<CompactActivity, (FrameworkElement? Leading, FrameworkElement? Trailing)> activityViews = [];
    private List<NotchSection> sections = [];
    private NotchWindow? window;
    private ForegroundWatcher? watcher;
    private IModuleManager? modules;
    private DisplayInfo? display;
    private IntPtr pendingMonitor;
    private string? currentSection;
    private string? pendingSection;
    private IDataObject? dragData;
    private bool dragActive;
    private bool shown;
    private bool shutDown;
    private bool resizeQueued;
    private bool restoreFocus = true;
    private int wheel;
    private DateTime lastPage;
    private TimeSpan graceDelay = TimeSpan.FromMilliseconds(350);
    private TimeSpan dwellDelay = TimeSpan.FromMilliseconds(120);
    private (NotchState State, string? Section, bool Keyboard) published = (NotchState.Hidden, null, false);
    private NotchState rendered = NotchState.Hidden;

    public NotchController(NotchDependencies deps)
    {
        this.deps = deps;
        settings = deps.Settings;
        ui = Dispatcher.CurrentDispatcher;
        view = new NotchView(animator, deps.Log);
        dwell = Timer(120, () => Fire(NotchInput.DwellElapsed, () => pendingSection ??= view.Compact.HoveredTarget));
        grace = Timer(350, () =>
        {
            machine.Blocked = IsBlocked();
            Fire(NotchInput.GraceElapsed);
        });
        dragDwell = Timer(250, () => Fire(NotchInput.DragDwellElapsed, () => pendingSection = DropTarget()));
        poll = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(100) };
        poll.Tick += (_, _) => Poll();
        noticeTimer = Timer(4000, () =>
        {
            if (notices.Advance())
                PresentNotice();
        });
        pointerDisplay = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        pointerDisplay.Tick += (_, _) => FollowPointer();

        WireView();
        ReadSettings();
        settings.Changed += OnSettingChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public NotchState State => published.State;
    public bool IsKeyboardMode => published.Keyboard;
    public string? VisibleSectionId => published.Section;
    public bool IsUserHidden => machine.UserHidden;

    public event EventHandler? StateChanged;

    // ── INotchShell ────────────────────────────────────────────────────────────────────────────

    public void Attach(IModuleManager moduleManager)
    {
        if (modules is not null)
            modules.Changed -= OnModulesChanged;
        modules = moduleManager;
        modules.Changed += OnModulesChanged;
        Rebuild();
    }

    public void Show()
    {
        if (shown || shutDown)
            return;
        shown = true;
        EnsureWindow();
        Reposition();
        deps.Hotkeys.Register(ToggleHotkey, "Ctrl+Alt+N", "Open the notch for typing / close it", () => Fire(NotchInput.HotkeyToggle));
        deps.Hotkeys.Register(HideHotkey, "Ctrl+Alt+H", "Hide or show the notch", () => SetUserHidden(!machine.UserHidden));
        watcher!.Start(machine.Policy != FullscreenPolicy.Stay);
        EvaluateFullscreen();
        Fire(NotchInput.RestChanged);
    }

    public void Expand(string? sectionId = null, bool keyboard = false) => OnUi(() =>
    {
        if (!shown)
            return;
        var target = Find(sectionId)?.Id;
        if (machine.IsExpanded && target is not null)
            SelectSection(target, animate: true);
        else
            pendingSection = target;
        Fire(keyboard ? NotchInput.ExpandKeyboardRequested : NotchInput.ExpandRequested);
    });

    public void Collapse() => OnUi(() => Fire(NotchInput.CollapseRequested));

    public void ShowNotice(NotchNotice notice) => OnUi(() =>
    {
        if (shutDown)
            return;
        if (!shown || machine.Rest == NotchState.Hidden)
        {
            if (notice.BalloonIfHidden)
                deps.ShowBalloon(notice.Title, notice.Message ?? "");
            return;
        }
        if (notices.Enqueue(notice))
            PresentNotice();
    });

    public void DismissNotice(string id) => OnUi(() =>
    {
        if (notices.Dismiss(id))
            PresentNotice();
    });

    public void SetUserHidden(bool hidden) => OnUi(() =>
    {
        settings.Set(SettingKeys.NotchUserHidden, hidden);
        if (machine.UserHidden == hidden)
            return;
        machine.UserHidden = hidden;
        Fire(NotchInput.RestChanged);
    });

    public async Task CaptureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var backdrop = new LinearGradientBrush(Color.FromRgb(0x3B, 0x41, 0x52), Color.FromRgb(0x24, 0x28, 0x33), 90);
        backdrop.Freeze();
        void Save(string name, NotchState state)
        {
            view.Render(state, animate: false);
            var shape = view.Target.Bounds;
            var margin = state == NotchState.Expanded ? 40 : 56;
            var crop = new Rect(Math.Max(0, shape.Left - margin), 0, Math.Min(NotchMetrics.CanvasWidth, shape.Width + 2 * margin), Math.Min(NotchMetrics.CanvasHeight, shape.Bottom + margin * 0.75));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(view.Snapshot(crop, 2, backdrop)));
            using var file = File.Create(Path.Combine(directory, name));
            encoder.Save(file);
        }

        var visibleBefore = view.Panel.Host.CurrentId;
        view.Panel.Host.SetVisible(false);
        Save("collapsed.png", NotchState.Collapsed);
        Save("sliver.png", NotchState.Sliver);
        if (notices.Current is { } notice)
        {
            view.Notice.Set(notice);
            Save("notice.png", NotchState.Notice);
        }
        var first = true;
        foreach (var section in sections.ToList())
        {
            view.Panel.Show(section, 0, false);
            view.Panel.Host.SetVisible(true);
            view.Render(NotchState.Expanded, animate: false);
            await Task.Delay(first ? 1500 : 400);   // let views fetch/sample once
            first = false;
            Save($"section-{section.Id}.png", NotchState.Expanded);
            view.Panel.Host.SetVisible(false);
        }
        currentSection = Find(visibleBefore)?.Id ?? currentSection;
        view.Panel.Show(Find(currentSection), 0, false);
        rendered = (NotchState)(-1);
        Sync(animate: false);
    }

    public void Shutdown()
    {
        if (shutDown)
            return;
        shutDown = true;
        if (machine.IsKeyboard)
            Fire(NotchInput.KeyboardExit);
        foreach (var timer in new[] { dwell, grace, dragDwell, poll, noticeTimer, pointerDisplay })
            timer.Stop();
        view.Panel.Host.SetVisible(false);
        deps.Hotkeys.Unregister(ToggleHotkey);
        deps.Hotkeys.Unregister(HideHotkey);
        watcher?.Dispose();
        settings.Changed -= OnSettingChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        if (modules is not null)
            modules.Changed -= OnModulesChanged;
        foreach (var activity in activities)
            activity.PropertyChanged -= OnActivityChanged;
        window?.Close();
        window = null;
        published = (NotchState.Hidden, null, false);
    }

    // ── State ──────────────────────────────────────────────────────────────────────────────────

    private void Fire(NotchInput input, Action? before = null)
    {
        if (shutDown && input != NotchInput.KeyboardExit)
            return;
        before?.Invoke();
        var effects = machine.Fire(input);
        // Opened by a module/API while the pointer is elsewhere: give the user time to get there.
        if (effects.HasFlag(NotchEffects.StartGrace))
            grace.Interval = input == NotchInput.ExpandRequested ? RemoteGrace : graceDelay;
        if (effects.HasFlag(NotchEffects.StartDwell))
            dwell.Interval = dwellDelay;
        Restart(dwell, effects, NotchEffects.StartDwell, NotchEffects.CancelDwell);
        Restart(grace, effects, NotchEffects.StartGrace, NotchEffects.CancelGrace);
        Restart(dragDwell, effects, NotchEffects.StartDragDwell, NotchEffects.CancelDragDwell);
        if (effects.HasFlag(NotchEffects.ExitKeyboard))
            window?.ExitKeyboardMode(restoreFocus);   // synchronous: the previous app is foreground before Collapse() returns
        if (effects.HasFlag(NotchEffects.EnterKeyboard))
        {
            window?.ShowQuietly();
            window?.EnterKeyboardMode();               // before Sync: OnShown runs with the window active
        }
        Sync(animate: true);
        if (effects.HasFlag(NotchEffects.EnterKeyboard))
            ui.BeginInvoke(DispatcherPriority.Input, FocusSection);
    }

    private static void Restart(DispatcherTimer timer, NotchEffects effects, NotchEffects start, NotchEffects cancel)
    {
        if (effects.HasFlag(cancel) || effects.HasFlag(start))
            timer.Stop();
        if (effects.HasFlag(start))
            timer.Start();
    }

    /// <summary>Bring the visuals, lifecycle, timers and published state in line with the machine.</summary>
    private void Sync(bool animate)
    {
        var state = shown && !shutDown ? machine.State : NotchState.Hidden;
        var previous = rendered;
        if (state == NotchState.Expanded && previous != NotchState.Expanded)
        {
            SelectSection(pendingSection ?? currentSection, animate: false);
            pendingSection = null;
        }
        rendered = state;
        if (state != NotchState.Hidden)
            window?.ShowQuietly();
        view.Panel.Pinned = machine.IsPinned;
        view.Render(state, animate && window?.IsShown == true);
        // After Render made the panel visible, so a view can take focus in OnShown (keyboard mode).
        view.Panel.Host.SetVisible(state == NotchState.Expanded);
        poll.IsEnabled = state == NotchState.Expanded;
        pointerDisplay.IsEnabled = shown && state is NotchState.Collapsed or NotchState.Sliver
            && string.Equals(settings.Get(SettingKeys.NotchDisplay, "primary"), "pointer", StringComparison.OrdinalIgnoreCase);

        if (previous == NotchState.Expanded && state != NotchState.Expanded)
        {
            SyncHoverAfterCollapse();
            if (notices.Current is not null && !machine.IsNoticeShowing)
                ui.BeginInvoke(DispatcherPriority.Background, PresentNotice);
        }
        Publish(state);
    }

    private void Publish(NotchState state)
    {
        var next = (state, state == NotchState.Expanded ? currentSection : null, machine.IsKeyboard && state == NotchState.Expanded);
        if (next == published)
            return;
        published = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncHoverAfterCollapse()
    {
        if (!machine.IsHovering || window is null)
            return;
        var rect = ScreenRect(NotchMetrics.HitRect(machine.State, view.Target));
        if (Displays.Pointer() is not { } p || !rect.Contains(p.X, p.Y))
            ui.BeginInvoke(DispatcherPriority.Input, () => Fire(NotchInput.PointerLeave));
    }

    // ── Sections and activities ────────────────────────────────────────────────────────────────

    private void OnModulesChanged(object? sender, EventArgs e) => OnUi(Rebuild);

    private void Rebuild()
    {
        if (modules is null || shutDown)
            return;
        var running = modules.Running;
        var all = new List<NotchSection>();
        foreach (var module in running)
            foreach (var section in Safe(() => module.Sections, module))
                if (all.All(s => s.Id != section.Id))   // two modules may share a section id ("tools"): shown once
                    all.Add(section);
        sections = all.Select((s, i) => (s, i)).OrderBy(x => x.s.Order).ThenBy(x => x.i).Select(x => x.s).ToList();
        view.Panel.Host.Retain(sections.Select(s => s.Id).ToHashSet());
        view.Panel.SetSections(sections);
        if (Find(currentSection) is null)
        {
            currentSection = (Find(settings.Get<string?>(SettingKeys.NotchLastSection, null)) ?? sections.FirstOrDefault())?.Id;
            view.Panel.Show(Find(currentSection), 0, false);
            if (machine.IsExpanded)
                Sync(animate: true);
        }

        var next = running.SelectMany(m => Safe(() => m.Activities, m)).Distinct().ToList();
        foreach (var gone in activities.Except(next).ToList())
        {
            gone.PropertyChanged -= OnActivityChanged;
            activityViews.Remove(gone);
        }
        foreach (var added in next.Except(activities))
            added.PropertyChanged += OnActivityChanged;
        activities.Clear();
        activities.AddRange(next);
        UpdateActivities();
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompactActivity.IsActive) or null)
            OnUi(UpdateActivities);
    }

    private void UpdateActivities()
    {
        var (left, right) = ActivitySelector.Select(activities, a => ViewsOf(a).Trailing is not null);
        view.Compact.Show(
            left is { } l ? View(l) : null,
            left?.Activity.TargetSectionId,
            right is { } r ? View(r) : null,
            right?.Activity.TargetSectionId,
            animate: window?.IsShown == true);
        QueueResize();
    }

    private FrameworkElement? View(WingSlot slot) => slot.Trailing ? ViewsOf(slot.Activity).Trailing : ViewsOf(slot.Activity).Leading;

    private (FrameworkElement? Leading, FrameworkElement? Trailing) ViewsOf(CompactActivity activity)
    {
        if (activityViews.TryGetValue(activity, out var views))
            return views;
        FrameworkElement? leading = null, trailing = null;
        try
        {
            leading = activity.CreateLeading();
            trailing = activity.CreateTrailing();
        }
        catch (Exception e)
        {
            deps.Log.Error($"Activity {activity.Id}: creating its pill view failed", e);
        }
        return activityViews[activity] = (leading, trailing);
    }

    private void QueueResize()
    {
        if (resizeQueued)
            return;
        resizeQueued = true;
        ui.BeginInvoke(DispatcherPriority.Render, () =>
        {
            resizeQueued = false;
            if (rendered is NotchState.Collapsed or NotchState.Notice)
                view.Render(rendered, window?.IsShown == true);
        });
    }

    private void SelectSection(string? id, bool animate)
    {
        var section = Find(id) ?? sections.FirstOrDefault();
        var oldIndex = sections.FindIndex(s => s.Id == currentSection);
        var newIndex = section is null ? -1 : sections.IndexOf(section);
        currentSection = section?.Id;
        view.Panel.Show(section, Math.Sign(newIndex - oldIndex), animate && machine.IsExpanded);
        if (section is not null)
            settings.Set(SettingKeys.NotchLastSection, section.Id);
        if (machine.IsExpanded && rendered == NotchState.Expanded)
        {
            view.Render(NotchState.Expanded, animate && window?.IsShown == true);
            Publish(NotchState.Expanded);
        }
    }

    private void Page(int delta)
    {
        if (!machine.IsExpanded || sections.Count < 2)
            return;
        var index = Math.Max(0, sections.FindIndex(s => s.Id == currentSection));
        var next = Math.Clamp(index + delta, 0, sections.Count - 1);
        if (next != index)
            SelectSection(sections[next].Id, animate: true);
    }

    private NotchSection? Find(string? id) => id is null ? null : sections.FirstOrDefault(s => s.Id == id);

    private string? DropTarget()
    {
        if (dragData is null)
            return null;
        foreach (var section in sections)
        {
            try
            {
                if (section.AcceptsDrop?.Invoke(dragData) == true)
                    return section.Id;
            }
            catch (Exception e)
            {
                deps.Log.Warn($"Section {section.Id}: AcceptsDrop failed", e);
            }
        }
        return null;
    }

    private void FocusSection()
    {
        if (window?.IsKeyboardMode != true || view.Panel.IsKeyboardFocusWithin)
            return;
        view.Panel.Host.CurrentView?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // ── Notices ────────────────────────────────────────────────────────────────────────────────

    private void PresentNotice()
    {
        noticeTimer.Stop();
        var notice = notices.Current;
        if (notice is null)
        {
            if (machine.IsNoticeShowing)
                Fire(NotchInput.NoticeEnded);
            return;
        }
        if (machine.IsExpanded)
            return;   // shown after the panel closes
        view.Notice.Set(notice);
        if (notice.Sound)
            NativeMethods.MessageBeep(notice.Tone is NoticeTone.Warning or NoticeTone.Critical ? NativeMethods.MB_ICONEXCLAMATION : NativeMethods.MB_ICONASTERISK);
        if (notice.Duration > TimeSpan.Zero)
        {
            noticeTimer.Interval = notice.Duration;
            noticeTimer.Start();
        }
        if (machine.IsNoticeShowing)
            view.Render(rendered, window?.IsShown == true);   // replaced in place: re-target the width
        else
            Fire(NotchInput.NoticeShown);
    }

    private void OnNoticeClicked()
    {
        var notice = notices.Current;
        if (notice is null)
            return;
        notices.Advance();
        try
        {
            notice.OnClick?.Invoke();
        }
        catch (Exception e)
        {
            deps.Log.Warn($"Notice {notice.Id}: click handler failed", e);
        }
        if (notice.TargetSectionId is not null)
        {
            noticeTimer.Stop();
            Fire(NotchInput.NoticeEnded, () => pendingSection = Find(notice.TargetSectionId)?.Id);
            Fire(NotchInput.ExpandRequested);
        }
        else
        {
            PresentNotice();
        }
    }

    // ── Window, display, fullscreen ────────────────────────────────────────────────────────────

    private void EnsureWindow()
    {
        if (window is not null)
            return;
        var mode = settings.Get(SettingKeys.NotchRenderMode, "auto");
        // DWM mode is verified on Windows 11; Windows 10 uses layered unless "dwm" is chosen.
        var layered = deps.Args.Layered || mode == "layered" || (mode != "dwm" && Environment.OSVersion.Version.Build < 22000);
        deps.Log.Info($"Notch render mode: {(layered ? "layered" : "dwm")}");
        window = new NotchWindow(layered) { Content = view };
        window.DisplayChanged += Reposition;
        window.HorizontalWheel += OnHorizontalWheel;
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.KeyDown += OnKeyDown;
        window.Deactivated += (_, _) =>
        {
            if (!machine.IsKeyboard)
                return;
            restoreFocus = false;   // the user clicked elsewhere: focus is already where they want it
            Fire(NotchInput.KeyboardExit);
            restoreFocus = true;
        };
        watcher = new ForegroundWatcher(() => window?.Handle ?? IntPtr.Zero);
        watcher.Changed += OnForegroundChanged;
        // Initialize the HWND without showing: Show() happens on the first visible state.
        new WindowInteropHelper(window).EnsureHandle();
    }

    private void Reposition()
    {
        if (window is null || shutDown)
            return;
        var displays = Displays.Enumerate();
        display = Displays.Choose(displays, settings.Get(SettingKeys.NotchDisplay, "primary"), Displays.Pointer());
        if (display is null)
            return;
        window.Place(Displays.WindowRect(display));
        view.PixelFactor = display.DpiScale;
        UpdateRegion();
        if (!view.IsAnimating)
            view.Render(rendered, false);
        EvaluateFullscreen();
    }

    private void UpdateRegion()
    {
        if (window is null)
            return;
        window.SetRegion(NotchMetrics.ToPixels(view.HitRect, window.DpiScale));
    }

    private PixelRect ScreenRect(Rect dip)
    {
        if (window is null)
            return default;
        var local = NotchMetrics.ToPixels(dip, window.DpiScale);
        var origin = window.Placement;
        return new(local.Left + origin.Left, local.Top + origin.Top, local.Right + origin.Left, local.Bottom + origin.Top);
    }

    private void OnForegroundChanged()
    {
        if (window is null || shutDown)
            return;
        if (window.IsShown && !window.IsKeyboardMode)
            window.ReassertTopmost();
        EvaluateFullscreen();
    }

    private void EvaluateFullscreen()
    {
        if (watcher is null || display is null)
            return;
        var fullscreen = machine.Policy != FullscreenPolicy.Stay && watcher.Evaluate(display);
        if (fullscreen == machine.Fullscreen)
            return;
        machine.Fullscreen = fullscreen;
        deps.Log.Info($"Fullscreen app {(fullscreen ? "detected" : "gone")} on {display.Device}");
        Fire(NotchInput.RestChanged);
    }

    private void FollowPointer()
    {
        if (display is null || Displays.Pointer() is not { } p)
            return;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = p.X, Y = p.Y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == display.Handle)
        {
            pendingMonitor = IntPtr.Zero;
            return;
        }
        // two 1 s samples on the other display (≥ 0.8 s dwell) instead of a faster poll, to keep idle timers ≥ 1 s.
        if (pendingMonitor == monitor)
        {
            pendingMonitor = IntPtr.Zero;
            Reposition();
        }
        else
        {
            pendingMonitor = monitor;
        }
    }

    private void Poll()
    {
        if (!machine.IsExpanded || window is null)
            return;
        var bounds = view.Target.Bounds;
        var inside = Displays.Pointer() is { } p
            && ScreenRect(new Rect(bounds.Left - NotchMetrics.HoverSlop, 0, bounds.Width + 2 * NotchMetrics.HoverSlop, bounds.Bottom + NotchMetrics.HoverSlop)).Contains(p.X, p.Y);
        machine.Blocked = IsBlocked();
        if (inside != machine.IsHovering)
            Fire(inside ? NotchInput.PointerEnter : NotchInput.PointerLeave);
    }

    /// <summary>Collapse blockers besides pin/drag/keyboard: a mouse button held (slider drag) or a popup open.</summary>
    private static bool IsBlocked() =>
        Mouse.LeftButton == MouseButtonState.Pressed || Mouse.Captured is not null
        || PresentationSource.CurrentSources.OfType<HwndSource>().Any(s => s.RootVisual?.GetType().Name == "PopupRoot" && NativeMethods.IsWindowVisible(s.Handle));

    // ── Input wiring ───────────────────────────────────────────────────────────────────────────

    private void WireView()
    {
        view.HitRectChanged += UpdateRegion;
        view.Settled += () =>
        {
            if (rendered == NotchState.Hidden)
                window?.HideQuietly();   // last frame drawn was empty, so a later re-show cannot flash
        };
        view.MouseEnter += (_, _) =>
        {
            if (!machine.IsExpanded)
                Fire(NotchInput.PointerEnter);
        };
        view.MouseLeave += (_, _) =>
        {
            if (!machine.IsExpanded)
                Fire(NotchInput.PointerLeave);
        };
        view.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled || machine.IsExpanded)
                return;
            e.Handled = true;
            Fire(NotchInput.Click);
        };
        view.Compact.WingClicked += target =>
        {
            pendingSection = Find(target)?.Id;
            Fire(NotchInput.Click);
        };
        view.Compact.ContentSizeChanged += QueueResize;
        view.Notice.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OnNoticeClicked();
        };
        view.Panel.TabClicked += id => SelectSection(id, animate: true);
        view.Panel.PinClicked += () =>
        {
            Fire(NotchInput.PinToggle);
            view.Panel.Pinned = machine.IsPinned;
        };
        view.Panel.SettingsClicked += () =>
        {
            Fire(NotchInput.CollapseRequested);
            deps.OpenSettings();
        };
        // A text box clicked inside a section enters keyboard mode.
        view.Panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!machine.IsKeyboard && IsInTextBox(e.OriginalSource as DependencyObject))
                Fire(NotchInput.KeyboardEnter);
        };
        view.DragEnter += (_, e) =>
        {
            dragData = e.Data;
            if (dragActive)
                return;
            dragActive = true;
            Fire(NotchInput.DragEnter);
        };
        view.DragOver += (_, e) =>
        {
            if (!e.Handled && !machine.IsExpanded)
            {
                e.Effects = DropTarget() is null ? DragDropEffects.None : DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        view.DragLeave += (_, _) => ui.BeginInvoke(DispatcherPriority.Input, () =>
        {
            // DragLeave also fires between child elements: only a pointer outside the hit area ends the drag.
            if (dragActive && (Displays.Pointer() is not { } p || !ScreenRect(view.HitRect).Contains(p.X, p.Y)))
                EndDrag();
        });
        view.Drop += (_, _) => ui.BeginInvoke(DispatcherPriority.Input, EndDrag);
        view.ContextMenu = BuildMenu();
        view.ContextMenuOpening += (_, e) =>
        {
            // Inside a section body the section owns right-click; the pill and the header get the notch menu.
            if (machine.IsExpanded && e.OriginalSource is DependencyObject source && IsWithin(source, view.Panel.Host))
                e.Handled = true;
        };
    }

    private void EndDrag()
    {
        if (!dragActive)
            return;
        dragActive = false;
        dragData = null;
        Fire(NotchInput.DragLeave);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Resources.MergedDictionaries.Add(NotchTheme.Create());
        var hide = new MenuItem { Header = "Hide notch" };
        hide.Click += (_, _) => SetUserHidden(true);
        var pin = new MenuItem { Header = "Pin open", IsCheckable = true };
        pin.Click += (_, _) =>
        {
            Fire(NotchInput.PinToggle);
            view.Panel.Pinned = machine.IsPinned;
            if (machine.IsPinned && !machine.IsExpanded)
                Fire(NotchInput.ExpandRequested);
        };
        var open = new MenuItem { Header = "Settings" };
        open.Click += (_, _) => deps.OpenSettings();
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => deps.Quit();
        menu.Items.Add(hide);
        menu.Items.Add(pin);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(quit);
        menu.Opened += (_, _) => pin.IsChecked = machine.IsPinned;
        return menu;
    }

    private void OnHorizontalWheel(int delta)
    {
        if (!machine.IsExpanded)
            return;
        if ((DateTime.UtcNow - lastPage).TotalMilliseconds < 350)
        {
            wheel = 0;
            return;
        }
        wheel += delta;
        if (Math.Abs(wheel) < 60)
            return;
        Page(Math.Sign(wheel));
        wheel = 0;
        lastPage = DateTime.UtcNow;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!machine.IsKeyboard || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab)
            Page(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        else if (key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            var index = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
            if (index < sections.Count)
                SelectSection(sections[index].Id, animate: true);
        }
        else if (key == Key.OemComma)
        {
            Fire(NotchInput.CollapseRequested);
            deps.OpenSettings();
        }
        else
            return;
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !machine.IsKeyboard)
            return;
        e.Handled = true;
        Fire(NotchInput.KeyboardExit);
    }

    // ── Settings ───────────────────────────────────────────────────────────────────────────────

    private void ReadSettings()
    {
        machine.ExpandOnHover = settings.Get(SettingKeys.NotchExpandOn, "hover") != "click";
        machine.Policy = settings.Get(SettingKeys.NotchFullscreen, "hide") switch
        {
            "shrink" => FullscreenPolicy.Shrink,
            "stay" => FullscreenPolicy.Stay,
            _ => FullscreenPolicy.Hide,
        };
        machine.UserHidden = settings.Get(SettingKeys.NotchUserHidden, false);
        // Intervals apply on the next start (assigning DispatcherTimer.Interval restarts a running timer).
        dwellDelay = TimeSpan.FromMilliseconds(Math.Clamp(settings.Get(SettingKeys.NotchHoverDelayMs, 120), 0, 2000));
        graceDelay = TimeSpan.FromMilliseconds(Math.Clamp(settings.Get(SettingKeys.NotchCollapseDelayMs, 350), 50, 5000));
        view.ReducedMotion = !SystemParameters.ClientAreaAnimation;
    }

    /// <summary>Behaviour settings only; the look is fixed by design.</summary>
    private static readonly HashSet<string> WatchedKeys =
    [
        SettingKeys.NotchDisplay, SettingKeys.NotchExpandOn, SettingKeys.NotchHoverDelayMs, SettingKeys.NotchCollapseDelayMs,
        SettingKeys.NotchFullscreen, SettingKeys.NotchRenderMode, SettingKeys.NotchUserHidden,
    ];

    private void OnSettingChanged(string key)
    {
        if (!WatchedKeys.Contains(key))
            return;
        OnUi(() =>
        {
            if (shutDown)
                return;
            ReadSettings();
            switch (key)
            {
                case SettingKeys.NotchDisplay:
                    Reposition();
                    break;
                case SettingKeys.NotchFullscreen:
                    watcher?.Start(machine.Policy != FullscreenPolicy.Stay);
                    EvaluateFullscreen();
                    break;
                case SettingKeys.NotchRenderMode:
                    deps.Log.Info("notch.renderMode changes apply on the next start");
                    break;
            }
            Fire(NotchInput.RestChanged);
        });
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            OnUi(() => view.ReducedMotion = !SystemParameters.ClientAreaAnimation);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private DispatcherTimer Timer(int milliseconds, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tick();
        };
        return timer;
    }

    private void OnUi(Action action)
    {
        if (ui.CheckAccess())
            action();
        else
            ui.BeginInvoke(action);
    }

    private IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> read, INotchModule module)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            deps.Log.Error($"Module {module.Manifest.Id}: reading its notch content failed", e);
            return [];
        }
    }

    private static bool IsInTextBox(DependencyObject? source)
    {
        for (var node = source; node is not null; node = Parent(node))
            if (node is TextBoxBase or PasswordBox)
                return true;
        return false;
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (var node = source; node is not null; node = Parent(node))
            if (node == ancestor)
                return true;
        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
