// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using Lintel.Agents.Logs;

namespace Lintel.Agents.Tests;

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
    public void Set(DateTimeOffset value) => now = value;
}

internal static class T
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string[] FixtureLines(string name) =>
        Fixture(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Feeds lines through a reader, collecting every entry.</summary>
    public static List<LogEntry> Parse(ILogReader reader, LogState state, IEnumerable<string> lines, DateTimeOffset? now = null)
    {
        var all = new List<LogEntry>();
        foreach (var line in lines)
            reader.Parse(Encoding.UTF8.GetBytes(line), state, now ?? Now, all);
        return all;
    }

    public static List<LogEntry> ParseFixture(ILogReader reader, string fixture, LogState? state = null) =>
        Parse(reader, state ?? new LogState(), FixtureLines(fixture));

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lintel-agents-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
