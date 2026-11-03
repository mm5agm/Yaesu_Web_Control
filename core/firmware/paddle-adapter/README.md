# Paddle adapter firmware

Firmware that turns an ESP32-S3 board into a USB MIDI paddle adapter for the
CW Paddle page of Yaesu Web Control (YWC) and Icom Web Control (IWC). It sends
what a HaliKey MIDI sends: note 20 for the dit paddle and note 21 for dah, note
on when a paddle is pressed and note off when it is let go. The keyer that
turns those into Morse is in the apps, not here.

Users never build this. Both apps carry the built file (`dist/`) and program
the board from the Install / Update box on the CW Paddle page
(`core/js/cw/paddle-firmware.js`), in Chrome or Edge.

It used to be the private repository mm5agm/YWC_IWC_PADDLE, which keeps the
history up to firmware 1.0 (moved here 2026-11-03).

## Boards

An ESP32-S3 with its native USB port brought out to a socket, and 4 MB of
flash or more. (An ESP32-S2 could be an adapter too, but would need a build of
its own; the file in `dist/` is for the S3 only.) The native USB port is the one marked
USB, not UART or COM; boards with only one USB-C socket usually have just the
native one. The adapter has to be a USB MIDI device, and only the native USB
port can be one: the original ESP32 (ESP-WROOM-32 boards such as ELEGOO's),
the C3 and boards whose only socket goes through a USB-serial chip cannot.

Developed on an ESP32-S3 DevKitC-1 N16R8, which has both ports:

- **USB** (native USB): the MIDI device, and the port the apps program.
- **COM** (CH343 serial): settings commands, a log of every MIDI message.

## Wiring

| Paddle      | Board pin |
|-------------|-----------|
| Dit contact | GPIO4     |
| Dah contact | GPIO5     |
| Common      | GND       |

The pins use the internal pull-ups. Keep the wires short, or add 4.7k-10k
resistors from each pin to 3.3 V, so nothing is picked up when no paddle is
pressed. `dist/expansion-board.jpg` shows a board in a screw-terminal
expansion board, which needs no soldering.

## Updating from the apps: SysEx

The firmware answers two SysEx messages on its MIDI port (`sysex()` in
`src/main.cpp`):

| Sent by the app  | Meaning | Answer |
|------------------|---------|--------|
| `F0 7D 59 50 01 F7` | version? | `F0 7D 59 50 01 <"1.0 user", ASCII> F7` |
| `F0 7D 59 50 02 F7` | restart in download mode | `F0 7D 59 50 02 F7`, then the restart |

`7D` is MIDI's ID for non-commercial use and `59 50` is "YP", so no other
device acts on these. After the restart the board is the chip's own USB
serial port (Espressif, USB ID 303A:1001) and the app programs it with
esptool-js; programming ends with a reset, which starts the new firmware.
Firmware before 1.0 does not answer, and a board that will not answer at all
is put in download mode by holding BOOT while plugging in the USB cable.

## Building

There are two builds:

- `esp32s3`: the adapter plus the emulator and test commands (`send`,
  `pulltest`, `ledscan` ...), for developing YWC/IWC. USB name "YWC Paddle
  Emulator".
- `user`: the adapter alone, for users. USB name "YWC Paddle Adapter". Only
  the settings commands are left (`status`, `pins`, `paddle`, `settle`,
  `debounce`, `note`, `channel`). Built for 4 MB flash and DIO, so one file
  runs on any ESP32-S3 board with 4 MB or more.

**The upload port is pinned to COM5 in platformio.ini. Change it to your
board's COM port, and never let PlatformIO search for one:** programming
toggles DTR and RTS, and on a radio's Standard COM port that can key the
transmitter.

To release a firmware change to the apps:

1. Raise `FW_VERSION` in `src/main.cpp`.
2. Run `./make-dist.ps1`. It builds `user`, merges it into
   `dist/ywc-paddle-adapter.bin` and sets the version in `dist/firmware.json`.
   It never uploads.
3. Run the core JS tests (`node --test tests/js/*.test.mjs` in core):
   `paddle-firmware.test.mjs` fails if `dist/` does not match the source.
4. Commit `dist/` with the source, and push core as usual.

Each app copies `dist/` to `wwwroot/firmware/paddle-adapter/` when it builds.

## Licence

GPL-3.0, as the rest of core.
