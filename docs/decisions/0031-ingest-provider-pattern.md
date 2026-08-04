# 0031 — Data ingest provider pattern

* Status: Accepted (direction locked; D1–D5 in §"Open decisions" remain
  TBD and will resolve in subsequent slices)
* Date: 2026-05-22
* Related: ADR-0006 (SimpleFIN over Plaid), ADR-0022 (postings model),
  ADR-0025 (transactions as postings list), ADR-0027 (investment
  action catalog), ADR-0028 (investment register surface),
  ADR-0029 (investment transaction editor)

## Context

Today we have one ingest source in production — SimpleFIN — implemented
as a single 774-line `SimpleFinSyncService`
that interleaves HTTP fetch, parse, dedup, needs-review marking,
`sync_runs` lifecycle, and DB writes. The Moneydance JSON importer
exists as a separate CLI in [`src/Importer.Moneydance/`](../../src/Importer.Moneydance/)
with its own bespoke pipeline; that CLI is treated as transitional
(import / export / backup / restore should ultimately be user-bound
API endpoints, not operator-only CLI surfaces).

The roadmap queues three more ingest sources:

- SimpleFIN brokerage feed (extends the existing pull integration
  to investment accounts)
- Investment OFX file import (user-uploads `.ofx` / `.qfx`)
- Investment CSV file import (user-uploads `.csv` with per-institution
  column mappings)

Without an abstraction, each lands as another 500–800 line bespoke
service duplicating dedup keys, needs-review marking, sync_runs
lifecycle, and DB-write coupling. The cost of doing nothing
compounds with every new source.

The domain DB shape is stable (per ADR-0022 / 0025 / 0028): every
ingest source ultimately produces `txn_headers` + `txn_legs` rows
under the same posting rules. So the variability is **upstream of
the DB** — parse + classify + identity — and the downstream
write path is shared.

## Decision

Introduce an ingest provider pattern that splits parse/classify
(per-source) from write/dedup/sync_runs (shared).

### §1. Two provider interfaces — pure translators

Providers translate raw foreign data into typed records ONLY.
They do not write to the DB, do not own dedup, do not manage
`sync_runs`. That logic moves to a single shared orchestrator.

```csharp
namespace Coffer.Api.Ingest;

/// <summary>Pull-based provider — has a long-lived connection
/// (auth credentials, institution metadata) and is polled on a
/// schedule or on user demand. Examples: SimpleFIN, future Plaid.</summary>
public interface IPullProvider
{
    string ProviderKey { get; }                  // e.g. "simplefin"
    Task<PullResult> PullAsync(
        FeedConnection conn,
        CancellationToken cancellationToken);
}

/// <summary>File-based provider — stateless per upload. Accepts a
/// payload stream + parsing context (provider key, account binding,
/// optional per-institution mapping id). Examples: ofx, qfx,
/// csv-generic, csv-brokerage-a.</summary>
public interface IFileProvider
{
    string ProviderKey { get; }                  // e.g. "csv-generic"
    Task<FileResult> ParseAsync(
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken);
}
```

The asymmetry between pull (connection lifecycle) and file
(stateless) is honest — collapsing them into one interface with a
synthetic-connection-per-upload hack would muddy provider code.

### §2. Shared orchestrator owns the write path

```csharp
public sealed class IngestOrchestrator
{
    public Task<IngestRunOutcome> RunPullAsync(
        FeedConnection conn, CancellationToken ct);

    public Task<IngestRunOutcome> RunFileAsync(
        string providerKey, Stream payload, FileIngestContext ctx,
        CancellationToken ct);
}
```

The orchestrator handles:

- Picking the right provider from the registered set (DI-resolved)
- Calling the provider's translator
- Dedup against existing rows by `(ledger_id, origin, external_id)`
  (mig 105). `external_id` is the universal per-provider stable
  identifier — each provider writes its own id there. The OFX-protocol
  columns `(online_match_fi_id, online_match_fitid)` are reserved for
  the MD importer's preserved OFX state and future OFX/QFX direct
  importers; they are NOT a SimpleFIN dedup surface.
- Needs-review flag application per-row
- `sync_runs` row creation + closing (status, counters, errors)
- Mapping the provider's typed records → `txn_headers` + `txn_legs`
  writes via the existing `TransactionsRepository` and
  `InvestmentTransactionsRepository`

