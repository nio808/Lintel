// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.App.Settings.Pages;
using Lintel.Core.Modules;
using Lintel.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lintel.App.Settings;

/// <summary>
/// Fluent settings window: created on demand, released on close. Left navigation with fixed pages, one page per
/// running module that has settings, and About; first run shows onboarding instead. Live: it listens to module, consent,
/// hotkey, update and a few setting changes and refreshes the visible page in place (coalesced, UI thread).
/// </summary>
internal partial class SettingsWindow : FluentWindow
{
    private static readonly (string Key, string Title, SymbolRegular Icon, Color Color)[] Fixed =
    [
        ("general", "General", SymbolRegular.Settings24, Palette.Gray),
        ("features", "Features", SymbolRegular.Apps24, Palette.Blue),
        ("permissions", "Permissions", SymbolRegular.ShieldCheckmark24, Palette.Green),
        ("shortcuts", "Shortcuts", SymbolRegular.Keyboard24, Palette.Indigo),
    ];

    private readonly AppServices s;
    private readonly Dictionary<INotchModule, FrameworkElement?> moduleViews = [];
    private readonly Dictionary<string, Border> badges = [];
    private List<string> moduleNav = [];
    private SettingsPage? page;
    private string currentKey = "";
    private bool navigating;
    private bool refreshQueued;
    private bool closed;
    private TextBlock? identityStatus;

    public SettingsWindow(AppServices services, string? startPage)
    {
        s = services;
        InitializeComponent();
        if (Environment.OSVersion.Version.Build < 22000)
            WindowBackdropType = WindowBackdropType.None;   // Windows 10: solid background instead of Mica
        SystemThemeWatcher.Watch(this, WindowBackdropType, true);
        BuildIdentity();
        Nav.SelectionChanged += (_, _) =>
        {
            if (!navigating && Nav.SelectedItem is ListBoxItem { Tag: string key })
                Navigate(key);
        };

        s.Modules.Changed += OnModulesChanged;
        s.Consent.Changed += OnConsentChanged;
        s.Hotkeys.Changed += OnHotkeysChanged;
        s.Updates.Changed += QueueRefresh;
        s.Settings.Changed += OnSettingChanged;
        // UnWatch needs the HWND, which is gone by Closed; forgetting it would keep this window alive in WPF-UI's list.
        Closing += (_, _) => SystemThemeWatcher.UnWatch(this);
        Closed += OnClosed;

        if (s.Settings.Get(SettingKeys.AppOnboarded, false))
            ShowMain(startPage ?? "general");
        else
            ShowOnboarding();
    }

    /// <summary>False in --screenshot runs so captures are not taken mid-transition.</summary>
    public bool Animate { get; set; } = true;

    public ContentDialogHost Dialogs => DialogHost;

    public FrameworkElement CaptureRoot => Root;

    public string CurrentKey => currentKey;

    public SettingsPage? Page => page;

    public IReadOnlyList<string> PageKeys => [.. Fixed.Select(f => f.Key), .. moduleNav, "about"];

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        // Beats the foreground lock when opened from the tray or by a second launch.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ShowOnboarding()
    {
        NavColumn.Width = new GridLength(0);
        NavPane.Visibility = Visibility.Collapsed;
        currentKey = "onboarding";
        SetPage(new OnboardingPage(this, s));
    }

    /// <summary>After onboarding: normal navigation, landing on the Features page with a welcome note.</summary>
    public void CompleteOnboarding()
    {
        ShowMain("features");
        if (page is FeaturesPage features)
            features.ShowWelcome();
    }

    public void Navigate(string key)
    {
        if (closed)
            return;
        if (NavPane.Visibility != Visibility.Visible)
        {
            ShowMain(key);
            return;
        }
        if (key.StartsWith("module:", StringComparison.Ordinal) && !moduleNav.Contains(key))
            key = "features";
        Select(key);
        if (key == currentKey && page is not null)
            return;
        currentKey = key;
        SetPage(key switch
        {
            "features" => new FeaturesPage(this, s),
            "permissions" => new PermissionsPage(this, s),
            "shortcuts" => new ShortcutsPage(this, s),
            "about" => new AboutPage(this, s),
            _ when key.StartsWith("module:", StringComparison.Ordinal) => new ModulePage(this, s, key["module:".Length..]),
            _ => new GeneralPage(this, s),
        });
    }

    /// <summary>Settings view of a running module (created once per window, null when it has none or it threw).</summary>
    public FrameworkElement? ModuleView(string moduleId) =>
        s.Modules.Running.FirstOrDefault(m => m.Manifest.Id == moduleId) is { } module && moduleViews.TryGetValue(module, out var view) ? view : null;

    public bool HasModulePage(string moduleId) => moduleNav.Contains("module:" + moduleId);

    private void ShowMain(string key)
    {
        NavColumn.Width = new GridLength(200);
        NavPane.Visibility = Visibility.Visible;
        RebuildNav();
        currentKey = "";
        Navigate(key);
    }

    private void SetPage(SettingsPage next)
    {
        page = next;
        ContentHost.Children.Clear();
        ContentHost.Children.Add(next);
        if (!Animate || !SystemParameters.ClientAreaAnimation)
            return;
        // Quiet entrance: rise 12 DIP with a decelerating curve while fading in.
        var shift = new TranslateTransform(0, 12);
        next.RenderTransform = shift;
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Select(string key)
    {
        navigating = true;
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == key);
        navigating = false;
    }

