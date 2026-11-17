// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers;

/// <summary>--demo and screenshots: two canned printers and a job that advances a page every 6 s. Touches no spooler.</summary>
public sealed class DemoSource : IPrinterSource
{
    public const string Epson = "EPSON L3250 Series";
    private Timer? timer;
    private int tick;

    public event Action<SpoolerSnapshot>? Changed;

    public void Start() => timer ??= new Timer(_ =>
    {
        tick++;
        Changed?.Invoke(Read());
    }, null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));

    public void SetActivity(bool visible, bool jobActive) { }

    public SpoolerSnapshot Read()
    {
        var step = tick % 11;   // 0..9: pages 3..12 printing, 10: between jobs
        List<JobInfo> jobs = step == 10 ? [] : [new JobInfo(41, Epson, "Quarterly report.pdf", JobFlags.Printing, 3 + step, 12, 2_400_000)];
        return new SpoolerSnapshot(
        [
            new PrinterInfo(Epson, Epson, "USB001", PortKind.Usb, step == 10 ? PrinterState.Ready : PrinterState.Printing, true, jobs.Count),
            new PrinterInfo("Office Laser HL-L2350", "Brother HL-L2350DW series", "IP_192.168.1.40", PortKind.Network, PrinterState.Offline, false, 0),
        ], jobs);
    }

    public void Dispose() => timer?.Dispose();

    public static PrinterStats SampleStats(DateTimeOffset now) => new()
    {
        LifetimePages = 1284, PagesSinceHeadClean = 312, PagesSincePowerClean = 1284, LastPrint = now.AddDays(-3),
        LastHeadClean = now.AddDays(-20), HeadCleans = 4, NozzleChecks = 6, PowerCleans = 0, LearnedPagesPerMinute = 8,
        LastPadReset = now.AddDays(-250), PadPercentBeforeReset = 88, LastPadPercent = 37, LastPadRead = now.AddDays(-1),
    };
}

/// <summary>Pretends to be an Epson: reads 37 %, accepts every command, sends nothing anywhere.</summary>
public sealed class DemoMaintenance : IPrinterMaintenance
{
    public bool IsSupported(string driver) => driver.StartsWith("EPSON", StringComparison.OrdinalIgnoreCase);
    public bool CanReadPad(string driver) => IsSupported(driver);
    public bool CanResetPad(string driver) => IsSupported(driver);
    public ModelLimits? LimitsFor(string driver) => IsSupported(driver) ? new ModelLimits(6000, null, null) : null;
    public Task<PadReading?> ReadPadAsync(string printer, CancellationToken ct) => Task.FromResult<PadReading?>(new PadReading(37, null, 2220, null));
    public Task<bool> RunAsync(string printer, MaintenanceAction action, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ResetPadAsync(string printer, CancellationToken ct) => Task.FromResult(true);
}
