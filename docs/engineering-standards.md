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
- **One exception: comments.** A shipped migration's comments
  may be rewritten, for example to keep account names and amounts out of the repo's
  prose (§8). DbUp journals by script name, so nothing re-runs. But Postgres keeps
  two kinds of text: `COMMENT ON` text, and a function's body, comments included. An
  edit to either, on an object that is still current, ships with a new migration that
  re-issues it verbatim, so an upgraded install holds exactly what a fresh one does
  (migration 257 is the first). Code is never edited in place, comment or not.
- Migration filenames are zero-padded sequential: `001_extensions.sql`, `002_tables.sql`, …
- Each migration is idempotent where reasonable, but the primary contract is "run once on a fresh DB". **DbUp** is the migration runner: the API applies every file in `db/migrations/` at startup via `MigrationRunner` (`src/Api/Migrations/`), journaled in a DbUp tracking table, using the service-role connection.
- **A data step that must run C# is a journaled C# step**, not SQL: listed in
  `MigrationRunner.CSharpSteps`, journaled under its own name directly after the script
  it belongs to, and deterministic like any migration (`264_stored_leg_notes_backfill` is
  the first). The memo-and-tags rule has no SQL definition, so a change to what it
  stores ships as a new numbered recompute step, which also retires older snapshots
  through the schema version. `StoredNotesRuleVersionTests` fails when the rule's output
  over its perspective matrix changes without one.

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
- **Validation (none left, ADR-0032 §4)** — all migrated to
  API-side enforcement. The last one,
  `trg_account_folders_refuse_cycle` (mig 258, added as a backstop
  under the API's own cycle check), was dropped in migration 261;
  `AccountFoldersRepository.MoveAsync` refuses the cycle under a
  per-section lock.
- **Not yet re-justified** — the two invariant-lockdown guards
  above predate this rule's gates and have never been checked
  against them; the open-work backlog tracks it.

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

1. **An API repository method is one unit of work.** Repositories hold
   no state between calls. When a method runs more than one statement
   that must stand or fall together, it opens the transaction itself,
   unless its caller already holds one, in which case it works in the
   caller's. An endpoint that writes through a repository opens no
   transaction. The endpoints that complete an invite, accept one and
   complete setup write a user's rows (the user, its passkey and
   recovery codes, a ledger grant, the admin flag) on a service
   context directly, with no repository between, and open their own.
   A write that can recompute
   a ledger's balances or holdings opens its transaction through
   `BeginLedgerWriteAsync` (`LedgerWriteLock`,
   [ADR-0112](decisions/0112-one-write-lock-per-ledger.md)), never
   through `BeginTransactionAsync` directly: the helper takes the
   ledger write lock as the transaction's first statement, or takes it
   in the caller's transaction. A save that opens no transaction gets
   the fallback transaction rule 4 describes.

2. **Where the boundaries are.**
   - **The API:** the repository method (rule 1).
   - **The Moneydance importer:** `MoneydanceImportService.ImportAsync`
     wraps every step in a single transaction on its connection, and
     the importer's repositories work in it. If any step fails, the
     entire import rolls back.
   - **Bank feeds and file imports** (`IngestOrchestrator`): each save
     of a run is its own transaction. A save that can recompute takes
     the ledger write lock; the lock is never held across the provider
     fetch or the file parse.
   - **Reads:** a single-statement read uses auto-commit. A read that
     needs one consistent view across several statements (a report,
     the consistency check) opens a `REPEATABLE READ` transaction.

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
   transaction, so a later failure rolls the whole thing back.

   The mechanism is not a trigger: migration 102 dropped the balance family,
   and the only triggers in the schema are the two `txn_headers`
   immutability guards (§3.3). Derived state is recomputed by an EF
   `SaveChangesInterceptor` after the write, inside the same transaction,
   which preserves the same rollback property. That transaction is the
   caller's when it holds one; otherwise `LegNotesRecomputeInterceptor`,
   registered last, opens one for every save and commits it after every
   other interceptor's post-save work, and `AppDbContext` ends it when the
   save throws (a concurrency failure included, which EF reports without
   `SaveChangesFailed`). A save that recomputes balances or holdings runs
   in its writer's own transaction, opened with the ledger write lock
   (`BeginLedgerWriteAsync`, rule 1): the recompute functions refuse a
   transaction that has written without that lock, and the fallback
   transaction does not take it. The fallback serves saves that recompute
   neither.

