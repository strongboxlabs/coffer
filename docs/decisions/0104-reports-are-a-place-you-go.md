# 0104 — Reports are a place you go, and a pinned report is a shortcut to it

* Status: Accepted
* Date: 2026-10-01

## Context

the open-work backlog has been shaped since 2026-08-28 and never
scheduled, on the stated premise that it "depends on the reporting layer below". That
premise has expired: the layer shipped with the MCP server (ADR-0063), and all four
primitives FU-008 said a Reports feature must reuse exist today.

| primitive | where | serves |
| --- | --- | --- |
| transaction aggregation | `ReportingRepository.SummarizeAsync`, over the override-aware `resolved_transactions` view | Expenses, Income, Income & Expenses, Cash Flow |
| transaction query | `ReportingRepository.ListTransactionsAsync` | Transactions / Transaction Filter |
| balances over time | `AccountsReportingRepository` — `NetWorth`, `NetWorthHistory`, `AccountPortfolio` | Net Worth, Account Balances |
| investment roll-ups | `InvestmentReportingRepository` — `HoldingsSnapshot`, `InvestmentIncome`, `RealizedGains`, `Returns`, `Activity`, `Allocation` | Portfolio, Allocation, Cost Basis, Capital Gains, Performance |

[`ReportSpec`](../../src/Api/Contracts/ReportingDtos.cs) already carries ledger, date
range, tag filter, measure (`Spending` / `Income` / `Net`), time bucket
(`None` / `Month` / `Quarter` / `Year` / `Day`), group-by (`Category` / `Account` /
`Payee`), rollup and top-N. Of the parity reports FU-008 enumerates, roughly fifteen
are renderable from what exists; one needs a new dimension (`Tag`), and three were
taken out of scope in the same pass that produced this ADR.

So the open questions were never about querying. They were about navigation, about
where a report a user named is stored, and about whether a report is one thing or two.
Those are the decisions here.

## Decision

### D1 — Reports is ONE rail destination, with the catalog behind it

A ninth per-ledger destination beside Overview, Budget, Accounts, Categories, Tags,
Securities, Reminders and Settings. Behind it: the canned catalog, and the list of
reports this ledger has saved.

The rejected alternative is worth naming because it was the recommendation: list every
saved report in the rail, the way accounts are listed, on the reasoning that a saved
report is a return-to artifact and accounts earn their rail place the same way. The
reasoning holds and the mechanism does not — **it does not scale.** The number of saved
reports is user-chosen and unbounded, and the rail already carries the account list,
which is the one thing it cannot be allowed to crowd out. A cap would have made the
failure quieter rather than absent: the user who saves a twelfth report is told the rail
is full, which is the app's constraint leaking into their work.

### D2 — Any report can be PINNED to the rail, saved or not

Pinning is its own act, orthogonal to saving. A user may pin a canned report —
"Expenses, by category" straight from the catalog, never named, never saved — or a
saved one. Nothing is pinned automatically.

This is what makes D1 scale where the rejected alternative did not. The rail holds what
someone chose to put there, so its length is a decision rather than a consequence, and
no cap has to exist. It also means the two things a user does with a report are
genuinely separate: *saving* records a question so it can be re-asked, *pinning* says
this is a question I ask from anywhere.

A pin therefore has to reference both kinds of target — a `saved_reports` row, or a
catalog preset by key. Pins are per-user: a shared ledger's members do not share a rail.

### D3 — Saved reports are a TABLE, not a preferences namespace

A `saved_reports` row per report, with its own identity, not a list inside a
`reports` entry in `user_preferences`.

The preference route was the cheap one and it was wrong for a reason that cannot be
fixed afterwards. `user_preferences (user_id, ledger_id, namespace, value jsonb)` from
migration 134 is a **singleton document per namespace** — `quotes` and `dashboard` are
each one blob with their own endpoint pair in `PreferencesEndpoints`. Three
consequences follow, and only the third is decisive:

