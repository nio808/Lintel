// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Printers.Epson;

namespace Lintel.Printers;

public enum MaintenanceAction { NozzleCheck, HeadClean, PowerClean }

/// <param name="MainPercent">0-100+ of the main waste ink pad.</param>
public sealed record PadReading(double MainPercent, double? BorderlessPercent, int MainRaw, int? BorderlessRaw)
{
    /// <summary>The worse of the two pads.</summary>
    public double Worst => Math.Max(MainPercent, BorderlessPercent ?? 0);
}

public sealed record ModelLimits(int? PadCounterMax, int? BorderlessPadCounterMax, string? Notes);

/// <summary>Everything that talks to the printer itself. Non-Epson printers get <see cref="NoMaintenance"/>; tests fake it.</summary>
public interface IPrinterMaintenance
{
    bool IsSupported(string driver);
    bool CanReadPad(string driver);
    bool CanResetPad(string driver);
    ModelLimits? LimitsFor(string driver);

    /// <summary>Null when unreadable right now. Never throws for device errors.</summary>
    Task<PadReading?> ReadPadAsync(string printer, CancellationToken ct);

    /// <summary>False when the command could not be sent.</summary>
    Task<bool> RunAsync(string printer, MaintenanceAction action, CancellationToken ct);

    Task<bool> ResetPadAsync(string printer, CancellationToken ct);
}

public sealed class NoMaintenance : IPrinterMaintenance
{
    public bool IsSupported(string driver) => false;
    public bool CanReadPad(string driver) => false;
    public bool CanResetPad(string driver) => false;
    public ModelLimits? LimitsFor(string driver) => null;
    public Task<PadReading?> ReadPadAsync(string printer, CancellationToken ct) => Task.FromResult<PadReading?>(null);
    public Task<bool> RunAsync(string printer, MaintenanceAction action, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> ResetPadAsync(string printer, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Adapter over the Epson implementation.</summary>
public sealed class EpsonMaintenanceAdapter : IPrinterMaintenance
{
    public bool IsSupported(string driver) => EpsonDevice.IsSupported(driver);
    public bool CanReadPad(string driver) => EpsonDevice.CanReadPadCounters(driver);
    public bool CanResetPad(string driver) => EpsonDevice.CanResetPadCounters(driver);

    public ModelLimits? LimitsFor(string driver) =>
        EpsonDevice.LimitsFor(driver) is { } l ? new ModelLimits(l.PadCounterMax, l.BorderlessPadCounterMax, l.Notes) : null;

    public async Task<PadReading?> ReadPadAsync(string printer, CancellationToken ct) =>
        await EpsonDevice.ReadPadCountersAsync(printer, ct).ConfigureAwait(false) is { } c
            ? new PadReading(c.MainPercent, c.BorderlessPercent, c.MainRaw, c.BorderlessRaw) : null;

    public Task<bool> RunAsync(string printer, MaintenanceAction action, CancellationToken ct) =>
        EpsonDevice.RunMaintenanceAsync(printer, action switch
        {
            MaintenanceAction.NozzleCheck => EpsonMaintenance.NozzleCheck,
            MaintenanceAction.HeadClean => EpsonMaintenance.HeadClean,
            _ => EpsonMaintenance.PowerClean,
        }, ct);

    public Task<bool> ResetPadAsync(string printer, CancellationToken ct) => EpsonDevice.ResetPadCountersAsync(printer, ct);
}

public static class PadMath
{
    /// <summary>Raw counter over the model's known maximum when both exist (exact), else the percent the device layer gave.</summary>
    public static double Percent(double reportedPercent, int? raw, int? max) =>
        raw is { } r && max is > 0 ? r * 100.0 / max.Value : reportedPercent;

    /// <summary>The worse pad of a reading, judged against the model's limits when known.</summary>
    public static double Worst(PadReading reading, ModelLimits? limits)
    {
        var main = Percent(reading.MainPercent, reading.MainRaw, limits?.PadCounterMax);
        var border = reading.BorderlessPercent is { } b ? Percent(b, reading.BorderlessRaw, limits?.BorderlessPadCounterMax) : 0;
        return Math.Max(main, border);
    }
}
