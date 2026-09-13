## 20. CW Reader

The **CW Read** button on the main control panel opens a reader that listens to the radio's receive audio and prints the Morse it hears as text. It also offers a **Reader Mode** button that sets the radio up for decoding and puts your settings back afterwards, keeps a transcript of everything it decoded, and can turn a contact into a line in an ADIF log.

Nothing here transmits. The reader only listens.

### 20.1 What to expect from a machine reading Morse

I want to be straight about this before describing the controls, because it is the thing that surprises people.

On a strong, clean, machine-sent signal the reader is close to perfect. On a marginal one it prints plausible-looking rubbish that looks exactly the same as good copy — the same confident letters, the same spacing. It has no way of knowing the difference, and neither has the screen. My own measurements have had it report full confidence on nearly six hundred characters of complete junk.

That is why the **status line** under the text matters as much as the text. It tells you which of the two you are looking at:

| What it says | What it means |
|---|---|
| `signal` / `no signal` | Whether there is a keyed tone in the passband at all. |
| `nothing readable - the tone is breaking up, not keying` | Something is there but it is not Morse — QSB, splatter, a chopped-up signal. The reader deliberately prints nothing rather than guessing. |
| `nothing readable - more than one signal in the passband` | Two or more stations are inside your filter. Narrow it, or move. |
| `tone 612 Hz` / `pitch 600 Hz` | The note it is tracking, and the CW pitch you have the radio set to. |
| `off pitch - tune +120 Hz` | The signal is not on your pitch. Tune, or use **ZIN** in the CW Keyer panel. This is worth watching: a station that reads as very weak is often just sitting on the skirt of a narrow filter. |
| `filter 500 Hz` / `filter unknown` | The IF width the radio reported. Unknown means the reader is using a default search window rather than your passband — usually because the radio is in AM or FM. |
| `search +/-250 Hz` | How far either side of your pitch it will hunt for a tone. It is half the filter width, clamped to between 100 and 500 Hz — so widening a 2.4 kHz filter further will **not** widen the search, and narrowing below 200 Hz will not narrow it. |
| `22 wpm` | Only shown once it has genuinely locked onto the keying. If it is absent, it does not know. |
| `SNR 14 dB` | Signal-to-noise in the audio passband. |

Colour in the decoded text marks what *looks like* QSO traffic — procedural signals (`CQ`, `DE`, `73`), signal reports, and callsigns, with a callsign heard more than once shown brighter than one heard only once. Nothing is hidden and nothing is corrected: the colour is a hint about what the reader thinks it saw, laid over exactly what it decoded.

### 20.2 Starting it

1. Open the panel with **CW Read**.
2. Put the radio in CW and tune the station in — or press **Reader Mode** and let YWC do the filter part (see below).
3. Press **Start**.

The reader opens the radio's USB audio codec for listening on its own. You do **not** need a Remote Audio session running first. If the capture device is not chosen or has gone away, the status line says so in words rather than sitting there blank.

**It shares the radio's audio with Remote Audio rather than competing for it.** If you are already listening from another room ([§18](remote-audio.md#18-remote-audio)) you can start the reader without stopping anything, and in either order — one capture of the radio feeds both. Starting or stopping either one leaves the other running.

