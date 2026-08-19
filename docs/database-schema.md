# Database Schema Reference

Column-level reference for the Coffer PostgreSQL schema. Mirrors the SQL in [db/migrations/](../db/migrations/). When the SQL and this document disagree, fix whichever is wrong — never let them drift.

Conventions:
- All `id` columns are `UUID` with `DEFAULT gen_random_uuid()` (built-in on PG13+).
- All `_at` columns are `TIMESTAMPTZ`.
- Money is `NUMERIC(19, 4)`. Quantities are `NUMERIC(19, 6)`.
- Enum-style fields are `TEXT` with a `CHECK` constraint listing allowed values (cheaper to evolve than Postgres ENUMs).
- FK actions: `ON DELETE RESTRICT` for value-bearing relationships, `CASCADE` for ownership, `SET NULL` for soft references. Specifics noted per-column below.

---

## Entity-relationship diagram

```mermaid
erDiagram
    %% ---- Identity / auth / access ----
    ledgers {
        uuid id PK
        text name
        bytea wrapped_lek
        text lek_kek_id
        timestamptz lek_created_at
        timestamptz created_at
    }

    users {
        uuid id PK
        text display_name
        text username UK
        text created_by
        bool is_disabled
        uuid last_opened_ledger_id FK
        bool is_admin
        timestamptz created_at
    }

    webauthn_credentials {
        uuid id PK
        uuid user_id FK
        bytea credential_id UK
        bytea public_key
        bigint signature_counter
        uuid aaguid
        text transports
        text nickname
        text rp_id
        timestamptz created_at
        timestamptz last_used_at
    }

    recovery_codes {
        uuid id PK
        uuid user_id FK
        text code_hash
        timestamptz used_at
        timestamptz created_at
    }

    auth_sessions {
        uuid id PK
        uuid user_id FK
        bytea session_hash UK
        text user_agent
        timestamptz created_at
        timestamptz last_seen_at
        timestamptz expires_at
        timestamptz revoked_at
    }

    webauthn_pending_challenges {
        uuid id PK
        text flow
        uuid user_id FK
        text options_json
        text metadata_json
        timestamptz created_at
        timestamptz expires_at
        timestamptz consumed_at
    }

    bootstrap_tokens {
        bytea token_hash PK
        timestamptz created_at
        timestamptz expires_at
        timestamptz consumed_at
    }

    user_ledger_grants {
        uuid user_id PK,FK
        uuid ledger_id PK,FK
        text role
        timestamptz granted_at
    }

    invites {
        bytea token_hash PK
        uuid id UK
        uuid issued_by_user_id FK
        uuid ledger_id FK
        text role
        bool grants_admin
        timestamptz expires_at
        timestamptz consumed_at
        timestamptz created_at
    }

    %% ---- Accounts / feeds / groups ----
    accounts {
        uuid id PK
        uuid ledger_id FK
        uuid parent_id FK
        text name
        text account_type
        text category_kind
        text currency_code
        numeric opening_balance
        date opened_on
        bool is_active
        uuid feed_connection_id FK
        text external_id
        bool is_system
        jsonb provider_raw_payload
        uuid holdings_account_id FK
        text account_number
        text institution_name
        timestamptz last_simplefin_sync_at
        bool is_trade_commission
        text tax_status
        timestamptz created_at
    }

    account_external_ids {
        uuid id PK
        uuid account_id FK
        uuid ledger_id FK
        text source
        text external_id
        timestamptz created_at
    }

    feed_connections {
        uuid id PK
        uuid ledger_id FK
        text provider
        text provider_item_id
        text status
        timestamptz last_synced_at
        timestamptz token_expires_at
        bytea access_url_ciphertext
        text institution_name
        uuid created_by_user_id FK
        timestamptz created_at
    }

    feed_connection_accounts {
        uuid id PK
        uuid feed_connection_id FK
        uuid ledger_id FK
        text external_id
        text name
        text org_name
        text currency
        numeric balance
        timestamptz balance_at
        timestamptz last_seen_at
        jsonb last_provider_raw_payload
        timestamptz created_at
    }

    user_account_groups {
        uuid id PK
        uuid user_id FK
        uuid ledger_id FK
        text name
        int sort_order
        timestamptz created_at
    }

    user_account_group_members {
        uuid group_id PK,FK
        uuid account_id PK,FK
        uuid ledger_id FK
        timestamptz added_at
    }

    tags {
        uuid id PK
        uuid ledger_id FK
        text name
        text color
        timestamptz created_at
    }

    %% ---- Transactions (events + postings + overlays) ----
    txn_headers {
        uuid id PK
        uuid ledger_id FK
        text origin
        text external_id
        text payee
        text memo
        timestamptz posted_at
        timestamptz transacted_at
        text check_number
        bool is_pending
        bool is_hidden
        uuid is_merged_into FK
        bigint seq UK
        text online_match_fitid
        text online_match_fi_id
        text provider_key
        text action
        bool is_merge_winner
        bool needs_review
        bool is_recurring_template
        uuid recurring_transaction_id FK
        date occurrence_date
        timestamptz created_at
    }

    txn_legs {
        uuid id PK
        uuid header_id FK
        uuid account_id FK
        uuid ledger_id FK
        int posting_index
        text leg_memo
        numeric amount
        uuid security_id FK
        numeric quantity
        numeric unit_price
        text posting_role
        int account_postings_on_header
        int header_total_postings
        timestamptz created_at
    }

    txn_header_account_balances {
        uuid header_id PK,FK
        uuid account_id PK,FK
        uuid ledger_id FK
        numeric balance_after
        numeric net_amount
    }

    txn_header_overrides {
        uuid header_id PK,FK
        uuid ledger_id FK
        text payee
        text memo
        timestamptz posted_at
        timestamptz transacted_at
        text check_number
        bool is_hidden
        timestamptz updated_at
    }

    txn_leg_overrides {
        uuid leg_id PK,FK
        uuid ledger_id FK
        text leg_memo
        numeric amount
        timestamptz updated_at
    }

    txn_leg_recon {
        uuid leg_id PK,FK
        uuid ledger_id FK
        text status
        timestamptz cleared_at
        uuid cleared_by_user_id FK
    }

    txn_header_tags {
        uuid header_id PK,FK
        uuid tag_id PK,FK
        uuid ledger_id FK
        timestamptz created_at
    }

    %% ---- Securities / holdings / lots / prices ----
    securities {
        uuid id PK
        uuid ledger_id FK
        text ticker
        text cusip
        text name
        text asset_class
        text vehicle_type
        text region
        text tax_character
        text classification_source
        text exchange
        bool is_active
        text external_id
        int share_decimals
        text quote_symbol
        bool auto_price
        bool quote_symbol_public
        timestamptz created_at
    }

    holdings {
        uuid id PK
        uuid ledger_id FK
        uuid account_id FK
        uuid security_id FK
        numeric quantity
        numeric cost_basis
        timestamptz as_of
    }

    lots {
        uuid id PK
        uuid ledger_id FK
        uuid holding_id FK
        uuid leg_id FK
        numeric quantity
        numeric unit_cost
        timestamptz acquired_at
        bool is_closed
    }

    realized_gains {
        uuid id PK
        uuid ledger_id FK
        uuid account_id FK
        uuid security_id FK
        uuid sell_leg_id FK,UK
        timestamptz sold_at
        numeric quantity
        numeric proceeds
        numeric cost_basis_sold
        numeric realized_gain
        numeric proceeds_lt
        numeric cost_basis_sold_lt
        numeric realized_gain_lt
        timestamptz created_at
    }

    security_prices {
        uuid id PK
        uuid ledger_id FK
        uuid security_id FK
        numeric price
        text currency_code
        date price_date
        numeric high
        numeric low
        bigint volume
        text source
    }

    security_splits {
        uuid id PK
        uuid ledger_id FK
        uuid security_id FK
        timestamptz split_at
        numeric ratio
        numeric old_shares
        numeric new_shares
        text external_id
        timestamptz created_at
    }

    security_components {
        uuid id PK
        uuid security_id FK
        text component_asset_class
        text component_region
        numeric weight
        timestamptz created_at
    }

    provider_security_mappings {
        uuid id PK
        uuid ledger_id FK
        text provider_key
        text provider_security_id
        uuid security_id FK
        uuid created_by_user_id FK
        timestamptz created_at
    }

    %% ---- Ledger operations (ingest / quote / import / restore activity log) ----
    ledger_operations {
        uuid id PK
        uuid ledger_id FK
        uuid feed_connection_id FK
        text family
        text provider_key
        text triggered_via
        uuid triggered_by_user_id FK
        text status
        jsonb details
        text error_message
        timestamptz started_at
        timestamptz completed_at
    }

    ledger_operation_errors {
        uuid id PK
        uuid ledger_operation_id FK
        uuid ledger_id FK
        text code
        text message
        text simplefin_connection_id
        text simplefin_account_id
        timestamptz created_at
    }

    ledger_operation_promotions {
        uuid id PK
        uuid ledger_operation_id FK
        uuid header_id FK
        uuid ledger_id FK
        numeric was_amount
        numeric became_amount
        timestamptz promoted_at
    }

    %% ---- Recurring / loans ----
    recurring_transactions {
        uuid id PK
        uuid ledger_id FK
        uuid source_account_id FK
        uuid loan_account_id FK
        date start_date
        date end_date
        date next_due_date
        date last_acknowledged_date
        bool is_loan_reminder
        bool is_active
        text origin
        text external_id
        text rrule
        jsonb source_payload
        int auto_commit_days_before
        uuid template_header_id FK
        timestamptz created_at
    }

    recurring_occurrence_exceptions {
        uuid id PK
        uuid ledger_id FK
        uuid recurring_transaction_id FK
        date occurrence_date
        uuid created_by_user_id FK
        timestamptz created_at
    }

    loan_terms {
        uuid account_id PK,FK
        uuid ledger_id FK
        numeric original_principal
        numeric annual_interest_rate
        numeric points
        int payment_count
        int payments_per_year
        date first_payment_date
        numeric escrow_amount
        uuid interest_account_id FK
        uuid escrow_account_id FK
        bool payment_is_computed
        numeric fixed_payment
        timestamptz created_at
    }

    %% ---- Preferences / schedulers ----
    user_preferences {
        uuid user_id PK,FK
        uuid ledger_id PK,FK
        text namespace PK
        jsonb value
        timestamptz updated_at
    }

    scheduled_jobs {
        uuid ledger_id PK,FK
        text job_type PK
        bool enabled
        int hour_local
        int minute_local
        text timezone
        uuid configured_by_user_id FK
        timestamptz last_run_at
        timestamptz next_run_at
        timestamptz created_at
        timestamptz updated_at
    }

    global_scheduled_jobs {
        text job_type PK
        bool enabled
        int hour_local
        int minute_local
        text timezone
        bytea passphrase_ciphertext
        uuid configured_by_user_id FK
        timestamptz last_run_at
        timestamptz next_run_at
        timestamptz created_at
        timestamptz updated_at
    }

    %% ---- MCP (connected apps) ----
    mcp_access_tokens {
        uuid id PK
        uuid user_id FK
        text name
        bytea token_hash UK
        text scopes
        timestamptz created_at
        timestamptz last_used_at
        timestamptz expires_at
        timestamptz revoked_at
    }

    mcp_tool_invocations {
        uuid id PK
        uuid user_id FK
        text tool_name
        text arguments
        text status
        text result
        uuid ledger_id
        timestamptz created_at
        timestamptz completed_at
        text trace_id
    }

    %% ---- Backups / Drive / system settings / snapshots ----
    drive_sync {
        int id PK
        bool enabled
        bytea oauth_ciphertext
        text folder_id
        text folder_name
        text install_id
        text connected_email
        timestamptz last_sync_at
        text last_sync_status
        uuid configured_by_user_id FK
        timestamptz created_at
        timestamptz updated_at
    }

    backup_settings {
        int id PK
        int retention_daily
        int retention_weekly
        int retention_monthly
        uuid configured_by_user_id FK
        timestamptz updated_at
    }

    backup_pins {
        text artifact_id PK
        uuid pinned_by_user_id FK
        timestamptz created_at
    }

    system_settings {
        text key PK
        jsonb value
        timestamptz updated_at
        uuid updated_by FK
    }

    ledger_snapshots {
        uuid id PK
        uuid ledger_id FK
        uuid created_by_user_id FK
        text kind
        text description
        text schema_version
        bytea content
        jsonb content_json
        int content_size_uncompressed
        timestamptz created_at
    }

    %% ==== Relationships ====
    %% Identity / auth / access
    users ||--o| ledgers : "last opened"
    users ||--o{ webauthn_credentials : "registers"
    users ||--o{ recovery_codes : "has"
    users ||--o{ auth_sessions : "logs in via"
    users ||--o{ webauthn_pending_challenges : "ceremony for"
    users ||--o{ user_ledger_grants : "granted"
    ledgers ||--o{ user_ledger_grants : "on"
    users ||--o{ invites : "issued by"
    ledgers ||--o{ invites : "grants role on"

    %% Accounts / feeds / groups
    ledgers ||--o{ accounts : "scopes"
    accounts ||--o{ accounts : "parent of (categories)"
    accounts ||--o| accounts : "Holdings sibling of"
    feed_connections ||--o{ accounts : "mapped to"
    accounts ||--o{ account_external_ids : "per-source id for"
    ledgers ||--o{ account_external_ids : "scopes"
    ledgers ||--o{ feed_connections : "scopes"
    users ||--o{ feed_connections : "connected by"
    feed_connections ||--o{ feed_connection_accounts : "directory of"
    ledgers ||--o{ tags : "scopes"
    users ||--o{ user_account_groups : "owns"
    ledgers ||--o{ user_account_groups : "scopes"
    user_account_groups ||--o{ user_account_group_members : "contains"
    accounts ||--o{ user_account_group_members : "member in"

    %% Transactions
    ledgers ||--o{ txn_headers : "scopes"
    txn_headers ||--o{ txn_legs : "postings"
    accounts ||--o{ txn_legs : "posts to"
    ledgers ||--o{ txn_legs : "scopes"
    securities ||--o{ txn_legs : "investment leg references"
    txn_headers ||--o{ txn_header_account_balances : "balance per account"
    accounts ||--o{ txn_header_account_balances : "running balance on"
    ledgers ||--o{ txn_header_account_balances : "scopes"
    txn_headers ||--o| txn_header_overrides : "header-level edits"
    txn_legs ||--o| txn_leg_overrides : "leg-level edits"
    txn_legs ||--o| txn_leg_recon : "per-account recon status"
    users ||--o{ txn_leg_recon : "cleared by"
    txn_headers ||--o{ txn_header_tags : "tagged with"
    tags ||--o{ txn_header_tags : "applied via"
    txn_headers ||--o| txn_headers : "merged into (is_merged_into)"

    %% Securities / holdings / lots / prices
    ledgers ||--o{ securities : "scopes"
    accounts ||--o{ holdings : "holds"
    securities ||--o{ holdings : "of security"
    ledgers ||--o{ holdings : "scopes"
    holdings ||--o{ lots : "broken into"
    txn_legs ||--o{ lots : "opened by (holdings-side leg)"
    ledgers ||--o{ lots : "scopes"
    accounts ||--o{ realized_gains : "disposals on"
    securities ||--o{ realized_gains : "of security"
    txn_legs ||--o| realized_gains : "sell leg"
    ledgers ||--o{ realized_gains : "scopes"
    securities ||--o{ security_prices : "priced by"
    ledgers ||--o{ security_prices : "scopes"
    securities ||--o{ security_splits : "split events"
    ledgers ||--o{ security_splits : "scopes"
    securities ||--o{ security_components : "look-through sleeves"
    securities ||--o{ provider_security_mappings : "resolves to"
    ledgers ||--o{ provider_security_mappings : "scopes"
    users ||--o{ provider_security_mappings : "recorded by"

    %% Ledger operations
    ledgers ||--o{ ledger_operations : "scopes"
    feed_connections ||--o{ ledger_operations : "for connection"
    users ||--o{ ledger_operations : "triggered by"
    ledger_operations ||--o{ ledger_operation_errors : "errlist entries"
    ledger_operations ||--o{ ledger_operation_promotions : "promote-on-clear"
    txn_headers ||--o{ ledger_operation_promotions : "promoted header"

    %% Recurring / loans
    accounts ||--o{ recurring_transactions : "source account"
    accounts ||--o| recurring_transactions : "loan reminder for"
    txn_headers ||--o| recurring_transactions : "template header"
    recurring_transactions ||--o{ txn_headers : "fires occurrences"
    recurring_transactions ||--o{ recurring_occurrence_exceptions : "exceptions"
    ledgers ||--o{ recurring_occurrence_exceptions : "scopes"
    users ||--o{ recurring_occurrence_exceptions : "skipped by"
    accounts ||--o| loan_terms : "amortization terms (1:1)"
    ledgers ||--o{ loan_terms : "scopes"
    accounts ||--o{ loan_terms : "interest/escrow category"

    %% Preferences / schedulers
    users ||--o{ user_preferences : "owns"
    ledgers ||--o{ user_preferences : "applies to"
    ledgers ||--o{ scheduled_jobs : "scopes"
    users ||--o{ scheduled_jobs : "configured by"
    users ||--o{ global_scheduled_jobs : "configured by"

    %% MCP
    users ||--o{ mcp_access_tokens : "owns"
    users ||--o{ mcp_tool_invocations : "acted by"

    %% Backups / Drive / system settings / snapshots
    users ||--o{ drive_sync : "configured by"
    users ||--o{ backup_settings : "configured by"
    users ||--o{ backup_pins : "pinned by"
    users ||--o{ system_settings : "updated by"
    ledgers ||--o{ ledger_snapshots : "snapshot of"
    users ||--o{ ledger_snapshots : "created by"
```

