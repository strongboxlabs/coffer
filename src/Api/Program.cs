using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Backup;
using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;
using Coffer.Api.Logging;
using Coffer.Api.Mcp;
using Coffer.Api.Migrations;

using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using Coffer.Api.Sync.SimpleFin;

var builder = WebApplication.CreateBuilder(args);

// -- configuration -----------------------------------------------------------
builder.Configuration.AddEnvironmentVariables(prefix: "COFFER_");

// Role passwords come from files, not the environment (same reasoning as the
// master key, ADR-0092 D1: an env var is readable via `docker inspect`,
// /proc/<pid>/environ and crash dumps). This rewrites the connection strings in
// configuration BEFORE anything binds them, so all dozen-odd consumers across
// the API, the backup service and the importer see a finished string. Throws on
// a configured-but-unreadable file rather than starting up passwordless.
var dbPasswordOutcomes = DbPasswordResolver.ApplyTo(builder.Configuration);

builder.Services
    .AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    // Fido2 origins must be configured per environment (dev: appsettings.Development.json;
    // prod: COFFER_API__Fido2__Origins__0). The first entry is the canonical browser
    // origin for the bootstrap link + Drive OAuth redirect (ADR-0062), so an empty
    // list is a fail-fast misconfiguration rather than a silent localhost fallback.
    .Validate(o => o.Fido2.Origins.Count > 0,
        "Api:Fido2:Origins must list at least one origin (set COFFER_API__Fido2__Origins__0).")
    .ValidateOnStart();

// -- cross-cutting -----------------------------------------------------------
builder.Services.AddCofferProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

// -- logging -----------------------------------------------------------------
// Production emits structured JSON so traceId / mcpTool / ledgerId scope fields
// are queryable (ADR-0086 Track B) — the api-standards "structured logging" line,
// via the built-in formatter (no extra dependency). Dev keeps the default
// human-readable console. IncludeScopes surfaces the RequestScopeMiddleware +
// MCP tool-call scopes.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
    });
}

// -- observability -----------------------------------------------------------
// OpenTelemetry tracing wired in Development as a perf-brainstorm tool.
// Spans land in the API console alongside Serilog-equivalent stdout
// logging so the HTTP request → EF Core query → Npgsql command nesting
// is visible without an external collector.
//
// Auto-instrumentation covers the three layers the register-latency hunt
// cared about:
//   * AspNetCore  — incoming HTTP request span (top-level).
//   * Npgsql.OpenTelemetry — raw command parse/bind/execute breakdown.
//   * Microsoft.EntityFrameworkCore — EF query span (LINQ translation +
//     materialization), via EF's built-in ActivitySource (no separate
//     instrumentation package needed in EF Core 9+).
//
// Production exporters (OTLP → Jaeger/Tempo/etc.) are a future wiring
// decision; the console exporter is dev-only and would be noisy in prod.
if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(serviceName: "coffer-api"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddNpgsql()
            .AddSource("Microsoft.EntityFrameworkCore")
            .AddConsoleExporter());
}

// -- DB plumbing -------------------------------------------------------------
// EF Core (AppDbContext) handles every API DB call per ADR-0005.
//
// PR 3.8 splits the connection into two roles per ADR-0020 Phase D:
//   * Runtime AppDbContext  → coffer_app (NOBYPASSRLS). Interceptor
//     runs SET app.user_id from ICurrentUserAccessor on each pooled
//     connection open; RLS policies in migration 017 filter rows to
//     the authenticated user's grants.
//   * ServiceDbContextFactory → coffer_service (BYPASSRLS). Used by
//     the WebAuthn handlers (pre-auth lookups), session/credential
//     writes, and POST /api/ledgers (escalated insert across the RLS
//     boundary). Migrations also run as this role.
builder.Services.AddScoped<AppUserDbConnectionInterceptor>();
// Mig 102: balance recompute moved from Postgres triggers to a
// SaveChangesInterceptor. ChangeTracker is scanned in
// SavingChangesAsync; recompute fires for every touched account in
// SavedChangesAsync — atomic with the caller's transaction. Every
// API writer that mutates txn_legs / txn_headers / overrides is
// implicitly covered; see LegDerivedRecomputeInterceptor's class
// comment for the contract.
builder.Services.AddSingleton<LegDerivedRecomputeInterceptor>();
// Mig 104 sibling: holdings + lots recompute after every API write
// that mutates investment-shape txn_legs (security_id IS NOT NULL +
// quantity IS NOT NULL). Replaces the txn_legs holdings trigger
// family (mig 068 / 073). Same SavingChangesAsync / SavedChangesAsync
// lifecycle as the balance interceptor; reads the ChangeTracker and
// invokes HoldingsRecomputeService for the affected (account, security)
// pairs. Both interceptors are independent — each scans the
// ChangeTracker for its own surface.
builder.Services.AddSingleton<HoldingsRecomputeInterceptor>();
// ADR-0084 sibling: seed a `trade`-source security_prices row after every API
// write that lands an investment trade leg (security_id set, quantity <> 0,
// unit_price > 0). Same SavingChanges / SavedChanges lifecycle as the holdings
// interceptor; reads the ChangeTracker and invokes TradePriceRecomputeService
// (the rank-gated Postgres upsert, mig 177) post-save. Independent of the other
// interceptors — each scans the ChangeTracker for its own surface.
builder.Services.AddSingleton<TradePriceFromLegInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var apiOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
    var roleInterceptor = sp.GetRequiredService<AppUserDbConnectionInterceptor>();
    var balanceInterceptor = sp.GetRequiredService<LegDerivedRecomputeInterceptor>();
    var holdingsInterceptor = sp.GetRequiredService<HoldingsRecomputeInterceptor>();
    var tradePriceInterceptor = sp.GetRequiredService<TradePriceFromLegInterceptor>();
    options.UseNpgsql(apiOpts.ConnectionString)
           .AddInterceptors(roleInterceptor, balanceInterceptor, holdingsInterceptor,
                            tradePriceInterceptor);
});
builder.Services.AddSingleton<ServiceDbContextFactory>();

// -- auth: cookie scheme always registered. Dev-auth scheme registered
// iff env=Development AND Api:DevAuth=true (ADR-0013 dual gate). The
// gate is registration-time so a production build with the wrong
// config doesn't even have the dev-auth scheme in DI — defence-in-depth
// against a future code change accidentally widening the runtime
// check. Tests that need dev-auth flip both gates via env vars before
// WebApplication.CreateBuilder runs (see ApiFactory.ApplyEnvOverrides).
var apiOptions = builder.Configuration
    .GetSection(ApiOptions.SectionName)
    .Get<ApiOptions>() ?? new ApiOptions();
var devAuthEnabled = builder.Environment.IsDevelopment() && apiOptions.DevAuth;
// MCP off-by-default (ADR-0063 §D7/D8). Effective gate = the eager
// COFFER_API__Mcp__Enabled config (a bootstrap/test/headless override) OR the
// `mcp.enabled` system setting an admin toggles in the UI. Read at startup so
// that when off the MCP scheme/policy/server/endpoints are never registered —
// the surface is absent, not merely 404 (D7). The DB read is defensive: a fresh
// install with no system_settings table yet falls back to false (D8), so
// default-off always holds; the admin's choice applies on the next restart.
var mcpEnabled = apiOptions.Mcp.Enabled
    || SystemSettingsBootstrap.TryReadBool(
        apiOptions.ServiceConnectionString,
        SystemSettingsRepository.McpEnabledKey,
        fallback: false);
// MCP WRITE tools (ADR-0068): the initial writes-enabled state comes from the config
// override OR the `mcp.writes_enabled` system setting, read once here to seed
// McpRuntimeState. ADR-0081 D2 makes it a HOT flag thereafter — the admin toggle
// flips it live and McpWriteGuard rejects writes per-call when off — so this startup
// read is only the seed value, not the gate.
var mcpWritesEnabled = mcpEnabled
    && (apiOptions.Mcp.WritesEnabled
        || SystemSettingsBootstrap.TryReadBool(
            apiOptions.ServiceConnectionString,
            SystemSettingsRepository.McpWritesEnabledKey,
            fallback: false));
// Expose the live state so the admin settings endpoint can report "pending
// restart" when a persisted toggle differs from what's actually running (D8).
builder.Services.AddSingleton(new Coffer.Api.Mcp.McpRuntimeState(mcpEnabled, mcpWritesEnabled));

