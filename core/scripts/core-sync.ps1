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
    Report whether core/ here and Radio_Web_Control_Core main differ, and which
    way. Read-only: fetches and compares, changes nothing.

.PARAMETER Pull
    Bring Radio_Web_Control_Core main down into core/.

.PARAMETER Push
    Send core/ up to Radio_Web_Control_Core main as one commit. Merges core
    main into core/ first, so nothing there is undone.

.EXAMPLE
    ./scripts/core-sync.ps1 -Check
    ./scripts/core-sync.ps1 -Push

.NOTES
    -Push does not use git subtree split. The split had to walk the app's
    whole history (ten minutes or more) whenever a branch merge took it back
    past the last commit it remembered, which on develop was nearly every
    time. Instead -Push merges core main into core/ (so core/ holds everything
    core main has), then commits core/'s contents on top of core main as one
    commit, listing the app commits it carries, and pushes that. Seconds,
    whatever the shape of the history. The push only fast-forwards, so if core
    main moved in between, it is rejected and nothing is lost.

    Core's history gets one commit per push rather than one per app commit.
    The app commits are named in the message, so each change can still be
    found in the app that made it.

    .git/core-sync-last ("<app commit> <core commit>", this clone only)
    remembers the last push: which core main commit core/ matched, and from
    which app commit. -Check uses it to tell "core main has new work" from
    "core main has our own push", and -Push to list only the new app commits.

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

function Fail([string]$m) { Write-Host $m -ForegroundColor Red; exit 1 }

$repoRoot = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { Fail 'Not inside a git repository.' }
Set-Location $repoRoot

$commonDir = git rev-parse --path-format=absolute --git-common-dir

# Read git's output as UTF-8, so commit subjects reach the core commit intact.
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false

# git subtree leaves its working notes behind, one folder per run.
function Remove-SubtreeCache {
    $cache = Join-Path $commonDir 'subtree-cache'
    if (Test-Path $cache) { Remove-Item -Recurse -Force $cache }
}

# The last push: "<app commit> <core commit>". Shared by this clone's worktrees.
$Marker = Join-Path $commonDir 'core-sync-last'

# The remembered push, if HEAD has it in its history; otherwise $null.
function Get-Marker {
    if (-not (Test-Path $Marker)) { return $null }
    $parts = (Get-Content $Marker -TotalCount 1) -split ' '
    if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[0-9a-f]{40}$' -or $parts[1] -notmatch '^[0-9a-f]{40}$') { return $null }
    git cat-file -e "$($parts[0])^{commit}"
    if ($LASTEXITCODE -ne 0) { return $null }
    git merge-base --is-ancestor $parts[0] HEAD
    if ($LASTEXITCODE -ne 0) { return $null }
    return $parts
}

function Get-Upstream {
    git fetch $CoreUrl main
    if ($LASTEXITCODE -ne 0) { Fail 'Could not fetch Radio_Web_Control_Core.' }
    $u = git rev-parse FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { Fail 'Could not resolve the fetched core main.' }
    return $u
}

# Is $a an ancestor of (or the same as) $b? Both must be present locally.
function Test-Ancestor([string]$a, [string]$b) {
    git merge-base --is-ancestor $a $b
    return ($LASTEXITCODE -eq 0)
}

# The newest core main commit core/ is known to hold: the last subtree pull's,
# or the last push's, whichever is later. $null if neither is known.
function Get-Synced([string]$upstream) {
    $pulled = $null
    $body = git log -1 --grep='^git-subtree-split: ' --format=%B HEAD
    foreach ($line in $body) {
        if ($line -match '^git-subtree-split: ([0-9a-f]{40})') { $pulled = $Matches[1] }
    }
    $m = Get-Marker
    $pushed = if ($m) { $m[1] } else { $null }
    $known = @($pulled, $pushed) | Where-Object { $_ -and (Test-Ancestor $_ $upstream) }
    if (-not $known) { return $null }
    $best = $known[0]
    foreach ($k in $known) { if (Test-Ancestor $best $k) { $best = $k } }
    return $best
}

# App commits that changed core/ and are not yet in core main: since the last
# push if it is in this branch's history, else since the last subtree pull.
function Get-AppCommits {
    $m = Get-Marker
    $from = if ($m) { $m[0] } else { git log -1 --grep='^git-subtree-split: ' --format=%H HEAD }
    $range = if ($from) { "$from..HEAD" } else { 'HEAD' }
    git log --no-merges --format='%h %s' $range -- $Prefix |
        Where-Object { $_ -notmatch "^[0-9a-f]+ Squashed '$Prefix/' changes" }
}

