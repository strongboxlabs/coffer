# Engineering Standards

This document is the contract between past, present, and future maintainers (including AI assistants) on **how** Coffer is built, not just what gets built. The architecture doc tells you *what* exists; this one tells you the rules of the road.

The non-negotiables — the "stringent practices, no hacks" charter — are in §1. Everything else is grounded in them.

---

## 1. The "no hacks" charter

A hack is anything that solves the immediate problem at the cost of leaving the system in a worse state than before. Concretely:

1. **No throwaway shortcuts in committed code.** If the right fix is too big for now, ship a smaller correct fix or open a tracked issue with a clear rationale. Don't leave `TODO: figure this out later` without a corresponding issue.
2. **No commented-out code, dead branches, or "removed for now" comments.** Delete cleanly. Git history is the audit trail.
3. **No silent failure-handling.** Errors are either expected (handle them with intent) or unexpected (let them bubble up loudly). Never `catch { /* ignore */ }`.
4. **No undocumented behaviour.** A surprise in the code (workaround for a bug, non-obvious invariant, ordering dependency) gets a comment explaining *why*.
5. **No magic numbers or magic strings without a name.** `3` for the merge date window is `merge_rules.date_window_days`. CHECK constraint enums are listed in [database-schema.md](database-schema.md).
6. **No bypassing tests, hooks, or CI.** If something is failing, fix the underlying problem, not the check. Pre-commit hooks and CI are part of the system.
7. **No backwards-compat shims for code that hasn't shipped to anyone.** Pre-1.0 we change schemas and APIs cleanly via migrations and breaking commits — we don't carry weight we never owed.
8. **No "I'll document it later."** Documentation is part of the change, not a follow-up task.

When tempted to violate any of the above, write down the reason in a commit message or ADR. The reason often turns out to be insufficient.

---

## 2. Source control

### 2.1 Branching

- Trunk-based development on `main`.
- Feature work in short-lived branches named `<type>/<short-slug>` (e.g. `feat/importer-skeleton`, `fix/balance-trigger-edge`).
- Merge via squash-and-merge so `main` has a clean linear history. Avoid merge commits on `main`.
- `main` should be deployable at all times.

### 2.2 Commit messages — Conventional Commits

```
<type>(<scope>): <subject>

<body — the WHY>

<footer — refs, breaking-change notes>
```

