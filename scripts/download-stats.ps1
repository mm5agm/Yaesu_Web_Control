<#
.SYNOPSIS
    Print GitHub release download statistics, split by full release vs pre-release.

.DESCRIPTION
    Pulls every release (and every asset) for one or more GitHub repos via the
    `gh` CLI and prints a per-release table plus running totals, split into
    Windows (.exe/.msi/.zip) and Mac (.dmg/.pkg) downloads. Unlike the
    shields.io badge on the code page, this counts pre-release downloads and
    Mac downloads too - the badge counts only Yaesu_Web_Control_Setup.exe.

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
        $win = 0; $mac = 0; $other = 0
        foreach ($a in $r.assets) {
            switch -Regex ($a.name) {
                '\.(exe|msi|zip)$' { $win   += $a.download_count; break }
                '\.(dmg|pkg)$'     { $mac   += $a.download_count; break }
                default            { $other += $a.download_count }
            }
        }
        [pscustomobject]@{
            Tag       = $r.tag_name
            Kind      = if ($r.prerelease) { 'pre' } else { 'release' }
            Published = if ($r.published_at) { ([datetime]$r.published_at).ToString('yyyy-MM-dd') } else { '-' }
            Windows   = [int]$win
            Mac       = [int]$mac
            Other     = [int]$other
            Downloads = [int]($win + $mac + $other)
        }
    }

    # Only show the Other column if some asset didn't match Windows or Mac.
    $columns = @('Published', 'Tag', 'Kind', 'Windows', 'Mac')
    if (($rows | Measure-Object Other -Sum).Sum -gt 0) { $columns += 'Other' }
    $columns += 'Downloads'

    $rows | Sort-Object Published -Descending |
        Format-Table $columns -AutoSize | Out-String | Write-Host

    function Sum-Of($set, $prop) {
        $s = ($set | Measure-Object $prop -Sum).Sum
        if ($null -eq $s) { 0 } else { [int]$s }
    }

    $relRows = @($rows | Where-Object Kind -eq 'release')
    $preRows = @($rows | Where-Object Kind -eq 'pre')

    $fmt = "  {0,-14}: {1,6}   (Windows {2,6}, Mac {3,6})"
    Write-Host ($fmt -f 'Full releases', (Sum-Of $relRows Downloads), (Sum-Of $relRows Windows), (Sum-Of $relRows Mac)) -ForegroundColor Green
    Write-Host ($fmt -f 'Pre-releases',  (Sum-Of $preRows Downloads), (Sum-Of $preRows Windows), (Sum-Of $preRows Mac)) -ForegroundColor Green
    Write-Host ($fmt -f 'Grand total',   (Sum-Of $rows Downloads),    (Sum-Of $rows Windows),    (Sum-Of $rows Mac))    -ForegroundColor Green
}

foreach ($slug in $Repo) {
    Get-RepoStats -Slug $slug
}
