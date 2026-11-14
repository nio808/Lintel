// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lintel.Stash;

public enum ClipKind { Text, Files, Image }

public sealed class ClipEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>PNG bytes; memory only, never persisted.</summary>
    public byte[]? Png { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public DateTimeOffset Created { get; set; }
    public bool Pinned { get; set; }

    /// <summary>Identity for dedupe (same content = same key).</summary>
    public string Key => Kind switch
    {
        ClipKind.Text => "t:" + Text,
        ClipKind.Files => "f:" + string.Join('\n', Files),
        _ => "i:" + Convert.ToHexString(SHA256.HashData(Png ?? [])),
    };

    public long Size => Kind switch
    {
        ClipKind.Text => Encoding.UTF8.GetByteCount(Text ?? ""),
        ClipKind.Files => Files.Sum(f => (long)f.Length),
        _ => Png?.Length ?? 0,
    };

    /// <summary>Up to ~240 chars for list rows.</summary>
    public string Preview
    {
        get
        {
            switch (Kind)
            {
                case ClipKind.Text:
                    var t = (Text ?? "").Trim();
                    return t.Length > 240 ? t[..240] + "…" : t;
                case ClipKind.Files:
                    return string.Join('\n', Files.Take(4).Select(f => Path.GetFileName(f.TrimEnd('\\', '/')))) + (Files.Count > 4 ? $"\n+{Files.Count - 4} more" : "");
                default:
                    return $"Image {ImageWidth} × {ImageHeight}";
            }
        }
    }

    public static ClipEntry FromText(string text, DateTimeOffset now) => new() { Kind = ClipKind.Text, Text = text, Created = now };

    public static ClipEntry FromFiles(IEnumerable<string> files, DateTimeOffset now) => new() { Kind = ClipKind.Files, Files = files.ToArray(), Created = now };

    public static ClipEntry FromImage(byte[] png, int width, int height, DateTimeOffset now) =>
        new() { Kind = ClipKind.Image, Png = png, ImageWidth = width, ImageHeight = height, Created = now };
}
