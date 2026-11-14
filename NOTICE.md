# Notices

Lintel is free software, licensed under the **GNU General Public License v3.0 or later** (see `LICENSE`).

## Credits

### vorssaint-utils (GPL-3.0-or-later)
Lintel borrows feature ideas from the macOS app **vorssaint-utils** and ports parts of its logic to C#:

- the AI-agent session-log parsing and usage aggregation (`AgentLogParser`, the per-harness readers, `AgentUsageSummary`, `AgentClaudeAppUsage`, `AgentOpenCodeReader`), and the model-pricing logic (`AgentPricing`);
- the seed price list `agent-prices.json` (embedded in `Lintel.Agents`), copied from vorssaint-utils' `Resources/agent-prices.json`.

Files adapted from it carry a header naming the original. vorssaint-utils is GPL-3.0-or-later, the same terms as this project. Its name, logo and artwork are **not** used here (see its `TRADEMARKS.md`); Lintel has its own name, icon and design.

## Third-party components

| Component | Licence | Used for |
|---|---|---|
| .NET runtime and WPF (Microsoft) | MIT | Bundled self-contained in `Lintel.exe` |
| WPF-UI | MIT | Settings window controls |
| CommunityToolkit.Mvvm | MIT | MVVM helpers |
| NAudio (NAudio.Wasapi) | MIT | Volume mixer / audio sessions |
| ZstdSharp.Port | MIT | Reading zstd-compressed agent logs |
| xUnit, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | Tests only |

The full licence text of each NuGet package is in its package on nuget.org; version numbers are in `Directory.Packages.props`.
