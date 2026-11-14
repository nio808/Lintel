// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Modules;

namespace Lintel.Core.Permissions;

public sealed record PermissionInfo(
    string Id, string Title, string PlainWords, string Touches,
    string? LeavesPcTo,              // null = stays on this PC; otherwise host list
    string? WindowsSettingsUri);     // e.g. "ms-settings:startupapps"

public static class PermissionIds
{
    public const string AgentLogs = "agents.logs", ClaudeCredentials = "claude.credentials",
        ClaudeAppHistory = "claude.app-history", CodexCli = "codex.cli", NetworkPrices = "network.prices",
        MediaSession = "media.session", AudioSessions = "audio.sessions",
        ClipboardRead = "clipboard.read", InputPaste = "input.paste",
        ShelfFiles = "files.shelf", DownloadsFolder = "files.downloads",
        KeepAwake = "power.keep-awake", SystemStats = "system.stats", SystemToggles = "system.toggles",
        Startup = "app.startup", NetworkUpdates = "network.updates";
}

/// <summary>Plain-words texts shown in onboarding, install dialogs and the Permissions page (mirrored in docs/PERMISSIONS.md).</summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionInfo> All { get; } =
    [
        new(PermissionIds.AgentLogs, "Coding agent logs",
            "Read how many tokens your coding agents used",
            "Session logs of Claude Code, Codex, Copilot, OpenCode: counts, model, folder name, times. Never prompts or replies.",
            null, null),
        new(PermissionIds.ClaudeCredentials, "Claude sign-in",
            "Use your Claude Code sign-in to ask Anthropic how much plan quota is left",
            @"Reads the access token in .claude\.credentials.json each check; never writes it",
            "api.anthropic.com (the token is sent only there)", null),
        new(PermissionIds.ClaudeAppHistory, "Claude app quota history",
            "Read the quota history the Claude desktop app saves",
            @"%APPDATA%\Claude\plan-usage-history.json",
            null, null),
        new(PermissionIds.CodexCli, "Codex CLI",
            "Run your Codex CLI in the background to ask for plan limits",
            "Starts hidden codex app-server, which talks to OpenAI with Codex's own login; killed after each check",
            "OpenAI (contacted by Codex itself)", null),
        new(PermissionIds.NetworkPrices, "Model price list",
            "Download the latest model price list once a day",
            "Nothing of yours is sent",
            "raw.githubusercontent.com (GET only)", null),
        new(PermissionIds.MediaSession, "Media controls",
            "See what is playing and press play/pause/next for you",
            "Title, artist, album art, position from Windows' media controls",
            null, null),
        new(PermissionIds.AudioSessions, "App audio",
            "See which apps make sound and change their volume",
            "App names, volumes, peak level. No audio is recorded.",
            null, null),
        new(PermissionIds.ClipboardRead, "Clipboard history",
            "Keep a history of what you copy",
            "Clipboard text, file paths, images; stored locally (encrypted)",
            null, null),
        new(PermissionIds.InputPaste, "Paste for you",
            "Press Ctrl+V for you when you pick a history entry",
            "Sends one synthetic keystroke to the focused app",
            null, null),
        new(PermissionIds.ShelfFiles, "Shelf files",
            "Remember the files you drop on the notch",
            "Paths of dropped files",
            null, null),
        new(PermissionIds.DownloadsFolder, "Downloads folder",
            "Watch your Downloads folder for downloads in progress",
            "File names and sizes in Downloads (top level)",
            null, null),
        new(PermissionIds.KeepAwake, "Keep awake",
            "Stop the PC or display from sleeping while on",
            "Power request to Windows",
            null, null),
        new(PermissionIds.SystemStats, "System stats",
            "Read CPU, memory, network, disk and battery numbers",
            "Windows performance counters",
            null, null),
        new(PermissionIds.SystemToggles, "System toggles",
            "Change the Windows settings you click",
            "Theme registry values, desktop-icon visibility, microphone mute, lock, Recycle Bin",
            null, "ms-settings:privacy-microphone"),
        new(PermissionIds.Startup, "Start with Windows",
            "Start with Windows",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run value Lintel",
            null, "ms-settings:startupapps"),
        new(PermissionIds.NetworkUpdates, "Update check",
            "Check GitHub for a new version once a day",
            "Nothing of yours is sent",
            "api.github.com, github.com (GET only)", null),
    ];

    public static PermissionInfo? Find(string permissionId) => All.FirstOrDefault(p => p.Id == permissionId);
}

public interface IConsentService
{
    bool IsGranted(string permissionId);
    void Grant(string permissionId);
    void Revoke(string permissionId);

    /// <summary>UI thread.</summary>
    event Action<string>? Changed;

    /// <summary>Granted permissions that no installed module lists (required or optional).</summary>
    IReadOnlyList<string> Unused(IReadOnlyList<ModuleManifest> installed);
}
