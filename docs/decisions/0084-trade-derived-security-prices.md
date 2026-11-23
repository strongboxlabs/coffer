# 0084 — Trade-derived security prices

* Status: Accepted; amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (a security's combined figures
  convert into the reporting currency)
* Date: 2026-07-25
* Extends: [ADR-0070](0070-daily-closing-price-model.md) (daily closing-price model + source ladder)
* Related: [ADR-0054](0054-market-data-quote-provider.md) (fetch provider), [ADR-0032](0032-triggers-as-last-resort.md) (recompute at call sites / interceptors, not triggers), [ADR-0063](0063-mcp-server.md) (net-worth-history / TWR read the priced holdings)

## Context

A security transaction records an **execution price** (`txn_legs.unit_price` = |cash| / |shares|), but **no code path writes that price into `security_prices`** — the source ladder was `import` / `fetch` / `manual` / `simplefin` only, and neither the native API investment write nor MCP writes touch `security_prices`. Consequence: a security that is *held but not fed* (an account no longer synced, any ticker the quote provider doesn't cover) has price history only from the one-time `import` seed — so the mig-172 as-of valuation feeder falls back to the last trade price (its tier-2), and if even that is absent it values the holding at **0**. Net-worth history and returns then understate those holdings for as long as the gap persists, and the gap closing reads as a spurious "jump" where net worth is really catching up to a long-standing pricing gap.

The execution price is a real market observation and should seed `security_prices` — while never clobbering a truer source (a Yahoo EOD close, a manual gap-fill).

## Decisions

### D1 — Add a `trade` source, ranked below feed/manual, above simplefin/import
`security_prices.source` gains `trade`. The ADR-0070 D2 ladder becomes:
```
manual == fetch (Yahoo)  >  trade  >  simplefin  >  import
```
Ranks: `import 0 < simplefin 1 < trade 2 < fetch 3 == manual 3` (only the ordering matters). A trade is a real execution — it beats the one-time import seed and SimpleFIN's intraday balance — but a **Yahoo EOD close or a manual price outranks it and overwrites**, so the scheduled feed reclaims the day. Same upsert rule as D2: **overwrite iff `rank(incoming) >= rank(existing)`**; insert when the day is empty.

### D2 — Written from the execution price, in a `SaveChangesInterceptor`
On any create/edit that lands an investment **trade** leg (`security_id` set, `quantity <> 0`, `unit_price > 0` — so `buy`/`sell`/`buyx`/`sellx`/`dividend_reinvest`; the priceless `dividend_cash`/`divx`/`inc`/`exp`/`misc`/`transfer`/`transfer_shares` legs have `pamt = 0 → unit_price 0` and are skipped), a `trade`-source price is upserted for `(security, day)`.

The writer is a **`SaveChangesInterceptor`** (`TradePriceFromLegInterceptor`), a sibling of `HoldingsRecomputeInterceptor` — **not a DB trigger** (ADR-0032) and not scattered call-site calls. It fires for every EF writer (native API + MCP) automatically; the ChangeTracker gives the changed legs. The Moneydance importer (Dapper, bypasses EF) is covered by the D5 backfill, not the interceptor.

To avoid re-entrancy (a tracked `SaveChanges` inside `SavedChanges` would re-fire interceptors) and keep the conflict SQL out of the app layer, the upsert is a **Postgres function** `security_price_upsert_from_trade(ledger, security, day, price)` invoked post-save via `HasDbFunction`, exactly like `HoldingsRecomputeService` calls `recompute_holdings_for_account_security`.

**Amended 2026-11-04 (migration 274): a trade price is in the currency of the brokerage that made the trade.** The upsert stored every trade-derived price with `currency_code` `USD`, and the importer's seed did the same, whatever currency the brokerage is kept in. Nothing converts (FU-106), so a buy in a brokerage kept in another currency stored its price as a dollar figure, and the readers that name a security's current price in its price row's currency (the security's detail, the securities catalog, the unbacked-price advisory: the "current figures name their currency" amendment below) printed it in dollars. The rule now names a currency with the price: that of the brokerage that made the execution the rule picks for the day (last by `seq`, then largest by value, then leg id). The execution's line sits on its brokerage's holdings account (ADR-0019 Rule 4); the currency read is the brokerage's, the account that names that holdings account, or a line's own account's when no brokerage names it. When several brokerages name one holdings account and disagree, the first code in ordinal order is read, as the holdings-currency check expects. The holdings account's own currency is never read: it is held to its brokerage's (ADR-0019, 2026-11-04 amendment), but while one still holds another code, as an edit before migration 274 could leave it, its trades are written and checked in the brokerage's currency, the one the account editor shows and the one the holdings-currency repair sets it to. One security traded in two brokerages on one day takes the currency of the brokerage whose execution the rule picks.

