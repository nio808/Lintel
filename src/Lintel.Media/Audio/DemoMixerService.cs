// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Media.Logic;

namespace Lintel.Media.Audio;

/// <summary>Invented apps and a fixed device for --demo; sliders and mutes work locally.</summary>
public sealed class DemoMixerService : IMixerService
{
    private List<AppRow> apps =
    [
        new("demo:player", "Demo Player", null, [1], 0.8f, false, 0.55f, true),
        new("demo:browser", "Web Browser", null, [2], 0.55f, false, 0.2f, true),
        new("demo:chat", "Voice Chat", null, [3], 1f, true, 0f, true),
        new("demo:game", "Pixel Quest", null, [4], 0.35f, false, 0f, false),
    ];
    private double master = 0.62;
    private bool masterMuted;

    public MixerState Current { get; private set; } = MixerState.Empty;
    public event Action? Changed;
    public event Action<double, bool>? MasterChangedExternally { add { } remove { } }

    public void Start() => Publish();
    public void SetVisible(bool visible) => Publish();
    public void SetMaster(double volume) { master = Math.Clamp(volume, 0, 1); Publish(); }
    public void SetMasterMute(bool muted) { masterMuted = muted; Publish(); }
    public void SetApp(string key, double volume) => Map(key, r => r with { Volume = (float)Math.Clamp(volume, 0, 1) });
    public void SetAppMute(string key, bool muted) => Map(key, r => r with { Muted = muted });
    public void Dispose() { }

    private void Map(string key, Func<AppRow, AppRow> change)
    {
        apps = [.. apps.Select(r => r.Key == key ? change(r) : r)];
        Publish();
    }

    private void Publish()
    {
        Current = new MixerState(true, "Speakers (Demo Audio)", master, masterMuted, apps);
        Changed?.Invoke();
    }
}