Providers never touch the repositories directly. Provider tests
mock the orchestrator boundary; orchestrator tests use real
repositories against a bootstrapped synthetic ledger (per the
project's API engineering standards — unit tests with mocks,
integration tests against a real per-test ledger, not the real
export).

### §3. SimpleFIN — full retrofit

The existing `SimpleFinSyncService` splits into:

- `SimpleFinPullProvider : IPullProvider` — the HTTP fetch + JSON
  parse + classification (free-text description → action where
  detectable). Roughly 300–400 lines once the orchestration
  scaffolding is gone.
- The orchestrator absorbs everything else.

The wire endpoint signature stays unchanged:
`POST /api/ledgers/{lid}/sync/feed-connections/{cid}/sync` now
calls `IngestOrchestrator.RunPullAsync(conn)` instead of the bespoke
service. No external behavior change.

### §4. CSV — hybrid (metadata-driven + per-institution escape hatch)

CSV is the most format-variant source. We support two tiers:

- **`GenericCsvProvider : IFileProvider`** (provider key
  `"csv-generic"`) — driven by a saved `feed_csv_mappings` row
  containing: column-name → field map (date, amount, description,
  fitid?, security?, shares?, price?), date format string, header
  row count, sign convention (debit-positive vs credit-positive),
  amount split rule (single-column signed vs two-column
  debit/credit). The mapping is data, not code; user creates one
  per institution via a column-mapping wizard (queued for the CSV
  slice).
- **Per-institution custom providers** (e.g.
  `BrokerageCsvProvider : IFileProvider`, provider key
  `"csv-brokerage-a"`) — hand-coded parsing for institutions whose
  format is too irregular for the metadata-driven path (multi-row
  headers, embedded summary rows, mid-file format changes). One
  class per institution, registered alongside the generic provider.

The user picks the provider key at upload time. The orchestrator
dispatches purely on `providerKey` lookup; CSV-generic-vs-per-institution
is no different from OFX-vs-SimpleFIN at the orchestrator layer.

### §5. `feed_connections.provider_key` replaces the SimpleFIN assumption

The current schema implicitly assumes every connection is
SimpleFIN. Add `feed_connections.provider_key TEXT NOT NULL`
(value `'simplefin'` for the existing rows in the migration).
Pull providers use this column to dispatch.

File-based providers do not necessarily need a `feed_connection`
row — see open decision §D4.

## Open decisions (TBD)

These resolve in implementing slices. Marked TBD so this ADR
stays honest about scope (an ADR documents only what's explicitly
agreed; unresolved decisions are surfaced, not papered over).

- **D1. Common typed-record DTO shape.** ~~Three candidates: (a) a
  single neutral `IngestedTxn` with optional investment fields,
  (b) a discriminated union `BankIngestedTxn | InvestmentIngestedTxn`
  matching ADR-0029's action × field matrix, or (c) the existing
  `txn_legs` write shape directly.~~ **Resolved per Phase 3:**
  option (a) — single `IngestedTransaction` record with optional
  nullable investment fields (`Action?`, `SecurityTickerHint?`).
  SimpleFIN's wire format doesn't discriminate bank vs investment
  per row — the same `{id, posted, amount, description, pending}`
  carries both — so forcing a discriminated union upstream would
  invent a distinction the source doesn't make. The investment
  fields are *classifier outputs* (provider-derived, not wire-
  provided); null on bank-shape rows + on rows the classifier
  couldn't read.
