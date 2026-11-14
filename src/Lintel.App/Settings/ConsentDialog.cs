// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;
using Wpf.Ui.Controls;

namespace Lintel.App.Settings;

/// <summary>Install / allow / remove dialogs for a feature, plus the permission row shared with Permissions and onboarding.</summary>
internal static class ConsentDialog
{
    /// <summary>
    /// Asks before installing (or re-allowing) a feature. Returns the permission ids to grant, an empty list when there is
    /// nothing new to ask, or null when the user cancelled. Already granted permissions are shown, never re-asked.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> AskAsync(SettingsWindow host, AppServices s, ModuleManifest m, bool install)
    {
        var required = m.RequiredPermissions.Select(PermissionCatalog.Find).OfType<PermissionInfo>().ToList();
        var optional = install ? m.OptionalPermissions.Select(PermissionCatalog.Find).OfType<PermissionInfo>().ToList() : [];
        var newRequired = required.Where(p => !s.Consent.IsGranted(p.Id)).ToList();
        var newOptional = optional.Where(p => !s.Consent.IsGranted(p.Id)).ToList();
        if (newRequired.Count == 0 && newOptional.Count == 0)
            return [];

        var rows = new List<UIElement>();
        foreach (var p in required)
            rows.Add(Row(p, s.Consent.IsGranted(p.Id) ? Ui.Pill("On", Tone.Success) : Ui.Pill("Needed", Tone.Accent)));
        var checks = new Dictionary<string, ToggleSwitch>();
        foreach (var p in optional)
        {
            if (s.Consent.IsGranted(p.Id))
            {
                rows.Add(Row(p, Ui.Pill("On", Tone.Success)));
                continue;
            }
            var toggle = new ToggleSwitch { IsChecked = m.Recommended };
            System.Windows.Automation.AutomationProperties.SetName(toggle, p.Title);
            checks[p.Id] = toggle;
            rows.Add(Row(p, toggle, Ui.Tertiary("Optional")));
        }

        var panel = new StackPanel { Width = 480, Margin = new Thickness(0, 0, 12, 0) };
        var intro = Ui.Secondary(install ? $"{m.Summary.TrimEnd('.')}. Nothing is read until you install it." : $"{m.Name} is installed but can't run without this.", Ui.Body);
        intro.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(intro);
        panel.Children.Add(Ui.Group(null, rows));
        panel.Children.Add(Ui.Tertiary("You can change any of this later on the Permissions page."));

        var dialog = new ContentDialog(host.Dialogs)
        {
            Title = install ? $"Install {m.Name}?" : $"Allow {m.Name}?",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = install ? "Install" : "Allow",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 600,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return [.. newRequired.Select(p => p.Id), .. newOptional.Where(p => checks[p.Id].IsChecked == true).Select(p => p.Id)];
    }

    /// <summary>Install flow: ask (if anything new is needed), grant, then install so it starts right away. False when cancelled.</summary>
    public static async Task<bool> InstallAsync(SettingsWindow host, AppServices s, ModuleManifest m)
    {
        var grants = await AskAsync(host, s, m, install: true);
        if (grants is null)
            return false;
        foreach (var id in grants)
            s.Consent.Grant(id);
        s.Modules.Install(m.Id);
        return true;
    }

    /// <summary>A feature that lost a required permission: ask again; the module manager restarts it on grant.</summary>
    public static async Task AllowAsync(SettingsWindow host, AppServices s, ModuleManifest m)
    {
        var grants = await AskAsync(host, s, m, install: false);
        if (grants is null)
            return;
        foreach (var id in grants)
            s.Consent.Grant(id);
    }

    /// <summary>False when cancelled.</summary>
    public static async Task<bool> RemoveAsync(SettingsWindow host, AppServices s, ModuleManifest m)
    {
        var panel = new StackPanel { Width = 420 };
        panel.Children.Add(Ui.Text($"{m.Name} stops now and leaves the notch. Reinstall it later and it picks up where it left off — unless you delete its data."));
        var detail = Ui.Tertiary($"Data = its settings and files in %LOCALAPPDATA%\\Lintel\\data\\{m.Id}");
        detail.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(detail);
        var dialog = new ContentDialog(host.Dialogs)
        {
            Title = $"Remove {m.Name}?",
            Content = panel,
            PrimaryButtonText = "Keep my data",
            SecondaryButtonText = "Delete data",
            CloseButtonText = "Cancel",
            SecondaryButtonAppearance = ControlAppearance.Danger,
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 520,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                s.Modules.Uninstall(m.Id, deleteData: false);
                return true;
            case ContentDialogResult.Secondary:
                s.Modules.Uninstall(m.Id, deleteData: true);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Tile · title + plain words · what it touches and where data goes · optional control on the right.</summary>
    public static Grid Row(PermissionInfo p, UIElement? trailing, UIElement? extra = null)
    {
        var below = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        below.Children.Add(Ui.Fact(p.Touches, "TextFillColorTertiaryBrush", ""));
        // Where the data goes and who uses it share one line.
        var facts = new WrapPanel();
        var leaves = Leaves(p);
        leaves.Margin = new Thickness(0, 4, 12, 0);
        facts.Children.Add(leaves);
        if (extra is not null)
            facts.Children.Add(extra);
        below.Children.Add(facts);
        return Ui.Row(Ui.Tile(IconFor(p.Id), ColorFor(p.Id)), p.Title, p.PlainWords, trailing, below);
    }

    public static Grid Leaves(PermissionInfo p) => p.LeavesPcTo is null
        ? Ui.Fact("Stays on this PC", "SystemFillColorSuccessBrush", "")          // lock
        : Ui.Fact($"Leaves this PC: {p.LeavesPcTo}", "SystemFillColorCautionBrush", "");   // globe

    public static SymbolRegular IconFor(string permissionId) => permissionId switch
    {
        PermissionIds.AgentLogs => SymbolRegular.DocumentBulletList24,
        PermissionIds.ClaudeCredentials => SymbolRegular.Key24,
        PermissionIds.ClaudeAppHistory => SymbolRegular.History24,
        PermissionIds.CodexCli => SymbolRegular.Code24,
        PermissionIds.NetworkPrices => SymbolRegular.CloudArrowDown24,
        PermissionIds.MediaSession => SymbolRegular.MusicNote224,
        PermissionIds.AudioSessions => SymbolRegular.Speaker224,
        PermissionIds.ClipboardRead => SymbolRegular.Clipboard24,
        PermissionIds.InputPaste => SymbolRegular.ClipboardPaste24,
        PermissionIds.ShelfFiles => SymbolRegular.BoxMultiple24,
        PermissionIds.DownloadsFolder => SymbolRegular.ArrowDownload24,
        PermissionIds.KeepAwake => SymbolRegular.Power24,
        PermissionIds.SystemStats => SymbolRegular.Gauge24,
        PermissionIds.SystemToggles => SymbolRegular.Options24,
        PermissionIds.Startup => SymbolRegular.Rocket24,
        PermissionIds.NetworkUpdates => SymbolRegular.ArrowSync24,
        _ => SymbolRegular.Shield24,
    };

    public static Color ColorFor(string permissionId) => permissionId switch
    {
        PermissionIds.AgentLogs => Palette.Coral,
        PermissionIds.ClaudeCredentials => Palette.Amber,
        PermissionIds.ClaudeAppHistory => Palette.Orange,
        PermissionIds.CodexCli => Palette.Slate,
        PermissionIds.NetworkPrices => Palette.Teal,
        PermissionIds.MediaSession => Palette.Pink,
        PermissionIds.AudioSessions => Palette.Purple,
        PermissionIds.ClipboardRead => Palette.Blue,
        PermissionIds.InputPaste => Palette.Indigo,
        PermissionIds.ShelfFiles => Palette.Teal,
        PermissionIds.DownloadsFolder => Palette.Indigo,
        PermissionIds.KeepAwake => Palette.Slate,
        PermissionIds.SystemStats => Palette.Green,
        PermissionIds.SystemToggles => Palette.Gray,
        PermissionIds.Startup => Palette.Coral,
        PermissionIds.NetworkUpdates => Palette.Green,
        _ => Palette.Blue,
    };
}
