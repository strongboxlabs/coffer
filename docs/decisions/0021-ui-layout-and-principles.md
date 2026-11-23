# 0021 — UI layout and visual principles: workflow-dense sidebar + slate teal, light only

* Status: Accepted; amended 2026-10-20 by [ADR-0106](0106-folders-replace-tabs.md) (the rail collapses, resizes and shows balances; tabs are gone); amended 2026-10-31 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (page widths; the Overview's side column is page content); amended 2026-11-03 by [ADR-0111](0111-structure-is-one-page.md) (an account is deactivated from its row menu or the sidebar, not from the editor); amended 2026-11-03 with [ADR-0109](0109-overview-is-an-attention-first-home.md)'s second slice (Rule 5: the system Uncategorized category is always the neutral chip and dot); amended 2026-11-04 (Rule 1: every scroll area that holds page content is the containing block of what it scrolls, so the document never scrolls); amended 2026-11-06 with [ADR-0109](0109-overview-is-an-attention-first-home.md)'s third slice (Rule 5: a report chart fills each series with its family's chart shade, one per family and theme, slate left to Other; every pair of fills in a theme at ΔE2000 13 or more and each at 3:1 or more on the card); amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (the Overview's, the Budget page's and Reports' totals convert into the reporting currency); amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (Rule 6: a register row on a transfer between currencies shows the other leg's amount after the account chip)
* Date: 2026-05-11
* Affects: every screen in `src/Web/`; the design-system PR cluster that follows this ADR; the throwaway `mockups/` directory (artifacts of the comparison)

## Context

Phase 4 (PRs 4.1–4.5) shipped the SPA scaffold, WebAuthn login, setup
ceremony, recovery-codes display, and a virtualised per-account
register — all functional, all unstyled beyond defaults. A casual
visitor won't open it twice, but neither will the user. Before piling on more
feature surfaces (last-opened redirect, accounts list polish, ledger
picker, settings, etc.) the visual language needs to be settled so
each subsequent surface lands inside a coherent system rather than
adding another one-off.

Three directions were prototyped end-to-end (`mockups/a`, `mockups/b`,
`mockups/c`) using identical layouts so the comparison was purely
visual:

| Direction | Reference apps | Density | Vibe |
|---|---|---|---|
| A — Refined Minimal | Linear, Stripe Dashboard, Notion, Vercel | Medium | Professional, restrained |
| B — Friendly Dashboard | Monarch, Copilot, Mint | Low | Approachable consumer finance |
| C — Workflow-dense | YNAB, Lunch Money, Bloomberg-lite | High | "I'm here to work with data" |

C won on three grounds:

1. **Audience.** Coffer is a self-hosted Moneydance replacement for a
   user who already lives in Moneydance/Quicken muscle memory. Power-
   user density isn't a risk for this audience — it's the baseline.
2. **Scale of data.** A long-kept ledger holds years of transactions,
   many accounts and many securities. B's ~10-rows-per-screen breathing room would mean
   scrolling for everything; C fits ~30 rows per screen with bulk-edit
   workflows first-class.
3. **Screen utilisation.** C uses the full viewport; A/B leave space
   on the sides. For a desktop-first app, this matters.

A `mockups/c-softened/` variant was also tried — warmer palette,
Inter for numbers, rounded corners, soft shadows, colored category
chips — and rejected: the original C uses the entire screen, and its
sidebar is more legible. Three softening touches
from that variant were grafted onto C: colored category chips, status
badges (✓ / P), and a barely-there panel shadow.

## Decision

### Rule 1 — Layout: sidebar + breadcrumb top bar + main pane

Persistent left sidebar (`w-56` / 224px), thin top bar (`h-10` /
40px) with breadcrumb + ⌘K, scrollable main pane fills the rest. No
right-rail panel, no full-bleed hero. The same chrome on every
authed surface — the main pane is the only thing that varies.

Sidebar structure, top-to-bottom:

1. Brand wordmark + collapse button (white block, hairline bottom border)
2. Active-ledger picker (single row, ledger swatch + name + chevron)
3. Section nav: Dashboard, All transactions, Needs review, Reconcile
4. Accounts grouped by type — `Banking`, `Credit`, `Investments` —
   each account a one-click target with its balance in monospace at
   the right edge
5. Tools section: Budget, Reports, Recurring, Rules, Tags
6. User card at the bottom (avatar + name + chevron → settings /
   switch user / sign out)

The grouped-accounts pattern is non-negotiable for a ledger with many
accounts: a flat "Accounts" link that lists everything is a dead end.
Account groups in the sidebar surface every account in one click.

*2026-10-20 (ADR-0106).* Items 1 and 4 are built as written: the
collapse button and the per-account balances shipped in 0.126.0, with
section and folder totals beside them. The width is no longer fixed:
it defaults to the 224px above and the reader drags it, within limits.
See the amendment at the end of this ADR.

*2026-10-31 (ADR-0109).* "No right-rail panel" is about chrome that
varies by surface. The Overview's side column is page content inside
the main pane: it scrolls with the page and joins the single column at
narrow widths, and the chrome around it is unchanged.

