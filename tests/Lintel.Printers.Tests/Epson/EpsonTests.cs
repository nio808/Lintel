// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Lintel.Printers.Epson;

namespace Lintel.Printers.Tests.Epson;

public class D4PacketTests
{
    [Fact]
    public void Build_matches_the_init_packet_seen_on_hardware()
    {
        Assert.Equal("0000000801000020", Convert.ToHexString(D4.Build(0, 0, [0x00, 0x20], credit: 1)));
    }

    [Fact]
    public void Parse_round_trips_and_reports_consumed_bytes()
    {
        var pkt = D4.Build(2, 2, [1, 2, 3], credit: 8);
        byte[] stream = [.. pkt, 0xAA];
        Assert.True(D4.TryParse(stream, out var p, out var used));
        Assert.Equal(pkt.Length, used);
        Assert.Equal((2, 2, 9, 8), (p.Psid, p.Ssid, p.Length, (int)p.Credit));
        Assert.Equal(new byte[] { 1, 2, 3 }, p.Payload);
    }

    [Fact]
    public void Parse_waits_for_more_bytes_and_consumes_malformed_headers()
    {
        var pkt = D4.Build(0, 0, [1, 2, 3, 4]);
        Assert.False(D4.TryParse(pkt[..^1], out _, out _));
        Assert.False(D4.TryParse(pkt[..3], out _, out _));
        Assert.True(D4.TryParse([0, 0, 0, 2, 0, 0, 9], out var p, out var used));
        Assert.Equal(6, used);
        Assert.Empty(p.Payload);
    }
}

public class EpsonRemoteTests
{
    private static readonly byte[] Key = [0x4A, 0x36];

    [Fact]
    public void Opcode_derivation()
    {
        Assert.Equal("41BEA0", Convert.ToHexString(EpsonRemote.Opcode('A')));
        Assert.Equal("42BD21", Convert.ToHexString(EpsonRemote.Opcode('B')));
    }

    [Fact]
    public void Eeprom_read_frame_matches_hardware_log()
    {
        Assert.Equal("7C7C07004A3641BEA03000", Convert.ToHexString(EpsonRemote.EepromRead(Key, 0x30)));
        Assert.Equal("7C7C07004A3641BEA00001", Convert.ToHexString(EpsonRemote.EepromRead(Key, 0x100)));
    }

    [Fact]
    public void Eeprom_write_frame_uses_shifted_write_key()
    {
        var f = EpsonRemote.EepromWrite(Key, Encoding.ASCII.GetBytes("Maribaya"), 0x2F, 0);
        Assert.Equal("7C7C10004A3642BD212F0000" + Convert.ToHexString("Nbsjcbzb"u8), Convert.ToHexString(f));
    }

    [Fact]
    public void Status_request()
    {
        Assert.Equal("7374010001", Convert.ToHexString(EpsonRemote.StatusRequest()));
    }

    [Fact]
    public void Eeprom_reply_parsing()
    {
        var ok = Encoding.Latin1.GetBytes("\0@BDC PS\r\nEE:003AA4;\f");
        Assert.True(EpsonRemote.TryParseEepromReply(ok, out var a, out var v));
        Assert.Equal((0x3A, (byte)0xA4), (a, v));
        Assert.False(EpsonRemote.TryParseEepromReply("@BDC PS\r\n||:NA;\f"u8, out _, out _));
        Assert.True(EpsonRemote.WriteAccepted("@BDC PS\r\n||:OK;\f"u8));
        Assert.False(EpsonRemote.WriteAccepted("@BDC PS\r\n||:NA;\f"u8));
    }

    [Fact]
    public void Percent_math()
    {
        Assert.Equal(2.58, EpsonRemote.Percent(164, 6346));
        Assert.Equal(100, EpsonRemote.Percent(6346, 6346));
        Assert.Equal(0, EpsonRemote.Percent(5, 0));
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);
    private static readonly string Leave = Convert.ToHexString([0x00, 0x00, 0x00, 0x1B, 0x01]) + Convert.ToHexString("@EJL 1284.4\n@EJL     \n"u8);
    private static readonly string Remote = "1B401B40" + "1B2852080000" + Convert.ToHexString("REMOTE1"u8);
    private const string ExitRemote = "1B000000";

    [Fact]
    public void Nozzle_check_sequence()
    {
        var expected = Leave + Remote + "4E4302000000" + ExitRemote + Remote + "4A45010000" + ExitRemote;
        Assert.Equal(expected, Hex(EpsonRemote.Maintenance(EpsonMaintenance.NozzleCheck)));
    }

