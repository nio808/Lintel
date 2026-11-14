// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Lintel.Core.Text;

/// <summary>Display formatting; invariant culture so widths stay stable.</summary>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"Resets in 2h 14m", "Reset due · awaiting update", "Reset time not reported".</summary>
    public static string Countdown(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (!reset.HasValue)
            return "Reset time not reported";
        var remaining = reset.Value - now;
        if (remaining.TotalSeconds <= 0)
            return "Reset due · awaiting update";
        if (remaining.TotalDays >= 1)
            return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    /// <summary>"42s", "4m 12s", "1h 5m", "2d 3h". Negative → "0s".</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d {value.Hours}h";
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1)
            return $"{value.Minutes}m {value.Seconds}s";
        return $"{value.Seconds}s";
    }

    /// <summary>"0:05", "12:34", "1:02:03". Negative → "0:00".</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    /// <summary>"842", "1.2K", "12.3K", "123K", "1.2M", "4.5B".</summary>
    public static string Tokens(long value) => Scaled(value, 1000, ["", "K", "M", "B", "T"], "");

    /// <summary>"$0.84"; null → "—".</summary>
    public static string Money(double? value) => value is { } v ? "$" + v.ToString("#,0.00", Inv) : "—";

    /// <summary>"512 B", "1.5 KB", "120 MB" (1024-based, like Explorer).</summary>
    public static string Bytes(long value) => Scaled(value, 1024, ["B", "KB", "MB", "GB", "TB"], " ");

    /// <summary>"42%" (rounded, not clamped).</summary>
    public static string Percent(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%";

    private static string Scaled(long value, double step, string[] units, string separator)
    {
        double number = Math.Abs(value);
        var unit = 0;
        while (number >= step && unit < units.Length - 1)
        {
            number /= step;
            unit++;
        }
        // Round first so 999.95K becomes 1.0M rather than "1000K".
        if (unit > 0 && unit < units.Length - 1 && Math.Round(number, number < 100 ? 1 : 0) >= step)
        {
            number /= step;
            unit++;
        }
        var sign = value < 0 ? "-" : "";
        var text = unit == 0 ? number.ToString("0", Inv) : number.ToString(number < 100 ? "0.#" : "0", Inv);
        return sign + text + separator + units[unit];
    }
}
