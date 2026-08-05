# Single-receiver VFO routing fixes: memory recall, roofing filter, A/B re-query + full state replay for late-joining clients

## Summary

On single-receiver radios (FTdx10, FT-710, FTDX3000, FT-991A), VFO A and B are frequency/mode slots for one receiver; the operating VFO is tracked by `ActiveVfo` (from the `VS` CAT command). Several code paths still ignored that and hard-wired VFO A. This branch fixes them, plus a related state-sync bug for browsers that connect after startup.

### Memory recall tunes the active VFO (`Controllers/MemoryController.cs`)

- Recall (`POST /api/memory/{id}/recall`) was hard-coded to VFO A — always sending `FA`/`MD0` and writing only `*A` state fields. Recalling a memory while VFO B was active updated the wrong slot, so the receiver did not move.
- `Recall` now uses `RadioCapabilities.VfoIsB` — the same routing helper used throughout `CatController` — to target the active VFO. Mode and frequency commands switch between `MD0`/`FA` and `MD1`/`FB`, and every per-VFO state write (clarifier, antenna, IF width/shift, roofing, NB, NR, AGC) routes to the matching A or B property. Dual-receiver behaviour is unchanged (recall still targets VFO A).

### Roofing filter is per-VFO state on single-receiver radios (`CatController`, `CatMessageDispatcher`, `RadioInitializationService`)

- The FTdx10/FTDX3000 roofing setters and the `RF` read-back handler used to mirror the filter code to both `RoofingFilterA` and `RoofingFilterB`. But the radio stores the roofing filter per VFO slot, so mirroring corrupted the inactive slot's value.
- Setters now write only the active VFO's slot, and the dispatcher's `RF` case routes through `SetPerVfo` like the other P1=0-Fixed receive controls (including the FTDX3000 read-code normalisation). `RF0;` was added to the init per-VFO query list so both slots are populated at connect.

### `SetMode` consolidation (`CatController`)

- Refactored to build the CAT command from `VfoP1Outgoing` and route state writes via `VfoIsB` instead of duplicated A/B branches — same fix pattern as the other receive-control endpoints, and the Contour/APF re-apply after a mode change now reads the correct per-VFO state.

### Front-panel A/B press re-syncs per-VFO controls (`CatMessageDispatcher`, `CatMultiplexerService`, `CatCommands`)

- Pressing A/B on the rig fires a burst of P1=0-Fixed auto-info broadcasts racing the `VS` answer. The 300 ms dispatch buffer previously applied any queued update to whichever `ActiveVfo` was current at flush time — updates spanning the VS boundary could land in the wrong slot. Buffered updates whose `ActiveVfo` changed between enqueue and flush are now dropped as ambiguous.
- Ground truth is restored by a new debounced post-VS re-query: 500 ms after a `VS` change (cancel-and-restart on rapid flips), the multiplexer re-reads the full per-VFO control set for the newly active VFO. The query list is now shared as `CatCommands.SingleReceiverPerVfoQueries` between init ping-pong and the re-query. `MD` read-back also routes via `SetPerVfo` now.
- AF Gain persisted-state restore at init is skipped on single-receiver radios — the radio is the source of truth there (same precedent as Mode #38, RF Power #35, MIC Gain #16); the `AG0;` queries populate the UI instead.

### Late-joining browsers get the full radio state (`RadioHub`, `RadioStateService`)

- SignalR broadcasts only fire on *change*, and `OnConnectedAsync` replayed only `FrequencyA`/`FrequencyB`. A browser connecting after startup (second tab, another computer) kept the frontend JS defaults for anything not server-rendered in the Razor page — most visibly `ActiveVfo`/`TxVfo`/`SplitMode`, which made VFO A always look active on late-joining clients.
- `RadioStateService.GetClientStateSnapshot()` returns every UI-relevant property (meters excluded), and the hub replays it to the connecting client through the normal `RadioStateUpdate` envelope. No frontend changes needed; voice announcements are unaffected (initial-load suppression already swallows first values).

## Test plan

- [x] On FTdx10: with VFO B active, recall a memory — VFO B retunes and the UI updates
- [x] On FTdx10: with VFO A active, recall a memory — VFO A retunes (regression)
- [ ] On FTdx10: press A/B on the rig — after ~0.5 s all per-VFO controls (mode, roofing, AGC, IF width/shift, NB/NR, contour/APF, AF/RF gain, squelch) show the newly active VFO's values
- [ ] On FTdx10: change the roofing filter with VFO B active, press A/B — each panel keeps its own filter (no mirroring)
- [ ] On FTdx10: with VFO B active, open the UI in a second browser/computer — VFO B shows as active; split/TX state also correct
- [ ] On FTdx101 (dual receiver): memory recall still targets VFO A; mode/roofing per-VFO commands unchanged; second-browser state still correct
- [ ] `dotnet build Yaesu_Web_Control.csproj` succeeds
