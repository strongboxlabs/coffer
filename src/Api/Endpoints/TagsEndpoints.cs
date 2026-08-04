using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger tag-dictionary management (Tags v1). REST surface for the
/// Settings → Tags panel and the shared tag autocomplete: list-with-usage,
/// rename / recolor (PATCH), merge, delete (delete-in-use allowed), and
/// cleanup-unused. Same auth contract as <see cref="CategoriesEndpoints"/>:
/// authenticated user + a grant on the ledger; RLS enforces the predicate at the
/// data layer. Assigning tags to a transaction stays on the PATCH transaction
/// endpoint (slice 2c.6b) — this file is dictionary admin only.
/// </summary>
public static class TagsEndpoints
{
    // One name cap whether a tag is minted via assignment or renamed here
    // (mirrors TransactionsEndpoints.MaxTagNameLength).
    private const int MaxTagNameLength = 64;

    public static IEndpointRouteBuilder MapTagsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/tags")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        // Literal "/unused" is matched ahead of "/{tagId:guid}" (the guid
        // constraint rejects it), so the two DELETEs don't collide.
        group.MapDelete("/unused", CleanupUnusedAsync);
        group.MapPatch("/{tagId:guid}", PatchAsync);
        group.MapPost("/{tagId:guid}/merge", MergeAsync);
        group.MapDelete("/{tagId:guid}", DeleteAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/tags</c> — every tag with its assignment
    /// count, name-sorted. Source for the management table and the autocomplete.
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TagsRepository tags,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await tags.ListWithUsageAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/tags/{tagId}</c> — rename and/or recolor.
    /// A rename that collides with another tag (case-insensitive) returns
    /// <c>tag-name-exists</c> so the UI can offer a merge.
    /// </summary>
    private static async Task<IResult> PatchAsync(
        Guid ledgerId,
        Guid tagId,
        PatchTagRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TagsRepository tags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Name: trim + length-cap (mirror TransactionsEndpoints.ValidateTags).
        string? newName = null;
        if (request.Name is not null)
        {
            newName = request.Name.Trim();
            if (newName.Length == 0)
                return BusinessError.Problem(BusinessError.Codes.TransactionTagEmpty,
                    "Tag names cannot be empty or whitespace-only.");
            if (newName.Length > MaxTagNameLength)
                return BusinessError.Problem(BusinessError.Codes.TransactionTagTooLong,
                    $"Tag names must be {MaxTagNameLength} characters or fewer.");
        }

        // Colour: #rrggbb hex, case-insensitive, stored lower-cased.
        string? newColor = null;
        if (request.Color is not null)
        {
            newColor = NormalizeColor(request.Color);
            if (newColor is null)
                return BusinessError.Problem(BusinessError.Codes.TagColorInvalid,
                    "Colour must be a #rrggbb hex value.");
        }

        if (newName is null && newColor is null)
            return BusinessError.Problem(BusinessError.Codes.TransactionPatchEmpty,
                "Supply a new name or colour.");

        var result = await tags.PatchAsync(
            ledgerId, tagId, newName, newColor, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            TagsRepository.PatchTagResult.Ok => Results.NoContent(),
            TagsRepository.PatchTagResult.NotFound =>
                BusinessError.Problem(BusinessError.Codes.TagNotFound,
                    "Tag not found in this ledger."),
            TagsRepository.PatchTagResult.NameExists =>
                BusinessError.Problem(BusinessError.Codes.TagNameExists,
                    "Another tag in this ledger already has that name; merge them instead."),
            _ => Results.Problem("Unknown patch-tag result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/tags/{tagId}/merge</c> — merge this
    /// (source) tag into <c>intoTagId</c>: repoint every assignment (deduped) and
    /// delete the source. Returns the count repointed.
    /// </summary>
    private static async Task<IResult> MergeAsync(
        Guid ledgerId,
        Guid tagId,
        MergeTagRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TagsRepository tags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IntoTagId == Guid.Empty)
            return BusinessError.Problem(BusinessError.Codes.TagNotFound,
                "A target tag is required.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await tags.MergeAsync(
            ledgerId, tagId, request.IntoTagId, cancellationToken).ConfigureAwait(false);
        return outcome.Result switch
        {
            TagsRepository.MergeTagResult.Ok =>
                Results.Ok(new MergeTagResponse(outcome.TransactionsRepointed)),
            TagsRepository.MergeTagResult.MergeSelf =>
                BusinessError.Problem(BusinessError.Codes.TagMergeSelf,
                    "A tag cannot be merged into itself."),
            TagsRepository.MergeTagResult.SourceNotFound =>
                BusinessError.Problem(BusinessError.Codes.TagNotFound,
                    "Source tag not found in this ledger."),
            TagsRepository.MergeTagResult.TargetNotFound =>
                BusinessError.Problem(BusinessError.Codes.TagNotFound,
                    "Target tag not found in this ledger."),
            _ => Results.Problem("Unknown merge-tag result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/tags/{tagId}</c> — hard-delete a tag and
    /// untag every transaction that carried it (FK cascade). Allowed even in use.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid tagId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TagsRepository tags,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await tags.DeleteAsync(ledgerId, tagId, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            TagsRepository.DeleteTagResult.Ok => Results.NoContent(),
            TagsRepository.DeleteTagResult.NotFound =>
                BusinessError.Problem(BusinessError.Codes.TagNotFound,
                    "Tag not found in this ledger."),
            _ => Results.Problem("Unknown delete-tag result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/tags/unused</c> — remove every tag with
    /// zero assignments. Returns the count removed.
    /// </summary>
    private static async Task<IResult> CleanupUnusedAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        TagsRepository tags,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var removed = await tags.CleanupUnusedAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new CleanupTagsResponse(removed));
    }

    /// <summary>Accept a 6-digit hex colour (<c>#rrggbb</c>, case-insensitive) and
    /// return it lower-cased; anything else → <c>null</c> (invalid).</summary>
    private static string? NormalizeColor(string raw)
    {
        var s = raw.Trim();
        if (s.Length != 7 || s[0] != '#')
            return null;
        for (var i = 1; i < 7; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
                return null;
        }
        return s.ToLowerInvariant();
    }
}
