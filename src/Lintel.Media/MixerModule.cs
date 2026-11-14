// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Media.Audio;
using Lintel.Media.Views;

namespace Lintel.Media;

/// <summary>Volume Mixer: master volume plus one slider, mute and peak meter per app.</summary>
public sealed class MixerModule : INotchModule
{
    public const string VolumePeekKey = "mixer.volumePeek";   // bool, default false

    public static ModuleManifest ManifestInfo { get; } = new()
    {
        Id = "mixer",
        Name = "Volume Mixer",
        Summary = "Master volume and a slider, mute and level meter for every app that makes sound.",
        Glyph = Glyphs.Volume,
        Category = ModuleCategory.Media,
        RequiredPermissions = [PermissionIds.AudioSessions],
        Recommended = true,
        Order = 30,
    };

    private IModuleContext? context;
    private IMixerService? mixer;
    private IReadOnlyList<NotchSection> sections = [];

    public ModuleManifest Manifest => ManifestInfo;
    public IReadOnlyList<NotchSection> Sections => sections;
    public IReadOnlyList<CompactActivity> Activities => [];

    public void Start(IModuleContext ctx)
    {
        context = ctx;
        var service = ctx.IsDemo ? new DemoMixerService() : (IMixerService)new AudioSessionService(ctx.Ui, ctx.Log);
        mixer = service;
        service.MasterChangedExternally += (volume, muted) =>
        {
            if (!ctx.Settings.Get(VolumePeekKey, false))
                return;
            ctx.Notch.ShowNotice(new NotchNotice
            {
                Id = "mixer.volume",
                Title = muted ? "Muted" : $"Volume {Math.Round(volume * 100):0}%",
                Glyph = muted || volume <= 0 ? Glyphs.Mute : Glyphs.Volume,
                Duration = TimeSpan.FromSeconds(1.5),
                TargetSectionId = "mixer",
            });
        };
        sections =
        [
            new NotchSection
            {
                Id = "mixer",
                Title = "Mixer",
                Glyph = Glyphs.Volume,
                Order = 30,
                PreferredSize = new Size(460, 220),
                CreateView = () => new MixerSectionView(service),
            },
        ];
        service.Start();
    }

    public void Stop()
    {
        mixer?.Dispose();
        mixer = null;
    }

    public FrameworkElement? CreateSettingsView()
    {
        var settings = context?.Settings;
        if (settings is null)
            return null;
        var peek = new CheckBox { Content = "Show a brief notice when the master volume changes", IsChecked = settings.Get(VolumePeekKey, false) };
        peek.Click += (_, _) => settings.Set(VolumePeekKey, peek.IsChecked == true);
        return new StackPanel { Children = { peek } };
    }

    public void DeleteData() => context?.Settings.RemovePrefix("mixer.");
}