---

## Tables

### `ledgers`

Phase A multi-ledger anchor (per [decisions/0020-multi-ledger-row-scoped.md](decisions/0020-multi-ledger-row-scoped.md)). A ledger is the unit of book-isolation: every anchor table carries `ledger_id`; every other table inherits its ledger membership transitively via FK chain.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `name` | `TEXT` | NOT NULL | Display name. Not unique — two ledgers can share a name (the importer disambiguates via id). |
| `wrapped_lek` | `BYTEA` | | AES-GCM-sealed 32-byte Ledger Encryption Key (ADR-0026, migration 035). Layout `nonce(12) ‖ ciphertext(32) ‖ tag(16)` = 60 bytes. Nullable: a pre-ADR-0026 row is lazily backfilled on first secret access; freshly-created ledgers always populate. |
| `lek_kek_id` | `TEXT` | | Id of the master KEK that wrapped this LEK (migration 035). Defaults to `"v1"` until master-KEK rotation introduces `"v2"`; rotation re-wraps each LEK and bumps this. Nullable until backfill. |
| `lek_created_at` | `TIMESTAMPTZ` | | Timestamp the LEK was generated (migration 035). Drives the LEK-rotation cadence; distinct from `created_at` because LEKs rotate independently of the ledger row. Nullable until backfill. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Migration 014 seeds a default ledger with the well-known id `00000000-0000-0000-0000-000000000001` to absorb existing data on a populated DB. Single-tenant deployments stay on the default; users who want a separate book pass `--ledger-name` to the importer.

### `users`

Skeleton in Phase A, extended in Phase 3 PR 3.2 (per [decisions/0013-webauthn-passkey-auth.md](decisions/0013-webauthn-passkey-auth.md)) with the columns the auth flow needs. The bootstrap "system" user owns the default ledger and is the service-account identity for unattended workers (importer, sync worker).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `display_name` | `TEXT` | NOT NULL | |
| `username` | `TEXT` `COLLATE username_ci` | UNIQUE WHERE NOT NULL (case-insensitive) | Login identifier — may be an email address (ADR-0089). Unique among non-NULL values; NULL allowed during the brief window between user-row creation and first credential registration, but the registration endpoints fail if it's still NULL at credential-create time. Added in migration 015. **Migration 187** applied the ICU collation `username_ci` (`und-u-ks-level2`, non-deterministic), so `=` and `uq_users_username` fold case for every caller — independent of the install's locale *and* of the user's own culture. Stored as typed (display case preserved); compared folded. Charset is validated by `UsernamePolicy`, which rejects whitespace and Unicode control/format characters. **No `LIKE`/pattern operators on this column** — non-deterministic collations reject them; a future username search must compare against an explicitly-collated expression. |
| `created_by` | `TEXT` | NOT NULL DEFAULT `'system'` | Identifier of the actor that created this row — `'system'` for the bootstrap user, `'bootstrap-token'` for the first interactive register, the inviting user's id for any later admin-issued accounts. Added in migration 015. |
| `is_disabled` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Soft-disable flag. Disabled users keep their grants but cannot log in. Added in migration 015. |
| `last_opened_ledger_id` | `UUID` | FK → `ledgers(id)` ON DELETE SET NULL | The ledger this user most recently switched to. UI auto-opens this on next login (after re-validating the user still has a grant). NULL on first login → ledger picker. |
| `is_admin` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Global operator/admin flag (migration 138, ADR-0060). Gates system-wide, cross-user actions (whole-DB backup; System settings). First human user becomes admin at setup-complete. Settable only by the service role — `coffer_app`'s table-wide UPDATE on `users` is revoked and re-granted column-scoped to `last_opened_ledger_id` so a user can't self-promote. Distinct from per-ledger `user_ledger_grants`. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

The bootstrap system user has the well-known id `00000000-0000-0000-0000-000000000001` and `username = 'system'`.

### `webauthn_credentials`

One row per FIDO2 / WebAuthn credential registered to a user (per [decisions/0013-webauthn-passkey-auth.md](decisions/0013-webauthn-passkey-auth.md)). Multiple credentials per user are first-class. Added in migration 015.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | |
| `credential_id` | `BYTEA` | NOT NULL UNIQUE | The FIDO2 credential id. Globally unique (not just per-user) so the same authenticator can never live on two accounts — replay-attack mitigation. |
| `public_key` | `BYTEA` | NOT NULL | COSE-encoded public key. |
| `signature_counter` | `BIGINT` | NOT NULL DEFAULT 0 | Replay-attack guard. Must strictly increase per credential per assertion; a backwards counter rejects the assertion. |
| `aaguid` | `UUID` | | Authenticator AAGUID; lets the UI label "YubiKey 5C" vs. a phone passkey. |
| `transports` | `TEXT[]` | | Reported transports (`usb`, `nfc`, `ble`, `internal`, `hybrid`). |
| `nickname` | `TEXT` | NOT NULL | User-supplied label. |
| `rp_id` | `TEXT` | | WebAuthn Relying Party ID (the domain) this credential was registered against. NULL for rows predating migration 157. Registration excludes only same-`rp_id` credentials, so one from a prior RP (domain rename / ADR-0061 restore) doesn't block re-enrolling the same authenticator. Migration 157. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `last_used_at` | `TIMESTAMPTZ` | | Bumped on each successful assertion. |

### `recovery_codes`

Argon2id-hashed one-shot recovery codes (10 issued at registration or regeneration per ADR-0013). Each row's `code_hash` is sufficient to verify a presented plaintext code; `used_at` flips on consumption so a code can never be reused. Added in migration 015.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | |
| `code_hash` | `TEXT` | NOT NULL | Argon2id PHC string (`$argon2id$v=19$m=…$t=…$p=…$salt$hash`). Verification reads parameters out of the string so increasing the cost is a one-line change without a migration. OWASP-2025-minimum parameters baseline (m=64MiB, t=3, p=1). |
| `used_at` | `TIMESTAMPTZ` | | One-shot semantics: NULL when unused, set on consumption, never cleared. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `auth_sessions`

Cookie-backed login sessions (per ADR-0013). The cookie carries an opaque random session id; this table stores `SHA-256(id)` so DB reads cannot forge sessions. Defaults: 30-day max lifetime, 7-day idle timeout. Added in migration 015; written in PR 3.3.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | |
| `session_hash` | `BYTEA` | NOT NULL UNIQUE | SHA-256 of the cookie value. Plaintext never persists. |
| `user_agent` | `TEXT` | | For the active-sessions UI. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `last_seen_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Application checks this against the idle timeout. |
| `expires_at` | `TIMESTAMPTZ` | NOT NULL | Hard upper bound on session lifetime regardless of activity. |
| `revoked_at` | `TIMESTAMPTZ` | | Set by logout / "sign out everywhere"; the session row stays for audit. |

### `webauthn_pending_challenges`

Server-side state for in-flight WebAuthn ceremonies (per ADR-0013). Rows live ~60-120s between `/begin` and `/complete`; `consumed_at` flips on first successful verify so a replay against the same challenge id fails. A periodic sweep (PR 3.5+) deletes expired rows. Added in migration 016.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `flow` | `TEXT` | NOT NULL CHECK in (`setup`, `login`, `register`, `invite`) | The ceremony shape this challenge was issued for. The `/complete` consumer must request the same flow it was issued for; mismatched flows fail the lookup. `register` (migration 140) is the add-a-passkey ceremony for an already-authenticated user, distinct from `setup` (which also creates the user). `invite` (migration 176, ADR-0083 slice B) is the invite-redeem ceremony — a scoped, repeatable clone of the first-user `setup` ceremony that `InvitesEndpoints` runs when a recipient redeems an invite link. |
| `user_id` | `UUID` | FK → `users(id)` ON DELETE CASCADE | NULL during the bootstrap setup flow (the user row doesn't exist yet — it's created at /complete in the same transaction as the credential). For login, the resolved user. |
| `options_json` | `TEXT` | NOT NULL | Fido2NetLib's `CredentialCreateOptions` / `AssertionOptions` JSON serialisation. The challenge bytes live inside. |
| `metadata_json` | `TEXT` | | Per-flow scratch: setup stores the proposed username + display name + the predetermined user_id here so `/complete` can build the user row; login leaves it NULL. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `expires_at` | `TIMESTAMPTZ` | NOT NULL | |
| `consumed_at` | `TIMESTAMPTZ` | | Set on first successful verify. `UPDATE … RETURNING` in `ChallengeStore.ConsumeAsync` makes this single-shot under concurrent callers. |

### `bootstrap_tokens`

One-shot setup tokens minted at API startup when no WebAuthn credentials exist (per ADR-0013). The plaintext is written to the API logs once and consumed by `/api/auth/setup/{token}/complete`. The SPA pre-validates the token (without consuming) via `GET /api/auth/setup/{token}/info`. Subsequent registrations require an authenticated session or a recovery code. Added in migration 015.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `token_hash` | `BYTEA` | PK | `SHA-256` of the plaintext token. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `expires_at` | `TIMESTAMPTZ` | NOT NULL | Default 24h after creation; configurable via `Api:Bootstrap:TokenLifetimeHours`. |
| `consumed_at` | `TIMESTAMPTZ` | | Set on first successful consume. Single-statement update guarantees only one consumer can succeed. |

### `user_ledger_grants`

Per-user permissions on each ledger. Composite PK `(user_id, ledger_id)`.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | |
| `role` | `TEXT` | NOT NULL CHECK in (`owner`, `editor`, `viewer`) | Owner: read+write+grant+delete. Editor: read+write. Viewer: read-only. |
| `granted_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

The ≥1-owner-per-ledger invariant is enforced in **API code**, not a DB trigger. The original constraint trigger (`trg_user_ledger_grants_owner_present` + `fn_validate_ledger_has_owner()`) was **dropped in migration 087** (ADR-0032, triggers-as-last-resort): any endpoint that revokes or downgrades a grant does its own owner-count check and returns a typed 422 rather than letting a Postgres exception surface as a 500. Direct-SQL ownership re-assignment remains the operator's responsibility.

`user_visible_ledgers` is a view over this table joined to `ledgers` — used by the login-time picker, the auto-open validation, and as a building block for the Phase 3 RLS policies.

### `invites`

