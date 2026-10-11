# The CI gate, run locally, printing failures only.
#
#   ./scripts/tools/verify.ps1                      # build + format + all tests (Release, as CI)
#   ./scripts/tools/verify.ps1 -Tests Core          # only tests/EQ2Parser.Core.Tests
#   ./scripts/tools/verify.ps1 -Filter "FullyQualifiedName~EnglishGrammar"
#   ./scripts/tools/verify.ps1 -NoFormat            # skip the format check (inner loop)
#   ./scripts/tools/verify.ps1 -Isolated            # build under artifacts/verify instead of bin/
#   ./scripts/tools/verify.ps1 -Diagnostics         # keep EQ2PARSER_SAMPLE_LOG for the env-gated tests
#
# Mirrors .github/workflows/ci.yml and .githooks/pre-push: build (analyzers,
# warnings as errors), dotnet format --verify-no-changes, dotnet test.
#
# One line per stage: PASS, SKIP <reason>, or FAIL followed by that stage's
# errors. Last line: "verify: OK" (exit 0), "verify: FAIL" (exit 1) or
# "verify: INCOMPLETE" (exit 2, a tool is missing).
#
# A copy of the app running out of bin/<Configuration> holds its DLLs open and
# would fail the build. That is detected, and the build then goes to
# artifacts/verify (git-ignored, inside the repo so the tests that read the
# source tree still find it).
#
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads a BOM-less
# script that is not.
param(
    [ValidateSet('All', 'Core', 'App')][string]$Tests = 'All',
    [string]$Filter,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$NoFormat,
    [switch]$Isolated,
    [switch]$Diagnostics
)

$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sln = Join-Path $repo 'EQ2Parser.slnx'
$failed = $false
$maxLines = 40

function Invoke-Stage {
    # Runs dotnet with the given arguments from the repo root; returns the
    # exit code, the output lines and the elapsed seconds.
    param([string[]]$DotnetArgs)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location $repo
    try {
        $lines = & dotnet @DotnetArgs 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    $sw.Stop()
    return [pscustomobject]@{
        Code    = $code
        Lines   = @($lines)
        Seconds = [int][math]::Round($sw.Elapsed.TotalSeconds)
    }
}

function Write-Trimmed {
    # Prints the diagnostic lines of a failed stage, or its tail when none match.
    param([string[]]$Lines, [string]$Pattern)
    $hits = @($Lines | Where-Object { $_ -match $Pattern } | Select-Object -Unique)
    if ($hits.Count -eq 0) { $hits = @($Lines | Select-Object -Last 30) }
    $hits | Select-Object -First $maxLines | ForEach-Object { Write-Output "  $_" }
    if ($hits.Count -gt $maxLines) { Write-Output "  ... $($hits.Count - $maxLines) more" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Output 'SKIP all (dotnet is not on PATH)'
    Write-Output 'verify: INCOMPLETE'
    exit 2
}

# --- Where to build ---------------------------------------------------------
$holders = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $p = $null
        try { $p = $_.Path } catch { $p = $null }
        $p -and $p.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase) -and
        ($p -like "*\bin\$Configuration\*")
    })
