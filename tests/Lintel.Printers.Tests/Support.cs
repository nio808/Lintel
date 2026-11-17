// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Tests;

internal sealed class FakeTime(DateTimeOffset? start = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeDevice : IPrinterMaintenance
{
    public bool Supported = true, Reset = true, SendOk = true;
    public List<string> Sent { get; } = [];
    public bool IsSupported(string driver) => Supported;
    public bool CanReadPad(string driver) => Supported;
    public bool CanResetPad(string driver) => Reset;
    public ModelLimits? LimitsFor(string driver) => null;
    public Task<PadReading?> ReadPadAsync(string printer, CancellationToken ct) => Task.FromResult<PadReading?>(null);
    public Task<bool> RunAsync(string printer, MaintenanceAction action, CancellationToken ct) { Sent.Add(action.ToString()); return Task.FromResult(SendOk); }
    public Task<bool> ResetPadAsync(string printer, CancellationToken ct) { Sent.Add("ResetPad"); return Task.FromResult(SendOk); }
}
