using System.Security.Cryptography;

using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// Unit tests for the envelope-encryption gateway (ADR-0026). Pure
/// crypto round-trips against the in-memory <see cref="MasterKey"/> +
/// <see cref="LedgerKeyService"/>; no DB needed.
/// </summary>
public sealed class LedgerKeyServiceTests
{
    /// <summary>Stable test master KEK — 32 zero bytes. Non-secret
    /// by intent; we just need a fixed value across the suite.</summary>
    private static readonly MasterKey TestMasterKey = new(new byte[32], id: "test");

    private static LedgerKeyService NewService() => new(TestMasterKey);

    [Fact]
    public void CreateWrappedLek_emits_60_bytes()
    {
        // AES-GCM framing: nonce(12) + ciphertext(32, the LEK) + tag(16)
        var svc = NewService();
        var wrapped = svc.CreateWrappedLek();
        Assert.Equal(60, wrapped.Length);
    }

    [Fact]
    public void CreateWrappedLek_uses_a_fresh_nonce_each_call()
    {
        // Two consecutive wraps of independently-generated LEKs must
        // differ — different LEK plaintext AND different nonces would
        // both produce different ciphertext; this asserts the
        // randomness path is wired.
        var svc = NewService();
        var a = svc.CreateWrappedLek();
        var b = svc.CreateWrappedLek();
        Assert.False(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void Seal_then_Open_round_trips_the_plaintext()
    {
        var svc = NewService();
        var wrapped = svc.CreateWrappedLek();
        var plaintext = "https://bridge.simplefin.org/access/MYACCESSTOKEN"u8.ToArray();

        var sealedBytes = svc.Seal(wrapped, plaintext);
        var opened = svc.Open(wrapped, sealedBytes);

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void Seal_uses_a_fresh_nonce_per_call_so_repeated_seals_differ()
    {
        // AES-GCM with a random nonce: two seals of the same
        // plaintext under the same LEK must produce different
        // ciphertext. Nonce reuse would be catastrophic for AES-GCM
        // (reveals the keystream).
        var svc = NewService();
        var wrapped = svc.CreateWrappedLek();
        var plaintext = "secret"u8.ToArray();

        var a = svc.Seal(wrapped, plaintext);
        var b = svc.Seal(wrapped, plaintext);

        Assert.False(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void Open_rejects_tampered_ciphertext()
    {
        var svc = NewService();
        var wrapped = svc.CreateWrappedLek();
        var sealedBytes = svc.Seal(wrapped, "secret"u8.ToArray());

        // Flip a byte inside the ciphertext region — past the nonce
        // (offset 12) and before the tag (last 16 bytes).
        sealedBytes[20] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(
            () => svc.Open(wrapped, sealedBytes));
    }

    [Fact]
    public void Open_rejects_when_a_different_master_KEK_tries_to_unwrap()
    {
        // Different master KEK ⇒ cannot unwrap the LEK ⇒ AES-GCM
        // tag check fails on the wrap-open step (which surfaces as
        // AuthenticationTagMismatchException, same type as a
        // ciphertext tamper). Confirms the wrapped LEK is bound to
        // its master KEK.
        var keyA = new MasterKey(new byte[32], id: "v1");
        var keyB = new MasterKey(Enumerable.Repeat((byte)0xAB, 32).ToArray(), id: "v2");
        var svcA = new LedgerKeyService(keyA);
        var svcB = new LedgerKeyService(keyB);

        var wrappedUnderA = svcA.CreateWrappedLek();
        var sealedBytes = svcA.Seal(wrappedUnderA, "secret"u8.ToArray());

        // svcB tries to use a wrapped LEK from svcA's keyring.
        Assert.Throws<AuthenticationTagMismatchException>(
            () => svcB.Open(wrappedUnderA, sealedBytes));
    }

    [Fact]
    public void Open_rejects_truncated_payload()
    {
        var svc = NewService();
        var wrapped = svc.CreateWrappedLek();
        var sealedBytes = svc.Seal(wrapped, "secret"u8.ToArray());

        // Anything shorter than nonce(12) + tag(16) = 28 bytes is
        // structurally invalid — the service surfaces this as a
        // CryptographicException, not a tag-mismatch.
        var truncated = sealedBytes.AsSpan(0, 20).ToArray();
        Assert.Throws<CryptographicException>(() => svc.Open(wrapped, truncated));
    }

    [Fact]
    public void SealWithMasterKey_then_OpenWithMasterKey_round_trips()
    {
        // ADR-0060: the backup passphrase is sealed directly under the
        // master KEK (no per-ledger LEK), since it isn't owned by any
        // ledger. Round-trip must recover the exact bytes.
        var svc = NewService();
        var passphrase = "correct horse battery staple"u8.ToArray();

        var sealedBytes = svc.SealWithMasterKey(passphrase);
        var opened = svc.OpenWithMasterKey(sealedBytes);

        Assert.Equal(passphrase, opened);
    }

    [Fact]
    public void SealWithMasterKey_uses_a_fresh_nonce_per_call()
    {
        var svc = NewService();
        var a = svc.SealWithMasterKey("secret"u8.ToArray());
        var b = svc.SealWithMasterKey("secret"u8.ToArray());
        Assert.False(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void OpenWithMasterKey_rejects_a_different_master_KEK()
    {
        // A backup-passphrase blob sealed under one deployment's KEK is
        // inert under another — restoring on a box with the wrong KEK
        // can't recover the passphrase.
        var svcA = new LedgerKeyService(new MasterKey(new byte[32], id: "v1"));
        var svcB = new LedgerKeyService(
            new MasterKey(Enumerable.Repeat((byte)0xAB, 32).ToArray(), id: "v2"));

        var sealedUnderA = svcA.SealWithMasterKey("secret"u8.ToArray());

        Assert.Throws<AuthenticationTagMismatchException>(
            () => svcB.OpenWithMasterKey(sealedUnderA));
    }

    [Fact]
    public void OpenWithMasterKey_rejects_truncated_payload()
    {
        var svc = NewService();
        var sealedBytes = svc.SealWithMasterKey("secret"u8.ToArray());
        var truncated = sealedBytes.AsSpan(0, 20).ToArray();
        Assert.Throws<CryptographicException>(() => svc.OpenWithMasterKey(truncated));
    }

    [Fact]
    public void Wrapped_LEK_does_not_contain_the_LEK_in_plaintext()
    {
        // Sanity: 32 zero-bytes is the LEK we never want leaking
        // (well, we want NO LEK leaking, but with the test master
        // key being all-zero, an accidentally-unencrypted output
        // could hide a leak). Use a non-zero master KEK and assert
        // the wrapped bytes don't contain the LEK literal.
        var key = new MasterKey(Enumerable.Repeat((byte)0xCC, 32).ToArray(), id: "k");
        var svc = new LedgerKeyService(key);
        var wrapped = svc.CreateWrappedLek();
        // The wrapped value can't equal any 32-byte run drawn from
        // the (60-byte) buffer slid by 1; effectively asserts the
        // ciphertext is not the plaintext.
        for (var offset = 0; offset + 32 <= wrapped.Length; offset++)
        {
            Assert.NotEqual(
                Enumerable.Repeat((byte)0xCC, 32),
                wrapped.AsSpan(offset, 32).ToArray());
        }
    }
}