- `fn_trade_execution_for_day` is the rule, returning the picked execution's price, its brokerage's currency and the account it is read from (`fn_trade_currency`, over `fn_trade_day_executions`); `fn_trade_price_for_day`, the price alone, is dropped, as nothing calls it. `fn_trade_price_reseed` writes both through `security_price_upsert_from_trade`, which takes the currency as a fifth argument; the four-argument form is dropped. The rank gate is unchanged.
- A brokerage's currency edit re-derives the day of every trade on it and on its holdings account, whatever the holdings account held before. The holdings-currency repair relabels holdings accounts and writes no price.
- One lock order for every path that writes a trade price into a committed ledger, or a currency the rule reads (the API's writers, native and MCP): first the ledger write lock, which every writer that can recompute balances or holdings and every consistency repair takes as its transaction's first statement ([ADR-0112](0112-one-write-lock-per-ledger.md), amended 2026-11-05); then the accounts whose currency the rule reads for the days the path derives (`fn_trade_price_accounts`), with the account a currency edit writes, in id order, in one statement (`fn_lock_trade_price_accounts`), before any holdings account row and any `security_prices` row; then the read, repeated while the set it reads grows; then the price rows, in (ledger, security, day) order. A re-derivation (every writer of a trade's day, through `TradePriceRecomputeService`, which locks all its days' accounts at once and writes its days in that order) and the holdings-currency repair lock `FOR SHARE`; a currency edit and the trade-price repair lock `FOR NO KEY UPDATE`. So a trade saved while a currency edit or the trade-price repair is uncommitted waits for it and writes what it commits, and an edit or the trade-price repair that starts after a trade's re-derivation waits for that transaction. The holdings-currency repair and a re-derivation take no lock the other holds, so neither waits for the other. The holdings-currency repair's `FOR SHARE` on its brokerages waits for any request in flight that writes or locks one of them more strongly (an edit of the brokerage, which updates its row, a currency edit among them, and the trade-price repair, whose `FOR NO KEY UPDATE` covers every brokerage whose currency the rule reads for the days it writes), and any such request that starts later waits for the repair. Re-derivations of the same days meet at their price rows, which the one order keeps from forming a cycle. Neither mode excludes the `KEY SHARE` a new line takes, and no path locks a holdings account before its brokerage. `merge_securities` first locks the source security `FOR UPDATE` (`fn_lock_securities`), before it reads the source's lines. Each path takes all its account locks in one id-ordered statement before any price row, and its price rows in one order, so that no two paths hold an account or a price row the other waits for. *Superseded 2026-11-05 by [ADR-0112](0112-one-write-lock-per-ledger.md) within one ledger:* every path in this order that can recompute balances or holdings, and every repair, takes the ledger write lock first, as do a currency edit and a re-derivation that opens its own transaction, so two of those never run at once in one ledger. A re-derivation run in a caller's transaction that recomputes neither balances nor holdings takes none, and the account and price-row steps (1 to 3) keep it from a cycle. A trade saved in several steps, each of which locks its accounts again, holds the ledger write lock across all of them, so the trade-price repair can no longer hold an account a later step waits for while it waits for one an earlier step holds. Two re-derivations that take the lock, or such a re-derivation and the holdings-currency repair, take turns there before they reach the accounts, where `FOR SHARE` would let them run at once; and the deadlocks and narrow timing windows the account and price-row steps leave between two paths (a security merge racing a re-dated trade, two saves re-deriving the same day, either of which could leave a trade price stale) cannot form between them. A transaction outside this order that row-locks the same accounts without the ledger write lock can still deadlock with one of them; Postgres then cancels one request, which writes nothing, and a retry succeeds. That is an accepted limit. The importer's seed is outside the order: it writes only into the ledger its own transaction creates, which no other session sees until it commits.
- The importer's `TradePriceSeedStep` writes the currency `fn_trade_currency` names. An imported account is in its own Moneydance currency (ADR-0114 D5), so a trade's price is in its brokerage's.
- `fn_trade_price_check` returns the expected and the stored currency, and reports `value` for a stored `import`, `simplefin` or `trade` row in another currency, as for one at another price. A `fetch` or `manual` price owns its day whatever its currency and is not compared. The consistency report names such a row as one mismatch per figure: the price, when it differs, as before, and the currency in a field naming both codes, with both figures 0, as there is no number to compare. The trade-price repair writes the brokerage's currency.

On an existing install no row is rewritten: a trade price stored in dollars on a day a trade in a brokerage kept in another currency prices is reported once by the check as a `value` mismatch on the currency, and the trade-price repair writes the brokerage's currency through the rank-gated writer.

### D3 — `price_date` is the UTC calendar day of `posted_at`
`price_date` is a `DATE` keyed in **UTC** (ADR-0070 D5: mig-154 converted with `AT TIME ZONE 'UTC'::date`; Yahoo close sits at midnight UTC). A transaction's date is stored as **midnight UTC** of that calendar day (importer `ParseMdDate` → `DateTimeOffset(…, TimeSpan.Zero)`; native create stores the request date as a UTC `timestamptz`). So `price_date(trade) = (posted_at AT TIME ZONE 'UTC')::date` — a trade and a same-day Yahoo close share one day-row, so the rank gate lets the feed overwrite it. In C# the interceptor normalizes `posted_at` to UTC before `DateOnly.FromDateTime` (dodging the ADR-0070 D7 `Kind` asymmetry); the SQL backfill uses the identical `AT TIME ZONE 'UTC'` expression.

### D4 — Edit re-upserts; delete does not retract
Editing a trade re-upserts the (possibly new) day at `trade` rank. **A delete does not retract** the price row — a past execution was a real observation, and the row is harmless (a feed close or a later write supersedes it by rank). This keeps the interceptor to Added/Modified legs only.

**Amended 2026-11-03: the day a trade leaves is re-derived from the executions that remain.** "A delete does not retract" stands; "Added/Modified legs only" does not. A day's price is the rule's (migration 234's `fn_trade_price_for_day`): the day's last execution by `seq`. With two executions on one day, removing the later one from the day left the stored price the departed trade's, while `fn_trade_price_check` derived the remaining one's, so the scheduled consistency check reported a repairable `value` mismatch after ordinary use. The refined rule:

- An execution leaves a (security, UTC day) when its transaction is deleted, singly or in bulk; when its transaction's date, action or provider action changes; when its line is deleted, or the line's security, quantity or unit price changes; and when its security is merged into another (ADR-0068 `merge_securities`).
- The day it leaves is re-derived as the day a trade arrives on is: `fn_trade_price_reseed`, at `trade` rank, so a `fetch` or `manual` price for the day is never overwritten. When executions remain on the day, the day takes the price the rule derives from them.
- When none remains, the rule derives nothing and the stored row is kept: a past execution was a real observation. Nothing retracts it; the unbacked-price advisory (D6) lists it.

`TradePriceFromLegInterceptor` reads the trade lines of every transaction such a save touches from the database before the save, as they stand then, and re-derives each (security, day) they sat on after it, beside the days the save's trades arrive on; re-deriving a day no execution actually left writes the price the rule already names for it. The bulk delete and `merge_securities` bypass the change tracker, so each re-derives the same days itself: the bulk delete the days of the executions it deletes, the merge the keeper's day for each execution it moves (the merged-away security keeps its rows, as nothing of it remains to derive from). A day left stale by such a change before this amendment is reported by the check and written by the repair: a `value` mismatch where the day still holds a departed trade's price, and `missing` where a security merge moved a trade onto a keeper's day that holds no price. No migration rewrites either.

**Amended 2026-11-04 (migration 273): one rule for what a trade is.** The 2026-10-21 amendment below made hidden, merged and template headers no trades in the ledger for valuation (`fn_last_trade_price`); the day's rule left out only the template. A hidden or merged-away execution therefore kept pricing its day while it priced no valuation, and deleting an imported trade, which hides it, kept its price on the day where deleting a manual one, which removes the row, did not. The rule is now one: a trade is an execution (D6) on a header that is not a recurring template, not hidden and not merged into another, for the day's price exactly as for valuation.

- `fn_trade_price_for_day` (so `fn_trade_price_reseed`), `fn_trade_price_check` and `fn_unbacked_price_dates` read the headers through `live_txn_headers` with `fn_last_trade_price`'s filter, `is_hidden = FALSE AND is_merged_into IS NULL`.
- An execution therefore also leaves its day when its transaction is hidden (a delete of an imported row, singly or in bulk) or merged into another, and arrives on it when the transaction is restored or un-merged (its winner deleted, singly or in bulk). `TradePriceFromLegInterceptor` counts a changed `is_hidden` or `is_merged_into` as a change to every trade line of the header; the bulk delete re-derives the days of the rows it hides and of the rows its delete un-merges, and the bulk restore the days of the rows it unhides. A day whose every execution is hidden or merged away keeps its price, as above, and the unbacked-price advisory lists it.
- The Moneydance importer's `TradePriceSeedStep` states the same rule. It also lacked D6's allow-list, so a `ShrsIn`/`ShrsOut` priced its day on import; it now calls `fn_leg_prices_a_day` as the rule does. The import writes every header visible and unmerged, so the header filter decides nothing there; it is stated so the copy matches the rule.
- The unbacked-price advisory (`fn_unbacked_price_securities`) takes its dates from `fn_unbacked_price_dates`, asked once for each security holding a trade-source price, where it asked `fn_price_is_unbacked` once per stored price. D6's point about shape applies to it as to the price list: per price, its cost was prices × trades within a security, which a long price history takes past the API's command timeout, and the consistency report, which reads the advisory in the same request, failed with it. The advisory lists the same securities, counts, dates and order, and adds no statement of the rule of its own. `fn_price_is_unbacked` has no caller after this, in SQL or in C#, so migration 273 drops it and `AppDbContext.PriceIsUnbacked`, its declaration, goes with it: a test nothing calls is a second statement of the advisory's test that nothing keeps in step. `fn_unbacked_price_dates` is the only one. The comparisons that pin the advisory to the per-row form write that form's body out.

On an existing install no row is rewritten. A day whose stored price came from an execution an earlier version hid or merged away, while another execution remains on it, is reported once by the check as a `value` mismatch, and the trade-price repair writes the remaining execution's price; a day whose only execution is hidden or merged away keeps its price and joins the advisory.

### D5 — One-time backfill from existing trades (migration 177)
Derive `trade` rows for all historical investment trade legs (`security_id` set, `quantity <> 0`, `unit_price > 0`), one per `(security, UTC-day)` taking the last trade of the day, rank-gated so it overwrites only `import`/`simplefin`/`trade` rows (never a `fetch`/`manual` price). This covers imported history (the held-but-unfed funds described above) too. On a trade day this **replaces the MD `import` snapshot** with the execution price (the truer observation), by design.

The migration backfill is one-time — it only covers ledgers that existed at deploy. So the **Moneydance importer** (Dapper, which bypasses the D2 EF interceptor) runs the identical seed at end-of-import via a pipeline step, `TradePriceSeedStep` — the per-ledger analogue of the backfill, scoped to the freshly-imported ledger and ordered right after `PriceSnapshotImportStep` so the `csnap` `import` prices exist first and the trade prices upsert over them on trade days. This is the same call-site-recompute contract the importer already uses for balances (`BalanceRecomputeStep`) and holdings; a FUTURE import is thereby covered too, not just pre-existing ledgers.

### D6 — A share movement is not a price (migrations 235, 236, 2026-09-28)

D5 assumed transfers were already excluded because "transfer_shares carries pamt = 0 -> unit_price 0/NULL writes no price". That covers the zero case only. Moneydance records a `ShrsIn`/`ShrsOut` — a movement with no cash consideration — with a NOMINAL amount equal to the share count (the leg's `pamt`, read as currency, carries the same digits as its `samt` read as shares), and the importer's `unit_price = amount / quantity` turns that into a unit price of about one, which passes `unit_price > 0` and becomes a `trade` price on funds whose real price is far higher.

**The test is the ACTION, full stop** (migration 237, replacing 235's narrower rule). Excluding such a leg only when its amount still equals the share count, on the reasoning that a `ShrsIn`/`ShrsOut` leg carrying a "real" figure is a real observation, fails twice:

* *The arithmetic is off by a cent.* The nominal amount can be truncated where 235 assumed rounding: a leg whose share count has a third decimal of 5 carries an amount truncated to two places, while `round(quantity, 2)` takes it up a cent — so the equality does not match and the placeholder still prices its day just under one.
* *A "real" figure on a share movement is cost basis.* An in-kind move carries each lot across at its original purchase price, so one security on one day arrives with as many unit_prices as there were lots, at widely different prices, and they sort AFTER the day's genuine trades by `seq`, displacing them. A check of such a row compares basis with basis: both the stored and the expected figure are lot basis, and neither is a market price.

Nothing in the row separates basis from price. The action does, so the action is the whole rule and `fn_leg_prices_a_day` has no arithmetic.

**And the rule is an allow-list, not an exclusion list** (migration 238). An exclusion keyed on the Moneydance spelling cannot see Coffer's own `transfer_shares`, which `convert_in_kind_transfer` creates natively with no provider payload and legs priced at each lot's carried basis. An exclusion list FAILS OPEN: an action nobody enumerated prices days by default.

Enumerating the `(action, provider_action)` pairs that can carry a priced security leg gives a short list, and a clean split. Executions: `buy`, `buyx`, `sell`, `sellx`, `dividend_reinvest` (the last at NAV, across all five Reinv* provider spellings). Not executions: `buyx/ShrsIn`, `sellx/ShrsOut`, `sell/ShrsOut`, and `transfer_shares` in any form. Note `buyx` appears on both sides — it is the mapping target for a real `BuyX` and for `ShrsIn` alike — so neither field decides alone and both are arguments.

Only those actions price a day. Anything else, including an action added later, prices nothing until it is named. A missing price surfaces as a stale valuation; a wrong one silently mis-values a holding and the checker cannot see it, because the checker shares the rule. A money-market fund genuinely priced at one is unaffected, because a sweep purchase is a `Buy`.

Cost: a day loses its trade price only when no execution occurred on it — the allow-list removes share movements, never trades. **Trade-off:** correcting a `ShrsOut` by hand no longer seeds a price either. Prices already written that way survive, but a future correction records the transaction without a price — which is right, since the corrected figure is a valuation rather than an execution, and the price CRUD exists for asserting one deliberately.

With the action rule in place no writer produces such a price any more. **Rows the old rule wrote are a closed set and cannot grow**, which is why migration 236 deletes them outright rather than adding a reason code and repair verb for a set that will never gain a member.

**A price nothing derives is ADVISORY, not a finding** (migrations 240, 241 — superseding 239's `orphaned` reason). Reported inside the consistency check, such prices read "N of M no longer backed by a trade" beside a Repair button that touches none of them. The framing is wrong, not only the wording: `security_prices` records THAT a trade wrote a row and never WHICH one, so "nothing derives this" can only be inferred, and cannot distinguish a deleted trade (D4 keeps its price on purpose) from a rule change from an edit. Nor is it a backlog to drain — deleting or editing any priced trade creates one, so the category is permanently non-empty by design.

So the check reports only `missing` and `value`, both repairable, and unbacked prices become a separate advisory: a list of SECURITIES, because that is the unit someone acts on, ordered by what the holding is worth now so what still carries money leads and a sold position sorts last rather than vanishing. Acting clears it either way — editing a price sets `source = 'manual'`, deleting removes the row. The security's price list strikes through the source word on such a row, and only that word: a struck-through row would read as deleted, and these are not.

Shape matters here. The per-row predicate `fn_price_is_unbacked` (dropped by migration 273, once nothing called it) rescans a security's legs for every price, so its cost is prices × legs and a long daily price history makes it the slow path; `fn_unbacked_price_dates` (241) answers it in one pass and both the paginated list and `price_history` use that. MCP's `price_point` carries `source` — without it an agent cannot tell a typed figure from a quote-feed close while `update_price` and `delete_price` let it act on either.

**Orphans are reported, not guessed at** (migration 239, now superseded). Migrations 236, 237 and 238 each end with a DELETE whose predicate is a fixed band: prices near one, on a security that otherwise trades above a fixed floor. A fixed band reaches only the figures that fall inside it. It cannot reach an orphaned trade price that is not near one, such as a carried lot basis; on an install whose placeholder figures are not near one it does nothing, while 238 simultaneously stops the check examining those days.

The general statement needs none: a `trade`-source price on a day the rule derives nothing for is not backed by anything. `fn_trade_price_check` now returns those with `reason = 'orphaned'`, `account_id` and `header_id` NULL, and `expected_price` equal to the stored price because there is nothing to derive. The repair does **not** act on them — D4 keeps the price a since-deleted trade seeded, and no column distinguishes that from a basis figure an in-kind transfer left behind — so the row links to the security's price list and a person decides. No more DELETEs in migrations.

**The checker's blind spot is accepted, not fixed.** `fn_trade_price_check` re-states the rule rather than calling the writer, so it catches the writers diverging — and by construction cannot catch the RULE being wrong, because both sides then compute the same wrong answer: the report can call a ledger healthy while placeholder prices of one sit in it. A plausibility detector (a `trade` price near one on a security whose own history reaches far above it) would catch the class directly, and is deliberately NOT built: the source is closed by this decision, the historical damage is deleted, and a heuristic threshold on volatile securities raises false alarms, which make a check untrustworthy. If a future writer invents prices some other way, this is the hole it will come through.

## Consequences

- Held securities get a real price observation at every trade, so the as-of feeder resolves `feed` instead of a stale/absent tier-2 — net-worth history and returns stop valuing traded-but-unfed holdings at 0.
- It does **not** invent prices *between* trades: a security with no feed and no recent trade is still marked stale (valued at its last trade). Accurate inter-trade history for such tickers is a separate concern (quote-provider historical backfill) — out of scope here.
- Deferred follow-up "MD-parity: native trades populate `security_prices`" is delivered by this ADR.
- **Superseded in part by migration 234 (2026-09-26).** D2 described the interceptor
as writing the execution price of the leg it saw. Taken literally, the WRITER then never
applies D5's "last trade of the day" rule: correcting one leg of a multi-leg day seeds
that leg's price, while the consistency check derives the day's by the rule, and the two
disagree by construction on a ledger that is fine. The
rule now lives once, in `fn_trade_price_for_day`, and the interceptor names the
(security, day) it touched rather than a price. The rule also gained a total ordering —
`h.seq DESC, abs(amount) DESC, l.id DESC` — because one header can carry several
security legs at different prices and `seq` alone ties, leaving the choice to whatever
plan Postgres picked that run.
- **Amended 2026-10-21 (migration 259): one no-price rule for every valuation.** A
position with no `security_prices` row on or before the date valued is valued at the
unit price of the most recent trade of that security in the LEDGER on or before that
date, and only if it never traded at a price, at its cost basis. "Trade" is what D6 lets
price a day (`fn_leg_prices_a_day`: an execution, never a share movement), ordered by
D5's last-trade-of-the-day rule extended across days (`posted_at`, then `seq`,
`abs(amount)`, leg id), at the price D2 would write (`round(unit_price, 4)`); hidden,
merged and template headers are not trades in the ledger. The rule lives once, in
`fn_last_trade_price(ledger, security, as_of)`, back-adjusted onto the valued instant's
split basis like any observed price. Before 259 the valuations disagreed three ways:
every current value (the dashboard, the sidebar, the portfolio card, MCP, the
unbacked-price advisory) carried such a position at cost basis; the net-worth history's
feeder (`holdings_market_value_as_of_set`, so also returns, allocation and
`holdings_snapshot`) used the last `unit_price` of ANY leg on the same
(account, security), share movements included, so an in-kind transfer priced a position
at its carried basis, and valued one with no such leg at **zero**. Now the current values
read the last trade's price through `security_prices_as_of_now`, and the feeder's tier 2
is the same function and its tier 3 the position's FIFO cost basis at the instant
(`priced_from` stays `none`, so `unpricedSecurityCount` still flags it). The securities
catalog's and a security's "latest price" show that same price, dated by the trade.
Consequence: the history no longer understates a never-priced holding, and a current
value and the history's last point agree for every position, priced or not. Pinned by
`CurrentBalanceAsOfNowTests.An_unpriced_position_is_valued_at_the_securitys_last_trade_price_today_and_in_history`
and `A_position_that_never_traded_at_a_price_is_carried_at_cost_basis_today_and_in_history`,
the latter also with the share movement carrying a unit price, which the old tier 2 would
have priced the position from.
- **Amended 2026-11-04: a security's current figures name their currency.** Nothing
converts (FU-106), so each is printed in its own. One derivation, `SecurityCurrencies`,
serves the catalog, the security's detail and the unbacked-price advisory. The current
price (`security_prices_as_of_now`), and the market value it makes, are in the price's
currency: the close the price was read from (a trade-derived row is in the currency of
the brokerage that made the trade, the D2 amendment above); for a last trade's price,
the security's newest price row's; with no price row, the holding accounts', else the
ledger's. `GET
/securities` rows carry it as `priceCurrencyCode`, so the catalog's price and amount are
in the detail's currency, row by row. The advisory's holding value is at the current
price when the security has one, so in the price's currency, and else at the positions'
cost basis, in the holding accounts' currency; each advisory row carries `currencyCode`
and `mixedCurrency`, true when that cost basis sums across currencies, and the settings
page then prints no figure for it. A security nobody holds is worth 0, named in its
price's currency when it has a current price and else in the ledger's. The derivation
asks neither as-of-now function again: each reader reads `holdings_as_of_now` and
`security_prices_as_of_now` once, takes its figures and their currencies from those
rows, and looks up a newest price row only for a security no close named.
`TradePriceCheckCostTests` times the catalog and the consistency report, the advisory in
it, on the long-history ledger; pinned by
`CurrentBalanceAsOfNowTests.The_unbacked_price_advisory_names_the_currency_its_holding_value_is_in`
and `An_unpriced_advisory_holding_is_named_in_its_holders_currency_and_says_mixed_across_two`.
  *(Amended 2026-11-13, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7 and D9: a figure that combines holding accounts
  converts. The detail's total cost basis is in the ledger's reporting currency
  (`costBasisCurrencyCode`), each lot converted at its acquisition date's rate (migration
  282), and the advisory's holding value is in the reporting currency at the latest rate;
  neither carries a mixed flag, and each is null with `rate_unavailable` when a rate is
  missing. The detail's market value and unrealized gain sum the same holdings, so they
  are in the reporting currency too (`marketValueCurrencyCode`,
  `unrealizedGainCurrencyCode`), sent by the server rather than derived by the page: each
  position from the price's currency into its brokerage's, then into the reporting
  currency, both at the latest rate, rounded once. A price, and a position's value at it,
  are still in the price's currency, which every valuation now returns beside it
  (migration 281).)*
- **The seed rule now exists in three writers** — the D5 backfill, the D2 interceptor's `security_price_upsert_from_trade`, and the importer's `TradePriceSeedStep` — each a hand-written copy of the same eligibility predicate and last-trade-by-`seq` ordering. D5 says the importer "runs the identical seed"; nothing enforced that, so the three were free to stop being identical without anything saying so, and a divergence would show up only as wrong valuation, allocation and returns figures. **Migration 232's `fn_trade_price_check` is the reader that notices**, wired into the ledger consistency report as a fifth projection with its own repair. It deliberately re-states the rule rather than calling a writer: a checker that reuses the writer's code cannot catch the writer being wrong — what it can catch is the writers ceasing to agree. The check is a read; repairing is a separate deliberate act, through the D2 upsert so the rank gate still protects a truer `fetch`/`manual` close.