*2026-11-04.* The document never scrolls: the frame is exactly the
viewport, and only its scroll areas move. Every scroll area that holds
page content is therefore the containing block of what it scrolls. The
main pane, the rail's nav and a dialog body that scrolls are `relative`;
the dialog overlay is `fixed`, which already makes it one. An absolutely
positioned box adds to the overflow of its containing block, not of the
scroll containers in between, so `sr-only` text with no positioned
ancestor inside an unpositioned scroll area escapes it and extends the
document, which then scrolls and carries the top bar and the rail with it.

### Rule 2 — Visual treatment: hard borders, no rounding on panels, barely-there shadow

- `bg-slate-50` for the page background; `white` for panels and the
  sidebar header/footer blocks.
- 1px borders (`#e2e8f0`) everywhere a region ends. No rounded
  corners on panels (they're rectangles); small radius (`rounded` /
  4px) on interactive controls only.
- Subtle panel lift: `box-shadow: 0 1px 2px rgba(15,23,42,.025)` on
  `.panel` and `.stat-card`. This is the single shadow recipe — no
  elevation hierarchy, no "card hover lift" effects.
- Hairline dividers between table rows (`#f1f5f9`), hover state
  `bg-slate-50`, selected state `bg-teal-50` + 2px inset teal left
  border.

The visual restraint is the message. A finance UI earns trust by
looking like a tool, not like a marketing site.

### Rule 3 — Typography: Inter for general, JetBrains Mono for numbers

- `Inter` 400/500/600/700 for everything text.
- `JetBrains Mono` 400/500 for any rendered numeric value — balances,
  amounts, dates in tables, KPIs, percentages. Hand-picked, not
  "every number that happens to be in monospace context."
- `font-feature-settings: 'tnum' 1` on numeric Inter contexts (sums,
  KPI deltas) so digits align without forcing mono.
- Base type 12–13px in the register, 14px in dashboard prose, 11px
  for muted labels (uppercase tracking-wide).

### Rule 4 — Accent: slate teal, single accent, four themes

- Accent: `teal-700` / `#0f766e` on the light themes, `teal-400` /
  `#2dd4bf` on the dark ones. Used for the active nav inset, the
  primary button fill, accent links, the spending-bar fill, and the
  selected-row indicator.
  - **Revised from `teal-600` / `#0d9488`.** That value is 3.74:1 as
    text on the white surface and 3.58:1 under a white button label,
    and `Button` is `text-sm`, so no large-text allowance applies.
    Both uses were below AA everywhere they appeared. teal-700 clears
    them (5.47:1 and 5.23:1) and changes the shape of nothing.
- **`text-inverse` is theme-aware**, and it is the foreground for
  every solid fill — `bg-accent` and `bg-state-danger` alike. Near-white
  on the light themes, near-black (`#020617`) on the dark ones: a white
  label on `teal-400` is 1.78:1. A single value cannot serve both
  directions, which is what `on-accent` and `accent-foreground` were
  each reaching for before they were folded into this token.
- Accent soft: `#ccfbf1` bg / `#0f766e` text — for the "active
  filter" pill and the inbox-link affordance.
- No second accent. Status colors (rose for outflow / debt, emerald
  for inflow / cleared, amber for pending / warning) are not
  accents — they're semantic and reserved for the data, never for
  chrome.
- **Four themes.** The bet that a dark pass would be "a token swap, not a
  screen-by-screen rebuild" held: every theme is a block of
  `--color-*` overrides in `index.css` keyed off `data-theme` on
  `<html>`, and no component changed to support them.
  - `light` — the default, unchanged.
  - `light-hc` — separators to slate-400, ink to slate-950, state
    and category fills up one step. On a white ground the contrast
    is carried by the ink and separator ramp, not the accent.
  - `dark` — slate-800 ground, softer ink, for long sessions.
  - `dark-hc` — slate-900 ground on a slate-950 canvas, ink ramp
    lifted a step.
- **The accent is per theme, but there is still only one of it.** The
  dark themes use `teal-400` / `#2dd4bf`, because on a dark ground
  contrast runs the other way. The single-accent rule is unchanged;
  what varies is the value, not the count.
- **Two inversions that are not symmetric.** Neither is a token swap:
  - The header band and canvas go DARKER than the surface on dark and
    LIGHTER on light. Lifting the band the way light does leaves
    muted text at 4.04:1 on it.
  - `surface-hover` cannot double as the neutral chip fill on dark: it
    has to lighten to read as hover, which drags the chip off its
    text. The chip now has its own `--color-chip-neutral` pair, which
    on light is exactly the values `Chip.tsx` used to hardcode.
- **Every theme is held to WCAG AA (4.5:1)** across the pairs the
  register actually renders — including the composited row-state
  fills (selected, needs-review, nested), where the tinted background
  is what the text sits on. A theme that fails that is not shipped.
- The choice is per device (localStorage), not per account — see
  `lib/theme.ts`. `index.html` applies it before first paint; without
  that, every load flashes light.
