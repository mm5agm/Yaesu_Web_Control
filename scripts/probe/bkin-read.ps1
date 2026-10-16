# Reads the FTdx101MP/D's break-in settings. It sends reads only and writes
# nothing to the radio.
#
#   ./scripts/probe/bkin-read.ps1
#
# The CAT manual's menu table can be read two ways for CW BK-IN TYPE and
# CW BK-IN DELAY (EX020111/EX020112, or EX020211/EX020212). This reads all of
# them next to SD; and BI;. Whichever pair answers with SD's value for the
# delay (04 = 200 ms) and 0 (SEMI) for the type is the right one.
# It opens the serial port itself, so YWC must be stopped first.

param(
    [string]$Port = 'COM4',
    [int]$Baud = 38400
)

$p = New-Object System.IO.Ports.SerialPort $Port, $Baud, 'None', 8, 'One'
$p.ReadTimeout = 1500
$p.Open()
try {
    foreach ($c in 'SD;', 'BI;', 'EX020111;', 'EX020112;', 'EX020211;', 'EX020212;') {
        $p.DiscardInBuffer()
        $p.Write($c)
        Start-Sleep -Milliseconds 300
        '{0,-11} -> {1}' -f $c, $p.ReadExisting()
    }
}
finally { $p.Close() }
