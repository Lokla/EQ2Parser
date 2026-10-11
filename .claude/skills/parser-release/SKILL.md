---
name: parser-release
description: Cut an EQ2Parser release - preflight, a local publish that is actually looked at, the tag, watching the Release workflow, checking the published assets, optional release notes. Use when the user says to release, tag, ship or publish EQ2Parser or asks for a new version to reach testers, and when asked how EQ2Parser versioning or releasing works. Pushing commits to EQ2Parser main is not a release and does not need this skill.
---

# Release (EQ2Parser)

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

This guard has teeth here. A release chain once ran with the shell in the sibling repo: it
created the version tag and a worktree there and tried to push both. Every command below
carries `-C` or `--repo` for that reason; keep them when you adapt a command.

## How a release works

- **The version is the tag.** No file holds a version number and nothing is bumped or
  committed. `.github/workflows/release.yml` takes `X.Y.Z` from the pushed tag `vX.Y.Z` and
  passes it to `dotnet publish` and `vpk pack`.
- **Pushing the tag publishes.** The workflow runs the CI gate (build, format check, tests),
  publishes a self-contained win-x64 app, checks that the four language dictionaries are
  embedded in the published assembly, packs it with Velopack, writes `SHA256SUMS.txt`,
  attests the build and creates a full (not pre-release) GitHub release on this repo.
  Installed copies check that feed at startup, download in the background and apply the
  update when the app exits, so users are on the new build from the launch after next.
- **The workflow runs the tests, and the preflight below is still worth doing.** A tag on a
  commit that fails the build, the format check or a test no longer publishes. But a failed
  release run has spent its tag (the fix is a new commit and so a new patch version, not a
  moved tag), and the UI check in step 2 sees what CI cannot: what the published app
  actually renders.
- `scripts/release.ps1` only builds and packs a local installer, for trying one before a
  release. It does not publish; pushing the tag is the only way to release.

Because a tag reaches every user through auto-update and cannot be recalled, cut one only
when the user has asked for a release in this conversation. "Push it" about commits means
push the commits.

## 1. Preflight

```bash
git -C <root> remote get-url origin              # must name VortexUK/EQ2Parser
git -C <root> status --short                     # must be empty
git -C <root> fetch origin --tags
git -C <root> rev-parse HEAD origin/main         # must be the same commit, on main
git -C <root> describe --tags --abbrev=0         # the previous version
git -C <root> log --no-merges --format=%s <previous tag>..HEAD   # what this release contains
gh run list --repo VortexUK/EQ2Parser --commit <HEAD sha> --json workflowName,status,conclusion
```

- If local main is ahead of `origin/main`, push the commits first and wait for the `CI` run
  on that commit to succeed. Do not tag a commit CI has not passed.
- If the log since the previous tag is empty there is nothing to release.
- Pick the version from that log and tell the user which one and why: patch for fixes, minor
  for a user-visible feature. The user's stated version wins.
- Run the `parser-verify` skill (the plain form, no filter). `core.hooksPath` is not
  necessarily set in a clone, so do not assume the pre-push hook ran.

## 2. Publish locally and look at it

Build the same artifact the workflow builds, then check what it renders:

```bash
dotnet publish <root>/src/EQ2Parser.App -c Release -r win-x64 --self-contained true -o <root>/artifacts/publish -p:Version=X.Y.Z
pwsh -NoProfile -File <root>/scripts/tools/ui-check.ps1 -Exe <root>/artifacts/publish/EQ2Parser.App.exe -AllPages -Log <root>/tests/logs/eq2log_Menludiir.txt -Expect "vX.Y.Z"
```

Write `-p:Version`, not `/p:Version`: Git Bash rewrites a leading `/p:` as a path and the
publish fails with a switch error.

Then open `artifacts/ui-check/Main.png` and at least one page the release changed. The
`parser-ui-check` skill explains the output. This step is not optional and a running process
is not evidence: one release shipped with every label showing its raw key because the string
tables had dropped out of the published assembly, which the unit tests did not see and a
liveness check did not either. Another threw at first render of the main view.

If the release changes anything the site must agree with (upload or attendance payload, a
mirrored list, a new endpoint), do the end-to-end check in the `parser-contract` skill before
going on. Uploads have shipped broken twice for want of one real request.

## 3. Tag and push

```bash
git -C <root> tag -a vX.Y.Z -m "EQ2Parser X.Y.Z"
git -C <root> push origin vX.Y.Z
```

## 4. Watch

```bash
gh run list --repo VortexUK/EQ2Parser --workflow release.yml --limit 1 --json databaseId,status,headBranch
gh run watch <databaseId> --repo VortexUK/EQ2Parser --exit-status
```

A run takes about four minutes (build, format, tests, publish, pack). The run can take a few seconds to appear; check
again rather than chaining `sleep` in front of `gh`.

- **No run appears** (an Actions outage has done this): leave the tag where it is. Deleting
  and re-pushing a tag has produced duplicate and failed runs before. Start the workflow on
  the tag by hand instead:
  `gh workflow run release.yml --repo VortexUK/EQ2Parser --ref vX.Y.Z -f version=X.Y.Z -f publish=true`
- **The run fails:** read `gh run view <databaseId> --repo VortexUK/EQ2Parser --log-failed`.
  Fix forward. If the failure is in the workflow file itself, the fix has to be on the commit
  the tag points at, which means a new patch version, not a moved tag.
- The workflow publishes to this repo only; the second copy that once went to an older
  releases repository is gone. Its last step attaches `SHA256SUMS.txt` to the release it has
  just created. If only that step fails, the release is already public without its checksum
  file; report it rather than rerunning the whole job.

## 5. Check what was published

```bash
gh release view vX.Y.Z --repo VortexUK/EQ2Parser --json isDraft,isPrerelease,assets --jq '{isDraft, isPrerelease, assets: [.assets[].name]}'
```

Expect `isDraft` and `isPrerelease` both false (the updater ignores pre-releases) and six
assets: `EQ2Parser-X.Y.Z-full.nupkg`, `EQ2Parser-win-Setup.exe`, `EQ2Parser-win-Portable.zip`,
`RELEASES`, `releases.win.json`, `SHA256SUMS.txt`. The site's download page links to the
Setup and Portable names, so a changed asset name is a site change too (`parser-contract`).

## 6. Release notes

Release bodies have been left empty so far. Offer a short draft built from the log in step 1,
written for players (what changed for them, no commit prefixes, no tester names), and set it
only if the user wants it:

```bash
gh release edit vX.Y.Z --repo VortexUK/EQ2Parser --notes-file <file>
```

## If the release is bad

Do not delete the release or move the tag: some installs will already have downloaded it and
the updater only moves forward. Fix the problem, run this skill again and ship the next patch
version. Say plainly what was wrong with the withdrawn one.

## Report

The version and the commit it points at, the verdicts of `parser-verify` and `ui-check`, the
workflow result, the asset list, and anything that was skipped, named as skipped.
