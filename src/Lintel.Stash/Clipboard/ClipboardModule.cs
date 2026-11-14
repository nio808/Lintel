// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;

namespace Lintel.Stash;

/// <summary>Clipboard history: listener, policy, store, optional DPAPI persistence, paste-back.</summary>
internal sealed class ClipboardModule : StashModule
{
    public const string KeyLimit = "clipboard.limit", KeyPersist = "clipboard.persist", KeyPasteOnClick = "clipboard.pasteOnClick",
        KeyExcluded = "clipboard.excludedApps", KeyPaused = "clipboard.paused";

    public static readonly ModuleManifest Info = new()
    {
        Id = "clipboard",
        Name = "Clipboard History",
        Summary = "Searchable local history of text, files and images; pin, click to copy or paste.",
        Glyph = Glyphs.Clipboard,
        Category = ModuleCategory.Productivity,
        RequiredPermissions = [PermissionIds.ClipboardRead],
        OptionalPermissions = [PermissionIds.InputPaste],
        Recommended = true,
        Order = 50,
    };

    private ClipboardStore store = null!;
    private readonly ClipboardPolicy policy = new();
    private ClipboardListener? listener;
    private Debouncer? saver;
    private string? historyPath;
    private ClipboardSectionView? view;
    private IReadOnlyList<NotchSection> sections = [];
    private bool started;

    public override ModuleManifest Manifest => Info;

    public override IReadOnlyList<NotchSection> Sections => sections;

    internal ClipboardStore Store => store;

    internal ClipboardPolicy Policy => policy;

    internal bool IsDemo => Context.IsDemo;

    internal TimeProvider Time => Context.Time;

