// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;
using Lintel.Media.Logic;

namespace Lintel.Media.Tests;

public sealed class MediaLogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Interpolate_AdvancesWhilePlayingAtRate()
    {
        var p = MediaMath.Interpolate(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(4), T0, T0.AddSeconds(5), 1.5, true);
        Assert.Equal(TimeSpan.FromSeconds(17.5), p);
    }

    [Fact]
    public void Interpolate_PausedDoesNotAdvance_AndClampsToDuration()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), MediaMath.Interpolate(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(4), T0, T0.AddSeconds(5), 1, false));
        Assert.Equal(TimeSpan.FromSeconds(30), MediaMath.Interpolate(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30), T0, T0.AddSeconds(50), 1, true));
    }

    [Fact]
    public void Interpolate_IgnoresMissingTimestampAndBadRate()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), MediaMath.Interpolate(TimeSpan.FromSeconds(7), TimeSpan.Zero, DateTimeOffset.MinValue, T0, 1, true));
        Assert.Equal(TimeSpan.FromSeconds(12), MediaMath.Interpolate(TimeSpan.FromSeconds(10), TimeSpan.Zero, T0, T0.AddSeconds(2), 0, true));
    }

    [Fact]
    public void ChooseSource_PrefersUserPick_ThenSystemCurrent_ThenPlaying_ThenFirst()
    {
        SourceInfo[] sources = [new("a", false), new("b", true), new("c", false)];
        Assert.Equal("c", MediaMath.ChooseSource(sources, "a", "c"));
        Assert.Equal("a", MediaMath.ChooseSource(sources, "a", "gone"));
        Assert.Equal("b", MediaMath.ChooseSource(sources, null, null));
        Assert.Equal("a", MediaMath.ChooseSource([new("a", false), new("c", false)], null, null));
        Assert.Null(MediaMath.ChooseSource([], "a", "b"));
    }

    [Fact]
    public void AverageBgra_SkipsTransparent()
    {
        byte[] px = [0, 0, 200, 255, 0, 0, 100, 255, 255, 255, 255, 0];   // two red-ish opaque pixels, one transparent white
        Assert.Equal(Color.FromRgb(150, 0, 0), MediaMath.AverageBgra(px));
        Assert.Null(MediaMath.AverageBgra([1, 2, 3, 0]));
    }

    [Fact]
    public void LiftLightness_RaisesDarkColours_AndKeepsBrightOnes()
    {
        var lifted = MediaMath.LiftLightness(Color.FromRgb(20, 30, 90));
        Assert.True(Lightness(lifted) >= 0.549);
        Assert.True(lifted.B > lifted.R && lifted.B > lifted.G);   // hue preserved
        var bright = Color.FromRgb(240, 200, 120);
        Assert.Equal(bright, MediaMath.LiftLightness(bright));
        var gray = MediaMath.LiftLightness(Color.FromRgb(10, 10, 10));
        Assert.Equal(gray.R, gray.G);
        Assert.True(Lightness(gray) >= 0.549);
    }

    private static double Lightness(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 510d;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.001, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    public void PeakToLevel_Bounds(double peak, double level) => Assert.Equal(level, MediaMath.PeakToLevel(peak), 6);

    [Fact]
    public void PeakToLevel_IsMonotonic() => Assert.True(MediaMath.PeakToLevel(0.5) > MediaMath.PeakToLevel(0.05));

    [Fact]
    public void Smoother_AttacksFast_ReleasesSlow_AndSettlesToZero()
    {
        var s = new PeakSmoother();
        var up = s.Next(1);
        Assert.InRange(up, 0.5, 0.7);
        var down = s.Next(0);
        Assert.True(down > up * 0.8);   // release is slower than attack
        for (var i = 0; i < 100; i++)
            s.Next(0);
        Assert.Equal(0, s.Value);
    }

    [Fact]
    public void ActivityWindow_FollowsPlaybackThenExpires()
    {
        var delay = TimeSpan.FromSeconds(30);
        Assert.False(MediaMath.IsActivityActive(false, false, null, T0, delay));
        Assert.True(MediaMath.IsActivityActive(true, true, null, T0, delay));
        Assert.True(MediaMath.IsActivityActive(true, false, T0, T0.AddSeconds(29), delay));
        Assert.False(MediaMath.IsActivityActive(true, false, T0, T0.AddSeconds(30), delay));
        Assert.False(MediaMath.IsActivityActive(true, false, null, T0, delay));          // never saw it play
        Assert.True(MediaMath.IsActivityActive(true, false, null, T0, TimeSpan.Zero));   // zero = while media exists
    }
}

