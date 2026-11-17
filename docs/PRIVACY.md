# Privacy

- No account, no analytics, no crash upload, no tracking.
- The only network use is in the table below; each item can be turned off.
- Your data stays on your PC in `%LOCALAPPDATA%\Lintel\`.
- The app never reads or stores prompts, replies, log-line contents, clipboard contents in logs, or e-mail addresses.

## Hosts the app contacts

All requests are plain HTTPS GETs with a generic User-Agent unless noted. Nothing about you is sent.

| Host | When | Why | Switch |
|---|---|---|---|
| `api.github.com`, `github.com`, `objects.githubusercontent.com` | Once a day (default on) | Is there a newer release? "Install update" downloads the release's `install.ps1` from `github.com` (GitHub redirects release downloads to `objects.githubusercontent.com`), which then downloads the zip and verifies its SHA-256 | `network.updates` |
| `raw.githubusercontent.com` | Once a day, only with `network.prices` | Newer model price list (`agent-prices.json`) | `network.prices` |
| `api.anthropic.com` | Each quota check, only with `claude.credentials` | Your Claude plan quota. Your Claude access token is sent here and nowhere else; it is never stored or logged by Lintel | `claude.credentials` |
| OpenAI (by the Codex CLI, not by Lintel) | Each check, only with `codex.cli` | Codex's own login fetches your plan limits; the hidden process is ended afterwards | `codex.cli` |

The installer script `install.ps1` contacts `github.com` (and `objects.githubusercontent.com`, where GitHub redirects release downloads) once, only when you run it. It does not call the GitHub API.

## Files and registry entries written

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\Lintel\settings.json` | Your settings (schema + key/value pairs) |
| `%LOCALAPPDATA%\Lintel\data\<module>\...` | Module data: AI usage cache and price list (`agents`), clipboard history (encrypted, `clipboard`), shelf item paths (`shelf`), scratchpad `.txt` notes (`scratchpad`), timer state (`timer`), per-printer page counts and cleaning history (`printers.stats` in `settings.json`; erase it with Settings > Printers > Reset statistics) |
| `%LOCALAPPDATA%\Lintel\logs\app.log`, `app.1.log` | Diagnostics, 1 MB each. No clipboard contents, tokens, log lines or e-mail addresses |
| `%LOCALAPPDATA%\Programs\Lintel\` | The installed app and `uninstall.ps1` (installer only) |
| Start menu `Lintel` shortcut | Installer |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lintel` | Uninstall entry (Settings > Apps) |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Lintel` | Only if you enable "Start with Windows" (in the app, or `-Startup` when installing) |
| `%TEMP%\Lintel-<id>\`, `%TEMP%\Lintel-update\` | Downloaded zip / `install.ps1` during install or update; removed afterwards (the update folder on the next update) |

Printers: the spooler is only read (printer list, job names and page counts; never the owner or the contents of a document). Document names are shown in the notch and in the "Printed" notice unless you hide them; they are not stored or logged. Maintenance commands and the ink pad counter go to the printer over USB, nothing leaves the PC.

Files of other programs (Claude, Codex, Copilot, OpenCode logs and credentials) are only read, and only after you grant the matching permission. They are never modified.

## Removing your data

Uninstall keeps `%LOCALAPPDATA%\Lintel` unless you pass `-Purge` to `install.ps1 -Uninstall`. You can also delete the folder yourself at any time.
