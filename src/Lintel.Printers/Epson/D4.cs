// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Epson;

/// <summary>Byte pipe to a printer: the real one is a usbprint handle, tests use a fake.</summary>
internal interface IEpsonTransport : IDisposable
{
    void Write(byte[] data, int timeoutMs, CancellationToken ct);

    /// <summary>Returns the bytes read, or an empty array on timeout.</summary>
    byte[] Read(int max, int timeoutMs, CancellationToken ct);
}

internal readonly record struct D4Packet(byte Psid, byte Ssid, int Length, byte Credit, byte Control, byte[] Payload);

/// <summary>IEEE 1284.4 ("D4") packets and the Epson EPSON-CTRL session on top of them. Port of the epson-usb handshake (MIT), see SOURCES.md.</summary>
internal static class D4
{
    public const byte CtrlSocket = 0x02;

    /// <summary>EJL switch into 1284.4 packet mode, with the 4 length-header bytes the Windows print channel expects.</summary>
    public static readonly byte[] EnterSequence = [0x00, 0x00, 0x00, 0x1B, 0x01, .. "@EJL 1284.4\n@EJL\n@EJL\n"u8.ToArray()];

    public static byte[] Build(byte psid, byte ssid, ReadOnlySpan<byte> payload, byte credit = 0, byte control = 0)
    {
        var len = 6 + payload.Length;
        var p = new byte[len];
        p[0] = psid; p[1] = ssid; p[2] = (byte)(len >> 8); p[3] = (byte)len; p[4] = credit; p[5] = control;
        payload.CopyTo(p.AsSpan(6));
        return p;
    }

    /// <summary>Decodes the first packet in <paramref name="buf"/>; false = need more bytes. A declared length under 6 is consumed as an empty packet.</summary>
    public static bool TryParse(ReadOnlySpan<byte> buf, out D4Packet packet, out int consumed)
    {
        packet = default; consumed = 0;
        if (buf.Length < 6) return false;
        var len = (buf[2] << 8) | buf[3];
        if (len < 6)
        {
            packet = new D4Packet(buf[0], buf[1], len, buf[4], buf[5], []);
            consumed = 6;
            return true;
        }
        if (buf.Length < len) return false;
        packet = new D4Packet(buf[0], buf[1], len, buf[4], buf[5], buf[6..len].ToArray());
        consumed = len;
        return true;
    }
}

/// <summary>A connected EPSON-CTRL conversation. Handshake order and timeouts follow the hardware-verified reference.</summary>
internal sealed class D4Session : IDisposable
{
    private readonly IEpsonTransport _t;
    private readonly CancellationToken _ct;
    private readonly Action<string, byte[]>? _log;
    private readonly List<byte> _buf = [];
    private readonly double _scale;
    private byte _rev = 0x20;
    private bool _open;

    /// <param name="scale">Multiplies every timeout (tests use a tiny value).</param>
    public D4Session(IEpsonTransport transport, CancellationToken ct, Action<string, byte[]>? log = null, double scale = 1)
    {
        _t = transport; _ct = ct; _log = log; _scale = scale;
    }

    private int Ms(int ms) => Math.Max(1, (int)(ms * _scale));

    private void Send(byte psid, byte ssid, byte[] payload, byte credit)
    {
        var pkt = D4.Build(psid, ssid, payload, credit);
        _log?.Invoke(">", pkt);
        _t.Write(pkt, Ms(3000), _ct);
    }

    private bool Fill(int timeoutMs)
    {
        var chunk = _t.Read(1024, Ms(timeoutMs), _ct);
        if (chunk.Length == 0) return false;
        _log?.Invoke("<", chunk);
        _buf.AddRange(chunk);
        return true;
    }

    private D4Packet? Recv(int timeoutMs)
    {
        while (true)
        {
            if (D4.TryParse(_buf.ToArray(), out var pkt, out var used)) { _buf.RemoveRange(0, used); return pkt; }
            if (!Fill(timeoutMs))
            {
                // truncated packet: drop its header so the stream can resync
                if (_buf.Count >= 6) _buf.RemoveRange(0, 6);
                return null;
            }
        }
    }

    /// <summary>Enter D4, Init, open the EPSON-CTRL channel. False when the printer does not answer D4.</summary>
    public bool Connect()
    {
        while (_t.Read(1024, Ms(300), _ct).Length > 0) { }   // throw away stale data
        _buf.Clear();
        _log?.Invoke(">", D4.EnterSequence);
        _t.Write(D4.EnterSequence, Ms(3000), _ct);
        Thread.Sleep(Ms(200));
        Recv(2500);

        var ok = false;
        for (var i = 0; i < 3 && !ok; i++)
        {
            Send(0, 0, [0x00, _rev], 1);
            var r = Recv(2500)?.Payload;
            if (r is { Length: >= 3 } && r[0] == 0x80)
            {
                if (r[1] == 0x00) { ok = true; break; }
                if (r[2] != 0 && r[2] != _rev) { _rev = r[2]; continue; }
            }
            break;
        }
        if (!ok) return false;

        // OpenChannel: revision 0x10 carries an extra initCredit field.
        byte[] open = _rev == 0x10
            ? [0x01, 0x02, D4.CtrlSocket, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00]
            : [0x01, 0x02, D4.CtrlSocket, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00];
        Send(0, 0, open, 1);
        var o = Recv(2500)?.Payload;
        _open = o is { Length: >= 2 } && o[0] == 0x81 && o[1] == 0x00;
        return _open;
    }

    /// <summary>Send one EPSON-CTRL frame and return the reply payload (null when nothing came back).</summary>
    public byte[]? Request(byte[] frame, int tries = 14)
    {
        // take send credit for the channel, then grant the printer reply credit
        byte[] cr = _rev == 0x10 ? [0x04, 0x02, D4.CtrlSocket, 0x00, 0x80, 0xFF, 0xFF] : [0x04, 0x02, D4.CtrlSocket, 0x00, 0x08];
        Send(0, 0, cr, 1);
        for (var i = 0; i < 4; i++)
        {
            var r = Recv(1500);
            if (r is null || (r.Value.Payload.Length > 0 && r.Value.Payload[0] == 0x84)) break;
        }
        Send(0, 0, [0x03, 0x02, D4.CtrlSocket, 0x00, 0x08], 1);
        Recv(1200);
        Send(D4.CtrlSocket, D4.CtrlSocket, frame, 8);
        for (var i = 0; i < tries; i++)
        {
            _ct.ThrowIfCancellationRequested();
            var p = Recv(2000);
            if (p is { Psid: D4.CtrlSocket } && p.Value.Payload.Length > 0) return p.Value.Payload;
        }
        return null;
    }

    /// <summary>Best effort: close the channel and leave D4 so the printer is back in its normal state. Never throws.</summary>
    public void Close()
    {
        try
        {
            if (_open)
            {
                Send(0, 0, [0x02, D4.CtrlSocket, D4.CtrlSocket], 1);   // CloseChannel
                Recv(800);
                Send(0, 0, [0x08], 1);                                  // Exit
                Recv(800);
            }
        }
        catch { /* the handle is closed right after; nothing more to do */ }
        _open = false;
    }

    public void Dispose() { Close(); _t.Dispose(); }
}
