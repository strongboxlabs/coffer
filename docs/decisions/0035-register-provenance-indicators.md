# ADR 0035 — Register provenance indicators + origin/provider_key reshape

**Status:** accepted (mig 107). 2026-06-03.

## Context

The register row carries only one provenance signal today —
`needs_review` — and it disappears after the user clicks Approve.
A row that came from a SimpleFIN sync looks identical to a row the
user typed by hand once it's been accepted. Same for OFX file
imports, and same for the tens of thousands of MD-bootstrapped rows. The user
flagged this:

> need indicators in the registries for transactions that have
> been ingested from online (simplefin) or file (ofx) post accept
> or accept/merge and distinguish between all

Plus the merge state: today the loser of a merge is hidden via
`is_merged_into`, but the winner has no visible indicator that
"something was merged into me." That state has to surface too.

Investigating revealed a deeper structural problem in
`txn_headers.origin`. The column conflated two concepts:

1. **How** the row reached Coffer (which ingest mechanism wrote it)
2. **Where** the underlying transaction originated (online feed
   vs. file upload vs. typed manually)

`origin='moneydance_import'` described (1) only — every row from
the MD JSON bootstrap importer — and erased (2). A SimpleFIN-style
live-feed row that MD had previously captured looked identical to
a row the user typed by hand in MD. The register couldn't render a
provenance indicator post-accept because the data didn't carry it.

The user's mental model: rows have THREE meaningful provenance
classes — manual / online / file — and within each, a specific
provider (SimpleFIN, MD+, OFX, QIF, CSV) the user wants to remember
for audit purposes.

## Decision

### §1. Reshape `origin` to icon-level; add `provider_key`

`txn_headers.origin` becomes the icon-level mechanism (three values
only): `manual` / `online_import` / `file_import`. Drives the
register provenance icon.

A new column `txn_headers.provider_key TEXT` carries the
per-provider audit detail: `simplefin`, `mdplus`, `ofx`, `qif`,
`csv`. NULL when `origin='manual'`. A DB CHECK
(`ck_txn_headers_provider_key_iff_not_manual`) enforces the
bi-implication.

