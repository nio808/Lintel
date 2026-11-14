// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Lintel.Core.Agents;
using Lintel.Core.Modules;
using Lintel.Core.Settings;

namespace Lintel.Agents.UI;

/// <summary>Entry point of the AI agents module.</summary>
public static class AgentsUiCatalog
{
    /// <summary>
    /// One descriptor for <see cref="AgentsModule"/>. A <c>--design[=full|low|blocked|stale|accounts|empty|loading]</c> switch on the
    /// command line swaps the service for hand-written fake data (UI development and screenshots); with it, <c>--set=key=json</c>
    /// seeds a setting first (e.g. <c>--set=agents.pill.display="used"</c>).
    /// </summary>
    public static IReadOnlyList<ModuleDescriptor> Modules(Func<IModuleContext, IAgentUsageService> serviceFactory)
    {
        if (DesignScenario() is { } scenario)
        {
            AgentDisplay.SelfCheck();
            serviceFactory = context =>
            {
                SeedSettings(context.Settings);
                return new DesignAgentService(scenario, context.Time, context.Ui);
            };
        }
        return [new ModuleDescriptor(AgentsModule.Info, () => new AgentsModule(serviceFactory))];
    }

    private static string? DesignScenario()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals("--design", StringComparison.OrdinalIgnoreCase))
                return "full";
            if (arg.StartsWith("--design=", StringComparison.OrdinalIgnoreCase))
                return arg["--design=".Length..].ToLowerInvariant();
        }
        return null;
    }

    private static void SeedSettings(ISettingsStore settings)
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith("--set=", StringComparison.OrdinalIgnoreCase) || arg.IndexOf('=', 6) is not (> 6 and var eq))
                continue;
            var raw = arg[(eq + 1)..];
            try
            {
                settings.Set(arg[6..eq], JsonDocument.Parse(raw).RootElement.Clone());
            }
            catch (JsonException)
            {
                settings.Set(arg[6..eq], raw);
            }
        }
    }
}
