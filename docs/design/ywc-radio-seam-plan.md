# The YWC radio seam

Agreed 2026-08-30 with Fabio, on YWC PR #111. The order is his:

1. a **YWC-local** seam first;
2. a thin **semantic** API — verbs, not wire traffic;
3. CAT behind it;
4. the stub as one implementation of it;
5. used in YWC until it has paid for itself.

Nothing here goes into `core`. `core/docs/design/shared-core-plan.md` §3a
records why, and what the later question actually is. This document lives in
YWC's own `docs/design/` deliberately — its location is part of the decision.

**IWC needs no equivalent work.** It already has this seam:
`Services/IRadioController.cs`, with `CivRadioController` on one side,
`StubRadioController` on the other and `IWC_USE_STUB_RADIO=1` choosing. That is
the working example to learn from, and the second half of "once two real seams
exist to compare."

---

## 1. What is already true, and must not be re-done

Three findings from surveying the code, all of them good news.

**The semantic vocabulary already exists — in HTTP.** `CatController` exposes 72
endpoints and they are already verbs: `POST frequency/a`, `POST mode/{receiver}`,
`POST ifwidth/{receiver}`, `POST split/{mode}`, `POST swap-vfo`. The browser has
never spoken CAT. So the seam is not a vocabulary to invent; it is mostly
**making the C# say what the HTTP already promises.** Where the two disagree,
HTTP is the older and better-tested design.

**There is already a partial seam, and it is the wrong shape.**
`Services/ICatClient.cs` is 35 lines. Most of it is genuinely semantic —
`ReadFrequencyAAsync`, `SetModeSubAsync`, `ReadSMeterMainAsync`,
`ReadTransmitStatusAsync`. But it also carries
`SendCommandAsync(string command, …)`, which takes a raw CAT string, and that
one member is what essentially everything actually calls. The seam exists and is
bypassed.

**The bypass is measurable.** 235 `SendCommandAsync` call sites outside `core/`:

| file | sites |
|---|---|
| `Controllers/CatController.cs` | 129 |
| `Controllers/MemoryController.cs` | 25 |
| `Services/MeterPollingService.cs` | 17 |
| `Services/MultiplexedCatClient.cs` | 16 |
| `Services/RadioInitializationService.cs` | 15 |
| `Services/RigctldServer.cs` | 13 |
| `Services/CatMultiplexerService.cs` | 10 |
| `Services/VcTune/*`, `Controllers/ScopeController.cs`, `Services/Voice/IntentDispatcher.cs` | 10 |

`MultiplexedCatClient` and `CatMultiplexerService` are the transport itself and
are *supposed* to be there. The other ~209 are the job.

## 2. What "thin" has to mean here

IWC's `IRadioController` is 586 lines and about 120 members, and its stub is
627 lines. It works, but it is not thin, and cloning it into YWC would be
building the fat version on purpose.

So the rule for this seam is: **a verb earns its place when a caller needs it,
not when the radio supports it.** The interface starts at the smallest set that
lets the app boot, render and be driven, and grows one verb at a time as
endpoints move across. IWC's interface is a menu to consult, not a template to
copy.

The first slice is, near enough, what `ICatClient` already declares minus the
escape hatch:

```
ConnectAsync / DisconnectAsync / IsConnected
GetFrequencyHzAsync(vfo) / SetFrequencyHzAsync(vfo, hz)
GetModeAsync(vfo)        / SetModeAsync(vfo, mode)
ReadSMeterAsync(vfo)
GetTransmitAsync()       / SetTransmitAsync(bool)
```

with `RadioVfo { A, B }` instead of A/B suffixed method pairs — IWC's enum,
which has held up.

## 3. Two halves, and only one of them is request/response

Worth stating before any code, because it is where a naive seam breaks.

**Commands** are request/response: send, await, get an answer.

**State is pushed.** `CatMessageDispatcher` parses unsolicited traffic and
writes `RadioStateService`; endpoints frequently send a query and then read the
*service*, not the reply. From `CatController.QueryIfWidth`:

```csharp
var response = await _catClient.SendCommandAsync($"SH{p1};", "WebUI", …);
// The dispatcher will have updated RadioStateService.IfWidthA/B by now.
var current = VfoIsB(receiver) ? _radioStateService.IfWidthB : _radioStateService.IfWidthA;
```

So the seam has a second obligation: **whatever is behind it must keep
`RadioStateService` fed.** A stub that only answers method calls would leave the
whole UI blank, because the UI reads state, not replies. This is the single
most important thing to get right in phase 1, and the thing most likely to be
discovered too late.

## 4. The boilerplate is the argument

Every one of those 72 endpoints repeats the same five steps: take
`_requestSemaphore` with a 2 s timeout, `EnsureConnectedAsync()`, build a CAT
string, write the result into `RadioStateService`, catch and log. That is
serialisation, connection policy and cache coherence decided 72 times.

Behind a seam it is decided once. That is worth more than the stub.

## 5. Phases

Each phase is separately shippable and separately revertable. No phase requires
the next one to have started.

### Phase 1 — the interface and the CAT implementation

