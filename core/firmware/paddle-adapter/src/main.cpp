// Paddle-to-MIDI emulator, for testing YWC's paddle keying without a paddle,
// and a paddle-to-MIDI adapter for when there is one.
//
// The board's native USB port (marked USB) is a MIDI device that sends what a
// HaliKey MIDI sends: note 20 for the dit paddle and note 21 for dah, note on
// when the paddle is pressed and note off when it is let go. Only the paddles
// are emulated; the keyer that turns them into Morse is YWC's.
//
// Commands, and a timestamped log of every MIDI message, are on COM5 (the port
// marked COM, the CH343). Type "help" there.
//
// A real paddle: dit contact to GPIO4, dah contact to GPIO5, common to GND.
// The pins use the internal pull-ups, so a pin with nothing on it reads as let
// go. Presses from the paddle and from the commands are combined: a paddle is
// down while either says so.
//
// Board: SP-Cow ESP32-S3 N16R8 (marked HW678), RGB LED on GPIO48. The LED is
// dim blue when idle, cyan for dit, magenta for dah, white for both, and dim
// yellow while the USB host isn't ready.
//
// USER_BUILD (platformio env "user") is the firmware users install: the paddle
// adapter alone. The emulator, its schedule of presses, pulltest and ledscan
// are left out, and only the settings commands remain on the COM port.
//
// SysEx on the MIDI port, for YWC/IWC's Install / Update box (see sysex()):
// the firmware version, and a restart into the chip's own download mode, so
// a board with only its native USB port can be updated without BOOT held.

// Raise with every firmware change that goes into YWC/IWC: the page offers an
// update when the board's version is older than the file the app carries.
#define FW_VERSION "1.0"
#ifndef USER_BUILD
#define USER_BUILD 0
#endif
#if USER_BUILD
#define DEVICE_NAME "YWC Paddle Adapter"
#else
#define DEVICE_NAME "YWC Paddle Emulator"
#endif
#ifndef LED_PIN
#define LED_PIN 48
#endif
#include <Arduino.h>
#include "USB.h"
#include "esp32-hal-tinyusb.h"
#include "soc/rtc_cntl_reg.h"
#include <Adafruit_NeoPixel.h>

enum Paddle : uint8_t { DIT = 0, DAH = 1 };
static const char *kPaddleName[] = {"dit", "dah"};

// Settings, changed with commands on COM5.
static uint8_t noteFor[2] = {20, 21};
static uint8_t channel = 1;       // 1-16
#if !USER_BUILD
static uint16_t wpm = 20;
static bool holdMode = false;     // false: tap each paddle for half a dit; true: hold it for the element
static uint16_t jitterMs = 0;     // +/- random shift on each press and release

static const char *kPhrases[] = {
    "CQ CQ DE MM5AGM MM5AGM K",
    "PARIS PARIS PARIS",
    "599 TU",
    "TEST",
    "EEEEE TTTTT",
};
static const size_t kPhraseCount = sizeof kPhrases / sizeof kPhrases[0];
#endif

// --- USB MIDI ------------------------------------------------------------
// The 2.0.x Arduino core has TinyUSB's MIDI class compiled in but no Arduino
// wrapper for it, so the interface is registered here as the 3.x core's
// USBMIDI does.

static uint16_t midiDescriptor(uint8_t *dst, uint8_t *itf) {
    uint8_t str = tinyusb_add_string_descriptor(DEVICE_NAME);
    uint8_t ep = tinyusb_get_free_duplex_endpoint();
    if (ep == 0) return 0;
    uint8_t d[TUD_MIDI_DESC_LEN] = {TUD_MIDI_DESCRIPTOR(*itf, str, ep, (uint8_t)(0x80 | ep), 64)};
    *itf += 2;
    memcpy(dst, d, sizeof d);
    return sizeof d;
}

static bool down[2] = {false, false};      // what was last sent over MIDI
static bool emuDown[2] = {false, false};   // pressed by the commands
static bool padDown[2] = {false, false};   // pressed on the real paddle

