# Security policy

## Reporting a vulnerability

Please report privately through GitHub: **Security > Report a vulnerability** on the repository (`https://github.com/nio808/Lintel/security/advisories/new`). Do not open a public issue for security problems. Expect an acknowledgement within a few days.

Include the version, what you did, and what you expected. Never attach real tokens, session logs or personal data.

## Supported versions

Only the latest release receives fixes.

## Scope and design notes

- No account, no telemetry. Network use is listed in [docs/PRIVACY.md](docs/PRIVACY.md).
- Claude and Codex credentials are read on demand, only after you grant the permission, never written or logged, and sent only to the provider's own host. An expired Claude token is renewed by the Claude CLI itself (`claude auth status`), never by Lintel.
- Downloads (installer, update, price list) are over HTTPS from `github.com`. `install.ps1` verifies the zip's SHA-256 against the release's `SHA256SUMS.txt` and aborts on a mismatch; the in-app updater verifies `install.ps1` the same way. v0.1 binaries are **not code-signed**, so that checksum is your integrity check.
- The installer is per-user and never needs administrator rights. It only ever stops a `Lintel.exe` that runs from its own install folder.
