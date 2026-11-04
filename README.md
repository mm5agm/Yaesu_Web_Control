
# Yaesu Web Control

![Latest release](https://img.shields.io/badge/Latest%20release-v2.5.4-blue?style=flat-square)
![Downloads](https://img.shields.io/github/downloads/mm5agm/Yaesu_Web_Control/latest/Yaesu_Web_Control_Setup.exe?label=Downloads&style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)

Yaesu Web Control (YWC) runs a Yaesu HF transceiver from a browser on your PC, tablet or phone: frequency, mode, meters, a live spectrum and waterfall, CW, RTTY, DX spots and voice control. It is free and open source, and it runs on your own PC or Raspberry Pi with no account and no cloud service.

**[Download the latest installer](https://github.com/mm5agm/Yaesu_Web_Control/releases/latest)** | [User manual](USER_MANUAL.md) | [Discussions](https://github.com/mm5agm/Yaesu_Web_Control/discussions) | [Website](https://mm5agm.co.uk)

![The Radio panel on an FTdx101MP, with an SDR spectrum and waterfall for Main and Sub](pictures/Radio_Panel.png)

## Why this application exists

I wrote YWC because I can't see the FTdx101MP's controls without using a magnifying glass. The Classic page was written specifically with partially sighted operators in mind, so that they can operate the radio with a screen reader and voice control. Everything else has grown from there.

![The Classic page: meters, the radio's screen over HDMI with its scope controls, an SDR spectrum for each receiver, and a card for each VFO](pictures/Classic.png)

## What it does

**Four views of the radio**

- **[Classic](USER_MANUAL.md#5-main-control-panel)**: everything on one page, made for [NVDA and Windows Narrator](USER_MANUAL.md#16-accessibility-and-screen-readers), with [voice control](USER_MANUAL.md#17-voice-control), [spoken announcements](USER_MANUAL.md#516-voice-announcements), High Contrast mode and labels you can rename in any language.
- **[Radio panel](USER_MANUAL.md#523-radio-panel)**: laid out like the front of the radio, with working keys, knobs and a main dial, in several themes.
- **[Experimental UI](USER_MANUAL.md#522-experimental-ui)**: a workspace of dockable panels, by Fabio Valente (CR7CDC).
- **[RTTY Contest](USER_MANUAL.md#524-rtty-contest)**: only what a contest RTTY operator uses, on one screen. *Still being tested.*

**See the band**

- **[SDR spectrum and waterfall](USER_MANUAL.md#63-sdr-spectrum-display)** from an SDR on the radio's IF output, one per receiver on an FTdx101. Spans run from 1 kHz to 2 MHz, the IF passband is drawn on the trace, and a click tunes to the signal. Please read the [SDR safety warning](#sdr-safety) first. **The FT-710 needs no SDR:** it can [send its own scope over the USB lead](USER_MANUAL.md#ft-710-the-radios-own-scope-no-sdr-needed-experimental). *Experimental, and Windows only.*
- **[Radio Display](USER_MANUAL.md#19-radio-display)**: the radio's own screen in the browser, captured from its DVI-D socket with an HDMI-to-USB stick. Click a signal to tune to it, and click the readouts and soft-buttons, with the radio's [scope controls](USER_MANUAL.md#194-cat-scope-controls) beside it. By Fabio. Please read the [DVI-D safety warning](#dvi-d-safety) first, and set the radio's EXT MONITOR PIXEL menu to 800x600.
- **[DX spots](USER_MANUAL.md#517-dx-spots-list)** on the spectrum, and a [watch list](USER_MANUAL.md#514-dx-watch-panel) that alerts you when a callsign or prefix appears.

**Operate**

- **[CW Reader](USER_MANUAL.md#20-cw-reader)** prints the Morse in the radio's receive audio as text, with no extra hardware. On a strong, clean signal it is close to perfect. On a marginal one it prints plausible rubbish, and the status line says what it is actually hearing.
- **[CW Send](USER_MANUAL.md#21-cw-send)**: type a line, press Enter, and the radio keys it. Only the CAT cable is needed.
- **[CW Paddle](USER_MANUAL.md#23-cw-paddle)** sends from your own paddle when the radio is somewhere else, through a HaliKey MIDI or an ESP32-S3 board and three wires. *Still being tested.*
- **[RTTY tuner](USER_MANUAL.md#22-rtty)**: a crossed-ellipse tuning scope and text decode. RTTY is receive only: transmit from your logger, or from MMTTY.
- **[Remote Audio](USER_MANUAL.md#18-remote-audio)**: receive audio in the browser's speakers, and the browser's microphone into the radio. By Fabio. For your own network or a VPN.
- **[Voice control](USER_MANUAL.md#17-voice-control)**: hold the mic button and say the band, mode or frequency. Recognition runs locally, with no cloud service. Windows only.
- **[Memory banks](USER_MANUAL.md#8-radio-memories)** with ADIF import, **[pop-out windows](USER_MANUAL.md#521-pop-out-windows)** for a second monitor, and **[WSJT-X, Log4OM, JTAlert and GridTracker](USER_MANUAL.md#9-external-applications)** alongside.


![The Radio Display: the radio's TFT captured over HDMI, with the scope controls docked beside it](pictures/Radio_Display_Docked.png)

## Release Notes

**v2.5.4 brings RTTY Contest and the CW Paddle.** RTTY Contest is a screen for RTTY contesting, and every part of it can go to a window of its own on another monitor. Bruce VK2RT asked for it and tested it through sixteen pre-releases. The CW Paddle sends CW from your own paddle when you operate the radio remotely, through a HaliKey or an ESP32-S3 board you program from the page.

[All of v2.5.4](RELEASE_HISTORY.md#2026-11-03---v254) | [Every earlier version](RELEASE_HISTORY.md) | [Installers](https://github.com/mm5agm/Yaesu_Web_Control/releases)

## Supported transceivers

| Transceiver | Power | Receivers | Notes |
|-------------|-------|-----------|-------|
| FTdx101MP | 200 W | Dual | All features supported |
| FTdx101D | 100 W | Dual | All features supported |
| FTdx10 | 100 W | Single | Two VFOs; no rear-panel IF output for spectrum |
| FT-710 | 100 W | Single | Two VFOs; no rear-panel IF output, but can show its own scope over USB with no SDR (experimental) |
| FTDX3000 | 100 W | Single | Two VFOs; no memory tag (MT) command |
| FT-991A | 100 W (50 W on 2 m / 70 cm) | Single | HF + 6 m + 2 m + 70 cm; C4FM; new in v2.5.3 — built from the 991A's CAT manual and still being tested on a real radio |
| FTDX5000MP | 200 W | Dual | New — available to test; power meter calibration still being refined |
| FTDX5000D | 200 W | Dual | New — available to test; power meter calibration still being refined |

I own and test on the **FTdx101MP**. The other models are built from Yaesu's published CAT documentation and confirmed by the people who own them.

**If your Yaesu isn't listed, I'll gladly add it, and all it takes from you is ordinary operating, not programming.** Use your radio normally with YWC and tell me what happens on the [Discussions tab](https://github.com/mm5agm/Yaesu_Web_Control/discussions). Now and then I may ask you to try a pre-release, or to send a log: the Diagnostics page has **Start fresh test log** and **Download test log** for that.

**FTX-1:** a user has it working on HF with the model set to *FTdx10*. A dedicated profile is on the way once someone can help confirm its 6 m, 2 m and 70 cm memory handling.

## Platforms

- **Windows 10/11:** the installer, with everything in it.
- **macOS:** a DMG for Apple Silicon or Intel, with no SDR spectrum or voice control. It isn't notarized, so the first time, right-click it and choose **Open**.
- **Linux and Raspberry Pi:** from source, or the Docker image `ghcr.io/mm5agm/yaesu_web_control`, with no SDR spectrum or voice control.

If the radio's ports don't appear, or Test Connection gets no reply, on Windows or macOS, install the [Silicon Labs CP210x driver](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) and reboot. Linux already has it.

The full comparison is in [the manual, §1](USER_MANUAL.md#1-introduction), and build commands are in [docs/BUILDING.md](docs/BUILDING.md).

## ⚠️ Windows Security Warnings on First Install

The installer is not code-signed, so Windows and antivirus tools may warn about it or block it. It is built by GitHub Actions from the source in this repository, and the release page shows a SHA-256 digest for each file that you can compare with `Get-FileHash`.

- **Defender says "This program is dangerous":** a false positive. Open **Windows Security → Protection history**, click the entry and choose **Actions → Allow on device**, not just Restore. Then download again, or start YWC again.
- **Norton or another antivirus flags it:** the same false positive. Choose **Restore & Exclude**, or its equivalent.
- **"Windows protected your PC":** click **More info → Run anyway**.
- **"This app can't run on your PC":** that is Smart App Control, which has to be switched off.

These are one-time steps. The full detail is in [the manual, §2.1](USER_MANUAL.md#21-windows-installer).

## Safety

This software interacts with radio hardware. I have used only the official Yaesu CAT commands as per the manual, however, you use entirely at your own risk. Please read the licence. Always verify transmit frequencies, power levels, and settings before use.

### SDR safety

An SDR receiver's front end is **extremely sensitive** and can be **destroyed by even a small amount of TX RF**.

- **FTdx101MP / FTdx101D / FTDX3000** (have IF output): connect the SDR to the rear-panel **IF OUT** RCA socket only. This is an internal low-level signal — safe during TX. Do **not** connect to an antenna port.
- **FTdx10 / FT-710** (no IF output): if you connect an SDR to an antenna port you **must** disconnect it during TX, or use a dedicated receive-only antenna well away from your TX antenna, or fit a T/R relay or PIN-diode T/R switch in front of the SDR. Transmitting with the SDR coax wired directly to your TX antenna will damage the SDR.
- In all cases, an antenna physically close to your TX antenna can still couple enough RF into the SDR to damage it. When in doubt, disconnect.

YWC also shows this warning on the Settings page when an SDR is configured, and a more prominent danger banner appears if your selected radio is an FTdx10 or FT-710.

**Devices:** SDRplay RSP1 / RSP1A / RSP1B / RSP2 / RSPdx / RSPduo through the SDRplay API ([installed separately](https://www.sdrplay.com/downloads/)); RTL-SDR, Airspy and HackRF through the bundled SoapySDR drivers, which I have not tested myself. More in [§6.3](USER_MANUAL.md#63-sdr-spectrum-display).

### DVI-D safety

The DVI-D socket on these radios is not a standard-conforming HDMI source, and not every passive DVI-D-to-HDMI adapter is safe on every Yaesu: there are community investigations of damaged radios. YWC does not supply, protect or vouch for any cable or capture device, and the chain from the radio's socket to the PC is your responsibility. [My own look at the question](https://www.youtube.com/watch?v=dtlziYEsXxE) is worth watching before you plug anything in, and the cable and stick I use on my own FTdx101MP are in [§19](USER_MANUAL.md#19-radio-display). They are not a guarantee for your radio. There is also a [video of the feature in action](https://www.youtube.com/watch?v=1EY7m5e91TI).

## Privacy

Yaesu Web Control runs on your own PC and does not send your data anywhere. It makes only these network connections:

- Between your browser and the PC it runs on, on your own network, for the web interface.
- From your browser to GitHub (api.github.com), to see whether a newer release exists. It reads the release list; nothing about you or your station is sent. See [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
- To a DX cluster, only if you turn DX spots on and enter a cluster yourself. It logs in with the callsign you enter, as every DX cluster requires.

## Feedback and updates

I operate FT8 on an FTdx101MP with an SDRplay, so that's the combination that actually gets tested, and new features depend on what other operators ask for. If you use another mode, radio or SDR, or a tablet, even a one-line "works fine" or "this is annoying because..." on the [Discussions tab](https://github.com/mm5agm/Yaesu_Web_Control/discussions) tells me something I can't find out on my own.

YWC shows a banner the first time you run it after a new release ([§5.1](USER_MANUAL.md#51-top-bar)). To hear about one outside the app, choose **Watch**, **Custom**, **Releases** on [the repository page](https://github.com/mm5agm/Yaesu_Web_Control), or subscribe to [the releases feed](https://github.com/mm5agm/Yaesu_Web_Control/releases.atom).

## Contributors

YWC is mostly my own work, but I'm grateful for the community contributions that have improved it:

- **Fabio Valente (CR7CDC)** — by now a good deal more than a contributor. The whole of **Radio Display** — HDMI capture of the radio's screen, the docked CAT scope controls, the Linux and Docker capture path — is his ([#97](https://github.com/mm5agm/Yaesu_Web_Control/pull/97), [#117](https://github.com/mm5agm/Yaesu_Web_Control/pull/117), [#120](https://github.com/mm5agm/Yaesu_Web_Control/pull/120)). So is **Remote Audio** ([#92](https://github.com/mm5agm/Yaesu_Web_Control/pull/92), [#112](https://github.com/mm5agm/Yaesu_Web_Control/pull/112)), and so is the entire **macOS / Linux / Docker** side — the CAT-only host, the macOS app and menu-bar item, the Dockerfile and multi-architecture image, and the release pipeline that builds them all. I did none of that; I tested it on a Raspberry Pi 3B ([#90](https://github.com/mm5agm/Yaesu_Web_Control/pull/90), [#133](https://github.com/mm5agm/Yaesu_Web_Control/pull/133), [#134](https://github.com/mm5agm/Yaesu_Web_Control/pull/134)). He also cut the CAT polling and persistence overhead and scaled the power meter to each radio's own rating ([#122](https://github.com/mm5agm/Yaesu_Web_Control/pull/122)), kept both VFO panels live on single-receiver radios ([#119](https://github.com/mm5agm/Yaesu_Web_Control/pull/119)), set up the daily `unstable` snapshot builds ([#115](https://github.com/mm5agm/Yaesu_Web_Control/pull/115)), and earlier contributed the keyboard transmit shortcut and split-mode fixes ([#79](https://github.com/mm5agm/Yaesu_Web_Control/pull/79)), FTdx10 roofing-filter support ([#80](https://github.com/mm5agm/Yaesu_Web_Control/pull/80)) and the single-receiver VFO routing fixes ([#81](https://github.com/mm5agm/Yaesu_Web_Control/pull/81)).