// The RGB LED is on GPIO48, as the DevKitC-1 v1.0 has it, in GRB order
// (checked 2026-10-31). Kept dim: at full power it is dazzling. Boards with
// it elsewhere (the DevKitC-1 v1.1 has GPIO38) can build with -DLED_PIN=38.
static Adafruit_NeoPixel statusLed(1, LED_PIN, NEO_GRB + NEO_KHZ800);

static void showLed() {
    uint32_t c;
    if (down[DIT] && down[DAH]) c = statusLed.Color(20, 20, 20);    // squeeze: white
    else if (down[DIT]) c = statusLed.Color(0, 20, 20);             // dit: cyan
    else if (down[DAH]) c = statusLed.Color(20, 0, 20);             // dah: magenta
    else if (tud_ready()) c = statusLed.Color(0, 0, 4);             // idle, USB ready: dim blue
    else c = statusLed.Color(6, 4, 0);                              // USB not ready: dim yellow
    statusLed.setPixelColor(0, c);
    statusLed.show();
}

// What TinyUSB thinks of the link, for the log: "configured" means the host
// has set the device up; Windows can still suspend it when it looks idle.
static const char *usbState() {
    if (!tud_connected()) return "no USB host";
    if (!tud_mounted()) return "USB not configured";
    if (tud_suspended()) return "USB suspended";
    return "USB ready";
}

// Sends the paddle's combined state if it changed. `why` goes in the log.
static void update(Paddle p, const char *why) {
    bool on = emuDown[p] || padDown[p];
    if (down[p] == on) return;
    down[p] = on;
    uint8_t msg[3] = {(uint8_t)((on ? 0x90 : 0x80) | ((channel - 1) & 0x0F)), noteFor[p], (uint8_t)(on ? 127 : 0)};
    if (tud_suspended()) tud_remote_wakeup();
    bool sent = tud_midi_stream_write(0, msg, 3) == 3;
    Serial.printf("%9lu ms  %s %s  %02X %02X %02X%s%s%s%s\n", (unsigned long)millis(), kPaddleName[p],
                  on ? "ON " : "OFF", msg[0], msg[1], msg[2], why,
                  sent ? "" : "  (not sent: ", sent ? "" : usbState(), sent ? "" : ")");
    showLed();   // after the MIDI message, so the LED never delays it
}

static void press(Paddle p, bool on) {
    emuDown[p] = on;
    update(p, "");
}

// --- The real paddle --------------------------------------------------------
// A press is sent the moment the contact closes; for debounceMs after any
// change the pin is not looked at, which rides out contact bounce without
// delaying anything. 5 ms was too short: a wire touched by hand chattered for
// about 20 ms (2026-10-31), so the default is 15, still far quicker than any
// operator re-presses a paddle. A pin that reads pressed at power-up (a short,
// or a paddle held) is ignored until it has been seen let go once, so it
// cannot send a key that never lifts.
//
// A press counts only once the pin has read closed for settleMs without a
// break. A real paddle stays closed for tens of ms; a spike picked up on
// loose wires (a static spark, a mains switch) is gone well inside 3 ms, and
// one such spike sent a dit with nothing touched (2026-10-31). Let-go is
// still sent at once.

static const uint8_t kPadPin[2] = {4, 5};
static uint32_t debounceMs = 15;
static bool padEnabled = true;
static bool padArmed[2] = {false, false};
static uint32_t padChangedAt[2] = {0, 0};
static uint32_t settleMs = 3;
static uint32_t padClosedSince[2] = {0, 0};   // micros() when the pin first read closed; 0: open

static void padSetup() {
    for (int p = 0; p < 2; p++) pinMode(kPadPin[p], INPUT_PULLUP);
    delay(2);
    for (int p = 0; p < 2; p++) {
        padArmed[p] = digitalRead(kPadPin[p]) == HIGH;
        if (!padArmed[p])
            Serial.printf("GPIO%u (%s) reads pressed at start: ignored until it is let go\n", kPadPin[p], kPaddleName[p]);
    }
}

