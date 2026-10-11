# logmine.ps1 - search real EQ2 log files for a line shape, safely and compactly.
#
# Runs under Windows PowerShell 5.1 and PowerShell 7. Keep this file ASCII-only.
#
# Why this exists: ad-hoc inline scripts for this job kept failing on shell
# quoting, backslash mangling and non-ASCII console output. This script takes
# the pattern as one argument (or from a file), reads logs the way the parser
# does (shared read, lenient UTF-8, streaming) and always prints UTF-8.
#
# USAGE
#   logmine.ps1 -Pattern <regex> [-Path <file-or-dir>[;...]] [options]
#   logmine.ps1 -Templates [-Pattern <regex>] [-CollapseNames] [options]
#
# WHERE IT LOOKS (first that applies)
#   -Path                  files or directories; several separated by ';'
#   EQ2PARSER_LOG_DIRS     environment variable, ';'-separated dirs or files
#   EQ2PARSER_SAMPLE_LOG   environment variable, one log file
#   Directories are searched recursively for eq2log_*.txt.
#
# WHAT THE PATTERN SEES
#   A .NET regex, matched against the MESSAGE: the text after the
#   "(epoch)[local timestamp] " prefix. So ^ and $ anchor exactly as they do
#   in the grammar, and a pattern can be pasted from a [GeneratedRegex]
#   attribute unchanged (expand any interpolated constant first).
#   Case-sensitive; start the pattern with (?i) to ignore case.
#   -Raw matches against the whole line, prefix included.
#   -PatternFile <file> reads the pattern from the first line of a UTF-8 file,
#   for patterns that are awkward to quote.
#   Quote the pattern with single quotes. From Git Bash, a pattern that starts
#   with / is rewritten into a Windows path before this script sees it: write
#   [/] for the leading slash, or use -PatternFile. When nothing matches, the
#   pattern is echoed as received so a mangled one is visible.
#
# OPTIONS
#   -Max <n>          cap printed matches (or templates); default 40. The
#                     summary line still reports the true total.
#   -Count            print only per-file counts and the total.
#   -Context <n>      print n lines either side of each match.
#   -Message          strip the "(epoch)[timestamp] " prefix from printed lines.
#   -Templates        print the most common line TEMPLATES instead of lines:
#                     the message with each number replaced by # and each game
#                     link by <link>, with a count. This is how new line
#                     shapes are discovered.
#   -CollapseNames    with -Templates: also replace runs of capitalised words
#                     by <N> (YOU, YOUR, You, Alas, The, A, An ... are kept)
#                     and print one example line under each template.
#   -Since <date>     only lines at or after this local date/time, for example
#                     2026-08-01 or "2026-08-01 20:00". Older files are skipped.
#   -Last <n>         only the n most recently written files.
#   -Width <n>        trim printed lines to n characters (default 400, 0 = off).
#
# OUTPUT
#   <tag>:<line>: <text>     a match        (tag = file name without eq2log_/.txt)
#   <tag>-<line>- <text>     a context line
#   logmine: ...             the summary, always the last line
#   Undecodable bytes print as U+FFFD, the same as the parser's own reader.
#
# EXIT CODES
#   0 matches found, 1 no matches, 2 usage or configuration error.
#
# EXAMPLES
#   # Every line of one shape in the committed corpus, message only
#   logmine.ps1 -Pattern ' multi attacks? ' -Path tests/logs/eq2log_Menludiir.txt -Message
#
#   # How often does a shape occur, per file, in the three newest logs?
#   logmine.ps1 -Pattern ' for (a critical of )?[\d,.]+[KMB] \w+ damage\.$' -Count -Last 3
#
#   # What do "tries to" lines look like? Top 20 shapes with an example each
#   logmine.ps1 -Templates -CollapseNames -Pattern ' tries to ' -Max 20
#
#   # Zone changes since a date, with the lines around them
#   logmine.ps1 -Pattern '^You have entered ' -Since 2026-08-01 -Context 2
#
#   # Does a candidate grammar regex match what it should? Paste it anchored
#   logmine.ps1 -Pattern '^YOU (?<verb>flurry|flurries) (?<victim>.+?) for ' -Max 10
#
# The per-line loop is compiled C# (Add-Type) because a PowerShell loop over
# millions of lines takes minutes. It is written to the C# 5 subset so that
# Windows PowerShell 5.1 can compile it; keep it that way.
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Pattern,
    [string]$PatternFile,
    [string[]]$Path,
    [int]$Max = 40,
    [switch]$Count,
    [int]$Context = 0,
    [switch]$Templates,
    [switch]$CollapseNames,
    [switch]$Message,
    [switch]$Raw,
    [string]$Since,
    [int]$Last = 0,
    [int]$Width = 400
)