    [Theory]
    [InlineData(EpsonMaintenance.HeadClean, "00")]
    [InlineData(EpsonMaintenance.PowerClean, "10")]
    public void Clean_sequences(EpsonMaintenance action, string group)
    {
        var now = new DateTime(2026, 11, 17, 9, 5, 7);
        var ti = "5449" + "0800" + "00" + "07EA" + "0B" + "11" + "09" + "05" + "07";   // TI: 00, year BE16, M D h m s
        var ch = "4348" + "0200" + "00" + group;                                          // CH 02 00 00 <group | power flag>
        var expected = Leave + Remote + ti + ch + ExitRemote + Remote + "4A45010000" + ExitRemote;
        Assert.Equal(expected, Hex(EpsonRemote.Maintenance(action, now)));
    }

    [Fact]
    public void Power_clean_differs_from_head_clean_only_in_the_power_flag()
    {
        var now = DateTime.Now;
        var h = EpsonRemote.Maintenance(EpsonMaintenance.HeadClean, now);
        var p = EpsonRemote.Maintenance(EpsonMaintenance.PowerClean, now);
        var diff = Enumerable.Range(0, h.Length).Where(i => h[i] != p[i]).ToArray();
        Assert.Equal(h.Length, p.Length);
        Assert.Single(diff);
        Assert.Equal((0x00, 0x10), (h[diff[0]], p[diff[0]]));
    }
}

public class EpsonModelTests
{
    [Theory]
    [InlineData("EPSON L3250 Series", true)]
    [InlineData("EPSON L3251 Series", true)]
    [InlineData("EPSON ET-2814 Series", true)]
    [InlineData("EPSON L32500 Series", false)]
    [InlineData("EPSON XP-205 207 Series", false)]
    [InlineData("Microsoft Print To PDF", false)]
    public void Pad_counters_are_known_only_for_tabulated_models(string driver, bool known)
    {
        Assert.Equal(known, EpsonDevice.CanReadPadCounters(driver));
        Assert.Equal(known, EpsonDevice.CanResetPadCounters(driver));
    }

    [Fact]
    public void Read_only_model_cannot_reset()
    {
        Assert.True(EpsonDevice.CanReadPadCounters("EPSON L3150 Series"));
        Assert.False(EpsonDevice.CanResetPadCounters("EPSON L3150 Series"));
    }

    [Theory]
    [InlineData("EPSON L3250 Series", true)]
    [InlineData("EPSON XP-205 207 Series", true)]
    [InlineData("EPSON TM-T88V Receipt", false)]
    [InlineData("EPSON LQ-590", false)]
    [InlineData("Brother HL-L2350", false)]
    public void Supported_is_epson_inkjet(string driver, bool supported) => Assert.Equal(supported, EpsonDevice.IsSupported(driver));

    [Fact]
    public void Limits_for_l3250()
    {
        var l = EpsonDevice.LimitsFor("EPSON L3250 Series");
        Assert.Equal((6346, 3416), (l!.PadCounterMax, l.BorderlessPadCounterMax));
        Assert.Null(EpsonDevice.LimitsFor("Some Other Printer"));
    }

    [Fact]
    public void Decode_uses_the_shared_high_byte_per_nibble()
    {
        var m = EpsonModels.Find("EPSON L3250 Series")!;
        var c = EpsonEeprom.Decode(m, new Dictionary<int, byte> { [0x30] = 0x10, [0x31] = 0x27, [0x2F] = 0x00, [0x32] = 0x58, [0x33] = 0x0D });
        Assert.Equal(10000, c.MainRaw);
        Assert.Equal(3416, c.BorderlessRaw);
        Assert.Equal(100, c.BorderlessPercent);
    }

    [Fact]
    public void Id1284_matches_driver_name()
    {
        const string id = "MFG:EPSON;CMD:ESCPL2,BDC,D4;MDL:L3250 Series;CLS:PRINTER;";
        Assert.True(UsbPrintLocator.MatchesDriver(id, "EPSON L3250 Series"));
        Assert.False(UsbPrintLocator.MatchesDriver(id, "EPSON L3150 Series"));
        Assert.False(UsbPrintLocator.MatchesDriver("MFG:HP;MDL:L3250 Series;", "EPSON L3250 Series"));
        Assert.False(UsbPrintLocator.MatchesDriver(null, "EPSON L3250 Series"));
    }
}

/// <summary>Simulated printer: answers the D4 handshake and EPSON-CTRL EEPROM reads/writes against a byte map.</summary>
internal sealed class FakePrinter : IEpsonTransport
{
    public readonly Dictionary<int, byte> Eeprom = [];
    public readonly List<(int Addr, byte Value)> Writes = [];
    private readonly Queue<byte[]> _out = new();
    private static readonly byte[] WriteKey = Encoding.ASCII.GetBytes("Nbsjcbzb");

