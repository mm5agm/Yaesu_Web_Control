# check-manual-links.ps1
# Checks the operator docs for links that render fine on GitHub and then do
# nothing when clicked, and for manual sections the contents never reach.
#
# Usage:
#   .\scripts\check-manual-links.ps1                    # USER_MANUAL.md, README.md, RELEASE_HISTORY.md
#   .\scripts\check-manual-links.ps1 -Path README.md    # one file
#   .\scripts\check-manual-links.ps1 -Quiet             # exit code only, for scripts
#
# Exit code is 0 when clean, 1 when anything is wrong, so finish-release.ps1
# and CI can block on it.
#
# What it checks, in each file:
#
#   1. Every in-page link (#anchor) matches a heading, or an <a id/name> tag.
#   2. Every link to another file in the repo (USER_MANUAL.md#54-..., a
#      picture, docs/...) points at a file that exists, and its #anchor, if
#      any, matches a heading in that file.
#   3. In USER_MANUAL.md only: every numbered section (## 5. / ### 5.4) is in
#      the table of contents at the top.
#
# Links to websites are not fetched. Links inside code blocks and `inline
# code` are not links on GitHub, so they are skipped, and so are headings
# inside code blocks.
#
# Why: two defects shipped in the manual on 2026-08-16, one of them twice in
# a day. A link whose anchor didn't match its heading (an em-dash is dropped,
# not replaced, which is easy to get wrong by hand), and sections 5.20 and
# 11.1, written and correct, that the contents never reached.
#
# Rewritten 2026-11-12 to match Icom Web Control's checker, which is the same
# file: the first version checked only in-page anchors in USER_MANUAL.md.
# Added: README.md and RELEASE_HISTORY.md by default, links into other files
# and to pictures, GitHub's -1 / -2 suffix on repeated headings, underscores
# and accented letters kept in anchors as GitHub keeps them, and code skipped.
# Keep the two copies the same.

