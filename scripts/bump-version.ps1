# bump-version.ps1
# Updates the four version markers before a release. Run from develop before
# finish-release.ps1.
#
# Updates:
#   1. Models\AppVersion.cs        - Current        ("X.Y.Z")
#   2. Models\AppVersion.cs        - ReleaseDate    ("YYYY-MM-DD")
#   3. installer.nsi               - !define VERSION
#   4. Yaesu_Web_Control.csproj    - Version, FileVersion, AssemblyVersion
#   5. README.md                   - Latest-release shields.io badge URL
#
# The csproj was added in August 2026. It had been left out, and with nothing
# else watching it, its three elements sat on 1.5.6 from the May release right
# through to 2.4.2 -- so every installer built in between reported a version in
# its file properties that had nothing to do with the build.
#
# Usage:
#   .\scripts\bump-version.ps1 -Version 2.2.0
#   .\scripts\bump-version.ps1 -Version v2.2.0
#   .\scripts\bump-version.ps1 -Version 2.2.0 -ReleaseDate 2026-06-10
#
# ReleaseDate defaults to today (system date) in ISO format.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseDate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Normalise: strip leading 'v' for the semver value; keep the v-prefixed form
# for the README badge.
$semver = $Version.TrimStart('v', 'V')
$vtag   = "v$semver"

if (-not ($semver -match '^\d+\.\d+\.\d+$')) {
    Write-Error "Version must look like X.Y.Z or vX.Y.Z (got '$Version')"
    exit 1
}

if (-not $ReleaseDate) {
    $ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
}
if (-not ($ReleaseDate -match '^\d{4}-\d{2}-\d{2}$')) {
    Write-Error "ReleaseDate must be ISO (YYYY-MM-DD), got '$ReleaseDate'"
    exit 1
}

# Resolve repo root from script location.
$repoRoot = Split-Path $PSScriptRoot -Parent

$appVersionPath = Join-Path $repoRoot 'Models\AppVersion.cs'
$installerPath  = Join-Path $repoRoot 'installer.nsi'
$csprojPath     = Join-Path $repoRoot 'Yaesu_Web_Control.csproj'
$readmePath     = Join-Path $repoRoot 'README.md'

foreach ($p in @($appVersionPath, $installerPath, $csprojPath, $readmePath)) {
    if (-not (Test-Path $p)) { Write-Error "File not found: $p"; exit 1 }
}

# Explicit UTF-8 reader/writer. PowerShell 5.1's `Get-Content` defaults to
# the system codepage (typically Windows-1252 in en-GB / en-US locales),
# which would mangle any non-ASCII bytes (emoji, em-dashes, smart quotes)
# on read — and `Set-Content -Encoding utf8` adds a BOM that some downstream
# tools (NSIS, some git diff readers) don't expect. Doing the byte-level
# read/write via System.IO.File side-steps both issues.
function Read-Utf8([string]$Path) {
    return [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

# Whether the file currently starts with a UTF-8 BOM.
function Test-Utf8Bom([string]$Path) {
    $b = [System.IO.File]::ReadAllBytes($Path)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}

# Write back with the BOM the file already had. AppVersion.cs, installer.nsi
# and README.md have none; the csproj has one, because Visual Studio wrote it.
# Stripping it would be a harmless-but-pointless whole-file change every time
# a version is bumped, so round-trip whatever was there.
function Write-Utf8Preserving([string]$Path, [string]$Content, [bool]$Bom) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($Bom))
}

function Replace-OrFail([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Label) {
    # Verify the pattern actually matches somewhere — protects against
    # silently leaving a file untouched if e.g. the user reformatted the
    # source and our pattern no longer fits. We do NOT fail if the
    # replacement happens to equal the original (that's the legitimate
    # case where the value is already correct, e.g. two releases on the
    # same day so ReleaseDate doesn't change).
    if (-not [regex]::IsMatch($Text, $Pattern)) {
        Write-Error "No match for $Label (pattern: $Pattern). File untouched."
        exit 1
    }
    return [regex]::Replace($Text, $Pattern, $Replacement)
}

# ---- 1 & 2. Models\AppVersion.cs ----
$av = Read-Utf8 $appVersionPath
$av = Replace-OrFail $av 'Current\s*=\s*"\d+\.\d+\.\d+"' "Current = `"$semver`""      'AppVersion.Current'
$av = Replace-OrFail $av 'ReleaseDate\s*=\s*"\d{4}-\d{2}-\d{2}"' "ReleaseDate = `"$ReleaseDate`"" 'AppVersion.ReleaseDate'
Write-Utf8NoBom $appVersionPath $av
Write-Host ("[1/4] AppVersion.cs   -> Current={0,-8} ReleaseDate={1}" -f $semver, $ReleaseDate) -ForegroundColor Green

# ---- 3. installer.nsi ----
$ns = Read-Utf8 $installerPath
$ns = Replace-OrFail $ns '!define\s+VERSION\s+"\d+\.\d+\.\d+"' "!define VERSION `"$semver`"" 'installer.nsi VERSION'
Write-Utf8NoBom $installerPath $ns
Write-Host ("[2/4] installer.nsi   -> VERSION={0}" -f $semver) -ForegroundColor Green

# ---- 4. Yaesu_Web_Control.csproj ----
# FileVersion and AssemblyVersion are four-part; the fourth field stays 0.
# These are what Windows shows in the installed exe's file properties, so
# leaving them behind makes every build claim a version it isn't.
$csHadBom = Test-Utf8Bom $csprojPath
$cs = Read-Utf8 $csprojPath
$cs = Replace-OrFail $cs '<Version>\d+\.\d+\.\d+</Version>'                 "<Version>$semver</Version>"                 'csproj Version'
$cs = Replace-OrFail $cs '<FileVersion>\d+\.\d+\.\d+\.\d+</FileVersion>'     "<FileVersion>$semver.0</FileVersion>"       'csproj FileVersion'
$cs = Replace-OrFail $cs '<AssemblyVersion>\d+\.\d+\.\d+\.\d+</AssemblyVersion>' "<AssemblyVersion>$semver.0</AssemblyVersion>" 'csproj AssemblyVersion'
Write-Utf8Preserving $csprojPath $cs $csHadBom
Write-Host ("[3/4] csproj          -> Version={0}  File/Assembly={0}.0" -f $semver) -ForegroundColor Green

# ---- 5. README.md shields.io badge ----
# Pattern matches the per-release Latest-release badge embedded in the URL.
# Leaves the Downloads badge (which references the installer asset URL) for
# any future per-release-asset bumps the user wants to make.
$rm = Read-Utf8 $readmePath
$rm = Replace-OrFail $rm 'Latest%20release-v\d+\.\d+\.\d+-' "Latest%20release-$vtag-" 'README Latest-release badge'
Write-Utf8NoBom $readmePath $rm
Write-Host ("[4/4] README.md       -> Latest-release badge={0}" -f $vtag) -ForegroundColor Green

Write-Host ""
Write-Host "All version markers bumped to $vtag (release date $ReleaseDate)." -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. git diff                                       # review the changes"
Write-Host "  2. Write the release-notes section in README.md, date-first:"
Write-Host "     ## $ReleaseDate - $vtag"
Write-Host "  3. .\scripts\finish-release.ps1 -Version $vtag    # commit, merge, tag, GitHub release"
