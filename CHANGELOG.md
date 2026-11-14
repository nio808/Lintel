# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow [SemVer](https://semver.org/).

## [Unreleased]

## [0.1.0]

First public release.

### Added
- The notch, with modules: AI Agents, Now Playing, Volume Mixer, Timer, Keep Awake, System, Quick Toggles, Clipboard, Shelf, Scratchpad, Downloads.
- One-line PowerShell installer (`irm .../install.ps1 | iex`): per-user, no administrator rights, no .NET needed, SHA-256 verified, in-place upgrades, `-Uninstall`.
- In-app update check and one-click update, verified against the release's `SHA256SUMS.txt`.
- Release assets `Lintel-win-x64.zip`, `SHA256SUMS.txt` and `install.ps1`; GitHub Actions CI and release workflow.
