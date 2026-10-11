---
name: parser-grammar
description: Find real EQ2 log lines, measure what EQ2Parser's line grammar misses, and add or fix a line shape without breaking the shapes behind it. Use when an EQ2 log line is not parsed or is parsed wrong in EQ2Parser, when damage, heals, avoidance or deaths are missing or differ from ACT, when a new line shape or event type must be supported, when a regex needs testing against real logs, or when the user asks to find or count lines in their EQ2 logs. Use it before editing EnglishGrammar.cs or LogLineProcessor.cs.
---

# Parser grammar

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

## Why this order

Two grammar gaps once dropped damage for weeks with every test green: amounts abbreviated
with K or M never matched, and second-person `YOU multi attack` lines never matched. A diff
against ACT (Advanced Combat Tracker, the incumbent parser) found both; reading code did not.
So: real lines, then measurements, then code, then proof. Say which step you are on.

## 1. Get real lines

`scripts/tools/logmine.ps1` searches real logs. Use it instead of an inline script: it reads
a log the game is still writing, prints non-ASCII text safely, and takes the regex as one
argument. Its header documents every option.

```powershell
$mine = '<EQ2Parser root>/scripts/tools/logmine.ps1'
& $mine -Pattern ' multi attacks? ' -Last 3 -Message                         # lines of a shape, 3 newest logs
& $mine -Pattern ' for (a critical of )?[\d,.]+[KMB] \w+ damage\.$' -Count   # how many, per file
& $mine -Templates -CollapseNames -Pattern ' tries to ' -Max 20              # which shapes exist, one example each
& $mine -Pattern '^You have entered ' -Since 2026-08-01 -Context 2           # with the lines around each match
& $mine -Pattern '^YOU (?<verb>flurry|flurries) ' -Path '<EQ2Parser root>/tests/logs/eq2log_Menludiir.txt'
```

- The pattern is a .NET regex matched against the message, after the `(epoch)[timestamp] `
  prefix, so `^` and `$` behave as they do in the grammar. A pattern pasted from a
  `[GeneratedRegex]` attribute shows what it really matches (expand `{DamageVerbs}` first).
- `-Templates` is how a new shape is found: numbers become `#`, names become `<N>` under
  `-CollapseNames`, and what is left is counted.
- The last line is a summary with the true total even when `-Max` cut the listing. Exit code
  0 means matches, 1 none, 2 a usage or configuration error.
- From Git Bash call `pwsh -NoProfile -File <path> ...` and single-quote the pattern. Git
  Bash rewrites a pattern that starts with `/`: write `[/]`, or use `-PatternFile`.

**Where the logs are.** Without `-Path` the script searches `EQ2PARSER_LOG_DIRS` (directories
or files separated by `;`, searched recursively for `eq2log_*.txt`), then
`EQ2PARSER_SAMPLE_LOG` (one file). If both are unset, look in this session's memory for the
log folder; if it is not there, ask the user once and reuse the answer. Do not hunt the disk.
Shell state does not survive between tool calls, so pass `-Path` or set
`$env:EQ2PARSER_LOG_DIRS = '<EQ2 install>\logs'` in the same command. Log folders hold months
of data, so start with `-Last` or `-Since`. The committed corpus
`tests/logs/eq2log_Menludiir.txt` (30 lines of the main combat shapes) is always available.

## 2. Measure coverage

Five tests in `tests/EQ2Parser.Core.Tests` run the real pipeline over the log named by
`EQ2PARSER_SAMPLE_LOG`. **When the variable is unset they return early and pass**, so a green
run without it proves nothing about them. `scripts/tools/verify.ps1` clears the variable and
prints failures only, so run these directly:

```powershell
$env:EQ2PARSER_SAMPLE_LOG = '<full path to one eq2log_<Character>.txt>'
dotnet test '<EQ2Parser root>/tests/EQ2Parser.Core.Tests' -c Release --filter "FullyQualifiedName~CoverageReportTests.Report_Unmatched_Line_Templates" --logger "console;verbosity=detailed"
```

| `--filter "FullyQualifiedName~..."` | What it prints |
|---|---|
| `CoverageReportTests.Report_Unmatched_Line_Templates` | The grammar-gap report: lines, matched %, chat noise %, unmatched %, then the 40 most common unmatched templates with an example each. Asserts nothing. |
| `GoldenLogTests.Full_Pipeline_Over_A_Real_Log` | Owner, line count, grammar-matched count and %, encounter count, and the ten biggest fights (zone, title, duration, damage, EncDPS, success). |
| `CalibrationReportTests.Success_Level_Ingredients_For_Big_Fights` | For the eight biggest fights of two minutes or more: success level, boss deaths, ally count, deathless player allies. For "ACT says win, we say partial". |
| `BucketSeparationProbe.Outgoing_Buckets_Contain_Only_Swings_By_The_Combatant` | Fails if a swing sits in the outgoing-damage bucket of someone who did not make it; prints up to 12 offenders. Prints nothing at all when skipped. |
| `ClassIdentifierTests.Class_Report_For_A_Real_Fight` | Class verdicts per combatant for fights whose title contains `EQ2PARSER_BOSS` (default `Wuoshi`). With `EQ2PARSER_DUMP_ABILITIES` set to a combatant name, also that combatant's abilities and the classes each maps to. |

