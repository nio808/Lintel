// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Lintel.App.Settings.Pages;

/// <summary>Hosts a running module's own settings view (plain WPF controls, styled by the app's Fluent resources) in a card.</summary>
internal sealed class ModulePage : SettingsPage
{
    public ModulePage(SettingsWindow host, AppServices s, string moduleId)
        : base(host, s, Manifest(s, moduleId)?.Name ?? moduleId, Manifest(s, moduleId)?.Summary.TrimEnd('.'))
    {
        var view = host.ModuleView(moduleId);
        if (view is null)
        {
            Body.Children.Add(Ui.Card(Ui.Secondary("This feature has no settings right now.")));
            return;
        }
        Detach(view);
        var card = Ui.Card(view, new Thickness(14, 12, 14, 12));
        card.Margin = new Thickness(0, 0, 0, 14);
        Body.Children.Add(card);

        if (Manifest(s, moduleId) is { } manifest)
        {
            Body.Children.Add(Ui.Group(null,
            [
                LinkRow(Ui.Tile(SymbolRegular.ShieldCheckmark24, Palette.Green), "Permissions", "What this feature may access", "permissions"),
                Ui.Row(Ui.Tile(manifest.Glyph, Palette.ForModule(manifest.Id)), $"Remove {manifest.Name}", "Keep or delete its data",
                    Ui.Button("Remove…", () => _ = ConsentDialog.RemoveAsync(host, s, manifest))),
            ]));
        }
    }

    private static Core.Modules.ModuleManifest? Manifest(AppServices s, string id) =>
        s.Modules.Catalog.FirstOrDefault(d => d.Manifest.Id == id)?.Manifest;

    /// <summary>The view is cached per window and re-hosted on every visit, so take it out of its previous card first.</summary>
    private static void Detach(FrameworkElement view)
    {
        switch (view.Parent)
        {
            case ContentControl owner:
                owner.Content = null;
                break;
            case Decorator owner:
                owner.Child = null;
                break;
            case Panel owner:
                owner.Children.Remove(view);
                break;
        }
    }
}