var authBuilder = builder.Services.AddAuthentication(defaultScheme: AuthSchemes.Cookie);
authBuilder.AddScheme<AuthenticationSchemeOptions, CookieAuthHandler>(
    AuthSchemes.Cookie, _ => { });
if (devAuthEnabled)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(
        AuthSchemes.DevAuth, _ => { });
}
if (mcpEnabled)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, McpTokenAuthHandler>(
        AuthSchemes.Mcp, _ => { });
}

builder.Services.AddAuthorization(options =>
{
    var schemes = devAuthEnabled
        ? new[] { AuthSchemes.Cookie, AuthSchemes.DevAuth }
        : new[] { AuthSchemes.Cookie };
    options.DefaultPolicy = new AuthorizationPolicyBuilder(schemes)
        .RequireAuthenticatedUser()
        .Build();

    // Admin-only policy (ADR-0060): authenticated + the is_admin claim the
    // auth handlers stamp from users.is_admin. Gates the deployment-wide
    // backup surface; the matching endpoints also re-assert nothing beyond
    // this since admin is a deployment-global capability, not per-ledger.
    options.AddPolicy(AuthPolicies.RequireAdmin, policy => policy
        .AddAuthenticationSchemes(schemes)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthPolicies.IsAdminClaim, AuthPolicies.IsAdminTrue));

    // MCP-only policy (ADR-0063): the bearer scheme authenticates /mcp and
    // nothing else (least privilege — a read-only token can't reach the REST
    // API). DevAuth joins it in dev/test so the operator can smoke /mcp without
    // minting a token; DevAuth is never registered in production.
    if (mcpEnabled)
    {
        // Two token paths authenticate /mcp: the manual revocable bearer
        // (AuthSchemes.Mcp) and OAuth access tokens (OpenIddict validation).
        // DevAuth joins in dev/test only. Still least-privilege — none of these
        // is in the default policy, so no MCP credential reaches the REST API.
        var mcpSchemes = devAuthEnabled
            ? new[] { AuthSchemes.Mcp, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, AuthSchemes.DevAuth }
            : new[] { AuthSchemes.Mcp, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme };
        options.AddPolicy(AuthPolicies.RequireMcp, policy => policy
            .AddAuthenticationSchemes(mcpSchemes)
            .RequireAuthenticatedUser());
    }
});

builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

// Sessions + cookie service. Repositories are scoped (one connection per
// request); SessionService takes the repository, no per-request state of
// its own.
builder.Services.AddScoped<SessionsRepository>();
builder.Services.AddScoped<SessionService>();

// Auth-data layer added in PR 3.2 + the WebAuthn ceremony in PR 3.4. The
// repositories take an IDbConnectionFactory and remain scoped per request.
builder.Services.AddScoped<UsersRepository>();
builder.Services.AddScoped<CredentialsRepository>();
builder.Services.AddScoped<RecoveryCodesRepository>();
builder.Services.AddScoped<BootstrapTokenService>();
builder.Services.AddScoped<BackupService>();
// Bootstrap restore (ADR-0061): abstracts "restart so the next boot applies the
// staged restore" so it's testable.
builder.Services.AddSingleton<Coffer.Api.Backup.IApplicationRestarter,
    Coffer.Api.Backup.HostApplicationRestarter>();
// Backup artifact store (ADR-0060): filesystem-backed, retention-capped. The
// directory defaults to data/backups beside the binary (the Docker volume);
// tests override it via Api:Backup:Directory. Stateless beyond config → singleton.
builder.Services.AddSingleton(sp =>
{
    var backup = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.Backup;
    var dir = string.IsNullOrWhiteSpace(backup.Directory)
        ? Path.Combine(AppContext.BaseDirectory, "data", "backups")
        : backup.Directory;
    return new Coffer.Api.Backup.BackupStore(
        dir, sp.GetRequiredService<ILogger<Coffer.Api.Backup.BackupStore>>());
});
builder.Services.AddScoped<Coffer.Api.Backup.BackupManager>();
// "Never delete" pins (ADR-0062 ④b+c) — excluded from retention.
builder.Services.AddScoped<Coffer.Api.Db.Repositories.BackupPinsRepository>();
// Deployment-level admin audit (ADR-0092 D2) — service-role only, append-only.
builder.Services.AddScoped<Coffer.Api.Db.Repositories.AdminAuditRepository>();
// Admin-editable backup retention (ADR-0074) — the single source of truth for
// local pruning + the Google Drive mirror.
builder.Services.AddScoped<Coffer.Api.Db.Repositories.BackupSettingsRepository>();

// Google Drive backup sync (ADR-0062). The OAuth redirect dance and the Drive
// REST calls each sit behind a seam so the connect/push flows are testable with
// fakes; the real impls are HttpClient + Google.Apis.Drive.v3. In-memory cache
// stashes the in-flight CSRF state between connect/start and the OAuth callback.
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<Coffer.Api.Backup.Drive.IDriveOAuthClient,
    Coffer.Api.Backup.Drive.GoogleDriveOAuthClient>();
builder.Services.AddSingleton<Coffer.Api.Backup.Drive.IDriveClient,
    Coffer.Api.Backup.Drive.GoogleDriveClient>();
builder.Services.AddScoped<Coffer.Api.Db.Repositories.DriveSyncRepository>();
builder.Services.AddScoped<Coffer.Api.Backup.Drive.DriveSyncService>();
// The Drive backup destination (ADR-0062 ④b+c): registered concretely (the Drive
// admin endpoints use it directly) AND exposed as IBackupDestination so
// BackupManager pushes to it after every backup.
builder.Services.AddScoped<Coffer.Api.Backup.Drive.GoogleDriveBackupDestination>();
builder.Services.AddScoped<Coffer.Api.Backup.IBackupDestination>(
    sp => sp.GetRequiredService<Coffer.Api.Backup.Drive.GoogleDriveBackupDestination>());

builder.Services.AddScoped<ChallengeStore>();
// Step-up gate for deployment-secret disclosures (ADR-0092 D2/D7). Shared by the
// master-KEK and backup-passphrase reveals so the check can't drift between them.
builder.Services.AddScoped<Coffer.Api.Auth.Webauthn.FreshAssertionGate>();

