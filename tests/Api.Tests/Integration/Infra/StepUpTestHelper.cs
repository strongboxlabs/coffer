using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;

using NSubstitute;

using Coffer.Api.Auth.Webauthn;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Drives the step-up tap a sensitive action needs (2026-10-12 security audit, M2) the
/// way the SPA does: begin a ceremony for the action, then send the assertion in the
/// <see cref="StepUpGuard.HeaderName"/> header of the request it approves.
/// </summary>
/// <remarks>
/// Fido2's signature check is substituted (as the master-key tests do), so these
/// isolate the gate's own rules: single use, the right action, the caller's passkey,
/// a passkey older than the session, the UV flag. The fake assertion sets the UV bit
/// explicitly — random authenticator data would pass or fail the UV check by chance.
/// </remarks>
public static class StepUpTestHelper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Make <paramref name="sub"/> accept every assertion (and begin one for any credentials).</summary>
    public static IWebAuthnService AcceptAssertions(IWebAuthnService sub)
    {
        sub.BeginAssertion(Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>(), Arg.Any<bool>())
           .Returns(_ => new AssertionOptions
           {
               Challenge = RandomNumberGenerator.GetBytes(32),
               Timeout = 60000,
               AllowCredentials = new List<PublicKeyCredentialDescriptor>(),
               UserVerification = UserVerificationRequirement.Required,
           });
        sub.CompleteAssertionAsync(
                Arg.Any<AuthenticatorAssertionRawResponse>(),
                Arg.Any<AssertionOptions>(),
                Arg.Any<byte[]>(),
                Arg.Any<uint>(),
                Arg.Any<IsUserHandleOwnerOfCredentialIdAsync>(),
                Arg.Any<CancellationToken>())
           .Returns(call => Task.FromResult(new WebAuthnAssertionOutcome(
               CredentialId: ((AuthenticatorAssertionRawResponse)call[0]).RawId,
               NewSignatureCounter: 7)));
        return sub;
    }

    /// <summary>A fresh substitute that accepts every assertion.</summary>
    public static IWebAuthnService AcceptingWebAuthn() => AcceptAssertions(Substitute.For<IWebAuthnService>());

    /// <summary>An assertion "from" <paramref name="credentialId"/>, with the UV flag as asked.</summary>
    public static AuthenticatorAssertionRawResponse FakeAssertion(byte[] credentialId, bool userVerified = true)
    {
        var authenticatorData = RandomNumberGenerator.GetBytes(64);
        // Byte 32 is the flags byte after the 32-byte rpIdHash: UP = 0x01, UV = 0x04.
        authenticatorData[32] = (byte)(userVerified ? 0x05 : 0x01);
        return new AuthenticatorAssertionRawResponse
        {
            Id = Convert.ToBase64String(credentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            RawId = credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                ClientDataJson = RandomNumberGenerator.GetBytes(128),
                Signature = RandomNumberGenerator.GetBytes(64),
            },
        };
    }

    /// <summary>
    /// Begin a tap for <paramref name="action"/> as <paramref name="client"/>'s user and
    /// return the header value approving one request with <paramref name="credentialId"/>.
    /// </summary>
    public static async Task<string> HeaderValueAsync(
        HttpClient client, string action, byte[] credentialId, bool userVerified = true)
    {
        var begin = await client.PostAsJsonAsync("/api/auth/step-up/begin", new { action });
        begin.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();
        return Encode(challengeId, FakeAssertion(credentialId, userVerified));
    }

    /// <summary>The header value for an explicit challenge id and assertion.</summary>
    public static string Encode(Guid challengeId, AuthenticatorAssertionRawResponse assertion)
    {
        var json = JsonSerializer.Serialize(
            new StepUpGuard.Proof(challengeId, assertion), Json);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Send <paramref name="request"/> with a tap for <paramref name="action"/> attached.</summary>
    public static async Task<HttpResponseMessage> SendWithStepUpAsync(
        HttpClient client, HttpRequestMessage request, string action, byte[] credentialId)
    {
        request.Headers.Add(StepUpGuard.HeaderName, await HeaderValueAsync(client, action, credentialId));
        return await client.SendAsync(request);
    }
}
