# Shared stylesheets

The same argument as `core/js/`: this is a class library *and* a folder of
browser assets, because roughly half the duplication between IWC and YWC is
in the browser rather than in C#.

## What's here

- `theme-tokens.css` — every value a theme is allowed to change, and
  nothing else. `:root` holds the Classic values, which are the values both
  applications already ship; `[data-theme="instrument"]` overrides them.
  Adding this file cannot change the default appearance of either app,
  which is the first thing to check if a review suspects otherwise.
- `theme-instrument.css` — the Bootstrap layer of the Instrument theme.
  Both applications are Bootstrap 5.3.0 and carve their UI out of the same
  component set, so cards, buttons, forms, tables, dialogs and the focus
  ring are written once. Every rule is scoped to
  `[data-theme="instrument"]`, so without that attribute on `<html>` not
  one selector here matches.

Each application keeps its own `wwwroot/css/theme-instrument-local.css` for
the components named after its own markup — YWC's `.fkb-*`, `.mem-*`,
`.vfo-*` and `.radio-scope-*`, and IWC's equivalents. Same split as the
calibration engine and its tables: core knows the shape, the app knows the
specifics.

## The accessibility contract

Semantic accessibility — `aria-*`, `role=`, `data-a11y-key` — lives in
markup, so a stylesheet cannot reach it and a theme inherits all of it for
free. **Presentational** accessibility is made of CSS and is therefore
exactly what a restyle breaks, silently. Four tokens carry it:

| token | requirement |
|---|---|
| `--rwc-focus-ring` | ≥ 3:1 against whatever it is drawn on (WCAG 1.4.11, 2.4.7) |
| `--rwc-caption` | ≥ 4.5:1 — the smallest text on screen (1.4.3) |
| `--rwc-alarm` | ≥ 4.5:1 — the colour that says something is wrong |
| `--rwc-target-min` | the smallest a hit target may get (2.5.5 / 2.5.8) |

The ratios in `theme-tokens.css` were computed, not judged by eye, and two
of them are there because the original mockups failed: the caption colour
was 4.27:1 at 10–11px, and the alarm colour 4.17:1. Both look completely
fine to an author and neither raises an error anywhere. Change one of the
four and recompute; the comments next to each are what a later reviewer
checks against.

## Why these are copied, not compiled

`RadioWebControl.Core.csproj` excludes `css/**` from MSBuild, exactly as it
excludes `js/**`. These are served to a browser, so each application copies
the ones it uses into its own `wwwroot/css/` at build time.

**A wrong path here fails silently in a browser**, where a wrong C#
namespace fails loudly at compile time — the same hazard `core/js/README.md`
describes, and the reason both apps' targets emit a `.gitignore` naming
what they generated rather than leaving anyone to hand-maintain one.

## How each app consumes these (the copy)

Each app's `.csproj` has a `CopySharedCoreCss` target that copies
`core/css/**/*.css` into its own `wwwroot/css/` early in the build, before
ASP.NET resolves static web assets. It mirrors `CopySharedCoreJs` line for
line, including adding the freshly-copied files to `@(Content)` itself —
the default `wwwroot/**` glob is evaluated *before* the copy runs, so on a
clean checkout the files do not exist yet when the glob is computed, and
without that step the very first build would 404 them.

The copies are **generated output**. Edit the file here; never edit the
`wwwroot` copy, because the next build overwrites it.

## Testing

There is nothing to unit-test in a stylesheet, and both applications verify
UI by hand in a browser. What is worth checking on any change to the
tokens, because it is the part that fails silently:

1. Every ratio quoted in a comment still holds. The figures were produced
   with the WCAG relative-luminance formula; recompute rather than trust a
   colour picker's "looks fine".
2. Tab right through a page in each theme and confirm the focus ring is
   visible on every control, including sliders and anything with a custom
   `outline`.
3. Load with `localStorage` blocked (a private window) — the theme must
   fall back to the server's default and the page must still render.
