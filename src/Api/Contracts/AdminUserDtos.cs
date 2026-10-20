namespace Coffer.Api.Contracts;

/// <summary>
/// One row of the admin user-management list (ADR-0083): identity + the instance
/// admin flag + the soft-disable (lockout) flag + how many ledgers the user has a
/// grant on. Returned by <c>GET /api/admin/users</c> (RequireAdmin).
/// </summary>
public sealed record AdminUserSummary(
    Guid Id, string DisplayName, string? Username, bool IsAdmin, bool IsDisabled, int LedgerCount);

/// <summary>Request body for <c>PUT /api/admin/users/{userId}/disabled</c>.</summary>
public sealed class SetUserDisabledRequest
{
    public bool Disabled { get; init; }
}

/// <summary>Request body for <c>PUT /api/admin/users/{userId}/admin</c>.</summary>
public sealed class SetUserAdminRequest
{
    public bool IsAdmin { get; init; }
}