    public void Write(byte[] data, int timeoutMs, CancellationToken ct)
    {
        if (data.Length > 5 && data[3] == 0x1B) { _out.Enqueue([0, 0, 0, 8, 1, 0, 0xC5, 0]); return; }   // EJL enter
        Assert.True(D4.TryParse(data, out var p, out _));
        if (p.Psid == 0)
        {
            var c = p.Payload;
            byte[] reply = c[0] switch
            {
                0x00 => [0x80, 0x00, 0x10],
                0x01 => [0x81, 0x00, 0x02, 0x02],
                0x04 => [0x84, 0x00, 0x02, 0x02, 0x00, 0x01],
                0x03 => [0x83, 0x00, 0x02, 0x02],
                0x02 => [0x82, 0x00, 0x02, 0x02],
                _ => [0x88, 0x00],
            };
            _out.Enqueue(D4.Build(0, 0, reply, 1));
            return;
        }
        var f = p.Payload;   // "||" len(2) key(2) opcode(3) lo hi [value wkey*8]
        var addr = f[9] | (f[10] << 8);
        if (f[6] == 0x41) _out.Enqueue(D4.Build(2, 2, Encoding.ASCII.GetBytes($"\0@BDC PS\r\nEE:{addr:X4}{Eeprom.GetValueOrDefault(addr):X2};\f"), 1));
        else
        {
            Assert.True(f[12..20].SequenceEqual(WriteKey));
            Writes.Add((addr, f[11]));
            Eeprom[addr] = f[11];
            _out.Enqueue(D4.Build(2, 2, "\0@BDC PS\r\n||:OK;\f"u8, 1));
        }
    }

    public byte[] Read(int max, int timeoutMs, CancellationToken ct) => _out.Count > 0 ? _out.Dequeue() : [];
    public void Dispose() { }
}

public class EpsonEepromTests
{
    private static (EpsonEeprom e, FakePrinter p, EpsonModel m, D4Session s) Make()
    {
        var p = new FakePrinter();
        var m = EpsonModels.Find("EPSON L3250 Series")!;
        var s = new D4Session(p, CancellationToken.None, scale: 0.01);
        Assert.True(s.Connect());
        return (new EpsonEeprom(s, m), p, m, s);
    }

    [Fact]
    public void Reads_counters_through_a_session()
    {
        var (e, p, _, s) = Make();
        p.Eeprom[0x30] = 0xA4;
        using (s) Assert.Equal(164, e.ReadCounters()!.MainRaw);
    }

    [Fact]
    public void Reset_writes_exactly_the_table_and_the_commit_mask()
    {
        var (e, p, m, s) = Make();
        using (s)
        {
            p.Eeprom[0x30] = 0xA4; p.Eeprom[0x100] = 0xFF; p.Eeprom[0x36] = 0x10;
            Assert.True(e.ResetWaste(out var before, out var err), err);
            Assert.Equal(0xA4, before[0x30]);
            var expected = m.ResetWrites.Select(w => w.Addr).Append(0x100).OrderBy(a => a);
            Assert.Equal(expected, p.Writes.Select(w => w.Addr).OrderBy(a => a));
            Assert.Equal(0x00, p.Eeprom[0x30]);
            Assert.Equal(0x5E, p.Eeprom[0x36]);
            Assert.Equal(0xFE, p.Eeprom[0x100]);   // bit 0 cleared, rest kept
        }
    }

    [Fact]
    public void Nothing_outside_the_whitelist_can_be_written()
    {
        var m = EpsonModels.Find("EPSON L3250 Series")!;
        var allowed = m.ResetWrites.Select(w => w.Addr).ToHashSet();
        for (var a = 0; a <= 0x7FF; a++)
        {
            if (allowed.Contains(a)) continue;
            Assert.Throws<InvalidOperationException>(() => EpsonEeprom.AssertWritable(m, a, 0));
        }
        Assert.Throws<InvalidOperationException>(() => EpsonEeprom.AssertWritable(m, 0x100, 0xFE));            // commit needs the current value
        Assert.Throws<InvalidOperationException>(() => EpsonEeprom.AssertWritable(m, 0x100, 0xAA, 0xFF));      // wrong mask result
        EpsonEeprom.AssertWritable(m, 0x100, 0xFE, 0xFF);
        Assert.Throws<InvalidOperationException>(() => EpsonEeprom.AssertWritable(m, 0x30, 1));                // right address, wrong value
        Assert.Throws<InvalidOperationException>(() => EpsonEeprom.AssertWritable(EpsonModels.Find("EPSON L3150 Series")!, 0x30, 0));   // read-only model
    }
}