static void padPoll() {
    uint32_t now = millis();
    for (int i = 0; i < 2; i++) {
        Paddle p = (Paddle)i;
        bool pressed = digitalRead(kPadPin[p]) == LOW;
        if (!padArmed[p]) {
            if (!pressed) {
                padArmed[p] = true;
                padChangedAt[p] = now;
                Serial.printf("GPIO%u (%s) let go: paddle input armed\n", kPadPin[p], kPaddleName[p]);
            }
            continue;
        }
        if (!padEnabled) pressed = false;
        if (!pressed) padClosedSince[p] = 0;
        else if (!padDown[p]) {
            uint32_t us = micros() | 1;
            if (!padClosedSince[p]) padClosedSince[p] = us;
            if (us - padClosedSince[p] < settleMs * 1000) continue;
        }
        if (pressed == padDown[p] || now - padChangedAt[p] < debounceMs) continue;
        padDown[p] = pressed;
        padChangedAt[p] = now;
        update(p, "  (paddle)");
    }
}

#if !USER_BUILD
// Makes a pin look pressed with nothing wired to it: the pull-up is swapped
// for a pull-down for `ms`, so the pin reads low as a closed contact would.
// Exercises the real input path, debounce and all.
static uint32_t pullTestEnd[2] = {0, 0};

static void pullTest(Paddle p, uint32_t ms) {
    pinMode(kPadPin[p], INPUT_PULLDOWN);
    pullTestEnd[p] = millis() + ms;
    if (!pullTestEnd[p]) pullTestEnd[p] = 1;
}

static void pullTestPoll() {
    for (int p = 0; p < 2; p++) {
        if (pullTestEnd[p] && (int32_t)(millis() - pullTestEnd[p]) >= 0) {
            pinMode(kPadPin[p], INPUT_PULLUP);
            pullTestEnd[p] = 0;
        }
    }
}
#endif

static void pins() {
    for (int p = 0; p < 2; p++)
        Serial.printf("  GPIO%u (%s): %s%s%s\n", kPadPin[p], kPaddleName[p],
                      digitalRead(kPadPin[p]) == LOW ? "closed (low)" : "open (high)",
                      padArmed[p] ? "" : ", not armed", padDown[p] ? ", sent as pressed" : "");
    Serial.printf("  paddle input %s, settle %lu ms, debounce %lu ms\n", padEnabled ? "on" : "off",
                  (unsigned long)settleMs, (unsigned long)debounceMs);
}

#if !USER_BUILD
// --- The schedule of paddle presses ---------------------------------------

struct Event { uint32_t at; Paddle p; bool on; };
static Event queue[1024];
static size_t queueLen = 0, queuePos = 0;

static void clearQueue() { queueLen = queuePos = 0; }

static void releaseAll() {
    clearQueue();
    press(DIT, false);
    press(DAH, false);
}

static int32_t jitter() { return jitterMs ? (int32_t)random(-(long)jitterMs, (long)jitterMs + 1) : 0; }

static void add(uint32_t at, Paddle p, bool on) {
    if (queueLen < sizeof queue / sizeof queue[0]) queue[queueLen++] = {at, p, on};
}

// Keeps the queue in time order after jitter has moved events about.
static void sortQueue() {
    for (size_t i = 1; i < queueLen; i++) {
        Event e = queue[i];
        size_t j = i;
        while (j > 0 && queue[j - 1].at > e.at) { queue[j] = queue[j - 1]; j--; }
        queue[j] = e;
    }
}

static const char *morse(char c) {
    switch (toupper(c)) {
        case 'A': return ".-";    case 'B': return "-...";  case 'C': return "-.-.";  case 'D': return "-..";
        case 'E': return ".";     case 'F': return "..-.";  case 'G': return "--.";   case 'H': return "....";
        case 'I': return "..";    case 'J': return ".---";  case 'K': return "-.-";   case 'L': return ".-..";
        case 'M': return "--";    case 'N': return "-.";    case 'O': return "---";   case 'P': return ".--.";
        case 'Q': return "--.-";  case 'R': return ".-.";   case 'S': return "...";   case 'T': return "-";
        case 'U': return "..-";   case 'V': return "...-";  case 'W': return ".--";   case 'X': return "-..-";
        case 'Y': return "-.--";  case 'Z': return "--..";
        case '0': return "-----"; case '1': return ".----"; case '2': return "..---"; case '3': return "...--";
        case '4': return "....-"; case '5': return ".....";  case '6': return "-...."; case '7': return "--...";
        case '8': return "---.."; case '9': return "----.";
        case '/': return "-..-."; case '?': return "..--.."; case '.': return ".-.-.-"; case ',': return "--..--";
        case '=': return "-...-"; case '+': return ".-.-.";
        default: return nullptr;
    }
}

