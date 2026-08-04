using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

[Collection(ApiCollection.Name)]
public sealed class CredentialsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public CredentialsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static byte[] FreshBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    [Fact]
    public async Task Insert_round_trips_every_field()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());

        var credentialId = FreshBytes(64);
        var publicKey = FreshBytes(77);
        var transports = new[] { "usb", "nfc" };
        var aaguid = Guid.NewGuid();

        var inserted = await repo.InsertAsync(
            userId: ledger.UserId,
            credentialId: credentialId,
            publicKey: publicKey,
            signatureCounter: 0,
            aaguid: aaguid,
            transports: transports,
            nickname: "YubiKey 5C (daily)",
            rpId: "coffer.example");

        Assert.NotEqual(Guid.Empty, inserted.Id);
        Assert.Equal(ledger.UserId, inserted.UserId);
        Assert.Equal(credentialId, inserted.CredentialId);
        Assert.Equal(publicKey,    inserted.PublicKey);
        Assert.Equal(0,            inserted.SignatureCounter);
        Assert.Equal(aaguid,       inserted.Aaguid);
        Assert.Equal(transports,   inserted.Transports);
        Assert.Equal("YubiKey 5C (daily)", inserted.Nickname);
        Assert.Equal("coffer.example", inserted.RpId);
        Assert.Null(inserted.LastUsedAt);

        var byCredentialId = await repo.GetByCredentialIdAsync(credentialId);
        Assert.NotNull(byCredentialId);
        Assert.Equal(inserted.Id, byCredentialId!.Id);
        Assert.Equal("coffer.example", byCredentialId.RpId);
    }

    [Fact]
    public async Task DeleteOwn_last_credential_is_refused_unless_allowLast()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var only = await ledger.AddCredentialAsync();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());

        // Guarded: the user's last credential can't be removed...
        Assert.Equal(CredentialDeleteResult.WasLastCredential,
            await repo.DeleteOwnAsync(only.Id, ledger.UserId, allowLast: false));

        // ...unless the caller has confirmed a fallback login path exists.
        Assert.Equal(CredentialDeleteResult.Deleted,
            await repo.DeleteOwnAsync(only.Id, ledger.UserId, allowLast: true));
    }

    [Fact]
    public async Task GetByUser_returns_every_credential_owned_by_the_user()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());

        for (var i = 0; i < 3; i++)
        {
            await repo.InsertAsync(
                userId: ledger.UserId,
                credentialId: FreshBytes(64),
                publicKey: FreshBytes(77),
                signatureCounter: 0,
                aaguid: null,
                transports: null,
                nickname: $"key-{i}");
        }

        var rows = await repo.GetByUserAsync(ledger.UserId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(ledger.UserId, r.UserId));
    }

    [Fact]
    public async Task GetByCredentialId_returns_null_when_not_present()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());

        Assert.Null(await repo.GetByCredentialIdAsync(FreshBytes(64)));
    }

    [Fact]
    public async Task Insert_rejects_duplicate_credential_id_across_any_user()
    {
        // The unique constraint on credential_id is global — replay-attack
        // mitigation requires that the same FIDO2 credential never lives
        // on two users, even across distinct ledgers. The ledger isolation
        // doesn't relax this; the index is on the bare column, no per-ledger
        // scope.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());
        var sharedCredId = FreshBytes(64);

        await repo.InsertAsync(
            userId: ledger.UserId,
            credentialId: sharedCredId,
            publicKey: FreshBytes(77),
            signatureCounter: 0,
            aaguid: null, transports: null,
            nickname: "first");

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => repo.InsertAsync(
            userId: ledger.UserId,
            credentialId: sharedCredId,
            publicKey: FreshBytes(77),
            signatureCounter: 0,
            aaguid: null, transports: null,
            nickname: "second"));
        Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
    }

    [Fact]
    public async Task UpdateAfterAssertion_bumps_counter_and_last_used_at()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var inserted = await ledger.AddCredentialAsync();
        await using var db = _fixture.NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());

        await repo.UpdateAfterAssertionAsync(inserted.Id, newSignatureCounter: 7);

        var refreshed = await repo.GetByCredentialIdAsync(inserted.CredentialId);
        Assert.Equal(7, refreshed!.SignatureCounter);
        Assert.NotNull(refreshed.LastUsedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Insert_rejects_blank_nickname(string nickname)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => repo.InsertAsync(
            userId: ledger.UserId,
            credentialId: FreshBytes(64),
            publicKey: FreshBytes(77),
            signatureCounter: 0,
            aaguid: null, transports: null,
            nickname: nickname));
    }
}
