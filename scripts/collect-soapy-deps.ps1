# collect-soapy-deps.ps1
# Copies locally-built SoapySDR DLLs into the soapysdr-dist/ folder
# that installer.nsi expects.  Run once before creating a release locally,
# or whenever the SoapySDR build is updated.
#
# Usage:  .\scripts\collect-soapy-deps.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root    = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root "soapysdr-dist\runtime"
$plugins = Join-Path $root "soapysdr-dist\plugins"

New-Item -ItemType Directory -Force -Path $runtime | Out-Null
New-Item -ItemType Directory -Force -Path $plugins | Out-Null

$copies = @(
    @{ Src = "C:\build\SoapySDR\lib\SoapySDR.dll";             Dst = "$runtime\SoapySDR.dll" },
    @{ Src = "C:\deps\airspy\x64\airspy.dll";                   Dst = "$runtime\airspy.dll" },
    @{ Src = "C:\build\libhackrf\libhackrf\src\hackrf.dll";     Dst = "$runtime\hackrf.dll" },
    @{ Src = "C:\deps\rtlsdr\librtlsdr.dll";                    Dst = "$runtime\librtlsdr.dll" },
    # libusb from the official libusb release (VS2022, x64), not the older
    # MinGW copy in C:\deps\rtlsdr: hackrf.dll needs libusb_endpoint_set_raw_io
    # and libusb_endpoint_supports_raw_io, which that copy lacks (YWC D#404).
    @{ Src = "C:\deps\libusb\VS2022\MS64\dll\libusb-1.0.dll";   Dst = "$runtime\libusb-1.0.dll" },
    @{ Src = "C:\deps\rtlsdr\libwinpthread-1.dll";              Dst = "$runtime\libwinpthread-1.dll" },
    @{ Src = "C:\deps\airspy\x64\pthreadVC2.dll";               Dst = "$runtime\pthreadVC2.dll" },
    @{ Src = "C:\build\pthreads4w\pthreadVC3.dll";              Dst = "$runtime\pthreadVC3.dll" },
    @{ Src = "C:\build\SoapyAirspy\airspySupport.dll";          Dst = "$plugins\airspySupport.dll" },
    @{ Src = "C:\build\SoapyHackRF\HackRFSupport.dll";          Dst = "$plugins\HackRFSupport.dll" },
    @{ Src = "C:\build\SoapyRTLSDR\rtlsdrSupport.dll";          Dst = "$plugins\rtlsdrSupport.dll" }
)

foreach ($c in $copies) {
    if (!(Test-Path $c.Src)) {
        Write-Warning "Missing: $($c.Src) - skipping"
        continue
    }
    Copy-Item $c.Src $c.Dst -Force
    Write-Host "Copied $($c.Src)" -ForegroundColor Green
}

Write-Host ""
Write-Host "soapysdr-dist/ is ready." -ForegroundColor Cyan