    protected override void OnStart()
    {
        started = true;
        store = new ClipboardStore(Context.Time);
        ApplySettings();
        Context.Settings.Changed += OnSettingChanged;
        sections =
        [
            new NotchSection
            {
                Id = "clipboard",
                Title = "Clipboard",
                Glyph = Glyphs.Clipboard,
                CreateView = () => view ??= new ClipboardSectionView(this),
                PreferredSize = new Size(480, 260),
                Order = 50,
                WantsKeyboard = true,
            },
        ];

        if (Context.IsDemo)
        {
            SeedDemo();
            return;
        }

        historyPath = Path.Combine(Context.GetDataDirectory(Info.Id), "history.bin");
        saver = new Debouncer(TimeSpan.FromSeconds(1), () => OnUi(() => SaveNow(background: true)), Context.Time);
        store.Changed += (_, _) => { if (Persist) saver.Trigger(); };
        if (Persist)
        {
            var path = historyPath;
            Task.Run(() => ClipboardPersistence.Load(path)).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    OnUi(() => { if (started) { store.Load(t.Result); store.Expire(); } });
            });
        }

        listener = new ClipboardListener(Context.Ui);
        listener.Changed += OnClipboardChanged;
        listener.Start();
        Context.Hotkeys.Register("clipboard.open", "Ctrl+Alt+V", "Open clipboard history",
            () => OnUi(() => Context.Notch.Expand("clipboard", keyboard: true)));
    }

    public override void Stop()
    {
        if (!started)
            return;
        started = false;
        Context.Settings.Changed -= OnSettingChanged;
        Context.Hotkeys.Unregister("clipboard.open");
        listener?.Dispose();
        listener = null;
        saver?.Dispose();
        saver = null;
        if (!Context.IsDemo)
            SaveNow(background: false);
    }

    private bool Persist => Context.Settings.Get(KeyPersist, true);

    private void OnSettingChanged(string key)
    {
        if (key.StartsWith("clipboard.", StringComparison.Ordinal))
            OnUi(ApplySettings);
    }

    private void ApplySettings()
    {
        if (!started)
            return;
        var settings = Context.Settings;
        store.Limits = store.Limits with { MaxEntries = Math.Clamp(settings.Get(KeyLimit, 200), 50, 500) };
        policy.Paused = settings.Get(KeyPaused, false);
        policy.ExcludedApps = settings.Get(KeyExcluded, ClipboardPolicy.DefaultExcludedApps) ?? ClipboardPolicy.DefaultExcludedApps;
        if (historyPath is not null)
        {
            if (Persist) saver?.Trigger();
            else TryDelete(historyPath);           // switching persistence off removes what is on disk
        }
        view?.RefreshState();
    }

    private async void OnClipboardChanged(ClipboardChange change)
    {
        try
        {
            if (policy.Evaluate(change) != CaptureDecision.Capture)
                return;
            var entry = await ClipboardReader.ReadAsync(Context.Time.GetUtcNow());
            // If the clipboard moved on while we were reading, a newer notification is already on its way; its own checks apply.
            if (entry is null || !started || Native.GetClipboardSequenceNumber() != change.Sequence)
                return;
            store.Add(entry);
        }
        catch (Exception e)
        {
            Context.Log.Warn("clipboard: capture failed", e);   // never log contents
        }
    }

    /// <summary>Click on an entry: copy it; optionally paste it into the app the user came from.</summary>
    internal async void Activate(ClipEntry entry)
    {
        if (!Context.IsDemo && !ClipboardReader.Write(entry, policy))
            return;
        if (Context.Settings.Get(KeyPasteOnClick, false) && Context.Consent.IsGranted(PermissionIds.InputPaste) && !Context.IsDemo)
        {
            Context.Notch.Collapse();
            await Task.Delay(60);
            Native.SendCtrlV();
        }
    }

    internal void SetPaused(bool paused) => Context.Settings.Set(KeyPaused, paused);

    private void SaveNow(bool background)
    {
        if (!Persist || historyPath is null || store is null)
            return;
        var snapshot = store.Items.Where(e => e.Kind != ClipKind.Image).ToList();
        var path = historyPath;
        void Write()
        {
            try { ClipboardPersistence.Save(path, snapshot); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Context.Log.Warn("clipboard: could not save history", e);
            }
        }
        if (background) Task.Run(Write);
        else Write();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void SeedDemo()
    {
        var now = Context.Time.GetUtcNow();
        var rows = new (ClipEntry Entry, TimeSpan Age, bool Pinned)[]
        {
            (ClipEntry.FromText("dotnet publish -c Release -r win-x64 --self-contained", now), TimeSpan.FromMinutes(1), true),
            (ClipEntry.FromText("Meeting moved to Thursday 14:30. Same room, bring the Q3 numbers and the updated roadmap draft.", now), TimeSpan.FromMinutes(6), false),
            (DemoImage(now), TimeSpan.FromMinutes(22), false),
            (ClipEntry.FromFiles([@"C:\Users\you\Documents\Invoice-2041.pdf", @"C:\Users\you\Documents\Budget.xlsx"], now), TimeSpan.FromHours(2), false),
            (ClipEntry.FromText("https://learn.microsoft.com/dotnet/desktop/wpf/", now), TimeSpan.FromHours(5), false),
            (ClipEntry.FromText("SELECT id, name FROM customers WHERE active = 1 ORDER BY name;", now), TimeSpan.FromDays(1), false),
            (ClipEntry.FromText("Remember to rotate the staging keys", now), TimeSpan.FromDays(3), false),
        };
        foreach (var (entry, age, pinned) in rows)
        {
            entry.Created = now - age;
            entry.Pinned = pinned;
        }
        store.Load(rows.Select(r => r.Entry));
    }

    private static ClipEntry DemoImage(DateTimeOffset now)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x7C, 0x9C, 0xFF), Color.FromRgb(0x9C, 0x89, 0xBF), 45), null, new Rect(0, 0, 320, 200));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), null, new Point(230, 70), 46, 46);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, new Rect(0, 140, 320, 60));
        }
        var bmp = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ClipEntry.FromImage(ms.ToArray(), 320, 200, now);
    }

    public override FrameworkElement? CreateSettingsView()
    {
        var panel = new StackPanel { MinWidth = 300 };
        var settings = Context.Settings;

        var limitLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        var limit = new Slider { Minimum = 50, Maximum = 500, TickFrequency = 50, IsSnapToTickEnabled = true, Value = Math.Clamp(settings.Get(KeyLimit, 200), 50, 500), Margin = new Thickness(0, 0, 0, 8) };
        void ShowLimit() => limitLabel.Text = $"Keep the last {(int)limit.Value} entries (pinned ones are extra)";
        ShowLimit();
        limit.ValueChanged += (_, _) => { ShowLimit(); settings.Set(KeyLimit, (int)limit.Value); };
        panel.Children.Add(limitLabel);
        panel.Children.Add(limit);

        var persist = new CheckBox { Content = "Remember across restarts (encrypted for your Windows account)", IsChecked = Persist, Margin = new Thickness(0, 0, 0, 8) };
        persist.Click += (_, _) => settings.Set(KeyPersist, persist.IsChecked == true);
        panel.Children.Add(persist);

        var paste = new CheckBox { Content = "Paste on click (sends Ctrl+V)", IsChecked = settings.Get(KeyPasteOnClick, false) && Context.Consent.IsGranted(PermissionIds.InputPaste), Margin = new Thickness(0, 0, 0, 8) };
        paste.Click += (_, _) =>
        {
            var on = paste.IsChecked == true;
            if (on) Context.Consent.Grant(PermissionIds.InputPaste);
            else Context.Consent.Revoke(PermissionIds.InputPaste);
            settings.Set(KeyPasteOnClick, on);
        };
        panel.Children.Add(paste);

        panel.Children.Add(new TextBlock { Text = "Never record copies from these apps (process names, comma separated)", Margin = new Thickness(0, 0, 0, 4) });
        var apps = new TextBox { Text = string.Join(", ", settings.Get(KeyExcluded, ClipboardPolicy.DefaultExcludedApps) ?? ClipboardPolicy.DefaultExcludedApps), Margin = new Thickness(0, 0, 0, 8) };
        apps.LostFocus += (_, _) => settings.Set(KeyExcluded, apps.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        panel.Children.Add(apps);

        var clear = new Button { Content = "Clear history", HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => store?.Clear(keepPinned: false);
        panel.Children.Add(clear);
        return panel;
    }
}
