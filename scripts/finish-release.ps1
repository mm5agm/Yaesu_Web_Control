# finish-release.ps1
# Completes a release where the develop work is done but the tag and GitHub release
# were not created. Run from the develop branch.
#
# Usage:
#   Production release: .\scripts\finish-release.ps1 -Version v2.5.0
#   Pre-release:        .\scripts\finish-release.ps1 -Version v2.5.0-pre1 -PreRelease
#
# Documentation (see the Release Process section of CLAUDE.md):
#   The script rewrites the version strings in README.md and USER_MANUAL.md to
#   match the release, and refuses to release at all if README.md has no
#   release-notes entry for it. It will not write those notes for you -- see
#   the long comment at the preflight for why. -NoDocUpdate leaves the docs
#   alone; -SkipVersionCheck skips the lot.
#
#   scripts\bump-version.ps1 still does the up-front bump of AppVersion.cs,
#   installer.nsi and the README badge. This script is the safety net that
#   checks it actually happened, across every version site including the ones
#   bump-version.ps1 does not touch.
#
# A pre-release differs in three ways:
#   1. develop is NOT merged into main (pre-releases live on the develop line only).
#   2. The tag is created on develop, not main.
#   3. The GitHub release is created with --prerelease (not --latest), so YWC's
#      auto-updater -- which polls /releases/latest -- ignores it. Only users
#      who follow the direct link see the build.
#
# ---------------------------------------------------------------------------
# Why this script checks exit codes on every git call
#
# Because the version of this script without those checks published the wrong
# code. It happened in the sibling repo (Icom Web Control) on 2026-08-05, to
# this script's twin. The develop -> main merge hit a conflict in README.md
# (both branches had edited the release badge). Git stopped with a conflicted
# index and a non-zero exit code, and the script -- which had no way to notice
# -- carried straight on: it tagged $Version on UNMERGED main and published a
# GitHub release from it. That release was months-old code wearing a current
# version label, and the build workflow had already started packaging it into
# an installer for the public download link.
#
# $ErrorActionPreference = 'Stop' does NOT catch this. It governs PowerShell
# errors; a native executable returning non-zero is not one. The only thing that
# sees a failed git command is $LASTEXITCODE, so every git and gh call in this
# script goes through Invoke-Checked below.
#
# NOTE: scripts\create-release.ps1 is a second, older path to the same place and
# has NOT had this treatment. Prefer this script.
# ---------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$PreRelease,

    # Skip the preflight that checks the version in the source files and the
    # README release notes. Use only when you know why they disagree.
    [switch]$SkipVersionCheck,

    # By default the script rewrites the version strings in README.md and
    # USER_MANUAL.md to match the release. Pass this to leave the docs exactly
    # as they are and be told about mismatches instead.
    [switch]$NoDocUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Repo root, so the script works from anywhere.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# --- helpers ---------------------------------------------------------------

# Run a native command and throw if it fails. Output is streamed to the console
# as normal so the transcript still reads like the old script.
function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$What
    )
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (exit $LASTEXITCODE). Command: $Exe $($Arguments -join ' ')"
    }
}

# Run a git command for its output. Throws on failure.
function Get-GitOutput {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $out = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed (exit $LASTEXITCODE)."
    }
    return $out
}

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Warn { param([string]$Text) Write-Host $Text -ForegroundColor DarkYellow }

# --- preflight -------------------------------------------------------------

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is not on PATH."
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is not installed. Download from https://cli.github.com/"
}

# gh being installed is not the same as gh being logged in. Check now rather
# than at the last step, when main has already been merged, pushed and tagged.
& gh auth status
if ($LASTEXITCODE -ne 0) {
    throw "gh is not authenticated. Run: gh auth login"
}

if (-not $Version.StartsWith('v')) { $Version = "v$Version" }

# "v2.5.0-pre1" -> "2.5.0". The source files carry the numeric core only; the
# tag carries any pre-release label.
$VersionCore = ($Version.TrimStart('v') -split '-')[0]
if ($VersionCore -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' does not contain a three-part numeric version (got '$VersionCore')."
}

$branch = Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD')
if ($branch -ne 'develop') {
    throw "Must be on the develop branch. Currently on: $branch"
}

# A conflicted or half-finished merge from a previous run must not be built on.
$unmerged = Get-GitOutput @('diff', '--name-only', '--diff-filter=U')
if ($unmerged) {
    throw "The working tree has unresolved merge conflicts:`n  $($unmerged -join "`n  ")`nResolve them and commit before releasing."
}

