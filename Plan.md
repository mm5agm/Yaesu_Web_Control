# Implementation Plan — Missing CAT Controls

*Ordered by priority. Estimates are for backend + frontend together.*

---

## Phase 1 — Band & Step Navigation *(Priority 2, ~1 day)*
**Commands: BU, BD, UP, DN, TS**

All five belong together — UP/DN are useless without TS, and BU/BD live in the same "navigation" mental model.

**Backend — `CatController.cs`**
- `POST /api/cat/band/up` → sends `BU;`, then re-reads `FA;`/`FB;` to update displayed frequency
- `POST /api/cat/band/down` → same with `BD;`
- `POST /api/cat/vfo/step/up` → sends `UP;`, then re-reads active VFO frequency
- `POST /api/cat/vfo/step/down` → sends `DN;`
- `POST /api/cat/tuningstep/{receiver}` body `{ "step": 4 }` → sends `TS{vfo}{step:D3}0;`

**Backend — `RadioStateService.cs`**
- Add `TuningStepA` and `TuningStepB` int properties (default 4 = 1 kHz)

**Backend — `CatMessageDispatcher.cs`**
- Add `case "TS":` — parse `TS{vfo}{step:D3}0` → update `TuningStepA` / `TuningStepB`

**Backend — `CatMultiplexerService.cs`**
- Add `TS00000;` and `TS10000;` to `GetInitialValues()` to read both VFO steps on connect

**Frontend — `Index.cshtml`**
- **Band ◄ ►** buttons in the toolbar alongside the existing VFO-B/Swap/Split buttons
- **Tuning step dropdown** per VFO in the frequency display row (below the digit display): `1Hz 10Hz 100Hz 500Hz 1k 2.5k 5k 10k 100k 500k 1M` — same row as the keyboard icon button
- **▲ ▼ step buttons** flanking the tuning step dropdown, calling step up/down

**Frontend — `site.js`**
- Click handlers for band up/down that POST then re-render frequency
- Click handlers for step up/down
- SignalR handler for `TuningStepA` / `TuningStepB` updates (keeps step dropdown in sync if changed from front panel)

---

## Phase 2 — Anti-VOX Wire-up *(Priority 2, ~1 hour)*
**Command: VA**

The state property, UI slider, and API endpoint already exist. The endpoint stores locally only and never sends a CAT command. This is a bug fix, not a new feature.

**Backend — `CatMessageDispatcher.cs`**
- Add `case "VA":` → parse `VA0{000-100}` → update `RadioStateService.AntiVoxGain`

**Backend — `CatController.cs` — `SetAntiVoxGain`**
- Replace `_radioStateService.AntiVoxGain = request.Gain` + comment with `await SendCommand("VA0" + request.Gain.ToString("D3") + ";", true)`, then read-back `VA;` and update state from the response

**Backend — `CatMultiplexerService.cs`**
- Add `await SendCommand("VA;", true);` to `GetInitialValues()` so the real radio value is read on connect

No UI changes needed — the slider is already there.

---

## Phase 3 — Keyer Type + TX Narrow *(Priority 3, ~half day)*
**Commands: KI, XN**

Two independent small features, grouped because they touch the same files.

**KI — Keyer Type**

- `RadioStateService.cs`: Add `KeyerType` int property (0–3)
- `CatMessageDispatcher.cs`: Add `case "KI":` → parse `KI{0-3}` → update `KeyerType`
- `CatMultiplexerService.cs`: Add `KI;` to `GetInitialValues()`
- `CatController.cs`: Add `POST /api/cat/cw/keyertype` body `{ "type": 1 }` → sends `KI{type};`
- `Index.cshtml`: Add **Keyer Type** dropdown to the CW Keyer dialog, below the Break-in row: `Single Paddle / Iambic A / Iambic B / Bug`
- `site.js`: Change handler + SignalR update on `KeyerType`

**XN — TX Narrow**

- `RadioStateService.cs`: Add `TxNarrow` bool property
- `CatMessageDispatcher.cs`: Add `case "XN":` → parse `XN{0/1}` → update `TxNarrow`
- `CatMultiplexerService.cs`: Add `XN;` to `GetInitialValues()`
- `CatController.cs`: Add `POST /api/cat/txnarrow` body `{ "narrow": true }` → sends `XN0;` or `XN1;`
- `Index.cshtml`: Add **Narrow TX** toggle button in the Transmit Controls row (§5.10), alongside the ATU and PROC buttons; show only on FTdx101MP/D (same `@if` gate as other dual-receiver-only controls)
- `site.js`: Toggle handler + SignalR update on `TxNarrow`

---

