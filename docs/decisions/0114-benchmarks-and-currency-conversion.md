# 0114 — Benchmarks and currency conversion: one outside market-series layer, two consumers

* Status: Accepted
* Date: 2026-11-12
* Closes: FU-008's benchmark half, FU-106
* Related: [0022](0022-txn-headers-and-legs.md) Rule 2 (mirrored legs, amended by D13),
  [0023](0023-ui-ux-interaction-conventions.md) §H and
  [0073](0073-investment-amount-authoritative.md) (money at 2 places and the leg's scale
  CHECK, amended by D15), [0031](0031-ingest-provider-pattern.md) and
  [0042](0042-qif-file-importer.md) (CSV layouts and QIF, which gain D15's decimal rules),
  [0033](0033-quote-providers.md) and [0054](0054-market-data-quote-provider.md)
  (quote providers, the Yahoo adapter, no egress from a bank sync),
  [0037](0037-snapshots-and-backups.md) and [0087](0087-snapshot-payload-server-side.md)
  (snapshot capture),
  [0055](0055-generic-provider-run-audit.md) and [0086](0086-mcp-write-observability.md)
  (`ledger_operations`),
  [0057](0057-user-preferences-store.md) (the `quotes` preference and `enabledProviders`),
  [0070](0070-daily-closing-price-model.md) and [0084](0084-trade-derived-security-prices.md)
  (one value per day, the source ladder),
  [0099](0099-budget-as-guideposts-and-hand-rolled-charts.md) (budget targets),
  [0100](0100-canonical-holds-current-sidecar-holds-original.md) (the originals sidecar,
  which gains the bank's amount in D15),
  [0104](0104-reports-are-a-place-you-go.md) (saved reports),
  [0109](0109-overview-is-an-attention-first-home.md) D8 and D9 (the display currency, no
  cross-currency sums).

## Context

FU-008 wants the Returns report compared with a benchmark. FU-106 wants a ledger whose
accounts span currencies to have totals anyone can read as a number. Both need a per-day
series that no ledger holds: a fund's total-return closes, and exchange rates. The quote
pull prices only held, auto-priced securities, and `security_prices` cannot hold either
series: its composite FK ties every row to one held security, and its 4dp scale is too
coarse for a rate.

Three defects sit in the same code and are fixed here. They are not options:

- The scalar `returns` (MCP and `GET /reports/returns`) sums brokerages in different
  currencies and runs both rates over those sums, while `returns_series` withholds the
  ledger line.
- `QuoteOrchestrator.BuildContextAsync` stamps every requested quote `USD`
  (`QuoteOrchestrator.cs:307`), and Yahoo never reads the response's currency, so a price
  fetched for a fund quoted in another currency is stored as USD.
- Valuation never reads `security_prices.currency_code`. Its one read, at
  `274_trade_prices_name_their_currency.sql:565`, is a consistency check. A position
  priced in one currency is added to its account in another, even on a ledger where every
  account agrees.

## Decision

### D1 — Benchmarks: up to three per ledger, picked per report

A ledger keeps up to three benchmark symbols (`ledger_benchmarks`, unique per ledger and
symbol). The cap is checked in the API, with no trigger (ADR-0032). Owners and editors
manage the list in a Benchmarks section on Settings → Quotes, under the Yahoo switch.

The Returns report's Benchmark control is a popover listing None and the ledger's
symbols. An option that cannot be drawn is disabled and gives its reason. The pick is
saved in the report spec (ADR-0104). The control hides in the Gain view, as Show hides in
Table, and the pick stays in the spec. When the ledger has no benchmarks, the popover
lists None and points to Settings → Quotes.

Removing a symbol asks first. The confirmation says: "Saved Returns reports that compare
with VT will show no benchmark until VT is added again." A saved spec keeps its pick and
draws no line while that symbol is not in the list. Its footnote says the symbol is no
longer one of the ledger's benchmarks. Nothing stored is rewritten. The symbol's stored
history is kept, so adding it back needs no fresh fetch.

### D2 — Total return only: fund and ETF symbols, checked when added

A benchmark is a fund or ETF whose Yahoo chart response carries an adjusted close. An
index symbol is refused at add time and the refusal gives the reason: Yahoo resolves
`^SP500TR` as `INDEX` with no field telling a total-return index from a price index. The
check reads `meta.instrumentType`, `meta.currency` and `indicators.adjclose` from one
chart request. It is egress, so it needs the adding user's Yahoo opt-in. With Yahoo off,
the symbol field and Add are disabled ahead of time, with the message and a link where
the hint goes. The server still refuses.

**What is stored is each day's growth, not the adjusted close.** Yahoo rescales the whole
adjusted-close history at every new dividend or split. So a close stored last month and
one fetched today sit on different bases, and chaining them would put a false jump in
the line. Each fetch therefore stores one value per trading day (`benchmark_growth`): that
day's adjusted close over the previous trading day's, both taken from the same response.
A later rescale multiplies both by the same factor and leaves the ratio as it was. Each
fetch starts at the last stored day, so the first new day has its previous close in the
same response. The value is `NUMERIC(20,12)`, rounded once at write.

Every series and benchmark value is one value per (symbol or pair, UTC day), stored as a
`DATE`, as ADR-0070 lays out.

### D3 — How the line is drawn and computed

The server rebases the benchmark to 100 at the series' `CoveredFrom`, chaining the stored
growth over the same invested time as the portfolio. Across a `no_invested_base` stretch
the benchmark pauses with the portfolio and resumes from the value it had. Each point
arrives already rebased, beside the series point it belongs to, so no client chains a
figure.

A benchmark in another currency than the series it is drawn against is converted into
the series' currency at each day's rate (D7), unhedged. A missing rate or growth value
gives that point a null and a reason (D9). Growth is stored only through yesterday, so a
point dated today reads the newest stored day, as long as the benchmark is covered through
yesterday. This is the same rule D9 sets for a current value's rate.

The line is dashed slate with an end label showing only the value (amended 2026-11-21 by
ADR-0104 D6: no line has an end label; the legend gives the benchmark the slate square
swatch every series has and its latest value). It is drawn with
`vector-effect: non-scaling-stroke`, so the dash stays even at every width. The table
gains a benchmark column. The footnote names the fund and says the line is its total
return, set to 100 at the same first invested point and paused when nothing is invested.
The MCP `returns_series` takes an optional `benchmark` (one of the ledger's symbols) and
returns the same points.

### D4 — Sources: Yahoo for benchmarks, Frankfurter for exchange rates

Benchmarks use the existing Yahoo adapter through a new caller of its chart fetch.
Exchange rates come from Frankfurter's blended feed: no key, no quotas, MIT-licensed, and
self-hostable. Its base URL is install configuration, `Quotes:Frankfurter:BaseUrl`, set
by `COFFER_FX_RATES_URL` and defaulting to `https://api.frankfurter.dev`. An operator can
point it at their own Frankfurter container. Because the URL is the operator's, not a
user's, the outbound guard for user-entered URLs does not apply to it.

A rate is fetched with the account's currency as `base` and the currencies it converts
into as `quotes`, so no stored rate is the inverse of another. Weekends and holidays are
absent from the feed: a day converts at the last rate on or before it. A rate between
two account currencies is never fetched and never stored; it is derived from the stored
rates against the reporting currency (D13, decided 2026-11-15).

### D5 — Every quote names its real currency

Yahoo's `meta.currency` replaces the `USD` stamp for held securities and benchmarks
alike. A quote in a minor unit (`GBp`, for example) is stored in its major unit, divided
by its factor, because no rate source quotes the minor unit. The build lists the minor
units Yahoo returns and fails closed on one it does not know.

A price typed by hand takes an explicit currency in the security's price editor and in
the MCP `add_price`. Both are prefilled with the security's latest price currency, else
the currency of the accounts holding it when they agree. The price editor's input shows
the symbol of its own Currency field, and none while the code is partial or invalid
(D14). A SimpleFIN holdings price is in its account's currency.

**A SimpleFIN account the feed names no currency for** is priced in the currency of the
Coffer account it is mapped to (decided 2026-11-20). Mapped means the binding the sync
uses: the Coffer account whose `feed_connection_id` and `external_id` are the feed
account's connection and id. This matches the account's transactions, which the sync
does not judge against the mapped account when the feed names no currency (D13's sync
rule), so they already take the account's currency. Two cases still store none of the
account's holdings and record one error for the account. A feed account with no currency
that is mapped to no Coffer account records `currency-missing`, and its message says to
map it in Settings → Bank feeds. A currency the feed does name but that is not a currency
code (a custom currency named by URL) records `currency-unknown`. It is not a missing
currency, so it does not fall back. The error names the feed account by its id
(`simplefin_account_id`), and the Activity log names the account from that id (ADR-0055).

**A Moneydance import** names the currency of each account and price the file holds.
A real account takes its own currency: its `currid` is the id of a `curr` item, whose
own `currid` is the code. A Holdings sibling takes its brokerage's (ADR-0019 Rule 4). A
category takes the ledger's reporting currency, whatever Moneydance kept it in, because
a category holds no money of its own (D7).

Every Moneydance amount is an integer at its own currency's `dec` places: 0 for the yen,
2 for the euro, the security's own for a share count. A split's `pamt` is in the parent
account's currency and its `samt` in the split account's. Each is scaled by its own
currency. A category's line is read in the currency of the real account on the other
side, so a category leg carries that side's amount negated, never the `samt`. Two real
accounts in different currencies each keep their own amount, as a two-amount pair
(D13). The import's header-balance check leaves such a posting out of its header's sum
and passes it when its two amounts are nonzero and opposite in sign. Every other leg is
summed per header and must come to zero, as before. A posting with a category leg is
never a two-amount pair, whatever currency the category carries.

A `csnap` price takes the currency its rate is relative to: the security's relative
currency (`rel_curr_id`, with its code in `relative_to_currid`) when it has one, else
the file's base currency, the `curr` item with `isbase` set. A trade price takes its
brokerage's currency (ADR-0084). Nothing defaults to USD or to two places. The preview
and the start refuse an export with `import-currency-unresolved`, naming the accounts
or securities, when it has any of these:

- no single base currency;
- an account whose own currency is missing, unknown, a security or not a 3-letter
  code, or not one Coffer accepts as an account currency (D15), or whose places are
  missing;
- a category that is the primary account of a posting to another category, when its
  own `dec` is more than the places of the reporting currency the import will stamp it
  with (the one chosen, else the file's base). Such a posting has no real side, so its
  legs carry the primary category's own `pamt` at its own places; a category that only
  meets real accounts is read at their places and is never refused for its own. Only a
  posting the import writes counts, judged by the Coffer account each side becomes: every
  blank top-level leaf category becomes the one Uncategorized, so a split from one onto
  another lands on its own primary and, like a split onto the primary itself, is never
  written; nor is any posting of a transaction or a reminder the import skips whole (a
  split onto an account it does not create, or a reminder with no start date it can read).
  The refusal names it as a category: "The category '<category>' moves money to another
  category in '<currency name>' at <dec> decimal places, and Coffer keeps a category in
  the ledger's reporting currency, <reporting code>, at <places>." (decided by the owner,
  2026-11-18). An unnamed one is "An unnamed category (<id>) …", as an unnamed account is
  named by its id (settled by Claude, 2026-11-18). An account's own currency that keeps
  more places than Coffer keeps for its code still reads "The account '<name>' is in
  '<currency name>', whose amounts the file keeps at <dec> decimal places, and Coffer keeps
  <code> at <places>.";
- a security whose relative currency cannot be read.

An account is checked when the import reads its currency: every account it creates, and
each blank top-level leaf category it maps to the ledger's Uncategorized, whose places it
still reads for the amounts that name it. A non-category account that parents others and
has no transactions of its own is a placeholder the import drops
([ADR-0016](0016-moneydance-account-translation.md)), so its currency and its loan terms
are never read and never refuse the file.

The places are checked per currency against D15's table, not against 2 (settled by
Claude, 2026-11-16). When the file's own `dec` for an account's currency is at most the
places Coffer keeps for that code, every amount fits exactly and the import goes ahead.
When it is more, the preview refuses the export, naming the account and both place
counts. The loan mapper checks escrow and the fixed payment the same way.

Imports that ran before this keep their USD stamps: the source `curr` and `csnap` items
were never stored, so there is nothing to backfill from.

**Prices fetched before this fix** were all stamped USD. Each security records when
Yahoo last named its currency (`securities.quote_currency_confirmed_at`). When a refresh
reads a security's currency, every older `fetch` row stamped otherwise is re-stamped to
it, and the price itself is left alone. There is one exception: when Yahoo names a minor
unit, the older rows hold the minor-unit figure, so they are converted as a new fetch is
stored. Price, high and low are divided by the unit's factor and rounded once at 4dp, and
the major-unit code is stamped. The first refresh after the upgrade also asks
Yahoo once for the currency of every security that has fetched prices, held or not: one
chart request each, under the Yahoo opt-in, storing no price. This is the one exception
to quoting held securities only (ADR-0054). The consistency check reports a security
with fetched prices whose currency has not been confirmed, or a fetched row whose
currency differs from the confirmed one, and its repair runs the same lookup.

### D6 — Storage: per ledger, beside `security_prices`

The new tables carry `ledger_id` with the role-aware RLS pair: read for any grant, write
for owner or editor. Snapshots capture them, and they join `SchemaDriftGuardTests`.

- `ledger_benchmarks`: symbol, name, currency, order.
- `benchmark_growth`: symbol, day, growth, source (`fetch`).
- `fx_rates`: from currency, to currency, day, rate `NUMERIC(28,12)`, source (`frankfurter`
  or `manual`), the upstream source Frankfurter names for that rate, and
  `entered_by_user_id` for a hand-entered one.
- `fx_pair_coverage`: from, to, and the day the pair was last fetched through (D9).

The first stored value for a day wins, whatever its source, and a day's value is never
rewritten. A rollback restores the snapshot's rows and drops the days after it, which the
next refresh fetches again. An upstream revision can therefore differ only across a
rollback. A transfer never writes `fx_rates`: the editor's prefill, its rate line, an
estimating reminder and the lot-basis move only read it (D13, D14), and no pair between
two account currencies is ever stored in it. The request role can insert into
`fx_rates` but not update it (migration 280), so the grant keeps a stored day from being
rewritten, and the transfer write paths are what keep a transfer from inserting one.

### D7 — Reporting currency, and which figures convert

`ledgers.currency_code` holds each ledger's reporting currency. A new ledger defaults to
USD in create-ledger, and to the file's base currency in the Moneydance import, whose
preview returns it as `baseCurrencyCode`. Either can be changed before it is created.
Only owners change it later,
in Settings → General. The migration sets existing ledgers from the rule
`DisplayCurrency.Of` applies today: the ordinally first currency of net worth's accounts,
else USD. That is derived from the data, so the migration is deterministic. Conversion
happens at read time, and nothing stored converts, with one exception: shares moved
between brokerages in different currencies have each lot's basis converted at its
acquisition date's rate, and the converted figure is stored (D13). That move is the one
stored conversion. The prefilled second amount of a transfer between currencies and an
estimating reminder's other side are entry figures: each is written once, when its
transaction is written, like any typed amount, and is not a stored figure that converts
(D13, D14).

Apart from that move, only figures that combine accounts convert, and every per-account
figure stays in its own currency. The date rules:

- A balance converts at its day's rate.
- A transaction converts at its date's rate.
- A trade converts at its trade date's rate, so a realized gain includes the FX move.
- A current value, and a forecast day, convert at the latest rate.

These are one rule for any dated figure: a day before the server's UTC today converts at
that day's rate, and today or a later day converts at the latest rate. So today's
transactions, a scheduled one, a lot bought today and the last point of a history all
read the latest rate, and none goes blank because today's rate is not yet published.

A position converts from its price's currency into its account's currency at the day's
rate, and the account then converts as a whole. A combined figure is the sum of what each
account shows, converted, rounded once at the end, to the 4-place bound read-side figures
keep, and the screen presents it at the reporting currency's places (D15). The ledger
Returns line is unhedged.

`DisplayCurrency` becomes the stored column. The `mixed` marker leaves the combined
figures, and with it the two client-side checks at `AccountsWidget.tsx:61` and
`ReturnsChart.tsx:179`.

One ledger-level note under the page header says that totals are converted into the
reporting currency. A figure with a missing rate is still marked on itself (D9).

### D8 — Consent per destination

Benchmarks ride the existing Yahoo opt-in. Frankfurter is a new opt-in provider in
`enabledProviders`, off by default, listed as a second row on the Quotes tab. A
scheduled run uses the consent of the user whose schedule drives it, as quotes do.

### D9 — A missing rate fails closed

A rate is missing when the pair has no rate on or before the day, or the day is after the
pair's coverage. A pair's coverage runs to its latest fetched-through day, or its latest
hand-entered day if that is later. A figure that needs a missing rate is null and carries
`rate_unavailable`, naming the pair and the day. The same answer applies on every surface:
screens, REST and MCP. No account is left out of a total. A rate derived between two
account currencies (D13) names the stored leg that is missing, in the direction it is
stored (account currency → reporting currency, D4), and the day; when both legs are
missing it names both (settled by Claude, 2026-11-15: a pair between account currencies
can be neither listed nor typed, D10, so naming it would leave nothing to act on).

A current value, and every forecast day, converts at the pair's newest stored rate on or
before today (UTC). It is missing only when the pair's coverage ends before yesterday
(UTC). One daily refresh therefore keeps a current figure readable all day, whatever time
of day the refresh runs and whenever the day's rate is published, and a whole missed day
fails closed. No rate can be stored for a day after today: the fetch stops at today, and
a hand-entered rate for a later day is refused.

### D10 — Hand-entered rates

A rate can be typed only for a day with no stored rate. It is marked as `manual` and
records who entered it. Only rates can be typed; benchmark growth is fetched only.

The rates view is a Settings tab after Quotes. It lists the pairs the ledger needs (price
currency to account currency, and account currency to reporting currency), how far each
is covered, and Add rate for a day with none. Viewers see it without the add control.
A pair between two account currencies is neither listed nor typed: its rate is derived
from the account-to-reporting pairs (D13), so the view and hand entry are unchanged by
transfers between currencies.

### D11 — When rates and benchmarks are fetched

Both ride the existing per-ledger daily quote refresh and its manual Refresh. Any change
that adds a needed pair, or moves a needed pair's start earlier, starts that pair's
backfill at once. Examples:

- turning on the Frankfurter opt-in,
- changing the reporting currency,
- creating or editing an account into a new currency,
- adding a benchmark,
- adding or editing an account so its opened date is earlier than the ledger's earliest
  day,
- posting, importing from a file, posting a reminder occurrence for (by hand or by the
  auto-post), or unhiding a transaction dated before the ledger's earliest day, and
- posting, editing, merging or moving a transaction onto a brokerage before the ledger's
  first brokerage funding, which moves every benchmark's start,
- a trade, a position move or a security merge that puts a security on an account in a
  currency none of its prices has met there, which adds a position pair, and
- adding or editing a price in a currency new to a held security, or dated before its
  first price in that currency.

The one rule replaces a list of four triggers as a defect fix (settled by Claude,
2026-11-15): an enumerated list misses changes that widen the range, and each one it
misses fails closed until the next refresh. So a write reads every needed pair's start,
and the set of needed pairs, together with the ledger's earliest day and (on a ledger with
a benchmark) its first brokerage funding, before it writes and again after, and starts the
backfill when a pair is new or any of these moved earlier. An ordinary write changes none
and starts nothing. A deleted price only narrows what is needed, so it is no trigger.
Only a live row counts toward either day: a hidden row, a row merged into another and a
reminder's template move neither, so creating or editing a reminder starts nothing.

A currency that first appears through a feed sync waits for the next refresh, because a
bank sync never triggers egress (ADR-0054 D1). A file import is the user's own upload, not
a feed sync, so it follows the one rule. A backfill reaches back to the earliest day
a figure needs: for an account currency into the reporting currency, the ledger's
earliest day, the first opened or posted date across all of its accounts; the first price
in that currency; or, for a benchmark, the earliest day a returns series can start from:
the ledger's first brokerage funding, which can come well before its first trade. When
that earliest day moves earlier, the earlier span is fetched once, and a span already
asked for is never asked again.

The ledger-wide start for an account-currency pair is a defect fix (settled by Claude,
2026-11-15). It replaces the first opened or posted date of the accounts in that
currency, which `NeededFxPairs` uses, because a derived rate (D13) can need an account currency's
rate on a day before any account in that currency existed: a lot keeps its acquisition
date when it moves into a newer brokerage, and an entry can be dated before an account's
own history. A day before the provider's history still has no rate, and a lot move that
needs one is refused, as D13 decides.

The run's audit row is the quote refresh's own `ledger_operations` row (family `quote`).
Its `details` gain `rates_inserted` and `benchmark_days_inserted`. Each pair or symbol
that fails writes a `ledger_operation_errors` row with its own code. A backfill started
by a user writes `triggered_via = 'manual'`. One the reminder auto-post starts runs as the
user who configured the schedule, under that user's opt-ins, as the scheduled refresh
does, and writes `triggered_via = 'scheduled'`.

> **Amended 2026-11-19 — the quote refresh's switch watches its rates.** Rates have no
> job of their own, so the dead-man's switch bound to `quote-refresh` is the one that
> watches them, and Settings → Notifications names it "quote-refresh — prices, exchange
> rates, benchmarks". A scheduled run whose rate days were not stored is reported as
> `Degraded`, not `Ok`. That covers the rate source refusing, being unreadable or
> misconfigured, and the market-series step throwing or finding the write lock held (the
> codes are enumerated in `QuoteRefreshJobHandler.RatesNotStoredCodes`). A pair that does
> not land converts to nothing until a later run stores it (D9), so unlike a per-security
> miss there is no benign rate failure. A run with no quote work whose rates all failed
> stored nothing at all and is reported as having done nothing, which escalates after a
> day. A benchmark refused for what it is (an index, an unsupported instrument, a changed
> currency) stays a per-symbol miss and does not degrade the run. Before this, a run with
> the rate source down while every quote provider answered read `Ok`, and the switch
> stayed green while conversions went stale.

### D12 — Budget targets keep the currency they were typed in

`budget_targets` gains `currency_code` (`NOT NULL`). A target is typed in the ledger's
reporting currency at the time it is typed, and it keeps that currency. The target input
shows the reporting currency's symbol, which is the currency the save stores (D14). The
migration sets every existing target to its ledger's new `currency_code` (D7), so it is
deterministic. After the reporting currency changes, a target in the old currency
converts like any other combined figure:

- A past month converts at the rate for its last day.
- The current month converts at the latest rate.

A missing rate gives D9's answer. Nothing is rewritten, so changing the reporting
currency back reads every target exactly as it was typed. Retyping a target stores it in
the current reporting currency. The derived normal (ADR-0099) needs nothing, because its
transactions convert at their own dates.

A category merge adds the source's target to the destination's for each month both have.
When their currencies differ, the source amount is converted into the destination's
currency first, at the same rate the Budget screen shows it at: the month's last day, or
the latest rate for the current month. The converted amount is rounded once, at the
destination currency's places, half away from zero (D15). The destination keeps the
currency it was typed in. If that rate is missing, the merge is refused with `rate_unavailable`, naming the
pair and the month. Copy last month keeps the source month's currency, because copying is
not typing. Fill from average takes the reporting currency, because the average is built
from converted transactions.

### D13 — Transfers between currencies record both amounts

A transfer between two real accounts in different currencies records both amounts, each
leg in its own account's currency. No rate is stored on the transaction. There is no
exchange step and no gain or loss line for the currency move. This is the one place
ADR-0022 Rule 2's mirrored legs do not hold (its 2026-11-14 amendment).

- **The second amount is required.** On a posting between real accounts in different
  currencies, a missing, zero or same-signed second amount is refused. A posting between
  accounts in one currency still carries one amount, and its other leg is that amount
  negated. On the wire it is a new field: `counterpartyAmount` on the bank request's
  posting (`TransactionPosting`), beside `counterpartyAccountId`, and `transferAmount`
  on the investment request, beside `transferAccountId`. A `counterpartyAmount` or
  `transferAmount` sent on a posting to a category, or on one between accounts in one
  currency, is dropped: that posting mirrors (settled by Claude, 2026-11-16).
- **The rate between two account currencies is derived** (decided 2026-11-15). For
  account currencies X and Y and the reporting currency R, it comes from the rates
  already stored against R. When R is X or Y, it is the stored rate or its inverse;
  otherwise it is the cross, rate(X→R) / rate(Y→R). No new pair is fetched:
  `NeededFxPairs` gains no pair, the rates view and hand entry (D10) are unchanged, and no
  pair between two account currencies is ever stored (D4, D6). Each account-currency pair
  is needed from the ledger's earliest day (D11). Both sides of any transfer are covered
  by D7's net-worth need: every non-category account's currency other than R needs its
  pair into R, inactive accounts included, and a holdings sibling takes its brokerage's
  currency (ADR-0019 Rule 4) (settled by Claude, 2026-11-15). A position's need, a price
  currency into an account currency, supplies no such pair.
- **A missing derived rate names the stored leg** (settled by Claude, 2026-11-15). The
  editor's no-rate line, the lot-move refusal, the Cash ahead exclusion row and every
  `rate_unavailable` reason for a derived rate name the stored leg that is missing, in
  the direction it is stored (account currency → R), and the day; both legs when both
  are missing (D9).
- **One rate rule.** The editor's prefill, its rate line, an estimating reminder's other
  side and the lot-basis move read the rate through `RateDay.Of`, D7's one rule with
  D9's guard: a day before the server's UTC today reads the pair's rate on or before that
  day, and today or a later day reads the latest rate. D9's coverage limit applies, so a
  pair whose coverage has lapsed has no rate, and the figure is missing. For a derived
  rate, each stored leg (X→R and Y→R) is read by this rule for the same day, the first
  stored value for a day winning (D6). The derived rate and the figure it gives are
  computed at full precision, with no rounding in between, and the result is rounded
  once, at the places of the currency it lands in, half away from zero (D15, settled by
  Claude, 2026-11-16). When either leg has no rate, the derived rate is missing.
- **The reporting currency never changes a stored figure.** A rate derived through R
  depends on which currency R is. Changing the reporting currency changes no stored
  figure; it changes only what a later prefill or estimate, not yet stored, derives.
- **A category leg mirrors the real side.** A category holds no money of its own (D5),
  so its leg always carries the real side's amount negated. Only a posting between two
  real accounts carries two amounts. A category leg is checked and rounded at the real
  side's currency's places, never at the category's own `currency_code` (D15, settled by
  Claude, 2026-11-16).