Types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`, `ci`, `perf`.
Scopes (initial set): `db`, `importer`, `api`, `web`, `sync`, `infra`, `docs`.

The body should answer "why this change is needed" and "what alternatives were considered" when non-trivial. The diff already says what changed.

### 2.3 Pull requests

- Every change goes through a PR, even when only one person reviews.
- The repository's PR template is mandatory (`.github/PULL_REQUEST_TEMPLATE.md`).
- CI must pass before merge.
- Schema changes require: a new migration file, a [database-schema.md](database-schema.md) update, and a verification step in [db/test/](../db/test/).

---

## 3. Database

### 3.1 Migrations are forward-only

- Once a migration file is committed to `main`, it is never edited.
- Need to fix a mistake? Add a new migration that corrects it.
- Migration filenames are zero-padded sequential: `001_extensions.sql`, `002_tables.sql`, …
- Each migration is idempotent where reasonable, but the primary contract is "run once on a fresh DB". **DbUp** is the migration runner: the API applies every file in `db/migrations/` at startup via `MigrationRunner` (`src/Api/Migrations/`), journaled in a DbUp tracking table, using the service-role connection.

### 3.2 Schema changes

Every schema change ships in the same commit as:

1. The migration file (`db/migrations/NNN_*.sql`).
2. An update to [database-schema.md](database-schema.md) reflecting the new shape.
3. A test or verification script if behaviour is non-trivial (triggers, complex constraints, computed columns).

### 3.3 Triggers as a last resort

Per [ADR-0032](decisions/0032-triggers-as-last-resort.md), validation
invariants live in API code; triggers exist only for derived-state
recompute that can't be reproduced at the call site without
duplication.

**A new trigger requires justification across THREE gates:**

1. **No API path can carry the invariant.** Coherence rules
   between rows (e.g. "leg's posting_role matches header's
   action") live in repository code that constructs the rows.
   Don't add a trigger to cover for a writer that should be
   doing the work itself; fix the writer.
2. **The trigger is idempotent.** Running the trigger twice on
   the same state produces the same result. AFTER UPDATE
   triggers that *read other rows the same statement might
   mutate* are forbidden — they create transient false positives
   during multi-row EF flushes.
3. **The trigger doesn't chain.** Don't add a trigger whose
   side-effect (UPDATE on another table) will fire ANOTHER
   trigger that reads state mid-flight. If the chain is
   unavoidable, the second trigger must be safe under partial
   state.

Triggers in scope today:

- **Recompute (REMOVED — moved to the API)** — the balance and
  holdings/lots recompute triggers were **dropped** in migrations
  102 (balances) and 104 (holdings + lots). Recompute now runs in
  EF `SaveChanges` interceptors — `LegDerivedRecomputeInterceptor`
  (balances) and `HoldingsRecomputeInterceptor` (holdings + lots),
  registered in `Program.cs`. The recompute reads the
  override-aware effective values; doing it at the persistence
  boundary (not a DB trigger) avoids the AFTER-UPDATE
  read-rows-being-mutated hazard and keeps the logic in one
  testable place.
- **Invariant lockdown (kept)** —
  `trg_reject_txn_headers_created_at_update` (mig 093) and
  `trg_reject_txn_headers_seq_update` (mig 095). Defend the
  canonical `(posted_at, seq)` ordering by rejecting any UPDATE
  that would mutate `seq` or `created_at`. Both columns are set
  once on INSERT and never change.
- **Validation (being phased out, ADR-0032 §4)** — all migrated
  to API-side enforcement; remaining cleanup tracked in
  follow-ups.

**Every trigger** (existing or new) must have:

- A clear name (`trg_<table>_<purpose>_<event>`).
- A documented invariant in
  [docs/database-schema.md](database-schema.md) and (for
  recompute triggers) in the originating ADR.
- An end-to-end test exercising insert, update, delete, and any
  chain interactions with other triggers on the same table.

**Code review checklist for any PR touching `db/migrations/`:**

- Does this PR add a trigger? If yes, the PR description must
  cite which of the three gates above is satisfied.
- Does this PR remove a trigger? If yes, the commit message
  must name the API surface that now upholds the invariant and
  link the integration test that proves it.

**Raw SQL writes to transaction tables are prohibited outside
of repository code and migrations.** Tests / fixtures may
INSERT directly with the understanding that they bypass API
invariants — those test fixtures must construct valid rows by
hand.

### 3.4 Naming

- snake_case for tables and columns.
- Singular-noun column names; plural-noun table names.
- Foreign keys end in `_id`.
- Boolean columns start with `is_` or `has_`.
- Timestamps end in `_at` and are stored as `TIMESTAMPTZ`.
- Dates without time end in `_date` and are stored as `DATE`.

### 3.5 Transactional integrity

Every multi-statement database operation lives inside an explicit
Postgres transaction. Single-statement reads and writes can use
auto-commit. The discipline applies to every layer — importer, future
API, future sync worker, tests — not just bulk imports.

**Rules:**

1. **Repositories are stateless.** They accept an `NpgsqlConnection`
   (or, in the future, a connection + transaction abstraction) and
   execute against the caller's transaction context. Repositories
   never call `BeginTransactionAsync` themselves; that's the caller's
   job at the orchestration layer.

2. **Transaction boundaries live at the orchestration layer.**
   - The Moneydance importer wraps every step inside a single
     transaction in `ImportCommand`. If any step fails, the entire
     import rolls back.
   - The .NET API (Phase 3+) wraps each write endpoint in a
     transaction. Read endpoints can use auto-commit / single-statement
     queries.
   - The SimpleFIN sync worker (Phase 5+) wraps each pulled batch (per
     institution per run) in a transaction.

3. **Schema invariants are enforced in the database, not in
   application code.** FK constraints, CHECK constraints, UNIQUE
   indexes (full and partial), and triggers exist precisely so that no
   application-layer bug can produce inconsistent state. If a code
   path attempts something the constraints disallow, Postgres rejects
   it and the surrounding transaction can roll back cleanly. Do not
   mirror these constraints with application-layer pre-checks unless
   you also need a friendlier error message — and even then, the DB
   constraint stays as the safety net.

4. **Bulk operations preserve transactionality.** Multi-row INSERT via
   `unnest()` is a single statement inside the surrounding
   transaction. Statement-level triggers (e.g. `balance_after`) fire
   once per statement with all affected rows in scope; if anything
   later in the transaction fails, ROLLBACK undoes both the bulk
   insert and the trigger's effects.

5. **No DDL inside data-write transactions.** `ALTER TABLE`,
   `DISABLE TRIGGER`, `CREATE INDEX`, etc. inside a write transaction
   is a smell — it usually signals an attempt to bypass schema
   invariants for performance. If a real performance problem exists,
   the right fix is almost always at the application level (batching,
   bulk SQL, query restructure), not at the schema level.

6. **Tests inherit the same discipline.** Integration tests reset
   state via transactions or `TRUNCATE`; either way the DB's
   invariants apply during test runs. Adding a test that explicitly
   exercises rollback (start a transaction, write rows, throw,
   ROLLBACK, assert zero rows) for any new repository is good
   practice.

7. **Single-writer concurrency model.** Coffer is a single-user app.
   We do not currently need row-level locking strategies, optimistic
   concurrency tokens, or conflict-resolution rules. If we ever go
   multi-user, this section gets superseded by a new ADR.

---

## 4. Code (.NET, TypeScript)

### 4.1 General

- Smallest reasonable abstraction. Three similar lines is better than a premature interface.
- No dead code. No unused parameters. No orphan helpers.
- Public APIs (HTTP endpoints, exported functions) get explicit input validation at the boundary; internal code trusts internal callers.
- Errors are typed where the language supports it. Wrap and re-throw with context; don't swallow.

### 4.2 .NET conventions

- Nullable reference types **on**.
- `record` for DTOs; `class` for behaviour.
- Async all the way; no `.Result` / `.Wait()`.
- Migrations and seed data live in SQL, not in C# fluent migrations. EF Core is for queries, not for schema authority.

### 4.2.1 Data-access split: EF Core vs. Dapper

Per [decisions/0005-dapper-and-efcore.md](decisions/0005-dapper-and-efcore.md), with the realignment as of PR 3.6.5:

| Layer | Default | Notes |
|---|---|---|
| **API** (`src/Api/`) | **EF Core via `AppDbContext`** | Routine CRUD, transactional inserts, view-backed reads, `ExecuteUpdate`/`ExecuteDelete` for set-based mutations. The Dapper package is not referenced by the API at all. |
| **Register query** (PR 3.7+) | **EF Core via [`MR.EntityFrameworkCore.KeysetPagination`](https://github.com/mrahhal/MR.EntityFrameworkCore.KeysetPagination)** | The composite-cursor `(posted_at, id)` paging is the one place ADR-0005 originally carved out for Dapper. The library generates a correct keyset-WHERE shape over EF, so the API can stay on one ORM. |
| **Importer** (`src/Importer.Moneydance/`) | **Dapper** | Bulk-insert patterns (108k+ rows in a single transaction), `unnest(@arr1, @arr2)` array-parameter inserts, deferred constraint timing — these are the genuine Dapper sweet spots. The importer never adopted EF and won't until there's a concrete reason to. |
| **Tests** | Same as the layer they test | Test helpers (arrange / assert) use the same data-access layer as the production code under test, so a test isn't more permissive than the path it covers. |

### 4.2.2 EF Core entity mapping rules

- **Every FK in the schema is configured on the entity from the start.** When a new entity is added to `AppDbContext`, every `REFERENCES` clause in its migration SQL becomes a `HasOne<T>().WithMany().HasForeignKey(...).OnDelete(...)` call. The `OnDelete` value mirrors the SQL `ON DELETE` clause (Cascade / Restrict / SetNull). This isn't optional — without it, EF picks an arbitrary INSERT order and a multi-entity `SaveChangesAsync` can fail FK constraints on Postgres.
- **Every column with a DB `DEFAULT` is `ValueGeneratedOnAdd`.** EF then skips the column on INSERT and reads the generated value back via `RETURNING`.
- Snake-case column names map via explicit `HasColumnName` calls. Adding a snake-case naming convention package is a future cleanup; explicit mapping is the readable choice for the column counts we have today.
- Keyless query types (`HasNoKey().ToView(...)`) are the right shape for view-backed reads. Project from the view type to a public DTO at the repository boundary so the API surface stays separable from the EF model.

### 4.2.3 API project layer layout

The API enforces a four-layer split (data / security / API-surface / cross-cutting). Every new file must land in its layer; vestigial single-file feature folders are a smell, not an organisation pattern.

| Layer | Path | What lives here |
|---|---|---|
| **Data** | `src/Api/Db/` | `AppDbContext` plus three subfolders: `Db/Entities/` (every `*Row` and `*View` keyed/keyless type — one namespace, one directory), `Db/Repositories/` (pure-CRUD wrappers over `AppDbContext`), `Db/Services/` (DB-touching business services like `ChallengeStore`, `BootstrapTokenService` whose race-safe / token-generation logic doesn't fit the repo shape). |
| **Security** | `src/Api/Auth/` | Auth handlers, schemes, `ICurrentUserAccessor`, plus `Auth/Webauthn/` for ceremony-layer code that doesn't touch `AppDbContext` directly (`Fido2WebAuthnService`, `SessionService`, `RecoveryCodes`, request/response models). |
| **API surface** | `src/Api/Endpoints/` + `src/Api/Contracts/` + `src/Api/Errors/` | Route mapping in `Endpoints/`; every public wire DTO in `Contracts/` (one namespace `Coffer.Api.Contracts`); ProblemDetails envelopes + the stable code catalogue in `Errors/`. |
| **Cross-cutting** | `src/Api/Configuration/`, `src/Api/Logging/`, `src/Api/Migrations/` | Options binding, request-scope logging, DbUp runner. |

Rules of the road:

- A repository projecting to a DTO inside the LINQ `Select(...)` is the deliberate efficiency trade-off — `Db/` references `Contracts/`, never the other way round.
- DTOs are wire shapes, not entities; they never get EF mappings.
- Test seed helpers (e.g. `SyntheticLedger`) may use raw SQL through `db.Database.ExecuteSqlInterpolatedAsync` for tables not yet mapped as EF entities — test infra is its own layer and isn't required to stay on EF.

### 4.2.4 RLS role split (PR 3.8 / ADR-0020 Phase D)

The API runs against two Postgres roles, with a strict boundary:

| Role | `BYPASSRLS` | Used by | Notes |
|---|---|---|---|
| **`coffer_app`** | no | Runtime `AppDbContext` (the scoped DI registration) | Every authenticated request runs `SET app.user_id = '<uuid>'` on the pooled connection via `AppUserDbConnectionInterceptor`. RLS policies on every ledger-scoped + identity table filter rows. Pre-auth requests leave `app.user_id` unset and RLS denies — fail-closed. |
| **`coffer_service`** | yes | `ServiceDbContextFactory` (singleton, manual `.Create()` per use). Owns the schema (table creator). | The migration runner, the WebAuthn pre-auth lookups, every auth-subsystem write (`SessionsRepository`, `CredentialsRepository`, `ChallengeStore`, `BootstrapTokenService`, `UsersRepository.CreateAsync` / `GetByUsernameAsync`), the ledger-create escalation (`LedgersRepository.CreateWithOwnerAsync`), and the importer all use this role. |

Boundary rules:

- **Authenticated endpoint code** receives `AppDbContext` from DI. RLS does its job; the explicit app-layer per-ledger gate stays as the friendly-error path.
- **Pre-auth or cross-user code paths** explicitly inject `ServiceDbContextFactory` and `await using var db = factory.Create()` for that operation. The factory boundary is visible in the type signature — there's no implicit switch.
- **Identity tables (`users`, `user_ledger_grants`, `auth_sessions`, `webauthn_credentials`, `recovery_codes`, `webauthn_pending_challenges`, `ledgers`)** are `FOR SELECT TO coffer_app` only in migration 017; writes require service-role escalation. The exception is `users` (and `users.last_opened_ledger_id` self-update) — `FOR ALL` with `id = current_app_user_id()` so the self-update path stays on `coffer_app`.
- **Ledger-content tables (`accounts`, `transactions`, `tags`, `merge_rules`, `transaction_rules`, …)** are `FOR ALL TO coffer_app` with `WITH CHECK == USING` so the user can read AND write under the same predicate — they're the legitimate writer.

When in doubt: if the operation is "the authenticated user doing a thing in their own data," use `AppDbContext` and trust RLS. If the operation crosses the authentication boundary (resolves who the user is, mints/burns auth state, creates a ledger as a unit, applies a DDL migration), use `ServiceDbContextFactory`.

### 4.3 TypeScript / React conventions

- Strict TS (`"strict": true`). Plus `noUnusedLocals`, `noUnusedParameters`, `noUncheckedSideEffectImports` per `src/Web/tsconfig.app.json`.
- Functional components only.
- TanStack Query for server state; Zustand or React Context for local UI state. No Redux.
- TanStack Router with **code-based routing** (not file-based) — the route tree in `src/router.ts` reads top-to-bottom with no codegen step.
- Tailwind v3 + hand-built primitives following shadcn/ui conventions (we don't pull shadcn via its CLI; we write the ~5 source files we need ourselves). No CSS-in-JS.
- Prefer composition over hooks-with-side-effects. No "magic" hooks that secretly mutate global state.
- `@simplewebauthn/browser` is the WebAuthn client. Don't improvise base64url encoding or `navigator.credentials` invocation.

### 4.3.1 Frontend engineering posture

The user is the long-term maintainer and is not a React expert. The posture: **conservative, grounded, security-first, no shortcuts** (saved as memory `feedback_frontend_engineering_posture` for AI agents and as standards here for humans).

Concretely:

- **Pick the boring stable tier of every dependency.** When two viable choices exist (Tailwind v3 vs v4, code-based routing vs file-based, Vite 5 vs 6), default to the one with more production miles. Note the trade-off in a comment so future-me knows why.
- **Pin versions exactly.** No `^` carets in `src/Web/package.json` for foundational deps. Updates are intentional; the committed `package-lock.json` makes installs reproducible.
- **Ship small slices.** Login flow before setup ceremony before register virtual scroll. Each PR defensible on its own; verify end-to-end manually in a browser before moving on.
- **Security defaults visible at the call site.** Cookie attributes (`HttpOnly`, `Secure`, `SameSite=Strict`) live on the API side per ADR-0013. On the SPA side: `credentials: 'include'` set explicitly on every fetch, no `dangerouslySetInnerHTML`, no `eval`, no auth state in `localStorage` (HttpOnly cookie is authoritative; SPA never reads it).
- **Heavy commenting is on-brand.** Explain the WHY of non-obvious decisions (a specific ARIA role, a debounce, a status-code branch). Skim-readability matters more than terseness.

### 4.3.2 Web project layout

```
src/Web/
├── package.json            Pinned deps + scripts (dev, build, typecheck, lint, test)
├── tsconfig.{json,app,node}.json    Strict TS, project references
├── vite.config.ts          /api proxy + Vitest test config (uses defineConfig from vitest/config so the `test` block types)
├── eslint.config.js        Flat config, typescript-eslint + react-hooks + react-refresh
├── tailwind.config.ts      Tailwind v3 — content globs only
├── postcss.config.js       Tailwind + autoprefixer
├── index.html              Vite entry
├── vitest.setup.ts         Extends expect with @testing-library/jest-dom
└── src/
    ├── main.tsx            QueryClientProvider + RouterProvider mount
    ├── App.tsx             Pure components only (RootLayout, AuthedOutlet)
    ├── router.ts           Route tree (code-based) + auth-check beforeLoad
    ├── index.css           Tailwind directives
    ├── lib/
    │   ├── api.ts          Typed fetch wrapper + ApiError + ProblemDetails decode
    │   ├── auth.ts         WebAuthn login ceremony via @simplewebauthn/browser
    │   ├── cn.ts           clsx + tailwind-merge helper
    │   └── types.ts        Shared API response types
    ├── components/ui/      Hand-built primitives (Button, Input, Label)
    └── routes/             One folder per route, route component lives here
