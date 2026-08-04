# 0064 — FIFO cost basis (replacing average cost)

* Status: Accepted — landed with the MCP v2 slice, v0.6.0. **Amended by
  [ADR-0065](0065-transfer-shares-in-kind.md)** (v0.7.0, mig 152): the FIFO consume
  loop gained a lot-availability gate (consume only lots whose creating-leg header
  `posted_at` ≤ the disposal time) so an in-kind transfer-in lot — which carries an
  earlier `acquired_at` than its arrival — can't be consumed by an earlier sale; a
  `transfer_shares` disposal consumes lots + reduces basis but records no realized
  gain.
* Date: 2026-06-26
* Related: ADR-0027 (investment action catalog), ADR-0029 (action × field matrix),
  ADR-0039/0040/0041 (recompute ordering), ADR-0063 (MCP — the `realized_gains`
  tool that surfaced this), ADR-0065 (transfer-shares — amends this recompute)

## Context

Coffer displayed holding cost basis using **average cost** (the recompute
function's running `v_running_basis / v_running_qty`), while *also* maintaining
the `lots` table with **FIFO** consumption (mig 054, "for a future per-lot tax
surface"). The two diverge the moment a security is partially sold, so the
displayed basis (average) and the lots (FIFO) disagreed.

Building the MCP `realized_gains` tool (ADR-0063 v2) forced the question: a
realized-gain figure must reconcile with the basis shown everywhere else. The
owner's call: **be consistent — switch the whole app to FIFO.** FIFO is also the
tax-correct default for US individual equities (average cost isn't permitted for
stocks), and the FIFO lot machinery already exists — only the *derivation of the
holding's basis* was average-cost.

## Decisions

### D1 — Holding cost basis is FIFO (the open-lot cost)
A holding's `cost_basis` becomes the sum of its **open FIFO lots'** cost
(Σ remaining-qty × lot `unit_cost`), not the average-cost running basis. The
recompute function already consumes lots FIFO on each sell; we change the
*basis trajectory* to track that consumption instead of average cost. With no
sells, FIFO ≡ average cost, so buy-only histories are unchanged.

### D1a — Lot invariant: every acquiring leg has a lot row
FIFO basis is derived from the `lots` table, so **every buy-side holdings leg
must have a `lots` row** — this is a maintained invariant, not something the
recompute self-heals (a self-heal would race the create path, which already
inserts the lot in the same unit of work, and mask a real producer bug). All
producers uphold it: the API create path (`_db.Lots.Add` from the domain
`InvestmentHoldingsImpact.NewLot`), the API edit/PATCH path (drops + rebuilds
legs *and* lots), and the importer (`BuildHoldingsImpact` → `BulkReplaceLots`).
The recompute only *resets + consumes* lots; it never invents them. Tests seed
lots the same way production does (`InsertLotForHeader`), so they exercise the
real path rather than a parallel one.

### D2 — Realized gains are persisted (new `realized_gains` table)
The recompute walk already knows, at each sell, which FIFO lots it consumes and
their cost. It now records one `realized_gains` row per sell leg:
proceeds (the security-leg market amount, net of a sell-side fee when the
brokerage's `is_trade_commission` is set — mirroring how buys fold fees into
basis), cost basis consumed, and realized gain = proceeds − cost consumed.
Keyed by the sell leg; recompute deletes + repopulates rows in its (account,
security) scope each run, so it's always consistent with the lots. This is the
home for the `realized_gains` MCP tool and a future short-/long-term breakdown.

### D3 — One-shot full recompute backfill
The migration rewrites `recompute_holdings_cost_basis` and runs it ledger-wide,
so every holding's basis converges to FIFO and `realized_gains` is populated for
all history in one pass (the established mig-068/116/117/118 pattern).

### D4 — Validate against brokerage statements before release
Per the standing rule that investment numbers must reconcile with reality:
holdings basis + realized gains are checked against brokerage statements in dev
before the release. Sell-side fee treatment (D2) is the most uncertain detail and
is the focus of that check.

### D5 — Short-/long-term split (mig 169)
The D2 "future short-/long-term breakdown" lands: `realized_gains` gains
`proceeds_lt` / `cost_basis_sold_lt` / `realized_gain_lt` — the **long-term**
portion of each sale (short-term = total − LT, derived in the reporting layer, so
no redundant column pair). The recompute's sell branch already visits each consumed
lot with its `acquired_at` (preserved across splits, so the holding period runs
from the original purchase); it now buckets each consumed portion as long-term iff
`sold_at > acquired_at + 1 year` (US "more than one year"; exactly one year is
short-term), accumulating the LT cost and apportioning proceeds to LT by consumed-
share share (multiply-before-divide, so an exactly-divisible split stays exact). A
sale straddling the 1-year line splits across both buckets; a `transfer_shares`
disposal still records no row. `RealizedGainsAsync` + the `realized_gains` MCP tool
surface the split. Chosen over a per-lot `realized_gain_lots` detail table: the
breakdown on the existing row answers the ask without the extra schema, and the
FIFO engine stays the single source. The one-shot recompute backfills all history.

## Consequences

- Every holding with a partial-sale history gets a (correct, FIFO) basis that
  differs from the old average-cost figure; unrealized gains, the Overview, and
  the Portfolio View move accordingly. Buy-only holdings are unchanged.
- Investment tests that asserted average-cost basis values for sell histories are
  updated to the FIFO expectations.
- `realized_gains` becomes a first-class, queryable surface (MCP + future UI).
- Tax-method correctness for US equities; groundwork for lot-level tax reporting.
- **Surfaces (does not cause) a pre-existing modeling gap:** in-kind transfers /
  rollovers recorded as sell+buy now show a fabricated realized gain (and the
  destination basis was reset to transfer-date market). The fix is the
  transfer-shares action + a data scrub — its own slice (see docs/follow-ups.md).
  FIFO is correct given the recorded actions; it just made the issue visible.