// Master KEK (ADR-0026, sourced per ADR-0092 D1): resolved once at startup from
// the key file at Api:MasterKey:Path, or migrated in from the deprecated
// COFFER_MASTER_KEK_BASE64 env var (ADR-0092 D6). Fail-fast on missing /
// malformed / wrong size — the API refuses to serve rather than silently fall
// through to a default. Registered as a singleton because the bytes never change
// at runtime; per-ledger LEK ops go through LedgerKeyService (scoped, since it's
// pulled into request-scoped repositories).
//
// The store is registered too: the rotation swap (ADR-0092 D4) and the restore
// adopt path (D5) write through it, and the setup ceremony (D2) creates the
// initial key with it.
var masterKeyStore = new MasterKeyStore(apiOptions.MasterKey.Path);
builder.Services.AddSingleton(masterKeyStore);
MasterKeyLoader.KeySource masterKeySource;
var masterKeyEnvIgnored = false;
var masterKeyWasMinted = false;
{
    var resolution = MasterKeyLoader.Resolve(masterKeyStore);
    var resolvedKey = resolution.Key;
    masterKeySource = resolution.Source;
    masterKeyEnvIgnored = resolution.EnvironmentIgnored;

    if (resolvedKey is null)
    {
        // ADR-0092 D3 — no key configured. Legal on a genuinely virgin install
        // (nothing to strand, so mint one and let the setup ceremony show it);
        // an operator error otherwise, because minting would orphan the wrapped
        // set. `--adopt-new-kek` is the deliberate override: the operator has
        // decided to abandon it. The probe fails CLOSED, so an unreachable
        // database refuses rather than being mistaken for virgin.
        Exception? probeFailure = null;
        var adoptNew = args.Contains("--adopt-new-kek");
        var hasWrapped = WrappedMaterialProbe.Exists(
            apiOptions.ServiceConnectionString, ex => probeFailure = ex);

        if (hasWrapped && !adoptNew)
            throw new InvalidOperationException(
                $"No master KEK found, and this database already holds KEK-wrapped material"
                + $"{(probeFailure is null ? "" : $" (could not verify: {probeFailure.Message})")}. "
                + $"Minting a new key would orphan it. Put the install's existing base64 key at "
                + $"'{masterKeyStore.Path}' (configure the location with Api:MasterKey:Path), or "
                + $"re-run with --adopt-new-kek to mint a fresh key and abandon the wrapped "
                + $"secrets — the SimpleFIN feed tokens, the stored backup passphrase, and the "
                + $"Google Drive token, all of which can be re-established. "
                + $"See docs/decisions/0092-kek-lifecycle-in-the-ui.md.");

        var minted = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        // Write the id INTO the file, so it is self-describing exactly like the one the
        // D6 migration and D4 rotation produce. Without it the id silently falls back
        // to COFFER_MASTER_KEK_ID, which is how `--adopt-new-kek` ended up labelling a
        // brand-new key with the same id the orphaned rows still carry — one label
        // naming two different keys, which is misleading precisely when someone is
        // reading it during an incident.
        //
        // On adopt-new-kek the id is bumped for that reason. It stays a best-effort
        // distinction, not a guarantee: the id is advisory, only rotation reads it, and
        // whether a blob actually opens is decided by trial-decrypt. Reading the
        // database's existing ids to guarantee non-collision would be real machinery
        // bought for a label.
        var mintedId = adoptNew
            ? MasterKeyLoader.NextKekId(MasterKeyLoader.ResolveId())
            : MasterKeyLoader.ResolveId();
        masterKeyStore.Write(minted, mintedId);
        resolvedKey = MasterKeyLoader.LoadFromValueOrThrow(
            minted, mintedId, masterKeyStore.Path);
        masterKeyWasMinted = true;
    }

    builder.Services.AddSingleton(resolvedKey);
}
builder.Services.AddScoped<LedgerKeyService>();
// Master-KEK rotation (ADR-0026 §rotation), driven from the admin UI (ADR-0092 D4).
// The `rotate-kek` CLI it replaced is gone: rotation is routine hygiene rather than
// disaster recovery, so an operator who can't sign in to reach the UI needs recovery
// codes, not a rotation. (`restore` stays a CLI command because it genuinely can't be
// a UI one — it skips migrations, so it works on a schema too broken to serve.)
builder.Services.AddScoped<KekRotationService>();
builder.Services.AddScoped<IKekRotationService>(sp => sp.GetRequiredService<KekRotationService>());
// Rotation ordering: the key-file swap sequenced against the re-wrap (ADR-0092 D4).
builder.Services.AddScoped<MasterKeyRotationCoordinator>();
// Post-restore reconciliation (ADR-0092 D5) — runs after every restore so no
// ciphertext this install can't open survives it.
builder.Services.AddScoped<KekReconciliationService>();

// SimpleFIN HTTP client (Phase 5 slice 1). Typed via the standard
// IHttpClientFactory pattern so connection pooling + DNS refresh
// are handled correctly. SimpleFinClient owns the protocol shape;
// the factory owns lifetime. No base address — every SimpleFIN
// call uses an absolute URL (setup token's claim URL, then the
// per-connection access URL).
builder.Services.AddHttpClient<SimpleFinClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Coffer/1.0 (+simplefin-bridge)");
});

// ADR-0031 ingest provider pattern. SimpleFinPullProvider is the
// SimpleFIN-specific translator (HTTP + access URL decryption +
// SimpleFinSyncResponse → typed ingest records). IngestOrchestrator
// owns the shared write path: sync_runs lifecycle, FITID dedup,
// txn_headers/txn_legs inserts, promote-on-clear, watermark
// advance, directory upsert. Both are scoped — they depend on
// AppDbContext.
builder.Services.AddScoped<Coffer.Api.Ingest.IPullProvider, Coffer.Api.Ingest.SimpleFin.SimpleFinPullProvider>();
// ADR-0031 Phase 4: OFX/QFX file upload. Stateless per-upload
// (no auth/credentials/last-synced state); registered as a
// singleton because the provider holds no per-request state — the
// payload + context arrive as method parameters.
// Real-world OFX 1.x headers commonly declare CHARSET:1252
// (Windows-1252); modern .NET strips legacy codepages from the
// default Encoding catalogue (compounded by InvariantGlobalization).
// Register the CodePagesEncodingProvider so OfxNet's SGML reader
// can resolve `1252` when it walks the header.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
builder.Services.AddSingleton<Coffer.Api.Ingest.IFileProvider, Coffer.Api.Ingest.Ofx.OfxFileProvider>();
// ADR-0042: QIF file upload (a workplace 401(k) plan + generic
// QIF). Hand-rolled parser, no NuGet dependency. The orchestrator
// resolves all IFileProvider registrations into a key→provider map.
builder.Services.AddSingleton<Coffer.Api.Ingest.IFileProvider, Coffer.Api.Ingest.Qif.QifFileProvider>();
builder.Services.AddScoped<Coffer.Api.Ingest.IngestOrchestrator>();

// Quote-provider family (ADR-0033). Parallel structure to ingest:
// per-family typed interfaces + per-family orchestrator. The
// SimpleFIN-holdings provider extracts prices from the stored
// per-account raw payload (migration 080) — no external HTTP,
// piggybacks on whatever the ingest sync already captured.
builder.Services.AddScoped<
    Coffer.Api.Quotes.IQuotePullProvider,
    Coffer.Api.Quotes.SimpleFin.SimpleFinHoldingsQuoteProvider>();
// ADR-0054 D1 / ADR-0057: market-data quote provider (Yahoo Finance, EOD close).
// ALWAYS registered; external egress is OPT-IN PER LEDGER via the `quotes`
// user-preference (enabledProviders). The orchestrator runs an opt-in provider
// (IQuotePullProvider.RequiresOptIn) only when the acting ledger's pref lists
// its key, so a default install still makes no external calls until a user
// turns Yahoo on for a ledger. Typed HttpClient via the factory (same pattern
// as SimpleFinClient): base address + UA live here; the provider owns the
// chart-endpoint shape.
builder.Services.Configure<Coffer.Api.Quotes.Yahoo.YahooQuoteOptions>(
    builder.Configuration.GetSection(Coffer.Api.Quotes.Yahoo.YahooQuoteOptions.SectionName));
builder.Services.AddHttpClient<Coffer.Api.Quotes.Yahoo.YahooFinanceQuoteProvider>(c =>
{
    c.BaseAddress = new Uri("https://query1.finance.yahoo.com");
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CofferQuotes/1.0)");
});
builder.Services.AddScoped<Coffer.Api.Quotes.IQuotePullProvider>(
    sp => sp.GetRequiredService<Coffer.Api.Quotes.Yahoo.YahooFinanceQuoteProvider>());
builder.Services.AddScoped<Coffer.Api.Quotes.QuoteOrchestrator>();
// Slice 2c.2: in-process per-connection lock paired with the
// DB-level UNIQUE partial index on sync_runs. Singleton so the
// semaphores live across requests.
builder.Services.AddSingleton<SyncConnectionLock>();

builder.Services.AddScoped<LedgersRepository>();
builder.Services.AddScoped<Coffer.Api.Auth.LedgerAuthorizer>();
builder.Services.AddScoped<InvitesRepository>();
builder.Services.AddScoped<AccountsRepository>();
builder.Services.AddScoped<AccountBalancesRepository>();
builder.Services.AddScoped<OverviewRepository>();
builder.Services.AddScoped<ReportingRepository>();
builder.Services.AddScoped<InvestmentReportingRepository>();
builder.Services.AddScoped<AccountsReportingRepository>();

// MCP server (ADR-0063): read-only report-building tools over the shared
// reporting/investment layers, exposed over streamable HTTP at /mcp. Tools
// run in the request's DI scope, so the repositories they take are the same
// RLS-scoped ones the REST endpoints use — the bearer's user is the data
// boundary. Stateful transport (the default): each client gets an Mcp-Session-Id
// + a persistent GET SSE stream for server->client messages. Stateless mode
// closes that stream, which makes mcp-remote (the bridge claude.ai / Claude
// Desktop use) reconnect-loop and drop the connector; a single self-hosted
// container holds session state in memory fine. Gated + mapped below.
builder.Services.AddScoped<McpTokensRepository>();
// Audit-log retention (ADR-0081 D3): prune mcp_tool_invocations + ledger_operations to
// Api:AuditRetentionDays (default 180) on a daily cadence. Always on — NOT gated on
// MCP, since ledger-operation retention applies regardless; a system invariant, not an
// admin-scheduled job.
builder.Services.AddHostedService<Coffer.Api.Audit.AuditRetentionService>();