```

---

## 5. Testing

| Layer | Required test type | Tooling |
|---|---|---|
| SQL (triggers, complex views, constraints) | End-to-end SQL script | psql DO blocks in `db/test/` |
| .NET pure logic (merge scoring, rule evaluation, hashers, encoders) | Unit tests with mocks | xUnit + NSubstitute (when needed) |
| .NET DB-touching code (EF Core or Dapper) | Integration tests against a disposable Postgres | Testcontainers |
| HTTP endpoints | Integration tests | WebApplicationFactory |
| Frontend pure logic | Unit tests | Vitest |
| Frontend UI | Component tests + manual browser verification | Vitest + Playwright (later) |

A change to behaviour without a test is a hack. Adding a test for behaviour you didn't change is welcome but optional.

### 5.1 Unit vs. integration test layout

Test projects keep the two buckets in **separate sub-folders / namespaces**: `Unit/` for pure-logic tests with no I/O and mocked collaborators, `Integration/` for tests that touch a database, an HTTP host, or any other external surface. The split lets reviewers see the cost of a test at a glance and lets contributors run only what they need (`dotnet test --filter FullyQualifiedName~Unit` for the fast path).

Example layout (from `tests/Api.Tests/`):

```
tests/Api.Tests/
├── Unit/
│   └── Auth/
│       ├── RecoveryCodesTests.cs            ← Argon2id round-trip; no DB
│       └── BootstrapTokenHelpersTests.cs    ← static encoders; no DB
└── Integration/
    ├── Infra/
    │   ├── PostgresFixture.cs               ← Testcontainers + DbUp
    │   ├── ApiFactory.cs                    ← WebApplicationFactory<Program>
    │   ├── SyntheticLedger.cs               ← per-test atomic builder
    │   └── TestConnectionFactory.cs
    ├── Auth/
    │   ├── BootstrapTokenServiceTests.cs    ← DB-touching service
    │   ├── UsersRepositoryTests.cs          ← Dapper CRUD round-trips
    │   └── CredentialsRepositoryTests.cs
    └── HealthEndpointsTests.cs              ← WebApplicationFactory
