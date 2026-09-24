# Reads, or sets, the FTdx101MP/D's DATA SHIFT (SSB) menu - EX010405,
# "the carrier point in DATA mode", 0-3000 Hz in 10 Hz steps.
#
#   ./scripts/probe/data-shift.ps1              # read only
#   ./scripts/probe/data-shift.ps1 -Hz 1000     # set, then read back
#
# This touches that one menu item and nothing else. It opens the serial port
# itself, so YWC must be stopped first (the open fails if YWC holds the port).
# Used to test whether the spectrum's DATA-mode slide follows this menu (#172).

param(
    [ValidateRange(0, 3000)]
    [int]$Hz = -1,
    [string]$Port = 'COM4',
    [int]$Baud = 38400
)

if ($Hz -ge 0 -and $Hz % 10 -ne 0) { throw "DATA SHIFT is set in 10 Hz steps; $Hz is not one." }

$p = New-Object System.IO.Ports.SerialPort $Port, $Baud, 'None', 8, 'One'
$p.ReadTimeout = 1500
$p.Open()
try {
    if ($Hz -ge 0) {
        $p.DiscardInBuffer()
        $p.Write(('EX010405{0:D4};' -f $Hz))
        Start-Sleep -Milliseconds 300
    }
    $p.DiscardInBuffer()
    $p.Write('EX010405;')
    Start-Sleep -Milliseconds 300
    $answer = $p.ReadExisting()
    if ($answer -match 'EX010405(\d{4});') { "DATA SHIFT (SSB) = $([int]$Matches[1]) Hz" }
    else { "Unexpected answer: '$answer'" }
}
finally { $p.Close() }
