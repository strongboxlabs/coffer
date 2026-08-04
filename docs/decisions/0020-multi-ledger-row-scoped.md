# 0020 — Multi-ledger support: row-level `ledger_id` scoping with RLS

* Status: Accepted (Phase A done in PR 2.x; Phase D / RLS turn-on done in PR 3.8 on 2026-05-10)
* Date: 2026-05-09
* Affects: a small set of "anchor" tables (accounts, securities, feed_connections, tags, merge_rules, transaction_rules); Phase 3 auth (ADR-0013); the API surface; the importer CLI

## Context

Phase 1 + Phase 2 ship a single-ledger schema: there's one logical
"book" of accounts, transactions, holdings, and so on, addressed
implicitly by every query. Real usage needs more than one — a personal
ledger, a household ledger, a side-business ledger, etc. — and each
ledger must be scoped to the users granted access to it. The auth ADR
(ADR-0013) lands users + passkeys in Phase 3 but says nothing about
which data each user can see.

Three relational shapes were considered:

| Shape | What it looks like | Why not (for Coffer) |
|---|---|---|
| **Database-per-ledger** | Separate Postgres database per ledger | N migrations, N backups, N connection pools; cross-ledger reports impossible. Strongest isolation, but Coffer is a single-Postgres self-hosted app, not a multi-tenant SaaS. Overkill. |
| **Schema-per-ledger** | One DB, N schemas, `search_path` per session | Migrations apply N times; tooling has to enumerate schemas; index/extension state diverges over time. Operationally costly. |
| **Row-level (`ledger_id` on anchor tables; derived rows scope via FK chain)** | One DB, one schema; the few tables with no transitive FK to accounts carry a `ledger_id`; everything else (transactions, holdings, lots, …) inherits its ledger via the FK it already has. RLS policies on derived tables read from the anchor. | Queryable, single migration target, single backup. Defense-in-depth via Postgres RLS. Single source of truth: no cross-row consistency invariant, no trigger to keep `transaction.ledger_id` in sync with `accounts.ledger_id` (because the transaction has no copy of its own). |

The user count and ledger count are both expected to be small (≤10 of
each). Row-level scoping is the right shape; placing the column only
on the anchor tables keeps the schema delta small and removes the
"two copies of the same fact" problem the redundant-column variant
introduces.

## Decision

### Rule 1 — `ledgers` table + `ledger_id` on the *anchor* tables only

A new `ledgers` table holds the set of distinct books. The column is
*not* sprinkled across every business table — only the tables that
don't transitively reach an existing ledger-bearing FK. Everything
else derives its ledger membership through the FK chain it already
has.

```sql
CREATE TABLE ledgers (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT         NOT NULL,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);
```

**Anchor tables** (`ledger_id UUID NOT NULL REFERENCES ledgers(id) ON
DELETE RESTRICT`):

| Table | Why it's an anchor |
|---|---|
| `accounts` | The primary anchor; every transactional row reaches it via FK. |
| `securities` | No FK to accounts — securities exist independently of any account. Two ledgers could legitimately have their own IDXB rows with separate cost-basis math. |
| `feed_connections` | No FK to accounts — accounts FK *to* feed_connections. SimpleFIN credentials are per-ledger. |
| `tags` | Global by name within a scope; a "vacation" tag in ledger A must not collide with one in ledger B. |
| `merge_rules` | Single-row config table per-ledger (different fuzzy thresholds per book). PK changes to `(ledger_id, ...)`. |
| `transaction_rules` | `apply_account_id` is *optional* (rules can match without setting an account). When it's NULL the row has no FK chain to a ledger, so the column is needed. |

**Derived tables** (no `ledger_id` column; ledger membership follows
existing FKs):

