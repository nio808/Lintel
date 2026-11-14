// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

/// <summary>What one response bills, beyond its model.</summary>
internal readonly record struct Billable(
    AgentTokens Tokens, long LongCacheWrite = 0, bool Fast = false, bool Domestic = false, int WebSearches = 0, bool IsAggregate = false);

/// <summary>What one log line says, reduced to the few facts kept. Never prompts, replies or tool data.</summary>
internal abstract record LogEntry
{
    public sealed record Usage(string Key, AgentProvider Provider, DateTimeOffset Date, string Model, string Project,
                               string Session, int Requests, Billable Billable, double? ReportedCost = null) : LogEntry;

    public sealed record Limits(IReadOnlyList<AgentLimitWindow> Windows, DateTimeOffset ObservedAt) : LogEntry;

    public sealed record Plan(string Name, DateTimeOffset ObservedAt) : LogEntry;

    public sealed record TurnBegan(DateTimeOffset At) : LogEntry;

    public sealed record TurnActive(DateTimeOffset? At) : LogEntry;

    public sealed record TurnEnded(DateTimeOffset? At, bool Completed, TimeSpan? Duration) : LogEntry;

    /// <summary>The file was replaced or truncated: forget its open turn.</summary>
    public sealed record Reset : LogEntry;
}

/// <summary>Per-file parser context carried from line to line (persisted in the usage cache).</summary>
internal sealed class LogState
{
    public string Session { get; set; } = "";
    public string Project { get; set; } = "";
    public string Model { get; set; } = "";
    public bool TurnOpen { get; set; }
    /// <summary>Newer Codex logs write one record per response; older ones only running totals.</summary>
    public bool SawUsageRecords { get; set; }
    public AgentTokens? LastTotal { get; set; }
    public bool Fast { get; set; }
    public bool CodexSubagent { get; set; }
    public bool CodexHeaderRead { get; set; }
    public string CodexRoot { get; set; } = "";
    public string CodexTotalsSession { get; set; } = "";
    /// <summary>Copilot shutdown counters are cumulative for the life of a session.</summary>
    public Dictionary<string, AgentTokens> CopilotTotals { get; set; } = [];
    public string? CopilotTurnId { get; set; }
    public bool CopilotFinalResponse { get; set; }
}

internal interface ILogReader
{
    AgentProvider Provider { get; }

    /// <summary>Appends the entries one line yields. Never throws on malformed input.</summary>
    void Parse(ReadOnlyMemory<byte> line, LogState state, DateTimeOffset now, List<LogEntry> output);
}
