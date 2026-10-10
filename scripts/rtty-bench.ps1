<#
.SYNOPSIS
    Drives the RTTY tuner, Auto and reader against a known station and prints
    PASS/FAIL, so that the bench result is a command and not a memory.

.DESCRIPTION
    RTTY receive is a protocol-level feature: the tuner writes mode, IF width
    and the dial to the operator's radio, and a green build says nothing about
    any of it. This script is the standing proof. It tunes a station whose
    shift, speed and text are published by somebody other than us, drives the
    same HTTP API the panel drives, and checks the answers against a table of
    expected values carried in this file.

    The point is that it can be run again. Every number it judges against is
    data in the station table below, with a note saying where that number came
    from. A regression in the decoder, the analyser or the IF-width arithmetic
    shows up as a named FAIL rather than as an operator saying it used to work.

    IT NEVER TRANSMITS. Nothing here is a transmit path, and Invoke-Api
    refuses any route not in the receive-only whitelist, so one cannot be
    added by accident. If you extend this script, add the route to that list
    deliberately and keep it receive-only.

    What it checks, in order:

      1  the app answers and the radio is connected
      2  VFO A reaches the station's dial frequency
      3  the tuner starts, with audio open and no capture error
      4  the radio ends up in an RTTY mode
      5  the IF filter is at least the computed floor for this tone pair
         (the one claim in the manual that was arithmetic, never measured)
      6  something is reaching the filters at all
      7  Auto measures the station's real shift, speed and sense, and the
         pair it settles on is balanced across the passband
      8  the station was actually on the air for the window, and the reader
         copied text containing its published tokens with few framing errors
      9  the radio is put back where it was found

    Step 8 is sampled once a second and judged only over the seconds that had
    a signal in them, because the decoder emits characters at the baud rate
    whether it is hearing tones or hiss - so a bad stretch read in one lump
    looks like a half-broken decoder and says nothing about which half.

    CLOSE THE RTTY PANEL IN THE BROWSER BEFORE RUNNING THIS. There is one
    tuner and one pair of audio filters, and the tuner's effective
    Mark/Shift/Rev is whatever the most recent start asked for, whoever sent
    it - so an open panel overwrites this script from its own dropdowns on its
    keep-alive. The script re-posts its own figures every five seconds to win
    that race and to stop its lease idling out, and step 8 checks afterwards
    that the figures it asked for were still in force. If that check FAILS,
    close the panel and run it again; the result above it means nothing.

.PARAMETER Station
    Which entry of the table to use. -List prints them.

.PARAMETER DialMHz
    Override the station's starting dial frequency. The table's figure is a
    starting point, not a calibration: Auto moves the dial onto the tones from
    wherever it is started, and that move is part of what step 7 proves.

.PARAMETER Seconds
    How long to let the reader copy before judging the text. DWD send about
    six characters a second, so 90 is a couple of lines plus the header.

.PARAMETER SkipTune
    Leave the dial alone. For when the operator has already found the signal
    by ear and wants only the decode judged.

.PARAMETER NoNarrow
    Do not narrow the IF filter before starting. Narrowing is the default
    because the alternative is a step 5 that only ever runs once: changing to
    RTTY brings the radio's OWN stored RTTY width with it, so after one run
    the radio remembers the widened figure and there is nothing left to widen.
    A run with -NoNarrow reports step 5 as a WARN saying so.

.PARAMETER NoRestore
    Leave the radio in RTTY on the station afterwards instead of putting the
    mode, width and frequency back. Useful when a FAIL needs looking at on
    the radio's own screen.

.PARAMETER Out
    Write the full transcript and every reply as JSON here, for attaching to
    a PR or a bug report.

.EXAMPLE
    .\scripts\rtty-bench.ps1
    The default run: DDK9 on 10 MHz, 90 seconds, radio restored afterwards.

.EXAMPLE
    .\scripts\rtty-bench.ps1 -List
    What stations are in the table, and what each one proves.

.EXAMPLE
    .\scripts\rtty-bench.ps1 -Station DDK9 -Seconds 180 -Out bench\rtty-ddk9.json
    A longer copy, saved. Three minutes gets the whole bulletin cycle.

.EXAMPLE
    .\scripts\rtty-bench.ps1 -SkipTune -NoRestore
    Judge what is already tuned, and leave it tuned.

.NOTES
    KEEP THIS FILE ASCII, NO BOM. PowerShell 5.1 reads an un-marked file as
    CP1252, where the third byte of a UTF-8 em-dash (0x94) is a curly closing
    quote that it honours as a string delimiter. The parse then fails twenty
    lines further down with an error naming neither the character nor the
    line. Same trap as scripts\cw-bench-record.ps1.

    PowerShell 5.1 also has no ternary, no null-coalescing and no &&, hence
    the long-hand if/else throughout.

    Last bench result, FTdx101MP, 2026-10-10: 27 passed, 0 failed. DDK9's
    bulletin copied for 119 of 119 seconds at 6.6 characters a second with
    4.6 percent framing errors, all three published frequencies intact and
    the date in the text. Auto measured shift 445 against a true 450 and
    50.5 baud against a true 50, moved the dial 417 Hz onto the tones, and
    the IF filter went from code 2 to code 15 - 1700 Hz, the narrowest rung
    at or above the computed 1650 Hz floor.
