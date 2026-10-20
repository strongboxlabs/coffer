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

// Same layer, same two strings: pin the Postgres session timezone to UTC so
// EXTRACT(YEAR/MONTH/DAY FROM posted_at) cannot depend on the server's default.
// Unconditional — the password resolver above returns early when no password
// file is configured, and this guarantee has to hold either way.
var dbTimeZoneOutcomes = DbSessionTimeZone.ApplyTo(builder.Configuration);

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
// The one wall clock. Everything in src/Api reads the time through this, never
// DateTime.UtcNow or TimeProvider.System directly, so a test can move it and no
// code path quietly reads a different clock (ClockDisciplineTests).
builder.Services.AddSingleton(TimeProvider.System);

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
// And a THIRD gate, at compile time (FU-089): a build without COFFER_DEV_AUTH — every
// Release build, so every image — has no DevAuthHandler at all, and this is false
// whatever the environment and configuration say.
#if COFFER_DEV_AUTH
var devAuthEnabled = builder.Environment.IsDevelopment() && apiOptions.DevAuth;
#else
const bool devAuthEnabled = false;
if (apiOptions.DevAuth)
    Console.Error.WriteLine("Api:DevAuth is set, but this build has no dev auth in it (FU-089); ignoring it.");
#endif
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
#if COFFER_DEV_AUTH
if (devAuthEnabled)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(
        AuthSchemes.DevAuth, _ => { });
}
#endif
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
    // An endpoint that says nothing about authorization requires a signed-in user
    // (FU-095). It used to be open: forgetting RequireAuthorization() on a new route
    // published it. Anonymous is now something an endpoint declares, and
    // EndpointAuthorizationCatalogTests holds every route to declaring one or the other.
    options.FallbackPolicy = options.DefaultPolicy;

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
        // coffer.read is required here, not only advertised (FU-095): IgnoreScopePermissions
        // lets a client ask for any scope, so a grant of none of Coffer's would otherwise
        // read every ledger the user can see.
        options.AddPolicy(AuthPolicies.RequireMcp, policy => policy
            .AddAuthenticationSchemes(mcpSchemes)
            .RequireAuthenticatedUser()
            .AddRequirements(new Coffer.Api.Mcp.McpScopeRequirement(Coffer.Api.Mcp.McpScopes.Read)));
    }
});
// A token missing that scope is answered 403 insufficient_scope, not 401 (FU-095).
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler,
    Coffer.Api.Mcp.McpInsufficientScopeResultHandler>();

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
    var store = new Coffer.Api.Backup.BackupStore(
        dir, sp.GetRequiredService<ILogger<Coffer.Api.Backup.BackupStore>>(),
        backup.PartSizeBytes, sp.GetRequiredService<TimeProvider>());
    // Runs once, as the singleton is built and before any create can start: a write
    // killed mid-way leaves a .partial that nothing else would ever remove.
    store.DeletePartials();
    return store;
});
// One backup at a time, and manual ones run off the request that asked (FU-065/067). A
// singleton: the gate and the recent-run table are process state.
builder.Services.AddSingleton<Coffer.Api.Backup.BackupRunCoordinator>();
builder.Services.AddScoped<Coffer.Api.Backup.BackupDestinationPusher>();
builder.Services.AddScoped<Coffer.Api.Backup.BackupManager>();
builder.Services.AddScoped<Coffer.Api.Backup.IBackupRunner>(
    sp => sp.GetRequiredService<Coffer.Api.Backup.BackupManager>());
// "Never delete" pins (ADR-0062 ④b+c) — excluded from retention.
builder.Services.AddScoped<Coffer.Api.Db.Repositories.CsvMappingsRepository>();
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
builder.Services.AddScoped<Coffer.Api.Auth.Webauthn.StepUpGuard>();
builder.Services.AddScoped<Coffer.Api.Auth.UserAccessRevoker>();
builder.Services.AddSingleton<Coffer.Api.Auth.OAuthConsentTickets>();