- Output is xunit test output only, no file; `dotnet test` hides it for a passing test unless
  the logger is at `verbosity=detailed`. Append `| Tee-Object "$env:TEMP\before.txt"` to keep it.
- The owner is taken from the file name (`eq2log_<Character>.txt`): a copy must keep its name
  or every `YOU` line is credited to the wrong character.
- The tests read with `File.ReadLines`, which cannot open a file another process holds open
  for writing. If that character is in game with logging on, point the variable at a copy.
- Each test reads the whole file. Run the coverage report before the change and again after.

## 3. Change the grammar

Follow `references/add-line-shape.md`: where a new branch goes in `EnglishGrammar.TryParse`,
how to check it takes no lines from a later branch, the tables that change together, amounts,
new event types, and which `GoldenLogTests` assertions move when the corpus gains a line. In
short: the first pattern that matches wins, `YOU`/`YOUR` forms are separate shapes from
third-person ones (fix both), and nothing in the line path may throw.

## 4. Prove it

1. **A test from the real line.** Swap player names for neutral ones and assert the attacker,
   ability, victim and amount it must produce, plus one nearby line that must not match.
2. **The corpus.** A new family gets one line in `tests/logs/eq2log_Menludiir.txt` and the
   matching updates in `tests/EQ2Parser.Core.Tests/GoldenLogTests.cs`.
3. **The gate.** Use the `parser-verify` skill; the change is not done before it is green.
4. **The real log again.** Rerun the step 2 reports. The matched count should rise by what
   `logmine.ps1 -Count` gave for the new shape and nothing else, and the shape should be gone
   from the unmatched templates. Fight damage that moved unasked means a branch stole lines.

## Rules

- **Cleanroom.** ACT's English parser source is unlicensed. It is a behavioural reference
  only: never copy or paraphrase its code, comments or identifiers into this repo. A pattern
  is justified by real log lines, and correctness by diffing output against ACT on real logs.
- **A numbers dispute starts with ACT's figures.** Ask the user for ACT's numbers for the
  same fight, or an ACT export of it, before forming a view. Diff per combatant, then per
  ability, then pull the lines behind the first differing row with `logmine.ps1`. An
  avoidance report once took six rounds because the reasoning came before that diff.
- **Regexes.** Grammar patterns are compile-time `[GeneratedRegex]` members, anchored `^...$`,
  each tried only behind a literal guard, with no per-pattern timeout. What bounds them is the
  16 KB line cap (`LogTailOptions.MaxLineBytes`) and a process-wide default match timeout the
  app sets at startup in `src/EQ2Parser.App/App.xaml.cs` (tests run without it). Keep new
  patterns to lazy `.+?` groups separated by literal text, no nested quantifiers, and never
  build a `Regex` from a runtime string here. A regex that comes from data (the trigger pack,
  an ACT import, user input) gets an explicit `MatchTimeout`, as `Trigger.MatchTimeout` does:
  that is the rule in `SECURITY.md`. A timeout arrives as an exception, so see the next rule.
- **A throw loses the line.** `LogSource.PumpLine` in
  `src/EQ2Parser.App/Services/LogSource.cs` catches an exception from one line, skips that
  line, writes a `log-line` crash report and carries on with the next. The source itself
  stops only after 200 failed lines in a row (`LineFaultBreaker.DefaultTripAfter`). The rule
  to never throw stands all the same: the skipped line's damage, heal or death is gone, so
  every skipped line is a wrong number. A decimal amount once threw through a plain
  `long.Parse`, and at that time a throw ended the whole tail (see the comment in
  `TryParseAmount`). So a parse helper returns null or false on anything unexpected, as
  `TryParseAmount` does and as `ParseLockoutRemaining` does with `int.TryParse`, and a regex
  must guarantee whatever the code after it indexes or converts.
- **Visible totals are a contract.** Stat definitions stay stable so site rankings remain
  comparable (`docs/engine-behaviour.md` section 3). A grammar fix that moves damage, healing,
  durations or deaths on existing logs may be right, but tell the user which numbers move and
  by how much before it ships, and never change a section 3 definition as part of one.
- **English only today.** `EnglishGrammar` is the one grammar and the processor calls it
  directly. Do not start a second language, or an abstraction for one, as a side effect.
- **Real names stay out.** The repo is public. A line from a real log has its character names
  replaced with neutral ones before it goes into a test, a comment or a commit message. The
  committed corpus is the one exception: when adding to it, reuse the names already in it.
