# Moneydance import fidelity

A single-source-of-truth audit of what the Coffer importer captures
from a Moneydance JSON export, and what it drops or transforms
lossily. Generated alongside [ADR-0022](decisions/0022-txn-headers-and-legs.md)
and the [importer code in `src/Importer.Moneydance`](../src/Importer.Moneydance);
the prose here pairs with the runnable audit command:

```
dotnet run --project src/Importer.Moneydance.Cli -- audit data/moneydance-export.json
```

Every gap listed here was found by reading the importer source +
running `audit` against a real local export. **Scale figures shown
are from a large real-world MD export (tens of thousands of
transactions, multi-year)**; gaps that don't materialise in this
dataset are still listed (could matter for other users / future
imports).

---

## Why bother with this doc

The importer's mappers carry inline comments about *some* of what
they drop. The full picture has historically been scattered across
those comments and the mapper's silent omissions. The recurring
question "what am I losing if I switch off Moneydance?" deserves
one answer at one URL rather than a re-derivation each time.

A new gap should be added here whenever a new MD field is
identified as unread or lossy. Promote the priority based on what
the runnable audit reports for real-world data.

---

## Gap inventory

### 1. Per-leg tags (`split.tags`)

**Lossy.** MD attaches tags per leg (`split.tags`) AND per
transaction (`txn.tags`). The importer reads both and **unions
them up to the header** (`txn_header_tags`). Per-leg attribution
is destroyed.

**Why it matters for this dataset:** over a thousand transactions
where the per-leg tag values disagree with the header tag. The
dominant pattern is rental-property accounting: a single check
split across multiple properties' rent income, with the property
tag on each leg. After import: tags `{Property A, Property B,
Property C}` all sit on the header; from the header alone you
can't tell which $amount belongs to which property — you have to
cross-reference the counterparty account.

**Follow-up:** ADR-0025 deferred the leg-vs-header tag schema
decision. Recommended direction is a new `txn_leg_tags` table +
view projection of `leg_tags` (raw) and `legTagsUnion` (header
summary). Form layout already reserves the leg-level tag slot —
no further form redesign needed when this ships.

---

### 2. Per-leg status (`split.stat`)

**Partially resolved (ADR-0082) — and the mental model matters.** MD's
status isn't "the transaction's status," it's **per-leg, because each
leg participates in the reconciliation of its own account**.

- **Source-side leg's status** = whether this txn has cleared
  against the *source account's* bank statement.