5. **No DDL inside data-write transactions.** `ALTER TABLE`,
   `DISABLE TRIGGER`, `CREATE INDEX`, etc. inside a write transaction
   is a smell — it usually signals an attempt to bypass schema
   invariants for performance. If a real performance problem exists,
   the right fix is almost always at the application level (batching,
   bulk SQL, query restructure), not at the schema level.

6. **Tests inherit the same discipline.** Every integration test runs on
   a database of its own, cloned from a migrated template and dropped
   after it (§5.2), so the DB's invariants apply during test runs exactly
   as they do on an install. Adding a test that explicitly
   exercises rollback (start a transaction, write rows, throw,
   ROLLBACK, assert zero rows) for any new repository is good
   practice.

7. **Concurrency.** Multi-user shipped in 0.34.0 (ADR-0083): per-ledger
   owner / editor / viewer grants, member management and invite links.

   There are still no optimistic
   concurrency tokens and no conflict-resolution rules; a second editor
   on the same row is last-write-wins. That is now a gap rather than a
   property of the product. Where concurrency IS handled it is handled
   explicitly and per-path: the transaction boundary on a write covers
   its own read-then-write (see the PATCH paths), and long operations
   take a named lock rather than relying on there being one user. Every
   write that can recompute a ledger's balances or holdings, and every
   consistency repair, takes the ledger write lock (`fn_ledger_write_lock`,
   [ADR-0112](decisions/0112-one-write-lock-per-ledger.md)) before it
   writes or locks anything, so writes to one ledger take turns. A write
   that waits past the bound (half its command timeout) is refused as
   `ledger-write-busy` (a 422, an MCP error result, a deferral for a
   scheduled writer) and changes nothing; a repair, which takes the lock
   one piece at a time, keeps the pieces it rewrote before the refusal.

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
- Schema and seed data live in SQL, not in C# fluent migrations. EF Core is for queries, not for schema authority. A data step that must run the C# memo-and-tags rule is a journaled C# step (§3.1), never a SQL copy of the rule.
- **Memo and tags have one writer and one reading.** Every write of a memo, a tag pairing or the originating account goes through the write primitive (`AnnotationPlanner` in `Coffer.Domain.Annotations`, applied in the API by `AnnotationWriter`); `AnnotationWriteCatalogTests` fails on a writer outside it. Every reader of what a register shows reads the stored memo-and-tags presentation (`notes_*`, `txn_leg_note_tags`), never the raw inputs; `AnnotationReaderCatalogTests` fails on a read of an input outside its allow-list.

### 4.2.1 Data-access split: EF Core vs. Dapper

Per [decisions/0005-dapper-and-efcore.md](decisions/0005-dapper-and-efcore.md), as realigned:

