# Shared Voice Core — keeping YWC and IWC voice work in sync

**Status:** scoping only (no code moved yet). Written 2026-07-31 after porting the
en-GB grammar-compile fix + full-Hz tuning + v7 synonyms from IWC into YWC by hand —
which is exactly the recurring cost this plan removes.

**Question this answers:** IWC was cloned from YWC, so the voice subsystem exists as
two near-identical copies that drift. Every voice-infra fix currently has to be applied
twice (and one direction gets forgotten). Can the shared part live in one place?

**Short answer:** Yes, and the boundary is natural — **the only file that genuinely
*should* differ between the two apps is `IntentDispatcher` (Yaesu CAT vs Icom CI-V).
Everything else in the voice stack is protocol-agnostic and can be shared.** That
"everything else" is precisely what drifted this session.

---

## Evidence: how much actually differs today

`git diff --no-index` between the two repos' voice files (2026-07-31):

| File | YWC↔IWC diff | What the diff actually is |
|---|---|---|
| `Services/Voice/AudioOutput.cs` | +1 / −1 | namespace only |
| `Services/Voice/VoiceStatus.cs` | +1 / −1 | namespace only |
| `Services/Voice/VoiceTtsService.cs` | +1 / −1 | namespace only |
| `Models/VoicePackMetadata.cs` | +1 / −1 | namespace only |
| `Models/VoicePhrasesConfig.cs` | +2 / −2 | namespace only |
| `Services/Voice/VoicePhraseValidator.cs` | +2 / −2 | namespace only |
| `Services/Voice/VoiceHelpBuilder.cs` | +4 / −3 | namespace + a comment |
| `Services/Voice/VoiceGrammar.cs` | +12 / −12 | namespace + branding in comments |
| `Services/Voice/MicrophoneCapture.cs` | +8 / −24 | namespace + comment trims |
| `Services/Voice/VoicePhraseStore.cs` | +17 / −26 | namespace + `BuildDefaults()` vocab + hard-coded app-folder path |
| `Services/Voice/VoiceControlService.cs` | +33 / −18 | namespace + branding in comments + **IWC-only audio diagnostics YWC never got** |
| `Controllers/VoiceController.cs` | +131 / −131 | namespace + app-folder path + pack prefix (`YWC`/`IWC`) + branding — **no radio logic** |
| `wwwroot/js/ui/voice-control.js` | +8 / −12 | namespace/branding |
| `Services/Voice/IntentDispatcher.cs` | **+187 / −112** | **the real divergence — CAT string building vs CI-V** |

Two things this proves:

1. **13 of 14 files differ only by namespace/branding/comments.** The big-looking
   `VoiceController` +131 is entirely `"Yaesu Web Control"` vs `"Icom Web Control"`
   in `%APPDATA%` paths, the `YWC`/`IWC` pack-filename prefix, and comment text —
   zero radio logic.
2. **The drift is already asymmetric.** `VoiceControlService` shows IWC has audio-path
   diagnostics (`SpeechDetected` / `AudioSignalProblemOccurred` / `AudioStateChanged`
   logging) that YWC simply never received. That's the sync gap made visible.

`VCTuneRecognizer.cs` exists only in YWC — IWC dropped the legacy VC-Tune subsystem
per `iwc-clone-split-plan.md`. It is not part of the shared surface.

---

## What moves to core vs stays per-app

### CORE — `RadioVoiceCore` (shared, neutral namespace, radio-agnostic)

- `VoiceGrammar` — grammar build + SRGS generation (incl. the en-GB CFG-compile fix)
- `VoiceControlService` — SAPI recognition engine, `ParseFractionalHzFromText`, intent normalisation
- `VoicePhraseStore` — **the persistence/versioning/history/migration mechanism only** (not `BuildDefaults`)
- `VoiceHelpBuilder`, `VoicePhraseValidator`, `VoiceStatus`
- `VoiceTtsService`, `AudioOutput`, `MicrophoneCapture`
- `VoiceController` — the HTTP surface (start/stop/status/phrases/help/export/import/history)
- Models: `VoicePhrasesConfig`, `VoicePackMetadata`
- Frontend: `wwwroot/js/ui/voice-control.js` + the help dialog markup/CSS

### PER-APP — stays in each repo (this is correct, not a compromise)

- **`IntentDispatcher`** — maps recognised intents to the wire protocol. Yaesu builds
  `FA…;` / `TX1;` / `GT…;`; Icom emits CI-V. This *should* differ; do not unify it.
- **`BuildDefaults()`** — the default phrase pack (modes, bands, macros, macro CAT
  strings). Yaesu mode names (`CW-U`, `DATA-U`), FTdx101 macros (`NR01;`, `RF03;`),
  4 m band coverage — all radio-specific data.
- **Radio-specific constants** — voice frequency guard bounds (Yaesu 30 kHz–75 MHz;
  IC-7300 0.03–74.8 MHz), band→default-frequency map.