// Text as paddle presses, the way an operator on an iambic keyer at the same
// speed would press them. Tap mode presses at the start of each element and
// lets go half a dit later, so the keyer makes exactly one element; hold mode
// holds the paddle for the whole element, for a keyer set to pass it through.
static void schedule(const char *text) {
    releaseAll();
    uint32_t u = 1200 / wpm, t = millis() + 50;
    bool first = true;
    for (const char *s = text; *s; s++) {
        if (*s == ' ') { t += 4 * u; continue; }     // 3u after the character + 4u = 7u between words
        const char *m = morse(*s);
        if (!m) { Serial.printf("No Morse for '%c', left out\n", *s); continue; }
        if (!first && t < millis()) t = millis();
        for (const char *e = m; *e; e++) {
            Paddle p = *e == '.' ? DIT : DAH;
            uint32_t len = p == DIT ? u : 3 * u;
            uint32_t held = holdMode ? len : u / 2;
            // Jitter must never put the release before its press: the paddle
            // would then stay down.
            uint32_t on = t + jitter();
            int32_t off = (int32_t)(t + held) + jitter();
            add(on, p, true);
            add(max((int32_t)on + 5, off), p, false);
            t += len + u;
        }
        t += 2 * u;                                   // 1u after the element + 2u = 3u between characters
        first = false;
    }
    sortQueue();
    Serial.printf("Sending \"%s\" at %u WPM, %s, %u events, about %lu ms\n", text, wpm,
                  holdMode ? "hold" : "tap", (unsigned)queueLen, (unsigned long)(t - millis()));
}

static void holdFor(bool dit, bool dah, uint32_t ms) {
    releaseAll();
    uint32_t t = millis();
    if (dit) { add(t, DIT, true); add(t + ms, DIT, false); }
    if (dah) { add(t, DAH, true); add(t + ms, DAH, false); }
    sortQueue();
}

static void runQueue() {
    uint32_t now = millis();
    while (queuePos < queueLen && (int32_t)(now - queue[queuePos].at) >= 0) {
        press(queue[queuePos].p, queue[queuePos].on);
        queuePos++;
    }
    if (queuePos >= queueLen) clearQueue();
}
#endif

// --- Commands on COM5 -------------------------------------------------------

#if USER_BUILD
static void help() {
    Serial.println(
        "Commands:\n"
        "  status           show the settings\n"
        "  pins             show the paddle pins (GPIO4 dit, GPIO5 dah, contacts to GND)\n"
        "  paddle on|off    use the paddle or ignore it (default on)\n"
        "  settle <ms>      a contact counts as pressed after reading closed this long, 0-10 (default 3)\n"
        "  debounce <ms>    ignore contact bounce for ms after each change, 1-50 (default 15)\n"
        "  note <dit> <dah> MIDI note numbers (default 20 21)\n"
        "  channel <n>      MIDI channel 1-16 (default 1)\n"
        "Settings are not kept when the board is unplugged.");
}

static void status() {
    Serial.printf("%s; notes %u/%u; channel %u; paddle input %s, debounce %lu ms\n",
                  usbState(), noteFor[DIT], noteFor[DAH], channel, padEnabled ? "on" : "off", (unsigned long)debounceMs);
}
#else
static void help() {
    Serial.println(
        "Commands:\n"
        "  send <text>      send text as paddle presses\n"
        "  phrase <n>       send stored phrase n (list: phrases)\n"
        "  stop             let go of both paddles and drop what is queued\n"
        "  wpm <n>          speed the presses are timed for (match YWC's keyer)\n"
        "  mode tap|hold    tap: half-dit presses for an iambic keyer; hold: whole elements\n"
        "  jitter <ms>      random +/- shift on each press and release (0 = off)\n"
        "  dit <ms>         hold the dit paddle for ms\n"
        "  dah <ms>         hold the dah paddle for ms\n"
        "  squeeze <ms>     hold both paddles for ms\n"
        "  stuck [ms]       hold dah for ms (default 5000): YWC should let go after 3 s\n"
        "  note <dit> <dah> MIDI note numbers (default 20 21)\n"
        "  channel <n>      MIDI channel 1-16 (default 1)\n"
        "  status           show the settings\n"
        "  pins             show the paddle pins (GPIO4 dit, GPIO5 dah, contacts to GND)\n"
        "  paddle on|off    use the real paddle or ignore it (default on)\n"
        "  settle <ms>      a contact counts as pressed after reading closed this long, 0-10 (default 3)\n"
        "  debounce <ms>    ignore contact bounce for ms after each change, 1-50 (default 15)\n"
        "  pulltest <dit|dah> [ms]  make a paddle pin read pressed for ms (default 200)\n"
        "                   by swapping its pull-up for a pull-down: tests with no paddle\n"
        "  ledscan          light each safe pin white in turn, to find the RGB LED\n"
        "  ledscan <pin>    red, green, blue on that pin, to check the colour order");
}