| Table | Reaches ledger via |
|---|---|
| `transactions` | `account_id → accounts.ledger_id` |
| `transaction_overrides` | `transaction_id → transactions → accounts.ledger_id` |
| `holdings` | `account_id → accounts.ledger_id` |
| `lots` | `holding_id → holdings → accounts.ledger_id` |
| `security_prices` | `security_id → securities.ledger_id` |
| `sync_runs` | `feed_connection_id → feed_connections.ledger_id` |
| `merge_candidates` | `incoming_txn_id → transactions → accounts.ledger_id` |
| `pending_transactions` | `account_id → accounts.ledger_id` |
| `recurring_transactions` | `source_account_id → accounts.ledger_id` |
| `transaction_tags` | `transaction_id → transactions → accounts.ledger_id` (and `tag_id → tags.ledger_id`) |

Six tables get the column instead of sixteen. The single source of
truth for "which ledger does this transaction belong to" is its
account; no cross-row consistency invariant has to be enforced by
trigger because there's no second copy that could disagree.

`ON DELETE RESTRICT` on the anchor tables is intentional: deleting a
ledger deletes its data via an explicit multi-step flow, never
implicitly through cascade.

### Rule 2 — Per-ledger uniqueness on the anchor tables

Idempotent-import indexes change shape only on the anchors that
currently key off `external_id`:

```sql
-- before
CREATE UNIQUE INDEX uq_accounts_external_id
    ON accounts(external_id) WHERE external_id IS NOT NULL;

-- after
CREATE UNIQUE INDEX uq_accounts_external_id_per_ledger
    ON accounts(ledger_id, external_id) WHERE external_id IS NOT NULL;
```

Same shape change for `securities(ledger_id, external_id)` and
`tags(ledger_id, name)`. Indexes on derived tables — e.g.
`transactions(account_id, external_id)`, `holdings(account_id,
security_id)` — stay as-is, because `account_id` already implies a
ledger (any duplicate would be on the same ledger, which is the
correct collision). The `pending_transactions(account_id,
external_pending_id)` and `security_prices(security_id, price_date)`
indexes stay similarly.

### Rule 3 — Auth model: per-user grants per ledger

Phase 3 introduces `users` (passkeys per ADR-0013). Multi-ledger adds:

```sql
CREATE TABLE user_ledger_grants (
    user_id    UUID  NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    ledger_id  UUID  NOT NULL REFERENCES ledgers(id) ON DELETE CASCADE,
    role       TEXT  NOT NULL CHECK (role IN ('owner', 'editor', 'viewer')),
    granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, ledger_id)
);
```

| Role | Read | Write transactions/accounts | Manage feeds/rules | Add users to ledger | Delete ledger |
|---|---|---|---|---|---|
| `owner` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `editor` | ✓ | ✓ | ✓ | ✗ | ✗ |
| `viewer` | ✓ | ✗ | ✗ | ✗ | ✗ |

A ledger must have ≥1 owner. Enforced by a constraint trigger on
`user_ledger_grants` that fires on DELETE/UPDATE OF `role` and rejects
the change if it would leave the ledger ownerless.

### Rule 4 — Postgres RLS as defense in depth

Every business table gets an RLS policy. Anchor tables match `ledger_id`
directly; derived tables use a subquery against the FK chain. The
application sets `app.user_id` per request (`SET LOCAL app.user_id =
'<uuid>'` inside the per-request transaction); the predicates read it.

```sql
-- Anchor (direct predicate)
ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
CREATE POLICY accounts_per_user ON accounts
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_setting('app.user_id', true)::uuid
        )
    );

-- Derived (subquery via the existing FK)
ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;
CREATE POLICY transactions_per_user ON transactions
    USING (
        account_id IN (
            SELECT id FROM accounts
            -- accounts policy itself filters this list
        )
    );
```

The derived-table policy reads from `accounts`, which has its own RLS
policy enabled — Postgres composes them, so the user only sees
transactions whose accounts they can see. No `ledger_id` predicate on
`transactions` is needed; the policy's subquery uses the existing
`account_id` index, and the planner hoists the `accounts` membership
list per session because `app.user_id` is stable inside a request.

App-layer filters stay (defence in depth, friendlier errors) — RLS is
the safety net that catches every missed scope predicate. The importer
and the SimpleFIN sync worker bypass RLS via a service role with the
Postgres `BYPASSRLS` attribute.

