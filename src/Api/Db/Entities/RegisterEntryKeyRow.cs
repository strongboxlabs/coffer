namespace Coffer.Api.Db.Entities;

/// <summary>
/// One register entry of a keyset page: its key (the header on the side
/// the header belongs to, else the leg) and the <c>(posted_at, seq)</c>
/// its cursor carries. <see cref="Repositories.RegisterQuery"/> returns
/// these in display order. A plain projection, not an entity.
/// </summary>
internal sealed class RegisterEntryKeyRow
{
    public DateTime PostedAt { get; init; }
    public long Seq { get; init; }
    public Guid EntryKey { get; init; }
}

/// <summary>
/// One entry of a page across several accounts: the account whose register shows it,
/// with its key and <c>(posted_at, seq)</c>, as
/// <see cref="Repositories.RegisterQuery.FirstEntryKeysOnAccountsAsync"/> returns them.
/// A key alone does not name such an entry: one header can be an entry, under the same
/// key, on more than one of the accounts. A plain projection, not an entity.
/// </summary>
internal sealed class AccountEntryKeyRow
{
    public Guid AccountId { get; init; }
    public DateTime PostedAt { get; init; }
    public long Seq { get; init; }
    public Guid EntryKey { get; init; }
}
