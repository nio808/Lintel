// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using Lintel.Core.Agents;
using Lintel.Core.Controls;
using Lintel.Core.Text;

namespace Lintel.Agents.UI;

/// <summary>Setting keys this module owns, including the alert and notice keys the data service reads.</summary>
internal static class UiKeys
{
    public const string PillFocus = "agents.pill.focus";        // mostUsed | session | weekly
    public const string PillDisplay = "agents.pill.display";    // remaining | used
    public const string PillReadout = "agents.pill.readout";    // elapsed | tokens | cost | limit
    public const string CardsOrder = "agents.cards.order";
    public const string CardsHidden = "agents.cards.hidden";
    public const string Period = "agents.period";               // today | week | month

    public const string AlertsEnabled = "agents.alerts.enabled";
    public const string AlertsThresholds = "agents.alerts.thresholds";
    public const string AlertsOnReset = "agents.alerts.onReset";
    public const string FinishEnabled = "agents.finish.enabled";
    public const string FinishMinSeconds = "agents.finish.minSeconds";

    public static readonly string[] DefaultCards = ["limits", "spend", "live", "trend", "models", "projects"];

    public static string Provider(AgentProvider provider) => "agents.providers." + provider.ToString().ToLowerInvariant();
}

internal enum LimitTone { Normal, Low, Critical }

/// <summary>A window picked for a glance: the account it belongs to and the window itself.</summary>
internal sealed record FocusReading(AgentLimits Limits, AgentLimitWindow Window);