- **Moving a transaction to another account, or changing an account's currency,
  relabels.** Every stored figure is kept. It is refused in two cases. One is a
  two-amount pair that would end up in one currency with magnitudes that differ, because
  that cannot be represented. The other is a stored figure that carries more places than
  the new currency allows; the refusal names how many figures do not fit (D15, settled by
  Claude, 2026-11-16), a row merged into another left out (D15, decided by the owner,
  2026-11-17). A two-amount pair on a row merged into another is still judged, and its
  refusal stands, because nothing detects a pair in one currency whose amounts differ
  once the row comes back. The refusal says the pair is on a row no register shows,
  names and links the transaction it was merged into, and says that deleting that
  transaction brings the row back so its amounts can be made to match; a pair on a row a
  register shows keeps the refusal it had, with no `mergedInto` (decided by the owner,
  2026-11-18). The names are `relabel-pair-not-representable`'s `mergedInto`: the
  transaction, the account it was entered from, its date, payee and whether it is hidden.
  A transaction since merged into another is followed to the end of the chain of merges,
  to the row a register shows, and the account editor links each to its register at that
  row, on the Hidden view for a hidden one (settled by Claude, 2026-11-18). The refusal
  says that deleting brings the row back only when that is true of every refused
  merged-away row: each was merged straight into a transaction a register shows, and
  each such transaction's delete removes it outright (a row entered by hand, not a
  reminder occurrence), which un-merges the row. Otherwise it says nothing about a way
  back: deleting a feed or import row only hides it, and deleting the end of a chain
  un-merges only the row merged straight into it. Such a row gets no other way out: the
  case is rare, and the refusal still names and links what blocks the change (settled by
  Claude, 2026-11-18). A mirrored pair that lands across currencies becomes a two-amount
  pair with equal magnitudes and opposite signs, and the move's or the edit's result
  reports how many did (ADR-0072 D3; ADR-0050 D8 for a real account's currency edit,
  ADR-0019 Rule 4 for the Holdings sibling it carries to). The account editor and the
  register's Move dialog show that count (decided by the owner, 2026-11-18). When it is
  above 0, the dialog stays open after the save. Its form is replaced by "1 transfer now
  keeps two amounts, one in each account's currency." or "<n> transfers now keep two
  amounts, one in each account's currency." and a single Done, which takes focus and
  closes the dialog, as Escape and the dialog's close do. The line is announced as a
  status. The save's refresh, and the move's clearing of the selection, happen at the
  save, not on Done. The title stays as it was, and no other line is added (settled by
  Claude, 2026-11-18). When the count is 0 the dialog closes, as before. A mirrored
  zero pair that lands across currencies is kept, since 0 and 0 is representable, and
  the nonzero and sign rule checks only a pair the write changes: an edit that resends a
  stored pair unchanged, on the same accounts, passes, whichever side it was opened from (settled by
  Claude, 2026-11-16). An edit from a category's register that repoints the paying
  account to an account in another currency is a relabel too: the category's figure is
  kept and the account's leg reads it in that account's currency, and a figure with more
  places than that currency allows is refused with D15's `amount-places-exceed-currency`
  (settled by Claude, 2026-11-16).
