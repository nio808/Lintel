// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using Lintel.Core.Modules;
using Lintel.Core.Notch;

namespace Lintel.Stash;

/// <summary>Shared plumbing: keeps the context, default empty members, and the "Delete data" behaviour.</summary>
internal abstract class StashModule : INotchModule
{
    protected IModuleContext Context { get; private set; } = null!;

    public abstract ModuleManifest Manifest { get; }

    public virtual IReadOnlyList<NotchSection> Sections => [];

    public virtual IReadOnlyList<CompactActivity> Activities => [];

    public void Start(IModuleContext context)
    {
        Context = context;
        OnStart();
    }

    public abstract void Stop();

    protected abstract void OnStart();

    public virtual FrameworkElement? CreateSettingsView() => null;

    public void DeleteData()
    {
        Context?.Settings.RemovePrefix(Manifest.Id + ".");
        if (Context is null || Context.IsDemo)
            return;
        try
        {
            var dir = Context.GetDataDirectory(Manifest.Id);
            foreach (var file in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(file)) Directory.Delete(file, true);
                else File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Context.Log.Warn($"{Manifest.Id}: could not delete all data", e);
        }
    }

    /// <summary>Runs on the UI thread (inline when already there).</summary>
    protected void OnUi(Action action)
    {
        if (Context.Ui.CheckAccess()) action();
        else Context.Ui.BeginInvoke(action);
    }
}