- **A second axis: the colour DIRECTION.** One hue regenerates both
  halves of the palette — the accent family, and the neutral ramp at
  hue+74 on slate's own lightness/chroma curve. `teal` (what ships),
  `indigo`, `rust`. Status colours and the category palette never
  rotate: those carry meaning, not brand.
  - The +74° offset is not invented. It is the teal/slate relationship
    this design already encodes (slate sits 40° off teal in HSL, 74° in
    OKLCh, at a fraction of its chroma), generalised. It holds well at
    rust, stretches at indigo and looks contrived at violet, which is
    not offered, so treat the rule as having a working range rather
    than as a law.
  - **OKLCh, not HSL.** HSL saturation is not perceptually uniform —
    rotating slate's 33% saturation gave a plum ground at indigo and
    olive at rust. The shipping ramp measures a near-constant OKLCh
    chroma of just under four hundredths, tapering to 0 at white, and that taper is what keeps a tinted near-white
    from going cream.
  - **Contrast targets are per direction, and the scale-down is
    dark-only.** Blue carries ~7% of luminance against green's ~72%, so
    indigo held to teal's 7.86:1 turns pastel on dark. On light the
    asymmetry runs the other way: scaling down there put indigo at
    4.01:1 and rust at 4.48:1 on white, both under AA.
  - **Selectors are compound** (`[data-theme][data-accent]`) and every
    block restates every token. Both are load-bearing for the previews
    on the Appearance screen, which nest a palette inside a different
    one: split the attributes across two elements and nothing matches;
    emit a partial block and it inherits from whatever it sits in.
  - Generated by `src/styles/theme-gen/`, committed as
    `styles/themes.generated.css`, regenerated with `npm run
    themes:gen`. A test fails if the committed file drifts from the
    model, and refuses any combination below AA.

### Rule 5 — Status and category color system

- **Status badges.** Cleared = pill with ✓, in the accent treatment
  below (it began as a green pill, which that treatment replaced);
  Pending = amber pill with P (`#fef3c7` bg / `#92400e` text). Small
  fixed circle, 1rem × 1rem. The style guide's register row draws the
  register's own `StatusBadge`, so its sample cannot drift from it, and
  its green swatch is labelled with what green marks: a status that
  succeeded, never an amount (ADR-0023 §H) and never Cleared.
- **The badge column: the glyph carries the meaning.** Every state has
  its own mark — check, dot, `P`, `S`, `!`, empty ring — so nothing is
  identified by colour alone. That is what lets the column work without
  colour vision, and it means colour does not have to do a second job.
  - Cleared and Reconciling share ONE treatment, `accent-soft` with
    `accent-soft-text`; the check and the dot tell them apart, and
    Reconciling adds a border.
  - Pending and Failed keep their status colours — those are warnings,
    and they should not blend into the app's own tone.
  - Uncleared stays a hollow ring: nothing asserted yet.
- **The check is not Tailwind green (hue 142).** The accent is at 172
  and the neutral ramp at 215 — the whole app lives in the teal/blue
  half, so a pure green is the only thing at its hue and reads as
  foreign rather than as meaningful; on the dark themes it reads as a
  different shade of green from the sage-toned Reconciling marker.
  Shifting or desaturating it does not fix that; the check needs no
  colour of its own.
  `state-success` is a status colour only; no amount is green
  (ADR-0023 §H). It is simply not what paints the badge.
- **Split-leg amounts carry no opacity.** A leg row is de-emphasised by
  the nested `surface-muted` plane and the tree glyph, never by dimming
  its amount. No opacity value lets a coloured amount clear AA over
  that plane, so leg amounts render at full token strength in both
  registers. The lines of a Scheduled split drop the colour with their
  parent (ADR-0023 §H).
- **Needs-review row treatment** (slice 2c, migration 037). The
  register flags bank-feed rows the user hasn't approved with a
  3px left bar in `state-warning` plus a soft `state-warning-soft`
  row tint at 70% opacity (`bg-state-warning-soft/70`, full on hover;
  ADR-0045). Concept-parity with MD's yellow-row +
  orange-bar pattern; treatment in our palette, not MD's literal yellow. Decouples
  from the bank-pending state — a row can be both
  `needs_review` (left bar + tint) and bank-pending (status
  badge); the visual layers cleanly. Clicking Approve in the
  context menu clears the bar.
- **Category chips.** Per-category color coding (small chip,
  uppercase-ish label, 11px). Initial palette:
  - groceries — orange (`#fff7ed` / `#9a3412`)
  - dining — purple (`#faf5ff` / `#6b21a8`)
  - housing — blue (`#eff6ff` / `#1d4ed8`)
  - utilities — amber (`#fffbeb` / `#92400e`)
  - subscriptions — indigo (`#eef2ff` / `#4338ca`)
  - transport — cyan (`#ecfeff` / `#155e75`)
  - salary / income — emerald (`#ecfdf5` / `#047857`)
  - transfer — slate (`#f1f5f9` / `#475569`)
  - phone — sky (`#f0f9ff` / `#075985`)
  - recreation — pink (`#fdf2f8` / `#9d174d`)
  - **uncategorized** — amber warning (`#fef3c7` / `#92400e`). *(Superseded
    2026-11-03: the system Uncategorized category is the neutral chip, below.)*
