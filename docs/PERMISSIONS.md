# Permissions

Windows does not sandbox desktop apps, so these are promises the app enforces on itself: **nothing is read or done until you grant the permission in the app, and the code for that feature is not even started until then.** Revoke any permission in Settings and the feature stops immediately. Where Windows has a real switch (Startup apps, microphone privacy) Settings links to it.

| Permission id | In plain words | Touches | Leaves the PC? | Used by |
|---|---|---|---|---|
| `agents.logs` | Read how many tokens your coding agents used | Session logs of Claude, Codex, Copilot, OpenCode: counts, model, folder name, times. Never prompts or replies. | No | AI Agents |
| `claude.credentials` | Use your Claude sign-in to ask Anthropic how much plan quota is left | Reads the access token in `.claude\.credentials.json` each check; never writes it. When it expires, runs `claude auth status` so the CLI renews it | Yes: token sent only to `api.anthropic.com` | AI Agents (optional) |
| `claude.app-history` | Read the quota history the Claude desktop app saves | `%APPDATA%\Claude\plan-usage-history.json` | No | AI Agents (optional) |
| `codex.cli` | Run your Codex CLI in the background to ask for plan limits | Starts a hidden `codex app-server`, which talks to OpenAI with Codex's own login; ended after each check | Codex itself contacts OpenAI | AI Agents (optional) |
| `network.prices` | Download the latest model price list once a day | Nothing of yours is sent | Yes: `raw.githubusercontent.com` (GET only) | AI Agents (optional) |
| `media.session` | See what is playing and press play/pause/next for you | Title, artist, album art, position from Windows' media controls | No | Now Playing |
| `audio.sessions` | See which apps make sound and change their volume | App names, volumes, peak level. No audio is recorded. | No | Volume Mixer, Now Playing (equalizer) |
| `clipboard.read` | Keep a history of what you copy | Clipboard text, file paths, images; stored locally (encrypted) | No | Clipboard |
| `input.paste` | Press Ctrl+V for you when you pick a history entry | Sends one synthetic keystroke to the focused app | No | Clipboard (optional) |
| `files.shelf` | Remember the files you drop on the notch | Paths of dropped files | No | Shelf |
| `files.downloads` | Watch your Downloads folder for downloads in progress | File names and sizes in Downloads (top level) | No | Downloads |
| `power.keep-awake` | Stop the PC or display from sleeping while on | Power request to Windows | No | Keep Awake |
| `system.stats` | Read CPU, memory, network, disk and battery numbers | Windows performance counters | No | System |
| `system.toggles` | Change the Windows settings you click | Theme registry values, desktop-icon visibility, microphone mute, lock, Recycle Bin | No | Quick Toggles |
| `printers.queue` | See your printers and what they are printing | Printer names, drivers, ports and status; print jobs in the queue: document name (can be hidden in Settings), page counts, state. Never the contents of a document, never the owner. | No | Printers |
| `printers.maintenance` | Send nozzle check, head clean and power clean to your Epson printer, and read its ink pad counter | Maintenance commands over the printer's USB connection, only when you press a button, and reads of the waste ink pad counter (when the section opens, at most every 10 minutes, never while printing). Head and power cleans use ink; power clean uses a lot and fills the waste ink pad. | No | Printers (optional) |
| `printers.padreset` | Reset the printer's waste ink pad counter | Writes the counter back to zero inside the printer. Only for use after the ink pads were replaced or cleaned; resetting without that can let waste ink overflow. Kept separate from `printers.maintenance` on purpose. | No | Printers (optional) |
| `app.startup` | Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Lintel` | No | App |
| `network.updates` | Check GitHub for a new version once a day | Nothing of yours is sent | Yes: `api.github.com`, `github.com`, and `objects.githubusercontent.com` (GitHub's release-download redirect), GET only | App |

## What each module needs

| Module | Required | Optional |
|---|---|---|
| AI Agents | `agents.logs` | `claude.credentials`, `claude.app-history`, `codex.cli`, `network.prices` |
| Now Playing | `media.session` | `audio.sessions` |
| Volume Mixer | `audio.sessions` | |
| Timer, Scratchpad | none | |
| Clipboard | `clipboard.read` | `input.paste` |
| System | `system.stats` | |
| Shelf | `files.shelf` | |
| Downloads | `files.downloads` | |
| Keep Awake | `power.keep-awake` | |
| Quick Toggles | `system.toggles` | |
| Printers | `printers.queue` | `printers.maintenance`, `printers.padreset` |

## Installer permissions

The installer (`install.ps1`) runs as your user (no administrator rights, no UAC prompt). It writes only the files and registry keys listed in [PRIVACY.md](PRIVACY.md#files-and-registry-entries-written).