```

### 5.2 Integration tests bootstrap a synthetic ledger atomically

Every integration test arranges its own state from scratch. Helpers like `SyntheticLedger.CreateAsync` mint a fresh ledger row, a fresh user with a random username, and a default owner grant in **one transaction** at the start of each test. Tests reach further state (accounts, transactions, credentials) via repository methods seeded by these per-test ids.

This pattern is the source of test isolation:

- **No shared fixture state.** Tests don't rely on rows another test left behind, and don't have to TRUNCATE between runs (except for genuinely global tables like `bootstrap_tokens` whose semantics are inherently global).
- **No real export.** Importing a large real-world Moneydance file in tests is forbidden — it's slow, non-deterministic, and forces every test to coexist with thousands of unrelated rows. Atomic synthetic data is fast and lets the test name what it actually exercises.
- **Per-anchor uniqueness keeps tests parallel-safe.** Migration 014 made the `external_id` indexes per-`ledger_id`, so two tests inserting the same MD external_id under different ledgers don't collide.

A test that needs the same ledger across phases of arrange/act builds it once and reuses the returned `SyntheticLedger` instance; a test that needs two ledgers (e.g. cross-ledger isolation) calls the builder twice.

---

### 5.3 API error envelopes — RFC 9457 ProblemDetails with a `code` discriminator

Every non-success API response is `application/problem+json` (RFC 9457). Within that envelope, **business-rule rejections use HTTP 422 Unprocessable Entity with a stable `code` extension** that the client dispatches on. Status codes carry only the transport-layer meaning:

| Status | Meaning | Example |
|---|---|---|
| `400 Bad Request` | Request can't be parsed (malformed JSON, missing required body). Almost always emitted by the framework, not by handler code. | `Content-Type: text/plain` for a JSON endpoint |
| `401 Unauthorized` | Caller didn't prove identity. Includes "no cookie," "stale challenge," "failed assertion," "cross-user credential." | `/api/auth/login/complete` with a forged signature |
| `403 Forbidden` | Caller is authenticated but doesn't have permission. Currently unused — visibility checks return 422 with `ledger-not-visible` instead so clients dispatch on `code`, not status. | (none yet) |
| `404 Not Found` | No route mapped at this URL, or framework-level "no such resource at all". Reserved for routing, not visibility. | `/api/nope`, or `/api/auth/dev-login` outside Development |
| `422 Unprocessable Entity` | **Business-rule rejection.** Always carries a `code`. | `setup-username-taken`, `ledger-not-visible`, `login-username-required` |
| `5xx` | Server bug. ProblemDetails includes `traceId`. | unhandled exception |

The `code` values are kebab-case strings, scoped by feature, listed centrally in `src/Api/Errors/BusinessError.Codes`. Adding a new code edits that one file; clients dispatch on the string.

Why 422 over the more granular 400 / 404 / 409: keeps HTTP-layer semantics meaningful (auth-vs-business-vs-transport) while giving clients a single dispatch shape across business rejections. A typed client doesn't need to know whether "ledger not visible" is conceptually "not found" or "forbidden" — just that the code is `ledger-not-visible`.

Endpoint code uses the helper:

```csharp
return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
    "Ledger not found or not visible to this user.");
