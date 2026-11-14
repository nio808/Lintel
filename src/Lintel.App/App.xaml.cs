// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Lintel.App;

public partial class App : Application
{
    private Composition? composition;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        composition = new Composition(this);
        composition.Start(e.Args);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        composition?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        composition?.Dispose();
        base.OnExit(e);
    }
}
