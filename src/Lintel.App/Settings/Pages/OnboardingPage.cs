// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;
using Lintel.Core.Settings;
using Wpf.Ui.Controls;

namespace Lintel.App.Settings.Pages;

/// <summary>
/// First run: preset → only the permissions those features need (+ the update check and startup switches) → Confirm.
/// Nothing is installed, granted or started before Confirm. Running it again starts from what is installed now.
/// </summary>
internal sealed class OnboardingPage : SettingsPage
{
    private readonly bool rerun;
    private readonly HashSet<string> custom;
    private readonly Dictionary<string, ToggleSwitch> optionalSwitches = [];
    private readonly StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private string preset;
    private ToggleSwitch? updatesSwitch;
    private ToggleSwitch? startupSwitch;

    public OnboardingPage(SettingsWindow host, AppServices s) : base(host, s, "")
    {
        rerun = s.Settings.Get(SettingKeys.AppOnboarded, false);
        custom = s.InstalledManifests.Select(m => m.Id).ToHashSet();
        preset = rerun ? "custom" : "recommended";
        SetFooter(footer);
        ShowPresets();
    }

    private IReadOnlyList<ModuleManifest> Catalog => S.Modules.Catalog.Select(d => d.Manifest).OrderBy(m => m.Order).ToList();

    private List<ModuleManifest> Chosen() => preset switch
    {
        "ai" => Catalog.Where(m => m.Category == ModuleCategory.Agents).ToList(),
        "recommended" => Catalog.Where(m => m.Recommended).ToList(),
        "everything" => Catalog.ToList(),
        _ => Catalog.Where(m => custom.Contains(m.Id)).ToList(),
    };

    // ---- step 1 --------------------------------------------------------------------------------------------------------

