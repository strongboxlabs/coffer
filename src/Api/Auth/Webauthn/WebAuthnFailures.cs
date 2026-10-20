using Fido2NetLib;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// What a browser is told when a WebAuthn ceremony fails verification (FU-095).
/// </summary>
/// <remarks>
/// The ceremonies returned the library's own message, which names the origins and the
/// relying-party id the server expects, to whoever sent the response, before they were
/// signed in. It is the operator who needs that text ("origin … not equal to …" is how a
/// misconfigured COFFER_RP_ID shows itself), so it goes to the server log, and the browser
/// is told where to look.
/// </remarks>
internal static class WebAuthnFailures
{
    public const string Assertion =
        "The passkey's response did not verify. Try again; if it keeps failing, the server log has the reason.";

    public const string Attestation =
        "The passkey could not be registered: its response did not verify. Try again; if it keeps "
        + "failing, the server log has the reason.";

    /// <summary>Log the library's reason for the operator.</summary>
    public static void Log(IServiceProvider services, Fido2VerificationException ex, string ceremony) =>
        services.GetRequiredService<ILoggerFactory>().CreateLogger("Coffer.Api.WebAuthn")
            .LogWarning("WebAuthn {Ceremony} failed verification: {Reason}", ceremony, ex.Message);
}
