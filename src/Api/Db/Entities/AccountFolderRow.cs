namespace Coffer.Api.Db.Entities;

/// <summary>
/// One sidebar folder (mig 258, FU-006): a named grouping of real accounts of ONE
/// account type, shared by every member of the ledger. Folders nest to any depth
/// through <see cref="ParentId"/>; a folder holds no transactions and has no balance
/// of its own (the sidebar shows the sum of what it holds).
/// </summary>
/// <remarks>
/// The database makes the type rule a fact rather than a check: a sub-folder's
/// parent and a member's folder are both referenced through
/// <c>(id, ledger_id, account_type)</c>, so neither can disagree with the folder on
/// the type. A trigger refuses a cycle (a folder inside its own sub-folder).
/// </remarks>
public sealed class AccountFolderRow
{
    public Guid Id { get; set; }

    public Guid LedgerId { get; set; }

    /// <summary>One of the six real account types; never <c>category</c>.</summary>
    public string AccountType { get; set; } = string.Empty;

    /// <summary>Trimmed, non-blank, at most 80 characters; unique among its siblings,
    /// ignoring case.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder this one sits in; null at the top level of its type's
    /// section.</summary>
    public Guid? ParentId { get; set; }

    public DateTime CreatedAt { get; set; }
}
