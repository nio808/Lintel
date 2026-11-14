// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;

namespace Lintel.Core.Services;

/// <summary>HKCU\…\Run value "Lintel" = "&lt;exe&gt;" --autostart; honours Task Manager's StartupApproved switch.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Lintel";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string { Length: > 0 })
            return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // StartupApproved: first byte odd (0x03, 0x07…) = disabled in Task Manager; missing = enabled.
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
    }

    public static void Set(bool enabled, string exePath)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled)
                run.SetValue(ValueName, $"\"{exePath}\" --autostart");
            else
                run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        // Drop Task Manager's override so the in-app switch is what counts.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
