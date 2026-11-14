// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;
using Lintel.Media.Logic;

namespace Lintel.Media.Smtc;

/// <summary>Immutable snapshot of the followed media session.</summary>
public sealed record NowPlaying(
    string SourceAppId, string SourceName, string Title, string Artist, string Album,
    ImageSource? Art, Color? Tint, bool IsPlaying, TimeSpan Position, TimeSpan Duration, DateTimeOffset PositionAt, double Rate,
    bool CanPrev, bool CanNext, bool CanSeek, bool CanShuffle, bool CanRepeat, bool? Shuffle, RepeatMode? Repeat)
{
    public TimeSpan PositionAtTime(DateTimeOffset now) => MediaMath.Interpolate(Position, Duration, PositionAt, now, Rate, IsPlaying);
}

public sealed record MediaSource(string Id, string Name, bool IsCurrent);

/// <summary>What the views and the activity need from a media backend (real SMTC or demo).</summary>
public interface IMediaService : IDisposable
{
    NowPlaying? Current { get; }
    IReadOnlyList<MediaSource> Sources { get; }
    /// <summary>Raised on the UI thread after any change (session, properties, playback, timeline).</summary>
    event Action? Changed;
    void Start();
    void TogglePlayPause();
    void Next();
    void Previous();
    void Seek(TimeSpan position);
    void SetShuffle(bool on);
    void SetRepeat(RepeatMode mode);
    /// <summary>Pin a source (AppUserModelId) or null for automatic.</summary>
    void Select(string? sourceId);
}
