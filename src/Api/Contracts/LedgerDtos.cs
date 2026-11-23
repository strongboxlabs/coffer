namespace Coffer.Api.Contracts;

/// <summary>
/// Public DTO returned by ledger-listing endpoints. Carries just the
/// fields the picker UI needs; the full row's <c>created_at</c> isn't
/// surfaced (clients don't sort by it; the row doesn't need to).
/// </summary>
/// <param name="CurrencyCode">The ledger's reporting currency (ADR-0114 D7): what a figure
/// that combines accounts converts into. Settings → General shows it, and only an owner
/// changes it (<c>PATCH /api/ledgers/{id}</c>).</param>
public sealed record LedgerSummary(Guid Id, string Name, string Role, string CurrencyCode);

/// <summary>
/// One row of <c>GET /api/ledgers</c>: a <see cref="LedgerSummary"/> plus what the hub
/// shows beside it.
/// </summary>
/// <param name="DemoExamplesMissing">For a Demo whose examples were not all added, the
/// names of those left out, which the hub lists under the row (ADR-0115 D9). Null for
/// every other ledger, a Demo with all its examples, and a Demo still being built.</param>
public sealed record LedgerListItem(
    Guid Id, string Name, string Role, string CurrencyCode, IReadOnlyList<string>? DemoExamplesMissing);

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

    /// <summary>
    /// The new ledger's reporting currency (ADR-0114 D7): an ISO 4217 currency with at most
    /// 3 places (<c>CurrencyMinorUnits.IsAcceptedAccountCurrency</c>, ADR-0114 D15), else
    /// <c>ledger-currency-invalid</c>. Null or blank is USD.
    /// </summary>
    public string? CurrencyCode { get; init; }
}

/// <summary>
/// Request body for <c>PATCH /api/ledgers/{id}</c>: a rename, a reporting-currency change,
/// or both. Owner only. A field left null is left as it is; at least one is required.
/// </summary>
public sealed class UpdateLedgerRequest
{
    /// <summary>The new name. Null leaves it; blank is refused.</summary>
    public string? Name { get; init; }

    /// <summary>The new reporting currency (ADR-0114 D7). Null leaves it. A code that is not
    /// an accepted ISO 4217 currency with at most 3 places is <c>ledger-currency-invalid</c>
    /// (ADR-0114 D15); resending the ledger's own stored code is not a change, so a code
    /// stored before the rule keeps working at 2 places. A change starts a market-series
    /// backfill (ADR-0114 D11).</summary>
    public string? CurrencyCode { get; init; }
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
