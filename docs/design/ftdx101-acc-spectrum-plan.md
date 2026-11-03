# FTdx101MP: spectrum data from the ACC socket via an ESP32-S3

Status: **draft plan, nothing built.** The cable (13-pin mini-DIN male to
female, Amazon UK B0BRQDDTTZ) is being ordered.

## What it is

The ACC socket on the back of the FTdx101 is the port the SCU-LAN10 plugs
into. While a listener answers its heartbeat, the radio streams the data for
its own scope over SPI: 4096-byte frames, 25 a second, with the waterfall
for both receivers, the audio FFT and waveform, and a block of settings.

It's the same frame format YWC already decodes for the FT-710
([ft710-native-scope.md](ft710-native-scope.md)), where the radio has the
SPI bridge built in. The FTdx101 has no bridge, so an ESP32-S3 does the job:
it reads SPI from the ACC socket, answers the heartbeat, and sends the
frames to the PC over USB or Wi-Fi.

Receive-only. Nothing in this plan keys the radio.

## ACC socket pinout

From page 17 of the FTdx101 operating manual
(`docs/manuals/FTDX101MP_D_OM_ENG_EH068H213_2102P-JS-3.pdf`, PDF page 19).

| Pin | Manual label | Function | Direction | This project |
|---|---|---|---|---|
| 1 | Power ON | Remote power-on line (believed to be how the SCU-LAN10 switches the radio on) | into radio | **Not connected** |
| 2 | GND | Signal ground | — | ESP32 GND |
| 3 | DATA1 | SPI data line 1. Function on the FTdx101 not yet confirmed; may carry the Sub receiver | out of radio | Probe first, then ESP32 GPIO14 if useful |
| 4 | DATA2 | SPI data line 2: the scope frames (4096 bytes each) | out of radio | ESP32 GPIO11 |
| 5 | CLOCK | SPI clock, 2 MHz, SPI mode 1. The radio is the SPI master | out of radio | ESP32 GPIO10 |
| 6 | CS | SPI chip select, active low: low for each frame, 25 times a second | out of radio | ESP32 GPIO13 |
| 7 | 3.3V | 3.3 V supply from the radio | out of radio | **Not connected.** Don't power anything from it |
| 8 | 13.8V | 13.8 V supply from the radio, fused inside the radio | out of radio | **Not connected.** A short blows the internal fuse |
| 9 | Reserve | Reserved by Yaesu, function unknown | — | **Not connected** |
| 10 | Reserve | Reserved by Yaesu, function unknown | — | **Not connected** |
| 11 | CNT RX | Control serial into the radio (UART, 115200). Carries the reply to the heartbeat, within 10 ms | into radio | ESP32 GPIO39 (UART TX) |
| 12 | CNT TX | Control serial out of the radio (UART, 115200). Sends the heartbeat `alive\0` once a second | out of radio | ESP32 GPIO38 (UART RX) |
| 13 | Reserve | Reserved by Yaesu, function unknown | — | **Not connected** |

"Direction" is as seen from the radio. The labels in the "Manual label"
column are exactly as printed in the manual; the functions come from the
manual where it says, and otherwise from the SCU-LAN10 protocol work listed
under "Where the code is". Pins 1, 7, 8, 9, 10 and 13 are what's left to
confirm or describe properly before this is written up for other users.

The socket is a **13-pin mini-DIN**: three rows of four pins plus one at the
bottom by the key. The manual's drawing shows the socket on the radio. That
is the same view as the female end of the extension cable; looking into the
male plug, the layout is mirrored left to right.

## Pins we need

Seven of the 13: pins 2, 4, 5, 6, 11 and 12, plus pin 3 for probing.

| ACC pin | Manual label | Function | Direction | Why |
|---|---|---|---|---|
| 2 | GND | Signal ground | — | Common ground with the ESP32 |
| 3 | DATA1 | SPI data line 1 | radio → ESP32 | **Probe only at first.** May carry Sub receiver data on the FTdx101 |
| 4 | DATA2 | SPI data line 2 | radio → ESP32 | The scope frames |
| 5 | CLOCK | SPI clock | radio → ESP32 | 2 MHz clock for the data |
| 6 | CS | SPI chip select | radio → ESP32 | Low for each 4096-byte frame |
| 11 | CNT RX | Control serial into the radio | ESP32 → radio | Heartbeat reply |
| 12 | CNT TX | Control serial out of the radio | radio → ESP32 | Heartbeat, `alive\0` once a second |

## Pins to leave unconnected

The other six.

