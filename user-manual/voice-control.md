## 17. Voice Control

> **Windows host only.** Voice Control uses Windows SAPI 5 (`System.Speech`) and is not available on the macOS/Linux CAT-only host or in Docker. Voice *announcements* (§5.16) still work on every platform — they use the browser's text-to-speech.

> **Not the same as Voice Announcements (§5.16).** Voice *control* (this section) is **you speaking to the app** — a press-and-hold mic button that lets you issue spoken commands. Voice *announcements* (§5.16) is **the app speaking to you** — automatic spoken cues for band, mode, TX state, DX alerts, etc., useful as an accessibility feature. They are independent features with separate on/off switches. If you're looking for the toggle to silence YWC's automatic speech, you want §5.16.

YWC includes hands-free voice control of common operating actions. **Each VFO has its own mic button**, on the main Index page next to that VFO's band/mode controls — press and hold VFO A's button to control VFO A, VFO B's button to control VFO B. On single-receiver radios (FTdx10, FT-710, FTDX3000) only VFO A's button appears, since there's no independent VFO B to target. Recognition happens entirely on your PC via Windows' built-in speech engine — your audio never leaves your computer. See [§15.8](faq.md#158-why-was-alexa-voice-control-dropped-in-favour-of-the-built-in-microphone-method) for the reasoning behind this approach and the Alexa method that was considered and dropped.

Voice control is **off by default** — it has to be turned on in Settings before the mic buttons appear (see [§17.2](#172-enabling-voice-control)).

### 17.1 What you can say

Every command below targets whichever VFO's mic button you're holding down — a command spoken into VFO B's button only ever touches VFO B, and vice versa. The phrases are the built-in English (UK) defaults; they're editable (see [§17.6](#176-adding-your-own-commands)), so if your installation has custom phrases, "Settings → Voice Control → Voice Phrases" is the definitive list, not this table.

| Command family | Say | What happens |
| --- | --- | --- |
| Set frequency | "tune to fourteen point zero seven four megahertz", "set frequency to fourteen megahertz" | Held VFO tunes to that frequency. Whole MHz, one decimal, or three decimals; "megahertz" is optional |
| Change band | "forty metres", "go to twenty metres", "switch to eighty metres"; or the digit form "two zero metres", "eight zero metres" | Held VFO jumps to that band's default (usually FT8) frequency. The lead-in word ("go to" / "switch to") is optional. Bands: 160, 80, 60, 40, 30, 20, 17, 15, 12, 10, 6 and 4 metres. "top band" also works for 160 m |
| Step up / down | "tune up" / "step up" / "nudge up"; "tune down" / "step down" / "nudge down" | Held VFO moves by that VFO's configured step size (see below; default 10 kHz) |
| Set step size | "set step to ten kilohertz", "step size one kilohertz", or just "ten kilohertz" | Changes the held VFO's step size: 10 Hz, 100 Hz, 1 kHz, 10 kHz, or 100 kHz. The lead-in word is optional here too. Same value as the dropdown next to that VFO's mic button — either one updates the other |
| Band up / down | "band up" / "band down" | Held VFO jumps to the next/previous ham band |
| Set mode | "mode U S B", "set mode L S B" (also C W, A M, F M, data, data l, r t t y — spell mode letters out one at a time) | Held VFO switches to that mode |
| Swap VFOs | "swap V F O", "swap A and B" | VFO A and B contents swap (radio-wide, not VFO-specific) |
| Set attenuator | "set attenuator off", "attenuator six d b" (also twelve, eighteen dB) | Held VFO's attenuator changes |
| Set preamp | "set preamp off" / "i p o", "preamp one" (also two) | Held VFO's preamp changes |
| Set AGC | "set a g c fast" (also mid, slow, auto, off) | Held VFO's AGC speed changes |
| Set AF gain | "set a f gain fifty" (0–100 in the steps listed in the phrase editor, or "mute"/"maximum") | Held VFO's volume changes |
| IF filter width | "filter wider" / "filter narrower" | Held VFO's IF (roofing) filter bandwidth nudges one step |
| Transmit | "key transmitter" / "start transmitting"; "stop transmitting" / "go to receive" | Radio keys up / drops back to receive |
| Split | "split on" / "enable split"; "split off" / "simplex" | Split operation toggles |
| Antenna tuner | "tuner on" / "tuner off"; "tune antenna" | "tuner on"/"off" engages/bypasses the ATU; "tune antenna" starts the auto-tune cycle. There's deliberately no bare "tune" — that would collide with the "tune up" / "tune down" step commands above |
| Status read-back | "what frequency", "what mode", "what band" | YWC speaks the held VFO's current value out loud — no CAT command is sent to the radio |
| Help | "help", "what can I say" | YWC speaks a short list of the available command categories |
| Macros | "noise reduction on/off", "noise blanker on/off", "copy a to b" / "copy b to a", "fine step up/down", "roofing three/six/twelve kilohertz" | Runs the matching one-shot CAT command. See the Macros group in the phrase editor for the full list and their exact CAT strings |

A few notes on phrasing:

- **Spell out mode letters.** Say "U S B" (three letters), not "USB" as a word — the speech engine handles letter-by-letter spelling much more reliably for short acronyms.
- **Fractional frequencies are spoken digit-by-digit** after "point". "Fourteen point zero seven four" parses as 14.074, not "fourteen point seventy-four". "Oh" is accepted as an alternative to "zero".
- **"megahertz" is optional.** Both "set frequency to fourteen point zero seven four megahertz" and "tune to fourteen point zero seven four" work — say it or skip it.
- **MHz only — no kHz.** The grammar recognises frequencies in whole-or-decimal **megahertz**, from 1 MHz up to 71 MHz (covering HF + 6 m + 4 m). It does **not** recognise kilohertz input. If you say something the grammar can't parse — e.g. "tune to thirty kilohertz" — the engine will fuzzy-match to the nearest valid in-range phrase ("tune to thirty point eight") and act on that instead. **Listen to the spoken confirmation** that follows every command: it tells you exactly what got recognised, which is the safety net against misrecognition. For sub-MHz tuning (LF, MF, the 30 kHz lower limit on the FTdx101), use the mouse or the keyboard-driven frequency display instead — see §16.7.
- **Bands supported:** 160, 80, 60, 40, 30, 20, 17, 15, 12, 10, 6 and 4 metres. The default frequency picked for each band is roughly the FT8 / digital hangout — adjust with a follow-up "set frequency to …" or "tune up" / "tune down".
- **Band and step phrases work with or without the lead-in word.** "forty metres" is the same as "go to forty metres", and "ten kilohertz" is the same as "set step to ten kilohertz". Say whichever feels natural.
- **Digit-form band names for tricky mics/accents.** "twenty metres" and "eighty metres" share the same rhythm, so on a quiet microphone or with a strong accent the speech engine can confuse them. Every band therefore also accepts a **digit form** where the leading word is unmistakable — "two zero metres", "eight zero metres", "four zero metres", and so on. "two" and "eight" sound nothing alike, so these are recognised reliably when the natural form isn't. "top band" is also accepted for 160 m.
- **Scots variants** are accepted: "go tae forty metres" works the same as "go to forty metres".

**After every command, YWC speaks a short confirmation** through the PC's default audio output:

- *"Move to fourteen point zero seven four megahertz, successful"* — for SetFrequency.
- *"Move to 20 metres, successful"* — for SetBand.
- *"Mode U S B, successful"* — for SetMode.
- *"Swap V F O, successful"* — for SwapVFO.
- *"Tune up, successful"* / *"Tune down, successful"* — for nudge.
- *"Antenna tuning"* — for "tune antenna". This one is a plain acknowledgement rather than a "successful"/"unsuccessful" verdict, because the radio doesn't report whether the tune cycle found a match.
- If the command was rejected (e.g. frequency out of range), the suffix is *"unsuccessful"* instead.

This is a primary accessibility feature: a partially-sighted operator can drive the radio without watching the screen and hear exactly what happened to each command. The confirmation also doubles as the safety net for misrecognition — if you said "tune to fourteen" but heard *"Move to forty megahertz, successful"*, the spoken readback tells you the engine misheard and you can issue the command again. Confirmations don't name the VFO — you already know which one from which mic button you were holding. Disable in **Settings → Voice Control → Speak confirmation after each voice command** if you find it chatty.

### 17.2 Enabling voice control

1. Open **Settings** in the YWC top navbar.
2. Scroll to the **Voice Control** section.
3. Tick **Enable voice control**, then click **Save Settings**. Every long section on the Settings page now has its own **Save Settings** button, so you can save in place without scrolling to the bottom — the one in the Voice Control section sits just above the Voice Phrases editor.
4. **Restart YWC.** The speech engine is loaded once at startup; the toggle takes effect on the next launch.
5. Confirm the **Windows speech recognition pack for your active language** is installed. Open Windows → Settings → Time &amp; Language → Speech and check the installed-languages list. The active language defaults to English (United Kingdom) — if it isn't listed, install it from there (most UK Windows installs already have it). The **Active language** dropdown in the Voice Control section lets you switch to any other installed language pack (see [§17.7](#177-more-languages)).

**Choosing the microphone and speaker.** The Voice Control section also lets you pick which **microphone** the recogniser listens to and which **speaker** the spoken confirmations play through, each with a **Test** button. Leave them on the Windows defaults to follow your system settings — but if your default output is tied up by another program (WSJT-X, rig audio) you may never hear the confirmations, so it's worth picking your own speakers here. Picking a device in YWC does **not** change your Windows defaults.

Both lists are read once when the Settings page opens, so a microphone or headset plugged in *after* that won't be in them. Each list has a **Refresh** button beside it that re-scans without reloading the page — plug the device in, press Refresh, and it appears.

After restart, you should see a **mic button on the Index page beside each VFO panel** — VFO A's next to VFO A's band/mode controls, and (on dual-receiver radios) VFO B's next to VFO B's. If you don't see them, jump to [§17.4 Troubleshooting](#174-troubleshooting).

### 17.3 Using the mic buttons

Each mic button is a **press-and-hold** control — it doesn't latch. The two VFOs' buttons are independent, but only one can be listening at a time (there's a single speech engine underneath); holding one button while the other is already listening simply has no effect until you release the first.

1. **Press and hold** a VFO's mic button. The button has three clear states so you can always tell what it's doing: **amber** when it's armed (pointer over it, not yet listening), a **pulsing red, pressed-in** look while it's listening, and **green** for a moment when a command was recognised. The pressed-in shape change (not just the colour) is there to help if colour alone is hard to make out.
2. **Speak the command clearly** at a normal volume.
3. **Release** the button. The engine processes what it heard.
4. If the phrase matched the grammar, that VFO responds within a fraction of a second. The button returns to its idle colour.
5. Bold text under the button shows the **last phrase recognised** and which command it matched — useful for spotting misrecognitions ("set frequency to forty metres" instead of "go to forty metres", say).

If you change your mind mid-phrase, just release the button without speaking. Nothing is sent to the radio unless a full grammar match is found.

**Right-click a mic button** (on desktop) to pop up the full list of commands you can say, grouped by category. The list is generated live from your current phrase set, so it always matches what your installation actually responds to — including any phrases you've customised — and it's a quick on-screen reference without leaving the main page. Close it with the red **✕** or the **Esc** key.

**Low-confidence matches are rejected.** If you say something the engine isn't sure about — a phrase outside the grammar, background noise during PTT, an ambient TV in the room — the recognition is dropped rather than fitted to the closest rule. This stops random audio from accidentally firing a "set mode" or "go to band" command that would change the radio's state without you intending it. The "Last heard" hint under the mic button shows what the engine almost picked up; the Diagnostics block on the Settings page logs it as "Low-confidence match".

The Settings page → Voice Control section has a **Diagnostics** block that shows the current state of the engine, the last phrase heard, the last intent matched, and any error message. Open it in another browser tab if you want a live view of what voice control is doing.

### 17.4 Troubleshooting

**No mic buttons on the Index page.**
- Did you tick "Enable voice control" in Settings *and* restart YWC? The toggle only takes effect on next launch.
- Only one mic button (VFO A's)? That's expected on single-receiver radios (FTdx10, FT-710, FTDX3000) — there's no independent VFO B to target.
- Open the Settings page → Voice Control → Diagnostics. If the **State** is anything other than `Idle`, there's an engine error — read the **Last error** line.
- If Diagnostics shows the active language's Windows speech pack isn't installed, install it (Windows → Settings → Time &amp; Language → Speech → Add a language) — see [§17.2](#172-enabling-voice-control).

**Mic button is there but commands don't do anything.**
- Open the **Diagnostics page** (`http://localhost:8080/Diagnostics`), click the **Voice Control Log** button at the top, then click **Refresh**. This shows the recent voice events (start / stop / heard / rejected / dispatched) from today's log without you having to find or parse the raw log file. Click **Copy to clipboard** to grab them for a bug report.
- You should see `SAPI recogniser ready` shortly after YWC startup and a `Rejected (best alt: '…')` line for each unmatched press. The "best alt" is the engine's best guess at what you said — if it's wildly wrong, the mic itself may have a problem (try Windows Sound settings → Input → speak and see if the level meter responds).
- If the log shows `Rejected (best alt: '<your phrase>')` and your phrase looks correct, the grammar wording isn't matching what you said. Try one of the alternative phrasings listed in [§17.1](#171-what-you-can-say), or open a [GitHub discussion](https://github.com/mm5agm/Yaesu_Web_Control/discussions) and propose a new phrasing.
- The raw log file lives at `%APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-YYYYMMDD.log` if you ever need the unfiltered version (e.g. CAT command traffic, SDR worker status, etc.), but the Diagnostics page is the right tool for voice-specific issues.

**A lot of commands are misheard, or the "best alt" is close but wrong.**
- A **very quiet microphone** is the usual cause — the engine gets too little signal to be confident, so correct recognitions get dropped and marginal ones get mis-fitted. YWC now amplifies a weak mic automatically, but if recognition is still poor, move closer to the mic, or raise the input level in Windows → Sound settings → Input (and check any hardware `-10 dB / 0 dB` pad switch on the mic itself is set to `0 dB`).
- For the specific case of **"twenty metres" being heard as "eighty metres"** (or vice versa), use the digit forms **"two zero metres"** / **"eight zero metres"** instead — see [§17.1](#171-what-you-can-say). They can't be confused the way the natural words can.

**"Tune up" doesn't seem to do much.**
- Check you're watching the VFO panel whose mic button you actually pressed — each VFO steps independently, so "tune up" spoken into VFO B's button moves VFO B, not VFO A.
- Each VFO's step size is shown (and changeable) in the dropdown next to that VFO's mic button, default **10 kHz**. If it's set small (e.g. 10 Hz) the movement can be easy to miss. Change it with the dropdown or by voice: "set step to ten kilohertz".
- If you need bigger jumps use "set frequency to …" or "go to … metres" instead.

**Speech engine works for a while then stops responding.**
- Restart YWC. The engine is held alive across recognitions, and on rare Windows audio-stack hiccups it can lose its mic handle. Restart is the cleanest fix; if you see this often, report it on GitHub with the log.

### 17.5 Privacy

- All speech recognition happens **locally on your PC** through the Windows SAPI 5 engine. No audio is uploaded to Anthropic, Microsoft, Amazon, or anyone else.
- Recognised phrases are written to YWC's log file (`ywc-YYYYMMDD.log`) so that misrecognitions can be diagnosed. If that's a concern, set the log retention / rotation in Settings, or simply disable voice control when not in use.
- Nothing leaves the PC except the standard CAT commands going to the radio over the serial port.

### 17.6 Adding your own commands

The voice command grammar is **data, not code** — it lives at `%APPDATA%\MM5AGM\Yaesu Web Control\Grammars\<culture>\Commands.<culture>.json` and is edited live from **Settings → Voice Control → Voice Phrases**:

- **Voice Phrases editor.** Every command family from the [§17.1](#171-what-you-can-say) table is listed with its trigger phrases in an editable grid — add, remove, or reword phrases, one or more per row, comma-separated. **Save phrases** writes immediately and takes effect with no restart; **Validate** checks for empty/duplicate entries before you save; **Reset to defaults** restores the built-in English (UK) set.
- **Test this pack.** A dry-run tester — speak a command and see what's recognised without sending anything to the radio, useful for checking a reworded phrase actually matches before trusting it live.
- **Version history.** Every save (and every pack import) snapshots the previous version — up to the last 5 — so a bad edit or import can be undone from **Show version history**.
- **Export / import as a language pack.** **Export language pack** bundles the current phrases, a generated `.srgs` reference copy, and author/description metadata into `YWC-VoicePack-<culture>-vN.zip` — share it on the [GitHub Discussions](https://github.com/mm5agm/Yaesu_Web_Control/discussions) group. **Preview import** lets you inspect another pack's contents before installing it.
- **Open user grammars folder** jumps straight to the `Grammars\` folder in Explorer if you'd rather hand-edit the JSON or inspect the generated `.srgs` file (a human-readable reference copy, not what the engine actually loads).
- **Advanced mode** (off by default) allows a Custom Command's CAT string to be anything, not just a recombination of prefixes the built-in Core Commands already send. Only enable it if you trust the source of any pack you import.

If there's a particular command or phrasing you'd like added to the *built-in* defaults (as opposed to your own local edit), please file it as a GitHub issue or discussion.

### 17.7 More languages

Only **English (UK)** ships as the built-in default, but the language pack system itself is already multi-language. As of v2.4.1 an **English (US)** pack is also available — same commands and phrases as the UK default, with US spelling ("meters" instead of "metres"). Get it from `/voice-packs/YWC-VoicePack-en-US-v2.zip` on your running YWC instance and install it via **Preview import** below.

1. **The Windows speech pack for the target language must be installed** on the operator's PC (Windows → Settings → Time &amp; Language → Speech → Add a language). Microsoft ships full recognition packs for US English, French, German, Spanish, Italian, Japanese, Mandarin Chinese, Brazilian Portuguese and Australian English (the list shifts between Windows releases). Some languages only ship voice synthesis, not recognition — those can't be used for voice control regardless of what YWC does.
2. **A phrase pack for that culture.** The **Active language** dropdown in Settings → Voice Control lists every culture with an installed pack (a ✓ or ⚠ shows whether Windows also has a matching recogniser). Installing a new one means either importing a `YWC-VoicePack-<culture>-vN.zip` someone else has authored and shared (**Preview import** → **Install**), or hand-authoring `Commands.<culture>.json` and dropping it into `Grammars\<culture>\` via **Open user grammars folder** — the semantic keys (intent names, parameter vocab) stay identical to the English defaults, only the phrases change.
3. **Switching locale takes effect immediately** — no restart needed, unlike the initial enable toggle.

The **Voice Phrases editor** itself currently only edits the **en-GB** pack in place; editing an *installed non-English pack* through the same in-app grid isn't wired up yet — for now, translate by hand-editing that culture's JSON file directly, or ask a fluent speaker to export their pack after editing it locally. If you'd like a particular language prioritised for a built-in default, or want the editor to support editing other locales directly, please open a GitHub discussion or issue and mention it.

---
