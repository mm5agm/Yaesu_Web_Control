
# Yaesu Web Control

![Latest release](https://img.shields.io/badge/Latest%20release-v2.5.2-blue?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Yaesu_Web_Control/latest/Yaesu_Web_Control_Setup.exe?label=Downloads&style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)

> **Feedback wanted — especially if your setup isn't mine.** I operate FT8 on an FTdx101MP with an SDRplay RSP1B/RSP1 for the spectrum display, so that's the combination that actually gets tested. YWC now does everything I personally need, which means I've run out of my own ideas for what to add next — new features from here on depend on what other operators ask for. If you use another mode (SSB, CW, RTTY, other digital modes), another supported radio (FTdx101D, FTdx10, FT-710, FTDX3000), or a different SDR, even a one-line "works fine" or "this is annoying because…" on the [Discussions tab](https://github.com/mm5agm/Yaesu_Web_Control/discussions) tells me something I can't find out on my own. Bug reports, layout issues, and feature requests are all equally welcome.

## ✨ Added since the last release

> **v2.5.2 is a substantial update for RTTY operators with an SDR connected.** The spectrum now draws every signal where you actually hear it, in every mode and on both sidebands; the shaded passband sits over the RTTY tones, where the radio's filter really is; clicking a RTTY signal tunes onto it in both RTTY-L and DATA-L, using your own software's mark tone; and there is a crossed-ellipse RTTY tuning scope and a tuning step down to 1 Hz. Most of it came from Bruce VK2RT's reports while getting ready for CQ WW RTTY. Everything is written up in the [v2.5.2 notes](RELEASE_HISTORY.md#2026-09-24---v252).

**Since v2.5.2** — in the code, not yet in a full release:

- **FT-710: the radio's own scope in the spectrum panel, with no SDR** *(experimental - FT-710 owners, please try it)*. With menu SCU-LAN10 switched ON (no unit needed), the FT-710 sends its scope over USB, and YWC can now draw it as the spectrum and waterfall. FTDI's driver and LibFT4222 have to be installed separately. This is built on protocol work by the Nexus project and ON8ST. ut9fj has checked it on his FT-710 on receive: signals land at their true frequency at every span, and the panel follows the span you set on the radio. Not yet checked while transmitting. With the radio's SCOPE CTR on FILTER, RTTY, PSK and FM stay centred on the dial. [§6.3](USER_MANUAL.md#63-sdr-spectrum-display).
- **Pop-out windows for a second monitor** *(v2.5.3-pre2)*. The CW Reader, CW Send, RTTY Tuner, Audio Filter and DX Spots list can each open in a window of their own with their **↗** button (CW Send from v2.5.3-pre3), at any size and on any screen; **Reattach** puts them back. They are meant for a second monitor: on the same screen as the main page, clicking the main page covers them, and the panel's button or Alt+Tab brings them back. [§5.21](USER_MANUAL.md#521-pop-out-windows).
- **A spectrum panel as tall or short as you want** *(v2.5.3-pre2)*: a **Height** box, in percent of the normal height from v2.5.3-pre4, and a grip along the bottom you can drag or use from the keyboard, from 40 to 1000 pixels per VFO. [§5.4](USER_MANUAL.md#54-spectrum-display).
- **CW Send stops within a word or two, and won't send in the wrong mode** *(v2.5.3-pre3)*. Lines go out a word or two at a time, so **Stop** takes effect almost at once. In a mode other than CW, Send refuses and offers to switch the VFO to CW. [§21](USER_MANUAL.md#21-cw-send).
- **CW break-in delay and Full break-in reach the radio** *(v2.5.3-pre3)*. On the FTdx101, FTdx10 and FT-710 the delay set in YWC was ignored by the radio, and Full left it on Semi. [§5.12](USER_MANUAL.md#512-cw-keyer-panel).
- **Hide the VFO panels, the meters and the Clarifier** *(v2.5.3-pre4)* ([#171](https://github.com/mm5agm/Yaesu_Web_Control/discussions/171)). A **VFOs** button beside each spectrum title hides both VFO panels, so the page can be just the spectrum; **Hide meters** and the Clarifier's **Hide** fold those away too. The browser remembers each one. [§5.4](USER_MANUAL.md#54-spectrum-display).
- **Drag the spectrum to tune** *(v2.5.3-pre4)*: hold the left button and slide, and the signals follow the pointer. A click still tunes to the point clicked. [§5.4](USER_MANUAL.md#54-spectrum-display).
- **Free the SDR when the last browser closes** *(v2.5.3-pre4)*, a new setting under Settings > SDR, off by default, so another program can use the SDR while YWC is still running.
- **RTTY: it reads the text now, and works the settings out for you** *(not yet in a build)*. The **RTTY Tune** panel has grown a **Decode** pane beside the crossed figure, so the tuner that showed you a signal was tuned in now also prints it. There is no second set of boxes to keep in step: the decoder reads the Mark, Shift, Speed and Rev you tuned with, and follows a change of mode on the radio by itself. A new **Auto** button listens for about four seconds and fills in all four from the signal — both tones, the shift, the speed and which way round they are — and when it is sure, moves the dial to bring the signal onto the tones. It measures rather than choosing from a list, so a weather station on a shift no amateur uses is reported as it is; when it cannot make sense of what it hears it says so and changes nothing, which is the whole point of pressing it. **From radio** takes the radio's own MARK and SHIFT menu settings, and in RTTY the two are now kept in step both ways round. The decoder and the measurement are shared with Icom Web Control, so the two apps read identical text from identical audio. **Receive only** — the Send box is there but disabled. Two new switches under Settings → RTTY Tuner control what it is allowed to touch: whether opening the panel sets RTTY mode and widens a too-narrow filter, and whether Auto moves the dial. [§22](USER_MANUAL.md#22-rtty).
- **RTTY Contest Mode** *(not yet in a build)*. A switch on the Settings page under **RTTY Tuner** that trims the panel to the figure and its controls: the decode and send panes go, the hint under the scope goes, and the two lines under it keep their height instead of reflowing the panel under your mouse every time the status changes. That last part is what Bruce VK2RT asked for after CQ WW RTTY. For contesting, where your logger already has a decoder and what it cannot draw is the figure. The same switch, under the same name, is in Icom Web Control. [§22.7](USER_MANUAL.md#227-contest-mode-the-tuner-on-its-own).
- **An Experimental UI, beside the Classic one** *(v2.5.3-pre7, Fabio Valente, CR7CDC)*. **Experimental UI** in the top bar opens a dark, dockable workspace: the VFOs, spectrum, meters, Radio Display and the rest are panels you can drag, dock, tab together and resize, and save as named layouts. The Classic page is unchanged and is still the default. [§5.22](USER_MANUAL.md#522-experimental-ui).

**New in v2.5.2:**

- **An RTTY tuning scope.** The **RTTY Tune** button opens the crossed-ellipse display that used to sit beside every RTTY terminal unit. You tune for a clean cross; the arms lean and open up when you are off tune, and one goes missing when the shift or polarity is wrong. It uses the same USB audio as the CW Reader and transmits nothing. [§22](USER_MANUAL.md#22-rtty).
- **A tuning step you can set**, 1 Hz to 10 MHz per VFO, from the **Step** box on the spectrum panel, a right-click on the spectrum, clicking a digit of the frequency display, or the voice command *"set step size one hertz"*. [§5.4](USER_MANUAL.md#54-spectrum-display), [§5.6](USER_MANUAL.md#56-frequency-display-and-tuning).
- **You can stop YWC changing the mode when you tune** from the spectrum or a DX spot: **Settings → Band Plan**. [§5.4](USER_MANUAL.md#54-spectrum-display).
- **Put your memories in the order you want** — sort by Label, Frequency or Mode, and move rows up and down. The order decides which 99 go to the radio. [§8.1](USER_MANUAL.md#81-memories-editor).
- **Keyboard shortcuts** (Fabio Valente, CR7CDC) — press **?** on the main page for the list. [§13](USER_MANUAL.md#13-keyboard-shortcuts).
- **Testers get told about new test builds.** On a full release nothing changes; on a pre-release you are offered newer pre-releases, clearly labelled. [§5.1](USER_MANUAL.md#51-top-bar).

**v2.5.0 was the biggest release since the SDR spectrum display arrived.** Five things are in it that were not in v2.4.2 at all: the radio's own screen in the browser — and you can click it — a Morse reader, a Morse sender, remote audio both ways, and YWC running on macOS, Linux and a Raspberry Pi. Two of those are Fabio Valente's (CR7CDC) work, and this release is as much his as mine.

### The radio's own screen in the browser — and you can click it

![The radio's TFT captured over HDMI, with the Radio scope controls docked beside it](pictures/Radio_Display_Docked.png)

The FTdx101, FTdx10 and FT-710 repeat their front-panel TFT on a rear DVI-D socket. Plug that into a cheap HDMI-to-USB capture stick and YWC shows the radio's screen — scope, 3DSS, meters, menus, all of it — in the browser, popped out into its own window, or on a tablet in another room. That part is Fabio's, start to finish.

**It is not a static picture of the radio.** HDMI capture is one-way, so a click can never reach the touchscreen — instead YWC works out what is under the mouse and sends the CAT command for it. On the FTdx101MP/D and the FTdx10 in MONO W/F:

- **Hover over the scope or the waterfall** to read the frequency under the cursor, and **click to tune there**. The frequency is measured from the radio's own VFO marker line, so it is right whatever the span is set to.
- **Click the ANT / ATT / IPO / R.FIL / AGC readouts** to cycle them, exactly as you would on the radio itself.
- **Click the CURSOR / SPAN / 3DSS / HOLD soft-buttons** along the bottom of the screen.
- Where a soft-button has no CAT command behind it (MONO, MULTI, EXPAND, MEM CH) the label says so rather than doing nothing silently. The overlay assumes EXPAND is off: with it on the scope grows up over the readout row, and the radio does not report that over CAT.

Docked to the right of the picture is a **Controls** column that drives the radio's own scope over CAT — centre/cursor/fix, 3DSS or waterfall, span, speed, level, peak, marker, hold, colour, and the AF-FFT and oscilloscope settings. Turn a knob on the front panel and the button in the browser follows. On the FTdx101 both the MAIN and SUB scopes are driven, and with no capture stick at all the same controls are still there as a **Radio Scope** card.

Two videos: [what the radio's DVI-D output actually is, and what can go wrong connecting it to HDMI](https://www.youtube.com/watch?v=dtlziYEsXxE) — worth watching before you plug anything in — and [the feature in action](https://www.youtube.com/watch?v=1EY7m5e91TI). **Set the radio's EXT MONITOR PIXEL menu to 800×600 first**, or the capture stick silently stretches the picture. The FT-710 has no clickable overlay yet. Full detail: [§19](USER_MANUAL.md#19-radio-display) and [§19.4](USER_MANUAL.md#194-cat-scope-controls).

### CW Reader — the Morse on the air, as text

![CW Reader panel decoding an off-air signal, with the status line, the tuning aids and the QSO log form](pictures/CW-Reader.png)

A **CW Read** button on the main page opens a reader that decodes the Morse in the radio's receive audio and prints it as text. No extra hardware, nothing transmitted. Neither Yaesu nor Icom hand their own decoder's output over CAT, so I wrote a decoder; it lives in the shared [Radio_Web_Control_Core](https://github.com/mm5agm/Radio_Web_Control_Core) and the identical code reads Morse in Icom Web Control.

The status line says what it is actually hearing — signal or not, the tone against your pitch, the filter width, the SNR, how far off pitch the station is — and when two stations are inside the filter, or the tone is breaking up, it says so and prints nothing rather than guessing. **Reader Mode** sets the radio up for copy (CW, a narrow filter, APF on) and puts your own settings back when you stop; **ZIN** drops the station onto your pitch; **Tune** shows the passband with a pitch marker and a zero-beat figure. Every session writes a timestamped transcript, and the log form *offers* the callsign and report the reader thinks it saw as one-click suggestions rather than filling them in — a callsign quietly filled in from junk is worse than an empty box. Confirmed contacts append to a plain ADIF file. Windows, macOS, Linux and Docker. Full detail: [§20](USER_MANUAL.md#20-cw-reader).

### CW Send — type a line, press Enter, the radio keys it

![CW Send panel part way through a long line, the character being keyed highlighted and the tag showing part 2 of 3](pictures/CW-Send.png)

The reader's other half. A **CW Send** button opens a box: type a line, press **Enter**, and the radio keys it. Nothing goes out until Enter, so you can type ahead or paste, and the character being keyed is highlighted as it goes out. Only the CAT cable is needed, so it runs everywhere the reader does.

It is built on the radio's own keyer memories: the line is cut into pieces of up to 50 characters at word boundaries, each written to keyer memory 5 and played, and your own M5 is put back at the end. With break-in **Off** the line goes to the monitor with a banner and nothing is transmitted, which is how I test it. **Stop** — or **Escape** — drops everything not yet started; the piece already on the air finishes, because the radio cannot be told to abandon a playback, which is the reason it is sent in pieces at all. Lines typed while one is sending are queued. Full detail: [§21](USER_MANUAL.md#21-cw-send).

### And the rest of v2.5.0

- **Remote Audio** (Fabio) — the radio's receive audio in the browser's speakers and the browser's microphone into the radio's USB audio for transmit, with a pop-out player and device pickers that find the radio's USB CODEC for you. For your own LAN or a VPN: YWC has no login, so don't port-forward it.
- **macOS, Linux and Docker** (Fabio) — an unsigned macOS app for Apple Silicon and Intel, and a multi-architecture Docker image that runs on a Raspberry Pi 3B.
- **Signals are drawn where they really are on the FTdx101 spectrum.** The IF OUT sockets are not at the SDR's centre and the radio slides its IF with the filter settings; YWC ignored both, so everything was drawn 5–7 kHz out. Measured and corrected on both receivers.
- **The span buttons are now the radio's own list** — 1k to 1M, plus 2M. Everything from 100 kHz down is a software zoom of a fixed stream, so stepping between 1k and 100k is instant: no retune, no blank trace.
- **The receiver's passband is shaded on the trace**, moving with IF width, shift and CW pitch, so you can see which of the signals on screen you are actually hearing.
- **Smooth is a slider now, and the default no longer buries CW** — the old fixed average took about 11 dB off a one-bin CW carrier.
- **The keyer's M1–M5 buttons finally send.** They had done nothing since v1.6.0: they used the command that *plays* a memory to try to *load* one. **Monitor level** had never worked either. Both fixed.
- **Meters** — the SWR needle no longer freezes or dips ([#124](https://github.com/mm5agm/Yaesu_Web_Control/issues/124)), the FTdx101's front-panel meter pair is yours again outside transmit, and RF power is scaled to each radio's own rating instead of the FTdx101MP's 200 W.
- **A keyboard-reachable ATU Tune button** and tuner voice commands, **CTCSS that works** (six separate faults), and **one screen-reader announcement per action** instead of an unpredictable number.
- **The log is a tenth of the size**, with **Start fresh test log** / **Download test log** on the Diagnostics page, so a bug report can carry just the part that matters.

Every one of these is written up in full in the [release notes](RELEASE_HISTORY.md#2026-09-14---v250) at the foot of this page.

## 🔧 Fixed since the last release

One line per fix, newest first, with the build that has it. A pre-release installs exactly like a release and carries everything before it; each one is written up under [Release Notes](#release-notes). *Not yet in a build* means the fix is in the code and will be in the next pre-release or release.

| Fixed | Issue | In build |
|---|---|---|
| The CW Reader has an **Auto** button, as the RTTY Tuner does. It finds the station being read, moves the dial so its tone sits on your CW pitch, and sets the filter for the speed it is sending (your Reader Mode width at most speeds, up to 500 Hz for fast CW). A station well off pitch in a wide filter is brought on pitch at 500 Hz first, then narrowed once it is copying. **Stop** puts your filter back; the dial stays on the station ([§22](USER_MANUAL.md)) | - | *Not yet in a build* |
| In the Experimental UI, a panel popped out into a window of its own showed a blank spectrum, S-meter and filter display, and stayed blank when it was put back. A drag started in the pop-out, such as the spectrum's splitter, never ended. Popped-out panels now draw, resize and take the mouse and keyboard as they do in the main page, and come back intact ([§5.22](USER_MANUAL.md#522-experimental-ui)) | [#213](https://github.com/mm5agm/Yaesu_Web_Control/pull/213) | *Not yet in a build* |
| Experimental UI: clicking a VFO did not make it the active (MAIN/SUB) band, and the AF Gain sliders on the VFO panels did not change the volume. Click the VFO's tab to make it active; AF Gain now works ([§5.22](USER_MANUAL.md#522-experimental-ui)) | - | Not yet in a build |
| A meter reading could occasionally show a different meter's value: a reply that arrived late was given to the next reading of the same kind, so the power gauge could show the compression/SWR reading, or VFO B's S-meter VFO A's. Each reading now only accepts its own reply | - | [v2.5.3-pre7](#2026-10-07---v253-pre7-pre-release) |
| With two capture dongles of the same model - one per radio - Radio Display could open the wrong radio's screen after a replug, because it remembered the dongle by its place in the list. It now remembers the dongle itself, as long as it goes back into the same USB socket, and the two are told apart in the device list. If the chosen dongle is not plugged in when the stream starts, the panel now says **Not connected** instead of disappearing, and the picture comes up when the dongle is plugged in ([§6.9](USER_MANUAL.md#69-radio-display)) | - | [v2.5.3-pre7](#2026-10-07---v253-pre7-pre-release) |
| WSJT-X, Log4OM and other programs using YWC's rigctld connection were told the radio was in USB whenever it was in CW, RTTY or a DATA mode, so FT8 on DATA-U read back as USB. They are now told the right mode (PKTUSB for DATA-U, CW, RTTY and so on) | - | [v2.5.3-pre6](RELEASE_HISTORY.md#2026-10-05---v253-pre6-pre-release) |
| The FT-991A is now a model of its own in **Settings > Radio Model**, so it no longer has to be set up as an FT-710. It adds **2m** and **70cm** band buttons and the **C4FM** mode (which the FT-710 setting showed as PSK). It also caps power at 50 W on 2 m and 70 cm, and has the 991A's own IF widths and IF shift (the width used to show as "1;"), split, break-in, RTTY tones and memory import. Built from the 991A's CAT manual and still being tested on a real radio | [#86](https://github.com/mm5agm/Yaesu_Web_Control/discussions/86) | [v2.5.3-pre6](RELEASE_HISTORY.md#2026-10-05---v253-pre6-pre-release) |
| After the browser was closed and reopened, a pop-out could open as an ordinary tab in the main browser window - filling it, with no edges to resize - instead of a window of its own. A pop-out page restored as a tab now lets go of the pop-out's name, so the next **↗** opens a proper window ([§5.21](USER_MANUAL.md#521-pop-out-windows)) | - | [v2.5.3-pre5](RELEASE_HISTORY.md#2026-10-04---v253-pre5-pre-release) |
| **Reattach** now brings the CW Reader, CW Send and RTTY Tuner back into the main page at the size you left the pop-out window, instead of the size the panel had before ([§5.21](USER_MANUAL.md#521-pop-out-windows)) | - | [v2.5.3-pre5](RELEASE_HISTORY.md#2026-10-04---v253-pre5-pre-release) |
| A pop-out window came back bigger each time it was reattached and popped out again when the browser's zoom wasn't 100% (twice the size at 50%). It now comes back the size you left it at any zoom, however small you make it ([§5.21](USER_MANUAL.md#521-pop-out-windows)) | - | [v2.5.3-pre5](RELEASE_HISTORY.md#2026-10-04---v253-pre5-pre-release) |
| A panel dragged to one place at one browser zoom could open off the window after the zoom was changed, so its button seemed to do nothing. The CW Reader, CW Send and RTTY Tuner are now brought back into view when they open, and the first time they open across the top of the window instead of far down the page ([§5.21](USER_MANUAL.md#521-pop-out-windows)) | - | [v2.5.3-pre5](RELEASE_HISTORY.md#2026-10-04---v253-pre5-pre-release) |
| The CW Reader's **Tune** box was always off when the reader opened in a pop-out window, and on a fresh main page, even if you had left it on. It now comes back the way you left it ([§20](USER_MANUAL.md#20-cw-reader)) | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The CW Reader's pop-out window opened at 720 x 480 the first time, which is tiny on a large monitor. It now opens at a quarter of the screen; a size you have set yourself is still remembered ([§5.21](USER_MANUAL.md#521-pop-out-windows)) | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The DX Spots list could open below the bottom of the window on a long page, so pressing its button seemed to do nothing. It now opens across the top of the window, and a place saved on a bigger screen is pulled back into view. A click beside the **All bands** switch now works the switch too ([§5.17](USER_MANUAL.md#517-dx-spots-list)) | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The two VFO panels could not be put away, so a page for a contest could not be just the spectrum. Each spectrum card now has a **VFOs** button beside its title that hides both VFO panels and shows them again; it is remembered by the browser, and the panels always come back if the spectrum is not showing ([§5.4](USER_MANUAL.md#54-spectrum-display)) | [#171](https://github.com/mm5agm/Yaesu_Web_Control/discussions/171) | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The meters and the Clarifier strip could not be put away, as the Radio Scope card can. A **Hide meters** button at the end of the meter toolbar now folds the gauges away (Power, MIC and the toolbar stay), and the Clarifier strip has a **Hide** button that leaves just its name; both are remembered by the browser ([§5.2](USER_MANUAL.md#52-meters), [§5.5](USER_MANUAL.md#55-vfo-panels)) | [#171](https://github.com/mm5agm/Yaesu_Web_Control/discussions/171) | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The spectrum panel's Height box now reads in percent, with 100% as the normal height: 50%, 75%, 100%, 150%, 200% and 300%, instead of Shortest through Tallest. A height dragged with the grip shows its own percentage instead of *Custom*. A height saved on one of the old presets is kept, and shows as its percentage ([§5.4](USER_MANUAL.md#54-spectrum-display)) | [#171](https://github.com/mm5agm/Yaesu_Web_Control/discussions/171) | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The power needle could stay up, at a figure from early in the over, after you stopped transmitting, while the label under it read 0 W. Every meter was affected: a new reading arriving while the needle was still moving to the last one made it finish on an older value. The needle now always ends on the latest reading | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| On the FTdx101MP/D, while transmitting in CW, RTTY, PSK and the DATA modes, YWC set the radio's own meters to COMP and SWR, so the radio's COMP needle went past full scale, where it looks like PO. In those modes YWC now sets PO and SWR instead. SSB, AM and FM still get COMP and SWR, and the radio's own choice still comes back 10 seconds after the last over | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| The spectrum can now be dragged to tune. Hold the left button and slide it, and the signals follow the pointer; a press that doesn't move is still a click-to-tune. A drag never changes the mode ([§5.4](USER_MANUAL.md#54-spectrum-display)) | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| New setting: **Free the SDR when the last browser closes** (Settings > SDR, off by default). YWC normally keeps the SDR open for as long as it is running. With this on, it lets go of the SDR 3 seconds after the last browser closes, so another program can use it, and opens it again when a browser comes back | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| Settings let the same SDR be chosen for both VFO A and VFO B, and one VFO's panel then showed *SDR device unavailable*. Settings now warns as soon as both are the same, and won't save until one is changed | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| With voice announcements on, CW with break-in said "transmit, receive" on every over, talking across the Morse. TX and RX are no longer announced while the transmit VFO is in CW. The TX timeout warning is still spoken ([§5.16](USER_MANUAL.md#516-voice-announcements)) | - | [v2.5.3-pre4](RELEASE_HISTORY.md#2026-10-04---v253-pre4-pre-release) |
| On the FTdx101, FTdx10 and FT-710, the break-in delay set on the CW Keyer panel never reached the radio, and the slider didn't show the radio's own setting. Choosing **Full** break-in left the radio on Semi. Both now reach the radio. The delay slider runs 30-3000 ms and moves to the nearest step the radio can hold. Full is offered on the FTdx101MP/D only for now ([§5.12](USER_MANUAL.md#512-cw-keyer-panel)) | - | [v2.5.3-pre3](RELEASE_HISTORY.md#2026-10-02---v253-pre3-pre-release) |
| CW Send keyed nothing at all when the transmit VFO was in a mode other than CW, such as DATA-U: the radio accepts the text and silently ignores it, so a line looked sent and nobody heard it. CW Send now refuses, says which VFO is in which mode, and offers a **Switch VFO A to CW** button; the mode changes only if you press it. The M1-M5 buttons give the same message ([§21.7](USER_MANUAL.md#217-troubleshooting)) | - | [v2.5.3-pre3](RELEASE_HISTORY.md#2026-10-02---v253-pre3-pre-release) |
| **Stop** in CW Send could leave up to 50 characters, about 20 seconds at 25 wpm, still to go out, because the radio cannot stop a keyer memory once it is playing. Lines now go out a word or two at a time, so Stop takes effect within a word or two, and the pauses between the pieces are shorter than before ([§21.5](USER_MANUAL.md#215-stopping)) | - | [v2.5.3-pre3](RELEASE_HISTORY.md#2026-10-02---v253-pre3-pre-release) |
| CW Send could only float inside the main page. Its new **↗** button opens it in a window of its own, for a second monitor beside the CW Reader; **Reattach** puts it back ([§21.6](USER_MANUAL.md#216-the-panel)) | [#190](https://github.com/mm5agm/Yaesu_Web_Control/issues/190) | [v2.5.3-pre3](RELEASE_HISTORY.md#2026-10-02---v253-pre3-pre-release) |
| In the pop-out windows (CW Reader, RTTY Tuner, Audio Filter, DX Spots) the close button **×** had none of the main page's styling and was dark on the dark title bar, so it was all but invisible. It is red in every window now, as it is in the main page | - | [v2.5.3-pre3](RELEASE_HISTORY.md#2026-10-02---v253-pre3-pre-release) |
| The bar for resizing the spectrum panel was hard to see, and when you reached it with Tab its thin blue outline was almost invisible against the blue VFO card. The bar is now lighter with a bigger grip mark, and keyboard focus draws a clear amber outline | [#171](https://github.com/mm5agm/Yaesu_Web_Control/issues/171) | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The Range, Speed, Bright and Smooth sliders on the spectrum display had a faint track that was hard to see against the dark panel. They now draw like the Notch slider, with a clear outline | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The Audio Filter kept showing the values it read when it opened, so a change made on the radio's own menu never appeared until it was closed and opened again. It now reads the radio again whenever you click on it or come back to its window ([§5.18](USER_MANUAL.md#518-audio-filter-popout)) | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The DX Spots list could only float inside the main page. Its **↗** button now opens it in a window of its own, for a second monitor, with the full spot list, live updates and click-to-tune; **Reattach** puts it back ([§5.17](USER_MANUAL.md#517-dx-spots-list)) | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The Audio Filter dialogs could only float inside the main page. Each VFO's Audio Filter now has a **↗** button that opens it in a window of its own, for a second monitor; **Reattach** puts it back ([§5.18](USER_MANUAL.md#518-audio-filter-popout)) | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The RTTY Tuner was a fixed size and could only float inside the main page. It can now be resized by its corner, with the scope staying square and filling the space; a **Tones only** button hides everything but the scope, so it can be made very small; and its **↗** button opens it in a window of its own for a second monitor. That window pauses, rather than closing, when the mode leaves RTTY, DATA, LSB and USB ([§22](USER_MANUAL.md#22-rtty)) | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| With the RTTY Tuner open in two places - two browser tabs, or two PCs - closing it in one stopped the figure in the other too, which sat on "Stopped." until it was closed and opened again. Each window now holds the radio's audio for itself, and the audio is let go only when the last one closes | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The CW Reader could only float inside the main page, so it couldn't go on a second monitor. Its new **↗** button opens it in a window of its own, which can be any size and go anywhere, and **Reattach** puts it back. The window remembers its size and position, and when the mode leaves CW it pauses instead of closing ([§5.21](USER_MANUAL.md#521-pop-out-windows)) | [#190](https://github.com/mm5agm/Yaesu_Web_Control/issues/190) | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| Dragging the CW Reader, RTTY Tuner, DX Watch, DX Spots or several other movable windows over the meters or the spectrum let parts of what was underneath (meter labels, buttons) show through them. Every movable window now sits on top of the page, and when two overlap, the one you last clicked or opened comes to the front | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| The spectrum panel was a fixed 280 pixels tall, on a 4K monitor as much as on a laptop, and nothing could be done about it. It now takes a **Height** setting on its control bar — Shortest through Tallest — and a grip along the bottom of the panel can be dragged (or driven from the keyboard) to anything from 40 to 1000 pixels, remembered per VFO ([§5.4](USER_MANUAL.md#54-spectrum-display)). | [#171](https://github.com/mm5agm/Yaesu_Web_Control/discussions/171) | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| Searching Start for "Yaesu" found nothing, because the Start menu entry sat inside a folder named MM5AGM, which sorts under M. It is now a top-level entry, and an upgrade removes the old one. The installer also now records YWC for Apps & features in the 64-bit part of the registry, where a 64-bit app belongs, instead of the 32-bit part | - | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| Installing an upgrade offered the default folder instead of the one YWC was already installed in, so anyone who had installed it somewhere else got a second copy unless they changed the folder by hand. The installer now offers the existing folder | [#192](https://github.com/mm5agm/Yaesu_Web_Control/issues/192) | [v2.5.3-pre2](RELEASE_HISTORY.md#2026-10-01---v253-pre2-pre-release) |
| FT-710 showed an antenna dropdown with three antennas, but the radio has one ANT jack and no CAT command for choosing one. The dropdown is gone on the FT-710, and on single-antenna radios (FT-710, FTdx10, FT-991A) YWC no longer asks the radio for an antenna it cannot report | - | [v2.5.3-pre1](RELEASE_HISTORY.md#2026-09-28---v253-pre1-pre-release) |
| Radio Display could go blank for a second or two after an antenna change (or any other change the app saves). Each save briefly left the capture with the default settings, which have Radio Display off, so it closed the capture device and reopened it | - | [v2.5.3-pre1](RELEASE_HISTORY.md#2026-09-28---v253-pre1-pre-release) |
| Radio Display: on the RTTY or CW decode screen the click zones stayed live over a screen they no longer matched, so DEC LVL cycled the scope placement (a different change on each press). The overlay now checks the frame is the scope screen before any click and turns the zones off when it is not; on the decode screen the ANT to AGC readouts stay clickable, and DEC LVL and DEC OFF say what to do on the radio, as neither has a CAT command | [#179](https://github.com/mm5agm/Yaesu_Web_Control/issues/179) | [v2.5.3-pre1](RELEASE_HISTORY.md#2026-09-28---v253-pre1-pre-release) |
| RTTY Tuner in RTTY-U listened for space at 1955 Hz instead of 2295, so it needed *Rev* to show a cross, and that *Rev* then broke the cross in RTTY-L. The band plan's RTTY segment on 20/15/10m also selected RTTY-U, where the radio's own decoder reads amateur RTTY reversed; it now selects RTTY-L | - | [v2.5.3-pre1](RELEASE_HISTORY.md#2026-09-28---v253-pre1-pre-release) |
| RTTY Tuner froze on *Stopped.* after its browser tab had been in the background, until the dialog was closed and reopened; it now starts again by itself | - | [v2.5.3-pre1](RELEASE_HISTORY.md#2026-09-28---v253-pre1-pre-release) |
| RTTY Tuner opened just after YWC started could use the mode from the last session instead of the radio's, which in RTTY-U put the space filter on the wrong side of mark until the radio's mode came in | - | [v2.5.3-pre1](RELEASE_HISTORY.md#2026-09-28---v253-pre1-pre-release) |

Everything fixed in the v2.5.2 pre-releases is in [v2.5.2](RELEASE_HISTORY.md#2026-09-24---v252).

**Supported transceivers:**

| Transceiver | Power | Receivers | Notes |
|-------------|-------|-----------|-------|
| FTdx101MP | 200 W | Dual | All features supported |
| FTdx101D | 100 W | Dual | All features supported |
| FTdx10 | 100 W | Single | Two VFOs; no rear-panel IF output for spectrum |
| FT-710 | 100 W | Single | Two VFOs; no rear-panel IF output for spectrum |
| FTDX3000 | 100 W | Single | Two VFOs; no memory tag (MT) command |
| FT-991A | 100 W (50 W on 2 m / 70 cm) | Single | HF + 6 m + 2 m + 70 cm; C4FM; new in v2.5.3-pre6 — built from the 991A's CAT manual and still being tested on a real radio |
| FTDX5000MP | 200 W | Dual | New — available to test; power meter calibration still being refined |
| FTDX5000D | 200 W | Dual | New — available to test; power meter calibration still being refined |

## Getting your Yaesu radio added — it's easier than you think

I own and test on the **FTdx101MP**; the other supported models are built from Yaesu's published CAT documentation and confirmed by the users who own them. If your Yaesu isn't listed yet — the **FTX-1** and others are all realistic — I'll gladly add it, and **all it takes from you is ordinary operating, not programming.**

"Testing" just means *using your radio normally* with YWC and telling me what happens: change bands, tune around, switch modes, key up, and drop a note on the [Discussions tab](https://github.com/mm5agm/Yaesu_Web_Control/discussions) like *"frequency and mode track fine, but the S-meter reads a bit low."* **No code, no build tools, no command line.** If you can run the installer and operate your rig, you can test — I do the programming, you just tell me what your radio does. Even a one-liner on a model that already works ("works fine on my FT-710") is useful: it tells me which radios have real users behind them.

**What it might involve.** Now and then I'll ask you to try a **pre-release** — a test build with a fix in it, installed exactly like a normal release — and, if something's misbehaving, to send me a **log file** so I can see what your radio and YWC actually did. YWC makes that easy: open the **Diagnostics page** (in the menu across the top of YWC), click **Start fresh test log**, do the test, then **Download test log** — you get a small log of just that test to **drag straight into the GitHub discussion or issue**. (The full logs also live on disk at `%APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-<date>.log` if you ever need them directly.) That's genuinely the whole job: install, operate, and occasionally send a file across.

**FTX-1:** a user has reported it working on HF (run with the model set to *FTdx10*) — frequency and mode track cleanly and quickly. Its 6m/2m/70cm memory handling still needs a little work, so a dedicated FTX-1 profile is on the way once someone can help confirm the VHF/UHF side. If you own an FTX-1, I'd love to hear from you.

### Platforms (Windows vs macOS / Linux)

| | **Windows** (shipped installer) | **macOS** (CAT-only DMG) | **Linux** (CAT-only host) |
|---|---|---|---|
| How to run | Download installer from [Releases](https://github.com/mm5agm/Yaesu_Web_Control/releases) | Download DMG from [Releases](https://github.com/mm5agm/Yaesu_Web_Control/releases) (unsigned; arm64 or x64), or `dotnet run --framework net10.0` | `dotnet run --framework net10.0`, or **Docker** (`ghcr.io/mm5agm/yaesu_web_control`, amd64 + arm64 / Pi) |
| Host UI | System tray | Menu-bar status item | Console only (Docker / from source) |
| CAT + browser UI | Yes | Yes | Yes |
| SDR spectrum | Yes | No | No |
| Voice Control (SAPI) | Yes | No | No |
| Voice announcements (browser TTS) | Yes | Yes | Yes |
| CW Reader | Yes | Yes | Yes |
| CW Send (keyboard keying via the radio's keyer) | Yes | Yes | Yes |
| Remote Audio (RX to browser, mic to radio) | Yes | Yes | Yes (native host; not Docker) |
| Radio Display (HDMI capture of the radio's screen) | Yes | Yes | Yes (map `/dev/video*` in Docker) |
| Serial port | `COM3`, … | `/dev/cu.*` | `/dev/ttyUSB*` / `/dev/ttyACM*` |
| USB serial driver | [Silicon Labs CP210x VCP](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) on **Windows, macOS, and Linux** — **reboot after install** |
| User data | `%APPDATA%\MM5AGM\Yaesu Web Control\` | `~/.config/MM5AGM/Yaesu Web Control/` | Same `~/.config/…` path; Docker uses `./data/ywc` volume |
| Auto-exit when no browser | Default on | Default on — turn **off** for headless | Default on from source; Docker forces off |

The macOS DMG is **not notarized** (no Apple Developer Program membership). Gatekeeper will warn on first open — right-click → **Open**, or use **Privacy & Security → Open Anyway**. Same class of warning as the unsigned Windows installer.

Full operational detail: [USER_MANUAL.md §1](USER_MANUAL.md#1-introduction), [§2.2 macOS DMG](USER_MANUAL.md#22-macos-dmg), [§2.4 USB serial driver](USER_MANUAL.md#24-usb-serial-driver-windows--macos--linux), and [§15.10](USER_MANUAL.md#1510-whats-different-on-macos--linux-vs-windows).

Before first CAT use on any OS: install the Silicon Labs driver from the link above and reboot the host.

### Building from source (developers)

Build commands for all four targets - the Windows product, the CAT-only host,
the macOS DMG and the Linux/Docker image - are in [docs/BUILDING.md](docs/BUILDING.md).

## What it looks like

Everything on one page: the meters across the top, the radio's own screen captured
over HDMI with the CAT scope controls docked to its right, both SDR spectrum panels
underneath, and a VFO card for each receiver.

![The main page - meter row, the radio's TFT over HDMI with the scope controls beside it, the two SDR spectrum panels, and a VFO card per receiver](pictures/DevelopScreen.png)

One SDR panel at the 20 kHz span. The shaded band is the receiver's IF passband, the
amber line is the dial, and a click on a CW signal lands on it.

![Spectrum panel at a narrow span showing the IF passband overlay and the dial marker](pictures/Spectrum_Passband.png)

The VOX, CW Keyer and FM Repeater panels, open together.

![VOX, CW Keyer and FM Repeater panels open simultaneously](pictures/Screen%20popups.png)

## ⚠️ Warning

This software interacts with radio hardware. I have used only the official Yaesu CAT commands as per the manual, however, you use entirely at your own risk. Please read the licence. Always verify transmit frequencies, power levels, and settings before use.

---

## 📖 Why This Application Exists
I wrote this application because I can't see the FTdx101MP controls without using a magnifying glass. I've added support for partially sighted users by utilising NVDA and windows narrator. As a ham who uses WSJT-X, JTAlert, and Log4OM, I thought it would be nice to add buttons to start them from the app as it saves openning up the individual programs. I've added memory channel banks and functions to read and save etc. You don't need to save to the transceiver unless you specifically want them on it, taking your transceiver to another location for example. Please read the settings carefully as you can overwrite the transceivers memories.  

Tablet testing has been limited — feedback from tablet users is particularly welcome.

---

## ⚠️ Windows Security Warnings on First Install

The installer is not code-signed (a signing certificate is a recurring cost I haven't taken on for a free program), so Windows and antivirus tools treat it as an unknown, newly downloaded executable and may warn about it or block it. This is expected — the installer is built by GitHub Actions directly from the source in this repository, and GitHub shows a SHA-256 digest beside each file on the release page — run `Get-FileHash Yaesu_Web_Control_Setup.exe` in PowerShell and the two should match. Follow whichever of these matches what you see:

**Microsoft Defender quarantines the download, or the installed program on first launch, and says "This program is dangerous and executes commands from an attacker"**
This is Defender's cloud/machine-learning classifier (the detection name ends in `!ml` — with v2.5.1 it was `Trojan:Win32/Wacatac.C!ml` on the installed `Yaesu_Web_Control.dll`, and it arrived with a definition update the day after release). It is a false positive that hits many unsigned .NET applications, it can appear on a build that scanned clean the day before, and the installer itself usually scans clean because the file it objects to is inside. To recover: open **Windows Security → Virus & threat protection → Protection history**, click the entry, choose **Actions → Allow on device**, then either download the installer again or simply start YWC again if it was the installed program that was taken. Choose *Allow*, not just *Restore* — Restore puts the file back but Defender may take it again at the next scan. Don't switch Defender off — allowing this one file is enough. If this happens to you, please post the detection name shown in Protection history in a Discussion; I report each one to Microsoft as a false positive, and that is what makes it stop happening for the next person.

**Norton (or other antivirus) flags the file as malware**
This is the same false positive, caused by the executable being unsigned and newly downloaded. In Norton, go to **Security → History**, find the quarantined file, and choose **Restore & Exclude** (or the equivalent Allow option in your antivirus).

**"Windows protected your PC" (SmartScreen) or an Unknown Publisher prompt when you run the installer**
Click **More info → Run anyway**.

**Right-click → Properties → Unblock**
Windows marks files downloaded from the internet as untrusted. Before running the installer, right-click the file, choose **Properties**, and if you see an **Unblock** checkbox at the bottom of the General tab, tick it and click OK.

**"This app can't run on your PC" — Smart App Control**
If Smart App Control is enabled it will block unsigned apps entirely. Go to **Settings → Privacy & Security → Windows Security → App & Browser Control → Smart App Control** and switch it to **Off**, then restart your PC and try again.

The screenshot below shows the Smart App Control setting:

![Smart App Control Screenshot](pictures/SmartAppControl.png)

These are one-time steps — once the app is installed you won't see them again.

---

## 📡 Spectrum display with an SDR

A real-time spectrum and waterfall, fed by an SDR connected to the transceiver's 9 MHz
IF output. On the dual-receiver FTdx101MP and FTdx101D, **two SDRs** can be used, one per
VFO. Spans run from 1 kHz to 2 MHz, the receiver's IF passband is drawn on the trace, and
a click tunes the VFO to the signal under the pointer.


> ## ⚠️ SDR safety — read before connecting
>
> An SDR receiver's front end is **extremely sensitive** and can be **destroyed by even a small amount of TX RF**.
>
> - **FTdx101MP / FTdx101D / FTDX3000** (have IF output): connect the SDR to the rear-panel **IF OUT** RCA socket only. This is an internal low-level signal — safe during TX. Do **not** connect to an antenna port.
> - **FTdx10 / FT-710** (no IF output): if you connect an SDR to an antenna port you **must** disconnect it during TX, or use a dedicated receive-only antenna well away from your TX antenna, or fit a T/R relay or PIN-diode T/R switch in front of the SDR. Transmitting with the SDR coax wired directly to your TX antenna will damage the SDR.
> - In all cases, an antenna physically close to your TX antenna can still couple enough RF into the SDR to damage it. When in doubt, disconnect.
>
> YWC also shows this warning on the Settings page when an SDR is configured, and a more prominent danger banner appears if your selected radio is an FTdx10 or FT-710.

**Devices:** SDRplay RSP1 / RSP1A / RSP1B / RSP2 / RSPdx / RSPduo through the SDRplay API
(installed separately from [sdrplay.com](https://www.sdrplay.com/downloads/)); RTL-SDR,
Airspy and HackRF through the bundled SoapySDR drivers, which need no separate install and
which I have not tested myself - feedback welcome.

The full span list, why two SDRs rather than one RSPduo, why an SDRplay rather than a £25
RTL-SDR dongle, and what the brief pause on a span change is, are in
[USER_MANUAL.md §6.3](USER_MANUAL.md#63-sdr-spectrum-display).

## 📝 CW Reader and ⌨️ CW Send

**CW Read** listens to the radio's receive audio and prints the Morse as text. It needs no
extra hardware, never transmits, and works on Windows, macOS, Linux and Docker alike.
**Reader Mode** is one button that sets the radio up the way the decoder wants it and puts
your mode, width and APF back when you stop; **ZIN** puts the station on your CW pitch.
Every session writes a timestamped transcript, and confirmed contacts append to a plain
ADIF file.

**I want to be honest about what a Morse decoder is.** On a strong, clean, machine-sent
signal it is close to perfect. On a marginal one it prints plausible-looking rubbish that
looks exactly like good copy, and it has no idea which it is doing - in my own testing it
reported full confidence on nearly six hundred characters of complete junk. So the status
line says what it is actually seeing rather than guessing, nothing is corrected or hidden,
and the log form offers a callsign as a button to click rather than filling it in for you.

**CW Send** is the other half: type a line, press Enter, and the radio keys it - no key, no
keyer interface, no extra audio, only the CAT cable. No Yaesu has a "send this text"
command, so the line goes out through keyer memory 5 in pieces of up to 50 characters at
word boundaries. The wait between pieces is Morse timing worked out from the text and the
keyer speed, because I measured the radio's "playback finished" flag through a whole
message on the air and it never once asserted. With break-in off the line plays to the
monitor and nothing goes on the air.

The decoder itself lives in [Radio_Web_Control_Core](https://github.com/mm5agm/Radio_Web_Control_Core),
shared with Icom Web Control, because a Morse decoder does not know what a radio is - and
not one line of it had to change to read Morse off a different brand.

Full detail: [§20 CW Reader](USER_MANUAL.md#20-cw-reader) and
[§21 CW Send](USER_MANUAL.md#21-cw-send).

## 📺 Radio Display - the radio's own screen in the browser

**This feature is Fabio Valente's (CR7CDC) work, not mine.** He designed it, wrote it and
carried it through three pull requests ([#97](https://github.com/mm5agm/Yaesu_Web_Control/pull/97),
[#117](https://github.com/mm5agm/Yaesu_Web_Control/pull/117),
[#120](https://github.com/mm5agm/Yaesu_Web_Control/pull/120)); I tested it on my FTdx101MP
and made a couple of suggestions.

The FTdx101, FTdx10 and FT-710 repeat their front-panel TFT on a DVI-D socket. Radio Display
captures it with an ordinary HDMI-to-USB stick and shows it in the browser, with the radio's
own scope controls docked beside it over CAT. Nothing is transmitted and nothing on the radio
is changed by the capture.

> **Electrical safety.** The DVI-D socket on these radios is not a standard-conforming HDMI source, and not every passive DVI-D-to-HDMI adapter is safe on every Yaesu — there are community investigations of damaged radios. YWC does not supply, protect or vouch for any cable or capture device; the chain from the radio's socket to the PC is your responsibility. The investigation video above is my own look at the question; the cable and card above are what I run on my own FTdx101MP and are not a guarantee for yours.

The cable and capture stick I use, the two explanatory videos, the 800x600 setting the radios
all need, and the Docker mapping are in
[USER_MANUAL.md §19 Radio Display](USER_MANUAL.md#19-radio-display).

## Project direction

The summer of 2026 went into three big pieces: the **CW Reader**, and two that are Fabio Valente's (CR7CDC) work — **Radio Display** and **Remote Audio**, and the whole of running YWC **outside Windows** (macOS, Linux, Docker on a Raspberry Pi); my part in those was testing on my FTdx101MP and a Raspberry Pi 3B. Together they mean the radio can now be operated, heard and *seen* from a browser anywhere on the network. What comes next depends on what people ask for — see the note at the top of this page. **Voice control** remains a first-class accessibility feature for partially sighted and blind operators — hands-free band changes, frequency entry, mode switching, and rig status without needing to see the screen.

**Voice control v1 shipped in v2.4.0-pre1 (2026-06-24)** and has been extended through the v2.4.0 pre-release series, most recently with independent per-VFO control (separate mic buttons for VFO A and VFO B) and a full Voice Language Pack Manager for editing phrases and macros. It uses **Windows' built-in speech recognition (SAPI 5 / `System.Speech`)** running locally on the user's PC, driven by an editable phrase pack tuned to ham-radio vocabulary, with a press-and-hold microphone button beside each VFO panel — the command targets whichever VFO's button you're holding (single-receiver radios show only one button). Recognised audio never leaves the PC; no cloud account, no public endpoint, no DNS or tunnel setup. A microphone connected to the PC is the only hardware requirement. See [USER_MANUAL.md §17 Voice Control](USER_MANUAL.md#17-voice-control) for what voice does, the full command list, and how to enable it. Feedback from real users is what's wanted right now — please try it and report back.

**On the abandoned Amazon Alexa route:** an earlier proof of concept routed voice through an Echo device over a Cloudflare tunnel into YWC. It worked end-to-end including signature verification, but setting it up required the user to own a domain, run a Cloudflare account, configure a custom Alexa Skill in the Amazon Developer Console, and install `cloudflared` — well over an hour of fiddly setup for the average ham. The local-SAPI approach above is dramatically simpler (one Windows speech-pack install, one Settings toggle) and runs entirely offline. The Alexa branch is therefore retired; the local mic approach is the supported path going forward.

## Staying informed about updates

Recent versions of YWC include an in-app update check that pops up a banner the first time you run it after a new release lands, listing what has changed. If you're already running v2.2.x or later you'll get those notifications automatically — no action needed.

**What it offers you depends on which build you are running.** On a **full release** the banner only ever tells you about another full release: pre-releases are deliberately left out, so if you don't go looking for one you will never be told it exists, and there is no setting that changes that. On a **pre-release** — anything with a `-pre` in its name — it also tells you when a newer pre-release arrives, marked **Pre-release** so you can see what you're being offered, because being kept up to date is the whole point of testing one. Neither ever offers you a nightly `unstable-` build. Full detail: [§5.1](USER_MANUAL.md#51-top-bar).

If you're on an older version that pre-dates the update check, or you want to know about a new release before you've launched the app, **any one of these will keep you in the loop**:

- **GitHub release notifications** (most reliable, free, no spam):
  1. Make sure you're signed in to GitHub
  2. Visit https://github.com/mm5agm/Yaesu_Web_Control
  3. Click the **Watch** dropdown at the top-right of the page
  4. Choose **Custom** and tick only **Releases**
  5. Save — you'll get one email per release, and nothing in between

- **RSS / Atom feed** — if you use a feed reader (Feedly, NewsBlur, Inoreader, Thunderbird, etc.), subscribe to https://github.com/mm5agm/Yaesu_Web_Control/releases.atom — new releases appear in your reader without any account or email signup.

If you're talking to another Yaesu operator running an older YWC, **a heads-up that a new version exists is the most reliable way it reaches them**. Word of mouth still works — there's no email list of all downloaders and no way to reach the very early users who don't fall into any of the channels above.

---

## Contributors

YWC is mostly my own work, but I'm grateful for the community contributions that have improved it:

- **Fabio Valente (CR7CDC)** — by now a good deal more than a contributor. The whole of **Radio Display** — HDMI capture of the radio's screen, the docked CAT scope controls, the Linux and Docker capture path — is his ([#97](https://github.com/mm5agm/Yaesu_Web_Control/pull/97), [#117](https://github.com/mm5agm/Yaesu_Web_Control/pull/117), [#120](https://github.com/mm5agm/Yaesu_Web_Control/pull/120)). So is **Remote Audio** ([#92](https://github.com/mm5agm/Yaesu_Web_Control/pull/92), [#112](https://github.com/mm5agm/Yaesu_Web_Control/pull/112)), and so is the entire **macOS / Linux / Docker** side — the CAT-only host, the macOS app and menu-bar item, the Dockerfile and multi-architecture image, and the release pipeline that builds them all. I did none of that; I tested it on a Raspberry Pi 3B ([#90](https://github.com/mm5agm/Yaesu_Web_Control/pull/90), [#133](https://github.com/mm5agm/Yaesu_Web_Control/pull/133), [#134](https://github.com/mm5agm/Yaesu_Web_Control/pull/134)). He also cut the CAT polling and persistence overhead and scaled the power meter to each radio's own rating ([#122](https://github.com/mm5agm/Yaesu_Web_Control/pull/122)), kept both VFO panels live on single-receiver radios ([#119](https://github.com/mm5agm/Yaesu_Web_Control/pull/119)), set up the daily `unstable` snapshot builds ([#115](https://github.com/mm5agm/Yaesu_Web_Control/pull/115)), and earlier contributed the keyboard transmit shortcut and split-mode fixes ([#79](https://github.com/mm5agm/Yaesu_Web_Control/pull/79)), FTdx10 roofing-filter support ([#80](https://github.com/mm5agm/Yaesu_Web_Control/pull/80)) and the single-receiver VFO routing fixes ([#81](https://github.com/mm5agm/Yaesu_Web_Control/pull/81)).

---

## Release Notes

**Only the version you are installing is listed here.** Earlier releases are in
[RELEASE_HISTORY.md](RELEASE_HISTORY.md); their installers are still on the
[releases page](https://github.com/mm5agm/Yaesu_Web_Control/releases).

## 2026-10-07 - v2.5.3-pre7 (pre-release)

*An Experimental UI beside the Classic one, meter readings that stay with their own meter, and capture dongles remembered by device instead of by their place in the list. Everything in v2.5.3-pre6 is included. Install it over v2.5.2 or an earlier pre-release; your settings are kept.*

- **Experimental UI** (Fabio Valente, CR7CDC). A new **Experimental UI** link in the top bar opens a dark, dockable workspace at `/flexui`. The VFOs, spectrum, meters, Radio Display, Radio Scope and the other controls are panels that can be dragged, docked, tabbed together and resized, and an arrangement can be saved as a named layout. The Classic page is unchanged and is still where YWC opens. The one change Classic users will see is the top bar: its links have been reordered and have icons, and none has been removed. It is experimental: a fix made to the Classic page doesn't automatically reach it. I've tested it on my FTdx101MP on receive and transmit, on both receivers. [§5.22](USER_MANUAL.md#522-experimental-ui).
- **Meter readings stay with their own meter.** A reply from the radio that arrived late was given to the next reading of the same kind, so the power gauge could briefly show the compression or SWR reading instead, and VFO B's S-meter could show VFO A's. Each reading now only accepts its own reply.
- **Two capture dongles of the same model** - one per radio - are told apart by the dongle itself, not by their place in the device list, so Radio Display no longer opens the wrong radio's screen after a replug or a reboot. Keep each dongle in the same USB socket: these dongles have no serial number, so the socket is what identifies them, and a dongle moved to another socket has to be chosen again. If the chosen dongle isn't plugged in when the stream starts, the panel says **Not connected** instead of disappearing. The first time you pick a device after installing, the setting moves to the new form; until then it works as before. [§6.9](USER_MANUAL.md#69-radio-display).