if (mcpEnabled)
{
    var mcpServer = builder.Services
        .AddMcpServer()
        // Stateless is pinned, NOT left to the SDK's default: the default flipped
        // from false to true in SDK 2.0, and stateless is the mode the comment
        // above describes as breaking mcp-remote. Nothing warned about it — the
        // 1.4.1 -> 2.1.0 bump built clean with zero warnings and would have
        // silently dropped the Claude connector at runtime. Reflected off both
        // packages to confirm: 1.4.1 ships Stateless=False, 2.1.0 ships True.
        .WithHttpTransport(o => o.Stateless = false)
        // Read tools auto-register via [McpServerToolType] assembly scan.
        .WithToolsFromAssembly(typeof(DiscoveryTools).Assembly);

    // Write tools (ADR-0068) live in classes WITHOUT [McpServerToolType] so the
    // assembly scan skips them. ADR-0081 D2: register them ALWAYS (not gated on the
    // startup writes flag) so the hot kill-switch works — McpWriteGuard rejects each
    // call unless writes are enabled RIGHT NOW and the token carries coffer.write.
    // The mutating surface is present but inert until an admin enables writes
    // (effective immediately, no restart).
    mcpServer.WithTools<Coffer.Api.Mcp.McpWriteTools>();

    // Per-call write audit (ADR-0081 D3): one CallTool filter records every write-tool
    // invocation (tool, user, bounded args, outcome) via McpAuditRecorder. Reads are
    // not audited; the single central filter covers the whole write surface (a new
    // write tool is picked up automatically via McpWriteTools.ToolNames).
    // Audit filter OUTERMOST (added first) so a write blocked by the ledger-role auth
    // filter (added second, inner) is still recorded as a failed attempt (ADR-0083 D2).
    mcpServer.WithRequestFilters(f => f
        .AddCallToolFilter(Coffer.Api.Mcp.McpAuditFilter.Create())
        .AddCallToolFilter(Coffer.Api.Mcp.McpLedgerWriteAuthFilter.Create()));
    builder.Services.AddScoped<Coffer.Api.Mcp.McpAuditRecorder>();
    builder.Services.AddScoped<Coffer.Api.Db.Repositories.McpAuditRepository>();

    // The write-tool authorization + kill-switch choke point (ADR-0081 D1/D2).
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<Coffer.Api.Mcp.McpWriteGuard>();

    // OAuth 2.1 AS (ADR-0063 §D2) for the native one-click connector flow:
    // authorization-code + PKCE + refresh, revocable reference access tokens,
    // a single read scope. EF stores live on AppDbContext (migration 146).
    // Signing/encryption keys persist under the data volume so tokens survive
    // restarts. The authorize/token endpoints are handled by our minimal-API
    // passthrough (slice 3); discovery + DCR are wired in later slices.
    var oidcKeys = OpenIddictKeyMaterial.LoadOrCreate(
        Path.Combine(builder.Environment.ContentRootPath, "data", "openiddict"));
    builder.Services.AddOpenIddict()
        .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
        .AddServer(o =>
        {
            o.SetAuthorizationEndpointUris("oauth/authorize")
             .SetTokenEndpointUris("oauth/token");
            o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
             .AllowRefreshTokenFlow();
            // Register both scopes so either is requestable at authorize time. The
            // write GATE is NOT the OAuth scope permission — it's the runtime
            // kill-switch + McpWriteGuard (ADR-0081 D1/D2): a token carries coffer.write
            // only if the client requested it and the user consented, and writes execute
            // only while the deployment-wide switch is on.
            o.RegisterScopes(Coffer.Api.Mcp.McpScopes.Read, Coffer.Api.Mcp.McpScopes.Write);
            // Don't reject an authorize request for coffer.write just because the client's
            // registration doesn't list it as a permitted scope. mcp-remote requests EVERY
            // advertised scope and DCR clients register read-only, so per-client scope
            // permissions would break the whole connection (OpenIddict ID2051). Gate writes
            // by the kill-switch + guard, not per-client scope perms (ADR-0081 D1).
            o.IgnoreScopePermissions();

            // MCP clients send an RFC 8707 `resource` parameter (the MCP server
            // URI) on the authorize + token requests. OpenIddict rejects resources
            // it doesn't know (ID2190). This AS protects a single resource — its
            // own /mcp — so audience-binding adds nothing: the access token is an
            // opaque, revocable, scoped reference token that only our /mcp accepts.
            // Strip the parameter before OpenIddict validates it (ordered first).
            o.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(b =>
                b.UseInlineHandler(ctx => { ctx.Request["resource"] = null; return default; })
                 .SetOrder(int.MinValue + 100_000));
            o.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(b =>
                b.UseInlineHandler(ctx => { ctx.Request["resource"] = null; return default; })
                 .SetOrder(int.MinValue + 100_000));

            o.UseReferenceAccessTokens();
            o.SetAccessTokenLifetime(TimeSpan.FromHours(1));
            o.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
            o.AddSigningKey(oidcKeys.Signing);
            o.AddEncryptionKey(oidcKeys.Encryption);
            o.UseAspNetCore()
             .EnableAuthorizationEndpointPassthrough()
             .EnableTokenEndpointPassthrough()
             // The container serves plain HTTP on :8080; TLS terminates upstream
             // (reverse proxy / the cloudflared tunnel), so the in-app HTTPS
             // requirement is relaxed — the deployment, not OpenIddict, owns TLS.
             .DisableTransportSecurityRequirement();
        })
        .AddValidation(o =>
        {
            o.UseLocalServer();
            o.UseAspNetCore();
        });
}
builder.Services.AddScoped<UserPreferencesRepository>();
builder.Services.AddScoped<SchedulesRepository>();
builder.Services.AddScoped<GlobalSchedulesRepository>();
// Always registered (not gated by mcpEnabled): the admin System-settings
// endpoint must be reachable to TURN MCP ON when it is currently off (D8).
builder.Services.AddScoped<SystemSettingsRepository>();
builder.Services.AddScoped<MetaRepository>();
builder.Services.AddScoped<AccountGroupsRepository>();
builder.Services.AddScoped<FeedConnectionsRepository>();
builder.Services.AddScoped<LedgerOperationsRepository>();
builder.Services.AddScoped<RegisterRepository>();
builder.Services.AddScoped<PayeesRepository>();
builder.Services.AddScoped<TransactionsRepository>();
builder.Services.AddScoped<TagsRepository>();
builder.Services.AddScoped<InvestmentTransactionsRepository>();
builder.Services.AddScoped<BulkTransactionsRepository>();
builder.Services.AddSingleton<Coffer.Domain.Reminders.RecurrenceExpander>();
builder.Services.AddScoped<RemindersRepository>();
// Mig 102 (balances) + mig 120 (posting counts): the two leg-derived
// denormalizations. Triggers dropped; every writer calls this directly
// (or is covered implicitly via LegDerivedRecomputeInterceptor).
builder.Services.AddScoped<LegDerivedRecomputeService>();
// Mig 104: holdings triggers dropped; every writer that mutates
// investment-shape txn_legs through a ChangeTracker-bypassing path
// (importer Dapper) calls this directly. EF-tracked paths — including
// the investment editor's create/patch — are covered by
// HoldingsRecomputeInterceptor implicitly.
builder.Services.AddScoped<HoldingsRecomputeService>();
builder.Services.AddScoped<HoldingsRepository>();
builder.Services.AddScoped<SecuritiesRepository>();
// ADR-0031 Phase 3a/c: orchestrator looks up provider → security
// mappings on the brokerage branch; investment editor's save path
// (Phase 3d) records new mappings.
builder.Services.AddScoped<ProviderSecurityMappingsRepository>();

// ADR-0037: per-ledger snapshots. Repository is request-scoped (it
// owns the DbContext lifetime); the scheduler is a singleton
// IHostedService that creates its own scope per tick.
builder.Services.AddScoped<Coffer.Api.Snapshots.LedgerSnapshotsRepository>();
// Generic per-ledger daily scheduler (mig 136): one worker dispatches each due
// scheduled_jobs row to its job_type handler.
builder.Services.AddScoped<Coffer.Api.Scheduling.IScheduledJobHandler,
    Coffer.Api.Quotes.Scheduling.QuoteRefreshJobHandler>();
