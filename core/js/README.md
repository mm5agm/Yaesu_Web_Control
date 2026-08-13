# Shared browser modules

**Empty at Phase 1. Nothing here yet — this folder documents the shape, so the
first JS move does not have to invent it.**

Roughly half the duplicated code between IWC and YWC is browser code, which is
why this repository is a class library *and* a folder of ES modules rather than
two repositories.

## Why these are copied, not compiled

`RadioWebControl.Core.csproj` explicitly excludes `js/**` from MSBuild. These
files are served to a browser, not linked into an assembly, so each application
copies the ones it uses into its own `wwwroot/js/` at build time.

**That means a wrong path here fails silently in a browser**, where a wrong C#
namespace fails loudly at compile time. It is the reason the migration order is
C# before JS: a mistake in the compiled half cannot reach a user.

## Expected first tenants

From the shared-core plan, the JS files already identical or near-identical
across both applications:

- `calibration/calibration-engine.js` — pure functions, no DOM, no side effects.
  Differs between the two repos **only in its header comment**, and is the
  natural first test subject alongside `AdifParser`.
- `guages/meter-gauge.js`, `guages/update-engine.js`, `guages/smeter-history-panel.js`
  (the folder name is misspelt in both applications; leave it alone unless
  deliberately renaming, because it is referenced by path in every importer)
- `websocket/ws-connection.js`, `websocket/ws-update-pipeline.js`
- `ui/calibration-editor.js`, `ui/freq-keyboard.js`

Nothing that knows a frequency came from CI-V or from CAT.