### THE SEAMS (three small injected dependencies)

1. **`IVoiceRadio`** — a *minimal* semantic interface (~12 methods) that
   `IntentDispatcher` needs: get/set frequency per VFO, set mode per VFO, swap/select
   VFO, band step, set transmit, set split, set AF gain / attenuator / preamp / AGC,
   get/set IF width, and a `SendRawAsync(string)` escape hatch for macros.
   **Note:** this is intentionally *not* IWC's existing `IRadioController` — that one
   has grown to ~50 Icom-specific methods (ATU, CW keyer, PBT, scope span, power) and
   is the whole-app seam. `IVoiceRadio` is just the slice voice needs; each app's full
   controller implements it.
2. **`IDefaultPhraseProvider`** — supplies `BuildDefaults()` content per radio.
3. **`IVoiceAppInfo`** — app display name (`"Yaesu Web Control"`), `%APPDATA%` sub-folder,
   and pack-filename prefix (`YWC`/`IWC`). Kills every branding/path diff in
   `VoiceController` and `VoicePhraseStore` in one shot.

---

## Delivery mechanism

**Recommended: git subtree of a small `RadioVoiceCore` repo into both apps.**

- One repo `RadioVoiceCore` = a `net10.0-windows` class library (needs WinForms +
  `System.Speech` framework refs; both apps already carry these) + the shared JS.
- Pulled into `YWC/RadioVoiceCore/` and `IWC/RadioVoiceCore/` via `git subtree`.
- Fix flow: commit to core → `git subtree push` → `git subtree pull` in the other app.
- Source-level (step-through debuggable), no NuGet publish pipeline — right weight for
  a solo dev. A private GitHub Packages NuGet is the alternative if you'd rather version
  it formally later; the code split is identical either way.

The frontend `voice-control.js` rides the same subtree (a `wwwroot/js/voice/` folder)
so JS and C# stay versioned together.

---

## Phased plan + effort

**Phase 1 — the identical files (≈1 day, ~90% of the value).**
Create `RadioVoiceCore` with a neutral namespace. Move the 13 agnostic files. Introduce
`IVoiceAppInfo` so `VoiceController`/`VoicePhraseStore` stop hard-coding the app name and
pack prefix. Wire the subtree into both repos. **After this, the en-GB fix, full-Hz
parse, phrase versioning, help builder, TTS, audio, mic capture, and the whole HTTP
surface are fixed once.** This alone would have made today's port a one-repo change.

**Phase 2 — the store split (≈0.5 day).**
Extract `IDefaultPhraseProvider`; each app keeps its own `BuildDefaults()` as a small
provider class. `VoicePhraseStore` mechanism goes fully to core.

**Phase 3 — `IntentDispatcher` behind the seam (OPTIONAL, several days, low payoff).**
To move `IntentDispatcher` to core it must call `IVoiceRadio` instead of building wire
strings, *and* core must abstract its other dependencies (`RadioStateService`,
`ISettingsService`, SignalR hub). That's a real surface. **Recommendation: don't.** The
dispatcher is the one thing that legitimately differs per radio; sharing it buys little
and costs the most. Revisit only if a *third* radio app appears and the intent→semantic
mapping is provably identical across all three.

---

## Risks / gotchas

- **`net10.0-windows` + `System.Speech` + STA.** The library must carry the WinForms
  framework ref and `System.Speech`; recognition needs an STA thread (both apps already
  satisfy this). Verified via the throwaway grammar probe used for the en-GB fix.
- **`%APPDATA%` path is currently hard-coded** in `VoicePhraseStore`
  (`MM5AGM\Yaesu Web Control\Grammars`). It must become `IVoiceAppInfo`-driven, and each
  app must keep pointing at its *own* existing folder so installed packs aren't orphaned.
- **Namespace flip is a one-time churn** across ~13 files in each repo; after it, the
  cross-repo diff for shared files is empty and stays empty.
- **IWC currently leads on a few things** (audio diagnostics). The first extraction should
  take IWC's newer version of `VoiceControlService` as the base so YWC *gains* those,
  not loses them.
- **IWC's `IntentDispatcher` still carries Yaesu 30 kHz–75 MHz guard bounds** (unadapted
  from the clone). Independent of this plan, but the per-app constant belongs in each
  app's dispatcher/profile and IWC's should be corrected to the IC-7300 range.

---

## Recommendation

Do **Phase 1 + Phase 2**. They remove essentially all the real drift for ~1.5 days of
work and a one-time namespace churn. Skip Phase 3 — `IntentDispatcher` differing is the
system working as intended, not a duplication to eliminate.

Until/unless this lands, the interim discipline that keeps manual porting cheap: **keep
shared-voice-infra changes in commits that touch only the agnostic files, never mixed
with radio-specific edits** — then a cross-repo `git cherry-pick` is a clean apply modulo
the namespace line. (Today's two commits already followed this: the grammar/synonym fix
and the TX-opcode fix were separate.)
