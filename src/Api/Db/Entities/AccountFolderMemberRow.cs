namespace Coffer.Api.Db.Entities;

/// <summary>
/// Which folder an account sits in (mig 258). One row per account at most, keyed
/// on <see cref="AccountId"/>; an account in no folder has no row.
/// <see cref="AccountType"/> is the column both foreign keys share, which is what
/// makes "the account's type is the folder's type" true in the database.
/// </summary>
public sealed class AccountFolderMemberRow
{
    public Guid AccountId { get; set; }

    public Guid LedgerId { get; set; }

    public Guid FolderId { get; set; }

    public string AccountType { get; set; } = string.Empty;
}
