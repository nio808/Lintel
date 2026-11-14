// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.Core.Agents;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Preview;

/// <summary>
/// Dev harness: Lintel.Preview.exe &lt;moduleId...|all|agents-data&gt; [--demo] [--screenshot &lt;dir&gt;]
/// Starts the modules with a throw-away context (temp settings + data, every consent granted, NullNotchShell printing notices)
/// and shows their compact activities in a mock pill and every section at its clamped PreferredSize, framed like the notch.
/// Unknown switches are ignored (a catalog may read its own, e.g. --design).
/// </summary>
public static class Program
{
    // Same clamp as the notch shell (NotchMetrics.MinContent / MaxContent).
    private static readonly Size MinSection = new(300, 100);
    private static readonly Size MaxSection = new(680, 420);

    [STAThread]
    public static int Main(string[] args)
    {
        var demo = false;
        string? shotDir = null;
        var ids = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--demo", StringComparison.OrdinalIgnoreCase))
                demo = true;
            else if (args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                shotDir = Path.GetFullPath(args[++i]);
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                ids.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        ids = ids.Distinct().ToList();
        demo |= shotDir is not null;   // screenshots never show real data

        var root = Path.Combine(Path.GetTempPath(), "Lintel.Preview", Guid.NewGuid().ToString("N"));
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(NotchTheme.Create());

        var log = new ConsoleLog();
        using var settings = new JsonSettingsStore(Path.Combine(root, "settings.json"));
        var consent = new ConsentService(settings);
        foreach (var permission in PermissionCatalog.All)
            consent.Grant(permission.Id);
        using var hotkeys = new HotkeyService(settings, log);
        var shell = new NullNotchShell(log);
        var appInfo = AppInfo.FromAssembly(typeof(Program).Assembly);
        using var http = AppHttp.Create(appInfo);
        var context = new ModuleContext(appInfo, demo, settings, consent, shell, hotkeys, log, Dispatcher.CurrentDispatcher, http, TimeProvider.System, root);

        var catalog = Catalog().ToList();
        log.Info($"Catalog: {(catalog.Count == 0 ? "(empty)" : string.Join(", ", catalog.Select(d => d.Manifest.Id)))}");
        if (ids.Contains("all"))
            ids = catalog.Select(d => d.Manifest.Id).ToList();

        var page = new StackPanel { Margin = new Thickness(24) };
        var started = new List<INotchModule>();
        var shown = new List<ISectionLifecycle>();
        IDisposable? dataService = null;
        var shots = new List<(string Name, FrameworkElement Element)>();

        page.Children.Add(Text($"Lintel Preview{(demo ? " · demo" : "")}", "Notch.Title"));
        page.Children.Add(Text($"Catalog: {(catalog.Count == 0 ? "empty" : string.Join(", ", catalog.Select(d => d.Manifest.Id)))}", "Notch.Caption"));

        foreach (var id in ids)
        {
            var descriptor = catalog.FirstOrDefault(d => d.Manifest.Id == id);
            if (descriptor is null)
            {
#if PREVIEW_AGENTS_DATA
                if (id is "agents-data" or "agents")
                {
                    dataService = ShowAgentsData(page, context);
                    continue;
                }
#endif
                log.Warn($"Module '{id}' is not in the catalog");
                page.Children.Add(Text($"Module '{id}' is not in the catalog.", "Notch.Body", "Tone.Warning"));
                continue;
            }
            try
            {
                var module = descriptor.Create();
                module.Start(context);
                started.Add(module);
                AddModule(page, module, shots, shown);
            }
            catch (Exception e)
            {
                log.Error($"Module '{id}' failed", e);
                page.Children.Add(Text($"Module '{id}' failed: {e.Message}", "Notch.Body", "Tone.Critical"));
            }
        }
        if (ids.Count == 0)
            page.Children.Add(Text("Usage: Lintel.Preview.exe <moduleId...|all|agents-data> [--demo] [--screenshot <dir>]", "Notch.Body"));

        var window = new Window
        {
            Title = "Lintel Preview",
            Width = 900,
            Height = 900,
            Background = (Brush)app.Resources["Notch.Background"],
            Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        ApplyTextDefaults(window);
        window.Loaded += (_, _) =>
        {
            foreach (var view in shown)
                view.OnShown();
            if (shotDir is null)
                return;
            // Give async sources (media sessions, first index pass) a moment, then render and exit.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Directory.CreateDirectory(shotDir);
                SavePng(page, Path.Combine(shotDir, "preview.png"));
                foreach (var (name, element) in shots)
                {
                    SavePng(element, Path.Combine(shotDir, name + ".png"));
                    // A section that scrolls: also its whole content.
                    if (name.StartsWith("section-", StringComparison.Ordinal) && Find<ScrollViewer>(element) is { Content: FrameworkElement body } scroll
                        && scroll.ScrollableHeight > 0)
                        SavePng(body, Path.Combine(shotDir, name + "-scroll.png"));
                }
                log.Info($"Screenshots written to {shotDir}");
                window.Close();
            };
            timer.Start();
        };

        var exit = app.Run(window);
        foreach (var view in shown)
            view.OnHidden();
        foreach (var module in started)
            module.Stop();
        dataService?.Dispose();
        settings.Flush();
        TryDelete(root);
        return exit;
    }

    private static IEnumerable<ModuleDescriptor> Catalog()
    {
#if PREVIEW_AGENTS_UI && PREVIEW_AGENTS_DATA
        foreach (var d in Lintel.Agents.UI.AgentsUiCatalog.Modules(Lintel.Agents.AgentsData.CreateService)) yield return d;
#elif PREVIEW_AGENTS_UI
        foreach (var d in Lintel.Agents.UI.AgentsUiCatalog.Modules(_ => new EmptyAgentUsageService())) yield return d;
#endif
#if PREVIEW_MEDIA
        foreach (var d in Lintel.Media.MediaCatalog.Modules) yield return d;
#endif
#if PREVIEW_TOOLS
        foreach (var d in Lintel.Tools.ToolsCatalog.Modules) yield return d;
#endif
#if PREVIEW_STASH
        foreach (var d in Lintel.Stash.StashCatalog.Modules) yield return d;
#endif
        yield break;
    }

    private static void AddModule(StackPanel page, INotchModule module, List<(string, FrameworkElement)> shots, List<ISectionLifecycle> shown)
    {
        var manifest = module.Manifest;
        page.Children.Add(Text($"{manifest.Name} ({manifest.Id})", "Notch.Title", margin: new Thickness(0, 24, 0, 8)));

        foreach (var activity in module.Activities)
        {
            var pill = new Grid { Width = 220, Height = 22, Background = Brushes.Transparent };
            pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pill.ColumnDefinitions.Add(new ColumnDefinition());
            pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var leading = Wing(activity.CreateLeading());
            pill.Children.Add(leading);
            if (activity.CreateTrailing() is { } trailing)
            {
                var wing = Wing(trailing);
                Grid.SetColumn(wing, 2);
                pill.Children.Add(wing);
            }
            var shape = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 3, 12, 3), Child = pill,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
            var label = Text("", "Notch.Caption", margin: new Thickness(12, 0, 0, 0));
            void Refresh()
            {
                label.Text = $"{activity.Id} · priority {activity.Priority} · {(activity.IsActive ? "active" : "inactive")} → {activity.TargetSectionId ?? "-"}";
                shape.Opacity = activity.IsActive ? 1 : 0.45;
            }
            activity.PropertyChanged += (_, _) => Refresh();
            Refresh();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(shape);
            row.Children.Add(label);
            row.Children[1].SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            page.Children.Add(row);
            shots.Add(($"activity-{activity.Id}", shape));
        }

        foreach (var section in module.Sections)
        {
            var size = new Size(Math.Clamp(section.PreferredSize.Width, MinSection.Width, MaxSection.Width),
                Math.Clamp(section.PreferredSize.Height, MinSection.Height, MaxSection.Height));
            page.Children.Add(Text($"Section {section.Id} · {section.Title} · {size.Width:0} × {size.Height:0}", "Notch.Caption", margin: new Thickness(0, 12, 0, 4)));
            var view = section.CreateView();
            if (view is ISectionLifecycle lifecycle)
                shown.Add(lifecycle);
            var panel = new Border
            {
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 20, 20),
                Padding = new Thickness(12, 0, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Border { Width = size.Width, Height = size.Height, Child = view, ClipToBounds = true },
            };
            page.Children.Add(panel);
            shots.Add(($"section-{section.Id}", panel));
        }

        if (module.CreateSettingsView() is { } settingsView)
        {
            // shown with the notch styles; the real Settings window restyles these plain controls.
            page.Children.Add(Text("Settings view", "Notch.Caption", margin: new Thickness(0, 12, 0, 4)));
            var host = new Border { Padding = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 680, Child = settingsView };
            host.SetResourceReference(Border.BackgroundProperty, "Notch.Surface");
            page.Children.Add(host);
            shots.Add(($"settings-{manifest.Id}", host));
        }
    }

#if PREVIEW_AGENTS_DATA
    private static IDisposable ShowAgentsData(StackPanel page, IModuleContext context)
    {
        var service = Lintel.Agents.AgentsData.CreateService(context);
        var dump = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Height = 700,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        void Show(AgentUsageSnapshot snapshot) => dump.Text = JsonSerializer.Serialize(snapshot, options);
        service.SnapshotChanged += (_, snapshot) => Show(snapshot);
        service.EventRaised += (_, e) => context.Log.Info($"Agent event: {e}");
        page.Children.Add(Text("Agents data (IAgentUsageService snapshot)", "Notch.Title", margin: new Thickness(0, 24, 0, 8)));
        page.Children.Add(dump);
        Show(service.Current);
        service.Start();
        return service;
    }
#endif

