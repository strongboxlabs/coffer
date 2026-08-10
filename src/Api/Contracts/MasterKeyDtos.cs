using Fido2NetLib;

namespace Coffer.Api.Contracts;

/// <summary>
/// Wire shapes for the admin master-KEK surface (ADR-0092 D2,
/// <c>/api/admin/master-key</c>). The key itself appears in exactly one place —
/// <see cref="MasterKeyContracts.RevealResponse"/>, returned only from a POST that
/// carried a verified fresh passkey assertion, with <c>Cache-Control: no-store</c>.
/// </summary>
public static class MasterKeyContracts
{
    /// <summary>
    /// Response for <c>GET /api/admin/master-key</c> — metadata only, no key
    /// material, so the panel can render without a re-auth ceremony.
    /// </summary>
    /// <param name="KekId">The id stamped on new wraps (<c>ledgers.lek_kek_id</c>).</param>
    /// <param name="Path">Where the key file lives, so an operator can find or
    /// back it up out of band.</param>
    /// <param name="Fingerprint">Short non-reversible fingerprint of the current
    /// key. Lets an operator confirm which key an install is running — and match
    /// it against a backup's — without revealing it.</param>
    public sealed record MasterKeyStatusResponse(string KekId, string Path, string Fingerprint);

    /// <summary>
    /// Response for <c>POST /api/admin/master-key/reveal/begin</c>: the assertion
    /// ceremony the caller must complete to see the key.
    /// </summary>
    public sealed record RevealBeginResponse(Guid ChallengeId, AssertionOptions AssertionOptions);

    /// <summary>
    /// Body for <c>POST /api/admin/master-key/reveal</c>. The assertion proves a
    /// human with an enrolled authenticator is present right now; the session
    /// cookie alone only proves an admin was here at some point in the last 30
    /// days.
    /// </summary>
    public sealed record RevealRequest(
        Guid ChallengeId,
        AuthenticatorAssertionRawResponse? AssertionResponse);

    /// <summary>
    /// The key, base64-encoded, plus its id. The only response body in the API
    /// that carries master key material.
    /// </summary>
    public sealed record RevealResponse(string KeyBase64, string KekId);


    /// <summary>
    /// Body for <c>POST /api/admin/master-key/rotate</c>. Same fresh-assertion
    /// requirement as a reveal — rotation both writes new key material and hands it
    /// back, so it is at least as sensitive.
    /// </summary>
    /// <remarks>
    /// No new-key-id field: the id is advisory (only rotation reads it, and it re-wraps
    /// everything regardless of what it says), so letting a caller choose one was a
    /// decision with no consequence — and the UI's placeholder reimplemented
    /// <c>NextKekId</c> client-side, so the two could drift over a label nothing depends
    /// on. The server increments.
    /// </remarks>
    public sealed record RotateRequest(
        Guid ChallengeId,
        AuthenticatorAssertionRawResponse? AssertionResponse);

    /// <summary>
    /// Result of a rotation: the NEW key (shown once here — the operator must save
    /// it), what was re-wrapped, and where the previous key was archived.
    /// </summary>
    /// <param name="RestartPending">Always true: the process holds the old key in
    /// memory, so it restarts to pick up the new one. The panel uses this to tell
    /// the operator to expect a brief reconnect.</param>
    public sealed record RotateResponse(
        string KeyBase64,
        string KekId,
        int LedgersRotated,
        bool BackupPassphraseRotated,
        bool DriveTokenRotated,
        string? PreviousKeyArchivedAt,
        bool RestartPending);
}