if ($PSCmdlet.ParameterSetName -eq 'Pull') {
    git subtree pull --prefix=$Prefix $CoreUrl main --squash
    $ok = $LASTEXITCODE -eq 0
    Remove-SubtreeCache
    if (-not $ok) { Fail 'Subtree pull failed.' }
    Write-Host 'core/ is up to date with Radio_Web_Control_Core main.' -ForegroundColor Green
    Write-Host 'Build once before committing: the shared JS copies and their .gitignore files are generated.' -ForegroundColor DarkGray
    exit 0
}

if ($PSCmdlet.ParameterSetName -eq 'Push') {
    $dirty = git status --porcelain
    if ($dirty) {
        Fail ('Working tree is not clean. Commit or stash first - only committed ' +
              'content is sent, so uncommitted core/ changes would be silently left behind.')
    }

    # Merge exactly the commit that will be pushed onto, so core/ holds all
    # of it and the snapshot below cannot undo anything there.
    $upstream = Get-Upstream
    Write-Host 'Merging core main into core/ first...' -ForegroundColor Cyan
    git subtree merge --prefix=$Prefix $upstream --squash
    $ok = $LASTEXITCODE -eq 0
    Remove-SubtreeCache
    if (-not $ok) { Fail 'Merging core main into core/ failed. Resolve, commit, then push again.' }

    $tree     = git rev-parse "HEAD:$Prefix"
    $coreTree = git rev-parse "$upstream^{tree}"
    if ($tree -eq $coreTree) {
        Write-Host 'Nothing to push - core/ already matches Radio_Web_Control_Core main.' -ForegroundColor Green
        exit 0
    }

    $head    = git rev-parse HEAD
    $app     = ((git remote get-url origin) -replace '\.git$', '') -replace '.*[/\:]', ''
    $branch  = git branch --show-current
    $commits = @(Get-AppCommits)

    $subject = if ($commits.Count -eq 1) { $commits[0] -replace '^[0-9a-f]+ ', '' }
               else { "core/ from $app $branch" }
    $body = if ($commits.Count) { "From ${app}:`n" + (($commits | ForEach-Object { "  $_" }) -join "`n") }
            else { "From $app." }
    # Through a file: Windows PowerShell 5.1 mangles double quotes in the
    # arguments it hands to git, and subjects have them.
    $msgFile = Join-Path $commonDir 'core-sync-msg'
    [IO.File]::WriteAllText($msgFile, "$subject`n`n$body`n`n$app-commit: $head`n", (New-Object Text.UTF8Encoding $false))
    $new = git commit-tree $tree -p $upstream -F $msgFile
    $ok = $LASTEXITCODE -eq 0
    Remove-Item $msgFile
    if (-not $ok) { Fail 'Could not make the core commit.' }

    Write-Host ''
    Write-Host 'This goes to Radio_Web_Control_Core main:' -ForegroundColor Yellow
    git log -1 --format='%s%n%n%b' $new
    git diff --stat $upstream $new
    Write-Host ''

    git push $CoreUrl "${new}:refs/heads/main"
    if ($LASTEXITCODE -ne 0) { Fail 'Push rejected. If core main moved, run -Push again.' }

    Set-Content -Path $Marker -Value "$head $new" -Encoding ascii
    Write-Host "Pushed ($($new.Substring(0, 9)))." -ForegroundColor Green
    Write-Host 'Now pull it into the sibling app so both carry the same core:' -ForegroundColor Cyan
    Write-Host '    ./scripts/core-sync.ps1 -Pull' -ForegroundColor Cyan
    exit 0
}

# -Check (the default)
$upstream = Get-Upstream
$tree     = git rev-parse "HEAD:$Prefix"
$coreTree = git rev-parse "$upstream^{tree}"

if ($tree -eq $coreTree) {
    Write-Host 'In step. core/ here and Radio_Web_Control_Core main are identical.' -ForegroundColor Green
    exit 0
}

$synced = Get-Synced $upstream
$behind = if ($synced) { [int](git rev-list --count "$synced..$upstream") } else { $null }
$owed   = if ($synced) { (git rev-parse "$synced^{tree}") -ne $tree } else { $true }

if ($owed) {
    Write-Host 'OWED UPSTREAM: core/ here has changes that are not in Radio_Web_Control_Core main.' -ForegroundColor Red
    $commits = @(Get-AppCommits)
    if ($commits.Count) { $commits | ForEach-Object { Write-Host "  $_" } }
    $base = if ($synced) { $synced } else { $upstream }
    git diff --stat $base $tree
    Write-Host 'Run: ./scripts/core-sync.ps1 -Push' -ForegroundColor Yellow
}
if ($null -eq $behind) {
    Write-Host 'Could not tell which core main commit core/ last matched; -Pull to be sure.' -ForegroundColor Yellow
} elseif ($behind -gt 0) {
    Write-Host "BEHIND: $behind commit(s) in Radio_Web_Control_Core are not here." -ForegroundColor Yellow
    git log --oneline "$synced..$upstream"
    Write-Host 'Run: ./scripts/core-sync.ps1 -Pull' -ForegroundColor Yellow
}