- **Each counterparty leg's status** = whether this txn has
  cleared against *that counterparty account's* statement
  (where the counterparty account sees this leg as a single
  non-split row — counterparties don't "see" the split).

Different statuses on different legs is the *normal* state —
each account reconciles independently against its own
statement. A paycheck split across savings + checking can have
the salary deposit cleared on one side while the other side
still shows uncleared simply because that account's statement
hasn't arrived yet.

**Resolved for bank transactions (ADR-0082).** Reconciliation status now
lives in the per-leg `txn_leg_recon` overlay (migration 171), and the bank
importer seeds each leg from its OWN Moneydance `stat`: the origin leg from
the parent txn's `stat`, each counterparty leg from that split's own
`MdSplit.Status`. A transfer cleared in one account but uncleared in the
other now imports with distinct per-account status — no flattening. The
several hundred transactions whose leg statuses disagree in the export keep
that disagreement. (`TransactionMapper` emits per-leg `LegReconSeed`s;
`TransactionsRepository.BulkUpsertAsync` writes them, category legs
excluded.)

**Resolved for investment transactions too (ADR-0082).** MD records
reconciliation per row on investment postings as well, and the importer used
to fan a single status across every leg. `InvestmentTransactionMapper` now
seeds each leg by its role: the **brokerage** cash leg from the txn's parent
`stat`; an **external-cash (`xfr`)** counterparty from its own split `stat`
(absent/space → uncleared, no parent fallback); the **Holdings/security**
leg not at all (a position, not a statement account); category legs dropped
at persist. So a `buyx`/`sellx`/`divx` cleared at the brokerage but not yet
at the external cash account imports with the two sides distinct. (Confirmed
against the real export: 2,912 investment txns carry a per-account
disagreement.) Reminders remain always-`uncleared` templates and write no
overlay.

---

### 3. `ol.orig-payee` / `ol.orig-memo` (bank-original strings)

**Lossy.** MD preserves the *original* OFX/bank-feed payee/memo
strings alongside the user-curated `desc`/`memo`. The importer
uses them only as **fallback** when the curated value is empty;
for any row the user actually edited, the original strings are
silently dropped.

**Why it matters for this dataset:**
- Tens of thousands of txns have `ol.orig-payee`. Of those, **about
  two-fifths of all txns** have a curated `desc` that differs from
  the original.
- Tens of thousands of txns have `ol.orig-memo`; a couple thousand
  have a curated memo that differs from the original.

Examples (illustrative of the shape):
```
orig="FUEL STOP 0000000000      CITYXXX"  -> desc="Fuel Stp"
orig="GENERIC DINER INC        CITYYYY"   -> desc="Generic Diner"
orig="Retailer"                           -> desc="Retailer.com"
```

The full payee-cleanup history that the user has built up over
years is gone after import.

**Follow-up:** Add `txn_headers.bank_payee` and
`txn_headers.bank_memo` columns to preserve the original strings.
View can expose `originalPayee` / `originalMemo` on the DTO. The
register would optionally show "feed value differs" indicator
(already conceptually planned per ADR-0003). Importer migration
to populate from the export is straightforward — `ol.orig-payee`
maps directly.

---

### 4. OFX feed identifiers (`ol_fitid_1`, `ol_fi_id`) — shipped in migration 034

**Status: shipped.** `txn_headers.online_match_fitid` +
`online_match_fi_id` preserve the OFX FITID and the issuing-FI
id. Partial index `(ledger_id, online_match_fi_id,
online_match_fitid) WHERE fitid IS NOT NULL` ready for
SimpleFIN dedup. Importer round-trips both fields from MD's
`ol_fitid_1` / `ol_fi_id`. Investment-side preservation not
shipped (legacy `TransactionRow` doesn't carry the fields;
investment txns rarely have OFX state in practice) — captured
as a follow-up.

---

### 5. Single-leg events lose `0.desc`

**Lossy by design (current importer guard).** The
`TransactionMapper.cs` has:

```csharp
var legMemo = isMultiSplit
    ? NullIfEmpty(split.Description)
    : null;
```

The rationale is that MD's `0.desc` on a single-leg event often
duplicates the parent description; setting `leg_memo` would
echo the payee into the memo column. Real audit numbers
suggest this guard is too aggressive.

**Why it matters for this dataset:** a couple thousand single-leg
events have a `0.desc` that differs from `txn.desc`. Dominant
pattern is **investment contributions** — `txn.desc = "CONTRIBUTION"`
and `0.desc = "INDEX FUND A"` (the fund name). Dropping
the leg memo loses the security-level context.

**Follow-up:** Drop the `isMultiSplit` guard; let `0.desc`
round-trip into `leg_memo` unconditionally. The "echoed payee"
worry can be re-handled at the view layer by COALESCEing
through `header.memo` only when `leg_memo == header.memo` (or
even by suppressing display when the leg memo equals the
parent's payee).

---

### 6. Online-match state (`ol.match-status`, `ol.match-type`, `ol.orig-txn`) — shipped in migration 034

**Status: shipped.** `txn_headers.online_match_status` /
`online_match_type` / `online_match_orig_id` preserve MD's
lifecycle code, match-type, and pointer to the original feed
item. Vocabulary stored verbatim — no CHECK constraint (MD's
on-disk values are documented only by example; SimpleFIN sync
will pick a unified vocabulary when it ships and we can
re-introduce a CHECK then). Investment-side same caveat as
item §4.

---

### 7. Reconciliation timestamps (`rec_asof`, `rec_dt`)

**Dropped entirely.** MD stores when a row was last reconciled
against a statement. Distinct from `cleared_at` (the cleared-
transition timestamp we DO have post-migration 030).

**Why it matters for this dataset:** a couple thousand txns. Modest.

**Follow-up:** If a "reconciliation history" view ever ships,
we'd want this. Not urgent.

---

### 8. Currency conversion (`pamt` ≠ `samt`)

**Mostly inapplicable for this dataset.** MD's `pamt` (parent
account amount) and `samt` (split amount) can differ for
currency conversions. The audit heuristic flagged the vast
majority of transactions — suspiciously close to all of them. The
heuristic is too loose: investment splits routinely use
`pamt`/`samt` for share/dollar conversion, which is not FX.

**For this real-world dataset:** all accounts are USD; real FX
conversion is functionally zero. The flagged near-total is the
audit script's heuristic catching investment share-conversion
noise. **Not a real gap for this user.**

**Follow-up:** If multi-currency support ever lands, tighten
the audit heuristic by joining splits to their account currency.

---

### 9. Other top-coverage unread keys

Surfaced by the audit's "root-level keys we don't read" tail.
None individually material, included for completeness:

| Key | Coverage | Notes |
|---|---|---|
| `ts` | always | Last-modified timestamp; we have our own `created_at`. |
| `oldid` | most | MD-version migration hint. Historical only. |
| `acct` | about half | Secondary account reference, related to MD's old transfer model. |
| `qif_sn` | about a quarter | QIF-migration source data from a pre-MD migration. Historical. |
| `qif_invst_action` | a small fraction | QIF investment action code. Historical. |
| `netsync.txnid` | rare | MD's NetSync (cloud-sync) IDs. |
| `qif.orig-txn` | rare | QIF original-txn pointer. |
| `ol_pmtid` | rare | OFX payment ID for bill-pay. |

None block any current functionality. Worth re-auditing if a
specific gap ever bites.

---

## Wholesale-dropped item types

The earlier sections track *per-txn* fields we drop. There's a
parallel question: which whole *item types* in the MD export
does the importer not process at all? The audit's item-type
census answers it. From a large real-world MD export (tens of
thousands of items):

| `obj_type` | magnitude | importer handles? | notes |
|---|---|---|---|
| `txn` | tens of thousands | yes | transactions |
| `csnap` | tens of thousands | yes | security price snapshots |
| `acct` | hundreds | yes | accounts |
| `oltxns` | hundreds | **no — dropped** | pending bank-feed item queue (un-matched feed items waiting for user action) |
| `curr` | hundreds | yes | currencies / securities |
| `reminder` | a handful | yes | scheduled / recurring txn templates |
| `mem_rpt` | a handful | **no — dropped** | memorized report definitions (saved filter/layout for reports) |
| `olsvc` | a handful | **no — dropped** | online-service connection config (bank-feed endpoints + credentials metadata) |
| `misc` | a handful | **no — dropped** | misc MD preference items |
| `olpayees` | a handful | **no — dropped** | online bill-pay payee list |
| `olpmts` | a handful | **no — dropped** | online bill-pay payment instructions |
| `secsubtypes` | a handful | **no — dropped** | custom security-subtype enum entries |

**What matters out of the dropped types:**

- **`oltxns` (hundreds)** — these are bank-feed items the user
  hasn't yet matched to a manual entry. They represent
  unfinished reconciliation work the user *would* see in MD's
  "online transactions" pane. We drop them entirely; if a user
  switches mid-stream, this work-in-progress is lost. Ships
  alongside the Phase 5+ bank-feed workflow (related to items
  4 + 6 above).
- **`mem_rpt` (a handful)** — saved report definitions. If we ship a
  reporting page, the user re-creates their views.
- **`olsvc` / `olpayees` / `olpmts`** — bill-pay infrastructure.
  Self-hosted Coffer isn't shipping bill-pay (separate scope);
  these are correctly dropped for now.
- **`misc` / `secsubtypes`** — MD-internal preferences /
  enum customizations; no Coffer analogue.

---

## Confirmed-zero in this dataset

The audit checked these and found **zero occurrences**:

- **Attachments / receipts** — checked across **all items**,
  not just txns. MD can store attachments as their own
  item type (e.g. `afile`/`fdata`) or as inline fields on a
  txn; the audit looks for both. This export has neither: no
  attachment-shaped item types exist, and no transaction-level
  field matches `attach*`, `file_ref`, `file_id`, `receipt`,
  or `files`. The "attachments not imported" gap does not
  apply to this dataset.
- **Custom user-defined fields** — none set on any txn.

If a future dataset uses these features, the audit will surface
them and the gap moves up the priority list.

---

## Dropped transactions are never silent

A transaction the mapper declines (any `SkipReason` — `UnknownShape`,
`UnknownSecurity`, `UnknownXferAccount`, …) is **data loss**, so the importer
records each one (`ImportStepResult.Skips` → `SkippedTxn` with the MD txn id,
reason, security/ticker, and shares) instead of only counting it. A lossy import
**fails** its `no-dropped-transactions` validation check, which surfaces in both
the CLI output and the UI import wizard (both render the validation report). The
`import` CLI also prints a per-(reason, ticker) table of what was dropped.

### `reconcile` — diagnose without a real import

`coffer-import-moneydance reconcile <export> [--tickers T,…] [--compare-ledger <uuid>]`
runs the mapping-bearing steps against an **ephemeral ledger inside a rolled-back
transaction** — it persists nothing and touches no real ledger. It reports:

- the transactions the current importer would drop (with reason + shares),
- the holdings a clean import of the export would compute, and
- (with `--compare-ledger`) a per-`(ticker, date, quantity)` diff against a real
  ledger, classifying each mismatch as *never imported*, *imported-then-hidden*,
  or *extra in real*.

This is the tool for localising a holdings discrepancy (e.g. the 2026-07 TDLM/TDLP
undercount, which it pinned to two backdated `buyx` contributions the real ledger
never received — a data-seam gap between parallel MD/Coffer import streams, not an
importer defect).

## Priorities ordered by impact

For a large real-world dataset (tens of thousands of txns), in
descending order of real-world impact:

1. **OFX FITIDs + online-match state — shipped (migration 034).**
   Virtually all txns now round-trip through
   `txn_headers.online_match_*` columns; partial index ready for
   SimpleFIN dedup. The "preserve the user's bank-feed work" pair
   is closed.
2. **`ol.orig-payee` (about two-fifths of txns differ from curated)** —
   the user's curation history. Easy schema add; high user value.
3. **Per-leg status (tens of thousands of legs / several hundred
   txns visibly mixed)** — reconciliation is per-account, not
   per-txn (user-confirmed semantics). Schema change:
   `txn_legs.status`; promotes alongside the Phase 5
   reconciliation work.
4. **Per-leg tags (over a thousand txns)** — rental-property
   attribution. Form layout already reserves the slot.
5. **Single-leg `0.desc` (a couple thousand txns)** — investment
   fund names. Trivial importer fix (drop one guard).
6. **`ol.orig-memo` (a couple thousand txns)** — same shape as #2
   but lower volume. Bundle with #2.
7. **`oltxns` items (hundreds)** — unfinished bank-feed matching
   work. Bundle with the Phase 5 bank-feed workflow.
8. **Reconciliation timestamps (`rec_asof`/`rec_dt`)** —
   a couple thousand txns. Lower priority.
9. **`mem_rpt` items (a handful)** — saved report definitions. Lands
   if/when a reporting page ships.
10. **Investment-side OFX preservation** — `InvestmentTransactionMapper`
    passes NULL for all five `online_match_*` columns (the legacy
    `TransactionRow` input doesn't surface OFX fields). Add
    propagation if an investment-side use case ever surfaces.

Re-run the `audit` command after each new MD export to keep
this section honest as the user's MD data evolves.
