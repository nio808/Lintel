// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Settings;
using Lintel.Printers.Spooler;

namespace Lintel.Printers;

/// <summary>
/// Settings page: which printers to show, privacy, cleaning thresholds, reset statistics. Plain WPF controls (the Settings window
/// styles them); the module may be stopped while this is open, so everything goes through the settings store lazily.
/// </summary>
internal sealed class PrintersSettingsView : StackPanel
{
    private static readonly int[] IdleChoices = [7, 14, 21, 30, 60];
    private static readonly int[] PageChoices = [200, 300, 500, 750, 1000];

    private readonly Func<IModuleContext?> context;
    private readonly Func<StatsStore> stats;
    private readonly StackPanel printerList = new();
    private bool resetArmed;

    public PrintersSettingsView(Func<IModuleContext?> context, Func<StatsStore> stats)
    {
        this.context = context;
        this.stats = stats;
        Header("Printers to show", "Virtual printers (PDF, XPS, OneNote, Fax) are never listed", first: true);
        Children.Add(printerList);

        Header("Privacy", "");
        var hide = new CheckBox { Content = "Hide document names (show \"Print job\")", Margin = new Thickness(0, 2, 0, 2) };
        Children.Add(hide);

        Header("Cleaning advice", "A reminder appears when either limit is reached");
        var idle = Choice("Remind after not printing for", PrintersKeys.IdleDays, 14, IdleChoices, v => $"{v} days");
        var pages = Choice("Remind after this many pages", PrintersKeys.PagesBetweenCleans, 500, PageChoices, v => $"{v} pages");
        Children.Add(idle);
        Children.Add(pages);

        Header("Statistics", "Page counts are what Lintel saw printing, not the printer's own counter");
        var resetButton = new Button { Content = "Reset statistics", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
        resetButton.Click += (_, _) =>
        {
            if (!resetArmed)
            {
                resetArmed = true;
                resetButton.Content = "Click again to erase all printer statistics";
                return;
            }
            resetArmed = false;
            context()?.Settings.Remove(StatsStore.Key);
            this.stats().ResetAll();
            resetButton.Content = "Statistics erased";
        };
        resetButton.LostFocus += (_, _) =>
        {
            resetArmed = false;
            resetButton.Content = "Reset statistics";
        };
        Children.Add(resetButton);

        Loaded += (_, _) =>
        {
            if (context() is not { } ctx)
                return;
            hide.IsChecked = ctx.Settings.Get(PrintersKeys.HideNames, false);
            hide.Checked += (_, _) => ctx.Settings.Set(PrintersKeys.HideNames, true);
            hide.Unchecked += (_, _) => ctx.Settings.Set(PrintersKeys.HideNames, false);
            FillPrinters(ctx);
        };
    }

    private void FillPrinters(IModuleContext ctx)
    {
        printerList.Children.Clear();
        var snapshot = ctx.IsDemo ? new DemoSource().Read() : WinSpoolerSource.ReadSnapshot();
        var all = PrinterRules.Physical(snapshot.Printers);
        if (all.Count == 0)
            printerList.Children.Add(new TextBlock { Text = "No printers found.", Opacity = 0.6 });
        foreach (var printer in all)
        {
            var name = printer.Name;
            var box = new CheckBox { Content = name, Margin = new Thickness(0, 2, 0, 2) };
            box.IsChecked = !ctx.Settings.Get(PrintersKeys.Hidden, Array.Empty<string>()).Contains(name, StringComparer.OrdinalIgnoreCase);
            box.Click += (_, _) =>
            {
                var hidden = ctx.Settings.Get(PrintersKeys.Hidden, Array.Empty<string>()).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (box.IsChecked != true)
                    hidden.Add(name);
                ctx.Settings.Set(PrintersKeys.Hidden, hidden.ToArray());
            };
            printerList.Children.Add(box);
        }
    }

    private UIElement Choice(string label, string key, int fallback, int[] values, Func<int, string> text)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var combo = new ComboBox { MinWidth = 120 };
        Grid.SetColumn(combo, 1);
        foreach (var v in values)
            combo.Items.Add(new ComboBoxItem { Content = text(v), Tag = v });
        combo.Loaded += (_, _) =>
        {
            if (context() is not { } ctx)
                return;
            var current = ctx.Settings.Get(key, fallback);
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current) ?? combo.Items[Array.IndexOf(values, fallback)];
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem item)
                    ctx.Settings.Set(key, (int)item.Tag);
            };
        };
        row.Children.Add(combo);
        return row;
    }

    private void Header(string title, string note, bool first = false)
    {
        Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 14, 0, 2) });
        if (note.Length > 0)
            Children.Add(new TextBlock { Text = note, Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
    }
}
