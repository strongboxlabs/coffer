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

## Consequences

- Held securities get a real price observation at every trade, so the as-of feeder resolves `feed` instead of a stale/absent tier-2 — net-worth history and returns stop valuing traded-but-unfed holdings at 0.
- It does **not** invent prices *between* trades: a security with no feed and no recent trade is still marked stale (valued at its last trade). Accurate inter-trade history for such tickers is a separate concern (quote-provider historical backfill) — out of scope here.
- Deferred follow-up "MD-parity: native trades populate `security_prices`" is delivered by this ADR.
