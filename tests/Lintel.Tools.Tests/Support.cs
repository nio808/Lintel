// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Tools.Tests;

internal sealed class FakeTime(DateTimeOffset? start = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => Now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan by) => Now += by;
}