if ($holders.Count -gt 0 -and -not $Isolated) {
    $Isolated = $true
    $names = ($holders | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
    Write-Output "NOTE $names is running from bin/$Configuration; building under artifacts/verify instead"
}
$layout = @()
if ($Isolated) { $layout = @('--artifacts-path', (Join-Path $repo 'artifacts\verify')) }

# --- Build ------------------------------------------------------------------
$build = Invoke-Stage (@('build', $sln, '-c', $Configuration, '--nologo', '-v', 'q') + $layout)
if ($build.Code -eq 0) {
    Write-Output "PASS build ($($build.Seconds)s)"
}
else {
    $failed = $true
    Write-Output "FAIL build ($($build.Seconds)s)"
    Write-Trimmed $build.Lines ': error |error MSB|error NU'
}

# --- Format -----------------------------------------------------------------
if ($NoFormat) {
    Write-Output 'SKIP format (-NoFormat)'
}
else {
    $fmt = Invoke-Stage @('format', $sln, '--verify-no-changes')
    if ($fmt.Code -eq 0) {
        Write-Output "PASS format ($($fmt.Seconds)s)"
    }
    else {
        $failed = $true
        Write-Output "FAIL format ($($fmt.Seconds)s) - fix with: dotnet format EQ2Parser.slnx"
        Write-Trimmed $fmt.Lines ': error |: warning '
    }
}

# --- Test -------------------------------------------------------------------
if ($build.Code -ne 0) {
    Write-Output 'SKIP test (build failed)'
}
else {
    $target = switch ($Tests) {
        'Core' { Join-Path $repo 'tests\EQ2Parser.Core.Tests' }
        'App' { Join-Path $repo 'tests\EQ2Parser.App.Tests' }
        default { $sln }
    }
    $results = Join-Path $repo 'artifacts\verify-results'
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    $testArgs = @('test', $target, '-c', $Configuration, '--no-build', '--nologo', '-v', 'q',
        '--logger', 'trx', '--results-directory', $results) + $layout
    if ($Filter) { $testArgs += @('--filter', $Filter) }

    # The env-gated diagnostics read a real log and can run for minutes. CI
    # never has the variable, so neither does the gate unless asked.
    $savedLog = $env:EQ2PARSER_SAMPLE_LOG
    if (-not $Diagnostics) { $env:EQ2PARSER_SAMPLE_LOG = $null }
    try { $test = Invoke-Stage $testArgs }
    finally { $env:EQ2PARSER_SAMPLE_LOG = $savedLog }

    $total = 0; $passed = 0; $bad = @()
    if (Test-Path $results) {
        foreach ($trx in Get-ChildItem $results -Filter *.trx -Recurse) {
            [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw
            $c = $doc.TestRun.ResultSummary.Counters
            if ($c) { $total += [int]$c.total; $passed += [int]$c.passed }
            foreach ($r in @($doc.TestRun.Results.UnitTestResult)) {
                if ($r -and $r.outcome -eq 'Failed') { $bad += $r }
            }
        }
    }

    if ($test.Code -eq 0 -and $total -gt 0) {
        Write-Output "PASS test ($passed of $total, $($test.Seconds)s)"
    }
    elseif ($test.Code -eq 0) {
        # Exit 0 with nothing executed is a filter that matched no test.
        $failed = $true
        Write-Output "FAIL test (no tests ran - check -Filter / -Tests)"
    }
    else {
        $failed = $true
        Write-Output "FAIL test ($($bad.Count) failed of $total, $($test.Seconds)s)"
        if ($bad.Count -eq 0) {
            Write-Trimmed $test.Lines 'error|\[FAIL\]|Failed '
        }
        foreach ($r in ($bad | Select-Object -First 15)) {
            Write-Output "  $($r.testName)"
            $msg = "$($r.Output.ErrorInfo.Message)".Trim() -split "`r?`n" | Select-Object -First 6
            foreach ($m in $msg) { Write-Output "      $m" }
            $at = "$($r.Output.ErrorInfo.StackTrace)".Trim() -split "`r?`n" |
                Where-Object { $_ -match ':line \d+' } | Select-Object -First 1
            if ($at) { Write-Output "      $($at.Trim())" }
        }
        if ($bad.Count -gt 15) { Write-Output "  ... $($bad.Count - 15) more" }
    }

    if (-not $Diagnostics) {
        Write-Output 'SKIP diagnostics (the EQ2PARSER_SAMPLE_LOG tests returned early; -Diagnostics runs them)'
    }
    if ($Isolated) {
        Write-Output 'NOTE isolated build: Dictionaries_Are_Embedded_In_The_Built_App_Assembly read whatever is in src/EQ2Parser.App/bin, not this build'
    }
}

if ($failed) {
    Write-Output 'verify: FAIL'
    exit 1
}
Write-Output 'verify: OK'
exit 0
