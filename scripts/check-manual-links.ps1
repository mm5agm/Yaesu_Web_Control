# check-manual-links.ps1
# Checks USER_MANUAL.md for two defects that are invisible when you read the
# file and only show up once GitHub renders it.
#
# Usage:
#   .\scripts\check-manual-links.ps1
#   .\scripts\check-manual-links.ps1 -Path README.md
#   .\scripts\check-manual-links.ps1 -Quiet     # exit code only, for scripts
#
# Exit code is 0 when clean, 1 when anything is wrong, so finish-release.ps1
# can block on it.
#
# ---------------------------------------------------------------------------
# Why this exists
#
# Both defects it looks for shipped in the manual on 2026-08-16, one of them
# twice in the same day:
#
#   1. A link whose anchor does not match its heading. Nothing warns you --
#      the link renders normally and simply does nothing when clicked. The
#      slug rules are easy to get wrong by hand, particularly the em-dash,
#      which is dropped rather than replaced and so leaves the two hyphens
#      from the spaces that surrounded it.
#
#   2. A section that never reaches the table of contents. Section 5.20 and
#      section 11.1 were both written, both correct, and both unreachable for
#      anyone navigating from the top of a 3,000-line document.
#
# The staleness heuristic already in finish-release.ps1 can only ever warn
# that the manual *looks* neglected, because no script can judge whether prose
# is right. These two checks are different: they are objective, so this script
# is allowed to be certain about them.
# ---------------------------------------------------------------------------

param(
    [string]$Path = 'USER_MANUAL.md',

    # Print nothing; report through the exit code alone.
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Path)) {
    $Path = Join-Path $RepoRoot $Path
}
if (-not (Test-Path $Path)) { throw "No such file: $Path" }

function Write-Unless-Quiet {
    param([string]$Message, [string]$Colour = 'Gray')
    if (-not $Quiet) { Write-Host $Message -ForegroundColor $Colour }
}

# Read as UTF-8 explicitly. Get-Content would use the console codepage and turn
# the manual's em-dashes and section signs into mojibake, which would then be
# stripped differently and produce phantom mismatches.
$text  = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
$lines = $text -split "`r?`n"

# --- slugify headings the way GitHub does ----------------------------------
# Lowercase, drop everything that is not a letter, digit, space or hyphen,
# then turn spaces into hyphens. Two details matter and are easy to miss:
# entities are decoded first (so "&amp;" contributes "&", which then drops
# out, rather than the letters "amp"), and backticks are removed before the
# general strip so `code` in a heading does not shift the result.
$slugs = @()
foreach ($line in $lines) {
    if ($line -match '^#{1,6} ') {
        $t = ($line -replace '^#{1,6} ', '').ToLower()
        $t = $t -replace '&amp;', '&' -replace '&lt;', '<' -replace '&gt;', '>'
        $t = $t -replace '`', ''
        $t = $t -replace '[^a-z0-9 \-]', ''
        $slugs += ($t -replace ' ', '-')
    }
}

# --- 1. every in-page link resolves to a real heading ----------------------
$linkMatches = [regex]::Matches($text, '\]\(#([^)]+)\)')
$broken = @()
foreach ($m in $linkMatches) {
    $target = $m.Groups[1].Value
    if ($slugs -notcontains $target) { $broken += $target }
}

# --- 2. every numbered subsection appears in the contents ------------------
# The table of contents runs from the top of the file to the first horizontal
# rule. Do NOT substitute a fixed line count here: the first version of this
# check read 95 lines of a 103-line contents and confidently reported seven
# sections as missing that were listed all along.
$ruleLine = $lines | Select-String -Pattern '^---$' | Select-Object -First 1
$tocEnd   = if ($ruleLine) { $ruleLine.LineNumber } else { $lines.Count }
$toc      = ($lines | Select-Object -First $tocEnd) -join "`n"

$missing = @()
foreach ($line in $lines) {
    if ($line -match '^#{2,3} ([0-9]+\.[0-9]+)') {
        $number = $Matches[1]
        if ($toc -notmatch ('\- ' + [regex]::Escape($number) + ' \[')) { $missing += $line }
    }
}

# --- report ----------------------------------------------------------------
$name = Split-Path $Path -Leaf
Write-Unless-Quiet "$name : $($slugs.Count) headings, $($linkMatches.Count) internal links"

$failed = $false

if ($broken.Count -gt 0) {
    $failed = $true
    Write-Unless-Quiet "BROKEN LINKS ($($broken.Count)) -- these render fine and do nothing when clicked:" 'Red'
    $broken | Sort-Object -Unique | ForEach-Object { Write-Unless-Quiet "   #$_" 'Red' }
} else {
    Write-Unless-Quiet "  all internal links resolve" 'Green'
}

if ($missing.Count -gt 0) {
    $failed = $true
    Write-Unless-Quiet "NOT IN THE CONTENTS ($($missing.Count)) -- written, correct, and unreachable from the top:" 'Yellow'
    $missing | ForEach-Object { Write-Unless-Quiet "   $_" 'Yellow' }
} else {
    Write-Unless-Quiet "  every numbered subsection is listed" 'Green'
}

if ($failed) { exit 1 }
exit 0
