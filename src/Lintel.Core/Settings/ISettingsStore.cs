// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Core.Settings;

public interface ISettingsStore
{
    /// <summary>System.Text.Json; wrong type → fallback.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>No-op if equal; debounced save 500 ms; atomic (temp + File.Replace).</summary>
    void Set<T>(string key, T value);

    bool Contains(string key);
    void Remove(string key);

    /// <summary>e.g. "clipboard."</summary>
    void RemovePrefix(string prefix);

    /// <summary>Key; raised on the caller's thread.</summary>
    event Action<string>? Changed;

    void Flush();
}
