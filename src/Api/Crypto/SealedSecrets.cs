namespace Coffer.Api.Crypto;

/// <summary>
/// What the master key seals, as the operator-facing messages name it: one wording, so the
/// refusal, the minted-key warning, the adoption output and the restore error cannot drift
/// apart. <c>MasterKeySealedColumnsTests</c> holds the columns behind it; FU-102 is to
/// derive both from one catalog.
/// </summary>
public static class SealedSecrets
{
    /// <summary>Every secret lost with the master key, as a phrase.</summary>
    public const string Described =
        "the bank-feed (SimpleFIN) tokens, the stored backup passphrase, the Google Drive "
        + "connection, a private fork's upgrade repository token, the notification targets' URLs "
        + "and tokens, and the OAuth server keys MCP sign-in uses";
}