- **Tag chips are the exception, and they are solid.** A tag's colour
  is USER DATA, not a token — the recolor endpoint validates the
  `#rrggbb` shape, not palette membership — so it cannot be audited
  with the token set and cannot rotate with a colour direction. A
  coloured tag therefore renders as a SOLID fill of its hex with a
  black or white label, whichever contrasts more.
  - It was the hex as text over a ~13% tint of the same hex. That is
    unfixable rather than mistuned: both sides of the pair move
    together, so the formula has no contrast term. 33 of 40
    swatch/theme combinations were below AA, and on light ALL TEN
    failed — amber at 1.94:1.
  - Solid fill makes contrast depend only on the hex, so it is immune
    to the theme x direction matrix. Optimal PURE black/white bottoms
    out at ~4.58:1 at the luminance crossover, so every colour a user
    can pick clears AA by construction. Near-black/near-white does
    NOT: the worst case drops to 4.34:1 and an 11232-colour sweep puts
    197 below the line.
  - It also makes the picker honest. The swatches in the Tags panel
    were always solid circles of the hex while the chip rendered a
    tint, so what you picked was never what you got.
  - The cost is that a coloured tag reads louder than a tinted one.
    That is the right trade for a label whose whole job is to be
    picked out at a glance, and colour is opt-in per tag.
