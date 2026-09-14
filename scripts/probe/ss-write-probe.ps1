# ss-write-probe.ps1 - the write half of the SS (SPECTRUM SCOPE) probe.
#
# ss-probe.ps1 confirmed the ANSWER format by reading only. This one confirms
# that Set frames actually land, which is the first bench gate in
# docs/design/scope-control-via-cat.md.
#
# For each sub-command it: reads the current value, writes a DIFFERENT value,
# reads back to confirm the radio took it, then writes the ORIGINAL value back.
# The radio is left exactly as it was found.
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

function Tell([string]$cmd) {
    $port.Write($cmd)
    Start-Sleep -Milliseconds 120
}

# Each test: P1P2, a friendly name, and a value to try that is not the current
# one. The alternate is chosen per sub-command so we never leave the scope in a
# state the operator would not recognise even if the restore failed.
$tests = @(
    @{ Key = '05'; Name = 'MAIN SPAN';   Alt = '4' }   # 20 kHz
    @{ Key = '08'; Name = 'MAIN HOLD';   Alt = '1' }   # ON
    @{ Key = '02'; Name = 'MAIN MARKER'; Alt = '0' }   # OFF
    @{ Key = '15'; Name = 'SUB SPAN';    Alt = '4' }
)

foreach ($t in $tests) {
    $key  = $t.Key
    $name = $t.Name

    $before = Ask "SS$key;"
    if ($before -notmatch "^SS$key(.{5});$") {
        Write-Output ("{0,-12} SKIPPED - unexpected read '{1}'" -f $name, $before)
        continue
    }
    $origField = $Matches[1]
    $origP3    = $origField.Substring(0,1)

    # Do not write the value it is already on - that proves nothing.
    $alt = $t.Alt
    if ($alt -eq $origP3) { $alt = if ($origP3 -eq '0') { '1' } else { '0' } }

    # NOTE: build the padded field with ${alt} + a literal string. Writing
    # "$alt`0000" looks right and is not - backtick-zero is a NUL character in
    # a double-quoted PowerShell string, so that sends SS054<NUL>000; instead
    # of SS0540000;. The radio happens to tolerate it, which is exactly why the
    # bug survives a casual read of the output.
    $field = '{0}0000' -f $alt

    Tell "SS$key$field;"
    $after = Ask "SS$key;"

    $took = ($after -match "^SS$key$alt")
    Write-Output ("{0,-12} was '{1}' -> wrote '{2}' -> reads '{3}'   {4}" -f `
        $name, $origField, $field, $after, $(if ($took) { 'WRITE OK' } else { 'NO CHANGE' }))

    # Restore whatever it was on when we arrived.
    Tell "SS$key$origField;"
    $restored = Ask "SS$key;"
    if ($restored -notmatch [regex]::Escape($origField)) {
        Write-Output ("             !! RESTORE FAILED - now reads '{0}', expected field '{1}'" -f $restored, $origField)
    }
}

$port.Close()
Write-Output ''
Write-Output 'Done. Every value that was changed has been written back to what it was.'