$ErrorActionPreference = 'Stop'

$MatchTimeoutSeconds = 2      # per line; a runaway pattern cannot hang the run
$MaxTimeouts = 5              # give up after this many timed-out lines
$TemplateCap = 200000         # distinct templates kept in memory
$TemplateWidth = 200          # template keys are cut to this many characters
$BigScanBytes = 4GB           # above this, hint at -Last / -Since

$inv = [System.Globalization.CultureInfo]::InvariantCulture

# Bump the namespace suffix whenever this C# changes: a type cannot be
# redefined inside a PowerShell session that already loaded the old one.
$scannerSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LogMine1
{
    public sealed class Hit
    {
        public long Line;
        public string Text;
        public bool IsMatch;
        public bool GapBefore;
    }

    public sealed class Scanner
    {
        // Settings.
        public Regex Pattern;       // null = every line counts (templates mode only)
        public Regex NumberRx;
        public Regex LinkRx;
        public Regex NameRx;
        public bool Raw;
        public bool Collapse;
        public long SinceEpoch;
        public int Mode;            // 0 lines, 1 count, 2 templates
        public int Max;
        public int Context;
        public int TemplateCap;
        public int TemplateWidth;
        public int MaxTimeouts;

        // Running totals across files.
        public long Total;
        public long Lines;
        public int Shown;
        public int Timeouts;
        public bool Aborted;
        public long TemplateOverflow;
        public Dictionary<string, long> Templates = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, string> Examples = new Dictionary<string, string>(StringComparer.Ordinal);

        // Result of the most recent ScanFile call.
        public long FileHits;
        public long FileLines;
        public long TimeoutLine;
        public string Error;
        public List<Hit> Hits = new List<Hit>();

        // Index of the first message character, or 0 when the line has no
        // "(epoch)[timestamp] " prefix. Same shape LogLine.TryParse accepts.
        public static int MessageStart(string line, out int closeParen)
        {
            closeParen = -1;
            int len = line.Length;
            if (len < 4 || line[0] != '(') return 0;
            int cp = line.IndexOf(')');
            if (cp < 2 || cp + 1 >= len || line[cp + 1] != '[') return 0;
            int cb = line.IndexOf(']', cp + 2);
            if (cb < 0) return 0;
            int start = cb + 1;
            while (start < len && line[start] == ' ') start++;
            closeParen = cp;
            return start;
        }

        public static int MessageStart(string line)
        {
            int ignored;
            return MessageStart(line, out ignored);
        }

        public void ScanFile(string path)
        {
            FileHits = 0;
            FileLines = 0;
            TimeoutLine = 0;
            Error = null;
            Hits = new List<Hit>();

            bool useContext = Mode == 0 && Context > 0;
            bool checkSince = SinceEpoch > 0;
            Queue<string> ring = new Queue<string>();
            int afterLeft = 0;
            long lastShown = -1;

            try
            {
                // Shared read: the game is still appending to this file.
                // Lenient UTF-8: bad bytes become U+FFFD, never an exception.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, false), true, 1 << 16))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        FileLines++;
                        int closeParen;
                        int start = MessageStart(line, out closeParen);

                        if (checkSince)
                        {
                            // Logs are chronological: skip to the first line at
                            // or after the date, then stop checking.
                            if (start == 0) continue;
                            long epoch;
                            if (!long.TryParse(line.Substring(1, closeParen - 1), NumberStyles.None,
                                    CultureInfo.InvariantCulture, out epoch))
                                continue;
                            if (epoch < SinceEpoch) continue;
                            checkSince = false;
                        }

                        if (Pattern != null)
                        {
                            bool hit;
                            try
                            {
                                hit = Raw
                                    ? Pattern.IsMatch(line)
                                    : Pattern.Match(line, start, line.Length - start).Success;
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                Timeouts++;
                                TimeoutLine = FileLines;
                                if (Timeouts >= MaxTimeouts)
                                {
                                    Aborted = true;
                                    break;
                                }
                                continue;
                            }

                            if (!hit)
                            {
                                if (useContext)
                                {
                                    if (afterLeft > 0)
                                    {
                                        Hits.Add(new Hit { Line = FileLines, Text = line });
                                        afterLeft--;
                                        lastShown = FileLines;
                                    }
                                    else
                                    {
                                        ring.Enqueue(line);
                                        if (ring.Count > Context) ring.Dequeue();
                                    }
                                }
                                continue;
                            }
                        }

                        FileHits++;
                        if (Mode == 1) continue;
                        if (Mode == 2)
                        {
                            AddTemplate(start > 0 ? line.Substring(start) : line);
                            continue;
                        }
                        if (Shown >= Max)
                        {
                            afterLeft = 0;
                            continue;
                        }

                        bool gap = false;
                        if (useContext)
                        {
                            long first = FileLines - ring.Count;
                            gap = lastShown < 0 || first > lastShown + 1;
                            foreach (string before in ring)
                            {
                                Hits.Add(new Hit { Line = first, Text = before, GapBefore = gap });
                                gap = false;
                                first++;
                            }
                            ring.Clear();
                            afterLeft = Context;
                            lastShown = FileLines;
                        }
                        Hits.Add(new Hit { Line = FileLines, Text = line, IsMatch = true, GapBefore = gap });
                        Shown++;
                    }
                }
            }
            catch (IOException ex)
            {
                Error = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                Error = ex.Message;
            }

            Lines += FileLines;
            Total += FileHits;
        }

        private void AddTemplate(string message)
        {
            string key = LinkRx.Replace(message, "<link>");
            if (Collapse) key = NameRx.Replace(key, "<N>");
            key = NumberRx.Replace(key, "#");
            if (key.Length > TemplateWidth) key = key.Substring(0, TemplateWidth);

            long seen;
            if (Templates.TryGetValue(key, out seen))
            {
                Templates[key] = seen + 1;
            }
            else if (Templates.Count < TemplateCap)
            {
                Templates[key] = 1;
                if (Collapse) Examples[key] = message;
            }
            else
            {
                TemplateOverflow++;
            }
        }

        // Most frequent first; ties in ordinal order so runs are repeatable.
        public List<KeyValuePair<string, long>> TopTemplates(int count)
        {
            List<KeyValuePair<string, long>> all = new List<KeyValuePair<string, long>>(Templates);
            all.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            if (all.Count > count) all.RemoveRange(count, all.Count - count);
            return all;
        }
    }
}
'@