builder.Services.AddScoped<Coffer.Api.Scheduling.IScheduledJobHandler,
    Coffer.Api.Snapshots.SnapshotJobHandler>();
// Global (non-ledger) job handlers — the same worker scans global_scheduled_jobs
// (mig 139) and dispatches these (ADR-0060: whole-DB backup).
builder.Services.AddScoped<Coffer.Api.Scheduling.IGlobalScheduledJobHandler,
    Coffer.Api.Backup.DailyBackupJobHandler>();
builder.Services.AddSingleton<Coffer.Api.Scheduling.SchedulerRunner>();
builder.Services.AddHostedService<Coffer.Api.Scheduling.SchedulerService>();

// Moneydance UI import (ADR-0071 D2): the shared pipeline service, an in-memory
// job registry, and the background runner that drives imports as coffer_service.
builder.Services.AddSingleton<
    Coffer.Importer.Moneydance.Pipeline.IMoneydanceImportService,
    Coffer.Importer.Moneydance.Pipeline.MoneydanceImportService>();
builder.Services.AddSingleton<Coffer.Api.Import.ImportJobRegistry>();
builder.Services.AddSingleton<Coffer.Api.Import.ImportJobRunner>();

// Starter categories for new ledgers (ADR-0071 D5). Stateless — parses the
// embedded catalogue once.
builder.Services.AddSingleton<Coffer.Api.Provisioning.StarterCategoriesSeeder>();

// Demo-ledger seeding (ADR-0088), driven by the setup form's "include Demo" box.
builder.Services.AddSingleton<Coffer.Api.Provisioning.ProvisioningService>();

// Fido2NetLib registration. RP id / origins / timeout come from
// Api:Fido2; tests overlay these with localhost values from the
// WebApplicationFactory's in-memory configuration.
builder.Services.AddFido2(fido2Options =>
{
    var fido2 = apiOptions.Fido2;
    fido2Options.ServerDomain = fido2.RpId;
    fido2Options.ServerName = fido2.RpName;
    fido2Options.Origins = new HashSet<string>(fido2.Origins);
    fido2Options.TimestampDriftTolerance = fido2.TimeoutSeconds * 1000;
});
builder.Services.AddScoped<IWebAuthnService, Fido2WebAuthnService>();

