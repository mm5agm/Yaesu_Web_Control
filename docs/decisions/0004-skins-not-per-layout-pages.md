# ADR 0004 — Alternative layouts are skins (one markup, CSS only), not separate Razor pages

**Status:** ACCEPTED — 2026-08-06
**Decision-makers:** Colin (MM5AGM)
**Driven by:** [Issue #48](https://github.com/mm5agm/Yaesu_Web_Control/issues/48) (Jacek SP3L) — re-arrangement of the controls in the YWC GUI; and [PR #93](https://github.com/mm5agm/Yaesu_Web_Control/pull/93) (Fabio Valente) — UI experimentation
**Supersedes:** the layout mechanism agreed in `Plan.md` Phase 7 ("SP3L Alternative GUI Layout")

---

## Context

Two separate people have now asked for a different arrangement of the YWC home page.

**Jacek SP3L** opened issue #48 in June 2026. The design agreed with him — recorded in
`Plan.md` Phase 7 — was:

> A second Razor page (e.g. `IndexSp3l.cshtml`) plus a companion CSS file. All existing
> JS and C# control logic reused as-is — no new backend work. User picks the layout in
> **Settings** via a new "GUI Layout" dropdown. Options: *Classic* (current) / *SP3L*.

A `feature/jacek-gui` branch was created carrying the Settings-dropdown skeleton. The
phase then **parked**, blocked on one unanswered question — *"where would the SDR
spectrum displays go?"* — which Jacek did not come back on.

**Fabio Valente** then, in August 2026 and without knowing Phase 7 existed, built
`Pages/UiV2.cshtml` (1,318 lines) plus `wwwroot/css/ui-v2.css` (805 lines) as a draft in
PR #93 — independently arriving at **exactly the agreed mechanism**: a second Razor page,
a companion stylesheet, and a Settings switch. His screenshots also answer Jacek's
spectrum-placement question, which unblocks the phase.

So the second-page approach is not a contributor's misstep. It is what this project had
already committed to, and two people reached it independently. It is being changed
anyway, for one reason.

## The problem with the agreed design

Two layouts is tolerable. Three is not.

With Classic, SP3L and Fabio's layout each being its own Razor page, **every new control
must be added three times, every bug fixed three times, and every `data-a11y-key`
accessibility attribute written three times.** They then drift apart, because in practice
one of the three gets updated and the others do not. YWC is maintained by one person.

ADR 0003 already predicted this cost, in its Consequences section, when it split the home
page by receiver class:

> **Negative:** Two distinct layouts to maintain. Future per-control changes need to be
> applied to both unless we factor common pieces into shared partials.

That warning is now being acted on rather than repeated a third time.

There is a specific and non-negotiable version of the problem. Voice control and
screen-reader labelling exist in these apps for **partially-sighted operators**;
regressions in them are release blockers. Labels bind strictly on `data-a11y-key`
attributes in the markup. N copies of the markup means N chances to omit the attribute,
and the failure is silent — the control simply stops being reachable by voice or by a
screen reader, with nothing to indicate it.

## Decision

**Alternative layouts are implemented as *skins*. A skin is CSS.**

1. **One set of markup, shared by every skin.** Every control is declared exactly once,
   in one place. There is no per-skin HTML.

2. **A skin repositions; it does not re-declare.** Each control group is a named CSS grid
   area, declared once in the shared markup. A skin is a stylesheet supplying its own
   `grid-template-areas`, plus token values for colour and typography.

3. **The control manifest is visibility, expressed in CSS.** Groups a skin does not want
   are hidden by that skin's stylesheet. They are **not** conditionally un-rendered in
   Razor, because conditional rendering fragments the markup again by another route.

4. **There is no per-skin Razor partial, and this is absolute.** The IWC Phase 7 sketch
   from which this design is drawn offered *"a CSS-grid `grid-template` **and/or** a
   per-skin Razor partial"*. **The Razor-partial half of that is deleted.** It is the
   loophole that reintroduces the exact N-way duplication this ADR exists to prevent.
   Any future proposal to add one is a proposal to abandon this decision, and should be
   treated as such.

5. **The current `Index.cshtml` arrangement becomes the default skin.** It is not
   privileged; it is skin zero.

6. **The user-facing surface is unchanged from what Jacek was promised** — a "GUI Layout"
   dropdown in Settings, with named options. Only the mechanism behind it changes. The
   Settings-dropdown skeleton on `feature/jacek-gui` remains useful; the rest of that
   branch's premise does not.

Adding a control after this lands costs: the markup once, a `grid-area` name, and one
line in each skin's area map. Fixing a bug costs once.

## Consequences

**Positive:**

- One place to add a control, one place to fix a bug, one place for each
  `data-a11y-key`. The accessibility failure mode above becomes structurally impossible
  rather than merely discouraged.
- Skins cost roughly a stylesheet each, so a fourth and fifth are cheap. Under the old
  design each new layout was a new page and a permanent tax on every future change.
- The CSS-custom-property token layer this needs is the same foundation the long-planned
  dark-mode / high-contrast work needs. It gets built once.
- A high-contrast, large-print skin for partially-sighted operators becomes a stylesheet
  rather than a project.

**Negative — accepted knowingly:**

- **CSS can move a control anywhere on the page, but not into a different parent.** If a
  skin wants a control nested somewhere the shared markup does not put it, the skin
  cannot express it. Contributors' layout freedom is genuinely constrained; this is the
  price of the maintenance property, and it is being paid deliberately.
- **The one-off refactor is large.** `Index.cshtml` is ~4,500 lines. Getting every
  control into a named grid area is the bulk of the work and is not a weekend.
- **Most of the markup in PR #93 will be discarded.** Fabio's design survives — the
  arrangement, the panel grouping, the promote/bury decisions, the tablet and mobile
  work. His 1,318 lines of HTML do not. He was told this directly in the PR thread on
  2026-08-06, before investing further.
- **Responsive behaviour is not yet settled.** Whether a responsive breakpoint is a
  property of each skin (media queries inside every skin stylesheet) or a separate axis
  is an open question, put to Fabio in the same thread.

## Sequencing

Build the mechanism in **IWC first**, then port to YWC.

- The design already lives in IWC (`docs/design/iwc-clone-split-plan.md`, Phase 7).
- IWC has no contributors working in it, so a 4,500-line refactor of `Index.cshtml`
  cannot collide with anyone. YWC currently has three open PRs (#90, #92, #93) all
  touching `Index.cshtml`, `Settings.cshtml` and `site.js`.
- IWC → YWC is the established direction of travel; `docs/design/` already holds five
  `*-port-from-iwc.md` documents.

Work that is layout-independent and survives the refactor untouched can proceed in YWC
meanwhile — notably keyboard shortcuts, which are also a screen-reader path.

## Related decisions and references

- [`Plan.md`](../../Plan.md) Phase 7 — rewritten in the same change as this ADR
- [ADR 0003](0003-single-vs-dual-receiver-ui.md) — predicted the two-layout maintenance
  cost that this ADR acts on
- [Issue #48](https://github.com/mm5agm/Yaesu_Web_Control/issues/48) — Jacek SP3L's
  original request
- [PR #93](https://github.com/mm5agm/Yaesu_Web_Control/pull/93) — Fabio Valente's
  prototype, and the thread where this decision was explained to him
- IWC `docs/design/iwc-clone-split-plan.md` Phase 7 — the skin design this adopts, minus
  its per-skin-Razor-partial option
