<#
.SYNOPSIS
    Regenerate the ARRL accuracy table in core/docs/design/cw-decoder.md section 8.

.DESCRIPTION
    Runs every ARRL W1AW code practice file in bench/arrl through CwBench at the
    pitch and filter width the table is measured at, under each of the eight
    noise and fade conditions, and prints the result as the markdown table ready
    to paste.

    This exists because the table went stale. It was published on 2026-08-23 and
    not re-measured until 2026-09-01, by which time several rounds of detector
    work had moved the hardest cells by up to 80 points - the document was
    understating the decoder badly and nobody could tell, because re-measuring
    by hand is eighty CwBench invocations.

    bench/ is gitignored in its entirety and the ARRL files are ARRL copyright:
    they stay local, and are fetched with scripts/get-arrl-practice.sh.

.EXAMPLE
    ./scripts/cw-arrl-table.ps1
#>
[CmdletBinding()]
param(
    [string] $BenchDir,
    [double] $Pitch  = 750,
    [int]    $Filter = 500
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not populated while param defaults are being bound
# under Windows PowerShell 5.1, so the paths are resolved here instead.
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $BenchDir) { $BenchDir = Join-Path $repo 'bench\arrl' }
$cwbench = Join-Path $repo 'tools\CwBench\bin\Release\net10.0\CwBench.exe'

if (-not (Test-Path $cwbench)) {
    throw "CwBench not built. Run: dotnet build tools/CwBench/CwBench.csproj -c Release"
}
if (-not (Test-Path $BenchDir)) {
    throw "No $BenchDir. Fetch the files with scripts/get-arrl-practice.sh, then convert them."
}

# The 5-18 WPM files and the 20-40 WPM files were published on different days,
# so the date prefix changes partway down the list.
$files = @(
    @{ Wpm =  5; Stem = '260304_05' }, @{ Wpm = 10; Stem = '260304_10' }
    @{ Wpm = 13; Stem = '260304_13' }, @{ Wpm = 15; Stem = '260304_15' }
    @{ Wpm = 18; Stem = '260304_18' }, @{ Wpm = 20; Stem = '260303_20' }
    @{ Wpm = 25; Stem = '260303_25' }, @{ Wpm = 30; Stem = '260303_30' }
    @{ Wpm = 35; Stem = '260303_35' }, @{ Wpm = 40; Stem = '260303_40' }
)

# A fade is applied BEFORE the noise, so f20n6 means peaks at +6 dB and troughs
# at -14 dB. Naming them the other way round would flatter the deep cells.
$conditions = @(
    @{ Name = 'clean'  }
    @{ Name = 'n12'    ; Noise = 12 }
    @{ Name = 'n9'     ; Noise =  9 }
    @{ Name = 'n6'     ; Noise =  6 }
    @{ Name = 'n3'     ; Noise =  3 }
    @{ Name = 'f20'    ;             Fade = 20 }
    @{ Name = 'f20n12' ; Noise = 12; Fade = 20 }
    @{ Name = 'f20n6'  ; Noise =  6; Fade = 20 }
)

function Get-Accuracy {
    param($Wav, $Txt, $Noise, $Fade)

    $cmd = @($Wav, '--pitch', $Pitch, '--filter', $Filter, '--telemetry', '0', '--expect', $Txt)
    if ($null -ne $Noise) { $cmd += @('--noise', $Noise) }
    if ($null -ne $Fade)  { $cmd += @('--fade',  $Fade)  }

    $out = & $cwbench @cmd 2>&1 | Out-String
    if ($out -match 'accuracy\s+([\d.]+)%') { return [double]$Matches[1] }
    return [double]::NaN
}

Write-Host ('| WPM | ' + (($conditions | ForEach-Object { $_.Name.PadRight(5) }) -join ' | ') + ' |')
Write-Host ('|-----|' + (($conditions | ForEach-Object { '-------' }) -join '|') + '|')

foreach ($f in $files) {
    $wav = Join-Path $BenchDir ('{0}WPM.wav' -f $f.Stem)
    $txt = Join-Path $BenchDir ('{0}.txt'    -f $f.Stem)
    if (-not (Test-Path $wav)) { Write-Warning "missing $wav"; continue }

    $cells = foreach ($c in $conditions) {
        '{0,5:F1}%' -f (Get-Accuracy -Wav $wav -Txt $txt -Noise $c.Noise -Fade $c.Fade)
    }
    Write-Host (('| {0,-3} | ' -f $f.Wpm) + ($cells -join ' | ') + ' |')
}
