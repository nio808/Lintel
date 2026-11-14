// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;

namespace Lintel.Stash;

/// <summary>Shelf: park files, links and text on the notch; drag them out later. References only, persisted.</summary>
internal sealed class ShelfModule : StashModule
{
    public static readonly ModuleManifest Info = new()
    {
        Id = "shelf",
        Name = "Shelf",
        Summary = "Drop files, links or text on the notch to park them; drag them out later.",
        Glyph = Glyphs.Tray,
        Category = ModuleCategory.Productivity,
        RequiredPermissions = [PermissionIds.ShelfFiles],
        Order = 70,
    };

    private ShelfActivity? activity;
    private ShelfSectionView? view;
    private IReadOnlyList<NotchSection> sections = [];
    private IReadOnlyList<CompactActivity> activities = [];

    public override ModuleManifest Manifest => Info;

    public override IReadOnlyList<NotchSection> Sections => sections;

    public override IReadOnlyList<CompactActivity> Activities => activities;

    internal ShelfStore Store { get; private set; } = null!;

    protected override void OnStart()
    {
        if (Context.IsDemo)
        {
            Store = new ShelfStore(null, Context.Time, _ => true);
            SeedDemo();
        }
        else
        {
            Store = new ShelfStore(Path.Combine(Context.GetDataDirectory(Info.Id), "items.json"), Context.Time);
            Store.Load();
        }
        activity = new ShelfActivity(Store);
        activities = [activity];
        sections =
        [
            new NotchSection
            {
                Id = "shelf",
                Title = "Shelf",
                Glyph = Glyphs.Tray,
                CreateView = () => view ??= new ShelfSectionView(Store),
                PreferredSize = new Size(460, 160),
                Order = 70,
                AcceptsDrop = ShelfSectionView.Accepts,
            },
        ];
    }

    public override void Stop() => activity?.Detach();

    private void SeedDemo()
    {
        var items = new (ShelfKind Kind, string Value, bool Folder)[]
        {
            (ShelfKind.File, @"C:\Users\you\Documents\Quarterly report.pdf", false),
            (ShelfKind.File, @"C:\Users\you\Pictures\Screenshots", true),
            (ShelfKind.Link, "https://learn.microsoft.com/dotnet/desktop/wpf/", false),
            (ShelfKind.File, @"C:\Users\you\Desktop\budget-2026.xlsx", false),
            (ShelfKind.Text, "Call back about the invoice before Friday", false),
            (ShelfKind.File, @"C:\Users\you\Downloads\setup.zip", false),
        };
        foreach (var (kind, value, folder) in items.Reverse())
            Store.Add(kind, value, folder);
    }
}

/// <summary>Tray glyph + item count in the collapsed pill while the shelf is non-empty (priority 25).</summary>
internal sealed class ShelfActivity : CompactActivity
{
    private readonly ShelfStore store;
    private TextBlock? count;

    public ShelfActivity(ShelfStore store) : base("shelf.count", 25, "shelf")
    {
        this.store = store;
        store.Changed += OnChanged;
        IsActive = store.Items.Count > 0;
    }

    public void Detach() => store.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        IsActive = store.Items.Count > 0;
        if (count is not null)
            count.Text = store.Items.Count.ToString();
    }

    public override FrameworkElement CreateLeading()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock { Text = Glyphs.Tray, Margin = new Thickness(0, 0, 4, 0) };
        glyph.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Icon");
        count = new TextBlock { Text = store.Items.Count.ToString(), VerticalAlignment = VerticalAlignment.Center };
        count.FontWeight = FontWeights.SemiBold;
        count.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Caption");
        count.SetResourceReference(TextBlock.ForegroundProperty, "Notch.Text");
        panel.Children.Add(glyph);
        panel.Children.Add(count);
        return panel;
    }
}
