// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Text.Json;
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

/// <summary>
/// OpenCode keeps every session in one SQLite database. Finished assistant messages after the last rowid read become
/// usage records (tokens, cost, model, folder); no live turns in v1. Only numbers, ids and folder names leave a row.
/// </summary>
internal static class OpenCodeDbReader
{
    public const int BatchSize = 2000;

    /// <summary>A reply nobody touched for this long is not being written any more.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);

    public sealed record Result(long Rowid, bool Supported, List<LogEntry.Usage> Records);

    /// <summary>
    /// Reads rows after <paramref name="afterRowid"/> (on a first read, after the first message created at or after
    /// <paramref name="horizon"/>). The returned rowid stops before a reply still being written, so it is read again.
    /// </summary>
    public static Result Read(string path, long afterRowid, DateTimeOffset horizon, DateTimeOffset now)
    {
        var records = new List<LogEntry.Usage>();
        using var db = WinSqlite.OpenReadOnly(path);
        if (db is null) return new(afterRowid, true, records);
        var v2 = db.TableExists("session_v2");
        var (messages, sessions) = v2 ? ("session_message", "session_v2") : ("message", "session");
        if (!db.TableExists(messages) || !db.TableExists(sessions)) return new(afterRowid, false, records);

        var max = db.Scalar($"SELECT max(rowid) FROM {messages}") ?? 0;
        if (afterRowid > max) afterRowid = 0;   // database recreated or reverted
        if (afterRowid == 0) afterRowid = FirstRow(db, messages, horizon.ToUnixTimeMilliseconds());

        var role = v2 ? "m.type" : "NULL";
        var sql = $"SELECT m.rowid, m.id, m.session_id, m.time_created, m.time_updated, {role}, s.directory, m.data " +
                  $"FROM {messages} m JOIN {sessions} s ON m.session_id = s.id WHERE m.rowid > ?1 ORDER BY m.rowid LIMIT {BatchSize}";
        var last = afterRowid;
        if (!db.Query(sql, c =>
        {
            var rowid = c.Int(0);
            var outcome = Row(c, v2, now, horizon, out var usage);
            if (outcome == RowOutcome.Pending) return false;
            last = rowid;
            if (usage is not null) records.Add(usage);
            return true;
        }, afterRowid))
            return new(afterRowid, false, records);
        return new(last, true, records);
    }

    private enum RowOutcome { Done, Pending }

    private static RowOutcome Row(WinSqlite.Cursor c, bool v2, DateTimeOffset now, DateTimeOffset horizon, out LogEntry.Usage? usage)
    {
        usage = null;
        var id = c.Text(1);
        var session = c.Text(2) ?? "";
        var created = c.IsNull(3) ? 0 : c.Int(3);
        var updated = c.IsNull(4) ? created : c.Int(4);
        var directory = c.Text(6);
        if (id is null || created <= 0 || c.Text(7) is not { } json) return RowOutcome.Done;
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(created);
        if (createdAt < horizon) return RowOutcome.Done;

        using var doc = LogJson.TryParse(System.Text.Encoding.UTF8.GetBytes(json));
        JsonElement? data = doc?.RootElement;
        if (data is null) return RowOutcome.Done;
        if ((v2 ? c.Text(5) : data.Str("role")) != "assistant") return RowOutcome.Done;

        var completed = data.Prop("time").Time("completed");   // only whether the reply is finished; the row's creation time dates the usage
        if (completed is null && !data.HasError())
        {
            // Still being written: wait, unless nothing changed for a while.
            if (now - DateTimeOffset.FromUnixTimeMilliseconds(updated) < Abandoned) return RowOutcome.Pending;
        }

        var tokens = data.Prop("tokens");
        var reasoning = tokens.Count("reasoning");
        var cache = tokens.Prop("cache");
        var t = new AgentTokens(tokens.Count("input"), cache.Count("write"), cache.Count("read"), tokens.Count("output") + reasoning, reasoning);
        var cost = data.Dbl("cost");
        if (t.Total <= 0 && (cost ?? 0) <= 0) return RowOutcome.Done;

        var model = data.Str("modelID") ?? data.Str("model_id") ?? data.Prop("model").Str("modelID") ?? "";
        var project = ProjectName.From(data.Prop("path").Str("cwd") is { Length: > 0 } cwd ? cwd : directory);
        usage = new LogEntry.Usage($"opencode:{id}", AgentProvider.OpenCode, createdAt, model, project, session, 1,
            new Billable(t), cost is > 0 ? cost : null);
        return RowOutcome.Done;
    }

    private static bool HasError(this JsonElement? data) => data.Prop("error") is not null;

    /// <summary>Rows are stored in the order they were saved: binary search for the first message at/after the floor.</summary>
    private static long FirstRow(WinSqlite db, string table, long floorMs)
    {
        if (floorMs <= 0) return 0;
        long low = 0, high = db.Scalar($"SELECT max(rowid) FROM {table}") ?? 0;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            long? rowid = null, time = null;
            db.Query($"SELECT rowid, time_created FROM {table} WHERE rowid >= ?1 ORDER BY rowid LIMIT 1",
                c => { rowid = c.Int(0); time = c.IsNull(1) ? null : c.Int(1); return false; }, middle);
            if (rowid is null) { high = middle - 1; continue; }
            if (time is null || time < floorMs) low = rowid.Value;
            else high = middle - 1;
        }
        return low;
    }
}
