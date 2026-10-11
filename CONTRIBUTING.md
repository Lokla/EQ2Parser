# Contributing

Thanks for considering a contribution. This is a small project; the process is light.

## Before you start

For non-trivial changes, [open an issue](https://github.com/VortexUK/EQ2Parser/issues/new/choose) first to discuss the approach. Small fixes (typos, docs, obvious bugs) can go straight to a PR.

For security issues, see [SECURITY.md](SECURITY.md) — don't file public issues for vulnerabilities.

## The one rule that is not negotiable

**Cleanroom parsing.** ACT's parser sources (`ACT_English_Parser.cs` and friends) are unlicensed — all rights reserved. They are a *behavioural reference only*. Never copy code, comments, or identifier names from them into this repo. Correctness is proven by diffing our output against ACT's on real logs, not by transcription. A PR that lifts ACT code will be closed regardless of quality.

## Dev setup

Requires the .NET 10 SDK (pinned via `global.json`) on Windows.

```powershell
dotnet build     # warnings are errors, analyzers on
dotnet test      # xunit; the golden corpus runs unconditionally
dotnet run --project src\EQ2Parser.App
```

Activate the pre-push hook once after cloning — it runs the same gates CI runs:

```powershell
git config core.hooksPath .githooks
```

## The gates (pre-push + CI)

| Gate | Command |
|------|---------|
| Build (analyzers, warnings-as-errors) | `dotnet build` |
| Formatting + style | `dotnet format EQ2Parser.slnx --verify-no-changes` |
| Tests | `dotnet test` |

To auto-fix formatting: `dotnet format EQ2Parser.slnx`. Analyzer policy (and every deliberate deviation, with its reason) lives in [.editorconfig](.editorconfig); the rule levels are set in [Directory.Build.props](Directory.Build.props).

## Architecture ground rules

Full detail in [CLAUDE.md](CLAUDE.md) and [docs/engine-behaviour.md](docs/engine-behaviour.md); the short version:

- **Core stays UI-free.** `EQ2Parser.Core` targets plain `net10.0` and must never reference WPF. Everything testable lives there; the App project is a thin shell. Within Core, `Combat` is the bottom layer (enforced by `LayeringTests`).
- **Match the numbers.** Stat definitions that feed visible numbers (EncDPS, durations, the 6s idle rule, success levels) stay stable so EQ2Lexicon rankings remain comparable. Improvements go around the numbers, not through them.
- **Multi-log first.** Engine types are source-aware; one parse pipeline per log, merged by the correlator. Don't add state that assumes a single log.
- **Server-supplied content is hostile until proven otherwise.** Regexes get `MatchTimeout`, sound paths go through the non-local refusal, upload URLs stay HTTPS. See [SECURITY.md](SECURITY.md).
- Comments explain *why*, not *what*.

## Localization

User-facing strings go through `Loc` with keys in `src/EQ2Parser.App/Localization/strings.*.json` — all four dictionaries (en/de/fr/ru). `LocalizationTests` fails on missing keys.

## PR checklist

- [ ] Pre-push hook passes locally (all gates above)
- [ ] New behaviour has tests; parser changes update the golden corpus if a new line shape is involved
- [ ] No ACT source code copied (see above)
- [ ] User-facing or architectural changes reflected in [CLAUDE.md](CLAUDE.md) / [README.md](README.md)
- [ ] New user-facing strings added to all four `strings.*.json` dictionaries
- [ ] Commit messages explain the *why*

## Releases

Maintainer-driven: pushing a tag `vX.Y.Z` runs the release workflow, which tests, packs with Velopack and publishes a GitHub release on this repo (the feed the in-app updater reads). No file holds a version number. `scripts/release.ps1` only builds a local installer to try. Contributors never need to touch this.
