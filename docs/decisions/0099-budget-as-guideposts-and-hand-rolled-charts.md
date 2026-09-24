# 0099 — Budgeting as guideposts, and hand-rolled charts

* Status: Accepted
* Date: 2026-09-17
* Related: [ADR-0021](0021-ui-layout-and-principles.md) (layout, colour, density),
  [ADR-0023](0023-ui-ux-interaction-conventions.md) (interaction conventions),
  [ADR-0056](0056-ledger-overview-dashboard.md) (the dashboard, which deferred
  spending-by-category "no data yet"), [ADR-0063](0063-mcp-server.md) (where the reusable
  aggregation layer this consumes was specified)

## Context

Coffer had no budgeting. The obvious shape — a table of monthly targets per
category — was built first and rejected on sight: it was a ranked table beside
Empower's donut, daily bars and click-through, and it looked like a database
listing.

Two questions fell out, and they are coupled: **what a budget in this app IS**,
and **how this app draws anything at all**, since it ships no charting library.

The owner's own segmentation shaped the first answer. Roughly: most people never
use a budget; a large minority use one as guideposts to notice a month that is
out of the ordinary; a small minority manage spending against a plan. They place
themselves between the first two, and said plainly: *"I don't want to build a
tool for 10%."*

## Decision

### D1 — A budget is a MARK, and the mark has two possible sources

Every category row carries one mark. It is:

* **your target**, when you have typed one for that category and month, or
* **your normal** — the mean of the trailing complete months — when you have not.

There is no mode to choose and no settings page. The Target cell shows the
derived normal as a muted, italic placeholder in an unbordered cell; typing over
it stores a target and the cell becomes solid. **Presence of a stored row IS the
state**, per category, not per ledger.

This dissolves the failure mode that killed the alternatives. With per-month
target rows and no fallback, forgetting to set October yields SILENCE — the
screen goes blank in the month you wanted it most. With per-category fallback,
October opens with every row showing its normal; forgetting costs precision, not
the screen. "Copy last month's targets" becomes a convenience rather than a
monthly obligation.

#### D1a — A target and its ancestors are MUTUALLY EXCLUSIVE (amendment)

D1 defines the mark for A ROW. It does not say what happens when a target is
typed on a category nested under another, and D3's "the ceiling is the sum of
the table's marks" assumes marks ROLL UP. A derived normal does: a parent's
normal already describes its whole subtree, because the aggregation rolls up. A
typed target does not.

The rule: **a target may exist on a node, or on its descendants, never both.**
Each subtree therefore has exactly one authoritative level, so:

* a **targeted** node's mark IS its target — it speaks for the whole subtree;
* an **untargeted** node's mark is the sum of its children's marks plus its own
  direct normal.

D3 then holds unamended. The ceiling stays the sum over ROOTS, with no
double-count and no special case, because each root's mark is authoritative for
everything beneath it.

Rejected alternatives, and why the obvious one is wrong. *Per-row with no
rollup* — each row's mark is its own target else its own normal — is what an
implementation does by accident, and it ships a typed number with NO VISIBLE
EFFECT: the table renders roots only, so a nested target never reaches the hero
and a depth-3 category is painted nowhere. A write that changes nothing reads as
a failed write. *Nearest-target-wins with ancestors composing around it* is more
flexible and needs no invariant, but a parent's target then silently stops
meaning what it says, and it reintroduces a parent number that is neither
anybody's target nor anybody's normal.

Note what this is NOT: not an arithmetic defect of the kind that drew a ceiling
1.87× too high (see D3). Nothing double-counts under any of the three options —
an untargeted parent's derived normal remains a complete, honest description of
its subtree. This is a product decision about whether a typed number is allowed
to be inert, and calling it a correctness bug would disguise the choice as a
requirement.

