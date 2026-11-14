// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Lintel.Agents.Logs;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class LogTailerTests : IDisposable
{
    private readonly string dir = T.TempDir();
    private string Path(string name = "a.jsonl") => System.IO.Path.Combine(dir, name);
    public void Dispose() => Directory.Delete(dir, true);

    private static (List<string> Lines, long Offset, bool Restarted) Read(string path, long offset)
    {
        var lines = new List<string>();
        var next = LogTailer.ReadLines(path, offset, l => lines.Add(Encoding.UTF8.GetString(l.Span)), out var restarted);
        return (lines, next, restarted);
    }

    [Fact]
    public void ReadsCompleteLinesAndLeavesAPartialLastLineForNextTime()
    {
        File.WriteAllText(Path(), "one\ntwo\npart");
        var (lines, offset, _) = Read(Path(), 0);
        Assert.Equal(["one", "two"], lines);
        Assert.Equal(8, offset);

        File.AppendAllText(Path(), "ial\nthree\n");
        (lines, offset, _) = Read(Path(), offset);
        Assert.Equal(["partial", "three"], lines);
        Assert.Equal(new FileInfo(Path()).Length, offset);

        (lines, _, _) = Read(Path(), offset);
        Assert.Empty(lines);
    }

    [Fact]
    public void CrLfAndBlankLinesAreHandled()
    {
        File.WriteAllText(Path(), "a\r\n\r\n\nb\r\n");
        Assert.Equal(["a", "b"], Read(Path(), 0).Lines);
    }

    [Fact]
    public void ATruncatedOrReplacedFileIsReadAgainFromTheStart()
    {
        File.WriteAllText(Path(), "first line here\nsecond line here\n");
        var offset = Read(Path(), 0).Offset;
        File.WriteAllText(Path(), "new\n");   // shorter than the remembered offset
        var (lines, next, restarted) = Read(Path(), offset);
        Assert.True(restarted);
        Assert.Equal(["new"], lines);
        Assert.Equal(4, next);
    }

    [Fact]
    public void ReadsWhileTheWriterStillHoldsTheFileOpen()
    {
        using var writer = new FileStream(Path(), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("live\n"u8);
        writer.Flush();
        Assert.Equal(["live"], Read(Path(), 0).Lines);
    }

    [Fact]
    public void LinesLongerThanTheInitialBufferAreReadWhole()
    {
        var big = new string('x', 3 << 20);
        File.WriteAllText(Path(), "s\n" + big + "\nend\n");
        var (lines, _, _) = Read(Path(), 0);
        Assert.Equal(3, lines.Count);
        Assert.Equal(big.Length, lines[1].Length);
        Assert.Equal("end", lines[2]);
    }

    [Fact]
    public void AMissingNewlineAtTheEndIsNeverConsumed()
    {
        File.WriteAllText(Path(), "done\nno-newline");
        var (lines, offset, _) = Read(Path(), 0);
        Assert.Equal(["done"], lines);
        Assert.Equal(5, offset);
    }

    [Fact]
    public void MultiByteUtf8StraddlingTheBufferIsNotCorrupted()
    {
        var text = string.Concat(Enumerable.Repeat("héllo wörld ✓ ", 90_000));   // > 1 MB with multi-byte characters
        File.WriteAllText(Path(), text + "\nz\n");
        var (lines, _, _) = Read(Path(), 0);
        Assert.Equal(text, lines[0]);
        Assert.Equal("z", lines[1]);
    }

    [Fact]
    public void AbsurdlyLongLinesAreSkippedNotHeld()
    {
        using (var f = File.Create(Path()))
        {
            f.Write("before\n"u8);
            var chunk = new byte[1 << 20];
            Array.Fill(chunk, (byte)'x');
            for (var i = 0; i < 70; i++) f.Write(chunk);   // one 70 MB line
            f.Write("\nafter\n"u8);
        }
        var (lines, offset, _) = Read(Path(), 0);
        Assert.Equal(["before", "after"], lines);
        Assert.Equal(new FileInfo(Path()).Length, offset);
    }

    [Fact]
    public void ACancelledReadStopsWithoutThrowing()
    {
        File.WriteAllText(Path(), "a\nb\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var offset = LogTailer.ReadLines(Path(), 0, _ => { }, out _, cts.Token);
        Assert.Equal(0, offset);
    }
}

public sealed class ProjectNameTests
{
    [Theory]
    [InlineData(@"C:\Users\me\work\app", "app")]
    [InlineData(@"C:\Users\me\work\app\", "app")]
    [InlineData("/home/u/proj", "proj")]
    [InlineData("/home/u/proj/", "proj")]
    [InlineData("app", "app")]
    [InlineData(@"C:\repo\.claude\worktrees\feature-x", "repo")]
    [InlineData(@"C:\repo\.claude\worktrees\feature-x\sub\dir", "repo")]
    [InlineData("/r/p/.claude/worktrees/abc", "p")]
    [InlineData("/r/p/.claude/worktrees", "p")]
    [InlineData(@"C:\a\.claude\worktreesX\b", "b")]
    [InlineData(@"C:\", "C:")]
    [InlineData("/", "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void LastPathComponent(string? path, string expected) => Assert.Equal(expected, ProjectName.From(path));
}

public sealed class OpenCodeDbReaderTests : IDisposable
{
    private readonly string dir = T.TempDir();
    private string Db => Path.Combine(dir, "opencode.db");
    public void Dispose() => Directory.Delete(dir, true);

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private WinSqlite Create(bool v2 = false)
    {
        var db = WinSqlite.OpenReadWrite(Db)!;
        Assert.NotNull(db);
        if (v2)
        {
            Assert.True(db.Exec("CREATE TABLE session_v2(id TEXT PRIMARY KEY, directory TEXT)"));
            Assert.True(db.Exec("CREATE TABLE session_message(id TEXT PRIMARY KEY, session_id TEXT, type TEXT, time_created INTEGER, time_updated INTEGER, data TEXT)"));
        }
        else
        {
            Assert.True(db.Exec("CREATE TABLE session(id TEXT PRIMARY KEY, directory TEXT)"));
            Assert.True(db.Exec("CREATE TABLE message(id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT)"));
        }
        return db;
    }

    private static void Session(WinSqlite db, string id, string dir, bool v2 = false) =>
        Assert.True(db.Exec($"INSERT INTO {(v2 ? "session_v2" : "session")} VALUES ('{id}','{dir.Replace("'", "''")}')"));

    private static void Message(WinSqlite db, string id, string session, DateTimeOffset created, string json, DateTimeOffset? updated = null, string? type = null)
    {
        var u = Ms(updated ?? created);
        var sql = type is null
            ? $"INSERT INTO message VALUES ('{id}','{session}',{Ms(created)},{u},'{json.Replace("'", "''")}')"
            : $"INSERT INTO session_message VALUES ('{id}','{session}','{type}',{Ms(created)},{u},'{json.Replace("'", "''")}')";
        Assert.True(db.Exec(sql));
    }

    private static string Assistant(string model = "claude-opus-5-5", int input = 100, int output = 50, int reasoning = 10, int read = 1000, int write = 200,
                                    double cost = 0.12, bool completed = true, string cwd = @"C:\\work\\oc-proj")
    {
        var done = completed ? ",\"completed\":2" : "";
        var c = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $$"""{"role":"assistant","modelID":"{{model}}","tokens":{"input":{{input}},"output":{{output}},"reasoning":{{reasoning}},"cache":{"read":{{read}},"write":{{write}} } },"cost":{{c}},"time":{"created":1{{done}} },"path":{"cwd":"{{cwd}}"} }""";
    }

    private static readonly DateTimeOffset Horizon = T.Now.AddDays(-30);

    [Fact]
    public void ReadsFinishedAssistantMessagesAsUsage()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\fallback\dir");
            Message(db, "m1", "ses1", T.Now.AddHours(-2), """{"role":"user","time":{"created":1}}""");
            Message(db, "m2", "ses1", T.Now.AddHours(-1), Assistant());
        }
        var r = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        Assert.True(r.Supported);
        var u = Assert.Single(r.Records);
        Assert.Equal("opencode:m2", u.Key);
        Assert.Equal(AgentProvider.OpenCode, u.Provider);
        Assert.Equal("claude-opus-5-5", u.Model);
        Assert.Equal("oc-proj", u.Project);                       // the reply's own cwd, folder name only
        Assert.Equal("ses1", u.Session);
        Assert.Equal(new AgentTokens(100, 200, 1000, 60, 10), u.Billable.Tokens);   // output includes reasoning
        Assert.Equal(0.12, u.ReportedCost);
        Assert.Equal(2, r.Rowid);
    }

    [Fact]
    public void ResumesAfterTheLastRowAndKeepsGoing()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\p\one");
            Message(db, "m1", "ses1", T.Now.AddHours(-3), Assistant());
        }
        var first = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        Assert.Single(first.Records);
        using (var db = WinSqlite.OpenReadWrite(Db)!)
            Message(db, "m2", "ses1", T.Now.AddHours(-2), Assistant(model: "gpt-6.1-sol", cwd: ""));
        var second = OpenCodeDbReader.Read(Db, first.Rowid, Horizon, T.Now);
        var u = Assert.Single(second.Records);
        Assert.Equal("opencode:m2", u.Key);
        Assert.Equal("one", u.Project);   // empty cwd: the session's directory
        Assert.Empty(OpenCodeDbReader.Read(Db, second.Rowid, Horizon, T.Now).Records);
    }

    [Fact]
    public void AReplyStillBeingWrittenIsReadAgainOnceItCompletes()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\p\one");
            Message(db, "m1", "ses1", T.Now.AddMinutes(-2), Assistant(), updated: T.Now.AddSeconds(-10));
            Message(db, "m2", "ses1", T.Now.AddMinutes(-2), Assistant(completed: false, output: 1), updated: T.Now.AddSeconds(-5));
            Message(db, "m3", "ses1", T.Now.AddMinutes(-1), Assistant());
        }
        var first = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        Assert.Equal(["opencode:m1"], first.Records.Select(r => r.Key));   // stops before the unfinished reply, m3 waits behind it
        Assert.Equal(1, first.Rowid);

        using (var db = WinSqlite.OpenReadWrite(Db)!)
            Assert.True(db.Exec($"UPDATE message SET data = '{Assistant(output: 77).Replace("'", "''")}' WHERE id = 'm2'"));
        var second = OpenCodeDbReader.Read(Db, first.Rowid, Horizon, T.Now);
        Assert.Equal(["opencode:m2", "opencode:m3"], second.Records.Select(r => r.Key));
        Assert.Equal(87, second.Records[0].Billable.Tokens.Output);
    }

    [Fact]
    public void AnAbandonedUnfinishedReplyDoesNotBlockTheQueue()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\p\one");
            Message(db, "m1", "ses1", T.Now.AddHours(-1), Assistant(completed: false, output: 5), updated: T.Now.AddMinutes(-30));
            Message(db, "m2", "ses1", T.Now.AddMinutes(-5), Assistant());
        }
        var r = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        Assert.Equal(2, r.Records.Count);
        Assert.Equal(2, r.Rowid);
    }

    [Fact]
    public void FirstReadSkipsMessagesOlderThanTheHorizon()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\p\one");
            Message(db, "old1", "ses1", T.Now.AddDays(-60), Assistant());
            Message(db, "old2", "ses1", T.Now.AddDays(-45), Assistant());
            Message(db, "new1", "ses1", T.Now.AddDays(-2), Assistant());
        }
        var r = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        Assert.Equal(["opencode:new1"], r.Records.Select(x => x.Key));
    }

    [Fact]
    public void ARecreatedDatabaseIsReadFromTheStart()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\p\one");
            Message(db, "m1", "ses1", T.Now.AddHours(-1), Assistant());
        }
        var r = OpenCodeDbReader.Read(Db, 5000, Horizon, T.Now);   // remembered rowid is beyond the new table
        Assert.Single(r.Records);
    }

    [Fact]
    public void ErrorsAndEmptyUsageProduceNothingAndUnknownModelsKeepTheirRecordedCost()
    {
        using (var db = Create())
        {
            Session(db, "ses1", @"C:\p\one");
            Message(db, "m1", "ses1", T.Now.AddHours(-1), """{"role":"assistant","modelID":"x","time":{"created":1,"completed":2}}""");
            Message(db, "m2", "ses1", T.Now.AddHours(-1), Assistant(model: "totally-unknown", cost: 0.5));
        }
        var r = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        var u = Assert.Single(r.Records);
        Assert.Equal(0.5, u.ReportedCost);
    }

    [Fact]
    public void Version2TablesAreRecognised()
    {
        using (var db = Create(v2: true))
        {
            Session(db, "ses1", @"C:\p\two", v2: true);
            Message(db, "m1", "ses1", T.Now.AddHours(-1), Assistant(cwd: ""), type: "assistant");
            Message(db, "m2", "ses1", T.Now.AddHours(-1), """{"role":"user"}""", type: "user");
        }
        var r = OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        var u = Assert.Single(r.Records);
        Assert.Equal("two", u.Project);
    }

    [Fact]
    public void UnknownSchemaIsReportedAsUnsupportedAndAMissingFileIsQuiet()
    {
        using (var db = WinSqlite.OpenReadWrite(Db)!)
            Assert.True(db.Exec("CREATE TABLE something_else(x)"));
        Assert.False(OpenCodeDbReader.Read(Db, 0, Horizon, T.Now).Supported);
        var missing = OpenCodeDbReader.Read(Path.Combine(dir, "nope.db"), 0, Horizon, T.Now);
        Assert.Empty(missing.Records);
        File.WriteAllText(Path.Combine(dir, "junk.db"), "this is not a database at all, just text padding padding padding padding");
        Assert.Empty(OpenCodeDbReader.Read(Path.Combine(dir, "junk.db"), 0, Horizon, T.Now).Records);
    }

    [Fact]
    public void TheDatabaseIsOpenedReadOnly()
    {
        using (var db = Create())
            Session(db, "ses1", "d");
        var before = File.GetLastWriteTimeUtc(Db);
        var length = new FileInfo(Db).Length;
        OpenCodeDbReader.Read(Db, 0, Horizon, T.Now);
        Assert.Equal(length, new FileInfo(Db).Length);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Db));
        using var ro = WinSqlite.OpenReadOnly(Db)!;
        Assert.False(ro.Exec("INSERT INTO session VALUES ('x','y')"));   // a read-only handle refuses writes
    }
}