* Every save rewrites the whole list, so two devices editing different reports
  last-writer-wins one of them away.
* No report has a stable id, so no report has a URL.
* A preference is **per-user, permanently**. Coffer is multi-user on a single ledger
  (ADR-0024, and FU-035 exists because of it). A household that cannot show each other
  a report has a missing feature, and retrofitting sharing onto a per-user blob means
  migrating it into the table this decision would have created.

A row carries an owner and a visibility column, so author-only is the default and
ledger-wide is a column change rather than a redesign. `ReportSpec` is stored as-is:
it is already serializable, and the MCP tool parameters and the saved row share the one
shape, which is what FU-008 asked for.

### D4 — One report with two views, not a spreadsheet list and a graphics list

Splitting the catalog into tabular reports and graphical reports is the right axis for
BUILDING — it is two renderers, a pivot grid and a chart canvas — and the wrong one for
the menu. Nobody opens a finance app wanting a graphical report; they want to know
where the money went, and then they want the table under the chart to check it against.

So a report is one object with a Table / Chart toggle, and the toggle state is part of
what a save records. Reports with only one sensible view do not render the toggle — a
transaction list is never a chart.

### D5 — A date range is a MODE: sliding window or static range

`ReportSpec` today has `FromUtc` / `ToUtc`, which can only express a fixed window. That
is the wrong default for the common case: "Groceries, 12 months" means the last twelve
from today, and a spec that froze Oct–Sep returns a progressively staler answer every
month it is re-run, while looking like it worked.

Both are wanted, so the date selection carries the choice explicitly — a sliding window
(N days / months / quarters / years back from today, optionally to date) or a static
range. The spec gains a representation for the relative form rather than having the SPA
resolve it to absolute dates before saving; resolving at save time is the same bug as
freezing, one layer up.

This lands in the spec shape now because it is nearly free now and a data migration
later. Every saved row written before the mode exists would have to be interpreted, and
there is no way to tell from `FromUtc`/`ToUtc` alone which of the two the author meant.

### D6 — The chart canvas extends the SpendCurve idiom; surfaces come first

*Added 2026-10-08, amending D4.*

**Build order: HTTP surfaces before the canvas.** D4 names two renderers and the
shape space looked like two — a ranked list and a group × period matrix, both from
`ReportingRepository.SummarizeAsync`. That was one repository's worth of imagination.
`AccountsReportingRepository` and `InvestmentReportingRepository` produce a continuous
time series (`NetWorthHistoryAsync`, `PriceHistoryAsync`) and a composition of a whole
(`AllocationAsync`), and none of them has a web surface. A canvas designed against the
two reachable shapes would be redesigned when the other three arrived, so the surfaces
land first and the canvas is designed once, against all of them.

**Two shapes stay out of reach even then, and it is DATA, not drawing.**
`ReturnsAsync` yields SCALARS for one window — TWR, MWR, start and end value, net
contributions — so an indexed performance CURVE needs a returns series that nothing
computes. And a benchmark comparison needs market index data; Coffer ingests prices
only for securities a ledger holds. Both are product decisions, not chart work.

**Hand-rolled SVG, generalised from
[`SpendCurve`](../../src/Web/src/routes/ledgers/budget/SpendCurve.tsx) — not a
charting library.** An earlier draft of this section chose Apache ECharts, on a brief
of "no limited capabilities", having grepped for chart code and concluded there was
none. That was wrong: `SpendCurve` is a real chart — cumulative line, shaded normal
band, projection split at the ceiling, today marker — and it is a house idiom with
three deliberate properties a canvas library would give up:

1. **It colours itself with theme classes** (`stroke-accent`, `stroke-state-danger`,
   `fill-surface-hover/40`), so both themes follow the tokens and the contrast audit
   can see it. Canvas text is not in the DOM and no DOM-based check reaches it.
2. **It does not measure its container.** One `viewBox` with
   `preserveAspectRatio="none"` fills the space without a ResizeObserver — and its own
   comment says why: the test environment stubs ResizeObserver to a no-op, so *a
   measuring chart renders 0×0 while every assertion passes*. A library that measures
   would ship every chart unverified in this repo's harness.
