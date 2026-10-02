# Can a CW memory that is already playing be stopped over CAT?
#
# The FTdx101 CAT set has no stop for a KY playback (measured 2026-09-09:
# KY0/KY1/TX0/KR0/MX0/BI0 and rewriting the memory all let it run on).
# Flipping the mode out of CW and back does not stop it either (measured
# 2026-10-01, methods cwl and usb). The OM (On-The-Air CW Message Playback)
# says pressing the same memory key again during playback cancels it, so
# method samekey sends the same KY a second time.
#
#   ./scripts/probe/cw-stop-probe.ps1 -Method none      # control: let it run out
#   ./scripts/probe/cw-stop-probe.ps1 -Method cwl       # CW-U -> CW-L -> back
#   ./scripts/probe/cw-stop-probe.ps1 -Method usb       # CW-U -> USB  -> back
#   ./scripts/probe/cw-stop-probe.ps1 -Method samekey   # KYA; again, as the front-panel key
#
# MONITOR ONLY. It refuses to start unless break-in is OFF (BI0), the radio is
# receiving (TX0), MAIN is the transmit VFO (FT0) and MAIN is in CW, and it
# checks BI0 again immediately before the playback. With break-in off a memory
# plays to the sidetone monitor and no RF is transmitted (OM p.64). RI4 does
# not assert in that mode, so the result is by ear: listen for the sidetone
# stopping about -StopAfterSec into the message.
#
# Memory 5 is borrowed for the test text and put back afterwards, and so is
# MAIN's mode. Opens the serial port itself, so YWC must be stopped first.

param(
    [ValidateSet('none', 'cwl', 'usb', 'samekey')]
    [string]$Method = 'none',
    [double]$StopAfterSec = 3,
    [int]$FlipMs = 150,
    [string]$Port = 'COM4',
    [int]$Baud = 38400
)

$p = New-Object System.IO.Ports.SerialPort $Port, $Baud, 'None', 8, 'One'
$p.ReadTimeout = 1500
$p.Open()
$t0 = [Diagnostics.Stopwatch]::StartNew()
function Stamp($msg) { '{0,7:F2}s  {1}' -f $t0.Elapsed.TotalSeconds, $msg }
function Ask($cmd) {
    $p.DiscardInBuffer(); $p.Write($cmd); Start-Sleep -Milliseconds 250
    ($p.ReadExisting() -split ';' | Where-Object { $_ -like ($cmd.TrimEnd(';') + '*') } | Select-Object -Last 1)
}
function Send-Cat($cmd) { $p.Write($cmd); Stamp "sent $cmd" }

$savedMd = $null; $savedKm = $null
try {
    $bi = Ask 'BI;'; $tx = Ask 'TX;'; $ft = Ask 'FT;'; $md = Ask 'MD0;'
    "BI=$bi TX=$tx FT=$ft MD=$md"
    if ($bi -ne 'BI0') { throw "Break-in is not OFF ($bi). Refusing: this test must not transmit." }
    if ($tx -ne 'TX0') { throw "Radio is not receiving ($tx). Refusing." }
    if ($ft -ne 'FT0') { throw "MAIN is not the transmit VFO ($ft). Refusing." }
    if ($md -notin 'MD03', 'MD07') { throw "MAIN is not in CW ($md). Put VFO A in CW-U first." }
    $savedMd = $md

    $km = Ask 'KM5;'
    if ($km -notmatch '^KM5') { throw "Could not read memory 5 ('$km')." }
    $savedKm = $km.Substring(3)                 # text including the } terminator
    "memory 5 held: '$savedKm'"

    Send-Cat 'KM5PARIS PARIS PARIS PARIS PARIS PARIS PARIS PARIS};' | Out-Host
    Start-Sleep -Milliseconds 300
    $check = Ask 'KM5;'
    "memory 5 now: '$check'"
    if ($check -notlike 'KM5PARIS*') { throw "Memory 5 did not take the test text ('$check'). Refusing." }
    if ((Ask 'BI;') -ne 'BI0') { throw 'Break-in changed. Refusing.' }

    $t0.Restart()
    Send-Cat 'KYA;' | Out-Host                        # A = memory 5 (see project notes: 6-A, not 1-5)
    Start-Sleep -Milliseconds ([int]($StopAfterSec * 1000))

    $other = @{ cwl = @{ MD03 = 'MD07'; MD07 = 'MD03' }; usb = @{ MD03 = 'MD02'; MD07 = 'MD02' } }
    if ($Method -eq 'samekey') {
        Send-Cat 'KYA;' | Out-Host
    } elseif ($Method -ne 'none') {
        Send-Cat ("{0};" -f $other[$Method][$md]) | Out-Host
        Start-Sleep -Milliseconds $FlipMs
        Send-Cat "$md;" | Out-Host
    } else { Stamp 'control run: nothing sent, the message should run to its end (~13 s at 25 wpm, measured)' }

    Stamp 'LISTEN: did the sidetone stop here, or carry on?'
    Start-Sleep -Seconds 4
    Stamp ("TX=" + (Ask 'TX;') + "  MD=" + (Ask 'MD0;'))
}
finally {
    if ($savedKm) {
        # A playback may still be running in the control case; wait it out
        # before rewriting the memory it is playing.
        # samekey might restart the message rather than stop it.
        if ($Method -in 'none', 'samekey') { Start-Sleep -Seconds 15 }
        $p.Write("KM5$savedKm;"); Start-Sleep -Milliseconds 300
        "memory 5 restored: " + (Ask 'KM5;')
    }
    if ($savedMd) { $p.Write("$savedMd;"); Start-Sleep -Milliseconds 200; "mode restored: " + (Ask 'MD0;') }
    $p.Close()
}
