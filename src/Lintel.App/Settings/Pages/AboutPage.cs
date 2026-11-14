// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Permissions;
using Lintel.Core.Settings;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lintel.App.Settings.Pages;

/// <summary>Version, update check / install, data and log folders, license and links.</summary>
internal sealed class AboutPage : SettingsPage
{
    private readonly TextBlock updateTitle = Ui.Text("", Ui.Body, weight: FontWeights.Medium);
    private readonly TextBlock updateDetail = Ui.Secondary("");
    private readonly StackPanel updateActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar progress = new() { Height = 3, Maximum = 1, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border updateTile = new() { Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };

    public AboutPage(SettingsWindow host, AppServices s) : base(host, s, "About")
    {
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = SettingsWindow.AppIcon(40);
        icon.Margin = new Thickness(0, 0, 12, 0);
        hero.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Strong("Lintel", 13));
        text.Children.Add(Ui.Secondary($"Version {s.App.Version} · GPL-3.0-or-later"));
        var tagline = Ui.Tertiary("AI plan limits at a glance, plus the small tools you choose, in a notch at the top of your screen.");
        tagline.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(tagline);
        Grid.SetColumn(text, 1);
        hero.Children.Add(text);
        var heroCard = Ui.Card(hero, new Thickness(12, 10, 12, 10));
        heroCard.Margin = new Thickness(0, 0, 0, 14);
        Body.Children.Add(heroCard);

        var update = new Grid { Margin = new Thickness(11, 6, 11, 6), MinHeight = Ui.RowHeight };
        update.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        update.ColumnDefinitions.Add(new ColumnDefinition());
        update.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        update.Children.Add(updateTile);
        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(updateTitle);
        status.Children.Add(updateDetail);
        status.Children.Add(progress);
        Grid.SetColumn(status, 1);
        update.Children.Add(status);
        updateActions.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(updateActions, 2);
        update.Children.Add(updateActions);
        Body.Children.Add(Ui.Group("Updates", [update]));

        Body.Children.Add(Ui.Header("Your data"));
        Body.Children.Add(Ui.Columns(2,
        [
            Ui.Card(Ui.Row(Ui.Tile(SymbolRegular.Folder24, Palette.Amber), "Data folder", "%LOCALAPPDATA%\\Lintel",
                Ui.Button("Open", () => OpenFolder(AppPaths.Root))), new Thickness(0)),
            Ui.Card(Ui.Row(Ui.Tile(SymbolRegular.DocumentText24, Palette.Gray), "Logs", "No tokens, prompts or clipboard",
                Ui.Button("Open", () => OpenFolder(AppPaths.Logs))), new Thickness(0)),
        ]));

        var links = new WrapPanel { Margin = new Thickness(5, 4, 5, 4) };
        links.Children.Add(Ui.Link("Privacy", Repo("blob/main/docs/PRIVACY.md")));
        links.Children.Add(Ui.Link("Permissions", Repo("blob/main/docs/PERMISSIONS.md")));
        links.Children.Add(Ui.Link("Troubleshooting", Repo("blob/main/docs/TROUBLESHOOTING.md")));
        links.Children.Add(Ui.Link("Source code", Repo("")));
        links.Children.Add(Ui.Link("License", Repo("blob/main/LICENSE")));
        Body.Children.Add(Ui.Group("Links", [links]));

        var credits = Ui.Tertiary("No account, no analytics, no tracking. Some feature ideas, parsing logic and the seed price list are adapted from vorssaint-utils (GPL-3.0-or-later). Built with WPF-UI, CommunityToolkit.Mvvm and NAudio. Not affiliated with Anthropic, OpenAI or GitHub.", 9.5);
        credits.Margin = new Thickness(2, 0, 0, 0);
        Body.Children.Add(credits);
        Refresh();
    }

    public override void Refresh()
    {
        var u = S.Updates;
        updateActions.Children.Clear();
        progress.Visibility = u.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        progress.Value = u.Progress ?? 0;
        Ui.SetColor(updateDetail, u.MessageIsError ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        var checkedText = u.LastChecked is { } last ? $"Checked {Ago(last)}" : "Not checked yet";
        if (u.Available is { } release)
        {
            updateTile.Child = Ui.Tile(SymbolRegular.ArrowDownload24, Palette.Blue);
            updateTitle.Text = $"Version {release.Version} is available";
            updateDetail.Text = u.Message ?? $"You have {S.App.Version} · the installer is checked against its SHA-256 before it runs";
            if (!u.Busy)
            {
                updateActions.Children.Add(Spaced(Ui.Button("Install", () => _ = u.InstallAsync(), primary: true)));
                updateActions.Children.Add(Spaced(Ui.Button("Skip", u.Skip)));
                if (release.HtmlUrl.Length > 0)
                    updateActions.Children.Add(Ui.Link("What's new", release.HtmlUrl));
            }
            return;
        }
        updateTile.Child = Ui.Tile(SymbolRegular.ArrowSync24, Palette.Green);
        if (!u.Allowed)
        {
            updateTitle.Text = "Update checks are off";
            updateDetail.Text = "Asks api.github.com for the latest version · nothing about you is sent";
            updateActions.Children.Add(Ui.Button("Allow and check", () =>
            {
                S.Consent.Grant(PermissionIds.NetworkUpdates);
                _ = u.CheckAsync(userInitiated: true);
            }, primary: true));
            return;
        }
        updateTitle.Text = u.Busy ? "Checking…" : u.Message is { } message && !u.MessageIsError ? message.TrimEnd('.') : "Lintel " + S.App.Version;
        updateDetail.Text = u.MessageIsError ? $"{u.Message} · {checkedText}" : checkedText;
        var check = Ui.Button("Check now", () => _ = u.CheckAsync(userInitiated: true), icon: SymbolRegular.ArrowSync20);
        check.IsEnabled = !u.Busy;
        updateActions.Children.Add(check);
    }

    private string Repo(string path) => $"https://github.com/{S.App.RepositoryOwner}/{S.App.RepositoryName}/{path}".TrimEnd('/');

    private void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Ui.Open(path, S.Log);
    }

    private static string Ago(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours} h ago";
        return when.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
    }

    private static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, 6, 0);
        return element;
    }
}
