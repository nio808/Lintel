// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Reflection;

namespace Lintel.Agents.Pricing;

/// <summary>The embedded price list, replaced by a newer valid one downloaded (opt-in) from the project's GitHub repo.</summary>
internal sealed class PriceSource
{
    private readonly string? cacheFile;

    public PriceSource(string? cacheFile = null)
    {
        this.cacheFile = cacheFile;
        Current = PriceList.Newer(Embedded(), LoadCached()) ?? PriceList.Empty;
    }

    public PriceList Current { get; private set; }

    public static PriceList Embedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent-prices.json");
        if (stream is null) return PriceList.Empty;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return PriceList.Decode(memory.GetBuffer().AsSpan(0, (int)memory.Length)) ?? PriceList.Empty;
    }

    public static string Url(string owner, string repo) =>
        $"https://raw.githubusercontent.com/{owner}/{repo}/main/src/Lintel.Agents/Resources/agent-prices.json";

    private PriceList? LoadCached()
    {
        try { return cacheFile is not null && File.Exists(cacheFile) ? PriceList.Decode(File.ReadAllBytes(cacheFile)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>GET only, nothing of the user's is sent. Returns true when a newer list was installed.</summary>
    public async Task<bool> RefreshAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > PriceList.MaximumSize) return false;
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[PriceList.MaximumSize + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        return Install(buffer.AsSpan(0, length));
    }

    /// <summary>Decodes and installs <paramref name="data"/> if it is valid and newer than (or the same day as) the current list.</summary>
    public bool Install(ReadOnlySpan<byte> data)
    {
        if (PriceList.Decode(data) is not { } list || list.Updated < Current.Updated || list == Current) return false;
        Current = list;
        if (cacheFile is not null)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                File.WriteAllBytes(cacheFile, data.ToArray());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }
}