#>
[CmdletBinding()]
param(
    [string] $Station = 'DDK9',
    [switch] $List,
    [string] $BaseUrl = 'http://localhost:8080',
    [double] $DialMHz = 0,
    [int]    $Seconds = 90,
    [switch] $SkipTune,
    [switch] $NoNarrow,
    [switch] $NoRestore,
    [string] $Out
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# The truth table.
#
# Every figure here is somebody else's measurement or somebody else's
# publication, never this app's own output. That is the whole value of it: a
# test that asserts what the code currently does proves only that the code has
# not changed. Each entry says where its numbers came from so that a future
# reader can tell a published fact from a bench observation.
# ---------------------------------------------------------------------------
$Stations = @{

    'DDK9' = @{
        Name = 'DDK9 - Deutscher Wetterdienst, Pinneberg'
        What = 'Continuous RTTY weather bulletin, 24 hours a day, strong into Scotland by day and night. Published parameters, published text, and it is always there, which is why it is the default.'

        # Published by DWD. The assigned frequency, not a dial reading.
        CentreKHz = 10100.8

        # A starting dial only, and on this radio it is 417 Hz out - which is
        # exactly the cross-radio trap this comment warned about before it was
        # measured. 10.100998 came from Icom Web Control on 2026-10-09. On the
        # FTdx101MP on 2026-10-10 the mark arrived at 2541.7 Hz audio against
        # the panel's 2125, and Auto moved the dial to 10.100581 to bring it
        # home. Fed the right tones at the WRONG dial the pair sits 14 dB
        # apart and four minutes of it decoded nothing, so Auto's dial move is
        # not a convenience - it is what makes this station readable here.
        #
        # It is left at the Icom figure on purpose: Auto has to be able to
        # find a signal that is several hundred Hz from where it was told to
        # look, and every run proves it again. Pass -DialMHz 10.100581 to
        # start from this radio's measured dial instead.
        DialMHz = 10.100998

        # The panel's own settings to start with. Mark 2125 is the Yaesu
        # default and the radio's own RTTY MARK menu default.
        MarkHz = 2125
        Baud   = 50

        # Measured, not chosen from a list. 450 is not an amateur rung - the
        # Yaesu rungs are 170/200/425/850 - and Auto reporting 450 rather than
        # snapping to 425 is itself one of the things under test.
        ExpectShift      = 450
        ShiftToleranceHz = 25

        ExpectBaud    = 50
        BaudTolerance = 2

        # Which way round the tones are, and this radio wants the OPPOSITE of
        # the Icom for the same station at the same dial. Measured here on
        # 2026-10-10: Rev ON decoded the bulletin cleanly twice, Rev OFF left
        # the space tone 7.1 dB down and gave nothing. For ITA2 the two senses
        # are mutually exclusive - a wrong sense decodes to the complementary
        # bit pattern and cannot produce readable English - so one clean copy
        # settles it.
        #
        # Why it differs from the Icom, as far as the bench can tell: in RTTY-L
        # this radio inverts the audio against the RF, so the station's mark
        # arrives as the HIGHER of the two tones. Mark 2125 with Rev on puts
        # the pair at 1675 / 2125, straddling this radio's fixed 1800 Hz
        # passband centre; Rev off would put it at 2125 / 2575, with the space
        # out near the edge of the filter. The two apps' TonesFor also differ -
        # IWC's reads the mode and flips on RTTY-U, this one keys on reverse
        # alone - which is worth an eye, though it is not what this result
        # turns on.
        ExpectReverse    = $true
        ReverseIsSettled = $true

        # The IF-filter floor for mark 2125, shift 450, 50 baud against this
        # radio's fixed passband centre near 1800 Hz:
        #   worst tone = 2125 + 450 = 2575, which is 775 Hz off centre
        #   floor      = 2 * 775 + 2 * 50 = 1650
        # COMPUTED from core RttyIfWidth.MinimumHz and written into the manual
        # as about 1650. Step 5 is the first thing that has ever checked it
        # against a radio. If this FAILs, the manual is wrong, not the test.
        ExpectFloorHz = 1650

        # The rung the floor implies, and the one to check, because the radio
        # cannot be set to 1650. The FTdx101MP's CW/RTTY ladder runs
        # ... 13=1200, 14=1400, 15=1700, 16=2000, 17=2400 (YaesuIfWidth), so
        # the narrowest rung at or above 1650 is 1700. MEASURED on 2026-10-10:
        # narrowed to code 2 by hand, the tuner chose code 15 unprompted.
        ExpectWidthCode = '15'
        ExpectWidthHz   = 1700

        # The bulletin's own header names the sister transmitters, so the text
        # can be judged without anybody reading RTTY by ear. Only ONE is
        # needed, because the header goes out once a cycle and whether the
        # window opens before it is luck: a run on 2026-10-10 started two
        # characters into the word FREQUENCIES and so saw none of the three
        # callsigns on an otherwise perfect copy. Failing that run would have
        # been the test blaming the decoder for its own timing.
        Tokens       = @('DDK2', 'DDH7', 'DDK9', 'KHZ')
        TokensNeeded = 1

        # The published frequencies, spelt as they came off the air on
        # 2026-10-10. THIS is the strong check: multi-digit numbers and a
        # decimal point cannot come out right by accident, so one of these
        # intact proves the whole chain - audio, filters, tone decision, ITA2
        # and the figures shift. One is required and all three are reported,
        # because how many arrive depends on where in the cycle the window
        # opened and on the fading, not on the code.
        FreqTokens       = @('4583 KHZ', '7646 KHZ', '10100.8 KHZ')
        FreqTokensNeeded = 1

        # Per second, not a flat total, so the thresholds mean the same thing
        # whatever -Seconds is set to. DWD send 50 baud ITA2, which is about
        # six characters a second; 4 leaves room for the RYRY idle and the
        # line breaks.
        MinCharsPerSecond = 4.0
        MaxFramingPercent = 5

        # Activity separates a station from the noise it is sitting in: a
        # clean one reads near 0.8 and noise alone near 0.33. Used twice -
        # once as the quality floor, and once to decide whether there was
        # anything to judge at all.
        #
        # It is NOT a measure of whether anything is decodable, and it must
        # not be read as one. Measured on 2026-10-10: with the dial 417 Hz out
        # the tuner sat at activity 0.55 for four minutes and produced
        # 909 characters of complete garbage, because it was hearing one tone
        # loudly. Only the tone GAP told the two cases apart - see
        # MaxToneGapDb, which is the check to look at first when a run
        # fails with the activity figures looking healthy.
        MinActivity = 0.55

        # How many seconds of signal there have to be before the copy is
        # judged at all - an absolute count, not a fraction of the window.
        # DDK9 sends its header and its RYRY idle in short bursts with long
        # quiet stretches between, and a window that is mostly quiet is the
        # station's business, not the decoder's: judged in one lump it gives
        # about 40 percent framing errors on pure noise, which reads as a
        # broken decoder and is nothing of the kind. Twelve live seconds is
        # enough to put a rate on the copy; whether the other two minutes
        # were quiet says nothing either way.
        MinSignalSeconds = 12

        # The app calls anything below about 0.4 a guess, so that is the line
        # here too rather than a figure of my own choosing. Measured on this
        # radio: 0.48 on a good copy, which is lower than the Icom managed on
        # the same station - the fixed 1800 Hz passband puts the space tone
        # 775 Hz off centre against the mark's 325, so the pair arrives
        # lopsided (space 4.4 dB down) and the estimate is softer for it.
        MinConfidence = 0.4

        # Measured on the FTdx101MP on 2026-10-10: 0.5 dB with Auto's figures
        # and Auto's dial, 3.1 dB on the table's guess, and 13-15 dB with the
        # dial 417 Hz out - which decoded nothing at all for four minutes.
        # Eight leaves room for this radio's fixed passband delivering the
        # pair lopsided without letting a one-armed signal through.
        MaxToneGapDb = 8.0
    }

    # Room for the next one. A beacon or a bulletin with published text is
    # what qualifies; another amateur QSO does not, because there is then
    # nothing to check the copy against. NCDXF on 14.100 is CW, not RTTY.
}

if ($List) {
    Write-Host ''
    Write-Host 'Stations in the table:' -ForegroundColor Cyan
    foreach ($key in ($Stations.Keys | Sort-Object)) {
        $s = $Stations[$key]
        Write-Host ''
        Write-Host ("  {0}" -f $key) -ForegroundColor White
        Write-Host ("    {0}" -f $s.Name)
        Write-Host ("    dial {0} MHz, mark {1} Hz, shift {2} Hz, {3} baud" -f $s.DialMHz, $s.MarkHz, $s.ExpectShift, $s.ExpectBaud)
        Write-Host ("    {0}" -f $s.What) -ForegroundColor DarkGray
    }
    Write-Host ''
    exit 0
}

if (-not $Stations.ContainsKey($Station)) {
    Write-Host ("Unknown station '{0}'. Try -List." -f $Station) -ForegroundColor Red
    exit 2
}
$st = $Stations[$Station]

if ($DialMHz -gt 0) { $dial = $DialMHz } else { $dial = $st.DialMHz }
$dialHz = [long][Math]::Round($dial * 1000000)

# ---------------------------------------------------------------------------
# Plumbing
# ---------------------------------------------------------------------------

# Receive only, and enforced. A transmit route cannot get into this script
# without being typed into this list on purpose.
$AllowedRoutes = @(
    'api/cat/status',
    'api/cat/frequency/a',
    'api/cat/mode/a',
    'api/cat/ifwidth/a',
    'api/rtty/tuner',
    'api/rtty/tuner/start',
    'api/rtty/tuner/stop',
    'api/rtty/auto',
    'api/rtty/radio-tones',
    'api/rtty/reader',
    'api/rtty/reader/start',
    'api/rtty/reader/stop',
    'api/rtty/reader/clear'
)

# The SH code to narrow to before starting, so that the widen path fires.
# Code 2 in RTTY on the FTdx101MP is 100 Hz. Deliberately absurd for RTTY:
# nothing carries a tone pair through it, so whatever the tuner chooses
# instead is a figure it worked out rather than one it inherited.
$NarrowCode = '2'

$script:CallLog = New-Object System.Collections.ArrayList
$script:Results = New-Object System.Collections.ArrayList

function Invoke-Api {
    param(
        [Parameter(Mandatory)] [string] $Route,
        [string] $Method = 'GET',
        $Body,
        [string] $Query,
        [int] $TimeoutSec = 30
    )
    if ($AllowedRoutes -notcontains $Route) {
        throw "Route '$Route' is not in the receive-only whitelist. If it belongs here, add it to the AllowedRoutes list on purpose."
    }
    $uri = "$BaseUrl/$Route"
    if ($Query) { $uri = $uri + '?' + $Query }

    $call = @{ Uri = $uri; Method = $Method; TimeoutSec = $TimeoutSec }
    if ($null -ne $Body) {
        $call.Body        = ($Body | ConvertTo-Json -Depth 5 -Compress)
        $call.ContentType = 'application/json'
    }
    elseif ($Method -eq 'POST') {
        # An empty POST body is not the same as no body: the tuner reads a
        # missing StartRequest as all-defaults and silently drops back to a
        # 170 Hz shift, which looks exactly like a failure to measure.
        $call.Body        = '{}'
        $call.ContentType = 'application/json'
    }
    $reply = Invoke-RestMethod @call
    [void]$script:CallLog.Add([pscustomobject]@{
        at = (Get-Date).ToString('o'); route = $Route; method = $Method
        query = $Query; sent = $Body; got = $reply
    })
    return $reply
}

function Add-Check {
    param(
        [Parameter(Mandatory)] [string] $Step,
        [Parameter(Mandatory)] [string] $What,
        [Parameter(Mandatory)] [bool]   $Ok,
        [string] $Detail,
        [switch] $Warn
    )
    if ($Ok)       { $state = 'PASS'; $colour = 'Green' }
    elseif ($Warn) { $state = 'WARN'; $colour = 'Yellow' }
    else           { $state = 'FAIL'; $colour = 'Red' }

    [void]$script:Results.Add([pscustomobject]@{ step = $Step; what = $What; state = $state; detail = $Detail })
    Write-Host ("  [{0}] {1,-2} {2}" -f $state, $Step, $What) -ForegroundColor $colour
    if ($Detail) { Write-Host ("            {0}" -f $Detail) -ForegroundColor DarkGray }
}

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

$client = 'bench-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

# ---------------------------------------------------------------------------
Write-Host ''
Write-Host ('RTTY bench: {0}' -f $st.Name) -ForegroundColor White
Write-Host ('  {0}  dial {1} MHz  mark {2} Hz  expecting shift {3} Hz at {4} baud' -f $BaseUrl, $dial, $st.MarkHz, $st.ExpectShift, $st.ExpectBaud) -ForegroundColor DarkGray
Write-Host '  Receive only. This script has no transmit path.' -ForegroundColor DarkGray

$savedFreq = $null
$savedMode = $null
$tunerUp   = $false
$readerUp  = $false
$frame     = $null
$text      = ''
$samples   = @()
$narrowed  = $false

try {
    # -- 1. the app and the radio ------------------------------------------
    Write-Step '1. The app and the radio'
    $status = $null
    try { $status = Invoke-Api -Route 'api/cat/status' -TimeoutSec 10 } catch { }
    if ($null -eq $status) {
        Add-Check -Step '1' -What 'the app answers on this address' -Ok $false -Detail ("Nothing at {0}. Start the app first: dotnet run -f net10.0-windows" -f $BaseUrl)
        throw 'halt'
    }
    Add-Check -Step '1' -What 'the app answers on this address' -Ok $true -Detail ("model {0}, VFO A {1} Hz, mode {2}, IF width code {3}" -f $status.radioModel, $status.vfoA.frequency, $status.vfoA.mode, $status.vfoA.ifWidth)

    # isConnected plus a real frequency. The flag alone can be true while the
    # first poll has not landed, and a frequency alone cannot tell a stale
    # cached value from a live one.
    $connected = ([bool]$status.isConnected) -and ($null -ne $status.vfoA.frequency) -and ([long]$status.vfoA.frequency -gt 0)
    Add-Check -Step '1' -What 'the radio is connected and reporting a frequency' -Ok $connected -Detail 'A zero or absent frequency means the serial port never opened - check the Diagnostics page.'
    if (-not $connected) { throw 'halt' }

    $savedFreq = [long]$status.vfoA.frequency
    $savedMode = [string]$status.vfoA.mode

    # -- 2. the dial --------------------------------------------------------
    Write-Step '2. The dial'
    if ($SkipTune) {
        Add-Check -Step '2' -What 'dial left alone as asked' -Ok $true -Detail ("still on {0} Hz" -f $savedFreq)
    }
    else {
        [void](Invoke-Api -Route 'api/cat/frequency/a' -Method POST -Body @{ frequencyHz = $dialHz })
        Start-Sleep -Milliseconds 700
        $status = Invoke-Api -Route 'api/cat/status'
        $got    = [long]$status.vfoA.frequency
        $near   = [Math]::Abs($got - $dialHz) -le 20
        Add-Check -Step '2' -What ("VFO A reached {0} Hz" -f $dialHz) -Ok $near -Detail ("radio reads {0} Hz" -f $got)
    }

    # -- 2b. narrow the filter on purpose ----------------------------------
    #
    # So that step 5 has something to measure. Changing to RTTY brings the
    # radio's own stored RTTY width with it, which after one run of this script
    # is the widened figure - so without this the widen path fires once, on the
    # first run ever, and every run after it says "already wide enough".
    #
    # The mode has to go first: widths belong to the mode they were set in, so
    # a code written in AM is not the code RTTY-L will come back with. The
    # tuner then saves this narrow width as the pre-session one and puts it
    # back on stop, which leaves the radio's RTTY width narrow afterwards -
    # which is how it was found on 2026-10-10 anyway.
    $narrowed = $false
    if ((-not $NoNarrow) -and (-not $SkipTune)) {
        Write-Step '2b. Narrowing the filter so that step 5 has work to do'
        [void](Invoke-Api -Route 'api/cat/mode/a' -Method POST -Body @{ mode = 'RTTY-L' })
        Start-Sleep -Milliseconds 600
        [void](Invoke-Api -Route 'api/cat/ifwidth/a' -Method POST -Body @{ code = $NarrowCode })
        Start-Sleep -Milliseconds 600
        $status   = Invoke-Api -Route 'api/cat/status'
        $narrowed = ([string]$status.vfoA.ifWidth -eq $NarrowCode)
        Add-Check -Step '2b' -What ("the filter is narrow before the tuner starts (code {0})" -f $NarrowCode) -Ok $narrowed -Detail ("mode {0}, IF width code {1}. Code {2} in RTTY on this radio is 100 Hz, far too narrow to carry any shift, so the tuner has to widen it and step 5 can read the figure it chose." -f $status.vfoA.mode, $status.vfoA.ifWidth, $NarrowCode)
    }

    # -- 3. the tuner -------------------------------------------------------
    Write-Step '3. The tuner'
    # Every start body is explicit, and the figures are kept in these four
    # variables so that step 8's keep-alive can send exactly the same ones.
    # An empty body is NOT "leave it as it is" - it falls back to the built-in
    # defaults and quietly puts the shift back to 170.
    $askMark    = [double]$st.MarkHz
    $askShift   = [int]$st.ExpectShift
    $askReverse = $false
    $askBaud    = [double]$st.Baud
    $frame = Invoke-Api -Route 'api/rtty/tuner/start' -Method POST -Query ("client={0}" -f $client) -Body @{ markHz = $askMark; shiftHz = $askShift; reverse = $askReverse; baud = $askBaud }
    $tunerUp = $true
    Start-Sleep -Seconds 2
    $frame = Invoke-Api -Route 'api/rtty/tuner' -Query ("points=0&client={0}" -f $client)

    Add-Check -Step '3' -What 'the tuner is running' -Ok ([bool]$frame.running)
    Add-Check -Step '3' -What 'the audio device opened' -Ok ([bool]$frame.audioDevicesOpen) -Detail 'No audio means no figure and no decode, whatever the radio is doing.'
    Add-Check -Step '3' -What 'no capture error' -Ok ([string]::IsNullOrEmpty($frame.captureError)) -Detail $frame.captureError
    if ($frame.modeNote) { Write-Host ("            radio changed: {0}" -f $frame.modeNote) -ForegroundColor DarkGray }

    # -- 4. the mode --------------------------------------------------------
    Write-Step '4. The mode'
    $status  = Invoke-Api -Route 'api/cat/status'
    $modeNow = [string]$status.vfoA.mode
    $isRtty  = $modeNow -match 'RTTY'
    Add-Check -Step '4' -What 'the radio is in an RTTY mode' -Ok $isRtty -Detail ("mode reads {0} (was {1}). The tuner sets RTTY-L unless the Settings switch says not to." -f $modeNow, $savedMode)

    # -- 5. the IF filter ---------------------------------------------------
    #
    # The floor in the table is arithmetic from core RttyIfWidth.MinimumHz, and
    # this step is what turns it into a measurement.
    #
    # What is judged is the width the RADIO ended up on, not the wording of
    # the tuner's status note. On 2026-10-10 the note said only "Mode set to
    # RTTY-L." while the width went from code 2 to code 15 underneath it, so a
    # test reading the note called a working widen a failure. The note is
    # still read, because when it does carry the Hz it is better evidence than
    # a code - but it is no longer the thing that decides.
    Write-Step '5. The IF filter'
    $note     = [string]$frame.modeNote
    $endCode  = [string]$status.vfoA.ifWidth
    if ($narrowed) {
        $gotRung = ($endCode -eq [string]$st.ExpectWidthCode)
        Add-Check -Step '5' -What ("the tuner widened the filter to code {0} ({1} Hz)" -f $st.ExpectWidthCode, $st.ExpectWidthHz) -Ok $gotRung -Detail ("narrowed to code {0} on purpose, ended on code {1}; expected {2}. The computed floor for mark {3} / shift {4} / {5} baud is {6} Hz and the narrowest rung at or above it is {7} Hz. This is the figure the manual states and it had never been measured before this test existed. Tuner note: {8}" -f $NarrowCode, $endCode, $st.ExpectWidthCode, $st.MarkHz, $st.ExpectShift, $st.Baud, $st.ExpectFloorHz, $st.ExpectWidthHz, $note)
    }
    if ($note -match 'widened to (\d+) Hz') {
        $widened = [int]$Matches[1]
        Add-Check -Step '5' -What ("the filter reaches the {0} Hz floor for this tone pair" -f $st.ExpectFloorHz) -Ok ($widened -ge $st.ExpectFloorHz) -Detail ("tuner widened to {0} Hz; the computed floor for mark {1} / shift {2} / {3} baud is {4} Hz. This figure is in the manual and had never been measured before this test existed." -f $widened, $st.MarkHz, $st.ExpectShift, $st.Baud, $st.ExpectFloorHz)
    }
    elseif ($note -match 'not known here') {
        Add-Check -Step '5' -What 'the filter reaches the floor' -Ok $false -Detail ("the tuner could not judge the width: {0}" -f $note)
    }
    else {
        # Nothing to widen means the filter was already wide enough, which is
        # a pass the script cannot put a number on - the width only reads back
        # as an SH code out here, and turning that into Hz needs the model and
        # mode table that lives inside the app.
        if ($narrowed) {
            # The check above has already judged this from the radio's own
            # width, which is the better witness. Say what the note said and
            # leave it at that.
            Write-Host ("            the widen is judged from the radio's width above; the note said: {0}" -f $note) -ForegroundColor DarkGray
        }
        else {
            Add-Check -Step '5' -What 'the filter was already wide enough' -Ok $true -Warn -Detail ("no widening note, so the width in place already carried the pair. IF width code reads {0}. Run without -NoNarrow to make the widen path fire and have this step measure something." -f $status.vfoA.ifWidth)
        }
    }

    # -- 6. both tones, as the table guessed them --------------------------
    #
    # A reading, not a verdict. At this point the tuner's filters are wherever
    # the table's mark, shift and sense put them, and the table's job is only
    # to get near enough for Auto to hear the signal. A wide gap here is
    # ordinary: it is what a mis-set sense looks like, and settling the sense
    # is step 7's work. The verdict on the pair comes after Auto has spoken.
    Write-Step '6. Both tones, before Auto'
    $gapBefore = [Math]::Abs([double]$frame.markDb - [double]$frame.spaceDb)
    Write-Host ("            mark {0:N1} dB, space {1:N1} dB, gap {2:N1} dB at the table's mark {3} / shift {4} / reverse {5}" -f $frame.markDb, $frame.spaceDb, $gapBefore, $st.MarkHz, $st.ExpectShift, $false) -ForegroundColor DarkGray
    Add-Check -Step '6' -What 'there is a signal on the filters at all' -Ok ([double]$frame.markDb -gt -90.0) -Detail 'A mark level at the floor means nothing is reaching the audio - check the dial, the mode and the sound device before reading anything below.'

    # -- 7. Auto ------------------------------------------------------------
    #
    # Twice on purpose. A first pass that finds only one tone is designed to
    # move the dial and refuse the figures, so the second pass is the one that
    # should measure - and running both is the standing test of that fix.
    Write-Step '7. Auto'
    $auto = Invoke-Api -Route 'api/rtty/auto' -Method POST -Body @{ markHz = $st.MarkHz } -TimeoutSec 60
    Write-Host ("            pass 1: ok={0} shift={1} baud={2} conf={3:N2} half={4} {5}" -f $auto.ok, $auto.shiftHz, $auto.baud, $auto.confidence, $auto.halfSignal, $auto.reason) -ForegroundColor DarkGray
    if ($auto.halfSignal -or (-not $auto.ok)) {
        Start-Sleep -Seconds 2
        $auto = Invoke-Api -Route 'api/rtty/auto' -Method POST -Body @{ markHz = $st.MarkHz } -TimeoutSec 60
        Write-Host ("            pass 2: ok={0} shift={1} baud={2} conf={3:N2} half={4} {5}" -f $auto.ok, $auto.shiftHz, $auto.baud, $auto.confidence, $auto.halfSignal, $auto.reason) -ForegroundColor DarkGray
    }

    Add-Check -Step '7' -What 'Auto made sense of the signal' -Ok ([bool]$auto.ok) -Detail $auto.reason
    if ($auto.ok) {
        Add-Check -Step '7' -What 'Auto did not report half a signal' -Ok (-not [bool]$auto.halfSignal) -Detail ("tone balance {0:N1} dB. Half a signal means one tone fell outside the scan; the dial move stands but the figures are refused." -f $auto.toneBalanceDb)

        $dShift = [Math]::Abs([int]$auto.shiftHz - $st.ExpectShift)
        Add-Check -Step '7' -What ("Auto measured the shift as {0} Hz" -f $st.ExpectShift) -Ok ($dShift -le $st.ShiftToleranceHz) -Detail ("measured {0} Hz (out by {1}). {2} is not an amateur rung, so this also proves Auto measures rather than snapping to a list." -f $auto.shiftHz, $dShift, $st.ExpectShift)

        $dBaud = [Math]::Abs([double]$auto.baud - $st.ExpectBaud)
        Add-Check -Step '7' -What ("Auto measured the speed as {0} baud" -f $st.ExpectBaud) -Ok ($dBaud -le $st.BaudTolerance) -Detail ("measured {0:N1} baud, snapped to {1}" -f $auto.baud, $auto.snappedBaud)

        $senseOk = ([bool]$auto.reverse -eq $st.ExpectReverse)
        if ($st.ReverseIsSettled) {
            Add-Check -Step '7' -What 'Auto got the tone sense the right way round' -Ok $senseOk -Detail ("reverse={0}, expected {1}" -f $auto.reverse, $st.ExpectReverse)
        }
        else {
            Add-Check -Step '7' -What 'Auto reported a tone sense' -Ok $senseOk -Warn -Detail ("reverse={0}, table says {1}, and the table is not settled for this radio yet. A WARN here is not a defect - it is this test admitting it does not know. Settle it by running the two senses back to back on a signal that stays up, then set ReverseIsSettled." -f $auto.reverse, $st.ExpectReverse)
        }

        Add-Check -Step '7' -What ("Auto was confident, not guessing (at least {0:N2})" -f $st.MinConfidence) -Ok ([double]$auto.confidence -ge $st.MinConfidence) -Detail ("confidence {0:N2}; below about 0.4 the panel itself calls it a guess" -f $auto.confidence)

        # Take Auto's answer on, the way the panel does, and judge the pair
        # that the decode is actually going to use. This is the check that
        # earns its place: before Auto the filters were on the table's guess,
        # and on this radio a wrong sense puts the far tone out at the edge of
        # a passband that does not move - 7.1 dB down, measured on 2026-10-10.
        # A real pair sits within a few dB; a fragment is 13-28 dB apart.
        $askShift   = [int]$auto.shiftHz
        $askReverse = [bool]$auto.reverse
        $askBaud     = [double]$auto.baud
        [void](Invoke-Api -Route 'api/rtty/tuner/start' -Method POST -Query ("client={0}" -f $client) -Body @{ markHz = $askMark; shiftHz = $askShift; reverse = $askReverse; baud = $askBaud })
        Start-Sleep -Seconds 2
        $frame = Invoke-Api -Route 'api/rtty/tuner' -Query ("points=0&client={0}" -f $client)
        $gapAfter = [Math]::Abs([double]$frame.markDb - [double]$frame.spaceDb)
        Add-Check -Step '7' -What 'with Auto applied, the two tone levels are within 8 dB' -Ok ($gapAfter -le 8.0) -Detail ("mark {0:N1} dB, space {1:N1} dB, gap {2:N1} dB on mark {3} / shift {4} / reverse {5} (it was {6:N1} dB on the table's guess). A gap still this wide means one tone is outside the filter - move the dial, or widen by hand." -f $frame.markDb, $frame.spaceDb, $gapAfter, $st.MarkHz, $auto.shiftHz, $auto.reverse, $gapBefore)
    }

    # -- 8. the copy --------------------------------------------------------
    #
    # Sampled once a second rather than read once at the end, because the
    # decoder emits characters at the baud rate whether it is hearing tones or
    # hiss. Read in one lump, a window with a bad stretch in it comes out as
    # one middling framing-error figure that reads like a half-broken decoder
    # and says nothing about which half. Per-second samples separate "the
    # station was not there" from "the decode was wrong", and the absence of a
    # signal is reported as its own named result rather than as a failure.
    #
    # This sampling was first written on a misreading, and the misreading is
    # worth recording. Three runs showed fifteen perfect seconds and then two
    # minutes of hash, and it was put down to DDK9 breaking between bulletins.
    # It was not: it was the lease expiring, 15 s being exactly IdleStop. A
    # 240-second probe that touched nothing settled it, and the keep-alive
    # above is the fix. The station turned out to be sending continuously, so
    # a run now expects every second to be live - but the gap handling stays,
    # because propagation is real even when the schedule is not the problem.
    Write-Step ("8. The copy ({0} seconds)" -f $Seconds)
    [void](Invoke-Api -Route 'api/rtty/reader/start' -Method POST)
    $readerUp = $true
    [void](Invoke-Api -Route 'api/rtty/reader/clear' -Method POST)

    $text     = ''
    $cursor   = 0
    $samples  = New-Object System.Collections.ArrayList
    $lastCh   = 0
    $lastEr   = 0
    $deadline = (Get-Date).AddSeconds($Seconds)
    $snap     = $null
    $tick     = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1000
        $tick = $tick + 1

        # The keep-alive, and it is load-bearing. Two separate reasons, both
        # of which were measured on 2026-10-10 as fifteen perfect seconds
        # followed by two minutes of hash:
        #
        #   * A lease idles out after 15 s and only POST start renews it. A
        #     GET does not, however often it is sent, so a loop that only
        #     polls loses the tuner a quarter of a minute in.
        #   * The tuner's effective Mark/Shift/Rev is whatever the MOST
        #     RECENT start asked for, whichever client sent it. An open RTTY
        #     panel in a browser re-starts on its own keep-alive with its own
        #     dropdown values, so a script that posts once gets overwritten.
        #     Re-posting is what the panel itself does; it re-tones a running
        #     capture rather than restarting it.
        if (($tick % 5) -eq 0) {
            try { [void](Invoke-Api -Route 'api/rtty/tuner/start' -Method POST -Query ("client={0}" -f $client) -Body @{ markHz = $askMark; shiftHz = $askShift; reverse = $askReverse; baud = $askBaud }) } catch { }
        }

        $snap = Invoke-Api -Route 'api/rtty/reader' -Query ("since={0}" -f $cursor)
        if ($snap.text) { $text = $text + [string]$snap.text }
        $cursor = [long]$snap.cursor

        # The tone levels, every second. The gap between them is a better
        # witness than activity: on this radio a working pair sat 0.5 dB
        # apart and a pair with the dial 417 Hz out sat 14 dB apart for four
        # minutes while activity read a healthy 0.55 the whole time.
        $tf = $null
        try { $tf = Invoke-Api -Route 'api/rtty/tuner' -Query ("points=0&client={0}" -f $client) } catch { }
        $mDb = -99.0
        $sDb = -99.0
        if ($null -ne $tf -and $null -ne $tf.markDb) { $mDb = [double]$tf.markDb; $sDb = [double]$tf.spaceDb }

        $ch = [long]$snap.characters
        $er = [long]$snap.framingErrors
        [void]$samples.Add([pscustomobject]@{
            activity = [double]$snap.activity
            dChars   = $ch - $lastCh
            dErrors  = $er - $lastEr
            markDb   = $mDb
            spaceDb  = $sDb
            gapDb    = [Math]::Abs($mDb - $sDb)
        })
        $lastCh = $ch
        $lastEr = $er

        $left = [int]($deadline - (Get-Date)).TotalSeconds
        Write-Host ("`r            {0,3}s left  {1} chars  {2} framing errors  activity {3:N2}  gap {4,5:N1} dB   " -f $left, $ch, $er, $snap.activity, [Math]::Abs($mDb - $sDb)) -NoNewline
    }
    Write-Host ''

    # Nothing below means anything if the tuner spent the window on somebody
    # else's settings, so this is checked before the copy is judged. A FAIL
    # here is an instruction, not a defect: close the RTTY panel in the
    # browser and run it again.
    $held = Invoke-Api -Route 'api/rtty/tuner' -Query ("points=0&client={0}" -f $client)
    $sameShift = ([int]$held.shiftHz -eq $askShift)
    $sameRev   = ([bool]$held.reverse -eq $askReverse)
    $sameMark  = ([Math]::Abs([double]$held.markHz - $askMark) -lt 1.0)
    Add-Check -Step '8' -What 'the tuner still had the settings this test asked for' -Ok ($sameShift -and $sameRev -and $sameMark) -Detail ("asked for mark {0} / shift {1} / reverse {2}, ended on mark {3} / shift {4} / reverse {5}. There is one tuner and one pair of filters, and the most recent start wins whoever sent it - so an open RTTY panel in a browser will overwrite this test from its own dropdowns. Close it and run again." -f $askMark, $askShift, $askReverse, $held.markHz, $held.shiftHz, $held.reverse)

    if ($null -eq $snap -or $samples.Count -eq 0) {
        Add-Check -Step '8' -What 'the reader answered' -Ok $false -Detail 'no snapshot came back at all'
    }
    else {
        # Tokens first, and over the whole window: a station that was heard
        # for ten seconds of ninety still published those words, and finding
        # them is the proof that the chain decoded real text. This is the one
        # check that does not care about the gaps.
        $upper = $text.ToUpperInvariant()
        $hit   = @()
        foreach ($t in $st.Tokens) { if ($upper.Contains($t)) { $hit = $hit + $t } }
        Add-Check -Step '8' -What ("the copy contains {0} of {1} published tokens" -f $st.TokensNeeded, $st.Tokens.Count) -Ok ($hit.Count -ge $st.TokensNeeded) -Detail ("found {0}. These are published by the station, not by us, which is why they are the pass criterion rather than anybody's ear." -f ($hit -join ', '))

        if ($st.FreqTokens.Count -gt 0) {
            $fhit = @()
            foreach ($t in $st.FreqTokens) { if ($upper.Contains($t)) { $fhit = $fhit + $t } }
            Add-Check -Step '8' -What ("at least {0} of the {1} published frequencies came through intact" -f $st.FreqTokensNeeded, $st.FreqTokens.Count) -Ok ($fhit.Count -ge $st.FreqTokensNeeded) -Detail ("found {0} of {1}: {2}. A digit wrong in any of these is a decode error that a callsign match would not have caught." -f $fhit.Count, $st.FreqTokens.Count, ($fhit -join ', '))
        }

        # Was there anything to judge?
        $live = @($samples | Where-Object { $_.activity -ge $st.MinActivity })
        $frac = [double]$live.Count / [double]$samples.Count
        $peak = ($samples | Measure-Object -Property activity -Maximum).Maximum

        Add-Check -Step '8' -What ("the station was heard for at least {0} seconds" -f $st.MinSignalSeconds) -Ok ($live.Count -ge $st.MinSignalSeconds) -Detail ("signal present in {0} of {1} seconds ({2:P0} of the window), peak activity {3:N2}, floor {4:N2}. Short of this there is not enough copy to put a rate on, which is a schedule or propagation result and NOT a decoder one. Give it a longer -Seconds, or come back when the station is sending. Note that activity alone does not mean the signal is decodable - one loud tone reads about 0.55 on its own - so read the tone-gap check below before blaming the station." -f $live.Count, $samples.Count, $frac, $peak, $st.MinActivity)

        if ($live.Count -lt $st.MinSignalSeconds) {
            Add-Check -Step '8' -What 'the copy was judged' -Ok $true -Warn -Detail 'Declined. There was too little signal in the window, so a character rate and a framing-error rate would be measuring noise. Nothing here says the decoder is wrong, and nothing here says it is right.'
        }
        else {
            # Over the live seconds only. Both figures are rates, so they mean
            # the same thing whatever -Seconds was set to.
            $liveChars = ($live | Measure-Object -Property dChars  -Sum).Sum
            $liveErrs  = ($live | Measure-Object -Property dErrors -Sum).Sum
            $cps       = [double]$liveChars / [double]$live.Count

            Add-Check -Step '8' -What ("at least {0:N1} characters a second while the station was up" -f $st.MinCharsPerSecond) -Ok ($cps -ge $st.MinCharsPerSecond) -Detail ("{0:N1} a second ({1} characters over {2} live seconds). 50 baud ITA2 is about six a second." -f $cps, $liveChars, $live.Count)

            if ($liveChars -gt 0) { $errPct = 100.0 * [double]$liveErrs / [double]$liveChars } else { $errPct = 100.0 }
            Add-Check -Step '8' -What ("framing errors under {0} percent while the station was up" -f $st.MaxFramingPercent) -Ok ($errPct -le $st.MaxFramingPercent) -Detail ("{0} errors in {1} characters ({2:N1} percent). A wrong shift or speed shows up here long before the text looks wrong to a reader." -f $liveErrs, $liveChars, $errPct)

            $median = ($live | Sort-Object activity | Select-Object -ExpandProperty activity)[[int]($live.Count / 2)]
            Add-Check -Step '8' -What ("activity held above {0:N2} while the station was up" -f $st.MinActivity) -Ok ([double]$median -ge $st.MinActivity) -Detail ("median {0:N2} over the live seconds, peak {1:N2}" -f $median, $peak)

            # The two arms have to stay level for the whole window, not just
            # at the moment Auto finished. This is the check that would have
            # caught the lease expiring, the browser overwriting the settings
            # and a 417 Hz dial error - all three show here as the gap walking
            # out to 13 dB or more and staying there.
            $gaps = @($live | Where-Object { $_.gapDb -lt 90.0 } | Select-Object -ExpandProperty gapDb)
            if ($gaps.Count -gt 0) {
                $gapMed = ($gaps | Sort-Object)[[int]($gaps.Count / 2)]
                $gapMax = ($gaps | Measure-Object -Maximum).Maximum
                Add-Check -Step '8' -What ("the two tone levels stayed within {0} dB of each other" -f $st.MaxToneGapDb) -Ok ([double]$gapMed -le $st.MaxToneGapDb) -Detail ("median gap {0:N1} dB over {1} live seconds, worst {2:N1} dB. A pair on the filters sits within a few dB; one tone off the filter sits 13 dB or more down and nothing readable comes out, however healthy the activity figure looks." -f $gapMed, $gaps.Count, $gapMax)
            }
        }

        Write-Host ''
        Write-Host '  --- what it copied ------------------------------------------' -ForegroundColor DarkGray
        foreach ($line in ($text -split "`n")) {
            if ($line.Trim().Length -gt 0) { Write-Host ("  {0}" -f $line.TrimEnd()) }
        }
        Write-Host '  -------------------------------------------------------------' -ForegroundColor DarkGray
    }
}
catch {
    if ("$_" -ne 'halt') {
        Write-Host ''
        Write-Host ("  Stopped: {0}" -f $_.Exception.Message) -ForegroundColor Red
        [void]$script:Results.Add([pscustomobject]@{ step = '-'; what = 'ran to the end'; state = 'FAIL'; detail = $_.Exception.Message })
    }
}
finally {
    # -- 9. put the radio back ---------------------------------------------
    Write-Step '9. Putting the radio back'
    if ($readerUp) { try { [void](Invoke-Api -Route 'api/rtty/reader/stop' -Method POST) } catch { } }
    if ($tunerUp)  { try { [void](Invoke-Api -Route 'api/rtty/tuner/stop' -Method POST -Query ("client={0}" -f $client)) } catch { } }

    if ($NoRestore) {
        Write-Host '            left as the test found it, as asked' -ForegroundColor DarkGray
    }
    elseif ($null -ne $savedFreq) {
        # The tuner restores the mode and width itself when the last window
        # lets go, which takes a couple of seconds. The dial is ours.
        Start-Sleep -Seconds 4
        try {
            if (-not $SkipTune) { [void](Invoke-Api -Route 'api/cat/frequency/a' -Method POST -Body @{ frequencyHz = $savedFreq }) }
            Start-Sleep -Milliseconds 700
            $status   = Invoke-Api -Route 'api/cat/status'
            $backFreq = ($SkipTune -or ([long]$status.vfoA.frequency -eq $savedFreq))
            $backMode = ([string]$status.vfoA.mode -eq $savedMode)
            $back     = ($backFreq -and $backMode)
            Add-Check -Step '9' -What 'the radio is back where it was found' -Ok $back -Warn:(-not $back) -Detail ("now {0} Hz / {1}, was {2} Hz / {3}. The mode and width are the tuner's own restore; the dial is this script's." -f $status.vfoA.frequency, $status.vfoA.mode, $savedFreq, $savedMode)
        }
        catch {
            Write-Host ("            could not confirm the restore: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------------------
$pass = @($script:Results | Where-Object { $_.state -eq 'PASS' }).Count
$warn = @($script:Results | Where-Object { $_.state -eq 'WARN' }).Count
$fail = @($script:Results | Where-Object { $_.state -eq 'FAIL' }).Count

Write-Host ''
if ($fail -gt 0) {
    Write-Host ("RESULT: FAIL - {0} passed, {1} warned, {2} failed" -f $pass, $warn, $fail) -ForegroundColor Red
    foreach ($r in ($script:Results | Where-Object { $_.state -eq 'FAIL' })) {
        Write-Host ("  step {0}: {1}" -f $r.step, $r.what) -ForegroundColor Red
    }
}
else {
    Write-Host ("RESULT: PASS - {0} passed, {1} warned" -f $pass, $warn) -ForegroundColor Green
}
Write-Host ''

if ($Out) {
    $dir = Split-Path $Out -Parent
    if ($dir -and (-not (Test-Path $dir))) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    $report = [pscustomobject]@{
        ranAt    = (Get-Date).ToString('o')
        station  = $Station
        expected = $st
        baseUrl  = $BaseUrl
        dialMHz  = $dial
        seconds  = $Seconds
        results    = $script:Results
        transcript = $text
        samples    = $samples
        calls      = $script:CallLog
    }
    $report | ConvertTo-Json -Depth 8 | Out-File -FilePath $Out -Encoding utf8
    Write-Host ("Report written to {0}" -f $Out) -ForegroundColor DarkGray
    Write-Host ''
}

if ($fail -gt 0) { exit 1 } else { exit 0 }
