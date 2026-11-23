# 0104 — Reports are a place you go, and a pinned report is a shortcut to it

* Status: Accepted; amended 2026-10-31 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (D4: totals across currencies read "mixed" and are not charted; D5: a sliding window ends at the reader's today; results carry per-period and per-group totals); amended 2026-11-03 by [ADR-0111](0111-structure-is-one-page.md) (D1: Accounts, Categories and Tags are one destination, Structure, so Reports is the seventh per-ledger destination, not the ninth); amended 2026-11-06 by [ADR-0109](0109-overview-is-an-attention-first-home.md)'s third slice (D2: a report on the home screen is a tile in the dashboard layout, not a pin, and needs no server change; D4: a chart asks for its own top-N and draws what the server marks drawn, the rest one slate Other from the server's `rest`, a rolled-up parent's own postings as a direct series; each series over time has a colour family of its own, and the ranked view is neutral); amended 2026-11-09 (D6: a returns series exists and Returns is a catalog report, a second view behind the same route selected by a `kind` marker in the spec; the benchmark stays out of reach); amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (D4: totals and rows convert and every chart is drawn; D6: the ledger line converts, and a Returns spec picks a benchmark); amended 2026-11-20 (D4: every group carries its full name as `groupPath`; a subcategory is shown under its path wherever nothing beside it names its parent); amended 2026-11-21 (D4: a name reads in one colour, its parents not muted; D6: each point's money-weighted return is the 12 months ending at it, read from history before the range; the Returns chart keys its lines with the square swatch and latest value, lights a line on hover, and has no column of end values)
* Date: 2026-10-01

## Context

the open-work backlog as first written (it is now only the benchmark
half; every citation of it below means that original item) "depends on the reporting layer below". That layer
exists: it shipped with the MCP server (ADR-0063), and all four primitives FU-008 says a
Reports feature must reuse exist today.

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
are renderable from what exists; one needs a new dimension (`Tag`), and three are out
of scope.

So the open questions were never about querying. They were about navigation, about
where a report a user named is stored, and about whether a report is one thing or two.
Those are the decisions here.

## Decision

### D1 — Reports is ONE rail destination, with the catalog behind it

A ninth per-ledger destination beside Overview, Budget, Accounts, Categories, Tags,
Securities, Reminders and Settings. Behind it: the canned catalog, and the list of
reports this ledger has saved.

The rejected alternative is worth naming: list every saved report in the rail, the way accounts are listed, on the reasoning that a saved
report is a return-to artifact and accounts earn their rail place the same way. The
reasoning holds and the mechanism does not — **it does not scale.** The number of saved
reports is user-chosen and unbounded, and the rail already carries the account list,
which is the one thing it cannot be allowed to crowd out. A cap makes the failure
quieter rather than absent: the user who saves a twelfth report is told the rail
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

*Amended 2026-11-06 (ADR-0109's third slice).* **A report on the home screen is a tile
in the dashboard layout, not a pin.** `report_pins` keeps meaning the rail and nothing
else. A tile is a widget whose `id` names its report, `report:saved:<uuid>` or
`report:catalog:<key>`, the same two kinds of target a pin has, stored with the rest of
the reader's layout (`user_preferences`, ADR-0109 D7). So the home screen needs no
table, endpoint or server change of its own: no route pins a report to it, adding a tile
pins nothing to the rail, and pinning a report adds no tile. A tile is added from the home
screen's own edit mode ("Add a report…"), never from Reports, at most six at a time and
each report once. As the Consequences below ask of a pin, a tile whose report is gone is
not drawn; while the layout is edited it says so and can be removed. A tile is a fixed
answer at a fixed height, with "Open report →" as its only control, so the reader who
wants to change the question goes to Reports, and the alternative rejected below
(canned reports as widgets in place of a destination) stays rejected.

### D3 — Saved reports are a TABLE, not a preferences namespace

A `saved_reports` row per report, with its own identity, not a list inside a
`reports` entry in `user_preferences`.

The preference route is the cheap one and is wrong for a reason that cannot be fixed
afterwards. `user_preferences (user_id, ledger_id, namespace, value jsonb)` from
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
shape, which is what FU-008 asked for. *(Amended 2026-10-27, ADR-0108 D12: the stored
spec names its tag by id (`spec.tagId`), the one tag reference no foreign key covers, so a
tag merge repoints every member's saved reports that filter on the merged-away tag to the
surviving one, and the upgrade's merge of case-variant tags does the same.)*

### D4 — One report with two views, not a spreadsheet list and a graphics list

Splitting the catalog into tabular reports and graphical reports is the right axis for
BUILDING — it is two renderers, a pivot grid and a chart canvas — and the wrong one for
the menu. Nobody opens a finance app wanting a graphical report; they want to know
where the money went, and then they want the table under the chart to check it against.

So a report is one object with a Table / Chart toggle, and the toggle state is part of
what a save records. Reports with only one sensible view do not render the toggle — a
transaction list is never a chart.

*Amended 2026-10-31 (ADR-0109 D9).* In a ledger whose accounts span currencies, the
table's Total row reads "mixed", as do the rows of a report grouped by income vs
expense, which are ledger-wide totals by kind; a category, account, payee or tag row
keeps its figure. A chart of totals is not drawn: grouped by kind, or stacked, where
each column is a period's total, the Chart view says the totals span currencies.
Grouped bars and the ranked view draw rows and still show. *(Superseded 2026-11-13
by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7 and D9: a report's totals and rows convert into the ledger's
reporting currency, so none reads "mixed" and every chart is drawn; a figure that needs
a missing rate is null with `rateUnavailable`.)*

*Amended 2026-11-06 (ADR-0109's third slice).* **The two views ask different questions
of top-N.** The table sends the report's own `topN` and lists every group it keeps. The
chart asked for `min(topN ?? 9, 9)`, one series per colour family (superseded 2026-11-07,
below), and draws the rows the
server marks `drawn`, never a cut the browser makes. Past that the rest are one series,
Other, in the slate no group can take: "Other (N)", or the one group's name when the
server's `rest.only` names it, with the server's `rest` for its figures, never a sum of
rows (ADR-0109, 2026-11-06 amendment). A chart is always flat (the 2026-11-08 amendment below), so a rolled-up parent's
"(direct)" row, which the table leaves out, is never drawn. A flat
report's parent with postings of its own is drawn beside its children, where the leaves
had left it out. On a ledger whose accounts span currencies Other has no figure, so it
has no bar and reads "mixed". *(Since 2026-11-13 Other converts like every other total,
ADR-0114.)*

*Amended 2026-11-07.* **The colour count does not decide how many series a chart
draws.** The nine-family cap above made a palette limit into a rule of the report: Show
read "All" while the chart drew nine series and an Other. The chart now sends the
report's own `topN`, or, over All, the largest whole number the server takes, which cuts
nothing but still has the server mark the rows it draws; so All is every group, and
Other exists only where a cap folded groups away. Past nine series a series takes a
deeper shade of a family already used (the family's token mixed 65% and then 35% with the
theme's text colour, so a shade only gains contrast on the card), and past three shades
the colours repeat. A repeat is told apart by name, not by colour: hovering a segment or a
legend entry lights that series and dims the rest, and a segment's tooltip names its
series, period and figure. The pair rule below holds for the nine families; a shade is
held to 3:1 on the card and to a visible step (ΔE2000 4) from the shade before it, a
perceptual floor, because the dark themes' pastels have little room to get lighter.

*Amended 2026-11-08.* **Roll-up is a table's option.** A parent's subtotal includes its
children, so a chart that drew it beside them would count the money twice; the chart
drew the leaves and the parent's own postings as "(direct)", the same pieces roll-up
off draws, so the control changed only a colour and a label. The chart view (Reports and
the home tiles) no longer offers Roll up and always requests the flat grouping, whatever
the report's saved setting; the table keeps it. The chart's "(direct)" naming is gone;
the server still sends direct rows under a cap with roll-up, for the MCP tool, and the
table leaves them out.

**Colour over time, none in a ranking.** A series over time needs a key, so each has a
colour family of its own, unique within the chart, and the legend shows the colour
drawn. A category takes its own family, the one its chip and dot have (ADR-0021 Rule 5),
larger categories first. A category whose family is slate, the ledger's Uncategorized
(whose own is the neutral, which is no series colour), or one a larger category in the
chart already holds borrows the first family no series holds, in a second pass, so a
borrow never takes the family a smaller category would have kept. Payee, account, tag
and kind charts take families by rank; a tag's colour is user data outside the token set
and is not used. Each family is drawn in its chart shade (ADR-0021 Rule 5). A ranking
names every bar beside it, so its bars share one neutral fill (ADR-0099 D5).

**A series keeps its colour and its name.** A category chart waits for the accounts list,
which alone says which category is the ledger's Uncategorized, showing Running until it
has it, and keeps per ledger the first answer it got: the list's, or none when the list
cannot be read. A later read does not recolour what is drawn. A group is named one way,
by one mapping, in the table, the legend, the ranked bars and the accessible text, so a
kind reads "Income", "Expenses" or "Adjustments" on the chart too, where it read the
stored kind. Switching between Table and Chart keeps the previous result, and the
controls, in place while the other request runs.

*Amended 2026-11-20.* **A subcategory is named by its path wherever nothing beside it
names its parent.** A category's own name alone is ambiguous: two branches can each hold
a subcategory of the same name, and a leaf shown without its parent does not say which
branch it is in. The server sends every group's full name as `groupPath`, on each row,
each group total and `rest.only`: for a category, the names from its top-level category
down to its own; for every other dimension, the one name (an account's sidebar folder is
not part of its name). It is a list, not a joined string, so a name containing the
separator cannot be split wrongly. `groupName` stays the leaf. What to show is the
view's call:

- **The chart** names every series by its whole path, joined with `/` as the register
  joins it, in the legend, the ranked bars, the tooltips, Other's single named group and
  the accessible text. A chart is flat and never draws a parent beside its child.
- **The table** names an indented row by its own name, since its parent is the row
  above it. A row at the top level whose parent has no row in the result (flat, or a
  parent with nothing in range) is named by its whole path. Siblings sort by path, so a
  top-level subcategory sits where its shown name puts it; under one parent that is the
  order of their own names.
- Where a name is cut to fit, the parents give way first, and the leaf truncates only
  once they are gone; the whole name is the title.
- *Amended 2026-11-21.* The whole name reads in one colour, the colour of the text
  around it. Muting the parents was meant to point the eye at the leaf, which tells
  series apart, but beside a muted parent the leaf read as highlighted.

The MCP `transaction_summary` tool returns `groupPath` too, and its description tells the
client to name a category by it.

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

*Amended 2026-10-31 (ADR-0109 D8, D11).* "From today" is the reader's today.
`GET /reports/summary` takes `today` as a request parameter, never stored in the spec,
and resolves a sliding window at the register's Scheduled boundary
(`ScheduledBoundary`): it ends where the register's Scheduled rows begin, and an
aligned window snaps from the last instant the register counts, so on a period's last
day it is not a period late. A caller with no date (MCP) gets the register's fallback.
Results also carry per-period and per-group totals, taken before top-N, so a Total row
is a server figure and never a sum of the rows shown. Grouped by tag, those totals
count a posting once per tag it carries, and the view's footnote says so. The view
names a category-kind grouping "Income vs expense" and its rows "Income", "Expenses"
and "Adjustments"; it is the home screen's Cash flow.

### D6 — The chart canvas extends the SpendCurve idiom; surfaces come first

*Added 2026-10-08, amending D4.*

**Build order: HTTP surfaces before the canvas.** D4 names two renderers, and
`ReportingRepository.SummarizeAsync` yields two shapes — a ranked list and a group ×
period matrix. But `AccountsReportingRepository` and `InvestmentReportingRepository`
produce a continuous time series (`NetWorthHistoryAsync`, `PriceHistoryAsync`) and a
composition of a whole (`AllocationAsync`), and none of them has a web surface. A canvas
designed against the two reachable shapes would be redesigned when the other three
arrive, so the surfaces land first and the canvas is designed once, against all of them.

**Two shapes stay out of reach even then, and it is DATA, not drawing.**
`ReturnsAsync` yields SCALARS for one window — TWR, MWR, start and end value, net
contributions — so an indexed performance CURVE needs a returns series that nothing
computes. And a benchmark comparison needs market index data; Coffer ingests prices
only for securities a ledger holds. Both are product decisions, not chart work.

*Amended 2026-11-09: the returns series exists; the benchmark does not.*
`InvestmentReportingRepository.ReturnsSeriesAsync` computes a cumulative time-weighted
index per month, quarter or year end (never a day), served by
`GET /reports/returns-series` and the MCP `returns_series` tool (ADR-0063). It is the
same chain the scalar `returns` reports, so the last point equals that figure; it is
not annualised; a period with no figure is a null with a reason, never zero and never
the previous value. The Reports catalog gains a **Returns** entry. It is a second view,
not a second route: the report spec carries a `kind` marker (`'returns'`), and the one
`/reports/view` route renders the Returns view for it and the aggregation for anything
else. A spec with no marker is an aggregation, so every row saved before a second report
existed reads as it did, with no migration. Pins and home tiles resolve through the
same route. The view is a chart (Index or Gain, a ledger line and a line per account),
a table, an empty state, and a mixed-currency marker: on a ledger that spans currencies
the ledger-wide series is null and says so, while each account keeps its own. What stays
out of reach is the benchmark: it needs a market-index data source, which is a product
decision (FU-008). *(Amended 2026-11-13, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D1 to D3 and D7: the ledger-wide
series converts into the reporting currency, unhedged, and a Returns spec can pick one of
the ledger's benchmark funds, whose total return is drawn beside every series; the pick is
saved in the spec.)*

*Amended 2026-11-21: each point's money-weighted return is the 12 months ending at it.*
It was the annualised money-weighted return from the window's start, shown once a year of
invested time was covered, so every range's first year read "under a year covered" however
much history lay before it, and choosing a range bought a year of blanks. Each point now
carries `mwrTrailingYear`: the money-weighted return the scalar `returns` gives for the 12
months ending there as its window (valued at the instant before the year opens, the year's
flows between, valued at the point), read from the scope's history before the range where
the year reaches back past it. A year is a year, so nothing is magnified; a year that
opened with nothing invested is null with `under_a_year_covered`. A funded opening balance
counts from its brokerage's start date (ADR-0063, amended the same day), so a ledger year
that closes before a brokerage opened is the other brokerages' alone, and one it opens
inside takes the balance as a contribution on that day. The series reads the year before the range by resolving each scope from the
first point's year and keeping, as the range's own, what falls inside it: the perimeter
and the flow classification do not depend on the start, and flows post at their header's
instant, so this is the scope resolved from the range's start. The valuation stays one
batched statement per feeder, with each point's year-opening instant among its instants.
A rate missing only for that year leaves the money-weighted figure alone null, the point
naming the rate. The table's column reads "Money-weighted, 12 mo"; its first row in a range
of dates is the range's start, not the first invested point, and says nothing.

*Amended 2026-11-21: the Returns chart keys its lines as every chart keys its series.* The
legend gives each line the square swatch the aggregation chart's key uses (the shared
`LegendChip` and `LegendEntry`) and its latest value beside its name, as that key gives a
series its total. The end values that stood in a column at the plot's right edge, coloured
to their lines, are gone: stacked there they read as an axis. Hovering a legend entry or a
line lights that line and dims the rest, as the aggregation chart lights a series; over a
line, a tooltip names it, the period end nearest the pointer and its value there.

**Hand-rolled SVG, generalised from
[`SpendCurve`](../../src/Web/src/routes/ledgers/budget/SpendCurve.tsx) — not a
charting library** such as Apache ECharts. `SpendCurve` is a real chart — cumulative
line, shaded normal band, projection split at the ceiling, today marker — and it is a
house idiom with three deliberate properties a canvas library would give up:

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

*Amended 2026-11-06: period labels thin by the width of their columns, still without
measuring.* Each period label sits centred under its own column, and no two labels ever
overlap. Each column and the axis row are CSS containers, and a label is drawn only
while its column is wide enough for its text plus a gap, at the widest spacing it
belongs to (`components/chart/axisLabels.ts`). As a chart narrows, the labels thin to
every second, fourth and so on from the first. Each spacing divides the next, so one
threshold per label suffices. The first label is always drawn, and the last while the
axis can hold it beside the first, moved back inside the plot when it is wider than its
column. This replaces the count-based thinning that stopped at twelve labels, so a wide
chart labels every period that fits. Gridlines are drawn under the marks and the zero
rule over them. The property in point 2 holds: nothing reads the container's size.

## Consequences

The SPA gains a route and two renderers; the API gains one table, its CRUD, and
`ReportGroupBy.Tag`. No existing query is rewritten, which is the point of sitting on
ADR-0063's layer.

`ReportGroupBy.Tag` inherits a question ADR-0103 created. Tags now exist at header and
posting level, so grouping by tag has to say which it means. A header-level grouping
cannot see "Project B" on one leg of a payment split across two projects, which is the exact case ADR-0103
was written for — so Tag Summary that groups only by header tags would ship already
unable to answer the question tags were extended to answer.

`ReportSpec` currently has `TagId` as a FILTER, and ADR-0103 D5 settled that filtering
spans both levels. Grouping should follow the same rule, which means a tag group-by can
return a transaction under more than one tag and its column total will exceed the
transaction count. That is correct and has to be stated on the report, or it reads as a
bug. *(Amended 2026-10-27, ADR-0108 D2: ADR-0103 D5 is replaced. A report's tag filter
and tag grouping read the real account's perspective: a posting counts the tags in play
in the paying account's register, and on a split entered from that account the
transaction's tags count for every line. A tag group-by can still place one posting
under several tags.)*

FU-009 (a realistic anonymized demo import) stops being adjacent and becomes a
dependency: `data/samples/moneydance-export-demo.json` is all-uncleared and
investment-only, so every spending report renders empty against it. Without it the
work is verifiable only against someone's real ledger, and a feature checked only
against one person's data has not been shown to work on anyone else's install.
*(Noted 2026-11-18, ADR-0115: that file is gone. A Demo is generated when it is added: five
years of a household's bank, card, loan and investment history ending that day, its
spending posted to expense categories and its rows cleared or uncleared by a statement
rule, then given examples that include half of one statement reconciling and saved
reports of its own, so a spending report has rows to read.)*

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
because the home screen had to settle the same question and landed on per-ledger
([ADR-0109](0109-overview-is-an-attention-first-home.md)). Reports follow it; a cross-ledger report needs the FX work (FU-008's v3)
before it means anything.