    private void ShowPresets()
    {
        SetTitle(rerun ? "Set up Lintel" : "Welcome to Lintel", "Pick what the notch should do. You can change it any time on the Features page.");
        Body.Children.Clear();

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -6, 6) };
        grid.Children.Add(Preset("ai", SymbolRegular.Sparkle24, Palette.Coral, "AI only", "Plan limits, tokens and live work of your coding agents",
            Catalog.Where(m => m.Category == ModuleCategory.Agents)));
        grid.Children.Add(Preset("recommended", SymbolRegular.Star24, Palette.Blue, "Recommended", "AI usage plus the everyday basics",
            Catalog.Where(m => m.Recommended)));
        grid.Children.Add(Preset("everything", SymbolRegular.Apps24, Palette.Purple, "Everything", $"All {Catalog.Count} features · each asks only for what it needs", Catalog));
        grid.Children.Add(Preset("custom", SymbolRegular.Options24, Palette.Gray, "Let me choose", "Pick features one by one", []));
        Body.Children.Add(grid);

        var list = Ui.Group("Choose features", Catalog.Select(m =>
        {
            var toggle = new ToggleSwitch { IsChecked = custom.Contains(m.Id) };
            System.Windows.Automation.AutomationProperties.SetName(toggle, m.Name);
            toggle.Checked += (_, _) => custom.Add(m.Id);
            toggle.Unchecked += (_, _) => custom.Remove(m.Id);
            return (UIElement)Ui.Row(Ui.Tile(m.Glyph, Palette.ForModule(m.Id)), m.Name, m.Summary.TrimEnd('.'), toggle);
        }));
        list.Visibility = preset == "custom" ? Visibility.Visible : Visibility.Collapsed;
        list.Margin = new Thickness(0, 6, 0, 6);
        Body.Children.Add(list);
        foreach (RadioButton radio in grid.Children)
            radio.Checked += (_, _) => list.Visibility = preset == "custom" ? Visibility.Visible : Visibility.Collapsed;

        footer.Children.Clear();
        if (rerun)
            footer.Children.Add(Spaced(Ui.Button("Cancel", () => Host.Navigate("general"))));
        footer.Children.Add(Ui.Button("Next", ShowPermissions, primary: true, icon: SymbolRegular.ArrowRight20));
    }

    private RadioButton Preset(string key, SymbolRegular icon, Color color, string name, string description, IEnumerable<ModuleManifest> includes)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = Ui.Tile(icon, color, 26);
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(tile);
        var text = new StackPanel();
        text.Children.Add(Ui.Strong(name, 11.5));
        text.Children.Add(Ui.Secondary(description));
        var tiles = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var m in includes)
        {
            var mini = Ui.Tile(m.Glyph, Palette.ForModule(m.Id), 16);
            mini.Margin = new Thickness(0, 0, 3, 0);
            mini.ToolTip = m.Name;
            tiles.Children.Add(mini);
        }
        if (tiles.Children.Count > 0)
            text.Children.Add(tiles);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var radio = new RadioButton { GroupName = "preset", Content = grid, IsChecked = preset == key, MinHeight = 62 };
        radio.Style = (Style)Host.FindResource("PresetCard");
        System.Windows.Automation.AutomationProperties.SetName(radio, $"{name}. {description}");
        radio.Checked += (_, _) => preset = key;
        return radio;
    }

    private static string Names(IEnumerable<ModuleManifest> modules) => string.Join(" · ", modules.Select(m => m.Name));

    // ---- step 2 --------------------------------------------------------------------------------------------------------

    public void ShowPermissions()
    {
        var chosen = Chosen();
        SetTitle("What it will access", "Only what the features you picked need. Change any of it later on the Permissions page.");
        Body.Children.Clear();
        optionalSwitches.Clear();

        var required = Catalog.Where(chosen.Contains).SelectMany(m => m.RequiredPermissions).Distinct().ToList();
        var optional = chosen.SelectMany(m => m.OptionalPermissions).Distinct().Except(required).ToList();
        var removed = S.InstalledManifests.Where(m => !chosen.Contains(m)).ToList();

        var picked = new WrapPanel();
        foreach (var m in chosen)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 10, 2) };
            var mini = Ui.Tile(m.Glyph, Palette.ForModule(m.Id), 16);
            mini.Margin = new Thickness(0, 0, 5, 0);
            chip.Children.Add(mini);
            var label = Ui.Text(m.Name, Ui.Body, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            chip.Children.Add(label);
            picked.Children.Add(chip);
        }
        if (chosen.Count == 0)
            picked.Children.Add(Ui.Secondary("No features picked — just the empty notch for now.", Ui.Body));
        var summary = new StackPanel();
        summary.Children.Add(picked);
        if (removed.Count > 0)
        {
            var note = Ui.Tertiary($"Removing {Names(removed)} · their data is kept");
            note.Margin = new Thickness(0, 6, 0, 0);
            summary.Children.Add(note);
        }
        var summaryCard = Ui.Card(summary, new Thickness(10, 6, 10, 6));
        summaryCard.Margin = new Thickness(0, 0, 0, 14);
        Body.Children.Add(summaryCard);

        if (required.Count > 0)
        {
            Body.Children.Add(Ui.Group("Needed", required.Select(PermissionCatalog.Find).OfType<PermissionInfo>()
                .Select(p => (UIElement)ConsentDialog.Row(p, Ui.Pill("Needed", Tone.Accent), For(chosen.Where(m => m.RequiredPermissions.Contains(p.Id)))))));
        }
        if (optional.Count > 0)
        {
            Body.Children.Add(Ui.Group("Optional · they make features better", optional.Select(PermissionCatalog.Find).OfType<PermissionInfo>().Select(p =>
            {
                var users = chosen.Where(m => m.OptionalPermissions.Contains(p.Id)).ToList();
                var toggle = new ToggleSwitch { IsChecked = S.Consent.IsGranted(p.Id) || users.Any(m => m.Recommended) };
                System.Windows.Automation.AutomationProperties.SetName(toggle, p.Title);
                optionalSwitches[p.Id] = toggle;
                return (UIElement)ConsentDialog.Row(p, toggle, For(users));
            })));
        }
        if (required.Count + optional.Count == 0 && chosen.Count > 0)
            Body.Children.Add(Ui.Banner("These features don't need any permissions.", Tone.Success));

        updatesSwitch = new ToggleSwitch { IsChecked = !rerun || S.UpdateChecksOn };
        startupSwitch = new ToggleSwitch { IsChecked = S.StartupEnabled };
        System.Windows.Automation.AutomationProperties.SetName(updatesSwitch, "Update check");
        System.Windows.Automation.AutomationProperties.SetName(startupSwitch, "Start with Windows");
        Body.Children.Add(Ui.Group("Lintel itself",
        [
            ConsentDialog.Row(PermissionCatalog.Find(PermissionIds.NetworkUpdates)!, updatesSwitch),
            ConsentDialog.Row(PermissionCatalog.Find(PermissionIds.Startup)!, startupSwitch),
        ]));

        footer.Children.Clear();
        footer.Children.Add(Spaced(Ui.Button("Back", ShowPresets, icon: SymbolRegular.ArrowLeft20)));
        footer.Children.Add(Ui.Button(rerun ? "Apply" : "Confirm and start", () => Confirm(chosen, required), primary: true, icon: SymbolRegular.Checkmark20));
    }

    private static FrameworkElement For(IEnumerable<ModuleManifest> users)
    {
        var text = Ui.Tertiary("For " + string.Join(", ", users.Select(m => m.Name)));
        text.Margin = new Thickness(0, 3, 0, 0);
        return text;
    }

    private void Confirm(List<ModuleManifest> chosen, List<string> required)
    {
        foreach (var id in required)
            S.Consent.Grant(id);
        foreach (var (id, toggle) in optionalSwitches)
        {
            if (toggle.IsChecked == true)
                S.Consent.Grant(id);
        }
        S.SetUpdateChecks(updatesSwitch?.IsChecked == true);
        var startup = startupSwitch?.IsChecked == true;
        if (startup != S.StartupEnabled)
            S.SetStartup(startup);
        foreach (var m in S.InstalledManifests.Where(m => !chosen.Contains(m)).ToList())
            S.Modules.Uninstall(m.Id, deleteData: false);
        S.Settings.Set(SettingKeys.AppOnboarded, true);
        foreach (var m in chosen)
            S.Modules.Install(m.Id);
        S.Settings.Flush();
        S.Log.Info($"Onboarding confirmed: {string.Join(", ", chosen.Select(m => m.Id))}");
        Host.CompleteOnboarding();
    }

    private static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, 8, 0);
        return element;
    }
}