- **Shares moved between brokerages in different currencies convert each lot's basis.**
  Each lot converts at the rate for its acquisition date, which an earlier move keeps,
  derived and read by the rules above. The first stored value for a day wins (D6), so
  the stored figure is reproducible from stored rates while the reporting currency stays
  the same; a later change of reporting currency leaves it as stored. The converted cost
  is rounded once, at the destination brokerage's currency's places, half away from zero,
  as the destination leg's amount (D15, settled by Claude, 2026-11-16), and the lot's
  `unit_cost` is that amount over its quantity. If any lot has no rate, the whole move is
  refused with `rate-unavailable` (`BusinessError.Codes.RateUnavailable`), whose
  `rateUnavailable` names each missing stored leg (account currency → R) and the day.
  This is the one stored conversion (D7),
  and the move still realizes no gain (ADR-0065).
- **A managed loan payment stays in the loan's currency.** Setup is refused when the
  paying account's currency differs from the loan's, and when a real escrow account is in
  another currency than the loan's, because interest and escrow are computed in the
  loan's currency. An escrow that is a category mirrors as usual. A series created before
  this guard is re-checked at fire time and refused there, so it can never post:
  auto-post sets it aside before the per-run cap, with the exclusion reason
  `loan-across-currencies`, and counts it in the backlog event under
  `managed_loan_across_currencies` (ADR-0097). Such a payment is entered as an ordinary
  transfer with both amounts (ADR-0078 D2).
