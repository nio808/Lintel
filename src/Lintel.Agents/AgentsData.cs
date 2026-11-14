// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Agents;
using Lintel.Core.Modules;

namespace Lintel.Agents;

/// <summary>Entry point of the AI agents data service.</summary>
public static class AgentsData
{
    /// <summary>Returns the demo service when context.IsDemo (canned data, touches no accounts).</summary>
    public static IAgentUsageService CreateService(IModuleContext context) =>
        context.IsDemo ? new DemoAgentUsageService(context) : new AgentUsageService(context);
}
