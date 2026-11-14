// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lintel.Tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class ListLog : IAppLog
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("I " + message);
    public void Warn(string message, Exception? error = null) => Lines.Add("W " + message);
    public void Error(string message, Exception? error = null) => Lines.Add("E " + message);
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public IReadOnlyList<HotkeyBinding> Bindings => [];
    public event EventHandler? Changed { add { } remove { } }
    public bool Register(string id, string defaultGesture, string description, Action callback) => true;
    public void Unregister(string id) { }
    public bool Rebind(string id, string gesture) => true;
    public void Dispose() { }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeModule(ModuleManifest manifest, Action<IModuleContext>? onStart = null) : INotchModule
{
    public ModuleManifest Manifest { get; } = manifest;
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int Deletes { get; private set; }
    public IReadOnlyList<NotchSection> Sections => [];
    public IReadOnlyList<CompactActivity> Activities => [];

    public void Start(IModuleContext context)
    {
        Starts++;
        onStart?.Invoke(context);
    }

    public void Stop() => Stops++;
    public FrameworkElement? CreateSettingsView() => null;
    public void DeleteData() => Deletes++;
}

/// <summary>A settings store + consent + context rooted in a temp folder.</summary>
internal sealed class Harness : IDisposable
{
    public TempDir Dir { get; } = new();
    public JsonSettingsStore Settings { get; }
    public ConsentService Consent { get; }
    public ModuleContext Context { get; }
    public ListLog Log { get; } = new();
    private readonly HttpClient http = new();

    public Harness()
    {
        Settings = new JsonSettingsStore(Dir.File("settings.json"));
        Consent = new ConsentService(Settings);
        Context = new ModuleContext(new AppInfo("1.0.0", "OWNER", "Lintel", "test.exe"), false, Settings, Consent,
            new NullNotchShell(), new FakeHotkeys(), Log, Dispatcher.CurrentDispatcher, http, TimeProvider.System, Dir.Path);
    }

    public void Dispose()
    {
        Settings.Dispose();
        http.Dispose();
        Dir.Dispose();
    }
}
