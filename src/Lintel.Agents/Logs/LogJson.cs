// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Globalization;
using System.Text.Json;
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

/// <summary>Defensive JSON accessors: wrong shapes yield null/0 instead of throwing.</summary>
internal static class LogJson
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static JsonElement? Prop(this JsonElement? e, string name) => e is { } x ? x.Prop(name) : null;

    public static string? Str(this JsonElement? e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static string? Str(this JsonElement e, string name) => ((JsonElement?)e).Str(name);

    public static bool IsTrue(this JsonElement? e, string name) => e.Prop(name) is { ValueKind: JsonValueKind.True };

    public static double? Dbl(this JsonElement? v) =>
        v is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public static double? Dbl(this JsonElement? e, string name) => e.Prop(name).Dbl();

    /// <summary>A non-negative count; damaged lines must not overflow sums.</summary>
    public static long Count(this JsonElement? v) =>
        v.Dbl() is { } d && d > 0 ? (long)Math.Min(d, 1e12) : 0;

    public static long Count(this JsonElement? e, string name) => e.Prop(name).Count();

    public static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    /// <summary>ISO-8601 text, or Unix seconds / milliseconds.</summary>
    public static DateTimeOffset? Time(this JsonElement? v)
    {
        switch (v?.ValueKind)
        {
            case JsonValueKind.String:
                return DateTimeOffset.TryParse(v.Value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
            case JsonValueKind.Number:
                return Seconds(v.Dbl());
            default:
                return null;
        }
    }

    public static DateTimeOffset? Time(this JsonElement? e, string name) => e.Prop(name).Time();

    public static DateTimeOffset? Seconds(double? raw)
    {
        if (raw is not { } r || r <= 0 || !double.IsFinite(r)) return null;
        if (r > 100_000_000_000) r /= 1000;
        return r > 253402300799 ? null : DateTimeOffset.UnixEpoch.AddSeconds(r);
    }

    /// <summary>Codex counts include cached input.</summary>
    public static AgentTokens CodexTokens(JsonElement? usage)
    {
        var input = usage.Count("input_tokens");
        var cached = Math.Min(input, usage.Count("cached_input_tokens"));
        var written = Math.Min(input - cached, usage.Count("cache_write_input_tokens"));
        return new(input - cached - written, written, cached, usage.Count("output_tokens"), usage.Count("reasoning_output_tokens"));
    }

    public static bool FastTier(string? tier) =>
        tier is not null && (tier.Equals("fast", StringComparison.OrdinalIgnoreCase) || tier.Equals("priority", StringComparison.OrdinalIgnoreCase));

    public static LimitKind Kind(int? minutes) => minutes switch
    {
        null or <= 0 => LimitKind.Other,
        <= 12 * 60 => LimitKind.Session,
        >= 6 * 1440 and <= 8 * 1440 => LimitKind.Weekly,
        _ => LimitKind.Other,
    };
}

/// <summary>Byte-level substring checks before any JSON parse, so huge histories index cheaply.</summary>
internal static class LineFilter
{
    public static bool Has(ReadOnlyMemory<byte> line, ReadOnlySpan<byte> needle) => line.Span.IndexOf(needle) >= 0;

    /// <summary>The first "type":"..." value at or after <paramref name="start"/> (within 64 bytes), and where it ends.</summary>
    public static (string Name, int End)? FirstType(ReadOnlySpan<byte> line, int start = 0)
    {
        ReadOnlySpan<byte> key = "\"type\":\""u8;
        if (start >= line.Length) return null;
        var at = line[start..].IndexOf(key);
        if (at < 0) return null;
        var value = start + at + key.Length;
        var window = line[value..Math.Min(line.Length, value + 64)];
        var quote = window.IndexOf((byte)'"');
        if (quote < 0) return null;
        return (System.Text.Encoding.UTF8.GetString(window[..quote]), value + quote + 1);
    }
}

internal static class ProjectName
{
    /// <summary>Last path component of a Windows or POSIX folder; a worktree kept inside a repo belongs to the repo.</summary>
    public static string From(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        foreach (var marker in new[] { "/.claude/worktrees", "\\.claude\\worktrees" })
        {
            var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (i + marker.Length == path.Length || path[i + marker.Length] is '/' or '\\')) { path = path[..i]; break; }
        }
        path = path.TrimEnd('/', '\\');
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
