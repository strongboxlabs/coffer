# Database Schema Reference

Column-level reference for the Coffer PostgreSQL schema. Mirrors the SQL in [db/migrations/](../db/migrations/). When the SQL and this document disagree, fix whichever is wrong — never let them drift.

Conventions:
- All `id` columns are `UUID` with `DEFAULT gen_random_uuid()` (built-in on PG13+).
- All `_at` columns are `TIMESTAMPTZ`.
- Money is `NUMERIC(19, 4)` (`budget_targets.amount` and the six `realized_gains` money columns are `NUMERIC(20, 3)`, widened from `NUMERIC(19, 2)` by migration 301, so 17 integer digits remain). Every money figure is held at its own currency's ISO 4217 minor units, 0, 2 or 3 places, not at 2 places for every currency ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). The places come from one checked-in ISO 4217 data file, compiled into the API and the SPA, with a test that flags drift between them. An IMMUTABLE SQL minor-units function is generated from the same file and pinned to it by a guard test (settled by Claude, 2026-11-16). For a stored account or ledger code that would now be refused for any reason (not an ISO 4217 currency with an exponent, an ISO "N.A." entry, or the 4-place fund units `CLF` and `UYW`), the function returns 2, so such a code keeps working at 2 places in SQL as in the API ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15, settled by Claude, 2026-11-16). A leg's `amount` is CHECKed to at most 3 places whatever its currency (the same migration replaces migration 159's 2-place CHECK); the exact rule per currency is enforced in the API (see `txn_legs.amount`). Share quantities and unit prices are `NUMERIC(25, 12)` (migrations 043 and 050). Prices, share quantities and exchange rates are not money and do not follow a currency's minor units.
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
        text currency_code
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
        text auth_method
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
        text import_provider_key
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

    account_folders {
        uuid id PK
        uuid ledger_id FK
        text account_type FK
        text name
        uuid parent_id FK
        timestamptz created_at
    }

    account_folder_members {
        uuid account_id PK,FK
        uuid ledger_id FK
        uuid folder_id FK
        text account_type FK
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
        uuid ledger_operation_id FK
        uuid originating_account_id FK
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
        text notes_kind
        bool notes_is_read_only
        bool notes_groups_by_header
        text notes_memo
        bool notes_memo_inherited
        text notes_inherited_memo
        uuid notes_inherit_from_account_id
        bool notes_tags_inherited
        text notes_header_memo
        bool notes_header_in_play
    }

    txn_header_account_balances {
        uuid header_id PK,FK
        uuid account_id PK,FK
        uuid ledger_id FK
        numeric balance_after
        numeric net_amount
    }

    txn_header_originals {
        uuid header_id PK,FK
        uuid ledger_id FK
        text payee
        text memo
        timestamptz posted_at
        timestamptz transacted_at
        text check_number
        numeric amount
        boolean rounded_at_import
        timestamptz captured_at
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

    txn_leg_note_tags {
        uuid leg_id PK,FK
        uuid tag_id PK,FK
        uuid header_id
        uuid ledger_id
        bool shown
        bool in_play
        bool inherited
        bool transaction_level
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
        timestamptz quote_currency_confirmed_at
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

    ledger_benchmarks {
        uuid id PK
        uuid ledger_id FK
        text symbol
        text name
        text currency_code
        int position
        timestamptz created_at
    }

    benchmark_growth {
        uuid ledger_id PK
        text symbol PK
        date growth_date PK
        numeric growth
        text source
        timestamptz created_at
    }

    fx_rates {
        uuid id PK
        uuid ledger_id FK
        text from_currency
        text to_currency
        date rate_date
        numeric rate
        text source
        text upstream
        uuid entered_by_user_id FK
        timestamptz created_at
    }

    fx_pair_coverage {
        uuid ledger_id PK
        text from_currency PK
        text to_currency PK
        date fetched_from_date
        date fetched_through_date
    }

    benchmark_coverage {
        uuid ledger_id PK
        text symbol PK
        date fetched_from_date
        date fetched_through_date
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
        uuid ledger_id FK
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
        text provider_key
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

    oauth_connection_meta {
        text authorization_id PK
        uuid user_id FK
        text label
        timestamptz last_used_at
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

    drive_mirrored_backups {
        text backup_id PK
        timestamptz mirrored_at
        timestamptz deleted_at
    }

    oidc_server_keys {
        int id PK
        bytea signing_key_ciphertext
        bytea encryption_key_ciphertext
        timestamptz created_at
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
        text kind
        text label
    }

    upgrade_repo_token {
        smallint id PK
        bytea token_ciphertext
        timestamptz updated_at
        uuid updated_by
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
    ledgers ||--o{ account_folders : "scopes"
    account_folders ||--o{ account_folders : "parent of"
    account_folders ||--o{ account_folder_members : "holds"
    accounts ||--o| account_folder_members : "filed in"

    %% Transactions
    ledgers ||--o{ txn_headers : "scopes"
    txn_headers ||--o{ txn_legs : "postings"
    accounts ||--o{ txn_legs : "posts to"
    accounts |o--o{ txn_headers : "originates"
    ledgers ||--o{ txn_legs : "scopes"
    securities ||--o{ txn_legs : "investment leg references"
    txn_headers ||--o{ txn_header_account_balances : "balance per account"
    accounts ||--o{ txn_header_account_balances : "running balance on"
    ledgers ||--o{ txn_header_account_balances : "scopes"
    txn_headers ||--o| txn_header_originals : "pre-edit feed values"
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

    %% Outside market series (mig 280, ADR-0114)
    ledgers ||--o{ ledger_benchmarks : "compares with"
    ledgers ||--o{ benchmark_growth : "scopes"
    ledgers ||--o{ fx_rates : "scopes"
    users |o--o{ fx_rates : "entered by (manual)"
    ledgers ||--o{ fx_pair_coverage : "scopes"
    ledgers ||--o{ benchmark_coverage : "scopes"

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
    users ||--o{ oauth_connection_meta : "owns"
    users ||--o{ mcp_tool_invocations : "acted by"

    %% Backups / Drive / system settings / snapshots
    users ||--o{ drive_sync : "configured by"
    users ||--o{ backup_settings : "configured by"
    users ||--o{ backup_pins : "pinned by"
    users ||--o{ system_settings : "updated by"
    ledgers ||--o{ ledger_snapshots : "snapshot of"
    users ||--o{ ledger_snapshots : "created by"
    ledger_snapshots ||--o{ ledger_snapshot_parts : "chunked into"

    %% Budgets, CSV layouts, events and audit.
    ledgers ||--o{ budget_targets : "budgeted in"
    ledgers ||--o{ feed_csv_mappings : "import layouts for"
    txn_legs ||--o{ txn_leg_tags : "tagged with"
    tags ||--o{ txn_leg_tags : "applied to"
    txn_legs ||--o{ txn_leg_note_tags : "presents"
    tags ||--o{ txn_leg_note_tags : "named in"
    ledgers ||--o{ saved_reports : "reports saved in"
    users ||--o{ saved_reports : "authored"
    saved_reports ||--o{ report_pins : "pinned as"
    users ||--o{ report_pins : "pins"
    ledgers ||--o{ ledger_events : "raised in"
    ledgers ||--o| ledger_consistency_state : "last checked"
    ledgers ||--o{ ledger_notification_subscribers : "notified for"
    users ||--o{ ledger_notification_subscribers : "subscribed by"
    users ||--o{ notification_subscribers : "subscribed by"
    users ||--o{ admin_audit_events : "acted by"

    %% Which import wrote a row, so an import can be undone (mig 221).
    ledger_operations ||--o{ txn_headers : "wrote"
```

> **`system_events` and `__schema_migrations` are deliberately absent.** Neither
> carries a `ledger_id` or a `user_id` — `system_events` is deployment-scoped by
> design (ADR-0096 D3) and has no foreign key to draw — so they have sections under
> **Tables** but no entity here.



---

## Tables

**Privileges and row-level security (migration 254, FU-086).** `coffer_app`, the request
role, holds only the privileges each table's policies serve, granted by that table's own
migration. Nothing is granted by default: a default privilege hands `coffer_app` full rights
on every new table whether or not that table has RLS, so migration 254 removed migration
017's `ALTER DEFAULT PRIVILEGES`. Every table `coffer_app` can reach has RLS, except the four `OpenIddict*` tables
(their stores run at the anonymous token endpoint). Every write policy admits a ledger role
(`owner`/`editor`), an admin, or the writer's own rows. Every view is `security_invoker`
and read-only to `coffer_app`. Only declared functions are `SECURITY DEFINER`, each with a
pinned `search_path`. `RlsCatalogTests` checks all of this against the migrated catalog, so
a new table, grant, policy, view or function is held to it.

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
| `currency_code` | `TEXT` | NOT NULL DEFAULT `'USD'` | The ledger's reporting currency: what a figure that combines accounts converts into ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D7, **migration 280**). ISO 4217, with the shape `accounts.currency_code` has: no CHECK, the API validates the code, and accepts the codes it accepts for an account (ADR-0114 D15; see `accounts.currency_code`). The default is the code a writer that names none gets, `DisplayCurrency.Fallback`'s. Written by create-ledger and the Moneydance import (inside the import's own transaction), and changed only by an owner through `PATCH /api/ledgers/{id}`, under the ledger write lock. Migration 280 set every existing ledger from the rule `DisplayCurrency.Of` applied: the first code in ordinal order (`COLLATE "C"`) among net worth's accounts (every non-category account no account of the ledger names as its `holdings_account_id`, active or not), else `USD`, so the backfill is derived from the data and deterministic. Not captured in snapshots (`ledgers` is the row a restore writes into), so a rollback leaves the reporting currency as it is; the budget targets it restores keep the currency each was typed in. |

Migration 014 seeds a default ledger with the well-known id `00000000-0000-0000-0000-000000000001` to absorb existing data on a populated DB; migration 186 drops it on an install with no human user yet (ADR-0088), so there is no implicit default. Every other ledger is created in the app, never by setup (ADR-0115 D2): by "New ledger"; by a Moneydance import, which always creates a new one; or by the hub's Demo ledger action, which is a Moneydance import of an export generated for the caller's date (ADR-0115 D1, D3).

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
| `signature_counter` | `BIGINT` | NOT NULL DEFAULT 0 | Replay-attack guard. Must strictly increase per credential per assertion; a backwards counter rejects the assertion. Written compare-and-set against the value the assertion was verified against (FU-095), so of two concurrent assertions carrying the same counter only one signs in. |
| `aaguid` | `UUID` | | Authenticator AAGUID; lets the UI label "YubiKey 5C" vs. a phone passkey. |
| `transports` | `TEXT[]` | | Reported transports (`usb`, `nfc`, `ble`, `internal`, `hybrid`). |
| `nickname` | `TEXT` | NOT NULL | User-supplied label. |
| `rp_id` | `TEXT` | | WebAuthn Relying Party ID (the domain) this credential was registered against. NULL for rows predating migration 157. Registration excludes only same-`rp_id` credentials, so one from a prior RP (domain rename / ADR-0061 restore) doesn't block re-enrolling the same authenticator. Migration 157. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Written by the API from its clock (ADR-0105 D9), not left to the default: step-up compares it with the session's `created_at`, so the two must come from one clock. A passkey created after the session began cannot approve a step-up from that session. |
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

Cookie-backed login sessions (per ADR-0013). The cookie carries an opaque random session id; this table stores `SHA-256(id)` so DB reads cannot forge sessions. Defaults: 30-day max lifetime, 7-day idle timeout. Added in migration 015; written in PR 3.3. A restore sets `revoked_at` on every session the backup brought back (FU-076, `RestoreReset`), so everyone signs in again.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | |
| `session_hash` | `BYTEA` | NOT NULL UNIQUE | SHA-256 of the cookie value. Plaintext never persists. |
| `user_agent` | `TEXT` | | For the active-sessions UI. |
| `auth_method` | `TEXT` | NOT NULL DEFAULT `'passkey'` CHECK in (`passkey`, `recovery-code`, `setup`, `invite`, `dev`) | How the session began (migration 248, ADR-0105 D2). A `recovery-code` session may add one passkey within 15 minutes without a step-up tap; no other value grants anything. Rows that predate 248 read `passkey`, which grants nothing. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Written by the API from its clock (ADR-0105 D9). The session's start: step-up accepts only passkeys created at or before it. |
| `last_seen_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Written by the API from its clock, at insert and on every authenticated request, and checked against the idle timeout on the same clock, so the bump and the check cannot disagree by the skew between the database's `now()` and the process clock. |
| `expires_at` | `TIMESTAMPTZ` | NOT NULL | Hard upper bound on session lifetime regardless of activity. |
| `revoked_at` | `TIMESTAMPTZ` | | Set by logout, "sign out other sessions", an admin's "sign out", and disabling the user (ADR-0105 D3); the session row stays for audit. A session also stops validating the moment its user is disabled, revoked or not. |

### `webauthn_pending_challenges`

Server-side state for in-flight WebAuthn ceremonies (per ADR-0013). Rows live ~60-120s between `/begin` and `/complete`; `consumed_at` flips on first successful verify so a replay against the same challenge id fails. The scheduler tick deletes expired rows (`ChallengeStore.SweepAsync`, ADR-0105 D8): anonymous `/login/begin` inserts a row per call, so without the sweep the table grows without bound. Added in migration 016.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `flow` | `TEXT` | NOT NULL CHECK in (`setup`, `login`, `register`, `invite`, `masterkey-reveal`, `backup-passphrase-reveal`, and nine `step-up:<action>` flows: `passkey-add`, `passkey-delete`, `recovery-codes`, `mcp-token`, `grant-admin`, `restore`, `backup-passphrase`, `backup-delete`, `mcp-writes`) | The ceremony shape this challenge was issued for. The `step-up:*` flows (migration 248, ADR-0105 D1) are one per guarded action, so a tap approves exactly the action it was minted for; they are listed in the CHECK, not matched by prefix, so a new guarded action is a deliberate widening. The `/complete` consumer must request the same flow it was issued for; mismatched flows fail the lookup. `register` (migration 140) is the add-a-passkey ceremony for an already-authenticated user, distinct from `setup` (which also creates the user). `invite` (migration 176, ADR-0083 slice B) is the invite-redeem ceremony — a scoped, repeatable clone of the first-user `setup` ceremony that `InvitesEndpoints` runs when a recipient redeems an invite link. |
| `user_id` | `UUID` | FK → `users(id)` ON DELETE CASCADE | NULL during the bootstrap setup flow (the user row doesn't exist yet — it's created at /complete in the same transaction as the credential). For login, the resolved user. |
| `options_json` | `TEXT` | NOT NULL | Fido2NetLib's `CredentialCreateOptions` / `AssertionOptions` JSON serialisation. The challenge bytes live inside. |
| `metadata_json` | `TEXT` | | Per-flow scratch: setup stores the proposed username + display name + the predetermined user_id here so `/complete` can build the user row; login leaves it NULL. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `expires_at` | `TIMESTAMPTZ` | NOT NULL | |
| `consumed_at` | `TIMESTAMPTZ` | | Set on first successful verify. `UPDATE … RETURNING` in `ChallengeStore.ConsumeAsync` makes this single-shot under concurrent callers. |

### `bootstrap_tokens`

One-shot setup tokens minted at API startup while setup has not completed: `system_settings['setup.completed']` is absent (migration 248, ADR-0105 D7). The condition is that setting and not "no WebAuthn credentials exist" (ADR-0013's), because a credential count re-arms setup on a populated install whose users have deleted every passkey. The plaintext is written to the API logs once and consumed by `/api/auth/setup/{token}/complete`. The SPA pre-validates the token (without consuming) via `GET /api/auth/setup/{token}/info`. Subsequent registrations require an authenticated session or a recovery code. Added in migration 015.

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

Invite links (migration 175, ADR-0083 slice B). A generalized, repeatable, scoped bootstrap token — same token crypto/storage as `bootstrap_tokens` (the plaintext is shown to the issuer once and never persisted; the SHA-256 is the PK). The scope columns record who issued it, the target ledger + grant role it confers (both NULL = an instance-only invite that just creates the account), an optional instance-admin grant, an expiry, and a single-use consume. Service-role only: redeem runs pre-auth (the token *is* the credential), so `coffer_app` is never granted — every read/write goes through `coffer_service`, exactly like `bootstrap_tokens`. Migration 231 gave it RLS together with a `coffer_app` grant and policies that nothing used, the write policy admitting any editor and any role; migration 254 removed the grant and the policies again (FU-086). A restore removes every invite not yet consumed (FU-076); consumed ones stay as the record of who joined how.

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
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor (ADR-0020). Set once at insert. Existing rows backfill to the default ledger via migration 014. |
| `parent_id` | `UUID` | composite FK `(parent_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE SET NULL (parent_id) | NULL for top-level. Allowed only when `account_type='category'`. Composite FK `accounts_parent_id_ledger_fkey` (migration 121) — the scoped `SET NULL (parent_id)` clears only the reference, leaving the row's NOT-NULL `ledger_id` intact. DEFERRABLE INITIALLY DEFERRED since migration 247: a snapshot restore inserts `accounts` in chunks, and a child can sit in an earlier chunk than its parent. |
| `name` | `TEXT` | NOT NULL | Display name |
| `account_type` | `TEXT` | NOT NULL CHECK in (`bank`, `credit_card`, `investment`, `asset`, `liability`, `loan`, `category`) | |
| `category_kind` | `TEXT` | CHECK in (NULL, `income`, `expense`, `adjustment`) | Set if and only if `account_type='category'` |
| `currency_code` | `TEXT` | NOT NULL DEFAULT `'USD'` | ISO 4217, and the code whose minor units every amount on the account is held at ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). No CHECK: the API accepts an ISO 4217 currency with 0, 2 or 3 places, and refuses a code that is not an ISO 4217 currency with an exponent (an ISO "N.A." entry or a code ISO does not list) and the 4-place fund units `CLF` and `UYW`, for an account's and for a ledger's currency alike. A stored account or ledger code this rule would now refuse, for any of these reasons, keeps working at 2 places, as it did, and the consistency check lists it as an advisory, as `txn_legs.amount` describes (settled by Claude, 2026-11-16). A price's or a quote's currency is not refused by this rule. A currency change, or a move of a transaction into an account in another currency, that would leave a stored figure with more places than the new currency allows is refused, naming how many figures do not fit (settled by Claude, 2026-11-16), a figure on a header merged into another left out (decided by the owner, 2026-11-17; see `txn_legs.amount`), the same kind of refusal as the one for a pair that cannot be represented; otherwise every stored figure is kept as it is (ADR-0114 D13). A category has no currency of its own (ADR-0109 D9): its lines carry the paying account's. The API's writers stamp a category with the ledger's reporting currency (`ledgers.currency_code`): `POST /accounts` when no code is sent, the starter tree a new ledger seeds, and the Uncategorized row a bank feed creates. A category create or edit that names another code is refused with `category-currency-not-reporting`. The Moneydance import still stamps its categories with each one's own Moneydance currency, and its Uncategorized with `USD`; rows written before this keep the code they were written with. A brokerage's Holdings sibling is in its brokerage's currency (ADR-0019 Rule 4, 2026-11-04 amendment): created in it, and a brokerage's currency edit writes the sibling's in the same transaction. A trade price is in the currency of the brokerage that made the trade, never the sibling's (migration 274), so the edit also re-derives the day of every trade on the brokerage and its sibling. The consistency check's `holdings_currency` projection reports a sibling in another currency than its brokerage's, and its repair sets the sibling's and writes no price. |
| `opening_balance` | `NUMERIC(19,4)` | NOT NULL DEFAULT 0 | Balance at start of tracking. Must be `0` for categories. Validated in the API at the account currency's minor units, as a leg's amount is (ADR-0114 D15, settled by Claude, 2026-11-16); no CHECK. |
| `opened_on` | `DATE` | NULL | The account's "Start Date" (the opening balance's as-of date). MD records it for most account types; seeded on import from the MD `acct` item's creation stamp — `date_created` (`yyyyMMdd` int) when present, else `creation_date` (epoch millis, read as its UTC date; MD stamps these at local noon so the day is stable) — and editable in Coffer later. The importer's upsert uses `COALESCE(accounts.opened_on, EXCLUDED.opened_on)` — seed-once, never overwriting a Coffer-side edit. NULL for categories (their opening balance is forced to 0). Added in migration 127 (ADR-0050); importer population landed later, and since MD import is a one-shot bootstrap with no re-import path, **migration 196** backfills already-imported ledgers by mining the same fields from `provider_raw_payload`. |
| `is_active` | `BOOLEAN` | NOT NULL DEFAULT TRUE | Single lifecycle flag (mig 106): `FALSE` = deactivated. The sidebar's "Show inactive" toggle surfaces these with a strikethrough; deactivated accounts are excluded from pickers + counterparty dropdowns. Mig 106 dropped the orthogonal `is_hidden` flag and collapsed its rows into this column; the MD importer maps both MD-side `is_inactive` AND `hide` flags here. |
| `feed_connection_id` | `UUID` | composite FK `(feed_connection_id, ledger_id)` → `feed_connections(id, ledger_id)` ON DELETE SET NULL (feed_connection_id) | NULL for manual/import-only and for all categories. Composite FK `accounts_feed_connection_id_ledger_fkey` (migration 121). DEFERRABLE INITIALLY DEFERRED since migration 247: `feed_connections` is not in a snapshot, so a restored account can name a connection deleted after the snapshot was taken, and the restore clears it (`fn_ledger_snapshot_reconcile_external_refs`) — the same `SET NULL` the delete applied to the live row — before the check fires. |
| `external_id` | `TEXT` | per-ledger UNIQUE `(ledger_id, external_id) WHERE external_id IS NOT NULL` | Source-system identifier — for Moneydance imports this is the raw MD UUID. NULL for accounts created by other paths. Set once at insert; the Moneydance import's dedup key within its one seeding run (an import always creates a new ledger). Added in migration 009; the original global unique was narrowed to per-ledger `uq_accounts_external_id_per_ledger` in migration 014. NULL on system-managed Holdings sibling rows. See also the `account_external_ids` junction (migration 064), which supersedes single-column keying by letting one account carry a distinct external id per source. |
| `is_system` | `BOOLEAN` | NOT NULL DEFAULT FALSE | TRUE on rows the importer/API creates and the user UI hides by default: the per-brokerage Holdings sibling accounts (ADR-0019), and the per-ledger Uncategorized category, an expense-kind category created by the first bank feed or file ingest that needs it, or by a Moneydance import that lands MD's blank-named top-level leaf categories on it (ADR-0027, 2026-10-21 amendment). Neither carries an `external_id`. Added in migration 011. |
| `provider_raw_payload` | `JSONB` | | Verbatim per-row provider data captured at import time (MD `acct` JSON for MD-bootstrap accounts; SimpleFIN/OFX-direct importers populate it analogously). Per ADR-0035 §3, classification rules read this directly rather than the source file — e.g. `olbfi` (online OFX) vs `ofx_import_acct_num` (QFX file) discriminates `online_import` from `file_import`. NULL on Coffer-native accounts created via the API. Added in migration 110. |
| `holdings_account_id` | `UUID` | composite FK `(holdings_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE SET NULL (holdings_account_id) | On a brokerage row, points at its system-managed Holdings sibling that hosts the holdings-side legs of investment transactions (ADR-0019). NULL on every other account. Added in migration 011; composite FK `accounts_holdings_account_id_ledger_fkey` (migration 121). DEFERRABLE INITIALLY DEFERRED since migration 247, for the same chunked-restore reason as `parent_id`. |
| `notes` | `TEXT` | | User-authored notes on the account. Maps to MD's `comment` field. Added in migration 012. |
| `account_number` | `TEXT` | | Account number at the institution. Sourced from `bank_account_number` on bank/credit accounts, `invst_account_number` on investment accounts. Added in migration 012. |
| `institution_name` | `TEXT` | | Name of the institution. Sourced from `bank_name` or `inst_name` (whichever is populated). Useful for matching against SimpleFIN feed accounts in Phase 5. Added in migration 012. |
| `routing_number` | `TEXT` | | ACH/OFX routing number. Sourced from MD's `ofx_bank_id`. Added in migration 012. |
| `account_url` | `TEXT` | | Institution's website. Sourced from MD's `account_url`. Added in migration 012. |
| `last_simplefin_sync_at` | `TIMESTAMPTZ` | | Per-account SimpleFIN sync watermark (slice 2c.5 / migration 042). The next sync against this account asks SimpleFIN for transactions from `(this − 7d)` forward; NULL = "no successful sync yet, full 90-day window next time." Advances only on syncs that actually persisted data for this account (mapped at sync time, not tagged in `errlist`). The user can also pick or reset this value via `PATCH /api/ledgers/{lid}/accounts/{aid}/sync-from-date`. Unbinding the feed mapping clears it so re-binding starts a fresh 90-day window. |
| `is_trade_commission` | `BOOLEAN` | NOT NULL DEFAULT FALSE; CHECK (`is_trade_commission = FALSE OR account_type = 'investment'`) | **On a brokerage (`account_type='investment'`)**: when TRUE, `recompute_holdings_cost_basis()` adds `posting_role='fee'` leg amounts from this brokerage's transactions into cost basis (and into `lots.unit_cost` on the function's next reset). Default FALSE. Migration 054 added the column; migration 056 narrowed semantics from per-category to per-brokerage and added the CHECK so non-investment accounts can't carry it. Typical settings: taxable brokerage = TRUE; 401k where in-transaction "fees" are administrative = FALSE. |
| `tax_status` | `TEXT` | CHECK in (NULL, `taxable`, `tax_deferred`, `tax_free`, `other`) | ADR-0066 (migration 149). The account's tax treatment, orthogonal to `account_type` (a brokerage and a Roth IRA are both `investment`). Importer seeds a best-guess from the source account name/type; Coffer owns it thereafter (import-once). Distinguishes 1099-B-relevant taxable accounts from tax-deferred/tax-free for reporting. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `import_provider_key` | `TEXT` | CHECK length ≤ 64 (`ck_accounts_import_provider_key_length`) | Migration 223: which file provider this account's imports default to, so the import dialog can preselect it. Free text rather than an enum because the provider set is open — a new `IFileProvider` must not need a migration to be selectable. |

**Cross-column CHECK constraints (migration 007):**

| Constraint | Rule |
|---|---|
| `accounts_category_kind_consistent` | `(account_type='category') = (category_kind IS NOT NULL)` |
| `accounts_parent_only_for_categories` | `parent_id IS NULL OR account_type='category'` |
| `accounts_category_has_no_real_state` | `account_type<>'category' OR (feed_connection_id IS NULL AND opening_balance = 0)` |

Table-level `UNIQUE (id, ledger_id)` (`uq_accounts_id_ledger`, migration 049) lets every child table (txn_legs, holdings, recurring_transactions, loan_terms, the self-FKs above, …) compose its FK against `(id, ledger_id)` so the DB structurally refuses a cross-ledger reference. `UNIQUE (id, ledger_id, account_type)` (`uq_accounts_id_ledger_type`, migration 258) is the same idea one column wider: `account_folder_members` composes against it, so a folder's member is always of the folder's account type. `id` alone is unique, so it refuses nothing; it does refuse changing the type of an account that sits in a folder, which the API never does (the type is immutable).

There is no `is_placeholder` column. For categories, the "this category is a folder" UI state is derived as "has children AND no own transactions" — a meaningful distinction, since a parent category may also receive direct transactions. Real accounts have no hierarchy (`parent_id` is categories-only); the sidebar groups them into folders through a separate table, [`account_folders`](#account_folders) (migration 258), which holds no transactions and no balance, so no reader of `accounts` sees a folder.

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
| `access_url_ciphertext` | `BYTEA` | | SimpleFIN access URL **sealed under the owning ledger's LEK** (ADR-0026, migration 036). Layout `nonce(12) ‖ ciphertext ‖ tag(16)`. Never plaintext. NULL only on rows predating the migration; every connection created since writes it. |
| `institution_name` | `TEXT` | | The connection's name. Migration 036. Set at connect from SimpleFIN's `connections[]`: the one name it lists when every entry with a name names the same institution (trimmed), NULL when it names several or none or the probe fails (`SimpleFinClient.GetInstitutionNameAsync`). An owner renames it (`PATCH …/feed-connections/{id}`, 1 to 80 characters trimmed; two connections may share one). Syncs never write it. NULL renders as "SimpleFIN". The column's `COMMENT` says the same since migration 278, which replaced migration 036's claim that the first sync fills it in. |
| `created_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Audit: which user clicked "Connect". Migration 036. SET NULL so a removed user doesn't cascade their feed history. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Table-level `UNIQUE (id, ledger_id)` (`uq_feed_connections_id_ledger`, migration 072) so child tables (`feed_connection_accounts`, `ledger_operations`, `accounts.feed_connection_id`) can compose ledger-coherent composite FKs.

A connection's delete (`FeedConnectionsRepository.DeleteAsync`) takes the ledger write lock (`fn_ledger_write_lock`) first. Its cascade updates the accounts it feeds while it holds the connection row; a sync's final save holds the ledger write lock and those accounts (their watermark), then inserts `feed_connection_accounts` rows whose foreign key takes `KEY SHARE` on the connection. With the lock first the two take turns (ADR-0112 D3). A rename (`FeedConnectionsRepository.RenameAsync`) takes no lock: it is one `UPDATE` of `institution_name` that recomputes nothing, holds only the connection's row and, once it holds it, waits for nothing (no key a foreign key reads changes, and the table has no trigger).

RLS (migration 255, FU-095): `feed_connections_read` (SELECT for any grant); `feed_connections_create` (INSERT) and `feed_connections_remove` (DELETE), `role = 'owner'`; `feed_connections_update` (UPDATE, `role IN ('owner','editor')`). A connection holds credentials to the owner's accounts at the bank, so creating or deleting one is the owner's. Editors keep syncing, and a sync records its status and `last_synced_at` on this row, so the UPDATE policy cannot hold a rename to owners: renaming is owner-only at the API alone (`AsLedgerOwner`, as Disconnect; ADR-0006, 2026-11-06 amendment). Mapping an account to a connection writes `accounts.feed_connection_id` / `external_id`, which RLS cannot tell from other account edits. The owner-only rule for mapping is the API's alone (ADR-0083, 2026-10-19 amendment).

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
Carries the metadata that's shared across all postings of the event:
payee, posted-at, check number, plus the online-match state. These
columns hold the CURRENT values — what the register shows. `memo` is
not one of them: it is a raw input of the stored memo-and-tags
presentation (`txn_legs.notes_*`), which is what the register shows.
The feed's are captured once, by the first edit that overwrites them,
into `txn_header_originals` (migration 230, ADR-0100); the Moneydance
importer seeds that table itself where MD kept a feed payee or memo that
differs from the curated one (migration 260 for earlier imports), and an
import that rounds an amount to its currency's places seeds it with the
bank's figure (ADR-0114 D15).
Reconciliation status is **not** here — it moved to the per-leg
`txn_leg_recon` overlay (migration 171, ADR-0082), because a transfer
can be cleared in one account while still uncleared in the other.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor — RLS short-circuits at this column for one-hop visibility checks. |
| `origin` | `TEXT` | NOT NULL CHECK in (`manual`, `online_import`, `file_import`) | Icon-level source mechanism (mig 107, ADR-0035). `online_import` covers any live feed (SimpleFIN, MD+ Direct Connect, OFX online); `file_import` covers any file upload (OFX/QFX, CSV, QIF); `manual` is user-typed. Per-provider audit detail lives in `provider_key`. The dedup query in `IngestOrchestrator` scopes by `(ledger_id, provider_key, external_id)` rather than `(ledger_id, origin, external_id)` since `origin` is no longer per-provider. |
| `external_id` | `TEXT` | CHECK `ck_txn_headers_external_id_for_non_manual` (`external_id IS NOT NULL OR origin = 'manual'`) | Universal per-provider stable identifier. Set by every ingest path: Moneydance import → MD txnid; SimpleFIN sync → SimpleFIN transaction id (mig 105); future OFX/QFX/CSV → provider-specific stable id. NULL only on manual rows (`origin='manual'`). ADR-0022 keys at the *event* level. Partial unique index `(ledger_id, external_id) WHERE external_id IS NOT NULL`. Mig 105 added the original CHECK; mig 109 rewrote it as `ck_txn_headers_external_id_for_non_manual` in the post-mig-107 vocabulary (the old `is_user_defined` predicate was retired with that column) so any ingest writer that forgets to populate the column trips at INSERT time. |
| `payee` | `TEXT` | | The current value. Mutable: both PATCH paths assign it, and the feed's is captured to `txn_header_originals` on the first edit (mig 230). Before that migration this column was raw-and-immutable and the edit lived in `txn_header_overrides`; the flip exists because a nullable override column could not express a CLEARED payee. |
| `memo` | `TEXT` | | The whole transaction's memo, where a header holds one: a split's, and a multi-posting investment event's. NULL on every one-posting header, whose memo sits on its legs (`txn_legs.leg_memo`, each side's own). Never blank: blank means inherit and is stored as NULL. Migration 264 moved every existing one-posting header's memo onto its legs, rewrote unedited Moneydance rows to the importer's current placement from their payload, and joined values found on an event's investment-side legs onto the event's header. A reminder template with no lines keeps its own, and the reminder shows it, since no leg presents it. An input of the stored presentation (`txn_legs.notes_*`); the API writes it only through the write primitive (`AnnotationWriter`), and readers read `notes_memo` / `notes_header_memo`. |
| `posted_at` | `TIMESTAMPTZ` | NOT NULL | The current value (mig 230 — see `payee`). NOT NULL, so a PATCH naming it with an explicit null is rejected (`transaction-date-null`) rather than ignored. A PATCH (bank or investment) carrying a date on the stored UTC calendar day keeps the stored timestamp, time of day included, and on its own captures no originals; a date on another day is applied as sent (`HeaderDates`). The editors send a picked day as midnight UTC, so assigning it would rewrite a feed row's time of day on a save that never touched the date. |
| `transacted_at` | `TIMESTAMPTZ` | NOT NULL (mig 189) | The current value (mig 230 — see `payee`). NOT NULL since mig 189: "no distinct tax date" is stored as the posted date, so there is no null state and an explicit null is rejected the same way. A PATCH date on the stored day keeps the stored timestamp, as for `posted_at`. |
| `check_number` | `TEXT` | | Paper-check number, or Moneydance's `chk` verbatim: on investment rows MD's `Auto` / `EXfr` / `Xfr` markers or, on a QIF-origin row, the QIF action word (`Buy`, `ReinvLg`, `XOut`, ...). The MD importer keeps it on both routes; the investment route dropped it until migration 260, which restores it over NULL from `provider_raw_payload` (ADR-0027, 2026-10-21 amendment). A blank `chk` is NULL. Shown on line 2 of the Action cell on the investment register (ADR-0028, 2026-10-21 amendment) and in its own column on the bank register. |
| `is_pending` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Bank-side state — TRUE while the bank itself has not cleared the transaction. Mutable: the sync service flips T→F in place on a future sync that returns the same FITID with `pending: false` (slice 2c promote-on-clear). Orthogonal to `needs_review`. |
| `is_hidden` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Soft-delete at the header level. The DELETE endpoint flips this to TRUE for any feed-sourced row (i.e. `external_id IS NOT NULL`, which after mig 105 covers SimpleFIN syncs alongside every other ingest path); manual rows (`origin='manual'`, no `external_id`) get hard-deleted instead. Canonical since mig 230: `txn_header_overrides.is_hidden` was dropped with its table, having had exactly one writer in the repository — a test fixture. |
| `is_merged_into` | `UUID` | composite FK `(is_merged_into, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE SET NULL (is_merged_into) | Set when this event lost a merge; NULL = active. Self-referential composite FK `txn_headers_is_merged_into_ledger_fkey` (migration 121), DEFERRABLE INITIALLY DEFERRED since migration 247 — a snapshot restore inserts headers in chunks keyed on `id`, so a loser can land a chunk before its winner; before 247 that failed every restore of a ledger past one chunk with a forward-pointing merge. **A merge also clears `needs_review` on the loser** — a folded-away row is not awaiting review, and the sidebar's review dot counts unreviewed rows. Both the bank and investment merge branches enforce this in the repository rather than trusting a caller to pair the merge with an approve, so a merge clears the dot whether or not the caller (the SPA, MCP or a direct API call) also sends `approve: true`. **Deleting a winner un-merges its losers** (the SET NULL): each is back in the register and in the balance and holdings walks, and in the consistency check's advisory of figures that do not fit their currency, which leaves a merged-away row out (see `txn_legs.amount`). A two-amount pair on a merged-away row still refuses a currency change or a move that would put both its sides in one currency with amounts that differ; the refusal (`relabel-pair-not-representable`) names each winner in `mergedInto` and says that deleting it brings the row back ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D13, decided by the owner, 2026-11-18). A winner since merged into another is followed to the end of the chain, the row a register shows, and the refusal says that deleting brings the row back only when every refused merged-away row was merged straight into a winner a register shows whose delete removes it outright (`TxnHeaderRow.IsDeletedOutright`: no `external_id`, not a reminder occurrence); a delete that hides a winner leaves this column set, and deleting the end of a chain un-merges only the row merged straight into it, so otherwise the refusal says nothing about a way back (ADR-0114 D13, settled by Claude, 2026-11-18). Every delete path recomputes them in the same transaction: the tracked deletes load the losers first so the save's interceptors see the change (`MergeLosers`), and the bulk delete adds their lines to its own recomputes (ADR-0032, 2026-11-03 amendment). **A merged-away row is a tombstone to every user write** ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15, decided by the owner, 2026-11-18): each route and connected-app tool that writes one transaction by its id answers it exactly as an id that does not exist and changes nothing, the read the editor loads a transaction from answers the same way, a bulk selection that names it ignores it, and the merge refuses it at either end (`merge-source-invalid`). Each such lookup reads `txn_headers` through `MergedAway.NotMergedAway`. Only system writes reach it: the un-merge when its winner is deleted, the ingest's promote-on-clear and matching, and the consistency repair. |
| `seq` | `BIGINT` | NOT NULL UNIQUE DEFAULT `nextval('txn_headers_seq')` | Strictly-monotonic insertion-order key (migration 095, ADR-0034 v2). The canonical ordering is `(posted_at, seq)`; within a batch INSERT each row gets a distinct value, eliminating the UUID-tiebreaker ambiguity of the prior `(created_at, id)` design. Immutable (column-level BEFORE-UPDATE trigger). Consumed by the running-balance recompute, `resolved_transactions`, the register's keyset page (`RegisterQuery`), and the register cursor codec. |
| `online_match_fitid` | `TEXT` | | OFX `<FITID>` — the bank's per-transaction id, unique only within one FI (migration 034). Part of the OFX dedup key `(ledger_id, online_match_fi_id, online_match_fitid)`. OFX-only (mig 105): the MD importer preserves MD's recorded OFX match state; SimpleFIN never touches it (SimpleFIN ids live on `external_id`). |
| `online_match_fi_id` | `TEXT` | | OFX FI id — identifies which institution issued the transaction (migration 034). Composite with `online_match_fitid`. |
| `ingest_action_hint` | `TEXT` | CHECK `ck_txn_headers_ingest_action_hint` (NULL or one of `buy`, `buyx`, `sell`, `sellx`, `dividend_cash`, `dividend_reinvest`, `divx`, `transfer`, `misc`) | Provider-classifier output (ADR-0031 Phase 3c, migration 076). Set by the orchestrator's brokerage branch when sync detects an investment-shape transaction; the editor pre-fills the action picker from it on review. NULL otherwise. |
| `provider_raw_payload` | `JSONB` | | Original provider JSON for this transaction, verbatim from the wire (migration 078, ADR-0031). Diagnostic / classifier-iteration use only. The Moneydance importer stores the txn's own MD JSON here (mig 109, ADR-0035 §3), which is what lets a later importer fix be replayed in SQL without the source file (migration 260 does; 196 did the same over `accounts.provider_raw_payload`). NULL on manual rows, on MD reminder template headers (their JSON is on `recurring_transactions.source_payload`), and on feed rows synced before the column existed (re-sync backfills). |
| `review_cleared_amount` | `NUMERIC` | CHECK `ck_txn_headers_review_cleared_by_match_has_amount`: not NULL when `review_cleared_by_match` | Migration 301 ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). Why a promote-on-clear put the row back in review, when the reason is a cleared figure the row does not hold: the bank's figure exactly as sent, a merged loser's when `review_cleared_by_match`, else the row's own, on a split whose lines add up to another total. Every promote-on-clear that sets `needs_review` writes it, NULL when the reason is a rounding (`TxnHeaderRow.HoldForClearedFigure` and `HoldForRounding`), so a figure from an earlier hold is never read as the current reason; a new row lands NULL. Read only while `needs_review`, for the Review marker; left as it is when the row is approved. Not derived from stored data, because a merge does not require equal amounts, so a loser's figure can differ from its winner's from the merge on, and a split's originals hold an imported figure on a row nothing cleared. NULL on every row before 301. |
| `review_cleared_by_match` | `BOOLEAN` | NOT NULL DEFAULT false | Migration 301 (ADR-0114 D15). True when `review_cleared_amount` is a merged loser's cleared figure, so this row is the winner it was folded into (the end of the loser's merge chain); false when it is the row's own, or there is none. |
| `ingest_amount` | `NUMERIC` | | Provider-stated **cash total** for the row, verbatim from the wire (OFX investment `TOTAL`), migration 228. No scale limit since migration 301, so it holds the figure exactly as sent; it was `NUMERIC(19,4)` before, and a figure stored then keeps the 4 places it was cut to. No import fits it to the currency: the editor rounds what it derives from it once, at the account currency's places, and the investment merge candidates read it at those places too, as the legs they match against are ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D14, D15). Exists because a REINVEST's total is NOT recoverable from shares x price: the wire rounds `UNITS * UNITPRICE` to the minor unit it actually moved, so rebuilding it lands up to one minor unit out (ADR-0073 D1 — the amount is the money, `unit_price` is derived). Populated only where the wire states a total the derived figure would contradict; the editor prefers it over `ingest_shares * ingest_unit_price` when pre-filling. NULL elsewhere. It is not where an ingested amount's bank figure is kept when an import rounds it: that is `txn_header_originals.amount` ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). |
| `ingest_shares` | `NUMERIC(28,8)` | | Provider-extracted share count from a file-import wire (OFX investment `UNITS`), migration 113. Populated only on investment rows where the provider carries the data; read by the editor's bank→investment upgrade. NULL for bank/credit and SimpleFIN brokerage rows. |
| `ingest_unit_price` | `NUMERIC(19,6)` | | Provider-extracted per-share price (OFX `UNITPRICE`), migration 113. Also preserves the wire's originally-reported trade price (see `txn_legs.unit_price`, which is derived). Same population rules as `ingest_shares`. |
| `ingest_fee` | `NUMERIC` | | Provider-extracted aggregated fee — sum of Commission + Fees + Load + Markup + Markdown (migration 113). NULL when the wire had no fee-shaped fields. Pre-fills the editor's single Fee field (ADR-0029). No scale limit since migration 301, so it holds the fee exactly as sent; it was `NUMERIC(19,4)` before, and a fee stored then keeps the 4 places it was cut to. No import fits it to the currency: the editor rounds the prefill once, at the account currency's places, takes a fee under half a minor unit as none, and takes the principal from the rounded fee, so Amount and Fee add back to the cash total ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D14, D15). |
| `ingest_security_ticker_hint` | `TEXT` | | Provider-extracted security identifier (OFX: SECLIST-resolved ticker or raw CUSIP fallback), migration 114. Persisted at ingest so the editor's Accept flow can record a `provider_security_mapping` with the same identifier the next ingest looks up. `resolved_transactions` LEFT JOINs `provider_security_mappings` on `(ledger_id, provider_key, ingest_security_ticker_hint)`. NULL on bank/credit rows, SimpleFIN rows (which re-derive from the payee classifier), and manual entries. |
| `provider_key` | `TEXT` | CHECK (`(origin='manual') = (provider_key IS NULL)`) | Mig 107, ADR-0035. Per-provider audit detail: `simplefin`, `mdplus`, `ofx`, `qif`, `csv` (a Moneydance row its own text import brought in), `csv-generic`, `csv-fidelity` (a CSV file import in Coffer, by its reader). NULL when `origin='manual'`. Drives the per-provider hover label on the register provenance icon AND is the per-provider dedup scope (mig 105's `external_id` is universal, `provider_key` qualifies it). |
| `is_merge_winner` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Mig 107, ADR-0035. TRUE when at least one other row has `is_merged_into` pointing at this row. Maintained atomically with `is_merged_into` in `TransactionsRepository.PatchAsync`. Drives the merge-winner overlay icon in the register. Monotonic — no unmerge surface today, so once TRUE, stays TRUE. |
| `import_source` | `TEXT` | | Bootstrap-import marker. `'moneydance_export'` on rows from the MD JSON bootstrap (`MoneydanceImportService.ImportSource`; mig 107 backfilled the bootstrapped rows); NULL on rows born in Coffer + live SimpleFIN sync + future OFX/CSV uploads. Audit / debug only — not surfaced in the register UI. Independent of `origin` (which describes the transaction's source mechanism) and `provider_key` (which identifies the specific provider); mig 109 §2.5. |
| `needs_review` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Bank-feed workflow flag (migration 037 / slice 2c). TRUE on rows the SimpleFIN sync just inserted; the register renders these with a distinct visual treatment (left bar in state-warning palette) until the Approve endpoint clears the bit — or until the row is merged away, which clears it too (see `is_merged_into`). A promote-on-clear sets it again on a row in a register whose cleared figure rounds, on a row in a register the user split into more than one posting when the cleared figure is not the split's total on the synced account (every line stays as split, decided by the owner, 2026-11-17), and, when a merge loser clears at a figure other than the one it held (for a split loser, its total), on the row the loser was folded into: the end of its merge chain, unless that row is hidden; the winner's own amounts are left ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). Each such hold records its reason in `review_cleared_amount` and `review_cleared_by_match`. Orthogonal to `is_pending` — a row can be `(is_pending=T, needs_review=T)` (bank-pending AND new to user) or `(is_pending=F, needs_review=T)` (cleared, awaiting approval). Manual entries + MD-imported rows write FALSE on insert. Partial index `(ledger_id) WHERE needs_review` backs the future "review-only" register filter + the inbox count badge. |
| `action` | `TEXT` | CHECK in (NULL, `buy`, `buyx`, `sell`, `sellx`, `dividend_cash`, `dividend_reinvest`, `divx`, `transfer`, `misc`, `transfer_shares`) | Investment-event action. Lifted from `txn_legs.investment_action` in migration 047; catalog locked to the 9-action set in migration 062 per ADR-0027 (`buyx`/`sellx`/`divx` first-class; `interest`/`misc_income`/`misc_expense` coalesced into `misc`; `split` moved to `security_splits` by migration 060). Migration 151 (ADR-0065) added the Ledger-native `transfer_shares` (in-kind share move; no MD txntype). One action per event, shared across all postings. NULL on non-investment events. A Moneydance row that takes the importer's bank route but whose home account is a brokerage (QIF cash words with `xfrtp_bank`, or no `xfer_type` at all) is `transfer` when it has exactly one posting and NULL when it has more; a reminder template is the same when its embedded txn is not investment-shaped. Migration 260 applied that rule to earlier imports, reminder templates and the occurrences already fired from them included (ADR-0027, 2026-10-21 amendment). |
| `is_recurring_template` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Migration 124 (ADR-0048): TRUE marks this header as a recurring-series **template**, not a live event. The `live_txn_headers` / `template_txn_headers` views partition on this flag so the register only ever sees live rows; templates fire occurrences via `recurring_transactions.template_header_id`. |
| `recurring_transaction_id` | `UUID` | FK → `recurring_transactions(id)` | Migration 124: on a **fired** occurrence, back-reference to the series that produced it. NULL on ordinary rows and on templates. |
| `occurrence_date` | `DATE` | | Migration 124: the series occurrence date a fired row materializes (paired with `recurring_transaction_id`). |
| `ledger_operation_id` | `UUID` | FK `txn_headers_ledger_operation_id_fkey` → `ledger_operations(id)` ON DELETE SET NULL, DEFERRABLE INITIALLY DEFERRED (migration 302) | Migration 221: which import wrote this row, so `POST /ledger-operations/{id}/undo-import` can hide exactly that set. **ON DELETE SET NULL is load-bearing** — `AuditRetentionService` prunes `ledger_operations`, and CASCADE would delete the transactions with the audit row. The consequence is that undo expires with the retention window. `ledger_operations` is not in a snapshot, so a restored row can name a run the prune deleted after the snapshot was taken; the restore clears the stamp (`fn_ledger_snapshot_reconcile_external_refs`), the same `SET NULL` the prune applied to the live row, before the deferred check fires (migration 302). Any other write is checked at its commit. |
| `originating_account_id` | `UUID` | composite FK `(originating_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT (`txn_headers_originating_account_ledger_fkey`) | Migration 263: the account the transaction was entered from — the entering side of a one-split, the split side of a multi-split, the brokerage of an investment event (for `transfer_shares`, its source brokerage as `transfer_shares_source_brokerages` defines it). Stored because posting counts cannot tell the two sides of a one-split apart, nor the two sides of a split whose every line goes to one category. The writers that reshape a transaction (a split, turning a row into an event, a repoint that leaves no leg on the account) hand it to the side that keeps it. RESTRICT: an account that still originates a header cannot be deleted, so a writer that removes an account's last leg without handing ownership over fails at the delete (legs already refuse their account's delete the same way). The Moneydance importer writes the home account. **NULL exactly when the header has no legs.** Invariants, asserted and never enforced at runtime: (a) the value is the account of a leg of its header; (b) an actioned event other than `transfer_shares` is owned by a brokerage carrying a leg in every posting; (c) a `transfer_shares` event by its source brokerage; (d) an action-less header of several postings on which some account carries a leg in every posting, by one of those accounts. Migration 263 backfills existing headers from their current shape (narrowing ties by the Moneydance home account, the reminder's source account, a real account over a category, the outflow side, then the lower id) and fails the upgrade if any header breaks an invariant; the Moneydance import check (`OriginatingAccountInvariants`) and the API tests run the same clauses. Indexed by `idx_txn_headers_originating_account`. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

> **Re-importing a file backfills these carriers.** A row imported before a carrier column existed has no way to acquire it otherwise, so the file-import dedup path fills `provider_raw_payload` and every `ingest_*` column above **over NULL only** when it re-sees a row it already knows (by `external_id`, or by `(fi_id, fitid)` for OFX). It never overwrites a stored value and never touches money, payee or memo — so it is safe on an accepted row and is the repair path for an unreviewed one.

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
`account_id`. The pair sums to zero when both accounts are in one
currency; a transfer between real accounts in different currencies
holds each account's own amount, nonzero and opposite in sign
([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D13). These
columns are canonical — there is no per-leg override layer
(`txn_leg_overrides` was dropped in migration 230: no writer anywhere,
so no code path could have filled it).

**Leg IDENTITY survives an edit, on BOTH write paths.** A leg id is not
a detail of one save — `txn_leg_recon` (ADR-0082), `lots` and
`realized_gains` all key on it, and the first cascades on delete. The
bank patch path keeps the legs a request names by
`legId` and mutates them in place. The investment patch path cannot be told
which legs to keep — investment legs are DERIVED from the action × field
matrix, so the client never sees them — so it re-establishes identity by
matching the rebuilt legs against the existing ones on
`(account_id, posting_role, security_id)` and reusing the id where they
agree. Only legs the new shape has no room for are deleted.

That match key is load-bearing. It is what prevents a reused id from
migrating between `(account, security)` pairs, which `realized_gains`
`UNIQUE (sell_leg_id)` (migration 148) would otherwise let collide, since
the recompute deletes and re-inserts per pair with no ordering guarantee
between the two.

Two consequences a writer must not "tidy":

- **Reconciliation is preserved on a kept leg.** Keeping a cleared row
  cleared across an edit is the reason leg identity is kept at all; do not
  "tidy" `txn_leg_recon` away with a reshape.
- **Holdings are reconciled once more at the end.** With legs surviving an
  edit, a shape-identical edit — notably a DATE change, which is what the
  FIFO walk orders by — produces no leg diff at all. `HoldingsRecomputeInterceptor`
  still recomputes it, because a changed date, action, hidden or merged flag
  on a header counts as a change to each of its investment legs (ADR-0032,
  2026-11-03 amendment). The investment patch path nonetheless calls the
  recompute explicitly, after its last save, for every `(account, security)`
  pair the header touches before and after: the acquisition lot is added in a
  save of its own, after the legs, and a fee leg's amount, which the walk folds
  into basis, sits on a leg the interceptor does not watch.

`transfer_shares` is excluded from leg reuse and still destroys and
rebuilds: its FIFO plan is computed from committed lots *after* the leg
drop, precisely so the plan cannot see the transfer's own effect, and its
per-moved-lot postings have no distinguishing identity under the match key
anyway.

**`posting_index` does double duty, and the second job is easy to
miss: it is also the split's DISPLAY ORDER.** The bank patch path
renumbers it 0..N-1 from the position of `postings.items[]` in the
request, which is what makes the editor's drag-to-reorder persist, and
every UI read sorts by it (`resolved_transactions` exposes it as
`leg_index`). The consequence for writers: **any code that rebuilds
the full `items[]` array from a leg query owns the user's ordering, so
that query MUST carry `ORDER BY posting_index`.** Postgres guarantees
no order without one, and because every UPDATE moves the row to a new
heap location a seq scan tends to return recently-touched legs last —
so an unordered read permutes a user's split reliably rather than
rarely: without that clause, recategorizing one line of a many-line
split reshuffles every other line as a side effect.

The other way a writer can corrupt the sequence is **duplicate
`legId`s in one `items[]` array**. Both items resolve to the same
tracked row, so the last array position wins, the earlier one writes
nothing, and the header ends up with fewer postings than the request
listed plus a hole in `posting_index` — which then trips the
`posting_index > 0` split detectors and the `posting_index = 0`
canonical-leg lookup. The patch path now rejects this with
`transaction-posting-leg-id-duplicated` (422) rather than applying it;
each item must reference a distinct existing leg, or omit `legId` to
create a new posting.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `header_id` | `UUID` | NOT NULL FK → `txn_headers(id)` ON DELETE CASCADE | |
| `account_id` | `UUID` | NOT NULL FK → `accounts(id, ledger_id)` ON DELETE RESTRICT | Composite FK includes `ledger_id` (migration 049) so a leg can only reference an account in the same ledger as its `txn_headers` row. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Denormalized from `txn_headers.ledger_id` (migration 049). All three composite FKs (header / account / security-when-set) reference `(parent_id, ledger_id)` so the DB structurally refuses a leg whose parents span different ledgers. |
| `posting_index` | `INTEGER` | NOT NULL CHECK ≥ 0 | Pairs the two legs of one posting, **and is the split's display order** — see the note above. For an MD txn with N splits, the legs use `posting_index = 0..N-1`; a patch that drops a middle posting compacts the survivors rather than leaving a hole, which matters because readers treat `posting_index > 0` as "this header is a split" and `posting_index = 0` as the canonical leg. |
| `leg_memo` | `TEXT` | | This side's own memo: either side of a one-posting transaction, or a split line's on its split side. On a split line's opposite side a value here is an override, shown there in place of the line's. NULL means the side shows what it inherits (the other side's, under the one rule; see `notes_memo`). Never blank, and never set on an actioned multi-posting event's investment side, which shows the header's (migration 264 moved such values to the header). An input of the stored presentation. |
| `amount` | `NUMERIC(19,4)` | NOT NULL, CHECK `amount = round(amount, 3)` (migration 301) | Impact on `account_id`, in that account's currency. The two legs of a posting sum to zero, except on a transfer between currencies (see the note above). **Money is authoritative at its currency's minor units** (ADR-0073, as amended by [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15): 0, 2 or 3 places, from the ISO 4217 data file (see Conventions). For investment share-trades the request `amount` is the real settled cash and lands here exactly. An amount finer than its currency (an unrounded `price × shares`) is barred: it leaks into fractional running balances that render as a negative zero. The column keeps scale 4 for headroom. **Two layers hold the rule.** The database holds a ceiling of 3 places for every currency: migration 301 drops migration 159's `ck_txn_legs_amount_scale_2` and adds the currency-agnostic `amount = round(amount, 3)`, NOT VALID and then validated, which every existing row passes. **The API holds the exact rule** (settled by Claude, 2026-11-16): a CHECK on this table cannot read the account's currency, so the exact rule per currency is the API writers': a typed amount finer than its currency is refused with a 422 and a named error code that names the currency and its places, and a figure the API computes (a loan payment and its split, the price-times-shares fallback, a lot's basis, an estimate, a budget merge) is rounded once at its destination currency's places, half away from zero, the loan payment rounding up to one minor unit. A category leg is checked and rounded at the real side's currency, never the category's own `currency_code`; a posting between two categories has no real side, so its lines take the category's own code. No trigger backs the rule (ADR-0032). **A category leg carries the real side's amount negated** (ADR-0114 D13), and the consistency check's `category_amounts` projection reports one that does not: a finding, counted in the verdict like every other projection, not an advisory (settled by Claude, 2026-11-16). Its repair sets the category leg to the real leg negated, only on a posting of exactly one real leg and one category leg, read again under the ledger write lock (`fn_ledger_write_lock`, migration 275); no real leg changes, so no account's balance moves. Any other posting with a category leg whose legs do not sum to zero (both legs on categories, or more than one real or category leg) is listed in the advisory below with its reason, `category-line-between-categories` or `category-line-many-lines`, and nothing rewrites it. **A stored amount that does not fit its currency is kept**: only a value a write changes is checked, so an edit that leaves it unchanged passes, and the consistency check lists every amount that does not fit as an advisory, as ADR-0084 D6's unbacked-price advisory is: shown beside its verdict and not counted in it (no `consistency.drift`, no Overview Problem, no ADR-0102 dot), with no repair; the cure is an edit by an owner or editor (settled by Claude, 2026-11-16). A leg of a header merged into another (`txn_headers.is_merged_into`) is not listed and not counted by a currency change or a move: no screen shows or opens it. Deleting the winner un-merges it, and a leg of it that does not fit is then listed like any other (ADR-0114 D15, decided by the owner, 2026-11-17). Migration 159 added the 2-place CHECK and rounded the sub-cent legs then stored to 2 decimals; it is never edited. |
| `security_id` | `UUID` | FK → `securities(id, ledger_id)` ON DELETE RESTRICT | Composite FK includes `ledger_id` (migration 049). Set on legs that participate in an investment posting. On the **holdings-side** leg of a buy/sell/divr it carries the position change. NULL on cash-side legs and non-investment legs. |
| `quantity` | `NUMERIC(25,12)` | | Share count on the holdings-side leg. 12-decimal scale matches MD's 11-decimal display with one digit of buffer; covers reinvest-dividend math (cash ÷ price) without rounding loss. |
| `unit_price` | `NUMERIC(25,12)` | CHECK (`unit_price IS NULL OR unit_price >= 0`) | Per-share price on the holdings-side leg. **Derived metadata** = `amount ÷ \|shares\|` rounded to 6dp (ADR-0073) — it reconciles to the authoritative `amount` rather than driving it, so `unit_price × quantity` need **not** equal `amount` (a rounded wire price against an exact settled total is normal and faithful to the feed). The wire's originally-reported price is preserved separately in `txn_headers.ingest_unit_price`. Non-negative CHECK added in migration 051 — price is a magnitude; the trade direction lives in `quantity` and `amount`. A signed price (positive cash ÷ signed qty) would store a negative unit_price on every Sell row, which the CHECK refuses. |
| `posting_role` | `TEXT` | CHECK in `{security, income, transfer, fee}` ∪ NULL | Investment posting role marker (migration 056). Stamped by the importer from MD's `invest.splittype` and by the editor when adding postings; both legs of a posting share the same value. NULL on non-investment legs. The MD importer also writes `transfer` on both legs of a one-posting bank-route brokerage row (the `transfer` action on `txn_headers.action`; migration 260 for earlier imports), whose other side may be a category: the returns walk counts a `transfer`-role leg facing a category as a contribution or withdrawal. Source of truth for fee identification — `posting_role='fee'` combined with the brokerage's `is_trade_commission=TRUE` is what folds a fee posting into cost basis. Category is metadata, not behavioral. |
| `account_postings_on_header` | `INT` | NOT NULL DEFAULT 1 | Denormalized count (migration 120, ADR-0046) of this header's distinct postings **on this leg's account**: a shape count, not the side that entered the transaction, which is `txn_headers.originating_account_id` (migration 263). Maintained by `fn_recompute_posting_counts_for_header()` via the recompute interceptor (no trigger). Replaced a per-row correlated `COUNT(DISTINCT posting_index)` subquery in `resolved_transactions`. |
| `header_total_postings` | `INT` | NOT NULL DEFAULT 1 | Denormalized count (migration 120) of the header's total distinct postings (constant across the header's legs). Same maintenance path. The register entry key is not derived from the two counts: it is the header on the owning side and the leg everywhere else, stored as `notes_groups_by_header`. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `notes_kind` | `TEXT` | CHECK `ck_txn_legs_notes_kind` in `{OnePosting, EventCounterpartOne, SplitSide, SplitOpposite, EventSide, EventOpposite}` | Migration 264, with the nine `notes_*` columns below: the **stored memo-and-tags presentation** of the leg as seen from its own account — what the one C# rule (`MemoTagRules`, through `StoredNotes.Compute`) produces from the header's rows. Derived, never an input: no SQL computes it. Written only by `LegNotes`, in the transaction of the write that changed an input (the save interceptor for tracked saves, an explicit recompute after set-based writes, the import's in-transaction hook); every tracked write that creates legs inserts them with their values (`AnnotationWriter` stamps each new leg through `LegNotes.Stamp`; the EF mapping on `TxnLegRow` is insert-only), so the recompute in the same save normally has nothing to change on a new leg; whatever a save inserts, that recompute leaves what the rule computes, and the tests hold every create path to stamping what the rule computes. The Moneydance import inserts through Dapper and fills its legs with the in-transaction recompute. Filled for existing data by the C# step `264_stored_leg_notes_backfill`, which computes through the same rule and writes through a store of its own that maps these columns and `txn_leg_note_tags` exactly as 264 creates them, so a later change to either cannot break an upgrade that runs the step before it. Captured by snapshots and copied back verbatim by a restore. |
| `notes_is_read_only` | `BOOLEAN` | | The row offers no Edit or Duplicate. |
| `notes_groups_by_header` | `BOOLEAN` | | The register entry key is the header (the owning side), not the leg. |
| `notes_memo` | `TEXT` | | The memo the row shows; NULL shows none. |
| `notes_memo_inherited` | `BOOLEAN` | | The shown memo comes from the other side. |
| `notes_inherited_memo` | `TEXT` | | What the row would show with its own memo blank, as the row is stored (the editors' placeholder while the field is empty and the save keeps this leg); NULL when there is nothing to inherit. |
| `notes_inherit_from_account_id` | `UUID` | | The account the inherited value comes from. |
| `notes_tags_inherited` | `BOOLEAN` | | The shown tags come from the other side. |
| `notes_header_memo` | `TEXT` | | The transaction's memo where the header is in play (a split-side line's search, `transactionMemo`). |
| `notes_header_in_play` | `BOOLEAN` | | The header's tags count for filters and reports. |

**`ck_txn_legs_notes_whole`**: a leg's `notes_*` are either all NULL (unfilled) or the kind and the five flags are all set. Unfilled is a state no writer leaves behind; the stored-notes projection of the consistency check (`stored_notes`) reports any leg whose values are not what the rule computes, unfilled ones included.

> **Notes.**
> - Migration 046 dropped the legacy `commission` column — the fee posting is the source of truth for the cash effect, and `lots.unit_cost` carries the apportioned commission for cost-basis math.
> - Migration 047 dropped `investment_action` from this table — action is a *header* property now (one action per event, see `txn_headers.action`). The action CHECK list moved to the header column (9 values at mig 062; `transfer_shares` added at mig 151).
> - Migration 049 added `ledger_id` (denormalized from the header) and converted the FKs on `header_id` / `account_id` / `security_id` into composite `(id, ledger_id)` references. The DB now rejects any leg whose header, account, or security points outside the leg's own ledger — cross-ledger leakage is structurally impossible.

**Unique index** `uq_txn_legs_posting`: `(header_id, posting_index, account_id)`
— enforces the two-legs-per-posting invariant, and is the natural key the Moneydance importer maps each inserted leg back by (its leg writes are plain inserts into a new ledger).

### `txn_header_originals`

The FEED's header values and the transaction's amount on its originating account, with a rounded-at-import flag, captured once by the first edit that overwrote them (migration 230, [ADR-0100](decisions/0100-canonical-holds-current-sidecar-holds-original.md)), or seeded at import as listed under **Writers** (the amount and flag: migration 301, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)). One row per header whose current values may differ from the feed's: every header edited in Coffer, plus every Moneydance-imported header whose `ol.orig-payee` or `ol.orig-memo` differs from the curated `desc` / `memo` the importer wrote (ADR-0100, 2026-10-21 amendment), plus every ingested header whose amount an import rounded to its account's currency's places ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15; ADR-0100, 2026-11-16 amendment). The row's existence is the signal `resolved_transactions.has_overrides` reports (a view column no API response carries); for Moneydance rows and rows seeded at import it does **not** mean "edited in Coffer".

Every feed-value column (`payee`, `memo`, `posted_at`, `transacted_at`, `check_number`, `amount`) is nullable, because the feed itself may not have supplied one — a manual row edited later captures nulls, and that is the correct original.

**Writers.** Five. The first three each write a whole row once per header; a promote-on-clear also changes the amount of a row on file, and the SimpleFIN pre-split backfill its payee and memo (below):

- **The first edit** (`HeaderOriginals.CaptureAsync`, through `AnnotationWriter.CaptureOriginalsAsync`), before any mutation and in the same transaction. It snapshots every column, the amount included, whatever field the edit changes, so the first edit of any bank row that has no originals on file yet now keeps its amount from before the edit, not only a rounded one's (an accepted consequence of D15). `rounded_at_import` is false. It returns early when a row is already on file, so a seeded row's first edit leaves it.
- **The Moneydance importer** (`FeedOriginals.SeedFor`, migration 260 for earlier imports), where a feed payee or memo differs. Its flag is false: a Moneydance amount is never rounded (D15 refuses a currency whose places do not fit). Its seed carries the amount as written, as it carries the dates and check number as written, since a later edit of a seeded row keeps nothing; seeds already made by migration 260 keep none (settled by Claude, 2026-11-16).
- **An import that rounds an amount** (D15): SimpleFIN, OFX/QFX, QIF, CSV and the brokerage CSV shim. It seeds the row at import, as whole as `CaptureAsync`'s snapshot: payee, `posted_at`, `transacted_at` and `check_number` as written, the feed's memo, the unrounded amount and `rounded_at_import` true. An import that does not round writes no row. The seed passes the feed's memo itself (the memo `AddIngestedLegsAsync` is given) and calls `HeaderOriginals.CaptureAsync` directly: `CaptureOriginalsAsync` reads the legs with `AsNoTracking`, so before `SaveChanges` it would see none and record the header's memo, NULL on a one-posting ingested row, instead of the feed's (settled by Claude, 2026-11-16).
- **A promote-on-clear** (SimpleFIN, a pending row that arrives cleared) restates the feed's amount, so it is the one write that changes an amount already on file (settled by Claude, 2026-11-16). It rounds the cleared figure like any ingest. Where the row has originals, it sets `amount` to the cleared figure and `rounded_at_import` to whether it rounded, and leaves the other columns; where it has none and the figure is rounded, it seeds the row as an import does; otherwise it writes nothing. A rounded promotion sets `needs_review` on a row still in a register (not a merge loser or a soft-deleted row; a merge loser whose synced leg changes holds its winner for review instead), and `ledger_operation_promotions.became_amount` records the amount written to the legs, not the bank's figure, which its `NUMERIC(19,4)` could re-round. A row the user split into more than one posting has no leg rewritten: the originals are restated as above, `was_amount` and `became_amount` both record the split's total on the synced account, and a cleared figure other than that total sets `needs_review` (ADR-0114 D15, decided by the owner, 2026-11-17). Without this the originals of a seeded row would still describe the pending figure, and a promoted amount would reach the legs unrounded. The file imports have no promote path: a known row gets only the NULL-only carrier backfill, which never touches an amount.

**The amount** (settled by Claude, 2026-11-16) is the transaction's amount on its originating account (`txn_headers.originating_account_id`): the sum of its legs there, which is the Amount its own register shows (the one leg on a bank register, `InvestmentEventProjector`'s sum on an investment register, ADR-0028). For an ingested row that is the imported account's leg, the amount the provider hands the orchestrator (`IngestedTransaction.Amount`) before rounding. It is in the row's own stored sign, the account holder's: after a CSV layout's `amount.invert` or its debit and credit columns (`-|debit|`, `|credit|`), QIF's signing by action, and OFX's and SimpleFIN's signs as sent, so the cell text can differ in sign when a layout inverts it or uses debit and credit columns. Every provider parses into `System.Decimal`, so the unbounded `NUMERIC` holds the figure exactly, its scale as parsed included. NULL on a row captured before migration 301, which nothing backfills because a leg's amount today may be an edit, and on a header with no originating account.

**The Review marker** reads `amount` and `rounded_at_import` through `resolved_transactions` (below) onto the register row (`RegisterRowDto`, as `IngestAmount` reaches `InvestmentRowDto`), so the To review widget, the Review… dialog and the register share one projection. It shows while the row needs review and the flag is set.

**The SimpleFIN pre-split backfill** rewrites the payee and memo of a row that still looks as the pre-split importer left it, without a capture. Where the row has originals that still hold the values it replaces, it writes the new payee and memo into them too, so payee recall (which anchors on the originals' payee) reads a seeded row as it reads one with no originals (settled by Claude, 2026-11-16).

**What lists the columns** (the D15 build extends each; settled by Claude, 2026-11-16): migration 301 (`ALTER TABLE` on migration 230's table; its header comment states the type it uses, which `SchemaDriftGuardTests.Migration_comments_do_not_assert_a_stale_numeric_scale` parses), `resolved_transactions` (a new definition adding the two through its existing `LEFT JOIN orig`), the EF mapping in `AppDbContext` and `TxnHeaderOriginalRow`, `HeaderOriginals.CaptureAsync`'s initializer, the Moneydance seed's explicit SQL column list (`TransactionsRepository` in the importer, whose `INSERT … SELECT` sets the flag false and the amount as the Moneydance writer above is decided), `ResolvedTransactionView` and the register DTOs, the tests with explicit column lists (`SyntheticLedger`, `SimilarPayeesTests`, `BrokerageTransferAndOriginalsBackfillTests`), this section, its ER block and the view table, and ADR-0100. Snapshot capture (`to_jsonb` over the whole row) and both restore paths (`SELECT * FROM jsonb_populate_recordset`) are generic over columns and need no change; a snapshot from before the migration is refused for its schema version, so it never meets the NOT NULL flag. The restore-drill dataset gains the columns when it is regenerated. Shipped migrations that list the old columns (230, 260) are history and are not edited.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `header_id` | `UUID` | PK FK → `txn_headers(id)` ON DELETE CASCADE | |
| `ledger_id` | `UUID` | NOT NULL; composite FK `(header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE CASCADE | Denormalized from the parent header so RLS gates on `ledger_id` directly, exactly as the table it replaced did. Composite FK `txn_header_originals_header_ledger_fkey`. |
| `payee` | `TEXT` | | What the feed said |
| `memo` | `TEXT` | | What the feed said |
| `posted_at` | `TIMESTAMPTZ` | | |
| `transacted_at` | `TIMESTAMPTZ` | | |
| `check_number` | `TEXT` | | |
| `amount` | `NUMERIC` | | Migration 301 ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)). The transaction's amount on its originating account, in the row's stored sign: the bank's figure as sent, before rounding, on a row an import seeded or a promote-on-clear restated; the amount as it stood on a row the first edit captured. Unbounded, so no figure is re-rounded on the way in. NULL on rows captured before the migration. See **The amount** above. |
| `rounded_at_import` | `BOOLEAN` | NOT NULL DEFAULT false | Migration 301. True when an import (or a promote-on-clear) rounded the amount to its account's currency's places, so `amount` is the bank's figure and differs from the legs for that reason, not because of an edit. With `needs_review`, it is what shows the Review marker. False on every other row, existing rows included. |
| `captured_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | When the first edit captured this, or when the importer seeded it (migration 260's seeds take the header's `created_at`). Diagnostic only. |

**RLS**: read `FOR SELECT TO coffer_app` scoped to the user's `user_ledger_grants`; write `FOR ALL` scoped to grants with role `owner`/`editor`. Same shape as the table it replaced.

**Write rule.** `HeaderOriginals.CaptureAsync` is the API's capture, called by both the bank and the investment PATCH before either touches a header column, and by an import that rounds an amount, which seeds the row with the unrounded amount and `rounded_at_import` true (D15). Capture is **unconditional on the field** (every column is snapshotted, the amount included, not just the ones this edit changes — a per-field capture would need a per-field "was this captured" marker, the exact ambiguity the flip removes) and **once per header** (a row already on file was written by an earlier edit and already holds the feed's values; overwriting it would redefine "original" as "whatever it was before the most recent edit"). The one exception is a promote-on-clear, which restates the feed's own amount: it sets `amount` and `rounded_at_import` on a row already on file and leaves the other columns (see **Writers** above).

**The Moneydance importer is a second writer** (FU-024; the import-rounding seed and promote-on-clear are under **Writers** above). `FeedOriginals.SeedFor` decides per header, on both import routes: a seed only when a non-blank feed payee or memo differs, ordinally, from the value written: the payee on the header; the memo wherever it was written, the header of a multi-posting transaction or the home account's leg of a one-posting one (compared trimmed). A side with no feed value takes the written one. `TransactionsRepository.BulkUpsertAsync` inserts it in the same transaction as the headers and reads `ledger_id`, `posted_at`, `transacted_at` and `check_number` off the header row just inserted, so the snapshot is as whole as `CaptureAsync`'s. Reminder templates get none. **Migration 260** seeds headers imported before this, from `provider_raw_payload`, wherever no row exists yet. It also brings an existing row (a first edit's capture of what the old importer wrote) to what it would hold under the new importer: a non-blank feed payee or memo that differs from the captured one replaces it, and on an investment row a NULL `check_number` takes MD's `chk`, even where the header's own check number has since been set, since the import dropped it from both. Because the API's capture is once per header, a later edit of a seeded header keeps the feed's values on file.

**Predecessors.** This replaced `txn_header_overrides` and `txn_leg_overrides`, both dropped by migration 230.

- `txn_header_overrides` held the user's edits over an immutable feed row. Its columns were plain nullable ones with no "is overridden" marker, so NULL meant both *not overridden* and *overridden to empty* and the read-side COALESCE always chose the first — a payee, memo or check number could not be cleared. It also could not see a canonical write, so an investment edit of a row carrying an override landed underneath it, invisibly. Its `is_hidden` column had exactly one writer in the whole repository: a test fixture. Migration 230 folded it with `COALESCE`, never assignment — a row could carry only some fields, so an assignment would have blanked the date on every one that had none.
- `txn_leg_overrides` (`leg_memo`, `amount`) was never written by the API. It cost a join on `resolved_transactions` and on the balance walk for a table no code path filled, so it was dropped rather than flipped.

Also of historical note: `status` was on the header override table prior to migration 030 and was dropped — reconciliation status is user-action data (the user clicks the badge to cycle), not an edited copy of an imported value. Since migration 171 (ADR-0082) it lives in the per-leg `txn_leg_recon` overlay (which survives an edit on both write paths — see **Leg IDENTITY** under `txn_legs`; it cascades on leg delete, so an edit that rebuilt the legs instead of keeping them would silently un-reconcile the row) — reconciliation is per-account, so it can't be a single header value.

### `txn_leg_recon`

Per-**leg** reconciliation overlay (migration 171, ADR-0082). Reconciliation is a per-account activity — a transfer from Checking to Savings can be cleared in Checking while still uncleared in Savings — so status can't be a single header value. It moved off `txn_headers` to here, keyed by `leg_id`. Only real-account legs are ever reconciled; category legs never get a row and resolve to `uncleared`. This is a genuine OVERLAY and stays one — clearing is a state the leg has no column for, not an edited copy of a column that already exists — which is why migration 230's flip left it alone while retiring the two override tables. A leg with no row reads as `uncleared` — `resolved_transactions` COALESCEs it.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `leg_id` | `UUID` | PK FK → `txn_legs(id)` ON DELETE CASCADE | One row per reconciled leg. |
| `ledger_id` | `UUID` | NOT NULL; composite FK `(leg_id, ledger_id)` → `txn_legs(id, ledger_id)` ON DELETE CASCADE | Denormalized for RLS + the composite FK (mig 072). |
| `status` | `TEXT` | NOT NULL DEFAULT `'uncleared'` CHECK in (`uncleared`, `reconciling`, `cleared`) | The 3-state recon vocabulary (formerly `txn_headers.status`, mig 030). `reconciling` is a workflow / visual aid (MD parity); functionally uncleared for reporting. |
| `cleared_at` | `TIMESTAMPTZ` | | Audit timestamp for the `status='cleared'` transition. DB CHECK `(status='cleared') ⇔ (cleared_at IS NOT NULL)` keeps the pair consistent. |
| `cleared_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | User who marked the leg cleared. NULL when uncleared / reconciling or when the user row was removed. |

**Index** `idx_txn_leg_recon_ledger`: `(ledger_id)`. **RLS**: `FOR ALL TO coffer_app` scoped to the user's `user_ledger_grants`. Writes upsert the register account's leg(s) via `SetReconStatusAsync` / `BulkSetReconStatusAsync`; reads flow through `resolved_transactions` (`COALESCE(lr.status, 'uncleared')`).

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

`fn_recompute_balances_for_account` deletes an account's rows from its anchor on and inserts them again: the rows `balance_walk` writes, seeded from the last stored running balance before the anchor. Since migration 277 it clears every stored row in the window on any header (it cleared through `live_txn_headers`, which drops reminder templates, so a running balance stored on a template survived every recompute), so what remains in the window is exactly what the walk writes; and it reads the seed only from a running balance the walk writes, on a header in `balance_walk_headers` with a line on the account (it read through `live_txn_headers`, which keeps hidden and merged-away headers, so a running balance stored on one could seed a later save). Since migration 275 it takes the ledger write lock of the account's ledger (`fn_ledger_write_lock`, see the function table; [ADR-0112](decisions/0112-one-write-lock-per-ledger.md)) before it reads the seed. In a transaction that took the lock first, as every writer that can recompute does, that does nothing. As a statement of its own, which is how the consistency repair of `balances` recomputes each account of a ledger, it takes the lock for that statement alone, so a save waits for at most the one account in flight. In a transaction that has written or row-locked without the lock it is refused, so a writer that misses the lock fails rather than races. Two writers of one ledger therefore run one after the other, and the later reads what the earlier committed. Two runs over one account at once would otherwise collide: the later insert would meet the rows the earlier committed and fail on the primary key, or a run anchored past every committed row would store a running balance that leaves the earlier transaction out. Every caller runs READ COMMITTED, which is what lets the statements after the wait see the earlier commit. A transaction that recomputes in several steps (an edit saved in two parts, a bulk delete) holds the lock from its first statement, so no other writer of the ledger runs between its steps. Snapshot restore rebuilds every balance of the ledger with `fn_recompute_balances_for_ledger` while it holds the lock's session form (see `ledger_snapshots`). One repair of a ledger runs at a time ([operations.md](operations.md), "The check and the repair").

### `txn_header_tags`

Many-to-many join: headers ↔ tags. The whole transaction's tags, where a header holds them: a split's and a multi-posting investment event's. A one-posting header holds none; its tags sit on its legs (`txn_leg_tags`), and migration 264 moved every existing one-posting header's tags there. A raw input of the stored presentation: readers read the tags a row shows and counts from `txn_leg_note_tags`.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `header_id` | `UUID` | composite FK `(header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE CASCADE | Composite PK component. Composite FK `txn_header_tags_header_ledger_fkey` (migration 072). |
| `tag_id` | `UUID` | composite FK `(tag_id, ledger_id)` → `tags(id, ledger_id)` ON DELETE CASCADE | Composite PK component. Composite FK `txn_header_tags_tag_ledger_fkey` (migration 072) — both the header and the tag must share this row's `ledger_id`. |
| `ledger_id` | `UUID` | NOT NULL | Denormalized (migration 072) so RLS gates on `ledger_id` directly instead of a transitive subquery. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

**RLS** gates on `ledger_id` directly (migration 072), as the role-aware pair of migration 174: `txn_header_tags_read` (SELECT, any grant) and `txn_header_tags_write` (ALL, `role IN ('owner','editor')`).

### `txn_leg_tags`

Many-to-many join: **legs** ↔ tags (migration 243, [decisions/0103-tags-at-both-levels.md](decisions/0103-tags-at-both-levels.md)). A tag on one side (leg) of a posting. Like `txn_header_tags`, a raw input of the stored presentation, which readers read from `txn_leg_note_tags`.

Distinct from `txn_header_tags`, and neither is derived from the other: a header tag is a claim about the transaction (*Holiday*, *Tax 2026*), a leg tag a claim about one posting (*Project B*). ADR-0009 had put tags at transaction level and named its own reopening condition — "rejected unless real use surfaces a split-level tag pattern that matters" — and a payment split across two projects, each line tagged with its project, is that pattern. The union model ADR-0009 offered as the alternative was **not** taken: it would make a transaction tag a derived artifact that changes silently when a leg is added later, and would require rewriting every header tag that is correct today. This table moves no existing row.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `leg_id` | `UUID` | composite FK `fk_txn_leg_tags_leg` `(leg_id, ledger_id)` → `txn_legs(id, ledger_id)` ON DELETE CASCADE | Composite PK component. |
| `tag_id` | `UUID` | composite FK `fk_txn_leg_tags_tag` `(tag_id, ledger_id)` → `tags(id, ledger_id)` ON DELETE CASCADE | Composite PK component. Both parents must share this row's `ledger_id`, so a leg cannot be tagged from another ledger's vocabulary. |
| `ledger_id` | `UUID` | NOT NULL | Denormalized, following migration 072. Not a convenience: `fn_snapshot_write_part` chunks on a generic `WHERE t.ledger_id = $1`, so a snapshot-captured table without this column **cannot be captured at all**. The composite FKs above are what make it provably equal to the parents' — which is the whole justification for the flat RLS predicate, rather than it being an unverified copy. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Primary key `pk_txn_leg_tags (leg_id, tag_id)` — the pair *is* the fact, and a surrogate id would permit the same tag twice on one posting.

Index `ix_txn_leg_tags_ledger_tag (ledger_id, tag_id)` — "which legs carry this tag?" is the filter direction the PK cannot serve, since it leads on `leg_id`. Ledger-first because every query is ledger-scoped before it is anything else.

**RLS** the role-aware pair, as `txn_header_tags` has: `txn_leg_tags_read` (SELECT, any grant) and `txn_leg_tags_write` (ALL, `role IN ('owner','editor')` in `USING` and `WITH CHECK`). Migration 243 created the table after 174 with one any-grant `FOR ALL` policy, so a viewer could tag postings at the database; migration 254 split it (FU-086).

Written by `AnnotationWriter` (the API's one write primitive, storing what `AnnotationPlanner` in `Coffer.Domain.Annotations` plans), by the API's tag merge (`TagsRepository.MergeAsync`, which repoints pairings), and by the Moneydance importer's `TagsRepository.BulkSetLegTagsAsync`, which puts each side's own tags here. In the API, tag NAMES resolve through `TagDictionary` for both levels, so a header and one of its postings tagged with the same new name in one request land on a single `tags` row.

### `txn_leg_note_tags`

Migration 264. The tag half of the stored memo-and-tags presentation (the other half is `txn_legs.notes_*`): one row per (leg, tag) the rule's output for the leg names, with the roles the tag plays there. Derived, written only by `LegNotes`; readers filter, group and name tags from it instead of evaluating the rule.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `leg_id` | `UUID` | composite FK `fk_txn_leg_note_tags_leg` `(leg_id, ledger_id)` → `txn_legs(id, ledger_id)` ON DELETE CASCADE | PK component. |
| `tag_id` | `UUID` | composite FK `fk_txn_leg_note_tags_tag` `(tag_id, ledger_id)` → `tags(id, ledger_id)` ON DELETE CASCADE | PK component. By id, so a rename touches nothing. |
| `header_id` | `UUID` | NOT NULL | The leg's header when the row was written. A recompute compares it with the leg's current header, so a leg a set-based writer moved gets its rows rewritten. A tag filter or tag-filtered report reads the tag's headers from it first (`StoredNotesQuery.InPlayTagHeaders`) and passes them to its `resolved_transactions` read as a list of values, so only those headers' legs are paired with their other leg before the exact per-side test. |
| `ledger_id` | `UUID` | NOT NULL | The flat RLS predicate, as `txn_leg_tags`. |
| `shown` | `BOOLEAN` | NOT NULL | The row shows the tag. |
| `in_play` | `BOOLEAN` | NOT NULL | Filters and reports count it. |
| `inherited` | `BOOLEAN` | NOT NULL | The row would show it with its own tags blank. |
| `transaction_level` | `BOOLEAN` | NOT NULL | It is the transaction's tag, where the header is in play. |

Primary key `pk_txn_leg_note_tags (leg_id, tag_id)`, which also serves the cascade from a deleted leg. Index `ix_txn_leg_note_tags_header (header_id)` (a recompute reads a header's rows), the partial `ix_txn_leg_note_tags_tag_in_play (tag_id, leg_id) WHERE in_play` (a tag filter or report starts from the tag: its headers, then its legs), and `ix_txn_leg_note_tags_ledger_tag (ledger_id, tag_id)`, as `txn_leg_tags` has: the cascade from a deleted tag matches every role, which the partial index cannot serve, so without it each deleted tag would scan the table. `StoredNotesBackfillTests` holds both cascades to an index.

**Written by difference.** A recompute keeps a row whose key and value (header and the four roles) are what the rule computes, deletes every other row of the headers and legs it recomputes, and inserts what is missing: a row whose value changed is deleted and inserted again, so `coffer_app` holds `SELECT, INSERT, DELETE` and no `UPDATE`.

**RLS** the role-aware pair: `txn_leg_note_tags_read` (SELECT, any grant) and `txn_leg_note_tags_write` (ALL, `role IN ('owner','editor')`).

**Snapshots** capture it as its own part and a restore copies it back verbatim after `txn_legs` and `tags`, with no recompute: the restore refuses a snapshot of another schema version, and a change to the rule ships as a numbered C# recompute step, which also retires older snapshots.

**Captured** by `fn_ledger_snapshot_payload` / `_part_names` / `_insert_order` (after both `txn_legs` and `tags`), and cleared by `fn_ledger_snapshot_clear` and `fn_ledger_delete`. `SchemaDriftGuardTests` forces that decision into the same migration as the table: a `ledger_id`-bearing table that is neither captured nor excluded fails the classification guard immediately. The explicit `DELETE` in the two clear paths is belt-and-braces — `ON DELETE CASCADE` from `txn_legs` already removes these rows — and matches what `txn_header_tags` does for the same reason: the ordering stays readable without tracing FKs.

### `saved_reports`

A **memorized report**: a persisted `ReportSpec` with an owner and a visibility (migration 244, [decisions/0104-reports-are-a-place-you-go.md](decisions/0104-reports-are-a-place-you-go.md) D3).

A table rather than a `reports` namespace in `user_preferences`, which was the cheap option. That table is a *singleton document per namespace* — `quotes` and `dashboard` are each one blob with their own endpoint pair — so a collection of user-named, individually-deletable things would share one row: every save rewrites the whole list, no report gets a stable id, and, decisively, a preference is **permanently per-user**. Coffer is multi-user on one ledger (ADR-0024), and a household that cannot show each other a report has a missing feature rather than a missing preference. Retrofitting sharing onto the blob would mean migrating it into this table anyway.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | The stable id a URL can carry — the thing a preference blob could never give a report. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | Also what snapshot capture chunks on. |
| `owner_user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | Who wrote it. A ledger member may *read* a shared row but only the owner may write it. |
| `name` | `TEXT` | NOT NULL CHECK `length(trim(name)) > 0` | |
| `spec` | `JSONB` | NOT NULL CHECK `jsonb_typeof(spec) = 'object'` | The serialized `ReportSpec`, stored whole. Columns per field would fork the shape the MCP tools already take, so the next dimension would need a migration here *and* a C# change, and the two would drift. The cost is that the database cannot validate a spec — only that it is an object — so a row the API cannot deserialize is a 500 its owner can delete. |
| `visibility` | `TEXT` | NOT NULL DEFAULT `'private'`, CHECK `ck_saved_reports_visibility` IN (`private`, `ledger`) | Default private: sharing is a decision someone makes, not one they discover they made. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Unique `uq_saved_reports_owner_name (ledger_id, owner_user_id, name)` — one author cannot keep two reports of the same name in one ledger. Two *different* authors can: a shared ledger has no single namespace to arbitrate, and refusing a name would leak that someone else's private report already uses it.

Index `ix_saved_reports_ledger_owner (ledger_id, owner_user_id)` — the list query is "every report I can see in this ledger", which is ledger first and then the owner half of the policy's split.

**RLS** two policies since migration 254 (FU-086). `saved_reports_read` (SELECT) admits rows in a granted ledger that are either shared (`visibility = 'ledger'`) or your own; `saved_reports_own` (ALL) admits only your own, in `USING` and `WITH CHECK`. `visibility = 'ledger'` means readable-by, not writable-by. Migration 244 expressed that as one `FOR ALL` policy whose `USING` was the read rule and whose `WITH CHECK` was the owner rule, which stopped a member's UPDATE of a shared report but not their DELETE: a DELETE is decided by `USING` alone.

Written by `SavedReportsRepository` behind `/api/ledgers/{id}/reports/saved`. The repository's explicit owner predicate on update and delete is **not** the guarantee — the policy is, since 254 for delete too — it exists to turn a silent zero-row write into a 404.

**`spec.tagId`** is the one stored tag reference outside a foreign key, and `saved_report_spec_tag_id` is its only parser. So the second writer is a tag merge, which repoints it on every member's reports: the API's merge through the SECURITY DEFINER `saved_reports_repoint_tag` (migration 263), and migration 264's case-variant merge directly. Both write only `spec.tagId` and leave `updated_at` unchanged. Without it a report filtered on a merged-away tag would silently match nothing.

**Captured** by `fn_ledger_snapshot_payload` / `_part_names` / `_insert_order` (last; nothing references it), and cleared by `fn_ledger_snapshot_clear` and `fn_ledger_delete`. Captured rather than excluded following `user_account_groups` (migration 033, the sidebar tabs; dropped in migration 258), the closest precedent at the time — user-created, per-(user, ledger), carrying a `user_id`, and captured. A restore that silently dropped someone's saved reports would be a data loss the schema never admitted to. `SchemaDriftGuardTests` forces the choice into this migration either way.

*Pins are not here.* ADR-0104 D2 makes pinning a separate act that can target a saved report **or** a catalog preset, and it is per-user rather than per-ledger. Its table arrives with the rail that renders it.

### `report_pins`

A report one user keeps in their sidebar (migration 245, [decisions/0104-reports-are-a-place-you-go.md](decisions/0104-reports-are-a-place-you-go.md) D2). The sidebar only: a report on the Overview is a tile in the reader's `dashboard` layout (`user_preferences`), not a row here, and neither adds the other (ADR-0104 D2, 2026-11-06 amendment).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | A surrogate key is unavoidable once the target is a choice: a composite PK cannot span two nullable columns. |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | Also what snapshot capture chunks on. |
| `saved_report_id` | `UUID` | NULL, composite FK `(saved_report_id, ledger_id)` → `saved_reports(id, ledger_id)` ON DELETE CASCADE | Set for a memorized report. |
| `catalog_key` | `TEXT` | NULL, CHECK non-blank | Set for a canned report. Only the KEY is stored — name and spec live in the SPA's catalog, so improving a preset never needs a data migration, and a key the client no longer knows renders nothing rather than lingering as an entry that opens a retired report. |
| `sort_order` | `INTEGER` | NOT NULL DEFAULT 0 | The reader's own order; a new pin appends, so pinning does not reshuffle a rail someone arranged. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

`ck_report_pins_one_target` — `(saved_report_id IS NULL) <> (catalog_key IS NULL)`. Exactly one, never both and never neither; without it a row could point at nothing and still satisfy every FK.

Unique `uq_report_pins_saved (user_id, saved_report_id) WHERE saved_report_id IS NOT NULL` and `uq_report_pins_catalog (user_id, ledger_id, catalog_key) WHERE catalog_key IS NOT NULL`. **Partial on purpose**: a plain composite UNIQUE over a nullable column would let the same target be pinned repeatedly, because NULL is never equal to NULL.

Index `ix_report_pins_user_ledger (user_id, ledger_id, sort_order)` — "what is in my rail for this ledger", which is the only read and is ordered.

**RLS** `report_pins_per_user` — `FOR ALL`, and both halves identical: your own rows, in ledgers you are granted. Unlike `saved_reports` there is no shared pin, so there is nothing a member may read but not write.

**Per user, not per ledger.** A pin says "I come back to this", which is not a property of the report and not something its author decides for everyone. That creates a deliberate asymmetry with `saved_reports.visibility`: a ledger-shared report can be pinned by someone who cannot edit it, and unpinning is theirs alone. Un-sharing a report drops it from other people's rails *without* deleting their pin row — `ReportPinsRepository.ListAsync` joins through RLS, so a report the reader can no longer see yields no row, and the rail never shows an entry that opens nothing.

**Captured** by `fn_ledger_snapshot_payload` / `_part_names` / `_insert_order` (after `saved_reports`, which it references), and cleared by `fn_ledger_snapshot_clear` and `fn_ledger_delete`. Captured rather than excluded following `saved_reports` and `user_account_groups` (the sidebar tabs, since dropped in migration 258): restoring a ledger to find every report present but nobody's rail intact is a loss the schema never admitted to.

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
| `external_id` | `TEXT` | per-ledger UNIQUE `(ledger_id, external_id) WHERE external_id IS NOT NULL` | Source-system identifier — for Moneydance imports this is the raw MD UUID. NULL for securities created by other paths. Set once at insert; the Moneydance import's dedup key within its one seeding run. Added in migration 008; the original global unique was narrowed to per-ledger `uq_securities_external_id_per_ledger` in migration 014. |
| `share_decimals` | `INTEGER` | NOT NULL DEFAULT 4, CHECK (`share_decimals BETWEEN 0 AND 12`) | Per-security precision for share quantities (Moneydance's `dec` field); the investment mapper looks this up to scale raw share-quantity integers. Added in migration 012 with a `[0,6]` ceiling; **widened to `[0,12]` in migration 050** to match the `NUMERIC(25,12)` scale of quantity/price columns — a `[0,6]` ceiling clamps an out-of-range `dec` (9, say) back to the default 4, which scales quantity and price by the lost decimals (five orders of magnitude for `dec=9`) on every leg. `holdings.quantity` was bumped from `NUMERIC(19,6)` to `NUMERIC(25,12)` in migration 043. |
| `quote_symbol` | `TEXT` | | Symbol sent to the market-data quote provider when it differs from the display `ticker` (mutual funds, international suffixes). Falls back to `ticker` when NULL. Migration 131 (ADR-0054 D2). |
| `auto_price` | `BOOLEAN` | NOT NULL DEFAULT TRUE | When FALSE, excludes the security from automated price fetches (manual-only, or a hand-pinned stable-NAV fund) without nulling its ticker. Migration 131 (ADR-0054 D2). |
| `quote_symbol_public` | `BOOLEAN` | NOT NULL DEFAULT TRUE, CHECK (`quote_symbol_public OR quote_symbol IS NOT NULL`) | When FALSE, `quote_symbol` is a private / feed-only identifier (e.g. a 529 portfolio number): matched by the no-egress SimpleFIN provider but never sent to an external (egress) provider. The CHECK codifies that a bare ticker is always public — non-public requires a `quote_symbol`. Migration 156 (ADR-0054 D2). |
| `quote_currency_confirmed_at` | `TIMESTAMPTZ` | | When a Yahoo read last named this security's currency (ADR-0114 D5). The quote refresh sets it on every read that names one, and in the same transaction re-stamps the currency of the security's `security_prices` rows with `source = 'fetch'` that carry another. For a symbol quoted in its major unit only the currency changes. For one quoted in a minor unit (`GBp`), those rows hold the minor unit's figures, so price, high and low are divided by its factor and rounded once to 4dp, as a new fetch is stored; volume is kept, and a row already in the major unit is not touched. No other source's row is touched. NULL until a read names it: the refresh then asks Yahoo once for the currency of each such security that has fetched prices, held or not, and the consistency check's `quote_currency` projection reports it. Existing rows took NULL; snapshots carry the column with the rest of the row, and one taken before migration 300 restores it as NULL. Migration 300. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `holdings`

One row per (account, security) a write has touched: `recompute_holdings_cost_basis`,
given a pair with no row, creates one, and nothing removes it when the position closes,
so a closed position stays at quantity 0. The consistency check's holdings comparison
relies on both ([operations.md](operations.md), "The check and the repair").

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
| `unit_cost` | `NUMERIC(25,12)` | NOT NULL CHECK (`unit_cost >= 0`) | Per-share acquisition price for the lot. Migration 056: refreshed by `recompute_holdings_cost_basis()` on every call — `(leg.amount + fee_total) / quantity` when the brokerage's `is_trade_commission=TRUE` (fee_total = same-header `posting_role='fee'` amounts), else `leg.amount / quantity`. So flipping a brokerage's flag propagates to per-lot prices on the next function call, not just to `holdings.cost_basis`. Non-negative CHECK added in migration 051. Migration 053 rebuilds every lot from `txn_legs`, so no lot keeps a stored value that does not follow from its legs. **Migration 180** widened this from `NUMERIC(19,4)` to `NUMERIC(25,12)` (matching the mig-043 precision family) — at 4dp, `quantity × unit_cost` drifts up to `quantity × 5e-5` per lot from the true basis, which accumulates across a fund's reinvestment lots into whole currency units on a large in-kind transfer. |
| `acquired_at` | `TIMESTAMPTZ` | NOT NULL | Critical for short vs long-term; also the FIFO closure ordering key (migration 054). For an **in-kind transfer-in lot** (`transfer_shares`, ADR-0065) this is the ORIGINAL acquisition date, carried from the source lot — so holding period survives the move. (Availability for consumption is gated separately by the creating leg's header `posted_at`; mig 152.) |
| `is_closed` | `BOOLEAN` | NOT NULL DEFAULT FALSE | TRUE when `quantity` has been drained to zero by FIFO Sell closures (migration 054). |

**Index** `idx_lots_leg_id`: `lots(leg_id)` — serves the `leg_id` foreign key's
`ON DELETE CASCADE`: deleting a leg finds its lots by `leg_id`.

### `realized_gains`

Per-disposal realized gain/loss under FIFO (ADR-0064, migration 148). One row per
sell leg; **owned by** `recompute_holdings_cost_basis()`, which deletes +
repopulates the rows in its `(account, security)` scope each run. Since migration 275
it takes the ledger write lock of the account's ledger, or of the ledger named, before it
creates a position's `holdings` row or reads a walk (`fn_ledger_write_lock`; see
`txn_header_account_balances` and [ADR-0112](decisions/0112-one-write-lock-per-ledger.md)):
nothing in a transaction that took it first, the statement's own for the holdings and
realized-gains repairs, which recompute one position per statement, and refused in a
transaction that has written or row-locked without it. A call naming neither a ledger nor
an account takes each holding's ledger in turn, which outside a migration's session is
refused at the second ledger. Two writers of one ledger run one after the other and the
later reads what the earlier committed, so two runs over one position never both insert:
the later would otherwise fail on `uq_realized_gains_sell_leg` (or, for a position's first
trade, on the `holdings` unique key) with the rows the earlier one committed. A
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
| `proceeds` | `numeric(20,3)` | NOT NULL | `−leg.amount`, net of a sell-side fee when the brokerage folds fees. **Scaled by migration 182** — unconstrained `NUMERIC` here can hold values that overflow .NET `decimal` on read once `lots.unit_cost` has 12 decimals (migration 180), and an overflow fails the `realized_gains` MCP tool and its report outright. The sibling money columns (`proceeds_lt`, `cost_basis_sold`, `cost_basis_sold_lt`, `realized_gain`, `realized_gain_lt`) carry the same scale; `quantity` is `numeric(25,12)`. Migration 182 set all six to `numeric(19,2)`; **migration 301 widens them to `numeric(20,3)`**, 17 integer digits as before, so a 3-place currency's figure fits ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). The same migration redefines `recompute_holdings_cost_basis` (migration 275's) and `realized_gains_walk` (migration 217's) to round each figure once at the minor units of the account's currency, read through the SQL minor-units function, with identical rounding in both (settled by Claude, 2026-11-16). Existing rows are derived data and the migration does not re-derive them: the consistency check's `realized_gains` projection reports a row not at its account's places, and its existing repair re-derives it (settled by Claude, 2026-11-16). |
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
| `price` | `NUMERIC(19,4)` | NOT NULL | Closing price (Moneydance's `urt`/`rate`, inverted: MD stores rates as 1/price for securities). Was bumped to `NUMERIC(25,12)` in migration 043, then **constrained back to `NUMERIC(19,4)` in migration 155** (ADR-0070 D8) to match `high`/`low` and scrub 32-bit-float representation noise (e.g. a two-decimal close stored with a run of noise digits after it); 4dp is ample for valuation and the DB now enforces it. (The trade price `txn_legs.unit_price` stays `NUMERIC(25,12)` — a per-share execution price legitimately needs >4dp.) |
| `currency_code` | `TEXT` | NOT NULL DEFAULT `'USD'` | The currency `price` is in. Since **migration 281** every valuation reads it: a position priced in another currency than its account's converts into the account's at the day's rate, in the API (ADR-0114 D7), and nothing stored converts. A `fetch` row's is the currency Yahoo names for the symbol, in its major unit (a minor-unit quote such as `GBp` is divided by its factor before it is stored), and fetch rows from before that fix were stamped `USD` until a refresh confirms the security's currency (`securities.quote_currency_confirmed_at`, migration 300); a `manual` row's is the one typed beside the price; a `simplefin` row's is its account's; an `import` row's is the one the Moneydance import names, and rows imported before ADR-0114 keep `USD` (ADR-0114 D5). A `trade` row's is the currency of the brokerage that made the day's picked trade (**migration 274**, ADR-0084 D2, 2026-11-04 amendment); before 274 every trade-derived row was written `USD`, and the trade-price check reports one in another currency than its brokerage's. |
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
| `external_id` | `TEXT` | UNIQUE WHERE NOT NULL | Source-system id; the Moneydance import's dedup key within its one seeding run. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `security_components`

Multi-asset look-through sleeves (ADR-0067, migration 150). One row per
`(security, asset_class × optional region)` sleeve; allocation decomposes a
`securities.asset_class = 'multi_asset'` wrapper through these instead of counting
100% in one bucket (migration 153 made `asset_class = 'multi_asset'` the single
look-through signal, retiring the separate `needs_look_through` flag). Populated
manually today (editor, gated on multi-asset); no provider feed yet. RLS: the
migration 174 role-aware pair on `ledger_id` (`security_components_read` for any
grant, `security_components_write` for owner/editor) since migration 247, which
replaced the `security_id IN (SELECT id FROM securities)` sub-select.

**Captured in snapshots since migration 247**, which added `ledger_id`. Capture scopes
every table with `WHERE ledger_id = $1`, so a table without that column cannot be
captured; a restore deletes `securities`, the CASCADE empties this table, and an
uncaptured table has nothing to put the rows back. The completeness guard recognises a
ledger-scoped table by its foreign keys as well as by that column, so a table reachable
only through a foreign key is classified too.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `security_id` | `UUID` | NOT NULL; composite FK `security_components_security_id_fkey` `(security_id, ledger_id)` → `securities(id, ledger_id)` ON DELETE CASCADE | Composite since migration 247, the migration 049 pattern: it is what makes `ledger_id` provably the security's ledger. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT; index `ix_security_components_ledger` | Migration 247, backfilled from `securities`. What snapshot capture and RLS key on. |
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

### The outside market series: `ledger_benchmarks`, `benchmark_growth`, `benchmark_coverage`, `fx_rates`, `fx_pair_coverage`

Five tables (**migration 280**, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D6) hold the per-day series no ledger table holds: the total-return growth of the funds a ledger compares its Returns report with, and the exchange rates a figure that combines accounts in different currencies converts at. `security_prices` cannot hold either: its composite foreign key ties every row to one held security, and its 4dp scale is too coarse for a rate. Storage is per ledger, as prices are: two ledgers that use the same series each fetch and store it.

What the five have in common:

- **Anchor.** `ledger_id` NOT NULL FK → `ledgers(id)` ON DELETE CASCADE, the anchor `budget_targets` and `account_folders` use. `fn_ledger_delete` deletes the ledger row last and the cascade takes these with it. None references another ledger table, so no composite foreign key applies: `benchmark_growth` and `benchmark_coverage` have no reference to `ledger_benchmarks` on purpose, because a removed benchmark keeps its history and the record of what was asked for it, and adding it back needs no fetch for a range already asked (ADR-0114 D1).
- **One value per day, first value wins, never rewritten** (ADR-0114 D6). Every value is one per (symbol or pair, UTC day), stored as a `DATE` (ADR-0070). The unique key of each series table is the day, whatever the source, so a fetch for a day that holds a hand-entered rate, and a hand entry for a day that holds a fetched one, are both refused by it. `coffer_app` holds only `SELECT` and `INSERT` on the two series tables, so a request cannot rewrite or remove a stored day either; the rows go only through a snapshot restore or a ledger delete, which run as the service role.
- **Writers.** `MarketSeriesFetcher` writes `benchmark_growth`, `benchmark_coverage`, the fetched `fx_rates` and `fx_pair_coverage`, riding the quote refresh's run-all path (the manual Refresh as the requesting user, the scheduled job as the schedule's user) and the standalone backfill (`MarketSeriesBackfill`, service role, as the user who started it), never a feed sync (ADR-0054 D1). Each destination needs that user's own opt-in (ADR-0114 D8): `yahoo` for benchmarks, `frankfurter` for rates. It fetches outside any transaction, then writes in one transaction under the ledger write lock (migration 275), reading the days already stored and inserting only the rest, so two runs at once take turns and neither meets the unique key; a coverage range only widens (below). `MarketSeriesRepository` writes `ledger_benchmarks` (add, remove and close up the positions) and the hand-entered `fx_rates`, each under the same lock. Which pairs a ledger needs, and from which day, is `NeededFxPairs.ForLedgerAsync`'s one rule.
- **RLS** the role-aware pair, as since migrations 247 and 254: `<table>_read` (SELECT, any grant) and `<table>_write` (ALL, `role IN ('owner','editor')`, in USING and WITH CHECK). Grants: `ledger_benchmarks` SELECT, INSERT, UPDATE, DELETE (add, reorder, remove); `fx_pair_coverage` and `benchmark_coverage` SELECT, INSERT, UPDATE (a range widens, never goes); `benchmark_growth` and `fx_rates` SELECT, INSERT.
- **Captured in snapshots** (ADR-0114 D6), inserted after the other captured tables (they reference only the ledger and users) and cleared by `fn_ledger_snapshot_clear`. A rollback restores the snapshot's rows and drops the days after it, coverage included, so the next refresh fetches those days again; an upstream revision can therefore differ only across a rollback. `fx_rates.entered_by_user_id` is a reference out of the snapshot to `users`, classified in `SchemaDriftGuardTests` with the other user references: no app path deletes a user.
- **The audit** of a fetch is the quote refresh's own `ledger_operations` row, family `quote`; the family CHECK does not change (ADR-0114 D11).

#### `ledger_benchmarks`

Up to three fund or ETF symbols a ledger's Returns report can be compared with (ADR-0114 D1, D2), managed on Settings → Quotes by owners and editors.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_ledger_benchmarks`, DEFAULT `gen_random_uuid()` | |
| `ledger_id` | `UUID` | NOT NULL FK `fk_ledger_benchmarks_ledger` → `ledgers(id)` ON DELETE CASCADE | |
| `symbol` | `TEXT` | NOT NULL CHECK `ck_ledger_benchmarks_symbol_not_blank`; UNIQUE `uq_ledger_benchmarks_symbol (ledger_id, symbol)` | The Yahoo symbol, as the add-time check resolved it: a fund or ETF whose chart response carries an adjusted close. An index symbol is refused at add time (ADR-0114 D2). |
| `name` | `TEXT` | NOT NULL CHECK `ck_ledger_benchmarks_name_not_blank` | What the Returns footnote names (ADR-0114 D3). |
| `currency_code` | `TEXT` | NOT NULL | The currency its closes are quoted in, in the major unit (ADR-0114 D5); a line drawn against a series in another currency converts at each day's rate (ADR-0114 D3). No CHECK, as `accounts.currency_code`. |
| `position` | `INTEGER` | NOT NULL CHECK `ck_ledger_benchmarks_position` (`BETWEEN 1 AND 3`); UNIQUE `uq_ledger_benchmarks_position (ledger_id, position)` DEFERRABLE INITIALLY DEFERRED | The order the Benchmark control lists it in. Deferred so a reorder can swap two positions inside one transaction. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

**The cap of three is the API's** (ADR-0032: no trigger), checked before the insert with a business error. The position CHECK and the position key together admit at most three rows per ledger, so the database is the backstop under it. Removing a symbol deletes this row only; its `benchmark_growth` and `benchmark_coverage` rows stay.

#### `benchmark_growth`

One growth factor per (ledger, symbol, trading day): that day's adjusted close over the previous trading day's, both taken from the same Yahoo response (ADR-0114 D2). Yahoo rescales a fund's whole adjusted-close history at every new dividend or split, so two closes fetched at different times sit on different bases; their ratio from one response does not change under a later rescale. A forward fetch starts at the last stored day, so the first new day has its previous close in the same response; what has been asked is recorded in `benchmark_coverage`. Fetched only: a growth factor is never typed (ADR-0114 D10).

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `ledger_id` | `UUID` | NOT NULL; PK part 1; FK `fk_benchmark_growth_ledger` → `ledgers(id)` ON DELETE CASCADE | |
| `symbol` | `TEXT` | NOT NULL; PK part 2; CHECK `ck_benchmark_growth_symbol_not_blank` | The `ledger_benchmarks.symbol` it was fetched for. No foreign key: the rows outlive the benchmark (above). |
| `growth_date` | `DATE` | NOT NULL; PK part 3 | The UTC trading day. |
| `growth` | `NUMERIC(20,12)` | NOT NULL CHECK `ck_benchmark_growth_positive` (`> 0`) | Rounded once, at write, to the column's scale. |
| `source` | `TEXT` | NOT NULL CHECK `ck_benchmark_growth_source` in (`fetch`) | |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

The primary key `pk_benchmark_growth (ledger_id, symbol, growth_date)` is the identity, the one-value-per-day rule, the index the chaining read scans (a symbol's days in a range, forward), and what the snapshot writer chunks on.

#### `fx_rates`

One exchange rate per (ledger, from currency, to currency, UTC day) (ADR-0114 D4, D6, D10). A rate is fetched with the account's currency as `from_currency` and the currency it converts into as `to_currency`, so no stored rate is the inverse of another. A day the feed skips (a weekend, a holiday) converts at the last rate on or before it.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_fx_rates`, DEFAULT `gen_random_uuid()` | |
| `ledger_id` | `UUID` | NOT NULL FK `fk_fx_rates_ledger` → `ledgers(id)` ON DELETE CASCADE | |
| `from_currency` | `TEXT` | NOT NULL; CHECK `ck_fx_rates_distinct_currencies` (`from_currency <> to_currency`) | One unit of it is worth `rate` units of `to_currency`. |
| `to_currency` | `TEXT` | NOT NULL | |
| `rate_date` | `DATE` | NOT NULL | The UTC day the rate is for. |
| `rate` | `NUMERIC(28,12)` | NOT NULL CHECK `ck_fx_rates_rate_positive` (`> 0`) | Rounded once, at write, to the column's scale. 28 significant digits is within .NET `decimal`. |
| `source` | `TEXT` | NOT NULL CHECK `ck_fx_rates_source` in (`frankfurter`, `manual`) | `frankfurter`: fetched from the configured Frankfurter feed (ADR-0114 D4). `manual`: typed on the rates view for a day with no stored rate (ADR-0114 D10). |
| `upstream` | `TEXT` | CHECK `ck_fx_rates_upstream_only_on_fetched` (`upstream IS NULL OR source = 'frankfurter'`) | The source Frankfurter names for a fetched rate, as the response names it; NULL on a manual rate. |
| `entered_by_user_id` | `UUID` | FK `fk_fx_rates_entered_by_user` → `users(id)` ON DELETE SET NULL; CHECK `ck_fx_rates_user_only_on_manual` (`entered_by_user_id IS NULL OR source = 'manual'`) | Who typed a manual rate. See the attribution note below. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Unique `uq_fx_rates_pair_day (ledger_id, from_currency, to_currency, rate_date)`: one rate per day whatever its source, so a hand entry can be made only for a day with no stored rate, and a fetch skips a day a hand entry holds (first value wins). **It is also the index of the hot read**, the last rate on or before a day for a pair: equality on the first three columns and `rate_date <= d`, newest first, one row. A btree is scanned in either direction, so that read is a backward scan of this index that stops at the first row; an index declared `rate_date DESC` would hold the same entries in the other order and serve the same plan, so there is none. The coverage read (ADR-0114 D9: the pair's latest hand-entered day) is the same backward scan, filtered on `source`.

**The attribution of a hand-entered rate.** `entered_by_user_id` is ON DELETE SET NULL, so a rate outlives its author. Its CHECK is therefore one-way: only a manual rate may name a user. The converse, that a manual rate names one, cannot be a CHECK: the SET NULL a user delete runs would violate it, and the delete would be refused with a check violation instead, which is RESTRICT under another name. A manual rate whose author is gone stays `manual`, so it is still attributable to hand entry. That a manual rate is written naming its author is the API writer's rule. No app path deletes a user, so only an operator's SQL reaches the SET NULL.

#### The coverage tables: `fx_pair_coverage` and `benchmark_coverage`

Per pair, and per benchmark symbol, the range of days fetches have **asked** the source for: `[fetched_from_date, fetched_through_date]`, whatever came back. A span before a currency existed or a fund launched returns nothing, and a range judged by the rows stored would look unfetched and be asked on every refresh; recorded as asked, it is asked once. Each refresh asks only what lies outside the range: forward past the through day (a pair from the through day inclusive, so a rate published for it later is read; a benchmark from its last stored growth day, whose close is the first new day's base), and back over `[needed day − reach-back, fetched_from_date)` when the earliest day a figure needs has moved before the from day (an older transaction or price in a covered currency, or for a benchmark an earlier first brokerage funding, the earliest day a returns series can start from, ADR-0114 D11; a benchmark's back range runs through its first stored growth day, so the day that was the first fetch's base gets its growth). A range is contiguous and only widens: by the span a forward fetch read through, or by a back span read in full; a back fetch that stopped part-way records nothing and is asked again. The write merges each range with the stored one (the earlier from day, the later through day) under the ledger write lock.

`benchmark_coverage` is keyed by symbol with no reference to `ledger_benchmarks`, so it is kept when a benchmark is removed, as its growth rows are: adding it back asks nothing already asked (ADR-0114 D1).

For a pair, a rate is missing for a day after its coverage, whatever `fx_rates` holds (ADR-0114 D9): coverage runs to `fetched_through_date`, or to the pair's latest hand-entered day if that is later. A pair with no row has no fetched coverage.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `ledger_id` | `UUID` | NOT NULL; PK part 1; FK `fk_fx_pair_coverage_ledger` / `fk_benchmark_coverage_ledger` → `ledgers(id)` ON DELETE CASCADE | |
| `from_currency`, `to_currency` (`fx_pair_coverage`) | `TEXT` | NOT NULL; PK parts 2 and 3; CHECK `ck_fx_pair_coverage_distinct_currencies` (`from_currency <> to_currency`) | |
| `symbol` (`benchmark_coverage`) | `TEXT` | NOT NULL; PK part 2; CHECK `ck_benchmark_coverage_symbol_not_blank` | The `benchmark_growth.symbol` the range was asked for. |
| `fetched_from_date` | `DATE` | NOT NULL; CHECK `ck_fx_pair_coverage_range` / `ck_benchmark_coverage_range` (`fetched_from_date <= fetched_through_date`) | The first UTC day fetches have asked for. |
| `fetched_through_date` | `DATE` | NOT NULL | The last UTC day fetches have asked for; never after today, since neither client asks for a later day. |

### `ledger_operations`

One recorded operation on a ledger — a feed sync / file import, a Moneydance bootstrap import, a Demo's examples phase, a quote refresh, or a snapshot restore. Named `sync_runs` originally (mig 038, SimpleFIN only), generalized to `provider_runs` in mig 132 (ADR-0055; file imports + quote refresh), then renamed to `ledger_operations` in **mig 185** (ADR-0086) when the Moneydance import and snapshot restore — which are not "provider" runs — joined the same per-ledger timeline. Feed syncs, file imports and quote refreshes are written two-phase (a `running` row at the start, updated to a terminal state at the end), and so is a Demo's examples row (`demo`): opened `running` inside the Demo's import transaction, then claimed and closed by the examples phase, or closed at start by `DemoMarkerSweep` after a restart (ADR-0115 D3, D4); the one-shot ops (Moneydance import, snapshot restore) write a single already-terminal row via `LedgerOperationsRepository.RecordTerminalAsync`. Backs the ledger Activity timeline (`settings/ActivityPanel.tsx`), whose rows open one run's errors and promotions; the Bank feeds panel no longer lists runs of its own (ADR-0055 D5 amendment). Per-run metrics live in a single `details` JSONB blob (replaced the typed `txns_*` counters in mig 132).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | RLS anchor — one-hop via `user_ledger_grants`. Survives `feed_connection_id` being nulled by a disconnect. |
| `feed_connection_id` | `UUID` | FK → `feed_connections(id)` ON DELETE SET NULL | NULL for runs not tied to a connection (file imports, quote refresh) or after a disconnect. |
| `family` | `TEXT` | NOT NULL CHECK in (`ingest`, `quote`, `snapshot`) | Operation family — `ingest` (feed/file/Moneydance import, and a Demo's examples row), `quote` (price refresh), or `snapshot` (restore). `snapshot` added in mig 185 (ADR-0086); `ingest`/`quote` from mig 132/135 (ADR-0055). |
| `provider_key` | `TEXT` | NOT NULL | Concrete operation within the family — `simplefin` / `ofx` / `qif` / `csv-generic` / `csv-fidelity` / `moneydance` / `simplefin-holdings` (ingest; a file import is recorded under its reader's key, and `file` is the key migration 132 gave the file runs recorded before then, which Activity lists as File import), `demo` (ingest: a Demo's examples phase, opened `running` inside the Demo's import and closed by the seeder; not an import, so undo-import refuses it, ADR-0115 D3), `quote-refresh` or `market-series-backfill` (quote: a refresh, or the standalone benchmark and exchange-rate backfill ADR-0114 D11 starts), or `snapshot-restore` (snapshot). NOT NULL (mig 132; backfilled for existing ingest rows). |
| `triggered_via` | `TEXT` | NOT NULL CHECK in (`manual`, `file-upload`, `post-sync`, `scheduled`) | How the operation started (mig 132). `file-upload` is a user's file or Moneydance upload; `manual` covers a snapshot restore and the Demo's Moneydance import, its examples (`demo`) and its card statement import (`csv-generic`), none of which was uploaded (ADR-0115 D3). Refines the "who" (`triggered_by_user_id`) with "how" (ADR-0054 D4 / ADR-0055). |
| `triggered_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who triggered it. For `scheduled` runs, the user whose schedule/prefs drove it. |
| `status` | `TEXT` | NOT NULL DEFAULT `'running'` CHECK in (`running`, `completed`, `partial`, `failed`, `needs_reauth`) | Terminal state written in the closing UPDATE. `partial` = 2xx with non-empty errlist (per-institution `con.auth` etc. recorded in `ledger_operation_errors`); `needs_reauth` = 403. A partial UNIQUE index keeps one `running` row per connection (concurrent runs → 422 `feed-sync-in-progress`); a lazy reaper sweeps a connection's stale `running` rows to `failed` before its next sync; a file import's run has no connection, so the import closes it itself on every way out but success. A `demo` row closes `completed` (every step added), `partial` (some failed or skipped, each named with its reason in `error_message`) or `failed` (nothing could be added, or the import reported problems); one a restart left `running` is closed by `DemoMarkerSweep` at boot: `partial`, naming the steps it lacks, or `completed` when its details already record every step. |
| `details` | `JSONB` | NOT NULL DEFAULT `'{}'::jsonb` | Per-operation metrics (replaced the typed `txns_*` counters in mig 132). Feed/file ingest: `{txns_fetched, txns_inserted, txns_promoted, txns_already_known, txns_still_pending, txns_skipped}`. Quote: `{prices_inserted, prices_updated, …, rates_inserted, benchmark_days_inserted}`, the last two the exchange-rate and benchmark-growth days the run stored (ADR-0114 D11; a backfill's row carries only these as non-zero), with one `ledger_operation_errors` row per failing symbol or pair. Moneydance import: `{duration_seconds, <step_name>: <written>, …}`. Snapshot restore: `{snapshot_id}`. A Demo's examples (`demo`): one key per examples step, `1` added and `0` not, written after each step (ADR-0115 D3). |
| `error_message` | `TEXT` | | Non-null on `status='failed'` — the `SimpleFinException` message, the access-URL decrypt error, the stale-run reaper's `Interrupted — process exited before sync completed.`, or `IngestOrchestrator`'s words for a run whose save did not commit: the busy refusal (`LedgerWriteLock.BusyDetail`), a save that failed (`SaveFailedDetail` on a sync, `ImportSaveFailedDetail` on a file import) or a cancellation, by the server stopping or the caller giving up (`CancelledDetail`, `ImportCancelledDetail`). On a `demo` row, also on `partial`: each step not added and why, separated by `; `. |
| `started_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | A `demo` row is opened at the import transaction's time and restamped when the examples phase claims it, so it lists after the import it follows (ADR-0115 D3). |
| `completed_at` | `TIMESTAMPTZ` | | NULL while `status='running'`. |

**RLS** the role-aware pair since migration 254 (FU-086), on this table and on `ledger_operation_errors` and `ledger_operation_promotions`: `_read` (SELECT, any grant) and `_write` (ALL, `role IN ('owner','editor')`). Manual syncs, file imports and quote refreshes write here as the requesting user, behind the API's write gate; scheduled runs, the Moneydance import, a Demo's examples row (opened by its import, then written by `DemoSeeder` and `DemoMarkerSweep`) and snapshot restores write as the service role. Because the requesting-user paths write as `coffer_app`, an any-grant `FOR ALL` policy (migration 038's, carried through the rename and kept by 174) lets a viewer write here; 254 replaced it with the pair.

**Retention.** `AuditRetentionService` deletes runs whose `started_at` is older than `Api:AuditRetentionDays` (default 180), once a day (see [operations.md, Audit retention](operations.md#audit-retention)). The delete cascades to `ledger_operation_errors` and `ledger_operation_promotions` and sets `txn_headers.ledger_operation_id` to NULL, so undo-import expires with the window. This table is not in a snapshot. A restore clears the stamp on restored headers and deletes restored promotions whose run is gone (`fn_ledger_snapshot_reconcile_external_refs`, migration 302); `ledger_operation_errors` is not captured either, so the restore has nothing of it to reconcile. See the `txn_headers.ledger_operation_id` row and the [`ledger_operation_promotions`](#ledger_operation_promotions) section.

### `ledger_operation_errors`

One row per provider `errlist[]` entry captured during a run (`sync_run_errors` → `provider_run_errors` in mig 132 → `ledger_operation_errors` in mig 185). Feed syncs and quote refreshes populate this (a quote refresh and a market-series backfill write one row per failing security, benchmark symbol or exchange-rate pair, with the fetch's own code, ADR-0114 D11); the one-shot import/restore ops record no per-error children. The SPA expands a run to show these — e.g. a multi-institution SimpleFIN connection where one institution returns `con.auth` ("Connection to … may need attention") surfaces here as a `partial` run with these rows.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_operation_id` | `UUID` | NOT NULL FK → `ledger_operations(id)` ON DELETE CASCADE | (FK column renamed from `sync_run_id`.) |
| `code` | `TEXT` | NOT NULL | Structured `prefix.subcode` from the provider (e.g. `con.auth`, `auth.revoked`, `fi.maintenance`). |
| `message` | `TEXT` | NOT NULL | Human message verbatim from the bank/provider. |
| `simplefin_connection_id` | `TEXT` | | Optional SimpleFIN-level scope id (the per-institution `MBR-…`). |
| `simplefin_account_id` | `TEXT` | | Optional SimpleFIN-level scope id: the feed's id for the account the error is about. A sync's errors carry it when SimpleFIN scoped the error to an account; a SimpleFIN holdings error from a quote run always does (ADR-0114 D5). The run-detail read resolves a label from it: the mapped Coffer account's name, else the bank-feed directory's "name · institution" (ADR-0055 D5, amended 2026-11-20). |
| `provider_key` | `TEXT` | | The source that raised the error, on a quote run's rows: `simplefin-holdings`, `yahoo` or `market-series` (migration 303). NULL on a sync's errors, whose run names its one source, and on rows written before 303. The Activity log groups an opened run's errors by it. |
| `ledger_id` | `UUID` | NOT NULL; part of the composite FK `(ledger_operation_id, ledger_id)` → `ledger_operations(id, ledger_id)` ON DELETE CASCADE | Denormalized for RLS/query (migration 072; FK column renamed in mig 132). It is **not** a direct FK to `ledgers(id)` — coherence is enforced by the composite FK to the parent run. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `ledger_operation_promotions`

One row per promote-on-clear event (`sync_run_promotions` → `provider_run_promotions` in mig 132 → `ledger_operation_promotions` in mig 185): the bank cleared a previously-pending FITID at a different amount than the original hold (tip, FX shift). Backs the per-run "Cleared at different amounts" detail.

**Captured in snapshots since migration 247** (after `txn_headers`, which it references), and cleared by `fn_ledger_snapshot_clear`. Excluding it as provenance that survives a rollback does not work: through the `header_id` CASCADE, a restore, which deletes `txn_headers`, takes every promotion in the ledger with it, so each past sync run reads "0 promoted" and the pre-promotion amount, recorded nowhere else, is lost. Captured rather than preserved across the restore (2026-10-09): a promotion describes a transaction, so it rolls back with one, and a promotion recorded after the snapshot goes with the restore. The parent run in `ledger_operations` stays outside the snapshot, and `AuditRetentionService` deletes runs past its window, which deletes their promotions (CASCADE). A snapshot taken before that prune still holds them, so the restore deletes a restored promotion whose run has gone, the same `CASCADE` the prune applied to the live row, before the deferred checks on both FKs to the run fire (`fn_ledger_snapshot_reconcile_external_refs`, migration 302). The restore's row-count check expects the snapshot's promotions less the ones it deleted. The guard lists both FKs as reconciled on restore (`OutwardReconciledOnRestore`).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_operation_id` | `UUID` | NOT NULL FK `ledger_operation_promotions_ledger_operation_id_fkey` → `ledger_operations(id)` ON DELETE CASCADE, DEFERRABLE INITIALLY DEFERRED (migration 302) | (Renamed from `sync_run_id`.) Deferred so a snapshot restore can delete a restored promotion whose run has gone before the check fires. |
| `header_id` | `UUID` | NOT NULL FK → `txn_headers(id)` ON DELETE CASCADE | Audit follows the row. |
| `was_amount` | `NUMERIC(19,4)` | NOT NULL | Pre-promotion source-side leg amount; on a split, its total on the synced account. |
| `became_amount` | `NUMERIC(19,4)` | NOT NULL | Post-promotion source-side leg amount; on a split, which no promotion rewrites, the same total as `was_amount` (ADR-0114 D15). |
| `ledger_id` | `UUID` | NOT NULL; part of the composite FK `ledger_operation_promotions_run_ledger_fkey` `(ledger_operation_id, ledger_id)` → `ledger_operations(id, ledger_id)` ON DELETE CASCADE, DEFERRABLE INITIALLY DEFERRED (migration 302) | Denormalized for RLS/query (migration 072; FK column renamed in mig 132). It is **not** a direct FK to `ledgers(id)` — coherence is enforced by the composite FK to the parent run. |
| `promoted_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `recurring_transactions`

Templates for scheduled/recurring transactions. Originates from Moneydance "reminders" on import.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL | Denormalized (migration 072) so RLS gates on `ledger_id` directly; anchors the composite FKs below. Added `UNIQUE (id, ledger_id)` (`uq_recurring_transactions_id_ledger`, mig 124) so `txn_headers.recurring_transaction_id` can compose a ledger-scoped FK. |
| `source_account_id` | `UUID` | NULL; composite FK `(source_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | Display/query pointer that drives the reminders agenda amount. **Dropped in migration 124, then re-added NULLABLE in 125/126** (`recurring_transactions_source_account_fkey`) — the template legs pin the real account, so this is a convenience pointer, not the source of truth. Set when the reminder is created; a reminder edit cannot change it (an edit naming another source is refused with `reminder-source-immutable`). |
| `loan_account_id` | `UUID` | NULL; composite FK `(loan_account_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE RESTRICT | For a managed loan-payment reminder (`is_loan_reminder`), the loan account whose principal/interest/escrow split is computed from `loan_terms` + the loan's balance as of each payment's own date (migration 168, ADR-0050 extension; ADR-0078). Partial unique index `uq_recurring_loan_account (loan_account_id) WHERE loan_account_id IS NOT NULL` — at most one managed reminder per loan account. |
| `start_date` | `DATE` | NOT NULL | |
| `end_date` | `DATE` | | |
| `next_due_date` | `DATE` | | Updated as instances are acknowledged |
| `last_acknowledged_date` | `DATE` | | |
| `is_loan_reminder` | `BOOLEAN` | NOT NULL DEFAULT FALSE | |
| `is_active` | `BOOLEAN` | NOT NULL DEFAULT TRUE | |
| `origin` | `TEXT` | NOT NULL DEFAULT `'manual'` CHECK in (`manual`, `moneydance_import`) | |
| `external_id` | `TEXT` | per-ledger UNIQUE `(ledger_id, external_id) WHERE external_id IS NOT NULL` | Source-system identifier — for Moneydance imports this is the raw MD reminder UUID. NULL for reminders created manually. Set once at insert; the Moneydance import's dedup key within its one seeding run. Added in migration 013; re-scoped to per-ledger (`uq_recurring_external_id`) in migration 162. |
| `rrule` | `TEXT` | | RFC-5545 recurrence rule (migration 124, ADR-0047). The **authoritative** schedule, expanded by the C# `RecurrenceExpander`. It replaced the discrete `frequency`/`monthly_day`/`weekly_dow`/`interval_units` columns, which migration 124 **dropped** (their shape now lives on the template header + legs + this rrule). NULL on a row migration 124 reshaped without converting it (no template header either); such a row stays dormant, since no import path re-materializes it. |
| `source_payload` | `JSONB` | | Raw MD reminder payload retained for audit, lossless (mig 124). |
| `auto_commit_days_before` | `INTEGER` | | If set, an occurrence auto-fires this many days before its due date (mig 124, ADR-0047); NULL = manual confirm only. |
| `template_header_id` | `UUID` | composite FK `(template_header_id, ledger_id)` → `txn_headers(id, ledger_id)` ON DELETE RESTRICT, DEFERRABLE INITIALLY DEFERRED | The template `txn_headers` row this series fires from (mig 124, ADR-0048 — templates live in `txn_headers` with `is_recurring_template=true`, surfaced via the `template_txn_headers` view). DEFERRABLE because it and `txn_headers.recurring_transaction_id` reference each other (resolved at commit during snapshot restore). |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `estimate_sample_count` | `INTEGER` | CHECK > 0 when set (`ck_recurring_transactions_estimate_sample_count`) | Migration 220: how many prior occurrences an ESTIMATED reminder averages to produce its amount. NULL on a fixed-amount series. Partial index `idx_recurring_transactions_estimated` backs the auto-post job's scan; the samples themselves come from `reminder_estimate_samples`. |

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
| `annual_interest_rate` | `NUMERIC(9,4)` | NOT NULL CHECK ≥ 0 | Annual rate as a **percent**, not a fraction: a rate of a few percent is stored as that single-digit number with four decimals (MD `int_rate`). |
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

The money terms (`original_principal`, `escrow_amount`, `fixed_payment`) are validated in the API at the minor units of the loan account's currency, and the loan payment preview request carries that currency ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15, settled by Claude, 2026-11-16). The columns keep scale 4 and carry no CHECK for it.

RLS: flattened per-ledger policy `loan_terms_per_user` (same shape as migrations 071/072/075/125). Covered by `fn_ledger_snapshot_payload` / `fn_ledger_snapshot_restore` (added alongside the table in migration 127).

### `budget_targets`

The number a person **typed** for one category in one month (migration 225, [decisions/0099-budget-as-guideposts-and-hand-rolled-charts.md](decisions/0099-budget-as-guideposts-and-hand-rolled-charts.md) D1/D1a). Its counterpart, the derived *normal*, is the mean of the trailing complete months (for a ledger younger than the window, of the months since its first transaction: ADR-0099 D1 as amended) and is stored **nowhere** — it is recomputed on every read, which is what lets the budget screen be useful before anyone has typed anything.

**Presence of the row IS the state** (ADR-0099 D1). There is no per-ledger "budgeting enabled" flag. A category with a row this month is judged against `amount`; one without is judged against its normal. Deleting a row therefore does not clear a budget, it returns that category to its normal.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_budget_targets`, DEFAULT `gen_random_uuid()` | A surrogate key, even though `(ledger_id, category_id, target_month)` is the real identity. The snapshot writer chunks on the primary key whatever its shape (migration 264; see `ledger_snapshot_parts`), so nothing depends on the column's name or type. |
| `ledger_id` | `UUID` | NOT NULL FK `fk_budget_targets_ledger` → `ledgers(id)` ON DELETE CASCADE | Phase A anchor (ADR-0020), same role as on `loan_terms`. |
| `category_id` | `UUID` | NOT NULL; composite FK `fk_budget_targets_category` `(category_id, ledger_id)` → `accounts(id, ledger_id)` ON DELETE CASCADE | Ledger-coherent composite, resolving against `uq_accounts_id_ledger`. CASCADE is **forced**: `fn_ledger_snapshot_clear` deletes every `accounts` row on every restore and `fn_ledger_delete` does the same on every ledger delete, so RESTRICT would break both. |
| `target_month` | `DATE` | NOT NULL CHECK `ck_budget_targets_month_is_first_of_month` (`= date_trunc('month', …)`) | Always the first of the month. Without the CHECK a mid-month date makes two rows that are one month to a reader and two to the unique index. |
| `amount` | `NUMERIC(20,3)` | NOT NULL CHECK `ck_budget_targets_amount_non_negative` (`>= 0`) | Zero is meaningful ("I intend to spend nothing here"); negatives are not. Held at the minor units of `currency_code`, the currency it was typed in ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15). Migration 225 made it (19,2); migration 301 widens it to (20,3), 17 integer digits as before. **No per-currency CHECK** (settled by Claude, 2026-11-16): a stored amount that does not fit is kept, and the D12 backfill stamps existing 2-place targets with a ledger currency that may have 0 or 3 places, so a validated CHECK would fail the migration on such a backup, and even a NOT VALID one would refuse an unchanged row on any UPDATE. A stored target that does not fit is kept and listed like any other figure (see `txn_legs.amount`). A typed target is checked at its currency's places like any typed amount, and one finer than its currency is refused with the same named error; a figure the API computes for it, such as a budget merge's, is rounded once at its currency's places, half away from zero (settled by Claude, 2026-11-16). Round nowhere else (see migration 209). |
| `currency_code` | `TEXT` | NOT NULL, no default | The currency `amount` was typed in: the ledger's reporting currency (`ledgers.currency_code`) when it was typed ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D12, **migration 280**). `BudgetTargetsRepository` stamps it on every insert and on every retype, read from the ledger under the ledger write lock (migration 275), which a reporting-currency change also takes, so a target is never stamped with a code already replaced when it commits. A typed target and a fill from the average take the reporting currency; a copy of last month (`copyFromMonth`) keeps each copied target's own currency and amount. Nothing rewrites it when the reporting currency changes, so changing the currency back reads every target as typed; a target in another currency than the ledger's converts at read time. Migration 280 set every existing target to its ledger's new code. No default, on purpose: a writer that names no currency is refused rather than stamped with one the person may not have typed in. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Unique: `uq_budget_targets_category_month (ledger_id, category_id, target_month)`. Index: `ix_budget_targets_ledger_month (ledger_id, target_month)` for the month-at-a-time read.

A bulk fill (copy last month, fill from average: `BudgetTargetsRepository.SetManyAsync`) writes its month's targets in a transaction that takes the ledger write lock (`fn_ledger_write_lock`) first, before it reads the tree and the targets it validates against. A category merge holds that lock while it locks its source and moves the source's targets onto the destination's; a fill that wrote the destination's target and then one naming the source would otherwise wait for the merge while the merge waited for it (ADR-0112 D3).

**Two rules the schema deliberately cannot enforce**, both owned by `BudgetTargetsRepository`:

1. *That `category_id` names a category at all.* Categories are rows in `accounts`, so the composite FK is satisfied by a bank account too. Enforcing it in SQL would need a unique index on `(id, ledger_id, category_kind)` to compose against, and ADR-0032 makes a trigger a last resort.
2. *That a target and its ancestors are mutually exclusive* (ADR-0099 D1a) — a target may sit on a node or on its descendants, never both, so each subtree has exactly one authoritative mark and the hero's roots-only ceiling needs no special case. This is a **recursive tree predicate**: no CHECK and no unique index can express it. Writes that would violate it are refused with a business error naming the conflicting category.

RLS: role-aware pair `budget_targets_read` (SELECT for any grant) / `budget_targets_write` (ALL, `role IN ('owner','editor')`, repeated in USING and WITH CHECK), per migration 174 / ADR-0083 D2. Covered by `fn_ledger_snapshot_payload` / `_restore` / `_part_names` / `_insert_order` / `_clear` (all five re-declared in migration 225) — a stored target is a decision about the book, the same class as a transaction, so it rolls back with one.

### `feed_csv_mappings`

A reusable, hand-editable description of one institution's delimited export (migration 222, [decisions/0031-ingest-provider-pattern.md](decisions/0031-ingest-provider-pattern.md) Phase 5). `GenericCsvProvider` (provider key `csv-generic`) reads a file according to one of these rows instead of a hand-written parser per institution. The mapping describes a **file shape, not a destination** — two cards from one issuer export identically, so it is bound to the ledger and never to an account; binding it to an account would duplicate the row and let the copies drift. Migration 223's `accounts.import_provider_key` deliberately does not go near this table: that column names a per-brokerage provider implemented in code ([decisions/0098-brokerage-csv-is-a-shim-to-qif.md](decisions/0098-brokerage-csv-is-a-shim-to-qif.md)), which needs no mapping document.

**Why the definition is a document and not typed columns.** The mapping describes, per field: the `delimiter` (comma, tab, semicolon or pipe, whatever the file's extension says); `header_rows` (leading lines to skip, `0` when line 1 is already data) and `footer_rows` (trailing lines to skip); the `date` column and its format; the `amount`, either `signed` (one column, with `invert` for a file signed from the *issuer's* perspective, where a purchase is positive because it increases what you owe) or `debit_credit` (two columns), and its decimal separator, point or comma ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15: guessed from the sample only when a layout is first made, and never re-guessed for a saved one; required from mapping schema version 2, see `schema_version`; the other character may group the whole part, in thousands (a first group of one to three digits, then groups of three) or lakh-style (a first group of one or two digits, groups of two, then a last group of three, with two grouping characters at least), and the first group never starts with a zero, the one grouping rule QIF reads by); and the `payee` and `memo` columns. Every mapping also gets, from the reader and with no field: UTF-8 with or without a BOM, a currency symbol, spaces or accounting parentheses inside an amount, and padding trimmed from the date, payee and memo text. The file is split by RFC 4180 with only the mapping's delimiter, never a detected one: a quoted field may hold the delimiter, doubled quotes and line breaks; a blank line or an Excel `sep=` line is a line like any other, so `header_rows` counts it; and `#` and `\` are text. A row that cannot be read is skipped and reported with the line of the file it starts on. As typed columns with `CHECK` constraints, each field is a column, and each new one a migration. A schema that needs a migration per institution is the wrong shape for a format nobody controls, and the churn would land on every existing row as a nullable-with-default.

**YAML text, not JSONB.** The point of a hand-editable document is that it survives being hand-edited. Comments, key order and formatting belong to whoever wrote it, and round-tripping through JSONB would silently discard all three — so what is stored is exactly what was written, and a preset for a given bank can be pasted in or shared as-is.

**What the table gives up, and where the real gate is.** Choosing a document surrenders every constraint typed columns would carry: that a signed shape names an amount column, that an index is ≥ 1, that the delimiter is one of a known set. Postgres cannot check any of it here. That work does not disappear — it moves to `CsvMappingValidator`, which is deliberately **stricter** than such CHECKs would be: unknown keys are *rejected* (a document parser's default is to ignore what it does not recognise, so a mistyped `ammount_column` would leave the amount unmapped and the import would read a different column as money); the cross-field rules CHECKs would hold (shape ↔ which columns) are asserted there instead; and column indexes are 1-based, because a `0` would read the wrong field rather than fail, and in an amount position that means importing a date as money.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_feed_csv_mappings`, DEFAULT `gen_random_uuid()` | |
| `ledger_id` | `UUID` | NOT NULL FK `fk_feed_csv_mappings_ledger` → `ledgers(id)` ON DELETE CASCADE | Phase A anchor (ADR-0020). The mapping belongs to the book, not to an account or a feed connection. |
| `name` | `TEXT` | NOT NULL CHECK `ck_feed_csv_mappings_name_present` (`length(btrim(name)) > 0`) | Stays a real column rather than living inside the document: it is what the picker lists and what uniqueness is enforced on, and a value that is both queried and hand-editable inside a blob is a value with two sources of truth. |
| `definition_yaml` | `TEXT` | NOT NULL CHECK `ck_feed_csv_mappings_yaml_present` (non-blank); CHECK `ck_feed_csv_mappings_yaml_size` (`length <= 65536`) | The YAML source as written, not a normalised re-render. The two CHECKs are the only things the database can honestly assert about a document — that it is not empty and not enormous; everything else is the validator's. The 64 KB cap is a guard, not a policy: this is a format description, and anything approaching it is a paste accident or an attack, better refused at the boundary than parsed. |
| `schema_version` | `INT` | NOT NULL DEFAULT `1`, CHECK `ck_feed_csv_mappings_schema_version` (`>= 1`) | Which validator understood the document, so a stored mapping can still be read by the validator that accepted it — a mapping written today must not start failing because version 2 added a required key. Bumped only when a change would reject a document that was valid before (`CsvMappingValidator.CurrentVersion`). Version 2 makes the amount's decimal separator a required key, so `CurrentVersion` goes to 2; a version-1 document, stored or sent, is still accepted and reads with a point, the only reading it ever had, and a layout written without a sample, by hand or through the MCP tools, must name the separator ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15, settled by Claude, 2026-11-16). Written by the save path from the document's own `version:` key, so the column is a queryable projection of the document, never an independent value. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT `now()` | |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT `now()` | Maintained by `CsvMappingsRepository` on save, not by a trigger (ADR-0032). |

Unique: `uq_feed_csv_mappings_ledger_name (ledger_id, lower(name))` — the import wizard offers these by name, and two "Card export" rows would make the choice a coin flip. Case-insensitive, because "Card export" and "card export" are the same choice to the person reading the list.

Nothing references this table: no FK points at a mapping, so deleting one leaves the transactions it produced untouched.

RLS: role-aware pair `feed_csv_mappings_read` (SELECT for any grant) / `feed_csv_mappings_write` (ALL, `role IN ('owner','editor')`, repeated in USING and WITH CHECK), per migration 174 / ADR-0083 D2 — declared by migration 222 itself, because a table added after 174 inherits no protection, and `ALTER DEFAULT PRIVILEGES` granted `coffer_app` write until migration 254, so "no policy" would have meant "no refusal". **Not** part of `fn_ledger_snapshot_*` — same class as `feed_connections`: configuration, not ledger data, and it must survive a data rollback. Rolling a ledger back to last month must not delete a mapping authored last week, and restoring an old snapshot must not resurrect one that was deliberately deleted. The transactions a mapping produced *are* captured — as ordinary rows in `txn_headers` / `txn_legs`. A format description and the money read through it have separate lifetimes.

### `ledger_events`

What a ledger has been **told** — one row per notification published about one ledger (migration 208, [decisions/0096-notification-bus-and-two-event-scopes.md](decisions/0096-notification-bus-and-two-event-scopes.md) D1). Migration 207 built the deployment half (`system_events`); 208 built this one because the consistency check produced ledger-scope findings and had nowhere to publish them, and writing them into `system_events` would have broken the scope split the ADR spends its length arguing for. Distinct from `ledger_operations`, which is a **run** log: a drift notice has no start, no end and no provider. ADR-0096 D4 keeps them apart from the other direction too — `ledger_operations` records every four-hourly quote refresh and should keep doing so, but if every activity row became a notification the channel would become noise and stop being read.

ADR-0096 D1 as written proposed *extending* `ledger_operations` for ledger scope; migration 208 took a separate table instead, for the reason above, while keeping the part of ADR-0096 D1 that is load-bearing — the two scopes never share a table. The alternative, a nullable `ledger_id` on `system_events`, fails on all four of ADR-0096 D1's tests: **authorization** (a ledger event is gated by grant, an RLS question; a system event by admin — one table means one policy answering both, and a NULL scope is exactly where fail-open and fail-closed bugs live), **lifecycle** (these rows die with their ledger, while "the backup failed" outlives every ledger), **snapshot capture** (see the closing line), and **audience** (the ledger's holders vs whoever operates the install).

`event_key` is the column the read side actually reasons with. Keys are `<subject>.<outcome>` — `consistency.drift`, `consistency.ok`, `quote-refresh.failed` — so the events panel derives "is this still true?" by finding the first later `info` event sharing the subject, and reports that instant as the resolution. That needs no `resolved_at` column and no correlation id, and it makes every scheduled-job warning self-resolving for free, because the success signal each job already publishes on its next good run *is* the all-clear. It is derived on read rather than stored deliberately: the log is append-only, and "is this still true" is a fact about the rows *around* a row, not a property of it — writing `resolved_at` back onto a log entry would mean mutating history to answer a question history can already answer.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_ledger_events`, DEFAULT `gen_random_uuid()` | |
| `ledger_id` | `UUID` | NOT NULL FK `fk_ledger_events_ledger` → `ledgers(id)` ON DELETE CASCADE | Phase A anchor (ADR-0020) and the RLS gate. CASCADE is ADR-0096 D1's lifecycle argument in one clause: a notice about a ledger is meaningless once the ledger is gone. Contrast `system_events`, which has no such parent. |
| `occurred_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Publish instant. Leading sort of the only non-PK index, and the cutoff column for retention pruning. |
| `severity` | `TEXT` | NOT NULL CHECK `ck_ledger_events_severity` in (`info`, `warning`, `critical`) | ADR-0096 D3, orthogonal to `topic` on purpose — "backup failed" and "quote provider unreachable" share a severity and want very different handling. `info` does double duty as the all-clear the resolution rule reads. |
| `topic` | `TEXT` | NOT NULL CHECK `ck_ledger_events_topic` in (`backup`, `snapshot`, `sync`, `quotes`, `consistency`, `scheduler`) | The subsystem the event is about. A new job does not widen this if it fits an existing topic: migration 219's reminder auto-post publishes under `scheduler` and needed no schema change. |
| `event_key` | `TEXT` | NOT NULL; deliberately no CHECK | `<subject>.<outcome>`. Machine-readable and part of the delivery contract (a heartbeat subscriber keys its monitor on it), not a label. Unconstrained because the key set is a property of the build's publishers, not of the schema — same argument migration 212 makes for `monitors`. Not unique, and repeats are expected: `PublishLedgerThrottledAsync` suppresses a repeat by asking this table whether the same key was published for the same ledger inside a window, which is scoped per ledger so one ledger's standing drift cannot silence another ledger's first report of the same kind. A job that switches itself off publishes `<job type>.disabled`, which that job's next `<job type>.succeeded` clears; migration 267 moved the older `scheduler.job-disabled` rows here and in `system_events` to their job's key. |
| `summary` | `TEXT` | NOT NULL | One line, for a human reading a list. |
| `detail` | `JSONB` | NOT NULL DEFAULT `'{}'::jsonb` | Structured extras, kept small — this is not a log. Notably does **not** carry the ledger's name: the publisher adds that to the *delivered* payload only, so a shared webhook can tell ledgers apart, while the stored row keeps the original detail. Repeating the name here would duplicate it into every historical row and freeze a stale copy of a renameable thing in the store. |

Index `ix_ledger_events_ledger_occurred (ledger_id, occurred_at DESC)` — one index serving both reads: the panel's bounded top-N per ledger, and `AuditRetentionService`'s cutoff scan. That service prunes rows older than `Api:EventRetentionDays` (default **365**), a separate and longer knob than `Api:AuditRetentionDays` (180) because the two answer different questions — the audit logs answer "what happened recently", the event log answers "how long has this been going wrong".

RLS: enabled, with a **single read policy** `ledger_events_read` (SELECT for any grant) and **no write policy, on purpose**. `NotificationPublisher` writes through `ServiceDbContextFactory` (BYPASSRLS) and no app-role path writes here at all — the panel only reads. RLS is default-deny for anything without a permissive policy, so a `coffer_app` INSERT/UPDATE/DELETE is refused outright; this mattered because migration 017's `ALTER DEFAULT PRIVILEGES` handed `coffer_app` write *permission* on every new table, until migration 254 removed both that default and these tables' unused write grants (FU-086). Same treatment migration 174 gave the other service-written logs (`ledger_operations`, `mcp_tool_invocations`). **Not** part of `fn_ledger_snapshot_payload` / `_restore` — this is a record of what was *announced*, not ledger data, and restoring it would resurrect stale notices (a "drift found" that has since been repaired) and erase newer ones. A rollback must not rewrite what you were already told.

### `ledger_consistency_state`

The latest consistency verdict for one ledger — one row per ledger, overwritten (migration 242). Without it, a check is computed and thrown away: the monitor runs it, publishes a notification if something drifted, and keeps nothing. A repair leaves no trace either, because repairing makes the stored rows agree with what they are derived from, which is indistinguishable from their having always agreed. So three situations look identical from outside — a ledger nobody has checked, one checked and healthy, and one checked long ago — and an operator with several ledgers cannot tell a repaired one from one nobody has opened.

**Not a log.** The append-only history of repairs is `ledger_events` with key `consistency.repaired`, which already has retention. A second copy here would be two sources of truth for one fact, and the copy is always the one that drifts. This table answers only "what is true now, and as of when".

**It also gates the monitor's cadence.** `ConsistencyMonitor` is invoked on every scheduler tick — fifteen minutes — because it deliberately is not a `global_scheduled_jobs` row: nobody should be able to switch off the thing whose purpose is noticing that the switchable jobs stopped. But being invoked often is not a reason to *work* often: ungated, it re-verifies every balance row and every position of each ledger 96 times a day, to announce at most once. `last_checked_at` lets the monitor skip a ledger checked recently. The gate is this recorded timestamp rather than an in-memory one specifically so a restart does not reset it — a process recycled every few minutes would otherwise re-check everything on every boot and be exactly where it started. *Since 2026-11-05 (ADR-0102, D1 amendment)* a ledger is due when its last check **or** its last announced verdict (a `consistency.ok` or `consistency.drift` row in `ledger_events`) is older than the cadence: a check a person runs writes this row too but announces only a change, so the check alone would let a person's checks hold back the ping a dead-man's switch waits for.

Recording the verdict is also why the monitor needs no announcement throttle. With the verdict remembered it can tell "still drifted" from "drifted again"; without it, a timer is the only tool, and a timer keyed on the event name swallows a *second* projection that starts to drift inside the window. The cadence is the only rate control; publishing happens on every pass, in both states, because one publish serves two audiences with opposite needs and suppressing it starves the dead-man's switch at the same moment it spares the human.

**Who writes it** (2026-11-05). Every check and every repair writes it through one path, `ConsistencyVerdicts`: the monitor's pass, a check a person runs (`POST …/balances/consistency`, which heals nothing), and a repair (`POST …/balances/consistency/{projection}/repair`), which writes the verdict of the full check it runs once it has committed. The Overview's Problems widget reads this row for its "consistency checked …" text and the ledger's outstanding `consistency.` subject for its rows, so each writer also publishes: the monitor its verdict on every pass; a person's check its verdict only when it differs from this row or from what the ledger's notice says, so pressing it again sends nothing; a repair one `consistency.repaired` event, info only when nothing disagrees and a warning otherwise, so the subject stays outstanding until the ledger is healthy. A repair that has begun runs to the end and is recorded whatever happens to the client or to the check after it: several rewrites commit in pieces, so once the ledger is found the rewrite, the check and the record run under no token of the request's, and a client that hangs up stops none of them. A check that fails leaves `last_repaired_at` stamped and the verdict cleared, as a restore clears it: the verdict was taken before the rewrite. The ledger is then due on the next tick, and the event is a warning whose detail says `checked: "false"`. A rewrite that throws partway, for a reason other than its client (a database error, say), answers its error, and the pieces it committed stand: the verdict is cleared through the same path, as of when the rewrite stopped, so the ledger is due on the next tick, but `last_repaired_at` is left as it was, since no repair completed, and no event is recorded, so an outstanding subject stays outstanding. One repair of a ledger runs at a time: a repair sent while another repair of the ledger runs, of any projection, is refused with `consistency-repair-in-progress` before it rewrites anything, and writes nothing here (the balances and holdings rewrites over the same rows at once fail one of them partway; see `txn_header_account_balances` and `realized_gains`).

**Writers that overlap.** A check takes a while, and others can record while it runs. A verdict's time is when its check began to read the ledger. It is written, and announced, only when that time is no earlier than `last_checked_at`, or, with no verdict stored, no earlier than `updated_at`, the time the clearing is as of: a restore's time; for a repair whose check did not finish, when its rewrite committed; or, for a rewrite that threw partway, when it stopped, after the last piece it committed; in each case the verdict cleared was taken before the rewrite. An older verdict describes data a newer check has already seen past, so a monitor pass that began before a repair committed neither overwrites the repair's verdict nor announces drift the repair fixed, and a check or a repair that began after a repair's rewrite writes over the verdict that repair cleared, whenever it records. A repair always stamps `last_repaired_at`, keeping the later of two, but writes or clears the verdict under the same rule, as does a rewrite that threw partway, which stamps nothing; one whose verdict is not written stamps `last_repaired_at` alone and leaves `updated_at` as it is, so it moves nothing a later verdict is compared with. Its `consistency.repaired` event follows the verdict this row holds once that is decided: when a later check's verdict stands, the event is info only when that verdict is healthy, its summary names what that check found (by name, as this row keeps them) and its detail says `superseded: "true"`; when a restore or a later repair has cleared the verdict since, it is a warning saying what still disagrees is not known. Every consistency event shares one subject, so an event read off the repair's own, older check would clear it on a ledger this row calls unhealthy, or reopen it on one it calls healthy. Every writer saves the row against the version it read, Postgres's `xmin` system column, mapped as the row's version and not a column of the table. When another writer saved the row, or created it, in between, the save fails, and the writer reads the row again and decides again. A person's check deciding again after such a race announces only a verdict other than the stored one, since the writer that won holds that verdict and announces it. A writer saves the row and the event that follows it in one transaction, writing the row even when no field changes, so no writer saves between the two. A writer whose save follows another's has read the row only after that one committed, and `ledger_events.occurred_at` is its own transaction's start, so its event, when it records one, is the later. The event is delivered to the ledger's notification targets once the transaction has committed.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `ledger_id` | `UUID` | PK `pk_ledger_consistency_state`, FK `fk_ledger_consistency_state_ledger` → `ledgers(id)` ON DELETE CASCADE | The ledger *is* the key. A surrogate id would permit two verdicts for one ledger, which is the ambiguity this table exists to remove. CASCADE for the same lifecycle reason as `ledger_events`: a verdict about a deleted ledger is meaningless. |
| `last_checked_at` | `TIMESTAMPTZ` | NULL | When the projections were last compared with what they are derived from (the transactions, or, for `holdings_currency`, each holdings account's brokerage, or, for `reminder_auto_post`, the ledger's reminders), written on every check: the monitor's, a person's, and the one a repair runs after it. It is the time the check began to read the ledger, and a check that began earlier than the stored one does not overwrite it. A verdict with no date cannot distinguish "fine an hour ago" from "fine last August", and that distinction is the point of recording it. Also the cadence gate, hence the index — and NULL is how anything invalidates a verdict, since `NULL > cutoff` is NULL rather than true, so the ledger reads as due without the invalidating code having to know what the cadence is. |
| `healthy` | `BOOLEAN` | NULL | The verdict at `last_checked_at`, NULL when there is none. `false` means **checked and wrong**, never "not known" — anything rendering the verdict reads false as unhealthy, so a sentinel here would show a red mark for a ledger nobody has examined. |
| `failing_projections` | `TEXT[]` | NOT NULL DEFAULT `'{}'` | Which projections disagreed, by their `ConsistencyProjections` name; empty when healthy or unknown. Stored rather than derived so the list survives without re-running the check that produced it. |
| `last_repaired_at` | `TIMESTAMPTZ` | NULL | The last time anyone repaired anything on this ledger. **Not cleared by a later healthy check** — that a ledger was once repaired is a fact about its history, and a green check does not retract it. Written by the repair endpoint, at the moment its rewrite has committed, together with the verdict of the full check the repair runs from then — never a verdict read off the one projection it repaired, which would assert more than was checked. When that check does not finish, the repair is still stamped here and the verdict is cleared. A rewrite that throws partway is not stamped here, since no repair completed, though the verdict is cleared. |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | When the verdict was last written, or, with no verdict stored, the time the clearing is as of: a restore's time; for a repair whose check did not finish, when its rewrite committed; or, for a rewrite that threw partway, when it stopped. A check's verdict is not written over a clearing it began before (**Writers that overlap**). A repair whose verdict is not written leaves it as it is. |

Four states: **no row** (nobody has ever checked this ledger), **`healthy IS NULL`** (something to remember, but no current verdict — invalidated by a restore, by a repair whose check did not finish, or by a repair whose rewrite threw partway; a repair otherwise writes the verdict of its own check), **true**, and **false with a list**. Two CHECKs keep those honest rather than by convention: `ck_ledger_consistency_state_verdict` asserts `(last_checked_at IS NULL) = (healthy IS NULL)`, so half a verdict cannot be written; `ck_ledger_consistency_state_failing_empty_when_unknown` asserts an unknown carries no failing projections, so an invalidation that cleared the verdict and forgot the list is refused rather than leaving stale names attached to it.

Index `ix_ledger_consistency_state_last_checked` on `(last_checked_at)` — the monitor's "which ledgers are due?" is the only query shaped by anything but the primary key.

**Not** part of `fn_ledger_snapshot_payload` / `_restore`, and on `SchemaDriftGuardTests`' exclusion list. Capturing a verdict would be capturing the answer along with the question: a restore replaces the very rows the verdict was about, so a captured "healthy" would come back as a claim about data it was never checked against. [SnapshotsEndpoints.cs](../src/Api/Endpoints/SnapshotsEndpoints.cs) nulls the verdict after a successful restore instead — which says the true thing, and makes the ledger due on the next tick. That matters under the cadence gate: at twice a day, a snapshot captured *while* drifted would otherwise sit behind a stale green for up to half a day. `last_repaired_at` is excluded for the `ledger_events` reason on top of that — it is history, and a rollback must not rewrite what already happened.

RLS: a SELECT policy for `coffer_app` scoped by `user_ledger_grants`, and **no write policy at all**. Every writer is the monitor or an endpoint going through the service role, the same treatment migration 208 gave `ledger_events` and 174 gave the other service-written logs. RLS is default-deny without a permissive policy, so an app-role INSERT/UPDATE/DELETE is refused outright rather than relying on nobody trying.

### `ledger_notification_subscribers`

A ledger's own notification delivery targets (migration 208, [decisions/0096-notification-bus-and-two-event-scopes.md](decisions/0096-notification-bus-and-two-event-scopes.md) D7/D8). One row per destination — a `webhook` URL, a `healthchecks` dead-man's switch — that `NotificationPublisher` delivers this ledger's `ledger_events` to. The deployment-scope counterpart is `notification_subscribers` (migration 207); the two rows are identical to deliver to, so one loop adapts both rather than existing twice, which is how the two scopes would quietly drift apart.

**Migration 214 retired the `inherit` mode.** Migration 208's `notification_mode` of `inherit` / `own` defaulted to `inherit`, which routes a ledger's events to *every* deployment target with no ledger filter — so on a shared install one ledger's activity goes to whoever watches the deployment's channel, by default. Two scopes that overlap by default are not two scopes. Retiring the mode alone would mute ledger scope on every existing install, the silent failure ADR-0096 exists to end, so 214 moves the deployment's **message** targets into each inheriting ledger first, in the same transaction, then drops the column and its CHECK. That move is pure SQL because `config_ciphertext` in both scopes is sealed with the same master KEK by the same primitive, so the bytes are portable verbatim and no key material is needed at migration time. Three things it deliberately did **not** copy: heartbeat targets (one healthchecks URL is one check — N ledgers pinging it means any one ledger's job holds it green and masks every other ledger's dead one), disabled targets (a target an admin switched off must not return as N copies), and ledgers already in `own` (they opted out explicitly).

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_ledger_notification_subscribers`, DEFAULT `gen_random_uuid()` | |
| `ledger_id` | `UUID` | NOT NULL FK `fk_ledger_notification_subscribers_ledger` → `ledgers(id)` ON DELETE CASCADE | Phase A anchor (ADR-0020) and the RLS gate. |
| `subscriber_key` | `TEXT` | NOT NULL; deliberately no CHECK | Which provider delivers (`healthchecks`, `webhook`). The registered set is build metadata, not schema. The API rejects an unknown key at create; the publisher records a target whose key resolves to no registered provider as a delivery **failure** rather than skipping it, because from the settings page an unroutable target looks exactly like a working one. |
| `display_name` | `TEXT` | NOT NULL | User label for the settings list. |
| `is_enabled` | `BOOLEAN` | NOT NULL DEFAULT TRUE | Delivery switch, and the predicate of the partial index below. |
| `min_severity` | `TEXT` | NOT NULL DEFAULT `'warning'` CHECK `ck_ledger_notification_subscribers_min_severity` in (`info`, `warning`, `critical`) | Floor for a **message** target. A heartbeat target ignores it entirely: its provider can express only "alive" or "down" and has no concept of severity, so routing it by severity would let an unrelated Critical event mark the backup check failed. |
| `topics` | `TEXT[]` | NULL | Topic filter for a message target; NULL or empty means every topic. A filter narrows what arrives — it cannot say what a URL *is*, which is the gap `monitors` exists to close. |
| `monitors` | `TEXT` | NULL (migration 212) | Singular despite the plural name: the **one** monitor a heartbeat URL watches. A binding, not a filter — a heartbeat subscriber receives its own monitor's signals and nothing else, which is what makes "for each known monitor, is anything watching for its absence?" answerable. No CHECK: the valid set lives in the build (`NotificationMonitors`), so a value list here would need amending by migration for every new job and would reject a value an install mid-upgrade considers valid. The API validates against the **ledger** monitor set (`quote-refresh`, `snapshot`, `feed-sync`, `reminder-auto-post`, `consistency`), requires it for a heartbeat provider, and forbids it for a message one — a rule the database cannot express, since capability lives in the provider, not the row. Migration 212 backfilled pre-existing `healthchecks` rows in both scopes to `backup`; `backup` is a *deployment* monitor that no ledger runs, so such a row at ledger scope is a switch that can never fire, and the coverage API reports it in "watching nothing". |
| `config_ciphertext` | `BYTEA` | NOT NULL | The delivery URL and any provider config as JSON, **sealed under the master KEK** (ADR-0096 D8) — not the ledger's LEK, even though this is a ledger-scoped table, because a webhook URL routinely carries its own token and the deployment scope needs the same primitive. That choice is what let migration 214 move ciphertext between the two tables as raw bytes, and what lets `KekRotationService` re-wrap both scopes in a single pass. Never plaintext at rest. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Moves on **delivery outcome**, not on user edits — the ledger surface is `POST /subscribers` + `DELETE /subscribers/{id}` only, so a target is replaced, never edited. |
| `last_success_at` | `TIMESTAMPTZ` | NULL | Last successful delivery. |
| `last_failure_at` | `TIMESTAMPTZ` | NULL | Last failed delivery. |
| `last_error` | `TEXT` | NULL | Failure detail, truncated to 500 chars by the publisher (no DB length bound); cleared on the next success. |
| `consecutive_failures` | `INT` | NOT NULL DEFAULT 0 | Current-state health, not a lifetime counter: incremented per failure, reset to 0 on success. Nothing auto-disables on it — it drives what the settings panel shows, and `is_enabled` stays the user's decision. |

Index `ix_ledger_notification_subscribers_ledger (ledger_id) WHERE is_enabled` — a partial index matching the delivery query exactly (`ledger_id = … AND is_enabled`), which is every publish on this path.

There is deliberately **no** unique constraint on `(ledger_id, subscriber_key)`: two `webhook` rows pointing at two destinations is a legitimate setup. Migration 214's backfill therefore enforces its own idempotence with a `NOT EXISTS` on `subscriber_key` rather than leaning on a constraint — and it compares the key rather than the ciphertext because `SealWithMasterKey` draws a fresh nonce per call, so two seals of the same URL differ byte for byte and ciphertext equality can never be tested.

RLS: `ledger_notification_subscribers_read` (SELECT for any grant) / `ledger_notification_subscribers_write` (ALL, `role = 'owner'`, repeated in USING and WITH CHECK). Owner-only since migration 255 (FU-095): a target receives the ledger's events, and an editor could point them at an address of their own. Before that, 174 let editors write it too. Every member can see where their ledger reports. The publisher's health columns are written as the service role. **Not** part of `fn_ledger_snapshot_payload` / `_restore` — delivery configuration, the same class as `feed_connections`: it must survive a data rollback, and there is a security edge besides. Capturing it would let restoring an old snapshot re-enable a target the user deliberately removed and re-arm a webhook URL they had revoked.

### `system_events`

Deployment-scope notification history — the installation's own event log (migration 207, [decisions/0096-notification-bus-and-two-event-scopes.md](decisions/0096-notification-bus-and-two-event-scopes.md) D1). Without it, scheduled snapshots and backups can stop with nothing said, and a projection left wrong by a data change shows only when somebody runs a maintenance action by hand. Neither lacks a *check*; what both lack is a way to tell anyone.

**No `ledger_id`, deliberately** (ADR-0096 D1), rather than a nullable scope column on a shared table with `ledger_operations`. Three things differ, not one: *authorization* — a ledger event is gated by grant (an RLS question), a system event by admin, and one policy answering both means a NULL scope, which is where fail-open/fail-closed bugs live; *lifecycle* — a ledger event dies with its ledger, "the backup failed" must outlive it; *snapshot capture* — ADR-0037 captures per-ledger tables, and system history must never ride inside a ledger snapshot. A row here survives deleting every ledger.

**This table is history, not the alarm** (ADR-0096 D5, the decision that inverts the obvious design). A system event about the system being broken cannot be written by the broken system: if Postgres is unreachable or the container is dead, nothing writes here, no subscriber fires, and nothing lights up — which is exactly the silent stop described above. Absence detection therefore lives *outside* the deployment, in a dead-man's-switch monitor pinged on success. The table records what was announced; it cannot notice silence.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_system_events`, DEFAULT `gen_random_uuid()` | |
| `occurred_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Newest-first reads dominate (the admin panel, the events API page), hence the two DESC indexes below. Also the retention cutoff column. |
| `severity` | `TEXT` | NOT NULL, CHECK `ck_system_events_severity` IN (`info`,`warning`,`critical`) | Orthogonal to `topic` (ADR-0096 D3), not one collapsed enum: "backup succeeded" and "quotes updated" share a severity and want different routing; "backup failed" and "provider unreachable" likewise. Collapsing them forces every subscriber to re-derive the distinction. Constrained rather than free text so a typo is a write error instead of an event no subscriber ever matches. |
| `topic` | `TEXT` | NOT NULL, CHECK `ck_system_events_topic` IN (`backup`,`snapshot`,`sync`,`quotes`,`consistency`,`scheduler`,`upgrade`) | The routing axis. `upgrade` (migration 272, [ADR-0110](decisions/0110-upgrade-from-the-app.md)) is deployment scope only: `ledger_events` keeps its list, because an available release is a fact about the installation. It is also the one topic a destination receives only by naming it. Adding a topic is a migration — that is the point of the CHECK. |
| `event_key` | `TEXT` | NOT NULL | Machine-readable discriminator within a topic, e.g. `backup.succeeded`. **Load-bearing for de-duplication**: `NotificationPublisher.PublishThrottledAsync` suppresses a repeat by asking this table whether the same `event_key` appeared inside a window — checked against the row history rather than kept in memory, so a process restart does not re-announce everything. A standing condition re-checked on the scheduler tick would otherwise be announced ~96 times a day, which mutes the channel and is ADR-0096 D4's cry-wolf failure arriving through repetition instead of severity. No index of its own; the probe is bounded by the `occurred_at` range, and the `Api:EventRetentionDays` prune bounds the table. |
| `summary` | `TEXT` | NOT NULL | Human-readable line as delivered. |
| `detail` | `JSONB` | NOT NULL DEFAULT `'{}'` | Structured payload for the event; JSONB so a new event kind is not a schema change. |

Indexes: `ix_system_events_occurred_at (occurred_at DESC)` for the panel, and `ix_system_events_severity_occurred (severity, occurred_at DESC)` for the badge/alert filter.

Rows are written by `NotificationPublisher` and `SchedulerRunner` on the **BYPASSRLS service role** — a background tick has no request user. `AuditRetentionService` prunes `occurred_at` older than `Api:EventRetentionDays` (default **365**, a separate and longer knob than `AuditRetentionDays`' 180: the audit logs answer "what happened recently", the event log answers "how long has this been going wrong"). Unlike `ledger_events`, this table has no parent to cascade from, so **that prune is the only thing that ever removes a row.**

RLS: **not** the role-aware pair of migration 174 / ADR-0083 D2 — deployment scope has no `ledger_id` and no grant to join, so the predicate is "is this app user an admin". Single read-only policy `system_events_admin_read` (SELECT for `coffer_app`, `USING current_app_user_is_admin()`), added in **migration 213, not 207** — 207 shipped this table with RLS disabled, so `coffer_app` had unrestricted CRUD by way of migration 017's `ALTER DEFAULT PRIVILEGES`. Those write grants stayed until migration 254 revoked them (FU-086); RLS refused the writes all along, because no policy covers INSERT/UPDATE/DELETE and nothing user-facing has any business amending an audit trail. It answers false for a disabled admin since migration 248 (ADR-0105 D3); before that a disabled admin kept reading through these policies. `current_app_user_is_admin()` is `SECURITY DEFINER` with a pinned `search_path` on purpose: an inline `EXISTS` over `users` would happen to work today (the row it needs is the caller's own) while silently coupling this policy to the shape of `users_self`. **Not** part of `fn_ledger_snapshot_*`, and it could not be — deployment scope puts it outside the ledger-scoped set `SchemaDriftGuardTests` scans at all. The reason is the same one that excludes `ledger_events`: this is a record of what was **announced**, not ledger data, and a rollback must not rewrite what you were already told — resurrecting a "drift found" notice that has since been repaired, and erasing newer ones.

### `notification_subscribers`

Configured delivery targets for deployment-scope events — one row per configured provider (migration 207, [decisions/0096-notification-bus-and-two-event-scopes.md](decisions/0096-notification-bus-and-two-event-scopes.md) D7/D8). Delivery is pluggable on the ADR-0033 provider pattern: an `INotificationSubscriber` declares a `SubscriberKey` and `DisplayName`, the registry is DI, and `NotificationPublisher` routes. Providers divide by **capability** — *heartbeat* (healthchecks.io, Uptime Kuma push) detects absence; *message* (ntfy, Discord, Slack, generic webhook) cannot, because noticing that nothing arrived requires something outside the deployment to be counting.

**The credential lives here, sealed, not in `secrets/`** (ADR-0096 D8, which supersedes ADR-0096 D5's earlier parenthetical). Docker secrets are for credentials the app needs *before* it can read a database; Postgres passwords qualify, a webhook URL does not — it is a user-configurable integration, added and changed at runtime, and one file per provider neither scales past the first nor survives adding a second without a container restart. So `config_ciphertext` is sealed with the master KEK by `LedgerKeyService`, exactly as Drive sync stores its outbound OAuth blob, and is re-wrapped by a master-KEK rotation (ADR-0092 D4).

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_notification_subscribers`, DEFAULT `gen_random_uuid()` | |
| `subscriber_key` | `TEXT` | NOT NULL | Matches `INotificationSubscriber.SubscriberKey`, e.g. `healthchecks`, `webhook`. Deliberately **not** unique — see `monitors`. |
| `display_name` | `TEXT` | NOT NULL | Admin's label for the target. |
| `is_enabled` | `BOOLEAN` | NOT NULL DEFAULT TRUE | |
| `min_severity` | `TEXT` | NOT NULL DEFAULT `'warning'`, CHECK `ck_notification_subscribers_min_severity` IN (`info`,`warning`,`critical`) | Inclusive floor: `warning` means warning **and** critical. |
| `topics` | `TEXT[]` | NULL | Topic filter; NULL means every topic **except the opt-in ones** (`upgrade`, ADR-0110), which a destination receives only by naming them. A filter narrows what arrives — it does not say what the URL *is*, which is why `monitors` had to be added separately. |
| `monitors` | `TEXT` | NULL (migration 212) | For a heartbeat target, the **one** monitor this URL watches, e.g. `backup`; NULL for a message target, which is bound to nothing. A healthchecks.io check is a single ping URL with no concept of severity, so routing any critical event to it would turn an unrelated subject into `/fail` — a critical `consistency` event would mark the *backup* check down. Not CHECK-constrained: which monitors exist is a property of the build (`NotificationMonitors`), so a value list would need a migration per new job and would reject a value an install's own code considers valid mid-upgrade. The API validates on write, requiring it for a heartbeat provider and forbidding it for a message one — a rule the database cannot express, since capability lives in the provider, not the row. |
| `config_ciphertext` | `BYTEA` | NOT NULL | The provider's URL/token **sealed under the master KEK** (ADR-0096 D8); a webhook URL with an embedded token is a credential and is never a plain column. NOT NULL but may be **zero-length**: `KekReconciliationService` clears the blob, disables the row and writes an explanatory `last_error` when a restore brings in a target sealed under a different KEK. The length guard is what makes a second reconciliation pass a no-op. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | No trigger — the application sets it. |
| `last_success_at` | `TIMESTAMPTZ` | NULL | Delivery health, surfaced in the admin panel rather than logged. |
| `last_failure_at` | `TIMESTAMPTZ` | NULL | Also set by KEK reconciliation when a seal cannot be carried over. |
| `last_error` | `TEXT` | NULL | |
| `consecutive_failures` | `INT` | NOT NULL DEFAULT 0 | **Why delivery health is schema at all**: a subscriber that silently stops delivering would rebuild, one layer up, the exact silent failure this whole subsystem exists to prevent. |

Partial index `ix_notification_subscribers_enabled (subscriber_key) WHERE is_enabled` for the publisher's fan-out. There is no unique constraint on `subscriber_key`: one healthchecks.io URL is one check, so watching a second job needs a second row.

Migration 214 (*ledgers own their notifications*) retired the per-ledger `inherit` mode and copied this table's **message** targets into every inheriting ledger's `ledger_notification_subscribers`, because `inherit` routed a ledger's events to every deployment target with no ledger filter — two scopes that overlap by default are not two scopes. Heartbeat rows (`monitors IS NOT NULL`) and disabled rows deliberately stayed here: copying one ping URL into N ledgers would let any single ledger's job hold the check green and mask every other ledger's dead one, and a target an admin switched off must not return as N copies. So a heartbeat row here is normally a *deployment* switch watching a deployment job that no ledger runs.

RLS: **not** the role-aware pair of migration 174 / ADR-0083 D2 — no `ledger_id`, no grant to join. A single `FOR ALL` policy `notification_subscribers_admin` for `coffer_app`, gated on the `SECURITY DEFINER` helper `current_app_user_is_admin()` in both `USING` and `WITH CHECK`; `coffer_service` is BYPASSRLS, which is how the publisher and monitors keep working with no user behind them. Added in **migration 213, not 207**: 207 created this table of sealed delivery credentials with RLS disabled under migration 017's default grants. The admin endpoints all carry `RequireAuthorization(AuthPolicies.RequireAdmin)`, but this repo treats RLS as the backstop and the API filter as the primary check precisely because a filter is one forgotten attribute away from absent. **Not** part of `fn_ledger_snapshot_*`, and outside the ledger-scoped set `SchemaDriftGuardTests` scans. Same reason as `feed_connections` and `ledger_notification_subscribers`: configuration that must survive a data rollback — plus a security edge, since capturing it would let an old snapshot re-enable a target the user deliberately removed and re-arm a webhook URL they had revoked.

### `user_preferences`

General per-(user, ledger) preference store, one table not a table per feature (migration 134, [decisions/0057-user-preferences-store.md](decisions/0057-user-preferences-store.md)). One row per `(user_id, ledger_id, namespace)`; `value` is a namespace-typed JSON document, so a new preference area is a new `namespace` (+ a typed record in the API), never a schema change. Consumers so far: the `quotes` namespace (`{ "enabledProviders": [...] }`) — the per-ledger opt-in for external market-data providers (Yahoo), which **supersedes** the ADR-0054 `Quotes:Yahoo:Enabled` config gate — and the `dashboard` namespace — the per-ledger Overview layout ([decisions/0109-overview-is-an-attention-first-home.md](decisions/0109-overview-is-an-attention-first-home.md) D7). Version 2 is `{ "version": 2, "columns", "widgets": [{ "id", "key", "visible", "column", "settings" }] }`, checked for structure and bounds only (ADR-0057's 2026-10-31 amendment); a version 1 row (`{ "widgets": [{ "key", "visible" }] }`) is returned as stored, migrated by the SPA, and written as version 2 on its next save. For this namespace `updated_at` is also the compare-and-swap token a save must echo, stored and compared at microsecond precision. A widget's `id` is its `key` for a widget that appears once; a home report tile, key `report`, has the id `report:saved:<uuid>` or `report:catalog:<key>`, which is the tile's only reference to its report (ADR-0104 D2, 2026-11-06 amendment). Nothing in the database ties that id to `saved_reports`, so deleting a report leaves the tile in the layout: the Overview draws nothing for it, and offers Remove while the layout is edited.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `user_id` | `UUID` | PK part; FK → `users(id)` ON DELETE CASCADE | The owning user — preferences are personal. |
| `ledger_id` | `UUID` | PK part; FK → `ledgers(id)` ON DELETE CASCADE | The ledger the preference applies to. |
| `namespace` | `TEXT` | PK part; CHECK `<> ''` | Preference area, e.g. `quotes`. The API owns the known set. |
| `value` | `JSONB` | NOT NULL DEFAULT `'{}'` | Namespace-typed JSON document. |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | For `dashboard`, the token a save must echo: a save carrying any other value is refused as stale. Written truncated to microseconds, the column's precision, so the value a reader gets back compares equal. |

RLS: own-user **and** per-ledger-visibility policy `user_preferences_per_user` — a user reads/writes only their own rows, for ledgers they can see. **Not** part of `fn_ledger_snapshot_*` — per-user UI config, not ledger financial data, so a snapshot restore leaves it untouched. The scheduled-refresh worker (ADR-0054 B) reads the **configuring user's** `quotes` row (own-user RLS rules out a system-user pref).

### `scheduled_jobs`

The single per-ledger daily scheduler (migration 136). One row per `(ledger_id, job_type)`; a single `SchedulerService` background worker polls `next_run_at` and dispatches each due row by `job_type` to a registered handler. Generalizes the former `quote_schedules` (mig 135, migrated in as `job_type='quote-refresh'`). Job types:
- **`quote-refresh`** (ADR-0054 B) — a quote refresh as `configured_by_user_id` (using that user's `quotes` opt-in), recorded `triggered_via='scheduled'`.
- **`snapshot`** (ADR-0037) — an `auto` snapshot for the ledger (5-cap eviction applies); replaces the original fixed-weekly auto-snap, which was a no-op under RLS.
- **`reminder-auto-post`** (ADR-0097) — posts the due occurrences of the ledger's reminders set to auto-post. Its row is created, enabled at 05:00 local with no `timezone`, the first time a reminder asks for it (`recurring_transactions.auto_commit_days_before` set): a reminder saved with Auto-post, a Moneydance import that brings one, or the consistency check's `reminder_auto_post` Repair, which a ledger whose reminders ask and which has no row reports (2026-11-18 amendment). None of them changes a row that exists, and the check never reports one.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `ledger_id` | `UUID` | PK part; FK → `ledgers(id)` ON DELETE CASCADE | The ledger. |
| `job_type` | `TEXT` | PK part; CHECK `IN ('quote-refresh','snapshot','feed-sync','reminder-auto-post')` — `feed-sync` added by mig 215, `reminder-auto-post` by mig 219 | The scheduled work. |
| `enabled` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Whether it runs (opt-in). |
| `hour_local` | `SMALLINT` | NOT NULL DEFAULT 19, CHECK 0–23 | Time-of-day hour in `timezone` (migration 136). |
| `minute_local` | `SMALLINT` | NOT NULL DEFAULT 0, CHECK 0–59 | Time-of-day minute in `timezone`. |
| `timezone` | `TEXT` | NULL | IANA tz (an `Area/Location` name) the time is interpreted in — the user's browser tz at save (mig 137). NULL → server-local fallback. |
| `configured_by_user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE RESTRICT | Run attribution / pref resolution. |
| `last_run_at` / `next_run_at` | `TIMESTAMPTZ` | NULL | Worker bookkeeping; `next_run_at` NULL when disabled. |
| `created_at` / `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `consecutive_failures` | `INTEGER` | NOT NULL DEFAULT 0 | Migration 194: how many runs in a row have thrown. Drives the disable-after-N rule so a permanently broken job stops retrying daily forever. |
| `last_error` | `TEXT` | NULL | Migration 194: the last failure's message, kept so the admin UI can say WHY a job stopped rather than only that it did. |
| `last_failure_at` | `TIMESTAMPTZ` | NULL | Migration 194: when that failure happened. |
| `last_success_at` | `TIMESTAMPTZ` | NULL | Migration 252 (FU-070, FU-071): the last run that did its work, fully or in part. `last_run_at` is stamped when a run is CLAIMED, before the handler does anything, so it says nothing about success; this is what the schedule shows. A run that did NOTHING (`JobRunOutcome.DidNothing`: an auto-snapshot skipped for a full pool, every quote provider down, every feed connection awaiting reconnection) or failed leaves it where it was, which is what lets "nothing done for a day" be seen — the schedule turns red and the run's signal escalates to Critical. Backfilled from the row, never the clock: a clean last run, or a warning that is not one of the three did-nothing messages, counts as a success at `last_run_at`. |
| `disabled_reason` | `TEXT` | NULL | Migration 216: why the scheduler turned this job off — distinguishes "the user disabled it" from "it failed too many times", which the `enabled` flag alone cannot. |

Partial index `idx_scheduled_jobs_due (next_run_at) WHERE enabled` for the worker's "what's due?" query. RLS: the role-aware pair since migration 254 (FU-086), `scheduled_jobs_read` (SELECT, any grant) and `scheduled_jobs_write` (ALL, `role IN ('owner','editor')`); a schedule is a ledger setting, not a personal pref, and a viewer reads it. Until 254 one any-grant policy, `scheduled_jobs_per_ledger`, let any member write it. The worker reads via the BYPASSRLS service role — a background tick has no request user, so the RLS app role would be fail-closed.

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
| `consecutive_failures` / `last_error` / `last_failure_at` | — | | Migration 194, same three columns and the same disable-after-N rule as the per-ledger twin. |
| `last_success_at` | `TIMESTAMPTZ` | NULL | Migration 252, as above (the backup has no did-nothing outcome; backfilled from a clean last run only). |
| `disabled_reason` | `TEXT` | NULL | Migration 216, as above. |

### `system_settings`

Deployment-global key/value settings (migration 147, ADR-0063 §D8) — install-wide config an admin changes from the System-settings UI instead of env/compose. Service-role only (RLS enabled+forced with **no policy** = deny-all for `coffer_app`; `RequireAdmin` is the boundary). `value` is JSONB so the store stays general without a schema change per setting. Seeded with `mcp.enabled = false` (the MCP runtime toggle, read at startup). `setup.completed = true` is written by first-run setup in the transaction that creates the first user, and backfilled by migration 248 on every install that already had one, with `updated_at` set by migration 249 to that install's first real user's `created_at`, so the backfill is a function of the data rather than of when it ran; only its absence lets the API mint a bootstrap token (ADR-0105 D7).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `key` | `TEXT` | PK, CHECK (`key <> ''`) | Setting name, e.g. `mcp.enabled`. |
| `value` | `JSONB` | NOT NULL | The setting value (a boolean today, number/object later). |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `updated_by` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who last changed it; SET NULL so the audit column survives a user delete. |

`REVOKE ALL FROM coffer_app`; `GRANT ALL TO coffer_service`.

### `mcp_access_tokens`

MCP bearer tokens for the **Connected apps** surface (ADR-0063 / ADR-0081). Opaque reference tokens — only the `SHA-256` is stored — that scope an MCP client's access; a presented token hashes to a single-row lookup. v1 is read-only (`coffer.read`). Owned by a user; listable / revocable from Account → Security → Connected apps. Added in migration 145. A restore sets `revoked_at` on every token the backup brought back (FU-076).

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

### `oauth_connection_meta`

The owner-chosen name and the last-used time of one **OAuth** connection to `/mcp` (migration 265, [ADR-0107](decisions/0107-connections-are-what-people-hold.md)) — the two things a person needs to tell their connections apart that OpenIddict's own schema does not carry. A personal token needs no counterpart: `mcp_access_tokens` already has `name` and `last_used_at`. Keyed on the OpenIddict authorization, so there is at most one row per connection. A table of our own rather than OpenIddict's `Properties` JSON column because `last_used_at` is stamped on every authenticated `/mcp` request (`McpOAuthUsageMiddleware`, after authorization, so a refused request is not a use) and a stamp into that column is a read-modify-write that loses a concurrent write and races OpenIddict's own updates to the row.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `authorization_id` | `TEXT` | PK, FK → `"OpenIddictAuthorizations"("Id")` ON DELETE CASCADE | Removing an authorization (an admin revoking a whole client registration) takes its row with it, and a stamp or a name for one that does not exist is refused rather than orphaned. A *revoked* authorization keeps its row; it is no longer listed, so nothing reads it. |
| `user_id` | `UUID` | NOT NULL FK → `users(id)` ON DELETE CASCADE | The connection's owner; every write names and matches it. |
| `label` | `TEXT` | CHECK (`label IS NULL OR label <> ''`) | The owner's name for the connection; NULL = none set, and the client's own registered name is shown. A blank name clears it. |
| `last_used_at` | `TIMESTAMPTZ` | | Last authenticated `/mcp` request on this connection. NULL = none recorded: a connection from before the table existed, or one not used since. Not backfilled — a stamped `now()` would claim a use that did not happen. |

RLS enabled + forced, own-user policy (the `mcp_access_tokens` pattern). The stamp and the admin list use the service role with the user named explicitly. A restore leaves the rows in place: it revokes every authorization (FU-076) but does not delete them, so a restored connection keeps its name and reads as revoked.

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

### Tables this document deliberately does not describe

Five tables exist in the schema and have no section here, on purpose:

* `OpenIddictApplications`, `OpenIddictAuthorizations`, `OpenIddictScopes`,
  `OpenIddictTokens` — created and owned by the OpenIddict library's own
  migrations, not by `db/migrations/`. Their shape is the library's to change,
  and documenting a copy here would rot silently on the next package bump. See
  [decisions/0063-mcp-server-and-reporting.md](decisions/0063-mcp-server.md)
  for how they are used. A restore sets every authorization's and token's
  `Status` to `revoked` (FU-076) and keeps the applications, so a connector signs
  in again rather than registering anew. Coffer writes one key of its own into an
  application's `Properties` JSON: `coffer:registered_at`, the registration time,
  set once by `/oauth/register` because the library keeps none
  ([ADR-0113](decisions/0113-a-connection-sits-under-its-app.md) D3).
* `__schema_migrations` — DbUp's own journal of applied scripts. One row per
  migration file; `LedgerSnapshotsRepository` reads its highest entry to stamp a
  snapshot's schema version, which is what makes a restore refuse across a
  migration boundary.

Everything else in `pg_tables` has a section. If you add a table and skip the
docs, this list is where the next person will look before concluding the
omission was deliberate.

### `admin_audit_events`

Durable record of deployment-level administrative actions on key material (migration 191, [decisions/0092-kek-lifecycle-in-the-ui.md](decisions/0092-kek-lifecycle-in-the-ui.md) D2). Once the master KEK became viewable and rotatable from the UI, "who saw the key, and when" had to be answerable after the fact — and until 191 the only record was the application log, which rotates away and isn't queryable. Scope is deliberately administrative-and-global rather than "everything": the two existing audit surfaces don't fit and weren't stretched — `ledger_operations` (ADR-0055) is per-ledger and RLS-scoped, `mcp_tool_invocations` (ADR-0081) is the MCP **write** audit. This table is for actions that belong to the *deployment*. The vocabulary lives in `AdminAuditActions`: `master-key.revealed` / `.rotated` / `.adopted` / `.shown-at-setup`, plus `backup-passphrase.revealed` (migration 192), and the privilege changes (FU-090, no migration): `user.admin-granted` / `.admin-revoked` / `.disabled` / `.enabled`, `member.role-changed` / `.removed`, `invite.issued` / `.revoked`. The setup disclosure is its own action rather than folded into `.revealed`, so an auditor can tell the unavoidable bootstrap showing — which happens once per install and has no fresh-assertion gate, the registration ceremony being the proof — from a deliberate later one.

**Append-only, and deliberately not pruned.** `AdminAuditRepository` exposes append + read and nothing else: a row the party it describes could edit wouldn't be worth writing. `AuditRetentionService` trims `ledger_operations` and `mcp_tool_invocations` on `Api:AuditRetentionDays` because those are high-volume operational records; key access is the opposite — a handful of rows per install, ever, whose whole value is that they are old — so the service explicitly skips this table and says so at the call site.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK `pk_admin_audit_events`, DEFAULT `gen_random_uuid()` | |
| `occurred_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | Indexed DESC (`ix_admin_audit_events_occurred_at`) — newest-first, optionally filtered by `action`, is the only query shape this table has. |
| `action` | `TEXT` | NOT NULL, CHECK `ck_admin_audit_events_action` (`action <> ''`) | Stable event name. The emptiness CHECK is the *only* constraint: the value set is deliberately **not** enumerated in SQL. An enumerated CHECK needs a migration to admit each new string, and an audit log is exactly the kind of table that grows new event types — so the vocabulary lives in the `AdminAuditActions` C# constants instead. This is the repo's position on vocabulary columns; migrations 212, 214 and 216 cite 191 for it. |
| `actor_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who did it. SET NULL, not CASCADE: the event must outlive the account, otherwise removing a user erases the record of what they did. NULL also covers the actor-less case — a `master-key.adopted` row is written during the adoption boot, before anyone is signed in. |
| `detail` | `TEXT` | NULL | Free-text operator context (e.g. "rotated to v2; 3 ledger key(s)"). A `COMMENT ON COLUMN` carries the rule: **never** key material, passphrases, or ciphertext, because any admin can read it back. |

RLS: **enabled and forced with zero policies** — deny-all for `coffer_app` — alongside `REVOKE ALL FROM coffer_app` / `GRANT ALL TO coffer_service`, the same service-role-only posture as `system_settings` / `global_scheduled_jobs` / `drive_sync`. The empty policy set is the design, not an unfinished migration: admin is a deployment-global capability, so `RequireAdmin` is the boundary and the runtime role has no business reading this at all; with no policy RLS denies by default, which keeps the table closed even if a grant to `coffer_app` were ever restored. `AdminAuditRepositoryTests.The_app_role_cannot_read_the_audit` pins the resulting failure mode as the real behaviour — a hard `42501` permission-denied from the revoked grant, not an RLS-filtered empty set. Not ledger-scoped (there is no `ledger_id`, by design — the events belong to the install, not to a book), so it is outside `fn_ledger_snapshot_payload` / `_restore` entirely; per-ledger snapshots neither capture nor clear it, and a ledger rollback must not be able to rewrite the record of who saw the master key. Its durability comes from the whole-DB backup (ADR-0060).

### `drive_sync`

Deployment-wide singleton holding the Google Drive backup-destination config (migration 142, ADR-0062). One row, `id = 1` (CHECK pins it). Service-role only with the same RLS-deny-all posture as `global_scheduled_jobs` — there's no ledger to scope it to. The OAuth blob is sealed under the master KEK (never plaintext) and re-wrapped by a master-KEK rotation (System → Encryption, ADR-0092 D4). ④a populates connect/disconnect + sync status; the `enabled` toggle drives auto-push. The Drive folder MIRRORS the local backup set (ADR-0074) — migration 160 dropped the former Drive-side retention columns, since there is no separate Drive retention.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `SMALLINT` | PK, CHECK (`id = 1`) | Singleton guard. |
| `enabled` | `BOOLEAN` | NOT NULL DEFAULT FALSE | Push-on-backup toggle (consumed in ④b+c). Set true on connect. |
| `oauth_ciphertext` | `BYTEA` | NULL | `client_id` + `client_secret` + `refresh_token` as JSON, **sealed under the master KEK** (ADR-0062 D3). NULL when disconnected. Disconnect revokes the refresh token at Google before clearing it (FU-080); a restore clears the restored token without revoking it, since it may be another install's live one. |
| `folder_id` / `folder_name` | `TEXT` | NULL | The Coffer-owned Drive folder (folder isolation). `folder_name` is `Coffer Backups [install_id]`. |
| `install_id` | `TEXT` | NULL | Stable opaque per-install id (mig 143), set once on first connect and kept across disconnect. Embedded in `folder_name` so two installs sharing one OAuth client + Google account land in **distinct** folders. |
| `connected_email` | `TEXT` | NULL | The connected Google account, for display. |
| `last_sync_at` | `TIMESTAMPTZ` | NULL | Last push attempt, **failures included** — so it is not "when Drive last took a copy". That is the newest `drive_mirrored_backups.mirrored_at`, which `DriveSyncMonitor` reads. |
| `last_sync_status` / `last_sync_error` | `TEXT` | NULL | `ok` / `error` + detail. |
| `configured_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | The admin who connected it. |
| `created_at` / `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `drive_mirrored_backups`

The backups **this install** has put on Google Drive (migration 251, FU-064). The Drive mirror may delete a remote backup only if it is listed here, so a stray, a legacy-extension file or another install's upload is never touched. Even a listed one goes only when it is no longer local, is not pinned, and either the operator deleted it here (`deleted_at`) or retention — applied to the local backups **and** every listed one, not to the local folder alone — would drop it. That last condition is what makes an emptied or swapped backup directory harmless: the other Drive copies still count, and only the genuinely aged ones go. Service-role only (RLS deny-all), same posture as `drive_sync`. Cleared on Drive disconnect and by a restore (`RestoreReset`), because what was uploaded through a connection that is gone is not this install's to delete; the restore drill excludes it with `drive_sync`.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `backup_id` | `TEXT` | PK | The artifact id (the `.cofferbak` filename stem). One row per backup, whatever number of parts it went up as. |
| `mirrored_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | When this install first saw the backup on Drive. The newest one is "when Drive last took a copy", which `DriveSyncMonitor` alerts on (`drive.stale`, 48 h). |
| `deleted_at` | `TIMESTAMPTZ` | NULL | Set by `DELETE /api/admin/backups/{id}`: the operator does not want it, so the Drive copy goes at the next mirror without waiting for retention. NULL = still wanted. |

### `oidc_server_keys`

The OAuth server's signing and encryption keys (migration 253, FU-069), the ones MCP clients' tokens are issued and validated with (ADR-0063 §D2). Singleton, `id = 1` (CHECK pins it); service-role only (RLS deny-all), same posture as `drive_sync`. Both keys are **sealed under the master KEK**, so a backup carries them, a master-key rotation re-wraps them, and post-restore reconciliation deletes a pair sealed under another install's key; the boot after it mints a fresh pair. The start-up check for a key-less boot counts a stored pair as wrapped material, so an install holding one refuses to mint a new master key (ADR-0092 D3). Until this table the keys were plaintext files under `data/openiddict`, outside every backup; the first boot with it moves those files in and deletes them — minting instead for a file that is not a readable key, or for the known pair published in early public snapshots. No migration writes a row: sealing needs the master key, which SQL does not have. `coffer-api reset-oauth-keys` replaces the pair (operations.md, Secrets). The restore drill compares it exactly, except when restoring a backup from a release that kept the files, whose backup has none.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `SMALLINT` | PK, CHECK (`id = 1`) | Singleton guard. |
| `signing_key_ciphertext` | `BYTEA` | NOT NULL | The RSA-2048 signing key as PKCS#1 DER, sealed under the master KEK. Key id `coffer-oidc-sign-1`. |
| `encryption_key_ciphertext` | `BYTEA` | NOT NULL | The 256-bit token-encryption key, sealed under the master KEK. Key id `coffer-oidc-enc-1`. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL | When this pair was stored (moved in, minted, or reset). Set by the API, no default. |

### `backup_settings`

Deployment-wide singleton holding the backup **retention policy** (migration 161, ADR-0074). One row, `id = 1` (CHECK pins it). Service-role only (RLS deny-all), same posture as `drive_sync`. This is the **single source of truth** for retention: it governs local backup pruning (`BackupStore`) AND, transitively, the Google Drive mirror (which just reflects the local set). Admin-editable via `GET/PUT /api/admin/backups/retention`; replaced the former startup-only `ApiOptions` retention config.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `SMALLINT` | PK, CHECK (`id = 1`) | Singleton guard. |
| `retention_daily` / `retention_weekly` / `retention_monthly` | `SMALLINT` | NOT NULL DEFAULT 7 / 8 / 12, CHECK (`retention_daily ≥ 1`, weekly and monthly `≥ 0`; migration 250 — at 0/0/0 a new backup would be deleted right after it is written. `BackupStore` also never prunes the newest backup) | GFS tiers: keep daily backups for N days, then the newest of each week for N weeks, then the newest of each month for N months; older is pruned. |
| `configured_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | The admin who last set it. |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

### `backup_pins`

Pins that exclude a backup artifact from retention: an admin's "never delete" pin (migration 144, ADR-0062 ④b+c) and, since migration 268, the pin an upgrade started from the app puts on the backup it took first (ADR-0110). Keyed by artifact id (the `.cofferbak` filename stem, shared by the local file and its Drive copy), so one pin excludes the artifact from **both** local and Drive retention sweeps. Deployment-global, service-role only (RLS deny-all), same posture as `drive_sync`.

One row per backup. An admin pin on an upgrade-kind backup converts the row to kind `admin`, which exempts it from the upgrade trim. The list's `pinned` flag is true for `admin` rows only; an `upgrade` row shows its `label` instead.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `artifact_id` | `TEXT` | PK | The backup id (`.cofferbak` stem). |
| `pinned_by_user_id` | `UUID` | FK → `users(id)` ON DELETE SET NULL | Who pinned it. For an upgrade pin, the admin who started the upgrade. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |
| `kind` | `TEXT` | NOT NULL DEFAULT `'admin'`, CHECK in (`admin`, `upgrade`) | `admin`: never deleted until unpinned. `upgrade`: held for an upgrade; the newest two are kept, trimmed when an upgrade succeeds, and released at once when the attempt ended before it changed anything. Existing rows are `admin`. Migration 268. |
| `label` | `TEXT` | NULL, CHECK (`label IS NULL OR length(label) <= 200`) | Shown in the backup list for an upgrade pin, such as "before upgrade 0.128.1 to 0.129.0". Migration 268. |

### `upgrade_repo_token`

The token the app reads a **private fork's** releases with (migration 269, ADR-0110). A public repository is read with none. The row is written by the app, not by SQL, because sealing needs the master key. No row means no token. Single row by construction. Service-role only (RLS deny-all).

It is sealed under the master KEK, so it joins the three places a master-sealed value is registered: key rotation re-wraps it, post-restore reconciliation deletes a row this install cannot open (the admin enters the token again), and the key probe counts it. A backup carries it.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `SMALLINT` | PK, DEFAULT 1, CHECK (`id = 1`) | Singleton. |
| `token_ciphertext` | `BYTEA` | NOT NULL | The token, sealed under the master KEK. Never returned by the API. |
| `updated_at` | `TIMESTAMPTZ` | NOT NULL | |
| `updated_by` | `UUID` | NULL | The admin who last stored it. No FK, as the audit trail names the person. |

The three upgrade switches (`upgrade.in_app_enabled`, default on; `upgrade.auto_check`, default off; `upgrade.private_repo`, default off) are rows in `system_settings`. So are two the app keeps for itself, both absent until there is something to keep: `upgrade.last_check`, the last successful release check (version, notes, link, publish date and when it was checked) so a restart shows what was shown before, and `upgrade.announced_version`, the release last announced as available, which makes a release be announced once and gives the all-clear something to clear.

### `ledger_snapshots`

Server-side capped snapshots of the user-curated ledger graph (migration 111, ADR-0037) — the in-place recovery half of the backup design (weekly auto-snaps + manual). Capped at 5 per ledger (auto-evicted first) in `LedgerSnapshotsRepository`, **not** a DB constraint (the eviction rule is nicer in LINQ than SQL). **Where the payload lives depends on the snapshot's format version, and `content` is only the v1 answer.** v1 put gzip JSON in `content`; v2 (migration 179) leaves `content` empty and writes `content_json`; v3 (migration 193) leaves `content_json` NULL and chunks the payload into `ledger_snapshot_parts`, which is what the `ledger_snapshot_parts` section below describes. The API discriminates on `content.Length > 0`. In every version the scope is the same: the in-scope per-ledger tables built by `fn_ledger_snapshot_payload`, with operational state (feed_connections, ledger operations, sessions) and the materialized `txn_header_account_balances` excluded — balances are re-derived on restore.

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
| `content_size_uncompressed` | `integer` | NOT NULL CHECK (`>= 0`) | Length of the payload's JSON text in bytes, recorded at capture. Not what the snapshot occupies, and not what the panel shows: Postgres compresses what it stores, so the panel reads `ledger_snapshot_stored_sizes` (migration 271) instead. |
| `content_json` | `jsonb` | NULL | Migration 179 (format v2): the payload, when the snapshot is v2. NULL on v1 (payload in `content`) and on v3 (payload chunked into `ledger_snapshot_parts`). The API discriminates on `content.Length > 0`; see the format note above. |

Indexes: `idx_ledger_snapshots_ledger_created (ledger_id, created_at DESC)`, `idx_ledger_snapshots_ledger_kind_created (ledger_id, kind, created_at)`. **RLS** since migration 231: `ledger_snapshots_read` (SELECT) and `ledger_snapshots_write`, both keyed `ledger_id IN (SELECT ledger_id FROM user_ledger_grants WHERE user_id = current_app_user_id())`. Since migration 254 (FU-086) the write policy narrows to `role IN ('owner','editor')` as its siblings' do, matching the endpoints' group gate, which requires write for both creating and deleting a snapshot. Without RLS the repository/service layer would be the only gate, and anything reaching the database as `coffer_app` outside it could read the table. Migration 112 later extended the snapshot scope (recurring transactions + splits).

**The size the snapshots panel shows** comes from `ledger_snapshot_stored_sizes(ledger_id)` (migration 271): one row per snapshot of the ledger with the bytes it occupies as stored, compression included. It sums `pg_column_size(content)` over a v3 snapshot's chunks, and reads `content_json` (v2) or `content` (v1) for the older formats. For a value held out of line `pg_column_size` reads the pointer and does not fetch or decompress it, so the call costs little beside the payload it measures. Nothing is written, so it covers every existing snapshot with no backfill. It is **service-role only**, like the chunk table it reads: the endpoints call it through the service role once the caller's access to the ledger is established, as snapshot create does. A backup leaves both snapshot tables' data out, so a snapshot's stored size is not part of a backup's size ([ADR-0062](decisions/0062-google-drive-backup-sync.md)). A chunk under Postgres's out-of-line threshold (about 2 KB) is stored as it is, so for a very small snapshot the stored size can sit above the JSON text's length: it is a measure of storage, not a promise of compression.

**Locks.** Capture and restore each run in a REPEATABLE READ transaction, so each takes its advisory lock on its session before that transaction begins and releases it after it ends: a lock taken inside would come after the transaction's snapshot. A snapshot create holds `snapshot_ledger_lock` (migration 247), so two creates of one ledger take turns and cannot both pass the five-snapshot cap. A restore rewrites the ledger and rebuilds its balances (`fn_recompute_balances_for_ledger`), so it holds the ledger write lock's session form (`fn_ledger_write_session_lock`, migration 275): a restore and every writer of the ledger that can recompute it take turns, and inside the restore the transaction form does nothing. Writers never take the snapshot-create lock, and a create never takes the ledger write lock, so a create and a write do not wait for each other. When a session holds both, the snapshot-create lock comes first: `snapshot_ledger_lock` refuses a session that holds the same ledger's write lock ([ADR-0112](decisions/0112-one-write-lock-per-ledger.md) D3). A write that holds the ledger write lock past the restore's wait refuses the restore with 422 `ledger-write-busy` before anything is rewritten.

### `ledger_snapshot_parts`

The v3 snapshot payload, stored in pieces: one row per (snapshot, source table, chunk of rows), added in migration 193 under the same decision as v2, [decisions/0087-snapshot-payload-server-side.md](decisions/0087-snapshot-payload-server-side.md). It has no ADR of its own because the decision did not change — the payload still never enters managed memory — only its shape did. The v2 shape (migration 179) keeps the payload out of the API process but not out of Postgres: `fn_ledger_snapshot_payload` builds a single `jsonb_build_object` over one `jsonb_agg` subquery per captured table, the v2 writer assigns that into a plpgsql variable, then renders the whole document to text purely to count its bytes. That holds several copies of the payload in backend memory at once, so a snapshot's peak is a multiple of its own size and grows with the ledger. Once it passes the container's memory limit the kernel OOM-kills the backend; the postmaster terminates every other backend and enters crash recovery, and the scheduler cannot persist `next_run_at` over the killed connection, so a daily job becomes a crash loop that reruns on every 15-minute tick, and every other job that needs the database meanwhile, the nightly whole-DB backup included, goes down with it.

Capture writes one table's rows here in chunks of 2000 (`fn_snapshot_write_part`) and restore replays them in `seq` order, one chunk in flight (`fn_snapshot_restore_part`), so peak memory is a function of chunk size and row width and stays flat as the ledger grows — the property the single-document form did not have. The chunking key is each table's **primary key, whatever its shape**, read from the catalog (migration 264; migration 197 had keyed on a column named `id` and corrected 193's `ctid` keyset, whose bound parameter never got a TID range scan, so every chunk sorted the whole table). Each chunk resumes strictly after the last key the previous one wrote, compared as a row value, so a composite key such as `txn_leg_note_tags (leg_id, tag_id)` resumes inside one parent's rows and every chunk but the last holds exactly the chunk size. Until 264 the tables without an `id` column (`txn_header_tags`, `txn_leg_tags`, `txn_leg_recon`, `txn_header_originals`, `loan_terms`, `account_folder_members`) were each written as one aggregate under a 500k-row ceiling; that path and its ceiling are gone, and a captured table without a primary key is refused (`SchemaDriftGuardTests` holds every captured table to having one, and captures at one row per chunk to prove each is chunked). **Chunks of one table are separate INSERTs**, so a reference from a row to another row of the SAME table can point into a later chunk; every such FK is DEFERRABLE INITIALLY DEFERRED (migrations 124 and 247) and the restore functions end with `SET CONSTRAINTS ALL IMMEDIATE`, so a violation raises inside the restore rather than at the caller's COMMIT. Chunk size is `ledger_snapshot_write`'s `p_chunk_rows` (default 2000); `SchemaDriftGuardTests` captures at 1 to put every row in its own chunk. The first chunk has no lower bound (migration 247 had started it AT the all-zero uuid, after mig 197 had started strictly after it and skipped a row with that id), and each table's written rows are counted against the live count in the same REPEATABLE READ snapshot, which capture and restore both require (`fn_snapshot_require_consistent_read`). The presence of any row for a snapshot **is** the v3 format gate — v3 leaves `ledger_snapshots.content_json` NULL, and existing v2 snapshots keep restoring through the unchanged v2 branch rather than being rewritten in place. There is no EF entity: the table is written and read only by the migration-193 functions, so it appears nowhere in `AppDbContext`, and the API discriminates formats on `content.Length > 0` (v1 only) without ever projecting a payload.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `snapshot_id` | `uuid` | NOT NULL; PK part 1; FK → `ledger_snapshots(id)` ON DELETE CASCADE | CASCADE is what keeps the 5-per-ledger eviction in `LedgerSnapshotsRepository` a single delete of the metadata row — the parts go with it, and no API code needs to know this table exists. |
| `part_name` | `text` | NOT NULL; PK part 2 | The source table name, one of `fn_ledger_snapshot_part_names()`. Deliberately unconstrained — no CHECK, no catalog FK — and safe because restore drives from `fn_ledger_snapshot_insert_order()` and *filters* on this value; a stored name is never interpolated as an identifier. |
| `seq` | `integer` | NOT NULL; PK part 3 | 0-based chunk index within `(snapshot_id, part_name)`. `seq = 0` is written even for an empty table, so the key exists as `'[]'`: migration 188's restore assertion distinguishes an absent key from an empty one, and skipping the write would drop the key for any ledger with an empty in-scope table. |
| `content` | `jsonb` | NOT NULL | Always a jsonb **array** of that chunk's rows, never an object and never NULL. jsonb rather than text so Postgres TOAST-compresses it on disk — which is what replaced the v1 hand-rolled gzip. The text length recorded at capture sums per-chunk `octet_length`, so it runs a few bytes per chunk above the v2 number for the same data; it is not a checksum, and not what the panel shows (see the size note under `ledger_snapshots`). |

Indexes: the primary key `(snapshot_id, part_name, seq)` is the only one, and it covers both access paths exactly — "any parts for this snapshot?" (the format gate) and "this table's chunks in order" (the restore loop). **Service-role only** since migration 254 (FU-086): RLS forced, no `coffer_app` policy and no `coffer_app` privilege. A part holds every member's rows of the ledger, private ones included (report pins, private saved reports), and nothing on the request path reads one: capture and restore run as the service role, and deleting a snapshot reaches its parts through the foreign key's cascade, which runs as the table's owner. Migration 231 had given it RLS with `_read` / `_write` policies joined through the parent, which let any member of the ledger read every other member's private rows out of a part. Not captured by `fn_ledger_snapshot_payload` / `_part_names` — it *is* the snapshot storage, and a snapshot cannot contain itself. It is on `SchemaDriftGuardTests`' exclusion list beside `ledger_snapshots`: since migration 247 the guard treats a table as ledger-scoped when it reaches a ledger-scoped table through a foreign key, not only when it carries `ledger_id`.

### `account_folders`

The sidebar's folders (migration 258, FU-006): a named grouping of real accounts of ONE account type, shared by every member of the ledger, nesting to any depth. A folder sits inside its type's section of the sidebar and holds sub-folders and accounts of that type only. It holds no transactions and has no balance of its own: the sidebar shows the sum of every account under it, which the SPA computes from the balances read. Folders are sidebar-only; pickers, the Accounts tab of Structure and the dashboard still group by type alone. Migration 258 replaced the per-user sidebar tabs (`user_account_groups` and `user_account_group_members`, migration 033), which were filters over the account list, not data, and were dropped with it.

A table of its own rather than an `accounts` row: a folder modelled as an account (an `is_folder` flag) would appear in every picker, register and report that lists accounts, each needing a filter it does not have.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `id` | `UUID` | PK DEFAULT `gen_random_uuid()` | The primary key, which the snapshot writer chunks on. |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE CASCADE | |
| `account_type` | `TEXT` | NOT NULL CHECK in (`bank`, `credit_card`, `investment`, `asset`, `liability`, `loan`) | The one type the folder holds: the real account types of the `accounts` CHECK, never `category` (the category tree is `accounts.parent_id`). |
| `name` | `TEXT` | NOT NULL CHECK `btrim(name) <> ''` and `char_length(name) <= 80` | The API stores it trimmed. Unique among its siblings, ignoring case (index below). |
| `parent_id` | `UUID` | composite FK `(parent_id, ledger_id, account_type)` → `account_folders(id, ledger_id, account_type)` ON DELETE CASCADE, DEFERRABLE INITIALLY DEFERRED (`fk_account_folders_parent`) | NULL = the top level of the type's section. Composing on the type makes "a sub-folder has its parent's type and ledger" a database fact. Deferred for the chunked-restore reason `accounts.parent_id` is: a sub-folder can sit in an earlier chunk than its parent. The cascade fires only for a whole-ledger delete or a snapshot clear, where every folder goes anyway: the API's delete moves a folder's contents up to its parent first, in the same transaction. |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

Constraints/indexes: UNIQUE `uq_account_folders_id_ledger_type (id, ledger_id, account_type)`, the target of the parent and member references; UNIQUE index `uq_account_folders_sibling_name (ledger_id, account_type, parent_id, lower(name)) NULLS NOT DISTINCT`, so two top-level folders of one type collide as well. The API checks the type and sibling-name rules first and answers with business errors; the database is the backstop for those. **No cycles** is the API's rule alone (ADR-0032; ADR-0106, 2026-10-22 amendment): no key can state it, and migration 261 dropped the trigger migration 258 added for it (`trg_account_folders_refuse_cycle`, `fn_account_folders_refuse_cycle`). `AccountFoldersRepository.MoveAsync`, the only write that names a new parent for an existing folder, locks every folder of the type in the ledger before it walks the new parent's ancestors, so two concurrent moves cannot together make a cycle; the delete, which moves sub-folders up, takes the same lock. A direct SQL write is not checked. **RLS** `account_folders_read` (any grant) and `account_folders_write` (`role IN ('owner','editor')`), the migration 225 pair: folders are ledger data, not per-user view state. **Captured** by the snapshot functions (after `holdings`, before `account_folder_members`), and cleared explicitly by `fn_ledger_snapshot_clear` and `fn_ledger_delete`: deleting `accounts` cascades only the members, and a folder left behind would sit beside its restored copy.

### `account_folder_members`

Which folder an account sits in directly (migration 258). One folder per account at most; an account in no folder has no row. Only real, non-system accounts are filed (the API refuses categories and Holdings siblings). Deactivating an account keeps its row, so it keeps its place.

| Column | Type | Constraints / FK | Notes |
|---|---|---|---|
| `account_id` | `UUID` | PK; composite FK `(account_id, ledger_id, account_type)` → `accounts(id, ledger_id, account_type)` ON DELETE CASCADE (`fk_account_folder_members_account`) | The primary key is the one-folder-per-account rule. CASCADE for the reason `budget_targets` gives: snapshot clear and ledger delete delete every `accounts` row. |
| `ledger_id` | `UUID` | NOT NULL | Part of both composite references. |
| `folder_id` | `UUID` | NOT NULL; composite FK `(folder_id, ledger_id, account_type)` → `account_folders(id, ledger_id, account_type)` ON DELETE CASCADE (`fk_account_folder_members_folder`) | |
| `account_type` | `TEXT` | NOT NULL | The column both references share, which is what makes "the account's type is the folder's type" true by construction. |

Index `ix_account_folder_members_folder (folder_id, ledger_id, account_type)` (a folder's members, and the folder reference's lookups). **RLS** `account_folder_members_read` / `account_folder_members_write`, the same pair as `account_folders`. **Captured** after `accounts` and `account_folders`, chunked on its primary key like every captured table (migration 264; see `ledger_snapshot_parts`).

### `tags`

Tags attach at transaction level (`txn_header_tags`, on a multi-split
and a multi-posting investment event only) and per side
(`txn_leg_tags`). What a row shows and counts is stored in
`txn_leg_note_tags`. All three are documented above with the other
transaction tables.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `UUID` | PK | |
| `ledger_id` | `UUID` | NOT NULL FK → `ledgers(id)` ON DELETE RESTRICT | Phase A anchor (ADR-0020). |
| `name` | `TEXT` | NOT NULL | UNIQUE per ledger via `uq_tags_name_per_ledger` (exact) and, since migration 264, `uq_tags_name_key_per_ledger` on `(ledger_id, tag_name_key(name))`: two names that differ only by case or surrounding white space are one tag. 264 merged the case variants a ledger held into the earliest created (then the lowest id), moving their links and repointing every saved report filtered on a merged variant, before creating the index. The API's dictionary re-reads a name whose save the index refused (another writer created it meanwhile) and saves once more, a rename it refuses is `tag-name-exists`, and the importer's insert conflicts on it. The pre-Phase-A global UNIQUE on `name` is dropped by migration 014. |
| `color` | `TEXT` | | Optional UI hint |
| `created_at` | `TIMESTAMPTZ` | NOT NULL DEFAULT now() | |

---

## Views

### `resolved_transactions`

Register and listing rows read this view; the tags a row shows and
counts are read from `txn_leg_note_tags`. Projects from
`live_txn_headers` + `txn_legs` (ADR-0022; rewritten by migration 023)
directly — the header/leg override layer it used to COALESCE through was
retired by migration 230 (ADR-0100). The column shape is migration 266's
(see the note below the table), plus the six columns migration 301 (ADR-0114 D13, D15) appends, mapped by the EF
`ResolvedTransactionView` entity.

Key column derivations:

| Column | Source |
|---|---|
| `id`, `account_id` | `txn_legs` (one row per leg) |
| `investment_action` | `h.action` — the header's action (a header property since migration 047) |
| `balance_after` | `txn_header_account_balances` joined on `(header_id, account_id)` (ADR-0034 / mig 091). Same value for every leg of `(account, header)`. |
| `payee`, `posted_at`, `transacted_at`, `check_number`, `external_id` | `txn_headers`, directly — these columns ARE the current values since mig 230 |
| `status` | `COALESCE(txn_leg_recon.status, 'uncleared')` — the per-leg recon overlay (migration 171, ADR-0082). Per-account: each leg reconciles independently, so a transfer can be cleared on one side and uncleared on the other. A leg with no overlay row reads `uncleared`. |
| `cleared_at`, `cleared_by_user_id` | `txn_leg_recon` (the leg's overlay row) — paired with `status` via the DB CHECK `(status='cleared') ⇔ (cleared_at IS NOT NULL)` |
| `amount` | `txn_legs.amount` |
| `is_hidden` | `COALESCE(header.is_hidden, FALSE)` |
| `has_overrides` | `txn_header_originals.header_id IS NOT NULL` — the row has an original on file: it was edited in Coffer, or it is a Moneydance row whose feed payee or memo differs from the curated one (the importer seeds those; ADR-0100, 2026-10-21 amendment), or an import rounded its amount and seeded it (ADR-0114 D15; ADR-0100, 2026-11-16 amendment); no reader of this column is affected by the last. Name kept across migration 230 so the view's column shape did not move; the question is the same one, answered from the other side, and it now catches investment edits, which the old override-row test missed entirely. Kept on the view; no API response carries it, since nothing in the SPA or MCP read it. |
| `original_amount`, `amount_rounded_at_import` | `orig.amount` and `COALESCE(orig.rounded_at_import, false)`, through the same `LEFT JOIN orig` (migration 301, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)); the same on every leg of the header, NULL and false where the header has no originals. Read onto the register row for the Review marker, which shows the bank's figure while the row needs review and the flag is set (settled by Claude, 2026-11-16). |
| `review_cleared_amount`, `review_cleared_by_match` | `header.review_cleared_amount` and `header.review_cleared_by_match` (migration 301, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15); the same on every leg of the header. Read onto the register row as `reviewClearedAmount` (text, exactly as sent) and `reviewClearedByMatch` for the Review marker, which says while the row needs review that the bank cleared it, or a matched transaction, at that figure, in place of the rounded note. |
| `counterparty_id`, `counterparty_account_id`, `counterparty_account_name`, `counterparty_account_type` | Structural via `LEFT JOIN txn_legs other ON other.header_id = leg.header_id AND other.posting_index = leg.posting_index AND other.id != leg.id` — finds the other side of the posting. `counterparty_account_name` uses the recursive `account_path()` function for the full root-to-leaf category path. |
| `counterparty_amount`, `counterparty_currency_code` | `other.amount` and the other leg's account's `currency_code` (`LEFT JOIN accounts ca ON ca.id = other.account_id`), through the same `LEFT JOIN other` (migration 301, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D13). The register row carries them (`CounterpartyAmount` and `CounterpartyCurrencyCode`, and `TransferAmount` and `TransferCurrencyCode` on an investment row's transfer slot) only on a transfer between real accounts in different currencies (ADR-0114 D14, "The register row"); on a category row, a row whose other side is a category (which mirrors), a row with no other side and a transfer in one currency they are dropped. |
| `txn_group_id` | `CASE WHEN l.header_total_postings > 1 THEN h.id ELSE NULL END` — emits `header.id` only when the header has multiple postings (preserves the pre-ADR-0022 grouping semantics expected by the API's AssembleEntries). |
| `leg_index` | `txn_legs.posting_index` |
| `header_id` | `txn_headers.id` (added in migration 028 — unconditional header identity, used by the SPA's inline-edit flow which targets a header even for single-row entries where `txn_group_id` is NULL). |
| `created_at` | `txn_legs.created_at` — leg insertion timestamp; all legs of a single header share this value (one transaction, one `now()`). |
| `notes_kind` … `notes_header_in_play` | The row's own leg's stored memo-and-tags presentation (`txn_legs.notes_*`, migration 264): what the row shows, as its own account's register shows it. |
| `counterparty_notes_kind` … `counterparty_notes_header_in_play` | The same ten columns of the posting's other leg (`other`), so a reader that presents a line from that leg's account (a report, a listing line) reads it with no join of its own. |

The view has **no memo or tag column of its own**. Migration 266 dropped `memo` (`COALESCE(leg_memo, header memo)`), `tags` (the header's tags on every leg), `leg_memo` and `header_memo`, and `memo` from `live_txn_headers` and `template_txn_headers`: they were a second, different definition of what a row shows, and every reader reads the stored presentation instead (`StoredNotesQuery`; the tag names from `txn_leg_note_tags`). The raw inputs stay on `txn_headers`, `txn_legs` and the two tag tables, read by the writers and the recompute only. The three views were dropped and recreated, since a column cannot be dropped in place; before dropping anything, 266 refuses the upgrade, naming each, if a function body names one of the views together with a dropped column (a string-bodied function is not a tracked dependent and would fail only when called).

Migration 301 dropped and recreated it again, with `live_txn_headers`, `template_txn_headers` and `balance_walk_headers`, to lift the scale limit on `txn_headers.ingest_amount` and `ingest_fee` (a column a view reads cannot change type); each came back with its last body and the same columns in the same order, this view with the six columns 301 appends and `live_txn_headers` with the two review columns, `review_cleared_amount` and `review_cleared_by_match`, appended.

Marked `security_invoker = true` so RLS evaluates as the caller's role (`coffer_app`), not the view owner's; `coffer_app` holds SELECT only (re-granted by 266 and again by 301).

See `db/migrations/301_each_currency_keeps_its_minor_units.sql` for the current SQL.

### `live_txn_headers`

`txn_headers WHERE NOT is_recurring_template` (migration 124, ADR-0048): the live events, so no read surface sees a reminder's template header. `resolved_transactions` projects from it, and the SQL balance and holdings recomputes read it. Its columns are `txn_headers`' except `memo` (dropped by migration 266 with the view's other memo columns), `originating_account_id` and `ledger_operation_id`, which it has never exposed. `security_invoker = true`, and `coffer_app` holds SELECT only (both re-applied by 266 and by 301, which each recreated the view).

### `template_txn_headers`

`txn_headers WHERE is_recurring_template` (migration 124): the reminder templates. The same columns as `live_txn_headers` without `ingest_amount`, `review_cleared_amount` and `review_cleared_by_match`, and the same `security_invoker` setting and grant (migrations 266 and 301).

### `balance_walk_headers`

`live_txn_headers WHERE is_merged_into IS NULL AND NOT is_hidden` (migration 277): the headers the balance walk counts, those that carry a running balance. Columns `id`, `ledger_id`, `posted_at`, `seq`. `balance_walk` reads its headers from it, and `fn_recompute_balances_for_account` its seed, so the walk and the seed cannot disagree about which headers count. `security_invoker = true`, and `coffer_app` holds SELECT only. Migration 301 recreated it with the same body and comment, because it reads `live_txn_headers`, which 301 recreated.

---

## Functions

The API uses Postgres-side functions for paths whose shape doesn't fit
cleanly in LINQ — read paths and set-based recomputes both. **Forty-six** are
bound to EF via `HasDbFunction` in `AppDbContext.OnModelCreating`; the C#
method on `AppDbContext` is a translation anchor only — its body is
never executed. Three are documented in full below and the others are
tabulated after them, followed by the ones only SQL calls.

The register's keyset page and its filter were the SQL functions
`register_entry_keys` and `register_filtered_entries` (migrations 019 to 226)
until migration 266 dropped them: they keyed and filtered entries on the view's
own memo and tag columns, which 266 dropped too. The register reads the stored
presentation through `RegisterQuery` (LINQ over `resolved_transactions`), which
is the one filter definition (ADR-0076) the page, the rail buckets, the status
counts and a filtered select-all share; `RegisterEntryKeyTests` holds every
register, filter and sort to the rule run over the loaded rows.

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
`/`-joined string (e.g. `Food/Groceries/Produce`). Used by the
`resolved_transactions` view to render `counterparty_account_name`
without the consumer having to walk `accounts.parent_id` themselves.
Defined in migration 020.

### `security_price_upsert_from_trade(p_ledger_id, p_security_id, p_day, p_price, p_currency_code)`

Rank-gated upsert of a `trade`-source closing price for a `(security, UTC-day)`
(migration 177, ADR-0084; **274** added `p_currency_code` and dropped the
four-argument form, which wrote `USD` whatever the brokerage's currency). Unlike the
read-path functions above this one is `VOLATILE` and WRITES — it is the sibling of
`recompute_holdings_for_account_security`. Every writer but the trade-price repair
reaches it through `fn_trade_price_reseed` (`TradePriceRecomputeService`), which passes
the price and currency the rule names: `TradePriceFromLegInterceptor` for native API
and MCP writes, the writers that bypass the change tracker (the bulk delete and
restore, `merge_securities`), and a brokerage's currency edit. The trade-price repair
calls it directly (via `HasDbFunction` / `AppDbContext.SecurityPriceUpsertFromTrade(...)`)
with the figures the check derived. The `holdings_currency` repair writes no price. The
Moneydance importer states the same upsert in its own SQL (`TradePriceSeedStep`, below).
A function call (not a trigger, ADR-0032) keeps the conflict SQL out of the app layer
and can't re-fire the interceptors.

| Parameter | Meaning |
|---|---|
| `p_ledger_id` (UUID) | Ledger scope stamped on the inserted row |
| `p_security_id` (UUID) | Security to price; also the single echoed return column |
| `p_day` (DATE) | The trade's UTC calendar day = `price_date` (ADR-0084 D3) |
| `p_price` (NUMERIC) | The per-share execution price; `NULL`/`<= 0` is a no-op |
| `p_currency_code` (TEXT) | The currency of the brokerage that made the day's trade (migration 274); `NULL` fails the column's NOT NULL rather than storing a default |

Behaviour: `INSERT ... ON CONFLICT (security_id, price_date) DO UPDATE` gated by
`WHERE security_prices.source IN ('import','simplefin','trade')`, so a `trade`
inserts on an empty day and overwrites only lower-or-equal-ranked rows — a truer
`fetch`/`manual` close for the day is never clobbered (rank ladder: `manual` ==
`fetch` > `trade` > `simplefin` > `import`, ADR-0084 D1). Returns the input
`security_id` so EF has a typed projection; callers discard it. The Moneydance
importer (Dapper, so the interceptor never sees its writes) seeds its own ledger's
days in `TradePriceSeedStep`, below. Migration 177's backfill was a one-time pass
over the trade legs that existed when it ran, under the rule of its day: the last
trade of each `(security, UTC-day)` by `seq`, any trade-shaped leg of a header that
is not a template, with neither D6's execution allow-list nor the hidden and merged
filter. Nothing runs it again.

**The rule lives in `fn_trade_execution_for_day` (migration 274; before it
`fn_trade_price_for_day`, migration 234, which 274 drops), and the writer calls
it.** It picks from the day's executions (`fn_trade_day_executions`) and names the
picked execution's price and the currency of the brokerage that made the trade
(`fn_trade_currency`). A writer that upserts the price of whatever leg a save touched,
rather than applying the rule, disagrees with the check on a multi-leg day:
correcting one leg seeds that leg's price, while `fn_trade_price_check` —
applying "last trade of the day" — derives a different one and reports the
difference as drift. Neither side is buggy on its own terms; they run different
rules. So `TradePriceFromLegInterceptor` names the (security, day) it touched
and `fn_trade_price_reseed` decides the price.

**The day a trade leaves is named too** (ADR-0084 D4, 2026-11-03 amendment). A
delete, a re-date, an action change, or a line's security, quantity or unit price
changing takes an execution off the day it priced; the interceptor reads those
transactions' trade lines before the save and re-derives each day they sat on
after it. The day then takes the price of the executions that remain, rank-gated;
with none, `fn_trade_price_reseed` derives nothing and the stored row is kept. The
bulk delete and `merge_securities`, which bypass the change tracker, re-derive
the days of the executions they delete or move through the same service.

**A hidden or merged-away row is no trade** (migration 273, ADR-0084 D4, 2026-11-04
amendment), for the day's price as for valuation (`fn_last_trade_price`): the rule
reads the headers through `live_txn_headers` with `is_hidden = FALSE AND
is_merged_into IS NULL`. So hiding a trade (the delete of an imported row), restoring
it, merging it into another and un-merging it (its winner deleted) each move it off or
onto its day. The interceptor counts a changed `is_hidden` or `is_merged_into` as a
change to every trade line of the header; the bulk delete re-derives the days of the
rows it hides and of the rows its delete un-merges, and the bulk restore the days of
the rows it unhides.

**A trade price is in the currency of the brokerage that made the trade** (migration
274, ADR-0084 D2, 2026-11-04 amendment). The picked execution's line sits on its
brokerage's Holdings sibling; `fn_trade_currency` reads the `currency_code` of the
brokerage that names that sibling (`holdings_account_id`), of a line's own account when
no brokerage names it, and, when several brokerages name one sibling and disagree, the
first code in ordinal order, as the `holdings_currency` check expects. The sibling's own
currency is never read: while a sibling still holds another code, its trades are priced,
and checked, in the brokerage's currency, the one the account editor shows and the one
the `holdings_currency` repair sets the sibling to. That repair writes no price. One
security traded in two brokerages on one day takes the currency of the brokerage whose
execution the rule picks. A brokerage's currency edit re-derives the day of every trade
on it and on its sibling, whatever the sibling held.

**The lock order.** Every path that writes a trade price into a committed ledger, or a
currency the rule reads, takes its locks in one order, all before it reads what it writes
from: the API's writers, native and MCP. The importer's `TradePriceSeedStep` is outside
it: it writes only into the ledger its own transaction creates, which no other session can
see until that transaction commits, so no other path can run against it.

Before step 0, every one of these paths that can recompute balances or holdings
(`merge_securities` among them), a currency edit, a re-derivation that opens its own
transaction, and every consistency repair, the trade-price and `holdings_currency` repairs
included, takes the ledger write lock (`fn_ledger_write_lock`, migration 275,
[ADR-0112](decisions/0112-one-write-lock-per-ledger.md)) as its transaction's first
statement. Two transactions that take it are never past it at once in one ledger.
`merge_securities` therefore holds the ledger write lock before it takes step 0's lock on
the source. A re-derivation run in a caller's transaction that recomputes neither balances
nor holdings takes none (`TradePriceRecomputeService` leaves it to the caller); steps 1 to
3 keep it from a cycle, so they keep their order.

0. `merge_securities` only: the source security's row, `FOR UPDATE`
   (`fn_lock_securities`), before it reads which lines are the source's. A line, holdings
   row or price row another request adds for the source takes `KEY SHARE` on that row,
   which `FOR UPDATE` excludes: one in flight is waited for and then read and moved, and
   one that starts later waits for the merge.
1. The accounts whose currency the rule reads for the (security, day) pairs the path
   derives (`fn_trade_price_accounts`), with the account a currency edit writes, in id
   order, in one statement (`fn_lock_trade_price_accounts`). A writer that derives
   several days locks the accounts of all of them in that statement
   (`TradePriceRecomputeService`, inside a transaction of its own when its caller has
   none). `FOR SHARE` for a re-derivation (a trade's save; a hide or merge; the
   bulk delete and restore; `merge_securities`; a currency edit's own re-derivation) and
   for the `holdings_currency` repair, which reads its brokerages' currencies; `FOR NO
   KEY UPDATE` for a brokerage's currency edit and for the trade-price repair, which
   writes the figures the check derived and so must not read past a re-derivation in
   flight.
2. A brokerage's Holdings sibling row, which a currency edit and the `holdings_currency`
   repair update once its brokerage is locked. No trade-price path locks a sibling.
3. `security_prices` rows, through the rank-gated upsert, in (ledger, security, day)
   order (`TradePriceRecomputeService.InWriteOrder`) by each path that writes more than
   one: the re-derivation and the trade-price repair.

The read starts after the locks are granted, and the set of accounts is read again with
it: one that joined in between (an execution of the day another request committed on
another brokerage) is locked and the read repeated (`TradePriceLocks`, and
`fn_trade_price_reseed`'s own loop). `FOR SHARE` conflicts with `FOR NO KEY UPDATE` and
with an `UPDATE` of the account, not with `FOR SHARE`, so a re-derivation waits for a
currency edit or a trade-price repair in flight, they wait for it, and re-derivations do
not wait for each other at step 1. The `holdings_currency` repair and a re-derivation take
no lock the other holds, so neither waits for the other. Neither mode conflicts with the
`KEY SHARE` a new line's foreign key takes, so no trade's insert waits on these locks. Each
upsert holds its price row until its transaction ends, so re-derivations of the same days
meet at step 3, and the one order keeps them from each holding a row the other waits for.

The ledger write lock supersedes what that paragraph says about not waiting, within one
ledger, for the paths that take it: two re-derivations that each take it, or such a
re-derivation and the `holdings_currency` repair, take turns at it before either reaches
step 1. For a re-derivation in a caller's transaction that recomputes neither balances nor
holdings, that paragraph stands.

Each path takes all its account locks in one id-ordered statement before any price row,
and its price rows in one order, so that no two paths hold an account or a price row the
other waits for. With the ledger write lock first, the deadlocks and narrow timing windows
the steps from 0 on would leave between two paths of one ledger (a security merge racing a
re-dated trade, two saves re-deriving the same day, either of which could leave a trade
price stale) cannot form. A transaction outside this order that row-locks the same
accounts without the ledger write lock can still deadlock with one of them. Postgres then
cancels one request, which writes nothing, and a retry succeeds. That is an accepted
limit.

`TradePriceCurrencyConcurrencyTests` holds these pairs to the order: a currency edit and
each repair against a trade, in both orders; each repair against a currency edit, in both
orders; two brokerages' currency edits over one day; a multi-day re-derivation in a
transaction that took no ledger write lock against the trade-price repair; two
re-derivations of the same days named in opposite orders, at the ledger write lock when
each opens its own transaction and at the price rows when neither takes the lock; a
currency edit sent while an edit of the brokerage's other fields is between its read and
its update; and a trade on the source against `merge_securities`. Where both sides take
the ledger write lock, each test asks that the later one waited there.

Which executions are trades, the header filter and `fn_leg_prices_a_day`, is
stated by hand in four places beside the rule, deliberately, and a change to it
changes all four:

- `fn_trade_price_check`, the checker. It re-states the rule rather than calling
  it so that it can catch the WRITERS diverging — a checker that reuses the
  writer's code cannot catch the writer being wrong. That has a matching blind
  spot, recorded in FU-048: when the RULE is wrong, both sides compute the same
  wrong answer and the check reports healthy. Since migration 274 it also reads the
  currency of the brokerage that made the picked trade (`fn_trade_currency`, the rule's)
  and compares the stored row's currency with it.
- `fn_unbacked_price_dates`, the advisory's dates in one pass (migration 241),
  because asking the rule once per stored price costs prices × legs. The price
  list reads it for one security and, since migration 273, the advisory for each.
- The importer's `TradePriceSeedStep`, set-based in Dapper, because the importer
  never touches EF; since migration 273 it states the whole rule, and since 274 it
  writes the currency of the brokerage that made the trade, read through
  `fn_trade_currency`.
- `fn_last_trade_price` (migration 259), the same trades read as of an instant
  rather than for one day: the valuations' price for a position with no
  `security_prices` row.

### The rest of the bound functions

Forty-six functions are bound via `HasDbFunction`; the three above (two bound, plus the
SQL-only `account_path`) are documented in full because their signatures are contracts
callers have to match. The
remainder are listed rather than expanded — each is a single-purpose call whose
shape lives in its migration — so that this section answers "what runs in the
database" completely rather than partially. Four rows sit beside the bound functions
they serve but are SQL-only, marked so in their notes.

| Function | What it does | Notes |
|---|---|---|
| `recompute_balances_for_account(account_id, from_posted_at)` | Rebuilds `txn_header_account_balances` for one account from the first affected header forward. | The canonical balance algorithm. Migration 102 dropped the trigger family that used to call it; the `LegDerivedRecomputeInterceptor` calls it now, and bulk paths that bypass the change tracker call `LegDerivedRecomputeService` explicitly, which calls it once per account. Since migration 275 it takes the account's ledger write lock before it reads, and is refused in a transaction that wrote without it (see `txn_header_account_balances`). Since migration 277 it clears every stored row in its window, on any header, and seeds only from a running balance the walk writes (a header in `balance_walk_headers` with a line on the account), so a stored row the walk does not produce neither survives a recompute that covers it nor seeds one. |
| `account_balance_walk(account_id, …)` | The pure walk — computes what the balance SHOULD be without writing. | Migration 206 split this out of the recompute so the drift check could be a genuine read; since migration 211 a thin wrapper over `balance_walk`, the one implementation, which since migration 277 reads its headers from `balance_walk_headers`. The `balances` projection of `POST .../balances/consistency` uses it: a stored row it does not produce is drift too, reason `extra`. `POST .../balances/consistency/balances/repair` is what persists. |
| `account_balance_as_of_instants(…)` | Account balances at a set of instants, in one round trip. | Feeds net-worth-over-time and the as-of reporting surfaces, and prices a managed loan payment's split at its own `posted_at` (ADR-0078). |
| `recompute_holdings_for_brokerage(account_id)` | Rebuilds `holdings` + `lots` for every security in one brokerage. | Called by the trade-commission flip (`AccountsRepository.SetIsTradeCommissionAsync`), in the one transaction that writes the flag, opened with the ledger write lock. Since migration 275 it takes the brokerage's ledger write lock before it reads, and is refused in a transaction that wrote without it (see `realized_gains`). |
| `recompute_holdings_for_account_security(account_id, security_id)` | The narrow version — one `(account, security)` pair. | Used when the affected pair is known, which is the common case: `HoldingsRecomputeService`, behind `HoldingsRecomputeInterceptor` and the explicit callers, calls it once per pair. Since migration 275 it takes the position's ledger write lock before it reads or creates it, and is refused in a transaction that wrote without it (see `realized_gains`). |
| `holdings_cost_basis_as_of(…)` | FIFO cost basis for a position at an instant. | Rewritten by migration 229 to order by the EFFECTIVE posted date, then again by 230 when effective and raw collapsed into one column. |
| `holdings_market_value_as_of_set(…)` | Market value for a set of positions at an instant: the latest close on or before the instant's date, else `fn_last_trade_price`, back-adjusted onto the instant's split basis; with neither, the position's FIFO cost basis (`priced_from = 'none'`). `price_currency` is the currency of the price used: the close's `currency_code`, else the trade's; NULL for a position carried at cost basis, which is in its account's currency. | Same 229 / 230 history as the cost-basis walk — they had to move together or a row would sort one way on screen and another in the FIFO walk. Migration 259 replaced its two no-price tiers with the one rule every valuation shares (ADR-0084, 2026-10-21 amendment): tier 2 had been the last `unit_price` of any leg on the same (account, security), share movements included, and tier 3 was zero. **Migration 281** added `price_currency` (DROP + CREATE, since the output columns changed; the body is 259's with the column beside the price), so the net-worth history converts a position from its price's currency into its account's at the instant's rate (ADR-0114 D7). The conversion is the API's: no function reads `fx_rates`. |
| `account_balances_as_of_now(ledger_id, scheduled_from)` | Every account's current balance: the latest `balance_after` among the rows before `scheduled_from`, opening-balance fallback. | Migration 259; migration 276 made each account's lookup keep to the headers the register shows in the account's own ledger, and migration 279 bounds it by the account's own running balances: it counts them (`idx_thab_account_ledger`), walks `idx_txn_headers_ledger_posted_seq` back from the boundary over at most that many headers, and only when the walk finds none reads the account's own rows and their headers by key, so it costs at most a small multiple of the account's row count, never the ledger's header count. The cash side of every current value, through `AccountBalancesRepository` (the sidebar's balances, the overview, the balance forecast's day 0, MCP); see the `account_current_balances` section for the boundary and the lookup. |
| `holdings_as_of_now(ledger_id, scheduled_from, account_ids)` | Every position held before the boundary: quantity, FIFO cost basis, and the `security_prices_as_of_now` price with its `price_currency` (NULL when the security has no price). | Migration 259; **281** added `price_currency` (DROP + CREATE), so a current value converts a position priced in another currency into its account's at the latest rate (ADR-0114 D7). The read behind every current value (dashboard, sidebar, MCP `list_accounts` / `net_worth` / `account_portfolio`, the portfolio card, the securities catalog and detail, the quote refresh's held set, `fn_unbacked_price_securities`); see the `account_current_balances` section. Reads the `holdings` projection for an account with nothing at or after the boundary, and `holdings_cost_basis_as_of` at the last instant before it for one with a security leg or a split at or after it. |
| `security_prices_as_of_now(ledger_id, scheduled_from, security_ids)` | Per security, the latest `security_prices` row dated on or before the caller's today, else `fn_last_trade_price` at the end of that day, back-adjusted onto the day's split basis for any split after the observation, with `price_currency`: the close's `currency_code`, else the trade's. | Migration 259; **281** added `price_currency` (DROP + CREATE). A split changes a price's basis, never its currency. The price every current value is computed at: the rule `holdings_market_value_as_of_set` applies at any instant, asked at the boundary, so every position is valued alike by both. |
| `realized_gains_walk(…)` | Replays disposals to produce realized gain rows. | Migration 182 scales its money columns, because unscaled `NUMERIC` can overflow .NET `decimal` on read. Migration 301 redefines it to round each money figure once at the minor units of the account's currency, read through the SQL minor-units function, with the same rounding as `recompute_holdings_cost_basis` ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D15, settled by Claude, 2026-11-16). |
| `holdings_fifo_walk(account_id, security_id, as_of, exclude_header_id)` | The pure FIFO walk (ADR-0064): quantity, cost basis, lots, gains, `o_shortfall`, the shares disposals could not match, and `o_gain_lots`, each lot every sale consumed. | **SQL-only** (not bound). The one implementation of the FIFO rules; the recompute persists it and the `*_as_of` readers ask it. Migration 256 added `o_shortfall` and `exclude_header_id` (DROP + CREATE, since the OUT shape changed). **Migration 282** added `o_gain_lots` (DROP + CREATE again): an array of the composite type `holdings_fifo_gain_lot` (`sell_leg_id`, `acquired_at`, `quantity`, `cost`), one element per take of a sale (a disposal `o_gains` records) from one lot, its cost the shares times the lot's unit cost, so a sale's takes sum to its cost basis sold. Nothing the walk computed before changes. |
| `holdings_open_lots_as_of(ledger_id, as_of, account_ids)` | Every FIFO lot open at an instant on the given accounts: account, security, acquisition instant, shares and cost (shares times unit cost), each rounded to 12 places. | **Migration 282**, ADR-0114 D7. What a cost basis converts at lot by lot, each lot at its acquisition date's rate, for an account whose currency differs from the figure's. Positions are discovered from the legs as `holdings_cost_basis_as_of` discovers them, so a position's open lots sum to its cost basis there. Every other reader uses the stored or as-of totals. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `realized_gain_lots(ledger_id, account_ids)` | Every sale's FIFO lots on the given accounts: account, security, the sale's leg, each lot's acquisition instant, shares and cost, rounded to 12 places. | **Migration 282**, ADR-0114 D7. Read from the unbounded walk `recompute_holdings_cost_basis` persists into `realized_gains`, so a sale's lots sum to its cost basis sold before the writer rounds it. A combined realized gain converts each lot's cost at its acquisition date's rate and the proceeds at the sale's, so it includes the FX move between buying and selling. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `holdings_fifo_shortfall(account_id, security_id)` | One position's shortfall. | Migration 256. The investment write paths read it before and after a save and refuse an increase (FU-097). |
| `holdings_fifo_open_lots(account_id, security_id, as_of, exclude_header_id)` | The open lots at an instant, optionally without one transaction. | Migration 256. Drives the editor's lot preview at that instant, not from today's `lots`. |
| `holdings_fifo_shortfalls(ledger_id)` | Every short position of a ledger, and by how much. | Migration 256. Behind the consistency report's `Oversold` section; nothing repairs these (ADR-0064, 2026-10-20 amendment). |
| `recompute_posting_counts_for_header(header_id)` | Refreshes the denormalized posting counts on a header. | ADR-0046: the counts are what let the register decide split-vs-flat without a second query. Its UPDATE computes the counts in subqueries that keep the statement's snapshot, so a statement that waited for a writer holding the header's legs would write the counts it read before that writer committed. The API runs it only in a transaction holding the ledger write lock (a writer's save, and the `posting_counts` repair, one header per transaction), so it begins only after any other writer of the ledger has committed ([ADR-0112](decisions/0112-one-write-lock-per-ledger.md)). The Moneydance import does not call it: its own set-based UPDATE recounts the whole ledger in the transaction that created it. |
| `fn_lock_txn_headers(header_ids uuid[])` | Takes `FOR NO KEY UPDATE` locks on the given headers in id order, until the caller's transaction ends; returns how many it locked. | Migration 264. The stored memo-and-tags recompute (`LegNotes`) locks its headers with it before reading the rule's inputs, so two recomputes of one header run one after the other and overlapping sets cannot deadlock (a locking `SELECT ... ORDER BY` locks in that order; an `UPDATE` locks in plan order). Refuses any isolation level but READ COMMITTED: reading after the lock sees the previous holder's commit only because each READ COMMITTED statement takes a fresh snapshot. The consistency check's re-read of drifted headers takes the same lock. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_lock_tags(tag_ids uuid[])` | Takes `FOR UPDATE` locks on the given tags in id order, until the caller's transaction ends; returns how many it locked. | Migration 264. A tag delete, merge or cleanup reads which headers carry the tag after this lock and recomputes them: a link another request adds references the tag with a `KEY SHARE` lock, which `FOR UPDATE` excludes, so it either commits before the read (and is read) or waits for the writer's commit. Without it a link added between the read and the delete or repoint would be changed with no recompute. Each of the three takes the ledger write lock (`fn_ledger_write_lock`) first: a save that holds that lock and a header links a tag, and this lock comes before the header locks of the recompute. READ COMMITTED only, as the header lock. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_lock_accounts(account_ids uuid[])` | Takes `FOR UPDATE` locks on the given accounts in id order, until the caller's transaction ends; returns how many it locked. | Migration 264. A category merge reads the source's legs, and a change of a brokerage's holdings link reads the headers on it, after this lock: a leg or a transaction another request adds references the account with a `KEY SHARE` lock, which `FOR UPDATE` excludes (the `FOR NO KEY UPDATE` an `UPDATE` of the account's other columns takes does not). No trade-price path takes it: excluding `KEY SHARE`, it would wait for every trade in flight on the account while those trades' re-derivations waited for it; they take `fn_lock_trade_price_accounts` (the lock order under `security_price_upsert_from_trade`). READ COMMITTED only, as the header lock. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_lock_trade_price_accounts(account_ids uuid[], for_write boolean)` | Takes `FOR NO KEY UPDATE` (`for_write`) or `FOR SHARE` locks on the given accounts in id order, until the caller's transaction ends; returns how many it locked. | **Migration 274.** Step 1 of the trade-price lock order (under `security_price_upsert_from_trade`): `FOR SHARE` for a re-derivation and the `holdings_currency` repair, `FOR NO KEY UPDATE` for a brokerage's currency edit and the trade-price repair. Neither mode excludes the `KEY SHARE` a new line takes. READ COMMITTED only, as the header lock. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_lock_securities(security_ids uuid[])` | Takes `FOR UPDATE` locks on the given securities in id order, until the caller's transaction ends; returns how many it locked. | **Migration 274.** Step 0 of the trade-price lock order: `merge_securities` locks the source before it reads which lines, holdings and days are the source's. A line, holdings row or price row another request adds for the security takes a `KEY SHARE` lock on it, which `FOR UPDATE` excludes, so one in flight is waited for and moved with the rest, and one that starts later waits for the merge, as `fn_lock_tags` and `fn_lock_accounts` serve a tag or category merge. READ COMMITTED only, as the header lock. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_trade_price_accounts(ledger_id, security_ids uuid[], days date[])` | Every account whose currency the trade-price rule can read for the given (security, UTC day) pairs, the arrays read in step, whichever execution of each day it picks. | **Migration 274.** For each execution of each day (`fn_trade_day_executions`), the accounts `fn_trade_currency` chooses among (`fn_trade_currency_accounts`): brokerages, and an account no brokerage names, never a Holdings sibling. What the trade-price writers lock (`fn_lock_trade_price_accounts`). EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_trade_currency_accounts(account_id)` / `fn_trade_currency(account_id)` | The accounts whose currency a trade on a line of this account is priced in, and the one it takes. | **SQL-only** (not bound). **Migration 274**, ADR-0084 D2 (2026-11-04 amendment): every brokerage naming the account as its `holdings_account_id`, or the account itself when none does; `fn_trade_currency` takes the first by code in ordinal order, then by id, the code the `holdings_currency` check expects when brokerages disagree. Read by the rule, the checker and the importer's seed, so the currency is chosen in one place. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_unbacked_price_dates(ledger_id, security_id)` | The dates where this security holds a trade-source price no trade derives. | Migration 241; **273** applies the rule's header filter (hidden, merged and template headers are not trades); **274** re-issues it, body unchanged, so its comments name `fn_trade_execution_for_day` as the rule. ONE pass — a per-row test (migration 240's `fn_price_is_unbacked`, dropped by 273) rescans the security's legs per price, so its cost is prices × legs and a long daily price history makes it the slow path; anything covering a whole history uses this, the advisory below included. The only statement of the advisory's test. |
| `fn_unbacked_price_securities(ledger_id, scheduled_from)` | One row per security holding such prices, ordered by what the holding is worth now. | Migration 240. Advisory behind the consistency panel — never a finding; see ADR-0084 D6. Since migration 259 "worth now" is the current value on the caller's today: `holdings_as_of_now`, at its price or else the cost basis (the one-argument form was dropped). Since **273** its dates are `fn_unbacked_price_dates`, asked laterally once per security holding a trade-source price; it had asked `fn_price_is_unbacked` per stored price, a cost of prices × legs within a security that a long history takes past the API's command timeout, and the consistency report reads it in the same request. Same columns, rows and order. With no caller left, 273 drops `fn_price_is_unbacked` and its C# declaration; the comparisons in `ConsistencyTradePricesTests` and `db/test/verify_share_movements_are_not_prices.sql` write its body out. |
| `fn_leg_prices_a_day(quantity, unit_price, action, provider_action)` | Whether a security leg is a genuine price observation. | **SQL-only** (not bound). Migrations 235 → 237 → **238**, ADR-0084 D6. An ALLOW-LIST of executions (`buy`, `buyx`, `sell`, `sellx`, `dividend_reinvest`) minus the Moneydance `ShrsIn`/`ShrsOut` that map onto them. Anything else — `transfer_shares`, or an action added later — prices no day until it is named. An exclusion list (235's and 237's) fails open, pricing a day from any action it does not name, which is why 238 replaced it with the allow-list. Shared by the rule and its four hand-written statements (the checker, `fn_unbacked_price_dates`, the importer's `TradePriceSeedStep` and `fn_last_trade_price`) so none can disagree about what counts. |
| `fn_trade_day_executions(ledger_id, security_id, day)` | A security's executions on a UTC day, with what the rule orders them by. | **SQL-only** (not bound). **Migration 274**: `fn_trade_price_for_day`'s filter of migration 273. Read by the rule and by `fn_trade_price_accounts`, so the pick and the accounts the writers lock cannot disagree about which lines are trades. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_trade_execution_for_day(ledger_id, security_id, day)` | THE rule: the execution that prices a day, as its price at 4dp, the currency of the brokerage that made the trade, and the account that currency is read from; no row when none does. | **SQL-only** (not bound). **Migration 274**, ADR-0084 D2 (2026-11-04 amendment): picks from `fn_trade_day_executions` and reads `fn_trade_currency` for the picked line's account. Ordered `h.seq DESC, abs(amount) DESC, l.id DESC` — the last two make it total, so the answer, currency included, cannot vary between runs. EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_trade_price_for_day(ledger_id, security_id, day)` | **Dropped by migration 274.** A day's trade-derived price, or NULL. | Migration 234; 238 made it executions only; **273** read `live_txn_headers` with `is_hidden = FALSE AND is_merged_into IS NULL`, `fn_last_trade_price`'s filter. Its one caller, `fn_trade_price_reseed`, reads `fn_trade_execution_for_day` since 274, which then drops it, as 273 dropped `fn_price_is_unbacked`; the tests that compared against it read `fn_trade_execution_for_day`. |
| `fn_trade_price_reseed(ledger_id, security_id, day)` | Derives the day's price and currency by that rule and rank-gates them into `security_prices`. | Migration 234; **274** passes the currency `fn_trade_execution_for_day` names to `security_price_upsert_from_trade`, read after it locks `FOR SHARE` every account `fn_trade_price_accounts` names for the day: the pick and that set are one statement, run again until every account in the set was locked before it began, so a currency edit or a trade-price repair in flight is waited for whichever execution the read picks. The writer's entry point: `TradePriceRecomputeService` names the (security, day) a write touched, never a price: the day a trade arrives on and the day it leaves. Returns NULL and leaves the stored row when no execution prices the day (ADR-0084 D4). |
| `fn_trade_price_check(ledger_id)` | Reads back what the trade-price rule IMPLIES for every `(security, UTC-day)` it covers, with `reason` NULL where the stored row agrees and `missing`/`value` where it does not (`ConsistencyReasons`). | Migration 232; **233** added `account_id` / `header_id`, so a row names the trade behind it rather than a bare GUID no one can act on; **239** added `orphaned` for a stored trade price nothing derives any more, and **240** took it back out: such a price is the unbacked-price advisory (`fn_unbacked_price_securities`), never a finding (ADR-0084 D6); **273** applies the rule's header filter, so a hidden or merged-away execution is expected to price nothing; **274** adds `expected_currency` (the currency of the brokerage that made the picked trade, `fn_trade_currency`) and `stored_currency`, and `value` covers a stored `import`, `simplefin` or `trade` row in another currency (DROP + CREATE, since the OUT shape changed). The reader half of `security_price_upsert_from_trade`, and one of the four hand-written statements of which executions are trades — see the note above, under `security_price_upsert_from_trade`, on why it re-states the rule rather than calling it. |
| `fn_ledger_write_lock(ledger_id, wait_ms)` | The ledger write lock: a transaction-scoped advisory lock on `coffer.ledger-write:<ledger_id>`, taken as the first statement of a transaction that writes to the ledger and can recompute its balances or holdings; returns true. | **Migration 275.** Refuses (SQLSTATE 25000) a transaction that has already written or row-locked anything without it (a row lock assigns a transaction id as a write does, so `fn_lock_*` and the trade-price `FOR SHARE` count), unless the transaction inserted the ledger row (the Moneydance import) or the session is a migration's (`MigrationRunner` connects with `coffer.migration_session = on`; the function then does nothing); refuses any isolation level but READ COMMITTED, and a session that holds another advisory lock other than the same ledger's snapshot-create lock, so it comes before the reminder slot lock (all 25000); refuses a null ledger or a wait that is not positive (22023). Does nothing when the transaction already holds it (the transaction-local setting `coffer.ledger_write_held` lists the ledgers it took) or the session holds the session form. Waits at most `wait_ms` under a transaction-local `lock_timeout` restored once granted, then refuses with SQLSTATE `LW001`, which the API answers as a 422 `ledger-write-busy`; the default, 15000, is half the API's 30-second command timeout, and the API passes half of its context's own (`LedgerWriteLock.BeginLedgerWriteAsync`, which opens a READ COMMITTED transaction when none is open). `fn_recompute_balances_for_account` and `recompute_holdings_cost_basis` call it themselves with the default wait. Writers that recompute neither but lock or write rows such a transaction goes on to need take it first too (ADR-0112 D3): a tag merge, delete or clean-up, a budget fill, a feed connection's delete and a ledger's delete. So do a typed budget target and a reporting-currency change (`PATCH /api/ledgers/{id}`), so a target is stamped with the reporting currency current when it commits (ADR-0112 D3, 2026-11-13 amendment; ADR-0114 D12), and the market-series writers (`MarketSeriesFetcher`, `MarketSeriesRepository`), so two runs take turns. Writes to one ledger take turns; reads, snapshot creates and other ledgers do not wait ([ADR-0112](decisions/0112-one-write-lock-per-ledger.md)). Bound as a TVF; EXECUTE is granted to `coffer_app` and `coffer_service`. |
| `fn_ledger_write_session_lock(ledger_id, wait_ms)` / `fn_ledger_write_session_unlock(ledger_id)` | The ledger write lock held by a session until released. | **Migration 275.** For snapshot restore, whose transaction is REPEATABLE READ: taken before that transaction begins, released after it ends, so the restore and the ledger's writers take turns; while the session holds it, `fn_ledger_write_lock` does nothing there. Refuses after a write or row lock and while the session holds another advisory lock other than the ledger's snapshot-create lock; waits and refuses as the transaction form does. |
| `reminder_occurrence_lock(recurring_transaction_id, occurrence_date)` | Advisory lock for one series occurrence. | Stops the auto-post job and a hand-fire from materializing the same occurrence twice, and a fire and a skip from both acting on one. A fire takes it after its ledger's write lock (`fn_ledger_write_lock`), so fires of one ledger take turns whatever their occurrence ([ADR-0112](decisions/0112-one-write-lock-per-ledger.md), superseding migration 218's parallel slots); a skip takes it alone. |
| `reminder_estimate_samples(recurring_transaction_id, …)` | The prior amounts an estimated reminder averages over. | Backs `estimate_sample_count`. |
| `ledger_snapshot_payload(ledger_id)` | Builds a snapshot's payload from the in-scope tables. | The captured set is `fn_ledger_snapshot_part_names()`; `SchemaDriftGuardTests` asserts the payload's keys match it. |
| `ledger_snapshot_write(…)` / `ledger_snapshot_restore(…)` | Write and replay a snapshot payload. | v3 chunks through `ledger_snapshot_parts`; `ledger_snapshot_restore_stored` is the stored-payload variant. `ledger_snapshot_write` takes an optional third argument, the chunk size (default 2000, migration 247) — EF binds the two-argument form. Both restore paths finish with `fn_ledger_snapshot_reconcile_external_refs`, which applies an FK's own `ON DELETE` action to a restored row whose parent outside the snapshot has since been deleted: an account's deleted bank connection (migration 247), and a header's or a promotion's run pruned by audit retention (migration 302). Since 302 it takes the restore's expected row counts per table and returns them less the rows it deleted, which `fn_ledger_snapshot_verify_restored` then checks. Every FK the fix-up reconciles is `DEFERRABLE INITIALLY DEFERRED`, so the restored row lands first and the restore's `SET CONSTRAINTS ALL IMMEDIATE` checks it after the fix-up. A restore runs while its session holds the ledger write lock's session form (`fn_ledger_write_session_lock`, see `ledger_snapshots`). |
| `ledger_delete(ledger_id)` | Deletes a ledger and everything scoped to it. | One function so the delete order is stated once rather than reconstructed by an ORM. `LedgersRepository.DeleteAsync` calls it in a transaction that takes the ledger write lock (`fn_ledger_write_lock`) first, so a write to the ledger in flight finishes before the wipe and one that starts later waits for it. |
| `restore_readiness()` | The connecting role, the live database, and whether the role may create databases and owns this one. | Migration 246. A backup restore builds a new database and swaps it in, which needs both; the restore endpoints refuse before staging when either is missing. |
| `snapshot_ledger_lock(ledger_id)` / `snapshot_ledger_unlock(ledger_id)` | A SESSION advisory lock serialising snapshot creates for one ledger. | Migration 247. Taken before the capture transaction and released after it: the five-snapshot cap is checked before the capture, so without the lock two concurrent creates can both pass it. A lock inside the REPEATABLE READ transaction would come after its snapshot. Since migration 275 the lock refuses a session that holds the same ledger's write lock: the snapshot-create lock is taken first. |
| `tag_name_key(name)` | The one definition of "the same tag name": `lower()` of the name with leading and trailing white space removed, white space being .NET's `char.IsWhiteSpace` set written out character by character (the set `string.Trim()` uses, which the writers store). | Migration 263. `IMMUTABLE STRICT`, `pg_catalog`-qualified and with no SET clause, so it means the same under any `search_path` and the planner inlines it into the queries that compare keys. Every name-to-id lookup goes through it, and migration 264's unique index `uq_tags_name_key_per_ledger` is on it. `[[:space:]]` is not used because it follows the server locale. EXECUTE is granted to `coffer_app`. |
| `tag_name_keys(names)` | One row `(name, key)` per name of an array: `tag_name_key` over a request's names. | Migration 263. The row source the API joins to `tags` in LINQ, so a request's names (saved or not yet) are keyed in SQL in the same round trip; EF cannot root a query on a parameter array, and folding them in .NET would be a second definition of the rule. Not STRICT, so it inlines. Linear in the names and the ledger's tags: the match plans as a hash or merge join. EXECUTE is granted to `coffer_app`. |
| `saved_reports_repoint_tag(ledger_id, from_tag_id, to_tag_id)` | On a tag merge, rewrites `spec.tagId` from one tag to the other on every saved report of the ledger, every member's included; returns the reports changed and, of those, the ones the caller can see. | Migration 263. `SECURITY DEFINER` with a pinned `search_path`, because saved-report writes are owner-only under RLS and the merging member cannot reach other members' private reports. It is therefore a second ledger-access check: the caller must hold an owner or editor grant (`insufficient_privilege` otherwise), the two tags must differ and both belong to the ledger (`invalid_parameter_value` otherwise). It writes `spec.tagId` and nothing else (`updated_at` stays). The merge reports only the visible count, so it never reveals how many private reports another member keeps on the tag. EXECUTE is granted to `coffer_app` only. |

`account_path(account_id)` is documented above but is NOT one of the forty-six — it
is called from SQL (the resolved view) rather than bound through EF.

Nor are two more from migration 263, which only their owner calls:

- `saved_report_spec_tag_id(spec)` — the only parser of a stored `spec.tagId`: the uuid
  when the value is a hyphenated uuid string, else NULL (a number, null or malformed
  string is no tag; the cast sits behind both tests in a `CASE`, so a malformed value is
  never cast). Both tag merges read a report's tag through it.
- `transfer_shares_source_brokerages(ledger_id)` — the one definition of an in-kind
  transfer's source brokerage, which owns it: per `transfer_shares` header, the brokerage
  whose leg sits in the same posting as a negative-quantity leg on that brokerage's own
  holdings account (pairing by posting keeps it unique when two brokerages share a
  holdings account); only for a header on which no posting pairs them, any brokerage on
  the header whose holdings account carries the negative leg. Migration 263's rebuild of
  unsourced transfers as zero-gain sales (ADR-0108 D18), its backfill and end assertion,
  the Moneydance import check and the test-time invariant check all read it, so they
  cannot disagree. `SECURITY INVOKER`, not STRICT and with no SET
  clause, so it inlines; every join is index-backed, so over a whole ledger it is linear
  in its in-kind transfers. No request path calls it, and PUBLIC's EXECUTE is revoked.

Nor is `fn_last_trade_price(ledger_id, security_id, as_of)` (migration 259, ADR-0084's
2026-10-21 amendment): THE no-price rule, called from SQL by
`security_prices_as_of_now` and `holdings_market_value_as_of_set`. It returns the unit
price, to 4 places as a trade price is written, the instant of the security's most
recent execution in the ledger at or before `as_of` (executions as
`fn_leg_prices_a_day` names them; hidden, merged and template headers excluded; ordered
`posted_at, seq, abs(amount), id` descending), and, since **migration 281**, its
`currency_code`: `fn_trade_currency`'s for the picked line's account, the currency
migration 274 writes a trade price in. No row when it never traded at a price.

Nor is `currency_minor_units(code)`, the SQL minor-units function, added by
migration 301, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)
D15 (settled by Claude, 2026-11-16): a currency code's ISO 4217 minor units, 0, 2 or 3.
It is `IMMUTABLE`, generated from the checked-in ISO 4217 data file that the API and the
SPA compile in, and pinned to that file by a guard test. It returns 2 for a stored
account or ledger code that would now be refused for any reason (see Conventions). Only
`recompute_holdings_cost_basis` and `realized_gains_walk` read it, so they round at the
same places, half away from zero.

### Trigger functions

The balance-after maintenance + posting-pair invariant trigger
functions are documented under [Triggers](#triggers) below.

---

## Triggers

### Running balance — header-walk on `txn_header_account_balances`

ADR-0034 (mig 089–097). One row per `(header, account)` carries the running balance on that account after the header is applied. Maintained by `fn_recompute_balances_for_account(account_id, from_posted_at)`. Until migration 102 (below) a statement-level dispatcher `fn_trg_legs_recompute_balances()` on `txn_legs`, plus `fn_trg_headers_recompute_balances()` on `txn_headers` for `posted_at` / `is_merged_into` changes, called it; those triggers honoured `pg_trigger_depth() > 1` for recursion prevention and exited early when only non-balance-relevant columns changed.

The recompute aggregates leg amounts per header (the **header-walk**), then running-SUMs in the canonical **`(posted_at, seq)`** order. `txn_headers.seq` is a strictly-monotonic `BIGINT` populated by the `txn_headers_seq` SEQUENCE; within a batch INSERT each row receives a distinct value, eliminating the UUID-tiebreaker ambiguity that plagued the initial `(created_at, id)` design. Multi-leg same-account headers (e.g. BuyXfr fan-out) collapse to a single step in the running total. Both `txn_headers.seq` (mig 095) and `txn_headers.created_at` (mig 093) are locked immutable by column-level BEFORE-UPDATE triggers.

Invariant: for any **visible** header (`is_merged_into IS NULL` AND `COALESCE(h.is_hidden, FALSE) = FALSE`) and any account it touches, `balance_after` equals `opening_balance` + the sum of net-per-header amounts for that account, summed across every earlier visible header in canonical `(posted_at, seq)` order. Hidden headers are excluded — the recompute predicate matches the resolved view's so the rows you can't see don't count against the rows you can. Amounts and dates come off `txn_legs` / `txn_headers` directly: migrations 099 / 101 / 103 taught the recompute to COALESCE through the override layer, and migration 230 removed the layer, so the COALESCE went with it.

**Mig 102** dropped the entire balance-trigger family. The recompute function stays as the algorithm but is invoked from API call sites instead: the `LegDerivedRecomputeInterceptor` (`SaveChangesInterceptor`) scans `ChangeTracker` and fires the recompute automatically for every API write that changes what it reads: an added or deleted leg, a modified leg whose changed properties are not all in the interceptor's `NonRecomputeLegProperties` (the leg's memo, its `notes_*` columns and its investment metadata never start one), and a header whose `posted_at`, `is_merged_into` or `is_hidden` changed; bulk paths that bypass the ChangeTracker (`ExecuteUpdateAsync` / `ExecuteDeleteAsync` / Dapper) invoke `LegDerivedRecomputeService` explicitly. **Mig 103** added `is_hidden` to the canonical recompute predicate set, so soft-delete (the bank + investment + bulk DELETE soft-hide branches) removes the row from the balance walk in the same SaveChanges that hides it from the register. See [decisions/0034-header-walk-running-balance.md](decisions/0034-header-walk-running-balance.md) for the rationale and [decisions/0032-triggers-as-last-resort.md](decisions/0032-triggers-as-last-resort.md) for the broader posture.

**An opening-balance edit re-derives the account's running balances.** The opening balance seeds every stored row on the account, and the interceptor never sees an account write, so the account edit that changes it recomputes the account's balances from its first header, in the edit's transaction (`AccountsRepository.UpdateAsync`). Two account edits racing on an opening balance can leave running balances stale until the consistency check reports it and Repair fixes it. This is an accepted limit.

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

**Stops where the register's Scheduled begins (mig 259).** The register marks a row
Scheduled when its `txn_headers.posted_at` (the date it shows, the effective date
since mig 230) is at or after the caller's LOCAL tomorrow at midnight UTC, which is
how it stores a date. A current balance counts exactly the rows before that instant,
`scheduled_from`, computed once in the API by `ScheduledBoundary` (the helper the
register's own status counts use) from the date the SPA sends (`?today=`), or, for a
caller with none, from the register's fallback, the server clock's UTC date. A row
dated later (a scheduled payment, a trade entered ahead) is left out until the
caller's date reaches it and then counts on its own; nothing is stored. The API reads
`account_balances_as_of_now(ledger, scheduled_from)`; this view keeps its shape and is
that function at the register's fallback (the next midnight UTC after the server
clock's UTC date, `ScheduledBoundary`'s fallback, rather than `CURRENT_DATE`, which
follows the session time zone), for any reader with no caller's date.
Inside the functions the boundary becomes the last instant not Scheduled
(`scheduled_from` less one microsecond, timestamptz's resolution), so the cut is
`account_balance_as_of_instants`'s rule at that instant and agrees with the net-worth
history's last point. The holdings side follows: current values read
`holdings_as_of_now` (positions held that day, priced by `security_prices_as_of_now`:
the latest `security_prices` row dated on or before the day, else
`fn_last_trade_price`, on the day's split basis; neither, cost basis) instead of the
`holdings` projection, which still counts every event entered. The register's rows,
their `balance_after` and the stored running balances are unchanged. The managed
loan split reads no current balance: it is priced with `account_balance_as_of_instants`
at the payment's own `posted_at` (ADR-0078, 2026-10-21 amendment).

**Each account's lookup is bounded by the account's own running balances (migs 276,
279).** For each account the function takes the running balance of the latest header
before the boundary that is in the account's own ledger and is neither hidden nor merged
away, ordered `(posted_at DESC, seq DESC)` (the rules since migration 276). Since
migration 279 it finds that header in three steps:

1. It counts the account's running balances (`idx_thab_account_ledger`): *n*.
2. It walks `idx_txn_headers_ledger_posted_seq` back from the boundary over at most *n*
   of the ledger's visible headers, probes each one's running balance on the account by
   its primary key, and stops at the first it finds. For an account with no running
   balance *n* is 0 and nothing is walked.
3. Only when the walk finds none, it reads the account's own running balances
   (`idx_thab_account_ledger`), reaches each one's header by its key, and takes the latest
   the rules admit.

Why each step is needed: a walk of the register's index with nothing to stop it sooner
(the plan migration 276 left open) is a few headers for an account with a row shortly
before the boundary, but every header before the boundary for an account with no row (a
category never used, an account holding only its opening balance, a brokerage never
traded in) and nearly every one for an account whose last row is years back, and a
ledger gathers such accounts as it ages. Reading the account's own rows instead costs
every row a busy account has ever had. Bounded by the account's own row count, the walk
stays a few headers for a busy account, and the whole lookup costs at most a small
multiple of the account's row count, whatever else the ledger holds. The bound is the
account's row count, not a tuned number, and it decides only how far the walk goes before
it hands over, never the answer: the walk applies the rules in the register's order, and
the fallback applies them to every running balance on the account, exactly. Before 276
the lookup named no ledger, and every account's read scanned the headers dated before the
boundary and sorted them.

The plan is held to those steps. The walk's `LIMIT` keeps it a subquery the planner cannot
flatten; the running balance the walk probes, and the header the fallback probes, are each
read in a subquery of their own (`OFFSET 0`), once per row and by key; and the fallback's
gate names only the walk's result, so it is tested once per account before any row is
read.

The row found is the same as under 276 on every state, drifted ones included (a running
balance on a hidden or merged-away header, or on one at or after the boundary, counts
towards *n* but is never taken), and the same as before 276 on every state the app writes:

- **The ledger**: a running balance's header and account are in one ledger, by
  `txn_header_account_balances`' composite foreign keys (mig 089), so naming the account's
  ledger leaves out no row.
- **Hidden and merged headers**: the balance walk stores no running balance on either,
  and an account's recompute deletes every stored row in its window before writing the
  walk's (on any header since migration 277), so a header hidden or merged away loses its
  own. Where stored balances have drifted so that such a header still carries one, the
  function skips it, as the register does; the balances check reports it as a stored row
  the walk does not produce (reason `extra`), and the balances repair removes it.
- **The order**: `(posted_at DESC, seq DESC)` exactly, so a tie on a day is broken as
  before.

The signature, volatility and `SECURITY INVOKER` are unchanged, so the owner, the grants
and row-level security as the caller stay as they were, and `account_current_balances`
reads it unchanged. `CurrentBalanceLookupShapesTests` holds the function, the register's
running balance on its newest counted row and `account_balance_as_of_instants` at the
last counted instant to one answer for rows tied on the latest day, a latest row hidden
or merged away, an account with one row or none, and a row after the boundary, and the
view to the function at the fallback date. `CurrentBalancePlanTests` holds the plan, as
`coffer_app` plans it under row-level security on a ledger with busy accounts and the
quiet ones a long-lived ledger gathers: the only reads of `txn_header_account_balances`
and `txn_headers` it admits are the count through `idx_thab_account_ledger`, the walk of
`idx_txn_headers_ledger_posted_seq` under a `Limit` of its own with each header's running
balance probed by key, and the fallback through `idx_thab_account_ledger` with each header
probed by key, judged on plan nodes, relation and index names, and failing on any other
read or on a plan missing one of the three. The stress lane times the function, the
sidebar's balances and the overview on the default, four-times and long-lived shapes
(`CurrentBalanceCostTests`), and the readers beside the balance forecast
(`TransactionEntriesCostTests`).

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

### Folder cycle guard on `account_folders` (dropped)

Migration 258 added `trg_account_folders_refuse_cycle` to refuse a folder becoming its own
ancestor; migration 261 dropped it and its function, because the API carries the rule
(ADR-0032). See [`account_folders`](#account_folders).

### Triggers that remain

Two, both on `txn_headers`: `trg_reject_txn_headers_created_at_update` (migration 093) and
`trg_reject_txn_headers_seq_update` (migration 095), which refuse an UPDATE of the
`created_at` and `seq` columns the canonical `(posted_at, seq)` order is built on. Neither
has been re-justified against ADR-0032's gates since that ADR; FU-099 tracks it.
`AccountFoldersEndpointsTests` pins this list against the catalog.

---

## Indexes

| Index | Table | Purpose |
|---|---|---|
| `uq_txn_headers_ledger_external_id` | `txn_headers(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent re-import key at the event level (ADR-0022). |
| `uq_txn_headers_online_match` | `txn_headers(ledger_id, online_match_fi_id, online_match_fitid) WHERE online_match_fitid IS NOT NULL` | FITID uniqueness for OFX-style feed rows (slice 2c.2 / migration 039). Concurrent inserts race; SyncService maps the unique-violation to `alreadyKnown`. |
| `uq_ledger_operations_one_running_per_connection` | `ledger_operations(feed_connection_id) WHERE status='running' AND feed_connection_id IS NOT NULL` | At-most-one running sync per connection (slice 2c.2 / migration 040). Concurrent sync attempts race; loser → 422 `feed-sync-in-progress`. Stale `running` rows swept by SyncService's lazy reaper. |
| `idx_txn_headers_ledger_posted_at` | `txn_headers(ledger_id, posted_at DESC, id DESC)` | Cursor-paginated register reads. |
| `idx_txn_headers_ledger_visible` | `txn_headers(ledger_id, posted_at DESC, id DESC) WHERE NOT is_hidden AND is_merged_into IS NULL` | Visible-only register reads (the common case); partial keeps the working set tight. |
| `idx_txn_headers_ledger_posted_seq` | `txn_headers(ledger_id, posted_at DESC, seq DESC) WHERE NOT is_hidden AND is_merged_into IS NULL` | Migration 095. A ledger's visible headers in the canonical `(posted_at, seq)` order, newest first, which `idx_txn_headers_ledger_visible` cannot give: it breaks a day's ties by id, which is not a time order. Read by the register's pages (`RegisterQuery`), the ledger-wide entries' newest-first header batches (`GET …/transactions/entries`), the balance forecast's committed rows (`GET …/accounts/balance-forecast`, through the register's `CandidateHeaders`) and each account's lookup in `account_balances_as_of_now`, behind every current balance. Since migration 279 that lookup walks at most as many headers as the account has running balances: walked back from the boundary with nothing to stop it sooner, the index reads every header of the ledger for an account with no row. Also walked oldest first by the ledger's earliest day (`NeededFxPairs.FirstLivePostingAsync`, ADR-0114 D11), each header's legs probed, to the first header with a leg on one of its non-category accounts, which hold most of its legs, so the walk stops within a few headers. The first invested day does not walk it: its investment accounts' first posting can lie behind most of the ledger's headers (see `idx_txn_legs_account_id`). |
| `idx_thab_account_ledger` | `txn_header_account_balances(account_id, ledger_id)` | Migration 089. An account's running balances. Since migration 279 `account_balances_as_of_now` counts them here, which bounds its walk of `idx_txn_headers_ledger_posted_seq`, and reads them here, each row's header by key, when the walk finds none. |
| `idx_txn_headers_is_merged_into` | `txn_headers(is_merged_into) WHERE is_merged_into IS NOT NULL` | Merged-into chain lookups; partial because most rows are NULL. |
| `idx_txn_headers_originating_account` | `txn_headers(originating_account_id, ledger_id)` | Migration 263. Backs the composite FK's RESTRICT check on an account delete, and the writers that hand ownership from one account to another. |
| `uq_txn_legs_posting` | `txn_legs(header_id, posting_index, account_id)` | Two-legs-per-posting invariant + the importer's natural key for an inserted leg. |
| `idx_txn_legs_header_posting` | `txn_legs(header_id, posting_index)` | "All legs of this header" — drives AssembleEntries. |
| `idx_txn_legs_account_id` | `txn_legs(account_id)` | Per-account leg lookup + the balance recompute's per-account scan. Also the ledger's first invested day (`NeededFxPairs.FirstLivePostingOfFewAsync`, ADR-0114 D11): a bitmap scan of its investment accounts' legs, their distinct headers, and each header's posting day looked up by key (`uq_txn_headers_id_ledger`) in a scalar subquery, so it reads those accounts' legs and one header per event on them, however many headers lie before the first. Written as a join, the planner hashed a scan of every header of the ledger instead; as the first header in posting order, it walked `idx_txn_headers_ledger_posted_seq` over the whole history before the first investment. |
| `idx_txn_legs_security_id` | `txn_legs(security_id) WHERE security_id IS NOT NULL` | Per-security investment register query. |
| `ix_txn_leg_tags_ledger_tag` | `txn_leg_tags(ledger_id, tag_id)` | Migration 243. "Which legs carry this tag?", the direction the primary key cannot serve. |
| `ix_txn_leg_note_tags_header` | `txn_leg_note_tags(header_id)` | Migration 264. A recompute reads a header's stored tag rows. |
| `ix_txn_leg_note_tags_tag_in_play` | `txn_leg_note_tags(tag_id, leg_id) WHERE in_play` | Migration 264. A tag filter or report starts from the tag: its headers, then its legs. |
| `ix_txn_leg_note_tags_ledger_tag` | `txn_leg_note_tags(ledger_id, tag_id)` | Migration 264. Serves the cascade from a deleted tag, which matches every role and so cannot use the partial index. |
| `idx_holdings_account` | `holdings(account_id, security_id)` | Per-account positions |
| `idx_prices_security_date` | `security_prices(security_id, price_date DESC)` | Latest price lookup |
| `uq_fx_rates_pair_day` | `fx_rates(ledger_id, from_currency, to_currency, rate_date)` | Migration 280. One rate per pair and day, whatever its source, and the last rate on or before a day for a pair, read as a backward scan (see `fx_rates`). |
| `idx_lots_holding` | `lots(holding_id) WHERE is_closed = FALSE` | Open-lot scan for sell-side matching |
| `idx_ledger_operations_started` | `ledger_operations(started_at DESC)` | Sync history list |
| `idx_recurring_active_due` | `recurring_transactions(next_due_date) WHERE is_active = TRUE` | Due-date scan |
| `idx_txn_header_tags_tag` | `txn_header_tags(tag_id, header_id)` | Tag-first lookups |
| `uq_securities_cusip_per_ledger` | `securities(ledger_id, cusip) WHERE cusip IS NOT NULL` | Per-ledger CUSIP uniqueness. Replaced the global `uq_securities_cusip` (migration 002) in migration 048 — two ledgers can each hold the same CUSIP. |
| `uq_securities_ticker_per_ledger` | `securities(ledger_id, LOWER(ticker)) WHERE ticker IS NOT NULL` | Per-ledger, case-insensitive ticker uniqueness (migration 048). |
| `uq_securities_external_id_per_ledger` | `securities(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent source-system upsert (Moneydance imports), scoped per-ledger so two ledgers can each carry the same MD security id. Replaced the global `uq_securities_external_id` in migration 014. |
| `uq_accounts_external_id_per_ledger` | `accounts(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent source-system upsert (Moneydance imports), scoped per-ledger. Replaced the global `uq_accounts_external_id` in migration 014. |
| `uq_recurring_external_id` | `recurring_transactions(ledger_id, external_id) WHERE external_id IS NOT NULL` | Idempotent source-system upsert (Moneydance reminders), scoped **per ledger**. Added global in migration 013; a UNIQUE index on `external_id` alone is not ledger-scoped, so importing the same MD export into a second ledger collides, and migration 162 re-scoped it to `(ledger_id, external_id)`. |
| `uq_tags_name_per_ledger` | `tags(ledger_id, name)` | Per-ledger tag namespace. Replaced the global `tags_name_key` in migration 014. |
| `uq_tags_name_key_per_ledger` | `tags(ledger_id, tag_name_key(name))` | Migration 264, built after its merge of existing case variants: two names that differ only by case or surrounding white space are one tag. The importer's insert conflicts on it; see `tags.name` for the API's handling. |

---

## Required PostgreSQL extensions

| Extension | Purpose |
|---|---|
| `pg_trgm` | Trigram similarity for fuzzy payee matching |
| `pgcrypto` | Cryptographic helpers; `gen_random_uuid()` is built-in PG13+ |
| `pg_stat_statements` (recommended) | Query performance monitoring |
