# 0099 — Budgeting as guideposts, and hand-rolled charts

* Status: Accepted; amended 2026-10-31 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (the current month's Spent includes its scheduled rows, and the projection adds them on their dates); D5 noted 2026-11-03 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (past the mark is by any amount, judged as the figures print; the system Uncategorized category's dot is the neutral one); D1 and the projection amended 2026-11-04 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (spent so far plus the larger of the spend still scheduled and what the band's baseline usually adds over the rest of the month, plus every credit still scheduled, never a rate; the crossing is the day the month goes past the ceiling for good; a ledger younger than the window averages each day over the months it already existed in); D5 amended 2026-11-06 by [ADR-0109](0109-overview-is-an-attention-first-home.md)'s third slice (a report chart over time keeps the category colour families and its legend, on Reports and on a home report tile, the one place on the home screen where colour does not mean state; a ranking is one neutral fill); D1 amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (the Target cell shows the reporting currency's symbol)
* Date: 2026-09-17
* Related: [ADR-0021](0021-ui-layout-and-principles.md) (layout, colour, density),
  [ADR-0023](0023-ui-ux-interaction-conventions.md) (interaction conventions),
  [ADR-0056](0056-ledger-overview-dashboard.md) (the dashboard, which deferred
  spending-by-category "no data yet"), [ADR-0063](0063-mcp-server.md) (where the reusable
  aggregation layer this consumes was specified)

## Context

Coffer had no budgeting. The obvious shape — a table of monthly targets per
category — is rejected: beside a commercial aggregator's donut, daily bars and
click-through, a ranked table looks like a database listing.

Two questions follow, and they are coupled: **what a budget in this app IS**,
and **how this app draws anything at all**, since it ships no charting library.

The design assumes three ways of using a budget: not at all; as guideposts, to
notice a month that is out of the ordinary; and as a plan to manage spending
against. It serves the first two; it is not a tool for the third.

## Decision

### D1 — A budget is a MARK, and the mark has two possible sources

Every category row carries one mark. It is:

* **your target**, when you have typed one for that category and month, or
* **your normal** — the mean of the trailing complete months — when you have not.
  *(Amended 2026-11-04: for a ledger younger than the window, of the months since its
  first transaction. See the second amendment at the end.)*

There is no mode to choose and no settings page. The Target cell shows the
derived normal as a muted, italic placeholder in an unbordered cell; typing over
it stores a target and the cell becomes solid. **Presence of a stored row IS the
state**, per category, not per ledger. *(Amended 2026-11-14,
[ADR-0114](0114-benchmarks-and-currency-conversion.md) D12 and D14: the Target cell shows the reporting currency's symbol,
muted and not typed, which is the currency the save stores.)*

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

Note what this is NOT: not an arithmetic defect like the double-counted ceiling
in D3. Nothing double-counts under any of the three options —
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
a dashed projection continuing at this month's own rate *(since 2026-11-04, from
the band's baseline instead: see the amendments at the end)*.

* A target is a **horizontal ceiling**, not a pace line. The shaded band already
  does the pace job, and does it better than any straight line because it knows
  rent lands on the 1st. Where the projection crosses the ceiling is the DATE you
  blow the budget, which is the actionable fact.
* The hero's ceiling is the **sum of the table's marks**, so the hero and the
  table can never disagree and there is no third number to maintain.

  This holds only when both sides reduce to ROOTS. The API rolls up, so a parent
  row already contains its children; a ceiling summed over every row counts each
  child twice (once on its own, once inside its parent) and draws the limit too
  high — the chart reading "comfortably under" directly above a table reading
  "over", with the crossing date suppressed entirely. The two share one
  `rootRows()` definition, which is what makes this bullet structural rather than
  aspirational.
  A claim that two numbers cannot disagree is worth only the code that enforces it.
* Expanding a table row opens that category's own curve BENEATH it; the hero
  always stays the whole month. Rejected alternatives, in order: a per-row
  sparkline, because a 150×22 line chart has no presence and shrinking a chart
  is not the same as designing a small one; and swapping the hero to the selected
  category, which takes the overall picture away at the exact moment you start
  investigating a part of it. Expansion also
  survives thirty categories, where a facet grid stops working.

*Amended 2026-10-31:* the projection carries this month's rate to date and adds each
scheduled row on its own date. See the amendment at the end of this ADR.

*Amended 2026-11-04:* the projection carries no rate. It is what is spent so far plus
the larger of the spend still scheduled and what the band's baseline usually adds over
the rest of the month, plus every credit still scheduled. See the second amendment at
the end of this ADR.

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
drawing nothing — a green suite over inert features, pre-installed. Canvas libraries (chart.js, uPlot) fail differently: `fillStyle`
accepts neither a class nor `var()`, there is no `getComputedStyle` bridge
anywhere in the SPA, and any bridge would be invisible to `designTokens.test.ts`.

Hand-rolling also needs **zero new tokens and zero guard edits** — every height
the design wants already exists as a pinned length, and `fill` / `stroke` / `cat`
are already guarded families.

### D5 — Colour means STATE, never category

Teal below the mark, red past it. The per-category palette appears **only** as an
8px identity dot beside the name. *(2026-11-03, [ADR-0109](0109-overview-is-an-attention-first-home.md):
past it by any amount, with no floor, judged on the figures as they print, for a row,
the bar and the projection's crossing alike; the system Uncategorized category's dot is
the neutral one.)* *(2026-11-06, [ADR-0109](0109-overview-is-an-attention-first-home.md)'s
third slice: one exception, a report chart over time. A series over time needs a key, so
a report chart keeps the category colour families and its legend, on Reports and on a
home report tile, each series in its family's chart shade (ADR-0021 Rule 5) and none
sharing one; a report tile is the one place on the home screen where colour does not
mean state. A ranking names each bar beside it, so its bars are one neutral fill and
carry no colour. Accepted with it: in the light theme the amber family's chart shade is
warning text's colour, and the emerald family's appears on the home screen; on a chart
each is a series named by the legend, never a state. Budget's bars and curves are
unchanged.)*

This is the discipline that makes red mean something. Painting progress bars in
category colours spends the one channel that carries meaning on decoration, and
then nothing is left to say "over". Category-coloured donut segments are wrong
for exactly this reason.

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

- No zero-based / envelope budgeting, ever, on this design. That is the
  plan-managers' tool and it is deliberately out of scope. A *"left to budget"* hero number is
  specifically rejected: it only means anything under income-first budgeting.
- The hero cancels out opposing category deviations. Mitigated by the table and
  by focus, not solved.
- Hand-rolled charts mean no tooltips, no axis ticks and no brushing until
  someone writes them.

**Still open**

- Whether a targeted row should also show its normal as a second faint tick.
  Deferred to hover; two marks on one bar are unreadable.
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

## Amendment, 2026-10-31 — the current month's Spent includes its scheduled rows (ADR-0109)

**Spent for the current month is the whole month, labelled.** It counts every expense
row dated in the month, including rows dated after today, which the register shows as
Scheduled; the hero, the table, its Total row and the over-the-mark rows all count
them. Where the month's total Spent shows (the hero, the Total row and the home
screen's This month) it reads "incl. $Y scheduled" when the month has scheduled rows.
`GET /budget/progress` reports that part as `scheduledTotal`, taken from the same daily
figures, so no browser subtracts one figure from another to find it.

**Scheduled rows never enter the pace.** Counting a bill dated late in the month as
spent early would multiply it across the month. So:

- the **rate** is the spend dated up to today over the elapsed days;
- the **projection** carries that rate to the month's end and adds each scheduled row
  on its own date (`projectedPath` in `cumulative.ts`), so it is never below Spent;
- the **crossing day** is where that path first passes the ceiling, which can be a
  scheduled bill's date.

*(Superseded 2026-11-04 by the amendment below: there is no rate. What is still
scheduled is a floor under the path, so the month-end figure is still never below
Spent, and the crossing is the day the month goes past the ceiling for good, not the
first day past it, though it can still fall on a scheduled bill's date.)*

**The curve draws it.** The dashed projection starts at today's cumulative and steps up
at each scheduled row, which is marked by a small circle (surface fill, `text-subtle`
stroke) and explained under the axis. The circles are HTML over the chart, not SVG,
because the chart's viewBox is stretched and would draw a circle as an ellipse. From
the crossing on, the dashed line is `mark-danger`. The home screen's This month draws
the same curve at `h-fixed-160px`, and its summary adds "projected past the mark on
<date>" in `state-danger` text.

*(Superseded 2026-11-04 by "The curve draws the same path" in the amendment below: the
line steps up at a bill only by as much as the bill moves the projection, and down by
all of a credit, and it is `mark-danger` wherever the path is past the ceiling, not from
the crossing on.)*

**"Day D of N" is the reader's day.** `GET /budget/progress` takes `today`, so elapsed
days follow the local date, not the server's UTC date.

A test pins that the home screen's This month and its Cash flow's This month differ by
exactly `scheduledTotal`.

## Amendment, 2026-11-04 — the projection reads the band's baseline (ADR-0109)

**Spending is not a daily rate.** A rate is the spend to date over the days elapsed,
and carried to the month's end it multiplies everything spent so far by the days in
the month over the days elapsed. A payment that leaves once a month, early in it, is
then counted many times over until late in the month, and a one-off is multiplied the
same way, so early in a month the projection is too high and its crossing date too
early. Scheduled rows were already protected from this (each counted
on its own date); a payment already gone was not.

**The projection is what is spent so far, plus the larger of the spend still scheduled
and what is usually spent over the rest of the month, plus every credit still
scheduled.** On each day from today to the month's end, the path is spent to date plus
the larger of the scheduled spend dated through that day and what the baseline adds
from tomorrow through that day, plus the scheduled credits (refunds and the like) dated
through that day (`projectedDays` and `projectedPath` in `cumulative.ts`). Scheduled
rows are split by sign per category and day, the finest the daily figures go
(`scheduledByDay`). The month-end figure ("on
track for, by month end") is the path's last point.

**The crossing day is the day the month goes past the ceiling for good.** The month's
values are its own line on the days up to today and the path after; the crossing day
(`crossingDay`) is the first day from which every value through the month's last day is
past the ceiling, and there is none when the month ends inside it. The path can come
back under the ceiling (a usual day that is net a refund, a scheduled credit), so the
first day past it is not where the month ends up: dated by the first, a month ending
inside the ceiling was told in red that it crosses it, beside a month-end figure that
does not. Dated for good, there is a crossing exactly when the month-end figure is past
the ceiling. A breach already behind today that stays is still dated the day it
happened, and a future one can still fall on a scheduled bill's date. Every place that
words or colours a crossing reads this one day: the hero's sentence for a crossing
behind today ("Past the ceiling since the …") and ahead of it, its screen-reader
summary, and This month's text and summary. With no crossing the hero reads "On track to
finish inside it." ("stay" would not be true of a path that passes the ceiling on the
way), or "Finished inside it." once the month is over. A month that has not started (a
later month the Budget page can open) has no values to project from, so it has no
projection, no crossing, no legend and no outlook sentence; its Spent, all of it
scheduled, still shows against the ceiling.

- **The baseline is the band's.** It is `dailyNormal` accumulated: the trailing
  window's mean spend per day of the month (the window's total for that day divided by
  the window's length, the rule `typical` uses, or by fewer months for a ledger younger
  than the window, below), the series the shaded band is drawn
  around. Every caller passes the projection the same baseline its band draws: the
  Budget hero and the home screen's This month. The window's months can be longer
  than this one, so what they spent on a day this month does not have counts on its
  last day (`usualByDay`), and the band ends at the window's mean month.
- **Each early payment and each one-off counts once**, in what is spent; the days
  after today add only what those days usually add.
- **A month running hotter than usual still projects higher**, because what is spent
  is real.
- **The larger, not the sum, for spend.** A scheduled bill is often one the usual month
  holds too, and adding both would count it twice. Taking the larger keeps a bill
  larger than the usual remainder landing on its own date.
- **A credit in full.** A scheduled refund or other credit is money coming back
  whatever the usual month holds, so it is added outside the larger, on its own date.
  Inside it, it would be lost whenever the usual remainder is the larger, and the path
  would end above the month's Spent, which counts it.
- **A floor of what is scheduled.** On each day the path is never below what is spent
  plus what is scheduled through that day, bills and credits alike, so the month-end
  figure is never below Spent.
- **With no usable history** the baseline is zero on every day, and the projection is
  spent plus scheduled: it ends at Spent.

**The curve draws the same path.** `SpendCurve` takes its dashed line from
`projectedDays` rather than arithmetic of its own, so the line, the figure and the date
cannot disagree. The dashed line is `mark-danger` wherever the path is past the ceiling
and the accent colour wherever it is not, each part starting where the one before ends,
so a path that comes back under the ceiling turns back. On a scheduled bill's date the
line steps up by as much as the bill moves the projection: the part of the bill that
lifts the spend scheduled through that day above what the baseline adds from tomorrow
through that same day. That can be all of the bill (the spend scheduled before it
already exceeds the baseline), part of it, or none of it (the baseline through that day
already holds all the spend scheduled). On a scheduled credit's date the line steps
down by all of the credit. The circle marks the row's date either way. When a bill or
a refund is scheduled in the rest of the month, the line under the curve says so in
plain words ("○ a scheduled bill or refund, on its date. The dashed line adds to what
you've spent whichever is larger: the bills still scheduled, or what you usually spend
in the rest of the month. A scheduled refund comes off in full."), the hero's crossing reads "If the rest of the month goes as usual, you
cross it on the …", and both charts' screen-reader summaries name the same condition.
Once the month is over (a past month, or this one on its last day) there is no rest of
the month: the hero shows no projection, and neither summary states one.

### A ledger younger than the window

**A month before the ledger's first transaction is no history, not a month of no
spending.** Every usual figure divided the window's total by the window's length
whether or not the ledger existed for the whole window, so a ledger younger than the
window read low in proportion to the months it did not exist: each row's typical, the
mark of every row without a target, the "usually" total, the shaded band and the
projection's usual remainder.

**Each day of the month divides by the window's months in which the ledger already
existed on that day**, counting from its first transaction of any kind (a live
transaction: not hidden, not merged away, not a reminder's template, as
`LedgerHistoryStart.FirstTransactionDayAsync` dates it; an account's opening date does
not count, unlike the ledger's history start in `EarliestFactAsync`), at least one and
at most the window's length (`MonthsAveragedByDay` in `BudgetProgressRepository`). A day a window month does
not have is read as that month's last day, so with the whole window every month counts
for every day. The same count serves every figure that averages the window: the daily
profile (`dailyNormal`) each day by its own count, and a row's typical and the total's
as the sum of their days' means, rolled up the category tree as the rows are. A ledger
whose first transaction falls mid-month therefore counts that month only for the days on
or after it. A ledger older than the window, or with no transactions, divides by the
window's length exactly as before. Dividing by the months that happened to have spending
stays rejected: a one-off would become a permanent expectation.

**The labels say what was averaged.** `GET /budget/progress` and MCP `budget_progress`
carry `historyFrom`, the day of the first transaction when it falls after the window's
first day. Where a label names the window it names what was averaged instead
(`usualSpan`): the true number of whole months when the history starts on a month's
first day ("your last month, same days", "unset = average of the 2 months before …",
"Fill from 2-month average", "typical, 2 months"); the day the history starts when it
starts mid-month, since the days before it then average one month fewer than the days
from it and no one number is true ("your months since …", "typical, since …"); and no
history when it starts on or after the reported month's first day ("unset = no history
before …", no caption for a band that is not drawn). With the whole window the labels
are unchanged.