### Rule 5 — API surface: ledger id in the path

Every read/write endpoint scopes to a ledger via the URL:

```
GET    /api/ledgers/{ledgerId}/transactions
POST   /api/ledgers/{ledgerId}/transactions
GET    /api/ledgers/{ledgerId}/accounts
POST   /api/ledgers
GET    /api/ledgers                   ← lists ledgers the user can see
```

Path-based over session-based because it's explicit (URLs are
copy-paste-friendly between users with overlapping access), self-
documenting in logs, and impossible to confuse client-side. The active-
ledger UX (a picker in the header) is purely client state — the URL is
the source of truth.

### Rule 6 — Importer takes a `--ledger` argument

```
coffer-import-moneydance import data/export.json --ledger <id>
coffer-import-moneydance import data/export.json --ledger-name "Personal"
```

The first form imports into an existing ledger; the second creates the
ledger inline (convenience for the first run). Re-runs of `--ledger
<id>` are still idempotent — every existing upsert key now reads
`(ledger_id, …)` so the same MD source doesn't collide across ledgers.

## Phased migration plan

This is a big DDL change but the work is mechanical. The phased plan
keeps the existing single-ledger DB usable throughout:

**Phase A — schema (one migration, no app code changes)**
1. `CREATE TABLE ledgers`; insert one default row (id captured in the
   migration's output).
2. For each *anchor* table (`accounts`, `securities`,
   `feed_connections`, `tags`, `merge_rules`, `transaction_rules`):
   `ALTER TABLE … ADD COLUMN ledger_id UUID`.
3. Backfill the six tables: `UPDATE … SET ledger_id = '<default-ledger-id>'`.
4. `ALTER TABLE … ALTER COLUMN ledger_id SET NOT NULL`.
5. `ALTER TABLE … ADD CONSTRAINT … FOREIGN KEY (ledger_id) REFERENCES
   ledgers(id) ON DELETE RESTRICT`.
6. Drop and recreate the per-ledger unique indexes on the three
   anchors that have idempotent-import keys (rule 2):
   `accounts.external_id`, `securities.external_id`, `tags.name`.
   Change `merge_rules` PK to include `ledger_id`.
7. RLS policies — written but disabled initially (no `users` table
   yet).

Derived tables get nothing in Phase A. Their existing FKs already
encode ledger membership; once the anchors carry `ledger_id`, every
row in the database has an unambiguous ledger via FK chain.

After Phase A the DB is in a multi-ledger shape with one ledger.
Single-ledger app code keeps working because every query implicitly
hits the one row that matches.

**Phase B — repository + pipeline updates (no schema change)**
1. Every Dapper repository that writes to an anchor table accepts a
   `ledgerId` and stamps it on insert.
2. Repositories that write to derived tables don't need `ledgerId` —
   the FK chain already binds them. (E.g. inserting a transaction
   only needs the account_id; the account already carries the ledger.)
3. Read queries against derived tables stay shape-identical at the
   SQL level; ledger scoping rides on the RLS policy + the
   account/security/feed-connection lookups the queries already do.
4. The importer CLI gains `--ledger` / `--ledger-name`; the
   `AccountImportStep`, `SecurityImportStep`, and SimpleFIN sync
   worker stamp `ledger_id` on every anchor row they create.

**Phase C — auth integration (lands with Phase 3)**
1. `users`, `user_ledger_grants`, the constraint trigger for ≥1 owner.
2. API request pipeline sets `app.user_id` from the authenticated
   session.
3. Service role for the importer + sync worker (BYPASSRLS).

**Phase D — RLS enforcement**
1. Enable RLS on every business table; policies from rule 4.
2. App-layer query filters stay (now redundant with RLS, but the
   friendlier error path is worth it).

**Phase E — UI**
1. Ledger picker in the app header.
2. Ledger management screen (create / rename / share / delete).
3. Cross-ledger views (if/when needed) live behind explicit endpoints
   that check the user has access to all involved ledgers.

Phases A + B can land before Phase 3 begins — they don't depend on
auth — and unblock multi-ledger imports for the user immediately.
Phases C + D land with Phase 3.

## Consequences

**Positive**
- Cross-ledger leakage is structurally impossible (RLS) plus
  app-layer-explicit (anchor queries carry the predicate; derived
  queries inherit it through the FK chain that already exists).
- Single source of truth for "which ledger does this row belong to."
  No cross-row consistency invariant to enforce by trigger — the
  derived tables can't disagree with the anchor because they have no
  copy.
- Single Postgres, single migration target, single backup. Adding a
  ledger is an `INSERT` + a CLI command, not a deploy.
- Existing data migrates to "the default ledger" and stays valid; no
  cutover.
- Schema delta is small: six tables gain a column, not sixteen.
  Smaller per-row footprint on the high-cardinality tables
  (`transactions`, `lots`, `holdings`).

**Negative**
- RLS policies on derived tables are subqueries against the anchor.
  Postgres flattens these well using existing FK indexes, but a few
  hot-path query plans deserve `EXPLAIN` checks once Phase D lands.
- Cross-ledger reports become structurally awkward — a query that
  needs to span ledgers has to UNION across explicit ledger ids it
  fetches from `user_ledger_grants`. We don't expect to need this in
  the foreseeable future, but documenting the rough shape of the
  query is cheap.
- `BYPASSRLS` is a footgun if granted too liberally. Restrict to the
  importer + sync worker service accounts the operator creates
  explicitly.

## Alternatives considered

- **`ledger_id` on every business table (the original draft).** The
  redundant column gives every RLS policy a direct predicate instead
  of a subquery, but introduces a cross-row consistency invariant:
  `transaction.ledger_id` must equal `accounts.ledger_id` for the
  transaction's account, enforced by trigger. Two places for the same
  fact is two places for the fact to drift. Rejected in favour of the
  single-source-of-truth design above.
- **Database-per-ledger.** Strongest isolation, heaviest ops. Fits a
  multi-tenant SaaS, not a self-hosted single-DB app. Rejected.
- **Schema-per-ledger.** Avoids the extra column entirely, but
  multiplies migration/index/extension state across schemas and forces
  every connection to set `search_path`. Rejected.
- **Application-layer scoping only (no RLS).** Simpler, but a single
  missing `WHERE` leaks. The cost of RLS is negligible relative to the
  blast radius of a bug. Rejected.
- **Path-less API (active ledger in session/cookie).** Cleaner URLs,
  but implicit state — copy-pasted links go to the wrong ledger,
  multi-ledger users have to switch contexts before every link works.
  Rejected.

## Addenda

### 2026-06-10 — Composite-FK cross-ledger hardening complete (Phase 2)

The defense-in-depth composite-FK pattern (a child row's
`(parent_id, ledger_id)` references the parent's `(id, ledger_id)`, so
PostgreSQL structurally refuses any reference that crosses ledgers) was
introduced for the investment surface by slice A3 (migration 049). It
has now been applied to every remaining single-column cross-table FK:

- **Phase 1 (mig 049):** holdings, lots, security_prices, txn_legs.
- **Phase 2 (mig 121):** accounts.parent_id, accounts.holdings_account_id,
  accounts.feed_connection_id, txn_headers.is_merged_into,
  recurring_transactions.target_account_id, sync_runs.feed_connection_id,
  sync_run_promotions.header_id (the only CASCADE; the rest are
  SET NULL).

All seven Phase-2 references were verified leak-free before the FKs were
added. The six nullable FKs use PostgreSQL 15+
`ON DELETE SET NULL (<col>)` so a parent delete nulls only the FK column
and never the NOT-NULL `ledger_id`. `users.last_opened_ledger_id` stays
deliberately non-composite — it intentionally points across ledgers
(the user's last-opened ledger among all their grants), so there is no
isolation invariant to enforce there. The full-schema cross-FK audit is
complete; the DB now refuses a cross-ledger reference anywhere in the
business schema.
