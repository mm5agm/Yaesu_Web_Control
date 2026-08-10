<#
.SYNOPSIS
    Print GitHub release download statistics, split by full release vs pre-release.

.DESCRIPTION
    Pulls every release (and every asset) for one or more GitHub repos via the
    `gh` CLI and prints a per-release table plus running totals. Unlike the
    shields.io badge on the code page, this counts pre-release downloads too.

    Defaults to both of my apps: Yaesu Web Control and Icom Web Control.

.PARAMETER Repo
    One or more owner/repo slugs to report on. Defaults to both YWC and IWC.

.PARAMETER PreOnly
    Show only pre-releases.

.EXAMPLE
    .\scripts\download-stats.ps1
    Report on both YWC and IWC.

.EXAMPLE
    .\scripts\download-stats.ps1 -Repo mm5agm/Yaesu_Web_Control -PreOnly
    Show only the YWC pre-releases.

.NOTES
    Requires the GitHub CLI (`gh`) authenticated with access to the repo.
    IWC is a private repo, so `gh auth status` must show a token that can see it.
#>
[CmdletBinding()]
param(
    [string[]]$Repo = @('mm5agm/Yaesu_Web_Control', 'mm5agm/Icom_Web_Control'),
    [switch]$PreOnly
)

function Get-RepoStats {
    param([string]$Slug)

    $json = gh api "repos/$Slug/releases" --paginate 2>$null
    if (-not $?) {
        Write-Host "  Could not read releases for $Slug (auth or access?)." -ForegroundColor Red
        return
    }

    $releases = $json | ConvertFrom-Json
    if (-not $releases -or $releases.Count -eq 0) {
        Write-Host "  No releases found for $Slug." -ForegroundColor Yellow
        return
    }

    Write-Host ""
    Write-Host "=== $Slug ===" -ForegroundColor Cyan

    $rows = foreach ($r in $releases) {
        if ($PreOnly -and -not $r.prerelease) { continue }
        $count = ($r.assets | Measure-Object -Property download_count -Sum).Sum
        if ($null -eq $count) { $count = 0 }
        [pscustomobject]@{
            Tag       = $r.tag_name
            Kind      = if ($r.prerelease) { 'pre' } else { 'release' }
            Published = if ($r.published_at) { ([datetime]$r.published_at).ToString('yyyy-MM-dd') } else { '-' }
            Downloads = [int]$count
        }
    }

    $rows | Sort-Object Published -Descending |
        Format-Table Published, Tag, Kind, Downloads -AutoSize | Out-String | Write-Host

    $rel  = ($rows | Where-Object Kind -eq 'release' | Measure-Object Downloads -Sum).Sum
    $pre  = ($rows | Where-Object Kind -eq 'pre'     | Measure-Object Downloads -Sum).Sum
    if ($null -eq $rel) { $rel = 0 }
    if ($null -eq $pre) { $pre = 0 }

    Write-Host ("  Full releases : {0,6}" -f $rel) -ForegroundColor Green
    Write-Host ("  Pre-releases  : {0,6}" -f $pre) -ForegroundColor Green
    Write-Host ("  Grand total   : {0,6}" -f ($rel + $pre)) -ForegroundColor Green
}

foreach ($slug in $Repo) {
    Get-RepoStats -Slug $slug
}
