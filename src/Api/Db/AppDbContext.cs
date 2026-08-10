using System.Reflection;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// EF Core context covering the entire API persistence surface. Per
/// ADR-0005 (as realigned in PR 3.6.5 and PR 3.7), the API runs on EF
/// Core end-to-end: routine CRUD, transactional inserts,
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> for set-based mutations,
/// view-backed reads, and the register-query keyset pagination via
/// <c>MR.EntityFrameworkCore.KeysetPagination</c>. Dapper stays in the
/// importer for its bulk-insert hot path.
/// </summary>
/// <remarks>
/// <para>Schema is owned by the SQL files under <c>db/migrations/</c>;
/// EF Core is configured here as a query/CRUD layer only. Never run
/// <c>dotnet ef migrations add</c> against this context — DbUp is the
/// migration runner. The Fluent API mapping below tells EF Core what
/// the existing schema looks like so it can generate valid SQL against
/// it.</para>
///
/// <para>Naming: column names map snake_case → property PascalCase via
/// explicit <c>HasColumnName</c> calls. Adding a snake-case naming
/// convention package is a future cleanup; for the column counts today,
/// explicit mapping is the readable choice.</para>
///
/// <para>Foreign keys: every <c>REFERENCES</c> clause in the schema is
/// configured on the entity up-front (engineering-standards §4.2.2).
/// Without it, EF picks an arbitrary INSERT order and a multi-entity
/// <c>SaveChangesAsync</c> can hit Postgres FK violations.</para>
/// </remarks>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<WebAuthnCredentialRow> WebAuthnCredentials => Set<WebAuthnCredentialRow>();
    public DbSet<SessionRow> AuthSessions => Set<SessionRow>();
    public DbSet<McpAccessTokenRow> McpAccessTokens => Set<McpAccessTokenRow>();
    public DbSet<McpToolInvocationRow> McpToolInvocations => Set<McpToolInvocationRow>();
    public DbSet<BootstrapTokenRow> BootstrapTokens => Set<BootstrapTokenRow>();
    public DbSet<InviteRow> Invites => Set<InviteRow>();
    public DbSet<WebAuthnPendingChallengeRow> PendingChallenges => Set<WebAuthnPendingChallengeRow>();
    public DbSet<RecoveryCodeRow> RecoveryCodes => Set<RecoveryCodeRow>();
    public DbSet<LedgerRow> Ledgers => Set<LedgerRow>();
    public DbSet<UserLedgerGrantRow> UserLedgerGrants => Set<UserLedgerGrantRow>();
    public DbSet<UserAccountGroupRow> UserAccountGroups => Set<UserAccountGroupRow>();
    public DbSet<UserAccountGroupMemberRow> UserAccountGroupMembers => Set<UserAccountGroupMemberRow>();
    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    // Investment surface (read-only at this layer; the importer owns
    // writes through Dapper). Internal — the Portfolio View / holdings
    // endpoints read through HoldingsRepository, callers don't touch
    // these DbSets directly.
    internal DbSet<SecurityRow> Securities => Set<SecurityRow>();
    // security_components (mig 150, ADR-0067): multi-asset look-through sleeves.
    internal DbSet<SecurityComponentRow> SecurityComponents => Set<SecurityComponentRow>();
    internal DbSet<HoldingRow> Holdings => Set<HoldingRow>();
    internal DbSet<SecurityPriceRow> SecurityPrices => Set<SecurityPriceRow>();
    // Per-acquisition lot audit (migration 049). The recompute function
    // (migration 056) owns the writes — the API endpoint queries open
    // lots for the editor's FIFO preview (ADR-0029).
    internal DbSet<LotRow> Lots => Set<LotRow>();
    // realized_gains (mig 148, ADR-0064): per-sale FIFO realized gains.
    // Owned by recompute_holdings_cost_basis; read-only here.
    internal DbSet<RealizedGainRow> RealizedGains => Set<RealizedGainRow>();
    public DbSet<FeedConnectionRow> FeedConnections => Set<FeedConnectionRow>();
    // Slice 2c.4: per-connection bank-side account directory. Sync
    // upserts; the GET endpoint joins to `accounts` for the Coffer
    // binding.
    internal DbSet<FeedConnectionAccountRow> FeedConnectionAccounts => Set<FeedConnectionAccountRow>();
    // Sync activity log (slice 2c.1, migration 038). Internal —
    // writes are driven by SimpleFinSyncService, reads by the
    // Provider-run audit (ADR-0055; formerly sync_runs).
    internal DbSet<LedgerOperationRow> LedgerOperations => Set<LedgerOperationRow>();
    internal DbSet<LedgerOperationErrorRow> LedgerOperationErrors => Set<LedgerOperationErrorRow>();
    internal DbSet<LedgerOperationPromotionRow> LedgerOperationPromotions => Set<LedgerOperationPromotionRow>();
    // ADR-0031 Phase 3a: provider security id → securities.id map
    // used by the orchestrator's brokerage branch to auto-resolve
    // known tickers without re-prompting the user.
    internal DbSet<ProviderSecurityMappingRow> ProviderSecurityMappings => Set<ProviderSecurityMappingRow>();
    // ADR-0037: server-side capped snapshots of the user-curated
    // ledger graph. Repository enforces the 5-cap + eviction rule.
    internal DbSet<LedgerSnapshotRow> LedgerSnapshots => Set<LedgerSnapshotRow>();

    // DbUp-managed migration tracking. Read-only from the API side —
    // surfaced for the snapshots repo to stamp the current schema
    // version onto each snapshot.
    internal DbSet<SchemaMigrationRow> SchemaMigrations => Set<SchemaMigrationRow>();

    // ADR-0022 normalised tables. Internal access only — the public
    // register surface reads through resolved_transactions (still
    // backed by `transactions` until migration 023 swings the view
    // onto these tables).
    internal DbSet<TxnHeaderRow> TxnHeaders => Set<TxnHeaderRow>();
    internal DbSet<TxnLegRow> TxnLegs => Set<TxnLegRow>();
    internal DbSet<TxnHeaderOverrideRow> TxnHeaderOverrides => Set<TxnHeaderOverrideRow>();
    internal DbSet<TxnLegOverrideRow> TxnLegOverrides => Set<TxnLegOverrideRow>();
    internal DbSet<TxnLegReconRow> TxnLegRecon => Set<TxnLegReconRow>();
    internal DbSet<TxnHeaderTagRow> TxnHeaderTags => Set<TxnHeaderTagRow>();
    internal DbSet<TagRow> Tags => Set<TagRow>();
    // ADR-0047 / migration 124: recurring-reminder series (recurrence
    // metadata + a pointer to the template txn_header). No EF entity existed
    // before — the table was importer-write-only; the reminders feature reads
    // + writes it now.
    internal DbSet<RecurringTransactionRow> RecurringTransactions => Set<RecurringTransactionRow>();
    // ADR-0047 D6 / migration 125: per-(series, date) skip suppressions. A row
    // hides one expanded occurrence from the upcoming agenda and blocks firing
    // it. Repository read+write.
    internal DbSet<RecurringOccurrenceExceptionRow> RecurringOccurrenceExceptions => Set<RecurringOccurrenceExceptionRow>();

    internal DbSet<LoanTermsRow> LoanTerms => Set<LoanTermsRow>();

    /// <summary>user_preferences (ADR-0057 / mig 134) — general per-(user, ledger) prefs.</summary>
    internal DbSet<UserPreferenceRow> UserPreferences => Set<UserPreferenceRow>();

    /// <summary>scheduled_jobs (mig 136) — per-(ledger, job_type) daily scheduler.</summary>
    internal DbSet<ScheduledJobRow> ScheduledJobs => Set<ScheduledJobRow>();

    /// <summary>global_scheduled_jobs (mig 139) — deployment-wide (non-ledger)
    /// daily schedules, e.g. the whole-DB backup; service-role only.</summary>
    internal DbSet<GlobalScheduledJobRow> GlobalScheduledJobs => Set<GlobalScheduledJobRow>();

    /// <summary>drive_sync (mig 142, ADR-0062) — deployment-wide singleton
    /// Google Drive backup-destination config. Service-role only.</summary>
    internal DbSet<DriveSyncRow> DriveSync => Set<DriveSyncRow>();

    /// <summary>backup_settings (mig 161, ADR-0074) — deployment-wide singleton
    /// backup retention policy (GFS tiers). Service-role only.</summary>
    internal DbSet<BackupSettingsRow> BackupSettings => Set<BackupSettingsRow>();

    /// <summary>backup_pins (mig 144, ADR-0062) — "never delete" pins keyed by
    /// backup artifact id. Service-role only.</summary>
    internal DbSet<BackupPinRow> BackupPins => Set<BackupPinRow>();

    /// <summary>system_settings (mig 147, ADR-0063 §D8) — deployment-global
    /// key/value settings (e.g. mcp.enabled). Service-role only.</summary>
    internal DbSet<SystemSettingRow> SystemSettings => Set<SystemSettingRow>();
    // ADR-0092 D2 / migration 191: deployment-level admin audit. Append-only.
    internal DbSet<AdminAuditEventRow> AdminAuditEvents => Set<AdminAuditEventRow>();
    // ADR-0034 / migration 089: per-(header, account) running balance.
    // Read-only from the API perspective; the header-walk trigger family
    // (mig 090) owns the writes.
    internal DbSet<TxnHeaderAccountBalanceRow> TxnHeaderAccountBalances => Set<TxnHeaderAccountBalanceRow>();

    /// <summary>
    /// View-backed read model for <c>user_visible_ledgers</c>. Mapped as
    /// a keyless query type (no PK, read-only) so EF Core won't try to
    /// generate writes against the view. Repositories project from this
    /// to the public <see cref="LedgerSummary"/> DTO.
    /// </summary>
    internal DbSet<UserVisibleLedgerView> UserVisibleLedgers => Set<UserVisibleLedgerView>();

    /// <summary>
    /// View-backed read model for <c>resolved_transactions</c> (migration
    /// 005). Application code reads the register through this view so
    /// the COALESCE-with-overrides logic is centralised in SQL.
    /// </summary>
    internal DbSet<ResolvedTransactionView> ResolvedTransactions => Set<ResolvedTransactionView>();

    /// <summary>account_current_balances view (migration 133) — current balance per account.</summary>
    internal DbSet<AccountCurrentBalanceView> AccountCurrentBalances => Set<AccountCurrentBalanceView>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // OpenIddict (ADR-0063) maps its OAuth AS entities onto this context.
        // Registered in the model (not just on one options builder) so every
        // AppDbContext instance — DI-scoped and ServiceDbContextFactory-built —
        // shares one consistent model; EF caches the model per context type, so
        // a divergent model between the two would conflict. Schema is migration
        // 146 (hand-authored from OpenIddict's own create script).
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<UserRow>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.DisplayName).HasColumnName("display_name");
            b.Property(x => x.Username).HasColumnName("username");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.IsDisabled).HasColumnName("is_disabled");
            b.Property(x => x.IsAdmin).HasColumnName("is_admin");
            b.Property(x => x.LastOpenedLedgerId).HasColumnName("last_opened_ledger_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            // users.last_opened_ledger_id → ledgers(id) ON DELETE SET NULL
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LastOpenedLedgerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WebAuthnCredentialRow>(b =>
        {
            b.ToTable("webauthn_credentials");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.CredentialId).HasColumnName("credential_id");
            b.Property(x => x.PublicKey).HasColumnName("public_key");
            b.Property(x => x.SignatureCounter).HasColumnName("signature_counter");
            b.Property(x => x.Aaguid).HasColumnName("aaguid");
            b.Property(x => x.Transports).HasColumnName("transports");
            b.Property(x => x.Nickname).HasColumnName("nickname");
            b.Property(x => x.RpId).HasColumnName("rp_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionRow>(b =>
        {
            b.ToTable("auth_sessions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.SessionHash).HasColumnName("session_hash");
            b.Property(x => x.UserAgent).HasColumnName("user_agent");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").ValueGeneratedOnAdd();
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<McpAccessTokenRow>(b =>
        {
            b.ToTable("mcp_access_tokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.TokenHash).HasColumnName("token_hash");
            b.Property(x => x.Scopes).HasColumnName("scopes");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<McpToolInvocationRow>(b =>
        {
            b.ToTable("mcp_tool_invocations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.ToolName).HasColumnName("tool_name");
            b.Property(x => x.Arguments).HasColumnName("arguments");
            b.Property(x => x.Result).HasColumnName("result");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.Status).HasColumnName("status");
            b.Property(x => x.CompletedAt).HasColumnName("completed_at");
            b.Property(x => x.TraceId).HasColumnName("trace_id");
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BootstrapTokenRow>(b =>
        {
            b.ToTable("bootstrap_tokens");
            // bootstrap_tokens primary key is token_hash (BYTEA), not a
            // generated UUID — the table holds at most one valid token at
            // a time and the hash is the natural identifier.
            b.HasKey(x => x.TokenHash);
            b.Property(x => x.TokenHash).HasColumnName("token_hash");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            b.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        });

        modelBuilder.Entity<InviteRow>(b =>
        {
            b.ToTable("invites");
            // Like bootstrap_tokens, the PK is the token's SHA-256 (BYTEA).
            b.HasKey(x => x.TokenHash);
            b.Property(x => x.TokenHash).HasColumnName("token_hash");
            b.Property(x => x.Id).HasColumnName("id");
            b.HasAlternateKey(x => x.Id);
            b.Property(x => x.IssuedByUserId).HasColumnName("issued_by_user_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Role).HasColumnName("role");
            b.Property(x => x.GrantsAdmin).HasColumnName("grants_admin");
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            b.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.IssuedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecoveryCodeRow>(b =>
        {
            b.ToTable("recovery_codes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.CodeHash).HasColumnName("code_hash");
            b.Property(x => x.UsedAt).HasColumnName("used_at");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebAuthnPendingChallengeRow>(b =>
        {
            b.ToTable("webauthn_pending_challenges");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Flow).HasColumnName("flow");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.OptionsJson).HasColumnName("options_json");
            b.Property(x => x.MetadataJson).HasColumnName("metadata_json");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            b.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            // user_id → users(id) ON DELETE CASCADE; nullable during the
            // bootstrap setup flow (the user row doesn't exist yet at
            // /begin time).
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LedgerRow>(b =>
        {
            b.ToTable("ledgers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            // ADR-0026 / migration 035: per-ledger LEK columns.
            // Nullable until the lazy backfill catches every existing
            // row; a follow-up migration sets NOT NULL.
            b.Property(x => x.WrappedLek).HasColumnName("wrapped_lek");
            b.Property(x => x.LekKekId).HasColumnName("lek_kek_id");
            b.Property(x => x.LekCreatedAt).HasColumnName("lek_created_at");
        });

        modelBuilder.Entity<UserLedgerGrantRow>(b =>
        {
            b.ToTable("user_ledger_grants");
            b.HasKey(x => new { x.UserId, x.LedgerId });
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Role).HasColumnName("role");
            b.Property(x => x.GrantedAt).HasColumnName("granted_at").ValueGeneratedOnAdd();

            // FKs on this junction matter for EF's insert ordering: a
            // SaveChanges that adds a user + ledger + grant together
            // (e.g. SyntheticLedger.CreateAsync in tests) must INSERT
            // the user and ledger before the grant, or Postgres rejects
            // the grant on FK violation. Configure both FKs explicitly
            // so EF generates the correct topological order.
            b.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<LedgerRow>()
                .WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // user_account_groups + user_account_group_members (migration
        // 033) — user-curated sidebar tabs. FKs declared so EF orders
        // inserts/deletes correctly and ON DELETE CASCADE matches the
        // SQL-side behaviour.
        modelBuilder.Entity<UserAccountGroupRow>(b =>
        {
            b.ToTable("user_account_groups");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.SortOrder).HasColumnName("sort_order");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            b.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<LedgerRow>()
                .WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAccountGroupMemberRow>(b =>
        {
            b.ToTable("user_account_group_members");
            b.HasKey(x => new { x.GroupId, x.AccountId });
            b.Property(x => x.GroupId).HasColumnName("group_id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.AddedAt).HasColumnName("added_at").ValueGeneratedOnAdd();

            b.HasOne<UserAccountGroupRow>()
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AccountRow>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // user_visible_ledgers: read-only join of user_ledger_grants and
        // ledgers per migration 014. Mapped as keyless so EF Core treats
        // it as a query type only.
        modelBuilder.Entity<UserVisibleLedgerView>(b =>
        {
            b.HasNoKey();
            b.ToView("user_visible_ledgers");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.LedgerName).HasColumnName("ledger_name");
            b.Property(x => x.Role).HasColumnName("role");
            b.Property(x => x.GrantedAt).HasColumnName("granted_at");
        });

        modelBuilder.Entity<FeedConnectionRow>(b =>
        {
            b.ToTable("feed_connections");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Provider).HasColumnName("provider");
            b.Property(x => x.ProviderItemId).HasColumnName("provider_item_id");
            b.Property(x => x.Status).HasColumnName("status");
            b.Property(x => x.LastSyncedAt).HasColumnName("last_synced_at");
            b.Property(x => x.TokenExpiresAt).HasColumnName("token_expires_at");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            // Migration 036: per-ADR-0026 sealed access URL + audit
            // fields for the SimpleFIN slice-1 endpoint.
            b.Property(x => x.AccessUrlCiphertext).HasColumnName("access_url_ciphertext");
            b.Property(x => x.InstitutionName).HasColumnName("institution_name");
            b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            // feed_connections.ledger_id → ledgers(id) ON DELETE RESTRICT
            // (ADR-0020 Phase A anchor: a ledger with feed connections must
            // be explicitly emptied before it can be deleted).
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Slice 2c.4 (migration 041): per-connection account
        // directory. Composite UNIQUE on (feed_connection_id,
        // external_id) backs the upsert key. Cascade-deletes
        // with the parent feed_connection.
        modelBuilder.Entity<FeedConnectionAccountRow>(b =>
        {
            b.ToTable("feed_connection_accounts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.FeedConnectionId).HasColumnName("feed_connection_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.OrgName).HasColumnName("org_name");
            b.Property(x => x.Currency).HasColumnName("currency");
            b.Property(x => x.Balance).HasColumnName("balance");
            b.Property(x => x.BalanceAt).HasColumnName("balance_at");
            b.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            // Migration 080 — ADR-0031 follow-up: per-account raw JSON.
            // JSONB so `last_provider_raw_payload->'holdings'` works for
            // ad-hoc classifier-iteration queries.
            b.Property(x => x.LastProviderRawPayload)
                .HasColumnName("last_provider_raw_payload")
                .HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<FeedConnectionRow>()
                .WithMany()
                .HasForeignKey(x => x.FeedConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.FeedConnectionId, x.ExternalId })
                .IsUnique()
                .HasDatabaseName("uq_feed_connection_accounts_external");
        });

        // Sync activity log (slice 2c.1, migration 038). Three
        // tables: parent run + two child detail tables (errors
        // + promotions). All cascade-delete on the parent.
        modelBuilder.Entity<LedgerOperationRow>(b =>
        {
            b.ToTable("ledger_operations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Family).HasColumnName("family");
            b.Property(x => x.ProviderKey).HasColumnName("provider_key");
            b.Property(x => x.TriggeredVia).HasColumnName("triggered_via");
            b.Property(x => x.FeedConnectionId).HasColumnName("feed_connection_id");
            b.Property(x => x.TriggeredByUserId).HasColumnName("triggered_by_user_id");
            b.Property(x => x.Status).HasColumnName("status");
            b.Property(x => x.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            b.Property(x => x.ErrorMessage).HasColumnName("error_message");
            b.Property(x => x.StartedAt).HasColumnName("started_at").ValueGeneratedOnAdd();
            b.Property(x => x.CompletedAt).HasColumnName("completed_at");
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
            // Mig 121 (ledger isolation Phase 2): the DB enforces a
            // COMPOSITE FK (feed_connection_id, ledger_id) →
            // feed_connections(id, ledger_id) with
            // `ON DELETE SET NULL (feed_connection_id)` — only the FK
            // column is nulled, ledger_id (NOT NULL) is untouched.
            // EF can't model a composite SetNull where one component
            // (LedgerId) is non-nullable, so we keep the single-column
            // navigation here; isolation is fully enforced by the DB
            // composite FK regardless of EF's model.
            b.HasOne<FeedConnectionRow>().WithMany()
                .HasForeignKey(x => x.FeedConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.TriggeredByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LedgerOperationErrorRow>(b =>
        {
            b.ToTable("ledger_operation_errors");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.LedgerOperationId).HasColumnName("ledger_operation_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Code).HasColumnName("code");
            b.Property(x => x.Message).HasColumnName("message");
            b.Property(x => x.SimpleFinConnectionId).HasColumnName("simplefin_connection_id");
            b.Property(x => x.SimpleFinAccountId).HasColumnName("simplefin_account_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<LedgerOperationRow>().WithMany()
                .HasForeignKey(x => x.LedgerOperationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LedgerOperationPromotionRow>(b =>
        {
            b.ToTable("ledger_operation_promotions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.LedgerOperationId).HasColumnName("ledger_operation_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.HeaderId).HasColumnName("header_id");
            b.Property(x => x.WasAmount).HasColumnName("was_amount");
            b.Property(x => x.BecameAmount).HasColumnName("became_amount");
            b.Property(x => x.PromotedAt).HasColumnName("promoted_at").ValueGeneratedOnAdd();
            b.HasOne<LedgerOperationRow>().WithMany()
                .HasForeignKey(x => x.LedgerOperationId)
                .OnDelete(DeleteBehavior.Cascade);
            // Mig 121 (ledger isolation Phase 2): composite FK
            // (header_id, ledger_id) → txn_headers(id, ledger_id) so a
            // promotion can only reference a header in its own ledger.
            // CASCADE has no NOT-NULL-column conflict (it deletes the
            // whole row), so EF models the composite directly.
            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => new { x.HeaderId, x.LedgerId })
                .HasPrincipalKey(h => new { h.Id, h.LedgerId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountRow>(b =>
        {
            b.ToTable("accounts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.ParentId).HasColumnName("parent_id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.AccountType).HasColumnName("account_type");
            b.Property(x => x.CategoryKind).HasColumnName("category_kind");
            b.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            b.Property(x => x.OpeningBalance).HasColumnName("opening_balance");
            b.Property(x => x.OpenedOn).HasColumnName("opened_on");
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.TaxStatus).HasColumnName("tax_status");   // ADR-0066 / mig 149
            b.Property(x => x.FeedConnectionId).HasColumnName("feed_connection_id");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            b.Property(x => x.IsSystem).HasColumnName("is_system");
            b.Property(x => x.HoldingsAccountId).HasColumnName("holdings_account_id");
            b.Property(x => x.Notes).HasColumnName("notes");
            b.Property(x => x.AccountNumber).HasColumnName("account_number");
            b.Property(x => x.InstitutionName).HasColumnName("institution_name");
            b.Property(x => x.RoutingNumber).HasColumnName("routing_number");
            b.Property(x => x.AccountUrl).HasColumnName("account_url");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.LastSimpleFinSyncAt).HasColumnName("last_simplefin_sync_at");
            b.Property(x => x.IsTradeCommission).HasColumnName("is_trade_commission");
            // Mig 110 / ADR-0035 §3: verbatim per-account provider JSON.
            b.Property(x => x.ProviderRawPayload).HasColumnName("provider_raw_payload").HasColumnType("jsonb");

            // accounts.ledger_id → ledgers(id) ON DELETE RESTRICT
            // (ADR-0020 Phase A anchor).
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);

            // accounts.parent_id → accounts(id) ON DELETE SET NULL
            // (self-FK; only legal on category rows).
            //
            // Mig 121 (ledger isolation Phase 2): the DB enforces a
            // COMPOSITE FK (parent_id, ledger_id) → accounts(id, ledger_id)
            // with `ON DELETE SET NULL (parent_id)` — Postgres nulls only
            // the FK column, leaving the row's NOT-NULL ledger_id intact.
            // EF cannot model a composite SetNull when one component
            // (LedgerId) is non-nullable (and this is a self-ref to boot),
            // so we keep the single-column navigation. The DB composite FK
            // fully enforces cross-ledger isolation; EF just models the
            // column nav for change-tracking.
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.SetNull);

            // accounts.feed_connection_id → feed_connections(id) ON DELETE SET NULL
            //
            // Mig 121: DB enforces COMPOSITE FK (feed_connection_id,
            // ledger_id) → feed_connections(id, ledger_id) with
            // `ON DELETE SET NULL (feed_connection_id)`. Single-column nav
            // kept for the same non-nullable-LedgerId reason as above; DB
            // composite FK enforces isolation.
            b.HasOne<FeedConnectionRow>().WithMany()
                .HasForeignKey(x => x.FeedConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            // accounts.holdings_account_id → accounts(id) ON DELETE SET NULL
            // (self-FK linking a brokerage to its system-managed holdings
            // sibling per ADR-0019). Distinct relationship from parent_id
            // — both are self-FKs but mean different things, so they get
            // separate HasOne configurations.
            //
            // Mig 121: DB enforces COMPOSITE FK (holdings_account_id,
            // ledger_id) → accounts(id, ledger_id) with
            // `ON DELETE SET NULL (holdings_account_id)`. Single-column nav
            // kept (non-nullable LedgerId + self-ref); DB composite FK
            // enforces isolation.
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.HoldingsAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // loan_terms (migration 127, ADR-0050). The importer seeds it (Dapper);
        // the API reads it to compute the loan split AND (slice 3) writes it from
        // the account editor — "import seeds once, Coffer owns it" (D10). Writes
        // go through Add (insert) / ExecuteUpdateAsync (update); the create path
        // saves the loan account BEFORE its terms, so the (account_id, ledger_id)
        // FK is satisfied without an EF navigation (the interest/escrow FKs point
        // at pre-existing accounts). FKs remain DB-enforced.
        modelBuilder.Entity<LoanTermsRow>(b =>
        {
            b.ToTable("loan_terms");
            b.HasKey(x => x.AccountId);
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.OriginalPrincipal).HasColumnName("original_principal");
            b.Property(x => x.AnnualInterestRate).HasColumnName("annual_interest_rate");
            b.Property(x => x.Points).HasColumnName("points");
            b.Property(x => x.PaymentCount).HasColumnName("payment_count");
            b.Property(x => x.PaymentsPerYear).HasColumnName("payments_per_year");
            b.Property(x => x.FirstPaymentDate).HasColumnName("first_payment_date");
            b.Property(x => x.EscrowAmount).HasColumnName("escrow_amount");
            b.Property(x => x.InterestAccountId).HasColumnName("interest_account_id");
            b.Property(x => x.EscrowAccountId).HasColumnName("escrow_account_id");
            b.Property(x => x.PaymentIsComputed).HasColumnName("payment_is_computed");
            b.Property(x => x.FixedPayment).HasColumnName("fixed_payment");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        // user_preferences (ADR-0057 / mig 134): general per-(user, ledger)
        // preference store. Composite key (user, ledger, namespace); value is a
        // namespace-typed jsonb document.
        modelBuilder.Entity<UserPreferenceRow>(b =>
        {
            b.ToTable("user_preferences");
            b.HasKey(x => new { x.UserId, x.LedgerId, x.Namespace });
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Namespace).HasColumnName("namespace");
            b.Property(x => x.ValueJson).HasColumnName("value").HasColumnType("jsonb");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<LedgerRow>().WithMany().HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // scheduled_jobs (mig 136): per-(ledger, job_type) daily scheduler.
        modelBuilder.Entity<ScheduledJobRow>(b =>
        {
            b.ToTable("scheduled_jobs");
            b.HasKey(x => new { x.LedgerId, x.JobType });
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.JobType).HasColumnName("job_type");
            b.Property(x => x.Enabled).HasColumnName("enabled");
            b.Property(x => x.HourLocal).HasColumnName("hour_local");
            b.Property(x => x.MinuteLocal).HasColumnName("minute_local");
            b.Property(x => x.Timezone).HasColumnName("timezone");
            b.Property(x => x.ConfiguredByUserId).HasColumnName("configured_by_user_id");
            b.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            b.Property(x => x.NextRunAt).HasColumnName("next_run_at");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne<LedgerRow>().WithMany().HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.ConfiguredByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // global_scheduled_jobs (mig 139): deployment-wide (non-ledger) daily
        // schedules, keyed by job_type alone. The backup row also holds the
        // master-KEK-sealed backup passphrase. Service-role only (no per-ledger
        // RLS predicate); the user FK is SET NULL on delete (nullable).
        modelBuilder.Entity<GlobalScheduledJobRow>(b =>
        {
            b.ToTable("global_scheduled_jobs");
            b.HasKey(x => x.JobType);
            b.Property(x => x.JobType).HasColumnName("job_type");
            b.Property(x => x.Enabled).HasColumnName("enabled");
            b.Property(x => x.HourLocal).HasColumnName("hour_local");
            b.Property(x => x.MinuteLocal).HasColumnName("minute_local");
            b.Property(x => x.Timezone).HasColumnName("timezone");
            b.Property(x => x.PassphraseCiphertext).HasColumnName("passphrase_ciphertext");
            b.Property(x => x.ConfiguredByUserId).HasColumnName("configured_by_user_id");
            b.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            b.Property(x => x.NextRunAt).HasColumnName("next_run_at");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.ConfiguredByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // drive_sync (mig 142, ADR-0062): deployment-wide singleton Google
        // Drive backup-destination config. Service-role only; the OAuth blob is
        // master-KEK-sealed.
        modelBuilder.Entity<DriveSyncRow>(b =>
        {
            b.ToTable("drive_sync");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Enabled).HasColumnName("enabled");
            b.Property(x => x.OauthCiphertext).HasColumnName("oauth_ciphertext");
            b.Property(x => x.FolderId).HasColumnName("folder_id");
            b.Property(x => x.FolderName).HasColumnName("folder_name");
            b.Property(x => x.InstallId).HasColumnName("install_id");
            b.Property(x => x.ConnectedEmail).HasColumnName("connected_email");
            b.Property(x => x.LastSyncAt).HasColumnName("last_sync_at");
            b.Property(x => x.LastSyncStatus).HasColumnName("last_sync_status");
            b.Property(x => x.LastSyncError).HasColumnName("last_sync_error");
            b.Property(x => x.ConfiguredByUserId).HasColumnName("configured_by_user_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.ConfiguredByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // backup_settings (mig 161, ADR-0074): singleton retention policy that
        // governs local pruning + the Drive mirror. Service-role only.
        modelBuilder.Entity<BackupSettingsRow>(b =>
        {
            b.ToTable("backup_settings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.RetentionDaily).HasColumnName("retention_daily");
            b.Property(x => x.RetentionWeekly).HasColumnName("retention_weekly");
            b.Property(x => x.RetentionMonthly).HasColumnName("retention_monthly");
            b.Property(x => x.ConfiguredByUserId).HasColumnName("configured_by_user_id");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.ConfiguredByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BackupPinRow>(b =>
        {
            b.ToTable("backup_pins");
            b.HasKey(x => x.ArtifactId);
            b.Property(x => x.ArtifactId).HasColumnName("artifact_id");
            b.Property(x => x.PinnedByUserId).HasColumnName("pinned_by_user_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.PinnedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // system_settings (mig 147, ADR-0063 §D8): deployment-global key/value
        // settings store, keyed by `key`; `value` is JSONB (raw text on the
        // entity). Service-role only; updated_by SET NULL on user delete.
        modelBuilder.Entity<SystemSettingRow>(b =>
        {
            b.ToTable("system_settings");
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasColumnName("key");
            b.Property(x => x.ValueJson).HasColumnName("value").HasColumnType("jsonb");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UpdatedBy)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Deployment-level admin audit (ADR-0092 D2, migration 191). Append-only:
        // the entity is init-only and nothing here updates or deletes.
        modelBuilder.Entity<AdminAuditEventRow>(b =>
        {
            b.ToTable("admin_audit_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            b.Property(x => x.Action).HasColumnName("action");
            b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            b.Property(x => x.Detail).HasColumnName("detail");
            b.HasOne<UserRow>().WithMany().HasForeignKey(x => x.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ----- Investment surface (read-only at this layer) ----------
        modelBuilder.Entity<SecurityRow>(b =>
        {
            b.ToTable("securities");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Ticker).HasColumnName("ticker");
            b.Property(x => x.Cusip).HasColumnName("cusip");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.AssetClass).HasColumnName("asset_class");
            // Rich classification (ADR-0067 / mig 150).
            b.Property(x => x.VehicleType).HasColumnName("vehicle_type");
            b.Property(x => x.Region).HasColumnName("region");
            b.Property(x => x.EquitySize).HasColumnName("equity_size");
            b.Property(x => x.EquityStyle).HasColumnName("equity_style");
            b.Property(x => x.FiDuration).HasColumnName("fi_duration");
            b.Property(x => x.FiCredit).HasColumnName("fi_credit");
            b.Property(x => x.TaxCharacter).HasColumnName("tax_character");
            b.Property(x => x.ClassificationSource).HasColumnName("classification_source");
            b.Property(x => x.ClassificationConfidence).HasColumnName("classification_confidence");
            b.Property(x => x.Exchange).HasColumnName("exchange");
            b.Property(x => x.IsActive).HasColumnName("is_active");
            // ADR-0054 D2 (slice A2): market-data override knobs.
            b.Property(x => x.QuoteSymbol).HasColumnName("quote_symbol");
            b.Property(x => x.AutoPrice).HasColumnName("auto_price");
            b.Property(x => x.QuoteSymbolPublic).HasColumnName("quote_symbol_public");
            b.Property(x => x.ShareDecimals).HasColumnName("share_decimals");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            // securities.ledger_id → ledgers(id) ON DELETE RESTRICT
            // (ADR-0020 Phase A: securities are ledger-scoped via the
            // ledger_id column, not through the account FK chain).
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // security_components (mig 150, ADR-0067): multi-asset look-through.
        modelBuilder.Entity<SecurityComponentRow>(b =>
        {
            b.ToTable("security_components");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            b.Property(x => x.ComponentAssetClass).HasColumnName("component_asset_class");
            b.Property(x => x.ComponentRegion).HasColumnName("component_region");
            b.Property(x => x.Weight).HasColumnName("weight");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<SecurityRow>().WithMany()
                .HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HoldingRow>(b =>
        {
            b.ToTable("holdings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            // Migration 049: denormalized ledger; the DB composite
            // FKs (account_id, ledger_id) and (security_id, ledger_id)
            // both reference this column. EF tracks only the simple
            // FKs (composite FKs without a navigation property are
            // awkward in EF); the DB is the authority on ledger
            // coherence.
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.CostBasis).HasColumnName("cost_basis");
            b.Property(x => x.AsOf).HasColumnName("as_of");

            // holdings.account_id → accounts(id, ledger_id) ON DELETE RESTRICT
            // (DB composite FK; EF declares the single-column FK only)
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // holdings.security_id → securities(id, ledger_id) ON DELETE RESTRICT
            b.HasOne<SecurityRow>().WithMany()
                .HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LotRow>(b =>
        {
            b.ToTable("lots");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.HoldingId).HasColumnName("holding_id");
            b.Property(x => x.LegId).HasColumnName("leg_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.UnitCost).HasColumnName("unit_cost");
            b.Property(x => x.AcquiredAt).HasColumnName("acquired_at");
            b.Property(x => x.IsClosed).HasColumnName("is_closed");

            // lots.holding_id → holdings(id) ON DELETE CASCADE.
            b.HasOne<HoldingRow>().WithMany()
                .HasForeignKey(x => x.HoldingId)
                .OnDelete(DeleteBehavior.Cascade);
            // lots.leg_id → txn_legs(id) ON DELETE CASCADE.
            b.HasOne<TxnLegRow>().WithMany()
                .HasForeignKey(x => x.LegId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // realized_gains (mig 148, ADR-0064): per-sale FIFO realized gains,
        // written by the recompute function. FKs declared per engineering
        // standards even though the recompute owns all writes.
        modelBuilder.Entity<RealizedGainRow>(b =>
        {
            b.ToTable("realized_gains");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            b.Property(x => x.SellLegId).HasColumnName("sell_leg_id");
            b.Property(x => x.SoldAt).HasColumnName("sold_at");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.Proceeds).HasColumnName("proceeds");
            b.Property(x => x.CostBasisSold).HasColumnName("cost_basis_sold");
            b.Property(x => x.RealizedGain).HasColumnName("realized_gain");
            // Mig 169 (ADR-0064 D2): long-term portion of the sale.
            b.Property(x => x.ProceedsLt).HasColumnName("proceeds_lt");
            b.Property(x => x.CostBasisSoldLt).HasColumnName("cost_basis_sold_lt");
            b.Property(x => x.RealizedGainLt).HasColumnName("realized_gain_lt");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<LedgerRow>().WithMany().HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AccountRow>().WithMany().HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<SecurityRow>().WithMany().HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TxnLegRow>().WithMany().HasForeignKey(x => x.SellLegId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SecurityPriceRow>(b =>
        {
            b.ToTable("security_prices");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            // Migration 049: denormalized ledger.
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Price).HasColumnName("price");
            b.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            // ADR-0070: one price per (security, day). The entity is a DateOnly,
            // which Npgsql maps to a `date` column natively — symmetric round-trip,
            // no Kind, no TZ day-shift. No column-type override needed.
            b.Property(x => x.PriceDate).HasColumnName("price_date");
            b.Property(x => x.High).HasColumnName("high");
            b.Property(x => x.Low).HasColumnName("low");
            b.Property(x => x.Volume).HasColumnName("volume");
            // ADR-0054 D2: price origin tag (import / fetch / manual).
            b.Property(x => x.Source).HasColumnName("source");

            // security_prices.security_id → securities(id) ON DELETE CASCADE
            b.HasOne<SecurityRow>().WithMany()
                .HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ADR-0031 Phase 3a: per-provider security id → securities.id
        // map. Composite FK (security_id, ledger_id) per migration 049
        // pattern so the row's ledger_id stays coherent with the
        // referenced security's ledger (a structural guarantee for
        // the flattened RLS policy from migration 075).
        modelBuilder.Entity<ProviderSecurityMappingRow>(b =>
        {
            b.ToTable("provider_security_mappings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.ProviderKey).HasColumnName("provider_key");
            b.Property(x => x.ProviderSecurityId).HasColumnName("provider_security_id");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");

            // provider_security_mappings.ledger_id → ledgers(id) ON DELETE CASCADE
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);

            // provider_security_mappings.(security_id, ledger_id)
            //   → securities(id, ledger_id) ON DELETE RESTRICT
            // Mirrors the composite FK pattern from migration 049.
            // RESTRICT so deleting a security doesn't silently orphan
            // a mapping — the user must re-link or delete the mapping
            // first.
            b.HasOne<SecurityRow>().WithMany()
                .HasForeignKey(x => new { x.SecurityId, x.LedgerId })
                .HasPrincipalKey(s => new { s.Id, s.LedgerId })
                .OnDelete(DeleteBehavior.Restrict);

            // provider_security_mappings.created_by_user_id → users(id)
            // ON DELETE SET NULL — audit attribution survives user
            // deletion as NULL.
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // DbUp's __schema_migrations table — read-only EF mapping so
        // the snapshots repo can stamp the live schema version onto
        // new snapshots. The column names are DbUp's defaults.
        modelBuilder.Entity<SchemaMigrationRow>(b =>
        {
            b.ToTable("__schema_migrations");
            b.HasKey(x => x.SchemaVersionsId);
            b.Property(x => x.SchemaVersionsId).HasColumnName("schemaversionsid");
            b.Property(x => x.ScriptName).HasColumnName("scriptname");
            b.Property(x => x.Applied).HasColumnName("applied");
        });

        // ADR-0037 / mig 111: ledger_snapshots. Server-side capped
        // pre-risk-safety-net snapshots of the user-curated graph.
        modelBuilder.Entity<LedgerSnapshotRow>(b =>
        {
            b.ToTable("ledger_snapshots");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            b.Property(x => x.Kind).HasColumnName("kind");
            b.Property(x => x.Description).HasColumnName("description");
            b.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            b.Property(x => x.Content).HasColumnName("content");
            b.Property(x => x.ContentSizeUncompressed).HasColumnName("content_size_uncompressed");
            // v2 payload (mig 179): server-side jsonb; NON-NULL marks a v2 snapshot.
            // Never SELECTed in full — callers project `ContentJson != null` only.
            b.Property(x => x.ContentJson).HasColumnName("content_json").HasColumnType("jsonb");

            // ledger_snapshots.ledger_id → ledgers(id) ON DELETE CASCADE
            // Snapshots are per-ledger artifacts; deleting the ledger
            // sweeps them.
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);

            // ledger_snapshots.created_by_user_id → users(id) ON DELETE RESTRICT
            // Auto-snaps reference the seeded system user (which we
            // never delete); manual snaps reference real users.
            // RESTRICT prevents accidental loss of audit attribution
            // by user deletion — if a real user is being deleted,
            // their snapshots must be handled first.
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ADR-0022 normalised tables (migration 022). Configured up-
        // front so the API can write through the EF DbSets once
        // migration 023 swings the view onto these tables. Until then
        // the read surface (resolved_transactions) is still backed by
        // `transactions`; these entities are registered but unused.
        modelBuilder.Entity<TxnHeaderRow>(b =>
        {
            b.ToTable("txn_headers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Origin).HasColumnName("origin");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            b.Property(x => x.Payee).HasColumnName("payee");
            b.Property(x => x.Memo).HasColumnName("memo");
            b.Property(x => x.PostedAt).HasColumnName("posted_at");
            b.Property(x => x.TransactedAt).HasColumnName("transacted_at");
            b.Property(x => x.CheckNumber).HasColumnName("check_number");
            b.Property(x => x.IsPending).HasColumnName("is_pending");
            b.Property(x => x.IsHidden).HasColumnName("is_hidden");
            b.Property(x => x.IsMergedInto).HasColumnName("is_merged_into");
            b.Property(x => x.ImportSource).HasColumnName("import_source");
            b.Property(x => x.OnlineMatchFitid).HasColumnName("online_match_fitid");
            b.Property(x => x.OnlineMatchFiId).HasColumnName("online_match_fi_id");
            b.Property(x => x.NeedsReview).HasColumnName("needs_review");
            // Migration 047 — investment-action moved from per-leg to
            // per-header (one event = one action across all postings).
            b.Property(x => x.Action).HasColumnName("action");
            // Migration 076 — ADR-0031 Phase 3c ingest classifier hints.
            // (`ingest_security_id` retired in migration 115 / ADR-0038:
            // resolved dynamically by `resolved_transactions` via a JOIN
            // against `provider_security_mappings`; no header column.)
            b.Property(x => x.IngestActionHint).HasColumnName("ingest_action_hint");
            // Migration 113 — OFX investment-row prefill carriers.
            b.Property(x => x.IngestShares).HasColumnName("ingest_shares");
            b.Property(x => x.IngestUnitPrice).HasColumnName("ingest_unit_price");
            b.Property(x => x.IngestFee).HasColumnName("ingest_fee");
            // Migration 114 — OFX security ticker hint (provider_id
            // string used to record the provider_security_mapping
            // on Accept, so future ingests auto-resolve).
            b.Property(x => x.IngestSecurityTickerHint).HasColumnName("ingest_security_ticker_hint");
            // Migration 078 — ADR-0031 follow-up: raw provider JSON.
            // EF stores string columns as TEXT by default; the
            // HasColumnType("jsonb") override gives Postgres the
            // proper type so the user can run JSONB operators
            // (`payload->>'description'`) for ad-hoc classifier
            // iteration without a per-query cast.
            b.Property(x => x.ProviderRawPayload)
                .HasColumnName("provider_raw_payload")
                .HasColumnType("jsonb");
            // Mig 107: register provenance / merge-winner.
            b.Property(x => x.ProviderKey).HasColumnName("provider_key");
            b.Property(x => x.IsMergeWinner).HasColumnName("is_merge_winner");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            // ADR-0034 v2 (migration 095): monotonic insertion-order
            // tiebreaker. Populated by txn_headers_seq SEQUENCE via
            // DEFAULT nextval(); EF treats it as DB-generated.
            b.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            // Mig 124 (ADR-0047): reminder template discriminator + the
            // fired-occurrence back-reference.
            b.Property(x => x.IsRecurringTemplate).HasColumnName("is_recurring_template");
            b.Property(x => x.RecurringTransactionId).HasColumnName("recurring_transaction_id");
            b.Property(x => x.OccurrenceDate).HasColumnName("occurrence_date");

            // FKs per engineering-standards §4.2.2 — every REFERENCES on
            // the schema is configured on the entity.
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
            // Mig 121 (ledger isolation Phase 2): DB enforces COMPOSITE FK
            // (is_merged_into, ledger_id) → txn_headers(id, ledger_id) with
            // `ON DELETE SET NULL (is_merged_into)` (self-ref). EF cannot
            // model a composite SetNull when one component (LedgerId) is
            // non-nullable, so we keep the single-column self-ref nav; the
            // DB composite FK fully enforces cross-ledger isolation.
            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => x.IsMergedInto)
                .OnDelete(DeleteBehavior.SetNull);
            // Mig 124 (ADR-0047): fired occurrence → its series. DB enforces
            // composite (recurring_transaction_id, ledger_id) →
            // recurring_transactions(id, ledger_id) ON DELETE SET NULL; EF
            // models the single-column nav (same pattern as is_merged_into).
            b.HasOne<RecurringTransactionRow>().WithMany()
                .HasForeignKey(x => x.RecurringTransactionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RecurringTransactionRow>(b =>
        {
            b.ToTable("recurring_transactions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            // Mig 124 (ADR-0047): recurrence metadata.
            b.Property(x => x.Rrule).HasColumnName("rrule");
            b.Property(x => x.SourcePayload).HasColumnName("source_payload").HasColumnType("jsonb");
            b.Property(x => x.AutoCommitDaysBefore).HasColumnName("auto_commit_days_before");
            b.Property(x => x.TemplateHeaderId).HasColumnName("template_header_id");
            b.Property(x => x.SourceAccountId).HasColumnName("source_account_id");
            b.Property(x => x.StartDate).HasColumnName("start_date");
            b.Property(x => x.EndDate).HasColumnName("end_date");
            b.Property(x => x.NextDueDate).HasColumnName("next_due_date");
            b.Property(x => x.LastAcknowledgedDate).HasColumnName("last_acknowledged_date");
            b.Property(x => x.IsLoanReminder).HasColumnName("is_loan_reminder");
            b.Property(x => x.LoanAccountId).HasColumnName("loan_account_id");
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.Origin).HasColumnName("origin");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
            // Mig 124 (ADR-0047/0048): template_header_id → txn_headers. DB
            // enforces composite (template_header_id, ledger_id) →
            // txn_headers(id, ledger_id) ON DELETE RESTRICT, DEFERRABLE
            // INITIALLY DEFERRED (mutual ref with
            // txn_headers.recurring_transaction_id). EF models the single-
            // column nav; the DB composite FK enforces ledger coherence.
            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => x.TemplateHeaderId)
                .OnDelete(DeleteBehavior.Restrict);
            // Mig 125 (ADR-0047): source_account_id -> accounts. DB enforces the
            // composite (source_account_id, ledger_id) -> accounts(id, ledger_id)
            // ON DELETE RESTRICT; EF models the single-column nav.
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.SourceAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            // Mig 168: loan_account_id -> accounts (the managed loan-payment
            // reminder link). DB enforces composite (loan_account_id, ledger_id)
            // -> accounts(id, ledger_id) ON DELETE RESTRICT + a partial unique
            // index (one managed reminder per loan). EF models the single-column nav.
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.LoanAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecurringOccurrenceExceptionRow>(b =>
        {
            // ADR-0047 D6 / migration 125: one suppressed (series, date) slot.
            b.ToTable("recurring_occurrence_exceptions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.RecurringTransactionId).HasColumnName("recurring_transaction_id");
            b.Property(x => x.OccurrenceDate).HasColumnName("occurrence_date");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");

            // FKs per engineering-standards §4.2.2 — every REFERENCES configured.
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Cascade);
            // DB enforces composite (recurring_transaction_id, ledger_id) ->
            // recurring_transactions(id, ledger_id) ON DELETE CASCADE; EF models
            // the single-column nav (same pattern as the fired-occurrence link).
            b.HasOne<RecurringTransactionRow>().WithMany()
                .HasForeignKey(x => x.RecurringTransactionId)
                .OnDelete(DeleteBehavior.Cascade);
            // created_by_user_id -> users(id) ON DELETE SET NULL.
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TxnLegRow>(b =>
        {
            b.ToTable("txn_legs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.HeaderId).HasColumnName("header_id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            // Migration 049: denormalized from txn_headers.ledger_id.
            // DB composite FKs lock all three references (header,
            // account, security-when-set) to the same ledger.
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.PostingIndex).HasColumnName("posting_index");
            b.Property(x => x.LegMemo).HasColumnName("leg_memo");
            b.Property(x => x.Amount).HasColumnName("amount");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.UnitPrice).HasColumnName("unit_price");
            b.Property(x => x.PostingRole).HasColumnName("posting_role");
            // Denormalized posting counts (migration 120, ADR-0036).
            // ValueGeneratedOnAddOrUpdate so EF treats them as fully
            // DB-owned: it omits them from INSERT (the DB default of 1
            // applies) and never emits them in UPDATE — the recompute fn
            // (fn_recompute_posting_counts_for_header) is the sole writer.
            // EF only ever READS these (e.g. the originating predicate in
            // BulkTransactionsRepository.BuildSelectionQuery).
            b.Property(x => x.AccountPostingsOnHeader)
                .HasColumnName("account_postings_on_header")
                .ValueGeneratedOnAddOrUpdate();
            b.Property(x => x.HeaderTotalPostings)
                .HasColumnName("header_total_postings")
                .ValueGeneratedOnAddOrUpdate();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => x.HeaderId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SecurityRow>().WithMany()
                .HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TxnHeaderAccountBalanceRow>(b =>
        {
            b.ToTable("txn_header_account_balances");
            // Composite primary key matches the DB PRIMARY KEY (header_id, account_id).
            b.HasKey(x => new { x.HeaderId, x.AccountId });
            b.Property(x => x.HeaderId).HasColumnName("header_id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.BalanceAfter).HasColumnName("balance_after");
            // Mig 098: per-step delta stored alongside the cumulative
            // balance_after. Populated by the recompute trigger.
            b.Property(x => x.NetAmount).HasColumnName("net_amount");
            // FKs are configured in the DB via composite constraints
            // (mig 089). EF needs to know the relationships only for
            // query path; OnDelete declarations mirror the DB.
            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => x.HeaderId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AccountRow>().WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TxnHeaderOverrideRow>(b =>
        {
            b.ToTable("txn_header_overrides");
            b.HasKey(x => x.HeaderId);
            b.Property(x => x.HeaderId).HasColumnName("header_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Payee).HasColumnName("payee");
            b.Property(x => x.Memo).HasColumnName("memo");
            b.Property(x => x.PostedAt).HasColumnName("posted_at");
            b.Property(x => x.TransactedAt).HasColumnName("transacted_at");
            b.Property(x => x.CheckNumber).HasColumnName("check_number");
            b.Property(x => x.IsHidden).HasColumnName("is_hidden");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAdd();
            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => x.HeaderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TxnLegOverrideRow>(b =>
        {
            b.ToTable("txn_leg_overrides");
            b.HasKey(x => x.LegId);
            b.Property(x => x.LegId).HasColumnName("leg_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.LegMemo).HasColumnName("leg_memo");
            b.Property(x => x.Amount).HasColumnName("amount");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAdd();
            b.HasOne<TxnLegRow>().WithMany()
                .HasForeignKey(x => x.LegId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Per-leg reconciliation overlay (ADR-0082, migration 171).
        modelBuilder.Entity<TxnLegReconRow>(b =>
        {
            b.ToTable("txn_leg_recon");
            b.HasKey(x => x.LegId);
            b.Property(x => x.LegId).HasColumnName("leg_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Status).HasColumnName("status");
            b.Property(x => x.ClearedAt).HasColumnName("cleared_at");
            b.Property(x => x.ClearedByUserId).HasColumnName("cleared_by_user_id");
            b.HasOne<TxnLegRow>().WithMany()
                .HasForeignKey(x => x.LegId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<UserRow>().WithMany()
                .HasForeignKey(x => x.ClearedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TagRow>(b =>
        {
            b.ToTable("tags");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.Color).HasColumnName("color");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            // tags.ledger_id → ledgers(id) ON DELETE RESTRICT
            // (Phase A anchor per ADR-0020).
            b.HasOne<LedgerRow>().WithMany()
                .HasForeignKey(x => x.LedgerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TxnHeaderTagRow>(b =>
        {
            b.ToTable("txn_header_tags");
            b.HasKey(x => new { x.HeaderId, x.TagId });
            b.Property(x => x.HeaderId).HasColumnName("header_id");
            b.Property(x => x.TagId).HasColumnName("tag_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            b.HasOne<TxnHeaderRow>().WithMany()
                .HasForeignKey(x => x.HeaderId)
                .OnDelete(DeleteBehavior.Cascade);
            // txn_header_tags.tag_id → tags(id) ON DELETE CASCADE
            // (mirrors the DB schema; PATCH-tags slice 2c.6b made
            // the tag entity first-class so this FK can be expressed
            // in the model).
            b.HasOne<TagRow>().WithMany()
                .HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // resolved_transactions: COALESCE(overrides, feed) view from
        // migration 005. Keyless because writes are forbidden — the
        // importer and API only INSERT into the underlying tables.
        modelBuilder.Entity<ResolvedTransactionView>(b =>
        {
            b.HasNoKey();
            b.ToView("resolved_transactions");
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.Payee).HasColumnName("payee");
            b.Property(x => x.Memo).HasColumnName("memo");
            b.Property(x => x.Amount).HasColumnName("amount");
            b.Property(x => x.PostedAt).HasColumnName("posted_at");
            b.Property(x => x.TransactedAt).HasColumnName("transacted_at");
            b.Property(x => x.Status).HasColumnName("status");
            b.Property(x => x.IsHidden).HasColumnName("is_hidden");
            b.Property(x => x.HasOverrides).HasColumnName("has_overrides");
            b.Property(x => x.BalanceAfter).HasColumnName("balance_after");
            b.Property(x => x.Origin).HasColumnName("origin");
            b.Property(x => x.IsPending).HasColumnName("is_pending");
            b.Property(x => x.IsMergedInto).HasColumnName("is_merged_into");
            b.Property(x => x.InvestmentAction).HasColumnName("investment_action");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            // Register-parity columns appended in migration 018.
            b.Property(x => x.CheckNumber).HasColumnName("check_number");
            b.Property(x => x.CounterpartyId).HasColumnName("counterparty_id");
            b.Property(x => x.TxnGroupId).HasColumnName("txn_group_id");
            b.Property(x => x.LegIndex).HasColumnName("leg_index");
            b.Property(x => x.CounterpartyAccountId).HasColumnName("counterparty_account_id");
            b.Property(x => x.CounterpartyAccountName).HasColumnName("counterparty_account_name");
            b.Property(x => x.CounterpartyAccountType).HasColumnName("counterparty_account_type");
            b.Property(x => x.Tags).HasColumnName("tags");
            // Migration 028: unconditional header identity.
            b.Property(x => x.HeaderId).HasColumnName("header_id");
            // Migration 030: cleared-transition audit, paired with the
            // normalized 3-state status column.
            b.Property(x => x.ClearedAt).HasColumnName("cleared_at");
            b.Property(x => x.ClearedByUserId).HasColumnName("cleared_by_user_id");
            // Migration 032 (ADR-0025): raw leg-vs-header memo split
            // for the SPA editor + display.
            b.Property(x => x.LegMemo).HasColumnName("leg_memo");
            b.Property(x => x.HeaderMemo).HasColumnName("header_memo");
            // Migration 034: OFX online-match state projected
            // straight from the header (no override layer).
            b.Property(x => x.OnlineMatchFitid).HasColumnName("online_match_fitid");
            b.Property(x => x.OnlineMatchFiId).HasColumnName("online_match_fi_id");
            // Migration 037 (slice 2c): bank-feed review flag.
            b.Property(x => x.NeedsReview).HasColumnName("needs_review");
            // Migration 045 (slice A1.c): investment-leg metadata
            // + securities join for ticker/name in one query.
            // Commission intentionally omitted — see entity remarks.
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            b.Property(x => x.SecurityTicker).HasColumnName("security_ticker");
            b.Property(x => x.SecurityName).HasColumnName("security_name");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.UnitPrice).HasColumnName("unit_price");
            // Migration 057 (slice A4.a): posting role marker.
            b.Property(x => x.PostingRole).HasColumnName("posting_role");
            // Migration 077 (ADR-0031 Phase 3d.1): classifier hints
            // projected from txn_headers for the editor pre-fill flow.
            b.Property(x => x.IngestActionHint).HasColumnName("ingest_action_hint");
            b.Property(x => x.IngestSecurityId).HasColumnName("ingest_security_id");
            // Migration 113: OFX investment-row prefill carriers
            // projected for the editor's bank→investment upgrade.
            b.Property(x => x.IngestShares).HasColumnName("ingest_shares");
            b.Property(x => x.IngestUnitPrice).HasColumnName("ingest_unit_price");
            b.Property(x => x.IngestFee).HasColumnName("ingest_fee");
            // Migration 114: persisted OFX ticker hint.
            b.Property(x => x.IngestSecurityTickerHint).HasColumnName("ingest_security_ticker_hint");
            // Migration 079 (ADR-0031 follow-up): raw provider JSON.
            // Read as string; SPA parses + displays via the right-
            // click "Show raw provider data" modal.
            b.Property(x => x.ProviderRawPayload)
                .HasColumnName("provider_raw_payload")
                .HasColumnType("jsonb");
            // ADR-0034 v2 (migration 097): owning header's seq projected
            // through. Canonical sort tiebreaker for register reads.
            b.Property(x => x.HeaderSeq).HasColumnName("header_seq");
            // Mig 098/100: per-(header, account) net cash effect.
            // Same value on every leg of (header, account) — SPA reads
            // once per entry instead of summing legs.
            b.Property(x => x.HeaderAccountNetAmount).HasColumnName("header_account_net_amount");
            // Mig 107: register provenance + merge-winner overlay.
            b.Property(x => x.ProviderKey).HasColumnName("provider_key");
            b.Property(x => x.IsMergeWinner).HasColumnName("is_merge_winner");
            b.Property(x => x.ImportSource).HasColumnName("import_source");
            // Mig 108: per-leg derived action + per-target posting
            // counts (originating-vs-target rule per ADR-0036).
            b.Property(x => x.DerivedAction).HasColumnName("derived_action");
            b.Property(x => x.AccountPostingsOnHeader).HasColumnName("account_postings_on_header");
            b.Property(x => x.HeaderTotalPostings).HasColumnName("header_total_postings");
            // Mig 119 (ADR-0030 §2): register-row discriminant.
            b.Property(x => x.AccountType).HasColumnName("account_type");
        });

        // account_current_balances (migration 133): one row per account with
        // its current balance (latest balance_after, opening-balance fallback).
        // Keyless view — the single definition of "current balance" shared by
        // the overview and HoldingsRepository (ADR-0056 slice 1).
        modelBuilder.Entity<AccountCurrentBalanceView>(b =>
        {
            b.HasNoKey();
            b.ToView("account_current_balances");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.Balance).HasColumnName("balance");
        });

        // register_entry_keys (migration 019) result type. Configured
        // as a keyless query type so EF can materialize the (posted_at,
        // entry_key) projection. No ToFunction/ToTable here — the TVF
        // binding below (HasDbFunction) is the sole entry point; doubling
        // up causes a half-built model and an NRE in CreateColumnExpression
        // during translation.
        modelBuilder.Entity<RegisterEntryKeyRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.PostedAt).HasColumnName("posted_at");
            b.Property(x => x.Seq).HasColumnName("seq");
            b.Property(x => x.EntryKey).HasColumnName("entry_key");
        });

        // ledger_payee_suggestions (migration 027) result type. Same
        // keyless-query-type pattern as RegisterEntryKeys.
        modelBuilder.Entity<PayeeSuggestionRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.Count).HasColumnName("count");
            b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        });

        // recompute_holdings_for_brokerage (migration 088) result type.
        // Wrapper over the void recompute_holdings_cost_basis; returns
        // the count of holdings under the brokerage for diagnostics.
        modelBuilder.Entity<RecomputeHoldingsForBrokerageRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.RecomputedCount).HasColumnName("recomputed_count");
        });

        // recompute_balances_for_account (migration 102) result type.
        // Wrapper over the void fn_recompute_balances_for_account; returns
        // the input account_id so EF has a typed projection. The balance-
        // trigger family was dropped in mig 102 per ADR-0032; every API
        // writer now invokes this explicitly.
        modelBuilder.Entity<RecomputeBalancesForAccountRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.AccountId).HasColumnName("account_id");
        });

        // recompute_posting_counts_for_header (migration 120) result type.
        // Wrapper over the void fn_recompute_posting_counts_for_header;
        // returns the input header_id so EF has a typed projection. Posting
        // counts are a leg-derived denormalization maintained at the same
        // write boundary as balances (folded into the recompute interceptor;
        // ADR-0032 / ADR-0036).
        modelBuilder.Entity<RecomputePostingCountsForHeaderRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.HeaderId).HasColumnName("header_id");
        });

        // recompute_holdings_for_account_security (migration 104) result
        // type. Wrapper over the void recompute_holdings_cost_basis;
        // returns the input account_id so EF has a typed projection.
        // The txn_legs holdings trigger family was dropped in mig 104
        // per ADR-0032; the HoldingsRecomputeInterceptor invokes this
        // explicitly for every API write that mutates investment-shape
        // legs.
        modelBuilder.Entity<RecomputeHoldingsForAccountSecurityRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.AccountId).HasColumnName("account_id");
        });

        // security_price_upsert_from_trade (migration 177, ADR-0084) result
        // type. Rank-gated upsert of a `trade`-source price per (security, day);
        // returns the input security_id so EF has a typed projection. The
        // TradePriceFromLegInterceptor invokes this post-save for every EF write
        // that lands an investment trade leg.
        modelBuilder.Entity<SecurityPriceUpsertFromTradeRow>(b =>
        {
            b.HasNoKey();
            // The function's OUT column is `upserted_security_id` (not
            // `security_id`) to avoid a plpgsql variable/column collision in its
            // ON CONFLICT clause — see migration 177.
            b.Property(x => x.SecurityId).HasColumnName("upserted_security_id");
        });

        // holdings_market_value_as_of + account_balance_as_of (migration 172)
        // result types — the as-of valuation feeder (ADR-0063 v2 / Track-2
        // historical valuations: net-worth-over-time + true TWR).
        modelBuilder.Entity<HoldingsMarketValueAsOfRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.SecurityId).HasColumnName("security_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.MarketValue).HasColumnName("market_value");
            b.Property(x => x.PricedFrom).HasColumnName("priced_from");
        });
        modelBuilder.Entity<AccountBalanceAsOfRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.Balance).HasColumnName("balance");
        });

        // ledger_snapshot_payload (migration 111) result type. Returns
        // the full snapshot tables-object as one jsonb row; Npgsql
        // materialises jsonb to string on the C# side.
        modelBuilder.Entity<LedgerSnapshotPayloadRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.Payload).HasColumnName("payload");
        });

        // ledger_snapshot_restore (migration 111) result type. Wrapper
        // over the void fn_ledger_snapshot_restore; returns the input
        // ledger_id so EF has a typed projection.
        modelBuilder.Entity<LedgerSnapshotRestoreRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
        });

        // ledger_snapshot_write (migration 179) result type — the captured
        // uncompressed payload byte size. Server-side capture into content_json.
        modelBuilder.Entity<LedgerSnapshotWriteRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.ContentSizeUncompressed).HasColumnName("content_size_uncompressed");
        });

        // ledger_delete (migration 141) result type — input ledger_id echoed.
        modelBuilder.Entity<LedgerDeleteRow>(b =>
        {
            b.HasNoKey();
            b.Property(x => x.LedgerId).HasColumnName("ledger_id");
        });

        // Table-valued function binding for migration 019. EF Core
        // translates the instance-method invocation on AppDbContext
        // into a `SELECT * FROM register_entry_keys(...)` call. The
        // method body is unreachable — the .NET runtime never executes
        // it; it exists only as a LINQ-translation anchor.
        // GetMethod(name, types) only matches public methods; our
        // translation anchor is internal so we pass BindingFlags
        // explicitly.
        const BindingFlags InternalInstance =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(RegisterEntryKeys), InternalInstance,
                    types: new[] { typeof(Guid?), typeof(Guid),
                                   typeof(Guid?), typeof(long?),
                                   typeof(string),
                                   typeof(int), typeof(bool),
                                   // Filters (mig 164).
                                   typeof(string), typeof(DateOnly?), typeof(DateOnly?),
                                   typeof(decimal?), typeof(decimal?), typeof(Guid?),
                                   typeof(string), typeof(Guid?), typeof(string),
                                   typeof(DateOnly?),
                                   // Sort (mig 166).
                                   typeof(string), typeof(string) })!)
            .HasName("register_entry_keys");

        // Migration 167 — see RegisterFilteredEntries method below. The shared
        // register-filter primitive: returns the resolved_transactions rows
        // matching the filter. register_entry_keys composes over it in SQL, and
        // the rail buckets + status counts call it here — one filter definition
        // (ADR-0076). Returns the ResolvedTransactionView type already mapped
        // ToView above; a TVF over a view-mapped entity is a supported pattern.
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(RegisterFilteredEntries), InternalInstance,
                    types: new[] { typeof(Guid?), typeof(Guid), typeof(bool?),
                                   typeof(string), typeof(DateOnly?), typeof(DateOnly?),
                                   typeof(decimal?), typeof(decimal?), typeof(Guid?),
                                   typeof(string), typeof(Guid?), typeof(string),
                                   typeof(DateOnly?) })!)
            .HasName("register_filtered_entries");

        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(LedgerPayeeSuggestions), InternalInstance,
                    types: new[] { typeof(Guid), typeof(int) })!)
            .HasName("ledger_payee_suggestions");

        // Migration 088 — see RecomputeHoldingsForBrokerage method below.
        // Wrapper over recompute_holdings_cost_basis so AccountsRepository
        // can invoke it via LINQ instead of relying on the now-removed
        // commission-flip trigger.
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(RecomputeHoldingsForBrokerage), InternalInstance,
                    types: new[] { typeof(Guid) })!)
            .HasName("recompute_holdings_for_brokerage");

        // Migration 104 — see RecomputeHoldingsForAccountSecurity method
        // below. TVF wrapper over recompute_holdings_cost_basis so the
        // HoldingsRecomputeInterceptor can invoke narrow recompute via
        // LINQ. Replaces the txn_legs holdings trigger family dropped
        // in mig 104 (ADR-0032).
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(RecomputeHoldingsForAccountSecurity), InternalInstance,
                    types: new[] { typeof(Guid), typeof(Guid) })!)
            .HasName("recompute_holdings_for_account_security");

        // Migration 177 — see SecurityPriceUpsertFromTrade method below. TVF
        // wrapper over the rank-gated `trade`-source price upsert so the
        // TradePriceFromLegInterceptor can invoke it via LINQ post-save
        // (ADR-0084 D2), a sibling of the holdings recompute above.
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(SecurityPriceUpsertFromTrade), InternalInstance,
                    types: new[] { typeof(Guid), typeof(Guid), typeof(DateOnly), typeof(decimal) })!)
            .HasName("security_price_upsert_from_trade");

        // Migration 172 — see HoldingsMarketValueAsOf / AccountBalanceAsOf
        // methods below. The as-of valuation feeder (ADR-0063 v2 historical
        // valuations): net-worth-over-time + true TWR.
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(HoldingsMarketValueAsOf), InternalInstance,
                    types: new[] { typeof(Guid), typeof(DateTime), typeof(Guid?), typeof(Guid?) })!)
            .HasName("holdings_market_value_as_of");
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(AccountBalanceAsOf), InternalInstance,
                    types: new[] { typeof(Guid), typeof(DateTime), typeof(Guid?) })!)
            .HasName("account_balance_as_of");

        // Migration 102 — see RecomputeBalancesForAccount method below.
        // Wrapper over fn_recompute_balances_for_account so every API
        // writer can invoke recompute via LINQ. Replaces the trigger
        // family dropped in mig 102 (ADR-0032 / ADR-0034).
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(RecomputeBalancesForAccount), InternalInstance,
                    types: new[] { typeof(Guid), typeof(DateTime) })!)
            .HasName("recompute_balances_for_account");

        // Migration 120 — see RecomputePostingCountsForHeader method below.
        // Wrapper over fn_recompute_posting_counts_for_header so the recompute
        // interceptor can re-derive the denormalized posting counts via LINQ
        // at the same write boundary as balances (ADR-0032 / ADR-0036).
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(RecomputePostingCountsForHeader), InternalInstance,
                    types: new[] { typeof(Guid) })!)
            .HasName("recompute_posting_counts_for_header");

        // Migration 111 — see LedgerSnapshotPayload / LedgerSnapshotRestore
        // methods below. TVF wrappers over fn_ledger_snapshot_payload
        // (returns the in-scope graph as jsonb) and fn_ledger_snapshot_restore
        // (wipes + re-inserts in one call). ADR-0037 §"Shared internals."
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(LedgerSnapshotPayload), InternalInstance,
                    types: new[] { typeof(Guid) })!)
            .HasName("ledger_snapshot_payload");

        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(LedgerSnapshotRestore), InternalInstance,
                    types: new[] { typeof(Guid), typeof(string) })!)
            .HasName("ledger_snapshot_restore");

        // Migration 179 — server-side snapshot payload (OOM fix). Capture into
        // content_json + restore from the stored row, without the payload ever
        // crossing into managed memory.
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(LedgerSnapshotWrite), InternalInstance,
                    types: new[] { typeof(Guid), typeof(Guid) })!)
            .HasName("ledger_snapshot_write");

        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(LedgerSnapshotRestoreStored), InternalInstance,
                    types: new[] { typeof(Guid), typeof(Guid) })!)
            .HasName("ledger_snapshot_restore_stored");

        // Migration 141 — see LedgerDelete below. TVF wrapper over the void
        // fn_ledger_delete (complete FK-ordered wipe of one ledger). ADR-0020.
        modelBuilder
            .HasDbFunction(typeof(AppDbContext)
                .GetMethod(nameof(LedgerDelete), InternalInstance,
                    types: new[] { typeof(Guid) })!)
            .HasName("ledger_delete");
    }

    // -- Postgres TVF translation anchor ----------------------------------------
    //
    // EF Core's HasDbFunction mapping requires a method whose
    // signature mirrors the function's parameter list. The runtime
    // body is never invoked — every call site reaches EF's expression
    // translator instead.

    /// <summary>
    /// Maps to <c>register_entry_keys(p_account_id, p_ledger_id,
    /// p_cursor_entry_key, p_cursor_seq, p_direction, p_limit, …,
    /// p_sort_column, p_sort_dir)</c> (migrations 097 / 164 / 166).
    /// Returns one row per register entry in the requested sort order —
    /// the outer SELECT in the SQL function re-sorts for a uniform
    /// consumer shape regardless of the fetch <paramref name="direction"/>
    /// (<c>"before"</c> = entries past the cursor in display order;
    /// <c>"after"</c> = entries before it). Mig 166: the cursor is the
    /// boundary entry's KEY (<paramref name="cursorEntryKey"/>) — the
    /// function derives that entry's sort value internally, so the opaque
    /// cursor stays sort-agnostic and entry_key is the final tiebreaker
    /// (total order). <paramref name="sortColumn"/> is whitelisted
    /// server-side (unknown ⇒ date); <paramref name="sortDir"/> is
    /// <c>"asc"</c> / <c>"desc"</c>.
    /// </summary>
    internal IQueryable<RegisterEntryKeyRow> RegisterEntryKeys(
        Guid? accountId,
        Guid ledgerId,
        Guid? cursorEntryKey,
        long? cursorSeq,
        string direction,
        int limit,
        bool hidden,
        // Filters (mig 164). All null ⇒ no-op, so the plain register is
        // unchanged. Positional to match the SQL function's arg order.
        string? search,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        decimal? amountMin,
        decimal? amountMax,
        Guid? securityId,
        string? tag,
        Guid? categoryId,
        string? status,
        DateOnly? today,
        // Sort (mig 166). Display-order only; whitelisted server-side.
        string sortColumn,
        string sortDir) =>
        FromExpression(() =>
            RegisterEntryKeys(accountId, ledgerId, cursorEntryKey, cursorSeq, direction, limit, hidden,
                search, dateFrom, dateTo, amountMin, amountMax, securityId, tag, categoryId, status, today,
                sortColumn, sortDir));

    /// <summary>
    /// Maps to <c>register_filtered_entries(p_account_id, p_ledger_id,
    /// p_hidden, …filters…)</c> (migration 167 / ADR-0076) — the single
    /// definition of the register filter predicate. Returns the
    /// <c>resolved_transactions</c> rows matching the filter (per-leg; an entry
    /// appears iff any of its legs match). <c>register_entry_keys</c> composes
    /// over it in SQL for the windowed page; this LINQ anchor lets the rail
    /// buckets and status counts share the same predicate instead of
    /// re-deriving it. A single-SELECT STABLE SQL function, so it inlines into
    /// the caller's plan (no barrier).
    /// </summary>
    internal IQueryable<ResolvedTransactionView> RegisterFilteredEntries(
        Guid? accountId,
        Guid ledgerId,
        bool? hidden,
        string? search,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        decimal? amountMin,
        decimal? amountMax,
        Guid? securityId,
        string? tag,
        Guid? categoryId,
        string? status,
        DateOnly? today) =>
        FromExpression(() =>
            RegisterFilteredEntries(accountId, ledgerId, hidden, search, dateFrom, dateTo,
                amountMin, amountMax, securityId, tag, categoryId, status, today));

    /// <summary>
    /// Maps to <c>ledger_payee_suggestions(p_ledger_id, p_limit)</c>
    /// in migration 027. Returns one row per distinct resolved payee
    /// in the ledger, ranked by usage count then recency, with hidden
    /// + merged headers excluded.
    /// </summary>
    internal IQueryable<PayeeSuggestionRow> LedgerPayeeSuggestions(
        Guid ledgerId, int limit) =>
        FromExpression(() => LedgerPayeeSuggestions(ledgerId, limit));

    /// <summary>
    /// Maps to <c>recompute_holdings_for_brokerage(p_holdings_account_id UUID)</c>
    /// in migration 088. Thin wrapper over the void
    /// <c>recompute_holdings_cost_basis</c>; calls it for the
    /// specified brokerage's holdings sibling and returns the count
    /// of holdings rows for diagnostics. Replaces the trigger-driven
    /// recompute that fired on accounts.is_trade_commission flip
    /// (ADR-0032). Repository invokes via
    /// <c>RecomputeHoldingsForBrokerage(id).Select(r => r.RecomputedCount).FirstAsync()</c>.
    /// </summary>
    internal IQueryable<RecomputeHoldingsForBrokerageRow> RecomputeHoldingsForBrokerage(
        Guid holdingsAccountId) =>
        FromExpression(() => RecomputeHoldingsForBrokerage(holdingsAccountId));

    /// <summary>
    /// Maps to <c>recompute_balances_for_account(p_account_id, p_from_posted_at)</c>
    /// in migration 102. Thin TVF wrapper over the void
    /// <c>fn_recompute_balances_for_account</c>; wipes every
    /// <c>txn_header_account_balances</c> row for the supplied account
    /// from <paramref name="fromPostedAt"/> forward and re-derives them
    /// from current <c>txn_legs</c> in canonical <c>(posted_at, seq)</c>
    /// order. Returns the input account id so EF has a typed projection;
    /// callers normally discard it.
    /// </summary>
    /// <remarks>
    /// Replaces the balance-trigger family (mig 090 / 094 / 099 / 101),
    /// all dropped in mig 102 per ADR-0032's triggers-as-last-resort
    /// posture. Every API writer that mutates legs / headers /
    /// header_overrides / leg_overrides invokes this at its terminal
    /// SaveChanges boundary; see <see cref="LegDerivedRecomputeService"/>.
    /// </remarks>
    internal IQueryable<RecomputeBalancesForAccountRow> RecomputeBalancesForAccount(
        Guid accountId, DateTime fromPostedAt) =>
        FromExpression(() => RecomputeBalancesForAccount(accountId, fromPostedAt));

    /// <summary>
    /// Maps to <c>recompute_posting_counts_for_header(p_header_id)</c> in
    /// migration 120. Thin TVF wrapper over the void
    /// <c>fn_recompute_posting_counts_for_header</c>; re-derives the
    /// denormalized <c>account_postings_on_header</c> +
    /// <c>header_total_postings</c> on every leg of the supplied header
    /// from the current <c>txn_legs</c>. Returns the input header id so EF
    /// has a typed projection; callers normally discard it.
    /// </summary>
    /// <remarks>
    /// Posting counts are a leg-derived denormalization (ADR-0036) kept at
    /// the same write boundary as balances: the recompute interceptor
    /// snapshots the touched headers once and drives both recomputes.
    /// See <see cref="LegDerivedRecomputeService"/>.
    /// </remarks>
    internal IQueryable<RecomputePostingCountsForHeaderRow> RecomputePostingCountsForHeader(
        Guid headerId) =>
        FromExpression(() => RecomputePostingCountsForHeader(headerId));

    /// <summary>
    /// Maps to <c>recompute_holdings_for_account_security(p_account_id, p_security_id)</c>
    /// in migration 104. Thin TVF wrapper over the void
    /// <c>recompute_holdings_cost_basis</c>; re-derives the
    /// <c>holdings</c> + <c>lots</c> state for the specified
    /// (account, security) pair from the live <c>txn_legs</c> +
    /// <c>security_splits</c> event stream. Returns the input account
    /// id so EF has a typed projection; callers normally discard it.
    /// </summary>
    /// <remarks>
    /// Replaces the txn_legs holdings trigger family (mig 068 / 073),
    /// dropped in mig 104 per ADR-0032's triggers-as-last-resort
    /// posture. The <see cref="Repositories.HoldingsRecomputeInterceptor"/>
    /// invokes this at every API SaveChanges that mutates
    /// investment-shape legs (security_id IS NOT NULL); see
    /// <see cref="Repositories.HoldingsRecomputeService"/>.
    /// </remarks>
    internal IQueryable<RecomputeHoldingsForAccountSecurityRow> RecomputeHoldingsForAccountSecurity(
        Guid accountId, Guid securityId) =>
        FromExpression(() => RecomputeHoldingsForAccountSecurity(accountId, securityId));

    /// <summary>
    /// Maps to <c>security_price_upsert_from_trade(p_ledger_id, p_security_id,
    /// p_day, p_price)</c> (migration 177, ADR-0084). Rank-gated upsert of a
    /// <c>trade</c>-source closing price for the (security, UTC-day): inserts
    /// when the day is empty, overwrites an existing <c>import</c>/<c>simplefin</c>/
    /// <c>trade</c> row, and leaves a truer <c>fetch</c>/<c>manual</c> price
    /// untouched. A non-positive price is a no-op. Returns the input security id
    /// so EF has a typed projection; callers discard it.
    /// </summary>
    /// <remarks>
    /// Invoked post-save by the <see cref="TradePriceFromLegInterceptor"/> for
    /// every EF write that lands an investment trade leg; see
    /// <see cref="Repositories.TradePriceRecomputeService"/>. A function call
    /// (not a trigger, ADR-0032) so it can't re-fire the interceptors.
    /// </remarks>
    internal IQueryable<SecurityPriceUpsertFromTradeRow> SecurityPriceUpsertFromTrade(
        Guid ledgerId, Guid securityId, DateOnly day, decimal price) =>
        FromExpression(() => SecurityPriceUpsertFromTrade(ledgerId, securityId, day, price));

    /// <summary>
    /// Maps to <c>holdings_market_value_as_of(p_ledger_id, p_as_of,
    /// p_account_id, p_security_id)</c> (migration 172). Per (holdings-sibling
    /// account, security), the split-adjusted quantity held at the instant and
    /// its market value (feed close ≤ instant, else the latest trade execution
    /// price ≤ instant). The as-of valuation feeder for net-worth-over-time and
    /// true time-weighted return (ADR-0063 v2). <paramref name="asOf"/> must be
    /// UTC (timestamptz binding).
    /// </summary>
    internal IQueryable<HoldingsMarketValueAsOfRow> HoldingsMarketValueAsOf(
        Guid ledgerId, DateTime asOf, Guid? accountId, Guid? securityId) =>
        FromExpression(() => HoldingsMarketValueAsOf(ledgerId, asOf, accountId, securityId));

    /// <summary>
    /// Maps to <c>account_balance_as_of(p_ledger_id, p_as_of, p_account_id)</c>
    /// (migration 172) — the date-bounded twin of the
    /// <c>account_current_balances</c> view (mig 133): each account's register
    /// balance as of the instant. <paramref name="asOf"/> must be UTC.
    /// </summary>
    internal IQueryable<AccountBalanceAsOfRow> AccountBalanceAsOf(
        Guid ledgerId, DateTime asOf, Guid? accountId) =>
        FromExpression(() => AccountBalanceAsOf(ledgerId, asOf, accountId));

    /// <summary>
    /// Maps to <c>ledger_snapshot_payload(p_ledger_id)</c> in
    /// migration 111. Walks the in-scope tables (per ADR-0037 §Scope)
    /// and returns the serialised tables-object as one jsonb row.
    /// Repository invokes via
    /// <c>LedgerSnapshotPayload(ledgerId).Select(r => r.Payload).FirstAsync()</c>.
    /// </summary>
    internal IQueryable<LedgerSnapshotPayloadRow> LedgerSnapshotPayload(
        Guid ledgerId) =>
        FromExpression(() => LedgerSnapshotPayload(ledgerId));

    /// <summary>
    /// Maps to <c>ledger_snapshot_restore(p_ledger_id, p_payload)</c> in
    /// migration 111. Replaces the in-scope tables for the given ledger
    /// with rows from the payload jsonb (sent as text; cast internally
    /// per the mig 070 EF-param convention). Caller wraps the LINQ
    /// invocation in an explicit transaction so a payload-shape error
    /// (e.g. jsonb_populate_recordset rejection) rolls back the wipe.
    /// </summary>
    internal IQueryable<LedgerSnapshotRestoreRow> LedgerSnapshotRestore(
        Guid ledgerId, string payloadJson) =>
        FromExpression(() => LedgerSnapshotRestore(ledgerId, payloadJson));

    /// <summary>
    /// Maps to <c>ledger_snapshot_write(p_snapshot_id, p_ledger_id)</c> (mig 179).
    /// Captures the in-scope graph into <c>ledger_snapshots.content_json</c> entirely
    /// server-side and returns the uncompressed byte size. The payload never enters
    /// managed memory (the OOM fix). Invoked via
    /// <c>LedgerSnapshotWrite(id, ledgerId).Select(r => r.ContentSizeUncompressed).FirstAsync()</c>.
    /// </summary>
    internal IQueryable<LedgerSnapshotWriteRow> LedgerSnapshotWrite(
        Guid snapshotId, Guid ledgerId) =>
        FromExpression(() => LedgerSnapshotWrite(snapshotId, ledgerId));

    /// <summary>
    /// Maps to <c>ledger_snapshot_restore_stored(p_snapshot_id, p_ledger_id)</c>
    /// (mig 179). Reads the stored <c>content_json</c> and reuses the existing
    /// restore body — all server-side. Caller validates schema/ownership first and
    /// wraps the invocation in a transaction. Returns the ledger id.
    /// </summary>
    internal IQueryable<LedgerSnapshotRestoreRow> LedgerSnapshotRestoreStored(
        Guid snapshotId, Guid ledgerId) =>
        FromExpression(() => LedgerSnapshotRestoreStored(snapshotId, ledgerId));

    /// <summary>
    /// Maps to <c>ledger_delete(p_ledger_id)</c> (migration 141) — the
    /// complete FK-ordered wipe of one ledger + its grants + the ledger row.
    /// Caller (<c>LedgersRepository.DeleteAsync</c>) wraps the LINQ invocation
    /// in a transaction and runs it through the BYPASSRLS service context.
    /// </summary>
    internal IQueryable<LedgerDeleteRow> LedgerDelete(Guid ledgerId) =>
        FromExpression(() => LedgerDelete(ledgerId));
}