The orchestrator's dedup query shifts from `(ledger, origin,
external_id)` to `(ledger, provider_key, external_id)` — origin is
no longer per-provider, so it's the wrong dedup scope. provider_key
is.

### §2. Drop `moneydance_import` as an origin value

The MD JSON importer is a one-time bootstrap, not a permanent
provenance class. Mig 107 decomposes the tens of thousands of rows by reading
MD per-row metadata and reclassifying:

| MD signal | Origin | provider_key |
|---|---|---|
| `qif_*` fields present (txn) | `file_import` | `qif` |
| `ol_fi_id = 'md:txtimport'` (txn) | `file_import` | `csv` |
| `ol_fitid` starts with `md{txt,csv,qif}import:` (txn) | `file_import` | `csv` |
| `ol_fitid` matches `^\d{8}:` or `^\d{4}-\d{2}-\d{2}` (legacy MD+ format, txn) | `online_import` | `mdplus` |
| `ol_fi_id` starts with `mdplus:` (txn) | `online_import` | `mdplus` |
| `ol_fi_id` starts with `ofx:` AND row's account has `olbfi` set (online OFX server configured) | `online_import` | `ofx` |
| `ol_fi_id` starts with `ofx:` AND row's account has `ofx_import_acct_num` set (QFX file import) | `file_import` | `ofx` |
| `ol_fi_id` starts with `ofx:` AND neither account marker set (account-payload missing — backfill via re-import) | `online_import` | `ofx` (defensive default) |
| `ol_fitid` set but no fi_id AND row's account has `ofx_import_acct_num` set (QFX file where MD didn't preserve `<FI><FID>`) | `file_import` | `ofx` |
| `ol_fitid` set but no fi_id AND row's account has `olbfi` set (real OFX server, pre-MD+ era) | `online_import` | `ofx` |
| `ol_fitid` set but no fi_id AND neither account marker set | `online_import` | `ofx` (defensive default) |
| nothing | `manual` | NULL |

**Mig 110 refinement (`ol_fi_id` `ofx:` prefix).** MD strips OFX
wire metadata into the same per-txn shape whether the source was a
live online OFX server or a downloaded QFX file. The actual
discriminator lives on the MD `acct` object:

  * `olbfi` (e.g. `:ofx.example-broker.com:0000`) is set when the account
    is configured for a live online OFX feed.
  * `ofx_import_acct_num` is set when the account is set up to
    import QFX files manually.

`accounts.provider_raw_payload` (mig 110, analogous to
`txn_headers.provider_raw_payload`) persists the per-account MD
JSON verbatim; the classifier reads `OlbFi` / `OfxImportAcctNum`
from the row's account ref to pick `online_import` vs
`file_import`. Without account-level config the classifier defaults
to `online_import` — same as the pre-mig-110 behaviour, so existing
rows don't move until a re-import populates the account payloads.

**Note on the legacy date-prefixed FITIDs.** An earlier draft of
this ADR mis-classified rows of the shape `YYYYMMDD:<payee>:...`
as `file_import / csv` based on the synthetic shape. User
correction during review: MD+ ingested these rows online during
an earlier era and stopped supporting the date-prefix format
later. The rows stayed in MD's database unchanged. They are
`online_import / mdplus` — same provider, just a legacy wire
shape. Affects a few thousand rows in a real-world dataset,
concentrated on two accounts.

The bootstrap fact survives on a separate column —
`txn_headers.import_source` (TEXT, NULL by default). Mig 107
populates `'moneydance_export'` on every row from the MD bootstrap.
SimpleFIN-synced rows and Coffer-native manual rows leave it NULL.

### §2.5 Bootstrap and classification are independent (mig 109 amendment)

Two unrelated questions, two unrelated fields:

| Question | Field | Values |
|---|---|---|
| Was this row part of the initial MD JSON bootstrap? | `import_source` | `'moneydance-import:…'` or `NULL` |
| What mechanism *originally* produced the row? | `origin` | `manual` / `online_import` / `file_import` |

`origin` describes the source mechanism the row's data came from
inside MD (or any future tracker); `import_source` records the
one-time delivery path into Coffer. The same row can have both
populated. **Mig 107 step 7h conflated them** — every MD-bootstrap
row whose classifier signal lived in the discarded `qif_*` fields
fell through to `origin='manual'`. The user reasonably objected:
many thousands of rows showing the ✏ manual icon when MD says they were
imported into MD via QIF / CSV / OFX. The fix is to keep the two
axes apart, never let "I came from the MD bootstrap" imply
"I'm a manual entry."

### §3 Provider data is persisted verbatim; classification is derived

Mig 107's classifier ran as a pure SQL backfill against
`txn_headers`. The importer's `DecomposeOrigin` already used the
right signals — including `qif_invst_action`, `qif.orig-txn`,
`qif_sn` — but those fields lived only in the transient MD JSON.
The importer read them, applied the rule, set `origin`, and dropped
the source. When mig 107 ran months later, the data wasn't in any
column it could see, so the rule was unreachable. The comment at
step 7h documented the consequence ("Lossy on the QFX-file MD
couldn't recognize case but unavoidable without an MD JSON
discriminator") without flagging that the lossiness was itself
the bug.

The rule going forward, applied uniformly to every provider:

  **Provider data lands in the database verbatim. Classification
  is derived from it.**

`txn_headers.provider_raw_payload` (JSONB, mig 078/079) is the
designated home — already populated by SimpleFIN; extended by mig
109 to also carry the per-`txn` MD JSON item for MD-bootstrap rows.
`provider_key` tells you which shape to expect inside it.

The consequence for migrations: any future "we should have
classified this differently" question is a forward SQL migration
that reads `provider_raw_payload` directly — no file dependency,
no out-of-band CLI tool to remember, no risk of the source file
being lost. The pattern matches how the database has to behave in
production, where the import file is gone and `git pull && restart`
is the only fix path.

### §4 Drop carry-through MD audit columns (mig 109)

Auditing every `txn_headers` column for "written by an importer,
never read by anything else" yielded four dead carry-throughs that
should live inside `provider_raw_payload` going forward:

| Column | Source | Why drop |
|---|---|---|
| `online_match_status` | MD `ol.match-status` | Zero production readers; mirrored through DTO + SPA test fixtures only. |
| `online_match_type` | MD `ol.match-type` | Zero production readers. |
| `online_match_orig_id` | MD `ol.orig-txn` | Zero production readers. |
| `is_user_defined` | Mig 002 / 011 / 022 era | Fully redundant with `origin='manual' AND import_source IS NULL` (equivalently `external_id IS NULL`). Predates the post-mig-107 vocabulary. |

`online_match_fitid` and `online_match_fi_id` STAY — they're the
OFX dedup composite key (`uq_txn_headers_online_match`), structural
identity, not audit.

The mig-105 CHECK `is_user_defined OR external_id IS NOT NULL`
becomes `external_id IS NOT NULL OR origin = 'manual'` — same
invariant, expressed in the new vocabulary.

Verified against a large real-world MD export with targeted
spot-checks against four representative accounts spanning the
source mix (one bank with mixed online + manual history; one
investment with predominantly MD+; one credit card with a
CSV-heavy era; one investment with legacy MD+ date-prefix
FITIDs). The decompose went through three rounds of correction
during review:

1. Initial pass mis-classified the legacy date-prefix FITIDs as
   `file_import / csv`. User correction: those were MD+ online
   feeds in an earlier era (a few thousand rows; see note above).
2. Initial pass also used `online_match_orig_id IS NOT NULL` as
   a QIF proxy. That column is set on EVERY OL-matched row (it
   carries MD's JSON-blob preservation of the bank-original
   txn), not just QIF — so the rule grabbed many thousands of rows and
   shadowed every CSV / MD+ / OFX classification below it.
   Dropped the QIF-from-backfill rule entirely; the importer
   detects QIF correctly on fresh imports via
   `MdTxn.QifInvstAction` / `QifOrigTxn` / `QifSn`. See
   §"Migration safety" for the lossiness this introduces.
3. Without the dedicated `md{txt,csv,qif}import:` FITID-prefix
   rule, the CSV-imported rows on the credit-card account
   would have mis-classified as `online_import / ofx` because
   their `ol_fitid` doesn't have a recognizable prefix without
   the explicit check.

Final classification counts after the corrected mig:

| Origin | Provider | Magnitude |
|---|---|---|
| `manual` | NULL | tens of thousands |
| `online_import` | `ofx` | tens of thousands |
| `online_import` | `mdplus` | thousands |
| `online_import` | `simplefin` | hundreds |
| `file_import` | `csv` | hundreds |

The `manual` bucket includes thousands of QIF-only rows that the
backfill cannot detect from txn_headers columns alone (see
§"Migration safety"). Re-running the MD importer over the
original JSON will re-stamp `provider_key='qif'` on those rows
via the importer's per-row UPSERT path.

### §3. Acknowledged lossiness: OFX-file vs OFX-online

A QFX file imported into MD where MD recognized the financial
institution populates `ol_fi_id='ofx:<FI>:...'` identically to an
OFX-online row. MD's JSON export doesn't carry a per-row
discriminator that distinguishes file-OFX from online-OFX, so we
classify both as `online_import / ofx`. Practical impact: rare
(modern MD users use either MD+ or QFX-from-bank with
unrecognized FIs, both of which classify correctly). The user
accepted this lossiness during slice design.

### §4. `is_merge_winner` denormalized flag

New column `txn_headers.is_merge_winner BOOLEAN NOT NULL DEFAULT
FALSE`. Maintained atomically with `is_merged_into` in
`TransactionsRepository.PatchAsync`: when the loser's
`IsMergedInto` flips to point at the winner, the winner's
`IsMergeWinner` flips to TRUE in the same SaveChanges.

Monotonic — there's no unmerge endpoint today, so once TRUE the
flag stays TRUE. Backfilled in mig 107 from existing
`is_merged_into` data via:

```sql
UPDATE txn_headers w SET is_merge_winner = TRUE
  FROM (SELECT DISTINCT is_merged_into AS winner_id
          FROM txn_headers WHERE is_merged_into IS NOT NULL) m
 WHERE w.id = m.winner_id;
