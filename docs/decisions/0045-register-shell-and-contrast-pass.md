# 0045 — Shared register shell + contrast/legibility pass

* Status: Accepted
* Date: 2026-06-09
* Refines: ADR-0021 (UI layout + principles), ADR-0030 (domain-pure
  organization — realizes its deferred "shared register row" direction)

## Context

Two problems surfaced while using the registers as a daily workhorse:

1. **Divergent chrome from duplicated code.** The bank and investment
   registers each hand-rolled their list chrome (surface, column-header
   band, scroll surface, row-state styling). They drifted: the bank list
   wrapped its rows in a white `bg-surface` surface, the investment list
   did **not** — so investment rows showed the grey app canvas
   (`bg-surface-muted`) through, while bank rows were white. Row-state
   styling was copy-pasted across four row renderers.
2. **Too-soft visual hierarchy.** The palette leaned on very low-opacity
   tints for state. The focused row was `bg-accent-soft/15` (effectively
   invisible); bulk-selected rows had no row-level highlight at all;
   borders (slate-200) and `text-subtle` (slate-400, ~2.6:1 on white —
   below WCAG AA) were too faint for a dense grid.

## Decision

**Reuse — one definition of the chrome, consumed by both registers:**
- `RegisterShell` — the white list surface + the grey column-header
  band (one padding scheme, one token).
- `RegisterScrollSurface` — the scroll area, custom scroll-track slot,
  and the `pr-12` gutter that keeps header columns aligned with data.
- `registerRowChrome(state)` — the single source of truth for row state:
  a full 2px accent **focus ring** (border only, no fill), a faint
  `accent-soft/40` **selection** tint, and a `warning-soft` fill + 3px
  bar for **needs-review** — orthogonal, so a row can carry several.

Both `BankRegisterPage` and `InvestmentRegisterPage`/`InvestmentRow`
render through these; the chrome can no longer drift.

**Surface rule — white is content, grey is chrome:**
- Content surfaces (register rows, the accounts panel / sidebar) are
  white (`bg-surface`).
- Grey is reserved for header bands (new `--color-surface-header`,
  slate-100) and the faint app canvas (`--color-surface-muted`,
  slate-50) — never as a background for content rows or panels.

**Contrast bumps (revising ADR-0021's tokens):**
- `--color-border`: slate-200 → **slate-300** (visible separators).
- `--color-surface-hover`: slate-100 → **slate-200** (clear hover).
- `--color-text-subtle`: slate-400 → **slate-500** (2.6→4.8:1, clears
  WCAG AA for the secondary labels it's used on).

**Sidebar top, made sensible:**
- The ledger picker is now a **working ledger-switch dropdown** (was a
  dead stub with a chevron that did nothing).
- The redundant "All ledgers" and duplicate ledger-name nav rows are
  removed (the picker carries ledger context + switching; the landing
  page stays reachable via the main-pane breadcrumb). The top now reads
  picker → account-filter tabs → accounts.
- Account categories are offset under a faint left rail and the rows are
  denser.

## Consequences

### Positive
- The two registers are visually identical by construction; new fields
  inherit the chrome.
- The reported grey-rows bug is fixed structurally (shared white
  surface), not patched per-page.
- Secondary text clears WCAG AA; the focused/selected/review states read
  at a glance in a dense grid.

### Negative / trade-offs
- The sidebar going white means the active account row no longer lifts
  via a white fill on grey — it's marked by its accent left-bar + bold
  text. Acceptable; revisit with a faint active tint if needed.
- Row-state box-shadows are inline styles (dynamic inset sets aren't
  picked up by Tailwind's JIT class scanner); centralized in
  `registerRowChrome` so this lives in exactly one place.