/// <summary>Pure display rules shared by the pill, the section and notices.</summary>
internal static class AgentDisplay
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly AgentProvider[] Providers = [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Copilot, AgentProvider.OpenCode];

    // mirrors Agent.* / Tone.* in NotchTheme.xaml; code paths (notices, OnRender) need brushes outside the notch tree.
    private static readonly Brush[] ProviderBrushes = [Frozen(0xD9, 0x8A, 0x66), Frozen(0x9C, 0x89, 0xBF), Frozen(0x6C, 0xA0, 0xF0), Frozen(0x5F, 0xB3, 0xA8)];
    public static readonly Brush Warning = Frozen(0xF5, 0xB8, 0x3D);
    public static readonly Brush Critical = Frozen(0xFF, 0x5C, 0x5C);
    public static readonly Brush Success = Frozen(0x3D, 0xDC, 0x84);
    public static readonly Brush Accent = Frozen(0x7C, 0x9C, 0xFF);
    public static readonly Brush Text = Brushes.White;

    /// <summary>Glyph for the section tab and manifest: Sparkle exists only in Segoe Fluent Icons (Windows 11).</summary>
    public static readonly string SectionGlyph =
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegoeIcons.ttf")) ? Glyphs.Sparkle : Glyphs.Robot;

    public static string Name(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.Copilot => "Copilot",
        _ => "OpenCode",
    };

    public static Brush Brush(AgentProvider provider) => ProviderBrushes[(int)provider];

    public static string BrushKey(AgentProvider provider) => "Agent." + provider;

    /// <summary>Agent colour while there is room; amber at ≤ 20% left; red at ≤ 5% left (the default alert thresholds).</summary>
    public static LimitTone Tone(double remainingPercent) =>
        remainingPercent <= 5 ? LimitTone.Critical : remainingPercent <= 20 ? LimitTone.Low : LimitTone.Normal;

    public static Brush ToneBrush(LimitTone tone, AgentProvider provider) => tone switch
    {
        LimitTone.Critical => Critical,
        LimitTone.Low => Warning,
        _ => Brush(provider),
    };

    /// <summary>"Codex · Work" for added accounts, the provider name for the default one.</summary>
    public static string AccountTitle(AgentProvider provider, AgentAccount? account) =>
        account is null || account.IsDefault || string.IsNullOrWhiteSpace(account.DisplayName)
            ? Name(provider)
            : $"{Name(provider)} · {account.DisplayName}";

    /// <summary>The card label of a window, falling back on its kind when the reader gave none.</summary>
    public static string Label(AgentLimitWindow window)
    {
        if (!string.IsNullOrWhiteSpace(window.Label))
            return window.Label;
        return window.Kind switch
        {
            LimitKind.Session => window.Minutes is int m && m > 0 ? Days(m) ? $"{m / 1440}-day" : $"{Hours(m)}-hour" : "Session",
            LimitKind.Weekly => window.Scope is { Length: > 0 } scope ? $"Weekly · {scope}" : "Weekly",
            _ => window.Scope ?? "Limit",
        };
    }

    /// <summary>Two-to-four characters for the pill: "5h", "wk", "Opus".</summary>
    public static string ShortLabel(AgentLimitWindow window) => window.Kind switch
    {
        LimitKind.Session => window.Minutes is int m && m > 0 ? Days(m) ? $"{m / 1440}d" : $"{Hours(m)}h" : "ses",
        LimitKind.Weekly => window.Scope is { Length: > 0 } scope ? Trim(scope, 4) : "wk",
        _ => window.Minutes is int m && m >= 60 ? Days(m) ? $"{m / 1440}d" : $"{Hours(m)}h" : Trim(window.Scope ?? window.Label, 4),
    };

    /// <summary>
    /// The window to show at a glance. A reset-due window is unknown (never assumed empty), so it is used only when
    /// nothing else exists. Ties prefer the default account and the session.
    /// </summary>
    public static FocusReading? Focus(IEnumerable<AgentLimits> limits, string focus, DateTimeOffset now)
    {
        var all = limits.SelectMany(l => l.Windows.Select(w => new FocusReading(l, w))).ToList();
        if (all.Count == 0)
            return null;
        var known = all.Where(r => !r.Window.ResetDue(now)).ToList();
        if (known.Count > 0)
            all = known;
        var weekly = all.Where(r => r.Window.Kind == LimitKind.Weekly).ToList();
        var preferred = focus switch
        {
            "session" => all.Where(r => r.Window.Kind == LimitKind.Session).ToList(),
            "weekly" => weekly.Any(r => r.Window.Scope is null) ? weekly.Where(r => r.Window.Scope is null).ToList() : weekly,
            _ => all,
        };
        if (preferred.Count == 0)
            preferred = all;
        return preferred.OrderByDescending(r => r.Limits.Blocked).ThenByDescending(r => r.Window.UsedPercent)
            .ThenBy(r => r.Window.Kind).First();
    }

    /// <summary>Fraction of the window that has passed (0–1), when its length and reset are known; null near the ends.</summary>
    public static double? Elapsed(AgentLimitWindow window, DateTimeOffset now)
    {
        if (window.Minutes is not int minutes || minutes <= 0 || window.ResetsAt is not { } reset)
            return null;
        var fraction = 1 - (reset - now).TotalMinutes / minutes;
        return fraction is > 0.02 and < 0.98 ? fraction : null;
    }

    /// <summary>Remaining or used percent as chosen in settings.</summary>
    public static double Shown(AgentLimitWindow window, bool used) => used ? Math.Clamp(window.UsedPercent, 0, 100) : window.RemainingPercent;

    /// <summary>The live readout of the working pill.</summary>
    public static string Readout(AgentUsageSnapshot snapshot, string readout, bool used, string focus, DateTimeOffset now)
    {
        var live = snapshot.Live;
        switch (readout)
        {
            case "tokens":
                return Format.Tokens(live.Sum(s => s.Tokens.Total));
            case "cost":
                return Format.Money(live.Sum(s => s.Cost));
            case "limit":
                return Focus(snapshot.Limits, focus, now) is { } reading ? Format.Percent(Shown(reading.Window, used)) : "—";
            default:
                return live.Count == 0 ? "0:00" : Format.Clock(now - live.Min(s => s.Started));
        }
    }


    /// <summary>"claude-opus-4-1-20250805" → "Opus 4.1"; "gpt-5.1-codex-max" → "GPT-5.1 Codex Max". Names that look finished stay.</summary>
    // heuristic prettifier for raw model ids; display names from the data service pass through untouched.
    public static string ModelName(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Unknown model";
        if (id.Contains(' ') || id.Any(char.IsUpper))
            return id;
        var name = id[(id.LastIndexOf('/') + 1)..];
        if (name.IndexOfAny(['@', ':']) is var suffix and > 0)
            name = name[..suffix];
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[^1].Length == 8 && parts[^1].All(char.IsAsciiDigit))
            parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 0 && parts[0] == "claude")
            parts.RemoveAt(0);
        var words = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part == "gpt" && i + 1 < parts.Count)
            {
                words.Add("GPT-" + parts[++i]);
                continue;
            }
            if (part.All(c => char.IsAsciiDigit(c) || c == '.') && words.Count > 0 && words[^1].All(c => char.IsAsciiDigit(c) || c == '.'))
            {
                words[^1] += "." + part;
                continue;
            }
            words.Add(part.Length <= 2 && !part.All(char.IsAsciiDigit) ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]);
        }
        return words.Count == 0 ? id : string.Join(' ', words);
    }

    /// <summary>"just now", "4m ago", "2h ago", "3d ago".</summary>
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span.TotalSeconds < 60)
            return "just now";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)
            return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    /// <summary>"in 4m", "in 1h 5m"; past → "now".</summary>
    public static string In(DateTimeOffset when, DateTimeOffset now)
    {
        var span = when - now;
        return span.TotalSeconds < 30 ? "now" : "in " + (span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m");
    }

    public static string Count(long value) => value.ToString("#,0", Inv);

    private static string Hours(int minutes) => (minutes / 60d).ToString("0.#", Inv);

    private static bool Days(int minutes) => minutes >= 2880 && minutes % 1440 == 0;

    private static string Trim(string text, int length) => text.Length <= length ? text : text[..length];

    private static SolidColorBrush Frozen(byte r, byte g, byte b) => (SolidColorBrush)Frozen(Color.FromRgb(r, g, b));

    internal static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>"2h 14m", "3d 17h", "12m"; "due" once passed, "—" when not reported.</summary>
    public static string ShortCountdown(DateTimeOffset? reset, DateTimeOffset now) =>
        reset is null ? "—" : Format.Countdown(reset, now) is var text && text.StartsWith("Resets in ", StringComparison.Ordinal) ? text[10..] : "due";

    /// <summary>Runs in Debug builds under --design: the display rules that would silently mislead if they broke.</summary>
    [Conditional("DEBUG")]
    public static void SelfCheck()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var session = new AgentLimitWindow("s", LimitKind.Session, 300, null, "5-hour", 41, now.AddHours(2));
        var weekly = new AgentLimitWindow("w", LimitKind.Weekly, 10080, null, "Weekly", 72, now.AddDays(3));
        var due = new AgentLimitWindow("d", LimitKind.Weekly, 10080, "Opus", "Weekly · Opus", 99, now.AddMinutes(-1));
        var claude = new AgentLimits("claude:default", AgentProvider.Claude, [session, weekly, due], now, LimitSource.Account, null, null, false, false, null, null);
        Debug.Assert(Focus([claude], "mostUsed", now)?.Window == weekly, "most used skips reset-due windows");
        Debug.Assert(Focus([claude], "session", now)?.Window == session, "session focus");
        Debug.Assert(Focus([claude], "weekly", now)?.Window == weekly, "weekly focus prefers the unscoped window");
        Debug.Assert(Focus([], "mostUsed", now) is null, "no limits → no focus");
        Debug.Assert(Math.Abs(Elapsed(session, now)!.Value - 0.6) < 1e-9, "3 of 5 hours passed");
        Debug.Assert(Tone(20) == LimitTone.Low && Tone(5) == LimitTone.Critical && Tone(21) == LimitTone.Normal, "tones");
        Debug.Assert(ShortLabel(session) == "5h" && ShortLabel(weekly) == "wk" && ShortLabel(due) == "Opus", "short labels");

        Debug.Assert(ModelName("claude-opus-4-1-20250805") == "Opus 4.1", ModelName("claude-opus-4-1-20250805"));
        Debug.Assert(ModelName("gpt-5.1-codex-max") == "GPT-5.1 Codex Max", ModelName("gpt-5.1-codex-max"));
        Debug.Assert(ModelName("Sonnet 4.5") == "Sonnet 4.5", "display names pass through");
        Debug.Assert(ShortCountdown(now.AddMinutes(134), now) == "2h 14m" && ShortCountdown(now.AddMinutes(-1), now) == "due" && ShortCountdown(null, now) == "—", "short countdowns");
    }
}
