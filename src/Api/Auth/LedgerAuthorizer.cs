using Microsoft.AspNetCore.Http;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Auth;

/// <summary>
/// Per-ledger role authorization (ADR-0020 owner/editor/viewer; ADR-0083 D2).
/// The API-layer PRIMARY check: resolves the caller's grant role on a ledger and
/// returns a 422 business-error <see cref="IResult"/> when the role is
/// insufficient, or <c>null</c> to proceed. Role-aware RLS (migration 174) is the
/// DB backstop beneath this, so a missed call can never let a viewer write — but
/// the friendly rejection lives here (and it avoids the silent 0-row no-op RLS
/// produces on a blocked UPDATE/DELETE). Reused by the REST ledger endpoints and
/// the MCP write tools; a single place resolves "what can this user do here".
/// </summary>
public sealed class LedgerAuthorizer(ICurrentUserAccessor currentUser, LedgersRepository ledgers)
{
    /// <summary>Roles permitted to mutate ledger data (ADR-0020 role matrix).</summary>
    private static readonly string[] WriteRoles = ["owner", "editor"];

    /// <summary>The caller's grant on the ledger, or null when they can't see it.</summary>
    public Task<LedgerSummary?> ResolveAsync(Guid ledgerId, CancellationToken ct = default) =>
        ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, ct);

    /// <summary>Null if the caller has any grant on the ledger; else not-visible.</summary>
    public async Task<IResult?> RequireReadAsync(Guid ledgerId, CancellationToken ct = default) =>
        await ResolveAsync(ledgerId, ct).ConfigureAwait(false) is null ? NotVisible() : null;

    /// <summary>Null if the caller is owner/editor; not-visible or not-writable otherwise.</summary>
    public async Task<IResult?> RequireWriteAsync(Guid ledgerId, CancellationToken ct = default)
    {
        var grant = await ResolveAsync(ledgerId, ct).ConfigureAwait(false);
        if (grant is null) return NotVisible();
        return WriteRoles.Contains(grant.Role) ? null : NotWritable();
    }

    /// <summary>Null if the caller is owner; not-visible or not-owner otherwise.</summary>
    public async Task<IResult?> RequireOwnerAsync(Guid ledgerId, CancellationToken ct = default)
    {
        var grant = await ResolveAsync(ledgerId, ct).ConfigureAwait(false);
        if (grant is null) return NotVisible();
        return grant.Role == "owner" ? null : NotOwner();
    }

    /// <summary>
    /// True when the caller may mutate the ledger (owner/editor). For call sites
    /// that don't return <see cref="IResult"/> (e.g. MCP write tools, which map to
    /// their own failure envelope).
    /// </summary>
    public async Task<bool> CanWriteAsync(Guid ledgerId, CancellationToken ct = default)
    {
        var grant = await ResolveAsync(ledgerId, ct).ConfigureAwait(false);
        return grant is not null && WriteRoles.Contains(grant.Role);
    }

    private static IResult NotVisible() =>
        BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
            "Ledger not found or not visible to this user.");

    private static IResult NotWritable() =>
        BusinessError.Problem(BusinessError.Codes.LedgerNotWritable,
            "This ledger is read-only for you. Ask an owner for editor or owner access.");

    private static IResult NotOwner() =>
        BusinessError.Problem(BusinessError.Codes.LedgerNotOwner,
            "Only an owner can perform this action.");
}