```

The body emitted:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.21",
  "title": "Unprocessable request",
  "status": 422,
  "detail": "Ledger not found or not visible to this user.",
  "instance": "/api/ledgers/me/last-opened/...",
  "traceId": "00-...-...",
  "code": "ledger-not-visible"
}
```

---

## 6. Documentation

### 6.1 Where things live

| Type | Location |
|---|---|
| Authoritative architecture | [architecture.md](architecture.md) |
| Schema reference | [database-schema.md](database-schema.md) |
| Operational procedures | [operations.md](operations.md) |
| Domain vocabulary | [glossary.md](glossary.md) |
| Decisions with rationale | [decisions/](decisions/) (ADRs) |
| Per-phase implementation notes | top-level [README.md](../README.md) status table; PR descriptions |

### 6.2 ADR format

ADRs live in `docs/decisions/NNNN-kebab-case-title.md` and follow [MADR](https://adr.github.io/madr/):

```
# NNNN — <Title>

* Status: Proposed | Accepted | Superseded by ADR-NNNN | Deprecated
* Date: YYYY-MM-DD

## Context
What problem are we solving? What constraints apply?

## Decision
What did we decide?

## Consequences
What changes because of this — good and bad. What does this make harder later?

## Alternatives considered
Options we evaluated and why we didn't pick them.
```

Write the ADR at the time of the decision, not retroactively. If a decision is superseded later, mark the old ADR `Superseded by ADR-NNNN` rather than editing it.

### 6.3 Updating docs

Documentation updates ship in the same commit/PR as the code change. Stale docs are bugs and treated as such in code review.

---

## 7. Dependencies

- Pin major versions; let minors float within a single major.
- Prefer the standard library over a third-party package for trivial helpers.
- Adding a new top-level dependency requires a one-line justification in the commit message: "why this, why now, why not stdlib".
- Run a vulnerability scan in CI on every push.

---

## 8. Secrets and sensitive data

- No secrets in the repo. `.env` is gitignored; `.env.example` is committed with placeholder values only.
- The Moneydance export (`data/moneydance-export.json`) is **personal financial data** and is gitignored. Test data is synthetic.
- Database passwords, OAuth tokens, and API keys live in environment variables loaded at runtime.
- Logs must not contain raw account numbers, OAuth tokens, or full transaction memos that may include personal info. Redact at the boundary.

---

## 9. CI

CI runs on every push and PR:

1. Validates SQL migrations apply cleanly to a fresh PG 16 container.
2. Runs every script in `db/test/` against the migrated DB.
3. (Phase 3+) builds the .NET solution and runs unit + integration tests.
4. (Phase 4+) builds the frontend and runs Vitest.
5. Checks that documentation cross-references resolve (no broken internal links).

A red CI never merges. If CI is flaky, fix the flake — don't retry.

### 9.1 Sharding: build once, and don't assume a matrix helps

The .NET suite is sharded by namespace, with the filters defined once in
`scripts/ci-dotnet-shards.sh` and never copied into a workflow — a namespace move
would otherwise desync the two silently. The last shard is the COMPLEMENT of the
others, so coverage is provably total and a newly added `Integration/<Folder>` can
never be skipped.

Whether to fan those shards across *processes* or across *jobs* depends on the
executor: one large machine wants a single job that builds once and runs the shards
in parallel across its cores (`ci-dotnet-shards.sh` with no arguments); several
smaller machines want one shard each (`--shard <name>`). Neither is universally
right, so pick from the runner topology rather than by habit.

**Measured dead end — do not revive without new measurement.** A pre-migrated
Postgres image (baking the ~183 migrations into the fixture image) was built and
benchmarked on the theory that per-shard migration bootstrap was costing ~85-90s.
It was not: DbUp against a fresh empty container is **~5s** (DDL on empty tables
is fast). Isolated single-class run came out 12s vs 11s stock — *worse* — and a
full contended preflight measured 328s against a ~325-362s baseline, dead in the
noise. Shards are bound by **test execution** (233-317s each), not bootstrap. The
image was abandoned as zero-gain added maintenance. The original ~85-90s figure
was an assumption that was never measured; measure first.
---

## 10. When in doubt

If a rule above is unclear or seems wrong for a specific case, write down the case, the candidate decision, and the reasoning in an ADR or PR description. Standards evolve; they evolve transparently.
