// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers;

public enum PrinterState { Ready, Printing, Paused, Offline, Error, PaperOut }

public enum PortKind { Usb, Network, Other }

/// <summary>One installed printer as the spooler reports it.</summary>
public sealed record PrinterInfo(string Name, string Driver, string Port, PortKind Kind, PrinterState State, bool IsDefault, int JobCount)
{
    public bool IsOnline => State is not (PrinterState.Offline or PrinterState.Error);
}

[Flags]
public enum JobFlags
{
    None = 0, Paused = 0x1, Error = 0x2, Deleting = 0x4, Spooling = 0x8, Printing = 0x10, Offline = 0x20, PaperOut = 0x40,
    Printed = 0x80, Deleted = 0x100, BlockedDevQueue = 0x200, UserIntervention = 0x400, Restart = 0x800, Complete = 0x1000,
}

/// <summary>JOB_INFO_2 reduced to what the section needs. The owner is never read.</summary>
public sealed record JobInfo(int Id, string Printer, string Document, JobFlags Flags, int PagesPrinted, int TotalPages, int SizeBytes);

public sealed record SpoolerSnapshot(IReadOnlyList<PrinterInfo> Printers, IReadOnlyList<JobInfo> Jobs)
{
    public static SpoolerSnapshot Empty { get; } = new([], []);
}

/// <summary>The only thing the module needs from the spooler; the real one is Win32, tests and demo use fakes.</summary>
public interface IPrinterSource : IDisposable
{
    /// <summary>Raised on a background thread after any printer or job change (and by the fallback poll when something differs).</summary>
    event Action<SpoolerSnapshot>? Changed;

    void Start();

    /// <summary>Shown = poll every 30 s as a safety net; a job in flight = every 2 s; otherwise only notifications.</summary>
    void SetActivity(bool visible, bool jobActive);

    SpoolerSnapshot Read();
}

public static class PrinterRules
{
    private static readonly string[] VirtualNames =
        ["Microsoft Print to PDF", "Microsoft XPS", "OneNote", "Fax", "Snagit", "PDF Creator", "Adobe PDF", "CutePDF", "doPDF", "PDFill", "Send to"];

    private static readonly string[] VirtualPorts = ["PORTPROMPT:", "nul:", "FILE:", "XPSPort:", "SHRFAX:", "Microsoft Document"];

    /// <summary>Virtual printers print to a file or app, not paper: they have no nozzles to clean.</summary>
    public static bool IsVirtual(string name, string driver, string port) =>
        VirtualNames.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase) || driver.Contains(v, StringComparison.OrdinalIgnoreCase))
        || VirtualPorts.Any(v => port.StartsWith(v, StringComparison.OrdinalIgnoreCase));

    public static PortKind KindOf(string port, bool networkAttribute)
    {
        if (port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || port.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase))
            return PortKind.Usb;
        if (networkAttribute || port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) || port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase)
            || port.StartsWith(@"\\", StringComparison.Ordinal) || port.Length > 0 && char.IsDigit(port[0]))
            return PortKind.Network;
        return PortKind.Other;
    }

    /// <summary>PRINTER_STATUS_* and PRINTER_ATTRIBUTE_WORK_OFFLINE (0x400) to one state, most urgent first.</summary>
    public static PrinterState StateOf(uint status, uint attributes)
    {
        if ((attributes & 0x400) != 0 || (status & (0x80 | 0x1000 | 0x800000)) != 0) return PrinterState.Offline;
        if ((status & (0x10 | 0x40)) != 0) return PrinterState.PaperOut;
        if ((status & (0x2 | 0x8 | 0x100000 | 0x400000 | 0x200000 | 0x40000)) != 0) return PrinterState.Error;
        if ((status & 0x1) != 0) return PrinterState.Paused;
        if ((status & (0x400 | 0x4000 | 0x100 | 0x200)) != 0) return PrinterState.Printing;
        return PrinterState.Ready;
    }

    public static string Describe(PrinterState state) => state switch
    {
        PrinterState.Ready => "Ready", PrinterState.Printing => "Printing", PrinterState.Paused => "Paused",
        PrinterState.Offline => "Offline", PrinterState.Error => "Error", PrinterState.PaperOut => "Paper out", _ => "",
    };

    public static string Describe(PortKind kind) => kind switch { PortKind.Usb => "USB", PortKind.Network => "Network", _ => "Local" };

    /// <summary>Default printer first, then by name; virtual printers dropped.</summary>
    public static IReadOnlyList<PrinterInfo> Physical(IEnumerable<PrinterInfo> all) =>
        all.Where(p => !IsVirtual(p.Name, p.Driver, p.Port)).OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
