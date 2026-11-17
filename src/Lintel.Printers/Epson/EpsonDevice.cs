// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Epson;

public sealed record EpsonPadCounters(double MainPercent, double? BorderlessPercent, int MainRaw, int? BorderlessRaw);

public enum EpsonMaintenance { NozzleCheck, HeadClean, PowerClean }

/// <summary>Outcome of a pad-counter reset, with the numbers the UI can show ("was 37 % - now 0 %").</summary>
public sealed record EpsonResetReport(bool Success, EpsonPadCounters? Before, EpsonPadCounters? After, string Message,
    IReadOnlyDictionary<int, byte> OriginalBytes);

/// <summary>
/// Talks to an Epson inkjet through Windows' own USB print interface (no driver swap, no elevation): waste-ink pad counters,
/// maintenance commands. Never sends anything while the printer's queue has jobs; never throws for device or I/O errors.
/// </summary>
public static class EpsonDevice
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Raised after every <see cref="ResetPadCountersAsync"/> attempt, success or not.</summary>
    public static event Action<EpsonResetReport>? ResetReported;

    public static bool IsSupported(string driverName) => EpsonModels.IsEpsonInkjet(driverName);

    public static bool CanReadPadCounters(string driverName) => EpsonModels.Find(driverName) is not null;

    public static bool CanResetPadCounters(string driverName) => EpsonModels.Find(driverName) is { CanReset: true };

    public static EpsonModelLimits? LimitsFor(string driverName) => EpsonModels.Find(driverName)?.Limits;

    public static Task<EpsonPadCounters?> ReadPadCountersAsync(string printerName, CancellationToken ct) =>
        Guarded<EpsonPadCounters?>(ct, null, () =>
        {
            using var s = Session(printerName, ct, out var model);
            return s is null ? null : new EpsonEeprom(s, model!).ReadCounters();
        });

    public static Task<bool> RunMaintenanceAsync(string printerName, EpsonMaintenance action, CancellationToken ct) =>
        Guarded(ct, false, () =>
        {
            var f = SpoolerRaw.Query(printerName);
            // the spooler path needs no usbprint handle, but the same rules apply: Epson inkjet, idle queue
            if (f is null || f.Jobs != 0 || !IsSupported(f.Driver)) return false;
            return SpoolerRaw.SendRaw(printerName, "Lintel maintenance", EpsonRemote.Maintenance(action));
        });

    /// <summary>Zeroes the waste counters of this model (its table's addresses only), then reads back. Listen to <see cref="ResetReported"/> for the numbers.</summary>
    public static Task<bool> ResetPadCountersAsync(string printerName, CancellationToken ct) =>
        Guarded(ct, false, () =>
        {
            using var s = Session(printerName, ct, out var model);
            if (s is null || model is not { CanReset: true }) return Report(false, null, null, "printer not reachable or model not resettable", new Dictionary<int, byte>());
            var eeprom = new EpsonEeprom(s, model);
            var before = eeprom.ReadCounters();
            if (before is null) return Report(false, null, null, "could not read the counters before the reset", new Dictionary<int, byte>());
            var ok = eeprom.ResetWaste(out var original, out var error);
            var after = eeprom.ReadCounters();
            ok = ok && after is { MainRaw: 0 } && (after.BorderlessRaw ?? 0) == 0;
            return Report(ok, before, after, ok ? "counters reset" : error.Length > 0 ? error : "counters did not read back as zero", original);
        });

    private static bool Report(bool ok, EpsonPadCounters? before, EpsonPadCounters? after, string msg, IReadOnlyDictionary<int, byte> original)
    {
        try { ResetReported?.Invoke(new EpsonResetReport(ok, before, after, msg, original)); } catch { /* a listener must not break the caller */ }
        return ok;
    }

    private static Task<T> Guarded<T>(CancellationToken ct, T failure, Func<T> work) => Task.Run(async () =>
    {
        try
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try { return work(); } finally { Gate.Release(); }
        }
        catch { return failure; }   // cancellation, device errors, spooler errors: "unreadable right now"
    }, CancellationToken.None);

    /// <summary>Finds the printer's usbprint interface and opens a D4 control session on it; null when not possible right now.</summary>
    private static D4Session? Session(string printerName, CancellationToken ct, out EpsonModel? model)
    {
        model = null;
        var f = SpoolerRaw.Query(printerName);
        if (f is null || f.Jobs != 0 || !f.Port.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return null;
        model = EpsonModels.Find(f.Driver);
        if (model is null) return null;
        foreach (var path in UsbPrintLocator.CandidatePaths(f.Driver))
        {
            ct.ThrowIfCancellationRequested();
            UsbPrintDevice dev;
            try { dev = UsbPrintDevice.Open(path); } catch { continue; }
            var s = new D4Session(new OwningTransport(dev), ct);
            if (s.Connect()) return s;
            s.Dispose();
        }
        return null;
    }

    // Disposes the usbprint handle together with the session.
    private sealed class OwningTransport(UsbPrintDevice d) : IEpsonTransport
    {
        public void Write(byte[] data, int timeoutMs, CancellationToken ct) => d.Write(data, timeoutMs, ct);
        public byte[] Read(int max, int timeoutMs, CancellationToken ct) => d.Read(max, timeoutMs, ct);
        public void Dispose() => d.Dispose();
    }
}

/// <summary>Maps a spooler driver name onto usbprint device paths through each device's IEEE-1284 ID.</summary>
internal static class UsbPrintLocator
{
    public static IEnumerable<string> CandidatePaths(string driverName)
    {
        foreach (var path in UsbPrintDevice.EnumerateEpsonPaths())
        {
            string? id;
            try { using var d = UsbPrintDevice.Open(path); id = d.ReadDeviceId(); }
            catch { continue; }
            if (MatchesDriver(id, driverName)) yield return path;
        }
    }

    /// <summary>"MFG:EPSON;MDL:L3250 Series;" matches the driver name "EPSON L3250 Series".</summary>
    public static bool MatchesDriver(string? id1284, string driverName)
    {
        var mfg = UsbPrintDevice.Field(id1284, "MFG") ?? UsbPrintDevice.Field(id1284, "MANUFACTURER");
        var mdl = UsbPrintDevice.Field(id1284, "MDL") ?? UsbPrintDevice.Field(id1284, "MODEL");
        return mfg is not null && mdl is not null
            && mfg.Equals("EPSON", StringComparison.OrdinalIgnoreCase)
            && driverName.Contains(mdl, StringComparison.OrdinalIgnoreCase);
    }
}
