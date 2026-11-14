// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lintel.App.Settings.Pages;

/// <summary>Features hub: a two-column grid of feature cards, each with a switch that installs or removes it.</summary>
internal sealed class FeaturesPage : SettingsPage
{
    private readonly List<FeatureCard> cards = [];
    private readonly Dictionary<ModuleCategory, TextBlock> groupCounts = [];
    private readonly TextBlock summary;
    private readonly ProgressBar share;
    private readonly Border welcome;

    public FeaturesPage(SettingsWindow host, AppServices s)
        : base(host, s, "Features", "Install only what you use. A feature you skip is never loaded.")
    {
        welcome = Ui.Banner("You're all set. Hover the notch at the top of your screen to open it.", Tone.Success);
        welcome.Visibility = Visibility.Collapsed;
        Body.Children.Add(welcome);

        summary = Ui.Strong("", 12);
        share = new ProgressBar { Height = 3, Width = 150, Maximum = Math.Max(1, s.Modules.Catalog.Count), VerticalAlignment = VerticalAlignment.Center };
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Ui.Tile(SymbolRegular.Apps24, Palette.Blue, 28);
        tile.Margin = new Thickness(0, 0, 10, 0);
        hero.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(summary);
        text.Children.Add(Ui.Secondary("Removing one stops it at once; installing it again restores its settings."));
        Grid.SetColumn(text, 1);
        hero.Children.Add(text);
        share.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(share, 2);
        hero.Children.Add(share);
        var heroCard = Ui.Card(hero);
        heroCard.Margin = new Thickness(0, 0, 0, 14);
        Body.Children.Add(heroCard);

        foreach (var group in s.Modules.Catalog.GroupBy(d => d.Manifest.Category).OrderBy(g => g.Min(d => d.Manifest.Order)))
        {
            var header = new Grid { Margin = new Thickness(2, 0, 2, 5) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(Ui.Heading(CategoryName(group.Key)));
            var count = Ui.Tertiary("");
            Grid.SetColumn(count, 1);
            header.Children.Add(count);
            groupCounts[group.Key] = count;
            Body.Children.Add(header);
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, -3, -3, 11) };
            foreach (var descriptor in group.OrderBy(d => d.Manifest.Order))
            {
                var card = new FeatureCard(this, descriptor.Manifest);
                cards.Add(card);
                grid.Children.Add(card.Root);
            }
            Body.Children.Add(grid);
        }
        if (s.Modules.Catalog.Count == 0)
            Body.Children.Add(Ui.Card(Ui.Secondary("No features are built into this copy of Lintel.")));
        Refresh();
    }

    public void ShowWelcome() => welcome.Visibility = Visibility.Visible;

    public override void Refresh()
    {
        var installed = S.InstalledManifests.Count;
        summary.Text = $"{installed} of {S.Modules.Catalog.Count} features installed";
        share.Value = installed;
        foreach (var (category, label) in groupCounts)
        {
            var members = S.Modules.Catalog.Where(d => d.Manifest.Category == category).ToList();
            label.Text = $"{members.Count(d => S.IsInstalled(d.Manifest.Id))} of {members.Count} on";
        }
        foreach (var card in cards)
            card.Update();
    }

    private static string CategoryName(ModuleCategory category) => category switch
    {
        ModuleCategory.Agents => "AI",
        ModuleCategory.Media => "Media and sound",
        ModuleCategory.Productivity => "Productivity",
        _ => "System",
    };

    private sealed class FeatureCard
    {
        private readonly FeaturesPage page;
        private readonly ModuleManifest m;
        private readonly Border tileHost = new() { Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel status = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        private readonly ToggleSwitch toggle;
        private readonly StackPanel extras = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        private readonly TextBlock failure;
        private string state = "";
        private bool busy;

        public FeatureCard(FeaturesPage page, ModuleManifest manifest)
        {
            this.page = page;
            m = manifest;
            toggle = Ui.Toggle(false, OnToggle, $"Install {m.Name}");

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(tileHost);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Ui.Text(m.Name, 11.5, weight: FontWeights.SemiBold, wrap: false));
            name.Children.Add(status);
            Grid.SetColumn(name, 1);
            top.Children.Add(name);
            toggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(toggle, 2);
            top.Children.Add(toggle);

            var summary = Ui.Secondary(m.Summary.TrimEnd('.'));
            summary.Margin = new Thickness(0, 6, 0, 0);
            summary.MaxHeight = 27;   // two lines
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            summary.ToolTip = m.Summary;

            failure = Ui.Text("", Ui.Small, "SystemFillColorCriticalBrush");
            failure.Margin = new Thickness(0, 3, 0, 0);
            failure.Visibility = Visibility.Collapsed;

            var bottom = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition());
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.Children.Add(Needs());
            Grid.SetColumn(extras, 1);
            bottom.Children.Add(extras);

            var stack = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(summary, Dock.Top);
            DockPanel.SetDock(failure, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            stack.Children.Add(top);
            stack.Children.Add(summary);
            stack.Children.Add(failure);
            stack.Children.Add(bottom);
            Root = Ui.Card(stack, new Thickness(10, 9, 10, 9));
            Root.Margin = new Thickness(3);
        }

        public Border Root { get; }

        private FrameworkElement Needs()
        {
            var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
            var all = m.RequiredPermissions.Concat(m.OptionalPermissions).Select(PermissionCatalog.Find).OfType<PermissionInfo>().ToList();
            if (all.Count == 0)
            {
                wrap.Children.Add(Ui.Fact("No permissions needed", "SystemFillColorSuccessBrush", ""));
                return wrap;
            }
            foreach (var p in all)
            {
                var optional = !m.RequiredPermissions.Contains(p.Id);
                var tip = $"{p.PlainWords}{(optional ? " (optional)" : "")}{(p.LeavesPcTo is null ? "" : $" · leaves this PC: {p.LeavesPcTo}")}";
                wrap.Children.Add(Ui.Chip((p.LeavesPcTo is null ? "" : "↗ ") + p.Title, tip, optional));
            }
            return wrap;
        }

        public void Update()
        {
            var s = page.S;
            var moduleStatus = s.Modules.GetStatus(m.Id);
            var installed = s.IsInstalled(m.Id);
            var hasPage = page.Host.HasModulePage(m.Id);
            var key = $"{moduleStatus}|{installed}|{hasPage}|{s.Modules.GetFailure(m.Id)}";
            if (key == state)
                return;
            state = key;

            Ui.Set(toggle, installed);
            tileHost.Child = Ui.Tile(m.Glyph, Palette.ForModule(m.Id), 26, muted: !installed);
            status.Children.Clear();
            extras.Children.Clear();
            failure.Visibility = Visibility.Collapsed;
            switch (moduleStatus)
            {
                case ModuleStatus.Running:
                    status.Children.Add(Status("Running", "SystemFillColorSuccessBrush"));
                    if (hasPage)
                        extras.Children.Add(Ui.IconButton(SymbolRegular.Settings20, $"{m.Name} settings", () => page.Host.Navigate("module:" + m.Id)));
                    break;
                case ModuleStatus.NeedsPermission:
                    status.Children.Add(Status("Needs permission", "SystemFillColorCautionBrush"));
                    extras.Children.Add(Small("Allow…", $"Allow {m.Name}", () => _ = ConsentDialog.AllowAsync(page.Host, s, m)));
                    break;
                case ModuleStatus.Failed:
                    status.Children.Add(Status("Couldn't start", "SystemFillColorCriticalBrush"));
                    failure.Text = s.Modules.GetFailure(m.Id) ?? "";
                    failure.Visibility = failure.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    extras.Children.Add(Small("Retry", $"Retry {m.Name}", () => s.Modules.Install(m.Id)));
                    break;
                default:
                    status.Children.Add(Status(installed ? "Installed" : "Off", "TextFillColorTertiaryBrush"));
                    if (!installed && m.Recommended)
                    {
                        var pill = Ui.Pill("Recommended", Tone.Accent);
                        pill.Margin = new Thickness(6, 0, 0, 0);
                        status.Children.Add(pill);
                    }
                    break;
            }
        }

        private async void OnToggle(bool on)
        {
            if (busy)
                return;
            busy = true;
            try
            {
                var done = on
                    ? await ConsentDialog.InstallAsync(page.Host, page.S, m)
                    : await ConsentDialog.RemoveAsync(page.Host, page.S, m);
                if (!done)
                    Ui.Set(toggle, !on);   // cancelled: put the switch back
            }
            finally
            {
                busy = false;
                state = "";
                Update();
            }
        }

        private static StackPanel Status(string text, string brush)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 1, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
            row.Children.Add(dot);
            row.Children.Add(Ui.Secondary(text));
            return row;
        }

        private static Wpf.Ui.Controls.Button Small(string text, string name, Action click)
        {
            var button = Ui.Button(text, click, primary: true);
            button.Padding = new Thickness(8, 1, 8, 2);
            button.MinHeight = 22;
            button.FontSize = 10.5;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            return button;
        }
    }
}
