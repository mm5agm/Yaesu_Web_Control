# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

> ## START HERE
>
> **Radio_Web_Control_Core** is the shared, radio-agnostic half of
> [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) and
> [Icom Web Control](https://github.com/mm5agm/Icom_Web_Control). Both consume
> it as a **git subtree at `core/`**.
>
> **Read this before editing anything here.** This clone is almost certainly
> not where you should be making the change.

---

## Where are you reading this?

This file travels with the code, so it turns up in two places and the advice
differs.

**At `<app>/core/CLAUDE.md`** - inside Yaesu_Web_Control or Icom_Web_Control.
This is the right place to work. Edit here, build and test against the app,
commit in the app repo, then push the subtree up (see below).

**At the top of a standalone Radio_Web_Control_Core clone.** Do not author
feature changes here. Committing in this clone does not reach either app until
someone pulls, and it is easy to produce a change that has never been compiled
against a real consumer. Use this clone for reading the code, running the tests
standalone, and repo-level files that no app needs - `README.md`, `LICENSE`,
`docs/`, `.gitignore`, this file.

## Getting a change into both apps

**Work inside `core/` in whichever app repo needs the change**, then push:

```powershell
# in Yaesu_Web_Control or Icom_Web_Control
./scripts/core-sync.ps1 -Check
./scripts/core-sync.ps1 -Push
./scripts/core-sync.ps1 -Pull   # then in the sibling app
```

That way the change is built and tested against a consumer before it lands in
the core, and both apps end up carrying it. The app repos' own CLAUDE.md files
carry the same rule from their side, including that the push is not optional.

## What belongs here

The seam is **the radio**. If a radio's wire protocol, register layout, filter
codes or calibration numbers appear in it, it does not belong here.

| belongs | does not |
|---|---|
| Signal processing, decoders, DSP | CAT / CI-V framing and addressing |
| Interchange models (ADIF, DX spots) | Per-radio lookup tables |
| Pure algorithms | Anything touching an app's DI, hubs or Razor pages |
| Browser modules that only talk to an HTTP API | Anything reading an app's own settings |

`Services/Cw/CwDecoderEngine` takes samples and a pitch, so it is here.
`YaesuIfWidth` maps a Yaesu SH code to Hz, so it is not - and Icom's widths are
a formula rather than a table, which is why it never could have been.

## Constraints that are easy to break

- **Target `net10.0`, never `net10.0-windows`.** YWC multi-targets so it can
  run CAT-only on macOS and Linux. A Windows-only dependency here breaks that
  host, and it will not show up in a Windows build.
- **No `Microsoft.AspNetCore.*`, no DI, no hosting.** Consumers wire this up;
  it does not wire itself.
- **Line endings are LF.** There is no `.gitattributes` here, and both apps
  have `* text=auto`, so their commits normalise to LF on the way in. Do not
  introduce CRLF.
- **`js/` is copied, not linked.** Each app's `CopySharedCoreJs` target copies
  `js/**/*.js` into its `wwwroot/js/` preserving the subdirectory, and writes
  the `.gitignore` for the copies. Edit here; never edit a `wwwroot` copy.

- **Moving a file *into* `js/` can silently delete someone's work.** Once it
  moves, the old `wwwroot/js/...` path becomes a generated, gitignored build
  artefact. Any PR still open against that path then merges as *modify/delete*,
  and resolving it as a delete - the tempting reading, since the path is
  generated now - drops that PR's change with no conflict marker, no build
  error and nothing in the diff to notice. Before moving a file here, check
  `gh pr list` for open PRs touching it, and if there are any, fold their
  changes into the core copy first and say so in the commit. This happened on
  2026-08-27 with `audio-playback.js` and PR #112.

## Build & test

```powershell
dotnet build -c Release
dotnet test tests/RadioWebControl.Core.Tests/RadioWebControl.Core.Tests.csproj -c Release
```

The test project must pass **standalone in this repo**, not only inside an app.
That is what catches an accidental dependency on a consumer.

## Plans

`docs/design/shared-core-plan.md` is the migration plan and phase table. Keep it
current when something moves in or out.