// Master KEK (ADR-0026, sourced per ADR-0092 D1): resolved once at startup from
// the key file at Api:MasterKey:Path — the only source. The
// COFFER_MASTER_KEK_BASE64 env var and its one-release migration window (D6) are
// gone; nothing needs to feed a key in, because a virgin install mints its own
// below (D3). Fail-fast on missing /
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
// FU-084: the API runs unprivileged, and a volume a root-run version wrote is not its own
// until install.sh hands it over. Stop here saying so, not later on an access-denied.
VolumeAccess.EnsureOwned(Path.Combine(AppContext.BaseDirectory, "data"), masterKeyStore.Path);
// FU-077: a key left at the old default (data/master.key, on the volume that holds every
// backup) moves to the configured location — the compose file's own coffer_keys volume —
// before it is resolved below. Throws, stopping the boot with what to do, when the move
// would be unsafe.
foreach (var note in MasterKeyRelocation.Run(
             masterKeyStore.Path, MasterKeyStore.DefaultPath, MasterKeyRelocation.IsOwnVolume,
             (BootstrapRestoreStaging.ReadAdoptedKeyArchive, BootstrapRestoreStaging.RecordAdoptedKeyArchive)).Notes)
    Console.WriteLine(note);
MasterKeyLoader.KeySource masterKeySource;
var masterKeyWasMinted = false;
{
    var resolution = MasterKeyLoader.Resolve(masterKeyStore);
    var resolvedKey = resolution.Key;
    masterKeySource = resolution.Source;

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
        // D4 rotation produces. Without it the id silently falls back
        // to the id fallback, which is how `--adopt-new-kek` ended up labelling a
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

        // A deliberate abandon EXITS instead of going on to serve, because of how the
        // flag has to be delivered: the container is refusing to boot, so `exec` has
        // nothing to attach to and the flag arrives as a temporary compose `command:`
        // override. If the process then served normally, that override would be left in
        // place — and the next boot that found no key would mint again silently, which
        // is the one thing this gate exists to prevent. Exiting makes it a one-shot the
        // operator must undo to get a running install.
        //
        // A virgin install minting its own first key is NOT this case: nothing was
        // abandoned, there is no flag to remove, and it goes on to serve the setup
        // ceremony that shows the key (D3).
        if (adoptNew && hasWrapped)
        {
            Console.Error.WriteLine(
                $"Minted a new master KEK (id '{mintedId}') at '{masterKeyStore.Path}' and "
                + "ABANDONED the previously wrapped secrets: the SimpleFIN feed tokens, the "
                + "stored backup passphrase, and the Google Drive connection. Ledger data and "
                + "passkeys are unaffected. Re-establish those three in the UI after starting."
                + Environment.NewLine
                + "Now REMOVE --adopt-new-kek before starting normally — leaving it in place "
                + "means a future boot with no key would mint again instead of refusing.");
            return 0;
        }
    }

    builder.Services.AddSingleton(resolvedKey);
}
// One per process: the rotation gate every sealing path takes a lease from (FU-078).
builder.Services.AddSingleton<Coffer.Api.Crypto.MasterKeyWriteGate>();
builder.Services.AddScoped<LedgerKeyService>();
// Master-KEK rotation (ADR-0026 §rotation), driven from the admin UI (ADR-0092 D4).
// The `rotate-kek` CLI it replaced is gone: rotation is routine hygiene rather than
// disaster recovery, so an operator who can't sign in to reach the UI needs recovery
// codes, not a rotation. (`restore` is no longer a CLI command either — see the note
// where the subcommands are dispatched, and ADR-0094.)
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
// The policy every client that fetches a user-entered URL is built on (FU-082): public
// HTTPS addresses only, unless an operator allows a network; checked where it connects.
builder.Services.AddSingleton<Coffer.Api.Http.OutboundPolicy>();
builder.Services.AddHttpClient<SimpleFinClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Coffer/1.0 (+simplefin-bridge)");
})
    // The setup token decodes to a URL the user chose, and the access URL is whatever
    // answered it.
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Coffer.Api.Http.OutboundPolicy>().CreateHandler());

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
// ADR-0031 Phase 5. One provider for every institution describable by a mapping
// document; the mapping is resolved per request by CsvIngestEndpoints, so the
// provider itself stays a pure stream-to-result function like its siblings.
builder.Services.AddSingleton<Coffer.Api.Ingest.IFileProvider, Coffer.Api.Ingest.Csv.CsvGenericFileProvider>();
// ADR-0031 Phase 6. One provider per BROKERAGE, and each is a shim: it converts
// the institution's CSV to QIF and delegates, so the investment action vocabulary
// lives once in QifFileProvider rather than once per brokerage.
builder.Services.AddSingleton<Coffer.Api.Ingest.IFileProvider, Coffer.Api.Ingest.Csv.FidelityActivityFileProvider>();
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
builder.Services.AddScoped<BudgetProgressRepository>();
builder.Services.AddScoped<BudgetTargetsRepository>();
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
    // Signing/encryption keys persist in the database, sealed under the master key
    // (FU-069), so tokens survive restarts and restores; they are loaded at boot and
    // handed over by OpenIddictKeysFromDatabase. The authorize/token endpoints are
    // handled by our minimal-API passthrough (slice 3); discovery + DCR are wired in
    // later slices.
    builder.Services.AddOpenIddict()
        .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
        .AddServer(o =>
        {
            // Revocation (RFC 7009, FU-087): a client ends its own tokens. Handled by
            // OpenIddict; discovery advertises it.
            o.SetAuthorizationEndpointUris("oauth/authorize")
             .SetTokenEndpointUris("oauth/token")
             .SetRevocationEndpointUris("oauth/revoke");
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

            // No form_post (2026-10-12 audit, H1). It answers consent with a page that
            // auto-submits a form to the redirect_uri, from Coffer's own origin — with a
            // javascript: redirect_uri that ran attacker script as the user. Redirect URIs
            // are now https or loopback http, but MCP clients use the query mode anyway,
            // so the mode that turns a bad redirect into script execution goes too.
            o.Configure(options => options.ResponseModes.Remove(
                OpenIddict.Abstractions.OpenIddictConstants.ResponseModes.FormPost));

            o.UseReferenceAccessTokens();
            o.SetAccessTokenLifetime(TimeSpan.FromHours(1));
            o.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
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
    builder.Services.AddSingleton<
        Microsoft.Extensions.Options.IConfigureOptions<OpenIddict.Server.OpenIddictServerOptions>,
        OpenIddictKeysFromDatabase>();
}
// Always registered, MCP on or off: every server boot keeps the OAuth keys in the
// database (FU-069), so a backup carries them whatever the switch said that day.
builder.Services.AddSingleton<OpenIddictKeyMaterial>();
builder.Services.AddScoped<UserPreferencesRepository>();
builder.Services.AddScoped<SchedulesRepository>();
builder.Services.AddScoped<GlobalSchedulesRepository>();
// Always registered (not gated by mcpEnabled): the admin System-settings
// endpoint must be reachable to TURN MCP ON when it is currently off (D8).
builder.Services.AddScoped<SystemSettingsRepository>();
builder.Services.AddScoped<MetaRepository>();
builder.Services.AddScoped<AccountGroupsRepository>();
builder.Services.AddScoped<SavedReportsRepository>();
builder.Services.AddScoped<ReportPinsRepository>();
builder.Services.AddScoped<FeedConnectionsRepository>();
builder.Services.AddScoped<LedgerOperationsRepository>();
builder.Services.AddScoped<RegisterRepository>();

// ---------------------------------------------------------------------------
// Notifications (ADR-0096). One publish path, pluggable delivery.
//
// Subscribers are PROVIDERS on the same shape as the quote providers: a stable
// key, a display name, and a declared capability. The capability is not
// decoration — only a Heartbeat provider can report what did NOT happen, because
// noticing silence needs something outside the deployment to be counting. A
// message-only webhook says nothing when the container is dead, which is the
// failure that once went unnoticed for days.
// ---------------------------------------------------------------------------
// Both deliver to a URL a user entered, so both connect through the outbound policy
// (FU-082).
builder.Services.AddHttpClient<Coffer.Api.Notifications.INotificationSubscriber,
    Coffer.Api.Notifications.Providers.HealthchecksSubscriber>(c =>
    {
        // Short: a heartbeat ping that hangs must not hold up the job reporting it.
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Coffer.Api.Http.OutboundPolicy>().CreateHandler());
builder.Services.AddHttpClient<Coffer.Api.Notifications.INotificationSubscriber,
    Coffer.Api.Notifications.Providers.WebhookSubscriber>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Coffer.Api.Http.OutboundPolicy>().CreateHandler());
builder.Services.AddScoped<Coffer.Api.Notifications.NotificationPublisher>();
builder.Services.AddScoped<Coffer.Api.Notifications.BackupAgeMonitor>();
builder.Services.AddScoped<Coffer.Api.Notifications.DriveSyncMonitor>();
builder.Services.AddScoped<Coffer.Api.Notifications.ConsistencyMonitor>();
builder.Services.AddScoped<LedgerConsistencyRepository>();
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
// Bank feed sync on a schedule (mig 215). IngestOrchestrator is already scoped, and the
// handler is resolved per tick inside the worker's own scope.
builder.Services.AddScoped<Coffer.Api.Scheduling.IScheduledJobHandler,
    Coffer.Api.Ingest.FeedSyncJobHandler>();
// Reminder auto-post (mig 219, ADR-0097). The only per-ledger job that WRITES financial
// transactions; it builds its own service-role contexts rather than writing on the one
// the runner hands it (see the handler's remarks for why that is not a style choice).
builder.Services.AddScoped<Coffer.Api.Scheduling.IScheduledJobHandler,
    Coffer.Api.Reminders.ReminderAutoPostJobHandler>();
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
// One parsed Moneydance export in memory at a time, across every user (FU-092).
builder.Services.AddSingleton<Coffer.Api.Import.ImportMemoryGate>();

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
    // RPID / RPName, not ServerDomain / ServerName — Fido2.AspNet 4.1.0
    // obsoleted the older pair in favour of the spec's own vocabulary, and this
    // repo compiles warnings as errors, so the bump does not build without the
    // rename. Same values, same meaning: the relying-party id is the registrable
    // domain an assertion is scoped to, and getting it wrong invalidates every
    // existing passkey rather than failing loudly.
    fido2Options.RPID = fido2.RpId;
    fido2Options.RPName = fido2.RpName;
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
// Recovery-code attempts per account (FU-088), beside the per-address window below.
builder.Services.AddSingleton<Coffer.Api.Auth.RecoveryAttemptLimiter>();
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

// -- multipart upload ceiling (ADR-0094). ASP.NET defaults
// FormOptions.MultipartBodyLengthLimit to 128 MB, which used to be the reason a
// large restore needed the `restore` CLI. That subcommand is gone, so the UI is the
// only restore path and must not be capped below what a real backup weighs.
//
// Raised rather than removed: the two restore endpoints (admin restore, bootstrap
// restore) lift Kestrel's per-request MaxRequestBodySize to null, so this limit is their
// only backstop against a stream that never ends. 4 GiB is far above any
// personal-finance dataset and still finite. The Moneydance import sets its own,
// smaller cap (Api:Import:MaxUploadMb, FU-092), since anyone signed in can upload one
// and its parse is held in memory.
//
// This does NOT widen the small uploads: file ingest caps itself at 5 MB with
// RequestSizeLimitAttribute, which is a body-size limit and bites first regardless of
// what the form limit says.
//
// Limits imposed by a reverse proxy in front of Coffer (nginx defaults
// client_max_body_size to 1 MB) are the operator's to raise — the same position the
// Moneydance import already takes, and the reason a localhost install with no proxy is
// the fallback for an awkwardly large restore.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = 4L * 1024 * 1024 * 1024);

var app = builder.Build();
var clock = app.Services.GetRequiredService<TimeProvider>();

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

// -- a deployment that set its OWN session timezone and just had it replaced.
// Only ever logged when the two disagree: silently discarding a value someone
// set on purpose is how a deliberate configuration becomes a mystery.
foreach (var outcome in dbTimeZoneOutcomes.Where(o => o.OverriddenZone is not null))
{
    app.Logger.LogWarning(
        "{Key} asked for the Postgres session timezone {Configured}; it has been overridden to {Pinned}. "
        + "Reporting groups months and days by extracting date parts from timestamptz values, so the "
        + "session zone decides which period a transaction falls in, and the app requires the pinned value.",
        outcome.Key, outcome.OverriddenZone, DbSessionTimeZone.Zone);
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

// -- bootstrap restore (ADR-0061): if a backup was staged (setup screen or admin
// panel), apply it BEFORE migrating/serving. RestoreAsync builds the backup as a
// NEW database, migrates it, and swaps it in with two renames in one
// transaction, keeping the replaced database as the way back; on any failure the
// new database is dropped and the live one was never touched. The outcome is
// recorded either way and read back by the browser that asked — "the server
// answers again" says nothing about whether a restore happened, and treating it
// as success is how a failed restore once reported "Restore complete". Any
// failure clears the request (no boot loop). Runs before the HTTP host starts,
// never concurrent with serving. Skipped under the same `Migrations:Skip` flag
// the migration/CLI blocks use: test fixtures share one database, so a staging
// marker leaked by a test must never trigger a restore at the next
// WebApplicationFactory boot.
// Only the server's own boot applies a staged restore. A CLI subcommand
// (`docker compose exec api dotnet coffer-api.dll backup …`) runs this same program,
// and letting it apply a pending restore meant two processes building and swapping
// at once — one dropping the other's half-built database, then swapping under a
// server that had already reported the restore failed. Flags such as
// --adopt-new-kek are not subcommands.
var isServerBoot = args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal);

// Leftovers of a restore or verification that was killed: the decrypted dump of the whole
// database and unfinished upload buffers (FU-075). A server boot only — a CLI command
// exec'd into a running container shares its temp, and the server may be mid-restore.
if (isServerBoot
    && app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.Backup.SweepTempAtBoot)
{
    var swept = BackupService.SweepLeftoversAtBoot();
    if (swept > 0)
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.BootstrapRestore")
            .LogWarning("Removed {Count} leftover restore or upload file(s) from temp.", swept);
}

// A staged source key older than the archive beside it belongs to an earlier attempt
// (FU-073); adopting it would replace the live key for a restore that never supplied one.
if (isServerBoot && BootstrapRestoreStaging.SourceKeyIsOlderThanArchive())
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.BootstrapRestore")
        .LogWarning("A staged source master key is older than the backup staged with it; not adopting it.");
    BootstrapRestoreStaging.ClearSourceKey();
}

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
    && isServerBoot
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
        archived = masterKeyStore.Archive($"{clock.GetUtcNow().UtcDateTime:yyyyMMddTHHmmssZ}");
        // NOT the source install's id — we don't have it. The archive carries the key
        // only, and the operator pastes just that. So the local id is kept, which means
        // the restored rows' lek_kek_id values (the source's) and this file's id can
        // disagree. Harmless: the id is advisory, only rotation reads it, and rotation
        // re-wraps every row regardless of what it says.
        masterKeyStore.Write(sourceKey, MasterKeyLoader.ResolveId());
        adopted = true;
        // So the restore boot can put it back if the restore then fails.
        if (archived is not null) BootstrapRestoreStaging.RecordAdoptedKeyArchive(archived);
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
    && isServerBoot
    && BootstrapRestoreStaging.IsPending())
{
    using var scope = app.Services.CreateScope();
    var restoreLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.BootstrapRestore");
    var restoreSvc = scope.ServiceProvider.GetRequiredService<BackupService>();
    var attemptId = BootstrapRestoreStaging.ReadAttemptId() ?? Guid.Empty;
    var adoptedKeyArchive = BootstrapRestoreStaging.ReadAdoptedKeyArchive();
    var keyPutBack = false;
    BootstrapRestoreStaging.RestoreOutcome outcome;
    try
    {
        // A boot that swapped this restore in and died before clearing the staging
        // left its stamp on the live database. Running the restore again would
        // rename the restored database away and prune the real way back.
        var readiness = await restoreSvc.CheckRestoreReadinessAsync();
        BackupService.RestoreResult result;
        if (readiness.Marker is { } done && done.AttemptId == attemptId && attemptId != Guid.Empty)
        {
            restoreLogger.LogWarning(
                "Bootstrap restore was already applied by an earlier boot that did not finish; not running it again.");
            result = new BackupService.RestoreResult(done.PreviousDatabase);
        }
        else
        {
            restoreLogger.LogWarning("Bootstrap restore pending — building the staged backup as a new database…");
            await using var staged = File.OpenRead(BootstrapRestoreStaging.ArchivePath);
            result = await restoreSvc.RestoreAsync(
                staged,
                BootstrapRestoreStaging.ReadPassphrase(),
                attemptId,
                MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory));
        }

        // Reconciliation (ADR-0092 D5) does NOT run here — it runs after migrations,
        // below, with the rest of the boot.
        restoreApplied = true;
        outcome = new(attemptId, "applied", clock.GetUtcNow().UtcDateTime, result.PreviousDatabase, Error: null);
        restoreLogger.LogWarning(
            "Bootstrap restore complete. The database it replaced is kept as {Previous}.",
            result.PreviousDatabase);
    }
    catch (RestoreOutcomeUnknownException ex)
    {
        outcome = new(attemptId, "unknown", clock.GetUtcNow().UtcDateTime, PreviousDatabase: null,
            BackupService.SummarizeForOutcome(ex.Message));
        // The summary, the type and the stack — not the exception's text, which can quote
        // rows of the backup (FU-083).
        restoreLogger.LogCritical(
            "Bootstrap restore: the swap's result could not be established: {Error} ({ExceptionType}){NewLine}{StackTrace}",
            outcome.Error, ex.GetType().FullName, Environment.NewLine, ex.StackTrace);
    }
    catch (Exception ex)
    {
        // Every other failure lands here, whatever its type, and happened before
        // the swap: the live database was not touched. If this restore adopted the
        // source install's key, the original goes back too — otherwise the database
        // is unchanged but nothing in it opens under the key now in place.
        string error = BackupService.SummarizeForOutcome(ex.Message);
        if (adoptedKeyArchive is not null)
        {
            try
            {
                masterKeyStore.RestoreFromArchive(adoptedKeyArchive);
                keyPutBack = true;
                error += " The master key adopted for this restore was put back.";
            }
            catch (Exception keyEx) when (keyEx is IOException or UnauthorizedAccessException)
            {
                error += $" The master key adopted for this restore could NOT be put back: restore {adoptedKeyArchive} to {masterKeyStore.Path} by hand.";
                restoreLogger.LogCritical(keyEx,
                    "Could not put the pre-adoption master key back from {Archive}.", adoptedKeyArchive);
            }
        }
        outcome = new(attemptId, "failed", clock.GetUtcNow().UtcDateTime, PreviousDatabase: null, error);
        // pg_restore's stderr quotes rows of the backup on a failed COPY ("Failing row
        // contains", CONTEXT ... line N); logged whole, a failed restore put the data in the
        // container log, which outlives the restore (FU-083). The same summary the outcome
        // carries, plus the type and stack for the diagnosis.
        restoreLogger.LogError(
            "Bootstrap restore FAILED; the database was not changed. Clearing the request (no retry loop). "
            + "{Error} ({ExceptionType}){NewLine}{StackTrace}",
            error, ex.GetType().FullName, Environment.NewLine, ex.StackTrace);
    }
    finally
    {
        BootstrapRestoreStaging.Clear();
    }

    try
    {
        BootstrapRestoreStaging.RecordOutcome(outcome);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        restoreLogger.LogError(ex,
            "Could not record the restore outcome ({Outcome}); the browser that asked will report it as unconfirmed.",
            outcome.Outcome);
    }

    if (keyPutBack)
    {
        // This process holds the adopted key in memory; the original is back on
        // disk. Restart onto it rather than serve under the wrong one.
        restoreLogger.LogWarning("Restarting to load the original master key.");
        return 0;
    }

    if (outcome.Outcome == "applied" && outcome.PreviousDatabase is { } keep)
    {
        // Only now, with the outcome recorded and the staging cleared: a crash
        // before this point must never cost the way back.
        try
        {
            await restoreSvc.PruneAfterRestoreAsync(keep);
        }
        catch (Exception ex) when (ex is BackupException or IOException)
        {
            restoreLogger.LogWarning(ex, "Restore applied, but older pre-restore databases could not be removed.");
        }
    }
}

