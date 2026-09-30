using System.Globalization;

using Microsoft.AspNetCore.Http;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;

namespace Coffer.Api.Backup;

/// <summary>
/// Receiving a restore artifact in pieces (ADR-0101), shared by the two paths
/// that need it: the authenticated admin restore and the pre-auth bootstrap
/// restore.
/// </summary>
/// <remarks>
/// One implementation rather than two, because the two paths must agree on how
/// bytes are assembled and there is no test that would catch them drifting: a
/// set assembled one way by one path and another way by the other still writes
/// a file, still stages, and only fails at decrypt — as a wrong passphrase.
/// <para>Nothing here is destructive. Parts accumulate in staging and are
/// promoted only by a separate, confirmed restore call, which is why neither
/// caller puts a confirmation gate in front of it.</para>
/// </remarks>
public static class RestorePartProtocol
{
    /// <summary>
    /// Validate and append one part. Returns the 200 acknowledgement, or the
    /// 422 explaining why the part was refused.
    /// </summary>
    public static async Task<IResult> AcceptAsync(
        IFormCollection form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var file = form.Files["archive"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "A part ('archive') is required.");

        if (!int.TryParse(form["part"].ToString(), CultureInfo.InvariantCulture, out var part)
            || !int.TryParse(form["partCount"].ToString(), CultureInfo.InvariantCulture, out var partCount)
            || !long.TryParse(form["partSizeBytes"].ToString(), CultureInfo.InvariantCulture, out var partSize)
            || part < 1 || partCount < 1 || part > partCount || partSize < 1)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                "'part', 'partCount' and 'partSizeBytes' must be whole numbers with "
                + "1 <= part <= partCount and partSizeBytes >= 1.");

        // The part size is the CLIENT's, not this server's.
        //
        // It is configurable (Api:Backup:PartSizeMb), so validating against the
        // server's current value would reject a set cut before someone changed
        // it — including parts downloaded from off-host storage months ago,
        // which is the case this whole path exists for.
        //
        // Taking it from the client is not trust: the arithmetic below makes a
        // wrong value self-defeating. Every part but the last is exactly
        // partSize, so part N can only begin at (N-1) * partSize. A client that
        // declares the wrong size fails that check on its very next part — and
        // on part 1 there is nothing yet to corrupt.
        //
        // Deriving the position from the bytes already on disk, rather than
        // tracking it in memory, is also what lets an interrupted upload resume
        // across a process restart.
        //
        // Part 1 always restarts rather than appending: the only way out of an
        // upload the client abandoned, and what a retry-from-scratch does anyway.
        var received = part == 1 ? 0L : BootstrapRestoreStaging.UploadedBytes() ?? 0L;
        var expectedOffset = (long)(part - 1) * partSize;
        if (received != expectedOffset)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                $"Part {part} should begin at byte {expectedOffset}, but {received} bytes "
                + "have arrived. Parts must be sent in order, without gaps or repeats — "
                + "start again from part 1.");

        // Every part but the last is exactly partSize. That is the invariant the
        // offset check above rests on, so a short part in the middle is refused
        // rather than quietly shifting every part after it. It also means a
        // re-sent LAST part is caught: once it has landed, no part number
        // satisfies the offset check.
        if (part < partCount && file.Length != partSize)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                $"Part {part} of {partCount} must be exactly {partSize} bytes "
                + $"(got {file.Length}) — only the last part may be short.");

        if (file.Length > partSize)
            return BusinessError.Problem(BusinessError.Codes.BackupRestoreInvalid,
                $"Part {part} is {file.Length} bytes, larger than the declared part "
                + $"size of {partSize}.");

        await using (var content = file.OpenReadStream())
            await BootstrapRestoreStaging.AppendUploadAsync(part, content, cancellationToken)
                .ConfigureAwait(false);

        return Results.Ok(new BackupContracts.RestorePartAccepted(
            Part: part,
            PartCount: partCount,
            ReceivedBytes: BootstrapRestoreStaging.UploadedBytes() ?? 0L,
            Complete: part == partCount));
    }
}
