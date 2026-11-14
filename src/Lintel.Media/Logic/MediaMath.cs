// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;

namespace Lintel.Media.Logic;

public enum RepeatMode { None, Track, List }

/// <summary>A media session as far as selection is concerned.</summary>
public readonly record struct SourceInfo(string Id, bool IsPlaying);

/// <summary>Pure helpers: timeline maths, session choice, artwork tint, peak to level, pill visibility window.</summary>
public static class MediaMath
{
    /// <summary>Position + (now - positionAt) x rate while playing, clamped to [0, duration] (duration 0 = unknown, no upper clamp).</summary>
    public static TimeSpan Interpolate(TimeSpan position, TimeSpan duration, DateTimeOffset positionAt, DateTimeOffset now, double rate, bool isPlaying)
    {
        var result = position;
        if (isPlaying && positionAt > DateTimeOffset.MinValue && now > positionAt)
            result += TimeSpan.FromSeconds((now - positionAt).TotalSeconds * (rate > 0 ? rate : 1));
        if (duration > TimeSpan.Zero && result > duration)
            result = duration;
        return result < TimeSpan.Zero ? TimeSpan.Zero : result;
    }

    /// <summary>Preferred (user-picked) source if still present; else the system's current one; else the first playing; else the first.</summary>
    public static string? ChooseSource(IReadOnlyList<SourceInfo> sources, string? systemCurrentId, string? preferredId)
    {
        if (sources.Count == 0)
            return null;
        if (preferredId is not null && sources.Any(s => s.Id == preferredId))
            return preferredId;
        if (systemCurrentId is not null && sources.Any(s => s.Id == systemCurrentId))
            return systemCurrentId;
        return sources.FirstOrDefault(s => s.IsPlaying) is { Id: { } id } ? id : sources[0].Id;
    }

    /// <summary>Average colour of a Bgra32 buffer (transparent pixels ignored); null when nothing is opaque.</summary>
    public static Color? AverageBgra(byte[] pixels)
    {
        long b = 0, g = 0, r = 0, n = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 32)
                continue;
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
            n++;
        }
        return n == 0 ? null : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>Raises HSL lightness to at least <paramref name="minLightness"/> (0-1), keeping hue and saturation.</summary>
    public static Color LiftLightness(Color color, double minLightness = 0.55)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (l >= minLightness)
            return Color.FromRgb(color.R, color.G, color.B);
        var d = max - min;
        double h = 0, s = 0;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? ((g - b) / d + (g < b ? 6 : 0)) / 6
              : max == g ? ((b - r) / d + 2) / 6
              : ((r - g) / d + 4) / 6;
        }
        l = minLightness;
        if (s < 1e-9)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromRgb((byte)Math.Round(Hue(p, q, h + 1d / 3) * 255), (byte)Math.Round(Hue(p, q, h) * 255), (byte)Math.Round(Hue(p, q, h - 1d / 3) * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1d / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
        }
    }

    /// <summary>Linear peak (0-1) to a perceptual 0-1 level over -50 dB to 0 dB.</summary>
    public static double PeakToLevel(double peak)
    {
        if (peak <= 0.00316)
            return 0;
        return Math.Clamp((20 * Math.Log10(Math.Min(peak, 1)) + 50) / 50, 0, 1);
    }

    /// <summary>Activity shows while playing and for <paramref name="hideAfter"/> after pausing (zero = while any media exists).</summary>
    public static bool IsActivityActive(bool hasMedia, bool isPlaying, DateTimeOffset? pausedSince, DateTimeOffset now, TimeSpan hideAfter)
    {
        if (!hasMedia)
            return false;
        if (isPlaying || hideAfter <= TimeSpan.Zero)
            return true;
        return pausedSince is { } since && now - since < hideAfter;
    }
}

/// <summary>Fast attack, slow release smoothing for a 0-1 level sampled at ~30 Hz.</summary>
public sealed class PeakSmoother(double attack = 0.6, double release = 0.15)
{
    public double Value { get; private set; }

    public double Next(double target)
    {
        Value += (target - Value) * (target > Value ? attack : release);
        if (Value < 0.001)
            Value = 0;
        return Value;
    }

    public void Reset() => Value = 0;
}
