# Moneydance import fidelity

A single-source-of-truth audit of what the Coffer importer captures
from a Moneydance JSON export, and what it drops or transforms
lossily. Generated alongside [ADR-0022](decisions/0022-txn-headers-and-legs.md)
and the [importer code in `src/Importer.Moneydance`](../src/Importer.Moneydance);
the prose here pairs with the runnable audit command:

```
dotnet run --project src/Importer.Moneydance.Cli -- audit path/to/export.json
```

`audit` and `reconcile` (below) are the command-line tool's only commands, and both
are read-only. Importing is done in the app's import wizard, the only import path.

Each gap listed here is stated from the importer source against the
format MD writes. **The prose states each gap as a rule**
(what MD records, what the importer keeps) and carries no counts;
`audit` reports how often each one occurs in the export it is pointed
at. A gap is listed whether or not any particular export exercises it.

---

## Why bother with this doc

The importer's mappers carry inline comments about *some* of what
they drop; the rest is in what the mappers silently omit. The recurring
question "what am I losing if I switch off Moneydance?" deserves
one answer at one URL rather than a re-derivation each time.

A new gap should be added here whenever a new MD field is
identified as unread or lossy. Rank it by what the loss breaks and
for whom; the audit's counts tell the owner of one export whether it
touches their data, and stay out of this doc.

---

## Gap inventory

### 1. Per-leg tags (`split.tags`) — shipped (ADR-0103, migration 243; per side, migration 264)