Write-Step "Fetching from origin..."
Invoke-Checked -Exe 'git' -Arguments @('fetch', 'origin', '--tags', '--prune') -What 'git fetch'

# A tag that already exists is the other way this script can publish the wrong
# commit: `git tag` fails, and if nobody checks, `gh release create` happily
# builds a release from wherever the OLD tag points.
$existingLocal = Get-GitOutput @('tag', '--list', $Version)
if ($existingLocal) {
    throw "Tag $Version already exists locally (points at $(Get-GitOutput @('rev-parse', '--short', $Version))).`nTo redo the release: gh release delete $Version --yes --cleanup-tag"
}
$existingRemote = Get-GitOutput @('ls-remote', '--tags', 'origin', $Version)
if ($existingRemote) {
    throw "Tag $Version already exists on origin.`nTo redo the release: gh release delete $Version --yes --cleanup-tag"
}

# ---------------------------------------------------------------------------
# Version consistency, and the documentation rules
#
# Every version site has to say the same thing, or the installer, the About
# page, the manual and the tag disagree about what this build is.
# Yaesu_Web_Control.csproj is in the list because nothing was checking it:
# bump-version.ps1 does not touch it and the Release Process section of
# CLAUDE.md does not mention it, so its Version/FileVersion/AssemblyVersion
# drifted a long way behind AppVersion.cs.
#
# What this script WILL fix for you (unless -NoDocUpdate): every one of these
# is a deterministic string substitution with exactly one right answer.
# What it will NOT do is write your release notes. A script can tell that the
# v2.5.0 section is missing; it cannot tell an operator which of their problems
# this build fixes, and generating something from commit subjects would produce
# developer prose under a heading that users actually read. (create-release.ps1
# does generate notes from git log -- that is one of the reasons to prefer this
# script.) It refuses and tells you what to add, which is the honest failure.
# ---------------------------------------------------------------------------

