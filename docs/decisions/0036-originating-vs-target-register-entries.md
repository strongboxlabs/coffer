# 0036 — Originating vs target register entries

* Status: Accepted
* Date: 2026-06-03
* Refines: [ADR-0022](0022-txn-headers-and-legs.md), [ADR-0028](0028-investment-register-surface.md)
* Related: [ADR-0019](0019-symmetric-postings.md), [ADR-0025](0025-transaction-as-postings-list.md)

## Context

Per ADR-0022, one user-visible event is one `txn_headers` row with N
`txn_legs`. A multi-posting MD transaction (a paycheck split, a manual
split, a multi-leg investment event) lives as one header. From the
account's perspective the same header appears differently:

  - The account where the user composed the split sees it as **one
    event** ("my paycheck", "my Buy+Fee"). That's the originating
    side.
  - Every other account touched by the split sees the individual
    *postings* that landed there as **independent events** —
    independent inflows or outflows that happen to share a header
    upstream.

The previous register-entry assembly buckets every multi-posting
header on every account into one entry keyed by `header_id`. That's
right for originating-side rendering (bank register's
"— N splits —" split-parent, investment register's collapse-to-one).
It's wrong for target-side accounts:

  - A paycheck split lands three transfer legs on a brokerage cash
    sleeve (100.00 / 50.00 / 25.00). The brokerage register
    aggregated them into one $175.00 row with no Action, no per-
    leg counterparty, and no way for the user to see "these are three
    independent contributions."
  - A manual cross-account split that hits two bank accounts collapses
    the target account's two legs into one split-parent in that
    account's register — but those two legs are conceptually two
    deposits to that account, not one composite event.

MD's per-account registers show each posting separately on the
target side. Coffer's data model already supports it (each `txn_legs`
row is its own leg-on-account); only the entry-assembly layer was
folding them.

## Decision

The register entry-key derivation is **asymmetric**:

  - **Originating side** (the account is touched by every posting of
    the header): bucket by `header_id`. All legs of the header on
    this account collapse into one entry. The bank register renders
    this as `kind='group'` (split-parent affordance); the investment
    register's aggregator collapses it to one row.

  - **Target side** (the account is touched by *some but not all*
    of the header's postings): bucket by `leg_id`. Each posting
    becomes its own entry of `kind='txn'`. The SPA's existing
    split-counter affordance (`txnGroupId !== null` →
    `isReadOnly = true` + "↗ Split" chip) keeps target rows non-
    editable; the user navigates to the originating account to
    edit.

Operationally the test for "originating" vs "target" is
`account_postings_on_header == header_total_postings`, both projected
through `resolved_transactions` (mig 108).

> **Update (mig 120, [ADR-0046](0046-denormalized-posting-counts-and-read-path-perf.md)):**
> these two counts are now **denormalized onto `txn_legs`** (read as
> columns) rather than computed by per-row correlated `COUNT(DISTINCT)`
> subqueries — the subqueries were the dominant per-row cost on
> full-account / report-scale scans. They're maintained by the same
> recompute interceptor that owns balances (`LegDerivedRecompute*`); the
> semantics here are unchanged.

### Worked examples

  - **Paycheck (28 legs / 14 postings) on Checking A
    (14/14 postings)**: originating → one split-parent entry. ✓
  - **Paycheck on Workplace 401(k) (3/14 postings)**: target → three
    entries, one per posting (100.00 / 50.00 / 25.00), each
    rendering with derived `'Xfr'` action in the investment register.
  - **Paycheck on FSA Account (1/14 postings)**: target → one entry
    (single posting on this account; behaviourally indistinguishable
    from a single-leg row).
  - **Buy+Fee on a brokerage cash sleeve (2/2 postings)**:
    originating → one group → investment aggregator collapses to
    one row (ADR-0028 still holds). ✓
  - **Buy+Fee's Fee category account (1/2 postings)**: target →
    one entry on the fee category. ✓

### Per-leg derived action

Target-side rows from a cash-shape header (`header.action IS NULL`)
need a label for the Action chip on the investment register and a
hint on the bank register's row body. The view exposes
`derived_action = COALESCE(h.action, 'Xfr' when this leg's counter
sits on an asset-shaped account, NULL otherwise)`:

  - True investment events (Buy / Sell / Div / DivReinvest / …)
    have `header.action` populated; `derived_action` passes through
    unchanged.
  - Cash-shape headers (paycheck splits, manual transfers between
    accounts) get `'Xfr'` per leg whose counter sits on a non-
    category account.
  - Cash-shape headers whose target leg counterparties are
    category-typed (income, expense) keep `derived_action = NULL`
    — those land in bank registers, not investment registers, and
    don't need an Action chip.

The investment register's slot-4 chip reads `derived_action`; the
aggregator's collapse-vs-expand decision reads `header.action`
(originating-side groups still aggregate as before).

## Implications

  - **Edit flow on target rows** is read-only via the existing
    `isReadOnly` / "↗ Split" path — the user opens the originating
    account to edit. No new editor work; the affordance already
    fires whenever `txnGroupId !== null` on a single-leg row, which
    is exactly the new target-side shape.
  - **Pagination cursor**: `register_entry_keys` (mig 108) emits
    the same entry-key derivation as `AssembleEntries` (CASE on
    `account_postings_on_header < header_total_postings`), so
    cursors land on entry boundaries regardless of which side of
    the asymmetry an entry sits on.
  - **Bank register split-parent** behavior is preserved on the
    originating side (paycheck on Checking A still renders as one
    split-parent). Target-side accounts no longer see split-parents;
    they see per-posting single rows.
  - **ADR-0028 amendment**: "investment events are ONE row regardless
    of posting count" applies to originating-side groups only.
    Target-side per-posting entries are independent rows; they do
    not pass through the investment aggregator's collapse path
    (they arrive as `kind='txn'`). The aggregator's `normalizeSingleLeg`
    still runs on them (Holdings-sibling strip, etc.).

## Out of scope (follow-ups)

  - **Bank register also benefits from the asymmetric rule.** A
    paycheck split landing two legs on a savings account currently
    renders as a split-parent in that savings account's register;
    after this change it renders as two independent rows. No SPA
    changes are needed for the bank register surface itself — the
    existing single-leg render path handles target rows, and the
    "↗ Split" chip already fires on `txnGroupId !== null`.
  - **Hidden data state issues** discovered while diagnosing this
    bug (multi-leg paycheck headers missing from
    `txn_header_account_balances`; duplicate header rows from
    re-imports without dedup) are tracked separately — they're
    distinct from the assembly-layer policy this ADR governs.
