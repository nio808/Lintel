// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;

namespace Lintel.Printers.Spooler;

/// <summary>The Win32 print spooler (winspool.drv). Read-only: it enumerates printers and jobs and waits for change notifications.</summary>
internal static class SpoolerNative
{
    public const uint PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4;
    public const uint ChangePrinter = 0x000000FF, ChangePrinterAndJob = 0x0000FFFF;
    public const uint PRINTER_ATTRIBUTE_NETWORK = 0x10;
    public const uint INFINITE = 0xFFFFFFFF, WAIT_TIMEOUT = 0x102, WAIT_FAILED = 0xFFFFFFFF;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string? name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumJobs(IntPtr printer, uint first, uint count, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDefaultPrinter(StringBuilder? buffer, ref uint size);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern IntPtr FindFirstPrinterChangeNotification(IntPtr printer, uint filter, uint options, IntPtr notifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool FindNextPrinterChangeNotification(IntPtr change, out uint changed, IntPtr options, IntPtr info);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool FindClosePrinterChangeNotification(IntPtr change);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    public struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode, SepFile, PrintProcessor,
            Datatype, Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobInfo2
    {
        public uint JobId;
        public IntPtr PrinterName, MachineName, UserName, Document, NotifyName, Datatype, PrintProcessor, Parameters, DriverName, DevMode, Status, SecurityDescriptor;
        public uint StatusFlags, Priority, Position, StartTime, UntilTime, TotalPages, Size;
        public ushort Y, Mo, Dow, D, H, Mi, S, Ms;   // Submitted (SYSTEMTIME)
        public uint Time, PagesPrinted;
    }
}

/// <summary>
/// Change-driven spooler source: one background thread waits on FindFirstPrinterChangeNotification handles (server + every printer)
/// and only polls as a safety net: 2 s while a job is in flight, 30 s while the section is open, 60 s otherwise.
/// </summary>
public sealed class WinSpoolerSource : IPrinterSource
{
    private readonly ManualResetEvent stop = new(false), wake = new(false);
    private Thread? thread;
    private volatile bool visible, jobActive;
    private string signature = "";

    public event Action<SpoolerSnapshot>? Changed;

    public void Start()
    {
        if (thread is not null)
            return;
        thread = new Thread(Loop) { IsBackground = true, Name = "Lintel.Printers.Spooler" };
        thread.Start();
    }

    public void SetActivity(bool visible, bool jobActive)
    {
        this.visible = visible;
        this.jobActive = jobActive;
        wake.Set();   // re-evaluate the timeout right away
    }

    public SpoolerSnapshot Read() => ReadSnapshot();

    public void Dispose()
    {
        stop.Set();
        thread?.Join(2000);
        stop.Dispose();
        wake.Dispose();
    }

    private void Loop()
    {
        var server = IntPtr.Zero;
        var handles = new Dictionary<string, (IntPtr Printer, IntPtr Notify)>();
        IntPtr serverNotify = IntPtr.Zero;
        try
        {
            if (SpoolerNative.OpenPrinter(null, out server, IntPtr.Zero))
                serverNotify = SpoolerNative.FindFirstPrinterChangeNotification(server, SpoolerNative.ChangePrinter, 0, IntPtr.Zero);
            if (serverNotify == new IntPtr(-1))
                serverNotify = IntPtr.Zero;
            while (true)
            {
                var snapshot = ReadSnapshot();
                var sig = Signature(snapshot);
                if (sig != signature)
                {
                    signature = sig;
                    Changed?.Invoke(snapshot);
                }
                Sync(handles, snapshot.Printers);

                var list = new List<IntPtr> { stop.SafeWaitHandle.DangerousGetHandle(), wake.SafeWaitHandle.DangerousGetHandle() };
                if (serverNotify != IntPtr.Zero)
                    list.Add(serverNotify);
                var notifies = handles.Values.Where(h => h.Notify != IntPtr.Zero).Select(h => h.Notify).Take(60).ToList();
                list.AddRange(notifies);
                var timeout = jobActive ? 2000u : visible ? 30000u : 60000u;
                var r = SpoolerNative.WaitForMultipleObjects((uint)list.Count, list.ToArray(), false, timeout);
                if (r == 0 || r == SpoolerNative.WAIT_FAILED)
                    break;
                if (r == 1)
                    wake.Reset();
                else if (r >= 2 && r < 2 + list.Count - 2)
                    SpoolerNative.FindNextPrinterChangeNotification(list[(int)r], out _, IntPtr.Zero, IntPtr.Zero);
                if (stop.WaitOne(150))   // coalesce the burst of notifications one print job causes
                    break;
            }
        }
        catch (Exception)
        {
            // A broken spooler must not take the app down; the section just stops updating.
        }
        finally
        {
            foreach (var h in handles.Values)
                Close(h);
            if (serverNotify != IntPtr.Zero)
                SpoolerNative.FindClosePrinterChangeNotification(serverNotify);
            if (server != IntPtr.Zero)
                SpoolerNative.ClosePrinter(server);
        }
    }

    private static void Sync(Dictionary<string, (IntPtr Printer, IntPtr Notify)> handles, IReadOnlyList<PrinterInfo> printers)
    {
        var names = printers.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in handles.Keys.Where(k => !names.Contains(k)).ToList())
        {
            Close(handles[gone]);
            handles.Remove(gone);
        }
        foreach (var name in names.Where(n => !handles.ContainsKey(n)))
        {
            if (!SpoolerNative.OpenPrinter(name, out var h, IntPtr.Zero))
                continue;
            var n = SpoolerNative.FindFirstPrinterChangeNotification(h, SpoolerNative.ChangePrinterAndJob, 0, IntPtr.Zero);
            handles[name] = (h, n == new IntPtr(-1) ? IntPtr.Zero : n);
        }
    }

    private static void Close((IntPtr Printer, IntPtr Notify) h)
    {
        if (h.Notify != IntPtr.Zero)
            SpoolerNative.FindClosePrinterChangeNotification(h.Notify);
        if (h.Printer != IntPtr.Zero)
            SpoolerNative.ClosePrinter(h.Printer);
    }

    private static string Signature(SpoolerSnapshot s) =>
        string.Join("|", s.Printers.Select(p => $"{p.Name};{p.State};{p.IsDefault};{p.JobCount}"))
        + "#" + string.Join("|", s.Jobs.Select(j => $"{j.Printer};{j.Id};{(int)j.Flags};{j.PagesPrinted};{j.TotalPages}"));

    /// <summary>All printers (virtual ones included; the module filters) and the jobs of those with a non-empty queue.</summary>
    public static SpoolerSnapshot ReadSnapshot()
    {
        var printers = new List<PrinterInfo>();
        var jobs = new List<JobInfo>();
        var def = DefaultPrinter();
        foreach (var raw in EnumPrinters2())
        {
            var (name, driver, port) = (raw.Name, raw.Driver, raw.Port);
            var info = new PrinterInfo(name, driver, port, PrinterRules.KindOf(port, (raw.Attributes & SpoolerNative.PRINTER_ATTRIBUTE_NETWORK) != 0),
                PrinterRules.StateOf(raw.Status, raw.Attributes), string.Equals(name, def, StringComparison.OrdinalIgnoreCase), (int)raw.Jobs);
            printers.Add(info);
            if (raw.Jobs > 0 && !PrinterRules.IsVirtual(name, driver, port))
                jobs.AddRange(EnumJobs2(name));
        }
        return new SpoolerSnapshot(printers, jobs);
    }

    private static string? DefaultPrinter()
    {
        uint size = 0;
        SpoolerNative.GetDefaultPrinter(null, ref size);
        if (size == 0)
            return null;
        var sb = new StringBuilder((int)size);
        return SpoolerNative.GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    private sealed record RawPrinter(string Name, string Driver, string Port, uint Attributes, uint Status, uint Jobs);

    private static List<RawPrinter> EnumPrinters2()
    {
        var result = new List<RawPrinter>();
        const uint flags = SpoolerNative.PRINTER_ENUM_LOCAL | SpoolerNative.PRINTER_ENUM_CONNECTIONS;
        SpoolerNative.EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return result;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!SpoolerNative.EnumPrinters(flags, null, 2, buffer, needed, out _, out var count))
                return result;
            var size = Marshal.SizeOf<SpoolerNative.PrinterInfo2>();
            for (var i = 0; i < count; i++)
            {
                var p = Marshal.PtrToStructure<SpoolerNative.PrinterInfo2>(buffer + i * size);
                result.Add(new RawPrinter(Marshal.PtrToStringUni(p.PrinterName) ?? "", Marshal.PtrToStringUni(p.DriverName) ?? "",
                    Marshal.PtrToStringUni(p.PortName) ?? "", p.Attributes, p.Status, p.Jobs));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static List<JobInfo> EnumJobs2(string printer)
    {
        var result = new List<JobInfo>();
        if (!SpoolerNative.OpenPrinter(printer, out var h, IntPtr.Zero))
            return result;
        try
        {
            SpoolerNative.EnumJobs(h, 0, 100, 2, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
                return result;
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!SpoolerNative.EnumJobs(h, 0, 100, 2, buffer, needed, out _, out var count))
                    return result;
                var size = Marshal.SizeOf<SpoolerNative.JobInfo2>();
                for (var i = 0; i < count; i++)
                {
                    var j = Marshal.PtrToStructure<SpoolerNative.JobInfo2>(buffer + i * size);
                    result.Add(new JobInfo((int)j.JobId, printer, Marshal.PtrToStringUni(j.Document) ?? "", (JobFlags)j.StatusFlags,
                        (int)j.PagesPrinted, (int)j.TotalPages, (int)Math.Min(j.Size, int.MaxValue)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            SpoolerNative.ClosePrinter(h);
        }
        return result;
    }
}