# Read/write helper that leaves the file's encoding alone. These docs are
# UTF-8 with no BOM and CRLF throughout; targeted regex replacement on the raw
# text preserves the line endings, and this preserves the missing BOM.
function Set-FileTextPreservingEncoding {
    param([string]$Path, [string]$Text)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

if (-not $SkipVersionCheck) {
    # --- the part a script must not invent, checked FIRST ----------------
    # README release notes are mandatory, pre-releases included -- YWC's README
    # has always carried a heading per pre-release, and that is the only place
    # a tester can read what they are being asked to try.
    #
    # This runs before the doc rewriting below so that a release refused for
    # missing notes leaves the working tree exactly as it found it.
    #
    # YWC headings are date-first: "## 2026-08-01 - v2.4.2", and for a
    # pre-release "## 2026-07-31 - v2.4.2-pre24 (pre-release)". So the version
    # is matched anywhere on the heading line, not just after the hashes.
    #
    # A full release looks for the bare core version; the trailing lookahead is
    # what stops "v2.4.2" being satisfied by an existing "v2.4.2-pre24" heading.
    # A pre-release looks for its own full tag.
    $notesVersion = $Version.TrimStart('v')
    $readme = [System.IO.File]::ReadAllText((Join-Path $RepoRoot 'README.md'))
    $headingRe = "(?m)^#{2,4}\s+.*\bv$([regex]::Escape($notesVersion))(?![\w.\-])"
    $heading = [regex]::Match($readme, $headingRe)
    if (-not $heading.Success) {
        $suffix = ''
        if ($PreRelease) { $suffix = ' (pre-release)' }
        throw @"
README.md has no release-notes entry for v$notesVersion.

Add one under '## Release Notes', above the previous version:

    ## $(Get-Date -Format 'yyyy-MM-dd') - v$notesVersion$suffix

    <what changed, written for an operator: what was wrong, what they will
     notice, and anything they must do differently>

This is not auto-generated on purpose -- commit subjects describe the code,
and these notes are the only thing most users ever read about a release.
Re-run with -SkipVersionCheck to release without them.
"@
    }

    # A heading with nothing under it is the same failure wearing a hat.
    $afterHeading = $readme.Substring($heading.Index + $heading.Length)
    $nextHeading = [regex]::Match($afterHeading, "(?m)^#{1,4}\s")
    $body = $afterHeading
    if ($nextHeading.Success) { $body = $afterHeading.Substring(0, $nextHeading.Index) }
    if ($body.Trim().Length -lt 40) {
        throw "README.md has a v$notesVersion heading but essentially nothing under it. Write the notes, or re-run with -SkipVersionCheck."
    }

    # --- version strings, rewritten in place -----------------------------
    # Pattern must capture the version in group 1. Fixable = the script can
    # rewrite it; the rest are code and get bumped by hand (or by
    # bump-version.ps1), deliberately, so a release cannot happen without
    # someone having looked at them.
    $versionSites = @(
        @{ File = 'Models/AppVersion.cs';       Pattern = 'Current\s*=\s*"([^"]+)"';                    Fixable = $false },
        @{ File = 'installer.nsi';              Pattern = '!define\s+VERSION\s+"([^"]+)"';              Fixable = $false },
        @{ File = 'Yaesu_Web_Control.csproj';   Pattern = '<Version>([^<]+)</Version>';                 Fixable = $false },
        @{ File = 'Yaesu_Web_Control.csproj';   Pattern = '<FileVersion>([^<]+)</FileVersion>';         Fixable = $false },
        @{ File = 'Yaesu_Web_Control.csproj';   Pattern = '<AssemblyVersion>([^<]+)</AssemblyVersion>'; Fixable = $false },

        # Documentation. Auto-fixable, pure version strings.
        # USER_MANUAL section 5 shows the version in the top bar.
        # Note: keep this file pure ASCII. PowerShell 5.1 reads a BOM-less
        # UTF-8 .ps1 as cp1252, so a literal em dash or smart quote in a
        # pattern here arrives as several characters and the pattern silently
        # stops matching. Use \uXXXX escapes if one is ever needed.
        @{ File = 'USER_MANUAL.md'; Pattern = 'Yaesu Web Control v([0-9]+\.[0-9]+\.[0-9]+)'; Fixable = $true }
    )

    # The README badge tracks FULL releases only -- bumping it during a
    # pre-release would advertise a version that is not out yet. This is the
    # same badge bump-version.ps1 makes; having it here too means a release run
    # after a hand-edited README still lands correctly.
    if (-not $PreRelease) {
        $versionSites += @{ File = 'README.md'; Pattern = 'Latest%20release-v([0-9]+\.[0-9]+\.[0-9]+)-'; Fixable = $true }
    }

    $mismatches = @()
    $fixed = @()
    foreach ($site in $versionSites) {
        $path = Join-Path $RepoRoot $site.File
        if (-not (Test-Path $path)) {
            $mismatches += "$($site.File) -- file not found"
            continue
        }
        $text = [System.IO.File]::ReadAllText($path)
        $m = [regex]::Match($text, $site.Pattern)
        if (-not $m.Success) {
            # A doc pattern that no longer matches means the wording was
            # reworded, not that the version is wrong. Say which, rather than
            # blocking the release over a sentence someone rephrased.
            if ($site.Fixable) {
                Write-Warn "$($site.File): could not find the version in '$($site.Pattern)' -- wording changed? Check it by hand."
            }
            else {
                $mismatches += "$($site.File) -- no match for $($site.Pattern)"
            }
            continue
        }
        $found = $m.Groups[1].Value
        # AssemblyVersion/FileVersion are four-part (2.5.0.0); compare the stem.
        if ($found -eq $VersionCore -or $found -eq "$VersionCore.0") { continue }

        if ($site.Fixable -and -not $NoDocUpdate) {
            # Replace only the captured version, inside this one match, so the
            # surrounding sentence is untouched and other versions on the page
            # (the older release-notes headings) are left alone.
            $g = $m.Groups[1]
            $text = $text.Substring(0, $g.Index) + $VersionCore + $text.Substring($g.Index + $g.Length)
            Set-FileTextPreservingEncoding -Path $path -Text $text
            $fixed += "$($site.File): '$found' -> '$VersionCore'"
        }
        else {
            $mismatches += "$($site.File) says '$found', expected '$VersionCore'"
        }
    }

    if ($fixed.Count -gt 0) {
        Write-Host "Updated the version in the documentation:" -ForegroundColor Green
        $fixed | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
        Write-Host "  (these are staged with the rest of the pending changes below)" -ForegroundColor DarkGray
    }
    if ($mismatches.Count -gt 0) {
        throw "Version bump incomplete:`n  $($mismatches -join "`n  ")`nRun scripts\bump-version.ps1 -Version $VersionCore, fix the rest by hand, or re-run with -SkipVersionCheck."
    }
    Write-Host "Version $VersionCore is consistent across the source, the installer and the docs." -ForegroundColor Green

    # --- staleness heuristic, advisory only ------------------------------
    # The manual is supposed to be brought in line with whatever the release
    # touched. No script can judge that, but it can notice that user-facing
    # code changed while the manual did not -- which is the usual way the
    # manual quietly drifts out of date.
    $prevTag = & git describe --tags --abbrev=0 2>$null
    if ($LASTEXITCODE -eq 0 -and $prevTag) {
        $touched = & git diff --name-only "$prevTag..HEAD"
        if ($LASTEXITCODE -eq 0 -and $touched) {
            # @() around every pipeline result: a Where-Object that matches one
            # item returns a bare string, and under Set-StrictMode asking a
            # string for .Count is a terminating error.
            $userFacing = @($touched | Where-Object { $_ -match '^(Pages/|wwwroot/|Grammars/)' })
            $manualTouched = @($touched | Where-Object { $_ -eq 'USER_MANUAL.md' })
            if ($userFacing.Count -gt 0 -and $manualTouched.Count -eq 0) {
                Write-Warn ""
                Write-Warn "USER_MANUAL.md has not changed since $prevTag, but these user-facing files have:"
                $userFacing | Select-Object -First 12 | ForEach-Object { Write-Warn "  $_" }
                if ($userFacing.Count -gt 12) { Write-Warn "  ... and $($userFacing.Count - 12) more" }
                Write-Warn "Bring every section the release touches in line, and re-capture any screenshot"
                Write-Warn "the change makes wrong. This is a warning, not a block -- plenty of changes need no manual edit."
                Write-Warn ""
            }
        }
    }

    # --- manual navigation, a hard check ---------------------------------
    # Unlike the staleness heuristic above, this one blocks. Broken anchors
    # and sections missing from the contents are objective defects, not
    # judgements about prose, so there is no reason to ship one. Both got
    # into the manual on 2026-08-16 and neither was visible while reading
    # the file -- a broken anchor renders as a normal link and just does
    # nothing. -SkipVersionCheck bypasses this along with the rest.
    & (Join-Path $PSScriptRoot 'check-manual-links.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "USER_MANUAL.md has broken links or sections missing from the contents (listed above). Fix them, or re-run with -SkipVersionCheck."
    }
}

# --- 1. commit pending work on develop -------------------------------------

# Use `git add -u` (not `git add .`) so we only stage modifications to ALREADY
# tracked files. This stops the script accidentally committing untracked
# build artefacts like Yaesu_Web_Control_Setup.exe (which the .gitignore
# correctly excludes but `git add .` would override).
# If you genuinely want new files in the release, stage them manually before
# running this script.
$pending = Get-GitOutput @('status', '--porcelain', '--untracked-files=no')
if ($pending) {
    Write-Host "About to commit these pending tracked changes on develop:" -ForegroundColor Yellow
    $pending | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    $label = 'Release'
    if ($PreRelease) { $label = 'Pre-release' }
    Invoke-Checked -Exe 'git' -Arguments @('add', '-u') -What 'git add -u'
    Invoke-Checked -Exe 'git' -Arguments @('commit', '-m', "${label}: pending changes for $Version") -What 'git commit'
}

# Warn if untracked files are present but do not stage them.
$untracked = Get-GitOutput @('ls-files', '--others', '--exclude-standard')
if ($untracked) {
    Write-Warn "Note: ignoring untracked files (not committed):"
    $untracked | ForEach-Object { Write-Warn "  $_" }
}

# --- 2. push develop -------------------------------------------------------

Write-Step "Pushing develop..."
Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', 'develop') -What 'git push origin develop'

$developSha = Get-GitOutput @('rev-parse', 'develop')

# --- 3. tag, and release ---------------------------------------------------

if ($PreRelease) {
    # Pre-release: tag from develop, no merge into main.
    $tagTarget = $developSha
    Write-Step "Creating tag $Version on develop (pre-release path -- no main merge)..."
}
else {
    # Production release: merge develop into main, tag main.
    Write-Step "Merging develop into main..."
    Invoke-Checked -Exe 'git' -Arguments @('checkout', 'main') -What 'git checkout main'

    try {
        # --ff-only: main should never carry commits of its own. If it does,
        # stop rather than opening an editor for a merge nobody expected.
        Invoke-Checked -Exe 'git' -Arguments @('pull', '--ff-only', 'origin', 'main') -What 'git pull --ff-only origin main'

        & git merge develop --no-ff -m "Release $Version"
        if ($LASTEXITCODE -ne 0) {
            $conflicts = & git diff --name-only --diff-filter=U
            $detail = "git merge exited $LASTEXITCODE."
            if ($conflicts) {
                $detail = "Merge conflicts in:`n  $($conflicts -join "`n  ")"
            }
            throw @"
$detail

NOTHING HAS BEEN TAGGED OR RELEASED. You are on main with a half-finished merge.
To finish by hand:
  1. Resolve the conflicts (usually 'git checkout --theirs <file>' to take develop's side).
  2. git add <file> ; git commit --no-edit
  3. Confirm main matches develop:  git diff develop --stat   (must be empty)
  4. git push origin main
  5. Re-run this script -- the merge will then be a no-op.
To abandon instead: git merge --abort ; git checkout develop
"@
        }

        # The check that would have caught the accident described at the top of
        # this file. After a clean merge, main's tree must be identical to
        # develop's -- if it is not, something was dropped and the tag would
        # ship the wrong code.
        $treeDiff = Get-GitOutput @('diff', 'develop', '--stat')
        if ($treeDiff) {
            throw "main does not match develop after the merge:`n$($treeDiff -join "`n")`nNothing has been tagged. Investigate before releasing."
        }

        Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', 'main') -What 'git push origin main'
        $tagTarget = Get-GitOutput @('rev-parse', 'main')
    }
    finally {
        # Come back to develop so the working copy is never left on main by
        # accident -- being left on main is also how the editor ends up showing
        # a branch's worth of unexpected file states.
        #
        # A conflicted merge pins us to main: git refuses to switch branches
        # with an unresolved index, and it is right to. Don't attempt it and
        # don't bury the real error under git's complaint about it -- the
        # message thrown above already explains how to finish or abandon.
        $stillConflicted = & git diff --name-only --diff-filter=U
        if ($stillConflicted) {
            Write-Warn "Left on main with the conflicted merge in place -- see the instructions above."
        }
        else {
            & git checkout develop
        }
    }

    Write-Step "Creating tag $Version on main ($(Get-GitOutput @('rev-parse', '--short', $tagTarget)))..."
}

# Tag the SHA that was actually verified above, not a branch name -- a branch
# name is what let the old script tag an unmerged main.
Invoke-Checked -Exe 'git' -Arguments @('tag', $Version, $tagTarget) -What "git tag $Version"
Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', $Version) -What "git push origin $Version"

$pushedTag = Get-GitOutput @('ls-remote', '--tags', 'origin', $Version)
if (-not $pushedTag) {
    throw "Tag $Version is not on origin after pushing it. Stopping before the release is created."
}

if ($PreRelease) {
    Write-Step "Creating GitHub pre-release $Version..."
    $notesBody = @"
**Pre-release for testing -- not a public build.**

Do not install unless you're prepared for bugs and will report them on GitHub. YWC's in-app auto-updater ignores pre-releases, so existing users will not be prompted to upgrade to this build.

Please send feedback via GitHub Issues (or mm5agm@outlook.com). Mention the ``$Version`` tag when reporting.
"@
    $ghArgs = @('release', 'create', $Version, '--title', $Version, '--notes', $notesBody, '--prerelease')
}
else {
    Write-Step "Creating GitHub release $Version..."
    $ghArgs = @('release', 'create', $Version, '--title', $Version, '--notes', "Release $Version - see README.md for the full release notes. Please send bug reports to mm5agm@outlook.com", '--latest')
}

# The build workflow triggers on release:[created], not on the tag push, so this
# step is what actually produces the installer. If it fails, the tag exists but
# no build ever runs -- say so plainly rather than reporting success.
& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    throw @"
gh release create failed (exit $LASTEXITCODE), but tag $Version IS pushed.
No build has run -- the workflow triggers on release creation, not on the tag.
Either retry the release:
  gh release create $Version --title $Version --notes "Release $Version" $(if ($PreRelease) { '--prerelease' } else { '--latest' })
or remove the tag and start over:
  git tag -d $Version ; git push origin :refs/tags/$Version
"@
}

Write-Host ""
$kind = 'Release'
if ($PreRelease) { $kind = 'Pre-release' }
Write-Host "$kind $Version created successfully at $(Get-GitOutput @('rev-parse', '--short', $tagTarget))." -ForegroundColor Green
Write-Host "The installer is built by the workflow, not by this script -- it is not downloadable until that finishes." -ForegroundColor Yellow
Write-Host "Watch it:       gh run watch" -ForegroundColor Yellow
Write-Host "Build workflow: https://github.com/mm5agm/Yaesu_Web_Control/actions" -ForegroundColor Yellow
Write-Host "Releases:       https://github.com/mm5agm/Yaesu_Web_Control/releases" -ForegroundColor Yellow
