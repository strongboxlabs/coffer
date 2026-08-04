using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// Test seam over Fido2NetLib's <see cref="Fido2"/>. The library exposes
/// a concrete class with non-virtual methods, which can't be mocked
/// directly; wrapping its narrow ceremony surface lets endpoint tests
/// substitute a fake without dragging in the full WebAuthn library.
/// </summary>
/// <remarks>
/// Registration (PR 3.4) and assertion (PR 3.5) share one interface so the
/// test seam stays a single abstraction. Endpoint tests substitute this
/// type via NSubstitute; the production code reaches Fido2NetLib only
/// through <see cref="Fido2WebAuthnService"/>.
/// </remarks>
public interface IWebAuthnService
{
    /// <summary>
    /// Generate a <see cref="CredentialCreateOptions"/> for a new
    /// registration ceremony. The returned object is sent to the browser;
    /// the challenge bytes embedded in it must be remembered until
    /// <see cref="CompleteRegistrationAsync"/> verifies the response.
    /// </summary>
    CredentialCreateOptions BeginRegistration(
        Fido2User user,
        IReadOnlyList<PublicKeyCredentialDescriptor> excludeCredentials);

    /// <summary>
    /// Verify the attestation response the browser produced and return
    /// the persistable shape of the new credential. Throws on any
    /// validation failure (the Fido2 library's exceptions bubble out
    /// untouched so endpoint code can map them to ProblemDetails).
    /// </summary>
    Task<WebAuthnRegistrationOutcome> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse rawResponse,
        CredentialCreateOptions originalOptions,
        IsCredentialIdUniqueToUserAsyncDelegate isCredentialIdUniqueToUser,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generate an <see cref="AssertionOptions"/> for a login ceremony.
    /// <paramref name="allowedCredentials"/> is the set the browser is
    /// allowed to use; pass the user's known credentials so the
    /// authenticator picker only offers relevant entries.
    /// </summary>
    AssertionOptions BeginAssertion(
        IReadOnlyList<PublicKeyCredentialDescriptor> allowedCredentials);

    /// <summary>
    /// Verify the assertion response the browser produced. Throws on any
    /// validation failure. The returned outcome carries the new signature
    /// counter (per WebAuthn spec, must strictly increase per credential
    /// per assertion); the caller persists it via
    /// <see cref="CredentialsRepository.UpdateAfterAssertionAsync"/>.
    /// </summary>
    Task<WebAuthnAssertionOutcome> CompleteAssertionAsync(
        AuthenticatorAssertionRawResponse rawResponse,
        AssertionOptions originalOptions,
        byte[] storedPublicKey,
        uint storedSignatureCounter,
        IsUserHandleOwnerOfCredentialIdAsync isUserHandleOwnerOfCredentialId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Library-agnostic result of a successful registration. Mirrors the
/// fields the API persists in <c>webauthn_credentials</c> so endpoint
/// code reads the result without learning Fido2NetLib's internal types.
/// </summary>
public sealed record WebAuthnRegistrationOutcome(
    byte[] CredentialId,
    byte[] PublicKey,
    uint SignatureCounter,
    Guid? Aaguid,
    string[]? Transports);

/// <summary>
/// Library-agnostic result of a successful assertion (login). The new
/// signature counter must be persisted before any further use of the
/// credential — replay attacks rely on a stale counter being accepted.
/// </summary>
public sealed record WebAuthnAssertionOutcome(
    byte[] CredentialId,
    uint NewSignatureCounter);
