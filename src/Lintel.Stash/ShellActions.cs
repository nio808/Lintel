// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;

namespace Lintel.Stash;

internal static class ShellActions
{
    /// <summary>Opens a file, folder or URL with its default handler.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Explorer window with the file selected.</summary>
    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = false }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    public static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{folder}\"", UseShellExecute = false }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }
}
