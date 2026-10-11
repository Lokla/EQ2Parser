# Launch the app on throwaway data, read what it actually rendered, and save a
# screenshot of each page.
#
#   ./scripts/tools/ui-check.ps1                          # build, launch, check the Main page
#   ./scripts/tools/ui-check.ps1 -AllPages                # walk every top tab
#   ./scripts/tools/ui-check.ps1 -Page Settings,Timers
#   ./scripts/tools/ui-check.ps1 -Log tests/logs/eq2log_Menludiir.txt   # with fights loaded
#   ./scripts/tools/ui-check.ps1 -Language de -AllPages
#   ./scripts/tools/ui-check.ps1 -Offline                 # no call to the live site at startup
#   ./scripts/tools/ui-check.ps1 -Exe publish/EQ2Parser.App.exe -AllPages   # a published build
#   ./scripts/tools/ui-check.ps1 -DataDir <copy of a data folder>           # reproduce on real state
#   ./scripts/tools/ui-check.ps1 -Page Settings -Expect "v0.5.8"            # text that must be on screen
#   ./scripts/tools/ui-check.ps1 -Log <file> -Today                          # re-stamp the log to end a minute ago
#   ./scripts/tools/ui-check.ps1 -Log <file> -Today -Select "Boss A|Boss B"   # select list items, then look
#
# What it checks, per page:
#   * no visible text is an unresolved localization key (Loc.Get falls back to
#     the key itself, which is how a release once shipped showing raw keys);
#   * every top-tab label of the chosen language is on screen;
#   * the app is still running and wrote no crash report.
#
# Output: <Out>/<Page>.png and <Out>/<Page>.txt (every UI Automation name on
# the page), one PASS/FAIL line per page, then "ui-check: OK" (exit 0),
# "ui-check: FAIL" (exit 1) or "ui-check: INCOMPLETE" (exit 2).
#
# -Today matters for the Main page: its fight list groups by day and only
# today's section starts expanded, so a log from last month shows a collapsed
# header and no fights to select. -Select takes text that appears in a list
# item (a fight's title, a row's name): the first match is selected, each
# further one is ADDED to the selection, as Ctrl+click would. Separate several
# texts with a bar, for -Select and for -Expect alike.
#
# The app runs with EQ2PARSER_DATA_DIR pointed at a scratch folder, so the real
# settings, history and token are never read or written. Its window is on
# screen for a few seconds and may take focus. Only the process started here is
# closed; a copy of the app already running is left alone.
#
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads a BOM-less
# script that is not.
param(
    [string]$Exe,
    [string[]]$Page = @('Main'),
    [switch]$AllPages,
    [string]$Log,
    [ValidateSet('en', 'de', 'fr', 'ru')][string]$Language = 'en',
    [string]$DataDir,
    [switch]$KeepSources,
    [string[]]$Expect = @(),
    [string]$Out,
    [switch]$Offline,
    [switch]$Today,
    [string[]]$Select = @(),
    [int]$SettleSeconds = 3,
    [int]$StartTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$navKeys = @('Main', 'Sources', 'Triggers', 'Timers', 'Overlays', 'Settings')
# "-File ... -Page Main,Settings" arrives as one string, so split it here.
# Fight titles and labels can contain commas, so several -Select or -Expect
# texts in one argument are separated by a bar: -Select "Boss A|Boss B".
$Select = @($Select | ForEach-Object { $_ -split '\|' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Expect = @($Expect | ForEach-Object { $_ -split '\|' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Page = @($Page | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($AllPages) { $Page = $navKeys }
if (-not $Out) { $Out = Join-Path $repo 'artifacts\ui-check' }
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Stop-Incomplete([string]$why) {
    Write-Output "SKIP all ($why)"
    Write-Output 'ui-check: INCOMPLETE'
    exit 2
}

function Read-Strings([string]$code) {
    $path = Join-Path $repo "src\EQ2Parser.App\Localization\strings.$code.json"
    $json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $map = @{}
    foreach ($p in $json.PSObject.Properties) { $map[$p.Name] = [string]$p.Value }
    return $map
}

# --- Inputs -----------------------------------------------------------------
foreach ($p in $Page) {
    if ($navKeys -notcontains $p) { Stop-Incomplete "unknown page '$p'; pages are: $($navKeys -join ', ')" }
}
$english = Read-Strings 'en'
$strings = if ($Language -eq 'en') { $english } else { Read-Strings $Language }
# A key whose English text IS the key would look unresolved when it is not.
$keySet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($k in $english.Keys) { if ($english[$k] -ne $k) { [void]$keySet.Add($k) } }

if ($Exe) {
    if (-not (Test-Path -LiteralPath $Exe)) { Stop-Incomplete "no such exe: $Exe" }
    $Exe = (Resolve-Path -LiteralPath $Exe).Path
}
else {
    # Build under artifacts/, never bin/: a copy of the app running from bin/
    # holds its DLLs open.
    $art = Join-Path $repo 'artifacts\verify'
    $ErrorActionPreference = 'Continue'
    $buildOut = & dotnet build (Join-Path $repo 'src\EQ2Parser.App') -c Debug --nologo -v q --artifacts-path $art 2>&1 |
        ForEach-Object { "$_" }
    $buildCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($buildCode -ne 0) {
        Write-Output 'FAIL build'
        $buildOut | Where-Object { $_ -match ': error |error MSB' } | Select-Object -Unique -First 30 |
            ForEach-Object { Write-Output "  $_" }
        Write-Output 'ui-check: FAIL'
        exit 1
    }
    $Exe = Join-Path $art 'bin\EQ2Parser.App\debug\EQ2Parser.App.exe'
    if (-not (Test-Path -LiteralPath $Exe)) { Stop-Incomplete "build produced no exe at $Exe" }
}

$realData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EQ2Parser'
if ($DataDir) {
    if (-not (Test-Path -LiteralPath $DataDir)) { Stop-Incomplete "no such data dir: $DataDir" }
    $DataDir = (Resolve-Path -LiteralPath $DataDir).Path
    if ($DataDir.TrimEnd('\') -ieq $realData.TrimEnd('\')) {
        Stop-Incomplete 'refusing to run on the real data folder; copy it somewhere and pass the copy'
    }
    # A copy of real state still names the real logs and may hold a working
    # upload token. Left alone, this instance would tail the live logs, write
    # command files into the real game folder and upload duplicates. So the
    # copy is disarmed unless -KeepSources says otherwise.
    $copied = Join-Path $DataDir 'settings.json'
    if ((Test-Path -LiteralPath $copied) -and -not $KeepSources) {
        $s = [System.IO.File]::ReadAllText($copied, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $s | Add-Member -NotePropertyName Sources -NotePropertyValue @() -Force
        $s | Add-Member -NotePropertyName WatchedFolders -NotePropertyValue @() -Force
        $s | Add-Member -NotePropertyName UploadEnabled -NotePropertyValue $false -Force
        $s | Add-Member -NotePropertyName FirstRunShown -NotePropertyValue $true -Force
        [System.IO.File]::WriteAllText($copied, ($s | ConvertTo-Json -Depth 20), $utf8)
        Write-Output 'NOTE -DataDir: cleared Sources and WatchedFolders and turned uploads off in that copy (-KeepSources leaves them)'
    }
}
else {
    # Scratch data, rebuilt every run. FirstRunShown stops the first-run
    # wizard, which is modal and would block the window.
    $DataDir = Join-Path $Out 'data'
    foreach ($stale in @($DataDir, (Join-Path $Out 'game'))) {
        if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
    }
    [void](New-Item -ItemType Directory -Force -Path $DataDir)
    $settings = [ordered]@{ FirstRunShown = $true; LanguageCode = $Language; Sources = @() }
    if ($Offline) {
        # By default the app fetches the public trigger library from the
        # live site at startup, which fills the Triggers and Timers pages.
        $settings.LexiconTriggersEnabled = $false
        $settings.LexiconBaseUrl = 'http://127.0.0.1:9'
    }
    if ($Log) {
        if (-not (Test-Path -LiteralPath $Log)) { Stop-Incomplete "no such log: $Log" }
        # The app treats the folder above logs/ as the game folder and writes
        # its raid command files (eq2lexicon-raid-*.txt) there, so the log is
        # always copied into a game-shaped scratch tree first. Pointing a
        # scratch instance at the original would drop those files beside it,
        # or overwrite the live ones in a real game folder.
        $src = Get-Item -LiteralPath $Log
        $logs = Join-Path $Out 'game\logs'
        if ($src.Directory.Name -ne 'logs') { $logs = Join-Path $logs $src.Directory.Name }
        [void](New-Item -ItemType Directory -Force -Path $logs)
        $copy = Join-Path $logs $src.Name
        Copy-Item -LiteralPath $src.FullName -Destination $copy -Force
        if ($Today) {
            # Shift every line's epoch by one amount so the last line lands a
            # minute ago. Spacing between lines, which is what fights are made
            # of, is unchanged; the bracketed local time is not read by the app.
            $lines = [System.IO.File]::ReadAllLines($copy, [System.Text.Encoding]::UTF8)
            $stamp = [regex]'^\((\d+)\)'
            $last = 0L
            foreach ($l in $lines) { $m = $stamp.Match($l); if ($m.Success) { $e = [long]$m.Groups[1].Value; if ($e -gt $last) { $last = $e } } }
            if ($last -gt 0) {
                $shift = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 60 - $last
                $moved = foreach ($l in $lines) {
                    $m = $stamp.Match($l)
                    if ($m.Success) { '(' + ([long]$m.Groups[1].Value + $shift) + ')' + $l.Substring($m.Length) } else { $l }
                }
                [System.IO.File]::WriteAllLines($copy, [string[]]$moved, $utf8)
            }
        }
        # LastPosition 0 = catch up from the first byte. Without a position a
        # restored source tails from the end and the page stays empty.
        $settings.Sources = @(@{ Path = $copy; ParseFromStart = $false; LastPosition = 0 })
    }
    [System.IO.File]::WriteAllText((Join-Path $DataDir 'settings.json'),
        ($settings | ConvertTo-Json -Depth 5), $utf8)
}
[void](New-Item -ItemType Directory -Force -Path $Out)

# --- Native helpers ---------------------------------------------------------
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
if (-not ('UiCheckNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiCheckNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
}
[void][UiCheckNative]::SetProcessDPIAware()

function Save-Window([IntPtr]$hwnd, [string]$path) {
    # PrintWindow with PW_RENDERFULLCONTENT (2) captures a composited WPF
    # window even when another window covers it.
    $r = New-Object UiCheckNative+RECT
    [void][UiCheckNative]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [UiCheckNative]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ok
}

function Get-Names($root) {
    # Every named element under the window: "ControlType<TAB>Name".
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($el in $all) {
        try {
            $name = $el.Current.Name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $type = $el.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            $rows.Add([pscustomobject]@{ Type = $type; Name = $name; Element = $el })
        }
        catch { continue }   # element vanished mid-walk
    }
    return $rows
}

function Select-Tab($root, [string]$label) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $label)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $node = $el
        for ($i = 0; $i -lt 3 -and $node; $i++) {
            $pattern = $null
            if ($node.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                # Select() returns before the app has acted on it, and a busy
                # UI thread can drop it, so confirm and retry: reporting on
                # the page that was showing before would be a false PASS.
                for ($try = 0; $try -lt 5; $try++) {
                    $pattern.Select()
                    Start-Sleep -Milliseconds 400
                    if ($pattern.Current.IsSelected) { return $true }
                }
                return $false
            }
            $node = $walker.GetParent($node)
        }
    }
    return $false
}

function Select-Items($root, [string[]]$texts) {
    # Selects list items by text they contain. Returns the texts not found.
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $missing = @()
    $first = $true
    foreach ($text in $texts) {
        $done = $false
        foreach ($row in (Get-Names $root)) {
            if (-not $row.Name.Contains($text)) { continue }
            $node = $row.Element
            for ($i = 0; $i -lt 4 -and $node -and -not $done; $i++) {
                $pattern = $null
                if ($node.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                    if ($first) { $pattern.Select() } else { $pattern.AddToSelection() }
                    $first = $false
                    $done = $true
                }
                else { $node = $walker.GetParent($node) }
            }
            if ($done) { break }
        }
        if (-not $done) { $missing += $text }
        Start-Sleep -Milliseconds 400
    }
    return $missing
}

function Get-CrashFiles {
    $dir = Join-Path $DataDir 'crashes'
    if (Test-Path -LiteralPath $dir) { return @(Get-ChildItem -LiteralPath $dir -Filter 'crash-*.txt') }
    return @()
}

# --- Launch -----------------------------------------------------------------
$failed = $false
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$previousForeground = [UiCheckNative]::GetForegroundWindow()
$crashesBefore = @(Get-CrashFiles | ForEach-Object { $_.Name })
# Builds up to v0.5.7 pointed a per-user crash-dump registry value at their
# data folder on every launch, so a scratch run of one would leave it aimed
# at the scratch folder. The value is saved here and put back afterwards.
# Current builds write nothing there and delete those keys (Windows ignores
# them); both image names are tracked so any build can be run.
$dumpRoot = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$dumpKeys = @()
foreach ($image in @([System.IO.Path]::GetFileName($Exe), 'EQ2Parser.App.exe', 'EQ2Parser.exe') | Select-Object -Unique) {
    $key = Join-Path $dumpRoot $image
    $existed = Test-Path -LiteralPath $key
    $folder = $null
    if ($existed) {
        $folder = (Get-Item -LiteralPath $key).GetValue('DumpFolder', $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    $dumpKeys += [pscustomobject]@{ Key = $key; Existed = $existed; Folder = $folder }
}
$savedDir = $env:EQ2PARSER_DATA_DIR
$env:EQ2PARSER_DATA_DIR = $DataDir
try { $proc = Start-Process -FilePath $Exe -PassThru }
finally { $env:EQ2PARSER_DATA_DIR = $savedDir }

try {
    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
        Start-Sleep -Milliseconds 300
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        $failed = $true
        if ($proc.HasExited) { Write-Output "FAIL launch (the app exited with code $($proc.ExitCode) before showing a window)" }
        else { Write-Output "FAIL launch (no window after $StartTimeoutSeconds s)" }
    }
    else {
        if ($previousForeground -ne [IntPtr]::Zero) { [void][UiCheckNative]::SetForegroundWindow($previousForeground) }
        Start-Sleep -Seconds $SettleSeconds
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $tabLabels = @($navKeys | ForEach-Object { $strings["Nav_$_"] })

        # Look at the startup view before navigating: with the dictionaries
        # missing, the tabs themselves are raw keys and cannot be found by label.
        $startNames = @(Get-Names $root | ForEach-Object { $_.Name })
        $startRaw = @($startNames | Where-Object { $keySet.Contains($_) } | Select-Object -Unique)
        if ($startRaw.Count -gt 0) {
            $failed = $true
            $png = Join-Path $Out 'Startup.png'
            [void](Save-Window $hwnd $png)
            Write-Output "FAIL startup (unresolved keys: $(($startRaw | Select-Object -First 12) -join ', ')) -> $png"
            $Page = @()
        }

        foreach ($p in $Page) {
            $label = $strings["Nav_$p"]
            if ($p -ne 'Main' -or $Page.Count -gt 1) {
                if (-not (Select-Tab $root $label)) {
                    $failed = $true
                    Write-Output "FAIL page $p (no selectable tab labelled '$label')"
                    continue
                }
                Start-Sleep -Seconds ([math]::Max(1, [int]($SettleSeconds / 2)))
            }
            if ($proc.HasExited) {
                $failed = $true
                Write-Output "FAIL page $p (the app exited with code $($proc.ExitCode))"
                break
            }

            $notSelected = @()
            if ($Select.Count -gt 0) {
                $notSelected = @(Select-Items $root $Select)
                Start-Sleep -Seconds 1
            }

            $rows = Get-Names $root
            $names = @($rows | ForEach-Object { $_.Name })
            foreach ($n in $names) { [void]$seen.Add($n) }
            $png = Join-Path $Out "$p.png"
            $shot = Save-Window $hwnd $png
            [System.IO.File]::WriteAllLines((Join-Path $Out "$p.txt"),
                [string[]]@($rows | ForEach-Object { "$($_.Type)`t$($_.Name)" }), $utf8)

            $problems = @()
            $raw = @($names | Where-Object { $keySet.Contains($_) } | Select-Object -Unique)
            if ($raw.Count -gt 0) {
                $problems += "unresolved keys: $(($raw | Select-Object -First 12) -join ', ')"
            }
            $missing = @($tabLabels | Where-Object { $names -notcontains $_ })
            if ($missing.Count -gt 0) { $problems += "tab labels not on screen: $($missing -join ', ')" }
            if (-not $shot) { $problems += 'screenshot failed' }
            if ($notSelected.Count -gt 0) { $problems += "no selectable item containing: $($notSelected -join ', ')" }

            if ($problems.Count -gt 0) {
                $failed = $true
                Write-Output "FAIL page $p ($($problems -join '; ')) -> $png"
            }
            else {
                Write-Output "PASS page $p ($($names.Count) named elements) -> $png"
            }
        }
    }
}
finally {
    if (-not $proc.HasExited) {
        try { [void]$proc.CloseMainWindow() } catch { }
        if (-not $proc.WaitForExit(8000)) { try { $proc.Kill() } catch { } }
    }
    try {
        foreach ($d in $dumpKeys) {
            $now = Test-Path -LiteralPath $d.Key
            if ($d.Existed -and $now -and $null -ne $d.Folder) {
                Set-ItemProperty -LiteralPath $d.Key -Name DumpFolder -Value $d.Folder -Type ExpandString
            }
            elseif (-not $d.Existed -and $now) {
                # This run created it; the real app writes its own on its next launch.
                Remove-Item -LiteralPath $d.Key -Recurse -Force
            }
            # A key the app itself removed (the stale EQ2Parser.exe one) stays removed.
        }
    }
    catch { Write-Output "NOTE could not restore the crash-dump registry value: $($_.Exception.Message)" }
}

foreach ($want in $Expect) {
    if (-not $want) { continue }
    $hit = $false
    foreach ($n in $seen) { if ($n.Contains($want)) { $hit = $true; break } }
    if ($hit) { Write-Output "PASS expect '$want'" }
    else {
        $failed = $true
        Write-Output "FAIL expect '$want' (not in any element name on the pages visited)"
    }
}

$newCrashes = @(Get-CrashFiles | Where-Object { $crashesBefore -notcontains $_.Name })
if ($newCrashes.Count -gt 0) {
    $failed = $true
    Write-Output "FAIL crash ($($newCrashes.Count) crash report(s) written during the run)"
    Get-Content -LiteralPath $newCrashes[0].FullName -TotalCount 25 | ForEach-Object { Write-Output "  $_" }
}

if ($failed) {
    Write-Output 'ui-check: FAIL'
    exit 1
}
Write-Output 'ui-check: OK'
exit 0
