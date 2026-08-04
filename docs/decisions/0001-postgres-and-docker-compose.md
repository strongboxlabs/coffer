# 0001 — PostgreSQL + Docker Compose as the deployment substrate

* Status: Accepted
* Date: 2026-05-08

## Context

Coffer replaces a desktop application (Moneydance) that uses an embedded local file store. The replacement needs to:

- Be reachable from a phone and other browsers, not just the desktop where it was installed.
- Hold years of historical financial data with strong durability guarantees.
- Run on hardware the user already owns (home server / VPS) without paying for a hosted database.
- Support advanced query patterns the application demands: trigram fuzzy matching, GIN indexes, statement-level triggers with transition tables, materialized views.
- Be operationally boring: standard backup/restore tooling, well-known monitoring story.

## Decision

The deployment substrate is **Docker Compose** orchestrating **PostgreSQL 16** + **Redis 7** + (later) the .NET API and the React SPA. PostgreSQL is the system of record. Redis is a cache only — never authoritative for financial state.

## Consequences

**Positive**
- Single command brings up the whole stack on any host with Docker.
- PostgreSQL is the most capable open-source database; nothing in the design is constrained by it.
- Migration files mounted at `/docker-entrypoint-initdb.d` give us a working schema apply on first run with zero tooling. (Replaced by a proper migration runner in Phase 3.)
- Trigger-maintained running balances and trigram payee matching both rely on capabilities Postgres has natively.

**Negative**
- More operational surface than a single-binary app with SQLite would have. We pay this cost once and reuse the leverage.
- Compose is fine for a single host but not a clustering story; we don't need clustering and accept the constraint.

## Alternatives considered

- **SQLite + a single .NET binary.** Simpler ops, but no trigram matching, no LISTEN/NOTIFY for the SSE push, no robust concurrent access, and less attractive backup/restore tooling. Rejected.
- **SQL Server / SQL Server Express.** Familiar in the .NET ecosystem but proprietary, larger footprint, weaker fit for the trigram/GIN workload. Rejected.
- **MySQL / MariaDB.** Capable but lacks Postgres-class features (trigger transition tables, partial indexes on expressions, mature trigram extension). Rejected.
- **Hosted Postgres (Supabase / Neon / RDS).** Pulls a single-user self-hosted app into a SaaS dependency loop, with monthly cost and outage risk. Rejected for the personal-use case.
