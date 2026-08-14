# Shared browser modules

Roughly half the duplicated code between IWC and YWC is browser code, which is
why this repository is a class library *and* a folder of ES modules rather than
two repositories.

## What's here

- `calibration/calibration-engine.js` — pure meter-calibration functions, no
  DOM, no side effects. Phase 2's first JS tenant, tested in
  `tests/js/calibration-engine.test.mjs`.

The per-radio numbers it works on are **not** here: each app keeps its own
`wwwroot/js/calibration/calibration-tables.js`, which the engine imports as a
sibling. That is the whole reason the engine is shareable — it knows the shape
of a calibration table, never the values in one.

## Why these are copied, not compiled

`RadioWebControl.Core.csproj` explicitly excludes `js/**` from MSBuild. These
files are served to a browser, not linked into an assembly, so each application
copies the ones it uses into its own `wwwroot/js/` at build time.

**That means a wrong path here fails silently in a browser**, where a wrong C#
namespace fails loudly at compile time. It is the reason the migration order is
C# before JS: a mistake in the compiled half cannot reach a user.

## How each app consumes these (the copy)

Each app's `.csproj` has a `CopySharedCoreJs` target that copies
`core/js/**/*.js` into its own `wwwroot/js/` early in the build, before ASP.NET
resolves static web assets. The copied files are **generated output** — each app
git-ignores `wwwroot/js/calibration/calibration-engine.js` and its siblings, so
this folder is the single source of truth. Edit the file here; never edit a
`wwwroot` copy, because the next build overwrites it.

The default `wwwroot/**` content glob is evaluated *before* the copy runs, so on
a clean checkout the file does not exist yet when the glob is computed. The
target therefore also adds the freshly-copied files to `@(Content)` itself
(excluding any the glob already found on a later build), so the very first build
serves them correctly rather than 404-ing until the second build.

## Tests

`calibration-engine.js` is exercised by Node's built-in test runner (no npm
dependencies, matching the library's no-dependencies rule). From the core repo
root:

```
node --test tests/js/calibration-engine.test.mjs
```

The C# tenants are tested with xUnit under `tests/RadioWebControl.Core.Tests`
(`dotnet test`).

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
