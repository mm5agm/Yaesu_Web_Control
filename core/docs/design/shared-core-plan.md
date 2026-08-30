# A shared core for YWC and IWC

**Status:** **agreed.** Written 2026-08-13; Phase 0's gate met the same day.
**Affects both repos and both maintainers** — this changes how Fabio works as
well as Colin, which is why it needed agreement before any code moved.

**Fabio agreed by email, 2026-08-13.** The ramp order is right, a shared core
would not disrupt his in-flight work, and he offered to help with any part of
it. His counter-proposal of a single both-brands monolith is **deferred by
mutual agreement, not rejected** — his words: *"that was just food for thought
and for now agree with you, lets look at it in a year or so (or if there's ever
a need for it)"*. He also accepted the Hamlib point, which was what the
monolith's coverage argument rested on: *"most of the things in the ham radio
world are still quite new to me as far as … timelines … thanks for raising that
point which does indeed change the whole point"*. See §5.

So Phase 1 is clear to start.

**Problem:** IWC was cloned from YWC. Most of the code above the radio layer is
still the same code, maintained twice. Every fix to it is either done twice or
silently done once.

---

## 1. How far apart are they actually? — measured, 2026-08-13

Comparing files that exist at the same path in both repos, with the rebrand
normalised away (`Yaesu_Web_Control`→`APP`, `Yaesu`/`Icom`→`RIG`, model names
→`RIGMODEL`) so cosmetic renaming does not count as divergence. Vendored
libraries under `wwwroot/lib/` are excluded — jQuery and Bootstrap are identical
because they are the same packages, not because anyone wrote them twice.

| | first-party files at a shared path |
|---|---|
| **identical** | **36** |
| under 10% changed | 26 |
| 10–50% changed | 24 |
| over 50% changed | 3 |
| **total compared** | **89** |

**62 of 89 — 70% — are effectively the same code.** Only three files are more
than half different, and two of those are exactly the ones you would expect:
`Controllers/CatController.cs` (52%) and `Services/RadioCapabilities.cs` (58%).

Raw method: `git ls-files '*.cs' '*.js'` in each repo, intersect the paths,
normalise, diff, bucket by percentage of lines changed. Worth re-running before
acting on this — it is a snapshot, and YWC is moving this week.

## 2. The boundary draws itself

The 36 identical files are not a random scatter. Every one of them is code that
**never touches the radio** — so every one is eligible for `core/`, and Phase 3
(see §7) moves them **on touch**: when you next edit one for its own reason, move
it into `core/` as part of that same, already-tested change. Do **not** batch
these.

This is the live checklist — the single source of truth for what's left. Tick a
box when a file lands in core; don't scatter "move me" markers across the files
themselves. **✅ = moved · ☐ = still local, move on next touch.**

