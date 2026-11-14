// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Text;

namespace Lintel.Stash;

internal static class StashText
{
    /// <summary>"now", "5m", "3h", "2d": compact age for list rows.</summary>
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span.TotalSeconds < 45) return "now";
        if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : Format.Bytes((long)bytesPerSecond) + "/s";
}
