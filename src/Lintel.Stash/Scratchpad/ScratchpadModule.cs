// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;

namespace Lintel.Stash;

/// <summary>Scratchpad: up to 8 autosaved plain-text tabs.</summary>
internal sealed class ScratchpadModule : StashModule
{
    public static readonly ModuleManifest Info = new()
    {
        Id = "scratchpad",
        Name = "Scratchpad",
        Summary = "Autosaved plain-text notes in tabs.",
        Glyph = Glyphs.Note,
        Category = ModuleCategory.Productivity,
        Order = 80,
    };

    private ScratchpadStore? store;
    private ScratchpadSectionView? view;
    private IReadOnlyList<NotchSection> sections = [];

    public override ModuleManifest Manifest => Info;

    public override IReadOnlyList<NotchSection> Sections => sections;

    protected override void OnStart()
    {
        string? dir = Context.IsDemo ? null : Context.GetDataDirectory(Info.Id);
        store = new ScratchpadStore(dir, Context.Time);
        store.Load();
        if (Context.IsDemo)
            SeedDemo();
        sections =
        [
            new NotchSection
            {
                Id = "notes",
                Title = "Notes",
                Glyph = Glyphs.Note,
                CreateView = () => view ??= new ScratchpadSectionView(store, dir),
                PreferredSize = new Size(480, 240),
                Order = 80,
                WantsKeyboard = true,
            },
        ];
    }

    public override void Stop()
    {
        store?.Dispose();     // flushes pending text
        store = null;
    }

    private void SeedDemo()
    {
        var first = store!.Tabs[0];
        store.Rename(first.Id, "Today");
        store.SetText(first.Id, "- Ship the notch build\n- Reply to Sam about the invoice\n- Book dentist (Thu after 15:00)\n\nIdeas\n  * drag a file onto the pill to park it\n  * Ctrl+Alt+V for clipboard history");
        store.AddTab("Snippets: dotnet test --filter Stash");
        store.Rename(store.Tabs[1].Id, "Snippets");
        store.AddTab("");
    }
}
