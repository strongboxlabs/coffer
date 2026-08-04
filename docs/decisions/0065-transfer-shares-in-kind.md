# 0065 — Transfer-shares (in-kind) action

* Status: Accepted — shipped v0.7.0 (mig 151/152). Engine + editor + D4 guided
  scrub all landed; the scrub ships as the read-only `find_in_kind_transfer_candidates`
  MCP detection tool + the `POST .../in-kind-transfers/convert` apply endpoint (no
  dedicated SPA page — chosen scope).
* Date: 2026-06-26
* Related: ADR-0027 (action catalog), ADR-0029 (action × field matrix), ADR-0064
  (FIFO lots — this carries them), ADR-0019/0025 (posting model)

## Context

No action moves *shares* between accounts: `transfer` moves cash; `buyx`/`sellx`
are cash-transfer-*funded* buy/sells that still realize a gain. So an in-kind
transfer / ACATS / account rollover (the same shares move X → Y) can only be
recorded as sell-in-X + buy-in-Y, which **fabricates a realized gain in X and
resets the destination basis to transfer-date market** instead of carrying the
original lots. Confirmed in real data (FIFO surfaced it, ADR-0064): same security,
same date, sold in one account + bought in another, net-zero quantity.

## Decisions

### D1 — New `transfer_shares` action; zero realized gain; basis carries
Add `transfer_shares` to the `txn_headers.action` CHECK. A transfer posts share
legs spanning two holdings accounts — source holdings leg(s) (−qty) and
destination holdings leg(s) (+qty), **no cash legs** (in-kind: no money moves).
It is **not** a disposition: the source realizes **no** gain (no `realized_gains`
row), and the destination inherits the source's cost basis.

### D2 — Per-lot carry (preserve acquisition dates), modelled as N symmetric pairs
The destination inherits **each moved source lot's `acquired_at` + `unit_cost`**
(not an aggregate single lot) — so future FIFO and short/long-term holding
periods stay correct.

**Mechanism (refined during build):** at create/scrub time the source's FIFO
lots to move are computed, then the transfer is posted as **one posting per moved
lot** — posting *i* = (source leg −lotᵢ.qty / −lotᵢ.cost, destination leg
+lotᵢ.qty / +lotᵢ.cost), both `posting_role='security'`, no cash side. The
destination lot rows are created **one-per-moved-lot**, each bound to its own
destination leg with the inherited `acquired_at`, `unit_cost` = lotᵢ.unit_cost,
qty = lotᵢ.qty.

The win of one-leg-per-lot: each destination lot is **leg-derived 1:1 exactly
like a normal buy** (leg amount = lot cost, leg qty = lot qty ⇒ the existing
recompute lot-reset re-derives `unit_cost = amount/qty` correctly), so the
recompute's reset and split handling need **no** transfer-specific code. The only
thing that differs from a buy is the lot's `acquired_at` (original, not the
posting date) and the absence of a cash leg.

### D3 — Recompute handling (3 minimal, surgical edits)
`recompute_holdings_cost_basis` (ADR-0064) needs only:
- **Thread `action` into the event stream** (the `txn_legs ⋈ live_txn_headers`
  walk now also selects `hd.action`).
- **Source (−qty leg):** consume lots FIFO and reduce basis by the consumed cost
  as usual, but **skip the `realized_gains` INSERT** when the event's action is
  `transfer_shares` (it's a transfer, not a sale). Basis still leaves the source.
- **Lot-availability gate:** the FIFO consume loop now only consumes lots whose
  **creating leg's header `posted_at` ≤ the sell event's time**. A transfer-in lot
  carries an *earlier* `acquired_at` (original) than its *arrival* (the transfer
  date); without this gate a destination sell dated **before** the transfer-in
  could wrongly consume a not-yet-arrived inherited lot. The gate is also a strict
  correctness improvement for native lots (a sell can never consume a lot bought
  *after* it — previously masked because valid data never needed future lots).

The **destination (+qty legs)** need no special branch: each is an ordinary buy-
shaped event (basis += leg amount = Σ inherited lot cost; qty += leg qty), and the
1:1 leg↔lot reset preserves the inherited unit cost.

**Known constraint:** per-lot carry is frozen at posting/scrub time (D2). Editing
*pre-transfer* source history later won't retroactively re-split an existing
transfer's destination lots — consistent with "computed at create/scrub time."

This is the riskiest part of the slice (cross-account lot carry + recompute
change) and gets the same brokerage-statement validation gate FIFO got.

### D4 — Editor + guided scrub
The investment editor gains a "Transfer shares" action (source acct → dest acct,
security, qty/all). Existing in-kind rollovers mis-modeled as sell+buy are
reclassified via a **guided, reviewable** tool (detect the net-zero
same-security/same-date cross-account pairs; the user reviews + applies after
checking against statements) — **not** a blind auto-migration of real data.

**Shipped shape (chosen scope).** Detection is the **read-only MCP tool**
`find_in_kind_transfer_candidates` (same-security / same-calendar-date /
equal-quantity / distinct-investment-account disposal+acquisition pairs, with
`sourceHadFee`/`destHadFee` flags for a fee the transfer would drop). Apply is the
authenticated **`POST /api/ledgers/{ledgerId}/in-kind-transfers/convert`** endpoint
(`{sellHeaderId, buyHeaderId}`): in one transaction it deletes both headers and
creates the `transfer_shares` (reusing the engine; the delete flush restores the
source lots so the FIFO plan is correct). **No dedicated SPA page** — the user
reviews conversationally via MCP and converts per-pair. Overlapping same-key
matches are all listed; converting one removes its headers from re-detection.

## Consequences

- Correct in-kind transfers/rollovers: no fabricated gains, basis + holding
  periods preserved.
- Recompute gains transfer-aware lot handling; the editor computes the FIFO split
  at posting time. Both validated against statements before deploy.
