# Security Policy

## Reporting a vulnerability

**Please do not report security issues via public GitHub issues.**

Report privately via [GitHub Security Advisories](https://github.com/VortexUK/EQ2Parser/security/advisories/new) on this repo. That opens a private channel to coordinate a fix before public disclosure.

Expect an initial response within 7 days. Confirmed issues affecting the upload token, the update channel, or execution of server-supplied content are prioritised.

## What this application is

A native Windows desktop app (.NET/WPF) that tails EverQuest II log files, parses combat locally, and optionally uploads finished encounters to [EQ2Lexicon](https://github.com/VortexUK/EQ2Lexicon). It:

- Parses **untrusted text** continuously: the EQ2 log file contains chat from arbitrary players.
- Executes **server-supplied content**: the Lexicon trigger pack ships regexes (run against every log line) and sound specs. Defences: a 100 ms regex match timeout (ReDoS), a log-line length cap, and refusal of UNC/network sound paths (SMB credential-leak vector).
- Holds one credential: the user's **EQ2Lexicon API token**, encrypted at rest with DPAPI (CurrentUser) — only ciphertext reaches `settings.json`.
- Signs uploads with **HMAC-SHA256** over the payload and refuses non-HTTPS upload URLs (loopback dev exception).
- Auto-updates via **Velopack** from this repo's [GitHub Releases](https://github.com/VortexUK/EQ2Parser/releases). Releases carry GitHub build-provenance attestation and SHA256SUMS; binaries are not yet Authenticode-signed (see [docs/code-signing.md](docs/code-signing.md)).

## Assets worth protecting

| Asset | Where | Risk if exposed |
|---|---|---|
| Lexicon API token | DPAPI blob in `%LOCALAPPDATA%\EQ2Parser\settings.json` | Upload/delete parses as the victim |
| Update channel | GitHub Releases (public repo) | Malicious update = code execution on every install |
| Trigger/sound execution path | Lexicon pack + ACT XML imports | ReDoS freeze, SMB auth leak, hostile file playback |
| Local archive | `history.db`, logs | Player/guild activity data (low sensitivity) |

## In scope

- API token recovery from disk **without** the victim's Windows session (DPAPI misuse, plaintext leak paths, token in logs/crash reports)
- HMAC or HTTPS-guard bypass in the upload client
- Escapes from the trigger sandbox assumptions: regex timeout bypass, sound-path checks bypassed (UNC/device/URL), pack content reaching the filesystem or network in unintended ways
- Update-channel integrity issues (downgrade, package substitution within our control)
- Path traversal via crafted log paths, ACT XML imports, or pack fields
- Crashes exploitable beyond denial of service, triggered by hostile log lines (remember: any player can write to the victim's log via /tell)

## Out of scope

- Server-side vulnerabilities (report to [EQ2Lexicon](https://github.com/VortexUK/EQ2Lexicon/security/advisories/new))
- Vulnerabilities in upstream components (Windows, .NET, NAudio, Velopack itself, GitHub)
- Anything requiring an already-compromised machine or the victim's own Windows session (DPAPI's boundary)
- The absence of Authenticode signing — a known, documented gap pending [docs/code-signing.md](docs/code-signing.md)
- Denial of service against the app by the user's own files

## Good-practice notes for contributors

- Every regex that can be server-supplied or user-imported gets an explicit `MatchTimeout` (see `Trigger.MatchTimeout`); never construct one without it.
- Sound/file paths from triggers go through the non-local-path refusal (`AlertAudioService.IsNonLocalPath`); don't add playback paths that bypass it.
- The token is read from the `PasswordBox` and cleared in the command — never bound, never logged. Keep it out of every log/crash string.
- Upload transport stays HTTPS-only with the loopback dev exception (`UrlProblem` guard); don't widen it.