Add `Services/Radio/IRadio.cs` (the slice in §2) and
`Services/Radio/YaesuCatRadio.cs` implementing it over the existing
`ICatClient`. `YaesuCatRadio` owns the semaphore, the connect check and the
`RadioStateService` write. Register it in `Program.cs` alongside the existing
`ICatClient` registration; change no caller yet.

Yaesu wire knowledge — `RadioCapabilities.VfoP1`, `ModeP1`,
`Services/YaesuIfWidth.cs` — moves *behind* `YaesuCatRadio` or stays where it
is, but stops being visible to controllers. Note that `VfoP1` and `ModeP1`
currently live in `RadioCapabilities`, which is otherwise a "what can this radio
do" lookup; they are CAT P1 digits, and they are the clearest example of the
wire format having leaked upward. IWC deleted its `VfoP1` at exactly this point
in its own migration.

Done when: builds, tests pass, nothing behaves differently.

### Phase 2 — move the callers, in batches

Not one commit. Suggested order, easiest and least dangerous first:

1. **Frequency, mode, S-meter, TX status.** Already in `ICatClient`; the
   endpoints are simple; failure is instantly visible.
2. **RX controls** — AF/RF gain, squelch, AGC, IPO, NB/NR, notch, attenuator,
   IF width and shift, contour, APF. Bulk of the count, low risk, RX only.
3. **VFO topology** — active/rx/tx VFO, swap, copy, split, clarifier. Higher
   risk: this is where dual-receiver behaviour differs between the FTdx101MP/D
   and FTDX5000 pair (true MAIN+SUB) and the FTdx10/FT-710 (single receiver,
   two memory slots). `RadioCapabilities.IsDualReceiver` already encodes it.
4. **TX-side controls** — power, mic gain, processor, VOX, monitor, ATU, CW.
   Last, because they are the ones that can key a transmitter.
5. **Non-controller callers** — `MeterPollingService`,
   `RadioInitializationService`, `MemoryController`, `RigctldServer`,
   `IntentDispatcher`, `VcTune`, `ScopeController`.

Every batch gets a hardware pass before the next starts. Only Colin has the
radios, so **the hardware check is the pacing constraint, not the coding.** Plan
batches small enough to be checked in one sitting.

`SendCommandAsync` stays on `ICatClient` throughout — diagnostics and the raw
command box need it. The goal is that no *controller* reaches it.

### Phase 3 — the stub

`Services/Radio/StubRadio.cs`, selected by `YWC_USE_STUB_RADIO=1`, matching
IWC's switch exactly so the two apps are driven the same way. It must feed
`RadioStateService` (§3), not merely answer calls.

It only becomes possible after phase 2 batch 1, and it gets more useful with
each later batch. It is not a phase-1 deliverable and should not be used to
justify one.

A benefit specific to this station: the stub is the only way to exercise the
**TX** code paths — `POST tx`, ATU tune, VOX, CW keying — without a
transmitter. Those are the paths currently least testable here, because the
standing rule is RX only and tune stays off.

### Phase 4 — earn its keep

Fabio's condition, so it needs a definition rather than a feeling. The seam has
paid for itself when it can point at:

- a bug **reproduced** under the stub with no radio attached (IWC's precedent:
  the Firefox meter-needle bug was both reproduced and its fix verified that
  way);
- an HTTP-layer change **runtime-verified** without opening the CAT port —
  the cost on record is PR #110, a static-asset caching fix that could not be
  checked at all;
- a WSJT-X or rigctld integration change tested without a radio, via
  `RigctldServer` and `WsjtxUdpService` sitting on the same seam;
- a per-model difference (FTdx101MP vs FT-710) fixed in **one** place rather
  than across scattered endpoints.

Two of those four, honestly claimed, is enough to call it.

## 6. Guardrails

- **Nothing goes to `core`.** Not the interface, not the stub, not the enum.
  The later question is described in `core/docs/design/shared-core-plan.md` §3a
  and is not open yet.
- **The front end is not in scope.** Some endpoints leak the wire format into
  their JSON — `GET ifwidth/{receiver}` returns a Yaesu `SH` *code*, and the
  browser depends on it. Translate at the seam if it is free; otherwise leave
  the HTTP contract exactly as it is. A seam migration that also rewrites
  `site.js` is two changes wearing one coat.
- **No big-bang.** `CatController.cs` is 3,087 lines. A single commit that
  rewrote it would be unreviewable and unverifiable against hardware, and there
  is no test suite that would catch the regression.
- **The seam is not an abstraction exercise.** If a verb has one caller and one
  implementation and always will, it can wait.

## 7. Open questions

- Does `MemoryController` (25 sites) belong behind the same interface, or does
  memory-channel work deserve its own narrower one? IWC put it on the same
  interface (`ReadMemoryChannelAsync` and friends) and that interface is now
  586 lines; that is evidence for splitting, not against.
- Where does the SDR sit relative to the seam? `SdrController` and
  `Services/Sdr` do not touch CAT at all, so probably nowhere near it — but
  VC-Tune does both, and it is the one caller that genuinely straddles.
- Does the stub need to fake a *model*, so that per-model UI paths
  (dual-receiver panels, 200 W vs 100 W scales) can be exercised? Probably yes,
  and probably as an env var alongside `YWC_USE_STUB_RADIO`.
