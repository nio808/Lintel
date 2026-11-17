// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Printers.Epson;

// Read-only probe: ids | status | counters  [printer name]. Hex of every exchange is printed.
var mode = args.Length > 0 ? args[0] : "ids";
var printer = args.Length > 1 ? args[1] : "EPSON L3250 Series";
if (mode == "lib")
{
    // public facade, read-only call
    Console.WriteLine(await EpsonDevice.ReadPadCountersAsync(printer, CancellationToken.None));
    Console.WriteLine(EpsonDevice.LimitsFor(printer));
    return 0;
}
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

Console.WriteLine($"== spooler: {printer}");
var f = SpoolerRaw.Query(printer);
if (f is null) { Console.WriteLine("printer not found"); return 1; }
Console.WriteLine($"driver='{f.Driver}' port='{f.Port}' jobs={f.Jobs} status=0x{f.Status:X}");

Console.WriteLine("== usbprint interfaces (Epson)");
var paths = UsbPrintDevice.EnumerateEpsonPaths();
foreach (var p in paths)
{
    Console.WriteLine(p);
    try
    {
        using var d = UsbPrintDevice.Open(p);
        Console.WriteLine("  open ok; 1284 id: " + (d.ReadDeviceId() ?? "(none)"));
    }
    catch (Exception e) { Console.WriteLine("  open failed: " + e.Message); }
}
if (mode == "ids") return 0;

if (f.Jobs != 0) { Console.WriteLine("REFUSING: queue not empty, no data is sent."); return 2; }
var model = EpsonModels.Find(f.Driver);
if (model is null) { Console.WriteLine("no model table entry"); return 3; }

static string Hex(byte[] b) => Convert.ToHexString(b);
void Log(string dir, byte[] b) => Console.WriteLine($"  {dir} {Hex(b)}");

foreach (var path in UsbPrintLocator.CandidatePaths(f.Driver))
{
    Console.WriteLine($"== D4 on {path}");
    UsbPrintDevice dev;
    try { dev = UsbPrintDevice.Open(path); } catch (Exception e) { Console.WriteLine("  open failed: " + e.Message); continue; }
    using var s = new D4Session(new Own(dev), cts.Token, Log);
    if (!s.Connect()) { Console.WriteLine("  D4 handshake failed on this interface"); continue; }
    Console.WriteLine("  D4 connected");
    if (mode == "status")
    {
        var r = s.Request(EpsonRemote.StatusRequest());
        Console.WriteLine(r is null ? "  no status reply" : "  status ASCII: " + new string(r.Select(c => c is >= 32 and < 127 ? (char)c : '.').ToArray()));
    }
    else
    {
        var e = new EpsonEeprom(s, model);
        var bytes = e.ReadBytes(model.CounterAddresses);
        if (bytes is null) { Console.WriteLine("  EEPROM read failed"); return 4; }
        foreach (var kv in bytes) Console.WriteLine($"  [0x{kv.Key:X3}] = 0x{kv.Value:X2}");
        Console.WriteLine("  " + EpsonEeprom.Decode(model, bytes));
    }
    return 0;
}
Console.WriteLine("no interface answered");
return 5;

sealed class Own(UsbPrintDevice d) : IEpsonTransport
{
    public void Write(byte[] data, int t, CancellationToken ct) => d.Write(data, t, ct);
    public byte[] Read(int max, int t, CancellationToken ct) => d.Read(max, t, ct);
    public void Dispose() => d.Dispose();
}
