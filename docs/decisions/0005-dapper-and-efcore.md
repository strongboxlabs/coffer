# 0005 — Dapper for register hot path, EF Core for CRUD

* Status: Accepted (refined in PR 3.6.5 — see below)
* Date: 2026-05-08; refined 2026-05-10

## Context

The .NET API has two distinct workloads:

- **Hot read path:** the register query. Cursor-paginated reads against `resolved_transactions` joined to splits. Needs precise SQL control: tuple-cursor `WHERE (posted_at, id) < (?, ?)`, partial index hints, careful column selection, fan-out to splits in a second round trip. Volume: every page load.
- **Cold CRUD path:** account create/edit, rule management, settings. Volume: tiny. Surface: 5–10 endpoints.

EF Core can do both, but: tuple-cursor expressions translate awkwardly, raw SQL escapes are common, and the LINQ-to-SQL translation layer is overhead the hot path doesn't need. Dapper can do both, but writing every CRUD endpoint as raw SQL with manual parameter mapping is tedious and error-prone.

## Decision

- **EF Core** is the default for the API. All CRUD repositories, transactional inserts, view-backed reads, and `ExecuteUpdate`/`ExecuteDelete` set-based mutations go through `AppDbContext`.
- **EF Core via [`MR.EntityFrameworkCore.KeysetPagination`](https://github.com/mrahhal/MR.EntityFrameworkCore.KeysetPagination)** for the register query (PR 3.7+). The library generates a correct keyset-WHERE shape over EF, so the cursor-paginated path stays on the same ORM.
- **Dapper** for the importer (`src/Importer.Moneydance/`). Bulk-insert patterns (108k+ rows in a single transaction), `unnest(@arr1, @arr2)` array parameters, and deferred-constraint timing are the genuine Dapper sweet spots; the importer never adopted EF and won't until there's a concrete reason to.
- Schema is owned by the SQL migration files in `db/migrations/`, not by EF Core fluent config. EF Core is **not** authoritative for the schema.

### Refinement note (PR 3.6.5, 2026-05-10)

The original wording carved out "Dapper for the register query, merge candidate scans, report aggregations." In practice the API drifted further than intended (Dapper used for routine CRUD across PRs 3.1-3.6) and the carve-out for register paging was based on EF Core's then-awkward composite-cursor expressions. PR 3.6.5 realigns:

- All API CRUD migrated to EF Core (`UsersRepository`, `LedgersRepository`, `CredentialsRepository`, `SessionsRepository`, `BootstrapTokenService`, `ChallengeStore`, `SetupEndpoints` inline transaction).
- `IDbConnectionFactory` and `Dapper` package removed from the API project entirely.
- The remaining "register query needs Dapper" carve-out is **superseded** by adopting `MR.EntityFrameworkCore.KeysetPagination` in PR 3.7. The library expresses the composite-cursor predicate cleanly in EF.

The importer stays on Dapper for its bulk patterns; that's the only Dapper user in the codebase going forward.

## Consequences

**Positive**
- Hot path stays fast and inspectable. Dapper queries are essentially `EXPLAIN`-able as written.
- CRUD endpoints stay short and conventional.
- Schema authority lives in one place (SQL files), so EF Core drift can't corrupt the schema.

**Negative**
- Two libraries to keep in mind. Acceptable; the boundary is clear: register/reports/merge/sync use Dapper, everything else uses EF Core.
- EF Core's change tracking is slightly redundant with our explicit save patterns. Negligible cost.

## Alternatives considered

- **EF Core only.** Hot path becomes harder to reason about and tune. Rejected.
- **Dapper only.** Forces every CRUD endpoint to hand-roll SQL and parameter binding. Rejected as YAGNI in the wrong direction.
- **Repository pattern abstracting both.** Premature abstraction. Skipped; can be added if duplication ever becomes painful.