**Status: preserved.** MD attaches tags per split (`N.tags`) AND per
transaction (`txn.tags`), and they mean different sides: a split's
tags are the entered side's, while `txn.tags` is the other side's
(on a multi-split, ONE set shared by every line's other side). The
importer places them per side (`MoneydanceNotes`,
[ADR-0108](decisions/0108-memo-and-tags-per-side.md)):

- **One-split:** `0.tags` on the home leg, `txn.tags` on the other
  leg, both in `txn_leg_tags`. The header carries none.
- **Multi-split:** each line's `N.tags`, with `txn.tags` merged in,
  on that line's home leg. The other side of a split line is
  read-only and could never edit the shared set, so it shows the
  merged set inherited.
- **Investment events** (a brokerage cash move, every investment
  transaction): every tag merged onto the event's editable side —
  the brokerage leg of a one-posting event, the header of a
  multi-posting one.

**Why it matters:** wherever a transaction's legs carry different
tags, a union on the header loses which leg each one belonged to.
An illustrative case: a single payment split across several
projects' expense categories, with the project tag on each leg.
Attributed per leg, each project's tag stays on its own amount.

**Ledgers imported earlier** are re-derived by migration 264's data
steps from `provider_raw_payload`: a row still exactly as one
importer vintage wrote it (the union on the header included) is
rewritten to today's placement, and a row edited since is left to the
generic move onto its legs, never overwritten.

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
other now imports with distinct per-account status — no flattening.
Transactions whose leg statuses disagree in the export keep that
disagreement. (`TransactionMapper` emits per-leg `LegReconSeed`s;
`TransactionsRepository.BulkUpsertAsync` writes them, category legs
excluded.)

**Resolved for investment transactions too (ADR-0082).** MD records
reconciliation per row on investment postings as well, and
`InvestmentTransactionMapper` seeds each leg by its role rather than fanning
one status across every leg: the **brokerage** cash leg from the txn's parent
`stat`; an **external-cash (`xfr`)** counterparty from its own split `stat`
(absent/space → uncleared, no parent fallback); the **Holdings/security**
leg not at all (a position, not a statement account); category legs dropped
at persist. So a `buyx`/`sellx`/`divx` cleared at the brokerage but not yet
at the external cash account imports with the two sides distinct
(`InvestmentTransactionMapperTests.MapToHeaderAndLegs_seeds_reconciliation_per_leg_role_on_buyx`
pins the split). Reminders remain always-`uncleared` templates and write no
overlay.

---

### 3. `ol.orig-payee` / `ol.orig-memo` (bank-original strings) — shipped (FU-024, migration 260)

**Status: preserved.** MD keeps the *original* OFX/bank-feed payee/memo
strings alongside the user-curated `desc`/`memo`. The importer writes
the curated payee on the header and the curated memo where it belongs
(falling back to the original when the curated one is blank): a
one-split's memo, and a one-posting event's, on its home leg; only a
multi-split's or a multi-posting event's on the header
(`MoneydanceNotes.WrittenMemo` and `PlanBankRoute`). It also seeds
`txn_header_originals` with the feed's payee and memo wherever a
non-blank original differs from what was written, on bank and
investment rows alike. That is the same state the API's first edit
leaves behind: the sidecar keeps the feed's value, payee recall reads
it, and `has_overrides` is set on the row, exactly as for an edit
([ADR-0100](decisions/0100-canonical-holds-current-sidecar-holds-original.md),
2026-10-21 amendment). Migration 260 seeds ledgers imported before this
from `provider_raw_payload`, and where a first edit had already
captured MD's curated text into the sidecar, puts the feed's text
there instead. Reminder templates carry no originals.

**The consequence to know about:** `has_overrides` is true on every such
row from the moment it is imported, so for an MD ledger it means "the
feed said something else", not "edited in Coffer". On the demo sample
most transactions qualify.

**Why it matters:** MD keeps the bank's original payee and memo
beside the user's cleaned values, so every renamed payee and every
edited memo holds its original only in `ol.orig-payee` /
`ol.orig-memo`. An importer that reads the curated fields alone loses
the original wherever the two differ.

Examples (illustrative of the shape):
```
orig="FUEL STOP 0000000000      CITYXXX"  -> desc="Fuel Stp"
orig="GENERIC DINER INC        CITYYYY"   -> desc="Generic Diner"
orig="Retailer"                           -> desc="Retailer.com"
```

The originals live in migration 230's `txn_header_originals`; no
column was added for them.

---

### 4. OFX feed identifiers (`ol_fitid_1`, `ol_fi_id`) — shipped in migration 034

**Status: shipped.** `txn_headers.online_match_fitid` +
`online_match_fi_id` preserve the OFX FITID and the issuing-FI
id. Partial index `(ledger_id, online_match_fi_id,
online_match_fitid) WHERE fitid IS NOT NULL`. Importer round-trips
both fields from MD's `ol_fitid_1` / `ol_fi_id`, and an OFX file
import reads them: `IngestOrchestrator` treats an incoming OFX row as
already known when a live header anywhere in the ledger carries the
same `(online_match_fi_id, online_match_fitid)` pair, bank and
investment rows alike. Investment-side preservation is not shipped:
`InvestmentTransactionMapper` writes NULL for both (the legacy
`TransactionRow` input doesn't carry the fields), so an MD-imported
investment row can never match, and importing a brokerage OFX that
covers the same transactions enters them a second time. Tracked as
priority 10 below.

---

### 5. Single-leg `0.desc` — shipped (memo and tags per side, migration 264)

**Status: preserved.** On a one-split, MD's `0.desc` is the other
side's memo, which MD fills with a copy of `desc` (the payee) unless
the user typed one. The importer keeps it as the other side's own
memo (`MoneydanceNotes.CounterpartMemo`) unless it is blank, equals
`desc`, or equals the memo written on the entered side; in those
cases nothing is stored and the other side inherits the entered
side's memo, which shows the same and keeps following later edits.

On an event the other side is read-only, so a differing value joins
the event's one memo as "memo · other" instead of sitting where
nothing could edit it: a one-posting event's `0.desc` on the bank
route, and on the investment route every non-security split's
`desc` that is the user's own (`MoneydanceNotes.InvestmentEvent`).
Each distinct memo is kept once, where it first appears (texts a,
b, a give "a · b"), compared trimmed and case-sensitively; migration
264 joins an upgraded event's memos by the same rule.

**Why it matters:** wherever a single-leg event's `0.desc` differs
from `txn.desc`, it carries context the header lacks. An example of
the shape is an **investment contribution** — `txn.desc = "CONTRIBUTION"`
and `0.desc = "INDEX FUND A"` (the fund name). Dropping it loses the
security-level context.

**Ledgers imported earlier** are re-derived by migration 264's data
steps under the same rule as §1: rows still exactly as an old
importer wrote them are rewritten from `provider_raw_payload`, and
rows edited since are not.

---

### 6. Online-match state (`ol.match-status`, `ol.match-type`, `ol.orig-txn`) — raw payload only

**Status: not in columns.** Migrations 022 and 034 added
`txn_headers.online_match_status` / `online_match_type` /
`online_match_orig_id` for MD's lifecycle code, match-type and
pointer to the original feed item; migration 109 dropped all three,
since nothing read them ([ADR-0035](decisions/0035-register-provenance-indicators.md) §4).
The values survive only in `provider_raw_payload` (JSONB), the
canonical home for provider fields no column carries (ADR-0035 §3),
where a future reader can pick them out. The OFX dedup pair of §4
(`online_match_fitid` / `online_match_fi_id`) stays.

---

### 7. Reconciliation timestamps (`rec_asof`, `rec_dt`)

**Dropped entirely.** MD stores when a row was last reconciled
against a statement. Distinct from `cleared_at` (the cleared-
transition timestamp we DO have post-migration 030).

**Why it matters:** only to a reconciliation-history view, which
Coffer does not have; nothing today reads when a row was last
reconciled. Modest.

**Follow-up:** If a "reconciliation history" view ever ships,
we'd want this. Not urgent.

---

### 8. Currency conversion (`pamt` ≠ `samt`)

**Mostly inapplicable to a single-currency export.** MD's `pamt` (parent
account amount) and `samt` (split amount) can differ for
currency conversions. The audit's `pamt` ≠ `samt` heuristic is too
loose to tell them apart: investment splits routinely use
`pamt`/`samt` for share/cash conversion, which is not FX, so on a
single-currency export it flags rows that involve no currency at all.

**For a single-currency ledger:** there is no FX conversion to lose.
Whatever the heuristic flags there is share-conversion noise, not a
gap.

**For a ledger that spans currencies, the importer keeps both
amounts** ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)
D5): `pamt` is read in the parent account's currency and `samt` in
the split account's, each scaled by its own currency's `dec`, and a
split between two real accounts in different currencies lands as a
two-amount pair (D13). A category's line takes the real side's amount
negated, never the `samt`. No conversion is lost.

