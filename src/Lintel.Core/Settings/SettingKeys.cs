// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Core.Settings;

/// <summary>App-level setting keys. The notch look is fixed, so only behaviour is configurable. Module keys are "&lt;moduleId&gt;.*" and owned by the module.</summary>
public static class SettingKeys
{
    public const string NotchDisplay = "notch.display";                 // "primary" | "pointer" | device name
    public const string NotchExpandOn = "notch.expandOn";               // "hover" | "click"
    public const string NotchHoverDelayMs = "notch.hoverDelayMs";       // 120
    public const string NotchCollapseDelayMs = "notch.collapseDelayMs"; // 350
    public const string NotchFullscreen = "notch.fullscreen";           // "hide" | "shrink" | "stay"
    public const string NotchRenderMode = "notch.renderMode";           // "auto" | "dwm" | "layered"
    public const string NotchLastSection = "notch.lastSection";         // string
    public const string NotchUserHidden = "notch.userHidden";           // bool false

    public const string AppOnboarded = "app.onboarded";                 // bool false
    public const string AppUpdatesCheck = "app.updates.check";          // bool true
    public const string AppUpdatesLastCheckUtc = "app.updates.lastCheckUtc";  // ISO-8601 string
    public const string AppUpdatesSkipVersion = "app.updates.skipVersion";    // string

    public static string ModuleInstalled(string moduleId) => $"modules.{moduleId}.installed";
    public static string Consent(string permissionId) => $"consent.{permissionId}";
    public static string Hotkey(string hotkeyId) => $"hotkeys.{hotkeyId}";
}