## Phase 4 — Scan *(Priority 4, ~1 day)*
**Command: SC**

No scan state currently exists. Scan is a self-contained mini-feature with 9 modes.

**Backend — `RadioStateService.cs`**
- Add `ScanActive` bool property; the radio doesn't echo SC back so this is inferred from the last sent command

**Backend — `CatController.cs`**
- `POST /api/cat/scan/stop` → sends `SC0;`, sets `ScanActive = false`
- `POST /api/cat/scan/start` body `{ "mode": 7 }` → sends `SC{mode};`, sets `ScanActive = true`
- Modes: 1 = Up, 2 = Down, 3 = MHz Up, 4 = MHz Down, 5 = Prog Up, 6 = Prog Down, 7 = Memory, 8 = Select Memory, 9 = Memory Repeat

**Frontend — `Index.cshtml`**
- Add a **Scan** button to the toolbar (next to the MEM button); clicking opens a small inline popout with scan mode options and a **Stop** button
- Button turns amber while `ScanActive` is true

**Frontend — `site.js`**
- Click handlers for start/stop
- SignalR update on `ScanActive` to keep button state in sync

---

## Phase 5 — Memory Channel Navigation *(Priority 4, ~half day)*
**Commands: MC, MA, MB, CH**

MR/MW (full channel read/write) are deferred — they may already be in the memory service and are a large, risky change. MC/MA/MB/CH are small and composable.

**Backend — `CatController.cs`**
- `POST /api/cat/memory/recall` body `{ "channel": 5, "vfo": "a" }` → sends `MC{0/1}{channel:D3};`
- `POST /api/cat/memory/tovfo` body `{ "channel": 5, "vfo": "a" }` → sends `MA{channel:D3};` or `MB{channel:D3};`
- `POST /api/cat/memory/channel/up` → sends `CH1;`
- `POST /api/cat/memory/channel/down` → sends `CH0;`

**Frontend — `Index.cshtml`**
- Add **◄ CH ►** step buttons to the Memory Panel header (§5.15)
- Add a **→ VFO A** / **→ VFO B** button next to each memory row in the panel

**Frontend — `site.js`**
- Click handlers for the above; update frequency display after recall

---

## Phase 6 — Advanced Odds and Ends *(Priority 5, pick individually)*

| Item | What to add | Effort |
|------|-------------|--------|
| **VA already wired** | Covered in Phase 2 | Done |
| **BY — Busy detection** | Add `GET /api/cat/busy` → sends `BY;`, returns `{ busy: true/false }`. Poll in `MeterPollingService` every 500 ms when squelch is active. Use in site.js to flash a "BUSY" badge. | 2 h |
| **PB — Playback recorder** | Add `POST /api/cat/playback/{stop\|play\|record}` → sends `PB{0/1/2};`. Add Rec / Play / Stop buttons to a small recorder popout (toolbar button). | 2 h |
| **KM — Read keyer memory** | Add `GET /api/cat/cw/memory/{1-5}` → sends `KM{n};`, returns text. Display read-back in the CW memory settings panel so user can verify what's stored. | 1 h |
| **KC — Clear keyer memory** | Add `POST /api/cat/cw/memory/{1-5}/clear` → sends `KC{n};`. Add a clear (✕) button next to each memory slot. | 30 min |
| **AB/BA — Atomic VFO copy** | Change `copyVfo('ab')` / `copyVfo('ba')` in site.js from the two-step FA/FB read-write approximation to a single `AB;` / `BA;` command. No state or UI changes. | 30 min |
| **PS write — Standby** | Add `POST /api/cat/power/standby` → sends `PS0;`. Add a **Standby** option to the app's tray menu or a confirmation dialog behind the Exit button. | 1 h |
| **BS — Band scope** | Low value — only relevant for the optional band-scope accessory. Skip unless a user reports it. | — |
| **EX remainder** | Tackle individual EX parameters on-demand as feature requests arrive rather than doing a mass mapping. | — |

---

## Phase 7 — Skins: switchable layouts from one set of markup *(issue [#48](https://github.com/mm5agm/Yaesu_Web_Control/issues/48), UNPARKED 2026-08-06)*

**Status: unblocked, not yet started. Sequenced after the IWC build — see "Sequencing" below.**

