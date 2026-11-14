// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Services;
using Lintel.Core.Settings;

namespace Lintel.Notch.Tests;

/// <summary>The real shell with stand-in modules, rendered off-screen (no window is shown).</summary>
public sealed class ShellTests
{
    [Fact]
    public void CaptureWritesEveryStateAndSectionAndDrivesLifecycle() => Sta.Run(async () =>
    {
        // NOTCH_SHOTS=<dir> keeps the PNGs (design screenshots); otherwise a temp folder is used and removed.
        var keep = Environment.GetEnvironmentVariable("NOTCH_SHOTS");
        var root = Path.Combine(Path.GetTempPath(), "Lintel.Notch.Tests", Guid.NewGuid().ToString("N"));
        var shots = keep ?? Path.Combine(root, "shots");
        try
        {
            using var host = new TestHost(root);
            var shell = host.Shell;

            await shell.CaptureAsync(shots);

            Assert.True(File.Exists(Path.Combine(shots, "collapsed.png")));
            Assert.True(File.Exists(Path.Combine(shots, "sliver.png")));
            // "tools" comes from two modules and is shown once.
            string[] expected = ["agents", "music", "timer", "tools"];
            Assert.Equal(expected, Directory.GetFiles(shots, "section-*.png").Select(f => Path.GetFileNameWithoutExtension(f)[8..]).Order());
            Assert.All(Directory.GetFiles(shots, "*.png"), f => Assert.True(new FileInfo(f).Length > 200, f));
            // Every section was told it became visible and then hidden.
            Assert.Equal(LifecycleView.TotalShown, LifecycleView.TotalHidden);
            Assert.True(LifecycleView.TotalShown >= expected.Length);
            Assert.Equal(NotchState.Hidden, shell.State);   // never shown
            Assert.Null(shell.VisibleSectionId);
            Assert.Empty(host.Log.Errors);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public void HostRegistersNothingUntilShownAndCleansUp() => Sta.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Lintel.Notch.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new TestHost(root);
            Assert.Empty(host.Hotkeys.Bindings);
            host.Shell.ShowNotice(new NotchNotice { Id = "n", Title = "Hidden", BalloonIfHidden = true });
            Assert.Equal(["Hidden"], host.Balloons);   // not shown yet → balloon only
            host.Shell.Expand("agents");
            Assert.Equal(NotchState.Hidden, host.Shell.State);
            host.Shell.Shutdown();
            host.Shell.Shutdown();   // idempotent
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    });

    private sealed class TestHost : IDisposable
    {
        private readonly JsonSettingsStore settings;
        private readonly HttpClient http;
        private readonly ModuleManager modules;

        public TestHost(string root)
        {
            Directory.CreateDirectory(root);
            settings = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            var consent = new ConsentService(settings);
            foreach (var permission in PermissionCatalog.All)
                consent.Grant(permission.Id);
            var args = AppArgs.Parse([]);
            Shell = NotchBootstrap.Create(new NotchDependencies(settings, Log, Hotkeys, args, () => { }, () => { }, (title, _) => Balloons.Add(title)));
            var app = AppInfo.FromAssembly(typeof(ShellTests).Assembly);
            http = AppHttp.Create(app);
            var context = new ModuleContext(app, true, settings, consent, Shell, Hotkeys, Log, Dispatcher.CurrentDispatcher, http, TimeProvider.System, root);
            var catalog = DemoModules.Catalog();
            modules = new ModuleManager(catalog, context);
            foreach (var descriptor in catalog)
                settings.Set(SettingKeys.ModuleInstalled(descriptor.Manifest.Id), true);
            Shell.Attach(modules);
            modules.StartInstalled();
            foreach (var activity in modules.Running.SelectMany(m => m.Activities))
                activity.IsActive = true;
        }

        public INotchShell Shell { get; }
        public NullLog Log { get; } = new();
        public ISettingsStore Settings => settings;
        public FakeHotkeys Hotkeys { get; } = new();
        public List<string> Balloons { get; } = [];

        public void Dispose()
        {
            modules.StopAll();
            Shell.Shutdown();
            http.Dispose();
            settings.Dispose();
        }
    }

    private sealed class NullLog : IAppLog
    {
        public List<string> Errors { get; } = [];

        public void Info(string message) { }

        public void Warn(string message, Exception? error = null) { }

        public void Error(string message, Exception? error = null) => Errors.Add(message + " " + error);
    }

    internal sealed class FakeHotkeys : IHotkeyService
    {
        private readonly List<HotkeyBinding> bindings = [];

        public IReadOnlyList<HotkeyBinding> Bindings => bindings;

        public event EventHandler? Changed { add { } remove { } }

        public bool Register(string id, string defaultGesture, string description, Action callback)
        {
            bindings.Add(new HotkeyBinding(id, description, defaultGesture, true));
            return true;
        }

        public void Unregister(string id) => bindings.RemoveAll(b => b.Id == id);

        public bool Rebind(string id, string gesture) => true;

        public void Dispose() { }
    }
}

/// <summary>Runs a test body on an STA thread with a pumping WPF dispatcher.</summary>
internal static class Sta
{
    public static void Run(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