public sealed class AppNamesTests
{
    [Theory]
    [InlineData("Spotify", "Spotify AB", @"C:\x\Spotify.exe", "Spotify")]
    [InlineData("", "Google Chrome", @"C:\x\chrome.exe", "Google Chrome")]
    [InlineData("@%SystemRoot%/System32/AudioSrv.Dll,-202", "", @"C:\x\vlc.exe", "Vlc")]
    [InlineData("  ", null, null, "Unknown app")]
    public void Resolve_FallsBackThroughDisplayDescriptionExe(string display, string? description, string? path, string expected)
        => Assert.Equal(expected, AppNames.Resolve(display, description, path));

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("chrome.exe", "Chrome")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("", "Unknown app")]
    public void FromAumid(string aumid, string expected) => Assert.Equal(expected, AppNames.FromAumid(aumid));

    [Theory]
    [InlineData("Spotify.exe", "Spotify.exe", true)]
    [InlineData("chrome.exe", "chrome.exe", true)]
    [InlineData("Chrome", "chrome.exe", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify.exe", true)]
    [InlineData("MSEdge", "msedge.exe", true)]
    [InlineData("Spotify.exe", "chrome.exe", false)]
    [InlineData(null, "chrome.exe", false)]
    public void AumidMatchesExe(string? aumid, string exe, bool expected) => Assert.Equal(expected, AppNames.AumidMatchesExe(aumid, exe));
}

public sealed class AudioGroupingTests
{
    private static RawSession S(int pid, string? path, string name = "", float vol = 0.5f, bool muted = false, float peak = 0, bool active = true, bool system = false, bool expired = false)
        => new(pid, path, name, null, vol, muted, peak, active, system, expired);

    [Fact]
    public void Group_DropsSystemSoundsExpiredAndPidZero()
    {
        var rows = AudioGrouping.Group([S(0, null), S(5, @"C:\a.exe", system: true), S(6, @"C:\b.exe", expired: true), S(7, @"C:\c.exe", "C")]);
        Assert.Single(rows);
        Assert.Equal("C", rows[0].Name);
    }

    [Fact]
    public void Group_MergesProcessesOfOneExecutable()
    {
        var rows = AudioGrouping.Group([S(1, @"C:\App\chrome.exe", vol: 0.3f, peak: 0.1f), S(2, @"c:\app\CHROME.exe", vol: 0.4f, peak: 0.6f, muted: true)]);
        var row = Assert.Single(rows);
        Assert.Equal([1, 2], row.Pids);
        Assert.Equal(0.4f, row.Volume);
        Assert.Equal(0.6f, row.Peak);
        Assert.False(row.Muted);   // muted only when every session is
        Assert.Equal("Chrome", row.Name);
    }

    [Fact]
    public void Group_ActiveFirstThenByName_AndUnknownPathsStaySeparate()
    {
        var rows = AudioGrouping.Group([S(1, @"C:\z.exe", "Zed"), S(2, @"C:\a.exe", "Alpha", active: false), S(3, null), S(4, null)]);
        Assert.Equal(["Unknown app", "Unknown app", "Zed", "Alpha"], rows.Select(r => r.Name));
    }
}
