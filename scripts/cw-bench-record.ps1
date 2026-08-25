<#
.SYNOPSIS
    Record a radio's receive audio and run it through the Core CW decoder.

.DESCRIPTION
    The bench comparison the CW plan asks for before Phase 2: put the same
    off-air signal through the radio's own decoder and through ours, and see
    which copies it. The radio decodes on its own screen; this records the
    audio it is decoding and hands it to tools/CwBench.

    Needs ffmpeg on PATH (winget install Gyan.FFmpeg) and the .NET SDK.

.EXAMPLE
    .\scripts\cw-bench-record.ps1 -List
    Show the capture devices, so the radio's USB CODEC can be named exactly.

.EXAMPLE
    .\scripts\cw-bench-record.ps1 -Device "Microphone (3- USB Audio Device)" -Seconds 60 -Pitch 600
    Record a minute and decode it.

.EXAMPLE
    .\scripts\cw-bench-record.ps1 -Decode .\bench\cw-2026-08-25.wav -Pitch 600
    Re-decode an existing recording, e.g. with a different pitch.
#>
# Keep this file ASCII. It has no BOM, so Windows PowerShell 5.1 reads it as
# CP1252; the third byte of a UTF-8 em-dash is 0x94, which is a curly closing
# quote in CP1252 and which PowerShell honours as a string delimiter. One
# em-dash in a Write-Host line therefore broke the parse twenty lines later,
# with an error naming neither the character nor the line it was on.
[CmdletBinding()]
param(
    [switch] $List,
    [switch] $Probe,
    [string] $Device,
    [int]    $Seconds = 60,
    [double] $Pitch   = 600,
    [double] $Search  = 250,
    [string] $Out,
    [string] $Decode
)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path $PSScriptRoot -Parent
$bench = Join-Path $repo 'bench'
$proj  = Join-Path $repo 'tools\CwBench\CwBench.csproj'

function Require-Ffmpeg {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        throw "ffmpeg is not on PATH. Install it with: winget install Gyan.FFmpeg"
    }
}

function Get-CaptureDevices {
    # ffmpeg writes the device list to stderr and exits non-zero by design.
    # Windows PowerShell 5.1 wraps a native command's stderr in ErrorRecords,
    # so with ErrorActionPreference Stop the listing itself throws. Drop to
    # Continue for the call and flatten the records back to strings.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = & ffmpeg -hide_banner -list_devices true -f dshow -i dummy 2>&1 |
           ForEach-Object { $_.ToString() }
    $ErrorActionPreference = $prev
    $raw | Select-String '"(.+)" \(audio\)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
}

function Get-DeviceLevel {
    param([string] $Name, [int] $ForSeconds = 3)

    # volumedetect prints mean/max to stderr, same wrapping problem as above.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & ffmpeg -hide_banner -f dshow -i "audio=$Name" -t $ForSeconds `
                    -af volumedetect -f null - 2>&1 |
           ForEach-Object { $_.ToString() }
    $ErrorActionPreference = $prev

    $mean = ($out | Select-String 'mean_volume: (\S+) dB').Matches.Groups[1].Value
    $max  = ($out | Select-String 'max_volume: (\S+) dB').Matches.Groups[1].Value
    [pscustomobject]@{ Device = $Name; MeanDb = $mean; MaxDb = $max }
}

if ($List) {
    Require-Ffmpeg
    Write-Host "Audio capture devices:`n" -ForegroundColor Cyan
    Get-CaptureDevices | ForEach-Object { Write-Host "  $_" }
    Write-Host "`nThe radio's USB CODEC is the one that goes quiet when you turn"
    Write-Host "the AF gain down. Probe them all with -Probe, or one with"
    Write-Host "-Device `"<name>`" -Seconds 5."
    return
}

if ($Probe) {
    Require-Ffmpeg
    # Which device is the radio is the question every bench run starts with, and
    # the device names Windows invents do not answer it. Levels do: with the
    # radio on and the AF gain up, the radio's CODEC is the one that is not
    # silent. A max of 0.0 dB means it is clipping - turn the radio's USB AF
    # output level down before recording anything worth comparing.
    Write-Host "Probing each device for 3 s. Have the radio on, tuned to a signal.`n" -ForegroundColor Cyan
    $rows = foreach ($d in Get-CaptureDevices) {
        Write-Host "  $d ..." -NoNewline
        $r = Get-DeviceLevel -Name $d
        Write-Host " mean $($r.MeanDb) dB, max $($r.MaxDb) dB"
        $r
    }
    Write-Host ""
    $rows | Format-Table -AutoSize
    Write-Host "Silent (around -90 dB) means nothing is arriving. Around -20 dB mean" -ForegroundColor Yellow
    Write-Host "is healthy. 0.0 dB max is clipping and will not decode." -ForegroundColor Yellow
    return
}

if (-not $Decode) {
    if (-not $Device) { throw "Give -Device (see -List), or -Decode <file.wav> to re-run an existing recording." }
    Require-Ffmpeg

    if (-not (Test-Path $bench)) { New-Item -ItemType Directory -Path $bench | Out-Null }
    if (-not $Out) { $Out = Join-Path $bench ("cw-" + (Get-Date -Format 'yyyy-MM-dd-HHmmss') + ".wav") }

    Write-Host "Recording $Seconds s from `"$Device`"" -ForegroundColor Cyan
    Write-Host "Watch the radio's decode screen NOW - you are comparing against it.`n"

    # -audio_buffer_size 80 keeps dshow's latency down; without it ffmpeg picks
    # a buffer big enough to lose the first second of a short capture.
    & ffmpeg -hide_banner -loglevel warning -f dshow -audio_buffer_size 80 `
             -i "audio=$Device" -t $Seconds -ac 1 -ar 48000 -y $Out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with exit code $LASTEXITCODE" }

    Write-Host "`nSaved $Out" -ForegroundColor Green
    $Decode = $Out
}

if (-not (Test-Path $Decode)) { throw "No such recording: $Decode" }

Write-Host "`nDecoding...`n" -ForegroundColor Cyan
& dotnet run --project $proj -c Release -- $Decode --pitch $Pitch --search $Search

Write-Host ""
Write-Host "Now write down what the RADIO decoded over the same period, and compare." -ForegroundColor Yellow
Write-Host "Keep both: the .wav is the evidence, and the decoder can be re-run over" -ForegroundColor Yellow
Write-Host "it after any change without going back to the air." -ForegroundColor Yellow
