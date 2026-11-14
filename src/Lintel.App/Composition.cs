// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Lintel.Agents;
using Lintel.Agents.UI;
using Lintel.App.Cli;
using Lintel.App.Settings;
using Lintel.App.Tray;
using Lintel.App.Update;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;
using Lintel.Media;
using Lintel.Notch;
using Lintel.Stash;
using Lintel.Tools;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Lintel.App;

/// <summary>
/// Composition root, app lifetime and the app-level actions the notch, tray and Settings call.
/// Exit is always <see cref="Quit"/> → <see cref="Dispose"/>: stop modules, close the notch, remove the tray icon, flush settings,
/// release the single-instance mutex (installers wait ≤ 5 s for it after "--quit").
/// </summary>
internal sealed class Composition(Application app) : IDisposable
{
    private readonly AppInfo appInfo = AppInfo.FromAssembly(typeof(Composition).Assembly);
    private SingleInstance? single;
    private JsonSettingsStore? settings;
    private FileLog? log;
    private ConsentService? consent;
    private HotkeyService? hotkeys;
    private HttpClient? http;
    private INotchShell? notch;
    private ModuleManager? modules;
    private UpdateInstaller? updates;
    private TrayIcon? tray;
    private SettingsWindow? window;
    private bool disposed;

    public void Start(string[] rawArgs)
    {
        var args = AppArgs.Parse(rawArgs);
        if (args.ShowVersion)
        {
            CliCommands.WriteLine($"Lintel {appInfo.Version}");
            app.Shutdown(0);
            return;
        }
        if (args.Quit)
        {
            SingleInstance.SignalQuit();   // no-op when nothing runs
            app.Shutdown(0);
            return;
        }
        HookExceptions();
        OverrideFrameRate();
        ApplicationThemeManager.ApplySystemTheme(true);
        if (args.ScreenshotDir is not null)
        {
            _ = RunScreenshotAsync(args);
            return;
        }
        if (args.ReadUsageFile is not null)
        {
            _ = RunReadUsageAsync(args);
            return;
        }

        single = new SingleInstance();
        if (!single.IsFirst)
        {
            AllowSetForegroundWindow(-1);   // ASFW_ANY: let the running instance bring Settings to the front
            single.SignalShow();
            single.Dispose();
            single = null;
            app.Shutdown(0);
            return;
        }
        single.ShowRequested += () => OpenSettings();
        single.QuitRequested += Quit;

        settings = new JsonSettingsStore(AppPaths.SettingsFile);
        Build(args, AppPaths.Logs, dataRoot: null);
        log!.Info($"Lintel {appInfo.Version} starting{(args.Demo ? " (demo)" : "")}");

        tray = new TrayIcon("Lintel", BuildTrayMenu, ToggleNotch);
        tray.ThemeChanged += () => ApplicationThemeManager.ApplySystemTheme(true);
        updates!.Changed += UpdateTray;
        updates.Found += AnnounceUpdate;
        updates.StartBackground();

        notch!.Attach(modules!);
        modules!.StartInstalled();
        notch.Show();
        if (!settings.Get(SettingKeys.AppOnboarded, false) || args.Settings || !args.Autostart)
            OpenSettings();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Guard("settings window", () => window?.Close());
        Guard("updates", () => updates?.Dispose());
        Guard("modules", () => modules?.StopAll());
        Guard("notch", () => notch?.Shutdown());
        Guard("tray", () => tray?.Dispose());
        Guard("hotkeys", () => hotkeys?.Dispose());
        Guard("settings", () => settings?.Dispose());
        http?.Dispose();
        log?.Info("Lintel exiting");
        log?.Dispose();
        single?.Dispose();
    }

    private void Build(AppArgs args, string logDirectory, string? dataRoot)
    {
        // WPF-UI's theme manager restyles Application.MainWindow (background + DWM backdrop) on every theme apply, and the
        // first Window created becomes MainWindow. Pin an inert, never-shown window there so it can't be the notch window.
        app.MainWindow ??= new Window();
        log = new FileLog(logDirectory);
        consent = new ConsentService(settings!);
        hotkeys = new HotkeyService(settings!, log);
        http = AppHttp.Create(appInfo);
        notch = NotchBootstrap.Create(new NotchDependencies(settings!, log, hotkeys, args, () => OpenSettings(), Quit, ShowBalloon));
        var context = new ModuleContext(appInfo, args.Demo, settings!, consent, notch, hotkeys, log, app.Dispatcher, http, TimeProvider.System, dataRoot);
        List<ModuleDescriptor> catalog =
        [
            .. AgentsUiCatalog.Modules(AgentsData.CreateService),
            .. MediaCatalog.Modules,
            .. ToolsCatalog.Modules,
            .. StashCatalog.Modules,
        ];
        modules = new ModuleManager(catalog.OrderBy(d => d.Manifest.Order).ToList(), context);
        updates = new UpdateInstaller(new UpdateChecker(http, appInfo, settings!, consent, TimeProvider.System), appInfo, settings!, consent, log, Quit);
    }

    private AppServices Services() => new(settings!, consent!, modules!, hotkeys!, notch!, appInfo, updates!, log!);

    /// <summary>Opens (or brings forward) Settings; onboarding while app.onboarded is false.</summary>
    private void OpenSettings(string? page = null)
    {
        if (disposed || settings is null)
            return;
        if (window is null)
        {
            window = new SettingsWindow(Services(), page);
            window.Closed += (_, _) => window = null;
            window.Show();
        }
        else if (page is not null)
        {
            window.Navigate(page);
        }
        window.BringToFront();
    }

