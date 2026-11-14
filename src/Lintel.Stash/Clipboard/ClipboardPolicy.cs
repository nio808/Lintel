// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Stash;

/// <summary>What the listener saw when the clipboard changed.</summary>
/// <param name="Sequence">GetClipboardSequenceNumber at read time.</param>
/// <param name="HasExclusionFormat">ExcludeClipboardContentFromMonitorProcessing or "Clipboard Viewer Ignore" present.</param>
/// <param name="CanIncludeInHistory">Value of the CanIncludeInClipboardHistory DWORD; null when the format is absent.</param>
/// <param name="OwnerProcess">Process name of the clipboard owner, with or without ".exe"; null when unknown.</param>
public sealed record ClipboardChange(uint Sequence, bool HasExclusionFormat, int? CanIncludeInHistory, string? OwnerProcess);

public enum CaptureDecision { Capture, Paused, OwnWrite, SensitiveFormat, ExcludedApp }

/// <summary>The "should this change be recorded?" rules, kept pure so they are unit-tested.</summary>
public sealed class ClipboardPolicy
{
    public static readonly string[] DefaultExcludedApps = ["1Password", "KeePass", "KeePassXC", "Bitwarden"];
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string LegacyIgnoreFormat = "Clipboard Viewer Ignore";
    public const string CanIncludeFormat = "CanIncludeInClipboardHistory";

    private uint ownSequence;

    public bool Paused { get; set; }
    public IReadOnlyCollection<string> ExcludedApps { get; set; } = DefaultExcludedApps;

    /// <summary>Call with the sequence number right after our own write to the clipboard.</summary>
    public void NoteOwnWrite(uint sequence) => ownSequence = sequence;

    public CaptureDecision Evaluate(ClipboardChange change)
    {
        if (Paused)
            return CaptureDecision.Paused;
        if (ownSequence != 0 && change.Sequence <= ownSequence)
            return CaptureDecision.OwnWrite;
        if (change.HasExclusionFormat || change.CanIncludeInHistory == 0)
            return CaptureDecision.SensitiveFormat;
        if (change.OwnerProcess is { Length: > 0 } owner)
        {
            var name = StripExe(owner);
            if (ExcludedApps.Any(a => string.Equals(StripExe(a.Trim()), name, StringComparison.OrdinalIgnoreCase)))
                return CaptureDecision.ExcludedApp;
        }
        return CaptureDecision.Capture;
    }

    private static string StripExe(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
}
