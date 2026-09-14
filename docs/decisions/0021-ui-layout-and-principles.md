# 0021 — UI layout and visual principles: workflow-dense sidebar + slate teal, light only

* Status: Accepted
* Date: 2026-05-11
* Affects: every screen in `src/Web/`; the design-system PR cluster that follows this ADR; the throwaway `mockups/` directory (artifacts of the comparison)

## Context

Phase 4 (PRs 4.1–4.5) shipped the SPA scaffold, WebAuthn login, setup
ceremony, recovery-codes display, and a virtualised per-account
register — all functional, all unstyled beyond defaults. Aunt Linda
won't open it twice, but neither will the user. Before piling on more
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
2. **Scale of data.** over a thousand transactions, hundreds of accounts, hundreds of
   securities. B's ~10-rows-per-screen breathing room would mean
   scrolling for everything; C fits ~30 rows per screen with bulk-edit
   workflows first-class.
3. **Screen utilisation.** C uses the full viewport; A/B leave space
   on the sides. For a desktop-first app, this matters.

A `mockups/c-softened/` variant was also tried — warmer palette,
Inter for numbers, rounded corners, soft shadows, colored category
chips — and rejected as "nice but the original C uses the entire
screen, and the sidebar is more legible." Three softening touches
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

The grouped-accounts pattern is non-negotiable for a hundreds-of-accounts
ledger: a flat "Accounts" link that lists everything is a dead end.
Account groups in the sidebar surface every account in one click.

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
    them (5.47 and 5.23) and changes the shape of nothing.
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
- **Four themes** (revised; this rule previously read "light theme
  only"). The bet that a dark pass would be "a token swap, not a
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
- **Two inversions that are not symmetric.** Porting to dark surfaced
  both, and neither is a token swap:
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
    rust and stretches at indigo; violet was tried and dropped for
    looking contrived, so treat the rule as having a working range
    rather than as a law.
  - **OKLCh, not HSL.** HSL saturation is not perceptually uniform —
    rotating slate's 0.33 gave a plum ground at indigo and olive at
    rust. The shipping ramp measures a near-constant C 0.037, tapering
    to 0 at white, and that taper is what keeps a tinted near-white
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

- **Status badges.** Cleared = green pill with ✓ (`#dcfce7` bg /
  `#15803d` text); Pending = amber pill with P (`#fef3c7` bg /
  `#92400e` text). Small fixed circle, 1rem × 1rem.
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
- **The check was Tailwind green (hue 142) and should not have been.**
  The accent is at 172 and the neutral ramp at 215 — the whole app
  lives in the teal/blue half, so a pure green was the only thing at
  its hue and read as foreign rather than as meaningful. Reported on
  the dark themes as the check being "a different shade of green" from
  the sage-toned Reconciling marker. Moving it a few degrees was not
  enough and neither was desaturating it; the answer was that it did
  not need its own colour at all.
  `state-success` still exists and still colours positive amounts in
  the investment register. It is simply not what paints the badge.
- **Split-leg amounts carry no opacity.** A leg row is de-emphasised by
  the nested `surface-muted` plane and the tree glyph, never by dimming
  its amount. `state-success` at 70% over that plane is 2.88:1 and no
  opacity value clears AA, so leg amounts render at full token
  strength in both registers.
- **Needs-review row treatment** (slice 2c, migration 037). The
  register flags bank-feed rows the user hasn't approved with a
  3px left bar in `state-warning` plus a soft `state-warning-soft`
  row tint at ~30% opacity. Concept-parity with MD's yellow-row +
  orange-bar pattern (user reference screenshot 2026-05-16);
  treatment in our palette, not MD's literal yellow. Decouples
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
  - **uncategorized** — amber warning (`#fef3c7` / `#92400e`)
- Categories the user adds get an auto-assigned color (rotating
  through the palette) with a per-category override on the category-
  edit form.

### Rule 6 — Register is the work surface; treat it as first-class

Columns (left to right): selection checkbox, flag indicator, date,
status badge, check #, payee + memo, category chip, tags, outflow,
inflow, balance. Toolbar at the top with status filters (All /
Cleared / Pending / Uncategorized) and a "+ Filter" affordance.
Bulk-action footer when ≥1 row is selected (Categorize / Tag / Flag /
Hide). Keyboard-first: arrow keys move row focus, space toggles
selection, `c` opens the category combobox on the focused row.

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

Added after an Appearance panel shipped a left-aligned **Apply / Cancel**
while all 25 other action footers in the app were right-aligned
**Cancel / Save**. Nothing caught it, because the pattern existed 25
times and was owned by nobody.

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
26th one does too.

**A rule this mechanical belongs in a primitive, not in prose.** The
follow-on is an `ActionFooter` in `components/ui/` plus a guard test —
the same move this repo already made for tokens (a colour class naming
a token that does not exist is a test failure, not a review catch) and
for the hand-copied pre-paint script in `index.html`. Until that lands,
this rule is the spec and review is the enforcement, which is exactly
the weak arrangement that produced the defect.

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
  friendly. Rejected: scrolling-everywhere at over a thousand transactions;
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
  area for the first design-system PR with no shipped value (the
  user works in light). Deferred to a later ADR + PR after the
  light system is stable.
