# Contributing

Thanks for helping. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains how the pieces fit together.

## Build and test

```powershell
./build.ps1 -Test                                  # build + unit tests (installs the .NET 10 SDK into .dotnet if missing)
./build.ps1 -All                                   # + publish; Lintel-win-x64.zip, SHA256SUMS.txt, install.ps1 in artifacts\release
powershell -File tests\installer\Install.Tests.ps1 # installer-script checks (fake zip in a temp folder, cleans up after itself)
```

Windows 10/11 x64 only (WPF, .NET 10). Releases: push a `v*` tag; the release workflow builds and uploads the three assets.

## Layout

`src/Lintel.Core` contracts, settings, permissions, module manager, UI kit · `Notch` the window · `Agents` / `Agents.UI` AI usage data and UI · `Media`, `Tools`, `Stash` feature modules · `App` the exe (composition root, tray, settings) · `tools/Lintel.Preview` dev harness · `tests/*` xUnit · `build.ps1`, `install.ps1`, `.github/` packaging.

## Rules

1. Contracts in `Lintel.Core` (`Modules`, `Notch`, `Agents`, `Settings`, `Permissions`) are stable. Need a change? Open an issue or PR that explains it; update every dependant in the same PR.
2. Use only the packages in `Directory.Packages.props`; adding one needs a reason (and a line in NOTICE.md).
3. Privacy is a feature: nothing reads or does anything before its permission is granted in-app, no analytics, no secrets, real log lines, e-mail addresses or tokens in code, tests, fixtures, logs or screenshots. Fixtures are hand-written.
4. Nullable warnings are errors. Keep idle CPU near zero: no polling faster than 1 s while collapsed, no work for hidden sections.
5. Code adapted from vorssaint-utils keeps its "Portions adapted from vorssaint-utils" header and stays credited in NOTICE.md.
6. Add tests for logic (parsers, stores, state machines).

## Adding a module

1. Pick an id and write its `ModuleManifest`: name, order, required/optional permissions, sections, activities.
2. Implement `IModule` (`Lintel.Core.Modules`) in the project that fits; do all work in `Start`, undo it in `Stop`; nothing runs while uninstalled.
3. Declare permission ids in `PermissionCatalog` text (plain words: what it touches, whether anything leaves the PC), and mirror them in `docs/PERMISSIONS.md` and `docs/PRIVACY.md`.
4. Export it from the project's catalog class (`MediaCatalog`, `ToolsCatalog`, ...) and add it to the onboarding presets if recommended.
5. Test it; check `--screenshot` output and idle CPU.

## Pull requests

Keep PRs focused, update `CHANGELOG.md`, and fill in the template. By contributing you agree to license your work under GPL-3.0-or-later.
