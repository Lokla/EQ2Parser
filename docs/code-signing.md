# Code signing — decision, current state, and the Certum path

Come-back-to-it reference. Captures why EQ2Parser ships **unsigned (for now)** and
the exact steps to switch to a real Authenticode signature via **Certum** when we
have a stable release worth signing.

---

## TL;DR

- **Right now:** releases are **not code-signed**. Integrity comes from **GitHub
  build-provenance attestation** (cryptographic proof the binary was built from
  this repo in CI) plus a **published `SHA256SUMS.txt`** on each release.
  Users get a one-time SmartScreen warning ("More info → Run anyway").
- **When we're ready to sign:** buy a **Certum Open Source Code Signing** cert
  (~$58/yr) — it requires a public repo, which this now is — and add a signing
  step. See [The Certum path](#the-certum-path) below.
- **Reality check:** an OV cert (Certum, SignPath, MS) does **not** remove the
  SmartScreen warning immediately — reputation builds over downloads/time. Only
  an **EV cert (~£300+/yr + a registered company)** kills the warning on day one.
  We are deliberately not doing EV.

---

## Why not the "easy" or free options

Since **June 2023** the CA/Browser Forum requires the signing key to live on
hardware or a cloud HSM — cheap file-based certs no longer exist. Every path
below involves a token or a cloud service. That's industry-wide, not a quirk of
any one vendor.

| Option | Cost | Blocker for us |
|---|---|---|
| **Microsoft Trusted / Azure Artifact Signing** | ~$10/mo | UK **individuals are not eligible** — UK access is **organizations only** (needs a registered company). |
| **SignPath Foundation** | Free | Real, but **open-source only** (public repo) **and** the free **self-service tier disables CI signing** — proven by `Error 403: TrustedBuildSystems feature is disabled for this subscription`. CI signing only unlocks once a **Foundation application is approved**. |
| **Certum Open Source** | ~$58/yr | Requires **public repo**. Company-free. **← our chosen path.** |
| **Certum Individual** | ~€189/yr | Works for a **private** repo, ~3× the price. |
| **EV certificate** | ~£300+/yr | Needs a **registered company**. Only option that removes SmartScreen instantly. |

Note: now that the repo is public, **SignPath Foundation is free** and
covers the same case as Certum-OSS. Certum's advantage is that it's **our own
cert** (no connector/trusted-build-system dependency, no approval wait). Pick
Certum if we want control + no wait; pick Foundation if £0 matters more than the
approval delay.

---

## Current state (interim) — attestation + hashes

Implemented in [`.github/workflows/release.yml`](../.github/workflows/release.yml).
Every release:

1. builds + `vpk pack`s the installer,
2. writes `SHA256SUMS.txt` over the `Setup.exe` and `.nupkg`,
3. **attests build provenance** (`actions/attest-build-provenance`), and
4. publishes the release with the checksums attached.

**How a user (or we) verifies a download without a code signature:**

```powershell
# 1. Checksum matches the published SHA256SUMS.txt
Get-FileHash .\EQ2Parser-win-Setup.exe -Algorithm SHA256

# 2. Provenance: the binary was built from THIS repo in CI (needs gh CLI)
gh attestation verify .\EQ2Parser-win-Setup.exe --repo VortexUK/EQ2Parser
```

This is the honest integrity story until we sign: it proves *authenticity and
build origin*, just not *publisher identity on the .exe*, and it doesn't suppress
SmartScreen.

---

## The Certum path

Do this when we have a release we're happy to stand behind long-term. Order
matters — SmartScreen reputation accrues **per certificate**, so switching certs
resets it. Sign consistently once you start.

### 1. Make the repo public (done)
Certum's cheap cert issues to an **"Open Source Developer"** identity and requires
a public project. The repo is public and MIT-licensed, so this step is complete.
(Security note we accepted at the time: client-side hardening is not defeated by
being readable — real upload integrity is server-side.)

### 2. Buy + validate the certificate
- Product: **Certum "Open Source Code Signing" on SimplySign (cloud)** —
  https://certum.store/open-source-code-signing-on-simplysign.html (~$58, verify
  the validity term at checkout; typically 1 year).
- Cloud delivery via **SimplySign** — **no USB token**. You install **SimplySign
  Desktop** on the PC and use the **SimplySign mobile app** for the per-signature
  OTP.
- Validation is real KYC: government ID for you as a person, plus evidence the
  project is open source (the public repo). Budget **several days to ~2 weeks**.

### 3. Choose where signing happens
**Option A — local signing (simplest, recommended first).** Sign the packed
`Setup.exe` on your machine at release time, before/instead of the CI publish:

```powershell
# After `vpk pack`, with SimplySign Desktop running + phone to hand:
signtool sign /tr http://time.certum.pl /td sha256 /fd sha256 `
  /n "Open Source Developer, <Your Name>" `
  .\Releases\EQ2Parser-win-Setup.exe
```

Then the release has to be published with the **signed** installer. Publishing is
CI-only today (`.github/workflows/release.yml`; the laptop publish flow was
removed), so local signing means uploading the signed `Setup.exe` over the CI
asset by hand — Option B is the cleaner route. Velopack hashes the `.nupkg`, not `Setup.exe`, so signing the Setup.exe
post-`pack` is safe and does not break the update manifest.

**Option B — CI signing (more automation, more setup).** Certum has no turnkey
GitHub Action like SignPath did. Drive the cloud cert from Actions with a
PKCS#11 signer — **[jsign](https://github.com/ebourg/jsign)** or the Certum
SimplySign KSP — feeding SimplySign credentials from repo **secrets**. This is
strictly more work than Option A; only do it if hands-off releases matter.

### 4. Wire it into the release
- If **local (A):** the CI workflow's job is just build → pack → **attest** →
  publish; you slot the `signtool` step in locally before publishing, or add a
  manual "sign then upload" step. Keep the attestation + `SHA256SUMS.txt` — they
  complement the signature.
- If **CI (B):** re-add a signing step to `release.yml` between *pack* and
  *attest* (the old SignPath step sat exactly there — see git history around the
  interim switch), attest the **signed** file, then publish.

### 5. What to expect after signing
- The publisher shows as **"Open Source Developer, <Your Name>"** on the .exe.
- **SmartScreen still warns at first** and eases as downloads accrue over
  days/weeks. This is normal for OV. Do not expect an instant clean install.

---

## If we ever revisit SignPath Foundation instead
The `release.yml` git history (the "interim switch" commit) contains the full
working SignPath step: `signpath/github-action-submit-signing-request@v1` with
org `c710b622-2381-4a27-9f97-f12a53e57850`, project `VortexUK_EQ2Parser`, and a
`<pe-file><authenticode-sign/></pe-file>` artifact config that signs the
`Setup.exe` as a single PE. It only needs a **Foundation-approved subscription**
(the self-service tier 403s on TrustedBuildSystems) and the `SIGNPATH_API_TOKEN`
secret, which is already set. Swap the policy slug to the issued cert's policy
and it works.