    private void Quit()
    {
        Dispose();
        app.Shutdown();
    }

    private void ToggleNotch()
    {
        if (notch is not null && !disposed)
            notch.SetUserHidden(!notch.IsUserHidden);
    }

    private void ShowBalloon(string title, string message)
    {
        if (tray is null)
            log?.Info($"Balloon (no tray): {title}");
        else
            tray.ShowBalloon(title, message);
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu { FontSize = 12 };
        var hidden = notch!.IsUserHidden;
        menu.Items.Add(Item(hidden ? "Show notch" : "Hide notch", hidden ? SymbolRegular.Eye24 : SymbolRegular.EyeOff24, ToggleNotch));
        menu.Items.Add(Item("Open Settings", SymbolRegular.Settings24, () => OpenSettings()));
        var release = updates!.Available;
        menu.Items.Add(Item(release is null ? "Check for updates" : $"Update to {release.Version}…", SymbolRegular.ArrowSync24, () =>
        {
            OpenSettings("about");
            if (release is null)
                _ = updates.CheckAsync(userInitiated: true);
        }));
        var services = Services();
        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = services.StartupEnabled };
        startup.Click += (_, _) => services.SetStartup(startup.IsChecked);
        menu.Items.Add(startup);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Lintel", SymbolRegular.Power24, Quit));
        return menu;

        static MenuItem Item(string header, SymbolRegular icon, Action click)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = icon } };
            item.Click += (_, _) => click();
            return item;
        }
    }

    private void UpdateTray()
    {
        var release = updates?.Available;
        tray?.SetBadge(release is not null, release is null ? "Lintel" : $"Lintel · update {release.Version} available");
    }

    private void AnnounceUpdate(ReleaseInfo release)
    {
        log?.Info($"Update available: {release.Tag}");
        notch?.ShowNotice(new NotchNotice
        {
            Id = "app.update",
            Title = $"Lintel {release.Version} is available",
            Message = "Click to see what's new and install",
            Glyph = Glyphs.Download,
            Tone = NoticeTone.Info,
            Duration = TimeSpan.FromSeconds(8),
            OnClick = () => OpenSettings("about"),
            BalloonIfHidden = true,
        });
    }

    // ---- CLI runs --------------------------------------------------------------------------------------------------------

    /// <summary>--screenshot: demo data, every module installed and every consent granted in a throw-away settings store.</summary>
    private async Task RunScreenshotAsync(AppArgs args)
    {
        var exit = 1;
        var root = Path.Combine(Path.GetTempPath(), "Lintel-shot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.GetFullPath(args.ScreenshotDir!);
            Directory.CreateDirectory(directory);
            settings = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            Build(args with { Demo = true }, Path.Combine(root, "logs"), root);
            foreach (var permission in PermissionCatalog.All.Where(p => p.Id != PermissionIds.Startup))
                consent!.Grant(permission.Id);
            foreach (var descriptor in modules!.Catalog)
                settings.Set(SettingKeys.ModuleInstalled(descriptor.Manifest.Id), true);
            settings.Set(SettingKeys.AppOnboarded, true);
            notch!.Attach(modules);
            modules.StartInstalled();
            notch.Show();
            await Task.Delay(1500);   // async sources (media, first index pass) settle
            await notch.CaptureAsync(directory);
            await CliCommands.CaptureSettingsAsync(Services(), directory);
            CliCommands.WriteLine($"Screenshots written to {directory}");
            exit = 0;
        }
        catch (Exception e)
        {
            log?.Error("--screenshot failed", e);
            CliCommands.WriteLine($"--screenshot failed: {e.Message}");
        }
        Dispose();
        TryDelete(root);
        app.Shutdown(exit);
    }

    /// <summary>--read-usage: no UI; the user's own settings and consents decide which sources are read.</summary>
    private async Task RunReadUsageAsync(AppArgs args)
    {
        var exit = 1;
        try
        {
            settings = new JsonSettingsStore(AppPaths.SettingsFile);
            log = new FileLog(AppPaths.Logs);
            consent = new ConsentService(settings);
            hotkeys = new HotkeyService(settings, log);
            http = AppHttp.Create(appInfo);
            var context = new ModuleContext(appInfo, args.Demo, settings, consent, new NullNotchShell(log), hotkeys, log,
                app.Dispatcher, http, TimeProvider.System);
            exit = await CliCommands.ReadUsageAsync(context, Path.GetFullPath(args.ReadUsageFile!));
        }
        catch (Exception e)
        {
            log?.Error("--read-usage failed", e);
            CliCommands.WriteLine($"--read-usage failed: {e.Message}");
        }
        Dispose();
        app.Shutdown(exit);
    }

    // ---- plumbing --------------------------------------------------------------------------------------------------------

    private void HookExceptions()
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            log?.Error("Unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log?.Error("Unhandled exception", e.ExceptionObject as Exception);
            if (e.IsTerminating)
                log?.Dispose();   // flush before the process dies
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log?.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Animations tick at the display's refresh rate (60–144 Hz) instead of WPF's default 60.</summary>
    private static void OverrideFrameRate()
    {
        var hz = Math.Clamp(Displays.MaxRefreshRate() ?? 60, 60, 144);
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)hz });
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            log?.Warn($"Shutdown: {what} failed", e);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; a module may still hold a file.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