static void status() {
    Serial.printf("%s; %u WPM; %s; jitter %u ms; notes %u/%u; channel %u; paddle input %s, debounce %lu ms\n",
                  usbState(), wpm, holdMode ? "hold" : "tap", jitterMs,
                  noteFor[DIT], noteFor[DAH], channel, padEnabled ? "on" : "off", (unsigned long)debounceMs);
}

// --- Finding the RGB LED ----------------------------------------------------
// Kept for other boards; this one's LED turned out to be on GPIO48 after all.
// The scan skips the paddle pins, 4 and 5, so it never fights a paddle.
// The first tries failed because the core's neopixelWrite() only ever drives
// the first pin it was called with. Only pins that are safe to drive are
// tried: not 0, 3, 45, 46 (strapping), 19, 20 (USB), 26-37 (flash and the
// N16R8's octal PSRAM) or 43, 44 (UART0, the COM5 log).
static const uint8_t kScanPins[] = {1, 2, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
                                    21, 38, 39, 40, 41, 42, 47, 48};

static void ledShow(uint8_t pin, uint8_t r, uint8_t g, uint8_t b) {
    Adafruit_NeoPixel led(1, pin, NEO_GRB + NEO_KHZ800);
    led.begin();
    led.setPixelColor(0, led.Color(r, g, b));
    led.show();
}

static void ledScan(long pin) {
    if (pin < 0) {
        Serial.println("LED scan: each pin white for 2 s, then off. Note the pin when the LED lights.");
        for (uint8_t p : kScanPins) {
            Serial.printf("  GPIO%u\n", p);
            ledShow(p, 40, 40, 40);
            delay(2000);
            ledShow(p, 0, 0, 0);
            pinMode(p, INPUT);
            delay(300);
        }
        Serial.println("LED scan done");
        showLed();
        return;
    }
    bool ok = false;
    for (uint8_t p : kScanPins) ok |= p == pin;
    if (!ok) { Serial.println("Not a pin the scan may drive"); return; }
    static const struct { const char *name; uint8_t r, g, b; } kColours[] = {
        {"RED", 40, 0, 0}, {"GREEN", 0, 40, 0}, {"BLUE", 0, 0, 40}};
    for (auto &c : kColours) {
        Serial.printf("  GPIO%ld %s (sent as GRB)\n", pin, c.name);
        ledShow(pin, c.r, c.g, c.b);
        delay(2000);
        ledShow(pin, 0, 0, 0);
        delay(1000);
    }
    Serial.println("Colour check done");
    showLed();
}
#endif

