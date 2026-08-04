# 0039 — `recompute_holdings_cost_basis`: quantity is a pure running sum

* Status: Accepted
* Date: 2026-06-09
* Related: ADR-0032 (triggers as last resort), migrations 067 / 068 / 104,
  follow-up to the OFX investment-prefill slice

## Context

`recompute_holdings_cost_basis` (mig 067 / 068) walks a unified event
stream (`txn_legs ∪ security_splits`) per holding in chronological
order and produces the authoritative `holdings.quantity` +
`holdings.cost_basis`. The function had two guards on the sell
branch that enforced "you can't own negative shares":

1. The sell branch only fired when `running_qty > 0`. Sells
   encountered with `running_qty = 0` were silently dropped.
2. After subtracting the sell's quantity, `running_qty` was clamped
   to 0 if it would have gone negative; `running_basis` was also
   zeroed.

Both backfire on legitimate histories whose intra-day ordering
cannot be faithfully recovered. MD's JSON has no intra-day
timestamp on `dt`; the importer lands events in MD's display order
(usually sorted by amount), which is not the broker's true sequence.
OFX likewise stamps `posted_at` to midnight UTC. Within a single
date, the recompute orders by `(event_at, leg_id)` — leg_id is
effectively arbitrary.

The clamp violated an invariant the user reasonably expected:

> For a history of buys / sells / reinvests (no splits), the final
> share count equals `SUM(quantity deltas)`. That sum is invariant
> under permutation.

Real-data evidence: on the user's ledger, **PTRQX** has a lifetime
quantity SUM of exactly 0 — fully bought and fully sold. The
recompute produced 0.345 shares. The 0.345 arose entirely from
intra-day permutations of 2026-04-15's 15 mixed events: when
ordering placed a transient sell against `running_qty = 0`, the
sell was dropped; later reinvests added 0.345 against the
clamped-to-zero base.

## Decision

Drop the clamp. Make `running_qty` a pure running sum.

### Quantity rule

Every event adds its signed quantity to `running_qty`:

* Buy / Reinvest: `running_qty += event.quantity` (positive)
* Sell: `running_qty += event.quantity` (negative; no guard, no clamp)

End-of-walk `running_qty = SUM(all event quantities)`, invariant
under permutation. If the lifetime SUM is consistent with the
broker (zero when fully sold, positive when shares are held), the
recompute converges to it regardless of intra-day order.

### Cost basis rule

Best-effort avg-cost, gated on positive inventory:

* Buy / Reinvest: `running_basis += event.amount + fee_if_applicable`
* Sell:
  * If `running_qty > 0` immediately before the sell:
    `running_basis -= avg_cost * min(running_qty, |sell_qty|)`.
    The `min(...)` clamp ensures we only consume basis up to what
    we have; the leftover sell quantity (if `|sell_qty| > running_qty`)
    has no basis to consume against.
  * If `running_qty <= 0`: skip basis reduction. There is no
    inventory to value the disposition against; any imputation
    would be arbitrary.

Clean-data histories (lifetime quantity SUM = 0 with matched buys
and sells) still produce `cost_basis = 0` at end-of-walk: at the
moment `running_qty` reaches exactly 0, the avg-cost reduction
consumes the entire remaining basis.

### Lot consumption

Unchanged. FIFO over open lots; over-sells past the open-lot
supply silently don't consume from non-existent lots. Same as
before.

## Consequences

### Positive

* **End-of-walk quantity = SUM(quantity)**. Order-independent.
  Eliminates an entire class of bugs (intra-day permutation
  sensitivity).
* **No data-collection pressure on intra-day ordering**. Neither
  MD nor OFX gives us reliable broker-true intra-day sequence;
  the algorithm no longer requires it.
* **PTRQX converges to 0**. Without any other change to data or
  imports.
* **Drift caused by past clamping in already-walked holdings is
  repaired** by the migration's one-shot full recompute call.

### Negative

* **Phantom over-sells now produce negative `holdings.quantity`**.
  A history with more sell-side than buy-side shares (data is
  internally inconsistent) will end with a negative quantity
  instead of being silently clamped to 0. This is arguably the
  RIGHT behavior — surfacing the inconsistency loudly beats
  hiding it — but it represents a contract change for downstream
  readers. The Portfolio View
  ([HoldingsRepository.cs:94](../../src/Api/Db/Repositories/HoldingsRepository.cs#L94))
  already filters `Quantity != 0`, so a negative-qty row would
  surface in the table. Acceptable: if the user sees a negative
  position, they have a data audit to do.
* **Cost basis on intra-day-mis-ordered data can differ from the
  pre-mig-116 value** by small amounts. The avg-cost reduction
  cascade depends on the order in which buys / sells / reinvests
  fire. For internally consistent histories ending at qty = 0, the
  basis still ends at 0. For histories that end with held shares,
  the basis may be a few cents different from what the previous
  clamping behavior would have produced. This is a known limit of
  avg-cost under uncertain ordering; no fix without a
  broker-provided intra-day timestamp (which we don't have).

## Alternatives considered

* **Add `entered_at` from MD's `dtentered` to txn_headers; order by
  it.** Investigated and rejected. `dtentered` is "when the
  user/sync entered the txn in MD," which for sync-fed events
  reflects MD's ingest order, not the broker's intra-day order.
  Adding it provides a stable tiebreaker but not a correct one.
  Worse, it deepens the algorithm's order-dependence rather than
  removing it, leaving the same clamp-induced fragility on any
  history that arrives in a different order than the broker
  intended.

* **Treat same-`posted_at` events as a single net delta.** Cleaner
  than `entered_at` (eliminates the ordering ambiguity entirely),
  but materially changes the lot-creation contract — every event
  on the same date would have to share a lot, breaking lot-level
  cost basis tracking for downstream reporting. Quantity-correct,
  basis-coarsening. Rejected in favour of the simpler
  no-clamp variant which keeps lots intact.

* **Keep the clamp, surface inconsistency as a validation error.**
  Would catch the bug but forces the user to manually reconcile
  every legacy history before the algorithm produces any output.
  Rejected: the algorithm should produce a sane answer for the
  data it has, not refuse to run.

## Migration

Single forward-only migration (116):

1. `CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(...)` —
   same signature, new body (no quantity clamp; basis reduction
   gated on positive inventory).
2. `SELECT recompute_holdings_cost_basis();` — one-shot full-ledger
   repair. Walks every existing holding row under the new contract
   so stored quantity + cost_basis converge.

No schema changes. No data migrations. No C# changes.
