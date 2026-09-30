# 0084 — Trade-derived security prices

* Status: Accepted
* Date: 2026-07-25
* Extends: [ADR-0070](0070-daily-closing-price-model.md) (daily closing-price model + source ladder)
* Related: [ADR-0054](0054-market-data-quote-provider.md) (fetch provider), [ADR-0032](0032-triggers-as-last-resort.md) (recompute at call sites / interceptors, not triggers), [ADR-0063](0063-mcp-server.md) (net-worth-history / TWR read the priced holdings)

## Context

A security transaction records an **execution price** (`txn_legs.unit_price` = |cash| / |shares|), but **no code path writes that price into `security_prices`** — the source ladder was `import` / `fetch` / `manual` / `simplefin` only, and neither the native API investment write nor MCP writes touch `security_prices`. Consequence: a security that is *held but not fed* (a dormant 401(k), any ticker the quote provider doesn't cover) has price history only from the one-time `import` seed — so the mig-172 as-of valuation feeder falls back to the last trade price (its tier-2), and if even that is absent it values the holding at **0**. Net-worth history and returns then understate those holdings for as long as the gap persists (observed: a ~$1.4M rollover "jump" that was really net worth catching up to a long-standing pricing gap).

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

### D3 — `price_date` is the UTC calendar day of `posted_at`
`price_date` is a `DATE` keyed in **UTC** (ADR-0070 D5: mig-154 converted with `AT TIME ZONE 'UTC'::date`; Yahoo close sits at midnight UTC). A transaction's date is stored as **midnight UTC** of that calendar day (importer `ParseMdDate` → `DateTimeOffset(…, TimeSpan.Zero)`; native create stores the request date as a UTC `timestamptz`). So `price_date(trade) = (posted_at AT TIME ZONE 'UTC')::date` — a trade and a same-day Yahoo close share one day-row, so the rank gate lets the feed overwrite it. In C# the interceptor normalizes `posted_at` to UTC before `DateOnly.FromDateTime` (dodging the ADR-0070 D7 `Kind` asymmetry); the SQL backfill uses the identical `AT TIME ZONE 'UTC'` expression.

### D4 — Edit re-upserts; delete does not retract
Editing a trade re-upserts the (possibly new) day at `trade` rank. **A delete does not retract** the price row — a past execution was a real observation, and the row is harmless (a feed close or a later write supersedes it by rank). This keeps the interceptor to Added/Modified legs only.

### D5 — One-time backfill from existing trades (migration 177)
Derive `trade` rows for all historical investment trade legs (`security_id` set, `quantity <> 0`, `unit_price > 0`), one per `(security, UTC-day)` taking the last trade of the day, rank-gated so it overwrites only `import`/`simplefin`/`trade` rows (never a `fetch`/`manual` price). This covers imported history (the T. Rowe funds) too. On a trade day this **replaces the MD `import` snapshot** with the execution price (the truer observation), by design.

The migration backfill is one-time — it only covers ledgers that existed at deploy. So the **Moneydance importer** (Dapper, which bypasses the D2 EF interceptor) runs the identical seed at end-of-import via a pipeline step, `TradePriceSeedStep` — the per-ledger analogue of the backfill, scoped to the freshly-imported ledger and ordered right after `PriceSnapshotImportStep` so the `csnap` `import` prices exist first and the trade prices upsert over them on trade days. This is the same call-site-recompute contract the importer already uses for balances (`BalanceRecomputeStep`) and holdings; a FUTURE import is thereby covered too, not just pre-existing ledgers.

### D6 — A share movement is not a price (migrations 235, 236, 2026-09-28)

D5 assumed transfers were already excluded because "transfer_shares carries pamt = 0 -> unit_price 0/NULL writes no price". That covers the zero case only. Moneydance records a `ShrsIn`/`ShrsOut` — a movement with no cash consideration — with a NOMINAL amount equal to the share count (`1.pamt = 50631` against `1.samt = -5063090`, i.e. $506.31 for 506.309 shares), and the importer's `unit_price = amount / quantity` turns that into ~$1.00, which passed `unit_price > 0` and became a `trade` price on funds worth hundreds.

**The test is the ACTION, full stop** (corrected by migration 237 — 235 shipped a narrower rule and it was wrong twice over). 235 excluded such a leg only when its amount still equalled the share count, reasoning that 503 `ShrsIn`/`ShrsOut` legs carrying a "real" figure were real observations. Running the check against production disproved both halves:

* *The arithmetic was off by a cent.* Quicken truncated where 235 assumed rounding — a leg of 7.275 shares carries an amount of 7.27, while `round(7.275, 2)` is 7.28 — so the equality never matched and the placeholder priced its day at $0.9993 regardless.
* *A "real" figure on a share movement is cost basis.* An in-kind move carries each lot across at its original purchase price, so one security on one day arrives with as many unit_prices as there were lots — 53 of them in one observed case, spanning $0.99 to $12.06 — and they sort AFTER the day's genuine trades by `seq`, displacing them. Six production findings were exactly this: both the stored and the expected figure were lot basis, and neither was a market price.

Nothing in the row separates basis from price. The action does, so the action is the whole rule and `fn_leg_prices_a_day` has no arithmetic.

**And the rule is an allow-list, not an exclusion list** (migration 238). 237 was still the third exclusion added one incident at a time — the first two keyed on the Moneydance spelling and could not see Coffer's own `transfer_shares`, which `convert_in_kind_transfer` creates natively with no provider payload and legs priced at each lot's carried basis. An exclusion list FAILS OPEN: an action nobody enumerated prices days by default, so the next unconsidered one is the next incident, which is precisely the sequence that produced 235, 237 and 238.

Enumerating every `(action, provider_action)` pair carrying a priced security leg on a real ledger gives 18, and a clean split. Executions: `buy`, `buyx`, `sell`, `sellx`, `dividend_reinvest` (the last at NAV, across all five Reinv* provider spellings). Not executions: `buyx/ShrsIn` (443 legs), `sellx/ShrsOut` (52), `sell/ShrsOut` (8), and `transfer_shares` in any form. Note `buyx` appears on both sides — it is the mapping target for a real `BuyX` and for `ShrsIn` alike — so neither field decides alone and both are arguments.

Only those actions price a day. Anything else, including an action added later, prices nothing until it is named. A missing price surfaces as a stale valuation; a wrong one silently mis-values a holding and the checker cannot see it, because the checker shares the rule. A money-market fund genuinely worth a dollar is unaffected, because a sweep purchase is a `Buy`.

Cost, measured: of 6,540 days priced by a trade, 6,415 keep a genuine one; 125 lose their trade price, and on every one of those no trade occurred. **Trade-off:** correcting a `ShrsOut` by hand no longer seeds a price either. Prices already written that way survive, but a future correction records the transaction without a price — which is right, since the corrected figure is a valuation rather than an execution, and the price CRUD exists for asserting one deliberately.

Measured across the whole ledger: of 450 legs matching the arithmetic, every one on a security that trades above $5 is a `ShrsIn` or `ShrsOut`, every one carries a `qif_sn` marker, and they run 1995–2014. The other 380 — including all 327 dated after 2012 — are money-market and stable-value funds legitimately at $1.00. **The damage is a closed historical artifact of one Quicken-to-Moneydance migration and cannot grow**, which is why migration 236 deletes the 26 already-written rows outright rather than adding a reason code and repair verb for a set that will never gain a member.

**A price nothing derives is ADVISORY, not a finding** (migrations 240, 241 — superseding 239's `orphaned` reason). 239 reported them inside the consistency check, which on a real ledger read "130 of 6540 no longer backed by a trade" beside a Repair button that would not have touched one of them. The framing was wrong before the wording was: `security_prices` records THAT a trade wrote a row and never WHICH one, so "nothing derives this" can only be inferred, and cannot distinguish a deleted trade (D4 keeps its price on purpose) from a rule change from an edit. Nor is it a backlog to drain — deleting or editing any priced trade creates one, so the category is permanently non-empty by design.

So the check reports only `missing` and `value`, both repairable, and unbacked prices become a separate advisory: a list of SECURITIES, because that is the unit someone acts on, ordered by what the holding is worth now so what still carries money leads and a sold position sorts last rather than vanishing. Acting clears it either way — editing a price sets `source = 'manual'`, deleting removes the row. The security's price list strikes through the source word on such a row, and only that word: a struck-through row would read as deleted, and these are not.

Shape matters here. The per-row predicate `fn_price_is_unbacked` rescans a security's legs for every price: measured at **728ms against 14ms** over a 1,229-price history, so `fn_unbacked_price_dates` (241) answers it in one pass and both the paginated list and `price_history` use that. MCP's `price_point` also gained `source` — its absence meant an agent could not tell a typed figure from a quote-feed close while `update_price` and `delete_price` let it act on either.

**Orphans were reported, not guessed at** (migration 239, now superseded). 236, 237 and 238 each ended with a DELETE whose predicate — `price BETWEEN 0.9 AND 1.1`, security trades above $5 — was calibrated on two funds in one ledger. Measured against that same ledger it reaches **1 of 130** orphaned trade prices; on an install whose placeholder figures are not near a dollar it does nothing, while 238 simultaneously stops the check examining those days. Shipping thresholds derived from one dataset as product behaviour was the error, not the specific numbers.

The general statement needs none: a `trade`-source price on a day the rule derives nothing for is not backed by anything. `fn_trade_price_check` now returns those with `reason = 'orphaned'`, `account_id` and `header_id` NULL, and `expected_price` equal to the stored price because there is nothing to derive. The repair does **not** act on them — D4 keeps the price a since-deleted trade seeded, and no column distinguishes that from a basis figure an in-kind transfer left behind — so the row links to the security's price list and a person decides. No more DELETEs in migrations.

**The checker's blind spot is accepted, not fixed.** `fn_trade_price_check` re-states the rule rather than calling the writer, so it catches the writers diverging — and by construction cannot catch the RULE being wrong, because both sides then compute the same wrong answer. That is exactly how this survived: the report called the ledger healthy while 28 dollar prices sat in it. A plausibility detector (a `trade` price near $1 on a security whose own history reaches far above it) would catch the class directly, and is deliberately NOT built: the source is closed by this decision, the historical damage is deleted, and a heuristic threshold on volatile securities would reintroduce the false alarms that made the check untrustworthy in the first place. If a future writer invents prices some other way, this is the hole it will come through.

## Consequences

- Held securities get a real price observation at every trade, so the as-of feeder resolves `feed` instead of a stale/absent tier-2 — net-worth history and returns stop valuing traded-but-unfed holdings at 0.
- It does **not** invent prices *between* trades: a security with no feed and no recent trade is still marked stale (valued at its last trade). Accurate inter-trade history for such tickers is a separate concern (quote-provider historical backfill) — out of scope here.
- Deferred follow-up "MD-parity: native trades populate `security_prices`" is delivered by this ADR.
- **Superseded in part by migration 234 (2026-09-26).** D2 described the interceptor
as writing the execution price of the leg it saw. That was implemented literally, and
it meant the WRITER never applied D5's "last trade of the day" rule: correcting one leg
of a multi-leg day seeded that leg's price, while the consistency check derived the
day's by the rule, and the two disagreed by construction on a ledger that was fine. The
rule now lives once, in `fn_trade_price_for_day`, and the interceptor names the
(security, day) it touched rather than a price. The rule also gained a total ordering —
`h.seq DESC, abs(amount) DESC, l.id DESC` — because one header can carry several
security legs at different prices and `seq` alone ties, leaving the choice to whatever
plan Postgres picked that run.
- **The seed rule now exists in three writers** — the D5 backfill, the D2 interceptor's `security_price_upsert_from_trade`, and the importer's `TradePriceSeedStep` — each a hand-written copy of the same eligibility predicate and last-trade-by-`seq` ordering. D5 says the importer "runs the identical seed"; nothing enforced that, so the three were free to stop being identical without anything saying so, and a divergence would show up only as wrong valuation, allocation and returns figures. **Migration 232's `fn_trade_price_check` is the reader that notices**, wired into the ledger consistency report as a fifth projection with its own repair. It deliberately re-states the rule rather than calling a writer: a checker that reuses the writer's code cannot catch the writer being wrong — what it can catch is the writers ceasing to agree. The check is a read; repairing is a separate deliberate act, through the D2 upsert so the rank gate still protects a truer `fetch`/`manual` close.
