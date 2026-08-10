namespace Coffer.Api.Configuration;

/// <summary>
/// Strongly-typed bind target for the <c>Api</c> configuration section. Loaded
/// once at startup and registered in DI as a singleton via
/// <c>IOptions&lt;ApiOptions&gt;</c>.
/// </summary>
/// <remarks>
/// PR 3.8 wires the ADR-0020 Phase D role split: the API runs as the
/// non-BYPASSRLS <c>coffer_app</c> role at request-handling time
/// (<see cref="ConnectionString"/>) and escalates to <c>coffer_service</c>
/// (<see cref="ServiceConnectionString"/>) for pre-auth lookups, the
/// migration runner, and a handful of session/credential write paths
/// that span the authentication boundary. Both are required from PR
/// 3.8 onward — no fallback.
/// </remarks>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// <c>coffer_app</c> connection string. Subject to row-level security:
    /// every authenticated request sets <c>app.user_id</c> via the
    /// connection interceptor so RLS policies on every ledger-scoped table
    /// filter rows to the current user's grants. Used by the runtime
    /// <c>AppDbContext</c>.
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// <c>coffer_service</c> (BYPASSRLS) connection string. Used by the
    /// migration runner (needs DDL + <c>ENABLE ROW LEVEL SECURITY</c>
    /// privilege), the WebAuthn pre-auth lookups (cookie validation,
    /// username resolution), the session/credential write paths, and
    /// <c>POST /api/ledgers</c> (which has to insert a ledger + grant
    /// pair across an RLS boundary). The importer + future sync worker
    /// also connect as this role. Required from PR 3.8 onward.
    /// </summary>
    public string ServiceConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Path to a file containing ONLY the <c>coffer_app</c> password. When set,
    /// <see cref="DbPasswordResolver"/> injects it into
    /// <see cref="ConnectionString"/> before this type is bound, and any
    /// <c>Password=</c> already in that string is ignored.
    /// </summary>
    /// <remarks>
    /// Exists so the credential isn't an environment variable — see
    /// <see cref="DbPasswordResolver"/> for why that matters. Optional: leave
    /// unset to keep the password in the connection string (the arrangement
    /// before this landed, and the simpler one for a bare-metal install).
    /// </remarks>
    public string AppPasswordFile { get; init; } = string.Empty;

    /// <summary>
    /// Path to a file containing ONLY the <c>coffer_service</c> password. As
    /// <see cref="AppPasswordFile"/>, but for
    /// <see cref="ServiceConnectionString"/>.
    /// </summary>
    public string ServicePasswordFile { get; init; } = string.Empty;

    /// <summary>
    /// When set, the dev-auth handler is registered and treats every request
    /// as the bootstrap system user. The handler additionally requires
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c>; both gates must hold per
    /// ADR-0013, so a production build with <c>DevAuth=true</c> still rejects
    /// unauthenticated requests.
    /// </summary>
    public bool DevAuth { get; init; }

    /// <summary>
    /// WebAuthn / FIDO2 relying-party configuration. Bound from
    /// <c>Api:Fido2</c>.
    /// </summary>
    public Fido2Options Fido2 { get; init; } = new();

    /// <summary>
    /// Bootstrap-token configuration. Bound from <c>Api:Bootstrap</c>.
    /// </summary>
    public BootstrapOptions Bootstrap { get; init; } = new();

    /// <summary>
    /// Cookie session configuration. Bound from <c>Api:Cookie</c>.
    /// </summary>
    public CookieSessionOptions Cookie { get; init; } = new();

    /// <summary>
    /// Whole-DB backup configuration (ADR-0060). Bound from <c>Api:Backup</c>.
    /// </summary>
    public BackupOptions Backup { get; init; } = new();

    /// <summary>
    /// Authentication tunables (ADR-0013). Bound from <c>Api:Auth</c>.
    /// </summary>
    public AuthOptions Auth { get; init; } = new();

    /// <summary>
    /// MCP server configuration (ADR-0063). Bound from <c>Api:Mcp</c>.
    /// </summary>
    public McpOptions Mcp { get; init; } = new();

    /// <summary>
    /// Days to retain the audit logs — the MCP write audit (ADR-0081 D3,
    /// <c>mcp_tool_invocations</c>) and the ledger-operation log (ADR-0055,
    /// <c>ledger_operations</c>) — before <c>AuditRetentionService</c> prunes older rows.
    /// Default 180. 0 or less disables pruning (retain indefinitely). Bound from
    /// <c>Api:AuditRetentionDays</c>.
    /// </summary>
    public int AuditRetentionDays { get; init; } = 180;

    /// <summary>
    /// Master-KEK file location (ADR-0092 D1). Bound from <c>Api:MasterKey</c>.
    /// </summary>
    public MasterKeyFileOptions MasterKey { get; init; } = new();
}

