# 0053 — Self-referential `buysellxfr` dropped sell proceeds (unbalanced investment headers)

* Status: Accepted — mapper fix + validator hardening + scrub (mig 129) implemented 2026-06-17.
* Date: 2026-06-17
* Related: ADR-0019 (symmetric postings — every posting/header balances),
  ADR-0027 (investment action catalog), ADR-0034 (`created_at` immutable;
  balances via explicit recompute at call sites, **not** triggers — mig 102),
  ADR-0052 (the re-import / merge audit that surfaced this).

## Context

The ADR-0052 D4 audit found **imbalanced investment headers** on the Default
ledger: the legs of a header did not sum to zero (a double-entry violation,
ADR-0019). All were Moneydance `sellx` events on a **closed brokerage account**
(a share-class-exchange / annual-fee history spanning several years).

## Root cause (evidence-backed)

Moneydance records a share-class exchange (and a fee-funded sale) as a `sellx`
with `xfer_type = xfrtp_buysellxfr` whose transfer target is the **brokerage
itself** (a self-referential transfer). The verbatim MD JSON for one such row:
the `sec` split carries real cash (`pamt` = the sale proceeds) and a share
reduction (`samt` < 0); the `xfr` split points back at the same brokerage.

`InvestmentTransactionMapper`, on detecting the self-referential transfer, ran:

```csharp
if (isSelfRefXfr) { ...; secCash = 0m; }   // "MD nets the cash to zero"
```

It **zeroed the brokerage cash leg**, keeping only the share-out leg. But the
"net to zero" happens across the *paired* buy (the new share class) or fee leg,
which lives in a **separate header / split** — not within this one header. So
zeroing dropped the proceeds and left every self-ref `sellx`/`buyx` header
unbalanced by the full trade amount.

**Balance impact (this is not cosmetic).** Because each sale credited `0` cash
instead of the proceeds, the closed account's cash balance read **negative by
exactly the sum of the dropped proceeds**. The earlier "0 drift" audit could not
catch it: the balance walk and the stored running balance are both derived from
the same broken legs, so they agree with each other (internally consistent) while
being wrong against reality — the same blind spot as a merge over-collapse.

**The validator actively excused it.** `ImportValidator.CheckPostingBalanceAsync`
checked balance *per posting* and **excluded any posting containing a zero-amount
leg**, with a comment calling "SHARE CLASS EXCHANGE … the other leg has amount=0
by design" legitimate. So the importer's own sanity check was coded to treat the
bug's output as correct — which is why it survived.

## Decision

1. **Mapper fix (producer).** Remove the self-ref cash-zeroing. The `sec` pair
   books cash normally (`cash = proceeds`, `holdings = −proceeds`); the self-loop
   `xfr` leg is still skipped (it nets to zero on the same account). This balances
   both the fee case (proceeds in, fee out) and the exchange case (proceeds in,
   funding the separately-recorded new-class buy). Covered by mapper tests for the
   self-ref sellx **and** buyx directions.

2. **Validator hardening (call-site guard), no trigger.** Replace the
   per-posting-with-zero-leg-exemption check with an **unconditional per-header**
   balance check (`SUM(legs.amount) = 0` per header, no exemptions). This is the
   guard that would have caught the bug at import time. It lives at the importer
   call site **deliberately, not as a DB trigger**: ADR-0034 / migration 102 moved
   balance off triggers to call-site recompute, and a cross-row "legs sum to zero"
   invariant cannot be a plain `CHECK` (no aggregation) — it would *have* to be a
   trigger, which we are not adding. The API side builds balanced pairs by
   construction (`InvestmentPostings`), so there is no second writer to retrofit.

3. **Scrub (state), after the producer fix.** Migration 129 books the dropped
   proceeds onto the zeroed sec-pair cash leg of every affected header
   (`cash = −holdings`), recomputes the affected account balances, and `RAISE`s if
   any `sellx`/`buyx` header is still unbalanced. The closed account returns to a
   $0 cash balance — the dropped proceeds *were* the entire negative.

## Consequences

* Future imports (and Demo refreshes / any re-seed) produce balanced investment
  headers; the validator fails loudly on any that don't.
* The historical rows are corrected and the closed account reads correctly.
* No trigger added — the invariant is enforced at the producer, consistent with
  the project's call-site model for balance.
* The "0 drift" audit remains a consistency check, not a correctness check: it
  cannot detect a uniformly-wrong balance. Statement reconciliation (and now the
  per-header balance guard) is what catches these.