| ACC pin | Manual label | Function | Why not |
|---|---|---|---|
| 1 | Power ON | Remote power-on line | Switches the radio on; not needed |
| 7 | 3.3V | 3.3 V supply from the radio | Don't power anything from it |
| 8 | 13.8V | 13.8 V supply from the radio | Internally fused; a short blows the fuse inside the radio |
| 9 | Reserve | Reserved, function unknown | Nothing to connect |
| 10 | Reserve | Reserved, function unknown | Nothing to connect |
| 13 | Reserve | Reserved, function unknown | Nothing to connect |

Cut each of these wires back and insulate it on its own (heat-shrink, not
just tape round the bundle), so none can touch another wire or the ESP32.
Pins 9, 10 and 13 can be checked with the scope during the level checks out
of curiosity, but nothing gets connected to them.

## Wiring to the ESP32-S3

This is the pin mapping SLTPLAN's firmware uses, so their code runs
unchanged.

| ACC pin | Manual label | Function | ESP32-S3 | Role on the ESP32 |
|---|---|---|---|---|
| 1 | Power ON | Remote power-on line | — | Not connected |
| 2 | GND | Signal ground | GND | Ground |
| 3 | DATA1 | SPI data line 1 | GPIO14 (suggested) | Spare input, only once the scope shows what's on it |
| 4 | DATA2 | SPI data line 2 | GPIO11 | SPI slave data in |
| 5 | CLOCK | SPI clock | GPIO10 | SPI slave clock in |
| 6 | CS | SPI chip select | GPIO13 | SPI slave chip select in |
| 7 | 3.3V | 3.3 V supply from the radio | — | Not connected |
| 8 | 13.8V | 13.8 V supply from the radio | — | Not connected |
| 9 | Reserve | Reserved | — | Not connected |
| 10 | Reserve | Reserved | — | Not connected |
| 11 | CNT RX | Control serial into the radio | GPIO39 | UART TX (heartbeat reply out) |
| 12 | CNT TX | Control serial out of the radio | GPIO38 | UART RX (heartbeat in) |
| 13 | Reserve | Reserved | — | Not connected |

Notes:

- Check that GPIO10, 11, 13, 14, 38 and 39 are broken out on the board.
  On boards with octal PSRAM (N8R8, N16R8), GPIO35–37 are taken but the
  pins above are free.
- Put a 330 Ω resistor in series with each line from the radio (pins 3, 4,
  5, 6, 12). It limits the current if the levels turn out higher than
  expected or a wire goes to the wrong pin, and does no harm at 2 MHz.
- Keep the wires from the cable to the ESP32 short; it's a 2 MHz clock.
- Power the ESP32 from its own USB lead, never from the ACC socket.

## Before connecting the ESP32: check the levels

The ESP32-S3 takes 3.3 V at most on any pin. With the cable wired to a
breakout but nothing else attached, and the Siglent scope's ground on pin 2:

1. **Pin 12:** expect a burst once a second (the `alive\0` heartbeat, UART
   at 115200). Note the high level.
2. **Pins 5 and 6:** with no heartbeat reply the radio may not send
   anything. Link pins 11 and 12 with a jumper (the radio accepts its own
   heartbeat echoed back) and look again. Expect CS going low for each
   frame, 25 times a second, and a 2 MHz clock while it's low.
3. **Pins 4 and 3:** data while CS is low. Note whether pin 3 is active,
   and whether it changes when the Sub receiver is on.
4. If any level is above 3.3 V, stop and add level shifting before going
   further.

## Where the code is

