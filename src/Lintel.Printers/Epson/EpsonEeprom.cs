// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Epson;

/// <summary>EEPROM access over an open EPSON-CTRL session. Reads are free; the only write path is the model's waste-counter reset list.</summary>
internal sealed class EpsonEeprom(D4Session session, EpsonModel model)
{
    public byte? ReadByte(int address)
    {
        var reply = session.Request(EpsonRemote.EepromRead(model.ReadKey, address));
        if (reply is null || !EpsonRemote.TryParseEepromReply(reply, out var a, out var v) || a != address) return null;
        return v;
    }

    /// <summary>Raw bytes at every counter address, or null if any read fails.</summary>
    public Dictionary<int, byte>? ReadBytes(IEnumerable<int> addresses)
    {
        var d = new Dictionary<int, byte>();
        foreach (var a in addresses)
        {
            if (ReadByte(a) is not { } v) return null;
            d[a] = v;
        }
        return d;
    }

    public EpsonPadCounters? ReadCounters()
    {
        if (ReadBytes(model.CounterAddresses) is not { } b) return null;
        return Decode(model, b);
    }

    public static EpsonPadCounters Decode(EpsonModel m, IReadOnlyDictionary<int, byte> b)
    {
        int Raw(PadCounterDef c) => c.Decode(b[c.Low], b[c.Mid], b[c.High]);
        var main = Raw(m.Main);
        int? border = m.Borderless is { } bc ? Raw(bc) : null;
        return new EpsonPadCounters(
            EpsonRemote.Percent(main, m.Main.Max),
            border is { } r ? EpsonRemote.Percent(r, m.Borderless!.Max) : null,
            main, border);
    }

    // ---- the single write path: waste-counter reset ---------------------------------------------------------------------

    /// <summary>
    /// Throws unless (address, value) is exactly an entry of the model's reset list, or the commit address with the masked value.
    /// <paramref name="current"/> is the byte currently stored at the commit address.
    /// </summary>
    internal static void AssertWritable(EpsonModel m, int address, byte value, byte? current = null)
    {
        if (!m.CanReset) throw new InvalidOperationException($"{m.Name} has no reset table");
        foreach (var (a, v) in m.ResetWrites)
            if (a == address) { if (v != value) throw new InvalidOperationException($"value 0x{value:X2} is not the table value for 0x{address:X}"); return; }
        if (m.ResetCommit is { } c && c.Addr == address && current is { } cur && value == (byte)(cur & c.AndMask)) return;
        throw new InvalidOperationException($"EEPROM address 0x{address:X} is not on the reset whitelist");
    }

    /// <summary>Writes one whitelisted byte and reads it back. False if refused or the read-back differs.</summary>
    private bool WriteVerified(int address, byte value, byte? current = null)
    {
        AssertWritable(model, address, value, current);
        var reply = session.Request(EpsonRemote.EepromWrite(model.ReadKey, model.WriteKey!, address, value));
        // ":OK;" alone proves nothing on some firmware: the read-back is the real check.
        return reply is not null && EpsonRemote.WriteAccepted(reply) && ReadByte(address) == value;
    }

    /// <summary>Applies the model's reset list. <paramref name="before"/> receives the original bytes (restorable by hand).</summary>
    public bool ResetWaste(out Dictionary<int, byte> before, out string error)
    {
        error = "";
        before = [];
        if (!model.CanReset) { error = "model has no reset table"; return false; }
        var addrs = model.ResetWrites.Select(w => w.Addr).Concat(model.ResetCommit is { } c ? new[] { c.Addr } : Array.Empty<int>()).ToArray();
        if (ReadBytes(addrs) is not { } snapshot) { error = "could not read the cells to be reset"; return false; }
        before = snapshot;
        foreach (var (a, v) in model.ResetWrites)
        {
            if (!WriteVerified(a, v)) { error = $"write of 0x{a:X} failed or did not stick"; return false; }
        }
        if (model.ResetCommit is { } commit)
        {
            var value = (byte)(snapshot[commit.Addr] & commit.AndMask);
            if (!WriteVerified(commit.Addr, value, snapshot[commit.Addr])) { error = "commit write failed or did not stick"; return false; }
        }
        return true;
    }
}
