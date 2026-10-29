# The script lives in core/ so YWC and IWC share one copy (core/scripts/core-sync.ps1).
# Same switches: -Check [-Verify], -Pull, -Push.
& (Join-Path $PSScriptRoot '..\core\scripts\core-sync.ps1') @args
exit $LASTEXITCODE
