# merge-calibration.ps1
# Fold a user-emailed calibration into the SHIPPED default for a radio model.
#
# When a user clicks "Email calibration to developer" on the Meter Calibration
# page, you receive the JSON from their calibration.user.json (the email subject
# names the radio). Save that JSON to a file and run this to merge the meters
# they actually calibrated into wwwroot/calibration.default.<Model>.json.
#
# Usage:
#   .\scripts\merge-calibration.ps1 -InputFile user-cal.json -Model FTDX3000
#   .\scripts\merge-calibration.ps1 -InputFile user-cal.json -Model FTDX3000 -Force
#
#   -Force   replace every meter, not just the ones whose points changed.
#
# By default only meters whose point curves differ from the current default are
# updated, so meters the user left untouched (still cloned from another model)
# are not disturbed. Tolerates an email preamble around the JSON. Rewrites the
# file via ConvertTo-Json, so expect a reformat in the diff -- harmless, the app
# reads it fine. Does NOT commit: review the summary + `git diff`, then commit.

param(
    [Parameter(Mandatory = $true)][string]$InputFile,
    [Parameter(Mandatory = $true)][string]$Model,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $InputFile)) { Write-Error "Input file not found: $InputFile"; exit 1 }

$repoRoot    = Split-Path $PSScriptRoot -Parent
$defaultPath = Join-Path $repoRoot "wwwroot\calibration.default.$Model.json"
if (-not (Test-Path $defaultPath)) {
    Write-Host "No shipped default for model '$Model' at: $defaultPath" -ForegroundColor Red
    Write-Host "Models available:"
    Get-ChildItem (Join-Path $repoRoot 'wwwroot') -Filter 'calibration.default.*.json' | ForEach-Object { Write-Host "  $($_.Name)" }
    exit 1
}

# Read the incoming JSON, tolerating an email preamble by taking the outermost { ... }.
$raw   = Get-Content $InputFile -Raw
$start = $raw.IndexOf('{'); $end = $raw.LastIndexOf('}')
if ($start -lt 0 -or $end -le $start) { Write-Error "No JSON object found in $InputFile"; exit 1 }
$incoming = $raw.Substring($start, $end - $start + 1) | ConvertFrom-Json
$current  = Get-Content $defaultPath -Raw | ConvertFrom-Json

if (-not $incoming.meters) { Write-Error "Input has no 'meters' array -- is this a YWC calibration export?"; exit 1 }

# Index the incoming meters by name.
$incomingByName = @{}
foreach ($m in $incoming.meters) { $incomingByName[$m.name] = $m }

$changed   = @()
$outMeters = @()
foreach ($cur in $current.meters) {
    $inc = $null
    if ($incomingByName.ContainsKey($cur.name)) { $inc = $incomingByName[$cur.name] }

    $curPts = ConvertTo-Json $cur.points -Depth 10 -Compress
    $incPts = if ($inc) { ConvertTo-Json $inc.points -Depth 10 -Compress } else { $null }

    if ($inc -and ($Force -or $incPts -ne $curPts)) {
        $outMeters += $inc
        $ptCount = @($inc.points).Count
        $changed += ('{0} ({1} points)' -f $cur.name, $ptCount)
    }
    else {
        $outMeters += $cur
    }
}
$current.meters = $outMeters

if ($changed.Count -eq 0) {
    Write-Host "No changes: the emailed calibration matches the current $Model default (nothing to merge)." -ForegroundColor Yellow
    exit 0
}

# Write it back (UTF-8, no BOM).
$outJson = $current | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($defaultPath, $outJson, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("Merged into {0}" -f $defaultPath) -ForegroundColor Green
Write-Host "Meters updated:" -ForegroundColor Green
$changed | ForEach-Object { Write-Host "  - $_" }
Write-Host ""
Write-Host "Review it, then commit:" -ForegroundColor Cyan
Write-Host ("  git diff -- wwwroot/calibration.default.{0}.json" -f $Model)
Write-Host ("  git add wwwroot/calibration.default.{0}.json" -f $Model)
