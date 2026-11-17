// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Lintel.Printers.Epson;

/// <summary>Facts about a model's waste-ink pads and maintenance guidance. Null means "no sourced value".</summary>
/// <param name="PadCounterMax">Raw main-pad counter value that equals 100 %.</param>
/// <param name="BorderlessPadCounterMax">Raw borderless-pad counter value that equals 100 %.</param>
/// <param name="Notes">Sourced maintenance guidance, see SOURCES.md.</param>
public sealed record EpsonModelLimits(int? PadCounterMax, int? BorderlessPadCounterMax, string? Notes);

/// <summary>One waste counter: little-endian bytes at <see cref="Low"/>, <see cref="Mid"/>, plus <see cref="High"/> masked/shifted.</summary>
internal sealed record PadCounterDef(int Low, int Mid, int High, byte HighMask, int HighShift, int Max)
{
    public int[] Addresses => [Low, Mid, High];

    public int Decode(byte low, byte mid, byte high) => low | (mid << 8) | (((high >> HighShift) & HighMask) << 16);
}

internal sealed record EpsonModel(
    string Name,
    string[] DriverTokens,
    byte[] ReadKey,
    PadCounterDef Main,
    PadCounterDef? Borderless,
    // null write key = this table entry is read-only
    byte[]? WriteKey,
    (int Addr, byte Value)[] ResetWrites,
    (int Addr, byte AndMask)? ResetCommit,
    EpsonModelLimits Limits)
{
    public bool CanReset => WriteKey is not null && ResetWrites.Length > 0;

    /// <summary>Every EEPROM address a read of the pad counters touches.</summary>
    public int[] CounterAddresses =>
        Main.Addresses.Concat(Borderless?.Addresses ?? []).Distinct().ToArray();
}

internal static class EpsonModels
{
    // Epson's own user guide: see SOURCES.md for the URLs behind each statement.
    private const string L3250Notes =
        "Head cleaning uses ink and fills the waste pads. If quality does not improve after up to 3 head cleanings, switch the printer off and wait at least 12 hours. "
        + "Power Cleaning uses a lot of ink and shortens pad life: only after that, each ink tank at least one-third full, at least 12 hours between power cleanings. "
        + "Ink used / counter increment per cleaning: not published by Epson.";

    private static readonly EpsonModelLimits L3250Limits = new(6346, 3416, L3250Notes);

    // Data: devices.xml block "L3250" (ez-reset) cross-checked with epson_print_conf ET-2812 profile; see SOURCES.md.
    private static readonly (int, byte)[] L3250Reset =
    [
        (0x30, 0), (0x31, 0), (0x2F, 0), (0x34, 0), (0x35, 0), (0x36, 0x5E),
        (0x32, 0), (0x33, 0), (0x37, 0x5E), (0x1C, 0), (0xFC, 0), (0xFD, 0), (0xFE, 0), (0xFF, 0x5E),
    ];

    private static readonly byte[] L3250ReadKey = [0x4A, 0x36];
    private static readonly byte[] MaribayaKey = Encoding.ASCII.GetBytes("Maribaya");

    private static readonly PadCounterDef L3250Main = new(0x30, 0x31, 0x2F, 0x0F, 0, 6346);
    private static readonly PadCounterDef L3250Border = new(0x32, 0x33, 0x2F, 0x0F, 4, 3416);

    public static readonly IReadOnlyList<EpsonModel> All =
    [
        new("L3250 family", ["L3250", "L3251", "L3253", "L3255", "L3256", "L3257", "L3258", "L3259",
                             "ET-2400", "ET-2401", "ET-2402", "ET-2403", "ET-2404", "ET-2405", "ET-2406", "ET-2408",
                             "ET-2810", "ET-2811", "ET-2812", "ET-2813", "ET-2814", "ET-2815", "ET-2816", "ET-2818"],
            L3250ReadKey, L3250Main, L3250Border, MaribayaKey, L3250Reset, (0x100, 0xFE), L3250Limits),
        // epson_print_conf L3150 (aliases L3151/L3160/L3166/L3168): keys and counters only; its reset table is not copied, so read-only.
        new("L3150 family", ["L3150", "L3151", "L3160", "L3166", "L3168"],
            [151, 7], new PadCounterDef(48, 49, 47, 0xFF, 0, 6346), new PadCounterDef(50, 51, 47, 0xFF, 0, 3416),
            null, [], null, new EpsonModelLimits(6346, 3416, "Divisors from epson_print_conf (63.46 / 34.16); no Epson guidance recorded for this model.")),
    ];

    public static EpsonModel? Find(string? driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName)) return null;
        foreach (var m in All)
            foreach (var t in m.DriverTokens)
                if (ContainsToken(driverName, t)) return m;
        return null;
    }

    // "L3250" must not match "L32500": the character after the token may not be a digit.
    private static bool ContainsToken(string name, string token)
    {
        for (var i = name.IndexOf(token, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = name.IndexOf(token, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + token.Length;
            var before = i == 0 || !char.IsLetterOrDigit(name[i - 1]);
            var after = end >= name.Length || !char.IsDigit(name[end]);
            if (before && after) return true;
        }
        return false;
    }

    private static readonly string[] NotInkjet = ["TM-", "TM ", "LQ-", "LX-", "FX-", "DFX", "PLQ", "ColorWorks", "SureColor", "Stylus Pro", "Fax", "Receipt"];

    /// <summary>Epson inkjet driver names the ESC/P-R remote-mode sequences are meant for.</summary>
    public static bool IsEpsonInkjet(string? driverName) =>
        driverName is not null
        && driverName.TrimStart().StartsWith("EPSON", StringComparison.OrdinalIgnoreCase)
        && !NotInkjet.Any(x => driverName.Contains(x, StringComparison.OrdinalIgnoreCase));
}
