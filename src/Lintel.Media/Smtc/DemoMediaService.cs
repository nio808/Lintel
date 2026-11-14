// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lintel.Media.Logic;

namespace Lintel.Media.Smtc;

/// <summary>Canned, fully local media source for --demo: invented track, drawn artwork, working transport.</summary>
public sealed class DemoMediaService(TimeProvider time) : IMediaService
{
    private static readonly Lazy<(ImageSource Art, Color Tint)> Cover = new(MakeCover);
    private NowPlaying state = null!;
    private string source = "Demo Player";

    public NowPlaying? Current => state;
    public IReadOnlyList<MediaSource> Sources => [new("demo.player", "Demo Player", source == "Demo Player"), new("demo.browser", "Web Browser", source != "Demo Player")];
    public event Action? Changed;

    public void Start()
    {
        var (art, tint) = Cover.Value;
        state = new NowPlaying("demo.player", source, "Paper Lanterns", "The Quiet Orchard", "Low Tide Hours", art, tint, true,
            TimeSpan.FromSeconds(72), TimeSpan.FromSeconds(228), time.GetUtcNow(), 1, true, true, true, true, true, false, RepeatMode.None);
        Changed?.Invoke();
    }

    public void TogglePlayPause() => Update(state with { Position = state.PositionAtTime(time.GetUtcNow()), PositionAt = time.GetUtcNow(), IsPlaying = !state.IsPlaying });
    public void Next() => Update(state with { Position = TimeSpan.Zero, PositionAt = time.GetUtcNow() });
    public void Previous() => Update(state with { Position = TimeSpan.Zero, PositionAt = time.GetUtcNow() });
    public void Seek(TimeSpan position) => Update(state with { Position = position, PositionAt = time.GetUtcNow() });
    public void SetShuffle(bool on) => Update(state with { Shuffle = on });
    public void SetRepeat(RepeatMode mode) => Update(state with { Repeat = mode });

    public void Select(string? sourceId)
    {
        source = sourceId == "demo.browser" ? "Web Browser" : "Demo Player";
        Update(state with { SourceName = source, SourceAppId = sourceId ?? "demo.player" });
    }

    public void Dispose()
    {
    }

    private void Update(NowPlaying next)
    {
        state = next;
        Changed?.Invoke();
    }

    private static (ImageSource, Color) MakeCover()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1F, 0x3A, 0x5F), Color.FromRgb(0xE0, 0x7A, 0x5F), 55), null, new Rect(0, 0, 240, 240));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE6, 0xF4, 0xD3, 0x5E)), null, new Point(150, 96), 46, 46);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x80, 0x10, 0x20, 0x38)), null, new Point(60, 220), 120, 70);
        }
        var bitmap = new RenderTargetBitmap(240, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return (bitmap, Artwork.TintOf(bitmap) ?? Colors.CornflowerBlue);
    }
}
