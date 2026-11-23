# 0109 — The Overview is an attention-first home

* Status: Accepted (amends 0021, 0056, 0057, 0099, 0102, 0104); amended 2026-11-03 (the
  home screen acts on review: its lists read register entries and the review count
  counts them, To review lists its rows with Review…, Recent transactions and Watchlist,
  over is over, gain incl. income in the portfolio's currency, a return's edges counted
  once, the month's projection reads the band's baseline; with it 0019 Rule 4, 0021
  Rule 5, 0023, 0024, 0025, 0027, 0028, 0029, 0032, 0035, 0040, 0043, 0049, 0051, 0063,
  0068, 0079, 0084, 0099 D1, D3 and D5, 0108 D14, 0111 D3 and D5 are amended); amended
  2026-11-06 (pins and projection: report tiles and Cash ahead join the catalog, hidden;
  a summary's top-N marks the rows it draws and carries the rest, which a chart folds
  into Other; each account a reminder is paid from is projected over a short window; a
  current balance walks the register's own index, migration 276; with it 0017, 0021
  Rule 5, 0049 D1, 0063, 0099 D5 and 0104 D2 and D4 are amended); amended 2026-11-07 (a
  current balance is bounded by the account's own running balances, migration 279);
  amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (D8 and D9: the display currency is the stored
  reporting currency, and figures that combine accounts convert into it); amended
  2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (D3: To review and Recent transactions show a transfer's
  other leg after the account chip; D8: Cash ahead places a reminder between currencies on
  both accounts, and lists one whose rate is missing as `rate-unavailable`; D9: every
  money input shows its currency's symbol, or none until the currency is known, and an
  account register's amount-range chip shows the account's; the editor's dialog layout,
  shared by Post or skip, the series editor and Review, gains a track for a transfer's
  second amount; extended 2026-11-16, D15: figures print at their currency's places, a
  security's unrealized gain is money at its minor units, and a To review row an import
  rounded shows the bank's figure in a muted second line, as does its Review… editor)
* Date: 2026-10-31
* Related: [0021](0021-ui-layout-and-principles.md) (layout and chrome),
  [0023](0023-ui-ux-interaction-conventions.md) (§H, sign colour),
  [0056](0056-ledger-overview-dashboard.md) (the dashboard this replaces),
  [0057](0057-user-preferences-store.md) (where the layout is stored),
  [0097](0097-reminder-auto-post.md) (auto-post), [0099](0099-budget-as-guideposts-and-hand-rolled-charts.md)
  (budget pace and hand-rolled charts), [0102](0102-a-problem-you-are-not-looking-at.md)
  (attention indicators), [0104](0104-reports-are-a-place-you-go.md) (reports),
  [0106](0106-folders-replace-tabs.md) (the rail and its "mixed" marker).

## Context

A ledger opens on its Overview (`/ledgers/{id}`). ADR-0056 made it a stack of widgets
(a net-worth strip, accounts, investments, upcoming reminders, recent provider runs, a
spending bar), reorderable and toggleable from Settings → Dashboard. A stack of
widgets is a list, not a screen: it gave no opinion on whether the page is for keeping
the books true or for seeing where you stand, so every widget decision was an argument
(FU-005).

What the page had to answer has also moved:

- **The rail lists every account with its balance** (ADR-0106). ADR-0056 D1 made the
  Overview the only way into a register; that job is done elsewhere now.
- **A problem was only explained on Settings → Notifications.** ADR-0102's dot trail
  ends there; the landing page said nothing.
- **Work had no home.** Reminders waiting to be posted and feed rows waiting for review
  each lived on their own page.

And the old page had defects of its own:

- Its accounts widget added balances in different currencies and said so once in a
  caption, while the rail shows "mixed" (FU-098).
- Its reminder window was built from the UTC date, not the reader's.
- Settings → Dashboard rendered the default layout when the stored one failed to load,
  and a move or toggle then wrote the default over the stored layout.
- A past occurrence of an auto-post reminder that the job had not posted showed no
  buttons, although nothing would post it.

## Decision

### D1 — Attention first, with a summary column

Each time it is opened, the Overview answers four questions, in this order:

1. Is anything broken?
2. What is waiting for me to post, skip or review?
3. Is this month on pace?
4. Where do I stand?

Every item in the work queue leaves by being done (posted, skipped, reviewed or
repaired), never by being dismissed. Position (net worth, investments, cash flow) is
always on the page, in the side column and below the work in the main one.

The page's `h1` is "Overview", as every other destination titles itself by
destination. The ledger's name stays in the breadcrumb.

### D2 — A header and two independent column stacks

- **The header** is page content, not a widget, and cannot be hidden: the `h1`, a
  "View only" chip for viewers, the **Customise** button, then the local date and the
  attention sentence (D4).
- **Two column stacks**, main (two thirds) and side, each an independent vertical stack,
  so a tall panel in one never leaves a hole in the other. The layout can also be
  `equal` (two equal columns) or `single` (main, then side, in one column).
- **No tile sizes, no grid, no resize.** Width comes from the column; height from the
  widget's own `rows` setting and pinned chart heights. ADR-0056 D4 stands. *(Amended
  2026-11-06: a report tile's `height` setting picks one of two pinned heights, compact
  or tall, as `rows` picks a list's; see "Pins and projection" below.)*
- **Breakpoints are container queries on the main pane**, not on the viewport, because
  the rail resizes and collapses. The page is an `@container/page`: at `@4xl` and above
  the columns sit side by side; from `@2xl` to `@4xl` the main widgets run full width
  and the side widgets follow two to a row; below `@2xl` it is one column, main stack
  then side stack, and the attention sentence becomes a list of link rows. Phone width
  assumes the collapsed rail, which is the reader's choice (ADR-0021).
- **Each panel is an `@container/panel`**, compact below `@xs/panel`: labels stack over
  values and names truncate, with a `title`, before figures do. Completeness flags and
  footnotes are never hidden by a container class.
- **The content is capped at `max-w-fixed-1600px`** (a new pinned token,
  `--spacing-fixed-1600px`), not the `max-w-5xl` of other destination pages: it is the
  only page with columns side by side. See the ADR-0021 amendment.

Container queries are new to the SPA. jsdom applies no CSS, so where a widget renders a
wide and a narrow form, both are in the test DOM and tests scope their queries.

### D3 — The widgets

| Key | Title | Default | What it answers | Reads |
|---|---|---|---|---|
| `problems` | Problems | main, 1 | Unresolved warning-or-above subjects, critical first; collapses to one line when clear | the rail's attention count; `GET /notifications/outstanding`; `GET /balances/consistency/state` |
| `bills` | Bills & income | main, 2 | Overdue (one row per series), today, the next 7 days, later to the horizon | `GET /reminders/upcoming`, `GET /reminders`, `GET /schedules` |
| `review` | To review | main, 3 | Rows waiting for review, counted per account, each linking to its register's review view | the rail's accounts query |
| `budget-pace` | This month | main, 4 | Spent against the mark, the pace curve, roots over the mark | `GET /budget/progress`, `GET /accounts` |
| `cash-flow` | Cash flow | main, 5 | Income against expenses per month, a net tick, and a This month / Last month / N months table | `GET /reports/summary` grouped by category kind |
| `data-health` | Feeds & checks | side, 1 | Each bank connection, prices, auto-post, backup and the consistency verdict, as facts with times | `GET /feed-connections`, `GET /snapshots`, `GET /securities`, `GET /schedules`, `GET /balances/consistency/state` |
| `net-worth` | Net worth | side, 2 | The headline, the change since the last month-end, assets and liabilities, month-end bars | `GET /overview`, `GET /reports/net-worth-history` |
| `investments` | Investments | side, 3 | Holdings, cash and their total, unrealized gain, return for the window, allocation | `GET /overview`, `GET /reports/returns`, `GET /reports/allocation`, `GET /securities` |
| `activity` | Imports & refreshes | hidden | The last provider runs | `GET /ledger-operations` |
| `accounts` | Accounts | hidden | Every account by type, with server subtotals | `GET /overview` |

*(Amended 2026-11-03: To review also lists its rows, and Recent transactions and
Watchlist join the catalog, hidden; see the amendment below. Amended 2026-11-06: report
tiles and Cash ahead join it, hidden; see "Pins and projection" below.)*

Every widget:

- is a `Panel` whose head holds an `<h2>`, a "Name →" link to its full surface and a
  row menu: Settings… (where it has settings), Hide, Move to other column, Customise
  layout…;
- fetches only while rendered, so a hidden widget costs nothing;
- is its own error boundary, showing the ADR-0023 band naming what failed, with Retry,
  and never its empty copy on an error;
- sends the reader's local date with every read of a current value;
- renders Investments only when the ledger has an investment account. In edit mode an
  absent Investments keeps its place as a frame saying when it appears.

A ledger with no accounts shows a "Get this ledger going" checklist that ticks from
real state in place of the columns, and Customise stays hidden until an account exists.

### D4 — Attention: problems, work and advisories

There are three tiers, and only the first two are ever counted.

- **Problems** are ADR-0102's outstanding subjects. The count is the rail's own cached
  value for the ledger, so the two cannot disagree. Detail comes from
  `GET /notifications/outstanding`, fetched only when that count is above zero. Each
  row carries the severity **word** in `state-*` text, the latest summary, "since" the
  first unresolved event, and a link to the page that holds the control that fixes it
  (a job's disable goes to that job's switch). The header sentence carries one
  `AttentionDot`, at the highest severity. See the ADR-0102 amendment.
- **Work** is reminder occurrences a person has to post and rows waiting for review.
  - Overdue is counted **per series**, within a fixed 90-day look-back, and a series
    whose next due date is older than that still counts, from that date.
  - The header's "to post by" horizon is fixed at 7 days. One agenda read, from 90
    days back to 30 ahead, serves the header and Bills. Fixed windows mean editing a
    widget never changes what the header counts.
  - The sentence's counts and the widgets' rows come from the same selectors over the
    same queries.
  - **An occurrence the auto-post job will post** (its series has auto-post days, the
    job's own exclusion rule finds nothing in the way, it is dated today or later, and
    the job is switched on) reads "auto-posts", with no button, and is not counted.
    **Any other occurrence is a person's**, including a past one the job has not
    posted: it offers Post or skip… and counts as overdue. Posting it by hand is safe,
    because one occurrence posts once (ADR-0097).
  - Rows to review are summed over real accounts only: a feed row's Uncategorized leg
    is also counted on the category, which would count every row twice.
  - Counts are neutral: no dot and no severity colour. The only coloured word is
    "Overdue", in `state-warning` text, with the needs-attention row bar.
- **Advisories** (the oldest price used, categories over the mark) are plain text,
  never counted, with no button. *(Amended 2026-11-06: so is Cash ahead, a projection;
  see "Pins and projection" below.)*
- **Nothing is claimed that was not checked.** "Nothing needs you today" appears only
  when every source has loaded with no work and no problem, and is followed by what was
  checked and when. A source that failed replaces its phrase with "Couldn't check …"
  and Retry.
- **No acknowledge, snooze or dismiss.** A problem leaves when its condition resolves;
  work leaves when it is done.
- **Viewers** see the sentence, the problems and every count, because events are
  readable by viewers on purpose, and no write affordance.

### D5 — Acting from the home screen

The Overview writes for the first time, owners and editors only, and only through the
writes that already exist:

- **Post or skip…** opens the existing occurrence dialog, which holds both actions and
  says when acting would skip earlier occurrences. On an overdue series it opens the
  earliest occurrence, so nothing cascades; the row menu also offers the latest, naming
  how many earlier occurrences it skips.
- **Sync now** pulls one active bank connection, as Settings → Bank feeds does.
- **Reconnect →** is a link to Settings → Bank feeds, for owners, on a connection that
  needs re-authorisation.

Viewers get none of them; the API remains the boundary.

*(Amended 2026-11-03: a fourth, Review…, opens a row's own register editor in a dialog
from To review; see the amendment below.)*

### D6 — Customising in place

- **Customise** puts the page itself into edit mode, over live data. A sticky bar at
  the top of the main pane holds the Columns choice, Reset to default, Cancel and Done;
  the chrome does not change (ADR-0021 Rule 1). Done saves once. Cancel and Esc discard
  the draft, asking first if it changed, and so does leaving the page.
- **Each widget is framed** with ↑ and ↓ (within its column), ← or → (to the other
  column), a gear (its settings) and Hide. On a phone, where there is one column, a
  "Column on wide screens" choice replaces ← and →. Links and controls inside a frame
  are inert. The frame is focusable and labelled with its place ("Bills & income, main
  column, 2 of 5"); Alt+↑ and Alt+↓ reorder, focus follows the moved widget, and a
  polite live region announces the move. There is no Alt+←/→ chord: it is the
  browser's Back and Forward.
- **Drag** is an accelerator on fine pointers, with drop targets between frames and at
  each column's end, drawn as the insertion line. No drag library.
- **A tray of hidden widgets**, each with Add, which places it at the end of its column.
  *(Amended 2026-11-03: the tray lists them in catalog order, and a widget that needs a
  subject opens its settings when added; see the amendment below. Amended 2026-11-06: at
  the report entry's place it holds "Add a report…", and any tile hidden earlier; see
  "Pins and projection" below.)*
- **Settings** open inline under the widget's head, with a live preview, Reset this
  widget, Cancel and Apply.
- **Outside edit mode**, a widget's menu hides it, moves it or opens its settings, each
  saved at once.
- **Settings → Dashboard is retired**, and with it the editor that could write the
  default over a stored layout. Its old link falls through to General.

### D7 — The stored layout, version 2

The namespace stays `dashboard` in `user_preferences`, per user and ledger.

```ts
interface DashboardPrefsV2 {
  version: 2;
  columns: 'wide-narrow' | 'equal' | 'single';
  widgets: { id: string; key: string; visible: boolean; column: 'main' | 'side';
             settings: Record<string, boolean | number | string | null | string[]> }[];
}
```

Order within a column is the order of `widgets` filtered by `column`. `id` equals `key`
for a widget that appears once. *(Amended 2026-11-06: a report tile appears once per
report, and its `id` names the report; see "Pins and projection" below.)*

- **The SPA owns the catalog**: every key's title, default column and visibility, and
  each setting's default and allowed values. A resolver turns whatever is stored into a
  complete layout: unknown keys are dropped, missing ones inserted, out-of-range
  settings replaced by their defaults. A stored document cannot break the page; the
  resolver repairs rather than rejects.
- **The API checks structure only**: `version` is 2; names and columns are non-empty,
  bounded strings; every setting value is a boolean, number, string, null or string
  array; the document is at most `PreferencesEndpoints.MaxDashboardLength` characters
  and 16 levels deep. It knows no widget
  key, column name or setting, so a new widget needs no API change. See the ADR-0057
  amendment.
- **GET** returns the stored document as stored, version 1 or 2, plus the row's
  `updated_at` as `updatedAt`, never stored inside the document.
- **PUT is a compare-and-swap.** It carries the `updatedAt` it read (null when no row
  existed); any other value is stale, refused with 422 `dashboard-prefs-stale`. The page
  then offers "Reload theirs" or "Keep mine", which saves again over the newer layout.
  `updated_at` is compared at microsecond precision.
- **DELETE** resets to the default. A viewer may reset or customise their own layout.
- **Version 1 migrates in the resolver**, because the column mapping is catalog
  knowledge, and the next save writes version 2. An empty or untouched version 1 gets
  the version 2 default. A customised one keeps its order and visibility, mapped onto
  the new keys and columns, with Bills' horizon at 30 days to match the old window.
  New widgets are inserted at fixed places with their catalog visibility. Accounts is
  hidden for every version 1 layout: the old editor forced it visible, so nobody chose
  it.
- **Before the layout loads**, the columns show skeleton frames in the last-rendered
  shape, kept per browser in `localStorage`, or the default shape. Widgets mount only
  once the layout resolves, so a hidden widget never flashes. *(Amended 2026-11-04:
  Customise waits on that read, so until it answers Customise is pending, not
  disabled, by ADR-0023 §L.)*
- **If the layout fails to load**, the default renders read-only with a banner, and
  Customise and every layout write are disabled. A default is never written over a
  stored layout.

### D8 — Every figure is computed on the server, and each widget uses its own reads

ADR-0056 D2's single aggregate call gives way to domain reads per widget: each widget
fails, caches and refreshes on its own, and a hidden one makes no request. No route is
named for the screen, and no payload carries interface copy. Every new read is LINQ on
the application context, so row-level security stays the backstop.

- **The reader's date.** Every read of a current value takes `today`, resolved through
  `ScheduledBoundary` (ADR-0056, 2026-10-21 amendment), and the SPA always sends the
  local date. With none (MCP), nothing changes.
- **One display currency.** One derivation over the net-worth accounts, ordered by
  currency code, gives a currency and whether the accounts span several
  (`DisplayCurrency`). The overview, net-worth history, summary report and budget
  progress use it. *(Amended 2026-11-13, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7: it is now the ledger's
  reporting currency, stored in `ledgers.currency_code` and chosen by the owner, which
  migration 280 set on every existing ledger from this rule. Nothing derives it from the
  accounts or reports a mix; see the amendment of that date below.)*
- **`GET /overview?since`** adds, for a month-end strictly before `today`, net worth at
  the end of that day and its change. It is computed as the overview at that date, so
  it equals the history's point for that day; before the ledger's first dated fact it
  is flagged `noHistory` instead. Anything else is 422 `overview-since-invalid`. The
  portfolio gains its cash and whether it spans currencies; each account says whether
  it is open.
- **`GET /reports/net-worth-history`** carries the display currency in place of a fixed
  one, a `noHistory` flag per point, and `today`, which ends the series at the
  overview's "now", so its last point equals the headline.
- **`GET /reports/summary`** takes `today` as a request parameter, never part of a saved
  report's spec, and resolves a sliding window at the register's Scheduled boundary.
  Results carry per-period and per-group totals, taken before top-N, so no figure on
  either screen is a browser sum of rows. See the ADR-0104 amendment.
- **`GET /reminders/upcoming`** occurrences carry their source account's id, name and
  currency, and an `autoPost` verdict from the one predicate the auto-post job also
  uses.
- **`GET /notifications/outstanding`**: one row per outstanding subject, from the same
  query as the cross-ledger attention summary.
- **`GET /schedules`** returns every job's schedule; **`GET
  /balances/consistency/state`** is the stored verdict's first REST read, and a ledger
  never checked reads "not checked yet", never unhealthy.
- **`GET /reports/returns` and `/reports/allocation`** take `today`, so they value the
  same instant as the overview.
- **`GET /budget/progress`** takes `today` for "day D of N" and reports
  `scheduledTotal` (D10).
- **A market value rounds half away from zero**, as Postgres `ROUND` does, so a value
  computed in C# and the same value computed in SQL agree in every place they carry.
  *(Amended 2026-11-16, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15: "to the cent" assumed two places. A market
  value keeps its 4-place internal bound, `ReportingScale.MarketValuePlaces`, and the
  SPA's formatter presents it at its currency's places (settled by Claude, 2026-11-16).)*
- *(Amended 2026-11-06: `GET /reports/summary` with a top-N marks the rows it draws and
  carries the rest, and `GET /accounts/balance-forecast` is new; see "Pins and
  projection" below.)*

### D9 — Money and currency

ADR-0023 §H, as amended on 2026-10-31, applies throughout: red marks a negative that is
news (a transaction amount, a change, a net below zero), totals whose sign is fixed by
what they are stay in the default colour, unposted amounts are muted, and nothing is
green.

**An aggregate that spans currencies shows the rail's "mixed" marker**, with the rail's
`title`, in place of a figure: net worth, assets, liabilities and the change, group
subtotals, the month's Spent, its scheduled part and its mark, cash-flow cells,
net-worth bars and investment totals. A chart over such totals is replaced by a line
saying the totals span currencies, and no projection or crossing date is read off them.
On the Overview, a row that belongs to one account is formatted in that account's
currency. A category row has no currency of its own and a report row carries none: each
sums what was filed under it and is formatted in the ledger's display currency;
converting is FU-106. The old summing caption is gone. *(Superseded 2026-11-13 by
[ADR-0114](0114-benchmarks-and-currency-conversion.md) D7 and D9: such an aggregate converts into the reporting currency and shows
a figure, and a category or report row converts too; see the amendment of that date
below.)*

**The Budget page follows**, since it shows the same month: its hero and its Total row
read "mixed" for Spent, the scheduled part, the mark and the usual figure, the hero
draws no curve, projection or crossing, and its summing caption is gone.

**So does Reports**, since Cash flow's head link opens the same report: its Total row
reads "mixed", and so do the rows of a report grouped by income vs expense, which are
the figures Cash flow shows; a category, account, payee or tag row keeps its figure. A
chart of totals (grouped by kind, or stacked, where each column is a period's total) is
not drawn; grouped bars and the ranked view are. See the ADR-0104 amendment.
*(Amended 2026-11-06: on such a ledger a chart's Other has no figure, and Cash ahead
draws no combined line; see "Pins and projection" below.)*

**So does Structure's Categories tab** (ADR-0111 D5, 2026-11-04 amendment): each
category's total is in the ledger's display currency, which `GET /categories` carries
beside its rows, never the code stored on the category's account row, and a kind's total
reads "mixed" when the ledger's accounts span currencies, as the month's Spent does.

### D10 — The current month's Spent includes its scheduled rows

Spent for the current month counts every expense row dated in the month, including
those after today, which the register shows as Scheduled. Wherever it shows (the Budget
page's hero, table and over-the-mark rows, and This month), it reads "incl. $Y
scheduled" when `scheduledTotal` is not zero.

- **Scheduled rows never enter the pace.** The rate is the spend dated up to today over
  the elapsed days. The projection carries that rate to the month's end and adds each
  scheduled row on its own date, so it is never below Spent, and it can cross the mark
  on a scheduled bill's date.
- **The curve** draws the projection dashed from today, stepping up at each scheduled
  row, which is marked by a small circle explained under the axis. From where it
  crosses the mark it turns `mark-danger`, and the summary says on what date.
- *(Amended 2026-11-04: the projection carries no rate. It is spent so far plus the
  larger of the spend still scheduled and what the band's baseline usually adds over the
  rest of the month, plus every credit still scheduled; a scheduled bill steps the curve
  up only by as much as it moves that, and a scheduled credit steps it down by all of
  it. The date the summary gives is the day the month goes past the mark for good. The
  dashed line is `mark-danger` wherever the path is past the mark, not from that date
  on, so it can be red before the date, and on a path that comes back under the mark
  and names no date. See "The projection reads the band's baseline" in the 2026-11-03
  amendment.)*
- **This month and Cash flow's This month differ by exactly `scheduledTotal`**, and a
  test pins it.

See the ADR-0099 amendment.

### D11 — Cash flow is a report

Cash flow is the Income and Expenses report grouped by category kind, with the same
spec, so the home screen and Reports share one definition. Its head link opens that
report. Reports labels the view in words: the grouping reads "Income vs expense", its
rows "Income", "Expenses" and "Adjustments", its Total row comes from the per-period
totals, and the URL accepts the category-kind grouping. The register accepts
`?status=needs_review`, so a To review link opens an account on its rows to review.

### D12 — New primitives

Each has a style-guide section and renders offline there:

- **`Skeleton`**, blocks at pinned heights, pulsing only under `motion-safe`; the
  loading region carries `aria-busy`.
- **Button `size="xs"`**, a 28px control, and a **`link` variant valid only with
  `xs`**, for in-row actions such as Post or skip….
- **A hatched chart `Bar`**, vertical stripes from a pattern with a `useId` id, for the
  month to date and "today".
- **`PointSelect`**, focusable buttons over a chart's points: ←, →, Home and End move,
  Enter or a click selects, and the selected point is `aria-pressed`. No chart had
  keyboard point selection before.

## Consequences

- **More requests on a cold load**, two of them shared with the rail, and one more when
  a problem is open. Widgets fail and cache independently, and a hidden widget makes
  none. A batched read can follow if a measured cold load needs it.
- **The Overview writes**, through the existing dialogs and mutations, for owners and
  editors.
- **Accounts is hidden on upgrade**, for every layout, and stays one Add away in the
  tray. The release notes say so.
- **A version 1 layout stays stored as version 1** until the first save, which writes
  version 2.
- **A standing problem sits at the top of the page until it is fixed.** That is
  accepted; acknowledging one would be its own ADR-0102 amendment.
- **Phone width assumes the collapsed rail.** There is no phone drawer.
- **What remains is FU-005**: the To review list with its review dialog, recent
  transactions, a watchlist and gain including income; then report tiles and a cash
  projection. *(Amended 2026-11-03: the first four shipped, as the amendment below
  records; report tiles and Cash ahead remain. Amended 2026-11-06: they shipped too, and
  FU-005 is closed; see "Pins and projection" below.)*

## Alternatives considered

**Summary first.** A position screen (net worth, cash flow, change) is better for a
monthly look. Rejected: it has no place for work, and at medium widths it pushes the
reminders below everything else.

**A counts endpoint for the header.** Rejected: it would be a second definition of
"overdue" and "to review" that the widget lists could disagree with, and a ledger with
nothing wrong would pay for it on every load.

**One aggregate home read.** Rejected for now: one failing part would fail them all,
and a hidden widget would still be paid for.

**The server owns the widget catalog** (the ADR-0057 way: typed records, validation
and defaults in the API). Rejected: every new widget or setting would need an API
change, for interface concepts the API has no use for. The opaque document it replaces
validated nothing; the exception checks structure and bounds.

**Keep the version 1 shape and a fixed two-column grid.** Rejected: column placement
and per-widget settings are what make the side column a choice.

**Keep Accounts visible for layouts that had been customised.** Rejected: the old
editor forced it visible, so no layout chose it.

**Acknowledge, snooze or dismiss a problem.** Rejected: a problem leaves when it is
fixed. An acknowledge lever is an ADR-0102 decision.

**Move the ledger's nav dot from Settings to Overview.** Rejected: ADR-0102's trail
stays as it is; the Overview explains, it does not join the trail.

**An Alt+←/→ chord to change column.** Rejected: it is the browser's Back and Forward.

## Amendment, 2026-11-03 — the home screen acts on review

The second slice is built: To review lists its rows and opens a row's own editor in a
dialog, Recent transactions and Watchlist join the catalog, and Investments shows its
gain. Most of what follows was decided on 2026-11-03; the one Esc rule for the editors'
lists, the trade-day rule, the investment action words and the projection's rule were
settled on 2026-11-04 and are recorded here with the rest. Where a rule belongs to
another ADR, that ADR carries it and this section points there.

### The lists read register entries (D4, D8)

To review's list and Recent transactions read one new route, and D4's per-account
review count changes its unit to match it.

- **`GET /transactions/entries`** answers one first page, newest first, of the register
  entries of every account a person reads: not a category, not a holdings sibling,
  inactive accounts included. Each entry is drawn exactly as its own account's register
  draws it (ADR-0108 D14), with that account's id and currency, and the page says whether
  more match. An entry is one register's row, so a transfer between two of the accounts
  is an entry on each, and each line of a split on another account is an entry there.
  - `needs_review=true` keeps the register's needs-review view.
  - `direction` (`all`, `inflow`, `outflow`) is a cash-flow direction, by the sign of the
    amount the entry's row shows on its account; it is not the register's paging
    `direction`. Anything else is 422 `transaction-entries-direction-invalid`.
  - `today` leaves out the entries dated after it, the register's Scheduled.
  - `limit` is 1 to 50, default 50; outside it, 422 `register-limit-invalid`, as the
    register page answers.
  - There is no cursor; the registers are where the rest is read. It is not ADR-0108
    D14's removed ledger-wide branch of `GET /transactions`, which still requires
    `account_id`.
  - Every page but `needs_review=true`, which its index serves in one statement, reads
    the ledger's headers newest first in batches that double, then the entries of those
    headers alone, through the register's same filter and key query, until the page is
    full or the headers run out. Entries order by their header first, so the page is
    the one a single statement over every leg would give, without aggregating every
    leg before the cut. The statements read one snapshot.
- **`needsReviewCount` on `GET /accounts` counts entries**, through the register's own
  needs-review filter, where it counted distinct transactions. A transaction with two
  lines on one other account counted once there while that register lists two rows; now
  the count, the register's needs-review status count and the list agree on every
  account, and all accounts are still counted in one statement. D4's sum over real
  accounts stands: the Uncategorized category still counts each imported row's line.
  No surface gives the count a noun (2026-11-04). The home header, Structure's Accounts
  tab and the rail's review dot, in its title and in the words a screen reader hears,
  say it as "N to review"; To review says it as "To review · N in M accounts" in its
  title and as the bare number on each account's link.
- **A transaction line carries `legId`**, on MCP `list_transactions` and the budget
  drill: a split's lines share their header and account, so nothing else names one of
  them apart. It also carries `currencyCode`, the currency its amount is in: the drill
  can hold lines of accounts in different currencies, and nothing converts them
  (FU-106). That is its own account's currency, unless the account is a category: a
  category holds no money of its own, so a category's line (`list_transactions` with a
  category as `accountId`) is in the currency of the account on its posting's other
  side, and only a line between two categories takes the category's own code.

### The widgets (D3)

| Key | Title | Default | What it answers | Reads |
|---|---|---|---|---|
| `review` | To review | main, 3 | The per-account counts as before, then the newest rows waiting for review across the ledger, each with Review… | the rail's accounts query; `GET /transactions/entries?needs_review=true` |
| `recent-transactions` | Recent transactions | hidden, main after Cash flow | The newest register entries across the ledger, each opening its register at the row | `GET /transactions/entries` |
| `watchlist` | Watchlist | hidden, side before Imports & refreshes | Up to eight chosen categories against their marks this month | `GET /budget/progress` (This month's request), `GET /accounts` |

**An entry row is drawn as its own account's register draws it**, through one set of
cells both lists share, so the home screen and the register cannot disagree about a
row: the date, the account, the payee, its category cell (the chip and tags, a split's
summary without its toggle) and the amount the row shows on its account, as news, in
that account's currency. A To review row leads with the provenance icon; a Recent
transactions row with the register's status badge, by its status rule (pending
included). A Recent transactions row awaiting review keeps the register's needs-review
chrome, with words for a screen reader; a To review row, which always awaits review,
leads with its provenance icon instead. Below `@xs/panel` a
row takes two lines, as Bills rows do. The category column is 128px, and 224px from a
42rem panel in Recent transactions; in To review it stays 128px at every width,
because Review… and the row menu would otherwise leave the payee too narrow to read on
a desktop panel, and the payee is what identifies a row to review. A category that does
not fit is cut, with its full name in the chip's title.

**An investment entry's category cell** carries the register's action chip (on a feed
row not yet confirmed, its hint chip: "↗" and the action; with no action and no hint,
its neutral "—" chip), in the action's one word
(below, "One word per investment action"), the security as
"TICKER · Name", then any category or transfer chip, on one line where the panel gives
the column the width. Under an amount of exactly zero (a reinvestment, an in-kind move)
the register's second line says what the event settled or cost ("settled …",
"fee …"). The shares-at-price line is left out, so the row keeps to one line.

**To review** (settings: `rows` 5/8/12/20, default 5; `groupByAccount`, default off):

- The list is read only while the counts say there is something to review. It holds its
  `rows` height while it loads, shows the error band under the counts when its read
  fails, groups under account labels when asked, and ends in a "+N more" footer: per
  account, its count less the rows the list shows of it, floored at zero, each linking
  to that register's needs-review view.
- An owner or editor gets Review… on each row and in its kebab, beside Open in
  register. A viewer gets Open → into the read-only register, and a kebab with Open in
  register only.
- After Review… closes, focus goes back to the row's Review…, or to its kebab when the
  menu opened the dialog. When that row then leaves the list, focus moves to the same
  control of the row now in its place, else of the last row, else to the panel.

**Recent transactions** (settings: `rows` 5/10/20, default 10; `direction`
all/outflow/inflow, default all) sends `today`, `direction` and `limit` and never asks
for the review flag: it shows every entry and marks those awaiting review where they
are drawn. The whole row is the link to its register at `?focus=<headerId>`, so it holds
no control of its own and nothing is edited there; focus shows as an outline, because
the review chrome's inset bar would replace a ring. Empty, it reads "No transactions yet
· Registers →".

**Watchlist** (settings: `categoryIds`, up to eight; `sort` chosen order or most over
first):

- Each row is the category's dot and name, a bar to its mark with "actual of mark", and
  what the mark is: "budgeted" when the category or anything beneath it carries a
  target (ADR-0099 D1a), otherwise "typical, N months", then how far over it is when it
  is. A watched category with no spending says "nothing spent yet" with no bar; one with
  spending and no mark yet reads "new", with no tick. Every row shares one bar scale, as
  the Budget table's do. A row opens its category's register with its subcategories.
- It reads This month's window from the same budget-progress request This month makes,
  through one hook, so the two widgets cannot disagree about a category and cost one
  request. Its figures stay in the display currency when the ledger spans currencies,
  as This month's rows do (D9).
- Whether a chosen id still names a category is decided where the widget draws it: an
  unknown id is skipped, and dropped from the list the next time its settings open.
  The picker offers the active expense categories not yet chosen. With none chosen the
  widget reads "No categories chosen · Settings…" and Apply is disabled. It never
  notifies, and has D4's two states only.

### Review… (D5)

D5's writes gain one: **Review…** opens the row's own register editor in a dialog, so a
row is accepted, folded into its match or corrected without leaving the home screen. It
reuses the editors unchanged: no one-click approve (approving is a choice of payee,
category or merge), no prefill from a merge candidate, and no bulk approve (FU-030).

- **It re-reads the row** with its register's own read for that header on that account
  (`?starting_at=<headerId>&limit=1`), under a home-owned key, never the register's
  window key, and mounts the editor only once a read made since the dialog opened has
  settled: both editors seed their drafts once. A bank account gets `TxnRowEdit`; an
  investment account gets `InvestmentTxnRowEdit`, seeded from the transaction's full leg
  set. Both go through the registers' shared edit seed and bank save.
- **Its title** is "Review: " and the payee ("(no payee)" when there is none), with the
  date, the amount in the row's own currency and the account beneath.
- **States:**
  - awaiting review: the editor saves as Accept, with the Similar and Merge candidates
    strips; a picked candidate folds the row into it through the merge route ("Fold into
    selected →");
  - accepted since the list was drawn: a plain edit, "Already accepted. Changes save as a
    normal edit.", with no strips;
  - read-only in this register: no editor, "This row is read-only here: it belongs to a
    transaction entered from <account>." and "Open in <account> →";
  - gone (deleted or merged away since the list was drawn): "This row is no longer in
    its register." with Close and no retry;
  - a failed read: "Could not load this transaction." with Retry and Close;
  - loading: "Loading…" with Close.
- **A refused save keeps the dialog open** with the reason. A saved one refreshes the
  registers, the home screen and the editors' candidate, recall and payee caches, then
  closes; the row leaves the list and the counts drop.
- **A merge candidate's tooltip names the row a merge keeps by its origin**, in both
  editors: a manual candidate as the person's own row, kept as they entered it, any
  other as the imported row, kept as it is. Both candidate reads carry the candidate's
  `origin` for it.

### Every dialog that hosts an editor

Post or skip…, the reminder series editor and Review… share one shell,
`components/ui/EditorDialog`, with a subtitle slot and a style-guide section (D12); the
shell moved there from the reminder dialogs. In it the editors take their own dialog
layout (ADR-0025, 2026-11-03 amendment): five columns on a wide card, and below 48rem of
dialog one field per row at 40px with 44px Cancel and primary buttons, the splits grid
scrolling sideways. The Similar and Merge chips take 32px on a narrow card. Esc belongs
to the dialog it was pressed in, and a tall dialog scrolls with its overlay, so a list
inside it is never clipped (ADR-0023 §L).

### Viewers (D4, D5)

"Open →" lands a viewer in a register, so **the registers offer a viewer no write
control**: no New transaction button or N key, no editor on double-click or Enter, a row
menu of Show other side and Show raw data, no Import or Sync, a status badge that does
not cycle, and a selection bar with its count and Σ and no actions. The home screen,
the rail and both registers ask `useLedgerRole` (ADR-0111 D3), whose `canEdit` is the one
predicate `canWriteLedger`; while the role is unknown nothing offers a write. The
selection summary is a read, so a viewer's bar shows its sum (ADR-0024, 2026-11-03
amendment). The API remains the boundary (ADR-0083).

### The home screen follows a register edit

Every precise register write (a save, an accept, a merge, a create, a delete, a bulk
action, an import) refreshes the home screen's reads without re-seeding the register's
window, through one helper, `invalidateHomeReads`. Writers outside the register whose
result a home read shows call it too; ADR-0079's 2026-11-03 amendment holds the one
list of them.

### Customising (D6, D7)

- **The hidden tray lists widgets in catalog order**, so a fresh layout and an upgraded
  one, which holds a newer widget at the end of its list, read the same.
- **Subject-first Add.** A widget that shows nothing until it has a subject (Watchlist)
  lands at the end of its catalog column with its settings open and focus in them.
  Apply keeps it; Cancel, Esc in the panel, or Done with the add still unapplied takes
  it back, restoring its visibility and place as they were, whatever else the edit has
  changed since.
- **Esc inside a settings panel is that panel's Cancel** and goes no further. The page's
  edit-mode Esc ignores an Esc something inside already handled; a plain Esc outside any
  panel still asks, as before.
- **Focus follows the panel.** Opening one, from wherever, moves focus to its first
  control a Tab would reach, or to the panel itself when that control is a picker,
  whose list opens on focus. Closing one moves focus to the widget's frame while the
  layout is edited, to the widget's actions button outside edit mode, and, when an add
  is taken back, to that widget's Add in the tray. The page holds each open panel's
  draft by widget id, so a panel open while its widget changes column stays open with
  its draft.
- **Inert means inert.** While the layout is edited a right-click opens no row menu, as
  a click activates nothing.
- **The resolver's `categories` setting kind** keeps ids only, each once, the first
  eight, and may be empty; whether an id names a category is the widget's to decide,
  since the resolver sees no accounts.

### Over is over (D4, D10)

**A category past its mark by any amount is over**, with no floor, on every surface that
says so: This month's rows over the mark, the Budget page's count of categories over
target, the Budget table's Difference cell, Watchlist and the mark bar. The figures are
compared as they print (`pastMark`), because a mark is often a mean finer than a printed
figure, and judging the raw numbers would call a category over while both its figures
print alike. The Difference cell prints "+" and the overage in danger whenever over, "—"
only at the mark and "−" with the shortfall under it; the bar draws its overflow
whenever spend is past the mark, never narrower than what shows past its tick.

The projection follows the same rule (`pastBy`): the Budget hero's projected figure, the
curve's red and the crossing date that This month and the hero both read call a
projection landing exactly on the ceiling, or finer than a printed figure past it, at
the mark, and one minor unit past it over.

### The projection reads the band's baseline (D10, 2026-11-04)

**Spending is not a daily rate**, so the projection no longer carries one. Spend to date
over the days elapsed, carried to the month's end, multiplies a payment that leaves
early in the month, and any one-off, by the days in the month over the days elapsed:
early in a month the figure was too high and its crossing date too early.

**The projection is spent so far, plus the larger of the spend still scheduled and what
is usually spent over the rest of the month, plus every credit still scheduled**, the
usual part taken from the same baseline the shaded band is drawn around (`dailyNormal`
accumulated: the trailing window's mean spend per day of the month, with what the
window spent on a day this month does not have counted on its last day, each day over
the window's months the ledger already existed in on that day). Day by day
from today, the path is spent to date plus the larger of the scheduled spend through
that day and what the baseline adds from tomorrow through that day, plus the scheduled
credits through that day. The month-end figure is its last point. The crossing date is
the day the month goes past the mark for good: the first day from which every value
through the month's last day (the month's own line up to today, the path after) is past
it, and none when the month ends under it, so the date and the month-end figure always
agree. A breach already behind today that stays is still dated the day the month's own
line passed it. This month and the Budget hero both pass it the baseline their band
draws, and the curve draws the same path.

- Each early payment and each one-off counts once; a month running hotter than usual
  still projects higher, because what is spent is real.
- The larger, not the sum, for spend: a scheduled bill the usual month already holds is
  counted once. A scheduled refund or other credit is money coming back whatever the
  usual month holds, so it comes off in full, outside the larger; inside it, it would be
  lost whenever the usual remainder is the larger.
- On each day the path is never below spent plus what is scheduled through that day,
  bills and credits alike, so the month-end figure is never below Spent.
- With no usable history the baseline is zero, and the projection is spent plus
  scheduled: it ends at Spent.
- A scheduled row's circle stays on its date; the curve steps up there only by as much
  as a bill moves the projection, and down by all of a credit. The dashed line is
  `mark-danger` wherever the path is past the mark, so a path that comes back under it
  turns back. A path that comes back under and ends under names no crossing: the hero
  reads "On track to finish inside it." and This month "projected to finish under the
  mark".
- When a bill or a refund is scheduled in the rest of the month, the line under the
  curve says what the dashed line adds up. The hero's crossing sentence, when the crossing is still
  ahead, and both charts' screen-reader summaries say the projection assumes the rest
  of the month goes as usual. On the month's last day, or a past month, neither summary
  states a projection: This month's reads "finished under the mark" where it would have
  said it projects under it. A month that has not started, which the Budget page can
  open, has no projection, so its hero shows no legend and no outlook sentence.
- A ledger younger than the window averages each day of the month over the window's
  months it already existed in on that day, counting from its first transaction, so a
  new install's usual figures, the band and the projection's usual remainder are not a
  fraction of what it spends. This month's and Watchlist's "typical, N months" then say
  the true count, or "typical, since …" when the history starts mid-month.

ADR-0099's 2026-11-04 amendment carries the rule in full.

### Investments: gain incl. income, in the portfolio's currency (D3, D8, D9)

The return block leads with **"Gain incl. income"**, the window's `gain`, as news: red
only when below zero, never green, and no plus. The gain is not annualized, so the
block's subhead no longer says "annualized" and each rate's label says it instead.

- **Every investment figure is in `portfolio.currencyCode`**, the investment accounts'
  own currency, not the ledger's. When the investment accounts themselves span
  currencies, the gain, both rates and every allocation figure (a bucket's share and
  bar, its hover value, the excluded cash, the undecomposed footnote's percentage) read
  "mixed" with the rail's wording, and the buckets stay named.
- **A one-year window from Feb 29** starts on the last day of February a year back,
  where it asked for a date the server refuses.
- **A money figure takes its sign, and so its colour, from the amount as it prints**: a
  residue below the last printed digit is a zero, never a red negative zero, and the
  unrealized gain's percentage rounds half away from zero and prints no sign on a zero.

On the server:

**`GET /reports/returns` adds `gain`**: the window's change in value that
contributions and withdrawals do not explain: price movement, realized or not, plus
dividends and interest, less fees and investment expenses; not annualized; for the
whole scope. It is `endValue − startValue − netContributions` exactly, from the
figures the result already holds, so it costs no query, and the browser never
subtracts one figure from another to find it (D8). It depends on the scope, as the
contributions do, so the per-account rows do not carry it. Measured from the start
value, it is neither the FIFO realized gain nor the unrealized gain against cost.

**A window's edges are counted once.** A register date is midnight UTC, and the
Investments widget asks for a window that opens at a local date's midnight UTC, so an
event dated the window's first day sits exactly on its start. Valued at that instant,
it fell into the start value and no window's return counted it. Now:

- an explicit window's start is valued at the last instant before `from`, so a
  dividend, interest or fee dated the first day is that window's return, and a
  contribution dated `from` is that window's flow, counted once;
- an explicit `to` is exclusive and valued at the last instant before it, so a flow
  dated `to` is the next window's, never in this one's end value without a flow to
  explain it;
- with no `to`, the end is the last instant of the caller's `today` (ScheduledBoundary),
  which is counted, as before, and a flow dated at it is one of the window's flows;
- bounds are compared at the microsecond, timestamptz's resolution;
- since-inception keeps its rule: valued at its anchor, with a flow landing there taken
  back out of the start value.

An allocation therefore reconciles with a window that has an explicit end at the
instant before that end; with no instant given, both value the end of `today`, as
before.

**`GET /overview` `portfolio` adds `currencyCode`**: the display currency of the
investment accounts alone, by D8's one derivation, beside its `mixedCurrency`. A
ledger mixed by its bank accounts can hold brokerages that all agree, and every
investment figure, the returns and allocation reports' included, belongs under the
portfolio's code, not the ledger's. Nothing is converted (FU-106).

### Figures that name their currency, or wait for it (D9)

The SPA knows no ledger-wide currency, so a figure formatted before its account was
known fell back to dollars. The figures below now take a currency their data carries
(the budget month's, the home entry's, the occurrence's, a line's `currencyCode`) and
otherwise print nothing until their account is known:

- the registers keep their loading state until the account list holds the account, a
  list that answers without it is the register's failed load, and New transaction and
  the N key wait for it;
- the dialogs show a placeholder or their loading line, and the series editor reads a
  deactivated source from the list that keeps inactive accounts;
- the investment register's price, fee and settled amount are the brokerage's, a Post or
  skip subtitle and a managed loan's split the occurrence's, and the budget drill's and
  a tag's lines each their own `currencyCode`;
- a security's page reads its hero's currencies from the server: `GET
  /securities/{securityId}` adds `priceCurrencyCode` (the price and the market value),
  `holdingsCurrencyCode` (the cost basis) and `holdingsMixedCurrency`. The cost basis
  reads "mixed" when the holding accounts span currencies, and so does the unrealized
  gain whenever the cost's currency is not the price's. The gain's marker is the one
  exception to D9's "with the rail's title": when the accounts share one currency and
  only the price's differs, the rail's wording (accounts in more than one currency)
  would be untrue, so its title says the price and the cost basis are in different
  currencies; when the accounts span currencies it keeps the rail's. A gain that prints
  takes its sign and its red from the figure at the price formatter's precision, so a
  residue below the last printed digit is a zero, never a red negative zero *(amended
  2026-11-16, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15: the gain is money, so it prints, and takes its sign, at
  its currency's minor units through `formatCurrency`, as the cost basis and the market
  value do; only the current price and the cost per share use `formatPrice`)*;
- the security page's trades are each in their own account's currency, which each row
  of `GET /securities/{securityId}/transactions` carries as `currencyCode` (the account
  the row names, a closed brokerage included), the way a reminder, a security's price
  and a line carry theirs, so the page reads no account list for them and prints no dash
  while one loads. The account a row names is the brokerage that owns the trade's own
  leg, never another brokerage on the same transaction: an in-kind transfer's shares
  leaving are named under the source, those arriving under the destination, and a trade
  funded from another brokerage's cash under the brokerage it was made in. When more than
  one brokerage names the holdings account that leg sits on, the row names the one whose
  currency the trade-price rule reads (ADR-0084 D2: the first by currency code in ordinal
  order, then by id), so a row and the trade price its line sets name the same currency;
- the securities catalog's price and amount are in the detail's price currency, row by
  row (`GET /securities` rows add `priceCurrencyCode`), and the settings page's
  unbacked-price advisory names each holding value's currency, printing no figure for
  one summed across currencies (ADR-0084, 2026-11-04 amendment);
- a reminder's amount, on the calendar, the list and Bills, is in its series' source
  account's currency (`GET /reminders` adds `currencyCode`); a series with no single
  source prints a dash, and no amount on a calendar chip (ADR-0049, 2026-11-04
  amendment). The home screen's money cell prints a dash for a figure with no currency,
  so no caller picks one it does not know;
- the Accounts widget reads whether a subtotal is mixed from every account the server's
  subtotal counts, the closed accounts hidden from the list included;
- the register's amount-range filter chip names the bounds as typed, with no currency
  symbol, since a register's entries need not share a currency *(amended 2026-11-14: an
  account register's chip shows the account's symbol, and only a category register's
  shows none; see that amendment)*;
- a sale's lot preview in the investment editor prints each lot's cost and the basis in
  the brokerage's currency, and no cost while the editor does not know it.

The SPA's money formatters (`formatCurrency`, `formatSignedAmount`, `formatPrice`,
`atDisplayPrecision`, `currencySymbol`, and the report chart's axis) take the currency
as a required argument, with no dollar default, so a caller that leaves it out fails
the type check instead of printing dollars.

### Two marks lose their colour

- **The system Uncategorized category is always the neutral chip and dot**, in both
  registers, the home rows and the budget dots: it is where unclassified rows land, not
  a category anyone chose. It is recognised by the account's system flag, never by its
  name, which a person's own category may share (ADR-0021 Rule 5, 2026-11-03
  amendment).
- **A file import's provenance icon is neutral**, as a manual row's is: amber only ever
  means a warning (ADR-0035, 2026-11-03 amendment).

### One Esc rule for the editors' lists (2026-11-04)

Every list in the bank and investment editors (the payee suggestions, the security
search, the category picker and the tags list) closes on the first Esc and keeps the
edit open; the next Esc cancels the edit, or in a dialog closes it. A menu's Esc gives
focus back to where the menu opened from, and an Esc the editor used never also clears
the register's selection. ADR-0023 §B.3, §B.4, §D–F and §L, amended 2026-11-03 and
2026-11-04, carry the rule.

One marking convention holds it in the code (ADR-0023 §F): a list or popover that
closes on an Esc marks it handled (`preventDefault`) and stops it (`stopPropagation`)
in the handler that closes it, and anything further out that acts on Esc (a dialog's
close, an editor's cancel, the register selection's clear, a menu) ignores an Esc
already marked. The register's date-jump popover follows it too, so its Esc never also
clears the selection, and like a menu it gives focus back to where it opened from, so
opened from an editor the next Esc cancels the edit. So do the register header's
Filter, Show and Sort popovers and a report's range, which did not close on Esc at all
(ADR-0023 §L). A web test holds every Esc handler in the SPA to its role and
fails on one it has not classified, a check added to a classified file included.

### One word per investment action (2026-11-04)

An investment action reads the same word wherever it is shown: the register's action
chip and feed-hint chip, To review and Recent transactions, a merge candidate, the
editor's action picker and the Action column of a security's transactions on its page.
The dividend actions read **Dividend**, **Reinvest** and **DividendXfr** (a dividend
paid in cash, reinvested, and received then transferred out); Buy, BuyXfr, Sell,
SellXfr, Xfr, Misc and Transfer shares keep their words. An in-kind transfer's chip
reads Transfer shares, as the picker does, in place of the stored `transfer_shares`,
and the security page's Action column reads the words in place of the stored values
(`dividend_reinvest`, `divx`, …) it printed. One table holds the words and every one
of these surfaces reads it, so no two can disagree. Only the words change: the stored
actions, the action value the API returns and the import verbs a file uses are as they
were; the investment register's Action sort orders by the word (ADR-0028). ADR-0027
carries the words (its catalog's
display-word column), ADR-0028 the chip and ADR-0029 the picker, each amended
2026-11-04.

Every word fits where the chip is placed as the longest did before: DividendXfr is as
wide as DivReinvest was, and the widest, Transfer shares, fits the register's 7rem
Action track and the home lists' 128px category column on one line. The security
page's Action column has no fixed width: the table sizes it to the words in it.

### What a trade is, and what the checks see

The stored figures the home screen reads are kept by rules recorded in their own ADRs,
and the slice tightens them: a header's change of date, action, hidden or merged state
recomputes every line it has (ADR-0032 and ADR-0040, 2026-11-03 amendments); a trade
leaving a day leaves it priced by the executions that remain (ADR-0084 D4, 2026-11-03
amendment); and a hidden or merged-away trade prices no day, one rule with valuation
(ADR-0084 D4, 2026-11-04 amendment, migration 273, function-only). A trade price is
in the currency of the brokerage that made the trade, which the security's page, the
catalog and the advisory then name (ADR-0084 D2, 2026-11-04 amendment, migration 274,
function-only), and a brokerage's holdings account keeps the brokerage's currency
through a currency edit (ADR-0019 Rule 4, 2026-11-04 amendment). The trade-price check
compares the currency, and a `holdings_currency` projection compares the holdings
account's with its brokerage's. The balances and
holdings checks now report a row that was never stored
([operations.md](../operations.md)). On an existing install the scheduled check may
report, once after upgrading, a row or a day an earlier version left stale, and its
repair clears it; no migration rewrites them.

### What remains

The third slice (FU-005): report tiles and Cash ahead, designed and approved with the
rest, to be built on its own go-ahead. *(Amended 2026-11-06: built; see below.)*

## Amendment, 2026-11-06 — pins and projection

The third slice is built: report tiles and Cash ahead join the catalog, both hidden, and
Reports' charts change with the tiles, since a tile draws a report as Reports does. Where
a rule belongs to another ADR, that ADR carries it and this section points there. With
it FU-005 is closed.

### The widgets (D3)

| Key | Title | Default | What it answers | Reads |
|---|---|---|---|---|
| `report` | the report's name | in no layout until added; main | One saved or catalog report on its own window, as a chart or a table, at a fixed height | `GET /reports/saved` for a saved report; `GET /reports/summary`; `GET /accounts` for a category chart |
| `cash-forecast` | Cash ahead | hidden, side | For each account a reminder is paid from: today's balance, and the lowest balance what is scheduled takes it to, with the day | `GET /accounts/balance-forecast`; `GET /reminders` |

### Report tiles (D3, D7)

A tile lives in the reader's layout, not in `report_pins`: ADR-0104 D2's amendment of
this date carries why.

- **The widget's `id` names the report**: `report:saved:<uuid>` or
  `report:catalog:<key>`, under the key `report`. Nothing else in the layout says which
  report a tile is, so a renamed report reads its new name. No table, endpoint or
  server code changes for tiles, and `report_pins` keeps meaning the rail.
- **At most six on the screen, each report once.** At the report entry's place, edit
  mode's tray of hidden widgets holds **Add a report…**: a menu of the ledger's saved
  reports, then the catalog's, in the order Reports lists them, with "N of 6" beside
  it. A report already on the screen is listed "· added" and cannot be chosen. With six
  on the screen the menu does not open and a hidden tile's Add is disabled. A new tile
  lands at the end of the main column with the default settings; a hidden one comes back
  with its own.
- **Settings:** `view`, chart or table (default chart), and `height`, compact or tall
  (default compact). Chart and legend, or the table, share the fixed height, and the
  legend scrolls within it.
- **Its title** is the report's name and its window, as Reports describes it; the edit
  frame, the tray, the move announcement and the menus call the tile by that name.
- **"Open report →" is its only control** beside the menu every widget has. It opens
  the report on Reports as the report's own card there does, a saved one under its id,
  so a question to change is changed there (ADR-0104).
- **It reads what Reports reads.** The summary takes the reader's `today`; a chart asks
  for its top-N as Reports' chart does and the table sends the report's own (ADR-0104
  D4). Reports' table, its currency rules (D9) and the chart's wait for the ledger's
  Uncategorized are one copy each, used by both, so a tile and its report cannot
  disagree. A ranked chart puts the server's total under its bars. The read sits under
  the home screen's key, so every write that refreshes home refreshes it.
- **A report that is gone**, a saved report deleted or no longer visible to the reader,
  draws nothing outside the layout edit. In the edit, its frame, or its row in the tray
  when it is hidden, reads "Report no longer exists · Remove", and Remove takes it off
  the layout. That Remove is the one control in a frame that stays usable while the
  layout is edited, because it changes the layout, not the tile's content.
- **The resolver** drops an id that names no report and keeps each report once; a
  stored tile shown past the sixth is kept, hidden. It inserts the widgets that appear
  once and never a tile, so a fresh layout has none.

### Cash ahead (D3, D4)

- **An advisory**: hidden by default, in the side column, with no button, and never
  counted in the header. It projects what is scheduled and nothing else, and its caption
  says so.
- **Its rows are every account in the projection** (below), by name, each with today's
  balance and its lowest, with the day; a row opens its register. The head link is
  "Reminders →".
- **The chart is one step chart** with a zero rule and the lowest point ringed, of the
  accounts combined or of one of them: the `focusAccountId` setting picks the chart,
  never the rows, and offers the accounts the projection lists. Combined is the default,
  and a stored account the projection no longer lists draws Combined. The chart's
  accessible name gives its lowest and the first day it is below zero, or that it never
  is.
- **"≈" marks a lowest whose `estimated` is true**, and no other figure.
- **The caption** reads "Projection from scheduled items", adding "includes estimates"
  when `includesEstimates` and "includes N overdue" when `overdueOnDay1` is above zero,
  and ends "not a forecast of anything unscheduled". When `excluded` is not empty, "N
  reminders not projected" follows, naming each as the Reminders list names a series,
  each a link to `?reminder=<id>` on the Reminders page, which opens that reminder's
  editor (ADR-0049 D1). The projection never reads as complete when it is not.
- **Settings:** `days` (14, 30 or 60; default 30) and `focusAccountId`, a new setting
  kind, `account`, whose options are the widget's own data; the resolver keeps a
  non-empty id or none, and whether it still names a listed account is the widget's to
  decide.
- **With Combined chosen on accounts that span currencies** the chart's place holds the
  line Cash flow shows there (D9), and the rows keep each account's figures in its own
  currency.
- **With no active reminder** it reads as Bills does, in the same words, with Create
  reminder for an owner or editor.

### The balance forecast (D8)

**`GET /accounts/balance-forecast?today&days`**, beside `GET /accounts/balances`, is any
member's read. It takes no account: the widget reads the whole scope and chooses its
chart in the browser.

- **`days`** defaults to 30 and is clamped to 1 through 60.
- **The scope is every account that is the source of an active reminder**: not a
  category, not a holdings sibling, inactive accounts included, ordered by name.
- **Day 0 is the register's current balance** for `today`, from the one read every
  current balance goes through (`AccountBalancesRepository`); a brokerage's is its cash.
- **Committed rows dated in the window** move an account on their own day, to the
  running balance each leaves there, read under the register's own visibility rule
  (`RegisterQuery.CandidateHeaders`), so a hidden, merged-away or template header moves
  nothing. They do not come from the agenda, which dates a posted occurrence by its slot
  and lists no row entered by hand.
- **Un-posted occurrences come from the agenda** (the read behind `GET
  /reminders/upcoming`), so their dates, loan payments, estimates and acknowledgement
  floor are Bills'. Its look-back is Bills' fixed 90 days, the C# constant and the
  TypeScript one held equal by `OverdueLookbackParityTests`. An occurrence dated today
  or earlier is placed on day 1, since day 0 is the register's; one dated before today
  is also counted in `overdueOnDay1`. Each template line on an account in scope applies
  as the post would write it; a loan places its computed payment on its source; an
  estimate places its amount on the source and the negation on the other line when that
  is in scope; an estimating reminder with nothing posted to estimate from places the
  amount entered, which is what the agenda gives its occurrence and Bills shows, with its
  lines as entered, and still counts as an estimate; an investment template moves only
  its cash lines.
- **Each account** carries `today`, `points` (the balance at the end of each day, day 0
  through the window's last) and `lowest { amount, date, estimated }`: the lowest point,
  the first day it is reached, and whether an estimate landed on that account on or
  before it. **`combined`** has the same shape, summed over the scope in the one
  currency it shares, and is null when the scope spans currencies or is empty; its
  `estimated` reads every account in scope. `includesEstimates` says whether an estimate
  was placed anywhere in scope.
- **What cannot be placed is listed in `excluded`**, one row per reminder with its
  reason, never guessed: `no-lines` (the template names no account),
  `override-not-placeable` (a computed amount cannot be placed on the accounts in scope:
  a loan line priced per payment lands in scope, the reminder has no source account, or
  an estimate's template lacks its source or its other line) and `cross-currency` (a
  computed amount would land on an account of another currency). *(Amended 2026-11-14:
  a reminder between currencies places its other leg, and a missing rate is listed as
  `rate-unavailable`; see that amendment.)*
- **Pinned** by `BalanceForecastEndpointTests`, which holds day 0 to the register with an
  overdue occurrence present, and each placement and exclusion rule above. The stress
  lane times it at four times the default bank volume, beside the sidebar's balances
  and the overview (`TransactionEntriesCostTests`), and checks day 0 against the
  sidebar's balance for every account there.

### A current balance is bounded by the account's own running balances (migrations 276, 279)

The forecast's day 0, the sidebar's balances and the overview read one current balance,
`account_balances_as_of_now`. Its per-account lookup named no ledger, so no index on
`txn_headers` served it, and each account's read scanned and sorted the headers dated
before the boundary. Migration 276 made it name the account's ledger and keep to the
headers the register shows, the conditions of `idx_txn_headers_ledger_posted_seq`, so
the planner walked that index back from the boundary for each account until a header
carried a running balance on it. The row is the same on every state the app writes: a
running balance's header and account share one ledger, the balance walk stores none on a
hidden or merged-away header, and the order is unchanged. Where stored balances have
drifted so that such a header still carries one, the current balance skips it, as the
register does.

*Amended 2026-11-07 (migration 279).* A walk of the ledger's headers with nothing to stop
it sooner is short only for an account with a row shortly before the boundary. For an
account with no row (a category never used, an account holding only its opening balance,
a brokerage never traded in) it reads every header before the boundary, and for one whose
last row is years back nearly every one; a long-lived ledger holds many of both. Reading
the account's own running balances instead costs every row a busy account has ever had.
Each account's lookup now counts its running balances (`idx_thab_account_ledger`), walks
the register's index back from the boundary over at most that many headers, and only when
the walk finds none reads the account's own running balances, each header by its key. It
costs at most a small multiple of the account's own row count, whatever the ledger holds,
and stays a few headers for a busy account. The bound is the account's row count, not a
tuned number, and it decides only how far the walk goes, never the answer: the walk and
the fallback apply 276's rules, the fallback to every running balance on the account, so
the row is the same as under 276 on every state.
`CurrentBalanceLookupShapesTests` holds the function, the register's newest counted row
and the net-worth history's feeder to one answer for each shape the lookup has to get
right, and `CurrentBalancePlanTests` holds the plan to the count, the bounded walk and the
fallback on a ledger with quiet accounts beside busy ones.
[database-schema.md](../database-schema.md) carries the function.

### A summary's top-N marks what it draws (D8)

`GET /reports/summary` with a `topN` of 1 or more:

- **Every row carries `drawn`**, and drawn rows never overlap. Flat, every kept row is
  drawn. Rolled up, a kept row holds its whole subtree, so it is drawn only when no kept
  row lies below it; a kept parent that has one gets a row per period it has postings
  in, with `isDirect: true`, its own `groupId` and only its own postings, always drawn.
  Without those rows a parent's own postings were drawn nowhere.
- **`rest { count, total, byPeriod, only? }`** is what no drawn row holds. It is summed
  from those postings themselves, not taken as the period total less the drawn rows, so
  that the drawn rows plus `rest.byPeriod` equalling `periodTotals` in every period,
  with rollup and without, shows they partition the money (`TopNRestTests`) rather than
  being arithmetic that holds whatever is drawn. `count` is how many series it folds, at
  the level the drawn rows are drawn at: under rollup a dropped parent and its children
  count once, at the highest such category, and a category that is not kept but has a
  kept row below it folds its own postings as one. `byPeriod` has an entry for each of
  `periodTotals`, in order, and is empty with no time bucket. `only { groupId,
  groupName, isDirect }` names the series when `count` is 1. Grouped by tag, the rest is
  on the totals' footing: sums of tag cells.
- **Without a `topN`** the response is as it was: no `drawn`, no direct row, no `rest`.
- **REST and MCP read one path**, `ReportingRepository.SummaryAsync`: the aggregation,
  then the display currency read once, through `ReportSummaryDto.From`, the one place a
  figure is withheld (D9). So MCP `transaction_summary` returns what the endpoint does,
  `currencyCode`, `mixedCurrency` and, with `topN`, `drawn`, the direct rows and `rest`
  (ADR-0063, 2026-11-06 amendment). Budget progress still calls the aggregation
  directly, several times per request.

### Reports' charts (ADR-0104 D4)

What a tile shares with Reports, which ADR-0104 D4's amendment of this date carries in
full: a chart asks for the spec's own `topN`, or every group over All (ADR-0104's
2026-11-07 amendment), and the table sends the spec's own; a chart draws the rows the server marks drawn, so a flat report's parent with
postings of its own is drawn where taking the leaves left it out, and a chart is always flat, so
it never draws the direct rows a rollup adds (ADR-0104, 2026-11-08); where a cap folded groups away the
rest are one slate Other, "Other (N)" or the one group's name, from `rest`, and past
nine series a series takes a deeper shade of a family; over time each series
has a colour family of its own, a category its chip's, and the ranked view is neutral
(ADR-0099 D5, ADR-0021 Rule 5).

### Money and currency (D9)

- **On a ledger whose accounts span currencies a chart's Other has no figure.** It would
  add currencies, so `ReportSummaryDto.From` keeps `rest.count` and `rest.only` and
  sends `rest.total` and `rest.byPeriod` as null. On the charts such a ledger still
  draws (grouped bars and the ranked view), Other has no bar: the legend lists it with
  no swatch as "Other (N) · mixed", and a ranked row shows its name with no bar and its
  figure reads "mixed".
- **Cash ahead's combined line** is an aggregate like the others: it exists only when
  every account in scope shares one currency, and otherwise the chart's place says the
  totals span currencies. A computed amount that would land on an account of another
  currency is not placed but listed (`cross-currency`), and each account's figures are
  in its own currency.
- **A report tile** follows Reports, by the same rules.

### A reminder opens from a link (ADR-0049 D1)

The Reminders page takes `?reminder=<id>` and opens that series' editor, as the list's
Edit does, so a link elsewhere (Cash ahead's footnote) can name the reminder it is
about. The parameter holds the dialog open: closing it clears the parameter in place, so
Back does not reopen it, and focus goes to the tab of the view showing. An id that names
no series opens nothing and says nothing.

### What remains

Nothing of FU-005: the three slices are built, and the entry is closed.

## Amendment, 2026-11-13 — totals convert ([ADR-0114](0114-benchmarks-and-currency-conversion.md))

ADR-0114 D7 and D9 replace this ADR's rule for a ledger whose accounts span currencies.
Wherever a passage above says that an aggregate across currencies reads "mixed", is not
charted, has no figure for Other or draws no combined line, or that nothing converts
(FU-106), it is superseded by these:

- **The display currency is stored.** D8's `DisplayCurrency` is the ledger's reporting
  currency, `ledgers.currency_code` (migration 280). Migration 280 set it on every
  existing ledger from D8's rule, so a ledger reads the same code after the upgrade as
  before. A new ledger takes it at creation, and only an owner changes it. No read
  carries `mixedCurrency`.
- **A figure that combines accounts converts.** Net worth, assets, liabilities and the
  change, group subtotals, the month's Spent, its scheduled part and its mark,
  cash-flow cells, net-worth bars, investment totals, a category's total, a report's
  totals and rows, a chart's Other and Cash ahead's combined line are converted into the
  reporting currency by ADR-0114 D7's date rule, and show a figure. A figure for one
  account stays in its own currency.
- **A missing rate fails closed.** A figure that needs a rate the ledger does not have is
  null and carries `rate_unavailable`, naming the pair and the day (ADR-0114 D9), and it
  is marked on itself. One ledger-level note under the page header says that totals are
  converted into the reporting currency.
- **What stays as it was.** A transaction line keeps its own `currencyCode` and does not
  convert. Cash ahead still lists, as `cross-currency`, a computed amount it would place
  on an account of another currency, because placing it is a per-account figure.
- **Budget targets keep the currency they were typed in** (ADR-0114 D12), and one in
  another currency than the ledger's converts like any other combined figure.

## Amendment, 2026-11-14 — transfers between currencies and money inputs ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D13, D14; extended 2026-11-16, D15)

- **The editor's dialog layout.** While the second amount of a transfer between
  currencies shows, the layout gains one track in every host of it: Post or skip…, the
  reminder series editor and Review…. The five columns on a wide card become six, with
  the second amount right of Amount, and below 48rem of dialog it takes a row after
  Amount. The splits grid gains its extra column and still scrolls sideways.
- **The lists' rows show the other leg (D3).** To review and Recent transactions draw
  each row through the register's row cells, so on a transfer between currencies the
  category cell shows the other leg's amount after the account chip, muted, as the
  register does. The account name truncates, and the amount stays whole.
- **Cash ahead places the other leg (D8).** A reminder between currencies is no longer
  listed as `cross-currency`. A fixed series places the template's other amount on the
  other account, as each template line is placed as the post would write it. An
  estimating series places on the other account the source estimate at the latest rate,
  derived from both accounts' stored rates against the reporting currency (ADR-0114
  D13), for every occurrence, an overdue one included, as a forecast day converts
  (ADR-0114 D7), so a series is still planned once. An occurrence due before today that
  is posted writes its due date's rate, so its figure can differ from the forecast's, as
  any estimate can. When the latest rate is missing, either stored rate being missing,
  the series is listed in `excluded`, one row per reminder with its reason as every
  exclusion is, under the reason `rate-unavailable`, and the row names each missing
  stored leg (account currency → reporting currency) and the day, as `rateUnavailable`
  does elsewhere (ADR-0114 D9, D13); the exclusion row, which carries the
  reminder and its reason today, gains that field. A managed loan series across
  currencies, which can never post (ADR-0097), is still listed as `cross-currency`.
- **Inputs name their currency, or wait for it (D9).** Every money input shows its
  currency's symbol, muted and not typed, from the `lib/money` formatters above; share
  quantities, exchange rates, percentages, counts and day numbers carry none. Inputs
  follow the figures' rule: a null currency, while the accounts load, shows no symbol,
  never a default. Escrow takes the loan's currency, a budget target the reporting
  currency, a category register's row its own row's currency, and the security price
  dialog its own Currency field, with no symbol for a partial or invalid code. An
  account register's amount-range filter inputs and its applied chip show the account's
  symbol. A category register's show none, because its rows can be in different
  currencies. The ad-hoc
  currency formatters that remain, the account editor's loan preview (pinned to en-US,
  with a USD fallback) and the returns model's `formatWhole`, move into `lib/money`.

*Extended 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15: each currency's minor units.*

- **Every money figure on the home screen is at its currency's places**, not two for
  every currency: the `lib/money` formatters pass the currency's minor units to `Intl`,
  so a 0-place currency prints no decimals and a 3-place one prints three. A price
  keeps `formatPrice`; a market value keeps its 4-place internal bound (D8 above) and
  prints at its currency's places through `formatCurrency`, as all money does.
- **A rounded row in To review says so.** A row whose ingested amount was rounded to its
  account's currency's places, because the bank sent more places than the currency has,
  shows a muted second line under the amount: "Rounded from <bank figure> as sent by the
  bank". The bank's figure has no currency symbol, so it is not read as an amount in the
  account's currency, and it is printed exactly as sent, in the row's stored sign
  (ADR-0114 D15) (settled by Claude, 2026-11-16). It is plain text after the amount in reading order, so a screen
  reader reads it with the row. Only that row grows by the line; the other rows are
  unchanged. The figure comes from the row's originals, `txn_header_originals.amount`,
  and the line shows while the row needs review and the originals' rounded-at-import
  flag is set (ADR-0114 D15; [ADR-0100](0100-canonical-holds-current-sidecar-holds-original.md)); both reach the widget on the register
  row its entries already carry.
- **Review… shows the same sentence.** The editor that Review… opens on such a row
  carries the sentence as its Amount field's description.
