// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Lintel.Printers.Epson;

/// <summary>Minimal winspool access: printer facts (driver, port, queued jobs) and sending one RAW job.</summary>
internal static class SpoolerRaw
{
    internal sealed record Facts(string Driver, string Port, int Jobs, uint Status);

    public static Facts? Query(string printerName)
    {
        if (!OpenPrinterW(printerName, out var h, IntPtr.Zero)) return null;
        try
        {
            GetPrinterW(h, 2, IntPtr.Zero, 0, out var need);
            if (need == 0) return null;
            var buf = Marshal.AllocHGlobal((int)need);
            try
            {
                if (!GetPrinterW(h, 2, buf, need, out _)) return null;
                var i = Marshal.PtrToStructure<PrinterInfo2>(buf);
                return new Facts(Marshal.PtrToStringUni(i.pDriverName) ?? "", Marshal.PtrToStringUni(i.pPortName) ?? "", (int)i.cJobs, i.Status);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>Sends <paramref name="data"/> as one RAW document. False when the spooler refuses any step.</summary>
    public static bool SendRaw(string printerName, string docName, byte[] data)
    {
        if (!OpenPrinterW(printerName, out var h, IntPtr.Zero)) return false;
        try
        {
            var doc = new DocInfo1 { pDocName = docName, pOutputFile = null, pDatatype = "RAW" };
            if (StartDocPrinterW(h, 1, ref doc) == 0) return false;
            try
            {
                if (!StartPagePrinter(h)) return false;
                var ok = WritePrinter(h, data, data.Length, out var written) && written == data.Length;
                EndPagePrinter(h);
                return ok;
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1 { public string pDocName; public string? pOutputFile; public string pDatatype; }

    // PRINTER_INFO_2W
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation, pDevMode,
            pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string name, out IntPtr h, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr h);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinterW(IntPtr h, uint level, IntPtr buf, uint size, out uint needed);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartDocPrinterW(IntPtr h, uint level, ref DocInfo1 doc);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr h);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr h);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr h);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr h, byte[] buf, int n, out int written);
}