| Layer | Default | Notes |
|---|---|---|
| **API** (`src/Api/`) | **EF Core via `AppDbContext`** | Routine CRUD, transactional inserts, view-backed reads, `ExecuteUpdate`/`ExecuteDelete` for set-based mutations. The Dapper package is not referenced by the API at all. |
| **Register query** (PR 3.7+) | **EF Core LINQ over `resolved_transactions`** (`RegisterQuery`; the Postgres function it replaced, `register_entry_keys`, was dropped by migration 266) | Keyset paging is the one place ADR-0005 originally carved out for Dapper. It is LINQ rather than raw SQL in C#, so the API stays on one ORM; the keyset is a row-value comparison EF translates. The cursor is `(posted_at, header_seq, entry_key)` — `seq` replaced `created_at` in migration 097, and the entry key is the stored grouping (the header on the side it belongs to, else the leg). |
| **Importer** (`src/Importer.Moneydance/`) | **Dapper** | Bulk-insert patterns (a whole export's rows in a single transaction), `unnest(@arr1, @arr2)` array-parameter inserts, deferred constraint timing — these are the genuine Dapper sweet spots. The importer never adopted EF and won't until there's a concrete reason to. It plans memo and tags through the same write primitive as the API (`AnnotationPlanner`, via `MoneydanceNotes`), and the API fills the stored memo-and-tags presentation inside the import's own transaction (`ImportJobRunner`'s before-commit step runs `LegNotes.RecomputeLedgerOnAsync`, first of the writes it makes inside the import's transaction). |
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
- **A `SECURITY DEFINER` function is a declared exception**, never a convenience, and each one checks access itself. `saved_reports_repoint_tag` (migration 263) is one: a tag merge has to repoint every member's saved reports, which RLS keeps owner-only, so the function rewrites only the tag reference inside `saved_reports.spec`, after checking that the caller holds an owner or editor grant on the ledger. That makes it a second place that checks ledger access, an accepted risk, so it is tested with a second ledger and a non-member caller (`SavedReportsRepointTagTests`).
- **A `BYPASSRLS` path never resolves a ledger by name.** The Moneydance import runs on the service role, where a lookup by name would find other users' ledgers; it only ever creates a new ledger, owned by the importing user.

When in doubt: if the operation is "the authenticated user doing a thing in their own data," use `AppDbContext` and trust RLS. If the operation crosses the authentication boundary (resolves who the user is, mints/burns auth state, creates a ledger as a unit, applies a DDL migration), use `ServiceDbContextFactory`.

### 4.3 TypeScript / React conventions

- Strict TS (`"strict": true`). Plus `noUnusedLocals`, `noUnusedParameters`, `noUncheckedSideEffectImports` per `src/Web/tsconfig.app.json`.
- Functional components only.
- TanStack Query for server state; Zustand or React Context for local UI state. No Redux.
- TanStack Router with **code-based routing** (not file-based) — the route tree in `src/router.ts` reads top-to-bottom with no codegen step.
- Tailwind **v4** + hand-built primitives following shadcn/ui conventions (we don't pull shadcn via its CLI; we write the source files we need ourselves). No CSS-in-JS. v4 is CSS-first: there is no `tailwind.config.*` and no PostCSS config — the design tokens live in an `@theme` block in `src/index.css`, and the Vite plugin does the rest.
- Prefer composition over hooks-with-side-effects. No "magic" hooks that secretly mutate global state.
- `@simplewebauthn/browser` is the WebAuthn client. Don't improvise base64url encoding or `navigator.credentials` invocation.

### 4.3.1 Frontend engineering posture

The frontend has to stay maintainable by someone who is not a React specialist. The posture: **conservative, grounded, security-first, no shortcuts**.

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
├── index.html              Vite entry
├── vitest.setup.ts         Extends expect with @testing-library/jest-dom; fails a test that sends a request whose path has an empty segment (an empty id), when the request goes through the API client's request functions (whatever fetch the test installed) or reaches the environment's fetch or the shared fetch stub
└── src/
    ├── main.tsx            QueryClientProvider + RouterProvider mount
    ├── App.tsx             Pure components only (RootLayout, AuthedOutlet)
    ├── router.ts           Route tree (code-based) + auth-check beforeLoad
    ├── index.css           The @theme token block (Tailwind v4 is CSS-first) + the
    │                       four [data-theme] blocks
    ├── lib/                ~32 modules. The ones worth knowing by name:
    │   ├── api.ts          Barrel re-export; the per-domain clients live in lib/api/
    │   ├── api/_request.ts Typed fetch wrapper + ApiError + ProblemDetails decode
    │   ├── auth.ts         WebAuthn login ceremony via @simplewebauthn/browser
    │   ├── cn.ts           clsx + tailwind-merge helper
    │   ├── theme.ts        Theme + accent selection, persisted per device
    │   ├── types/          Shared API response types, split per domain
    │   └── useWindowedRegister.ts   The register's bidirectional page window
    ├── components/ui/      ~25 hand-built primitives — Button, Input, Label,
    │                       Typeahead, ContextMenu, ConfirmDialog, Modal,
    │                       StatusBadge, Skeleton, Chip, Panel, Toolbar, …
    └── routes/             One folder per route, route component lives here
```

The counts are deliberately approximate and the lists deliberately partial — a
file-by-file tree in prose is a thing that goes stale between the PR that adds a
file and the next person to read it. What must stay true is the SHAPE: primitives
in `components/ui/`, per-domain clients under `lib/api/`, one folder per route.

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

### 5.0 A new assertion is mutation-checked before it counts

**Break the code the assertion is about, confirm the test fails, put it back.** An
assertion nobody has seen fail is a claim, not a check — and the failure mode is silent
by construction: the suite goes green either way, which is the one defect a test suite
cannot find.

The shapes it catches: a component test asserting that two strings are absent, run on
a frame where the component short-circuited and the DOM is empty, passes whether or
not the feature works; a test asserting that admin-only tabs are hidden from
non-admins, querying before any tab renders, passes whether or not the tabs are gated;
a repository test that calls `Revoke` twice and asserts nothing passes whatever
`Revoke` does. Each reads as a normal, well-named test.

Scoped deliberately, or it becomes ceremony nobody does:

- **Assertions on NEW behaviour**, not every test a change happens to touch.
- **Negative assertions always** — `Assert.Null`, `Assert.Empty`, `DoesNotContain`,
  "expect 204", `queryBy*`. A negative assertion is true of an empty world, so it
  passes whenever the code under test never ran. Every shape above is one.
- **Not required where the test already failed first** and was made to pass. That is
  the same evidence arrived at from the other side.
- Name the mutation in the PR body — "dropping the account filter deletes every member
  of the group, caught by X". A mutation the test does NOT catch is the finding.

**The React half is enforced**, not merely stated: `local/no-unanchored-negative-assertion`
(`src/Web/eslint-rules/`) fails the build on a test block whose every assertion is
satisfied by an empty render. The predicate is exact, so there is nothing to suppress.

**The .NET half stays a rule rather than a gate**, because "would this pass against
unmodified code" is undecidable in general and a mechanical approximation produces far
more noise than signal — a gate that cries wolf is one someone turns off.

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
    │   ├── PostgresFixture.cs               ← Testcontainers + migrated template
    │   ├── DatabaseTest.cs                  ← base class: a database per test (§5.2)
    │   ├── TestDatabase.cs                  ← one test's database and its handles
    │   ├── ApiFactory.cs                    ← WebApplicationFactory<Program>
    │   └── SyntheticLedger.cs               ← per-test atomic builder
    ├── Auth/
    │   ├── BootstrapTokenServiceTests.cs    ← DB-touching service
    │   ├── UsersRepositoryTests.cs          ← Dapper CRUD round-trips
    │   └── CredentialsRepositoryTests.cs
    └── HealthEndpointsTests.cs              ← WebApplicationFactory
```

### 5.2 Every integration test gets a database of its own

Tests that share a database see whatever earlier tests left in it, so a result depends on what ran before, and a per-test timing measures the whole run's rows. Unique ids per test separate rows logically; they do not isolate global tables, planner statistics, or anything a test counts. So **every test that touches the database runs on its own database**: a clone of a template, created before the test and dropped after it. The one exception is a class fixture that holds one database for every test of its class (`PostgresFixture.CreateDatabaseAsync(owner, log)`), used where building the state is a cost the whole class shares: `FullDemoTests` adds the whole Demo once and every fact reads it (ADR-0115 D8). Its database gets the same end checks when the class ends, and no test of that class may also be a `DatabaseTest`, whose end would find the fixture's sessions in a database not its own.

**How.** A test class derives from `DatabaseTest` (`tests/Api.Tests/Integration/Infra/DatabaseTest.cs`; the importer test project has its own, over the same linked machinery) and reaches its database through `Db`: connection strings for the three roles, contexts and factories, all pointing at that one database (the importer's `Db` is `coffer_service` only, the role production's import writes as). Setup that needs it goes in `OnInitializedAsync`, since a constructor runs before it exists. The fixture exposes no connection string; a test that points one of its own at another database is caught by the end-of-test check.

**The template**, built once per test process, the same way in both test projects: roles provisioned cluster-wide as `db/init` does, the template owned by the fixture's own superuser role so no role a test runs as can unlock it, migrated as `coffer_service` by the production runner, first-boot writes settled through the app's own code (API project: the OAuth key pair, the backup-age monitor's first alert) so a host booted on a clone writes nothing at boot, ANALYZEd, then locked (`IS_TEMPLATE`, no connections). A clone starts with exactly its rows and statistics. The backup alert throttles the monitor for `BackupAgeMonitor.RepeatAfter` from when it was written, so a clone is refused, with a message saying so, once the process is within an hour of that bound.

**What a test's end checks**, failing the test (`TestDatabaseCluster.EndTestAsync`): a session left in a database that is not the test's own; a session in its own still running a statement or holding a transaction; once every pool in the process is cleared, any session left in the maintenance database (a connection never returned, which could hold an advisory lock or a LISTEN into the next test); a connection into an earlier test's database after that test released it; a large object in the maintenance database; a database outside the allowlist (the server's own, the template, running and pending per-test databases), whatever its name; a change to the two roles (attributes, memberships, settings) or to the databases no test owns (owner, flags, ACL); a failed drop of an earlier test's database. The fixture's own sessions run as a role whose password only the cluster holds, and are the only ones the checks leave out. The drop runs in the background; at the end of the run the fixture waits for every drop, repeats the checks, compares the template's content through a fresh clone, and fails the run (a collection cleanup failure, which makes `dotnet test` exit non-zero) on any of them. A test whose `OnInitializedAsync` throws has its database ended and released before the exception propagates, since xunit 2 skips `DisposeAsync` after a failed `InitializeAsync`. Each test's output says `db <name> clone <ms>`.

**The stored notes, before that.** `DatabaseTest`'s teardown, in both test projects, first runs the stored-equals-computed check over the test's database (`StoredNotesDatabaseCheck`): every leg's stored memo-and-tags presentation must be what the rule computes from its current rows. A test that seeds or changes an input of the rule past every writer ends with the backfill (`SyntheticLedger.RecomputeAllNotesAsync`, `LegNotesBackfill.RunAsync`) or fails here, by name; one that makes the stored values wrong on purpose puts them right before it ends.

**A second database.** `Db.CreateSiblingAsync()` is another clone of the template; `Db.EmptyDatabaseConnectionString(suffix)` is one with no schema, the state a fresh install boots into (upgrade and migration tests). Both are named under the test's database and dropped with it.

**What is still shared** between the tests of one process, which run one at a time in one collection:

- the container, and the roles with their cluster-wide settings (`RoleSettingsMirrorInitScriptTests` holds them to `db/init/00-init-roles.sh`);
- process environment variables (`ApiFactory` rewrites the ones `Program.cs` reads at host build);
- temp and bin-directory files (backup and restore-staging directories: give a test its own);
- anything a test changes cluster-wide, which it must put back in a `finally` — `AdminRestoreEndpointTests` toggles `coffer_service`'s `CREATEDB`.

Nothing a test could share may live in the maintenance database or in `template1`: `coffer_app` can connect to neither; `coffer_service`, which the migration runner connects to `postgres` with, can create nothing there (no `CREATE` on `public` by the server's default, no `TEMPORARY`, no large objects), and cannot connect to `template1`. Empty databases are created from `template0`, owned by `coffer_service` as a fresh install's is. Tests that run the migration runner hand it an unpooled connection string (`TestDatabaseCluster.ForMigrationRunner`): its database-exists check pools in a place `ClearAllPools` does not reach, and that idle connection would read as a leak.

So a test never truncates or deletes rows to protect itself from other tests. It deletes rows only when its own scenario needs them gone — for example the OAuth key pair or the first backup alert the template carries.

**Synthetic data still.** `SyntheticLedger.CreateAsync` mints a ledger, a user with a random username and the owner grants in one transaction, and tests reach further state through repository methods seeded by those ids, so a test names the state it exercises rather than leaning on whatever the template holds. Importing a large real-world Moneydance file in tests is forbidden: it is slow, non-deterministic, and buries what the test exercises. A test that needs the same ledger across phases reuses the returned `SyntheticLedger`; a test that needs two ledgers (cross-ledger isolation) calls the builder twice.

---

### 5.2.1 Financial paths test a `{ typical, boundary }` matrix

Tests seeded with small round data — a three-digit amount, a handful of whole shares — never approach the magnitudes where money math breaks. Code that is wrong at scale passes every such test, because none asks a question big enough to get the wrong answer.

So **a test touching money, a quantity, or a `decimal` materialisation path is a `[Theory]` over `Boundary.Positions`, not a `[Fact]` at a comfortable size**:

```csharp
[Theory]
[MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
public async Task Something_holds(Boundary.Position p) { ... }
```

`tests/Api.Tests/Integration/Infra/Boundary.cs` is the single source of truth for the magnitudes, and every value in it is tied to a **column precision verified against the migration that set it** — never a round number chosen for looking large. `SyntheticLedger.AddBoundaryPositionAsync` seeds a position at a given magnitude in one call (holdings row, buy, and recompute together — doing only some of the three silently leaves the projection empty).

Why fractional quantities specifically: a `decimal` product keeps the **sum of its operands' scales**, so a `NUMERIC(25,12)` quantity times a `NUMERIC(19,4)` price runs to 16 decimal places. Whole shares divide evenly and never grow the scale, so a whole-share fixture cannot reach the failure: on one, the net-worth cross-check between the overview and the history series passes even where the two routes would disagree at a fractional position.

A value that lands exactly on a midpoint is the other case round data never reaches. `decimal.Round` rounds a half to even by default and Postgres `ROUND` rounds it away from zero, so a figure computed in C# and the same figure computed in SQL differ by one unit in the last place there. C# rounding that must agree with SQL passes `MidpointRounding.AwayFromZero` (`ReportingScale.MarketValue`), and `NetWorthReconciliationTests` seeds a position whose market value is a midpoint.

Money also has a currency dimension. Every money figure is held at its own currency's ISO 4217 minor units ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15), so a test of a path that validates, rounds, stores or formats a money figure covers each case a 2-place fixture cannot reach:

- **0, 2 and 3 places**: a currency of each, with an amount at exactly its places, one with a digit more (refused when typed; rounded half away from zero when computed or ingested, except the loan payment, which rounds up to one minor unit), and, for the 3-place currency, an amount with three decimals that a 2-place assumption would round away;
- **the refused codes**: a 4-place fund unit (`CLF`, `UYW`) and a code that is not an ISO 4217 currency with an exponent, each refused as an account's and as a ledger's currency;
- **a grandfathered value**: a stored amount that does not fit its currency, which is kept, passes an edit that leaves it unchanged, and is listed by the consistency check, as an advisory without counting in its verdict (settled by Claude, 2026-11-16); and a stored account or ledger code that would now be refused for any reason (a 4-place fund unit, an "N.A." entry or a code that is not an ISO 4217 currency with an exponent), which keeps working at 2 places, also in the SQL minor-units function, and is listed too (settled by Claude, 2026-11-16).

The places come from the checked-in ISO 4217 data file the API and the SPA share (ADR-0114 D15). Prices, share quantities and exchange rates are not money and keep the magnitudes above.

The complement is the schema-drift guards (`SchemaDriftGuardTests`): those catch the *column* side, boundary data catches the *code-path* side.

---

### 5.2.2 A disposed test host is garbage

Every endpoint test boots its own host through `ApiFactory`, so nothing process-wide may hold a host once the test has disposed it. A host that is held is kept for the rest of the run: memory grows by a host per test, and every full collection walks the pile, so later tests run slower than they do alone. A production process builds one host and keeps it for its life, so the same holder costs production nothing, and only the suite shows it.

The holders are things a host fills or starts and the framework or a library never empties or stops: ASP.NET Core's rate-limiting middleware never disposes its endpoint limiter, whose replenishment timer then holds the pipeline; the HTTP client factory's handler-expiry and cleanup timers call back into the factory, which holds the host's services until its handlers expire and are cleaned up, minutes after the host is disposed; and Microsoft.Extensions.AI caches each MCP tool's descriptor under the MCP SDK's process-wide serializer options, with an entry key that captures the host's services. A holder that lets go after minutes still counts: the suite then keeps the hosts of the last few minutes, and every test runs slower for them. So:

- **The fix for a holder only the suite shows lives in the test host, not in production code.** Production rate limiting, authentication, authorization and session code is never changed or replaced to fix a test problem. `ApiFactory` releases the holders when it disposes a host: it disposes the framework rate limiters it finds in the host's pipeline (`PipelineRateLimiters`), stops the timers of the host's HTTP client factory (`HttpClientFactoryTimers`), and drops the tool descriptors cached under the SDK's default options (`McpToolDescriptorCache`). Each reads framework or library internals and fails, rather than do nothing, when those move. The test host keeps production's configuration, such as the handler lifetime; the release happens after the host is disposed.
- A timer, limiter or background loop the app itself starts is owned by the container (a singleton disposed with it) or stopped by the host's shutdown.
- A test disposes every `ApiFactory` it builds, including one a setup helper returns: a host that is never disposed keeps its process-wide registrations (its tracing listener among them) for the rest of the run.

`DisposedHostTests` boots and disposes hosts, with and without MCP, and fails if one is still reachable after a full collection. At the end of the run `PostgresFixture` fails the run if any `ApiFactory` was never disposed, naming the tests that built them.

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
| Open work, ranked | the open-work backlog — maintainer-only, excluded from the public mirror |
| Closed work, with what was learned | the closed-work log |
| Upgrade + migration procedure | [upgrading.md](upgrading.md) |
| These standards | [engineering-standards.md](engineering-standards.md) |

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
- **Vulnerability scanning is Dependabot, not a CI job.** No workflow runs a
  vulnerability scan on push. `.github/dependabot.yml` opens PRs on its own schedule, and
  `dotnet restore --force` re-runs the NuGet audit (which is why `Api.Tests.csproj`
  carries pinned transitive versions with a comment each). If you want a push gate,
  that is work to do rather than a rule to cite.
- **A package that is referenced but never called is a dependency too.** Check
  before adding, and check when removing or replacing a subsystem: the package it
  used can stay referenced with no call sites.

---

## 8. Secrets and sensitive data

- No secrets in the repo. `.env` is gitignored; `.env.example` is committed with placeholder values only.
- A Moneydance export is **personal financial data**: anything placed under `data/` as `*.json`, `*.moneydance`, `*.csv`, `*.ofx`, `*.qif` or `*.qfx` is gitignored, and only the synthetic samples in `data/samples/` are tracked. Test data is synthetic.
- **No account names and no amounts in prose, real or invented.** Docs and code comments
  name no account, security, payee, institution, plan or place, and state no figure; a
  case is described by its shape ("a basis with more digits than the column holds", "the brokerage"), and
  a test's arithmetic lives in its code, not restated in a comment. Fixtures use generic
  names (Brokerage, Fund A, Payee A) and invented amounts, never values copied from a
  ledger, a feed, an export or a screenshot. "Real or not" is the point: nobody can keep
  a list of what is real, so the rule forbids the shape, and a preflight gate checks it.
  The one institution the product names is an import format: the Fidelity CSV.
- **Nothing derived from a real dataset in prose, approximate included.** No count,
  magnitude, proportion or observation attributed to an export or ledger someone
  actually keeps: state the rule ("MD keeps the bank's original string beside the
  cleaned one") and justify a priority by what breaks and for whom. The same gate
  checks this by shape: real-data provenance in a sentence with a magnitude or
  proportion, or as the thing an observation was made on or the subject of an
  observation verb.
- **State the issue and how it is handled; never how, when, where or by whom it was
  found.** Prose describes the mechanism in the present tense (what breaks, for whom,
  under what condition) and then the fix, rule or test that deals with it. It tells no
  story: not how a defect was noticed, when or where it happened, who reported it, how
  long it went on, or what it cost before it was fixed. A story adds history, not
  reasoning, and history about an install or the data in it must not publish. So an
  incident is written as its condition ("a cache can serve a stale row when ..."), a
  discovery as the test that pins it, and an ADR's Context as a
  problem statement rather than a timeline. A size or shape that an incident would
  imply is stated generically ("any ledger past one chunk"). Two things are not
  stories: a decision date in an ADR's status line or amendment heading, which is
  metadata, and the release in which a behaviour changed, stated as a fact ("since
  0.36.8 the timeout is 600s"). The same gate checks the strongest markers of a story
  by shape.
- Database passwords, OAuth tokens, and API keys live in environment variables loaded at runtime.
- Logs must not contain raw account numbers, OAuth tokens, or full transaction memos that may include personal info. Redact at the boundary.

---

## 9. CI

**CI runs on PULL REQUESTS, not on push.** `ci.yml` has no `push: main` trigger,
and says why in a comment: a PR's run already validated that exact tree, so
re-running on merge doubles the work — **the local preflight is the gate for a
direct push to main**. It also carries `paths-ignore` for `**.md`, `docs/**` and
`mockups/**`, so a docs-only change skips the heavy workflow entirely and
`doc-link.yml` covers it instead.

Only `secret-gate.yml` runs on both push and PR, because key material reaching
main is not something a skipped workflow may miss.

Four jobs in `ci.yml`:

1. **`schema-and-trigger`** — validates SQL migrations apply cleanly to a fresh
   PG 16 container, then runs every script in `db/test/` against the migrated DB.
2. **`api-no-raw-sql-audit`** — runs `scripts/audit-no-raw-sql.sh`, which fails on
   unsanctioned raw SQL in `src/Api/`. The data-access rules in §4 are enforced,
   not merely written down.
3. **`dotnet-test`** — builds the solution and runs the suite, sharded (see §9.1).
4. **`web-build-test`** — builds the frontend and runs Vitest.

Plus two workflows of their own:

5. **`doc-link.yml`** — checks documentation cross-references resolve. It is a LINK
   checker and nothing more: it cannot tell you a document has become false, which
   is why §6 puts that burden on the author.
6. **`secret-gate.yml`** — the key-material and identity deny-list, so a key or a
   personal identifier cannot reach a public mirror.

There is **no vulnerability-scan job**: dependency alerts come from Dependabot on its
own schedule, not from a gate on push.

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

**Rejected: a pre-migrated Postgres image** (the migrations baked into the fixture
image). It saves only DbUp's run against a fresh empty container, which is short
because DDL on empty tables is fast, while shards are bound by **test execution**, not
bootstrap; the saving falls inside run-to-run noise, and the image would add
maintenance for no gain. Revisit only with a measurement showing bootstrap dominates a
shard. Measure a bootstrap cost before optimizing it.

---

## 10. When in doubt

If a rule above is unclear or seems wrong for a specific case, write down the case, the candidate decision, and the reasoning in an ADR or PR description. Standards evolve; they evolve transparently.