WHERE THE INVARIANT LIVES. In API code, not the schema. "No ancestor or
descendant holds a target" is a recursive tree predicate, so no CHECK or unique
index can express it, and ADR-0032 makes a trigger a last resort. A conflicting
write is REFUSED with a business error naming the node that already holds the
target; the cell renders disabled with that name on hover rather than accepting
input it would then reject. Nothing typed is ever silently discarded.

Two lifecycle operations can manufacture a violation from two individually legal
states, so both need a hook alongside the merge hook D1 already implies:
**reparent**, which can move a targeted node under a targeted ancestor, and
**merge**, which reparents the source's children into the destination. The bulk
fill actions need the same guard — copying a month whose hierarchy has since
changed can produce an illegal set, and it must report what it skipped rather
than silently dropping typed numbers or writing violations.

### D2 — Derived numbers move; decided numbers do not

`typical` describes history, so it changes when history changes — a backdated
receipt, a late feed import, a category merge (which repoints legs with NO date
predicate and so rewrites months long past). That is correct for a description,
and it is why the field is called *typical* and never *budget*: the word carries
the expectation. A stored target is a decision and is frozen.

The Target cell commits IN PLACE, which ADR-0023 §B otherwise reserves against
for tabular data; §B.2 records the scoped exception and its limits. Escape
abandons a draft, an empty field clears the target rather than storing a zero,
and a cell blocked by D1a renders disabled with the conflicting category named
on hover instead of accepting a number it would refuse.

There is deliberately **no month-close ritual**. The actual side of this app
already moves for closed months, so freezing only the baseline would invent an
inconsistency that does not exist today. If frozen history is ever wanted, the
mechanism is a snapshot, which the app already has.

### D3 — The hero is a cumulative line against a ceiling; rows EXPAND in place

The screen leads with one chart: cumulative spend for the month, running inside a
shaded band of the trailing months' same-day range, stopping dead at today, with
a dashed projection continuing at this month's own rate.

* A target is a **horizontal ceiling**, not a pace line. The shaded band already
  does the pace job, and does it better than any straight line because it knows
  rent lands on the 1st. Where the projection crosses the ceiling is the DATE you
  blow the budget, which is the actionable fact.
* The hero's ceiling is the **sum of the table's marks**, so the hero and the
  table can never disagree and there is no third number to maintain.

  This held only once both sides reduced to ROOTS. The API rolls up, so a parent
  row already contains its children; the table was fixed to list roots only and
  the ceiling was not, which drew a limit 1.87× too high on real March data — the
  chart reading "comfortably under" directly above a table reading "over", with
  the crossing date suppressed entirely. The two now share one `rootRows()`
  definition, which is what makes this bullet structural rather than aspirational.
  A claim that two numbers cannot disagree is worth only the code that enforces it.
* Expanding a table row opens that category's own curve BENEATH it; the hero
  always stays the whole month. Rejected alternatives, in order: a per-row
  sparkline, because a 150×22 line chart has no presence and shrinking a chart
  is not the same as designing a small one; and swapping the hero to the selected
  category, which was built and then removed — it took the overall picture away
  at the exact moment you started investigating a part of it. Expansion also
  survives thirty categories, where a facet grid stops working.

The hero's known weakness is that it is an aggregate — a month can sit on the
spine while one category runs hot and another runs cool, cancelling. The table's
marks are what surface that, and the readout says it in words.

### D4 — No charting library

Every mark is a `<div>` with a percentage width, or one `<svg>` polyline/polygon.
`visx` (~24 KB gzip) is the named escape hatch, to be adopted behind the same
component signatures if a real time axis or brushing is ever needed.

The deciding argument was not bundle size. **Recharts would have installed a
vacuous test suite**: its `ResponsiveContainer` renders 0×0 under the no-op
`ResizeObserver` stub in `vitest.setup.ts`, so every chart test would pass while
drawing nothing — this repo's own "green preflight hides inert features" failure,
pre-installed. Canvas libraries (chart.js, uPlot) fail differently: `fillStyle`
accepts neither a class nor `var()`, there is no `getComputedStyle` bridge
anywhere in the SPA, and any bridge would be invisible to `designTokens.test.ts`.

