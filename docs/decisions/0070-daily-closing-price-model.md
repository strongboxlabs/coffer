# 0070 — Daily closing-price model (one price per security per day)

* Status: Accepted (0.12.0)
* Date: 2026-06-30
* Related: ADR-0054 (market-data quotes — the `fetch` provider + scheduled refresh this
  reshapes), ADR-0031/0033 (SimpleFIN ingest — the holdings price pull), ADR-0064 (FIFO
  cost basis — a consumer of these prices), ADR-0019/0028 (holdings valuation /
  portfolio view — the read side)

## Context

`security_prices` was **timestamp-keyed**: `price_date TIMESTAMPTZ`, `UNIQUE (security_id,
price_date)` on the exact timestamp. So multiple rows per calendar day were allowed, and
in practice happened:

- **Yahoo** (scheduled refresh) writes the EOD close at **midnight UTC** — one clean row/day.
- **SimpleFIN** holdings writes the brokerage's **`balance-date`** (Unix *seconds* — an
  intraday time, `NOW()` if absent), tagged **`fetch`, the same source as Yahoo**.
- **Importer** writes the MD snapshot (millisecond or day precision), tagged `import`.
- **Reads** take the latest timestamp.

Net: one security could hold several rows for a single day (a Yahoo close *and* a SimpleFIN
intraday balance *and* repeat syncs), and because reads take the latest timestamp, a
mid-day SimpleFIN sync could **shadow the real close**. We want exactly **one row per
security per day = that day's closing price**.

## Decisions

### D1 — One row per (security, day)
`price_date` becomes a **`DATE`**; the existing `UNIQUE (security_id, price_date)` then
means one-per-day. The `high`/`low`/`volume` columns already imply daily bars, so the
type now matches the intent. The EF entity follows suit — `PriceDate` is a **`DateOnly`**,
not a `DateTime` mapped onto a date column (see D7).

### D2 — Source-priority ladder
```
manual (2)  ==  Yahoo/fetch (2)   >   simplefin (1)   >   import (0)
```
Rule for **every** writer: insert when the day is empty; on a same-day conflict,
**overwrite iff `rank(incoming) >= rank(existing)`**. This generalizes ADR-0054's "a
fetched quote never clobbers a manual/import price."

- **`manual == Yahoo`** (mutual last-write-wins): manual prices are **gap-fills** — they
  hold while Yahoo lacks coverage and are cleanly superseded if Yahoo later covers the
  security (equal rank → overwrite). The delete-price action is the release valve.
- **`import` is the floor** (the one-time MD seed): it only ever refreshes another
  `import` row, never a live/manual price.

### D3 — SimpleFIN is its own source, ranked below Yahoo
Add **`simplefin`** to the `source` CHECK. SimpleFIN's `balance-date` is truncated to its
**calendar day**; it fills a day no higher source owns (a true fallback), and a Yahoo EOD
close wins the day when both exist. (Its price is an intraday balance, not a true close —
hence below Yahoo.)

### D4 — The ladder lives in one runtime place
The runtime ladder is **`PriceSource.Rank(source)` in C#**, used by the API writers
(`QuoteOrchestrator`, the manual add-price). The importer (always the floor) needs no
rank function — its upsert only updates an existing `import` row (`… DO UPDATE … WHERE
existing.source = 'import'`), which *is* the ladder for an `import` write. The migration's
one-time dedup ranks inline. A test asserts the importer floor-rule and the C# ladder
agree with this ADR.

### D5 — Migration (154, applied via DbUp / API restart only)
1. **Dedup** existing rows to one per `(security, day)` by the ladder — within `fetch`,
   the **midnight (Yahoo) row beats an intraday (SimpleFIN) row**, then latest wins.
2. `ALTER price_date → DATE` (`USING (price_date AT TIME ZONE 'UTC')::date` — UTC-anchored,
   so a midnight-UTC timestamp can't shift to the previous day under the server tz); the
   unique index rebuilds per-day.
3. Add `simplefin` to the source CHECK.

History stays tagged `fetch` (harmless — Yahoo still wins a future rank tie, and SimpleFIN
never outranks it).

### D6 — Manual add-price replaces its day
Because `manual` tops the ladder, the manual add-price endpoint now **upserts** (replaces
the day's price) instead of hard-rejecting on a same-day conflict. Reads are unchanged —
"latest by `price_date`" is now the single daily row.

### D7 — `DateOnly` end-to-end (no DateTime-over-DATE mapping)
A `DATE` column modeled as a C# `DateTime` is a type lie: the write tags `Kind=Utc` while
Npgsql reads the column back as `Kind=Unspecified` (asymmetric round-trip), and every
comparison (`PriceDate <= asOf`, history range bounds) silently drops a time-of-day the
type implies but the column can't hold. So `PriceDate` is a **`DateOnly`** at every
layer — entity, repositories, the `PricePoint` / price-row / latest-as-of / holdings DTOs,
and the add/patch request bodies. Npgsql maps `DateOnly ↔ date` natively and symmetrically;
System.Text.Json serializes it as `"YYYY-MM-DD"`. Instant→day projections become
**explicit** at the seams that genuinely start from a timestamp —
`DateOnly.FromDateTime(q.PriceAsOfUtc)` in the orchestrator, `DateOnly.FromDateTime(asOf)`
in the as-of valuation — instead of an invisible truncation. `QuoteEntry.PriceAsOfUtc`
stays a `DateTime` (a provider legitimately reports an instant, e.g. SimpleFIN's
balance-date); the collapse to a calendar day happens once, at persist.

### D8 — `price` precision: NUMERIC(19,4), matching high/low
`security_prices.price` was `NUMERIC(25,12)` while its own `high` / `low` were
`NUMERIC(19,4)`. That 12dp scale let single-precision **float noise** be stored — `7.15`
written as `7.150000095367`, `10.35` as `10.350009934741` — in historical `fetch`
(pre-split SimpleFIN) and `import` (MD seed) rows; the true values are clean, the tail is
representation noise from a producer that went through a 32-bit float. Migration 155
constrains `price` to **`NUMERIC(19,4)`** (matching high/low): the `numeric->numeric` cast
rounds every existing value to 4dp (scrubbing the noise) and the DB now **enforces** 4dp,
so no writer can reintroduce it. 4dp is ample for valuation — the high/low band already
says so; the producer-side rounds (importer `PriceSnapshotMapper`, the SimpleFIN provider)
stay as belt-and-suspenders.

This is the **valuation** price only. The **trade** price `txn_legs.unit_price` stays
`NUMERIC(25,12)` — a per-share execution price (`amount / shares`) legitimately needs >4dp
— and is a different column, untouched. The Security-detail prices table also surfaces
`security_prices.source` (SimpleFIN / Market data / Manual / Imported) so each row's origin
is visible.

## Consequences

The existing ~50 same-day extras collapse to one-per-day. Securities Yahoo doesn't cover
keep a SimpleFIN fallback price. SPA: the add/patch price dialog now sends the bare
`YYYY-MM-DD` (a `DateOnly` bind — the old `T00:00:00Z` wrapper is gone); display is
unchanged, since `formatLedgerDate` is already UTC-anchored for calendar-date fields
(including `price_as_of`).