// Rate limiting for the recovery-code login (ADR-0013 follow-through). A
// recovery code is a bearer secret (unlike an unforgeable assertion) and
// each attempt is an expensive Argon2id verify, so cap attempts per client
// IP: bounds brute-force AND the memory-DoS the verify cost would enable.
// The limit is read from IOptions at request time (not eagerly) so the
// configured value — including test overrides applied after host build —
// takes effect. Window is fixed at 1 minute.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(LoginEndpoints.RecoveryRateLimitPolicy, httpContext =>
    {
        var limit = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>()
            .Value.Auth.RecoveryRateLimitPerMinute;
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    // ADR-0081 D4: rate-limit anonymous Dynamic Client Registration. DCR is
    // internet-facing + unauthenticated; a legitimate client registers once, so a
    // tight per-IP window bounds burst abuse of the endpoint (MaxDynamicClients is
    // the absolute ceiling). Same shape + IP partition as the recovery limiter.
    options.AddPolicy(OAuthEndpoints.DcrRateLimitPolicy, httpContext =>
    {
        var limit = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>()
            .Value.Mcp.DcrRateLimitPerMinute;
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

var app = builder.Build();

// -- master-KEK env-var deprecation (ADR-0092 D6). The key was read from
// COFFER_MASTER_KEK_BASE64 and written through to the key file, so this boot and
// every later one work either way. Say so once, loudly enough to act on: the env
// var is removed a release after this one. Logged here rather than at resolution
// time because the logging pipeline doesn't exist until the host is built.
// -- the env var is set but the key file won (ADR-0092 D1). Loud, because the
// operator plainly believes that variable is doing something: they may have just
// edited it expecting a key change, or — worse — be looking at a stale value left
// over from before a rotation. Silence here is what made the original env-first
// precedence dangerous.
if (masterKeyEnvIgnored)
    app.Logger.LogWarning(
        "{EnvVar} is set but does NOT match the key file at {Path}, and was IGNORED — the file "
        + "is the source of truth (ADR-0092). If you rotated the key from the UI, the variable is "
        + "stale: remove it from your .env. If you meant to change the key, write it to the file "
        + "instead. Leaving a mismatched value here changes nothing and only invites confusion.",
        MasterKeyLoader.EnvVarName, masterKeyStore.Path);

if (masterKeySource == MasterKeyLoader.KeySource.MigratedFromEnvironment)
    app.Logger.LogWarning(
        "Master KEK was read from the deprecated {EnvVar} environment variable and copied to {Path} "
        + "(ADR-0092). Remove {EnvVar} from your .env — the key file is now the source of truth, and "
        + "the environment variable stops being read in a future release.",
        MasterKeyLoader.EnvVarName, masterKeyStore.Path, MasterKeyLoader.EnvVarName);

// -- where the DB role passwords came from. Worth stating for the same reason as
// the key's source: "which credential is this process actually using" is the
// first question when a connection is refused, and an install mid-transition has
// a password in two places.
foreach (var outcome in dbPasswordOutcomes)
{
    if (outcome.InlinePasswordIgnored)
        app.Logger.LogWarning(
            "{Role}'s password came from its password file, and the Password= in its connection "
            + "string was IGNORED. Remove it — the file is the source of truth, and leaving a second "
            + "copy in the environment defeats the point of moving the secret out of it.",
            outcome.Role);
    else if (outcome.FromFile)
        app.Logger.LogInformation("{Role} authenticates with a password read from its password file.", outcome.Role);
    else
        app.Logger.LogWarning(
            "{Role}'s password is embedded in its connection string, so it is present in this "
            + "process's environment — readable via `docker inspect`, /proc/<pid>/environ and crash "
            + "dumps. Move it to a file and point {Key} at it (see scripts/migrate-db-secrets.sh).",
            outcome.Role,
            outcome.Role == "coffer_app"
                ? DbPasswordResolver.AppPasswordFileKey
                : DbPasswordResolver.ServicePasswordFileKey);
}

// -- a freshly minted key (ADR-0092 D3). Said at Warning, not Information: on a
// virgin install this is the moment the key that wraps everything came into
// existence, and the operator has not seen it yet — the setup ceremony (D2) shows
// it. If they reached here with --adopt-new-kek instead, the previously wrapped
// secrets are now unreadable and need re-establishing.
if (masterKeyWasMinted)
    app.Logger.LogWarning(
        "A new master KEK was generated at {Path}. Back it up — it is shown once during setup, "
        + "and it is what carries sealed secrets (feed tokens, the stored backup passphrase, the "
        + "Google Drive token) to another install. Your ledger data and passkeys do not depend on it.",
        masterKeyStore.Path);

// -- bootstrap restore (ADR-0061): if the setup UI staged a backup, apply it
// BEFORE migrating/serving. RestoreAsync(clean: true) wipes the schema to empty
// (dropping only the service-role-owned objects; the install-managed extensions
// survive) and rebuilds from the dump, so the server comes up on
// the restored data. The passphrase was verified at upload, so a decrypt failure
// here is not expected; any failure clears the request (no boot loop) and logs
// loudly. Runs as a one-off before the HTTP host starts — never concurrent with
// serving (the operator's machine is the only client in the bootstrap window).
// Skipped under the same `Migrations:Skip` flag the migration/CLI blocks use:
// test fixtures share one database, so a staging marker leaked by a test must
// never trigger a clean restore (it would DROP the shared schema) at the next
// WebApplicationFactory boot.
// -- adopt the source install's KEK (ADR-0092 D4), BEFORE the restore is applied.
//
// Ordering is the whole design here. The MasterKey singleton was resolved during
// DI registration, so this process holds the LOCAL key; applying the restore now
// would leave D5's reconciliation running under the wrong key and clearing secrets
// the operator explicitly supplied the key for. So: adopt the key, restart, and let
// the NEXT boot — whose MasterKey is the adopted one — apply the still-pending
// restore and reconcile against it.
//
// If the supplied key turns out to be wrong (only possible for a v1 archive, which
// carries no fingerprint for the upload to check), reconciliation on that boot
// clears what won't open — the same outcome as not supplying a key at all.
if (!app.Configuration.GetValue<bool>("Migrations:Skip")
    && BootstrapRestoreStaging.IsPending()
    && BootstrapRestoreStaging.HasSourceKey())
{
    var adoptLogger = app.Services
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.BootstrapRestore");
    var sourceKey = BootstrapRestoreStaging.ReadSourceKey()!;

    // The key file may not be writable — the documented read-only injection case
    // (/run/secrets/…, a projected Kubernetes Secret). Rotation guards this; adoption
    // must too, and more carefully: a throw here happens BEFORE ClearSourceKey, so the
    // staged key survives and the next boot tries again — a permanent crash loop that
    // takes the install down rather than just refusing one operation. Clear the
    // staging and let the restore proceed WITHOUT adopting: D5 reconciliation then
    // does the honest thing and reports what it had to abandon.
    string? archived = null;
    var adopted = false;
    try
    {
        archived = masterKeyStore.Archive($"{DateTime.UtcNow:yyyyMMddTHHmmssZ}");
        // NOT the source install's id — we don't have it. The archive carries the key
        // only, and the operator pastes just that. So the local id is kept, which means
        // the restored rows' lek_kek_id values (the source's) and this file's id can
        // disagree. Harmless: the id is advisory, only rotation reads it, and rotation
        // re-wraps every row regardless of what it says.
        masterKeyStore.Write(sourceKey, MasterKeyLoader.ResolveId());
        adopted = true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        if (archived is not null)
            try { masterKeyStore.RestoreFromArchive(archived); }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                adoptLogger.LogError(restoreEx,
                    "Could not put the previous key file back after a failed adoption. It is at "
                    + "{Archive}; restore it to {Path} by hand.", archived, masterKeyStore.Path);
            }
        archived = null;
        adoptLogger.LogError(ex,
            "Could not adopt the source install's master KEK: the key file at {Path} is not "
            + "writable (an injected read-only secret?). Continuing WITHOUT it — the restore will "
            + "apply and reconciliation will clear the secrets that key would have preserved "
            + "(feeds, backup passphrase, Drive). Put the key in place where that secret is "
            + "managed and re-upload if you need them carried over.",
            masterKeyStore.Path);
    }

    // Shred the staged copy either way — it now lives in the key file, or it never
    // will, and a leftover makes the next boot retry the adoption forever.
    BootstrapRestoreStaging.ClearSourceKey();

    if (!adopted)
        // Nothing was adopted, so there is no new key to load and no reason to bounce.
        // Fall through to the restore block below, which applies under the local key.
        adoptLogger.LogWarning("Proceeding with the pending restore under the existing key.");

    // Durable audit (ADR-0092 D2). No actor: this runs at boot, not in a request —
    // the operator who uploaded the restore is recorded by that action, and there is
    // no authenticated user here to attribute the adoption to.
    //
    // Best-effort, UNLIKE the reveal endpoint where the audit write gates the
    // response. The tradeoff inverts here: this block runs before migrations, so a
    // missing or unreachable table would throw, and letting that abort the boot
    // would leave the install unable to start at all. An unaudited adoption is far
    // better than a boot loop — and the key file has already been written by this
    // point, so failing here wouldn't even undo anything.
    if (adopted)
    {
        try
        {
            await app.Services.GetRequiredService<Coffer.Api.Db.Repositories.AdminAuditRepository>()
                .AppendAsync(
                    Coffer.Api.Db.Repositories.AdminAuditActions.MasterKeyAdopted,
                    actorUserId: null,
                    detail: $"adopted the source install's key for a pending restore; previous key "
                          + $"archived at {archived ?? "(none)"}");
        }
        catch (Exception ex)
        {
            adoptLogger.LogWarning(ex,
                "Could not record the KEK adoption in admin_audit_events; continuing — the adoption "
                + "itself already succeeded.");
        }

        adoptLogger.LogWarning(
            "Adopted the source install's master KEK for the pending restore; previous key archived "
            + "at {Archive}. Exiting so the restore applies under the adopted key on the next boot "
            + "(compose `restart: unless-stopped` brings the container straight back; restart manually "
            + "if you're running outside Docker).",
            archived ?? "(none)");

        // Exit rather than serve-then-stop: this process still holds the OLD key in
        // memory, and there is no reason to answer requests with it. Nothing has been
        // applied yet — the restore is still staged — so an exit here is safe to repeat.
        return 0;
    }
    // Adoption failed and was reported. The staged key is gone, so the block below
    // applies the restore under the existing key on THIS boot; no bounce needed.
}

// Set when a staged restore was applied on this boot, so the post-migration block
// below knows to reconcile KEK-wrapped material (ADR-0092 D5).
var restoreApplied = false;

if (!app.Configuration.GetValue<bool>("Migrations:Skip")
    && BootstrapRestoreStaging.IsPending())
{
    using var scope = app.Services.CreateScope();
    var restoreLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.BootstrapRestore");
    var restoreSvc = scope.ServiceProvider.GetRequiredService<BackupService>();
    try
    {
        restoreLogger.LogWarning("Bootstrap restore pending — applying the staged backup over the database…");
        await using (var staged = File.OpenRead(BootstrapRestoreStaging.ArchivePath))
            await restoreSvc.RestoreAsync(staged, BootstrapRestoreStaging.ReadPassphrase(), clean: true);

        // Reconciliation (ADR-0092 D5) does NOT run here — it runs after migrations,
        // below. The restored dump can be older than this build, so querying it with
        // the current EF model raises 42703 undefined_column; that isn't a
        // BackupException, so it would escape the filter below and kill the boot —
        // after `finally` had already cleared the staging, meaning the restore was
        // applied and reconciliation was silently skipped for good.
        restoreApplied = true;
        restoreLogger.LogWarning("Bootstrap restore complete.");
    }
    catch (Exception ex) when (ex is BackupException or BackupDecryptException)
    {
        restoreLogger.LogError(ex,
            "Bootstrap restore FAILED; clearing the request (no retry loop). Re-upload from the setup screen.");
    }
    finally
    {
        BootstrapRestoreStaging.Clear();
    }
}

// -- migrations: applied at startup with DbUp using the service-role
// connection (coffer_service has BYPASSRLS and DDL privilege; coffer_app
// can't run ENABLE ROW LEVEL SECURITY). Skipped when the
// "Migrations:Skip" config key is true so test fixtures that already
// applied the SQL by hand can opt out (saves a round-trip per fixture
// setup). Also skipped for `restore`: a whole-DB restore (ADR-0060) lands
// on a fresh install and rebuilds the entire schema from the dump — running
// migrations first would create objects pg_restore then collides with.
if (!app.Configuration.GetValue<bool>("Migrations:Skip")
    && args is not ["restore", ..])
{
    using var scope = app.Services.CreateScope();
    var apiOpts = scope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
    var migrationLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.Migrations");
    var migrationsDirectory = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
    MigrationRunner.Run(apiOpts.ServiceConnectionString, migrationsDirectory, migrationLogger);
}

// -- post-restore KEK reconciliation (ADR-0092 D5). AFTER migrations, deliberately:
// a restored dump can predate this build, and reconciliation queries it through the
// current EF model, so running it against the un-migrated schema raises 42703
// undefined_column.
//
// Unconditional for a restore, not gated on the upload's fingerprint pre-flight: v1
// artifacts carry no fingerprint, so gating would skip exactly the backups that
// couldn't be checked.
//
// A failure here does NOT abort the boot. The install would otherwise crash-loop into
// unavailability, and the state left behind is the same one that existed before D5 —
// bad, but serving. Logged at Error so it's discoverable rather than silent.
if (restoreApplied)
{
    using var scope = app.Services.CreateScope();
    var reconcileLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.BootstrapRestore");
    try
    {
        await scope.ServiceProvider
            .GetRequiredService<KekReconciliationService>()
            .ReconcileAsync();
        reconcileLogger.LogWarning("Serving the restored database.");
    }
    catch (Exception ex)
    {
        reconcileLogger.LogError(ex,
            "Post-restore KEK reconciliation FAILED. The restore itself succeeded and the server "
            + "is starting, but secrets sealed under a different master KEK may remain in the "
            + "database and will fail when a feed sync, scheduled backup, or Drive push touches "
            + "them. Check the master key, then re-run a restore or re-establish those secrets.");
    }
}

// CLI subcommand dispatch (no HTTP host). Runs after migrations so the
// bootstrap_tokens table exists; reuses the app's DI. Surface:
//   docker compose exec api dotnet coffer-api.dll bootstrap-token
//   (locally: dotnet run -- bootstrap-token)
// NB `dotnet coffer-api.dll`, not `coffer-api`: ENTRYPOINT is
// ["dotnet","coffer-api.dll"] and the image carries no apphost binary.
// Prints a fresh first-run setup URL on stdout (ADR-0059 / follow-up).
if (args is ["bootstrap-token", ..])
{
    using var cliScope = app.Services.CreateScope();
    var bootstrap = cliScope.ServiceProvider
        .GetRequiredService<Coffer.Api.Db.Services.BootstrapTokenService>();
    var url = await bootstrap.ReissueSetupUrlAsync();
    if (url is null)
    {
        await Console.Error.WriteLineAsync(
            "Setup already complete — a credential exists; no bootstrap token issued.");
        return 1;
    }
    Console.WriteLine(url);
    return 0;
}

// Whole-DB backup / restore (ADR-0060), operator CLI via `docker compose exec`.
// The passphrase rides an env var, never an arg (args leak in the process
// list). Backup encrypts a pg_dump archive; restore is destructive and gated
// behind --force.
static string? CliArg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

if (args is ["backup", ..])
{
    var outPath = CliArg(args, "--out");
    var passphrase = Environment.GetEnvironmentVariable("COFFER_BACKUP_PASSPHRASE");
    if (string.IsNullOrEmpty(outPath) || string.IsNullOrEmpty(passphrase))
    {
        await Console.Error.WriteLineAsync(
            "Usage: coffer-api backup --out <path>   (set COFFER_BACKUP_PASSPHRASE)");
        return 2;
    }
    using var cliScope = app.Services.CreateScope();
    var svc = cliScope.ServiceProvider.GetRequiredService<BackupService>();
    try
    {
        await using (var outStream = File.Create(outPath))
            await svc.CreateAsync(passphrase, outStream);
    }
    catch (Exception ex) when (ex is BackupException or BackupDecryptException)
    {
        await Console.Error.WriteLineAsync($"Backup failed: {ex.Message}");
        return 1;
    }
    Console.WriteLine($"Backup written to {outPath}");
    return 0;
}

if (args is ["restore", ..])
{
    var inPath = CliArg(args, "--in");
    var passphrase = Environment.GetEnvironmentVariable("COFFER_BACKUP_PASSPHRASE");
    if (string.IsNullOrEmpty(inPath) || string.IsNullOrEmpty(passphrase))
    {
        await Console.Error.WriteLineAsync(
            "Usage: coffer-api restore --in <path> --force   (set COFFER_BACKUP_PASSPHRASE)");
        return 2;
    }
    if (!args.Contains("--force"))
    {
        await Console.Error.WriteLineAsync(
            "Refusing without --force: restore REPLACES the entire database.");
        return 2;
    }
    if (!File.Exists(inPath))
    {
        await Console.Error.WriteLineAsync($"No such file: {inPath}");
        return 2;
    }
    using var cliScope = app.Services.CreateScope();
    // KEK pre-flight (ADR-0074 / ADR-0071 D4): a cross-install restore leaves the
    // backup's KEK-sealed secrets (backup passphrase, Drive token) unopenable.
    // Refuse on a mismatch unless --allow-kek-mismatch, so an operator can't
    // silently clone onto a different Master KEK.
    try
    {
        await using var fpStream = File.OpenRead(inPath);
        var backupFp = await Coffer.Api.Backup.BackupCrypto.ReadKekFingerprintAsync(fpStream);
        var mk = cliScope.ServiceProvider.GetRequiredService<Coffer.Api.Crypto.MasterKey>();
        if (backupFp.Length > 0
            && !Coffer.Api.Backup.KekFingerprint.Matches(
                backupFp, Coffer.Api.Backup.KekFingerprint.Compute(mk.KeyBytes))
            && !args.Contains("--allow-kek-mismatch"))
        {
            // The remedy names the key FILE, not COFFER_MASTER_KEK_BASE64. ADR-0092
            // D1 made the file the source of truth and the env var is IGNORED
            // whenever the file exists — which is every install now. The old wording
            // sent an operator to set a variable, restart, and find the log telling
            // them it had been ignored: advice that cannot work, offered at the
            // moment they are least able to afford a wrong turn.
            await Console.Error.WriteLineAsync(
                "Refusing: this backup was sealed under a DIFFERENT Master KEK. Data + passkeys "
                + "restore, but the backup passphrase and Google Drive connection will NOT (re-set "
                + "them afterward). To carry those across too, put the SOURCE install's key in the "
                + $"key file at {masterKeyStore.Path} before restoring (ADR-0092 D1 — the file is "
                + "the source of truth; COFFER_MASTER_KEK_BASE64 is ignored while it exists). "
                + "Re-run with --allow-kek-mismatch to restore without them.");
            return 2;
        }
    }
    catch (Coffer.Api.Backup.BackupDecryptException)
    {
        await Console.Error.WriteLineAsync($"Not a valid .cofferbak file: {inPath}");
        return 2;
    }

    var svc = cliScope.ServiceProvider.GetRequiredService<BackupService>();
    try
    {
        await using (var inStream = File.OpenRead(inPath))
            // clean: true — wipe the schema to empty first, same as the bootstrap
            // path. This used to be left at the default (false) on the premise that
            // the CLI only ever meets a fresh, empty database. It doesn't:
            // `docker compose up` boots the API, which runs DbUp before an operator
            // can get a shell, so by the time this executes the schema is fully
            // migrated and may hold real rows. pg_restore into a populated schema
            // collides on every existing object and MERGES what it can, which
            // produces a hybrid database — two of each seeded ledger, hundreds of
            // ignored "already exists" errors, and a non-zero exit only after the
            // damage is done.
            //
            // Safe in the empty case the old premise imagined: the wipe drops only
            // service-role-owned objects, so on an empty schema it finds nothing.
            await svc.RestoreAsync(inStream, passphrase, clean: true);
    }
    catch (Exception ex) when (ex is BackupException or BackupDecryptException)
    {
        await Console.Error.WriteLineAsync($"Restore failed: {ex.Message}");
        return 1;
    }

    // ADR-0092 D5 — same invariant as the bootstrap path: leave no ciphertext
    // this install can't open. Reported on stdout because the CLI operator has
    // re-establishment work to do and won't be looking at the app's log.
    var reconciled = await cliScope.ServiceProvider
        .GetRequiredService<KekReconciliationService>()
        .ReconcileAsync();
    if (reconciled.AnythingChanged)
    {
        Console.WriteLine(
            $"KEK reconciliation: {reconciled.LedgersRekeyed} ledger key(s) replaced, "
            + $"{reconciled.FeedConnectionsNeedingReauth} feed connection(s) need re-auth"
            + $"{(reconciled.BackupPassphraseCleared ? ", backup passphrase cleared (schedule disabled)" : "")}"
            + $"{(reconciled.DriveDisconnected ? ", Google Drive disconnected" : "")}.");
        Console.WriteLine(
            "Those secrets were sealed under a different master KEK. Your ledger data and "
            + "passkeys restored intact — re-link feeds, set a new backup passphrase, and "
            + "reconnect Drive as needed.");
    }

    Console.WriteLine("Restore complete.");
    return 0;
}

// (The `provision --mode clean|demo` subcommand was retired in ADR-0088. It
// shaped install state before the first user existed, which only made sense
// while migrations seeded placeholder Default/Demo ledgers. Migration 186 drops
// those, so there is nothing to clean, and the Demo ledger is now an opt-in
// checkbox on the setup form that runs the same import as any other.)

// -- unknown subcommand. Every dispatch block above returns, so reaching here with
// a non-flag first argument means it matched nothing — and the default behaviour
// was to fall through and START THE HTTP SERVER. That is a bad way to retire a
// command: `rotate-kek` was removed in ADR-0092 D4, and an operator running it from
// memory or an old runbook silently got a SECOND API instance against the same
// database — exactly what scripts/dev-up-docker.sh goes out of its way to prevent.
//
// Flags are excluded on purpose: `--adopt-new-kek` (D3) and friends are read by the
// startup path, not dispatched here, so they must still reach the server.
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    await Console.Error.WriteLineAsync(
        $"coffer-api: unknown subcommand '{args[0]}'.\n"
        + "Available: bootstrap-token, backup, restore.\n"
        + "`rotate-kek` was removed in ADR-0092 — rotate the master key from the admin UI "
        + "(System → Encryption).\n"
        + "Run with no arguments to start the server.");
    return 2;
}


// First-run bootstrap. When no WebAuthn credentials exist, mint a
// one-shot token and log its plaintext exactly once — the operator
// pastes the URL into the browser to register the first passkey at
// /setup/{token}. Idempotent: subsequent starts (credentials already
// present, or a fresh token still unconsumed) no-op cleanly.
// Skipped under the same `Migrations:Skip` flag so test fixtures that
// own their own seeding aren't second-guessed.
if (!app.Configuration.GetValue<bool>("Migrations:Skip"))
{
    using var scope = app.Services.CreateScope();
    var bootstrapService = scope.ServiceProvider
        .GetRequiredService<Coffer.Api.Db.Services.BootstrapTokenService>();
    await bootstrapService.EnsureBootstrapTokenAsync();
}

// -- pipeline ----------------------------------------------------------------
// Honor the reverse proxy's X-Forwarded-Proto / X-Forwarded-Host (Traefik,
// nginx, etc.) so request-derived URLs are the external https://<domain> the
// browser/client actually used — not the container's internal http host. The
// OAuth issuer + discovery endpoints, the RFC 9728 resource metadata, the DCR
// registration_endpoint, and the /login?returnUrl redirect all depend on this
// behind a proxy (ADR-0063). No-op when no proxy sets the headers (local
// :8080 direct). KnownProxies/Networks are cleared because the container is
// only reachable via the proxy in that topology; if it were directly exposed,
// these headers would be spoofable.
var forwardedHeaderOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
forwardedHeaderOptions.KnownIPNetworks.Clear();
forwardedHeaderOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaderOptions);