function Write-Err([string]$text) {
    [Console]::Error.WriteLine('logmine: ' + $text)
}

function Stop-Usage([string]$text) {
    Write-Err $text
    exit 2
}

function Format-Text([string]$line) {
    $text = $line
    if ($Message) {
        $s = [LogMine1.Scanner]::MessageStart($line)
        if ($s -gt 0) { $text = $line.Substring($s) }
    }
    if ($Width -gt 0 -and $text.Length -gt $Width) {
        $text = $text.Substring(0, $Width) + ' ...[+' + ($text.Length - $Width) + ' chars]'
    }
    return $text
}

# Expand -Path / environment entries into a de-duplicated list of FileInfo.
function Resolve-LogFiles([string[]]$entries, [string]$origin) {
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $found = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    foreach ($entry in $entries) {
        foreach ($piece in ($entry -split ';')) {
            $p = $piece.Trim().Trim('"').Trim("'")
            if ($p.Length -eq 0) { continue }
            $items = @()
            if (Test-Path -LiteralPath $p -PathType Container) {
                $items = @(Get-ChildItem -LiteralPath $p -Recurse -File -Filter 'eq2log_*.txt' -ErrorAction SilentlyContinue)
            }
            elseif (Test-Path -LiteralPath $p -PathType Leaf) {
                $items = @(Get-Item -LiteralPath $p)
            }
            else {
                # Not a literal path: allow a wildcard such as logs\*\eq2log_A*.txt
                $items = @(Get-ChildItem -Path $p -File -ErrorAction SilentlyContinue)
                if ($items.Count -eq 0) { Write-Err ('not found (' + $origin + '): ' + $p) }
            }
            foreach ($item in $items) {
                if ($seen.Add($item.FullName)) { $found.Add($item) }
            }
        }
    }
    return , $found
}

