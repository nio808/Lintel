// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lintel.Stash;

/// <summary>history.bin = DPAPI (CurrentUser) over JSON. Text and file entries only; images never touch disk.</summary>
public static class ClipboardPersistence
{
    private sealed record Row(ClipKind Kind, string? Text, string[]? Files, DateTimeOffset Created, bool Pinned);

    private static readonly byte[] Entropy = "Lintel.clipboard.v1"u8.ToArray();

    public static void Save(string path, IEnumerable<ClipEntry> entries)
    {
        var rows = entries.Where(e => e.Kind != ClipKind.Image)
            .Select(e => new Row(e.Kind, e.Text, e.Kind == ClipKind.Files ? [.. e.Files] : null, e.Created, e.Pinned)).ToList();
        var blob = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(rows), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Missing file gives empty. An unreadable or corrupt file is moved aside as *.corrupt and gives empty.</summary>
    public static List<ClipEntry> Load(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var rows = JsonSerializer.Deserialize<List<Row>>(json) ?? [];
            return rows.Where(r => r.Kind != ClipKind.Image).Select(r => new ClipEntry
            {
                Kind = r.Kind,
                Text = r.Text,
                Files = r.Files ?? [],
                Created = r.Created,
                Pinned = r.Pinned,
            }).ToList();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or ArgumentException or NotSupportedException)
        {
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch (IOException) { }
            return [];
        }
    }
}
