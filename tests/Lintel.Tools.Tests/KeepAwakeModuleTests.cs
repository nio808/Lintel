// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;
using Lintel.Tools.KeepAwake;

namespace Lintel.Tools.Tests;

/// <summary>Drives the real module against a fake execution-state backend: the system is never asked to stay awake.</summary>
public sealed class KeepAwakeModuleTests : IDisposable
{
    private sealed class FakeBackend : IAwakeBackend
    {
        public List<string> Calls { get; } = [];
        public void Set(bool keepDisplay) => Calls.Add(keepDisplay ? "set:display" : "set:system");
        public void Clear() => Calls.Add("clear");
        public void Dispose() => Calls.Add("dispose");
    }

    private sealed class NoHotkeys : IHotkeyService
    {
        public List<string> Registered { get; } = [];
        public IReadOnlyList<HotkeyBinding> Bindings => [];
        public event EventHandler? Changed { add { } remove { } }
        public bool Register(string id, string defaultGesture, string description, Action callback) { Registered.Add(id); return true; }
        public void Unregister(string id) => Registered.Remove(id);
        public bool Rebind(string id, string gesture) => true;
        public void Dispose() { }
    }

    private readonly string dir = Path.Combine(Path.GetTempPath(), "Lintel.Tools.Tests", Guid.NewGuid().ToString("N"));
    private readonly JsonSettingsStore settings;
    private readonly FakeTime time = new();
    private readonly NullNotchShell shell = new();
    private readonly NoHotkeys hotkeys = new();
    private readonly FakeBackend backend = new();
    private readonly HttpClient http = new();
    private PowerReading power = new(80, false);
    private bool appRunning = true;

    public KeepAwakeModuleTests()
    {
        Directory.CreateDirectory(dir);
        settings = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
    }

    public void Dispose()
    {
        settings.Dispose();
        http.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private KeepAwakeModule Started()
    {
        var module = new KeepAwakeModule(() => backend, () => power, _ => appRunning);
        var ctx = new ModuleContext(new AppInfo("1.0.0", "o", "r", "x.exe"), false, settings, new ConsentService(settings), shell, hotkeys,
            new NullLog(), Dispatcher.CurrentDispatcher, http, time, dir);
        module.Start(ctx);
        return module;
    }

    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message, Exception? error = null) { }
        public void Error(string message, Exception? error = null) { }
    }

    [Fact]
    public void TimedSessionSetsAndClearsExecutionState()
    {
        var module = Started();
        Assert.Contains("keepawake.toggle", hotkeys.Registered);
        module.StartTimed(15);
        Assert.Equal(["set:display"], backend.Calls);
        Assert.Equal(TimeSpan.FromMinutes(15), module.Remaining);
        module.End(null);
        Assert.Equal(["set:display", "clear"], backend.Calls);
        Assert.Null(module.Session);
        Assert.Empty(shell.Notices);   // turning it off yourself is quiet
        module.Stop();
        Assert.Equal("dispose", backend.Calls[^1]);
        Assert.Empty(hotkeys.Registered);
    }

    [Fact]
    public void DisplayOptionOffKeepsOnlyTheSystemAwake()
    {
        settings.Set(KeepAwakeModule.DisplayKey, false);
        var module = Started();
        module.StartIndefinite();
        Assert.Equal(["set:system"], backend.Calls);
        module.Stop();
        Assert.Contains("clear", backend.Calls);
    }

    [Fact]
    public void StopWhileActiveReleasesTheRequest()
    {
        var module = Started();
        module.StartIndefinite();
        module.Stop();
        Assert.Equal(["set:display", "clear", "dispose"], backend.Calls);
    }

    [Fact]
    public void RuleEndShowsAQuietInfoNotice()
    {
        var module = Started();
        module.StartTimed(15);
        module.End(KeepAwakeEnd.Expired);
        var notice = Assert.Single(shell.Notices);
        Assert.Equal(NoticeTone.Info, notice.Tone);
        module.Stop();
    }

    [Fact]
    public void ToggleStartsIndefiniteThenStops()
    {
        var module = Started();
        module.Toggle();
        Assert.Equal(KeepAwakeKind.Indefinite, module.Session?.Kind);
        module.Toggle();
        Assert.Null(module.Session);
        module.Stop();
    }
}