app.UseCofferProblemDetails();
// Single-container packaging (ADR-0059): the Docker image copies the built
// SPA into wwwroot and the API serves it same-origin. Static assets are
// served before auth/scope so they don't pay that overhead. No-op in dev —
// there's no wwwroot; Vite serves the SPA and proxies /api.
app.UseStaticFiles();
app.UseMiddleware<RequestScopeMiddleware>();
// One access line per request (method/path/status/duration) within the traceId
// scope (ADR-0086). Framework request logging stays at Warning; this is ours.
app.UseMiddleware<RequestAccessLogMiddleware>();
// MCP OAuth discovery (ADR-0063): an unauthenticated /mcp call must answer 401
// with a WWW-Authenticate header pointing at the protected-resource metadata, so
// an MCP client can find the authorization server (RFC 9728 + the MCP auth spec).
// Runs outermost so it patches the header on the way out, after the policy has
// produced the 401.
if (mcpEnabled)
{
    app.Use(async (ctx, next) =>
    {
        await next();
        if (ctx.Response.StatusCode == StatusCodes.Status401Unauthorized
            && !ctx.Response.HasStarted
            && ctx.Request.Path.StartsWithSegments("/mcp"))
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            ctx.Response.Headers["WWW-Authenticate"] =
                $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
        }
    });

    // OpenIddict 7.5 has no built-in DCR, so its discovery document omits the
    // registration_endpoint our /oauth/register (RFC 7591) provides. Inject it
    // into the response so MCP clients can discover where to register. Buffering
    // the small JSON document is cheap and keeps this independent of OpenIddict's
    // internal handler ordering.
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Path.Equals("/.well-known/oauth-authorization-server"))
        {
            await next();
            return;
        }
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next();
        }
        finally
        {
            ctx.Response.Body = original;
        }
        buffer.Seek(0, SeekOrigin.Begin);
        if (ctx.Response.StatusCode == StatusCodes.Status200OK
            && (ctx.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false)
            && JsonNode.Parse(buffer) is JsonObject doc)
        {
            if (doc["registration_endpoint"] is null)
                doc["registration_endpoint"] = $"{ctx.Request.Scheme}://{ctx.Request.Host}/oauth/register";
            var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString());
            ctx.Response.ContentLength = bytes.Length;
            await original.WriteAsync(bytes);
        }
        else
        {
            await buffer.CopyToAsync(original);
        }
    });
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// OpenAPI document at /openapi/v1.json (the .NET 10 default).
app.MapOpenApi();