| Source | Licence | What it gives us |
|---|---|---|
| [SLTPLAN/FTDX10_WF_HUB/ESP32](https://github.com/SLTPLAN/FTDX10_WF_HUB/tree/main/ESP32) | **None** (asked in [issue #1](https://github.com/SLTPLAN/FTDX10_WF_HUB/issues/1)) | Complete ESP32-S3 firmware `DX10_WF.ino` plus `remote.py` |
| [SLTPLAN/FTDX10_WF](https://github.com/SLTPLAN/FTDX10_WF) | None | The PC side of the same project |
| [ratmandu/YaesuWFTesting](https://github.com/ratmandu/YaesuWFTesting) | MIT | First published frame layout and test tools |
| [CarrierWaveApp/sculan10-protocol](https://github.com/CarrierWaveApp/sculan10-protocol) | MIT | Written spec of the SCU-LAN10 protocol |
| [sanjint.com/yaesu-ftdx10-scope](https://sanjint.com/yaesu-ftdx10-scope) | — | Write-up of the ACC signals on the FTdx10 |
| YWC: `Ft710ScopeFrame` and the FT-710 scope path | Ours | The frame decoder and spectrum display we already have |

What SLTPLAN's firmware does:

- Arduino settings: board "ESP32S3 Dev Module", USB Mode "USB-OTG",
  USB CDC On Boot "Disabled".
- Reads each frame as an SPI slave (mode 1, 2 MHz, 4096 bytes while CS is
  low) and answers `alive\0` within 10 ms.
- Sends each frame wrapped as `66 CC FF`, a 2-byte big-endian length, a
  1-byte XOR checksum, then the payload.
- Outputs on USB CDC, on UDP port 51235 (all 25 frames a second, after the
  client sends `HELLO`) and on TCP port 51234.

## Most of it already exists in YWC

The FT-710 sends the same frames over the same kind of SPI link. The
difference is only where the SPI-to-USB bridge sits:

```
FT-710:    radio SPI ──> FT4222H (inside the radio) ──USB──> YWC
FTdx101:   ACC SPI   ──> ESP32-S3 (outside)         ──USB──> YWC
```

Everything after the bridge is already written and working for the FT-710:
`Ft710ScopeFrame` (finding whole frames in the byte stream, decoding them),
CAT placement by `ScopeCatGate`, and the spectrum display. The ESP32 only
has to stand in for the FT4222H.

The FT4222H is a fixed-function chip with no firmware. The ESP32 needs a
little, but only enough to be a dumb pipe:

- **SPI slave:** read the bytes while CS is low (ESP-IDF's `spi_slave`
  driver with DMA does this).
- **USB:** write the bytes out unchanged over USB CDC. No header, no
  checksum. `Ft710ScopeFrame.Assembler` already finds frame boundaries in
  an unaligned stream by their trailer.
- **Heartbeat:** the FT-710 answers its own heartbeat inside the radio; on
  the FTdx101 something outside has to. Either the ESP32 echoes `alive\0`
  back, or pins 11 and 12 are simply linked by a wire and the ESP32 needn't
  touch them at all.

That's a few dozen lines, so we don't need SLTPLAN's firmware or its
licence. Their code stays useful for a bench comparison in step 3.

What's left to check:

- Whether FTdx101 frames end with the same `FF 01 EE 01` trailer as the
  FT-710's. If they don't, the assembler needs another way to find frame
  boundaries (CS going high is the obvious one, and the ESP32 can mark it).
- The extra content on the FTdx101: the Sub receiver's waterfall at bytes
  850–1699, and whatever differs in the settings block.
- On the YWC side, the source is a USB serial port instead of the FT4222H.
  That's a new input class in `Yaesu_Sdr_Worker` beside `Ft4222Bridge`, not
  a new decoder.

Rules for our own firmware:

- Write it from the MIT sources (ratmandu, sculan10-protocol) and our own
  scope captures, not from SLTPLAN's code. Facts about the protocol
  (timings, frame layout, the heartbeat) aren't anyone's property; their
  code is.
- Credit SLTPLAN for showing that an ESP32-S3 can do it.

## Steps

1. **Cable.** Check the male plug fits the ACC socket before cutting. Cut
   near the female end. Use the female end as a test socket and make a
   table of wire colour to pin number with a meter. Cheap cables sometimes
   leave pins unconnected, so check all 13.
2. **Levels.** Scope checks as above, nothing connected to the ESP32.
3. **Bench run.** Flash SLTPLAN's firmware unchanged onto the ESP32-S3,
   wire it as in the table, and read frames with `remote.py` over USB.
   Confirm 25 frames a second and a waterfall that follows the radio.
4. **Decode the FTdx101 frame.** Bytes 0–849 are the Main waterfall
   (bit-inverted, they're in wfview's format), 850–1699 the Sub
   waterfall. Map the settings block (about 144 bytes at the end) by
   changing one control at a time on the radio and diffing captures.
   Work out what pin 3 carries.
5. **Our own firmware.** The dumb pipe described above: SPI in, raw bytes
   out over USB CDC.
6. **YWC input.** A serial-port source in `Yaesu_Sdr_Worker` beside
   `Ft4222Bridge`, feeding the existing `Ft710ScopeFrame` path. Centre
   frequency and span come from CAT, as they do for the FT-710. Then the
   FTdx101 additions: Main and Sub waterfalls, each placed on its own VFO.

## Open questions

- Logic levels on the ACC pins: to be measured.
- Does pin 3 carry the Sub receiver, or something else?
- Does the radio need a menu setting before it streams to the ACC socket,
  as the FT-710 does (EX 03-01-26 SCU-LAN10)?
- Will the radio stream to the ESP32 and an SCU-LAN10 at the same time? Not
  needed now, but it decides whether the female half of the cable is worth
  keeping as a pass-through.