- **A feed keeps the far leg.** A pending feed row that clears at a new amount keeps the
  far leg, the one in the other currency, as entered. Keeping it leaves `needs_review` as
  it was; a promotion that rounds sets it (D15) (settled by Claude, 2026-11-16).
- **An account's currency must match what is imported into it.** Binding a SimpleFIN
  account whose currency differs from the Coffer account's is refused (ADR-0083). An OFX
  import whose statement currency differs from the target account's is refused before
  the run opens (ADR-0031). A SimpleFIN binding made before this guard, or one the bind
  could not judge because the connection's directory did not hold the feed account yet,
  is judged at every sync by the same rule: a feed account whose currency differs hands on
  none of its rows and keeps its watermark, and its error, `feed-account-currency-mismatch`,
  names both currencies. A feed account that reports no currency is not judged, and one
  that reports a value that is not a currency code fails closed (settled by Claude,
  2026-11-16).
- **In-kind transfer candidates exclude pairs across currencies** (ADR-0065 D4).
- **The Moneydance import keeps both amounts** (D5). The header-balance check leaves a
  two-amount pair out of its header's sum and passes it when its two amounts are nonzero
  and opposite in sign. Every other leg is summed per header and must come to zero, as
  before (ADR-0053).
- **An estimating reminder estimates both sides.** The source side is estimated as
  before, from the average of the series' own past occurrences on its source account.
  The other side is set for each occurrence from the source estimate at the derived rate
  the one rate rule gives its due date, the rule the editor's prefill follows (D14), and
  `fx_rates` is never written. It is an entry figure, written once when the occurrence
  posts. So the other side, and the reason it has none, are resolved per occurrence
  (series and due date), not per series as the existing reasons are. With no rate for
  the due date the estimate is unavailable, under a new reason code in
  `EstimateUnavailable`, and the fire is refused (`EstimateUnavailableForFire`), as an
  estimate with no history is; nothing posts the template's figure. Both figures are
  estimates, and both carry the existing ≈ mark; with no rate, the mark takes the
  existing title for an estimate that cannot be made yet, and no new wording is added.
  Under Manual approval both figures show, and the second amount can be corrected before
  posting. Under Auto-post the figure from the rate posts, and a fire refused because
  the estimate is unavailable is counted under `estimate_unavailable` and stays due
  (ADR-0049 D3, ADR-0097). With Estimate from history chosen, the reminder editor's
  Schedule says where the other side's figure comes from, in one muted line that
  describes the choice: "The amount received in <account> is set from the stored <A> to
  <B> rate on each due date. With no stored rate for that date, the occurrence is not
  posted until you enter that amount." when the money goes to the other account, and
  "The amount sent from <account> is set from …" with the same rest when it comes in
  from there (board OT-4; the second line decided by the owner, 2026-11-18). A is the
  source account's currency and B the other account's.
- **Cash ahead places the other leg on the other account** (ADR-0109 D8). A reminder
  between currencies is no longer listed as `cross-currency`. A fixed series places the
  template's other amount on the other account, as each template line is placed as the
  post would write it. An estimating series places on the other account the source
  estimate at the latest derived rate, for every occurrence, an overdue one included, as
  D7 has a forecast day convert, so a series is still planned once. An occurrence due before
  today that is posted writes its due date's rate, so its figure can differ from the
  forecast's, as any estimate can. When the latest rate is missing, the series is listed
  in `excluded`, one row per reminder with its reason as every exclusion is, under the
  reason `rate-unavailable`, and the row names each missing stored leg (account currency
  → R) and the day, as D9's `rateUnavailable` does. An exclusion row carries only the reminder and its reason
  today, so it gains that field. A managed loan series across currencies, which can
  never post, is still listed as `cross-currency`.

### D14 — The editor's second amount, the rate line, and money inputs

**The second amount** sits right of Amount and is built like Amount: the same caption
style and the same input, red for a negative in the bank editor. Its caption is one word:
Received when the money moves into the other account, Sent when it moves out of it. It
has no sign segment, because its sign follows from Amount, and its caption carries no
currency suffix. The field holds a magnitude: its sign is always opposite Amount's, a
minus typed into it is ignored, and flipping Amount's sign flips the figure it saves
(settled by Claude, 2026-11-16). Like every money input, it shows its own account's currency symbol,
muted and not typed. The account's name and its currency's code are in the field's
accessible name and title only. It shows only while the counterparty is a real account
in another currency. Switching the counterparty to a category or to an account in the
same currency removes the field and drops its value. Where it sits:

- **The bank editor in register edit mode:** the second amount takes the Balance track
  (ADR-0023 §C and §Q), so the faded pre-edit balance is not shown while it does.
- **A split:** one extra column right of Amount, with a header caption (ADR-0025,
  2026-09-16 amendment). The caption is Received or Sent while every line with a second
  amount goes that way, and "Other amount" while some lines receive and others send,
  since either word would then be false for one of them; each field's accessible name
  and title still name its own direction (decided by the owner, 2026-11-18). The split's
  footer total sums the Amount column only, in the register account's currency, never
  the second amount's column, as it does today.
  A category holds no money of its own, so on a category's register each line is in
  the currency of the account that paid it. There the footer, the editor's Amount and
  the collapsed split row show the total in the one currency the lines share, and the
  "mixed" marker, in the subtle text colour, when the lines are in more than one: no
  figure adds currencies, and none of these converts (D7). The marker's hover says
  "These lines are paid from accounts in more than one currency, so there is no single
  total." wherever it stands in for such a total, the home screen's To review and its
  Review… title included; the editor's "Sum of postings (saved as the transaction
  total)" hover shows only over a sum, never over the marker (decided by the owner,
  2026-11-18). A filter matches only what
  the row shows (ADR-0108 D2), so the register's amount range and direction keep no
  such row, and its amount sort places it as a row with no figure, which sorts as zero.
  The bulk bar's selection total still converts each row into the reporting currency:
  it adds many rows, while a row or a transaction's own total shows only what is stored
  and is never converted (decided by the owner, 2026-11-17).
