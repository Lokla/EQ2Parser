---
name: parser-verify
description: Run EQ2Parser's CI gate locally (build with analyzers, format check, xunit tests) and see only the failures. Use before saying EQ2Parser work is done, before a commit or push to it, after fixing a failing check, and whenever you would otherwise type dotnet build or dotnet test in that repo by hand.
---

# Verify (EQ2Parser)

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

## The command

`scripts/tools/verify.ps1` runs what `.github/workflows/ci.yml` and `.githooks/pre-push` run,
in the Release configuration, and prints one line per stage. It takes about 30 seconds on a
warm tree.

```powershell
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/verify.ps1                 # build, format, every test
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/verify.ps1 -Tests Core     # only tests/EQ2Parser.Core.Tests (-Tests App for the other)
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/verify.ps1 -Filter "FullyQualifiedName~EnglishGrammar"
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/verify.ps1 -NoFormat       # inner loop: skip the 15 s format check
```

It works from any directory, so there is no need to `cd` into the repo.

Each stage prints `PASS`, `SKIP <reason>`, or `FAIL` followed by that stage's errors (compiler
and analyzer errors for the build; file, line and rule for format; test name, assertion
message and source line for tests). The last line is the verdict: `verify: OK` (exit 0),
`verify: FAIL` (exit 1) or `verify: INCOMPLETE` (exit 2, `dotnet` is missing).

Run the plain form before a push. The narrower forms are for iterating; a `-Filter` or
`-NoFormat` run does not stand in for the gate.

## Reading the result

- **The build is also the linter and the type check.** `Directory.Build.props` turns on
  nullable, the recommended analyzers, code-style enforcement and warnings-as-errors, so a
  `CAxxxx` or `IDExxxx` line fails the build like a compile error. Fix the code; per-rule
  exceptions live in `.editorconfig` and are a deliberate decision, not a quick fix.
- **A format failure** is fixed by `dotnet format EQ2Parser.slnx` run in the repo root, then
  a rerun.
- **`FAIL test (no tests ran ...)`** means the filter matched nothing. It is reported as a
  failure on purpose: a filter with a typo would otherwise look green.
- **`SKIP diagnostics`** is always printed. Several tests (`GoldenLogTests`,
  `CoverageReportTests`, `CalibrationReportTests`, `BucketSeparationProbe`, one in
  `ClassIdentifierTests`) only do work when `EQ2PARSER_SAMPLE_LOG` names a real log, and
  return early otherwise. They show as passed either way, so a green run says nothing about
  them. The script clears the variable for its run to match CI; `-Diagnostics` keeps it. The
  `parser-grammar` skill covers running them for real.

## The app is usually running

The developer normally has the app open from `src/EQ2Parser.App/bin/Debug`, and it holds its
DLLs open, so a Debug build into `bin/` fails with a file-in-use error. That is why the gate
defaults to Release, and why the script checks first: if a process is running out of the
`bin/<Configuration>` it is about to build, it prints a `NOTE` and builds under
`artifacts/verify` instead (git-ignored). Never stop that process to free the files: it is
a live parsing session.

One consequence of an isolated build: `LocalizationTests.Dictionaries_Are_Embedded_In_The_Built_App_Assembly`
looks at `src/EQ2Parser.App/bin`, so it checks an older build, not this one. The script says
so. The `parser-ui-check` skill is the stronger check for that failure anyway.

## What green does not cover

- Anything visible. XAML binding errors, a template that throws at first render and a
  missing string all pass the gate. Look at the running app with the `parser-ui-check` skill.
- The self-contained publish that ships. `parser-release` builds and checks that.
- Agreement with the site. A change to the upload payload or a mirrored list needs
  `parser-contract`.

Report what ran and what it showed. A skipped stage is "not run", never "passed".