3. **Its 1px rules do not scale.** The ceiling is drawn as HTML over the SVG precisely
   so the same component is a hairline in a table row and a hairline in the hero, where
   a scaled SVG stroke would be neither.

**The ceiling is interaction, not shapes.** Against the shapes this feature wants —
area fills, zero-baseline lines, sparklines, stacked and grouped bars — SpendCurve's
approach already does the work. Donut and gauge arcs are one piece of path maths used
twice; a candlestick is rects and lines. What is genuinely expensive to hand-roll is
`dataZoom`, brush selection and crosshair tooltips. **That is the trigger for
revisiting this**: an interaction we actually want and cannot reasonably build — not a
shape.

**What the library would have cost, measured, so the decision is not re-litigated from
guesses.** Lazily imported, ECharts moves the main bundle 273.55 KB → 274.27 KB gzip and
puts 216.28 KB gzip in its own chunk. The fixed cost dominates: bar + line alone is
180 KB, and pie + gauge + candlestick + dataZoom add 31 KB. So the bundle was never the
argument against it — the three properties above are.

The kit lives in `src/Web/src/components/chart/`, and `SpendCurve` refactors onto it
rather than sitting beside it. One idiom, or this decision has bought nothing.

## Consequences

The SPA gains a route and two renderers; the API gains one table, its CRUD, and
`ReportGroupBy.Tag`. No existing query is rewritten, which is the point of sitting on
ADR-0063's layer.

`ReportGroupBy.Tag` inherits a question ADR-0103 created. Tags now exist at header and
posting level, so grouping by tag has to say which it means. A header-level grouping
cannot see "Property B" on one leg of a rental split, which is the exact case ADR-0103
was written for — so Tag Summary that groups only by header tags would ship already
unable to answer the question tags were extended to answer.

`ReportSpec` currently has `TagId` as a FILTER, and ADR-0103 D5 settled that filtering
spans both levels. Grouping should follow the same rule, which means a tag group-by can
return a transaction under more than one tag and its column total will exceed the
transaction count. That is correct and has to be stated on the report, or it reads as a
bug.

FU-009 (a realistic anonymized demo import) stops being adjacent and becomes a
dependency: `data/samples/moneydance-export-demo.json` is all-uncleared and
investment-only, so every spending report renders empty against it. Without it the
work is verifiable only against someone's real ledger, and a feature checked only
against one person's data has not been shown to work on anyone else's install.

Pins are per-user while saved reports may be ledger-wide, so a ledger-wide report that
its author deletes has to deal with other users' pins to it. Deleting the row and
leaving dangling pins is the shape to avoid; a pin that resolves to nothing should
disappear from the rail rather than render an error.

## Alternatives considered

**Saved reports listed in the rail automatically (the "accounts treatment").** The
reasoning was that a named, returned-to report is the same kind of object as an
account, which is true. Rejected on scale: unbounded, user-grown, and competing with
the account list for the one surface that must always show accounts. D2 keeps the
benefit — one-click access to the reports you actually use — and pays for it with an
explicit act instead of a cap.

**No Reports destination: canned reports as Overview widgets, plus "save this view"
from the register's filter bar.** The cheapest path, and not absurd — the Overview
already renders six configurable widgets and the register already filters on search,
dates, amounts, counterparty and tag. Rejected because a widget is a fixed answer at a
fixed size. What a report does that a widget cannot is let the reader change the
question: swap the dimension, move the window, drill into a row. Collapsing reports
into widgets means every new question needs new code, which is the opposite of the
preset-`ReportSpec` model FU-008 chose.

**Reports as a cross-ledger surface.** Not considered seriously, and recorded only
because FU-005 had to settle the same question for the home screen and landed on
per-ledger. Reports follow it; a cross-ledger report needs the FX work (FU-008's v3)
before it means anything.