- **The investment editor:** right of Amount. BuyXfr, SellXfr and DividendXfr pair it
  with the trade amount, not the fee (ADR-0027, ADR-0029).
- **The bank editor's dialog layout:** one extra track while it shows, in every host of
  that layout: Post or skip…, the reminder series editor and the home screen's Review…
  (ADR-0025, 2026-11-03 amendment).

**A missing or zero second amount** in the bank editor is refused with the field's own
sentence, "Enter the amount in <code> that arrived in <account>." or "… that left
<account>.", which also marks the field and is its row marker's hint. On a split line it
carries the line's posting number, as the split's other messages do: "Posting <n>: enter
the amount in <code> that arrived in <account>." (or "… that left <account>."), and the
split's problem list gives it a group headed "Enter the amount in <code> that arrived in
<account>:", whose numbers go to each line's field (decided by the owner, 2026-11-18). A
line whose money left the other account is grouped under "Enter the amount in <code>
that left <account>:", and lines share a group only when the currency, the account and
the direction all agree; these groups follow the problem list's own two, in the order
of their first line (settled by Claude, 2026-11-18). A one-posting transaction's sentence
has no posting number.

**The rate line** is always shown under the second amount. A new entry prefills the
second amount from Amount at the derived rate the one rate rule gives its date (D13),
and the line says "Rate for <date>: 1 X = r Y". When the two stored rates behind a
derived rate fall on different days, the line names the older of the two, the day the
derived figure is good for. Until the field is typed in, it follows Amount
and the date. Once typed, it stays, and the line shows the ratio of the two amounts
("…, from the two amounts"). The prefilled amount is an entry figure, written once when
the transaction is written, like a typed one (D7). A saved transaction is never
refilled: both amounts are seeded from what was stored, and "never refilled" protects a
stored second amount. With no rate, the field is empty and required, and the line says
"No <A> to <R> rate for <date>", naming the stored leg that is missing, account
currency A into the reporting currency R, and both legs when both are missing (D13,
settled by Claude). It shows when either stored leg has no rate on or before the day,
and it holds for a leg with no rate and for one whose coverage has lapsed. A new entry
dated before the ledger's earliest day gets no prefill until it is saved: its second
amount is empty and required and the line names the missing stored leg, as for any
missing rate, and once it is saved the backfill (D11) covers that day for later entries
while the saved entry is never refilled (settled by Claude, 2026-11-15). A transfer
never writes `fx_rates` (D6).

The prefill and the rate line belong to the editor, whatever the row came from: a
posting that gains the second amount in the editor, because its counterparty becomes a
real account in another currency, is prefilled the way a new entry is, so a feed row
opened in Review… gets the prefill too. Imports and feeds take no prefill: the import and feed
write paths never derive an amount from a rate.

**A fixed-amount reminder between currencies** seeds both amounts from its template in
Post or skip… (ADR-0049 D10), as the reminder series editor does, and neither is
refilled from a rate. Both hosts seed from one web prefill, `reminderBankPrefill`
(`src/Web/src/routes/ledgers/reminders/reminderOccurrenceDraft.ts`), which builds each
posting from the source leg's amount alone and drops the counterpart leg's. One fix
serves both: it carries the counterpart's amount as the posting's second amount, for
Post or skip… and the series editor alike.

**The register row.** On a transfer between currencies the counterparty cell shows the
other leg's amount after the account chip, muted, formatted by the app's formatter
(symbol, no plus). A long account name truncates, and the amount stays whole.
Same-currency transfers and category rows are unchanged. The home screen's To review
and Recent transactions lists draw their rows through the register's row cells
(ADR-0109), so they show the other leg's amount after the account chip too. The
investment register's transfer chip on an Xfr, BuyXfr, SellXfr or DividendXfr between
currencies shows the other leg's amount, muted, after the chip, as the bank register
does (ADR-0028 Slot 6). The MCP `activity` tool reads the same projector (ADR-0080), so
its row carries the same figure: `transferAmount` and `transferCurrencyCode` are the
transfer slot's other leg on a transfer between currencies, and null on a transfer in
one currency, on a category side and when there is no transfer, by the register's rule.
Its description says so (decided 2026-11-17).

**Every money input shows its currency's symbol**, app-wide: muted, not typed, from the
shared formatter in `src/Web/src/lib/money.ts`. The locale follows the browser, and the
symbol sits where that formatter puts it, before or after the figure, so CA$ is told
apart from $. Share quantities, exchange rates, percentages, counts and day numbers carry
none. The symbol takes no room from the figure: a fixed-width money input is drawn wider
by the space its symbol is padded with. An input still takes a signed figure. Every money input's step and its rounding on
blur follow its currency's places from D15's ISO 4217 table, which `lib/money` reads, so
a currency with no minor unit takes whole units and a 3-place currency takes three
decimals. The rounding is the one `roundToMinorUnits` helper, half away from zero, and
the save body is rounded the same way; while the currency is null nothing is rounded
(D15, settled by Claude, 2026-11-16). This covers Amount and the second amount alike;
`TxnRowEdit` and `PostingRowEditor` assume two places today, with a one-cent step and a
two-place rounding on blur. A price input shows its currency's symbol but is not money:
it takes any step and gets no money rounding (D15). The currency at each site:

- the investment editor's price: the brokerage's, since price × shares = amount;
- the security price dialog: its own Currency field, with no symbol while the code is
  partial or invalid (D5);
- an account register's amount-range filter, its inputs and its chip: the account's;
- a category register's rows: each row's own currency; its amount-range filter's inputs
  and chip show no symbol, because its rows can be in different currencies;
- escrow: the loan's;
- a budget target: the reporting currency, which is what the save stores (D12);
- a null currency, while the accounts load: no symbol, never a default.

The remaining ad-hoc currency formatters, the account editor's loan preview (pinned to
en-US, with a USD fallback) and the returns model's `formatWhole`, move into `lib/money`,
which already says every ad-hoc currency formatter is to be replaced.

### D15 — Each currency's minor units

Every money figure is displayed, typed and stored at its currency's own ISO 4217 minor
units, 0, 2 or 3 places, not at 2 places for every currency. Prices, share quantities
and exchange rates are not money and do not follow this rule. The points below are
decided, except those marked settled by Claude.

**Where the places come from.** One checked-in data file holds the ISO 4217 exponent of
each currency. The API and the SPA both compile it in, and a test flags any drift between
them. `lib/money` passes the digits to `Intl` explicitly and never relies on `Intl`'s
defaults, because the browser's CLDR data disagrees with ISO for several currencies. The
SPA and the API ship the same table in the same release (settled by Claude, 2026-11-16).

**Which codes an account or a ledger can take.** At most 3 places. An account or ledger
currency must be an ISO 4217 currency with an exponent. A code that is not one, an
"N.A." entry included, is refused from now on, and so are the two fund units with 4
places, CLF and UYW. A code already stored that would now be refused, for any of these
reasons, keeps working at 2 places, as today, and the consistency check lists it as an
advisory (below) (settled by Claude, 2026-11-16). A price or quote currency is not
checked by this refusal. The account and ledger currency pickers offer every accepted
code in the same native select: today's 14 first, then the rest alphabetically.

**The database holds a ceiling of 3 places.** Migration 301 replaces
`ck_txn_legs_amount_scale_2` (migration 159, ADR-0073 D3) with a CHECK that does not
depend on the currency, `amount = round(amount, 3)`, added `NOT VALID` and then
validated. `txn_legs.amount` stays `NUMERIC(19,4)`. `budget_targets.amount` and the six
`realized_gains` money columns widen from scale 2 to scale 3, and their precision grows
with the scale, so 17 integer digits remain.

**The API holds the exact rule** (settled by Claude, 2026-11-16). A CHECK on `txn_legs`
cannot read the account's currency, so the per-currency rule is enforced in the API
writers only, with no trigger behind it (ADR-0032). The database keeps the 3-place
ceiling, and the consistency check lists the figures that do not fit their currency as
an advisory (below). An IMMUTABLE SQL minor-units function is generated from the same
data file and pinned to it by a guard test. For a stored account or ledger code that
would now be refused for any reason (not an ISO 4217 currency with an exponent, an
"N.A." entry, or CLF or UYW), it returns 2, so such a code keeps working at 2 places in
SQL as in the API. Only `recompute_holdings_cost_basis` (as last defined in migration
275) and `realized_gains_walk` (migration 217) use it, and they change in the same
migration with the same rounding as the API.

`budget_targets` gets no per-currency CHECK (settled by Claude, 2026-11-16). Stored
amounts that do not fit are kept (below), and D12 stamps existing 2-place targets with a
ledger currency that may have 0 or 3 places, so a validated CHECK would fail the
migration on such a backup, and even a `NOT VALID` one would refuse an unchanged row on
any UPDATE. The API checks a typed target at its currency's places like any typed
amount, and a stored target that does not fit is kept and listed like any other figure.

