// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lintel.Printers.Epson;

/// <summary>Byte builders and parsers for the EPSON-CTRL channel and for remote-mode maintenance commands. Pure functions.</summary>
internal static partial class EpsonRemote
{
    // ---- EPSON-CTRL frames: 2-char name, LE16 payload length, payload (epson_print_conf / epson-usb) ----

    public static byte[] Frame(string name, ReadOnlySpan<byte> payload)
    {
        if (name.Length != 2) throw new ArgumentException("command name is 2 characters", nameof(name));
        var f = new byte[4 + payload.Length];
        f[0] = (byte)name[0]; f[1] = (byte)name[1];
        f[2] = (byte)(payload.Length & 0xFF); f[3] = (byte)(payload.Length >> 8);
        payload.CopyTo(f.AsSpan(4));
        return f;
    }

    /// <summary>"st 01 00 01": status block request.</summary>
    public static byte[] StatusRequest() => Frame("st", [0x01]);

    /// <summary>Opcode letter, its complement, and the rotated form: 'A' gives 41 BE A0.</summary>
    internal static byte[] Opcode(char letter) =>
        [(byte)letter, (byte)(~letter & 0xFF), (byte)(((letter >> 1) & 0x7F) | ((letter << 7) & 0x80))];

    /// <summary>"|| 07 00 k1 k2 41 BE A0 lo hi": read one EEPROM byte.</summary>
    public static byte[] EepromRead(byte[] readKey, int address)
    {
        CheckAddress(address);
        byte[] p = [readKey[0], readKey[1], .. Opcode('A'), (byte)(address & 0xFF), (byte)(address >> 8)];
        return Frame("||", p);
    }

    /// <summary>"|| 10 00 k1 k2 42 BD 21 lo hi value wkey+1": write one EEPROM byte. Only <see cref="EpsonEeprom"/> may call this.</summary>
    internal static byte[] EepromWrite(byte[] readKey, byte[] writeKey, int address, byte value)
    {
        CheckAddress(address);
        // epson_print_conf "caesar": every non-zero write-key byte is sent plus one.
        var wk = writeKey.Select(b => b == 0 ? (byte)0 : (byte)(b + 1));
        byte[] p = [readKey[0], readKey[1], .. Opcode('B'), (byte)(address & 0xFF), (byte)(address >> 8), value, .. wk];
        return Frame("||", p);
    }

    private static void CheckAddress(int a)
    {
        if (a is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(a));
    }

    [GeneratedRegex("EE:([0-9A-Fa-f]{4})([0-9A-Fa-f]{2})")]
    private static partial Regex EeReply();

    /// <summary>Parse "...EE:AAAAVV;" (address 16 bit hex, value 8 bit hex).</summary>
    public static bool TryParseEepromReply(ReadOnlySpan<byte> reply, out int address, out byte value)
    {
        address = 0; value = 0;
        var m = EeReply().Match(Encoding.Latin1.GetString(reply));
        if (!m.Success) return false;
        address = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        value = byte.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool WriteAccepted(ReadOnlySpan<byte> reply) => Encoding.Latin1.GetString(reply).Contains(":OK;", StringComparison.Ordinal);

    /// <summary>Raw counter to percent: raw / (max/100), rounded to 2 decimals as the reference does.</summary>
    public static double Percent(int raw, int max) => max <= 0 ? 0 : Math.Round(raw * 100.0 / max, 2);

    // ---- Remote-mode maintenance (gutenprint reference, epson_escp2 sequences) ----

    private static readonly byte[] LeavePacketMode = [.. new byte[] { 0x00, 0x00, 0x00, 0x1B, 0x01 }, .. "@EJL 1284.4\n@EJL     \n"u8.ToArray()];
    private static readonly byte[] InitPrinter = [0x1B, 0x40];
    // ESC @ ESC @ ESC ( R 08 00 00 "REMOTE1"
    private static readonly byte[] EnterRemote = [.. InitPrinter, .. InitPrinter, 0x1B, 0x28, 0x52, 0x08, 0x00, 0x00, .. "REMOTE1"u8.ToArray()];
    private static readonly byte[] ExitRemote = [0x1B, 0x00, 0x00, 0x00];
    private static readonly byte[] JobEnd = Frame("JE", [0x00]);

    private static byte[] RemoteSession(params byte[][] commands) =>
        [.. LeavePacketMode, .. EnterRemote, .. commands.SelectMany(c => c), .. ExitRemote];

    /// <summary>
    /// Raw data for a RAW spooler job: leave packet mode, enter remote mode, command(s), leave, then JE to close the job.
    /// NozzleCheck is "NC 02 00 00 00"; HeadClean is "CH 02 00 00 00" (all heads);
    /// PowerClean is "CH 02 00 00 10" (group 0 with the 0x10 power flag).
    /// </summary>
    public static byte[] Maintenance(EpsonMaintenance action, DateTime? now = null) => action switch
    {
        EpsonMaintenance.NozzleCheck => [.. RemoteSession(Frame("NC", [0x00, 0x00])), .. ExitRemoteThenJobEnd()],
        EpsonMaintenance.HeadClean => [.. RemoteSession(TimeSync(now ?? DateTime.Now), Frame("CH", [0x00, 0x00])), .. ExitRemoteThenJobEnd()],
        EpsonMaintenance.PowerClean => [.. RemoteSession(TimeSync(now ?? DateTime.Now), Frame("CH", [0x00, 0x10])), .. ExitRemoteThenJobEnd()],
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    // After CH the reference re-enters remote mode only to send JE ("mark maintenance job complete").
    private static byte[] ExitRemoteThenJobEnd() => [.. EnterRemote, .. JobEnd, .. ExitRemote];

    /// <summary>"TI": sets the printer clock: 00, year (BE16), month, day, hour, minute, second.</summary>
    public static byte[] TimeSync(DateTime t) =>
        Frame("TI", [0x00, (byte)(t.Year >> 8), (byte)(t.Year & 0xFF), (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second]);
}