**Controllers/**
- [ ] `DxClusterController`
- [ ] `MemoryBankController`

**Models/**
- [x] `DxSpot` — Phase 1
- [ ] `VoicePackMetadata`
- [ ] `Calibration/CalibrationFile`
- [ ] `Calibration/MeterCalibration`

**Pages/**
- [ ] `Calibrations`
- [ ] `Diagnostics`
- [ ] `Error`
- [ ] `Labels`
- [ ] `Memories`
- [ ] `UserManual`
- [ ] `Calibration/MeterCalibration`
- [ ] `Calibration/SMeterCalibration`
- [ ] `Shared/_BandButtonsPartial`

**Services/**
- [x] `AdifParser` — Phase 2
- [ ] `AppMemory`
- [ ] `AppStatus`
- [ ] `DxClusterService`
- [ ] `MemoryService`
- [ ] `MemoryBankService`
- [ ] `ProcessStatusCacheService`
- [ ] `RadioStatePersistenceService`
- [ ] `SystemTrayService`
- [ ] `IRadioStateService`
- [ ] `ISettingsService`
- [ ] `Voice/AudioOutput`
- [ ] `Voice/VoiceTtsService`

**wwwroot/js/**
- [x] `calibration/calibration-engine` — Phase 2
- [ ] `guages/meter-gauge`
- [ ] `guages/smeter-history-panel`
- [ ] `guages/update-engine`
- [ ] `ui/calibration-editor`
- [ ] `ui/freq-keyboard`
- [ ] `websocket/ws-connection`
- [ ] `websocket/ws-update-pipeline`

DX cluster, memories, ADIF, calibration maths, the meter gauges, the SignalR
transport, the tray icon, text-to-speech. **That is the shared core, and it did
not need designing — the measurement found it.**

> These 36 all sit *above* the radio seam. The other ~24 "middling" files that
> *do* touch the radio are **not** on this list — they are gated behind Phase 5
> (the `IRadioController` back-port, §3), and must not be moved on touch until
> that lands.

## 3. The blocker: YWC has no radio seam

IWC's carve introduced `Services/IRadioController.cs` — everything above it
speaks frequencies, modes and S-units, and nothing above it knows the wire
protocol. **YWC never had that seam.**

The consequence is a clean split in what is achievable:

- **Above the seam** — the §2 list — can be shared **now**. It already is,
  informally; the only change is where the file lives.
- **Touching the radio** — the 24 files in the 10–50% band, most of `site.js`,
  `Index.cshtml`, `Program.cs` — **cannot be shared until YWC grows the same
  seam.**

Back-porting `IRadioController` into YWC is therefore the price of the second
half. It is a substantial job, but it has independent value: it is what let IWC
swap its whole CAT layer without touching the UI, and it is what would let YWC
add a second radio model without forking again.

**Do not treat that back-port as a prerequisite for starting.** It gates the
second half only.

### 3a. When the seam question actually arrives — and who has to be in it

Raised 2026-08-15, after a no-hardware **stub radio** for YWC was proposed.
Answered 2026-08-30 — see "What Fabio said" below, which supersedes the
plan this section originally carried.

IWC can run its whole UI with no radio attached (`IWC_USE_STUB_RADIO=1` swaps a
canned `StubRadioController` in behind the seam). That has already paid for
itself — the Firefox meter-needle bug was reproduced *and* its fix verified
entirely under the stub, no radio involved — and its absence in YWC has a cost
on record: the static-asset caching fix (YWC PR #110) is a pure HTTP-layer
change that still could not be runtime-verified, because starting YWC opens the
CAT port to the radio.

**The stub is the wrong unit to argue about.** A canned controller is shaped to
one radio's capabilities and has no second copy to agree with, so it fails §4's
"only move what already agrees" rule outright. The unit is the seam — and the
decision point arrives the **moment** stub work starts, because you cannot build
the stub without building a seam.

What made that a question rather than a task is that the two seams are not the
same shape. IWC's is built around a **single-receiver IC-7300**; YWC spans the
**dual-receiver FTdx101**, the FTdx10 and the FT-710. Deciding which behaviour
wins is not a copy. And it points in the same direction as the monorepo proposal
in §5 — smaller, but adjacent to something parked by agreement — so it went to
Fabio rather than being started unilaterally.

#### What Fabio said (2026-08-30)

**Not yet, for anything landing in `core`.** The order is:

1. a **YWC-local** seam first;
2. a thin **semantic** API — verbs, not wire traffic;
3. CAT behind it;
4. the stub as one implementation of it;
5. used in YWC until it has paid for itself.

So building locally first is the agreed order. It is not the expensive detour
this section originally called it, and there is no plan here to promote the
seam to `core` once the two contracts look alike.

What is open is only the **shape of a later question**. If a radio type is ever
shared, it is only the verbs both apps already treat as generic — frequency,
mode, PTT, and whatever else genuinely turns out to be common **once two real
seams exist to compare**. Brand quirks stay in each app. `core` still does not
know what a roofing filter or a waterfall is.

That narrow slice is the only candidate worth comparing, judged on evidence from
two working seams. It is not one seam spanning both radios, and it is not
something to start now.

*On doing without a stub in the meantime: for anything in the HTTP/static layer
you do not need a fake radio, only a booting app. Starting YWC against a
non-existent COM port does serve its pages, showing disconnected — enough to
check response headers and rendered markup. That was an untested guess when it
was written here; Fabio has since tested it. But — his point, and the reason it
belongs in this paragraph rather than replacing the section above — **a missing
COM port is not a substitute for a stub.** A dead port gives you a disconnected
app. Anything that needs live frequency, mode or meter behaviour, the Firefox
meter-needle bug being exactly that, still needs something answering behind the
seam.*

## 4. What to do first — and it is not extracting all 36 at once

A big-bang extraction across two live applications with installers going out is
how both break at once, with no tests to catch it (§6).

**Start with Colin's own already-duplicated code, smallest first.** There is
plenty of it — the whole §2 list — and it has three advantages over anything
else as a first tenant: it is already written and already duplicated, so moving
it is pure gain; it needs nobody else's agreement; and a mistake affects only
code that both apps have been running unchanged for months.

A sensible ramp:

1. `Models/DxSpot.cs` — trivial, no dependencies, proves the plumbing end to end.
2. `Services/AdifParser.cs` and `wwwroot/js/calibration/calibration-engine.js` —
   pure functions, no DOM, no radio, and the natural home for the first tests.
3. `Services/MemoryService.cs`, `Services/MemoryBankService.cs`,
   `Services/DxClusterService.cs` — real services with real behaviour.

**Then migrate the rest opportunistically, not in a batch.** The rule: the next
time you would have edited one of them in both repos, move it to the shared
library instead. The work gets paid for by the change that needed doing anyway.

### Fabio's audio layer — a candidate, but deliberately not first

`feature/add-audio-rx-tx` in YWC (4,148 lines, 32 files, 2026-08-10) is a
natural fit on the merits: almost entirely new files under `Services/Audio/`
(device enumeration, Opus codec, session management, wire protocol, HTTPS
certificates) and `wwwroot/js/audio/`, none of which knows what a radio is. The
only radio-specific part is ~21 lines in `CatController.cs`, presumably the PTT
hook, which in IWC would go through the seam instead. IWC wants it, because
remote audio is the gap that stops IWC being usable for real remote operation.

**But it should not be the first thing moved.** It is unmerged and actively
changing, it is someone else's work in flight, and making it the pilot would
mean the person bearing the disruption is the one volunteer on the project.
Prove the mechanism on code that is already ours and already duplicated, then
offer the audio layer a home once there is something working to offer. Whether
it moves at all is Fabio's call, not a consequence of this plan.

## 5. Mechanism

Four options, and the weights are not close for a project this size:

| | verdict |
|---|---|
| **git subtree** from a third repo | **recommended** — one command to pull updates, no daily friction, works with the existing NSIS/self-contained publish |
| git submodule | same idea, but it will annoy you every single day; contributors forget to init |
| NuGet package | correct for a library with strangers as consumers; heavy for two apps and one-and-a-half maintainers, and painful during rapid co-development |
| monorepo | technically cleanest, but merges two release processes, two installers and two GitHub identities. Too big a move to make on this evidence |

**On the monorepo row, for the record.** Fabio proposed the stronger version of
it — one app covering Icom and Yaesu now, Kenwood and Xiegu later, brand-scoped
endpoints and per-brand UI panels, with Hamlib filling the gaps. It is a
coherent design and it is not dismissed here; it is **parked for about a year,
by agreement on both sides**. Two things argue against doing it now and neither
is about the design: his own roadmap answer is that **no big features remain
after audio and video**, so there is little future work for a migration that
size to amortise over; and the monolith is paid up front and is not reversible,
where the shared core is incremental and is. If the picture changes — a third
brand with a real user base, or the roadmap refilling — reopen it then.

So: a third repository — working name **Radio Web Control Core** — consumed as a
subtree in both. It ships as a .NET class library plus a folder of JS modules,
because the shared code is roughly half C# and half browser code and splitting
those into two repos would double the ceremony for no gain.

## 6. Costs, honestly

- **Neither project has automated tests.** Today a mistake breaks one app;
  afterwards it breaks two, and you find out from a user. **If anything ever
  justifies writing the first tests in either project, it is this library** —
  `AdifParser` and `calibration-engine` are pure functions and are the easiest
  possible place to start.
- **Release coupling.** A shared fix means two installers and two release runs.
  `finish-release.ps1` already checks five version sites; a shared library adds
  another thing to keep in step.
- **Loss of freedom to hack.** IWC has been able to delete Yaesu code freely
  during the carve precisely because nobody else depended on it. Sharing
  reintroduces "don't break the other app" as a constraint on every change.
- **Divergence has a cost too, and it is already being paid** — that is the
  point of §1. This is a choice between two costs, not between a cost and free.

Licensing is a non-issue: both repos are GPL-3.0 and Colin holds the project
lead on both.

## 7. Order

| Phase | Work | Gate |
|---|---|---|
| **0** ✅ | Agree the idea with Fabio; agree the repo name and that new shared work lands there | **met 2026-08-13** — agreed by email; repo named **`Radio_Web_Control_Core`** |
| **1** ✅ | Create the core repo; subtree it into both; move **one** trivial file (`Models/DxSpot.cs`) end to end | **met 2026-08-13** — see below |
| **2** | Move `AdifParser` + `calibration-engine`, and write the first tests against them there | something fails when deliberately broken |
| **3** | Migrate the remaining §2 files opportunistically, on touch | no batch, no deadline |
| **3a** | *If Fabio wants it:* offer the audio layer a home in the core | his call, not a dependency |
| **5** | Back-port `IRadioController` into YWC | unlocks the 24 middling files; own plan, own note |

### Phase 1, as built — 2026-08-13

**[`mm5agm/Radio_Web_Control_Core`](https://github.com/mm5agm/Radio_Web_Control_Core)**,
public, GPL-3.0, consumed at `core/` in both applications. IWC PR #29, YWC PR #99.

`Models/DxSpot.cs` moved and nothing else. `Services/DxClusterService.cs` turned
out to be the **only** C# file in either repository that used the type — every
other "DxSpot" match is the unrelated `DxSpotAgeMinutes` setting or browser code.

Four things worth not rediscovering:

- **`<Compile Remove="core\**" />` is mandatory in both `.csproj` files.** Both
  apps use the Web SDK, which globs `**/*.cs`, so without it every file in the
  subtree compiles twice — once into `RadioWebControl.Core.dll` and once
  straight into the application — and the error names duplicate types rather
  than the cause. `Content`, `None` and `EmbeddedResource` are removed alongside
  it, matching how YWC already excludes `Workers/`.
- **The core targets `net10.0`, not `net10.0-windows`.** YWC multi-targets for
  its macOS/Linux CAT-only host; a Windows-only core would have built fine
  against IWC and broken YWC's second TFM silently. YWC's `ProjectReference` is
  deliberately *not* conditioned on `TargetFramework` for the same reason.
- **Neither `installer.nsi` needed changing.** Both take the publish folder
  wholesale (`File /r "publish\*"`), so a new DLL is picked up on its own.
- **The core repo's `.gitignore` covers `core/bin` and `core/obj` inside both
  applications**, because it comes along with the subtree.

Verified at build time: IWC builds and publishes self-contained win-x64; YWC
builds **both** TFMs and publishes with the exact CI command;
`RadioWebControl.Core.dll` lands beside each `.exe`; and fresh clones of both
branches straight from GitHub build clean with `core/` populated, which is the
whole argument for subtree over submodule.

Verified at **run** time, which is the check that matters — a compile proves the
reference resolves, not that the assembly loads. Both applications were run from
their phase-1 branches and their DX cluster reached `connected` and returned
populated spot arrays. Those objects **are** `DxSpot` serialised out of the
shared assembly, so a failed assembly load could not have produced them; it
would have thrown `FileNotFoundException: Could not load file or assembly
'RadioWebControl.Core'` at startup instead. IWC was additionally run against a
live IC-7300 MkII on COM8 — radio identified, VFO A/B, meters and modes all
reading normally, zero `ERR`/`FTL` log entries on the day.

**Not** verified: an install over an existing install (deferred to the next real
release, where it happens anyway) and the macOS/Linux host on real macOS or
Linux — that one is Fabio's, and is asked for in YWC PR #99.

---

Related: [`lan-civ-transport-plan.md`](lan-civ-transport-plan.md) — that one is
IWC-only and below the seam, so it is unaffected by any of this.
