# Architecture

Lintel is a single self-contained WPF exe (.NET 10, x64) that lives in the notification area and draws a notch at the top centre of one display. It has no main window and never appears in the taskbar.

## Projects

| Project | Responsibility |
|---|---|
| `src/Lintel.Core` | Contracts (`INotchModule`, `IModuleContext`, `INotchShell`, `IAgentUsageService`), `ModuleManager`, permissions and `ConsentService`, `JsonSettingsStore` + migrator, `AppPaths`, `HotkeyService`, `SingleInstance`, `StartupRegistration`, `UpdateChecker`, `FileLog`, shared controls and the notch theme. No feature code. |
| `src/Lintel.Notch` | The notch: window, pure state machine, notice queue, layout metrics, spring animator, views (compact bar, expanded panel, notices), display and fullscreen tracking. |
| `src/Lintel.Agents` | AI usage data: log readers, indexer, usage store and cache, plan-limit readers, pricing, demo service. No WPF views. |
| `src/Lintel.Agents.UI` | The AI Agents module: pill activities, section, settings view. |
| `src/Lintel.Media` | Now Playing (Windows media session) and Volume Mixer (WASAPI sessions via NAudio). |
| `src/Lintel.Tools` | Timer, Keep Awake, System stats, Quick Toggles. |
| `src/Lintel.Stash` | Clipboard history, Shelf, Scratchpad, Downloads. |
| `src/Lintel.Printers` | Printers: spooler watcher (`EnumPrinters`/`EnumJobs` driven by `FindFirstPrinterChangeNotification`), job tracker and ETA, page statistics, recommendation engine, maintenance controller (two-step confirms, lockout), section, settings. `Epson\` talks to Epson inkjets (ink pad counters, maintenance commands). |
| `src/Lintel.App` | The exe: composition root, tray icon, Settings window (WPF-UI), CLI switches, update installer. |
| `tools/Lintel.Preview` | Dev harness that renders modules outside the notch. |
| `tests/*` | xUnit tests per project; `tests/installer` checks `install.ps1` and `build.ps1`. |

Feature projects depend only on `Lintel.Core`. `Lintel.App` references everything and wires it together in `Composition`.

## Startup and lifetime

`Composition.Start` parses the arguments (`--autostart`, `--settings`, `--quit`, `--demo`, `--version`, `--screenshot <dir>`, `--read-usage <file>`). A named mutex (`Local\Lintel.SingleInstance`) plus two named events make a second launch ask the running instance to open Settings, and `--quit` ask it to exit. The first instance creates the settings store, log, consent service, hotkeys, notch shell and module manager, adds the tray icon (`Shell_NotifyIcon` on a hidden `HwndSource`), starts the installed modules and shows the notch. Settings opens on first run or when the user launches the exe by hand. Exit always runs `Composition.Dispose`: stop modules, close the notch, remove the tray icon, flush settings, release the mutex.

## Modules, Features hub and consents

Each feature is an `INotchModule` described by a `ModuleManifest` (id, name, glyph, category, required and optional permission ids, order). Project catalogs (`AgentsUiCatalog`, `MediaCatalog`, `ToolsCatalog`, `StashCatalog`, `PrintersCatalog`) export `ModuleDescriptor`s; a module object is constructed only when it starts.

- **Install state** is the setting `modules.<id>.installed`. The Features page toggles it through `ModuleManager.Install/Uninstall`; uninstall can also delete the module's data folder and setting keys.
- **Consents** are settings `consent.<permissionId>`. `PermissionCatalog` holds the plain-words text shown in onboarding, the install dialog and the Permissions page. A module starts only when all its required permissions are granted (otherwise its status is `NeedsPermission`); optional permissions gate individual data sources inside the module. Revoking a permission stops the feature immediately.
- A module contributes `NotchSection`s (pages of the expanded panel), `CompactActivity`s (left/right wings of the collapsed pill, by priority) and an optional settings view. Modules receive an `IModuleContext` with settings, consent, log, UI dispatcher, a shared `HttpClient` (no redirects, no cookies, 20 s timeout), a `TimeProvider`, and a per-module data folder.
- `--demo` (and `--screenshot`) set `IModuleContext.IsDemo`: every module uses canned data and touches no files, accounts or network.

## The notch window

`NotchWindow` is a fixed-size (840 × 600 DIP) borderless, topmost WPF window placed in physical pixels at the top centre of the chosen display.

- **Composition:** a non-layered window whose frame is extended over the whole client area (`DwmExtendFrameIntoClientArea` with -1 margins, transparent WPF clear colour), so DWM composes per-pixel alpha on the GPU. Rounded corners, border, backdrop and DWM transitions are turned off. A layered (`AllowsTransparency`) fallback exists behind `--layered`.
- **Never a taskbar or Alt-Tab entry:** `WS_EX_TOOLWINDOW` without `WS_EX_APPWINDOW`, `ShowInTaskbar = false`, excluded from Aero Peek.
- **Never steals focus:** `WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE` answered with `MA_NOACTIVATE`, shown with `SW_SHOWNA`, moved with `SWP_NOACTIVATE`. Keyboard mode (global hotkey) temporarily drops `WS_EX_NOACTIVATE` and gives focus back to the previous window afterwards.
- **Hit region:** `SetWindowRgn` limits the window to the visible pill or panel, so clicks anywhere else on the canvas go to the app below.
- **Behaviour:** `NotchStateMachine` (pure, unit-tested) maps inputs (hover, dwell, grace, click, hotkey, drag, notices, fullscreen) to states (Collapsed, Notice, Expanded, Hidden) and timer effects; `NotchController` runs the timers and animates the shape with short, near-critically damped springs. A `SetWinEventHook` foreground hook hides the notch while an app is fullscreen on its display.

## AI usage data

`AgentUsageService` (real) or `DemoAgentUsageService` (`--demo`) implements `IAgentUsageService` and publishes at most one snapshot per second to the UI thread.

- **Token usage** (`agents.logs`): `LogIndexer` incrementally tails local session logs on one background worker: Claude Code (`CLAUDE_CONFIG_DIR` or `~\.claude\projects`), Codex (`CODEX_HOME` or `~\.codex`, plus any extra Codex homes the user adds; zstd-compressed logs supported), Copilot CLI (`~\.copilot\session-state`) and OpenCode (`opencode.db`, read with the Windows SQLite). Discovery every 30 s, tailing every 2 s only for recently active files; no `FileSystemWatcher`. Only counts, model, project folder name and times are kept, never prompts or replies. Offsets and hourly aggregates are cached in `data\agents\usage-cache.json`.
- **Plan limits:** `LimitsCoordinator` schedules reads (5 min per account, 60 s while the section is open) with back-off. Claude: the OAuth usage endpoint on `api.anthropic.com` with the token from `.claude\.credentials.json` (`claude.credentials`), falling back to the Claude desktop app's `plan-usage-history.json` (`claude.app-history`). Codex: a hidden `codex app-server` started from the user's own CLI (`codex.cli`), falling back to the rate-limit lines in Codex session logs.
- **Pricing:** `AgentPricing` uses the embedded `agent-prices.json`, optionally replaced once a day by a newer valid copy from this repository (`network.prices`).

## Settings and data

Everything lives under `%LOCALAPPDATA%\Lintel`: `settings.json` (`{ "schema": 1, "values": { key: json } }`, written atomically by `JsonSettingsStore`, migrated by `SettingsMigrator`), `data\<moduleId>\` per module, and `logs\app.log` (rolls at 1 MB; never contains tokens, e-mail addresses, clipboard or log-line contents). "Start with Windows" is the `HKCU\...\Run` value `Lintel` and honours Task Manager's StartupApproved switch.

## Updates

With `network.updates` granted, `UpdateInstaller` asks `api.github.com/repos/nio808/Lintel/releases/latest` 30 s after start and then at most once every 24 h (prereleases and drafts are ignored; the user can skip a version). "Install" downloads the release's `install.ps1` and `SHA256SUMS.txt` from `github.com`, verifies the script's SHA-256, starts it hidden with `-Version <tag>` and quits. The script downloads `Lintel-win-x64.zip`, verifies its SHA-256, stops the app with `--quit`, replaces the files in place and relaunches.

## Build and release

`build.ps1` uses a .NET 10 SDK from `PATH`, else `.dotnet\` in the repo, else installs one there. `-Test` builds and runs the tests, `-Publish` produces a self-contained single-file ReadyToRun `Lintel.exe` in `artifacts\publish\win-x64`, and `-Zip` (or `-All`) writes the release assets to `artifacts\release`: `Lintel-win-x64.zip` (exe, LICENSE, README.txt), `install.ps1` and `SHA256SUMS.txt`. CI (`.github/workflows/ci.yml`) builds, tests and runs the installer checks on Windows PowerShell 5.1 and PowerShell 7. Pushing a `v*` tag runs `release.yml`, which builds with that version and uploads the three assets to a GitHub release.

`install.ps1` (per-user, no admin) installs to `%LOCALAPPDATA%\Programs\Lintel`, writes `uninstall.ps1`, a Start-menu shortcut and an Apps entry, and optionally the Run value. `-Uninstall [-Purge]` removes all of it.
