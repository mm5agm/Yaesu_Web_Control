# create-release.ps1
#
# PREFER scripts\finish-release.ps1. This script has NOT had the exit-code
# hardening that one got: none of its git calls check $LASTEXITCODE, so a
# develop -> main merge that conflicts is not noticed and the tag and GitHub
# release are created from an unmerged main anyway. That is not theoretical --
# it shipped the wrong code in the sibling repo (Icom Web Control) on
# 2026-08-05. It also generates release notes from git log, which produces
# developer prose under a heading users actually read.
#
# Full automated release from develop branch.
# Run with no arguments: .\scripts\create-release.ps1
# Run with explicit version: .\scripts\create-release.ps1 -Version v0.9.0-rc1
#
# What it does:
#   1. Auto-increments the patch version (e.g. v0.7.1 -> v0.7.2), or uses -Version if supplied
#   2. Commits any pending changes in develop
#   3. Updates README.md with release notes built from git log
#   4. Commits the README update in develop and pushes
#   5. Merges develop -> main and pushes
#   6. Creates the GitHub release (triggers the build workflow)

param(
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Prerequisites
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) is not installed. Download from https://cli.github.com/"
    exit 1
}

# Must be on develop
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne "develop") {
    Write-Error "Must be on the develop branch to create a release. Currently on: $branch"
    exit 1
}

# Fetch latest from origin
Write-Host "Fetching from origin..." -ForegroundColor Cyan
git fetch origin

# Compute next version tag
$latestTag = git tag --sort=-version:refname | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | Select-Object -First 1

if (-not $latestTag) {
    Write-Error "No version tags found (expected e.g. v0.7.1). Create the first release manually."
    exit 1
}

if ($Version -ne "") {
    if ($Version.StartsWith('v')) { $newTag = $Version } else { $newTag = "v$Version" }
} else {
    $parts  = $latestTag.TrimStart('v') -split '\.'
    $newTag = "v$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
}

Write-Host "Last release : $latestTag" -ForegroundColor Yellow
Write-Host "New  release : $newTag"    -ForegroundColor Green

# Build release notes from git log since last tag
$commits = git log "$latestTag..HEAD" --pretty=format:"- %s" --no-merges |
    Where-Object { $_ -notmatch 'Co-Authored-By|^\s*$' }

if (-not $commits) {
    $commits = @("- Minor fixes and improvements")
}

$commitBlock = $commits -join "`n"

# Update installer.nsi version
$newVersion = $newTag.TrimStart('v')
$installerPath = (Join-Path $PSScriptRoot "..\installer.nsi" | Resolve-Path).Path
$installerContent = [System.IO.File]::ReadAllText($installerPath)
$installerContent = $installerContent -replace '!define VERSION "[^"]*"', "!define VERSION `"$newVersion`""
[System.IO.File]::WriteAllText($installerPath, $installerContent)
Write-Host "installer.nsi version set to $newVersion" -ForegroundColor Green

# Commit any pending changes in develop
$pending = git status --porcelain
if ($pending) {
    Write-Host "Committing pending changes in develop..." -ForegroundColor Yellow
    git add .
    git commit -m "Pre-release: pending changes for $newTag"
}

# Update README.md with release notes
$today      = Get-Date -Format "yyyy-MM-dd"
$readmePath = (Join-Path $PSScriptRoot "..\README.md" | Resolve-Path).Path

$lines  = [System.IO.File]::ReadAllLines($readmePath)
$marker = "## Release Notes"
$match  = $lines | Select-String -SimpleMatch $marker | Select-Object -First 1

if ($null -eq $match) {
    Write-Warning "Could not find '$marker' in README.md - skipping README update."
}
else {
    $idx = $match.LineNumber - 1

    $newSection = @(
        "",
        "## $today - $newTag",
        "",
        "### Changed",
        "",
        $commitBlock,
        ""
    )

    $updated = $lines[0..$idx] + $newSection + $lines[($idx + 1)..($lines.Length - 1)]
    [System.IO.File]::WriteAllLines($readmePath, $updated)

    Write-Host "README.md updated." -ForegroundColor Green

    git add README.md
    git commit -m "Release notes: $newTag"
}

# Push develop
Write-Host "Pushing develop..." -ForegroundColor Cyan
git push origin develop

# Merge develop -> main
Write-Host "Merging develop into main..." -ForegroundColor Cyan
git checkout main
git pull origin main
git merge develop --no-ff -m "Release $newTag"
git push origin main

# Return to develop
git checkout develop

# Delete existing release and tag if they already exist (e.g. from a failed previous run)
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
gh release view $newTag | Out-Null
$releaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEAP
if ($releaseExists) {
    Write-Host "Deleting existing release $newTag..." -ForegroundColor Yellow
    gh release delete $newTag --yes
}
$existingTag = git tag -l $newTag
if ($existingTag) {
    Write-Host "Deleting existing tag $newTag..." -ForegroundColor Yellow
    git tag -d $newTag
    git push origin ":refs/tags/$newTag"
}

# Create GitHub release
$releaseNotes = "Release $newTag - please send bug reports to mm5agm@outlook.com"

Write-Host "Creating GitHub release $newTag..." -ForegroundColor Cyan
gh release create $newTag `
    --title $newTag `
    --notes $releaseNotes `
    --latest

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create GitHub release $newTag"
    exit 1
}

Write-Host ""
Write-Host "Release $newTag created successfully." -ForegroundColor Green
Write-Host "Build workflow running at:"            -ForegroundColor Cyan
Write-Host "  https://github.com/mm5agm/Yaesu_Web_Control/actions"  -ForegroundColor Yellow
Write-Host "  https://github.com/mm5agm/Yaesu_Web_Control/releases" -ForegroundColor Yellow