static void command(String line) {
    line.trim();
#if USER_BUILD
    if (!line.length()) { help(); return; }   // Enter on its own: the list of commands
#else
    if (!line.length()) return;
#endif
    int sp = line.indexOf(' ');
    String cmd = sp < 0 ? line : line.substring(0, sp);
    String arg = sp < 0 ? String() : line.substring(sp + 1);
    arg.trim();
    cmd.toLowerCase();
    long n = arg.toInt();

    if (cmd == "help" || cmd == "?") help();
    else if (cmd == "status") status();
    else if (cmd == "pins") pins();
    else if (cmd == "paddle" && (arg == "on" || arg == "off")) { padEnabled = arg == "on"; status(); }
    else if (cmd == "debounce" && n >= 1 && n <= 50) { debounceMs = n; status(); }
    else if (cmd == "settle" && arg.length() && n >= 0 && n <= 10) { settleMs = n; pins(); }
    else if (cmd == "note") {
        int sp2 = arg.indexOf(' ');
        long a = arg.toInt(), b = sp2 < 0 ? -1 : arg.substring(sp2 + 1).toInt();
        if (a >= 0 && a <= 127 && b >= 0 && b <= 127) { noteFor[DIT] = a; noteFor[DAH] = b; status(); }
        else Serial.println("note <dit> <dah>, each 0-127");
    }
    else if (cmd == "channel" && n >= 1 && n <= 16) { channel = n; status(); }
#if !USER_BUILD
    else if (cmd == "ledscan") ledScan(arg.length() ? n : -1);
    else if (cmd == "pulltest" && arg.length()) {
        int sp2 = arg.indexOf(' ');
        String which = sp2 < 0 ? arg : arg.substring(0, sp2);
        long ms = sp2 < 0 ? 200 : arg.substring(sp2 + 1).toInt();
        if ((which == "dit" || which == "dah") && ms > 0 && ms <= 10000) pullTest(which == "dit" ? DIT : DAH, ms);
        else Serial.println("pulltest <dit|dah> [ms], ms 1-10000");
    }
    else if (cmd == "send" && arg.length()) schedule(arg.c_str());
    else if (cmd == "phrases") for (size_t i = 0; i < kPhraseCount; i++) Serial.printf("  %u: %s\n", (unsigned)(i + 1), kPhrases[i]);
    else if (cmd == "phrase" && n >= 1 && (size_t)n <= kPhraseCount) schedule(kPhrases[n - 1]);
    else if (cmd == "stop") { releaseAll(); Serial.println("Stopped"); }
    else if (cmd == "wpm" && n >= 5 && n <= 60) { wpm = n; status(); }
    else if (cmd == "mode" && (arg == "tap" || arg == "hold")) { holdMode = arg == "hold"; status(); }
    else if (cmd == "jitter" && n >= 0 && n <= 200) { jitterMs = n; status(); }
    else if (cmd == "dit" && n > 0) holdFor(true, false, n);
    else if (cmd == "dah" && n > 0) holdFor(false, true, n);
    else if (cmd == "squeeze" && n > 0) holdFor(true, true, n);
    else if (cmd == "stuck") holdFor(false, true, n > 0 ? n : 5000);
#endif
    else Serial.printf("Didn't understand \"%s\". Type help.\n", line.c_str());
}

// --- SysEx from YWC/IWC -----------------------------------------------------
// F0 7D 59 50 <cmd> F7. 7D is MIDI's ID for non-commercial use, and 59 50 is
// "YP" (YWC Paddle), so no other device acts on these and these act on no other.
//   01  version: answered with F0 7D 59 50 01 <FW_VERSION and build, ASCII> F7
//   02  restart in download mode: answered with F0 7D 59 50 02 F7, then the
//       board lets go of both paddles and restarts as the chip's own USB
//       serial port, ready to be programmed. Programming ends with a reset,
//       which starts the new firmware.

static const uint8_t kSysexHead[] = {0xF0, 0x7D, 0x59, 0x50};

static void sysexReply(uint8_t cmd, const char *text) {
    uint8_t msg[48];
    size_t n = 0;
    for (uint8_t b : kSysexHead) msg[n++] = b;
    msg[n++] = cmd;
    for (const char *s = text; *s && n < sizeof msg - 1; s++) msg[n++] = *s & 0x7F;
    msg[n++] = 0xF7;
    tud_midi_stream_write(0, msg, n);
}

// Not the core's usb_persist_restart(RESTART_BOOTLOADER): it turns the USB
// peripheral off while TinyUSB's task still uses it, and the board hangs
// instead of restarting. Here USB is let go first, and the pins are handed to
// the chip's own serial/JTAG port, which download mode uses; that setting
// survives the restart.
static void restartInDownloadMode() {
    tud_disconnect();
    delay(50);
    CLEAR_PERI_REG_MASK(RTC_CNTL_USB_CONF_REG,
        RTC_CNTL_SW_HW_USB_PHY_SEL | RTC_CNTL_SW_USB_PHY_SEL | RTC_CNTL_USB_PAD_ENABLE);
    REG_WRITE(RTC_CNTL_OPTION1_REG, RTC_CNTL_FORCE_DOWNLOAD_BOOT);
    esp_restart();
}