```

Chose denormalized boolean over an EXISTS subquery on the resolved
view because the resolved view is hot (every register read) and
the flag is cheap to maintain (single mutation flips both rows).
Chose boolean over enum (e.g. `winner_count INTEGER`) because
nothing today needs the count; YAGNI applies. Easy upgrade if a
count column ever becomes useful.

### §5. SPA visual treatment — icon column, three icons

Decided after presenting three alternatives (icon column / left-bar
extension / inline chip):

- **🌐 (Globe)** — `origin='online_import'`. Muted accent palette.
  Hover label: `Online import · <provider>` (e.g. `Online import · SimpleFIN`).
- **📄 (FileText)** — `origin='file_import'`. Muted warning palette.
  Hover label: `File import · <provider>` (e.g. `File import · OFX`).
- **✏ (Pencil)** — `origin='manual'`. Muted text palette.
  Hover label: `Manual entry`.

Plus a **GitMerge overlay** (small chevron, upper-right of the icon)
when `is_merge_winner=true`. Hover label appends ` · merged ←`.

Implementation: `src/Web/src/components/register/ProvenanceIcon.tsx`.
Renders inline before the payee text in the register row's
payee cell (no new grid column — minimal layout disturbance).
Same component used by bank + investment registers.

Other treatments considered + rejected:
- **Left-bar color extension** (different palette per provenance):
  rejected because today's left bar carries needs_review state;
  doubling it up creates ambiguity.
- **Inline chip** (` · SimpleFIN` text after payee): rejected as
  too verbose for a register that's already information-dense.

### §6. `needs_review` is orthogonal

The existing pre-accept indicator (left bar in state-warning
palette on rows where `needs_review=true`) stays untouched. It
represents lifecycle (awaiting user acceptance) — the new icon
represents source. A row can be both, and the visuals don't
conflict (left bar on the row edge, icon inline with payee text).

### §7. Schema invariant

```sql
CHECK ((origin = 'manual') = (provider_key IS NULL))
```

Atomic constraint: every non-manual row carries a provider_key;
every manual row leaves it NULL. The next ingest writer that
forgets to set provider_key trips at INSERT time. Catches
orchestrator / mapper bugs before they leak into user data.

## Consequences

- Mig 107 is the largest single migration since mig 102. It runs
  ten coordinated UPDATEs against the tens-of-thousands-row table and adds two
  columns + two constraints. The defensive `RAISE EXCEPTION`
  inside the backfill catches any row the decompose missed (zero
  rows in real-world data).

- `IngestOrchestrator` dedup queries shift from `origin` to
  `provider_key`. Both `RunPullAsync` (SimpleFIN) and `RunFileAsync`
  (OFX) updated.

- `Importer.Moneydance.TransactionMapper` gains a public
  `DecomposeOrigin(MdTxn)` helper applied by both the bank-shape
  mapper (`TransactionMapper.Map`) and the investment-shape mapper
  (`InvestmentTransactionMapper.MapCtx.OriginAndProviderKey`).

- `TxnHeaderRow` (API + importer) gains `ProviderKey` + `IsMergeWinner`
  fields. Importer's positional record uses default values to keep
  existing test fixtures compiling.

- `ResolvedTransactionDto` (SPA) gains `providerKey`,
  `isMergeWinner`, `importSource` fields. View projection in mig
  107 adds them at the tail of the column list.

- Cross-source FITID dedup against MD-preserved OFX rows
  (`docs/follow-ups.md` entry) becomes more interesting now: an
  MD-bootstrapped row with `provider_key='ofx'` and an
  `online_match_fitid` matches the shape of an incoming OFX file
  importer's row. The OR-branch dedup the follow-up captures is
  the next step.

## Migration safety

- All decomposition rules tested against a real-world export
  before the mig was written; the `RAISE EXCEPTION` in step 9
  catches any drift between rule and data.
- `is_merge_winner` backfill is idempotent — running the UPDATE
  twice produces the same result.
- The mig is one transaction; either every change lands or nothing
  does.
- DbUp tracks application; mig 107 will not re-run on subsequent
  API restarts.

### QIF-only rows: backfill lossiness

The mig CANNOT classify QIF-only rows (no `ol_fitid`, only
`qif_*` fields in MD JSON) as `file_import / qif` because the QIF
metadata was never projected to `txn_headers` columns — only the
`ol_*` family is on the table. In real-world data, thousands of
QIF-only rows classify as `origin='manual'` after the backfill.
They are not visibly distinct from truly-typed manual rows in
the register (both render with the ✏ pencil icon).

Three ways to recover the QIF tagging:

1. **Re-run the MD importer over the original JSON.** The
   importer's `DecomposeOrigin` reads `MdTxn.QifInvstAction` /
   `QifOrigTxn` / `QifSn` and correctly classifies. The
   per-row UPSERT path updates `provider_key='qif'` on rows
   it identifies. Cleanest fix; safe to re-run.
2. **Add a `had_qif_signal BOOLEAN` column to `txn_headers`**
   in a future mig + extend the importer to set it on insert.
   Forward-compatible but doesn't fix existing data without
   option 1.
3. **Live with the lossiness.** Those thousands of rows still carry
   their `online_match_orig_id` JSON blob if anyone wants to
   query for "rows MD had OFX-original data for"; the
   classification is just less precise in the register icon.
