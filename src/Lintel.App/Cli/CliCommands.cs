// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.Agents;
using Lintel.App.Settings;
using Lintel.App.Settings.Pages;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lintel.App.Cli;

/// <summary>Command-line helpers: console output for a GUI exe, --read-usage, and the Settings part of --screenshot.</summary>
internal static class CliCommands
{
    private static bool consoleReady;

    /// <summary>Writes to the caller's console or redirected stdout (a WinExe has neither by default).</summary>
    public static void WriteLine(string text)
    {
        if (!consoleReady)
        {
            consoleReady = true;
            var handle = GetStdHandle(-11);   // STD_OUTPUT_HANDLE
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                AttachConsole(-1);           // ATTACH_PARENT_PROCESS
        }
        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // No console to write to.
        }
    }

    /// <summary>--read-usage: no UI; refresh limits once (consents as the user set them) and write normalized JSON.</summary>
    public static async Task<int> ReadUsageAsync(IModuleContext context, string file)
    {
        using var service = AgentsData.CreateService(context);
        service.Start();
        try
        {
            var refresh = service.RefreshLimitsAsync();
            if (await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(90))) != refresh)
                context.Log.Warn("--read-usage: refresh timed out; writing what is known");
            else if (refresh.IsFaulted)
                context.Log.Warn($"--read-usage: refresh failed ({refresh.Exception?.InnerException?.GetType().Name})");
            await Task.Delay(1200);   // snapshots are coalesced to one per second
            var snapshot = service.Current;
            var folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(file, LimitsJson(snapshot));
            WriteLine($"Wrote {snapshot.Limits.Count} account(s) to {file}");
            return 0;
        }
        finally
        {
            service.Stop();
        }
    }

    /// <summary>Shape: [ { provider, account, plan, credits, blocked, stale, updatedAt, windows: [ { label, kind, durationMinutes, remainingPercent, resetsAt } ] } ].</summary>
    internal static string LimitsJson(AgentUsageSnapshot snapshot) => JsonSerializer.Serialize(
        snapshot.Limits.Select(l => new
        {
            provider = l.Provider.ToString().ToLowerInvariant(),
            account = l.AccountId,
            plan = l.Plan?.Name,
            credits = l.Credits,
            blocked = l.Blocked,
            stale = l.IsStale,
            updatedAt = l.ObservedAt,
            windows = l.Windows.Select(w => new
            {
                label = w.Label,
                kind = w.Kind.ToString().ToLowerInvariant(),
                durationMinutes = w.Minutes,
                remainingPercent = Math.Round(w.RemainingPercent, 1),
                resetsAt = w.ResetsAt,
            }),
        }),
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>--screenshot: renders onboarding (both steps) and every Settings page to settings-&lt;page&gt;-&lt;theme&gt;.png, light then dark.</summary>
    public static async Task CaptureSettingsAsync(AppServices s, string directory)
    {
        var wasOnboarded = s.Settings.Get(SettingKeys.AppOnboarded, false);
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            var suffix = theme.ToString().ToLowerInvariant();
            s.Settings.Set(SettingKeys.AppOnboarded, false);
            var window = await OpenAsync(s, theme);
            Save(window.CaptureRoot, Path.Combine(directory, $"settings-onboarding-{suffix}.png"));
            if (window.Page is OnboardingPage onboarding)
            {
                onboarding.ShowPermissions();
                await SettleAsync();
                Save(window.CaptureRoot, Path.Combine(directory, $"settings-onboarding-permissions-{suffix}.png"));
            }
            window.Close();

            s.Settings.Set(SettingKeys.AppOnboarded, true);
            window = await OpenAsync(s, theme);
            foreach (var key in window.PageKeys)
            {
                window.Navigate(key);
                await SettleAsync();
                var name = $"settings-{key.Replace("module:", "module-")}-{suffix}";
                Save(window.CaptureRoot, Path.Combine(directory, name + ".png"));
                // Pages taller than the window: also the whole scrolling body.
                if (window.Page is { } page && page.ScrollContent.ActualHeight > page.ActualHeight)
                    Save(page.ScrollContent, Path.Combine(directory, name + "-full.png"));
            }
            await CaptureDialogAsync(window, s, Path.Combine(directory, $"settings-dialog-install-{suffix}.png"));
            window.Close();
        }
        s.Settings.Set(SettingKeys.AppOnboarded, wasOnboarded);
        ApplicationThemeManager.ApplySystemTheme(true);
    }

    /// <summary>The install dialog of the first feature with an optional permission (that permission is revoked for the shot, then restored).</summary>
    private static async Task CaptureDialogAsync(SettingsWindow window, AppServices s, string path)
    {
        var manifest = s.Modules.Catalog.Select(d => d.Manifest).FirstOrDefault(m => m.OptionalPermissions.Count > 0);
        if (manifest is null)
            return;
        var optional = manifest.OptionalPermissions[0];
        var wasGranted = s.Consent.IsGranted(optional);
        s.Consent.Revoke(optional);
        window.Navigate("features");
        var ask = ConsentDialog.AskAsync(window, s, manifest, install: true);
        await SettleAsync();
        Save(window.CaptureRoot, path);
        (window.Dialogs.Content as ContentDialog)?.Hide();
        await ask;
        if (wasGranted)
            s.Consent.Grant(optional);
    }

    private static async Task<SettingsWindow> OpenAsync(AppServices s, ApplicationTheme theme)
    {
        var window = new SettingsWindow(s, null)
        {
            Animate = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 860,
            Height = 1000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, true);
        await SettleAsync();
        return window;
    }

    private static async Task SettleAsync()
    {
        await Task.Delay(450);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(FrameworkElement root, string path)
    {
        var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        if (rect.Width < 1 || rect.Height < 1)
            return;
        const double scale = 1.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White, null, rect);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(rect.Width * scale), (int)Math.Ceiling(rect.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
