# 0041 — `recompute_holdings_cost_basis`: deterministic intra-day event order

* Status: Accepted
* Date: 2026-06-09
* Related: ADR-0039 (drop the quantity clamp; mig 116),
  ADR-0040 (exclude hidden headers; mig 117), migration 060
  (security splits), migration 067 (narrowable scope), migration 068
  (auto-create holding)

## Context

After mig 116 made quantity a pure running sum (order-independent
for final `holdings.quantity`) and mig 117 excluded soft-hidden
headers, the recompute had two remaining sort-order issues that
neither migration touched:

1. **Splits sorted after same-day legs.** The event-stream sort
   was `ORDER BY event_at, kind, leg_id` where `kind` is the
   literal text `'leg'` or `'split'`. Alphabetical: `'leg' < 'split'`.
   On a date that has BOTH a stock split AND same-day buy/sell
   activity, the split's ratio is applied AFTER the legs land —
   contradicting the conceptual model (a split adjusts the
   *pre-existing* holdings; same-day activity should be in
   post-split units).
2. **Same-day legs sorted by `leg_id`.** Within a single
   `posted_at`, leg events sort by leg_id — effectively random
   (we don't have a broker-true intra-day timestamp). Quantity
   is unaffected (mig 116), but cost basis under avg-cost still
   depends on order: a sell processed before its same-day funding
   buy/reinvest computes avg-cost against a smaller (or empty)
   inventory pool, producing a different basis trajectory than
   the reverse order.

Both are determinism gaps. Neither was a P0 quantity-correctness
bug after migs 116/117, but both make the basis number a function
of insertion order (`leg_id` is a UUID — effectively a coin flip),
which is the wrong kind of fragile.

## Decision

Sort events within a date by their natural causal class, then by
`leg_id` for full determinism:

| sort_class | events            | rationale                                          |
| ---------- | ----------------- | -------------------------------------------------- |
| 0          | splits            | adjust the pre-existing pool first                 |
| 1          | qty > 0 (buy / reinvest) | establish lots before they can be consumed |
| 2          | qty < 0 (sell)    | consume FIFO from lots established above           |

Encoded as:

```sql
ORDER BY event_at,
         sort_class,    -- 0 split, 1 buy/reinvest, 2 sell
         leg_id         -- final deterministic tiebreaker
```

Where `sort_class` is a `CASE` in the SELECT projection, set to
`0` on the split union arm and `1` / `2` on the leg arm based on
`l.quantity`.

## Consequences

### Positive

* **Lots exist before sells consume them.** No more avg-cost
  reductions against an empty pool followed by a same-day buy
  inflating the basis afterward.
* **Splits adjust pre-existing holdings first.** A same-day
  split + activity scenario walks correctly.
* **Determinism without a broker timestamp.** `leg_id` is still
  a UUID, but it's now only a tiebreaker within a single
  sort_class bucket — within a bucket the ordering doesn't affect
  the basis math (all buys add to the pool; all sells consume
  proportionally).
* **The mig 116 clamp removal becomes unreachable for the
  apparent-over-sell case** (a sell that's only "over" because
  it was ordered before its same-day funding buy). It stays as
  defence in depth for the TRUE over-sell case — a history that
  genuinely has more sell shares than buy shares (data error;
  surfaces as negative `holdings.quantity` rather than being
  silently clamped to zero per ADR-0039).

### Negative

* **Cost basis shifts for histories with same-day buy+sell
  interleaving.** For genuinely sell-first-then-rebuy-same-day
  patterns (tax-loss harvest with same-day repurchase, security
  swaps), our heuristic processes the buy first; the broker's
  true order was sell→buy. Quantity is unaffected. Avg-cost basis
  can differ by a small amount (few cents to a few dollars per
  disposition, typically). For tax-advantaged accounts
  (401(k), IRA) this doesn't matter (basis isn't tracked at the
  participant level for tax purposes). For taxable accounts it
  produces a different — but defensible and deterministic —
  basis trajectory than the previous arbitrary `leg_id` order.
* **The one-shot recompute will move basis numbers.** Quantities
  won't move (mig 116 already made those order-independent).
  Cost-basis values shift on holdings whose history has same-day
  interleaving. Worth surfacing to the user.

## Alternatives considered

* **Add `entered_at` from MD's `dtentered` to txn_headers and
  use it as the within-date sort.** Investigated in this session
  and rejected: `dtentered` is "when the user/sync entered the
  txn in MD," which for sync-fed events reflects MD's ingest
  order, not the broker's true intra-day order. Adds dependency
  on a per-provider field most providers don't carry, for no
  meaningful improvement.

* **Aggregate same-day events into a single net delta.** Cleaner
  for quantity (eliminates the ordering question entirely), but
  changes the lot-creation contract — every event on the same
  date would have to share a lot. Breaks lot-level FIFO
  reporting for downstream consumers (the Sell editor's
  per-lot consumption preview, ADR-0028's posting-role contract).
  Rejected in favour of preserving per-event lots while making
  their walk deterministic.

* **Keep the `leg_id` sort and accept basis fragility.** The
  status quo before this migration. Rejected on principle: a
  derived value should not depend on a UUID assignment order.

## Migration

Single forward-only migration (118):

1. `CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(...)` —
   same signature, new body with `sort_class` projection in the
   event SELECT and `ORDER BY event_at, sort_class, leg_id`.
2. `SELECT recompute_holdings_cost_basis();` — one-shot full-
   ledger repair.

No schema changes. No C# changes.