- Categories the user adds get an auto-assigned color: the name is
  pattern-matched onto a palette variant, and anything unmatched hashes
  its account id to one, so every transaction in a category lands on
  the same colour. Category colour is therefore entirely TOKEN-driven —
  it rotates with the theme and is covered by the token audit, which is
  why the tag-chip contrast failure cannot reach a category.
  - *(Amended 2026-11-03.)* **The system Uncategorized category is
    always the neutral chip**, and its identity dot the neutral dot,
    everywhere a category chip or dot is drawn: both registers, the home
    screen's rows and the budget dots. It is where unclassified rows
    land, not a category anyone chose, and a hashed colour made it look
    like one. It is recognised by the account's `isSystem` flag on a
    category (the ledger's one system category), never by its name,
    which a person's own category may share (`lib/categoryChip.ts`).
  - *(Amended 2026-11-06, with
    [ADR-0109](0109-overview-is-an-attention-first-home.md)'s third slice.)*
    **A report chart fills each series with its family's chart shade**,
    `--color-cat-<family>-chart`, not the chip's `-text` shade. The text
    shades are tuned for words on a chip, and several pairs of them cannot
    be told apart as fills side by side. Nine families have one, in each of
    the four theme blocks and the `@theme` default; slate has none, because
    slate is a chart's Other (`--color-cat-xfer-text`), so no series can
    look like the fold. Chips and dots keep the `-text` shades, so a
    category is the same family everywhere and a different shade only on a
    chart. The system Uncategorized category's own colour, the neutral, is
    no series colour, so on a chart it borrows a free family, as a category
    whose family another holds does (ADR-0104 D4).
  - **The chart shades' floors, per theme**, held by `chartShades.test.ts`:
    every pair of fills one chart can hold, Other's slate included, at
    ΔE2000 13 or more, and every fill at 3:1 or more on the theme's card
    (`--color-surface`). The pair floor is what a chart needs: a contrast
    floor is per colour, and cannot see two fills that each clear it and
    look alike. The chip text shades all clear AA and fail the pair floor,
    which the test also asserts, so it can fail on real input. The floor
    is the closest pair the light set reaches within the families, the
    tightest theme. The test computes ΔE2000 itself and checks it against
    the formula's published reference pairs, since an error in the formula
    would pass every shade. The style guide's Chart shades section shows
    them side by side, as a stack of neighbours is read.
  - **There is no per-category override.** There is no `color` field
    on the category type and no endpoint for it.
  - If one is ever built it takes a user-supplied hex, which puts it in
    the same class as tags: outside the token system, invisible to the
    token audit, and reachable by arbitrary `#rrggbb`. Use the tag
    treatment — solid fill, black-or-white label — not a tint. A tint
    over the surface is what failed 33 of 40 combinations.

### Rule 6 — Register is the work surface; treat it as first-class

Columns (left to right): selection checkbox, flag indicator, date,
status badge, check #, payee + memo, category chip, tags, outflow,
inflow, balance. Toolbar at the top with status filters (All /
Cleared / Pending / Uncategorized) and a "+ Filter" affordance.
Bulk-action footer when ≥1 row is selected (Categorize / Tag / Flag /
Hide). Keyboard-first: arrow keys move row focus, space toggles
selection, `c` opens the category combobox on the focused row.
*(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: on a transfer between currencies the category
cell, which shows the counterparty, shows the other leg's amount after the account chip,
muted, in the app's format (the symbol, no plus). A long name truncates and the amount
stays whole. Same-currency transfers and category rows are unchanged.)*

These behaviors are not in this ADR — they live in the design-system
PRs and the per-surface PRs — but the layout reserves the chrome for
them.

This rule covers the **bank** register (also used by credit-card /
cash / asset / liability — same shape). The **investment** register
is a separate surface with its own column layout, multi-posting
aggregation, and three-chip slot-6 treatment; see
[ADR-0028](0028-investment-register-surface.md).

### Rule 7 — Iconography: lucide, hand-rolled inline SVG until D.3

Mockups use hand-rolled `<svg>` with lucide-style paths. The
design-system cluster's D.3 PR introduces the `lucide-react` package
and replaces the inline SVGs with named icon components, but the
visual silhouette is already locked in.

### Rule 8 — Tailwind v4 with semantic tokens

- Tailwind v4 (the `@theme` block in CSS, not `tailwind.config.js`).
- Define semantic tokens (`--color-surface`, `--color-surface-muted`,
  `--color-border`, `--color-accent`, `--color-accent-soft`,
  `--color-text`, `--color-text-muted`, `--color-success`,
  `--color-warning`, `--color-danger`, plus the category palette as
  `--color-cat-groc` etc.) — components consume these, not raw
  palette names.
- Dark mode (future) is a single `[data-theme="dark"]` override that
  remaps every semantic token.

### Rule 9 — Action affordances: one shape for "commit or back out"

A pattern repeated across every action footer in the app and owned by
nobody drifts: a left-aligned **Apply / Cancel** among right-aligned
**Cancel / Save** footers passes review unremarked.

- **Right-aligned, `flex justify-end gap-2`.** The commit action sits
  furthest right, where the eye and the thumb finish.
- **Cancel first, then the affirmative.** Reading order is
  retreat-then-commit; the destructive-by-accident click is the one
  furthest from where you were reading.
- **`variant="secondary"` for the retreat, `variant="primary"` for the
  commit**, both `size="sm"`. A destructive commit takes
  `variant="danger"` instead of primary — never a second primary.
- **Vocabulary.** "Save" commits an edit; "Cancel" abandons one. Not
  "Apply", not "OK", not "Done". A pending commit reads "Saving…" and a
  pending destructive one "Working…", matching `ConfirmDialog`.
- **Disabled until there is something to do.** Both buttons are
  disabled when nothing has changed, so the footer states whether the
  form is dirty without a separate indicator.

This is a description of what the app already does, not a new
invention — `ConfirmDialog`, `LayoutNameDialog`, `AccountEditorDialog`
and the split-posting editor all follow it. The rule exists so the
next one does too.

**Use `ActionFooter`.** The rule is mechanical, so it lives in a
primitive rather than in this paragraph: `components/ui/ActionFooter`
takes optional `tertiary` / `cancel` / `confirm` slots and renders them
in that order with the variants and size above, whatever order you pass
them in.

Slots rather than `onCancel` / `onSave` because the app has four footer
shapes, and a two-callback API fits one and fights the rest: cancel +
confirm; cancel + tertiary + confirm (the import wizard's Back);
tertiary + confirm with no cancel ("Import another" / "Done"); and a
decision pair ("Deny" / "Allow"). The primitive fixes what should never
vary — placement, order, variants, size, gap — and leaves the wording,
which is a product decision.

**A single-slot footer is still a footer.** A lone "Done" or "Close"
gets the same treatment — `ActionFooter` with only `confirm` filled —
because cohesion is about the shape of the thing, not the number of
buttons in it.

`ActionFooter.test.ts` holds the line: a `justify-end` container with a
Button outside the primitive fails, with the nineteen pre-existing
sites frozen in a list that may shrink and must not grow.

Know what that guard cannot do. It is a text scan, and it errs both
ways: it does NOT catch a left-aligned footer (`justify-start`),
and it DOES flag right-aligned toolbar actions that
are not footers at all. The threshold is one Button rather than two
because the two errors cost differently — a false positive is one line
on the frozen list, a false negative is drift nobody sees. It stops the
next hand-rolled footer; it does not verify the rule.

### Rule 10 — A visible affordance, with right-click as the accelerator

A panel whose row actions — rename, recolour, merge, delete, add
sub-category, move — are reachable only by right-clicking a row has
no visible path to them, and none at all on touch.

- **Every action needs a path you can see.** Right-click is an
  accelerator for people who already know, never the only way in.
- **A `title` tooltip is not an affordance.** "Right-click for actions"
  does not exist on touch, is not announced as an action, and is
  invisible until you are already hovering the thing you did not know
  was interactive.
- **On a list row, the visible path is `RowActionsButton`** — a kebab
  that opens the same `ContextMenu`, anchored to the button so keyboard
  users get a sensible position rather than a pointer coordinate that
  never existed.
- **A row with no actions gets a same-size spacer**, so the columns of
  rows that do have them stay aligned.

**The register is a deliberate partial exception, and it is a gap, not
a clean bill of health.** Selecting rows reveals a bulk bar covering
Categorize / Move / Delete, so those have a visible path. Accept,
Duplicate, Create reminder and Show other side remain right-click-only.
A control on every row of a grid that dense would cost more than it
buys, so this is recorded rather than closed. Anyone adding a row
action there should ask whether it also belongs on the bulk bar.

**Built.** `components/ui/RowActions.test.ts` checks that every surface
attaching `onContextMenu` also renders a `RowActionsButton`, or is
listed as an exception WITH A REASON (the register trio, on the terms
above).

**On a dense rail, only the SELECTED row carries the kebab.** A rail
action reachable only by right-clicking a nav row — with an empty
state reading *"Right-click an account … to add it"* — cannot be
followed on touch at all, the same defect as the `title` tooltip
above and more explicit about it.

A control on every row of a ~23px rail is the cost Rule 10 already
declines to pay in the register. One on the selected row is not:

- It is **always visible where it is**, so it is discoverable. A
  hover-revealed control is not an answer — the rule's own objection is
  to affordances "invisible until you are already hovering the thing
  you did not know was interactive", and hover-reveal is exactly that.
- **Every row reserves the slot** from the same pinned token the button
  uses (`--spacing-control-20px`), so names and review dots stay in one
  column and stay aligned at all three densities (Rule 11).
- A **tab strip needs no reserved slot** — it wraps rather than forming
  columns, so nothing below it shifts.
- `IconButton` gained a 20px `sm` size for this. It is not a "make a
  button unobtrusive" knob; 20px is the floor of a comfortable pointer
  target.

**A third answer, for a dense grid inside a form** (added 2026-09-16, the
splits editor). The leg rows of a split are a 30px grid of up to twenty-five
rows, each carrying Move up / Move down / Remove. Neither existing answer fits:
a full-strength kebab on every row is twenty-five competing controls in a
block the eye is meant to scan down, and there is no "selected row" — every
row is editable at once.

The treatment is **present on every row and always visible, but quiet at rest
and emphasised on the row you are working in** — `opacity-50`, going to full on
`group-hover` and `group-focus-within`. That is NOT the hover-reveal this rule
rejects, and the distinction is the whole point: the control is visible and
hit-testable before you go near it, so it is discoverable by sight and reachable
by Tab. What hover changes is emphasis, not existence. A reader scanning the
list sees that every leg has actions; a reader working a leg sees that leg's
actions clearly.

The same row keeps `onContextMenu`, so the guard in `RowActions.test.ts` still
holds the file to prescribing a visible path.

**What the guard cannot check.** It is file-level, so it proves a
surface has *an* affordance, not that every row type does. "Renders any
button" would be worthless as a check: the sidebar carries four
IconButtons — System settings, collapse, Account security, Sign out —
all global chrome, none a row action. The check names the mechanism the
rule prescribes instead.

Deactivating an account has a visible path: the kebab and right-click
menu of its row on Structure's Accounts tab, and the sidebar's, both
through `setAccountActive`. *(Amended 2026-11-03, [ADR-0111](0111-structure-is-one-page.md)
D4: the account editor dialog used to carry an "Active" checkbox that
PATCHed `isActive` through a different call, deactivating on Save without
the question the menu asks. It is gone, so Deactivate always asks and a
grep for `setAccountActive` finds every path.)* Tab membership belongs
where the tabs are, not on the accounts page, which sections by type and
never names a tab.

### Rule 11 — Three kinds of length, and rhythm rides a ramp

A spacing vocabulary that grows by copying the neighbouring component
rather than by decision drifts unless it is defended: nothing but a rule
distinguishes `py-2.5` (the list-row standard) from `py-3.5` (a one-off
with no reason).

**Rhythm** — padding, margin, gap, space — uses only:

```
0  0.5  1  1.5  2  2.5  3  4  5  6  8  10  12
```

- `0.5 1 1.5 2 3` inside controls; `2.5` is the list-row vertical
  padding (8px rows are cramped, 12px wastes a settings page);
  `4 5 6` between blocks, with **`5` the page and dialog padding
  standard** (25 call sites — every page shell, every dialog);
  `8 10 12` between sections and for empty-state breathing room.
- `7`, `9` and `11` are absent from rhythm and stay absent. They exist
  here only as control heights (`h-7`) and gutter offsets.

**Hairlines** are `px` — one device pixel, on purpose. Dividers
(`gap-px`), border pull-ups (`-mb-px`), optical nudges (`py-px`).
Deliberately off the ramp, and deliberately not scaled.

**Pinned lengths** are icon sizes, control heights, layout widths and
scroll caps. They sit on NAMED tokens — `--spacing-icon-md`,
`--spacing-control-28px`, `--spacing-fixed-96px` — which Tailwind
compiles to a bare `var(--spacing-icon-md)` rather than
`calc(var(--spacing) * N)`. Density therefore cannot reach them by
construction, not by anyone remembering to keep them out of the way.
(The register's scroll-gutter offsets are a fourth case: `right-[26px]`
aligns to a browser-drawn scrollbar and belongs to no design scale at
all.)

**Arbitrary values are barred from rhythm.** Not for tidiness: Tailwind
v4 derives every rhythm utility from `--spacing`, so `p-4` is
`calc(var(--spacing) * 4)`. An arbitrary value does not move when that
variable does, so it stops matching its neighbours at every density but
the one it was eyeballed at. If a value is genuinely needed it gets
a token, not a bracket.

Enforced by `src/Web/src/styles/spacingScale.test.ts`, which is a proof
rather than a proxy — exact utility matches, not a heuristic. It scans
comments too, because a comment quoting an arbitrary value goes stale
with the code it describes.

### Rule 11a — Density is the third Appearance axis, and it moves space only

The axis this rule existed to unblock. `data-density` on `<html>`
redefines exactly one token:

| | `--spacing` | page padding | list row | icon-to-label gap |
|---|---|---|---|---|
| Compressed | 0.2rem (0.8×) | 16px | 8px | 6.4px |
| Regular | 0.25rem | 20px | 10px | 8px |
| Relaxed | 0.3rem (1.2×) | 24px | 12px | 9.6px |

One variable moves ~1700 utilities. Text, icons, controls and fixed
widths do not move at any setting.

**Why controls hold still.** Text does not scale here — this is a
spacing control, not a zoom control — so a box that shrank around it
would close on its own contents. At 0.8× an `h-7` icon button would be
22.4px around 13px of text; at 1.2× a primary button would be 48px.
The same logic bars icons for a different reason: an icon rasterises a
shape, so 14px at 0.8× is 11.2px carrying a sub-pixel stroke, which
renders *soft* rather than small. Fractional pixels are fine for
rhythm (`gap-2` → 6.4px) because layout is subpixel-positioned.

**What holds the line.** `pinnedLengths.test.ts` bans numeric sizers
outright — `h-4`, `w-24`, `size-4`, `min-h-9` — rather than setting a
threshold. A threshold that lets control-sized squares scale produces
split pairs such as `h-7 w-fixed-28px`: the height scaling, the width
not, every icon button visibly non-square at any setting except the
default, and correct-looking in review. The same file asserts that
the density blocks declare `--spacing` and nothing else, and that the
Rule 10 row-action spacer still matches `IconButton`.

### Rule 11b — A preview varies one axis; everything else shows what you have

"Staged" is a claim about what the user SEES, not about where the state
lives. A panel can save nothing on selection and still read as applying
on the fly: if every preview follows every *draft*, one click repaints
most of the cards, and a panel where most of the surface changes the
instant you click is indistinguishable from one that applied your
choice.

- **Each card shows its own option against the APPLIED values of the
  other axes.** A click moves the radio and nothing else.
- **The cost, accepted deliberately**: stage a theme and a colour
  together and the colour cards still show the old theme until you
  save. That is the honest reading — they show what you currently have
  — and it beats the churn. The helper text says so.
- **A preview must be a large enough sample to show its own axis.**
  Density cards of three heights, 16 / 20 / 24px, are a 4px spread
  beside a two-line label, and read as identical — the same *symptom*
  as the compound-selector bug in Rule 4, from a different cause.
  Density is cumulative, so the sample has to accumulate: six rows puts
  the spread at 48 / 60 / 72px.

Guarded in `AppearancePanel.test.tsx` by snapshotting all ten preview
cards and asserting that a click on any axis changes none of them.

**Per device, like the other two axes.** localStorage, stamped before
first paint by the inline script in `index.html` — without that the app
lays out at the default spacing and reflows when React mounts, which is
a visible jump rather than a flash. `theme.test.ts` guards the
duplication.

## Design-system PR cluster (follow-on)

This ADR is the foundation; the cluster delivers the system in three
small PRs. PR 4.6 (last-opened auto-redirect) and other Phase 4
feature PRs resume **after** the cluster lands so they're built
inside the system, not around it.

### D.1 — Design tokens + style guide route

* Add Tailwind v4 `@theme` with the semantic + category tokens above.
* Add JetBrains Mono to the Google Fonts loader; add `font-mono`
  utility variants.
* Add `src/Web/src/routes/styleguide/StyleGuidePage.tsx` (dev-only
  route, gated behind `import.meta.env.DEV` so it never ships in
  prod) showing every token, every primitive, every chip color,
  every status badge, the layout grid, and a sample register row.
* No visual change to existing screens — pure foundation.

### D.2 — Primitives + apply to existing screens

* Build the primitive set: `Button`, `IconButton`, `Input`,
  `Combobox`, `Chip`, `StatusBadge`, `Panel`, `PanelHead`,
  `SidebarLayout`, `Breadcrumb`, `KpiTile`, `BulkActionBar`.
* Rewrite the existing screens against the primitives:
  - `LandingPage` — branded landing matching the system
  - `RegisterPage` (setup ceremony) — Panel + primary Button
  - `RecoveryCodesPage` — Panel with monospace code grid
  - `AuthedHeader` → full `SidebarLayout` chrome
  - `LedgerPickerPage` — accounts-list-style picker
  - `AccountsListPage` — sidebar-grouped accounts surface
  - `RegisterPage` (per-account txn register) — full toolbar +
    status filters + bulk-action footer
* No new features, no new surfaces — just visual + structural
  consistency.

### D.3 — Lucide icons, responsive behavior, ledger-picker chrome, ⌘K affordance

* Add `lucide-react`; replace hand-rolled SVGs with named components.
* Sidebar collapse for narrow widths (icons-only ≤ 1024px, hidden
  ≤ 768px with a hamburger trigger).
  *2026-10-20 (ADR-0106):* collapse shipped as the reader's choice, not
  by breakpoint. A button, `Ctrl+\` or the resize handle collapses the
  rail to a column of icons, and the choice is remembered per browser.
  No breakpoint collapses or hides it. The one rule tied to the window
  is a width ceiling of 40% of it, so a width saved on a wide screen
  cannot crowd the main pane on a narrow one.
* Top-bar `⌘K` opens a command palette (initially: ledger
  switching, account jump, recent actions). The actual command set
  fills in over later PRs — D.3 ships the affordance + the empty
  palette.
* Ledger picker in the sidebar header gains the actual ledger-list
  flyout (currently a stub button in the layout).

## Consequences

**Positive**
- Every subsequent UI PR slots into an existing system. No
  per-surface visual debate.
- The mockups become a permanent reference: when ambiguity arises
  about "what does X look like" the answer is in `mockups/c/` (the
  decision artifact for the chosen direction).
- Dark mode is a future token swap, not a redesign.
- The semantic-token discipline means a palette tweak (e.g. "the
  teal feels too cool, try teal-700") is one CSS change, not a
  codebase-wide find-and-replace.

**Negative**
- Information density is a barrier to a first-time consumer-finance
  user. We accept this; the audience is power users. If we ever
  ship a "novice mode" it's a per-user preference toggle, not a
  redesign.
- A single-accent system is visually constraining — there's no
  second color to lean on for emphasis. Discipline keeps it
  coherent; sloppiness makes it monotone.
- The category palette has to be maintained: a new category needs
  an assigned color. The auto-assignment + override fallback keeps
  this from being a per-category PR each time.
- Hand-rolling primitives instead of pulling in a component library
  (shadcn/ui, Radix, Headless UI) costs initial build time. Worth
  it for control over the visual language; a third-party library
  would push us toward its default aesthetic and away from the
  workflow-dense direction.

## Alternatives considered

- **Direction A (Refined Minimal).** Cleaner, ages well, lower
  aesthetic risk. Rejected: too restrained for a data-heavy app,
  and the audience can handle (and prefers) more density.
- **Direction B (Friendly Dashboard).** Warm, inviting, screenshot-
  friendly. Rejected: scrolling-everywhere at a long-kept ledger's transaction count;
  consumer-toy feel against power-user expectations; "every
  category needs a colour and an icon" maintenance overhead.
- **c-softened variant (warm C).** Same density as C but with
  stone-* palette, Inter for numbers, rounded corners, larger base
  type. Rejected: the warm palette reduced sidebar legibility and
  the rounding wasted screen edge real estate. Three specific
  touches (colored category chips, status badges, panel shadow)
  were grafted onto C.
- **Component library (shadcn/ui, Radix, Headless UI).**
  Production-quality primitives off the shelf, faster to PR-1 of
  the system. Rejected for now: the libraries' visual defaults
  pull toward a different aesthetic (rounded, shadow-heavy,
  consumer-friendly), and Coffer's needs are narrow enough that
  hand-rolling ~12 primitives is one PR of work. Re-evaluable
  later if the primitive set grows past ~25.
- **Dark mode in this ADR.** Doubles the token-validation surface
  area for the first design-system PR with no shipped value.
  Deferred to a later ADR + PR after the
  light system is stable.

## Amendment, 2026-10-20 — the rail collapses, resizes and shows balances; tabs are gone (ADR-0106)

**Rule 1 is built.** The header carries the collapse button it always
listed, and every account row carries its balance in monospace at the
right edge.

- **Width.** The default stays Rule 1's 224px. A handle on the rail's
  right edge resizes it, held between 200px and the smaller of 480px and
  40% of the window. The handle is a `role="separator"`: arrow keys
  resize, Enter collapses, double-click resets to the default.
- **Collapsed** is a 56px column: the destinations' icons, one icon per
  account type present, named with that type's total, and an expand
  button. There is no tooltip primitive, so the icons carry `title` text.
- **Width, collapse and each folder's open state are remembered per
  browser**, and the first two are applied before first paint, as theme
  and density are (Rules 4 and 11a). ADR-0106 D7 gives the reasons.
- **Nothing new is drawn.** The new rows take the rail's existing sizes,
  tokens and components, and a balance renders as the dashboard renders
  one; where the design's measurements differed, the app's won
  (ADR-0106 D8). The one new token is `--spacing-fixed-56px`, the
  collapsed width.
- **Balances.** An account shows its current balance, a folder the sum of
  everything under it, and a section heading its type's total. Liabilities
  are negative, as on the dashboard. A total that spans currencies shows
  "mixed", not a sum; the dashboard sums without conversion instead, so
  this is new (ADR-0106 D6).
- **Folders** sit inside each type's section, nested, folders before
  accounts. Each level indents with the rail's existing grouped-block
  device, an indent under a faint left border (ADR-0106 D2 to D5, D8).

**Rule 10's tab text is history.** The tab strip, its reserved-slot
exemption, and "membership belongs where the tabs are" described tabs,
which ADR-0106 removed. The rule itself stands, and folders are held to
it: every move a drag makes is also on a row menu, because there is no
drag on touch or from a keyboard. The collapse button named in the
guard's note above now exists, and the "New tab" button is gone.

Each section heading gains a new-folder button for owners and editors,
the folder-with-plus icon rather than a bare plus, which on a list of
accounts reads as "add account". It takes this rule's third answer, not
a hover-reveal: present and reachable by Tab at rest, quiet until the
heading is hovered or focused. On touch it is the only way to make a
top-level folder, so it cannot be invisible there.

**D.3's breakpoints are not built.** Collapse is the reader's choice;
see the note under D.3.

## Amendment, 2026-10-31 — page widths, and the Overview (ADR-0109)

**Page widths are stated here.** Context point 3 says C uses the full
viewport; only the registers do. The widths are:

- **Registers** run the full width of the main pane.
- **Destination pages** cap their content at `max-w-5xl`.
- **The Overview** caps its content at `max-w-fixed-1600px`, a pinned
  token (`--spacing-fixed-1600px`). It is the only page with two columns
  side by side.
- **The Securities catalog** caps at `max-w-6xl`. It is the only
  destination page that is one wide table, and the extra width goes to
  the security's name.

**The Overview's breakpoints are container queries on the main pane**,
not on the viewport, because the rail resizes and collapses: the page is
an `@container/page` and each panel an `@container/panel`. They are the
first container queries in the SPA. D.3's viewport breakpoints remain
unbuilt.

**The Overview shows "mixed" as the rail does.** The amendment above
says the dashboard summed across currencies; it no longer does. Every
aggregate on the Overview that spans currencies shows the rail's marker
and wording, and so do the totals on the Budget page and in Reports.
*(Superseded 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7: those totals convert into the
ledger's reporting currency and show a figure.)*