**Stored amounts that do not fit are kept.** A write validates only the values it
changes, so an edit that leaves such an amount as it was passes. There is no rounding
migration and no pre-upgrade script. The consistency check lists the amounts that do not
fit, and the stored codes that would now be refused, as an advisory, as ADR-0084 D6's
unbacked-price advisory is: shown beside its verdict and not counted in it (no
`consistency.drift`, no Overview Problem, no ADR-0102 dot), with no repair; the cure is
an edit by an owner or editor (settled by Claude, 2026-11-16). The amounts' advisory,
"Amounts with more decimals than their currency", explains itself as "These have more
places than their currency uses. They are kept as they are. Editing one to fit its
currency takes it off this list." It does not say the figures predate each currency's
own places: a row merged into another keeps its figure through a change of its account's
currency (below), so once it is un-merged it can be listed for the new currency's places,
whenever it was saved (decided by the owner, 2026-11-18). A hidden row keeps its
figure and comes back on unhide (ADR-0072 D2), so its figure is listed, and its line
opens the account's register on the Hidden view at that row, the one view that shows
it (ADR-0072 D1): the register accepts `?status=hidden` beside `?status=needs_review`
(settled by Claude, 2026-11-17). A row merged into another is not listed, and the
shapes the Category amounts repair leaves are not listed for one either: no screen shows
or opens it, so its line would be a dead end. Deleting its winner, where the delete
removes it rather than hiding it, un-merges it (`is_merged_into` ON DELETE SET NULL), and a figure it holds that does not fit is then
listed like any other row, which an edit fixes (decided by the owner, 2026-11-17). The
Category amounts finding counts every stored posting, a merged-away one included, as its
cure is its repair, which reaches a row no screen opens (settled by Claude, 2026-11-17).
Its line for a hidden row opens the same view; its line for a reminder's template or a row merged into another names
where the line is, with no link, as no register view shows either. Existing `realized_gains`
rows are derived data: the consistency check sees a row that is not at its account's
places as ordinary `realized_gains` drift, and its existing repair derives it again
(settled by Claude, 2026-11-16).

**The Category amounts finding** (settled by Claude, 2026-11-16). A category line that
does not carry the real side's amount negated, as D13 requires, is reported by its own
consistency projection, `category_amounts`. It is a finding, counted in the verdict like
every other projection's mismatches, not an advisory. Its repair sets the category line
to the real line negated, and only on the clean shape, a posting of exactly one real line
and one category line, read again under the ledger write lock so a write in flight is
waited for and what it committed is what is judged. No real line changes, so no
account's balance moves. Every other posting with a category line whose lines do not sum
to zero is listed in the advisory of amounts that do not fit, with its reason and no
repair: both lines on categories (`category-line-between-categories`), or more than one
real or category line (`category-line-many-lines`). Its line gives the reason, muted,
after where it is, joined by " · ": "both lines are on categories, so Repair cannot set
it" or "more than one account or category line, so Repair cannot set it", and a line
listed for its places shows none (decided by the owner, 2026-11-18); a line with a
reason the SPA does not know shows none either (settled by Claude, 2026-11-18). A
posting of one category line and nothing else has no line to mirror and is in neither.
A line the finding reports is not listed again by the advisory, since its repair makes
it the real line negated. The repair's notification says what it did: "Repaired
category_amounts: set the {N} category {line|lines} out of step to the paying account's
amount, negated.", "line" for one and "lines" otherwise (decided by the owner,
2026-11-18). It names the projection by its key, as every repair notification does.

**A reminder whose stored figure does not fit is refused at fire** (settled by Claude,
2026-11-16). A fire is a new write: each figure it writes, a template line's own, or a
loan split's or its override, or the estimate in its place, must fit its currency's
places, as a by-hand post of the same line must. A series with one that does not is
refused at fire, auto-post included: the scan sets it aside before the per-run cap by
the fire's own predicate, with the exclusion reason `template-amount-does-not-fit`, and
the backlog event counts it under `template_amount_does_not_fit` (ADR-0097). The
advisory lists its template line, and the cure is an edit of the reminder or the loan
terms. The backlog event's summary names it, as it names the other reminders only the
user can fix, with " {count} reminder(s) cannot be posted because a figure has more
decimal places than its currency uses ("{name}"; ...). Edit each reminder, or its loan's
terms, so every figure fits its currency." (decided by the owner, 2026-11-18); it comes
after the other named reasons (settled by Claude, 2026-11-18).

**A typed amount finer than its currency is refused; a computed one is rounded**
(settled by Claude, 2026-11-16).

- A typed amount with more places than its currency allows is refused with a 422 and a
  new named error code that names the currency and its places. Today it surfaces as a
  500, or as the failure of a whole import.
- A figure the API computes is rounded once, at the places of the currency it lands in,
  half away from zero: the loan payment and its split, the price-times-shares fallback,
  the lot basis (D13), an estimate, a reminder's other side included (D13), and a
  budget merge (D12). The loan payment
  rounds up to one minor unit.
- The midpoint rule is half away from zero everywhere. The reminder estimate rounds half
  to even today, and changes.
- Loan terms (principal, escrow and the fixed payment) and the opening balance are
  checked at the account currency's places, and the loan payment preview request carries
  the currency.
- Read-side converted and combined figures, on REST and MCP, keep their 4-place internal
  bound (`ConvertedTotal.DestinationScale`, `ReportingScale.MarketValuePlaces`). The
  SPA's formatter presents them at the currency's places.

**Which currency's places a leg takes** (settled by Claude, 2026-11-16). A leg takes its
own account's currency. A category leg is checked and rounded at the real side's
currency, never at the category's own `currency_code` (D13). A posting between two
categories has no real side, so its lines take the category's own code. A single-amount posting
between real accounts in different currencies no longer exists, because D13 requires two
amounts, so no posting has to choose between two sides' places.

**A currency change or a move that would not fit is refused** (settled by Claude,
2026-11-16). Changing an account's currency, or moving a transaction into an account in
another currency, is refused when stored figures carry more places than the new currency
allows, and the refusal names how many figures do not fit. It is the same kind of refusal
as D13's two-amount pair that cannot be represented. The count is of transactions: one
with a leg on the account, or on the Holdings sibling an edit carries with it, that does
not fit, or whose category line paired with that leg does not, counts once, so a
category's mirror is not counted twice. An account edit names the opening balance and
the loan terms separately, as figures of the account; a move names neither, as they stay
on the source account (settled by Claude, 2026-11-16). Otherwise the relabel keeps every
figure, as D13 decides. A figure of a row merged into another is not counted, as the
advisory does not list it: no screen shows or opens the row, so counting it would refuse a
change over a row no screen reaches; the relabel keeps its figure as it is, and deleting
its winner, where the delete removes it, un-merges it into the advisory (decided by the owner, 2026-11-17). A hidden
row and a reminder's template are counted, as each can be opened and edited. The
refusal's message has a sentence for each kind of figure that does not fit, only those
that apply, in this order and joined by a space: "<N> transaction(s) carry more decimal
places than <code> allows (<places>). Edit them to fit first.", "The opening balance has
more decimal places than <code> allows." and "The loan terms have more decimal places
than <code> allows." (decided by the owner, 2026-11-18). Its code and its `count`,
`accountFigures`, `currencyCode` and `places` fields are unchanged.

**A row merged into another is absent to every write by its id** (decided by the owner,
2026-11-18). That no one can open or edit it is enforced, not assumed. Every route and
connected-app tool that writes one transaction by its id answers a merged-away row
exactly as it answers an id that does not exist, with that path's own not-found
response, and changes nothing: the edit, the approve, the reconciliation status, the
delete, the investment edit, delete and approve, the in-kind conversion, and the
recategorize and tag tools. There is no wording or error code of its own. The read the
editor loads a transaction from answers the same way, and a bulk selection that names
one ignores it, as it ignores an unknown id: the bulk unhide, the only restore since the
single-row unhide was removed (ADR-0072 D2, settled by Claude, 2026-11-18), ignores a
merged-away id. The merge keeps its own refusal
(`merge-source-invalid`) at either end. System writes read the row as it is: the
ingest's promote-on-clear and matching, the reminder fire, the consistency repair, and
the delete of a winner, which un-merges its losers. A fail-closed catalog test holds
every route and tool that names a transaction by id to a guarded or an exempt list.

**An ingested amount is rounded and the row is held for review.** An amount from
SimpleFIN, OFX or QFX, QIF, CSV, a brokerage CSV export, or a promote-on-clear, that has more places
than its account's currency allows is rounded to the currency's places, half away from
zero. The bank's figure is kept as sent, in the row's originals (below), and the row
lands needing review, marked as rounded and showing the bank's figure. A feed or
statement currency that differs from its Coffer account's is refused (D13, settled by
Claude). These go with it (settled by Claude, 2026-11-16):

- `SyncRunPromotionDto` gains the synced account's currency.
- A QIF external id is hashed from the amount as parsed, before any rounding, so a
  re-import does not duplicate rows.
- A QIF amount that cannot be parsed is an error on its row, never a silent zero.
- `FidelityActivityToQif` emits cash amounts exactly. It formats with `"0.####"` today,
  which rounds a finer cell before ingest, so the one rounding is at the ingest write and
  the Review note shows the CSV cell's figure unrounded, in the row's stored sign (D15).
- The fee and total carriers, `txn_headers.ingest_fee` and `ingest_amount`, keep the
  provider's figure as sent, with no scale limit. The investment editor rounds the Fee
  prefill it takes from them once, at the brokerage currency's places, and takes the
  principal from the rounded fee, so Amount and Fee add back to the cash total.