**The reader is not Windows-only.** Unlike the SDR spectrum and Voice Control, it runs on the macOS and Linux hosts and in Docker as well. What it needs is a capture device it can open — on Linux and in Docker that means the sound devices have to be passed through, which is the same requirement Remote Audio has ([§18](remote-audio.md#18-remote-audio)).

Set your CW pitch in the CW Keyer panel (§5.12) and tune the station onto it. The reader decodes at the pitch you are using, because that is the tone you have tuned the signal to — hunting for a note you are not listening to is not much use to either of you.

| Control | What it does |
|---|---|
| **Start / Stop** | Runs the decoder. Stop also restores your radio settings if Reader Mode is on. |
| **Clear** | Empties the on-screen text. The transcript file on disk is not touched. |
| **Reader Mode** | Sets the radio up for decoding — see §20.3. |
| **Log QSO** | Opens the log form — see §20.5. |
| **Follow** | Keeps the newest text in view. Turn it off to scroll back through an over without being dragged to the bottom. |
| **Tune** | Shows a tuning display beside the text: a passband spectrum with a marker at your pitch, and an X-Y figure that stops turning when you are exactly zero-beat. Handy if you would rather tune by eye than by ear. |

### 20.3 Reader Mode

**Reader Mode** is one button that sets the radio the way the decoder wants it, and remembers what you had so it can put it back:

- **CW mode** — if the radio is already on CW-U or CW-L it stays on the one you were using.
- **A narrow IF filter** — 250 Hz by default, configurable in [§6.10](settings.md#610-cw-reader-mode).
- **APF on** — at your current APF frequency, unless you have turned that off in settings.

Press it again, or press **Stop**, and your mode, filter width and APF go back exactly as they were.

The button's tooltip names what it is holding for you, so you can see before you press it a second time what is about to come back.

Two details worth knowing:

- **It restores on Stop, not when you close the panel.** Closing the reader deliberately leaves it running — you can put the panel away and come back to it. Stop is when you have actually finished reading, so that is when the radio goes back to how you had it. A filter that quietly re-opened to 2.4 kHz in the middle of a QSO would be the worse surprise.
- **It survives a page reload.** YWC remembers your previous settings on the host, not in the browser tab, so if you reload the page — or open YWC in a second browser — the button still knows what to put back. That is the whole reason it is a server-side feature rather than three quick commands from the page.

Reader Mode only touches VFO A.

### 20.4 Transcripts

Every session writes a plain-text transcript, so nothing you decoded is lost because you did not think to save it at the time.

- **Windows:** `%APPDATA%\MM5AGM\Yaesu Web Control\CW Transcripts\`
- **macOS / Linux:** `~/.config/MM5AGM/Yaesu Web Control/CW Transcripts/`

Files are named for when the session started — `cw-20260901-143000.txt` — with a header line recording the frequency, mode and pitch, and each line stamped with the time it began. A session that decodes nothing leaves no file at all, so the folder does not fill up with empties.

The text is written as it arrives rather than held until you close the reader, because the thing a transcript most needs to survive is a crash, and a crash happens while something is arriving. You can open a transcript in a text editor while the reader is still running.

Transcripts are never deleted automatically. They are small — a long session is a few kilobytes — but they are yours to tidy up.

### 20.5 Logging a QSO

**Log QSO** opens a short form under the decoded text and appends a confirmed contact to an ADIF file:

- **Windows:** `%APPDATA%\MM5AGM\Yaesu Web Control\ywc-log.adi`
- **macOS / Linux:** `~/.config/MM5AGM/Yaesu Web Control/ywc-log.adi`

Log4OM and GridTracker both watch ADIF files, so this reaches those programs with nothing else to set up (see §9.3 and §9.4).

One rule shapes the whole form, and it follows directly from §20.1:

> **A field the radio or the clock knows is filled in. A field the *decoder* thinks it knows is offered, and left empty until you pick it.**

So the frequency, band, mode and UTC time are simply shown as a line of facts — they come from the rig and the system clock, and they will be written whatever you do. The callsign, report, name and QTH come from the copy, and the copy can be confidently wrong. Those boxes start **empty**, with suggestions beside them as buttons. Each button carries the reason it was suggested — *follows DE*, *sent 3 times* — on its face, so you can weigh it. One click fills the box.

That costs you a single click when the copy was good, and saves you a wrong log entry when it was not. A callsign silently pre-filled from junk is worse than an empty box, because you have no reason to look twice at a field that already has something in it.

| Field | Where it comes from |
|---|---|
| Callsign | Suggested from the copy. Required — the form will not save without one. |
| RST rcvd | Suggested from the copy. `5NN` and `599` are both recognised. |
| RST sent | Pre-filled with `599`, because it is what you decided, not something in the copy. |
| Name, QTH | Suggested from the copy. |
| Comment | Yours to type. |
| Frequency, band, mode, time, your callsign | From the radio, the clock and your DX cluster login callsign. Shown on the facts line. |

**Re-read copy** asks the reader again. This is worth a button of its own — you usually open the form when the other station starts sending their details, and the name and QTH arrive after that.

After a save the QSO fields clear but **RST sent** and the facts line stay, ready for the next contact on the same frequency.

If a suggestion box says *nothing in the copy*, that is a result rather than a failure: the reader found no candidate it was willing to put its name to, and you should type the field yourself.

### 20.6 Troubleshooting

| Symptom | What to try |
|---|---|
| Status says the radio audio could not be opened | Set the Remote Audio **RX device** to the radio's USB codec in Settings (§6.8). The reader uses the same device. |
| Nothing prints, but the status line looks healthy | Read the status line properly — `nothing readable` means the reader is deliberately refusing to guess. `no signal` means there is no keyed tone in the passband. |
| Status shows `off pitch - tune ±N Hz` | The station is not on your CW pitch. Tune, or press **ZIN** (§5.12). |
| Text is confident but wrong | That is the normal failure mode on a marginal signal. Narrow the filter (**Reader Mode**), and check the SNR and `wpm` readings before trusting a callsign. |
| `filter unknown` | The radio is in a mode with no IF width table — AM or FM. Go to CW. |
| Widening the filter did not widen the search | It is clamped to ±500 Hz. See the `search` row in §20.1. |
| `N frames dropped` | The host could not keep up with the audio. On a Pi-class machine, close other panels — particularly the spectrum display and Radio Display. |
| Reader Mode did not restore my filter | Press it again, or press **Stop**; if a command failed part way it will retry. Reader Mode only touches VFO A. |
| The log file is not where I expected | The exact path is shown in the form's status line after a save. |

The reader decodes from the receive audio, so anything you can hear is something it can be given — but equally, if you cannot hear it, neither can it.

---


*Yaesu Web Control is written and maintained by mm5agm@outlook.com. For bug reports and feedback, please use the [Groups.io discussion group](https://groups.io/g/Yaesu-Web-Control/topics) or the [GitHub issues page](https://github.com/mm5agm/Yaesu_Web_Control/issues).*