**Read [ADR 0004](docs/decisions/0004-skins-not-per-layout-pages.md) before touching this phase.** The mechanism agreed with Jacek — a second Razor page per layout — was **superseded on 2026-08-06**. Three layouts (Classic, SP3L, Fabio's) would have meant adding every control three times, fixing every bug three times, and writing every `data-a11y-key` three times. Alternative layouts are now **skins**.

**The blocker is gone.** This phase was parked waiting for Jacek to answer *"where would the SDR spectrum displays go?"*, which he never did. Fabio Valente's prototype in [PR #93](https://github.com/mm5agm/Yaesu_Web_Control/pull/93) answers it with working desktop, tablet and mobile layouts.

**What a skin is (the mechanism — ADR 0004 has the full reasoning):**

- **One set of markup, shared by every skin.** Every control declared exactly once. No per-skin HTML.
- **A skin is a stylesheet.** Each control group is a named CSS grid area declared once in the shared markup; a skin supplies its own `grid-template-areas` plus colour/typography tokens.
- **Hiding a control group is CSS**, not conditional Razor rendering.
- **No per-skin Razor partial. This is absolute** — it is the loophole that reintroduces the duplication the decision exists to prevent.
- **The current `Index.cshtml` arrangement becomes the default skin.** Skin zero, not privileged.
- **Known limit, accepted:** CSS can move a control anywhere on the page but not into a different parent. Skins constrain layout freedom; that is the price of the maintenance property.

**What is unchanged from what Jacek was promised** — the user-facing surface is identical, only the mechanism behind it differs:

- User picks the layout in **Settings** via a "GUI Layout" dropdown. Options: *Classic* (current) / *SP3L* / others as they arrive.
- The Settings dropdown label for Jacek's layout stays **SP3L**.
- The `feature/jacek-gui` branch's Settings-dropdown skeleton is still useful; the rest of that branch's premise (a second Razor page) is not.

**Sequencing — IWC first.** Build the skin mechanism in IWC (`docs/design/iwc-clone-split-plan.md` Phase 7, where the design already lives), then port to YWC. IWC has no contributors working in it, so the ~4,500-line `Index.cshtml` refactor cannot collide; YWC has three open PRs (#90, #92, #93) all touching `Index.cshtml`, `Settings.cshtml` and `site.js`. Layout-independent work — notably **keyboard shortcuts**, which are also a screen-reader path — can proceed in YWC meanwhile.

**Open question:** is responsive (desktop/tablet/mobile) behaviour a property of each skin, carried by media queries inside every skin stylesheet, or a separate axis? Put to Fabio in the PR #93 thread; not yet answered.

**Accessibility is a condition, not a preference.** A skin offered in the Settings dropdown or made default must carry its `data-a11y-key` attributes — voice control and screen-reader labelling exist for partially-sighted operators and regressions are release blockers. An experimental skin behind a flag and unlisted in Settings may ship without them if labelled as such.

**The first two skins** are Jacek's layout (SP3L) and Fabio's. A high-contrast large-print skin for partially-sighted operators is wanted early, and under this mechanism is a stylesheet rather than a project.

**What is agreed on Jacek's layout itself:**

- Two panels per radio: **VFO-A** and **VFO-B** (not MAIN/SUB/VFO-A/VFO-B — MAIN≡VFO-A and SUB≡VFO-B on the FTdx101 family; on single-receiver radios one panel goes grey as per R1–R12).
- A **global strip** at the bottom: Frequency Lock, RF Gain, AF Gain, BK-IN. On the FTdx101 family RF/AF Gain are per-panel (inside each VFO panel), not in the global strip.
- A **TX Audio** sub-menu button (next to VOX) grouping: MIC Gain, PROC, Monitor — same controls as the existing popouts, just regrouped.
- Controls styled as: toggle button (ON/OFF state) + optional slider or dropdown alongside it when ON.

**What still needs deciding before code starts:**

1. ~~**Spectrum placement**~~ — **resolved.** Fabio's PR #93 screenshots answer it. Confirm the final choice against his layout rather than re-opening the question with Jacek.
2. **"Button OFF" semantics** — does pressing a control button to OFF mean "send the radio's default value" (active write) or "this YWC panel stops tracking / go configure from front panel" (read-only handoff)? Needs resolution for IF Width and similar always-on controls. Skin-independent — it is a behaviour question, not a layout one.
3. **Low Cut / Audio Filter** — in the current layout these are per-mode parameters accessed via the Audio Filter popout (EX commands). Jacek's mockup removed Low Cut. Under skins this becomes a control-manifest question: which skins show the group, which collapse it.
4. **Responsive axis** — skin-carried media queries, or a separate axis? Put to Fabio, unanswered.

**Implementation order (revised for skins — supersedes the second-page order):**

1. **Build the mechanism in IWC first** and prove it there. Do not start in YWC while #90, #92 and #93 are open against `Index.cshtml`.
2. Port to YWC: refactor `Pages/Index.cshtml` so every control group is a named CSS grid area, declared once. This is the bulk of the work (~4,500 lines). No behaviour change — the default skin must look identical to today when finished.
3. Extract the current look into `wwwroot/css/skins/classic.css` as skin zero.
4. Wire the Settings "GUI Layout" dropdown to the active skin (store choice in `appsettings.user.json`, apply via a class or attribute on the page root — **not** by serving a different Razor page). The `feature/jacek-gui` skeleton covers most of this.
5. Add `sp3l.css` and Fabio's skin as stylesheets. No new Razor, no new JS init, no per-skin SignalR wiring — the DOM is the same DOM.
6. Verify `data-a11y-key` coverage once, in the shared markup, and confirm voice control and screen-reader labels work in every listed skin.
7. Test on FTdx101MP and FTdx10 (single-receiver grey-panel behaviour must match R1–R12 spec).

---

## Phase 8 — Voice Control v2 *(WAITING — no tester feedback on v1 yet)*

**v1 status: SHIPPED in v2.4.0-pre1 / pre2.** System.Speech (SAPI 5), grammar-based, 6 intents, en-GB only, on-screen mic button. Functionally complete and verified end-to-end on Colin's FTdx101MP.

**Trigger for v2 work: at least one named tester confirms v1 working.** Do not start v2 until feedback arrives. The failure modes (accent, mic quality, background noise) need real-world data before extending the feature.

**Named testers — neither has responded:**
- **Yuri W4YSW** — accessibility user, FTdx10. Original requestor of the feature.
- **Thomas OZ1JTE** — partially sighted, detailed reporter, engaged on #20.
- **Not Bill W1WRH** — feedback pace is too slow for a 1–2 week pre-release cycle.

**Do not wide-announce on groups.io** until v2.4.0 ships clean — voice recognition fails in subtle ways (accent, mic quality, room noise) and a broadcast would generate "doesn't recognise me" reports without clear fixes.

**v1 known limitations (become v2 backlog when feedback arrives):**

| Limitation | v2 fix |
|---|---|
| en-GB only | Multi-language scaffold — let user pick recognition language in Settings |
| VFO A only for all commands | Add "VFO A / VFO B" prefix intents: "set VFO B to 14 megahertz" |
| Restart required to enable/disable | Live Settings toggle with no-restart grammar reload |
| No voice setup wizard | Guided in-app setup (mic selection, test phrase, confidence calibration) — design doc at `docs/design/voice-control-setup-wizard.md` |
| User-defined commands not implemented | Load SRGS files from `%APPDATA%\…\Grammars\` at startup and merge into the live grammar. The folder and Settings button already exist as a v2 placeholder (§17.6). Needs: SRGS loader, merge logic, and either a hot-reload path or restart-to-apply. A simple table UI in Settings (phrase → intent mapping) is the minimum; a full SRGS editor is a stretch goal. |

**v2 implementation notes (when triggered):**

- Multi-language: `CultureInfo` is already a constructor parameter on `SpeechRecognitionEngine`. Wiring a Settings dropdown → service restart is the bulk of the work.
- VFO B targeting: the intent dispatcher routes to `CatController` helpers that already accept a receiver parameter — adding "VFO A / VFO B" slots to the grammar is the main change.
- Live toggle: the `IHostedService` lifecycle needs a `ReloadAsync` path that tears down and rebuilds the engine without a full app restart.
- Setup wizard: design doc already accepted — `docs/design/voice-control-setup-wizard.md` is the authoritative spec. Read it end-to-end before implementing; don't re-derive choices.

---

## Summary Table

| Phase | Commands | New endpoints | UI additions | Estimate |
|-------|----------|---------------|--------------|----------|
| 1 | BU, BD, UP, DN, TS | 5 | Band ◄/►, step ▲/▼, step dropdown | ~1 day |
| 2 | VA (fix) | 0 (existing fix) | None | ~1 hour |
| 3 | KI, XN | 2 | Keyer type dropdown, Narrow TX toggle | ~half day |
| 4 | SC | 2 | Scan button + mode popout | ~1 day |
| 5 | MC, MA, MB, CH | 4 | CH step buttons, →VFO buttons | ~half day |
| 6 | BY, PB, KM, KC, AB/BA, PS | 8 | Minor per-feature additions | ~pick & mix |
| 7 | *(no CAT commands)* | 0 new | Skins — switchable layouts from one markup (see [ADR 0004](docs/decisions/0004-skins-not-per-layout-pages.md)) | Mechanism first in IWC, then port. `Index.cshtml` refactor is the bulk — **not** ~1 week |
| 8 | *(no CAT commands)* | 0 new | Voice control v2 improvements | **WAITING** — v1 shipped in pre-release, no tester feedback yet |
