namespace Coffer.Api.Db.Entities;

/// <summary>
/// Row shape returned by the <c>register_entry_keys</c> Postgres
/// function (migration 019; canonical-ordering rewrite in mig 097).
/// One row per register entry; entry_key is always the owning
/// header's id. The function returns these in
/// <c>(posted_at DESC, seq DESC)</c> order — canonical ADR-0034 v2
/// pair, no UUID tiebreaker.
/// </summary>
/// <remarks>
/// Configured as a keyless EF entity in <c>AppDbContext.OnModelCreating</c>
/// with explicit <c>HasColumnName</c> mappings (snake_case → PascalCase).
/// Not exposed via a <c>DbSet</c>; reachable only through the mapped
/// <c>RegisterEntryKeys(...)</c> TVF anchor.
/// </remarks>
internal sealed class RegisterEntryKeyRow
{
    public DateTime PostedAt { get; init; }
    public long Seq { get; init; }
    public Guid EntryKey { get; init; }
}