// -- migrations: applied at startup with DbUp using the service-role
// connection (coffer_service has BYPASSRLS and DDL privilege; coffer_app
// can't run ENABLE ROW LEVEL SECURITY). Skipped when the
// "Migrations:Skip" config key is true so test fixtures that already
// applied the SQL by hand can opt out (saves a round-trip per fixture
// setup). No longer skipped for a `restore` subcommand — that CLI path is
// gone (ADR-0094). A restore no longer runs into this: it migrates the database
// it builds BEFORE swapping it in, so here it is already current.
if (!app.Configuration.GetValue<bool>("Migrations:Skip"))
{
    using var scope = app.Services.CreateScope();
    var apiOpts = scope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
    var migrationLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.Migrations");
    var migrationsDirectory = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
    MigrationRunner.Run(apiOpts.ServiceConnectionString, migrationsDirectory, migrationLogger);
}

// -- LEK backfill. AFTER migrations, for the same reason reconciliation is.
//
// Migration 035 shipped `ledgers.wrapped_lek` nullable and promised "a subsequent
// migration sets NOT NULL once backfill is verified complete". No such migration could
// exist: wrapping a LEK needs the master KEK, which SQL does not have. So the backfill
// runs here, in the process that holds the key, and the constraint can only follow in a
// LATER release — migrations run before this line on the same boot.
//
// The NULLs are not API-created ledgers (CreateWithOwnerAsync has wrapped one in the
// ledger's own INSERT since ADR-0026); they come from the Moneydance importer, a
// separate binary with no KEK that inserts `ledgers (id, name)` directly.
//
// Does not abort the boot, matching the reconciliation posture below: a ledger without
// a LEK is exactly as usable as it was a moment ago — nothing can seal a secret for it
// until it has one, which is the lazy path's job — so crash-looping would turn a
// deferred repair into an outage.
{
    using var scope = app.Services.CreateScope();
    var lekLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.LedgerKeys");
    try
    {
        var wrapped = await scope.ServiceProvider
            .GetRequiredService<Coffer.Api.Db.Repositories.LedgersRepository>()
            .BackfillMissingWrappedLeksAsync(
                scope.ServiceProvider.GetRequiredService<Coffer.Api.Crypto.LedgerKeyService>());
        if (wrapped > 0)
            lekLogger.LogInformation(
                "Wrapped a ledger encryption key for {Count} ledger(s) that had none.", wrapped);
    }
    catch (Exception ex)
    {
        lekLogger.LogError(ex,
            "LEK backfill FAILED. Ledgers without a wrapped_lek keep working until something "
            + "tries to seal a secret for them, which then falls back to the lazy per-ledger "
            + "path. Check the master key if this repeats.");
    }
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
            "Setup already complete — a user exists; no bootstrap token issued.");
        return 1;
    }
    Console.WriteLine(url);
    return 0;
}