$previousEncoding = $null
try {
    # UTF-8 out, so RU/DE/FR text and curly apostrophes print instead of
    # throwing or turning into question marks on a legacy code page.
    try {
        $previousEncoding = [Console]::OutputEncoding
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    }
    catch {
        $previousEncoding = $null
    }

    # ---- arguments ---------------------------------------------------------
    if ($PatternFile) {
        if ($Pattern) { Stop-Usage 'give -Pattern or -PatternFile, not both.' }
        if (-not (Test-Path -LiteralPath $PatternFile -PathType Leaf)) {
            Stop-Usage ('pattern file not found: ' + $PatternFile)
        }
        $fileLines = @([System.IO.File]::ReadAllLines((Get-Item -LiteralPath $PatternFile).FullName, [System.Text.Encoding]::UTF8))
        if ($fileLines.Count -gt 0) { $Pattern = $fileLines[0] }
        if (-not $Pattern) { Stop-Usage ('pattern file is empty: ' + $PatternFile) }
    }
    if (-not $Pattern -and -not $Templates) {
        Stop-Usage 'missing -Pattern <regex> (only -Templates may run without one). See the header of this script for examples.'
    }
    if ($Count -and $Templates) { Stop-Usage '-Count and -Templates are different modes; pick one.' }
    if ($CollapseNames -and -not $Templates) { Stop-Usage '-CollapseNames only applies with -Templates.' }
    if ($Max -lt 0) { Stop-Usage '-Max must be 0 or more.' }
    if ($Context -lt 0) { Stop-Usage '-Context must be 0 or more.' }
    if ($Last -lt 0) { Stop-Usage '-Last must be 0 or more.' }

    $rxOptions = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor [System.Text.RegularExpressions.RegexOptions]::Compiled
    $rx = $null
    if ($Pattern) {
        try {
            $rx = New-Object System.Text.RegularExpressions.Regex($Pattern, $rxOptions, [TimeSpan]::FromSeconds($MatchTimeoutSeconds))
        }
        catch {
            Stop-Usage ('invalid regex: ' + $_.Exception.GetBaseException().Message)
        }
    }

    $sinceEpoch = 0L
    $sinceDate = $null
    if ($Since) {
        $parsed = [datetime]::MinValue
        $styles = [System.Globalization.DateTimeStyles]::AssumeLocal
        if (-not [datetime]::TryParse($Since, $inv, $styles, [ref]$parsed)) {
            Stop-Usage ('cannot read -Since "' + $Since + '"; use a form like 2026-08-01 or "2026-08-01 20:00".')
        }
        $sinceDate = $parsed
        $sinceEpoch = (New-Object System.DateTimeOffset($parsed)).ToUnixTimeSeconds()
    }

    # ---- which files -------------------------------------------------------
    $origin = ''
    $entries = @()
    if ($Path -and $Path.Count -gt 0) {
        $origin = '-Path'
        $entries = $Path
    }
    elseif ($env:EQ2PARSER_LOG_DIRS) {
        $origin = 'EQ2PARSER_LOG_DIRS'
        $entries = @($env:EQ2PARSER_LOG_DIRS)
    }
    elseif ($env:EQ2PARSER_SAMPLE_LOG) {
        $origin = 'EQ2PARSER_SAMPLE_LOG'
        $entries = @($env:EQ2PARSER_SAMPLE_LOG)
    }
    else {
        Stop-Usage 'no logs to search. Pass -Path <file-or-dir>, or set EQ2PARSER_LOG_DIRS (semicolon-separated directories or files) or EQ2PARSER_SAMPLE_LOG (one log file).'
    }

    $files = Resolve-LogFiles $entries $origin
    if ($files.Count -eq 0) {
        Stop-Usage ('no eq2log_*.txt files found via ' + $origin + '.')
    }
    $candidates = $files.Count
    $files = @($files | Sort-Object -Property LastWriteTime, FullName)
    if ($sinceDate) {
        # A file last written before the date cannot hold a later line.
        $files = @($files | Where-Object { $_.LastWriteTime -ge $sinceDate })
    }
    if ($Last -gt 0 -and $files.Count -gt $Last) {
        $files = @($files | Select-Object -Last $Last)
    }

    $totalBytes = 0L
    foreach ($f in $files) { $totalBytes += $f.Length }
    if ($totalBytes -gt $BigScanBytes -and $Last -eq 0 -and -not $Since) {
        Write-Err ('scanning ' + $files.Count + ' files, ' + ($totalBytes / 1MB).ToString('F0', $inv) + ' MB; narrow with -Last <n> or -Since <date> if this is slow.')
    }

    # Short tags: the file name without eq2log_ and .txt; the parent folder
    # is added only when two files would otherwise share a tag.
    $tags = @{}
    $tagUse = @{}
    foreach ($f in $files) {
        $t = $f.Name -replace '^eq2log_', '' -replace '\.txt$', ''
        $tags[$f.FullName] = $t
        $tagUse[$t] = 1 + [int]$tagUse[$t]
    }
    foreach ($f in $files) {
        $t = $tags[$f.FullName]
        if ($tagUse[$t] -gt 1) { $tags[$f.FullName] = $f.Directory.Name + '/' + $t }
    }

    # ---- scan --------------------------------------------------------------
    if (-not ('LogMine1.Scanner' -as [type])) {
        try {
            Add-Type -TypeDefinition $scannerSource -Language CSharp
        }
        catch {
            Stop-Usage ('could not compile the scanner (Add-Type): ' + $_.Exception.GetBaseException().Message)
        }
    }

    # Inner apostrophes and hyphens stay inside a name (D'Morte) but a
    # possessive 's does not, so "Name's Ability" keeps its shape. The curly
    # apostrophe is built from its code point to keep this file ASCII.
    $curly = [string][char]0x2019
    $keep = '(?!(?:YOU|YOUR|YOURSELF|THEIR|You|Your|Alas|The|A|An|This)\b)'
    $word = $keep + '\p{Lu}[\p{L}\p{N}]*(?:[''' + $curly + '-](?!s\b)[\p{L}\p{N}]+)*'

    $scan = New-Object LogMine1.Scanner
    $scan.Pattern = $rx
    $scan.NumberRx = New-Object System.Text.RegularExpressions.Regex('\d+(?:[.,]\d+)*', $rxOptions)
    $scan.LinkRx = New-Object System.Text.RegularExpressions.Regex('\\a[A-Za-z]+ [^\\]*\\/a', $rxOptions)
    $scan.NameRx = New-Object System.Text.RegularExpressions.Regex(('\b' + $word + '(?: ' + $word + ')*'), $rxOptions)
    $scan.Raw = [bool]$Raw
    $scan.Collapse = [bool]$CollapseNames
    $scan.SinceEpoch = $sinceEpoch
    $scan.Mode = 0
    if ($Count) { $scan.Mode = 1 }
    if ($Templates) { $scan.Mode = 2 }
    $scan.Max = $Max
    $scan.Context = $Context
    $scan.TemplateCap = $TemplateCap
    $scan.TemplateWidth = $TemplateWidth
    $scan.MaxTimeouts = $MaxTimeouts

    $filesSearched = 0
    $filesWithHits = 0
    $unreadable = 0
    $anyPrinted = $false
    $counts = New-Object 'System.Collections.Generic.List[object]'
    $clock = [System.Diagnostics.Stopwatch]::StartNew()

    foreach ($f in $files) {
        $tag = $tags[$f.FullName]
        $scan.ScanFile($f.FullName)

        if ($scan.Error) {
            $unreadable++
            Write-Err ('could not read ' + $f.FullName + ' past line ' + $scan.FileLines + ': ' + $scan.Error)
        }
        if (-not $scan.Error -or $scan.FileLines -gt 0) { $filesSearched++ }

        foreach ($hit in $scan.Hits) {
            if ($hit.GapBefore -and $anyPrinted) { Write-Output '--' }
            $mark = '-'
            if ($hit.IsMatch) { $mark = ':' }
            Write-Output ($tag + $mark + $hit.Line + $mark + ' ' + (Format-Text $hit.Text))
            $anyPrinted = $true
        }

        if ($scan.FileHits -gt 0) {
            $filesWithHits++
            $counts.Add([pscustomobject]@{ Hits = $scan.FileHits; Tag = $tag; File = $f.FullName })
        }
        if ($scan.Aborted) {
            Write-Err ('the pattern timed out on ' + $scan.Timeouts + ' lines (' + $MatchTimeoutSeconds + ' s each), last at ' + $tag + ':' + $scan.TimeoutLine + '. It probably backtracks badly; simplify it.')
            break
        }
    }
    $clock.Stop()

    # ---- report ------------------------------------------------------------
    $shown = $scan.Shown
    if ($scan.Mode -eq 1) {
        foreach ($c in $counts) {
            Write-Output ($c.Hits.ToString($inv).PadLeft(9) + '  ' + $c.Tag + '  ' + $c.File)
        }
    }
    elseif ($scan.Mode -eq 2) {
        $ranked = $scan.TopTemplates($Max)
        foreach ($entry in $ranked) {
            Write-Output ($entry.Value.ToString($inv).PadLeft(9) + '  ' + (Format-Text $entry.Key))
            if ($CollapseNames) {
                Write-Output ('           e.g. ' + (Format-Text $scan.Examples[$entry.Key]))
            }
        }
        $shown = $ranked.Count
    }

    $what = 'matches'
    if ($scan.Mode -eq 2) { $what = 'lines in ' + $scan.Templates.Count + ' templates' }
    $summary = 'logmine: ' + $scan.Total + ' ' + $what
    if ($scan.Mode -ne 1) { $summary += ' (' + $shown + ' shown)' }
    $summary += ', ' + $filesSearched + ' files searched, ' + $filesWithHits + ' with matches, ' + $scan.Lines + ' lines, '
    $summary += ($totalBytes / 1MB).ToString('F1', $inv) + ' MB, ' + $clock.Elapsed.TotalSeconds.ToString('F1', $inv) + ' s'
    if ($candidates -gt $files.Count) { $summary += ', ' + ($candidates - $files.Count) + ' files skipped by -Since/-Last' }
    if ($scan.TemplateOverflow -gt 0) { $summary += ', ' + $scan.TemplateOverflow + ' lines beyond the ' + $TemplateCap + '-template cap (narrow with -Pattern)' }
    if ($unreadable -gt 0) { $summary += ', ' + $unreadable + ' unreadable' }
    if ($scan.Timeouts -gt 0) { $summary += ', ' + $scan.Timeouts + ' lines skipped on pattern timeout' }
    if ($scan.Aborted) { $summary += ', ABORTED early (totals are partial)' }
    if ($scan.Total -eq 0 -and $Pattern) {
        # A shell can rewrite the argument before it gets here (Git Bash turns
        # a leading / into a Windows path); show what actually arrived.
        Write-Output ('logmine: no match; pattern as received: ' + $Pattern)
    }
    Write-Output $summary

    if ($scan.Aborted) { exit 2 }
    if ($scan.Total -gt 0) { exit 0 }
    exit 1
}
finally {
    if ($previousEncoding) {
        try { [Console]::OutputEncoding = $previousEncoding } catch { }
    }
}
