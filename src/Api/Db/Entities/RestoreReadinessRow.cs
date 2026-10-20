namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for <c>restore_readiness()</c> (migration 246): whether the
/// connecting role may restore a backup by building a new database and swapping
/// it in — CREATEDB, and ownership of the live database.
/// </summary>
public sealed class RestoreReadinessRow
{
    public string RoleName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public bool CanCreateDatabases { get; init; }
    public bool OwnsDatabase { get; init; }
    /// <summary><c>c</c> (libc) or <c>i</c> (ICU); the swap rebuilds libc only.</summary>
    public string LocaleProvider { get; init; } = string.Empty;
    /// <summary>The live database's COMMENT: a restore's stamp, when one put it there.</summary>
    public string? RestoreMarker { get; init; }
}
