namespace Coffer.Api.Contracts;

/// <summary>
/// Payload of <c>GET /api/meta/version</c> (ADR-0044). The two
/// server-side version axes the SPA can't determine on its own; the UI
/// supplies its own axis from build-time constants. Installation-wide,
/// not per-ledger — there is one DB, one API process.
/// </summary>
public sealed record VersionResponse(ApiVersionDto Api, DbVersionDto Db);

/// <param name="Version">Semver release handle, e.g. <c>0.1.0</c>.</param>
/// <param name="Build">Git commit count — the monotonic build number
/// that lets the user see a progression (412 is newer than 408).</param>
/// <param name="Commit">Git short SHA — exact "what's running".</param>
/// <param name="CommitDate">Commit date (<c>yyyy-MM-dd</c>), or empty
/// when built outside a git checkout.</param>
public sealed record ApiVersionDto(
    string Version,
    int Build,
    string Commit,
    string CommitDate);

/// <param name="SchemaVersion">The latest applied migration number
/// (parsed from the DbUp script name's <c>NNN_</c> prefix) — the DB's
/// own progression.</param>
/// <param name="Script">The latest applied migration's name, without
/// path or <c>.sql</c> extension, e.g.
/// <c>118_recompute_holdings_split_first_buys_before_sells</c>.</param>
public sealed record DbVersionDto(int SchemaVersion, string Script);
