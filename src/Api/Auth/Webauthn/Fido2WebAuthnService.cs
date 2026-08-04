using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// Concrete <see cref="IWebAuthnService"/> backed by Fido2NetLib's
/// <see cref="Fido2"/>. Translates Coffer's library-agnostic types to the
/// params-objects API in 4.0.x; the rest of the codebase doesn't reach
/// past this seam.
/// </summary>
public sealed class Fido2WebAuthnService : IWebAuthnService
{
    private readonly IFido2 _fido2;

    public Fido2WebAuthnService(IFido2 fido2)
    {
        _fido2 = fido2;
    }

    public CredentialCreateOptions BeginRegistration(
        Fido2User user,
        IReadOnlyList<PublicKeyCredentialDescriptor> excludeCredentials)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(excludeCredentials);

        return _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = excludeCredentials.ToList(),
            // Explicit (not AuthenticatorSelection.Default) so a library
            // default change can't silently force platform-only attachment and
            // lock out the user's primary cross-platform YubiKey (ADR-0013).
            // null attachment = allow both cross-platform (USB/NFC security
            // keys) and platform (Touch ID / Windows Hello); UV preferred.
            AuthenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = null,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
    }

    public async Task<WebAuthnRegistrationOutcome> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse rawResponse,
        CredentialCreateOptions originalOptions,
        IsCredentialIdUniqueToUserAsyncDelegate isCredentialIdUniqueToUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawResponse);
        ArgumentNullException.ThrowIfNull(originalOptions);
        ArgumentNullException.ThrowIfNull(isCredentialIdUniqueToUser);

        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = rawResponse,
            OriginalOptions = originalOptions,
            IsCredentialIdUniqueToUserCallback = isCredentialIdUniqueToUser,
        }, cancellationToken).ConfigureAwait(false);

        // The library returns a strongly-typed result object whose shape
        // mirrors what we persist; project to our library-agnostic record
        // so endpoint code stays clean of Fido2NetLib types.
        return new WebAuthnRegistrationOutcome(
            CredentialId: result.Id,
            PublicKey: result.PublicKey,
            SignatureCounter: result.SignCount,
            Aaguid: result.AaGuid == Guid.Empty ? null : result.AaGuid,
            Transports: result.Transports?.Select(t => t.ToString()).ToArray());
    }

    public AssertionOptions BeginAssertion(
        IReadOnlyList<PublicKeyCredentialDescriptor> allowedCredentials)
    {
        ArgumentNullException.ThrowIfNull(allowedCredentials);

        return _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials.ToList(),
            UserVerification = UserVerificationRequirement.Preferred,
        });
    }

    public async Task<WebAuthnAssertionOutcome> CompleteAssertionAsync(
        AuthenticatorAssertionRawResponse rawResponse,
        AssertionOptions originalOptions,
        byte[] storedPublicKey,
        uint storedSignatureCounter,
        IsUserHandleOwnerOfCredentialIdAsync isUserHandleOwnerOfCredentialId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawResponse);
        ArgumentNullException.ThrowIfNull(originalOptions);
        ArgumentNullException.ThrowIfNull(storedPublicKey);
        ArgumentNullException.ThrowIfNull(isUserHandleOwnerOfCredentialId);

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = rawResponse,
            OriginalOptions = originalOptions,
            StoredPublicKey = storedPublicKey,
            StoredSignatureCounter = storedSignatureCounter,
            IsUserHandleOwnerOfCredentialIdCallback = isUserHandleOwnerOfCredentialId,
        }, cancellationToken).ConfigureAwait(false);

        return new WebAuthnAssertionOutcome(
            CredentialId: rawResponse.RawId,
            NewSignatureCounter: result.SignCount);
    }
}
