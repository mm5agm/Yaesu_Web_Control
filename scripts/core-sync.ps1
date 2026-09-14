<#
.SYNOPSIS
    Keeps this app's core/ subtree in step with Radio_Web_Control_Core.

.DESCRIPTION
    Shared, radio-agnostic code lives in Radio_Web_Control_Core and is consumed
    here as a git subtree at core/. Work is authored inside core/ in whichever
    app you happen to be in, and has to be pushed up afterwards or it exists in
    exactly one branch of one repo with no second copy.

    That push step is the one that gets forgotten. This script exists so it is
    one command rather than four, and so -Check can answer "is anything owed?"
    without anyone having to remember the question.

.PARAMETER Check
    Report whether core/ here and Radio_Web_Control_Core main have diverged.
    Read-only: fetches, splits to a temp branch, compares, cleans up.

.PARAMETER Pull
    Bring Radio_Web_Control_Core main down into core/.

.PARAMETER Push
    Send local core/ commits up to Radio_Web_Control_Core main. Pulls first,
    because a push that does not fast-forward is rejected.

.EXAMPLE
    ./scripts/core-sync.ps1 -Check
    ./scripts/core-sync.ps1 -Push

.NOTES
    git subtree split walks the whole history of this repo - over a thousand
    commits - and prints nothing for a couple of minutes. It is not hung.

    Deliberately no 2>$null anywhere: in Windows PowerShell 5.1 redirecting a
    native command's stderr wraps each line in an ErrorRecord and trips
    $ErrorActionPreference, so git writing a harmless notice to stderr would
    kill the script. Exit codes are checked instead.
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(ParameterSetName = 'Check')][switch]$Check,
    [Parameter(ParameterSetName = 'Pull')][switch]$Pull,
    [Parameter(ParameterSetName = 'Push')][switch]$Push
)

$CoreUrl = 'https://github.com/mm5agm/Radio_Web_Control_Core.git'
$Prefix  = 'core'
$Tmp     = 'tmp/core-sync-split'

function Fail([string]$m) { Write-Host $m -ForegroundColor Red; exit 1 }

$repoRoot = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { Fail 'Not inside a git repository.' }
Set-Location $repoRoot

function Remove-Split {
    git show-ref --verify --quiet "refs/heads/$Tmp"
    if ($LASTEXITCODE -eq 0) { git branch -D $Tmp | Out-Null }
}

function Split-Core {
    Remove-Split
    Write-Host 'Splitting core/ out of the history. A couple of minutes, no output. Not hung.' -ForegroundColor DarkGray
    # subtree split prints the resulting commit id on stdout even under -q.
    # Left unswallowed it becomes part of this function's return value and the
    # caller ends up with two hashes in one string.
    git subtree split --prefix=$Prefix -b $Tmp -q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'git subtree split failed.' }
    $tip = git rev-parse $Tmp
    if ($LASTEXITCODE -ne 0) { Fail 'Could not read the split branch.' }
    return $tip
}

function Get-Upstream {
    git fetch $CoreUrl main
    if ($LASTEXITCODE -ne 0) { Fail 'Could not fetch Radio_Web_Control_Core.' }
    $u = git rev-parse FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { Fail 'Could not resolve the fetched core main.' }
    return $u
}

if ($PSCmdlet.ParameterSetName -eq 'Pull') {
    git subtree pull --prefix=$Prefix $CoreUrl main --squash
    if ($LASTEXITCODE -ne 0) { Fail 'Subtree pull failed.' }
    Write-Host 'core/ is up to date with Radio_Web_Control_Core main.' -ForegroundColor Green
    Write-Host 'Build once before committing: the shared JS copies and their .gitignore files are generated.' -ForegroundColor DarkGray
    exit 0
}

if ($PSCmdlet.ParameterSetName -eq 'Push') {
    $dirty = git status --porcelain
    if ($dirty) {
        Fail ('Working tree is not clean. Commit or stash first - the split only sees committed ' +
              'content, so uncommitted core/ changes would be silently left behind.')
    }

    Write-Host 'Pulling first, so the push can fast-forward...' -ForegroundColor Cyan
    git subtree pull --prefix=$Prefix $CoreUrl main --squash
    if ($LASTEXITCODE -ne 0) { Fail 'Subtree pull failed. Resolve before pushing.' }

    $tip      = Split-Core
    $upstream = Get-Upstream

    if ($tip -eq $upstream) {
        Write-Host 'Nothing to push - core/ already matches Radio_Web_Control_Core main.' -ForegroundColor Green
        Remove-Split; exit 0
    }

    Write-Host ''
    Write-Host 'These commits will go to Radio_Web_Control_Core main:' -ForegroundColor Yellow
    git log --oneline "$upstream..$tip"
    Write-Host ''

    git push $CoreUrl "${Tmp}:main"
    if ($LASTEXITCODE -ne 0) { Remove-Split; Fail 'Push rejected.' }

    Write-Host 'Pushed.' -ForegroundColor Green
    Write-Host 'Now pull it into the sibling app so both carry the same core:' -ForegroundColor Cyan
    Write-Host '    ./scripts/core-sync.ps1 -Pull' -ForegroundColor Cyan
    Remove-Split; exit 0
}

# -Check (the default)
$tip      = Split-Core
$upstream = Get-Upstream

$ahead  = git rev-list --count "$upstream..$tip"
$behind = git rev-list --count "$tip..$upstream"

if ($ahead -eq '0' -and $behind -eq '0') {
    Write-Host 'In step. core/ here and Radio_Web_Control_Core main are identical.' -ForegroundColor Green
} else {
    if ($ahead -ne '0') {
        Write-Host "OWED UPSTREAM: $ahead commit(s) touching core/ are not in Radio_Web_Control_Core." -ForegroundColor Red
        git log --oneline "$upstream..$tip"
        Write-Host 'Run: ./scripts/core-sync.ps1 -Push' -ForegroundColor Yellow
    }
    if ($behind -ne '0') {
        Write-Host "BEHIND: $behind commit(s) in Radio_Web_Control_Core are not here." -ForegroundColor Yellow
        Write-Host 'Run: ./scripts/core-sync.ps1 -Pull' -ForegroundColor Yellow
    }
}
Remove-Split