**The bank's figure is kept in the row's originals.** The bank's figure as sent and the
rounded mark live in the originals sidecar, `txn_header_originals`
([ADR-0100](0100-canonical-holds-current-sidecar-holds-original.md)), which holds a
transaction's feed values, not in a new carrier column on `txn_headers`. The sidecar
gains an amount column, the bank's figure exactly, with no scale limit, and its own
rounded-at-import flag, needed because an edited amount also differs from the stored
one. When an import rounds an amount, it seeds the row's originals at import time, as
whole as the first edit's capture (`HeaderOriginals.CaptureAsync`): payee, memo, dates
and check number, as the Moneydance importer's seed does, with the unrounded amount and
the flag set. The Review marker shows while the row needs review and the flag is set,
and its figure is the sidecar's amount. `CaptureAsync` snapshots every column on a
header's first edit, so it now also keeps the bank's amount for any edited bank row that
has no originals on file yet, not only a rounded one, and that is accepted. Every place
that lists the sidecar's columns gains the two (`docs/database-schema.md` lists them).
The details (settled by Claude, 2026-11-16; the column-level rules are in
`docs/database-schema.md`):

- The amount recorded is the transaction's amount on its originating account, the sum of
  its legs there, which is the Amount its own register shows: at import, the amount the
  provider hands on, before rounding; at a first edit, the amount as it stood. A row
  captured before migration 301 keeps no amount.
- The bank's figure is in the row's own stored sign, after a CSV layout's sign inversion
  or debit and credit columns and QIF's signing by action, and before rounding; "its
  sign included" (settled above) means that sign.
- A promote-on-clear restates the feed's amount: it writes the cleared figure and the
  flag (set when it rounds, cleared when it does not) into the row's originals, seeding
  them when it rounds and the row has none, and its audit records the amount written to
  the legs.
- A promote-on-clear of a row the user split into more than one posting rewrites no leg:
  every line stays exactly as split, and the account keeps the split's total, its legs on
  the synced account summed, in whichever postings they are. When the cleared figure,
  fitted to the account's currency, equals that total, nothing changes and the row is not
  flagged; when it differs, the row goes to review with its lines as split, and the
  account shows the split's total until the user adjusts it. The originals are restated
  as above either way, so the bank's figure is on file, and the audit records the split's
  total as both figures. A row of one posting takes the cleared figure, as above. A split
  merge loser keeps its lines the same way, and its winner is held as below when the
  cleared figure is not the loser's total (decided by the owner, 2026-11-17).
- A promote-on-clear of a merge loser restates the loser as above, and when the figure
  written to its synced leg differs from the one it held, the row it was folded into goes
  back to review with its own amounts unchanged, a split included; it is never rewritten
  to the bank's figure. A loser that clears at the figure it held leaves its winner alone.
  A winner since folded into another row passes the flag to the row at the end of the
  chain, the one a register shows; a winner since deleted (hidden) is left (decided by the
  owner, 2026-11-17).
- Why a promote-on-clear held a row is kept on the row, in two `txn_headers` columns
  added by migration 301: `review_cleared_amount`, the bank's cleared figure exactly
  as sent, when the row was held because it does not hold that figure, and
  `review_cleared_by_match`, true when the figure is a merged loser's and the row its
  winner, false when it is the row's own split. Every hold writes them, and a hold for a
  rounding clears them, so a figure from an earlier hold is never read as the reason. They
  reach the register row beside the originals' figure. They are not derived from data
  already stored: the merge does not require equal amounts, so a loser's figure can differ
  from its winner's from the merge on, and a split's originals hold an imported figure on
  a row that never cleared (settled by Claude, 2026-11-17).
- A seeded row reads `has_overrides` true from import, as a Moneydance seed does;
  nothing that reads it is affected. Where the SimpleFIN pre-split backfill rewrites a
  row's payee and memo, it writes them into the originals too when those still hold the
  values it replaces, so payee recall reads the row as it reads one with no originals.
- The figure and the flag reach the To review widget, Review… and the register on the
  register row, read through `resolved_transactions`' existing join to the sidecar.
- Snapshot capture and restore copy whole rows and need no change.

The Moneydance importer's seed carries the amount as written, with its flag false, because
a seed is a whole-row snapshot and a later edit of a seeded row keeps nothing; migration
260's seeds keep none (settled by Claude, 2026-11-16).

**The Review marker** (design approved 2026-11-16, boards RV-1a and RV-2). A rounded row
in the home screen's To review widget (ADR-0109) shows a muted second line under the
amount: "Rounded from <bank figure> as sent by the bank". It is plain text after the
amount in reading order, so it is seen without hovering, read with the row by a screen
reader, and kept in a screenshot. The row grows by that one line, and other rows do not
change. The editor that Review… opens carries the same sentence as the Amount field's
description. In the bank editor it is on its own line directly under Amount, also while
the posting carries a second amount (D14), not in the rate line's cell (decided by the
owner, 2026-11-17). With a second amount, the rate line then sits one line lower, and
in a wide dialog the note and the rate line both end at the second amount's right edge;
in a narrow one each sits under its own field (decided by the owner, 2026-11-17). The
bank's figure is shown as sent, with no currency symbol, so it is not
read as an amount in the account's currency. It is shown exactly as sent, its sign
included (settled by Claude, 2026-11-16).

A row a promote-on-clear held because it does not hold the bank's cleared figure says
why on the same line, in the To review widget and as Amount's description in the editor
Review… opens, in place of a rounded note left from the row's own import (decided by the
owner, 2026-11-17). The row a cleared transaction was merged into reads "The bank cleared
a matched transaction at <bank figure>"; a split held for its own figure reads "The bank
cleared this at <bank figure>; your lines add up to <split total>". Both figures follow
the rule above: shown as sent, with no currency symbol, the split's total at its
currency's places. The total is the one the bank's figure was compared with, the split's
lines on the account the transaction was entered from, so only that account's row gives
it; the review flag is the whole transaction's, and a line the split lists on another
account (a transfer line, read-only there) reads "The bank cleared this at <bank figure>",
as the rounded note gives the bank's figure on every side (settled by Claude,
2026-11-17). The note is read from the row's held figure
(`review_cleared_amount`, above) while the row needs review, so a row accepted since
shows none, and a later rounding of the row's own figure shows the rounded note again.
The wording is proposed and awaits the owner's approval. The editor's split layout has no
Amount field; where it shows the note is not yet decided, and until then a split shows it
in the widget only.

**A CSV layout names its decimal separator.** Each CSV layout gets a decimal-separator
setting, point or comma. It is guessed from the sample only when a layout is first made,
and the wizard shows the guess. A saved layout keeps the separator it was saved with and
is never guessed again. The setting is part of the layout's YAML, so the mapping schema,
`CsvMappingValidator` and the MCP tool docs change (ADR-0031). The key is required in
CSV mapping schema version 2: `CsvMappingValidator.CurrentVersion` goes to 2, per the
`feed_csv_mappings` `schema_version` rule, bumped because a document valid before would
now be rejected. A version-1 document, stored or sent, is still accepted and reads with
a point, the only reading it ever had, and is not guessed. A layout written without a
sample, by hand or through the MCP tools, must therefore name the separator (settled by
Claude, 2026-11-16). This fixes
a defect: CSV and QIF parsing read every comma as a thousands separator, so a file that
writes a decimal comma is read as a far larger amount, silently.

**The CSV wizard** (design approved 2026-11-16, boards CS-1 to CS-3). A "Decimal
separator" select sits at the foot of the Amount fieldset on the "Describe this file"
step, built like that step's other format choices. Its options are Point and Comma, each
followed by an example. On a new layout it is preset from the sample, with the muted line
"Guessed from the sample."; a saved layout shows no such line. The sample grid shows the
cells as the file has them, as today, with no "reads as" column, because the server is
the only reader of a layout (settled by Claude, 2026-11-16). A cell that cannot be read
with the chosen separator is reported after Continue, on the "Confirm the import" step,
in the existing Preview warnings box, as `csv_row_unreadable` naming the line, and only
the readable rows are counted. The message gains the clause "with a point as the decimal
separator" (or a comma); the rest of it is the existing message.

**QIF has no layout, so it refuses an amount it cannot read one way only** (ADR-0042):
a comma that does not group the whole part, and, in an account whose currency has 3
places, any comma. The import names the line. QIF accepts commas that group the whole part in thousands (a first group of one
to three digits, then groups of three) or lakh-style (a first group of one or two
digits, groups of two, then a last group of three, with two grouping characters at
least), by the one grouping rule a CSV layout reads by; the first group never starts
with a zero, so a longer first group or one starting with a zero is refused; it is read one way only, and still refuses any comma in a 3-place currency (decided
2026-11-17). The rule covers every numeric field QIF reads: the amount, the commission,
the quantity and the price. The 3-place half applies to money only, the amount and the
commission; a quantity or a price is never money, so there a comma that groups the whole
part reads as grouping whatever the account's currency. The currency is the target
account's where it is known: an import always names it, and a preview applies the rule
once the import dialog has chosen the account and sends it (settled by Claude,
2026-11-16).

**A Moneydance currency is accepted when its places fit** (settled by Claude,
2026-11-16). The import goes ahead when the file's own `dec` for a currency is at most
Coffer's places for that code, so every amount fits exactly, and the preview refuses it
when the `dec` is greater, naming the account and both place counts (D5). The loan
mapper checks escrow and the fixed payment the same way.

**Prices and display.**

- A price's minimum places equal its currency's places, and its maximum is the larger of
  6 and those places (settled by Claude, 2026-11-16).
