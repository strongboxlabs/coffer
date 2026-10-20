namespace Coffer.Api.Contracts;

/// <summary>
/// Public DTO returned by ledger-listing endpoints. Carries just the
/// fields the picker UI needs; the full row's <c>created_at</c> isn't
/// surfaced (clients don't sort by it; the row doesn't need to).
/// </summary>
public sealed record LedgerSummary(Guid Id, string Name, string Role);

/// <summary>
/// Request body for <c>POST /api/ledgers</c>.
/// </summary>
public sealed class CreateLedgerRequest
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Seed a curated starter category tree into the new ledger (ADR-0071 D5).
    /// Default true so a fresh ledger is usable immediately; the New Ledger
    /// dialog exposes it as an opt-out for users who want to start blank.
    /// </summary>
    public bool SeedDefaultCategories { get; init; } = true;
}

/// <summary>
/// Request body for <c>PATCH /api/ledgers/{id}</c> (rename).
/// </summary>
public sealed class RenameLedgerRequest
{
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// A member of a ledger (ADR-0083): a <c>user_ledger_grants</c> row joined to the
/// user's display fields. Returned by <c>GET /api/ledgers/{id}/members</c>.
/// </summary>
public sealed record LedgerMember(Guid UserId, string DisplayName, string? Username, string Role);

/// <summary>
/// Request body for <c>PUT /api/ledgers/{id}/members/{userId}</c> — set a member's role.
/// </summary>
public sealed class SetMemberRoleRequest
{
    public string Role { get; init; } = string.Empty;
}