    private static Border Wing(FrameworkElement content) =>
        new() { MaxWidth = 96, MaxHeight = 22, VerticalAlignment = VerticalAlignment.Center, Child = content };

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Find<T>(child)) is { } found)
                return found;
        }
        return null;
    }

    private static TextBlock Text(string text, string style, string? brush = null, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 0, 0, 4) };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (brush is not null)
            block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private static void ApplyTextDefaults(Window window)
    {
        window.SetResourceReference(Control.ForegroundProperty, "Notch.Text");
        window.SetResourceReference(Control.FontFamilyProperty, "Notch.Font");
        window.FontSize = 11;
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width < 1 || height < 1)
            return;
        const double scale = 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
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

    private sealed class ConsoleLog : IAppLog
    {
        public void Info(string message) => Console.WriteLine($"[info]  {message}");

        public void Warn(string message, Exception? error = null) => Console.WriteLine($"[warn]  {message}{(error is null ? "" : " · " + error.Message)}");

        public void Error(string message, Exception? error = null) => Console.WriteLine($"[error] {message}{(error is null ? "" : Environment.NewLine + error)}");
    }

#if PREVIEW_AGENTS_UI && !PREVIEW_AGENTS_DATA
    /// <summary>Feeds the agents UI when built with -p:Only=Agents (the data project is not referenced).</summary>
    private sealed class EmptyAgentUsageService : IAgentUsageService
    {
        public AgentUsageSnapshot Current => AgentUsageSnapshot.Empty;

        public event EventHandler<AgentUsageSnapshot>? SnapshotChanged { add { } remove { } }

        public event EventHandler<AgentUsageEvent>? EventRaised { add { } remove { } }

        public void Start() { }

        public void Stop() { }

        public void SetSectionVisible(bool visible) { }

        public Task RefreshLimitsAsync(string? accountId = null) => Task.CompletedTask;

        public AgentAccount AddCodexHome(string folder, string displayName) => new("codex:" + displayName, AgentProvider.Codex, displayName, folder, false);

        public void RemoveAccount(string accountId) { }

        public void Dispose() { }
    }
#endif
}