Invite links (migration 175, ADR-0083 slice B). A generalized, repeatable, scoped bootstrap token — same token crypto/storage as `bootstrap_tokens` (the plaintext is shown to the issuer once and never persisted; the SHA-256 is the PK). The scope columns record who issued it, the target ledger + grant role it confers (both NULL = an instance-only invite that just creates the account), an optional instance-admin grant, an expiry, and a single-use consume. Service-role only: redeem runs pre-auth (the token *is* the credential), so `coffer_app` is never granted — every read/write goes through `coffer_service`, exactly like `bootstrap_tokens`.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `token_hash` | `BYTEA` | PK | `SHA-256` of the plaintext invite token (32 bytes). |
| `id` | `UUID` | NOT NULL UNIQUE | Public, non-secret handle for list / revoke (never expose `token_hash`). App-generated. |
| `issued_by_user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | The issuing user. |
| `ledger_id` | `UUID` | FK → `ledgers(id)` ON DELETE CASCADE | The target ledger; NULL = an instance-only invite (creates the account, grants no ledger). |
| `role` | `TEXT` | CHECK in (`owner`, `editor`, `viewer`) | The grant role conferred on the target ledger. NULL for an instance-only invite. |
| `grants_admin` | `BOOLEAN` | NOT NULL DEFAULT FALSE | When TRUE, the redeemed account also gets the instance-admin flag. |
| `expires_at` | `TIMESTAMPTZ` | NOT NULL | Hard expiry. |
| `consumed_at` | `TIMESTAMPTZ` | | Single-use: NULL while unredeemed, set on redeem. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Table CHECK `invites_ledger_role_together`: `(ledger_id IS NULL) = (role IS NULL)` — a ledger invite carries a role; an instance-only invite carries neither. Indexes `ix_invites_issued_by (issued_by_user_id)`, `ix_invites_ledger (ledger_id) WHERE ledger_id IS NOT NULL`. `GRANT ALL` to `coffer_service`; no `coffer_app` grant.

### `accounts`

Both real accounts (bank, credit_card, investment, asset, liability, loan) **and** budgeting categories. Unified per [decisions/0002-unified-accounts-table.md](decisions/0002-unified-accounts-table.md), with the discriminator refinement in [decisions/0017-account-discriminator.md](decisions/0017-account-discriminator.md).

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor (ADR-0020). Set once at insert; the importer keeps it stable across re-runs. Existing rows backfill to the default ledger via migration 014. |
| `parent_id` | `UUID` | composite FK `(parent_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE SET NULL (parent_id) | NULL for top-level. Allowed only when `account_type='category'`. Composite FK `accounts_parent_id_ledger_fkey` (migration 121) — the scoped `SET NULL (parent_id)` clears only the reference, leaving the row's NOT-NULL `ledger_id` intact. |
| `name` | `TEXT` | NOT NULL | Display name |
| `account_type` | `TEXT` | NOT NULL CHECK in (`bank`, `credit_card`, `investment`, `asset`, `liability`, `loan`, `category`) | |
| `category_kind` | `TEXT` | CHECK in (NULL, `income`, `expense`) | Set if and only if `account_type='category'` |
| `currency_code` | `TEXT` | NOT NULL DEFAULT `'USD'` | ISO 4217 |
| `opening_balance` | `NUMERIC(19,4)` | NOT NULL DEFAULT 0 | Balance at start of tracking. Must be `0` for categories. |
| `opened_on` | `DATE` | NULL | The account's "Start Date" (the opening balance's as-of date). MD records it for most account types; seeded on import from the MD `acct` item's creation stamp — `date_created` (`yyyyMMdd` int) when present, else `creation_date` (epoch millis, read as its UTC date; MD stamps these at local noon so the day is stable) — and editable in Coffer later. The importer's upsert uses `COALESCE(accounts.opened_on, EXCLUDED.opened_on)` — seed-once, never overwriting a Coffer-side edit. NULL for categories (their opening balance is forced to 0). Added in migration 127 (ADR-0050); importer population landed later, and since MD import is a one-shot bootstrap with no re-import path, **migration 196** backfills already-imported ledgers by mining the same fields from `provider_raw_payload`. |
| `is_active` | `BOOLEAN` | NOT NULL DEFAULT TRUE | Single lifecycle flag (mig 106): `FALSE` = deactivated. The sidebar's "Show inactive" toggle surfaces these with a strikethrough; deactivated accounts are excluded from pickers + counterparty dropdowns. Mig 106 dropped the orthogonal `is_hidden` flag and collapsed its 109 rows into this column; the MD importer maps both MD-side `is_inactive` AND `hide` flags here. |
| `feed_connection_id` | `UUID` | composite FK `(feed_connection_id, ledger_id)` → `feed_connections(id, ledger_id)` ON DELETE SET NULL (feed_connection_id) | NULL for manual/import-only and for all categories. Composite FK `accounts_feed_connection_id_ledger_fkey` (migration 121). |
| `external_id` | `TEXT` | per-ledger UNIQUE `(ledger_id, external_id) WHERE external_id IS NOT NULL` | Source-system identifier — for Moneydance imports this is the raw MD UUID. NULL for accounts created by other paths. Lets imports re-run idempotently. Added in migration 009; the original global unique was narrowed to per-ledger `uq_accounts_external_id_per_ledger` in migration 014. NULL on system-managed Holdings sibling rows. See also the `account_external_ids` junction (migration 064), which supersedes single-column keying by letting one account carry a distinct external id per source. |
| `is_system` | `BOOLEAN` | NOT NULL DEFAULT FALSE | TRUE on rows the importer/API creates and the user UI hides by default — currently only the per-brokerage Holdings sibling accounts (ADR-0019). Added in migration 011. |
| `provider_raw_payload` | `JSONB` | | Verbatim per-row provider data captured at import time (MD `acct` JSON for MD-bootstrap accounts; SimpleFIN/OFX-direct importers populate it analogously). Per ADR-0035 §3, classification rules read this directly rather than the source file — e.g. `olbfi` (online OFX) vs `ofx_import_acct_num` (QFX file) discriminates `online_import` from `file_import`. NULL on Coffer-native accounts created via the API. Added in migration 110. |
| `holdings_account_id` | `UUID` | composite FK `(holdings_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE SET NULL (holdings_account_id) | On a brokerage row, points at its system-managed Holdings sibling that hosts the holdings-side legs of investment transactions (ADR-0019). NULL on every other account. Added in migration 011; composite FK `accounts_holdings_account_id_ledger_fkey` (migration 121). |
| `notes` | `TEXT` | | User-authored notes on the account. Maps to MD's `comment` field. Added in migration 012. |
| `account_number` | `TEXT` | | Account number at the institution. Sourced from `bank_account_number` on bank/credit accounts, `invst_account_number` on investment accounts. Added in migration 012. |
| `institution_name` | `TEXT` | | Name of the institution. Sourced from `bank_name` or `inst_name` (whichever is populated). Useful for matching against SimpleFIN feed accounts in Phase 5. Added in migration 012. |
| `routing_number` | `TEXT` | | ACH/OFX routing number. Sourced from MD's `ofx_bank_id`. Added in migration 012. |
| `account_url` | `TEXT` | | Institution's website. Sourced from MD's `account_url`. Added in migration 012. |
| `last_simplefin_sync_at` | `TIMESTAMPTZ` | | Per-account SimpleFIN sync watermark (slice 2c.5 / migration 042). The next sync against this account asks SimpleFIN for transactions from `(this − 7d)` forward; NULL = "no successful sync yet, full 90-day window next time." Advances only on syncs that actually persisted data for this account (mapped at sync time, not tagged in `errlist`). The user can also pick or reset this value via `PATCH /api/ledgers/{lid}/accounts/{aid}/sync-from-date`. Unbinding the feed mapping clears it so re-binding starts a fresh 90-day window. |
| `is_trade_commission` | `BOOLEAN` | NOT NULL DEFAULT FALSE; CHECK (`is_trade_commission = FALSE OR account_type = 'investment'`) | **On a brokerage (`account_type='investment'`)**: when TRUE, `recompute_holdings_cost_basis()` adds `posting_role='fee'` leg amounts from this brokerage's transactions into cost basis (and into `lots.unit_cost` on the function's next reset). Default FALSE. Migration 054 added the column; migration 056 narrowed semantics from per-category to per-brokerage and added the CHECK so non-investment accounts can't carry it. Typical settings: taxable brokerage = TRUE; 401k where in-transaction "fees" are administrative = FALSE. |
| `tax_status` | `TEXT` | CHECK in (NULL, `taxable`, `tax_deferred`, `tax_free`, `other`) | ADR-0066 (migration 149). The account's tax treatment, orthogonal to `account_type` (a brokerage and a Roth IRA are both `investment`). Importer seeds a best-guess from the source account name/type; Coffer owns it thereafter (import-once). Distinguishes 1099-B-relevant taxable accounts from tax-deferred/tax-free for reporting. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

**Cross-column CHECK constraints (migration 007):**

| Constraint | Rule |
|---|---|
| `accounts_category_kind_consistent` | `(account_type='category') = (category_kind IS NOT NULL)` |
| `accounts_parent_only_for_categories` | `parent_id IS NULL OR account_type='category'` |
| `accounts_category_has_no_real_state` | `account_type<>'category' OR (feed_connection_id IS NULL AND opening_balance = 0)` |

Table-level `UNIQUE (id, ledger_id)` (`uq_accounts_id_ledger`, migration 049) lets every child table (txn_legs, holdings, recurring_transactions, loan_terms, the self-FKs above, …) compose its FK against `(id, ledger_id)` so the DB structurally refuses a cross-ledger reference.

There is no `is_placeholder` column. The "this account is a folder" UI state is derived as "has children AND no own transactions" — for categories, that's a meaningful distinction (a parent category may also receive direct transactions); for non-categories, hierarchy is forbidden so the question doesn't arise.

**Holdings sibling pattern (ADR-0019).** A brokerage's holdings-side
postings live on a system-managed sibling account at the root rather than
as a child of the brokerage itself. This keeps the `parent_id`-only-for-
categories invariant intact while giving each brokerage a paired account
to host the asset side of investment transactions. The link is the
`holdings_account_id` self-FK on the brokerage row.

### `account_external_ids`

Junction (migration 064, ADR-0031) mapping accounts to per-provider external ids, so one Coffer account can carry a distinct id per source (SimpleFIN emits one, the Moneydance import another). The importer's account-adoption path consults this to find an existing account before creating a new one — closing the dual-source account-drift gap that single-column `accounts.external_id` keying caused. RLS inherits from the parent account.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | |
| `account_id` | `UUID` | NOT NULL; composite FK `(account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE CASCADE | The Coffer account. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Per-ledger anchor. |
| `source` | `TEXT` | NOT NULL CHECK in (`moneydance`, `simplefin`, `manual`) | Which upstream source this id came from. |
| `external_id` | `TEXT` | NOT NULL | The source-specific identifier. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Constraints/indexes: UNIQUE `uq_account_external_ids_source_extid (ledger_id, source, external_id)` (the importer's primary lookup); UNIQUE `uq_account_external_ids_account_source (account_id, source)` (at most one id per source per account); index `idx_account_external_ids_account (account_id)`. RLS policy `account_external_ids_per_user` — `USING (account_id IN (SELECT id FROM accounts))`.

### `feed_connections`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor (ADR-0020). |
| `provider` | `TEXT` | NOT NULL CHECK in (`simplefin`, `plaid`, `manual`) | |
| `provider_item_id` | `TEXT` | | Provider's identifier for this connection |
| `status` | `TEXT` | NOT NULL DEFAULT `'active'` CHECK in (`active`, `needs_reauth`, `error`, `disconnected`) | |
| `last_synced_at` | `TIMESTAMPTZ` | | "Last sync attempt timestamp" — advances on every 2xx response from SimpleFIN, including `needs_reauth`. Display only ("Last synced 3h ago" connection label); the sync algorithm's start-date math reads `accounts.last_simplefin_sync_at` per-account instead (slice 2c.5 / migration 042). |
| `token_expires_at` | `TIMESTAMPTZ` | | For OAuth (a major brokerage via SimpleFIN/MX) |
| `access_url_ciphertext` | `BYTEA` | | SimpleFIN access URL **sealed under the owning ledger's LEK** (ADR-0026, migration 036). Layout `nonce(12) ‖ ciphertext ‖ tag(16)`. Never plaintext. NULL only on rows predating the migration (none in practice — every fresh connection writes it). |
| `institution_name` | `TEXT` | | FI display name from SimpleFIN's `/info` (or any account's `org.name` on first sync). Migration 036. NULL until populated; the SPA falls back to "SimpleFIN". |
| `created_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Audit: which user clicked "Connect". Migration 036. SET NULL so a removed user doesn't cascade their feed history. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Table-level `UNIQUE (id, ledger_id)` (`uq_feed_connections_id_ledger`, migration 072) so child tables (`feed_connection_accounts`, `ledger_operations`, `accounts.feed_connection_id`) can compose ledger-coherent composite FKs.

### `feed_connection_accounts`

Per-connection bank-side account directory (slice 2c.4 / migration 041).
One row per SimpleFIN account the bank surfaces on a connection.
Upserted on every sync; the SPA reads this to render the unified accounts
list (mapped + unmapped together) on the bank-feeds page without
requiring a fresh sync.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | |
| `feed_connection_id` | `UUID` | NOT NULL; composite FK `(feed_connection_id, ledger_id)` → `feed_connections(id, ledger_id)` ON DELETE CASCADE | Parent connection. Composite FK `feed_connection_accounts_connection_ledger_fkey` (migration 072). |
| `ledger_id` | `UUID` | NOT NULL | Denormalized from the parent connection (migration 072) so RLS gates on `ledger_id` directly. |
| `external_id` | `TEXT` | NOT NULL | SimpleFIN `account.id` from the v2 payload. Composite with `feed_connection_id` for the upsert key. |
| `name` | `TEXT` | NOT NULL | SimpleFIN-supplied account name. Updated on every sync. |
| `org_name` | `TEXT` | | SimpleFIN-supplied institution name (`account.org.name`). |
| `currency` | `TEXT` | | ISO 4217 code from SimpleFIN. |
| `balance` | `NUMERIC(19, 4)` | | Latest balance the bank surfaced. |
| `balance_at` | `TIMESTAMPTZ` | | When SimpleFIN observed the balance. |
| `last_seen_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT `now()` | Bumped on every sync that returns this `external_id`. Rows that fall behind subsequent syncs are stale entries — the bank stopped exposing this account. Future slice will sweep / flag these. |
| `last_provider_raw_payload` | `JSONB` | | Verbatim per-account JSON from the provider (SimpleFIN account shape, including the `holdings[]` block discarded by the typed projection). Overwritten on each sync's directory upsert. Diagnostic / classifier-iteration use only; not a source of truth. Migration 080. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT `now()` | |

**RLS** `feed_connection_accounts_per_user` gates on `ledger_id` directly (migration 072 flattened the former transitive `feed_connection_id IN (SELECT id FROM feed_connections)` subquery).

**Upsert key** `uq_feed_connection_accounts_external`:
`UNIQUE (feed_connection_id, external_id)` — every sync's directory write
hits the same row per `(connection, SimpleFIN account)` pair.

The mapping between a Coffer account and a SimpleFIN account lives on
`accounts (feed_connection_id, external_id)` (the PATCH/DELETE
`/accounts/{id}/feed-mapping` endpoints). The
`feed_connection_accounts` row is the bank-side metadata; the mapping
is whether (and to which Coffer account) it's bound.

### `txn_headers`

Event envelope under the ADR-0022 normalised schema. One row per
Moneydance txn (or user-entered event, or SimpleFIN feed event).
Carries the umbrella metadata that's shared across all postings of
the event: payee, memo, posted-at, check number, plus the online-match
state. User edits to header fields live in `txn_header_overrides`.
Reconciliation status is **not** here — it moved to the per-leg
`txn_leg_recon` overlay (migration 171, ADR-0082), because a transfer
can be cleared in one account while still uncleared in the other.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor — RLS short-circuits at this column for one-hop visibility checks. |
| `origin` | `TEXT` | NOT NULL CHECK in (`manual`, `online_import`, `file_import`) | Icon-level source mechanism (mig 107, ADR-0035). `online_import` covers any live feed (SimpleFIN, MD+ Direct Connect, OFX online); `file_import` covers any file upload (OFX/QFX, CSV, QIF); `manual` is user-typed. Per-provider audit detail lives in `provider_key`. The dedup query in `IngestOrchestrator` scopes by `(ledger_id, provider_key, external_id)` rather than `(ledger_id, origin, external_id)` since `origin` is no longer per-provider. |
| `external_id` | `TEXT` | CHECK `ck_txn_headers_external_id_for_non_manual` (`external_id IS NOT NULL OR origin = 'manual'`) | Universal per-provider stable identifier. Set by every ingest path: Moneydance import → MD txnid; SimpleFIN sync → SimpleFIN transaction id (mig 105); future OFX/QFX/CSV → provider-specific stable id. NULL only on manual rows (`origin='manual'`). ADR-0022 keys at the *event* level. Partial unique index `(ledger_id, external_id) WHERE external_id IS NOT NULL`. Mig 105 added the original CHECK; mig 109 rewrote it as `ck_txn_headers_external_id_for_non_manual` in the post-mig-107 vocabulary (the old `is_user_defined` predicate was retired with that column) so any ingest writer that forgets to populate the column trips at INSERT time. |
| `payee` | `TEXT` | | Raw — never modified after insert. |
| `memo` | `TEXT` | | Raw event memo (Moneydance's `txn.memo`, e.g. "Electronic/ACH Credit"). Per-split memos live on `txn_legs.leg_memo`. |
| `posted_at` | `TIMESTAMPTZ` | NOT NULL | Raw — never modified after insert. |
| `transacted_at` | `TIMESTAMPTZ` | | Raw. |
| `check_number` | `TEXT` | | Paper-check number (Moneydance's `chk` field). |
| `is_pending` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Bank-side state — TRUE while the bank itself has not cleared the transaction. Mutable: the sync service flips T→F in place on a future sync that returns the same FITID with `pending: false` (slice 2c promote-on-clear). Orthogonal to `needs_review`. |
| `is_hidden` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Soft-delete at the header level. The DELETE endpoint flips this to TRUE for any feed-sourced row (i.e. `external_id IS NOT NULL`, which after mig 105 covers SimpleFIN syncs alongside every other ingest path); manual rows (`origin='manual'`, no `external_id`) get hard-deleted instead. Override via `txn_header_overrides.is_hidden`. |
| `is_merged_into` | `UUID` | composite FK `(is_merged_into, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE SET NULL (is_merged_into) | Set when this event lost a merge; NULL = active. Self-referential composite FK `txn_headers_is_merged_into_ledger_fkey` (migration 121). |
| `seq` | `BIGINT` | NOT NULL UNIQUE DEFAULT `nextval('txn_headers_seq')` | Strictly-monotonic insertion-order key (migration 095, ADR-0034 v2). The canonical ordering is `(posted_at, seq)`; within a batch INSERT each row gets a distinct value, eliminating the UUID-tiebreaker ambiguity of the prior `(created_at, id)` design. Immutable (column-level BEFORE-UPDATE trigger). Consumed by the running-balance recompute, `resolved_transactions`, `register_entry_keys`, and the register cursor codec. |
| `online_match_fitid` | `TEXT` | | OFX `<FITID>` — the bank's per-transaction id, unique only within one FI (migration 034). Part of the OFX dedup key `(ledger_id, online_match_fi_id, online_match_fitid)`. OFX-only (mig 105): the MD importer preserves MD's recorded OFX match state; SimpleFIN never touches it (SimpleFIN ids live on `external_id`). |
| `online_match_fi_id` | `TEXT` | | OFX FI id — identifies which institution issued the transaction (migration 034). Composite with `online_match_fitid`. |
| `ingest_action_hint` | `TEXT` | CHECK `ck_txn_headers_ingest_action_hint` (NULL or one of `buy`, `buyx`, `sell`, `sellx`, `dividend_cash`, `dividend_reinvest`, `divx`, `transfer`, `misc`) | Provider-classifier output (ADR-0031 Phase 3c, migration 076). Set by the orchestrator's brokerage branch when sync detects an investment-shape transaction; the editor pre-fills the action picker from it on review. NULL otherwise. |
| `provider_raw_payload` | `JSONB` | | Original provider JSON for this transaction, verbatim from the wire (migration 078, ADR-0031). Diagnostic / classifier-iteration use only. NULL on manual + MD-imported rows and on feed rows synced before the column existed (re-sync backfills). |
| `ingest_shares` | `NUMERIC(28,8)` | | Provider-extracted share count from a file-import wire (OFX investment `UNITS`), migration 113. Populated only on investment rows where the provider carries the data; read by the editor's bank→investment upgrade. NULL for bank/credit and SimpleFIN brokerage rows. |
| `ingest_unit_price` | `NUMERIC(19,6)` | | Provider-extracted per-share price (OFX `UNITPRICE`), migration 113. Also preserves the wire's originally-reported trade price (see `txn_legs.unit_price`, which is derived). Same population rules as `ingest_shares`. |
| `ingest_fee` | `NUMERIC(19,4)` | | Provider-extracted aggregated fee — sum of Commission + Fees + Load + Markup + Markdown (migration 113). NULL when the wire had no fee-shaped fields. Pre-fills the editor's single Fee field (ADR-0029). |
| `ingest_security_ticker_hint` | `TEXT` | | Provider-extracted security identifier (OFX: SECLIST-resolved ticker or raw CUSIP fallback), migration 114. Persisted at ingest so the editor's Accept flow can record a `provider_security_mapping` with the same identifier the next ingest looks up. `resolved_transactions` LEFT JOINs `provider_security_mappings` on `(ledger_id, provider_key, ingest_security_ticker_hint)`. NULL on bank/credit rows, SimpleFIN rows (which re-derive from the payee classifier), and manual entries. |
| `provider_key` | `TEXT` | CHECK (`(origin='manual') = (provider_key IS NULL)`) | Mig 107, ADR-0035. Per-provider audit detail: `simplefin`, `mdplus`, `ofx`, `qif`, `csv`. NULL when `origin='manual'`. Drives the per-provider hover label on the register provenance icon AND is the per-provider dedup scope (mig 105's `external_id` is universal, `provider_key` qualifies it). |
| `is_merge_winner` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Mig 107, ADR-0035. TRUE when at least one other row has `is_merged_into` pointing at this row. Maintained atomically with `is_merged_into` in `TransactionsRepository.PatchAsync`. Drives the merge-winner overlay icon in the register. Monotonic — no unmerge surface today, so once TRUE, stays TRUE. |
| `import_source` | `TEXT` | | Bootstrap-import marker. `'moneydance-import:<file>'` on rows from the MD JSON bootstrap (mig 107 backfilled the bootstrapped rows); NULL on rows born in Coffer + live SimpleFIN sync + future OFX/CSV uploads. Audit / debug only — not surfaced in the register UI. Independent of `origin` (which describes the transaction's source mechanism) and `provider_key` (which identifies the specific provider); mig 109 §2.5. |
| `needs_review` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Bank-feed workflow flag (migration 037 / slice 2c). TRUE on rows the SimpleFIN sync just inserted; the register renders these with a distinct visual treatment (left bar in state-warning palette) until the Approve endpoint clears the bit. Orthogonal to `is_pending` — a row can be `(is_pending=T, needs_review=T)` (bank-pending AND new to user) or `(is_pending=F, needs_review=T)` (cleared, awaiting approval). Manual entries + MD-imported rows write FALSE on insert. Partial index `(ledger_id) WHERE needs_review` backs the future "review-only" register filter + the inbox count badge. |
| `action` | `TEXT` | CHECK in (NULL, `buy`, `buyx`, `sell`, `sellx`, `dividend_cash`, `dividend_reinvest`, `divx`, `transfer`, `misc`, `transfer_shares`) | Investment-event action. Lifted from `txn_legs.investment_action` in migration 047; catalog locked to the 9-action set in migration 062 per ADR-0027 (`buyx`/`sellx`/`divx` first-class; `interest`/`misc_income`/`misc_expense` coalesced into `misc`; `split` moved to `security_splits` by migration 060). Migration 151 (ADR-0065) added the Ledger-native `transfer_shares` (in-kind share move; no MD txntype). One action per event, shared across all postings. NULL on non-investment events. |
| `is_recurring_template` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Migration 124 (ADR-0048): TRUE marks this header as a recurring-series **template**, not a live event. The `live_txn_headers` / `template_txn_headers` views partition on this flag so the register only ever sees live rows; templates fire occurrences via `recurring_transactions.template_header_id`. |
| `recurring_transaction_id` | `UUID` | FK → `recurring_transactions(id)` | Migration 124: on a **fired** occurrence, back-reference to the series that produced it. NULL on ordinary rows and on templates. |
| `occurrence_date` | `DATE` | | Migration 124: the series occurrence date a fired row materializes (paired with `recurring_transaction_id`). |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

**Idempotent partial unique index** `uq_txn_headers_ledger_external_id`:
`(ledger_id, external_id) WHERE external_id IS NOT NULL` — re-import
idempotency at the event level.

**FITID uniqueness** `uq_txn_headers_online_match` (slice 2c.2 / migration 039):
`(ledger_id, online_match_fi_id, online_match_fitid) WHERE online_match_fitid IS NOT NULL` —
promotes the migration-034 lookup index to UNIQUE on the OFX-protocol
columns. Mig 105 reclassified these columns as OFX-only: the MD
importer writes them when preserving MD's recorded OFX match state,
and future OFX/QFX direct importers will write them natively from the
wire format. SimpleFIN does NOT touch these columns — SimpleFIN ids
live on `external_id` (origin-scoped dedup in `IngestOrchestrator`).

### `txn_legs`

Per-account postings. Two legs per posting (one on each account), N
postings per multi-split header. `posting_index` structurally pairs
the two sides of one posting — same value within the header, different
`account_id`. The pair sums to zero (same-currency invariant). User
edits to per-leg fields (amount, leg memo) live in `txn_leg_overrides`.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `header_id` | `UUID` | NOT NULL FK → `txn_headers(id)` ON DELETE CASCADE | |
| `account_id` | `UUID` | NOT NULL FK → `accounts(id, ledger_id)` ON DELETE RESTRICT | Composite FK includes `ledger_id` (migration 049) so a leg can only reference an account in the same ledger as its `txn_headers` row. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Denormalized from `txn_headers.ledger_id` (migration 049). All three composite FKs (header / account / security-when-set) reference `(parent_id, ledger_id)` so the DB structurally refuses a leg whose parents span different ledgers. |
| `posting_index` | `INTEGER` | NOT NULL CHECK ≥ 0 | Pairs the two legs of one posting. For an MD txn with N splits, the legs use `posting_index = 0..N-1`. |
| `leg_memo` | `TEXT` | | Per-split memo (MD's `0.desc`, e.g. "Salary", "Federal Tax"). NULL on single-split events (the view falls back to `txn_headers.memo`). |
| `amount` | `NUMERIC(19,4)` | NOT NULL, CHECK `amount = round(amount, 2)` | Impact on `account_id`. The two legs of a posting sum to zero. **Money is authoritative at 2 decimals** (ADR-0073, migration 159 `ck_txn_legs_amount_scale_2`): for investment share-trades the request `amount` is the real settled cash and lands here exactly. Sub-cent amounts (historically `price × shares` unrounded) are barred — they had leaked into fractional / "-$0.00" running balances; migration 159 scrubbed 56 such legs. The column keeps scale 4 for headroom; the CHECK pins the money model. |
| `security_id` | `UUID` | FK → `securities(id, ledger_id)` ON DELETE RESTRICT | Composite FK includes `ledger_id` (migration 049). Set on legs that participate in an investment posting. On the **holdings-side** leg of a buy/sell/divr it carries the position change. NULL on cash-side legs and non-investment legs. |
| `quantity` | `NUMERIC(25,12)` | | Share count on the holdings-side leg. 12-decimal scale matches MD's 11-decimal display with one digit of buffer; covers reinvest-dividend math (cash ÷ price) without rounding loss. |
| `unit_price` | `NUMERIC(25,12)` | CHECK (`unit_price IS NULL OR unit_price >= 0`) | Per-share price on the holdings-side leg. **Derived metadata** = `amount ÷ \|shares\|` rounded to 6dp (ADR-0073) — it reconciles to the authoritative `amount` rather than driving it, so `unit_price × quantity` need **not** equal `amount` (a rounded wire price against an exact settled total is normal and faithful to the feed). The wire's originally-reported price is preserved separately in `txn_headers.ingest_unit_price`. Non-negative CHECK added in migration 051 — price is a magnitude; the trade direction lives in `quantity` and `amount`. Prior to 051 the importer wrote a signed price (positive cash ÷ signed qty), so every Sell row stored a negative unit_price; a batch of rows across many securities were scrubbed by sign-flip on 2026-05-19. |
| `posting_role` | `TEXT` | CHECK in `{security, income, transfer, fee}` ∪ NULL | Investment posting role marker (migration 056). Stamped by the importer from MD's `invest.splittype` and by the editor when adding postings; both legs of a posting share the same value. NULL on non-investment legs. Source of truth for fee identification — `posting_role='fee'` combined with the brokerage's `is_trade_commission=TRUE` is what folds a fee posting into cost basis. Category is metadata, not behavioral. |
| `account_postings_on_header` | `INT` | NOT NULL DEFAULT 1 | Denormalized count (migration 120, ADR-0046) of this header's distinct postings **on this leg's account** — the ADR-0036 originating-vs-target discriminator. Maintained by `fn_recompute_posting_counts_for_header()` via the recompute interceptor (no trigger). Replaced a per-row correlated `COUNT(DISTINCT posting_index)` subquery in `resolved_transactions`. |
| `header_total_postings` | `INT` | NOT NULL DEFAULT 1 | Denormalized count (migration 120) of the header's total distinct postings (constant across the header's legs). Same maintenance path; a leg is a target-split entry iff `account_postings_on_header < header_total_postings`. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

> **Notes.**
> - Migration 046 dropped the legacy `commission` column — the fee posting is the source of truth for the cash effect, and `lots.unit_cost` carries the apportioned commission for cost-basis math.
> - Migration 047 dropped `investment_action` from this table — action is a *header* property now (one action per event, see `txn_headers.action`). The action CHECK list moved to the header column (9 values at mig 062; `transfer_shares` added at mig 151).
> - Migration 049 added `ledger_id` (denormalized from the header) and converted the FKs on `header_id` / `account_id` / `security_id` into composite `(id, ledger_id)` references. The DB now rejects any leg whose header, account, or security points outside the leg's own ledger — cross-ledger leakage is structurally impossible.

**Unique index** `uq_txn_legs_posting`: `(header_id, posting_index, account_id)`
— enforces the two-legs-per-posting invariant and drives re-import upsert idempotency.

### `txn_header_overrides`

One row per overridden header. NULL columns mean "use feed value". See [decisions/0003-immutable-feed-and-overrides.md](decisions/0003-immutable-feed-and-overrides.md) for the override pattern and [decisions/0022-txn-headers-and-legs.md](decisions/0022-txn-headers-and-legs.md) for the header/leg split.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `header_id` | `UUID` | PK FK → `txn_headers(id)` ON DELETE CASCADE | |
| `ledger_id` | `UUID` | NOT NULL; composite FK `(header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE CASCADE | Denormalized from the parent header (migration 072) so RLS gates on `ledger_id` directly. Composite FK `txn_header_overrides_header_ledger_fkey`. |
| `payee` | `TEXT` | | NULL = use feed |
| `memo` | `TEXT` | | NULL = use feed |
| `posted_at` | `TIMESTAMPTZ` | | |
| `transacted_at` | `TIMESTAMPTZ` | | |
| `check_number` | `TEXT` | | |
| `is_hidden` | `BOOLEAN` | | NULL = use header's value |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Note: `status` was on this table prior to migration 030 but was dropped — reconciliation status is user-action data (the user clicks the badge to cycle), not an override of an imported value. Since migration 171 (ADR-0082) it lives in the per-leg `txn_leg_recon` overlay — reconciliation is per-account (a transfer can be cleared in one account and uncleared in the other), so it can't be a single header value.

### `txn_leg_overrides`

One row per overridden leg. NULL columns mean "use feed value".

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `leg_id` | `UUID` | PK FK → `txn_legs(id)` ON DELETE CASCADE | |
| `ledger_id` | `UUID` | NOT NULL; composite FK `(leg_id, ledger_id)` → `txn_legs(id, ledger_id)` ON DELETE CASCADE | Denormalized from the parent leg (migration 072) so RLS gates on `ledger_id` directly. Composite FK `txn_leg_overrides_leg_ledger_fkey`. |
| `leg_memo` | `TEXT` | | NULL = use feed |
| `amount` | `NUMERIC(19,4)` | | NULL = use feed |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `txn_leg_recon`

Per-**leg** reconciliation overlay (migration 171, ADR-0082). Reconciliation is a per-account activity — a transfer from Checking to Savings can be cleared in Checking while still uncleared in Savings — so status can't be a single header value. It moved off `txn_headers` to here, keyed by `leg_id`. Only real-account legs are ever reconciled; category legs never get a row and resolve to `uncleared`. Follows the ADR-0003 immutable-feed pattern (like `txn_leg_overrides`): the raw `txn_legs` row stays untouched; the user's clearing action lives in the overlay. A leg with no row reads as `uncleared` — `resolved_transactions` COALESCEs it.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `leg_id` | `UUID` | PK FK → `txn_legs(id)` ON DELETE CASCADE | One row per reconciled leg. |
| `ledger_id` | `UUID` | NOT NULL; composite FK `(leg_id, ledger_id)` → `txn_legs(id, ledger_id)` ON DELETE CASCADE | Denormalized for RLS + the composite FK (same shape as `txn_leg_overrides`, mig 072). |
| `status` | `TEXT` | NOT NULL DEFAULT `'uncleared'` CHECK in (`uncleared`, `reconciling`, `cleared`) | The 3-state recon vocabulary (formerly `txn_headers.status`, mig 030). `reconciling` is a workflow / visual aid (MD parity); functionally uncleared for reporting. |
| `cleared_at` | `TIMESTAMPTZ` | | Audit timestamp for the `status='cleared'` transition. DB CHECK `(status='cleared') ⇔ (cleared_at IS NOT NULL)` keeps the pair consistent. |
| `cleared_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | User who marked the leg cleared. NULL when uncleared / reconciling or when the user row was removed. |

**Index** `idx_txn_leg_recon_ledger`: `(ledger_id)`. **RLS**: `FOR ALL TO coffer_app` scoped to the user's `user_ledger_grants` (same policy shape as `txn_leg_overrides`). Writes upsert the register account's leg(s) via `SetReconStatusAsync` / `BulkSetReconStatusAsync`; reads flow through `resolved_transactions` (`COALESCE(lr.status, 'uncleared')`).

### `txn_header_account_balances`

Materialized per-`(header, account)` running balance — the stored `balance_after` (and per-account `net_amount`) the register reads, so a page fetch never re-walks history. Recomputed by the `fn_recompute_balances_for_account` Postgres function invoked at the EF Core call sites; migration 102 dropped the triggers that used to maintain it (see [architecture.md](architecture.md) §6.1), and the per-ledger verify-and-heal endpoint ([operations.md](operations.md) → *Diagnostics*) reconciles any drift. Added in migration 089 (ADR-0034); `net_amount` added in migration 098.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `header_id` | `UUID` | NOT NULL, PK part; composite FK `(header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE CASCADE | Balance rows die with their header. |
| `account_id` | `UUID` | NOT NULL, PK part; composite FK `(account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | An account can't be deleted while it holds balance rows. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor; ties both composite FKs to one ledger. |
| `balance_after` | `NUMERIC(19,4)` | NOT NULL | Running balance on `account_id` after `header_id` applies, ordered by `(posted_at, seq)`. |
| `net_amount` | `NUMERIC(19,4)` | NOT NULL | This header's net effect on `account_id` (migration 098) — the per-account delta, so the register shows the amount and the resulting balance without a second query. |

PRIMARY KEY `(header_id, account_id)`. RLS enabled (migration 089).

### `txn_header_tags`

Many-to-many join: headers ↔ tags. Tags describe the event, not individual legs.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `header_id` | `UUID` | composite FK `(header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE CASCADE | Composite PK component. Composite FK `txn_header_tags_header_ledger_fkey` (migration 072). |
| `tag_id` | `UUID` | composite FK `(tag_id, ledger_id)` → `tags(id, ledger_id)` ON DELETE CASCADE | Composite PK component. Composite FK `txn_header_tags_tag_ledger_fkey` (migration 072) — both the header and the tag must share this row's `ledger_id`. |
| `ledger_id` | `UUID` | NOT NULL | Denormalized (migration 072) so RLS gates on `ledger_id` directly instead of a transitive subquery. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

**RLS** `txn_header_tags_per_user` gates on `ledger_id` directly (migration 072).

### `securities`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor (ADR-0020). Securities are per-ledger so two ledgers can each track the same ticker without trading position data across books. Migration 049 adds a `UNIQUE (id, ledger_id)` so derivative tables (holdings, lots, security_prices, txn_legs) can compose their FKs against `(id, ledger_id)` and the DB rejects any cross-ledger reference. |
| `ticker` | `TEXT` | UNIQUE `(ledger_id, LOWER(ticker)) WHERE NOT NULL` | May be NULL for some mutual funds. Migration 048 adds the per-ledger case-insensitive partial unique index. |
| `cusip` | `TEXT` | UNIQUE `(ledger_id, cusip) WHERE NOT NULL` | 9-char identifier. Migration 048 narrowed the original global uniqueness (migration 002) to per-ledger — multi-tenant deployments can have the same CUSIP in two different users' ledgers. |
| `name` | `TEXT` | NOT NULL | |
| `asset_class` | `TEXT` | CHECK in (NULL, `equity`, `fixed_income`, `multi_asset`, `cash`, `real_assets`, `alternative`) | ADR-0067 (migration 150): **economic class only**. The vehicle (`etf`/`mutual_fund`/…) moved out to `vehicle_type`; migration 150 remediated existing rows (vehicle values → `vehicle_type`, `bond`→`fixed_income`, `cash_equivalent`→`cash`, `other`→`alternative`). |
| `vehicle_type` | `TEXT` | CHECK in (NULL, `mutual_fund`, `etf`, `stock`, `money_market`, `cit`, `separate_account`, `plan_529`, `option`, `cd`, `bond`, `other`) | ADR-0067. The legal wrapper, orthogonal to `asset_class`. |
| `region` | `TEXT` | CHECK in (NULL, `us`, `developed_ex_us`, `emerging`, `global`, `na`) | ADR-0067. |
| `equity_size` | `TEXT` | CHECK in (NULL, `large`, `mid`, `small`) | ADR-0067. Equity style box (size); populated only for equity. |
| `equity_style` | `TEXT` | CHECK in (NULL, `value`, `blend`, `growth`) | ADR-0067. Equity style box (style). |
| `fi_duration` | `TEXT` | CHECK in (NULL, `short`, `intermediate`, `long`) | ADR-0067. Fixed-income character (duration); populated only for fixed_income. |
| `fi_credit` | `TEXT` | CHECK in (NULL, `government`, `investment_grade`, `high_yield`) | ADR-0067. Fixed-income character (credit). |
| `tax_character` | `TEXT` | CHECK in (NULL, `taxable`, `tax_managed`, `tax_exempt`) | ADR-0067. The security's own tax nature (muni exemption, tax-managed funds) — distinct from the account's `tax_status`. |
| `classification_source` | `TEXT` | CHECK in (NULL, `import`, `manual`, `provider`) | ADR-0067. Provenance; any editor classification edit sets `manual` (seed-once vs re-import). |
| `classification_confidence` | `TEXT` | CHECK in (NULL, `known`, `assumed`) | ADR-0067. |
| `exchange` | `TEXT` | | NYSE, NASDAQ, … |
| `is_active` | `BOOLEAN` | NOT NULL DEFAULT TRUE | |
| `external_id` | `TEXT` | per-ledger UNIQUE `(ledger_id, external_id) WHERE external_id IS NOT NULL` | Source-system identifier — for Moneydance imports this is the raw MD UUID. NULL for securities created by other paths. Lets imports re-run idempotently. Added in migration 008; the original global unique was narrowed to per-ledger `uq_securities_external_id_per_ledger` in migration 014. |
| `share_decimals` | `INTEGER` | NOT NULL DEFAULT 4, CHECK (`share_decimals BETWEEN 0 AND 12`) | Per-security precision for share quantities (Moneydance's `dec` field). Stocks/ETFs typically 4; mutual funds typically 5; the investment mapper looks this up to scale raw share-quantity integers. Added in migration 012 with a `[0,6]` ceiling; **widened to `[0,12]` in migration 050** to match the `NUMERIC(25,12)` scale of quantity/price columns — the old ceiling silently misclamped real-world `dec=9` funds back to 4 and produced 100,000×-scaled qty + price on every leg. `holdings.quantity` was bumped from `NUMERIC(19,6)` to `NUMERIC(25,12)` in migration 043. |
| `quote_symbol` | `TEXT` | | Symbol sent to the market-data quote provider when it differs from the display `ticker` (mutual funds, international suffixes). Falls back to `ticker` when NULL. Migration 131 (ADR-0054 D2). |
| `auto_price` | `BOOLEAN` | NOT NULL DEFAULT TRUE | When FALSE, excludes the security from automated price fetches (manual-only, or a hand-pinned stable-NAV fund) without nulling its ticker. Migration 131 (ADR-0054 D2). |
| `quote_symbol_public` | `BOOLEAN` | NOT NULL DEFAULT TRUE, CHECK (`quote_symbol_public OR quote_symbol IS NOT NULL`) | When FALSE, `quote_symbol` is a private / feed-only identifier (e.g. a 529 portfolio number): matched by the no-egress SimpleFIN provider but never sent to an external (egress) provider. The CHECK codifies that a bare ticker is always public — non-public requires a `quote_symbol`. Migration 156 (ADR-0054 D2). |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `holdings`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | UNIQUE `(id, ledger_id)` (migration 049) so `lots.holding_id` can compose its FK. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Denormalized (migration 049). The two composite FKs below enforce that both references resolve to the same ledger — refusing any cross-ledger holdings row at the DB layer. |
| `account_id` | `UUID` | NOT NULL FK → `accounts(id, ledger_id)` ON DELETE RESTRICT | UNIQUE with `security_id`. Composite FK includes `ledger_id` (migration 049). |
| `security_id` | `UUID` | NOT NULL FK → `securities(id, ledger_id)` ON DELETE RESTRICT | Composite FK (migration 049). |
| `quantity` | `NUMERIC(25,12)` | NOT NULL DEFAULT 0 | Bumped from `NUMERIC(19,6)` in migration 043 to match `txn_legs.quantity` and absorb MD's 11-decimal share displays without rounding. |
| `cost_basis` | `NUMERIC(19,4)` | NOT NULL DEFAULT 0 | Cost basis of currently-held shares under the **FIFO method** (ADR-0064, migration 148; was average-cost in 053). `cost_basis = Σ open-lot cost`. `recompute_holdings_cost_basis()` walks the (legs ∪ splits) event stream: buys/transfer-ins add `leg.amount` + commission (same-header `posting_role='fee'` legs, gated by the brokerage's `is_trade_commission`) and open a lot; disposals consume lots FIFO and reduce basis by the consumed cost. Migration 152 (ADR-0065): a disposal only consumes lots that have ARRIVED by its time (creating-leg `posted_at` ≤ disposal), and a `transfer_shares` disposal records no realized gain. Refreshed on every investment write (interceptor) + import. |
| `as_of` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `lots`

Optional depth for tax-lot tracking (specific identification). Captured from Moneydance import day one; selection UI deferred. `leg_id` retargeted from the legacy `transactions(id)` to `txn_legs(id)` in migration 025 (ADR-0022 Phase 2).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Denormalized (migration 049). Both composite FKs below reference `(parent_id, ledger_id)`. |
| `holding_id` | `UUID` | NOT NULL FK → `holdings(id, ledger_id)` ON DELETE CASCADE | Composite FK (migration 049). |
| `leg_id` | `UUID` | NOT NULL FK → `txn_legs(id, ledger_id)` ON DELETE CASCADE | The opening buy / reinvest's holdings-side leg. Composite FK (migration 049); changed RESTRICT → CASCADE in migration 123 so a leg delete carries its derived lot away (a lot is meaningless without its acquisition leg — matches the EF model, which always declared this CASCADE). |
| `quantity` | `NUMERIC(25,12)` | NOT NULL | Remaining open shares in this lot. **FIFO drained on Sell events** (migration 054): for each Sell touching this holding, lots are walked in `acquired_at ASC` order and decremented in place; the original acquired quantity is recoverable via the lot's `leg_id` pointer to the source `txn_legs` row (which is immutable). Bumped from `NUMERIC(19,6)` in migration 043. |
| `unit_cost` | `NUMERIC(25,12)` | NOT NULL CHECK (`unit_cost >= 0`) | Per-share acquisition price for the lot. Migration 056: refreshed by `recompute_holdings_cost_basis()` on every call — `(leg.amount + fee_total) / quantity` when the brokerage's `is_trade_commission=TRUE` (fee_total = same-header `posting_role='fee'` amounts), else `leg.amount / quantity`. So flipping a brokerage's flag propagates to per-lot prices on the next function call, not just to `holdings.cost_basis`. Non-negative CHECK added in migration 051. Migration 053 wiped + rebuilt every lot from `txn_legs` after a partial scrub left the prior values stale ($10.00 flat on every lot of one bond fund). **Migration 180** widened this from `NUMERIC(19,4)` to `NUMERIC(25,12)` (matching the mig-043 precision family) — at 4dp, `quantity × unit_cost` drifted up to `quantity × 5e-5` per lot from the true basis, which accumulated across a fund's reinvestment lots (~$4 on a $570k in-kind transfer). |
| `acquired_at` | `TIMESTAMPTZ` | NOT NULL | Critical for short vs long-term; also the FIFO closure ordering key (migration 054). For an **in-kind transfer-in lot** (`transfer_shares`, ADR-0065) this is the ORIGINAL acquisition date, carried from the source lot — so holding period survives the move. (Availability for consumption is gated separately by the creating leg's header `posted_at`; mig 152.) |
| `is_closed` | `BOOLEAN` | NOT NULL DEFAULT FALSE | TRUE when `quantity` has been drained to zero by FIFO Sell closures (migration 054). |

**Index** `idx_lots_leg_id`: `lots(leg_id)` — speeds the
`DELETE FROM lots WHERE leg_id = ANY(...)` step in the importer's
re-run idempotency path.

### `realized_gains`

Per-disposal realized gain/loss under FIFO (ADR-0064, migration 148). One row per
sell leg; **owned by** `recompute_holdings_cost_basis()`, which deletes +
repopulates the rows in its `(account, security)` scope each run. A
`transfer_shares` disposal records **no** row (in-kind, zero realized gain;
ADR-0065). RLS: per-user via the `security_id IN (SELECT id FROM securities)`
sub-select (the `security_splits` pattern).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | |
| `account_id` | `UUID` | NOT NULL FK → `accounts(id)` ON DELETE CASCADE | The holdings-sibling account (resolve to the brokerage via `accounts.holdings_account_id`). |
| `security_id` | `UUID` | NOT NULL FK → `securities(id)` ON DELETE CASCADE | |
| `sell_leg_id` | `UUID` | NOT NULL FK → `txn_legs(id)` ON DELETE CASCADE; UNIQUE | The holdings-side disposal leg. |
| `sold_at` | `TIMESTAMPTZ` | NOT NULL | |
| `quantity` | `NUMERIC` | NOT NULL | Shares disposed (absolute). |
| `proceeds` | `NUMERIC` | NOT NULL | `−leg.amount`, net of a sell-side fee when the brokerage folds fees. |
| `cost_basis_sold` | `NUMERIC` | NOT NULL | Σ consumed FIFO lot cost. |
| `realized_gain` | `NUMERIC` | NOT NULL | `proceeds − cost_basis_sold`. |
| `proceeds_lt` | `NUMERIC` | NOT NULL DEFAULT 0 | Long-term portion of proceeds — lots held > 1 year at sale (migration 169, ADR-0064). Short-term proceeds = `proceeds − proceeds_lt`. |
| `cost_basis_sold_lt` | `NUMERIC` | NOT NULL DEFAULT 0 | Long-term portion of `cost_basis_sold` (migration 169). Short-term = `cost_basis_sold − cost_basis_sold_lt`. |
| `realized_gain_lt` | `NUMERIC` | NOT NULL DEFAULT 0 | Long-term realized gain = `proceeds_lt − cost_basis_sold_lt` (migration 169). Short-term = `realized_gain − realized_gain_lt`. A sale straddling the 1-year line splits across both buckets; the recompute buckets each consumed FIFO lot by holding period (LT iff `sold_at > acquired_at + 1 year`). |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `security_prices`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Denormalized from `securities.ledger_id` (migration 049). |
| `security_id` | `UUID` | NOT NULL FK → `securities(id, ledger_id)` ON DELETE CASCADE | UNIQUE with `price_date`. Composite FK (migration 049) — a price row can only reference a security in the same ledger. |
| `price` | `NUMERIC(19,4)` | NOT NULL | Closing price (Moneydance's `urt`/`rate`, inverted: MD stores rates as 1/price for securities). Was bumped to `NUMERIC(25,12)` in migration 043, then **constrained back to `NUMERIC(19,4)` in migration 155** (ADR-0070 D8) to match `high`/`low` and scrub 32-bit-float representation noise (e.g. 7.15 stored as 7.150000095367); 4dp is ample for valuation and the DB now enforces it. (The trade price `txn_legs.unit_price` stays `NUMERIC(25,12)` — a per-share execution price legitimately needs >4dp.) |
| `currency_code` | `TEXT` | NOT NULL DEFAULT `'USD'` | |
| `price_date` | `DATE` | NOT NULL | One row per `(security, calendar day)` = that day's closing price. Was `TIMESTAMPTZ` until **migration 154** (ADR-0070), which collapsed it to a UTC calendar `DATE` and deduped multiple same-day rows (a Yahoo EOD close, a SimpleFIN intraday balance, repeat syncs) down to the source-ladder winner. UNIQUE with `security_id`. |
| `high` | `NUMERIC(19,4)` | NULLABLE; `high IS NULL OR low IS NULL OR high >= low` | Intraday high. Sourced from `lo` after MD's reciprocal inversion (urt.lo → price.high). NULL when MD didn't carry one (typical for manually-entered prices). Added in migration 013. |
| `low` | `NUMERIC(19,4)` | NULLABLE; same CHECK as `high` | Intraday low. Sourced from `hi` after inversion. Added in migration 013. |
| `volume` | `BIGINT` | NULLABLE; `volume IS NULL OR volume >= 0` | Share volume traded that day. NULL when MD didn't carry one. `BIGINT` because liquid ETFs exceed 2^31 shares traded. Added in migration 013. |
| `source` | `TEXT` | NOT NULL CHECK in (`import`, `fetch`, `manual`, `simplefin`, `trade`) | Origin of the price row (migration 130, ADR-0054 D2). `simplefin` added in **migration 154** (ADR-0070 D3) as its own source so it can rank below Yahoo/`fetch`; `trade` added in **migration 177** (ADR-0084) for the execution price seeded from an investment trade leg. Per-day source-priority ladder (ADR-0084 D1): `manual` == Yahoo/`fetch` > `trade` > `simplefin` > `import`. The source-aware upsert refuses to overwrite a higher-ranked price for a day with a lower-ranked one (rank in `PriceSource.Rank`; the trade path's rank gate lives in `security_price_upsert_from_trade`). |

### `security_splits`

Stock-split / corporate-action records (migration 060, ADR-0026). Captured at import; the lot fan-out + recompute on a `split` action is a deferred follow-up.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Per-ledger anchor. |
| `security_id` | `UUID` | NOT NULL FK → `securities(id, ledger_id)` ON DELETE CASCADE | Composite FK. |
| `split_at` | `TIMESTAMPTZ` | NOT NULL | Effective date of the split. |
| `ratio` | `NUMERIC(25,12)` | NOT NULL CHECK (`ratio > 0`) | Split ratio (`new_shares / old_shares`, e.g. 2.0 for a 2:1 forward split, 0.5 for a 1-for-2 reverse). Migration 060. The load-bearing field. |
| `old_shares` | `NUMERIC(25,12)` | | Pre-split share basis (audit from MD `csplit.oldshrs`; not used by the recompute). |
| `new_shares` | `NUMERIC(25,12)` | | Post-split share basis (audit from MD `csplit.newshrs`; not used by the recompute). |
| `external_id` | `TEXT` | UNIQUE WHERE NOT NULL | Source-system id for idempotent re-import. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `security_components`

Multi-asset look-through sleeves (ADR-0067, migration 150). One row per
`(security, asset_class × optional region)` sleeve; allocation decomposes a
`securities.asset_class = 'multi_asset'` wrapper through these instead of counting
100% in one bucket (migration 153 made `asset_class = 'multi_asset'` the single
look-through signal, retiring the separate `needs_look_through` flag). Populated
manually today (editor, gated on multi-asset); no provider feed yet. RLS: per-user
via the `security_id IN (SELECT id FROM securities)` sub-select.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `security_id` | `UUID` | NOT NULL FK → `securities(id)` ON DELETE CASCADE | |
| `component_asset_class` | `TEXT` | NOT NULL CHECK in (`equity`, `fixed_income`, `cash`, `real_assets`, `alternative`) | The sleeve's economic class. |
| `component_region` | `TEXT` | CHECK in (NULL, `us`, `developed_ex_us`, `emerging`, `global`, `na`) | Optional sleeve region. |
| `weight` | `NUMERIC(7,4)` | NOT NULL CHECK (`weight >= 0`) | Percent (0–100) of the wrapper in this sleeve. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

**Unique** `uq_security_components`: `(security_id, component_asset_class, component_region)`.

### `provider_security_mappings`

Maps an external provider's security identifier to an internal `securities(id)`, per ledger + provider (migration 075, ADR-0038). The `resolved_transactions` view LEFT JOINs this on `(ledger_id, provider_key, ingest_security_ticker_hint)` so a re-link propagates instantly with no repo-layer backfill.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | Per-ledger anchor. CASCADE (migration 075) — the mapping cleans up when the ledger is deleted. |
| `provider_key` | `TEXT` | NOT NULL CHECK (`length(btrim(provider_key)) > 0`) | Provider family (`simplefin` / `ofx` / market-data provider). |
| `provider_security_id` | `TEXT` | NOT NULL CHECK (`length(btrim(provider_security_id)) > 0`) | The provider's identifier (ticker/CUSIP/symbol). UNIQUE with `(ledger_id, provider_key)`. |
| `security_id` | `UUID` | NOT NULL; composite FK `(security_id, ledger_id)` → `securities(id, ledger_id)` ON DELETE RESTRICT | The internal security it resolves to. RESTRICT — don't orphan a mapping; the user must re-link before deleting the security. |
| `created_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who recorded the mapping (set when the user picks a security on review). SET NULL so attribution outlives the user account. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `ledger_operations`

One recorded operation on a ledger — a feed sync / file import, a Moneydance bootstrap import, a quote refresh, or a snapshot restore. Named `sync_runs` originally (mig 038, SimpleFIN only), generalized to `provider_runs` in mig 132 (ADR-0055; file imports + quote refresh), then renamed to `ledger_operations` in **mig 185** (ADR-0086) when the Moneydance import and snapshot restore — which are not "provider" runs — joined the same per-ledger timeline. Feed syncs and quote refreshes are written two-phase (a `running` row at the start, updated to a terminal state at the end); the one-shot ops (Moneydance import, snapshot restore) write a single already-terminal row via `LedgerOperationsRepository.RecordTerminalAsync`. Backs the Bank feeds settings panel's activity view (`settings/FeedConnectionsPanel.tsx`) and the ledger Activity timeline (`settings/ActivityPanel.tsx`). Per-run metrics live in a single `details` JSONB blob (replaced the typed `txns_*` counters in mig 132).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | RLS anchor — one-hop via `user_ledger_grants`. Survives `feed_connection_id` being nulled by a disconnect. |
| `feed_connection_id` | `UUID` | FK → `feed_connections(id)` ON DELETE SET NULL | NULL for runs not tied to a connection (file imports, quote refresh) or after a disconnect. |
| `family` | `TEXT` | NOT NULL CHECK in (`ingest`, `quote`, `snapshot`) | Operation family — `ingest` (feed/file/Moneydance import), `quote` (price refresh), or `snapshot` (restore). `snapshot` added in mig 185 (ADR-0086); `ingest`/`quote` from mig 132/135 (ADR-0055). |
| `provider_key` | `TEXT` | NOT NULL | Concrete operation within the family — `simplefin` / `ofx` / `qif` / `moneydance` / `simplefin-holdings` (ingest), market-data provider (quote), or `snapshot-restore` (snapshot). NOT NULL (mig 132; backfilled for existing ingest rows). |
| `triggered_via` | `TEXT` | NOT NULL CHECK in (`manual`, `file-upload`, `post-sync`, `scheduled`) | How the operation started (mig 132). `file-upload` is the file/Moneydance-import path; `manual` covers a snapshot restore. Refines the "who" (`triggered_by_user_id`) with "how" (ADR-0054 D4 / ADR-0055). |
| `triggered_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who triggered it. For `scheduled` runs, the user whose schedule/prefs drove it. |
| `status` | `TEXT` | NOT NULL DEFAULT `'running'` CHECK in (`running`, `completed`, `partial`, `failed`, `needs_reauth`) | Terminal state written in the closing UPDATE. `partial` = 2xx with non-empty errlist (per-institution `con.auth` etc. recorded in `ledger_operation_errors`); `needs_reauth` = 403. A partial UNIQUE index keeps one `running` row per connection (concurrent runs → 422 `feed-sync-in-progress`); a lazy reaper sweeps stale `running` rows to `failed`. |
| `details` | `JSONB` | NOT NULL DEFAULT `'{}'::jsonb` | Per-operation metrics (replaced the typed `txns_*` counters in mig 132). Feed/file ingest: `{txns_fetched, txns_inserted, txns_promoted, txns_already_known, txns_still_pending, txns_skipped}`. Quote: `{prices_inserted, prices_updated, …}`. Moneydance import: `{duration_seconds, <step_name>: <written>, …}`. Snapshot restore: `{snapshot_id}`. |
| `error_message` | `TEXT` | | Non-null on `status='failed'` — the `SimpleFinException` message or the access-URL decrypt error. |
| `started_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `completed_at` | `TIMESTAMPTZ` | | NULL while `status='running'`. |

**RLS** (migration 038, carried through the rename): `ledger_id IN (SELECT ledger_id FROM user_ledger_grants WHERE user_id = current_app_user_id())`.

### `ledger_operation_errors`

One row per provider `errlist[]` entry captured during a run (`sync_run_errors` → `provider_run_errors` in mig 132 → `ledger_operation_errors` in mig 185). Only feed syncs populate this; the one-shot import/restore ops record no per-error children. The SPA expands a run to show these — e.g. a multi-institution SimpleFIN connection where one institution returns `con.auth` ("Connection to … may need attention") surfaces here as a `partial` run with these rows.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_operation_id` | `UUID` | NOT NULL FK → `ledger_operations(id)` ON DELETE CASCADE | (FK column renamed from `sync_run_id`.) |
| `code` | `TEXT` | NOT NULL | Structured `prefix.subcode` from the provider (e.g. `con.auth`, `auth.revoked`, `fi.maintenance`). |
| `message` | `TEXT` | NOT NULL | Human message verbatim from the bank/provider. |
| `simplefin_connection_id` | `TEXT` | | Optional SimpleFIN-level scope id (the per-institution `MBR-…`). |
| `simplefin_account_id` | `TEXT` | | Optional SimpleFIN-level scope id. |
| `ledger_id` | `UUID` | NOT NULL; part of the composite FK `(ledger_operation_id, ledger_id)` → `ledger_operations(id, ledger_id)` ON DELETE CASCADE | Denormalized for RLS/query (migration 072; FK column renamed in mig 132). It is **not** a direct FK to `ledgers(id)` — coherence is enforced by the composite FK to the parent run. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `ledger_operation_promotions`

One row per promote-on-clear event (`sync_run_promotions` → `provider_run_promotions` in mig 132 → `ledger_operation_promotions` in mig 185): the bank cleared a previously-pending FITID at a different amount than the original hold (tip, FX shift). Backs the per-run "Cleared at different amounts" detail.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_operation_id` | `UUID` | NOT NULL FK → `ledger_operations(id)` ON DELETE CASCADE | (Renamed from `sync_run_id`.) |
| `header_id` | `UUID` | NOT NULL FK → `txn_headers(id)` ON DELETE CASCADE | Audit follows the row. |
| `was_amount` | `NUMERIC(19,4)` | NOT NULL | Pre-promotion source-side leg amount. |
| `became_amount` | `NUMERIC(19,4)` | NOT NULL | Post-promotion source-side leg amount. |
| `ledger_id` | `UUID` | NOT NULL; part of the composite FK `(ledger_operation_id, ledger_id)` → `ledger_operations(id, ledger_id)` ON DELETE CASCADE | Denormalized for RLS/query (migration 072; FK column renamed in mig 132). It is **not** a direct FK to `ledgers(id)` — coherence is enforced by the composite FK to the parent run. |
| `promoted_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `recurring_transactions`

Templates for scheduled/recurring transactions. Originates from Moneydance "reminders" on import.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL | Denormalized (migration 072) so RLS gates on `ledger_id` directly; anchors the composite FKs below. Added `UNIQUE (id, ledger_id)` (`uq_recurring_transactions_id_ledger`, mig 124) so `txn_headers.recurring_transaction_id` can compose a ledger-scoped FK. |
| `source_account_id` | `UUID` | NULL; composite FK `(source_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | Display/query pointer that drives the reminders agenda amount. **Dropped in migration 124, then re-added NULLABLE in 125/126** (`recurring_transactions_source_account_fkey`) — the template legs pin the real account, so this is a convenience pointer, not the source of truth. |
| `loan_account_id` | `UUID` | NULL; composite FK `(loan_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | For a managed loan-payment reminder (`is_loan_reminder`), the loan account whose principal/interest/escrow split is computed from `loan_terms` + current balance (migration 168, ADR-0050 extension). Partial unique index `uq_recurring_loan_account (loan_account_id) WHERE loan_account_id IS NOT NULL` — at most one managed reminder per loan account. |
| `start_date` | `DATE` | NOT NULL | |
| `end_date` | `DATE` | | |
| `next_due_date` | `DATE` | | Updated as instances are acknowledged |
| `last_acknowledged_date` | `DATE` | | |
| `is_loan_reminder` | `BOOLEAN` | NOT NULL DEFAULT FALSE | |
| `is_active` | `BOOLEAN` | NOT NULL DEFAULT TRUE | |
| `origin` | `TEXT` | NOT NULL DEFAULT `'manual'` CHECK in (`manual`, `moneydance_import`) | |
| `external_id` | `TEXT` | per-ledger UNIQUE `(ledger_id, external_id) WHERE external_id IS NOT NULL` | Source-system identifier — for Moneydance imports this is the raw MD reminder UUID. NULL for reminders created manually. Lets imports re-run idempotently. Added in migration 013; re-scoped to per-ledger (`uq_recurring_external_id`) in migration 162. |
| `rrule` | `TEXT` | | RFC-5545 recurrence rule (migration 124, ADR-0047). The **authoritative** schedule, expanded by the C# `RecurrenceExpander`. It replaced the discrete `frequency`/`monthly_day`/`weekly_dow`/`interval_units` columns, which migration 124 **dropped** (their shape now lives on the template header + legs + this rrule). NULL on a reshaped-but-not-yet-reimported row (dormant until the importer re-materializes it). |
| `source_payload` | `JSONB` | | Raw MD reminder payload retained for audit, lossless (mig 124). |
| `auto_commit_days_before` | `INTEGER` | | If set, an occurrence auto-fires this many days before its due date (mig 124, ADR-0047); NULL = manual confirm only. |
| `template_header_id` | `UUID` | composite FK `(template_header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE RESTRICT, DEFERRABLE INITIALLY DEFERRED | The template `txn_headers` row this series fires from (mig 124, ADR-0048 — templates live in `txn_headers` with `is_recurring_template=true`, surfaced via the `template_txn_headers` view). DEFERRABLE because it and `txn_headers.recurring_transaction_id` reference each other (resolved at commit during snapshot restore). |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `recurring_occurrence_exceptions`

Per-occurrence exceptions to a recurring series (migration 125, ADR-0047) — a skipped or already-materialized date so the expander doesn't re-offer it.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Per-ledger anchor. |
| `recurring_transaction_id` | `UUID` | NOT NULL FK → `recurring_transactions(id)` ON DELETE CASCADE | The series. |
| `occurrence_date` | `DATE` | NOT NULL | The excepted date. UNIQUE with `recurring_transaction_id`. |
| `created_by_user_id` | `UUID` | FK → `users(id)` | Who skipped it. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `loan_terms`

Amortization parameters for a loan account, **1:1** with `accounts` (one row per `account_type='loan'` account). Added in migration 127 ([decisions/0050-account-editor-and-loan-amortization.md](decisions/0050-account-editor-and-loan-amortization.md)). Moneydance keeps these on the loan *account* (`obj_type:"o"`), not the reminder. The current balance owed is **not** stored here — it is derived from the loan account's posted leg sum (ADR-0050 D3), so the principal/interest split recomputes each period as the balance amortizes. The MD importer **seeds** this row once (`ON CONFLICT (account_id) DO NOTHING`); Coffer owns it thereafter (ADR-0050 D10). Read-only at the API layer (the importer is the sole writer); the API reads it to compute the per-occurrence loan reminder split (principal / interest / escrow).

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `account_id` | `UUID` | PK; FK `(account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE CASCADE | The loan account. Terms are meaningless once it's gone, hence CASCADE. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | Phase A anchor (ADR-0020); part of the ledger-coherent composite FKs (mig 049/072 pattern). |
| `original_principal` | `NUMERIC(20,4)` | NOT NULL CHECK > 0 | Initial loan principal (MD `init_principal`). |
| `annual_interest_rate` | `NUMERIC(9,4)` | NOT NULL CHECK ≥ 0 | Annual rate as a **percent**, e.g. `3.6500` (MD `int_rate`). |
| `points` | `NUMERIC(9,4)` | NOT NULL DEFAULT 0 CHECK ≥ 0 | Loan points (MD `points`). |
| `payment_count` | `INTEGER` | NOT NULL CHECK > 0 | Total scheduled payments / term (MD `num_payments`). |
| `payments_per_year` | `INTEGER` | NOT NULL CHECK > 0 | Payment frequency, e.g. 12 (MD `pmts_per_year`). |
| `first_payment_date` | `DATE` | NULL | First payment date when known. |
| `escrow_amount` | `NUMERIC(20,4)` | NOT NULL DEFAULT 0 | Per-period escrow (taxes/insurance), added to P&I (MD `escrow_payment`). The **current** value — escrow is not amortized. |
| `interest_account_id` | `UUID` | NULL; FK `(interest_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | Category the interest portion posts to (MD `interest_account_id`). RESTRICT (not SET NULL) because the composite includes NOT-NULL `ledger_id`. |
| `escrow_account_id` | `UUID` | NULL; FK `(escrow_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | Account/category the escrow portion posts to (MD `escrow_account_id`). |
| `payment_is_computed` | `BOOLEAN` | NOT NULL DEFAULT TRUE | TRUE = derive the fixed payment via amortization (`P·r/(1−(1+r)⁻ⁿ)`); FALSE = use `fixed_payment` (MD `calc_pmt`). |
| `fixed_payment` | `NUMERIC(20,4)` | NULL | The fixed total payment when `payment_is_computed = FALSE`. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

RLS: flattened per-ledger policy `loan_terms_per_user` (same shape as migrations 071/072/075/125). Covered by `fn_ledger_snapshot_payload` / `fn_ledger_snapshot_restore` (added alongside the table in migration 127).

### `user_preferences`

General per-(user, ledger) preference store, one table not a table per feature (migration 134, [decisions/0057-user-preferences-store.md](decisions/0057-user-preferences-store.md)). One row per `(user_id, ledger_id, namespace)`; `value` is a namespace-typed JSON document, so a new preference area is a new `namespace` (+ a typed record in the API), never a schema change. Consumers so far: the `quotes` namespace (`{ "enabledProviders": [...] }`) — the per-ledger opt-in for external market-data providers (Yahoo), which **supersedes** the ADR-0054 `Quotes:Yahoo:Enabled` config gate — and the `dashboard` namespace (`{ "widgets": [{ "key", "visible" }] }`) — the per-ledger Overview layout (order + show/hide), ADR-0056 slice 3.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `user_id` | `UUID` | PK part; FK → `users(id)` ON DELETE CASCADE | The owning user — preferences are personal. |
| `ledger_id` | `UUID` | PK part; FK → `ledgers(id)` ON DELETE CASCADE | The ledger the preference applies to. |
| `namespace` | `TEXT` | PK part; CHECK `<> ''` | Preference area, e.g. `quotes`. The API owns the known set. |
| `value` | `JSONB` | NOT NULL DEFAULT `'{}'` | Namespace-typed JSON document. |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

RLS: own-user **and** per-ledger-visibility policy `user_preferences_per_user` — a user reads/writes only their own rows, for ledgers they can see. **Not** part of `fn_ledger_snapshot_*` — per-user UI config, not ledger financial data, so a snapshot restore leaves it untouched. The scheduled-refresh worker (ADR-0054 B) reads the **configuring user's** `quotes` row (own-user RLS rules out a system-user pref).

### `scheduled_jobs`

The single per-ledger daily scheduler (migration 136). One row per `(ledger_id, job_type)`; a single `SchedulerService` background worker polls `next_run_at` and dispatches each due row by `job_type` to a registered handler. Generalizes the former `quote_schedules` (mig 135, migrated in as `job_type='quote-refresh'`). Job types:
- **`quote-refresh`** (ADR-0054 B) — a quote refresh as `configured_by_user_id` (using that user's `quotes` opt-in), recorded `triggered_via='scheduled'`.
- **`snapshot`** (ADR-0037) — an `auto` snapshot for the ledger (5-cap eviction applies); replaces the original fixed-weekly auto-snap, which was a no-op under RLS.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `ledger_id` | `UUID` | PK part; FK → `ledgers(id)` ON DELETE CASCADE | The ledger. |
| `job_type` | `TEXT` | PK part; CHECK `IN ('quote-refresh','snapshot')` | The scheduled work. |
| `enabled` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Whether it runs (opt-in). |
| `hour_local` | `SMALLINT` | NOT NULL DEFAULT 19, CHECK 0–23 | Time-of-day hour in `timezone` (migration 136). |
| `minute_local` | `SMALLINT` | NOT NULL DEFAULT 0, CHECK 0–59 | Time-of-day minute in `timezone`. |
| `timezone` | `TEXT` | NULL | IANA tz (e.g. `America/New_York`) the time is interpreted in — the user's browser tz at save (mig 137). NULL → server-local fallback. |
| `configured_by_user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE RESTRICT | Run attribution / pref resolution. |
| `last_run_at` / `next_run_at` | `TIMESTAMPTZ` | NULL | Worker bookkeeping; `next_run_at` NULL when disabled. |
| `created_at` / `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Partial index `idx_scheduled_jobs_due (next_run_at) WHERE enabled` for the worker's "what's due?" query. RLS: per-ledger-visibility policy `scheduled_jobs_per_ledger` (a ledger setting, not a personal pref). The worker reads via the BYPASSRLS service role — a background tick has no request user, so the RLS app role would be fail-closed.

### `global_scheduled_jobs`

Deployment-wide (non-ledger) sibling of `scheduled_jobs` (migration 139, ADR-0060). One row per `job_type` — currently just `backup` (the daily whole-DB encrypted backup). The same `SchedulerService` polls it via an `IGlobalScheduledJobHandler` alongside the per-ledger jobs (one worker, two scopes). Service-role only — there's no ledger to scope it to, so it has no RLS app-role access.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `job_type` | `TEXT` | PK | The global job, e.g. `backup`. |
| `enabled` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Opt-in. |
| `hour_local` | `SMALLINT` | NOT NULL DEFAULT 3, CHECK 0–23 | Time-of-day hour in `timezone` (migration 139). |
| `minute_local` | `SMALLINT` | NOT NULL DEFAULT 0, CHECK 0–59 | Time-of-day minute in `timezone`. |
| `timezone` | `TEXT` | NULL | IANA tz the time runs in; NULL → server-local. |
| `passphrase_ciphertext` | `BYTEA` | NULL | The backup passphrase **sealed under the master KEK** (ADR-0060) — the single restore secret, set once by an admin; drives both scheduled and on-demand backups. Never stored plaintext. |
| `configured_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | The admin who configured it. SET NULL (not RESTRICT) so the deployment's backup schedule + passphrase survive the configuring admin's removal — attribution is nice-to-have, the schedule is operationally critical. |
| `last_run_at` / `next_run_at` | `TIMESTAMPTZ` | NULL | Worker bookkeeping. |
| `created_at` / `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `system_settings`

Deployment-global key/value settings (migration 147, ADR-0063 §D8) — install-wide config an admin changes from the System-settings UI instead of env/compose. Service-role only (RLS enabled+forced with **no policy** = deny-all for `coffer_app`; `RequireAdmin` is the boundary). `value` is JSONB so the store stays general without a schema change per setting. Seeded with `mcp.enabled = false` (the MCP runtime toggle, read at startup).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `key` | `TEXT` | PK, CHECK (`key <> ''`) | Setting name, e.g. `mcp.enabled`. |
| `value` | `JSONB` | NOT NULL | The setting value (a boolean today, number/object later). |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `updated_by` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who last changed it; SET NULL so the audit column survives a user delete. |

`REVOKE ALL FROM coffer_app`; `GRANT ALL TO coffer_service`.

### `mcp_access_tokens`

MCP bearer tokens for the **Connected apps** surface (ADR-0063 / ADR-0081). Opaque reference tokens — only the `SHA-256` is stored — that scope an MCP client's access; a presented token hashes to a single-row lookup. v1 is read-only (`coffer.read`). Owned by a user; listable / revocable from Account → Security → Connected apps. Added in migration 145.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | Owning user. |
| `name` | `TEXT` | NOT NULL CHECK (`name <> ''`) | User label, e.g. "Claude Desktop (laptop)". |
| `token_hash` | `BYTEA` | NOT NULL UNIQUE | `SHA-256` of the opaque token sent in the `Authorization` header. Plaintext never persists. |
| `scopes` | `TEXT` | NOT NULL DEFAULT `'coffer.read'` | Space-separated OAuth-style scopes; v1 read-only. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `last_used_at` | `TIMESTAMPTZ` | | Bumped on each authenticated `/mcp` request. |
| `expires_at` | `TIMESTAMPTZ` | | NULL = never expires (lives until revoked); a past expiry is treated as revoked. |
| `revoked_at` | `TIMESTAMPTZ` | | Set on revoke. |

RLS enabled + forced (migration 145).

### `mcp_tool_invocations`

Per-call audit of MCP **write**-tool invocations (migration 170, ADR-0081 D3; two-phase since migration 178, ADR-0086): one row per write tool an AI client runs — reads are not recorded. Written by `McpAuditRecorder` via the service role (like `mcp_access_tokens` — auditing must record reliably, independent of the caller's RLS write-check); the own-user RLS policy is defence-in-depth for any `coffer_app` read. The row is **two-phase**: a `pending` row is written *before* the tool runs (so a committed change always has a row) and finalized to `ok`/`error`/`cancelled` after — both phases on `CancellationToken.None`, so a client cancel/timeout can never drop the record. `AuditRetentionService` (a hosted `BackgroundService`) prunes rows older than `Api:AuditRetentionDays` (default 180) daily, alongside `ledger_operations`.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT gen_random_uuid() | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | The acting user (the bearer's owner). |
| `tool_name` | `TEXT` | NOT NULL, CHECK `<> ''` | e.g. `set_transaction_tags`. |
| `arguments` | `TEXT` | NULL | Serialized, length-bounded JSON of the call args (write-tool args carry no credentials, so bounded rather than deeply redacted). |
| `status` | `TEXT` | NOT NULL DEFAULT `'pending'`, CHECK in (`pending`,`ok`,`error`,`cancelled`) | Lifecycle (ADR-0086): `pending` attempt (pre-call) → terminal after. A lingering `pending` row is a visible "started, outcome unknown" (hang/crash). The sole outcome field — the redundant `is_error` boolean was retired in migration 184. |
| `result` | `TEXT` | NULL | Bounded result / error summary. |
| `ledger_id` | `UUID` | NULL (no FK) | Best-effort lift of the `ledgerId` arg for filtering; deliberately no FK so the audit survives a ledger delete. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Attempt (pre-call) instant. |
| `completed_at` | `TIMESTAMPTZ` | NULL; CHECK `(status = 'pending') = (completed_at IS NULL)` | Finalize instant; NULL while pending. |
| `trace_id` | `TEXT` | NULL | `HttpContext.TraceIdentifier`, correlating the row with the app log line + client response. |

### `drive_sync`

Deployment-wide singleton holding the Google Drive backup-destination config (migration 142, ADR-0062). One row, `id = 1` (CHECK pins it). Service-role only with the same RLS-deny-all posture as `global_scheduled_jobs` — there's no ledger to scope it to. The OAuth blob is sealed under the master KEK (never plaintext) and re-wrapped by a master-KEK rotation (System → Encryption, ADR-0092 D4). ④a populates connect/disconnect + sync status; the `enabled` toggle drives auto-push. The Drive folder MIRRORS the local backup set (ADR-0074) — migration 160 dropped the former Drive-side retention columns, since there is no separate Drive retention.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `SMALLINT` | PK, CHECK (`id = 1`) | Singleton guard. |
| `enabled` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Push-on-backup toggle (consumed in ④b+c). Set true on connect. |
| `oauth_ciphertext` | `BYTEA` | NULL | `client_id` + `client_secret` + `refresh_token` as JSON, **sealed under the master KEK** (ADR-0062 D3). NULL when disconnected. |
| `folder_id` / `folder_name` | `TEXT` | NULL | The Coffer-owned Drive folder (folder isolation). `folder_name` is `Coffer Backups [install_id]`. |
| `install_id` | `TEXT` | NULL | Stable opaque per-install id (mig 143), set once on first connect and kept across disconnect. Embedded in `folder_name` so two installs sharing one OAuth client + Google account land in **distinct** folders. |
| `connected_email` | `TEXT` | NULL | The connected Google account, for display. |
| `last_sync_at` | `TIMESTAMPTZ` | NULL | Last push attempt. |
| `last_sync_status` / `last_sync_error` | `TEXT` | NULL | `ok` / `error` + detail. |
| `configured_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | The admin who connected it. |
| `created_at` / `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `backup_settings`

Deployment-wide singleton holding the backup **retention policy** (migration 161, ADR-0074). One row, `id = 1` (CHECK pins it). Service-role only (RLS deny-all), same posture as `drive_sync`. This is the **single source of truth** for retention: it governs local backup pruning (`BackupStore`) AND, transitively, the Google Drive mirror (which just reflects the local set). Admin-editable via `GET/PUT /api/admin/backups/retention`; replaced the former startup-only `ApiOptions` retention config.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `SMALLINT` | PK, CHECK (`id = 1`) | Singleton guard. |
| `retention_daily` / `retention_weekly` / `retention_monthly` | `SMALLINT` | NOT NULL DEFAULT 7 / 8 / 12, CHECK (`≥ 0`) | GFS tiers: keep daily backups for N days, then the newest of each week for N weeks, then the newest of each month for N months; older is pruned. |
| `configured_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | The admin who last set it. |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `backup_pins`

Admin "never delete" pins for backup artifacts (migration 144, ADR-0062 ④b+c). Keyed by artifact id (the `.cofferbak` filename stem, shared by the local file and its Drive copy), so one pin excludes the artifact from **both** local and Drive retention sweeps. Deployment-global, service-role only (RLS deny-all), same posture as `drive_sync`.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `artifact_id` | `TEXT` | PK | The backup id (`.cofferbak` stem). |
| `pinned_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who pinned it. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `ledger_snapshots`

Server-side capped snapshots of the user-curated ledger graph (migration 111, ADR-0037) — the in-place recovery half of the backup design (weekly auto-snaps + manual). Capped at 5 per ledger (auto-evicted first) in `LedgerSnapshotsRepository`, **not** a DB constraint (the eviction rule is nicer in LINQ than SQL). `content` is gzip JSON of the in-scope per-ledger tables built by `fn_ledger_snapshot_payload`; operational state (feed_connections, ledger operations, sessions) and the materialized `txn_header_account_balances` are excluded — balances are re-derived on restore.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `uuid` | PK | App-supplied. |
| `ledger_id` | `uuid` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | |
| `created_at` | `timestamptz` | NOT NULL DEFAULT now() | |
| `created_by_user_id` | `uuid` | NOT NULL FK → `users(id)` ON DELETE RESTRICT | The system user for auto-snaps; the acting user for manual snaps. |
| `kind` | `text` | NOT NULL CHECK in (`auto`, `manual`) | Auto-snaps are evicted before manual ones. |
| `description` | `text` | | Optional label. |
| `schema_version` | `text` | NOT NULL | DB schema version at capture; restore refuses on mismatch. |
| `content` | `bytea` | NOT NULL | gzip-compressed JSON of the in-scope tables. |
| `content_size_uncompressed` | `integer` | NOT NULL CHECK (`>= 0`) | Uncompressed byte count for the SPA's "N MB before compression" display without decompressing. |

Indexes: `idx_ledger_snapshots_ledger_created (ledger_id, created_at DESC)`, `idx_ledger_snapshots_ledger_kind_created (ledger_id, kind, created_at)`. No RLS — access is mediated by the repository/service layer. Migration 112 later extended the snapshot scope (recurring transactions + splits).

### `user_account_groups`

User-curated sidebar "tabs" — named account groups scoped per `(user, ledger)`; each holds any subset of one ledger's accounts (an account can belong to several groups). The implicit "All" tab is virtual (not a row). Migration 033.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | The owning user — groups are personal, even on a shared ledger. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | The ledger the group is scoped to. |
| `name` | `TEXT` | NOT NULL CHECK (`length(trim(name)) > 0`) | Tab label. |
| `sort_order` | `INTEGER` | NOT NULL DEFAULT 0 | Sidebar render order (ASC). |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Constraints/indexes: UNIQUE `uq_user_account_groups_name (user_id, ledger_id, lower(name))`; index `idx_user_account_groups_listing (user_id, ledger_id, sort_order, created_at)`; UNIQUE `uq_user_account_groups_id_ledger (id, ledger_id)` (migration 072) so the members table can compose a ledger-coherent composite FK. RLS `user_account_groups_self` pins to the owning user AND an accessible ledger grant.

### `user_account_group_members`

N:M membership between `user_account_groups` and `accounts`. Migration 033; `ledger_id` added in migration 072 to flatten RLS and enforce ledger coherence via composite FKs.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `group_id` | `UUID` | NOT NULL; composite FK `(group_id, ledger_id)` → `user_account_groups(id, ledger_id)` ON DELETE CASCADE | Composite FK `user_account_group_members_group_ledger_fkey` (migration 072). |
| `account_id` | `UUID` | NOT NULL; composite FK `(account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE CASCADE | Composite FK `user_account_group_members_account_ledger_fkey` (migration 072) — the account must share the group's ledger. |
| `ledger_id` | `UUID` | NOT NULL | Denormalized from the group (migration 072). |
| `added_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

PRIMARY KEY `(group_id, account_id)`; index `idx_user_account_group_members_account (account_id)` (reverse "which groups is this account in?"). RLS `user_account_group_members_self` gates on `ledger_id` directly (migration 072).

### `tags`

Tags are modelled at the event (header) level under ADR-0022. The
junction table is `txn_header_tags`, documented above with the other
ADR-0022 tables.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor (ADR-0020). |
| `name` | `TEXT` | NOT NULL | UNIQUE per ledger via `uq_tags_name_per_ledger`. The pre-Phase-A global UNIQUE on `name` is dropped by migration 014. |
| `color` | `TEXT` | | Optional UI hint |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

---

## Views

### `resolved_transactions`

Application code reads from this view exclusively. Projects from
`txn_headers` + `txn_legs` (ADR-0022; rewritten by migration 023)
with header-level + leg-level overrides coalesced in. The column shape
matches the pre-ADR-0022 view byte-for-byte so the EF `ResolvedTransactionView`
entity + every repository / DTO consumer continues to work.

Key column derivations:

| Column | Source |
|---|---|
| `id`, `account_id`, `investment_action` | `txn_legs` (one row per leg) |
| `balance_after` | `txn_header_account_balances` joined on `(header_id, account_id)` (ADR-0034 / mig 091). Same value for every leg of `(account, header)`. |
| `payee`, `memo`, `posted_at`, `transacted_at`, `check_number`, `external_id` | `txn_headers` (with `txn_header_overrides` COALESCE'd in) |
| `status` | `COALESCE(txn_leg_recon.status, 'uncleared')` — the per-leg recon overlay (migration 171, ADR-0082). Per-account: each leg reconciles independently, so a transfer can be cleared on one side and uncleared on the other. A leg with no overlay row reads `uncleared`. |
| `cleared_at`, `cleared_by_user_id` | `txn_leg_recon` (the leg's overlay row) — paired with `status` via the DB CHECK `(status='cleared') ⇔ (cleared_at IS NOT NULL)` |
| `memo` precedence | `COALESCE(leg_override.leg_memo, leg.leg_memo, header_override.memo, header.memo)` — leg memo wins on multi-split events; single-leg events leave `leg.leg_memo` NULL and the chain falls back to header memo |
| `amount` | `COALESCE(leg_override.amount, leg.amount)` |
| `is_hidden` | `COALESCE(header_override.is_hidden, header.is_hidden, FALSE)` |
| `has_overrides` | `(header_override.header_id IS NOT NULL OR leg_override.leg_id IS NOT NULL)` |
| `counterparty_id`, `counterparty_account_id`, `counterparty_account_name`, `counterparty_account_type` | Structural via `LEFT JOIN txn_legs other ON other.header_id = leg.header_id AND other.posting_index = leg.posting_index AND other.id != leg.id` — finds the other side of the posting. `counterparty_account_name` uses the recursive `account_path()` function for the full root-to-leaf category path. |
| `txn_group_id` | `CASE WHEN EXISTS (SELECT 1 FROM txn_legs WHERE header_id = h.id AND posting_index > 0) THEN h.id ELSE NULL END` — emits `header.id` only when the header has multiple postings (preserves the pre-ADR-0022 grouping semantics expected by the API's AssembleEntries). |
| `leg_index` | `txn_legs.posting_index` |
| `tags` | `ARRAY-agg from txn_header_tags + tags` |
| `header_id` | `txn_headers.id` (added in migration 028 — unconditional header identity, used by the SPA's inline-edit flow which targets a header even for single-row entries where `txn_group_id` is NULL). |
| `created_at` | `txn_legs.created_at` — leg insertion timestamp; all legs of a single header share this value (one transaction, one `now()`). Drives the same-day creation-order tiebreaker in `register_entry_keys` (migration 029). |

Marked `security_invoker = true` so RLS evaluates as the caller's role (`coffer_app`), not the view owner's.

See `db/migrations/023_swing_view_and_trigger_onto_headers_and_legs.sql` for the full SQL.

---

## Functions

The API uses Postgres-side functions for read paths whose shape doesn't
fit cleanly in LINQ. Every function is bound to EF via
`HasDbFunction` in `AppDbContext.OnModelCreating`; the C# instance
method on `AppDbContext` is a translation anchor only — its body is
never executed.

### `register_entry_keys(p_account_id, p_ledger_id, p_cursor_entry_key, p_cursor_seq, p_direction, p_limit, p_hidden, p_search, p_date_from, p_date_to, p_amount_min, p_amount_max, p_security_id, p_tag, p_category_id, p_status, p_today, p_sort_column, p_sort_dir)`

Returns one row per **register entry** in the requested sort order (default
`(posted_at DESC, seq DESC)`) — ready for keyset pagination over the windowed
register. `entry_key` is the `header_id` when the viewing account owns all of
the header's postings, else the leg `id` (a target-split leg shows as its own
entry). Mig 166 (dynamic-SQL body) parameterizes the sort column + direction and
switched the cursor to be **entry-key based** — the function derives the cursor
entry's sort value internally, so the opaque cursor stays sort-agnostic and the
RETURN shape is unchanged.

| Parameter | Meaning |
|---|---|
| `p_account_id` (UUID, nullable) | Narrow to one account; NULL = all visible accounts in the ledger |
| `p_ledger_id` (UUID) | Ledger scope (required for the EXISTS-on-accounts guard when account_id is NULL) |
| `p_cursor_entry_key`, `p_cursor_seq` | Boundary entry's key + seq from the previous page (mig 166: entry-key based, so the cursor is sort-agnostic); NULL on the first page |
| `p_direction` (TEXT) | `'before'` (further along the display order — scroll down) or `'after'` (earlier — scroll up); ignored when the cursor is NULL. Output is always in display order — `'after'` fetches in reverse under the LIMIT and the outer SELECT flips it back |
| `p_limit` (INT) | Row cap; the API asks for `limit + 1` to detect a next page |
| `p_hidden` (BOOL) | FALSE = the visible register; TRUE = the soft-hidden recovery view (ADR-0072) |
| `p_search` (TEXT) | Case-insensitive substring over payee / memo / check# / counterparty name / tags |
| `p_date_from`, `p_date_to` (DATE) | Inclusive posted-date range |
| `p_amount_min`, `p_amount_max` (NUMERIC) | Range on the entry amount's *magnitude* (matches inflow + outflow) |
| `p_security_id` (UUID) | Entries involving this security |
| `p_tag` (TEXT) | Entries carrying this exact tag |
| `p_category_id` (UUID) | Entries posting to this category (counterparty account) |
| `p_status` (TEXT) | `cleared` / `uncleared` / `reconciling` / `scheduled` / `needs_review`; NULL = every status. `scheduled` = posted after `p_today`; the three recon states also require not-pending + posted on/before `p_today`; `needs_review` is the bank-feed flag |
| `p_today` (DATE) | Caller's LOCAL calendar date for the `scheduled` cutoff; falls back to `CURRENT_DATE` |
| `p_sort_column` (TEXT) | Sort dimension (mig 166): `date` (default) / `amount` / `payee` / `category` / `security` / `shares` / `price` / `action`. Whitelisted → a coalesced `MAX()` aggregate; unknown ⇒ `date`. The investment columns are all-NULL on bank rows, so they collapse to a harmless no-op order there |
| `p_sort_dir` (TEXT) | `asc` / `desc` (default). `entry_key` is the final keyset tiebreaker, so the order is total |

Every filter/status param defaults NULL ⇒ a no-op, so the plain register is
byte-for-byte unchanged. Predicates go in the WHERE (per-leg) before the GROUP
BY, so an entry appears iff ANY of its legs match. Return shape: `posted_at
TIMESTAMPTZ, seq BIGINT, entry_key UUID`. Hidden (per `p_hidden`) + merged-away
rows are handled inside the function.

History: migration 019 (5-param original) → 029 (created-at tiebreaker) → 031
(bidirectional `p_direction`) → 158 (cursor keyed on `header_seq`) → 164
(filter / search / status params, folding the status tabs server-side) → 165
(add the `reconciling` status) → 166 (dynamic sort + entry-key cursor) → 167
(the filter WHERE factored into `register_filtered_entries`; this function now
`SELECT`s FROM that primitive and adds only the GROUP BY + sort + keyset +
LIMIT). Bound via `HasDbFunction` / `AppDbContext.RegisterEntryKeys(...)`;
cursor codec in `RegisterRepository.EncodeCursor` / `DecodeCursor`. The
scroll-rail buckets, the per-status dropdown counts, and a filtered select-all
call the SAME `register_filtered_entries` primitive (ADR-0076), so the page,
the rail, the counts, and select-all can't drift.

End-to-end test: [db/test/verify_register_entry_functions.sql](../db/test/verify_register_entry_functions.sql).

### `register_filtered_entries(p_account_id, p_ledger_id, p_hidden, p_search, p_date_from, p_date_to, p_amount_min, p_amount_max, p_security_id, p_tag, p_category_id, p_status, p_today)`

The **single source of truth** for the register filter (mig 167 / ADR-0076).
Applies visibility + ledger/account scope + every filter dimension (search /
date / amount / security / tag / category / status) and returns the matching
rows as `SETOF resolved_transactions`. Four consumers share it, so the filter is
defined exactly once:

- `register_entry_keys` (the windowed page) `SELECT`s FROM it, adding sort +
  keyset + LIMIT.
- `RegisterRepository.GetIndexBucketsAsync` — the date-rail scroll buckets.
- `RegisterRepository.GetStatusCountsAsync` — the status-count badges (two
  calls, one per `p_hidden` side, since it counts a Hidden bucket).
- `BulkTransactionsRepository` — the filtered select-all intersection.

Filter params match `register_entry_keys` (same meaning; each defaults NULL ⇒
no-op). `p_hidden` is three-valued: `FALSE`/`TRUE` selects one visibility side;
**NULL returns both** (used by select-all, whose own query scopes visibility).
Predicates are per-leg, so a header appears iff any of its legs match. As a
single-`SELECT` `LANGUAGE sql STABLE` function it **inlines** into its callers
(verified via `EXPLAIN` — no plan change vs the pre-167 inline filter). Bound via
`HasDbFunction` / `AppDbContext.RegisterFilteredEntries(...)`. Cross-surface
agreement is pinned by `RegisterFilterConsistencyTests`.

### `ledger_payee_suggestions(ledger_id, limit)`

Returns one row per distinct resolved payee across a ledger's headers,
ranked by usage count then recency. Drives the payee Typeahead in the
register's inline-edit flow.

| Parameter | Meaning |
|---|---|
| `p_ledger_id` (UUID) | Ledger scope |
| `p_limit` (INT) | Hard cap; the API requests ~50 — enough for the Typeahead's case-insensitive substring filter to feel instant |

Return shape: `name TEXT, count BIGINT, last_used_at TIMESTAMPTZ`. Hidden
+ merged headers are excluded so suggestions stay clean.

Bound via `HasDbFunction` and reached through
`AppDbContext.LedgerPayeeSuggestions(...)`. Added in migration 027.

### `account_path(account_id)`

Recursive walker that returns the root-to-leaf category path as a
`/`-joined string (e.g. `Food/Groceries/Whole Foods`). Used by the
`resolved_transactions` view to render `counterparty_account_name`
without the consumer having to walk `accounts.parent_id` themselves.
Defined in migration 020.

### `security_price_upsert_from_trade(p_ledger_id, p_security_id, p_day, p_price)`

Rank-gated upsert of a `trade`-source closing price for a `(security, UTC-day)`
(migration 177, ADR-0084). Unlike the read-path functions above this one is
`VOLATILE` and WRITES — it is the sibling of `recompute_holdings_for_account_security`:
the `TradePriceFromLegInterceptor` invokes it post-save (via `HasDbFunction` /
`AppDbContext.SecurityPriceUpsertFromTrade(...)`) for every EF write that lands
an investment trade leg (`security_id` set, `quantity <> 0`, `unit_price > 0`),
so the execution price seeds `security_prices` for native API + MCP writes. A
function call (not a trigger, ADR-0032) keeps the conflict SQL out of the app
layer and can't re-fire the interceptors.

| Parameter | Meaning |
|---|---|
| `p_ledger_id` (UUID) | Ledger scope stamped on the inserted row |
| `p_security_id` (UUID) | Security to price; also the single echoed return column |
| `p_day` (DATE) | The trade's UTC calendar day = `price_date` (ADR-0084 D3) |
| `p_price` (NUMERIC) | The per-share execution price; `NULL`/`<= 0` is a no-op |

Behaviour: `INSERT ... ON CONFLICT (security_id, price_date) DO UPDATE` gated by
`WHERE security_prices.source IN ('import','simplefin','trade')`, so a `trade`
inserts on an empty day and overwrites only lower-or-equal-ranked rows — a truer
`fetch`/`manual` close for the day is never clobbered (rank ladder: `manual` ==
`fetch` > `trade` > `simplefin` > `import`, ADR-0084 D1). Returns the input
`security_id` so EF has a typed projection; callers discard it. The one-time
migration-177 backfill applies the identical logic to historical trade legs
(taking the last trade of each `(security, UTC-day)`), covering the Dapper
importer path the interceptor doesn't see.

### Trigger functions

The balance-after maintenance + posting-pair invariant trigger
functions are documented under [Triggers](#triggers) below.

---

## Triggers

### Running balance — header-walk on `txn_header_account_balances`

ADR-0034 (mig 089–097). One row per `(header, account)` carries the running balance on that account after the header is applied. Maintained by `fn_recompute_balances_for_account(account_id, from_posted_at)` and a statement-level dispatcher `fn_trg_legs_recompute_balances()` on `txn_legs`, plus `fn_trg_headers_recompute_balances()` on `txn_headers` for `posted_at` / `is_merged_into` changes. All triggers honour `pg_trigger_depth() > 1` for recursion prevention and early-exit when only non-balance-relevant columns changed.

The recompute aggregates leg amounts per header (the **header-walk**), then running-SUMs in the canonical **`(posted_at, seq)`** order. `txn_headers.seq` is a strictly-monotonic `BIGINT` populated by the `txn_headers_seq` SEQUENCE; within a batch INSERT each row receives a distinct value, eliminating the UUID-tiebreaker ambiguity that plagued the initial `(created_at, id)` design. Multi-leg same-account headers (e.g. BuyXfr fan-out) collapse to a single step in the running total. Both `txn_headers.seq` (mig 095) and `txn_headers.created_at` (mig 093) are locked immutable by column-level BEFORE-UPDATE triggers.

Invariant: for any **visible** header (`is_merged_into IS NULL` AND `COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE`) and any account it touches, `balance_after` equals `opening_balance` + the sum of net-per-header amounts for that account, summed across every earlier visible header in canonical `(posted_at, seq)` order. Hidden headers (`is_hidden=TRUE` on the raw row or via `txn_header_overrides.is_hidden`) are excluded — the recompute predicate matches the resolved view's effective-hidden expression so the rows you can't see don't count against the rows you can. Override amounts (`txn_leg_overrides.amount`) and override posted_at (`txn_header_overrides.posted_at`) are honoured via `COALESCE` in the recompute (mig 099 / 101 / 103).

**Mig 102** dropped the entire balance-trigger family. The recompute function stays as the algorithm but is invoked from API call sites instead: the `BalanceRecomputeInterceptor` (`SaveChangesInterceptor`) scans `ChangeTracker` and fires the recompute automatically for every API write; bulk paths that bypass the ChangeTracker (`ExecuteUpdateAsync` / `ExecuteDeleteAsync` / Dapper) invoke `BalanceRecomputeService` explicitly. **Mig 103** added `is_hidden` to the canonical recompute predicate set, so soft-delete (the bank + investment + bulk DELETE soft-hide branches) removes the row from the balance walk in the same SaveChanges that hides it from the register. See [decisions/0034-header-walk-running-balance.md](decisions/0034-header-walk-running-balance.md) for the rationale and [decisions/0032-triggers-as-last-resort.md](decisions/0032-triggers-as-last-resort.md) for the broader posture.

End-to-end test: `tests/Api.Tests/Integration/Transactions/BalanceConsistencyTests.cs`.

### Current balance — `account_current_balances` view

ADR-0056 / mig 133. The single definition of "an account's current balance", so
the dashboard overview and `HoldingsRepository`'s brokerage-cash read share one
source instead of each re-deriving it. One row per account:
`COALESCE(latest balance_after, opening_balance)` — the register's latest
`balance_after` (by canonical `(posted_at, seq)` via a `LATERAL`), with the
`opening_balance` fallback for an account with no transactions. `security_invoker`
so RLS on the underlying `accounts` / `txn_header_account_balances` applies as the
querying role. Read through `AccountBalancesRepository` (single account / all /
active-only). Net worth (ADR-0056) is the straight sum of these balances —
liabilities are stored negative — and investment accounts add holdings market
value on top.

### Posting-pair invariant on `txn_legs`

ADR-0019's symmetric-pair trigger (`fn_validate_counterparty_symmetric`)
was retired when the legacy `transactions` table dropped in migration
025. ADR-0022 replaces it with a structural invariant: the unique
index `(header_id, posting_index, account_id)` on `txn_legs` enforces
that each posting has exactly two legs on distinct accounts. "Other
side of this leg" is one self-JOIN away:

```sql
SELECT * FROM txn_legs
WHERE header_id = $1 AND posting_index = $2 AND id != $3;
```

Exactly one row by invariant. No deferred trigger needed.

---

## Indexes

| Index | Table | Purpose |
|---|---|---|
| `uq_txn_headers_ledger_external_id` | `txn_headers(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent re-import key at the event level (ADR-0022). |
| `uq_txn_headers_online_match` | `txn_headers(ledger_id, online_match_fi_id, online_match_fitid) WHERE online_match_fitid IS NOT NULL` | FITID uniqueness for OFX-style feed rows (slice 2c.2 / migration 039). Concurrent inserts race; SyncService maps the unique-violation to `alreadyKnown`. |
| `uq_ledger_operations_one_running_per_connection` | `ledger_operations(feed_connection_id) WHERE status='running' AND feed_connection_id IS NOT NULL` | At-most-one running sync per connection (slice 2c.2 / migration 040). Concurrent sync attempts race; loser → 422 `feed-sync-in-progress`. Stale `running` rows swept by SyncService's lazy reaper. |
| `idx_txn_headers_ledger_posted_at` | `txn_headers(ledger_id, posted_at DESC, id DESC)` | Cursor-paginated register reads. |
| `idx_txn_headers_ledger_visible` | `txn_headers(ledger_id, posted_at DESC, id DESC) WHERE NOT is_hidden AND is_merged_into IS NULL` | Visible-only register reads (the common case); partial keeps the working set tight. |
| `idx_txn_headers_is_merged_into` | `txn_headers(is_merged_into) WHERE is_merged_into IS NOT NULL` | Merged-into chain lookups; partial because most rows are NULL. |
| `uq_txn_legs_posting` | `txn_legs(header_id, posting_index, account_id)` | Two-legs-per-posting invariant + leg upsert key. |
| `idx_txn_legs_header_posting` | `txn_legs(header_id, posting_index)` | "All legs of this header" — drives AssembleEntries. |
| `idx_txn_legs_account_id` | `txn_legs(account_id)` | Per-account leg lookup + running-balance trigger scan. |
| `idx_txn_legs_security_id` | `txn_legs(security_id) WHERE security_id IS NOT NULL` | Per-security investment register query. |
| `idx_txn_account_date` | `transactions(account_id, feed_posted_at DESC, id DESC)` *(legacy)* | Pre-ADR-0022 register pagination. Unused. Drops with `transactions`. |
| `idx_txn_external`, `idx_txn_merge_window`, `idx_txn_payee_trgm`, `idx_txn_group`, `idx_txn_counterparty`, `idx_txn_security` | `transactions` *(legacy)* | All unused as of migration 023; drop with `transactions`. |
| `idx_holdings_account` | `holdings(account_id, security_id)` | Per-account positions |
| `idx_prices_security_date` | `security_prices(security_id, price_date DESC)` | Latest price lookup |
| `idx_lots_holding` | `lots(holding_id) WHERE is_closed = FALSE` | Open-lot scan for sell-side matching |
| `idx_ledger_operations_started` | `ledger_operations(started_at DESC)` | Sync history list |
| `idx_recurring_active_due` | `recurring_transactions(next_due_date) WHERE is_active = TRUE` | Due-date scan |
| `idx_transaction_tags_tag` | `transaction_tags(tag_id, transaction_id)` | Tag-first lookups |
| `uq_securities_cusip_per_ledger` | `securities(ledger_id, cusip) WHERE cusip IS NOT NULL` | Per-ledger CUSIP uniqueness. Replaced the global `uq_securities_cusip` (migration 002) in migration 048 — two ledgers can each hold the same CUSIP. |
| `uq_securities_ticker_per_ledger` | `securities(ledger_id, LOWER(ticker)) WHERE ticker IS NOT NULL` | Per-ledger, case-insensitive ticker uniqueness (migration 048). |
| `uq_securities_external_id_per_ledger` | `securities(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent source-system upsert (Moneydance imports), scoped per-ledger so two ledgers can each carry the same MD security id. Replaced the global `uq_securities_external_id` in migration 014. |
| `uq_accounts_external_id_per_ledger` | `accounts(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent source-system upsert (Moneydance imports), scoped per-ledger. Replaced the global `uq_accounts_external_id` in migration 014. |
| `uq_recurring_external_id` | `recurring_transactions(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent source-system upsert (Moneydance reminders), scoped **per ledger**. Added global in migration 013 (that migration's "already per-ledger by transitive scoping" reasoning was wrong — a UNIQUE index on `external_id` alone is not ledger-scoped, so importing the same MD export into a second ledger collided); re-scoped to `(ledger_id, external_id)` in migration 162. |
| `uq_tags_name_per_ledger` | `tags(ledger_id, name)` | Per-ledger tag namespace. Replaced the global `tags_name_key` in migration 014. |

---

## Required PostgreSQL extensions

| Extension | Purpose |
|---|---|
| `pg_trgm` | Trigram similarity for fuzzy payee matching |
| `pgcrypto` | Cryptographic helpers; `gen_random_uuid()` is built-in PG13+ |
| `pg_stat_statements` (recommended) | Query performance monitoring |