**A currency's places must fit Coffer's.** Coffer holds money at each
currency's ISO 4217 minor units, 0, 2 or 3 places (ADR-0114 D15). An
account currency whose `dec` in the file is at most Coffer's places
for that code imports, so a 3-place currency imports too, and every
amount then fits exactly. One whose `dec` is greater is refused at
preview, naming the account and both place counts; the loan mapper
checks a loan's escrow and fixed payment the same way (settled by
Claude, 2026-11-16).

**Follow-up:** tighten the audit heuristic by joining splits to their
account currency, so it stops flagging share conversions.

---

### 9. Other unread keys

Listed by the audit's "root-level keys we don't read" tail.
None individually material, included for completeness:

| Key | Notes |
|---|---|
| `ts` | Last-modified timestamp; we have our own `created_at`. |
| `oldid` | MD-version migration hint. Historical only. |
| `acct` | Secondary account reference, related to MD's old transfer model. |
| `qif_sn` | QIF-migration source data from a pre-MD migration. Historical. |
| `qif_invst_action` | QIF investment action code. Read by the investment dispatch (ADR-0027's secondary source); see §11 for the cash words that never reach it. |
| `netsync.txnid` | MD's NetSync (cloud-sync) IDs. |
| `qif.orig-txn` | QIF original-txn pointer. |
| `ol_pmtid` | OFX payment ID for bill-pay. |

None block any current functionality. Worth re-auditing if a
specific gap ever bites.

---

### 10. `chk` on investment rows — shipped (migration 260)

**Status: preserved.** The bank route always copied `chk` to
`txn_headers.check_number`; the investment route dropped it. It now
keeps it verbatim (blank → NULL). On investment rows it carries MD's
`Auto` / `EXfr` / `Xfr` markers, and on a QIF-origin row the QIF action
word (`Buy`, `ReinvLg`, ...), the only place the reinvest variants stay
distinct once ADR-0027 maps them all to `dividend_reinvest`. Migration
260 restores it over NULL on ledgers imported before. The investment
register shows it under the Action chip
([ADR-0028](decisions/0028-investment-register-surface.md), 2026-10-21
amendment).

---

### 11. Brokerage cash rows on the bank route — shipped (migration 260)

**Status: classified.** QIF-origin brokerage cash rows
(`qif_invst_action` `XIn` / `XOut` / `Cash` / `ContribX` / `MiscIncX`
with `xfrtp_bank`, no `invest.txntype`) and plain brokerage rows with
no `xfer_type` fail the importer's investment-shape test and take the
bank route. A bank-route row whose home account is a brokerage and
which has exactly one posting is written as a `transfer` with the
`transfer` posting role on both legs, as the investment route writes
for txntype `bank`. With more than one posting it is written with no
action and no posting role (fail closed); where the other side is a
category the register derives no action for it, and the investment
editor opens on "Pick action...". The QIF word stays in
`check_number`. Reminder templates follow the same rule when the
reminder's embedded txn has one split and is not investment-shaped
(no `txn.invest.txntype`, no investment `txn.xfer_type`), exactly as
the bank route only sees txns that are not: a brokerage reminder is an
investment (Xfr) reminder and fires from the brokerage. Migration 260
applies the rule to ledgers imported before, templates and the
occurrences already fired from them included, only where the row still
has the shape the payload says
([ADR-0027](decisions/0027-investment-action-catalog.md), 2026-10-21
amendment).

**What it changes downstream:** a transfer facing a category counts as
a contribution or withdrawal in investment returns, and the row
becomes investment-owned, so the other side's register shows it
read-only and it is edited from the brokerage.

---

### 12. The blank-named category

**Transformed.** MD's "uncategorized" is a top-level category with a
blank name. The importer used to name it "(unnamed)", one row per such
category. A fresh import now lands every blank-named TOP-LEVEL LEAF
category (its parent MD's root, or none; no children) on the ledger's
system Uncategorized, the one bank feeds create (reused when it
exists, created otherwise), so a split that pointed at one lands
there. That row is an expense-kind category whatever MD's kind was
(`i` or `e`): Uncategorized means "not yet classified", as bank feeds
use it, not a direction. A blank
category that has children, or that sits under another category, is a
real node in the user's tree and keeps its own "(unnamed)" row in its
place. A user's own category named "Uncategorized" stays theirs, beside
the system row. Existing ledgers are not changed: their "(unnamed)"
category stays.

### 13. Memo and tags on reminders, read-only sides and blank text — shipped (migration 264)

**Status: preserved.** Three rules complete §1 and §5:

- **Reminder templates** carry their tags and memos the way the bank
  route places them, and a fired occurrence clones the template's
  values where they sit. Before migration 264 no template's tags were
  imported; 264's data steps add them to existing ledgers from the
  payload, creating a tag the ledger does not hold, even one deleted
  in Coffer since (nothing records a deletion).
- **Nothing lands on a read-only side.** A value Moneydance keeps for
  a side that Coffer shows read-only (an investment event's
  counterpart rows, the other side of a one-posting event such as a
  brokerage cash move) is merged into the event's editable value:
  tags union into its tags, a differing memo joins its memo as
  "memo · other". Lossless and editable.
- **Text.** Every memo is trimmed, and a blank one is no memo, so the
  side inherits. Tag names resolve case-insensitively, so a name that
  differs only by case joins the existing tag rather than making a
  second one. A system merge is never capped by the tag limit and
  never drops a tag.

### 14. Security type (`sec_type`, `sec_subtype`, `sec_exchange`) — shipped

**Status: read from the security account.** Moneydance writes the three
on the type-`s` security account, not on the security's `curr` row, and
`sec_type` is a numeric code. The importer reads them there and maps the
code (with the subtype for an ETF or a money-market fund) to
`vehicle_type` and, where the vehicle implies it, `asset_class`
(ADR-0016, amendment of 2026-11-18, has the table). A code outside the
table imports as vehicle type Other and fails the
`security-types-recognised` check, which the wizard shows as a warning.
Ledgers imported earlier keep their securities unclassified.

---

## Wholesale-dropped item types

The earlier sections track *per-txn* fields we drop. There's a
parallel question: which whole *item types* in the MD export
does the importer not process at all? The audit's item-type
census answers it for any export it is pointed at. The item types
MD writes:

| `obj_type` | importer handles? | notes |
|---|---|---|
| `txn` | yes | transactions |
| `csnap` | yes | security price snapshots |
| `acct` | yes | accounts |
| `oltxns` | **no — dropped** | pending bank-feed item queue (un-matched feed items waiting for user action) |
| `curr` | yes | currencies / securities |
| `reminder` | yes | scheduled / recurring txn templates; one set to auto-commit (`acdays` 0 or more) turns the new ledger's `reminder-auto-post` job on, inside the import's transaction, as ticking Auto-post in the app does ([ADR-0097](decisions/0097-reminder-auto-post.md)); a ledger imported before that is reported by the consistency check's `reminder_auto_post` projection, whose Repair turns the job on |
| `mem_rpt` | **no — dropped** | memorized report definitions (saved filter/layout for reports) |
| `olsvc` | **no — dropped** | online-service connection config (bank-feed endpoints + credentials metadata) |
| `misc` | **no — dropped** | misc MD preference items |
| `olpayees` | **no — dropped** | online bill-pay payee list |
| `olpmts` | **no — dropped** | online bill-pay payment instructions |
| `secsubtypes` | **no — dropped** | custom security-subtype enum entries |

**What matters out of the dropped types:**

- **`oltxns`** — these are bank-feed items the user
  hasn't yet matched to a manual entry. They represent
  unfinished reconciliation work the user *would* see in MD's
  "online transactions" pane. We drop them entirely; if a user
  switches mid-stream, this work-in-progress is lost. Ships
  alongside the Phase 5+ bank-feed workflow (related to items
  4 + 6 above).
- **`mem_rpt`** — saved report definitions. If we ship a
  reporting page, the user re-creates their views.
- **`olsvc` / `olpayees` / `olpmts`** — bill-pay infrastructure.
  Self-hosted Coffer isn't shipping bill-pay (separate scope);
  these are correctly dropped for now.
- **`misc` / `secsubtypes`** — MD-internal preferences /
  enum customizations; no Coffer analogue.

---

## Not imported, and checked for by the audit

The importer reads neither of these, so the audit looks for both and
an export that uses them says so:

- **Attachments / receipts** — checked across **all items**,
  not just txns. MD can store attachments as their own
  item type (e.g. `afile`/`fdata`) or as inline fields on a
  txn; the audit looks for both: attachment-shaped item types,
  and any transaction-level field matching `attach*`, `file_ref`,
  `file_id`, `receipt`, or `files`.
- **Custom user-defined fields** — any `udf.` / `custom_` /
  `user_` key on a txn.

If an export uses these features, the audit surfaces them and the
gap moves up the priority list.

---

## Dropped transactions

A transaction the importer declines is **data loss**, on either route: an
investment mapper `SkipReason` (`UnknownShape`, `UnknownSecurity`,
`UnknownXferAccount`, …), a bank mapper `SkipReason` (`UnknownPrimaryAccount`,
`UnknownSplitAccount`, `NoSplits`), or a bank transaction dropped as a second
copy of an OFX `(fi_id, fitid)` pair already in the export (`DuplicateFitid`).
The importer records each one (`ImportStepResult.Skips` → `SkippedTxn` with the
MD txn id, reason, date and, for an investment transaction, security/ticker and
shares) instead of only counting it. A lossy import **fails** its
`no-dropped-transactions` validation check. The check also fails when a
transaction step's `Skipped` count runs ahead of its list, because a drop
without a reason is still a drop; other steps' counts (the securities step's
fiat currencies) are not transactions and are not held to it. The ledger is still
committed, so the import wizard's last step reports it: the status line says the
import completed with warnings, each failed check gets its own box with a
plain-language sentence (the check's summary, not its message in the importer's own
terms, which stays for diagnosis), and the
dropped-transactions box, first of them, lists every transaction not imported in a
scrolling table — date, security (ticker and name), shares and why, with a dash
for the security and shares of a bank transaction — up to a fixed number of rows, with a count of the rest. They are still in the Moneydance file; the fix is made there and the
export imported again into a new ledger. The job status carries the same detail
(`warnings`, `skippedTransactions`, `skippedTransactionsNotListed`; ADR-0071,
amendment of 2026-10-25). The status is held in memory only, so the API also writes
a warning to the server log naming the job, the new ledger and each failed check.
`reconcile` (below) lists the dropped transactions without importing.

### `reconcile` — diagnose without a real import

```powershell
$env:COFFER_DB_CONNECTION = "Host=localhost;Port=5432;Database=coffer;Username=coffer_service;Password=$(Get-Content -Raw secrets/coffer_service_password)"
dotnet run --project src/Importer.Moneydance.Cli -- reconcile path/to/export.json [--tickers T,…] [--compare-ledger <uuid>]
```

`--db <CONNECTION_STRING>` takes precedence over `COFFER_DB_CONNECTION`. It connects as
`coffer_service` (BYPASSRLS, for cross-ledger access). It runs the mapping-bearing steps against an **ephemeral ledger
inside a rolled-back transaction** — it persists nothing and touches no real ledger.
It reports:

- the transactions the current importer would drop (with reason + shares, as a
  per-(step, reason, ticker) table),
- the holdings a clean import of the export would compute, and
- (with `--compare-ledger`) a per-`(ticker, date, quantity)` diff against a real
  ledger, classifying each mismatch as *never imported*, *imported-then-hidden*,
  or *extra in real*.

This is the tool for localising a holdings discrepancy. A mismatch classified *never
imported* for a transaction the current importer maps cleanly says the ledger never
received that row — a gap in how the ledger was populated, not an importer defect.

## Priorities ordered by impact

In descending order of impact: how much of the user's own work each
loss throws away, and whether anything in Coffer reads what is lost.

1. **OFX FITIDs — shipped (migration 034).** The FITID and FI id
   round-trip, wherever MD recorded them, through
   `txn_headers.online_match_fitid` / `online_match_fi_id`, which an
   OFX file import reads for cross-source dedup (§4). Closed on the
   bank route; the investment route is \#10. The online-match state
   (§6) has no reader and stays in `provider_raw_payload`.
2. **`ol.orig-payee` (every renamed payee lost its feed original) —
   shipped (FU-024, migration 260).** Seeded into
   `txn_header_originals` where it differs; see §3.
3. **Per-leg status (every txn whose legs cleared in different
   statements) — shipped (ADR-0082, migration 171).** Reconciliation
   is per-account, not per-txn; each leg is seeded from its own
   `stat` into `txn_leg_recon` (§2).
4. **Per-leg tags (every txn whose legs carry different tags) —
   shipped (ADR-0103, migration 243; per side, migration 264).** Each
   side's tags land on that side (§1), and migration 264 re-derives
   ledgers imported earlier.
5. **Single-leg `0.desc` (every single-leg event whose `0.desc`
   differs from its description) — shipped (migration 264).** It is
   the other side's own memo, or joins an event's memo (§5).
6. **`ol.orig-memo` — shipped with #2.**
7. **`oltxns` items** — unfinished bank-feed matching
   work. Bundle with the Phase 5 bank-feed workflow.
8. **Reconciliation timestamps (`rec_asof`/`rec_dt`)** —
   nothing reads them yet. Lower priority.
9. **`mem_rpt` items** — saved report definitions. Lands
   if/when a reporting page ships.
10. **Investment-side OFX preservation** — `InvestmentTransactionMapper`
    passes NULL for both `online_match_*` columns (the legacy
    `TransactionRow` input doesn't surface OFX fields). Until the
    fields propagate, a brokerage OFX import cannot recognise
    MD-imported investment rows and enters them again (§4).

The order rests on what each loss breaks, not on counts. Run the
`audit` command against an export to see which of these it
exercises; its counts belong to that export and stay out of this
section.
