// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;
using Lintel.Core.Services;

namespace Lintel.Core.Notch;

/// <summary>Headless INotchShell for tests and Preview: tracks state, records notices, logs every call.</summary>
public sealed class NullNotchShell(IAppLog? log = null) : INotchShell
{
    private readonly List<NotchNotice> notices = [];

    public NotchState State { get; private set; } = NotchState.Hidden;
    public bool IsKeyboardMode { get; private set; }
    public string? VisibleSectionId { get; private set; }
    public bool IsUserHidden { get; private set; }
    public IModuleManager? Modules { get; private set; }

    /// <summary>Every notice passed to ShowNotice, oldest first.</summary>
    public IReadOnlyList<NotchNotice> Notices
    {
        get
        {
            lock (notices)
                return notices.ToList();
        }
    }

    public event EventHandler? StateChanged;

    public void Attach(IModuleManager modules)
    {
        Modules = modules;
        log?.Info("NullNotchShell.Attach");
    }

    public void Show()
    {
        log?.Info("NullNotchShell.Show");
        SetState(IsUserHidden ? NotchState.Hidden : NotchState.Collapsed, null, false);
    }

    public void Expand(string? sectionId = null, bool keyboard = false)
    {
        log?.Info($"NullNotchShell.Expand({sectionId ?? "<last>"}, keyboard: {keyboard})");
        SetState(NotchState.Expanded, sectionId ?? "", keyboard);
    }

    public void Collapse()
    {
        log?.Info("NullNotchShell.Collapse");
        SetState(IsUserHidden ? NotchState.Hidden : NotchState.Collapsed, null, false);
    }

    public void ShowNotice(NotchNotice notice)
    {
        lock (notices)
            notices.Add(notice);
        log?.Info($"Notice [{notice.Tone}] {notice.Id}: {notice.Title}{(notice.Message is null ? "" : " · " + notice.Message)}");
    }

    public void DismissNotice(string id) => log?.Info($"NullNotchShell.DismissNotice({id})");

    public void SetUserHidden(bool hidden)
    {
        log?.Info($"NullNotchShell.SetUserHidden({hidden})");
        IsUserHidden = hidden;
        SetState(hidden ? NotchState.Hidden : NotchState.Collapsed, null, false);
    }

    public Task CaptureAsync(string directory)
    {
        log?.Info($"NullNotchShell.CaptureAsync({directory})");
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        log?.Info("NullNotchShell.Shutdown");
        SetState(NotchState.Hidden, null, false);
    }

    private void SetState(NotchState state, string? section, bool keyboard)
    {
        if (State == state && VisibleSectionId == section && IsKeyboardMode == keyboard)
            return;
        State = state;
        VisibleSectionId = section;
        IsKeyboardMode = keyboard;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
