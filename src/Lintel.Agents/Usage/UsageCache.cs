// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using Lintel.Agents.Logs;
using Lintel.Core.Agents;

namespace Lintel.Agents.Usage;

/// <summary>What is remembered of one log file between runs: where reading stopped and the parser's context.</summary>
internal sealed class FileProgress
{
    public AgentProvider Provider { get; set; }
    public long Offset { get; set; }
    public long Mtime { get; set; }
    /// <summary>On-disk size when last read; compressed logs use it (with Mtime) to tell whether they changed.</summary>
    public long Size { get; set; }
    public LogState State { get; set; } = new();
}

/// <summary>
/// data\agents\usage-cache.json: per-file offsets + parser state and the hourly aggregates, so restarts are instant.
/// Shape: { "schema":1, "files": { path: {provider, offset, mtime, size, state} }, "seen": [ remembered responses ], "buckets": [ {h,p,m,j,t:[in,cw,cr,out,reason],c,s,r,u} ] }.
/// Never holds tokens, credentials, e-mail addresses, prompts, replies or raw responses.
/// </summary>
internal static class UsageCache
{
    private const int Schema = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void Save(string path, IReadOnlyDictionary<string, FileProgress> files, IReadOnlyDictionary<BucketKey, Bucket> buckets,
                            IReadOnlyList<SeenEntry>? seen = null)
    {
        var temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(temp))
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartObject("files");
            foreach (var (file, p) in files)
            {
                w.WriteStartObject(file);
                w.WriteString("provider", p.Provider.ToString());
                w.WriteNumber("offset", p.Offset);
                w.WriteNumber("mtime", p.Mtime);
                w.WriteNumber("size", p.Size);
                w.WritePropertyName("state");
                JsonSerializer.Serialize(w, p.State, Options);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartArray("buckets");
            foreach (var (k, b) in buckets)
            {
                w.WriteStartObject();
                w.WriteNumber("h", k.Hour);
                w.WriteString("p", k.Provider.ToString());
                w.WriteString("m", k.Model);
                w.WriteString("j", k.Project);
                w.WriteStartArray("t");
                foreach (var n in new[] { b.Tokens.Input, b.Tokens.CacheWrite, b.Tokens.CacheRead, b.Tokens.Output, b.Tokens.Reasoning }) w.WriteNumberValue(n);
                w.WriteEndArray();
                w.WriteNumber("c", b.Cost);
                w.WriteNumber("s", b.Savings);
                w.WriteNumber("r", b.Requests);
                w.WriteNumber("u", b.Unpriced);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // Remembered responses: [hash, hour, provider, model, project, in, cw, cr, out, reason, longCw, flags, web, model, cost|null].
            w.WriteStartArray("seen");
            foreach (var e in seen ?? [])
            {
                var t = e.Billable.Tokens;
                w.WriteStartArray();
                w.WriteNumberValue(e.Hash);
                w.WriteNumberValue(e.Key.Hour);
                w.WriteStringValue(e.Key.Provider.ToString());
                w.WriteStringValue(e.Key.Model);
                w.WriteStringValue(e.Key.Project);
                foreach (var n in new[] { t.Input, t.CacheWrite, t.CacheRead, t.Output, t.Reasoning, e.Billable.LongCacheWrite }) w.WriteNumberValue(n);
                w.WriteNumberValue((e.Billable.Fast ? 1 : 0) | (e.Billable.Domestic ? 2 : 0) | (e.Billable.IsAggregate ? 4 : 0));
                w.WriteNumberValue(e.Billable.WebSearches);
                w.WriteStringValue(e.Model);
                if (e.ReportedCost is { } c) w.WriteNumberValue(c); else w.WriteNullValue();
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    /// <summary>Empty when the file is missing, unreadable, or another schema: the logs are simply read again.</summary>
    public static (Dictionary<string, FileProgress> Files, List<(BucketKey Key, Bucket Bucket)> Buckets) Load(string path, List<SeenEntry>? seen = null)
    {
        var files = new Dictionary<string, FileProgress>(StringComparer.OrdinalIgnoreCase);
        var buckets = new List<(BucketKey, Bucket)>();
        try
        {
            if (!File.Exists(path)) return (files, buckets);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement? root = doc.RootElement;
            if (root.Dbl("schema") != Schema) return (files, buckets);
            if (root.Prop("files") is { ValueKind: JsonValueKind.Object } f)
                foreach (var p in f.EnumerateObject())
                {
                    JsonElement? e = p.Value;
                    if (!Enum.TryParse<AgentProvider>(e.Str("provider"), out var provider)) continue;
                    var state = e.Prop("state") is { } s ? s.Deserialize<LogState>(Options) ?? new() : new();
                    files[p.Name] = new FileProgress { Provider = provider, Offset = (long)(e.Dbl("offset") ?? 0), Mtime = (long)(e.Dbl("mtime") ?? 0), Size = (long)(e.Dbl("size") ?? 0), State = state };
                }
            if (root.Prop("buckets") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var e0 in arr.EnumerateArray())
                {
                    JsonElement? e = e0;
                    if (!Enum.TryParse<AgentProvider>(e.Str("p"), out var provider) || e.Prop("t") is not { ValueKind: JsonValueKind.Array } t) continue;
                    var n = t.EnumerateArray().Select(x => (long)(x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0)).ToArray();
                    if (n.Length < 5) continue;
                    buckets.Add((new BucketKey((long)(e.Dbl("h") ?? 0), provider, e.Str("m") ?? "", e.Str("j") ?? ""), new Bucket
                    {
                        Tokens = new AgentTokens(n[0], n[1], n[2], n[3], n[4]),
                        Cost = e.Dbl("c") ?? 0,
                        Savings = e.Dbl("s") ?? 0,
                        Requests = (int)(e.Dbl("r") ?? 0),
                        Unpriced = (int)(e.Dbl("u") ?? 0),
                    }));
                }
            if (seen is not null && root.Prop("seen") is { ValueKind: JsonValueKind.Array } sa)
                foreach (var r in sa.EnumerateArray())
                {
                    var a = r.EnumerateArray().ToArray();
                    if (a.Length != 15 || !Enum.TryParse<AgentProvider>(a[2].GetString(), out var provider)) continue;
                    var tokens = new AgentTokens(a[5].GetInt64(), a[6].GetInt64(), a[7].GetInt64(), a[8].GetInt64(), a[9].GetInt64());
                    var flags = a[11].GetInt32();
                    seen.Add(new SeenEntry(a[0].GetUInt64(), new BucketKey(a[1].GetInt64(), provider, a[3].GetString() ?? "", a[4].GetString() ?? ""),
                        new Billable(tokens, a[10].GetInt64(), (flags & 1) != 0, (flags & 2) != 0, a[12].GetInt32(), (flags & 4) != 0),
                        a[13].GetString() ?? "", a[14].ValueKind == JsonValueKind.Number ? a[14].GetDouble() : null));
                }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            files.Clear();
            buckets.Clear();
            seen?.Clear();
        }
        return (files, buckets);
    }
}