// Replace the OAuth server's signing and encryption keys (FU-069): the remedy for a
// key compromise, which used to be "delete data/openiddict and restart". The keys are
// in the database now, so this replaces them there; the running server keeps the old
// pair in memory until it restarts. This process has its own rotation gate, so it cannot
// see a rotation the server is running: a pair it seals mid-rotation is stranded under
// the old key, and the next boot says so and points back here.
//   docker compose exec api dotnet coffer-api.dll reset-oauth-keys
if (args is ["reset-oauth-keys", ..])
{
    using var cliScope = app.Services.CreateScope();
    await cliScope.ServiceProvider.GetRequiredService<OpenIddictKeyMaterial>().ResetAsync(
        cliScope.ServiceProvider.GetRequiredService<Coffer.Api.Crypto.LedgerKeyService>());
    Console.WriteLine(
        "New OAuth server keys stored. Restart the server to use them (docker compose restart "
        + "api). Every MCP client must then re-authorize; web sign-in is unaffected.");
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
        // The same guarantees as a stored backup: written to a .partial name, read back
        // (decrypted in full, every live table present), and only then given its name.
        var partialPath = outPath + ".partial";
        try
        {
            await using (var outStream = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await svc.CreateAsync(passphrase, outStream);
                await outStream.FlushAsync();
                outStream.Flush(flushToDisk: true);
            }
            await using (var back = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await svc.VerifyAsync(passphrase, back);
            File.Move(partialPath, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partialPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
    catch (Exception ex) when (ex is BackupException or BackupDecryptException)
    {
        await Console.Error.WriteLineAsync($"Backup failed: {ex.Message}");
        return 1;
    }
    Console.WriteLine($"Backup written to {outPath}");
    return 0;
}

// (The `restore` subcommand is gone. It duplicated the bootstrap and admin restore
// UIs (ADR-0061 / ADR-0071 D3) and every reason it existed for turned out not to
// hold: "works on a schema too broken to serve" is answered by reinstalling, since
// a restore builds a fresh database and never reads the old schema; "skips migrations" was a
// collision-avoidance detail of this code path, not a capability the UI lacked; the
// ~128 MB ceiling was ASP.NET's default MultipartBodyLengthLimit, now raised; and a
// headless install cannot exist, because setup enrols a passkey in a browser.
// Upload limits imposed by a reverse proxy are the operator's to configure — the
// same position the Moneydance import upload already takes. See ADR-0094.)

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
        + "Available: bootstrap-token, backup, reset-oauth-keys.\n"
        + "`rotate-kek` was removed in ADR-0092 — rotate the master key from the admin UI "
        + "(System → Encryption).\n"
        + "`restore` was removed in ADR-0094 — restore from the bootstrap screen on a fresh "
        + "install, or System → Backups → Restore on a running one.\n"
        + "Run with no arguments to start the server.");
    return 2;
}

// -- OAuth server keys (FU-069). AFTER migrations, which create their table, and after
// post-restore reconciliation, which replaces a restored pair this install cannot open;
// before the host starts, so nothing reads them first. Every CLI subcommand has returned
// above, so this is always the server's own boot.
//
// Not gated on MCP being on: the keys go into the database on every boot, so the files
// earlier releases wrote are moved in and deleted even while MCP is off. And not fatal,
// matching the blocks above: without keys only OAuth sign-in fails, and the next boot
// retries.
{
    using var scope = app.Services.CreateScope();
    var oidcLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.OAuthKeys");
    var mcpOpts = scope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.Mcp;
    var legacyKeyDirectory = string.IsNullOrEmpty(mcpOpts.LegacyKeyDirectory)
        ? Path.Combine(app.Environment.ContentRootPath, "data", "openiddict")
        : mcpOpts.LegacyKeyDirectory;
    try
    {
        await scope.ServiceProvider.GetRequiredService<OpenIddictKeyMaterial>().LoadAsync(
            scope.ServiceProvider.GetRequiredService<Coffer.Api.Crypto.LedgerKeyService>(),
            legacyKeyDirectory);
    }
    catch (Exception ex)
    {
        oidcLogger.LogError(ex,
            "Loading the OAuth server keys FAILED, so MCP sign-in is unavailable until a boot "
            + "succeeds. Key files under {Directory} were left in place.", legacyKeyDirectory);
    }
}

// Clients registered before the revocation endpoint existed hold no permission for it,
// and OpenIddict refuses an endpoint a client has no permission for (FU-087). Idempotent;
// not fatal, like the block above: only revocation is affected until a boot succeeds.
if (mcpEnabled)
{
    using var scope = app.Services.CreateScope();
    var oauthLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.OAuthClients");
    try
    {
        var granted = await OAuthGrants.GrantRevocationToEveryClientAsync(
            scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>(),
            CancellationToken.None);
        if (granted > 0)
            oauthLogger.LogInformation("Gave {Count} registered OAuth client(s) the revocation endpoint.", granted);
    }
    catch (Exception ex)
    {
        oauthLogger.LogError(ex, "Giving registered OAuth clients the revocation endpoint failed; the next boot retries.");
    }
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
// :8080 direct).
//
// And X-Forwarded-For, so the client address — what every rate limit partitions on —
// is the client's and not the proxy's (FU-088). All three are believed only from the
// trusted proxies (Api:TrustedProxies; loopback and the private ranges by default):
// they used to be believed from any peer, which on a published port let anyone set
// the scheme and host the app builds its URLs from, and would have let anyone pick
// their own rate-limit bucket.
var forwardedHeaderOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
                       | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
forwardedHeaderOptions.KnownIPNetworks.Clear();
forwardedHeaderOptions.KnownProxies.Clear();
foreach (var network in Coffer.Api.Configuration.TrustedProxies.Parse(
             app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.TrustedProxies))
    forwardedHeaderOptions.KnownIPNetworks.Add(network);
app.UseForwardedHeaders(forwardedHeaderOptions);
// Browser security headers on every response (FU-091). After forwarded headers, so HSTS
// goes on what the browser reached over HTTPS; before static files, so the SPA's assets
// carry them too.
app.UseMiddleware<Coffer.Api.Auth.SecurityHeadersMiddleware>();

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
// Cross-site request forgery: a cookie-carrying unsafe request must come from our own
// origin (2026-10-12 audit, M1). After forwarded headers, so "our origin" is the one
// the browser sees; after the access log, so a refusal is logged; before the
// endpoints, so nothing forged reaches one.
app.UseMiddleware<Coffer.Api.Auth.SameOriginRequestMiddleware>();
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

// OpenAPI document at /openapi/v1.json (the .NET 10 default), in Development only
// (FU-095): it was served to anyone in production, a map of every route for whoever
// asked. Nothing in the app reads it; it is for local tooling.
if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

// Deployment-scope notifications (ADR-0096). Unconditional: nothing here depends
// on the MCP server being enabled, and a backup heartbeat must publish regardless.
app.MapAdminNotificationsEndpoints();
app.MapLedgerNotificationsEndpoints();
app.MapHealthEndpoints();
app.MapRestoreOutcomeEndpoints();
app.MapMetaEndpoints();
app.MapAuthEndpoints();
app.MapSetupEndpoints();
app.MapLoginEndpoints();
app.MapAccountEndpoints();
app.MapStepUpEndpoints();
app.MapLedgersEndpoints();
app.MapMembersEndpoints();
app.MapAdminUsersEndpoints();
app.MapInvitesEndpoints();
app.MapAccountsEndpoints();
app.MapCategoriesEndpoints();
app.MapTagsEndpoints();
app.MapAccountGroupsEndpoints();
app.MapReportsEndpoints();
app.MapSavedReportsEndpoints();
app.MapFeedConnectionsEndpoints();
app.MapSyncRunsEndpoints();
app.MapLedgerOperationsEndpoints();
app.MapOverviewEndpoints();
app.MapBudgetEndpoints();
app.MapPreferencesEndpoints();
app.MapTransactionsEndpoints();
app.MapBalancesEndpoints();
app.MapOfxIngestEndpoints();
app.MapQifIngestEndpoints();
app.MapFidelityIngestEndpoints();
app.MapCsvIngestEndpoints();
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
    app.MapOAuthGrantsEndpoints();
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
}).AllowAnonymous(); // the shell is what a signed-out browser loads to reach the login page

app.Run();
return 0;

/// <summary>
/// Non-empty <c>Program</c> partial so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in <c>tests/Api.Tests</c> can find an entry-point type to bootstrap. The
/// top-level statements above generate the actual <c>Main</c>; this declaration
/// just exposes the synthesised class as <c>public</c> for cross-assembly use.
/// </summary>
public partial class Program;
