// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Lintel.Agents.Limits;

internal enum LimitFailure { Other, RateLimited, Auth }

/// <summary>A failed limits read, with a friendly fixed message. Never carries tokens or response text.</summary>
internal sealed class LimitReadException(string message, LimitFailure kind = LimitFailure.Other, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public LimitFailure Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Pure: when to ask again.</summary>
internal static class BackoffPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan VisibleFreshness = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ManualMinimum = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// HTTP 429: Retry-After (delta or date), at least 5 min, 15 min when absent. 401/403: 15 min.
    /// Anything else: 5 min, doubling per consecutive failure up to 30 min.
    /// </summary>
    public static TimeSpan Delay(LimitFailure kind, int consecutiveFailures, TimeSpan? retryAfter = null)
    {
        switch (kind)
        {
            case LimitFailure.RateLimited:
                var wait = retryAfter ?? TimeSpan.FromMinutes(15);
                return wait > Interval ? wait : Interval;
            case LimitFailure.Auth:
                return TimeSpan.FromMinutes(15);
            default:
                var doublings = Math.Clamp(consecutiveFailures - 1, 0, 6);
                var delay = TimeSpan.FromTicks(Interval.Ticks << doublings);
                return delay > Cap ? Cap : delay;
        }
    }

    public static string Friendly(Exception error) => error switch
    {
        LimitReadException e => e.Message,
        TaskCanceledException or TimeoutException or OperationCanceledException => "Usage check timed out. Will try again automatically.",
        HttpRequestException => "Offline or network unavailable. Your last reading is kept.",
        InvalidDataException or ArgumentException or InvalidOperationException or JsonException or FormatException =>
            "The service returned no readable subscription quotas. Try your CLI's usage screen.",
        UnauthorizedAccessException or IOException => "Could not read local login files. Check access and try again.",
        _ => "Usage check failed. Will try again automatically.",
    };

    public static LimitFailure KindOf(Exception error) => error is LimitReadException e ? e.Kind : LimitFailure.Other;
}
