using System.Text.Json;

using OpenIddict.Abstractions;

namespace Coffer.Api.Auth;

/// <summary>
/// What Coffer records about an OAuth client registration beyond what OpenIddict keeps.
/// OpenIddict stores no creation time for an application, so the registration endpoint
/// stamps one into the application's <c>Properties</c> bag. It is written once, when the
/// client registers, and never updated, so the bag's read-modify-write hazard (ADR-0107 D3)
/// does not arise.
/// </summary>
public static class OAuthClientRegistration
{
    /// <summary>The property holding when the client registered, as an ISO-8601 instant.</summary>
    public const string RegisteredAtProperty = "coffer:registered_at";

    /// <summary>The value to store under <see cref="RegisteredAtProperty"/>.</summary>
    public static JsonElement RegisteredAtValue(DateTimeOffset registeredAt) =>
        JsonSerializer.SerializeToElement(registeredAt.UtcDateTime);

    /// <summary>
    /// When the client registered, or null for one registered before the time was recorded:
    /// there is no honest value to show for it.
    /// </summary>
    public static async Task<DateTime?> ReadRegisteredAtAsync(
        IOpenIddictApplicationManager applications, object application, CancellationToken ct)
    {
        var properties = await applications.GetPropertiesAsync(application, ct).ConfigureAwait(false);
        if (!properties.TryGetValue(RegisteredAtProperty, out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTime(out var at))
            return null;
        return DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc);
    }
}
