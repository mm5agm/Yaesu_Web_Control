# ss-probe.ps1 - READ-ONLY probe of the SS (SPECTRUM SCOPE) command.
#
# Sends only Read frames (SS P1 P2 ;). Sends NO Set frames, so nothing on the
# radio changes. Purpose is to confirm the answer format inferred from the
# layout-mangled CAT manual PDF before any of it is written into code.
#
# Run with YWC STOPPED (it holds COM4 exclusively).
# ASCII output only.

$port = New-Object System.IO.Ports.SerialPort 'COM4',38400,'None',8,'one'
$port.ReadTimeout = 60
try { $port.Open() } catch {
    Write-Output "OPEN FAILED (is YWC running and holding COM4?): $($_.Exception.Message)"
    exit 1
}
Start-Sleep -Milliseconds 250
$port.DiscardInBuffer()

function Ask([string]$cmd, [int]$budgetMs = 300) {
    $port.DiscardInBuffer()
    $port.Write($cmd)
    $sw  = [System.Diagnostics.Stopwatch]::StartNew()
    $buf = ''
    while ($sw.ElapsedMilliseconds -lt $budgetMs) {
        try { $buf += $port.ReadExisting() } catch {}
        if ($buf -match ';') { break }
        Start-Sleep -Milliseconds 5
    }
    return $buf.Trim()
}

Write-Output "Sanity check first - ID and the meter selection we already understand:"
Write-Output ("  ID;  -> '{0}'" -f (Ask 'ID;'))
Write-Output ("  MS;  -> '{0}'" -f (Ask 'MS;'))
Write-Output ''

$names = @{
    '0' = 'SPEED'; '1' = 'PEAK';  '2' = 'MARKER'; '3' = 'COLOR'
    '4' = 'LEVEL'; '5' = 'SPAN';  '6' = 'MODE';   '7' = 'AF-FFT/OSC'
    '8' = 'HOLD'
}

foreach ($p1 in @('0','1')) {
    $band = if ($p1 -eq '0') { 'MAIN' } else { 'SUB' }
    Write-Output "--- SS P1=$p1 ($band) ---"
    foreach ($p2 in @('0','1','2','3','4','5','6','7','8')) {
        $cmd = "SS$p1$p2;"
        $ans = Ask $cmd
        $label = $names[$p2]
        if ([string]::IsNullOrWhiteSpace($ans)) { $ans = '(no answer)' }
        Write-Output ("  {0,-7} {1,-12} -> '{2}'" -f $cmd, $label, $ans)
    }
    Write-Output ''
}

$port.Close()
Write-Output 'Done. No Set frames were sent - the radio is exactly as it was.'
