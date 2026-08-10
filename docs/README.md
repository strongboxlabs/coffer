# Coffer — Documentation Index

This directory contains the design, reference, and process documentation for Coffer.
Treat these documents as the source of truth — they are kept in sync with the code
as part of every change ([engineering-standards.md](engineering-standards.md)).

## Design

| Doc | Purpose |
|---|---|
| [architecture.md](architecture.md) | High-level system design — components, data flow, build phases. Authoritative. |
| [database-schema.md](database-schema.md) | Column-level schema reference. Mirrors the SQL in `db/migrations/`. |
| [glossary.md](glossary.md) | Domain terms (transaction, counterparty, lot, ledger, override, …). |
| [moneydance-investment-actions.md](moneydance-investment-actions.md) | Ground-truth reference for what Moneydance emits in JSON per investment-transaction type (before any Coffer mapping). |
| [moneydance-import-fidelity.md](moneydance-import-fidelity.md) | Audit of what the importer captures vs. drops from an MD export; pairs with the `audit` CLI verb. |

## Process

| Doc | Purpose |
|---|---|
| [engineering-standards.md](engineering-standards.md) | Coding standards, testing policy, migration rules, the "no hacks" charter. |
| [operations.md](operations.md) | Run / backup / restore / disaster recovery. |
| [upgrading.md](upgrading.md) | Moving a **live** install between versions — order of operations, the migrations that don't apply themselves, and why a rollback is a restore. |
| [decisions/](decisions/) | Architecture Decision Records (ADRs) — one file per material decision, written when the decision is made. |

## Planning

| Doc | Purpose |
|---|---|
| [follow-ups.md](follow-ups.md) | Open-work backlog — the ordered *Next* slices + the unordered long-tail behind them. |

## How these docs interact with the code

- Every schema change ships with an update to [database-schema.md](database-schema.md) in the same commit. Stale schema docs are treated as a bug.
- Every material design decision (new dependency, framework choice, deviation from `architecture.md`) gets an ADR before merge.
- Operational procedures (backup format, restore command, env vars) are documented in [operations.md](operations.md) and exercised in CI where feasible.
- New domain vocabulary that appears in code or commit messages goes into [glossary.md](glossary.md).

If a doc and the code disagree, the code wins for *what is*, but the doc wins for *what should be* — fix whichever is wrong, don't paper over the gap.
