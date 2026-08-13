# Pull (or push) the Radio_Web_Control_Core git subtree at core/.
#
# A plain clone already contains core/ and builds with no extra steps. Run this
# when you want the latest shared code from the core repo — not as part of
# every build. `git subtree pull --squash` creates a commit in this repo.
#
# Usage (from anywhere; the script cds to the repo root):
#   .\scripts\update-core.ps1
#   .\scripts\update-core.ps1 -Push
#
# Keep PREFIX / URL / BRANCH in sync with scripts/update-core.sh.

param(
    [switch]$Push
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Prefix     = 'core'
$RemoteName = 'radio-core'
$Url        = 'https://github.com/mm5agm/Radio_Web_Control_Core.git'
$Branch     = 'main'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

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

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git is not on PATH.'
}

$inside = & git rev-parse --is-inside-work-tree 2>$null
if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
    throw 'not a git repository.'
}

$execPath = & git --exec-path
$subtree = Get-ChildItem -LiteralPath $execPath -Filter 'git-subtree*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or -not $subtree) {
    throw 'git subtree is not available. Install a full Git distribution (not a minimal git).'
}

if (-not (Test-Path -LiteralPath $Prefix -PathType Container)) {
    throw "$Prefix/ is missing. First-time add (once, from a clean tree): git subtree add --prefix=$Prefix $Url $Branch --squash"
}

$status = & git status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw 'git status failed.'
}
if ($status) {
    Write-Host ($status -join [Environment]::NewLine)
    throw "working tree is not clean. Commit or stash before a subtree $(if ($Push) { 'push' } else { 'pull' })."
}

& git remote get-url $RemoteName *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Adding git remote $RemoteName -> $Url"
    Invoke-Checked git @('remote', 'add', $RemoteName, $Url) "git remote add $RemoteName"
}

if ($Push) {
    Write-Host "git subtree push --prefix=$Prefix $RemoteName $Branch"
    Invoke-Checked git @('subtree', 'push', '--prefix', $Prefix, $RemoteName, $Branch) 'git subtree push'
    Write-Host "Pushed core/ to $Url ($Branch)."
    return
}

Write-Host "git subtree pull --prefix=$Prefix $RemoteName $Branch --squash"
Invoke-Checked git @('fetch', $RemoteName, $Branch) "git fetch $RemoteName"
Invoke-Checked git @('subtree', 'pull', '--prefix', $Prefix, $RemoteName, $Branch, '--squash') 'git subtree pull'
Write-Host 'core/ is up to date. Review the squash commit git just created, then push this branch when ready.'
