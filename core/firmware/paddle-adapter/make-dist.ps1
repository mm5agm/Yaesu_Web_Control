<#
.SYNOPSIS
    Builds the user firmware into dist/, the files YWC and IWC carry.

.DESCRIPTION
    Builds PlatformIO env "user", merges bootloader, partition table,
    boot_app0 and the app into the one file the Install / Update box writes
    at offset 0, and sets dist/firmware.json's version to FW_VERSION in
    src/main.cpp. Raise FW_VERSION first: the box offers an update only when
    the board's version is older than firmware.json's.

    Builds only; never uploads, so no COM port is touched.
    core/tests/js/paddle-firmware.test.mjs checks that dist/ matches the source.
#>
# Not 'Stop': in Windows PowerShell 5.1 a compiler warning on stderr would end
# the script. Exit codes are checked instead, as in scripts/core-sync.ps1.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

$pio = Join-Path $env:USERPROFILE '.platformio'
$python = Join-Path $pio 'penv\Scripts\python.exe'
$esptool = Join-Path $pio 'packages\tool-esptoolpy\esptool.py'
$bootApp0 = Join-Path $pio 'packages\framework-arduinoespressif32\tools\partitions\boot_app0.bin'

$m = Select-String -Path 'src\main.cpp' -Pattern '^#define FW_VERSION "([^"]+)"'
if (-not $m) { throw 'FW_VERSION not found in src\main.cpp' }
$version = $m.Matches[0].Groups[1].Value

& (Join-Path $pio 'penv\Scripts\pio.exe') run -e user
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$b = '.pio\build\user'
& $python $esptool --chip esp32s3 merge_bin -o 'dist\ywc-paddle-adapter.bin' --flash_mode dio --flash_size 4MB `
    0x0 "$b\bootloader.bin" 0x8000 "$b\partitions.bin" 0xe000 $bootApp0 0x10000 "$b\firmware.bin"
if ($LASTEXITCODE -ne 0) { throw 'merge_bin failed' }

$json = Get-Content 'dist\firmware.json' -Raw
$json = $json -replace '"version": "[^"]*"', ('"version": "' + $version + '"')
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'dist\firmware.json'), $json)
Write-Host "dist/ now holds firmware $version"