    private void RebuildNav()
    {
        SyncModuleViews();
        navigating = true;
        Nav.Items.Clear();
        badges.Clear();
        foreach (var (key, title, icon, color) in Fixed)
            Nav.Items.Add(Item(key, title, Ui.Tile(icon, color, 18)));
        if (moduleNav.Count > 0)
        {
            var header = Ui.Tertiary("Feature settings");
            header.Margin = new Thickness(0, 10, 0, 2);
            Nav.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false, MinHeight = 0 });
            foreach (var key in moduleNav)
            {
                var manifest = s.Modules.Catalog.First(d => "module:" + d.Manifest.Id == key).Manifest;
                Nav.Items.Add(Item(key, manifest.Name, Ui.Tile(manifest.Glyph, Palette.ForModule(manifest.Id), 18)));
            }
        }
        var about = Item("about", "About", Ui.Tile(SymbolRegular.Info24, Palette.Slate, 18));
        about.Margin = new Thickness(0, 10, 0, 1);
        Nav.Items.Add(about);
        navigating = false;
        Select(currentKey);
        UpdateBadges();
    }

    private ListBoxItem Item(string key, string title, FrameworkElement icon)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var label = Ui.Text(title, Ui.Body, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var badge = Ui.Pill("", Tone.Caution);
        badge.Visibility = Visibility.Collapsed;
        badge.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        badges[key] = badge;
        var item = new ListBoxItem { Content = grid, Tag = key, ToolTip = title.Length > 22 ? title : null };
        System.Windows.Automation.AutomationProperties.SetName(item, title);
        return item;
    }

    private void BuildIdentity()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = AppIcon(30);
        icon.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Strong("Lintel", 12));
        identityStatus = Ui.Text("", Ui.Small, "TextFillColorSecondaryBrush", wrap: false);
        text.Children.Add(identityStatus);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Identity.Child = grid;
    }

    public static Image AppIcon(double size)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
        // frame for 200% scaling, downsampled with high quality below that.
        var frame = frames.FirstOrDefault(f => f.PixelWidth >= size * 2) ?? frames[^1];
        var image = new Image { Source = frame, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private void UpdateBadges()
    {
        var running = s.Modules.Running.Count;
        var installed = s.InstalledManifests.Count;
        if (identityStatus is not null)
        {
            var notch = s.Notch.IsUserHidden ? "notch hidden" : "notch on";
            identityStatus.Text = installed == 0 ? $"No features · {notch}" : $"{running}/{installed} features · {notch}";
        }
        var attention = s.Modules.Catalog.Count(d => s.Modules.GetStatus(d.Manifest.Id) is ModuleStatus.NeedsPermission or ModuleStatus.Failed);
        SetBadge("features", attention > 0 ? attention.ToString() : null);
        var unused = s.UnusedPermissions.Count;
        SetBadge("permissions", unused > 0 ? unused.ToString() : null);
        SetBadge("about", s.Updates.Available is not null ? "Update" : null, Tone.Accent);
    }

    private void SetBadge(string key, string? text, Tone tone = Tone.Caution)
    {
        if (!badges.TryGetValue(key, out var old))
            return;
        if (old.Parent is not Grid grid)
            return;
        var index = grid.Children.IndexOf(old);
        var badge = Ui.Pill(text ?? "", tone);
        badge.Margin = old.Margin;
        badge.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(badge, 2);
        grid.Children.RemoveAt(index);
        grid.Children.Insert(index, badge);
        badges[key] = badge;
    }

    /// <summary>Creates settings views for newly running modules, drops views of stopped ones; true when the nav list changed.</summary>
    private bool SyncModuleViews()
    {
        var running = s.Modules.Running;
        foreach (var gone in moduleViews.Keys.Where(m => !running.Contains(m)).ToList())
            moduleViews.Remove(gone);
        foreach (var module in running.Where(m => !moduleViews.ContainsKey(m)))
        {
            try
            {
                moduleViews[module] = module.CreateSettingsView();
            }
            catch (Exception e)
            {
                s.Log.Error($"Settings view of {module.Manifest.Id} failed", e);
                moduleViews[module] = null;
            }
        }
        var nav = running.Where(m => moduleViews[m] is not null).Select(m => "module:" + m.Manifest.Id).ToList();
        var changed = !nav.SequenceEqual(moduleNav);
        moduleNav = nav;
        return changed;
    }

    private void OnModulesChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnConsentChanged(string permissionId) => QueueRefresh();

    private void OnHotkeysChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnSettingChanged(string key)
    {
        if (key == SettingKeys.NotchUserHidden || key.StartsWith("modules.", StringComparison.Ordinal))
            Dispatcher.BeginInvoke(QueueRefresh);
    }

    private void QueueRefresh()
    {
        if (refreshQueued || closed)
            return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            refreshQueued = false;
            if (closed)
                return;
            if (NavPane.Visibility == Visibility.Visible)
            {
                if (SyncModuleViews())
                    RebuildNav();
                if (currentKey.StartsWith("module:", StringComparison.Ordinal) && !moduleNav.Contains(currentKey))
                    Navigate("features");
                UpdateBadges();
            }
            page?.Refresh();
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        s.Modules.Changed -= OnModulesChanged;
        s.Consent.Changed -= OnConsentChanged;
        s.Hotkeys.Changed -= OnHotkeysChanged;
        s.Updates.Changed -= QueueRefresh;
        s.Settings.Changed -= OnSettingChanged;
        ContentHost.Children.Clear();
        moduleViews.Clear();
        page = null;
    }
}
