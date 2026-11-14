// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Permissions;

namespace Lintel.Agents.UI;

/// <summary>
/// The module's Settings page, compact: small section labels, one-line rows with the control on the right, two-column grids.
/// Plain WPF controls only (the Settings window styles them); colours are inherited, secondary text is dimmed with opacity so
/// light and dark themes both work. Subscriptions live only while loaded.
/// </summary>
internal sealed class AgentsSettingsView : StackPanel
{
    private static readonly (string Id, string Name)[] CardNames =
    [
        ("limits", "Limits (one card per account)"), ("spend", "API value"), ("live", "Working now"),
        ("trend", "Trend"), ("models", "Top models"), ("projects", "Top projects"),
    ];

    private static readonly int[] ThresholdChoices = [50, 30, 20, 10, 5];

    private readonly IModuleContext context;
    private readonly IAgentUsageService? service;
    private readonly StackPanel cards = new();
    private readonly StackPanel accounts = new();
    private readonly List<(string Id, CheckBox Box)> consentBoxes = [];
    private string accountKey = "";

    public AgentsSettingsView(IModuleContext context, IAgentUsageService? service)
    {
        this.context = context;
        this.service = service;
        Header("Agents", "A switched-off agent is not read at all", first: true);
        var providers = new UniformGrid { Columns = 4 };
        foreach (var (provider, label) in new[] { (AgentProvider.Claude, "Claude"), (AgentProvider.Codex, "Codex"), (AgentProvider.Copilot, "Copilot"), (AgentProvider.OpenCode, "OpenCode") })
            providers.Children.Add(Check(label, UiKeys.Provider(provider), true));
        Children.Add(providers);

        Header("Plan limit sources", "Each one is a permission; switching it on shows what it touches first");
        var sources = new UniformGrid { Columns = 2 };
        foreach (var id in new[] { PermissionIds.ClaudeCredentials, PermissionIds.ClaudeAppHistory, PermissionIds.CodexCli, PermissionIds.NetworkPrices })
            sources.Children.Add(ConsentRow(id));
        Children.Add(sources);

        Header("Alerts", "Never on the first reading after start; interrupted turns stay quiet");
        var alerts = new UniformGrid { Columns = 3 };
        alerts.Children.Add(Check("Limit running low", UiKeys.AlertsEnabled, true));
        alerts.Children.Add(Check("A warned limit resets", UiKeys.AlertsOnReset, true));
        alerts.Children.Add(Check("A long task finished", UiKeys.FinishEnabled, true));
        Children.Add(alerts);
        Children.Add(Row("Warn when this much is left", Thresholds()));
        Children.Add(Row("Count a task as long after", Choice(UiKeys.FinishMinSeconds, 60, (0, "Any length"), (30, "30 seconds"), (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes"))));

        Header("Pill", "What the collapsed notch shows");
        Children.Add(Row("Ring and number follow", Choice(UiKeys.PillFocus, "mostUsed", ("mostUsed", "Most used window"), ("session", "Session (5-hour)"), ("weekly", "Weekly window"))));
        Children.Add(Row("Percentages show", Choice(UiKeys.PillDisplay, "remaining", ("remaining", "Remaining"), ("used", "Used"))));
        Children.Add(Row("While an agent works", Choice(UiKeys.PillReadout, "elapsed", ("elapsed", "Elapsed time"), ("tokens", "Tokens"), ("cost", "API value"), ("limit", "Plan limit"))));

        Header("Cards", "Order and visibility in the AI section");
        Children.Add(cards);
        BuildCards();

        if (service is not null)
        {
            Header("Codex accounts", "Extra Codex homes, each with its own login, limits card and sessions");
            Children.Add(accounts);
            BuildAccounts();
        }

        Loaded += (_, _) =>
        {
            context.Consent.Changed += OnConsentChanged;
            if (service is not null)
                service.SnapshotChanged += OnSnapshot;
        };
        Unloaded += (_, _) =>
        {
            context.Consent.Changed -= OnConsentChanged;
            if (service is not null)
                service.SnapshotChanged -= OnSnapshot;
        };
    }

    private void Header(string title, string description, bool first = false)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, first ? 0 : 14, 0, 4) };
        line.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom });
        line.Children.Add(new TextBlock { Text = description, FontSize = 10, Opacity = 0.6, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis });
        Children.Add(line);
        Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 0, 0, 4) });
    }

    private static readonly Brush Hairline = Frozen(Color.FromArgb(0x26, 0x80, 0x80, 0x80));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private CheckBox Check(string text, string key, bool fallback)
    {
        var box = new CheckBox { Content = text, IsChecked = context.Settings.Get(key, fallback), Margin = new Thickness(0, 2, 12, 2), MinHeight = 28 };
        box.Click += (_, _) => context.Settings.Set(key, box.IsChecked == true);
        return box;
    }

    private ComboBox Choice<T>(string key, T fallback, params (T Value, string Text)[] options)
    {
        var combo = new ComboBox { MinWidth = 170 };
        foreach (var (value, text) in options)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        var current = context.Settings.Get(key, fallback);
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, current)) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value })
                context.Settings.Set(key, value);
        };
        return combo;
    }

    /// <summary>Label on the left, control on the right, ~32 DIP.</summary>
    private static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private FrameworkElement Thresholds()
    {
        // Chips: one toggle per threshold, checked = alert at that share left.
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var value in ThresholdChoices)
        {
            var box = new ToggleButton
            {
                Content = $"{value}%", Margin = new Thickness(4, 0, 0, 0), MinWidth = 40, MinHeight = 24, Padding = new Thickness(8, 2, 8, 3),
                FontSize = 10.5, IsChecked = Current().Contains(value),
            };
            AutomationName(box, $"Alert at {value}% left");
            box.Click += (_, _) =>
            {
                var set = Current().ToHashSet();
                if (box.IsChecked == true)
                    set.Add(value);
                else
                    set.Remove(value);
                context.Settings.Set(UiKeys.AlertsThresholds, set.OrderDescending().ToArray());
            };
            panel.Children.Add(box);
        }
        return panel;

        int[] Current() => context.Settings.Get(UiKeys.AlertsThresholds, new[] { 20, 5 }) ?? [20, 5];
    }

    private FrameworkElement ConsentRow(string permissionId)
    {
        var info = PermissionCatalog.All.First(p => p.Id == permissionId);
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = info.Title, FontWeight = FontWeights.Medium });
        label.Children.Add(new TextBlock { Text = info.PlainWords, FontSize = 10, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        var box = new CheckBox { Content = label, IsChecked = context.Consent.IsGranted(permissionId), Margin = new Thickness(0, 3, 12, 3), VerticalContentAlignment = VerticalAlignment.Top };
        consentBoxes.Add((permissionId, box));

        var explain = new StackPanel { Margin = new Thickness(26, 0, 12, 6), Visibility = Visibility.Collapsed };
        explain.Children.Add(new TextBlock { Text = "Touches: " + info.Touches, FontSize = 10, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        explain.Children.Add(new TextBlock
        {
            Text = info.LeavesPcTo is null ? "Nothing leaves this PC." : "Leaves this PC: " + info.LeavesPcTo,
            FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 4), Opacity = 0.8,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var allow = new Button { Content = "Allow", Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Not now", Padding = new Thickness(10, 2, 10, 3) };
        buttons.Children.Add(allow);
        buttons.Children.Add(cancel);
        explain.Children.Add(buttons);

        box.Click += (_, _) =>
        {
            if (box.IsChecked == true && !context.Consent.IsGranted(permissionId))
            {
                box.IsChecked = false;          // only "Allow" grants it
                explain.Visibility = Visibility.Visible;
            }
            else if (box.IsChecked != true)
            {
                explain.Visibility = Visibility.Collapsed;
                context.Consent.Revoke(permissionId);
            }
        };
        allow.Click += (_, _) =>
        {
            context.Consent.Grant(permissionId);
            box.IsChecked = true;
            explain.Visibility = Visibility.Collapsed;
        };
        cancel.Click += (_, _) => explain.Visibility = Visibility.Collapsed;

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(explain);
        return panel;
    }

    private void OnConsentChanged(string permissionId)
    {
        foreach (var (id, box) in consentBoxes)
        {
            if (id == permissionId)
                box.IsChecked = context.Consent.IsGranted(id);
        }
    }

    private void BuildCards()
    {
        cards.Children.Clear();
        var order = Order();
        var hidden = context.Settings.Get(UiKeys.CardsHidden, Array.Empty<string>()) ?? [];
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var index = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new CheckBox { Content = CardNames.First(c => c.Id == id).Name, IsChecked = !hidden.Contains(id), VerticalAlignment = VerticalAlignment.Center };
            box.Click += (_, _) =>
            {
                var set = (context.Settings.Get(UiKeys.CardsHidden, Array.Empty<string>()) ?? []).ToHashSet();
                if (box.IsChecked == true)
                    set.Remove(id);
                else
                    set.Add(id);
                context.Settings.Set(UiKeys.CardsHidden, set.ToArray());
            };
            row.Children.Add(box);
            row.Children.Add(Arrow("", "Move up", 1, index > 0, () => Move(index, -1)));
            row.Children.Add(Arrow("", "Move down", 2, index < order.Count - 1, () => Move(index, 1)));
            cards.Children.Add(row);
        }
    }

    private static Button Arrow(string glyph, string tip, int column, bool enabled, Action click)
    {
        var button = new Button
        {
            Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
            Width = 26, Height = 22, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, IsEnabled = enabled,
        };
        AutomationName(button, tip);
        button.Click += (_, _) => click();
        Grid.SetColumn(button, column);
        return button;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private List<string> Order()
    {
        var saved = context.Settings.Get(UiKeys.CardsOrder, UiKeys.DefaultCards) ?? UiKeys.DefaultCards;
        return saved.Where(UiKeys.DefaultCards.Contains).Concat(UiKeys.DefaultCards).Distinct().ToList();
    }

    private void Move(int index, int delta)
    {
        var order = Order();
        (order[index], order[index + delta]) = (order[index + delta], order[index]);
        context.Settings.Set(UiKeys.CardsOrder, order.ToArray());
        BuildCards();
    }

    private void OnSnapshot(object? sender, AgentUsageSnapshot snapshot)
    {
        var key = string.Join("|", Extra(snapshot).Select(a => a.Id + a.DisplayName));
        if (key != accountKey)
            BuildAccounts();
    }

    private static IEnumerable<AgentAccount> Extra(AgentUsageSnapshot snapshot) =>
        snapshot.Accounts.Where(a => a.Provider == AgentProvider.Codex && !a.IsDefault);

    private void BuildAccounts()
    {
        if (service is null)
            return;
        accounts.Children.Clear();
        var extra = Extra(service.Current).ToList();
        accountKey = string.Join("|", extra.Select(a => a.Id + a.DisplayName));
        if (extra.Count == 0)
            accounts.Children.Add(new TextBlock { Text = "Only the default Codex home is read.", FontSize = 10.5, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 2) });
        foreach (var account in extra)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = account.DisplayName, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = account.HomePath ?? "", FontSize = 10, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 3), VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) =>
            {
                service.RemoveAccount(account.Id);
                BuildAccounts();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            accounts.Children.Add(row);
        }

        var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var name = new TextBox { MinWidth = 180, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationName(name, "Account name");
        var confirm = new Button { Content = "Add", Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 2, 10, 3) };
        entry.Children.Add(name);
        entry.Children.Add(confirm);
        entry.Children.Add(cancel);
        var pick = new Button { Content = "Add a Codex home…", Padding = new Thickness(10, 2, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        string? folder = null;
        pick.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose a Codex home folder (the one with auth.json)" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            folder = dialog.FolderName;
            name.Text = Path.GetFileName(folder.TrimEnd('\\', '/')).TrimStart('.');
            entry.Visibility = Visibility.Visible;
            pick.Visibility = Visibility.Collapsed;
            name.Focus();
            name.SelectAll();
        };
        confirm.Click += (_, _) =>
        {
            if (folder is null || string.IsNullOrWhiteSpace(name.Text))
                return;
            try
            {
                service.AddCodexHome(folder, name.Text.Trim());
            }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
            {
                MessageBox.Show(Window.GetWindow(this)!, e.Message, "Lintel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            BuildAccounts();
        };
        cancel.Click += (_, _) => BuildAccounts();
        accounts.Children.Add(pick);
        accounts.Children.Add(entry);
    }
}
