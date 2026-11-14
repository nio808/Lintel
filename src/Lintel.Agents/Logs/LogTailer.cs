// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using ZstdSharp;

namespace Lintel.Agents.Logs;

/// <summary>Incremental line reader: remembers a byte offset, only ever consumes complete lines.</summary>
internal static class LogTailer
{
    private const int InitialBuffer = 1 << 20;
    private const int MaxLine = 64 << 20;

    /// <summary>
    /// Reads complete lines after <paramref name="offset"/> and returns the offset after the last one consumed.
    /// A partial last line (writer mid-append) is left for the next call. If the file is now shorter than
    /// <paramref name="offset"/> it was truncated or replaced: it is read again from the start and
    /// <paramref name="restarted"/> is true. Lines over 64 MB are skipped. Shares the file with its writer.
    /// </summary>
    public static long ReadLines(string path, long offset, Action<ReadOnlyMemory<byte>> onLine, out bool restarted,
                                 CancellationToken ct = default, Action? onRestart = null)
    {
        restarted = false;
        if (IsCompressed(path)) return ReadCompressed(path, offset, onLine, out restarted, ct, onRestart);
        using var stream = Open(path);
        if (stream.Length < offset) { offset = 0; restarted = true; onRestart?.Invoke(); }
        if (stream.Length == offset) return offset;
        stream.Seek(offset, SeekOrigin.Begin);
        return Pump(stream, offset, onLine, ct);
    }

    public static bool IsCompressed(string path) => path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    /// <summary>
    /// A zstd log is decompressed as a stream (never held whole): the first <paramref name="offset"/> decompressed bytes
    /// are skipped and only what follows is parsed. If the log decompresses to less than that, it was replaced: start over.
    /// </summary>
    private static long ReadCompressed(string path, long offset, Action<ReadOnlyMemory<byte>> onLine, out bool restarted,
                                       CancellationToken ct, Action? onRestart)
    {
        restarted = false;
        var scratch = new byte[1 << 16];
        long Skip(Stream s, long count)
        {
            long done = 0;
            while (done < count && s.Read(scratch, 0, (int)Math.Min(scratch.Length, count - done)) is var n and > 0) done += n;
            return done;
        }
        var file = Open(path);
        var stream = new DecompressionStream(file);
        try
        {
            if (offset > 0 && Skip(stream, offset) < offset)
            {
                stream.Dispose();
                file.Dispose();
                restarted = true;
                onRestart?.Invoke();
                offset = 0;
                file = Open(path);
                stream = new DecompressionStream(file);
            }
            return Pump(stream, offset, onLine, ct);
        }
        finally
        {
            stream.Dispose();
            file.Dispose();
        }
    }

    private static long Pump(Stream stream, long offset, Action<ReadOnlyMemory<byte>> onLine, CancellationToken ct)
    {
        var buffer = new byte[InitialBuffer];
        var filled = 0;
        var skipping = false;
        var consumed = offset;
        while (!ct.IsCancellationRequested)
        {
            if (filled == buffer.Length)
            {
                if (buffer.Length >= MaxLine)
                {
                    // Line too long to hold: drop what we have and discard up to the next newline.
                    consumed += filled;
                    filled = 0;
                    skipping = true;
                }
                else Array.Resize(ref buffer, buffer.Length * 2);
            }
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) break;
            filled += read;

            var start = 0;
            int nl;
            while ((nl = buffer.AsSpan(start, filled - start).IndexOf((byte)'\n')) >= 0)
            {
                var end = start + nl;
                if (skipping) skipping = false;
                else
                {
                    var length = end - start;
                    if (length > 0 && buffer[end - 1] == (byte)'\r') length--;
                    if (length > 0) onLine(buffer.AsMemory(start, length));
                }
                start = end + 1;
            }
            if (skipping) { consumed += filled; filled = 0; continue; }
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
                filled -= start;
                consumed += start;
            }
        }
        return consumed;
    }
}