app.MapHealthEndpoints();
app.MapMetaEndpoints();
app.MapAuthEndpoints();
app.MapSetupEndpoints();
app.MapLoginEndpoints();
app.MapAccountEndpoints();
app.MapLedgersEndpoints();
app.MapMembersEndpoints();
app.MapAdminUsersEndpoints();
app.MapInvitesEndpoints();
app.MapAccountsEndpoints();
app.MapCategoriesEndpoints();
app.MapTagsEndpoints();
app.MapAccountGroupsEndpoints();
app.MapFeedConnectionsEndpoints();
app.MapSyncRunsEndpoints();
app.MapLedgerOperationsEndpoints();
app.MapOverviewEndpoints();
app.MapPreferencesEndpoints();
app.MapTransactionsEndpoints();
app.MapBalancesEndpoints();
app.MapOfxIngestEndpoints();
app.MapQifIngestEndpoints();
app.MapImportEndpoints();
app.MapSnapshotsEndpoints();
app.MapSchedulesEndpoints();
app.MapInvestmentTransactionsEndpoints();
app.MapRemindersEndpoints();
app.MapPayeesEndpoints();
app.MapSecuritiesEndpoints();
app.MapQuotesEndpoints();
app.MapAdminBackupsEndpoints();
app.MapAdminDriveSyncEndpoints();
// Master-KEK reveal (ADR-0092 D2) — admin-gated, and the reveal itself needs a
// fresh passkey assertion on top of the session.
app.MapAdminMasterKeyEndpoints();
// Always mapped (independent of mcpEnabled): the admin reads/sets the MCP
// runtime toggle here even while MCP itself is off (D8).
app.MapAdminSystemSettingsEndpoints();

// MCP server (ADR-0063): streamable-HTTP JSON-RPC at /mcp, authenticated by the
// revocable bearer-token scheme (RequireMcp — least privilege, no REST access).
// Token-management endpoints are interactive (default cookie policy): a token
// can't mint another token. Both mapped only when MCP is enabled.
if (mcpEnabled)
{
    // RFC 9728 protected-resource metadata: tells an MCP client which
    // authorization server protects /mcp. Public (pre-auth discovery).
    app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) =>
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        return Results.Json(new Dictionary<string, object?>
        {
            ["resource"] = baseUrl + "/mcp",
            ["authorization_servers"] = new[] { baseUrl },
            // offline_access so clients request it and get a refresh token (silent
            // renewal); without it the 1h access token forces hourly re-auth.
            ["scopes_supported"] = new[] { "coffer.read", "coffer.write", "offline_access" },
            ["bearer_methods_supported"] = new[] { "header" },
        });
    }).AllowAnonymous();

    app.MapMcp("/mcp").RequireAuthorization(AuthPolicies.RequireMcp);
    app.MapMcpTokensEndpoints();
    app.MapOAuthEndpoints();
    app.MapAdminMcpEndpoints();
}

// SPA client-routing fallback (single-container packaging). A non-/api path
// that matched no endpoint serves the SPA shell so deep links / client routes
// load; an unmatched /api path stays a genuine JSON 404 (never shadowed by the
// shell). No-op in dev where wwwroot/index.html is absent.
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var indexPath = Path.Combine(
        app.Environment.WebRootPath ?? string.Empty, "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
});

app.Run();
return 0;

/// <summary>
/// Non-empty <c>Program</c> partial so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in <c>tests/Api.Tests</c> can find an entry-point type to bootstrap. The
/// top-level statements above generate the actual <c>Main</c>; this declaration
/// just exposes the synthesised class as <c>public</c> for cross-assembly use.
/// </summary>
public partial class Program;