Hand-rolling also needs **zero new tokens and zero guard edits** — every height
the design wants already exists as a pinned length, and `fill` / `stroke` / `cat`
are already guarded families.

### D5 — Colour means STATE, never category

Teal below the mark, red past it. The per-category palette appears **only** as an
8px identity dot beside the name.

This is the discipline that makes red mean something. Painting progress bars in
category colours spends the one channel that carries meaning on decoration, and
then nothing is left to say "over". An earlier draft of this work proposed
category-coloured donut segments; it was wrong for exactly this reason.

No donut at all: once a chart needs a legend, the legend is doing the work.

## Consequences

**Positive**

- The first slice needs **no migration**: derived marks require no storage, so
  the screen ships before any schema decision is made.
- The aggregation is reused, not duplicated —
  [ADR-0063](0063-mcp-server.md)'s engine already computed spend by category
  and was reachable only from the MCP tools. This is its first REST caller.
- The budget reads back OUT over MCP as `budget_progress`, so the traffic runs
  both ways: ADR-0063's engine feeds the screen, and the screen's subject is
  visible to an agent. The tool returns the rows and totals but not the per-day
  series the chart needs, and `mark` is returned rather than left to be derived
  — under D1a a parent's mark already accounts for targets set on its
  descendants, so a caller reconstructing it from `target` and `typical` would
  be wrong on exactly the rows a target was set on. Read-only on purpose: a
  target is a person's decision, and there is no reading of "the agent set my
  budget" that is better than the person typing it.
- One visual language covers budget, the dashboard tile and a future reports
  surface, because the primitives are components rather than a library's idea of
  a chart.

**Negative, accepted**

- No zero-based / envelope budgeting, ever, on this design. That is the 10% tool
  and it is deliberately out of scope. A *"left to budget"* hero number is
  specifically rejected: it only means anything under income-first budgeting.
- The hero cancels out opposing category deviations. Mitigated by the table and
  by focus, not solved.
- Hand-rolled charts mean no tooltips, no axis ticks and no brushing until
  someone writes them.

**Still open**

- Whether a targeted row should also show its normal as a second faint tick.
  Deferred to hover; two marks on one bar is what made an earlier draft
  unreadable.
- Whether the mark should compose up the tree for an untargeted ancestor in the
  UI's expanded child list as well as in the API (D1a settles the semantics; the
  rendering depth beyond the first generation is not yet decided, and
  `CategoryRows` currently builds a row's curve from root + DIRECT kids only, so
  a grandchild's spend is already missing from an expanded parent's curve).

**Decided since, in slice B**

- `budget_targets` is ledger-scoped with an RLS pair and IS snapshot-captured.
  Capture is close to structurally forced rather than merely preferable:
  `fn_ledger_snapshot_clear` deletes every `accounts` row on every restore, so a
  table excluded from the payload but carrying an `accounts` FK is either wiped
  by the cascade on each restore or breaks the restore outright. A stored target
  is also a decision about the book, the same class as a transaction, and D2
  leans on the snapshot as THE mechanism for frozen history — which is only true
  if targets are in the payload.
- `MergeCategoryAsync` repoints the source's targets to the destination and SUMS
  where both hold a target for the same month. The merge repoints every leg with
  no date predicate, so the destination's ACTUALS for months long past grow by
  the source's spend the instant it commits; under "destination wins" or
  "delete the source's", every one of those months would compare an inflated
  actual against an unchanged mark and read as newly over for a reason the user
  never chose. Summing does mutate a number D2 calls frozen, and that is
  justified as a new explicit instruction to treat the two categories as one —
  not as history drifting.
- Changing a category's `category_kind` while it holds stored targets is
  REFUSED. The alternative — allow and discard — loses a typed decision
  silently. (Note the kind flip is unguarded today for income↔expense and
  silently moves a whole history between the Spending and Income measures; that
  is a pre-existing defect this slice inherits rather than creates.)
