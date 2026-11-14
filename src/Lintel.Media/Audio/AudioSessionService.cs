// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Lintel.Core.Services;
using Lintel.Media.Logic;

namespace Lintel.Media.Audio;

public sealed record MixerState(bool HasDevice, string DeviceName, double Master, bool MasterMuted, IReadOnlyList<AppRow> Apps)
{
    public static MixerState Empty { get; } = new(false, "No output device", 0, false, []);
}

/// <summary>What the mixer view and module need from an audio backend (real WASAPI or demo).</summary>
public interface IMixerService : IDisposable
{
    MixerState Current { get; }
    /// <summary>Raised on the UI thread with a fresh snapshot.</summary>
    event Action? Changed;
    /// <summary>Master volume or mute changed by someone other than this service (UI thread).</summary>
    event Action<double, bool>? MasterChangedExternally;
    void Start();
    /// <summary>Visible = poll sessions (2 s) and meters (15 Hz); hidden = idle, zero work.</summary>
    void SetVisible(bool visible);
    void SetMaster(double volume);
    void SetMasterMute(bool muted);
    void SetApp(string key, double volume);
    void SetAppMute(string key, bool muted);
}

/// <summary>
/// Per-app volume mixer on the default render device (NAudio.Wasapi: sessions, volume, mute, peak meter; no capture).
/// All COM use lives on one dedicated MTA thread that sleeps unless the mixer is visible.
/// </summary>
public sealed class AudioSessionService(Dispatcher ui, IAppLog log) : IMixerService
{
    private sealed record Entry(RawSession Raw, AudioSessionControl Control, string Key);

    private static readonly TimeSpan EnumerateEvery = TimeSpan.FromSeconds(2);
    private const int MeterMs = 66;           // 15 Hz
    private const long SelfSetGraceMs = 400;

    private readonly BlockingCollection<Action> queue = [];
    private Thread? thread;
    private volatile bool disposed;
    private long lastSelfSet;

    // Thread-owned state.
    private bool visible;
    private MMDeviceEnumerator? enumerator;
    private MMDevice? device;
    private string? deviceId;
    private List<Entry> entries = [];
    private DateTime lastEnumerate = DateTime.MinValue;
    private bool needsEnumerate = true;
    private AudioEndpointVolumeNotificationDelegate? volumeHandler;
    private AudioEndpointVolume? hooked;

