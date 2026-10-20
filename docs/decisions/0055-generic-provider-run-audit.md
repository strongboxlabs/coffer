# 0055 — Generic provider-run audit

* Status: Accepted (table renamed `provider_runs` → `ledger_operations` by ADR-0086 / mig 185)
* Date: 2026-06-20
* Related: ADR-0031 (ingest provider pattern), ADR-0033 (quote providers),
  ADR-0054 (market-data updater), ADR-0020 (multi-ledger / RLS), ADR-0013
  (dev-auth + the system user), ADR-0086 (observability sweep — the rename below)

> **Amendment (ADR-0086 sweep, migration 185).** The observability sweep added two
> operations to this audit that are *not* external-provider runs — the Moneydance
> bootstrap import (a sibling to the OFX/QIF file imports already here: `family=ingest`,
> `provider_key=moneydance`) and snapshot restore (a new `family=snapshot`,
> `provider_key=snapshot-restore`). Rather than a parallel table, `provider_runs` and its
> two child tables were renamed to `ledger_operations` / `ledger_operation_errors` /
> `ledger_operation_promotions` — the honest name for "one recorded operation on a ledger."
> Feed syncs + quote refreshes keep their two-phase `running`→terminal write; the one-shot
> import/restore ops write a single terminal row (`LedgerOperationsRepository.RecordTerminalAsync`).
> The rest of this ADR describes the original `provider_runs` design; read `provider_runs`
> as `ledger_operations` throughout.

## Context

Provider operations are audited inconsistently:

- **Ingest is audited.** `sync_runs` records SimpleFIN syncs **and** OFX/QIF
  file imports alike — status lifecycle (`running` / `completed` / `partial` /
  `failed` / `needs_reauth`), typed counters (`txns_*`), `started_at` /
  `completed_at`, `triggered_by_user_id`, plus `sync_run_errors` (per-error
  detail) and `sync_run_promotions` (promote-on-clear). It's surfaced
  per-connection on the Connections page.
- **Quotes are not audited at all.** The quote family (ADR-0033/0054) returns a
  `QuoteRunOutcome` to the caller and persists nothing. A refresh that changed
  nothing — or failed, or was rate-limited — is indistinguishable from one that
  never ran. No timestamp, no last-run summary. (This is the gap that surfaced
  in testing: 0 rows written, nothing to show, no record of when or why.)
- **No cross-provider view.** `sync_runs` is shaped for ingest, so there's no
  single place that answers "what ran across all providers, when, and how did
  it go."
- The fast-follow scheduled quote worker (ADR-0054 slice B) will produce
  automated runs that also need recording.

## Decision

One generic provider-run audit that **every** provider family writes — evolve
`sync_runs` into `provider_runs`. A single typed table (no JSON grab-bag); the
next provider sets two columns and inherits the audit.

### D1 — `provider_runs` (rename + generalize `sync_runs`)

Rename `sync_runs` → `provider_runs` in place (existing ingest history carries
over) and add:

- `family` TEXT NOT NULL — `'ingest'` | `'quote'` (CHECK).
- `provider_key` TEXT NOT NULL — `'simplefin'` | `'ofx'` | `'qif'` | `'yahoo'`
  | `'simplefin-holdings'` | … .
- `triggered_via` TEXT NOT NULL — `'manual'` | `'file-upload'` | `'post-sync'`
  | `'scheduled'` (CHECK). (`trigger` is a reserved word.)
- `details` JSONB NOT NULL DEFAULT `'{}'` — the provider-specific breakdown.
  It's open-ended and grows per provider, so it lives in one jsonb field, not
  nullable typed columns. Ingest: `{txns_fetched, txns_inserted, txns_skipped,
  txns_promoted, txns_already_known, txns_still_pending}`; quote:
  `{prices_inserted, prices_updated, securities_unresolved}`; a future provider
  writes its own shape with no migration.