/// <summary>
/// Where the master KEK file lives (ADR-0092 D1). Only the <i>path</i> is
/// configuration — the key itself never enters the options tree, because that
/// tree lands in log dumps and <c>appsettings.json</c> commits (see
/// <c>MasterKey</c> for the same reasoning applied to the in-memory holder).
/// </summary>
public sealed class MasterKeyFileOptions
{
    /// <summary>
    /// Path to the base64 key file. Null → <c>data/master.key</c> beside the
    /// binary, on the <c>coffer_data</c> volume (ADR-0059). Point it at an
    /// injected secret to keep the key off the app's own volume —
    /// <c>/run/secrets/coffer_kek</c> for Docker secrets, a projected Kubernetes
    /// Secret, or a Key Vault CSI mount. Tests set a temp path, the same way
    /// <c>Api:Backup:Directory</c> works.
    /// </summary>
    public string? Path { get; init; }
}

/// <summary>
/// MCP server configuration (ADR-0063). Off by default: the <c>/mcp</c>
/// endpoint and the token-management surface are only mapped when
/// <see cref="Enabled"/> is true, so a deployment that hasn't opted into
/// AI access exposes neither. Bound from <c>Api:Mcp</c>
/// (<c>COFFER_API__Mcp__Enabled</c> in compose).
/// </summary>
public sealed class McpOptions
{
    /// <summary>
    /// Master switch for the MCP read-only report server. Default false —
    /// the operator opts in (ADR-0063 §D7 "off-by-default"). When false the
    /// <c>/mcp</c> JSON-RPC endpoint and the <c>/api/account/mcp-tokens</c>
    /// management endpoints are not mapped.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Opt-in default for MCP <b>write</b> tools (ADR-0068). Default false. Seeds the
    /// startup value of the runtime writes kill-switch (ADR-0081 D2): the write tools
    /// are ALWAYS registered, but each is gated by <see cref="Coffer.Api.Mcp.McpWriteGuard"/>
    /// on this flag AND the caller's <c>coffer.write</c> scope. A deployment override
    /// (<c>COFFER_API__Mcp__WritesEnabled</c>) sets the boot value; the
    /// <c>mcp.writes_enabled</c> system setting an admin toggles in the UI flips it
    /// live, no restart.
    /// </summary>
    public bool WritesEnabled { get; init; }

    /// <summary>
    /// Lifetime of a newly issued access token in days. 0 means no expiry
    /// (the token lives until explicitly revoked). Default 90 — a remote AI
    /// client re-issues rather than holding an indefinite bearer secret.
    /// </summary>
    public int TokenLifetimeDays { get; init; } = 90;

    /// <summary>
    /// Cap on OAuth clients created via Dynamic Client Registration (RFC 7591).
    /// Registration is internet-facing (anonymous, per the spec), so the cap
    /// bounds resource exhaustion: once reached, <c>/oauth/register</c> rejects
    /// further registrations until an operator prunes. Default 50 — ample for a
    /// self-hosted install (a handful of AI clients), low enough to matter.
    /// </summary>
    public int MaxDynamicClients { get; init; } = 50;

    /// <summary>
    /// Max <c>POST /oauth/register</c> (Dynamic Client Registration) attempts per
    /// client IP per minute (ADR-0081 D4). DCR is anonymous + internet-facing, but a
    /// legitimate client registers once and caches, so this is tight — it bounds burst
    /// abuse of the endpoint, complementing <see cref="MaxDynamicClients"/> (the
    /// absolute ceiling). Default 5.
    /// </summary>
    public int DcrRateLimitPerMinute { get; init; } = 5;

    /// <summary>
    /// Public base URL an MCP client should be pointed at, e.g.
    /// <c>https://mcp.coffer.example.com</c>. Surfaced in the admin UI so an
    /// operator can copy the address to paste into Claude or another client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fed by <c>COFFER_MCP_URL</c>. Optional: when unset the UI falls back to the
    /// origin of the request it is being viewed on, which is right for the common
    /// single-host install and wrong for a split one — an install whose MCP server
    /// answers on its own subdomain would otherwise be told to use the main web
    /// address, which is the case an operator most needs told correctly.
    /// </para>
    /// <para>
    /// Named for its role rather than derived from a positional slot. It would have
    /// been available as <c>Fido2:Origins[1]</c> by convention, but that array is an
    /// allowed-origin list whose second entry only happens to be the MCP host —
    /// nothing enforces it, and a second consumer reading meaning into the index
    /// would make an undeclared convention load-bearing.
    /// </para>
    /// </remarks>
    public string PublicUrl { get; init; } = string.Empty;
}

/// <summary>
/// Authentication tunables (ADR-0013 follow-through). Bound from
/// <c>Api:Auth</c>.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Max <c>POST /api/auth/login/recovery</c> attempts per client IP per
    /// minute. A recovery code is a bearer secret and each attempt is an
    /// expensive Argon2id verify, so the window caps both brute-force and
    /// the memory-DoS the verify cost would otherwise enable. Default 10.
    /// </summary>
    public int RecoveryRateLimitPerMinute { get; init; } = 10;
}

