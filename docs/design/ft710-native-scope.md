# FT-710: the radio's own scope as the spectrum panel

Status: **built, not yet run against an FT-710.** I don't own one. ut9fj
offered to bench-check it in Discussion #187, and nothing here counts as
working until that has been done.

## What it is

The FT-710 has an FTDI FT4222H USB-to-SPI bridge inside it. When menu item
**EX 03-01-26 SCU-LAN10** is set to ON, the bridge shows up as a third USB
device next to the two CAT serial ports and the sound card, and it streams
the data for the radio's own spectrum scope. No SCU-LAN10 unit is needed. The
menu switch is all it takes, and Yaesu's FT-710 CAT manual lists the item
(`26 SCU-LAN10 0: OFF 1: ON`).

This gives an FT-710 owner a spectrum and waterfall in YWC with no SDR, no
antenna splitter and no T/R switch. The FT-710 has no IF output, so until now
an SDR on that radio meant an antenna-port connection and a risk to the SDR
on every transmission.

## Credits

None of the protocol was found by me. It is a C# port of what other GPL
projects measured on real radios:

- **kd9taw/Nexus**, `crates/tempo-audio/src/yaesu_wf.rs` (GPL-3.0). The bench
  measurements behind it were made by **ON8ST** on his FT-710 between
  2026-08-17 and 2026-08-20.
- **ratmandu/YaesuWFTesting** (MIT) published the first frame layout.
- The test fixture `Tests/YaesuWebControl.Tests/Fixtures/ft710_wf_frame.bin`
  is ON8ST's captured frame, taken from the Nexus repository (commit 5320fb3).

## How it fits in

It runs through the existing SDR machinery so that nothing new has to be
built around it:

```
FT-710 FT4222H ──USB──> Yaesu_Sdr_Worker (Ft4222Bridge + Ft710ScopeFrame)
                          │  SpectrumFrame, centre 0 / span 0 ("unplaced")
                          ▼
                        SdrManager ──── SS05 / SS06 over CAT, once a second
                          │  places each frame: centre = VFO A dial,
                          │  span = the radio's scope span, rfOrdered = true
                          ▼
                        SignalR SpectrumUpdate ──> spectrum-panel.js
```

- **Worker.** The operator picks *FT-710 internal scope* in the Settings SDR
  dropdown, and the device key is `yaesu-scope:ft710`. `WorkerHost` sees the
  key and runs `RunRadioScopeAsync` instead of opening an SDR. The FTDI code
  runs in the worker process for the same reason the SDR code does: it is
  native code we didn't write, and if it faults, only the worker goes down.
- **FTDI libraries.** `ftd2xx.dll` (D2XX) and `LibFT4222-64.dll` are closed
  source, so a GPL program can't ship them. `Ft4222Libraries` looks for them
  in the program folder, System32 and PATH. If either is missing, the panel
  shows how to install them. That is the same approach as `sdrplay_api.dll`.
- **Open.** `Ft4222Bridge.Open` picks D2XX device ID `0x0403601C` and prefers
  interface A. It sets SPI master mode (single I/O, clock/16, CPOL high, CPHA
  trailing, SS0) and a 48 MHz system clock. Nexus saw the first open of a
  freshly-appeared bridge hang for minutes, so the open runs under a 20 s
  timeout. If that runs out, the worker exits and SdrManager tries again.
- **Frames.** Each frame is 4096 bytes, ending in the trailer `FF 01 EE 01`
  repeated four times. The reads aren't aligned to frames, so each read is
  8192 bytes. `Ft710ScopeFrame.Assembler` cuts the newest whole frame out of
  the stream, keeping at most three frames of unmatched bytes.
- **Row.** The MAIN row is the 850 bytes at offset 0. Bytes 850 and 851 are
  always 0 and are left out. A lower byte means a *stronger* signal. Each
  byte becomes `-20 - 0.5 × byte` dB, and bins run low to high frequency.
- **Placement.** The frame carries no frequency; its parameter block is
  zeroes on this model. So SdrManager reads `SS05;` for the span code and
  `SS06;` for the mode once a second, under the same gate as the Radio Scope
  controller (`ScopeCatGate`). Each frame is then drawn centred on VFO A at
  that span.
  - **Only CENTER modes are placed:** 3DSS CENTER, and W/F CENTER in EXPAND
    or NORMAL. In CURSOR mode the window stays still while the dial moves,
    and in FIX mode it starts at a band edge that no CAT command reports.
    Either would put a believable picture at the wrong frequency. In those
    modes the panel says which mode the radio is in and asks for CENTER.
  - **YWC never changes the radio's scope mode itself.**
- **Browser.** A frame with `rfOrdered: true` isn't reversed. The 9 MHz IF
  flip applies to IF-tap SDRs only. Its axis is centred on the frame's
  `centreHz` rather than on an IF offset. The SDR span buttons are hidden,
  because the span is whatever the radio is set to, and `/api/sdr/span`
  refuses the request for a scope panel.

## What is not measured, and what ut9fj is asked to check

1. **Signals land at the right frequency.** Tune a steady carrier (a
   broadcast station, a beacon or a signal generator) and check that the peak
   sits under the dial at several spans: 10k, 50k, 200k and 1M. Then tune
   off by a known amount and check that the peak moves by that amount, in the
   right direction.
2. **SCOPE CTR (menu 04-02-02).** FILTER and CARRIER POINT may put the centre
   in different places. Repeat item 1 with each setting. Nexus found a
   residual offset of about 1.1 kHz, and this setting may be the reason.
3. **Levels.** The 0.5 dB per step figure is a guess that makes the display
   look like the radio's own screen. Compare a signal's height against the
   S-meter, or step a generator 10 dB, and report how far the trace moves.
4. **Opening.** Record how long the first open takes after power-on or
   plug-in, and whether it ever hits the 20 s timeout. Check that it recovers
   when the USB lead is pulled and put back.
5. **Modes.** Check that CURSOR and FIX show the message rather than a
   waterfall, and that returning to CENTER brings the waterfall back within
   about a second.
6. **Span changes on the radio.** Check that the width follows within about a
   second.
7. **VFO B / split.** The panel is placed on VFO A only. Report what the
   radio's scope actually shows when VFO B is the receive VFO.
8. **CAT load.** The two extra reads a second share the port with the meter
   poll. Check that the meters and the frequency display still feel as they
   did.

Receive only. Nothing here transmits, and none of the checks needs a
transmission.

## Not handled

- **FTdx10 and FTdx101.** They put similar data on the rear ACC socket,
  which needs an external USB-to-SPI adapter and a lead. Nobody has asked for
  it yet.
- **macOS and Linux.** LibFT4222 exists for both, under other names, but the
  SDR worker is Windows-only today.
- **Setting the span or mode from YWC.** The Radio Scope panel can already
  do that over CAT on the FTdx101. Enabling it for the FT-710 is a separate
  bench check (`scripts/probe/ss-probe.ps1`), and it changes the operator's
  front panel.