static void sysex(const uint8_t *m, size_t n) {
    if (n != sizeof kSysexHead + 2 || memcmp(m, kSysexHead, sizeof kSysexHead) || m[n - 1] != 0xF7) return;
    uint8_t cmd = m[sizeof kSysexHead];
    if (cmd == 0x01) {
        sysexReply(0x01, USER_BUILD ?FW_VERSION " user" : FW_VERSION " emulator");
        Serial.printf("%9lu ms  version asked: %s\n", (unsigned long)millis(), FW_VERSION);
    } else if (cmd == 0x02) {
        sysexReply(0x02, "");
        Serial.printf("%9lu ms  restarting in download mode, to be programmed\n", (unsigned long)millis());
        emuDown[DIT] = emuDown[DAH] = padDown[DIT] = padDown[DAH] = false;
        update(DIT, "  (restart)");
        update(DAH, "  (restart)");
        delay(100);   // for the replies to reach the host before USB goes
        Serial.flush();
        restartInDownloadMode();
    }
}

// Collects SysEx bytes from the MIDI stream; everything else is dropped.
static void readMidi() {
    static uint8_t buf[16];
    static size_t len = 0;
    static bool in = false;
    uint8_t chunk[32];
    while (tud_midi_available()) {
        uint32_t got = tud_midi_stream_read(chunk, sizeof chunk);
        if (!got) break;
        for (uint32_t i = 0; i < got; i++) {
            uint8_t b = chunk[i];
            if (b == 0xF0) { in = true; len = 0; }
            else if (!in) continue;
            if (len < sizeof buf) buf[len++] = b;
            else in = false;                        // too long: not ours
            if (b == 0xF7) { if (in) sysex(buf, len); in = false; }
        }
    }
}

// --- Arduino ----------------------------------------------------------------

void setup() {
    Serial.begin(115200);
    tinyusb_enable_interface(USB_INTERFACE_MIDI, TUD_MIDI_DESC_LEN, midiDescriptor);
    USB.manufacturerName("MM5AGM");
    USB.productName(DEVICE_NAME);
    USB.begin();
#if !USER_BUILD
    randomSeed(esp_random());
#endif
    delay(300);
    Serial.println();
    Serial.println(DEVICE_NAME " " FW_VERSION ": paddle presses as MIDI notes on the USB port. Type help.");
#if USER_BUILD
    help();
#endif
    statusLed.begin();
    showLed();
    padSetup();
}

void loop() {
    static String line;
    static char lastChar = 0;
    static bool typed = false;
    while (Serial.available()) {
        char c = Serial.read();
        typed = true;
        // CR LF is one Enter, not two: the second would be an empty line,
        // which the user build answers with the list of commands.
        if (c == '\n' && lastChar == '\r') { lastChar = c; continue; }
        lastChar = c;
        if (c == '\r' || c == '\n') { command(line); line = ""; }
        else if (line.length() < 200) line += c;
    }
#if USER_BUILD
    // The board cannot tell when a terminal opens the COM port (DTR and RTS
    // only reach EN and GPIO0), so until something is typed it says how to
    // get the commands every few seconds: whoever opens the port sees it soon.
    static uint32_t hintAt = 5000;
    if (!typed && (int32_t)(millis() - hintAt) >= 0) {
        Serial.println("Press Enter for the list of commands.");
        hintAt = millis() + 5000;
    }
#endif
    // SysEx from YWC/IWC is acted on; anything else the host sends is read
    // and dropped so the endpoint never fills up.
    readMidi();

#if !USER_BUILD
    runQueue();
    pullTestPoll();
#endif
    padPoll();

    static const char *wasState = nullptr;
    const char *state = usbState();
    if (state != wasState) {
        wasState = state;
        Serial.printf("%9lu ms  %s\n", (unsigned long)millis(), state);
        showLed();
    }
}
