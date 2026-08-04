using System.Text.Json;

namespace Coffer.Api.Backup.Drive;

/// <summary>
/// Serializes the Drive OAuth material to/from the byte blob that gets sealed
/// under the master KEK (ADR-0062 D3). One place so the connect path (which
/// seals) and the push path (which opens) agree on the JSON shape.
/// </summary>
internal static class DriveCredentialCodec
{
    public static byte[] Serialize(DriveCredentials c) =>
        JsonSerializer.SerializeToUtf8Bytes(new Blob(c.ClientId, c.ClientSecret, c.RefreshToken));

    public static DriveCredentials Deserialize(byte[] json)
    {
        var b = JsonSerializer.Deserialize<Blob>(json)
            ?? throw new DriveOAuthException("The stored Drive credentials are corrupt.");
        return new DriveCredentials(b.ClientId, b.ClientSecret, b.RefreshToken);
    }

    private sealed record Blob(string ClientId, string ClientSecret, string RefreshToken);
}