`status`, `feed_connection_id` (nullable — a real FK / index / RLS
relationship, stays typed), `triggered_by_user_id`, `error_message`, and the
timestamps stay typed + provider-neutral. The existing typed `txns_*` counters
are **migrated into `details`** (one-time backfill) and dropped — all provider
detail lives in one place, not "ingest typed, the rest JSON"; the vestigial
`txns_merged` / `txns_queued` are dropped outright. C# stays typed at the
edges: the repository (de)serializes `details` into per-family records.

Backfill: `family='ingest'`, `provider_key` (SimpleFIN when
`feed_connection_id` is set, else `file`), `triggered_via`
(`manual` / `file-upload`).

### D2 — Who triggered it (`triggered_by_user_id`)

- The **authenticated user** for user-initiated runs (manual refresh, file
  upload, manual sync, and the post-sync quote pull that rides a user's sync).
- The **system user** (`00000000-0000-0000-0000-000000000001`, ADR-0013 /
  migration 014) for fully automated runs with no human owner.

> **Refined by ADR-0054 B:** the scheduled quote worker attributes its run to
> the schedule's **configuring user** (`quote_schedules.configured_by_user_id`),
> not the system user — the run needs that user's `quotes` opt-in (ADR-0057), and
> the own-user RLS on `user_preferences` rules out a system-user pref. The system
> user remains the attribution for any future ownerless automation.

The column keeps its `ON DELETE SET NULL` FK (attribution nulls if a user is
deleted) but is always written — a real user or the system user, never blank.

### D3 — Error + detail children

- `sync_run_errors` → `provider_run_errors` (already provider-neutral: `code`,
  `message`, optional provider-specific ids). Both families write it.
- `sync_run_promotions` stays ingest-specific (promote-on-clear has no quote
  analogue); FK retargets to `provider_runs`.

### D4 — Writers

- **IngestOrchestrator** — already opens/closes a run; now stamps `family`,
  `provider_key`, `trigger` (no behavior change otherwise).
- **QuoteOrchestrator** — now records **one `provider_runs` row per refresh
  run** (`family='quote'`), with the aggregate counts + unresolved count +
  per-error rows. `trigger` = `'manual'` (from `/quotes/refresh`),
  `'post-sync'` (when `IngestOrchestrator` calls it after a sync), or
  `'scheduled'` (the worker). One row per run (aggregate across the fanned-out
  providers, matching the single `QuoteRunOutcome`); per-provider granularity
  is deferred unless a need appears.
- **Scheduled worker** (ADR-0054 B) — writes `triggered_via='scheduled'`,
  attributed to the schedule's configuring user (see D2 refinement).

### D5 — Read surface

A ledger-wide **Provider activity** timeline (API + SPA) listing all
`provider_runs` newest-first with family/provider badges, status, counts, and
who/when — generalizing the per-connection `SyncActivityPanel`. The existing
per-connection panel narrows to that connection's ingest runs.

## Consequences

- Quotes get parity: every refresh leaves a dated, attributed record with
  counts — the "last run summary" + timestamps that were missing.
- One place to see all provider activity; the scheduled worker and any future
  provider inherit the audit by setting `family`/`provider_key`/`trigger`.
- Cost is honest: renaming a mature surface — table + child tables + EF
  entities + `SyncRunsRepository` → `ProviderRunsRepository` + endpoints + web
  client/types + the SPA panel — in one migration so history is preserved.

## Out of scope

- Export / backup / snapshot-restore audit (own slice if wanted later).
- Per-provider rows for a multi-provider quote run (aggregate-per-run for v1).
- Retention/pruning of old run rows (revisit if the table grows large).

## Slices (each = one PR, green on CI)

A. **Rename + generalize** — `provider_runs` migration (rename + new columns +
   backfill), entity/repo/endpoint/web/​SPA rename, ingest stamps
   family/provider_key/trigger. No behavior change; ingest history intact.
B. **Quotes write runs** — `QuoteOrchestrator` records a `provider_run` per
   refresh (manual / post-sync), with counts + unresolved + errors + the
   right `triggered_by_user_id`. Fills the quote-audit gap.
C. **Provider activity UI** — the ledger-wide unified timeline.