/// <summary>
/// Whole-DB backup configuration (ADR-0060). The encrypted artifacts are
/// retained server-side under a tiered grandfather-father-son policy
/// (daily → weekly → monthly), pruned after each create.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>
    /// Directory for the stored <c>.cofferbak</c> artifacts. Null → the default
    /// <c>data/backups</c> beside the binary, which the Docker image mounts as
    /// a volume (ADR-0059). Set explicitly in tests to use a temp directory.
    /// </summary>
    public string? Directory { get; init; }

    // Backup retention (the GFS tiers) is admin-editable + persisted in
    // backup_settings (ADR-0074), no longer a startup option — see
    // BackupSettingsRepository.

    /// <summary><c>pg_dump --compress</c> value for the custom-format dump
    /// (ADR-0062). Default <c>zstd</c> — ~10% smaller than the historical zlib at
    /// similar/faster speed; PG16 restore reads it. Set e.g. <c>zstd:19</c> for a
    /// smaller artifact at much higher CPU, or <c>zlib</c>. Empty falls back to
    /// pg_dump's own default (zlib).</summary>
    public string Compress { get; init; } = "zstd";
}

/// <summary>
/// Cookie-session configuration. Defaults match the security posture in
/// ADR-0013: <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Strict</c>, 30-day
/// max lifetime, 7-day idle timeout. Tests can opt out of <c>Secure</c>
/// because <see cref="WebApplicationFactory"/> serves over plain HTTP.
/// Renamed from the obvious <c>CookieOptions</c> so it doesn't clash
/// with <see cref="Microsoft.AspNetCore.Http.CookieOptions"/> in callers
/// that <c>using</c> both namespaces.
/// </summary>
public sealed class CookieSessionOptions
{
    /// <summary>
    /// Cookie name. Default <c>coffer.session</c>; the <c>coffer.</c> prefix
    /// keeps it disambiguated from any other app sharing the host.
    /// </summary>
    public string Name { get; init; } = "coffer.session";

    /// <summary>
    /// Maximum session lifetime regardless of activity. Past this, the
    /// session is invalid even if the user has been active continuously.
    /// </summary>
    public int MaxLifetimeDays { get; init; } = 30;

    /// <summary>
    /// Idle timeout: a session whose <c>last_seen_at</c> is older than this
    /// is treated as expired. Tightens the lifetime envelope for
    /// long-abandoned tabs without forcing a daily re-login.
    /// </summary>
    public int IdleTimeoutDays { get; init; } = 7;

    /// <summary>
    /// When false, the <c>Secure</c> flag is omitted from the cookie. Test
    /// hosts (HTTP) need this; production deployments leave it true.
    /// </summary>
    public bool RequireHttps { get; init; } = true;
}

/// <summary>
/// Relying-party metadata Fido2 needs to validate registration and
/// assertion ceremonies. RP id is the eTLD+1 (or its sub-domain) of the
/// origin the browser sees; <see cref="Origins"/> is the exact origin set
/// allowed during registration. Both must match what the browser sends
/// or every ceremony fails with an obscure CBOR error — pin them
/// explicitly per environment instead of inferring at runtime.
/// </summary>
public sealed class Fido2Options
{
    /// <summary>
    /// RP id. Default <c>localhost</c> works for dev; production sets this
    /// to the actual host (e.g. <c>coffer.example.com</c>).
    /// </summary>
    public string RpId { get; init; } = "localhost";

    /// <summary>
    /// Display name shown in the browser's credential picker.
    /// </summary>
    public string RpName { get; init; } = "Coffer";

    /// <summary>
    /// Origins allowed to drive registration / assertion, and — via the first
    /// entry — the canonical browser-facing URL used to build the bootstrap
    /// setup link (<see cref="BootstrapTokenService"/>) and the Drive OAuth
    /// redirect URI (ADR-0062). Set per environment: dev via
    /// <c>appsettings.Development.json</c>, production via
    /// <c>COFFER_API__Fido2__Origins__0</c> (the compose <c>COFFER_WEB_ORIGIN_0</c>).
    ///
    /// Defaults to <b>empty</b> on purpose: the .NET configuration binder
    /// <i>appends</i> env-supplied collection entries to a non-empty default
    /// rather than replacing it, which would leave a stale entry (e.g.
    /// <c>http://localhost:5000</c>) at index 0 and silently misdirect the
    /// setup link / OAuth callback. An empty default lets config fully own the
    /// list. Validation rejects an empty effective list at startup.
    /// </summary>
    public IReadOnlyList<string> Origins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Lifetime of the registration / assertion challenge before the
    /// browser is expected to respond. Short by design: a stale challenge
    /// must be retried, not replayed.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;
}

/// <summary>
/// Configuration for the one-shot bootstrap token (per ADR-0013). Minted
/// at startup when no WebAuthn credentials exist; logged once at
/// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/> so an
/// operator tailing container logs sees it.
/// </summary>
public sealed class BootstrapOptions
{
    /// <summary>
    /// How long the minted token is valid before expiring. 24h matches the
    /// "operator finds the log line, completes setup the same day"
    /// expected flow; longer windows mean a dormant unconsumed token sits
    /// in the DB.
    /// </summary>
    public int TokenLifetimeHours { get; init; } = 24;
}
