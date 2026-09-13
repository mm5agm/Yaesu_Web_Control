# check-manual-links.ps1
# Checks the operator book under user-manual/ for defects that are invisible
# when you read a chapter and only show up once the generated site is clicked.
#
# Usage:
#   .\scripts\check-manual-links.ps1
#   .\scripts\check-manual-links.ps1 -Quiet     # exit code only, for scripts
#
# Exit code is 0 when clean, 1 when anything is wrong, so finish-release.ps1
# can block on it.
#
# ---------------------------------------------------------------------------
# Why this exists
#
# Both defects it looks for shipped in the (then single-file) manual on
# 2026-08-16, one of them twice in the same day:
#
#   1. A link whose anchor does not match its heading. Nothing warns you --
#      the link renders normally and simply does nothing when clicked.
#
#   2. A section that never reaches the table of contents. After the split
#      into a NuStreamDocs book, the equivalent is a chapter file that is
#      not listed in mkdocs.yml nav -- written, correct, and unreachable
#      from the sidebar.
# ---------------------------------------------------------------------------

param(
    # Print nothing; report through the exit code alone.
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ManualDir = Join-Path $RepoRoot 'user-manual'
$Mkdocs = Join-Path $RepoRoot 'mkdocs.yml'

if (-not (Test-Path $ManualDir)) { throw "No such folder: $ManualDir" }
if (-not (Test-Path $Mkdocs)) { throw "No such file: $Mkdocs" }

function Write-Unless-Quiet {
    param([string]$Message, [string]$Colour = 'Gray')
    if (-not $Quiet) { Write-Host $Message -ForegroundColor $Colour }
}

function Get-GitHubSlug([string]$heading) {
    $t = $heading.ToLowerInvariant()
    $t = $t -replace '&amp;', '&' -replace '&lt;', '<' -replace '&gt;', '>'
    $t = $t -replace '`', ''
    $t = $t -replace '[^a-z0-9 \-]', ''
    return (($t -replace ' ', '-').Trim('-'))
}

$files = @(Get-ChildItem -Path $ManualDir -Filter '*.md' -File | Where-Object { $_.Name -ne '404.md' })
$slugsByFile = @{}
foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    $lines = $text -split "`r?`n"
    $slugs = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line -match '^#{1,6} ') {
            $slugs.Add((Get-GitHubSlug ($line -replace '^#{1,6} ', '')))
        }
    }
    $slugsByFile[$f.Name] = $slugs
}

$broken = New-Object System.Collections.Generic.List[string]
$missingImages = New-Object System.Collections.Generic.List[string]
$linkCount = 0

foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($text, '\]\(([^)]+)\)')) {
        $target = $m.Groups[1].Value
        if ($target -match '^(https?:|mailto:|/)') { continue }
        $linkCount++
        $pathPart = $target
        $anchor = $null
        if ($target.Contains('#')) {
            $hash = $target.IndexOf('#')
            $pathPart = $target.Substring(0, $hash)
            $anchor = $target.Substring($hash + 1)
        }

        if ([string]::IsNullOrEmpty($pathPart)) {
            $page = $f.Name
        }
        else {
            if ($pathPart -match '\.md$') {
                $resolved = [System.IO.Path]::GetFullPath((Join-Path $f.DirectoryName ($pathPart -replace '/', [IO.Path]::DirectorySeparatorChar)))
                if (-not (Test-Path $resolved)) {
                    $broken.Add("$($f.Name) -> $target (missing file)")
                    continue
                }
                $page = [System.IO.Path]::GetFileName($resolved)
            }
            elseif ($pathPart -match '\.(png|jpe?g|gif|webp|svg)$') {
                $decoded = [Uri]::UnescapeDataString($pathPart)
                $resolved = [System.IO.Path]::GetFullPath((Join-Path $f.DirectoryName ($decoded -replace '/', [IO.Path]::DirectorySeparatorChar)))
                if (-not (Test-Path $resolved)) {
                    $missingImages.Add("$($f.Name) -> $target")
                }
                continue
            }
            else {
                continue
            }
        }

        if ($anchor) {
            $slugs = $slugsByFile[$page]
            if ($null -eq $slugs -or $slugs -notcontains $anchor) {
                $broken.Add("$($f.Name) -> #$anchor (in $page)")
            }
        }
    }
}

$mkdocsText = [System.IO.File]::ReadAllText($Mkdocs, [System.Text.Encoding]::UTF8)
$navFiles = [regex]::Matches($mkdocsText, ':\s*([A-Za-z0-9._\-]+\.md)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$orphan = @()
foreach ($f in $files) {
    if ($navFiles -notcontains $f.Name) { $orphan += $f.Name }
}
$navMissing = @()
foreach ($n in $navFiles) {
    if (-not (Test-Path (Join-Path $ManualDir $n))) { $navMissing += $n }
}

Write-Unless-Quiet "user-manual : $($files.Count) pages, $linkCount internal links, $($navFiles.Count) nav entries"

$failed = $false

if ($broken.Count -gt 0) {
    $failed = $true
    Write-Unless-Quiet "BROKEN LINKS ($($broken.Count)) -- these render fine and do nothing when clicked:" 'Red'
    $broken | Sort-Object -Unique | ForEach-Object { Write-Unless-Quiet "   $_" 'Red' }
} else {
    Write-Unless-Quiet "  all internal links resolve" 'Green'
}

if ($missingImages.Count -gt 0) {
    $failed = $true
    Write-Unless-Quiet "MISSING IMAGES ($($missingImages.Count)):" 'Red'
    $missingImages | Sort-Object -Unique | ForEach-Object { Write-Unless-Quiet "   $_" 'Red' }
} else {
    Write-Unless-Quiet "  all picture links resolve" 'Green'
}

if ($orphan.Count -gt 0 -or $navMissing.Count -gt 0) {
    $failed = $true
    if ($orphan.Count -gt 0) {
        Write-Unless-Quiet "NOT IN mkdocs.yml NAV ($($orphan.Count)) -- written and unreachable from the sidebar:" 'Yellow'
        $orphan | ForEach-Object { Write-Unless-Quiet "   $_" 'Yellow' }
    }
    if ($navMissing.Count -gt 0) {
        Write-Unless-Quiet "NAV POINTS AT MISSING FILES ($($navMissing.Count)):" 'Red'
        $navMissing | ForEach-Object { Write-Unless-Quiet "   $_" 'Red' }
    }
} else {
    Write-Unless-Quiet "  every chapter file is listed in mkdocs.yml" 'Green'
}

if ($failed) { exit 1 }
exit 0
