# Troubleshooting

Logs: `%LOCALAPPDATA%\Lintel\logs\app.log` (and `app.1.log`). They contain no tokens or clipboard contents, so they are safe to attach to a bug report. The installer prints its errors in the terminal.

## SmartScreen says "Windows protected your PC"

v0.1 builds are not code-signed, so Windows has no reputation for the file. This only appears if you download the zip in a browser and run `Lintel.exe` from it: choose **More info > Run anyway**. To be sure you got the real file, compare its hash with the release's `SHA256SUMS.txt`:

```powershell
(Get-FileHash .\Lintel-win-x64.zip -Algorithm SHA256).Hash
```

The one-liner (`install.ps1`) checks this for you and is not blocked by SmartScreen because it downloads the file itself.

## `irm ... | iex` fails

- "running scripts is disabled": the one-liner runs as text, not a file, so this only appears with `-File`. Use `powershell -ExecutionPolicy Bypass -File install.ps1`.
- TLS / connection errors on old Windows PowerShell: update Windows; the script already enables TLS 1.2.
- "SHA-256 mismatch": the download was corrupted or altered; nothing was installed. Run it again; if it repeats, open an issue.

## The notch is not visible

- It hides on purpose over **fullscreen** apps and games (F11 video, borderless games). A maximised normal window does not hide it.
- Check the tray icon exists (it may be in the overflow area); right-click > **Show notch**. Left-click toggles it too.
- It sits at the top centre of the main display, or the display under the pointer (Settings > General > Display). After a monitor/DPI/taskbar change it re-centres; if not, quit from the tray and start again.
- Graphics glitches (flicker, black box) on some drivers/VMs: Settings > General > **Render mode**, switch to the compatibility mode and restart.

## Codex limits say "not found"

Lintel runs your own `codex` CLI (permission `codex.cli`). Make sure `codex --version` works in a normal terminal and that you are signed in (`codex login`). If Codex is installed in a non-standard place, add its folder to your PATH and restart Lintel.

## Claude limits say the sign-in expired

Quota is read with your Claude sign-in (permission `claude.credentials`). Run `claude auth login` in a terminal; the next check (a minute or so) picks the new token up. The Claude desktop app history (`claude.app-history`) is a fallback.

## A hotkey does nothing

Another program already owns that combination. Settings > Shortcuts shows a conflict; pick another gesture. The default keyboard-mode hotkey is `Ctrl+Alt+N`.

## A module says "Needs permission"

Open Settings > Features, select it and grant the permission, or reinstall the module. Revoking a permission stops the feature right away by design.

## Start with Windows is on but the app does not start

Settings > Apps > Startup (Windows) may have switched it off; the Run value is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Lintel`. Toggle it in Lintel Settings to rewrite it.

## Uninstall

Settings > Apps > Lintel, or `install.ps1 -Uninstall [-Purge]`. If the entry is missing but files remain, delete `%LOCALAPPDATA%\Programs\Lintel` and the `Lintel` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Reporting a bug

Open an issue with: Lintel version (Settings > About), Windows version, what you expected, what happened, and the last lines of `app.log`. No real tokens or personal data, please.