- `PriceField` takes `step="any"`, and a price gets no money rounding. The SPA has one
  `roundToMinorUnits` helper, half away from zero, that replaces every `toFixed(2)` and
  ×100 money site and is applied to the save body too. While a currency is null, nothing
  is rounded (settled by Claude, 2026-11-16).
- Prices (current price, cost per share) are formatted by `formatPrice` everywhere.
  Money (cost basis, market value, unrealized gain, trade amount) is formatted by
  `formatCurrency` at the currency's places everywhere. `HoldingsPanel` and
  `SecurityDetailPage` converge on this.
- Money columns stay right-aligned, as today, with no fraction slot. Where currencies
  with different places mix, the decimal points do not line up, and that is accepted.

**The upgrade.** One deterministic migration, 301, with no `now()`:

- It drops `ck_txn_legs_amount_scale_2`, and adds the 3-place ceiling `NOT VALID` and
  then validates it. Every existing row passes, because every leg is at 2 places.
  Migration 159 is never edited.
- It widens `budget_targets` and `realized_gains`. The `ALTER TYPE` rewrites both tables
  under `ACCESS EXCLUSIVE`.
- It redefines `recompute_holdings_cost_basis` and `realized_gains_walk`, creates the
  minor-units function, and reissues the COMMENTs.
- It adds `amount`, an unbounded `NUMERIC`, and the flag `rounded_at_import`,
  `BOOLEAN NOT NULL DEFAULT false`, to `txn_header_originals`, and exposes both on
  `resolved_transactions`. Existing rows get no amount and a cleared flag; nothing is
  backfilled, because a leg's amount today may be an edit, not the bank's (settled by
  Claude, 2026-11-16).
- Stored amounts that do not fit are kept and listed by the consistency check as an
  advisory. There is no pre-upgrade script.
- Moneydance imports made by released code stamped their accounts USD at 2 places, and
  they stay consistent.
- Old full backups migrate forward at restore, so the migration must succeed on any
  historic backup.
- Like any new migration, it retires every existing ledger snapshot, and the release notes
  say so.

The release notes say that amounts now show and are typed at each currency's places,
that Moneydance currencies with 3 places now import, that CSV layouts gain a decimal
separator, that existing snapshots are retired, and how existing amounts that do not fit
their currency are treated.

## The FU-106 inventory, corrected

Every figure below shows an amount in the reporting currency, or D9's answer. The list
covers FU-106's four groups, plus what the inventory missed:

- **Report rows grouped by account.** Each row stays native and gains a `currencyCode`;
  the total converts. So do its group totals; the period totals and the rest convert.
  Rows by any other dimension combine accounts and convert. A chart of rows not all in
  the reporting currency is not drawn, since one axis would hold two currencies; the
  page says why in its place, and the table shows each row in its own currency.
- **The sidebar rail's folder and section totals** (ADR-0106 D6). The server converts
  and sums them (`GET …/accounts/subtotals`), and the rail's "mixed" marker goes.
- **`GET /reports/returns` and `GET /reports/allocation`.** These are the same DTOs as
  the MCP tools.
- **`net_worth`'s per-account lines.** These stay native and gain a `currencyCode`; the
  total converts.
- **The Home Watchlist's budget rows.**
- **A category's register.** Each row stays in its account's currency; the Balance
  converts at each transaction's date. So does the bulk bar's Σ when the selected rows
  are in more than one currency; in one currency it is that currency's sum. A row's
  inputs take that row's own currency, its real side's, and the register's amount-range
  filter shows no symbol (D14).
- **Investment results with no `currencyCode`.** `holdings_snapshot`, `allocation`,
  `investment_income`, `realized_gains` and `returns` gain one.
- **The scalar `returns` rates.** These were computed over cross-currency sums, not only
  the totals.
- **Positions priced in another currency** (D5, D7).
- **The Maintenance advisory's `holdingValue`**, and the order it gives the list.
- **Cash ahead's combined line**, at the latest rate.

The two client-side mixed checks named in D7 go with them. Of the MCP tools,
`investment_income`, `realized_gains` and `returns` never said they sum across currencies,
despite the inventory's "as their descriptions say". Every converted tool's description
now says what it converts and how.

## Consequences

- Coffer gains a fourth named egress, to Frankfurter. It is opt-in and documented
  wherever the other three are.
- Per-ledger storage fetches the same series once per ledger, with no cross-ledger
  dedupe, as prices already do.
- Net-worth history and the returns series gain a rate lookup per account per day. Both
  are timed on the stress ledger and on a sparse long-lived shape before merge.
- Money is no longer 2 places everywhere (D15). ADR-0073's 2-place rule and its CHECK,
  and ADR-0023 §H's money inputs, now follow each currency's places. Every API writer of
  a money figure checks it against its currency, and every SPA money site rounds through
  one helper.
- Migration 301 rewrites `realized_gains` and `budget_targets` under
  `ACCESS EXCLUSIVE`, and, like any new migration, retires every existing ledger
  snapshot. It must succeed on any historic backup restored forward.
- Stored amounts that do not fit their currency stay as they are, and the consistency
  check lists them as an advisory, not counted in its verdict.
- Ingested rows that needed rounding land in review, showing the bank's figure, which
  their originals keep. The first edit of a transaction that has no originals yet now
  also keeps its amount from before the edit.
- One PR, with commits per layer.

## To confirm in the build, not assumed here

- The `instrumentType` Yahoo returns for a mutual fund.
- Whether a blended Frankfurter response names its source per rate. If it does not, the
  column stores what the response does name.
- Any range limit on Frankfurter's `/v2/rates`. If there is one, a backfill pages by year.
- The minor-unit quote codes Yahoo uses (`GBp` and the like, D5). These are quote units,
  not D15's minor units.
- Every money site that rounds to 2 places today, in the API and the SPA. The build lists
  them and moves each onto D15's rule; this ADR does not assume the list is known.
- That migration 301 runs on an existing install and on an old full backup restored
  forward, before it is released.
- That every place listing `txn_header_originals`' columns carries the two new ones
  (`docs/database-schema.md` lists them), shown by a snapshot round trip and the restore
  drill on a ledger with a seeded row.

## Alternatives considered

- **A fixed product benchmark.** Rejected: one index cannot fit every portfolio.
- **Price-only indexes.** Rejected: the portfolio chain includes income, so the comparison
  would be biased.
- **Tiingo for rates.** Rejected: licensed for internal use only, and its forex history
  starts in 2020.
- **ECB reference rates only.** Rejected: 45 currencies against the blended feed's 219.
- **Install-wide storage.** Rejected: it needs a new read-all RLS shape and an
  install-wide consent no ledger owns.
- **A stale rate, marked.** Rejected: it reads as a number while being wrong.
- **Leaving out an account with no rate.** Rejected: the total understates the ledger
  and still reads as a total.
- **Rewriting budget targets when the reporting currency changes.** Rejected: it
  replaces what was typed, and a change and its reversal would not round-trip.
- **Hand entry overriding a fetch.** Rejected: two sources would compete for a day, and
  the ladder would gain a fourth home.
- **Two PRs.** Rejected: CI overhead, and the benchmark would need conversion either way.
- **Writing a transfer's ratio back to `fx_rates`.** Rejected: the first stored value for
  a day wins (D6), so one transfer's bank rate would stand for that day in every
  conversion.
- **Fetching each account-currency pair directly.** Rejected: more pairs to fetch and
  backfill, the rates view and hand entry would grow, and a fetched cross could disagree
  with the cross the two stored rates imply.
- **One symbol on a category register's amount filter.** Rejected: its rows mix
  currencies, so no one symbol is true of them.
- **2 places for every currency.** Rejected: a 0-place currency would show and take
  minor units it does not have, and a 3-place currency could not be held exactly.
- **The browser's CLDR digits through `Intl`'s defaults.** Rejected: CLDR disagrees with
  ISO 4217 for several currencies, and the SPA would disagree with the API.
- **A 4-place ceiling.** Rejected: the only ISO codes with 4 places are the fund units
  CLF and UYW, which are refused as account and ledger currencies, so no figure needs it.
- **A currency stored on each leg, so a CHECK could read it.** Rejected: it copies the
  account's currency onto every leg, and a relabel, a move and a currency change would
  each have to keep the copies in step, for a rule the API writers already enforce.
- **Rounding existing data by migration.** Rejected: it would change stored figures
  without anyone seeing which, and could not be undone. They are kept and listed instead.
- **Skipping or refusing an ingested row with too many places.** Rejected: the bank's
  transaction would be missing from the register, or a whole import would fail, over
  places the currency cannot hold. Rounding it and holding the row for review keeps the
  row and the bank's figure.
- **A new `ingest_*` carrier column on `txn_headers` for the bank's figure and the
  rounded mark.** Rejected: a transaction's feed originals stay in one place, the
  sidecar (ADR-0100).
- **Refusing a cell with a decimal comma, instead of a separator setting.** Rejected: a
  file that writes decimal commas could never be imported.
- **A fraction slot that lines up decimal points.** Rejected: it changes every money
  column's layout, and shifts a column's figures when its mix of currencies changes.
  Right-aligned, as today, was chosen.
- **A "Rounded" badge on a Review row, with the bank's figure in its tooltip.** Rejected:
  the figure to check is seen only on hover, keyboard and touch users cannot reach it,
  and the badge takes space from the category column.
