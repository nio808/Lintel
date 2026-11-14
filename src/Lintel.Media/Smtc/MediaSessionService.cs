// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.Core.Services;
using Lintel.Core.Settings;
using Lintel.Media.Logic;
using Windows.Media;
using Windows.Media.Control;
using WinAppInfo = Windows.ApplicationModel.AppInfo;

namespace Lintel.Media.Smtc;

/// <summary>Follows the Windows system media transport controls (SMTC). Events arrive on thread-pool threads; results are published on the UI thread.</summary>
public sealed class MediaSessionService(Dispatcher ui, ISettingsStore settings, IAppLog log) : IMediaService
{
    public const string PreferredKey = "media.preferredSource";

    [Flags]
    private enum Part { Sessions = 1, Props = 2, State = 4, All = 7 }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> nameCache = [];
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? hooked;
    private bool disposed;

    // Last read media properties (gate-protected), reused by playback/timeline-only refreshes.
    private string title = "", artist = "", album = "", artKey = "";
    private ImageSource? art;
    private Color? tint;

    public NowPlaying? Current { get; private set; }
    public IReadOnlyList<MediaSource> Sources { get; private set; } = [];
    public event Action? Changed;

    public void Start() => Task.Run(async () =>
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => Kick(Part.All);
            manager.SessionsChanged += (_, _) => Kick(Part.All);
            await RefreshAsync(Part.All);
        }
        catch (Exception e)
        {
            log.Warn("Media sessions unavailable", e);
        }
    });

    public void TogglePlayPause() => Command(s => s.TryTogglePlayPauseAsync());
    public void Next() => Command(s => s.TrySkipNextAsync());
    public void Previous() => Command(s => s.TrySkipPreviousAsync());
    public void SetShuffle(bool on) => Command(s => s.TryChangeShuffleActiveAsync(on));

    public void SetRepeat(RepeatMode mode) => Command(s => s.TryChangeAutoRepeatModeAsync(mode switch
    {
        RepeatMode.Track => MediaPlaybackAutoRepeatMode.Track,
        RepeatMode.List => MediaPlaybackAutoRepeatMode.List,
        _ => MediaPlaybackAutoRepeatMode.None,
    }));

    public void Seek(TimeSpan position)
    {
        var start = hooked?.GetTimelineProperties().StartTime ?? TimeSpan.Zero;
        Command(s => s.TryChangePlaybackPositionAsync((start + position).Ticks));
    }

    public void Select(string? sourceId)
    {
        settings.Set(PreferredKey, sourceId ?? "");
        Kick(Part.All);
    }

    public void Dispose()
    {
        disposed = true;
        Unhook();
    }

    private void Command(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
    {
        var session = hooked;
        if (session is null)
            return;
        Task.Run(async () =>
        {
            try
            {
                await action(session);
                await RefreshAsync(Part.State);
            }
            catch (Exception e)
            {
                log.Warn("Media command failed", e);
            }
        });
    }

    private void Kick(Part part) => Task.Run(() => RefreshAsync(part));

    private async Task RefreshAsync(Part part)
    {
        if (manager is null || disposed)
            return;
        await gate.WaitAsync();
        try
        {
            var sessions = manager.GetSessions().ToList();
            var preferred = settings.Get(PreferredKey, "");
            var infos = sessions.Select(s => new SourceInfo(s.SourceAppUserModelId, SafeStatus(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)).ToList();
            var chosenId = MediaMath.ChooseSource(infos, manager.GetCurrentSession()?.SourceAppUserModelId, preferred.Length == 0 ? null : preferred);
            var session = sessions.FirstOrDefault(s => s.SourceAppUserModelId == chosenId);
            var sources = sessions.Select(s => new MediaSource(s.SourceAppUserModelId, SourceName(s.SourceAppUserModelId), s.SourceAppUserModelId == chosenId)).ToList();

            if (!ReferenceEquals(session, hooked) && session?.SourceAppUserModelId != hooked?.SourceAppUserModelId)
            {
                Unhook();
                hooked = session;
                if (session is not null)
                {
                    session.MediaPropertiesChanged += OnPropsChanged;
                    session.PlaybackInfoChanged += OnStateChanged;
                    session.TimelinePropertiesChanged += OnStateChanged;
                }
                part = Part.All;
            }
            else if (session is not null)
            {
                hooked = session;   // same app, fresh object: keep the newest reference for commands
            }

            NowPlaying? now = null;
            if (session is not null)
            {
                if (part.HasFlag(Part.Props) || part.HasFlag(Part.Sessions))
                    await ReadPropertiesAsync(session);
                now = Build(session);
            }
            Publish(now, sources);
        }
        catch (Exception e)
        {
            log.Warn("Media refresh failed", e);
        }
        finally
        {
            gate.Release();
        }
    }

    private void OnPropsChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Kick(Part.Props | Part.State);
    private void OnStateChanged(GlobalSystemMediaTransportControlsSession s, object e) => Kick(Part.State);

    private void Unhook()
    {
        if (hooked is not { } s)
            return;
        try
        {
            s.MediaPropertiesChanged -= OnPropsChanged;
            s.PlaybackInfoChanged -= OnStateChanged;
            s.TimelinePropertiesChanged -= OnStateChanged;
        }
        catch (Exception)
        {
            // session already gone
        }
        hooked = null;
    }

    private async Task ReadPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        title = props.Title ?? "";
        artist = props.Artist ?? "";
        album = props.AlbumTitle ?? "";
        var key = $"{session.SourceAppUserModelId}|{title}|{artist}|{album}";
        if (key == artKey && art is not null)
            return;
        artKey = key;
        art = null;
        tint = null;
        if (props.Thumbnail is null)
            return;
        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            (art, tint) = Artwork.Decode(ms.ToArray());
        }
        catch (Exception e)
        {
            log.Warn("Artwork unavailable", e);
        }
    }

    private NowPlaying Build(GlobalSystemMediaTransportControlsSession session)
    {
        var info = session.GetPlaybackInfo();
        var line = session.GetTimelineProperties();
        var controls = info.Controls;
        var duration = line.EndTime - line.StartTime;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        var position = line.Position - line.StartTime;
        var id = session.SourceAppUserModelId;
        return new NowPlaying(id, SourceName(id), title, artist, album, art, tint,
            info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            position < TimeSpan.Zero ? TimeSpan.Zero : position, duration, line.LastUpdatedTime, info.PlaybackRate ?? 1,
            controls.IsPreviousEnabled, controls.IsNextEnabled, controls.IsPlaybackPositionEnabled && duration > TimeSpan.Zero,
            controls.IsShuffleEnabled, controls.IsRepeatEnabled, info.IsShuffleActive,
            info.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => RepeatMode.Track,
                MediaPlaybackAutoRepeatMode.List => RepeatMode.List,
                MediaPlaybackAutoRepeatMode.None => RepeatMode.None,
                _ => null,
            });
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus SafeStatus(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus; }
        catch (Exception) { return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed; }
    }

    private string SourceName(string aumid)
    {
        lock (nameCache)
        {
            if (nameCache.TryGetValue(aumid, out var cached))
                return cached;
            string name;
            try { name = WinAppInfo.GetFromAppUserModelId(aumid).DisplayInfo.DisplayName; }
            catch (Exception) { name = ""; }
            return nameCache[aumid] = string.IsNullOrWhiteSpace(name) ? AppNames.FromAumid(aumid) : name;
        }
    }

    private void Publish(NowPlaying? now, List<MediaSource> sources) => ui.BeginInvoke(() =>
    {
        if (disposed)
            return;
        Current = now;
        Sources = sources;
        Changed?.Invoke();
    });
}

/// <summary>Thumbnail bytes to a frozen 240 px BitmapImage plus its lifted average colour.</summary>
internal static class Artwork
{
    public static (ImageSource? Image, Color? Tint) Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return (null, null);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 240;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return (image, TintOf(image));
    }

    public static Color? TintOf(BitmapSource image)
    {
        var small = new TransformedBitmap(image, new ScaleTransform(16d / image.PixelWidth, 16d / image.PixelHeight));
        var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return MediaMath.AverageBgra(pixels) is { } avg ? MediaMath.LiftLightness(avg) : null;
    }
}
