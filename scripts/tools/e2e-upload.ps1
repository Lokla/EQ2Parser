# Upload real fights from this working tree to a local copy of the site and
# read back what it stored. The check unit tests on either side cannot make:
# the client's gzip, signature and queue against the site's real ingest route.
#
#   ./scripts/tools/e2e-upload.ps1                         # site checkout beside this repo
#   ./scripts/tools/e2e-upload.ps1 -Lexicon <path to EQ2Lexicon>
#   ./scripts/tools/e2e-upload.ps1 -Keep                   # leave the scratch folder for a look
#
# What it does, all on this machine:
#   1. creates a throwaway database on the LOCAL PostgreSQL server the site's
#      own tests use (TEST_DATABASE_URL in the site's .env), and migrates it;
#   2. starts the site from its checkout against that database, with every
#      database variable pointed at it, so no other database is reachable;
#   3. mints an API token for a throwaway user in it;
#   4. runs this repo's app (through ui-check.ps1) on scratch data with two
#      logs: one under logs/Varsoon (accepted) and one under logs/Kaladim
#      (not on the allow-list, so refused with X-Lexicon-Reject);
#   5. reads the stored rows back and checks them;
#   6. stops the site, drops the database and deletes the token. Always.
#
# Needs: the EQ2Lexicon checkout with its .env, uv on PATH, and the local
# PostgreSQL the site's test suite uses. About a minute.
#
# One PASS/FAIL line per check, then "e2e-upload: OK" (exit 0), "FAIL" (1) or
# "INCOMPLETE" (2: something it needs is missing; nothing was checked).
#
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads a BOM-less
# script that is not.
param(
    [string]$Lexicon,
    [int]$Port = 8765,
    [string]$Log,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$helper = Join-Path $PSScriptRoot 'e2e_upload_helper.py'
$work = Join-Path $repo 'artifacts\e2e-upload'
$failed = $false

function Stop-Incomplete([string]$why) {
    Write-Output "SKIP all ($why)"
    Write-Output 'e2e-upload: INCOMPLETE'
    exit 2
}
function Pass([string]$what) { Write-Output "PASS $what" }
function Fail([string]$what) { $script:failed = $true; Write-Output "FAIL $what" }

if (-not $Lexicon) { $Lexicon = Join-Path (Split-Path $repo -Parent) 'EQ2Lexicon' }
if (-not (Test-Path -LiteralPath (Join-Path $Lexicon 'main.py'))) { Stop-Incomplete "no EQ2Lexicon checkout at $Lexicon; pass -Lexicon" }
if (-not (Test-Path -LiteralPath (Join-Path $Lexicon '.env'))) { Stop-Incomplete 'the site checkout has no .env (it names the local test database)' }
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { Stop-Incomplete 'uv is not on PATH' }
$Lexicon = (Resolve-Path -LiteralPath $Lexicon).Path
if (-not $Log) { $Log = Join-Path $repo 'tests\logs\eq2log_Menludiir.txt' }
if (-not (Test-Path -LiteralPath $Log)) { Stop-Incomplete "no such log: $Log" }
$probe = New-Object System.Net.Sockets.TcpClient
try { $probe.Connect('127.0.0.1', $Port); $probe.Close(); Stop-Incomplete "port $Port is already in use; pass -Port" } catch { }

function Invoke-Helper([string[]]$HelperArgs) {
    # The helper runs in the site's own Python environment.
    $env:E2E_LEXICON = $Lexicon
    $ErrorActionPreference = 'Continue'
    $lines = & uv run --frozen --project $Lexicon python $helper @HelperArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    return [pscustomobject]@{ Code = $code; Lines = @($lines) }
}

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
[void](New-Item -ItemType Directory -Force -Path $work)
$tokenFile = Join-Path $work 'token.txt'
$serverLog = Join-Path $work 'server.log'
$server = $null

try {
    # --- 1. database ---------------------------------------------------------
    $r = Invoke-Helper @('db-create')
    if ($r.Code -ne 0) { Stop-Incomplete ("could not create the scratch database: " + ($r.Lines | Select-Object -Last 1)) }
    $r = Invoke-Helper @('migrate')
    if ($r.Code -ne 0) {
        Fail 'migrate (the site''s migrations did not apply to an empty database)'
        $r.Lines | Select-Object -Last 6 | ForEach-Object { Write-Output "  $_" }
        throw 'stop'
    }
    Pass ("migrate (" + (($r.Lines | Where-Object { $_ -match '^migrations applied' }) -join '') + ")")

    # --- 2. site -------------------------------------------------------------
    $env:E2E_LEXICON = $Lexicon
    $server = Start-Process -FilePath 'uv' -PassThru -WindowStyle Hidden `
        -ArgumentList @('run', '--frozen', '--project', "`"$Lexicon`"", 'python', "`"$helper`"", 'serve', "$Port") `
        -RedirectStandardOutput $serverLog -RedirectStandardError (Join-Path $work 'server.err.log')
    $up = $false
    for ($i = 0; $i -lt 45 -and -not $up; $i++) {
        Start-Sleep -Seconds 2
        try {
            $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/zones/raid-bosses" -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) { $up = $true }
        }
        catch { }
        if ($server.HasExited) { break }
    }
    if (-not $up) {
        Fail 'site start (it did not answer on the local port)'
        Get-Content -LiteralPath (Join-Path $work 'server.err.log') -ErrorAction SilentlyContinue |
            Where-Object { $_ -notmatch '://' } | Select-Object -Last 8 | ForEach-Object { Write-Output "  $_" }
        throw 'stop'
    }
    Pass 'site start'

    # --- 3. token ------------------------------------------------------------
    $r = Invoke-Helper @('token', $tokenFile)
    if ($r.Code -ne 0 -or -not (Test-Path -LiteralPath $tokenFile)) {
        Fail 'token (could not mint one in the scratch database)'
        $r.Lines | Where-Object { $_ -notmatch '://' } | Select-Object -Last 6 | ForEach-Object { Write-Output "  $_" }
        throw 'stop'
    }

    # --- 4. the app ----------------------------------------------------------
    $data = Join-Path $work 'data'
    $accepted = Join-Path $work 'game\logs\Varsoon'
    $refused = Join-Path $work 'game\logs\Kaladim'
    [void](New-Item -ItemType Directory -Force -Path $data, $accepted, $refused)
    $acceptedLog = Join-Path $accepted (Split-Path $Log -Leaf)
    $refusedLog = Join-Path $refused 'eq2log_Tester.txt'
    Copy-Item -LiteralPath $Log -Destination $acceptedLog
    Copy-Item -LiteralPath $Log -Destination $refusedLog

    # The app stores its token DPAPI-protected (TokenProtector); write it the same way.
    Add-Type -AssemblyName System.Security
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('EQ2Parser.LexiconApiToken.v1')
    $raw = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
    $blob = [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect(
            [System.Text.Encoding]::UTF8.GetBytes($raw), $entropy,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
    $settings = [ordered]@{
        FirstRunShown            = $true
        LanguageCode             = 'en'
        UploadEnabled            = $true
        UploadRaidOnly           = $false
        LexiconBaseUrl           = "http://localhost:$Port"
        LexiconApiTokenProtected = $blob
        LexiconTriggersEnabled   = $false
        Sources                  = @(
            @{ Path = $refusedLog; ParseFromStart = $false; LastPosition = 0 },
            @{ Path = $acceptedLog; ParseFromStart = $false; LastPosition = 0 }
        )
    }
    [System.IO.File]::WriteAllText((Join-Path $data 'settings.json'), ($settings | ConvertTo-Json -Depth 5),
        (New-Object System.Text.UTF8Encoding($false)))

    $ui = Join-Path $work 'ui'
    $ErrorActionPreference = 'Continue'
    $uiOut = & (Join-Path $PSScriptRoot 'ui-check.ps1') -DataDir $data -KeepSources -Page Settings -SettleSeconds 25 -Out $ui 2>&1 |
        ForEach-Object { "$_" }
    $ErrorActionPreference = 'Stop'
    if (($uiOut | Select-Object -Last 1) -ne 'ui-check: OK') {
        Fail 'app run (ui-check did not pass)'
        $uiOut | Select-Object -Last 6 | ForEach-Object { Write-Output "  $_" }
    }
    $page = Join-Path $ui 'Settings.txt'
    $statusLines = @()
    if (Test-Path -LiteralPath $page) {
        $statusLines = @(Get-Content -LiteralPath $page -Encoding UTF8 |
                Where-Object { $_ -match 'Uploaded \(|skipped |refused|paused|not uploaded|upload failed|retrying|waiting' } |
                ForEach-Object { ($_ -split "`t", 2)[1] })
    }
    if ($statusLines | Where-Object { $_ -match 'paused' }) { Fail 'queue not paused (the Settings page says uploads are paused)' }
    else { Pass 'queue not paused by the refused server' }

    # --- 5. what the site stored ---------------------------------------------
    $r = Invoke-Helper @('readback')
    $json = $r.Lines | Where-Object { $_.StartsWith('{') } | Select-Object -Last 1
    if ($r.Code -ne 0 -or -not $json) {
        Fail 'readback'
        $r.Lines | Where-Object { $_ -notmatch '://' } | Select-Object -Last 6 | ForEach-Object { Write-Output "  $_" }
        throw 'stop'
    }
    $stored = $json | ConvertFrom-Json
    $enc = @($stored.encounters)
    $rows = @($stored.attack_rows)
    $onAccepted = @($enc | Where-Object { $_.world -eq 'Varsoon' })
    $elsewhere = @($enc | Where-Object { $_.world -ne 'Varsoon' })

    if ($onAccepted.Count -ge 1) { Pass ("fights stored for the accepted server: $($onAccepted.Count) (" + (($onAccepted | ForEach-Object { $_.title }) -join ', ') + ')') }
    else { Fail 'no fight was stored for the accepted server' }
    if ($elsewhere.Count -eq 0) { Pass 'nothing stored for the refused server' }
    else { Fail "fights stored under an unexpected world: $(($elsewhere | ForEach-Object { $_.world } | Select-Object -Unique) -join ', ')" }
    if ($stored.quarantined -eq 0) { Pass 'no upload quarantined by the plausibility gate' }
    else { Fail "$($stored.quarantined) upload(s) quarantined by the plausibility gate" }

    $types = @($rows | ForEach-Object { [int]$_.swing_type } | Sort-Object -Unique)
    Write-Output "NOTE swing types stored: $($types -join ', ') across $($rows.Count) attack rows"
    if ($types -contains 30) { Fail 'status-effect rows (swing type 30) were uploaded' } else { Pass 'no status-effect rows uploaded' }
    $unlabelled = @($rows | Where-Object { ([int]$_.swing_type -in 3, 13, 16) -and -not $_.resist })
    if ($unlabelled.Count -eq 0) { Pass 'heal, power and threat rows carry their resist label' }
    else { Fail "$($unlabelled.Count) heal/power/threat row(s) without a resist label (first: $($unlabelled[0].attack_name))" }
    $emptyCures = @($rows | Where-Object { [int]$_.swing_type -eq 20 -and [long]$_.damage -lt 1 })
    if ($emptyCures.Count -eq 0) { Pass 'cure rows carry a count' } else { Fail "$($emptyCures.Count) cure row(s) uploaded with a count of 0" }
    $idle = @($rows | Where-Object { [long]$_.damage -eq 0 -and [int]$_.hits -eq 0 -and $_.attack_name -eq 'Killing' })
    if ($idle.Count -eq 0) { Pass 'no zero-activity Killing rows uploaded' } else { Fail 'zero-activity Killing rows were uploaded' }
    if ($statusLines.Count -gt 0) { Write-Output "NOTE the app's last status line: $($statusLines[-1])" }
}
catch {
    if ("$_" -ne 'stop') { Fail "unexpected error: $($_.Exception.Message)" }
}
finally {
    # --- 6. always clean up --------------------------------------------------
    if ($server -and -not $server.HasExited) { & taskkill /PID $server.Id /T /F 2>&1 | Out-Null }
    $drop = Invoke-Helper @('db-drop')
    if ($drop.Code -ne 0) { Write-Output 'NOTE could not drop the scratch database eq2parser_e2e_upload; drop it by hand' }
    if (Test-Path -LiteralPath $tokenFile) { Remove-Item -LiteralPath $tokenFile -Force }
    if (-not $Keep -and (Test-Path -LiteralPath $work)) {
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch { }
    }
}

if ($failed) {
    Write-Output 'e2e-upload: FAIL'
    exit 1
}
Write-Output 'e2e-upload: OK'
exit 0