- **D2. Investment-action classification ownership.** ~~SimpleFIN
  sends free-text descriptions only (no Buy/Sell/Div field). Either
  the provider classifies and tags the action, or the orchestrator
  runs a shared classifier.~~ **Resolved per Phase 3:** the
  provider classifies. Description conventions are provider-
  specific (SimpleFIN's `"YOU BOUGHT ETFA"` vs OFX's `INVTRAN` type
  enum vs CSV's column map), so the regex / lookup table lives
  with the provider. `SimpleFinDescriptionClassifier` covers
  Buy / Sell / Div / DivReinvest / Transfer; abstains on
  unrecognized descriptions. Orchestrator stays neutral —
  it sees `Action?` on the typed record and dispatches.
- **D3. Moneydance importer fate.** In-scope (becomes a batch
  provider; the CLI thins to a generic dispatcher invoking the
  orchestrator) or out-of-scope (stays transitional per
  `project_user_bound_data_io` until a Phase-6 user-bound import UI
  lands). The pattern itself doesn't depend on this; the CLI's
  refactor is independently scoped.
- **D4. File-provider connection lifecycle.** Either file uploads
  create a synthetic `feed_connection` row per upload (uniform
  `sync_runs.feed_connection_id` lineage), or `sync_runs` gains a
  nullable connection FK so file ingest skips the connection layer
  entirely.
- **D5. Holdings projection.** SimpleFIN's `/accounts` response
  carries an undocumented `holdings[]` array (verified by external
  consumers; not in the [official protocol page](https://www.simplefin.org/protocol.html)).
  Decide whether `PullResult` exposes `Holdings` as a distinct
  array (provider produces aggregate-by-security records) or
  whether we ignore SimpleFIN's holdings and continue deriving our
  own from imported transactions.

## Phase 3 design — investment classification + per-provider security mapping

Phase 3 extends `SimpleFinPullProvider` to brokerage accounts. The
problem isn't translation (the wire format is identical for bank
+ brokerage on SimpleFIN); it's deciding what to do with cash-flow
rows that happen to land in an investment account. The Middle
scope agreed in the design pass:

### Description classifier

`SimpleFinDescriptionClassifier` runs regex passes on
`SimpleFinTransaction.Description`:

| Pattern | Action |
|---|---|
| `^YOU BOUGHT` | `buy` |
| `^YOU SOLD` / `^SOLD` | `sell` |
| `^DIVIDEND RECEIVED` / `^DIV` | `dividend_cash` |
| `^REINVESTMENT` / `^REINVEST` | `dividend_reinvest` |
| `^TRANSFER` | `transfer` |
| _no match_ | `null` (orchestrator falls back to cash-flow + needs_review) |

Ticker extraction: `\(([A-Z]{1,5})\)` from anywhere in the
description (e.g. `"YOU BOUGHT … (ETFA) (Cash) Cash"` → `ETFA`).
Both `Action` and `SecurityTickerHint` are added to
`IngestedTransaction` as nullable fields; bank-shape providers
leave them null.

### Per-provider security mapping (new table)

`provider_security_mappings` persists the link between a
provider's security identifier (SimpleFIN ticker, future OFX
CUSIP, future CSV ticker) and a `securities.id` in the user's
ledger. Once the user resolves a ticker once (by saving an
investment transaction with that security selected), every
subsequent sync of the same ticker auto-resolves to the same
`security_id` without re-prompting.

```sql
CREATE TABLE provider_security_mappings (
    id                   UUID PRIMARY KEY,
    ledger_id            UUID NOT NULL REFERENCES ledgers(id) ON DELETE CASCADE,
    provider_key         TEXT NOT NULL,           -- 'simplefin', future 'ofx', 'csv-brokerage-a'
    provider_security_id TEXT NOT NULL,           -- 'ETFA' for SimpleFIN, CUSIP for OFX
    security_id          UUID NOT NULL REFERENCES securities(id) ON DELETE RESTRICT,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by_user_id   UUID NULL REFERENCES users(id),
    UNIQUE (ledger_id, provider_key, provider_security_id)
);
```

Row-level security: same per-ledger policy as other
ledger-scoped tables (the flattened-RLS pattern from migration
072 — `ledger_id IN (SELECT FROM user_ledger_grants WHERE user_id
= current_app_user_id())`).

### Sync-time write path (orchestrator brokerage branch)

For each ingested row with `Action != null`:

1. If `SecurityTickerHint != null`, look up
   `provider_security_mappings (ledger, 'simplefin', ticker)`.
2. **Mapping found**: insert investment-shape `txn_header` with
   `action` set, `origin='simplefin'`, `needs_review=true`, posting
   legs per ADR-0029 with `security_id` pre-filled, `quantity` /
   `unit_price` left null. User confirms shares + price.
3. **Mapping not found** (or no ticker hint): insert bank-shape
   cash-flow row (existing path) with `needs_review=true`. User
   reviews and picks the security manually via the editor.

For each ingested row with `Action == null`: bank-shape cash-flow
row (unchanged from Phase 2).

### Editor-time mapping record

The `/investment-transactions` create + PATCH endpoints accept an
optional `provider_security_hint = { providerKey, providerSecurityId }`
field on the request. When present AND `security_id` is set, the
endpoint upserts `provider_security_mappings`. Idempotent —
overwriting an existing mapping is the user explicitly re-linking
that ticker.

### Phase 3 phasing — 4 PRs

- **3a** — migration for `provider_security_mappings` + EF entity
  + repo. No callers yet.
- **3b** — `SimpleFinDescriptionClassifier` + `IngestedTransaction`
  extension + provider hints. Unit tests per regex branch.
- **3c** — orchestrator brokerage branch (mapping lookup,
  investment-shape insert). Integration test against synthetic
  brokerage payload.
- **3d** — editor mapping-record-on-save + brokerage register
  hint chip surfacing the detected action / ticker.

### D5 stays deferred

SimpleFIN's `holdings[]` block is not consumed in Phase 3. The
existing recompute trigger derives `holdings` from `txn_legs`
once the user upgrades each row. A future slice can add a
holdings-reconciliation panel (Rich scope from the Phase 3 design
pass) without churning the contract decided here.

## Consequences

- New namespace `Coffer.Api.Ingest` housing the two provider
  interfaces, the orchestrator, the typed-record DTOs (shape
  resolved per Phase 3 §D1 above), and the per-provider
  implementations as siblings: `SimpleFin/`, `Ofx/`, `Qfx/`,
  `Csv/`.
- New DB migration: add `feed_connections.provider_key` (NOT NULL,
  default `'simplefin'` for existing rows) + new `feed_csv_mappings`
  table (columns TBD with the CSV slice).
- `SimpleFinSyncService` is deleted (PR #123); the logic splits
  across `SimpleFinPullProvider` and `IngestOrchestrator`. The
  retrofit is a behavior-zero refactor — the existing sync
  endpoint still produces the same DB state from the same
  SimpleFIN payload.
- OFX, QFX, and CSV slices land as additional `IFileProvider`
  implementations + (for CSV) `feed_csv_mappings` config + (for
  CSV per-institution) a registry entry. Adding a new file-based
  source no longer requires touching the orchestrator.
- A future `IPushProvider` (webhook-based, e.g. for direct feeds
  that notify on new transactions) is a natural third interface
  without disrupting the two existing ones.
- Provider boundary is enforced structurally by interface
  visibility — providers cannot reach `TransactionsRepository`
  because the orchestrator owns the DI scope. Layer independence
  is a structural property, not a code-review convention.

## Implementation phasing

Each slice is an independent PR:

1. **Phase 1** — scaffold `Coffer.Api.Ingest` namespace + the two
   interfaces + an empty orchestrator. No behavior change.
2. **Phase 2** — full SimpleFIN retrofit. Delete
   `SimpleFinSyncService`; introduce `SimpleFinPullProvider` +
   orchestrator dedup / needs-review / sync_runs logic. Behavior
   zero from the user's perspective.
3. **Phase 3** — extend SimpleFIN provider to brokerage accounts
   (the original "SimpleFIN brokerage feed" roadmap item).
   Resolves D1 + D2 (DTO shape via optional investment fields;
   provider-owned regex classifier). Introduces
   `provider_security_mappings` so the user only resolves a ticker
   → security link once. D5 (holdings) stays deferred. See
   §"Phase 3 design" above for the four sub-PRs (3a–3d).
4. **Phase 4** — OFX / QFX file provider. Resolves D4 (file-provider
   connection lifecycle). **Fully shipped across three slices:**
   slice 1 (bank + credit-card statements via `OfxFileProvider`
   wrapping `OfxNet` 1.8.1 + per-ledger
   `POST /ingest/ofx/{preview,import}` endpoints); slice 2
   (investment statements — `INVSTMTMSGSRSV1` action-vocab mapper +
   SECLIST CUSIP→ticker, persisting `IngestActionHint` + the
   provider's ticker hint; `ingest_security_id` resolved at
   read time by the view per ADR-0038);
   slice 3 (SPA upload wizard on both bank and investment
   registers). Multi-account files are previewed in one call; the
   user imports one mapping at a time. Cross-source dedup
   against MD-imported rows whose preserved `online_match_fitid`
   matches an incoming OFX FITID is NOT yet implemented — the
   orchestrator's file dedup is origin-scoped on `external_id`
   per mig 105; the OR-branch on `(online_match_fi_id,
   online_match_fitid)` is captured in `docs/follow-ups.md`.
5. **Phase 5** — CSV generic provider + `feed_csv_mappings` schema
   + column-mapping wizard UI.
6. **Phase 6** — per-institution CSV providers as needed (the most
   irregular formats first if a provider's format proves recalcitrant).

Moneydance CLI (D3) reconsidered at the start of phase 4 or later;
not blocking.