    public MixerState Current { get; private set; } = MixerState.Empty;
    public event Action? Changed;
    public event Action<double, bool>? MasterChangedExternally;

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "Lintel.Mixer" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        queue.Add(() => { Enumerate(); Publish(); });   // first read so the peek has a baseline
    }

    public void SetVisible(bool value) => queue.Add(() =>
    {
        visible = value;
        if (value)
        {
            needsEnumerate = true;
            Publish();
        }
    });

    public void SetMaster(double volume) => Run(() => { if (Volume() is { } v) v.MasterVolumeLevelScalar = (float)Math.Clamp(volume, 0, 1); });
    public void SetMasterMute(bool muted) => Run(() => { if (Volume() is { } v) v.Mute = muted; });
    public void SetApp(string key, double volume) => Run(() => ForApp(key, c => c.SimpleAudioVolume.Volume = (float)Math.Clamp(volume, 0, 1)));
    public void SetAppMute(string key, bool muted) => Run(() => ForApp(key, c => c.SimpleAudioVolume.Mute = muted));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        queue.CompleteAdding();
    }

    private void Run(Action action)
    {
        Interlocked.Exchange(ref lastSelfSet, Environment.TickCount64);
        if (!queue.IsAddingCompleted)
            queue.Add(() => { action(); Publish(); });
    }

    private AudioEndpointVolume? Volume()
    {
        if (device is null)
            Enumerate();
        return device?.AudioEndpointVolume;
    }

    private void ForApp(string key, Action<AudioSessionControl> apply)
    {
        foreach (var entry in entries.Where(e => e.Key == key))
            try { apply(entry.Control); }
            catch (Exception e) { log.Warn("Setting app volume failed", e); needsEnumerate = true; }
    }

    private void Loop()
    {
        try
        {
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var action, visible ? MeterMs : Timeout.Infinite))
                {
                    try { action(); }
                    catch (Exception e) { log.Warn("Mixer command failed", e); }
                    if (queue.Count > 0)
                        continue;
                }
                if (visible && !queue.IsCompleted)
                {
                    try
                    {
                        if (needsEnumerate || DateTime.UtcNow - lastEnumerate > EnumerateEvery)
                            Enumerate();
                        Publish();
                    }
                    catch (Exception e)
                    {
                        log.Warn("Mixer poll failed", e);
                        needsEnumerate = true;
                    }
                }
            }
        }
        finally
        {
            Release();
        }
    }

    private void Enumerate()
    {
        needsEnumerate = false;
        lastEnumerate = DateTime.UtcNow;
        enumerator ??= new MMDeviceEnumerator();
        MMDevice? current = null;
        try
        {
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception e)
        {
            log.Warn("No default audio device", e);
        }

        DisposeEntries();
        if (current is null || current.ID != deviceId)
        {
            Unhook();
            device?.Dispose();
            device = current;
            deviceId = current?.ID;
            Hook();
        }
        else
        {
            current.Dispose();
        }
        if (device is null)
            return;

        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        var list = new List<Entry>();
        for (var i = 0; i < sessions.Count; i++)
        {
            try
            {
                var control = sessions[i];
                var pid = (int)control.GetProcessID;
                var path = pid == 0 ? null : ProcessInfo.ImagePath(pid);
                var raw = new RawSession(pid, path, control.DisplayName, path is null ? null : ProcessInfo.Description(path),
                    0, false, 0, control.State == AudioSessionState.AudioSessionStateActive, control.IsSystemSoundsSession,
                    control.State == AudioSessionState.AudioSessionStateExpired);
                var key = string.IsNullOrEmpty(path) ? "pid:" + pid : path.ToLowerInvariant();
                list.Add(new Entry(raw, control, key));
            }
            catch (Exception e)
            {
                log.Warn("Skipping an audio session", e);
            }
        }
        entries = list;
    }

    private void Publish()
    {
        MixerState state;
        if (device is null)
        {
            state = MixerState.Empty;
        }
        else
        {
            var live = new List<RawSession>(entries.Count);
            foreach (var entry in entries)
            {
                try
                {
                    var volume = entry.Control.SimpleAudioVolume;
                    live.Add(entry.Raw with
                    {
                        Volume = volume.Volume,
                        Muted = volume.Mute,
                        Peak = entry.Control.AudioMeterInformation.MasterPeakValue,
                        IsActive = entry.Control.State == AudioSessionState.AudioSessionStateActive,
                        IsExpired = entry.Control.State == AudioSessionState.AudioSessionStateExpired,
                    });
                }
                catch (Exception)
                {
                    needsEnumerate = true;   // the session died under us
                }
            }
            var rows = AudioGrouping.Group(live).Select(r => r with { Icon = r.ExePath is null ? null : ProcessInfo.IconOf(r.ExePath) }).ToList();
            var master = device.AudioEndpointVolume;
            state = new MixerState(true, device.FriendlyName, master.MasterVolumeLevelScalar, master.Mute, rows);
        }
        ui.BeginInvoke(() =>
        {
            if (disposed)
                return;
            Current = state;
            Changed?.Invoke();
        }, DispatcherPriority.Background);
    }

    private void Hook()
    {
        if (device is null)
            return;
        try
        {
            hooked = device.AudioEndpointVolume;
            volumeHandler = data =>
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastSelfSet) < SelfSetGraceMs)
                    return;
                ui.BeginInvoke(() => MasterChangedExternally?.Invoke(data.MasterVolume, data.Muted));
            };
            hooked.OnVolumeNotification += volumeHandler;
        }
        catch (Exception e)
        {
            log.Warn("Master volume notifications unavailable", e);
        }
    }

    private void Unhook()
    {
        try
        {
            if (hooked is not null && volumeHandler is not null)
                hooked.OnVolumeNotification -= volumeHandler;
        }
        catch (Exception)
        {
            // device gone
        }
        hooked = null;
        volumeHandler = null;
    }

    private void DisposeEntries()
    {
        foreach (var entry in entries)
            try { entry.Control.Dispose(); } catch (Exception) { }
        entries = [];
    }

    private void Release()
    {
        DisposeEntries();
        Unhook();
        device?.Dispose();
        enumerator?.Dispose();
        device = null;
        enumerator = null;
    }
}
