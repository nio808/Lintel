// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Lintel.Core.Services;
using Lintel.Media.Logic;

namespace Lintel.Media.Audio;

/// <summary>Reads the current output peak (0-1 linear), preferring the app behind the media source.</summary>
public interface IPeakSource : IDisposable
{
    /// <summary>Peak of the sessions whose executable matches <paramref name="sourceAppId"/>, else the device peak; null when unavailable.</summary>
    float? Read(string? sourceAppId);
}

/// <summary>
/// Drives the equalizer: 30 Hz peak sampling on a private thread, only while at least one consumer is visible and media is playing.
/// Without a peak source (consent missing) it reports a gentle constant level and the bars wobble on their own.
/// </summary>
public sealed class LevelMeter : IDisposable
{
    public const double SyntheticLevel = 0.5;

    private readonly Dispatcher ui;
    private readonly Func<IPeakSource?> createSource;
    private readonly Func<string?> sourceApp;
    private readonly IAppLog log;
    private readonly ManualResetEventSlim wake = new(false);
    private readonly PeakSmoother smoother = new();
    private int consumers;
    private volatile bool playing, stop;
    private Thread? thread;

    public LevelMeter(Dispatcher ui, Func<IPeakSource?> createSource, Func<string?> sourceApp, IAppLog log)
    {
        this.ui = ui;
        this.createSource = createSource;
        this.sourceApp = sourceApp;
        this.log = log;
    }

    /// <summary>UI thread, at most 30 Hz while active.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>A visible equalizer / section asks for samples; dispose when hidden.</summary>
    public IDisposable Acquire()
    {
        Interlocked.Increment(ref consumers);
        Update();
        return new Lease(this);
    }

    public void SetPlaying(bool value)
    {
        playing = value;
        Update();
    }

    public void Dispose()
    {
        stop = true;
        wake.Set();
    }

    private bool Wanted => playing && Volatile.Read(ref consumers) > 0 && !stop;

    private void Update()
    {
        if (!Wanted)
            return;
        if (thread is null)
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "Lintel.Levels" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        wake.Set();
    }

    private void Loop()
    {
        IPeakSource? source = null;
        var created = false;
        try
        {
            while (!stop)
            {
                if (!Wanted)
                {
                    // Idle: release the COM objects and sleep until somebody needs levels again.
                    source?.Dispose();
                    source = null;
                    created = false;
                    smoother.Reset();
                    wake.Reset();
                    if (!Wanted)
                        wake.Wait();
                    continue;
                }
                if (!created)
                {
                    created = true;
                    source = createSource();
                }
                double level;
                try
                {
                    level = source is null ? SyntheticLevel : smoother.Next(MediaMath.PeakToLevel(source.Read(sourceApp()) ?? 0));
                }
                catch (Exception e)
                {
                    log.Warn("Peak read failed", e);
                    source?.Dispose();
                    source = null;
                    level = SyntheticLevel;
                }
                ui.BeginInvoke(() => LevelChanged?.Invoke(level), DispatcherPriority.Background);
                Thread.Sleep(33);
            }
        }
        catch (Exception e)
        {
            log.Warn("Level meter stopped", e);
        }
        finally
        {
            source?.Dispose();
        }
    }

    private sealed class Lease(LevelMeter owner) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                Interlocked.Decrement(ref owner.consumers);
        }
    }
}

/// <summary>Peak via NAudio.Wasapi (sessions + device meter). Create and use on the meter thread only.</summary>
public sealed class WasapiPeakSource : IPeakSource
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    private readonly MMDeviceEnumerator enumerator = new();
    private readonly List<(string Exe, AudioSessionControl Control)> sessions = [];
    private MMDevice? device;
    private DateTime lastRefresh = DateTime.MinValue;

    public float? Read(string? sourceAppId)
    {
        if (DateTime.UtcNow - lastRefresh > Refresh)
            RefreshSessions();
        if (device is null)
            return null;
        float peak = 0;
        var matched = false;
        if (sourceAppId is not null)
        {
            foreach (var (exe, control) in sessions)
            {
                if (!AppNames.AumidMatchesExe(sourceAppId, exe))
                    continue;
                matched = true;
                peak = Math.Max(peak, control.AudioMeterInformation.MasterPeakValue);
            }
        }
        return matched ? peak : device.AudioMeterInformation.MasterPeakValue;
    }

    public void Dispose()
    {
        Clear();
        enumerator.Dispose();
    }

    private void Clear()
    {
        foreach (var (_, control) in sessions)
            control.Dispose();
        sessions.Clear();
        device?.Dispose();
        device = null;
    }

    private void RefreshSessions()
    {
        lastRefresh = DateTime.UtcNow;
        Clear();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            return;
        device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        for (var i = 0; i < manager.Sessions.Count; i++)
        {
            var control = manager.Sessions[i];
            var path = control.IsSystemSoundsSession ? null : ProcessInfo.ImagePath((int)control.GetProcessID);
            if (path is null)
                control.Dispose();
            else
                sessions.Add((Path.GetFileName(path), control));
        }
    }
}