param(
    # One file to check. Without it, the three operator docs are checked.
    [string]$Path,

    # Print nothing; report through the exit code alone.
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Say {
    param([string]$Message, [string]$Colour = 'Gray')
    if (-not $Quiet) { Write-Host $Message -ForegroundColor $Colour }
}

# Read as UTF-8 explicitly. Get-Content would use the console codepage and turn
# em-dashes and section signs into mojibake, which then slug differently.
function Read-Doc([string]$File) {
    [System.IO.File]::ReadAllText($File, [System.Text.Encoding]::UTF8)
}

# The lines of a document with fenced code blocks blanked out, so neither a
# "# comment" in a PowerShell sample nor a [x](y) in a code sample counts.
function Get-ProseLines([string]$Text) {
    $out = New-Object System.Collections.Generic.List[string]
    $fence = $null
    foreach ($line in ($Text -split "`r?`n")) {
        if ($fence) {
            if ($line -match ('^\s{0,3}' + [regex]::Escape($fence))) { $fence = $null }
            $out.Add('')
            continue
        }
        if ($line -match '^\s{0,3}(```+|~~~+)') { $fence = $Matches[1]; $out.Add(''); continue }
        $out.Add($line)
    }
    return ,$out.ToArray()
}

# A heading's anchor, the way GitHub makes it: lowercase; markup and
# punctuation dropped, but letters (accented ones too), digits, spaces,
# hyphens and underscores kept; spaces to hyphens. An em-dash is dropped, not
# replaced, so "A — B" becomes "a--b". Entities are decoded first, so "&amp;"
# drops out rather than leaving "amp".
function ConvertTo-Slug([string]$Heading) {
    $t = $Heading.Trim()
    $t = $t -replace '\s+#+\s*$', ''                      # closing ###
    $t = $t -replace '!?\[([^\]]*)\]\([^)]*\)', '$1'       # [text](link) -> text
    $t = $t -replace '<[^>]+>', ''                        # inline HTML tags
    $t = $t -replace '&amp;', '&' -replace '&lt;', '<' -replace '&gt;', '>' -replace '&quot;', '"' -replace '&#39;', "'" -replace '&nbsp;', ' '
    $t = $t.ToLowerInvariant()
    $t = $t -replace '[^\p{L}\p{Mn}\p{Nd}\p{Pc} \-]', ''
    return ($t -replace ' ', '-')
}

# Every anchor a document offers: its headings, with GitHub's -1, -2 ... on a
# repeated one, and any <a id="..."> or <a name="..."> tags.
$anchorCache = @{}
function Get-Anchors([string]$File) {
    if ($anchorCache.ContainsKey($File)) { return $anchorCache[$File] }
    $set  = New-Object 'System.Collections.Generic.HashSet[string]'
    $seen = @{}
    $lines = Get-ProseLines (Read-Doc $File)
    foreach ($line in $lines) {
        if ($line -match '^\s{0,3}#{1,6}\s+(.+)$') {
            $slug = ConvertTo-Slug $Matches[1]
            if ($seen.ContainsKey($slug)) {
                $seen[$slug]++
                [void]$set.Add("$slug-$($seen[$slug])")
            } else {
                $seen[$slug] = 0
                [void]$set.Add($slug)
            }
        }
        foreach ($m in [regex]::Matches($line, '<a\s[^>]*(?:id|name)\s*=\s*"([^"]+)"', 'IgnoreCase')) {
            [void]$set.Add($m.Groups[1].Value)
        }
    }
    $anchorCache[$File] = $set
    return $set
}

function Test-Doc([string]$File) {
    # Not GetRelativePath: Windows PowerShell 5.1 runs on .NET Framework, which lacks it.
    $name  = $File.Substring($RepoRoot.Length).TrimStart('\', '/').Replace('\', '/')
    $dir   = Split-Path -Parent $File
    $lines = Get-ProseLines (Read-Doc $File)
    $own   = Get-Anchors $File
    $problems = New-Object System.Collections.Generic.List[string]
    $count = 0

    for ($i = 0; $i -lt $lines.Length; $i++) {
        # `inline code` is not a link on GitHub.
        $line = $lines[$i] -replace '`[^`]*`', ''
        # [text](target) and [text](target "title"), images included.
        foreach ($m in [regex]::Matches($line, '\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)')) {
            $target = $m.Groups[1].Value
            if ($target -match '^[a-z][a-z0-9+.-]*:' ) { continue }   # http:, https:, mailto:
            $count++
            $where = "line $($i + 1)"

            $hash = $target.IndexOf('#')
            $pathPart   = if ($hash -ge 0) { $target.Substring(0, $hash) } else { $target }
            $anchorPart = if ($hash -ge 0) { $target.Substring($hash + 1) } else { '' }
            $pathPart   = [uri]::UnescapeDataString($pathPart)

            if ($pathPart -eq '') {
                if (-not $own.Contains($anchorPart)) { $problems.Add("$where : #$anchorPart -- no such heading") }
                continue
            }

            $full = if ($pathPart.StartsWith('/')) { Join-Path $RepoRoot $pathPart.TrimStart('/') } else { Join-Path $dir $pathPart }
            $full = [System.IO.Path]::GetFullPath($full)
            if (-not (Test-Path -LiteralPath $full)) {
                $problems.Add("$where : $pathPart -- no such file")
                continue
            }
            if ($anchorPart -ne '' -and $full -match '\.md$') {
                if (-not (Get-Anchors $full).Contains($anchorPart)) {
                    $problems.Add("$where : $pathPart#$anchorPart -- no such heading in $pathPart")
                }
            }
        }
    }

    # The manual's numbered sections all in its contents. The contents run
    # from the top to the first horizontal rule; a fixed line count once
    # reported seven listed sections as missing in YWC's manual.
    $missing = New-Object System.Collections.Generic.List[string]
    if ((Split-Path -Leaf $File) -eq 'USER_MANUAL.md') {
        $rule  = [array]::IndexOf($lines, '---')
        $toc   = ($lines[0..([Math]::Max($rule, 0))]) -join "`n"
        # Listed means the contents link to the heading's own anchor, whatever
        # the list looks like ("5. [Main](#5-...)" or "- 5.4 [Spectrum](#54-...)").
        foreach ($line in $lines) {
            if ($line -match '^#{2,3} ([0-9]+(?:\.[0-9]+)?\.? .+)$') {
                $slug = ConvertTo-Slug $Matches[1]
                if (-not $toc.Contains("](#$slug)")) { $missing.Add($line) }
            }
        }
    }

    Say "$name : $($own.Count) anchors, $count links to check"
    if ($problems.Count -gt 0) {
        Say "  BROKEN LINKS ($($problems.Count)) -- these render fine and do nothing when clicked:" 'Red'
        $problems | ForEach-Object { Say "    $_" 'Red' }
    } else {
        Say "  all links resolve" 'Green'
    }
    if ($missing.Count -gt 0) {
        Say "  NOT IN THE CONTENTS ($($missing.Count)) -- written, and unreachable from the top:" 'Yellow'
        $missing | ForEach-Object { Say "    $_" 'Yellow' }
    } elseif ((Split-Path -Leaf $File) -eq 'USER_MANUAL.md') {
        Say "  every numbered section is in the contents" 'Green'
    }
    return ($problems.Count + $missing.Count) -eq 0
}

$files = if ($Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { @($Path) } else { @(Join-Path $RepoRoot $Path) }
} else {
    @('USER_MANUAL.md', 'README.md', 'RELEASE_HISTORY.md') | ForEach-Object { Join-Path $RepoRoot $_ }
}

$ok = $true
foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath $f)) { throw "No such file: $f" }
    if (-not (Test-Doc $f)) { $ok = $false }
}
if ($ok) { exit 0 } else { exit 1 }
