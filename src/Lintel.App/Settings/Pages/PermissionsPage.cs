// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lintel.App.Settings.Pages;

/// <summary>
/// One row per permission: plain words, what it touches, whether anything leaves the PC, which installed
/// features use it, a switch, a Windows Settings link where Windows has a real switch, and the "not needed" flag.
/// Always re-reads consent, so changes made elsewhere (e.g. a module's own settings view) show up live.
/// </summary>
internal sealed class PermissionsPage : SettingsPage
{
    private readonly List<PermissionRow> rows = [];
    private readonly Border unusedBanner;
    private readonly TextBlock unusedText;
    private readonly TextBlock tally = Ui.Tertiary("");

    public PermissionsPage(SettingsWindow host, AppServices s)
        : base(host, s, "Permissions", "Nothing is read or done until you switch it on. Windows doesn't sandbox desktop apps, so Lintel enforces this itself.")
    {
        unusedBanner = Ui.Banner("", Tone.Caution, Ui.Button("Turn off", () =>
        {
            foreach (var id in S.UnusedPermissions)
                S.Consent.Revoke(id);
        }));
        unusedText = (TextBlock)((Grid)unusedBanner.Child).Children[1];
        Body.Children.Add(unusedBanner);

        // Two columns of permission cards: each is short and self-contained, so the page scans instead of scrolls.
        Body.Children.Add(Ui.Header("Used by features", tally));
        Body.Children.Add(Ui.Columns(2, PermissionCatalog.All.Where(p => !ConsentService.AppLevel.Contains(p.Id)).Select(Add).ToList(), 6));
        Body.Children.Add(Ui.Header("Used by Lintel itself"));
        Body.Children.Add(Ui.Columns(2, PermissionCatalog.All.Where(p => ConsentService.AppLevel.Contains(p.Id)).Select(Add).ToList(), 6));
        Refresh();
    }

    public override void Refresh()
    {
        var unused = S.UnusedPermissions;
        unusedBanner.Visibility = unused.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        unusedText.Text = unused.Count == 0 ? "" :
            $"{string.Join(", ", unused.Select(id => PermissionCatalog.Find(id)?.Title ?? id))} {(unused.Count == 1 ? "is" : "are")} on but not needed by anything installed.";
        foreach (var row in rows)
            row.Update(unused);
        tally.Text = $"{rows.Count(r => r.IsOn)} of {rows.Count} on";
    }

    private UIElement Add(PermissionInfo p)
    {
        var row = new PermissionRow(this, p);
        rows.Add(row);
        return Ui.Card(row.Root, new Thickness(0));
    }

    private sealed class PermissionRow
    {
        private readonly PermissionsPage page;
        private readonly PermissionInfo p;
        private readonly ToggleSwitch toggle;
        private readonly WrapPanel usedBy = new() { Margin = new Thickness(0, 2, 0, 0) };
        private readonly Wpf.Ui.Controls.Button notNeeded;

        public PermissionRow(PermissionsPage page, PermissionInfo permission)
        {
            this.page = page;
            p = permission;
            toggle = Ui.Toggle(false, Switch, p.Title);
            notNeeded = new Wpf.Ui.Controls.Button
            {
                Content = "Not needed by anything — turn off",
                Appearance = ControlAppearance.Transparent,
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(0, 3, 4, 0),
                FontSize = Ui.Small,
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed,
            };
            notNeeded.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCautionBrush");
            notNeeded.Click += (_, _) => Switch(false);

            var extra = new WrapPanel();
            extra.Children.Add(usedBy);
            extra.Children.Add(notNeeded);
            if (p.WindowsSettingsUri is { } uri)
            {
                var link = Ui.Link("Windows setting", uri);
                link.FontSize = Ui.Small;
                link.Padding = new Thickness(5, 0, 5, 1);
                link.Margin = new Thickness(0, 3, 0, 0);
                link.Icon = Ui.Icon(SymbolRegular.Open20, 10);
                extra.Children.Add(link);
            }
            Root = ConsentDialog.Row(p, toggle, extra);
        }

        public Grid Root { get; }

        public bool IsOn { get; private set; }

        public void Update(IReadOnlyList<string> unused)
        {
            var s = page.S;
            IsOn = p.Id == PermissionIds.Startup ? s.StartupEnabled
                : p.Id == PermissionIds.NetworkUpdates ? s.UpdateChecksOn
                : s.Consent.IsGranted(p.Id);
            Ui.Set(toggle, IsOn);
            notNeeded.Visibility = unused.Contains(p.Id) ? Visibility.Visible : Visibility.Collapsed;

            usedBy.Children.Clear();
            if (ConsentService.AppLevel.Contains(p.Id))
                return;
            var users = s.Modules.Catalog.Select(d => d.Manifest)
                .Where(m => m.RequiredPermissions.Contains(p.Id) || m.OptionalPermissions.Contains(p.Id))
                .ToList();
            foreach (var m in users.Where(m => s.IsInstalled(m.Id)))
            {
                var optional = !m.RequiredPermissions.Contains(p.Id);
                var waiting = !optional && s.Modules.GetStatus(m.Id) == ModuleStatus.NeedsPermission;
                var tip = optional ? $"{m.Name} works without it, but does more with it."
                    : IsOn ? $"Turning this off stops {m.Name}." : $"{m.Name} can't run until this is on.";
                usedBy.Children.Add(Ui.Chip(m.Name + (optional ? " · optional" : waiting ? " · waiting" : ""), tip));
            }
            if (usedBy.Children.Count == 0)
            {
                var none = Ui.Tertiary(users.Count == 0 ? "Not used yet" : $"For {string.Join(", ", users.Select(u => u.Name))} (not installed)");
                none.Margin = new Thickness(0, 3, 8, 0);
                usedBy.Children.Add(none);
            }
        }

        private void Switch(bool on)
        {
            var s = page.S;
            switch (p.Id)
            {
                case PermissionIds.Startup:
                    s.SetStartup(on);
                    break;
                case PermissionIds.NetworkUpdates:
                    s.SetUpdateChecks(on);
                    break;
                default:
                    if (on)
                        s.Consent.Grant(p.Id);
                    else
                        s.Consent.Revoke(p.Id);
                    break;
            }
            page.Refresh();
        }
    }
}
