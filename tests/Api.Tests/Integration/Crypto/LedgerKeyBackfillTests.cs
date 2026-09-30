using Microsoft.EntityFrameworkCore;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// Boot-time LEK backfill (<see cref="LedgersRepository.BackfillMissingWrappedLeksAsync"/>).
///
/// <para>Migration 035 shipped <c>ledgers.wrapped_lek</c> nullable and promised "a
/// subsequent migration sets NOT NULL once backfill is verified complete". No such
/// migration could exist — wrapping a LEK needs the master KEK and SQL does not have
/// it — so the backfill runs in the API at boot and the constraint has to follow in a
/// later release.</para>
///
/// <para>The NULLs come from the Moneydance importer, a separate binary with no KEK
/// that inserts <c>ledgers (id, name)</c> directly; the API's own
/// <c>CreateWithOwnerAsync</c> has wrapped one in the ledger's INSERT since
/// ADR-0026.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LedgerKeyBackfillTests
{
    private readonly PostgresFixture _fixture;

    public LedgerKeyBackfillTests(PostgresFixture fixture) => _fixture = fixture;

    private static LedgerKeyService Keys => new(new MasterKey(new byte[32], "v1"));

    // The backfill only uses the service factory; the other two are constructor
    // requirements of the repository as a whole.
    private LedgersRepository NewRepository() =>
        new(_fixture.NewDbContext(), _fixture.NewServiceFactory(), Keys);

    /// <summary>A ledger inserted the way the importer does it — name only.</summary>
    private async Task<Guid> InsertLekLessLedgerAsync()
    {
        var id = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = id,
            Name = $"importer-shaped-{id:N}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(byte[]? Wrapped, string? KekId)> ReadAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewDbContext();
        var row = await db.Ledgers.AsNoTracking().FirstAsync(l => l.Id == ledgerId);
        return (row.WrappedLek, row.LekKekId);
    }

    [Fact]
    public async Task Wraps_a_key_for_a_ledger_that_has_none_and_leaves_existing_keys_alone()
    {
        var lekLess = await InsertLekLessLedgerAsync();

        // A ledger that already has a key, to prove the backfill DISCRIMINATES.
        // Without this the method could rewrap everything on every boot — which
        // would be a silent key-rotation nobody asked for, and would break any
        // ciphertext sealed under the old LEK.
        var existing = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.Ledgers.Add(new LedgerRow
            {
                Id = existing,
                Name = $"already-keyed-{existing:N}",
                CreatedAt = DateTime.UtcNow,
                WrappedLek = Keys.CreateWrappedLek(),
                LekKekId = "pre-existing",
                LekCreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (beforeWrapped, beforeKekId) = await ReadAsync(existing);
        Assert.Null((await ReadAsync(lekLess)).Wrapped);

        var wrapped = await NewRepository().BackfillMissingWrappedLeksAsync(Keys);

        // At least the one we seeded — other tests in this collection may leave
        // their own LEK-less rows behind, so an exact count would be flaky.
        Assert.True(wrapped >= 1);

        var after = await ReadAsync(lekLess);
        Assert.NotNull(after.Wrapped);
        Assert.Equal(Keys.CurrentKekId, after.KekId);

        var untouched = await ReadAsync(existing);
        Assert.Equal(beforeWrapped, untouched.Wrapped);
        Assert.Equal(beforeKekId, untouched.KekId);
    }

    [Fact]
    public async Task Running_it_twice_wraps_nothing_the_second_time()
    {
        // It runs on EVERY boot, so "idempotent" is not a nicety — a second pass
        // that rewrapped would rotate every ledger's key on every restart.
        await InsertLekLessLedgerAsync();

        var repo = NewRepository();
        var first = await repo.BackfillMissingWrappedLeksAsync(Keys);
        Assert.True(first >= 1);

        var second = await repo.BackfillMissingWrappedLeksAsync(Keys);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Each_ledger_gets_its_OWN_key_rather_than_a_shared_one()
    {
        // Generating one wrapped LEK outside the loop and reusing it would pass
        // every other assertion here while giving every ledger the same key —
        // which is the whole thing per-ledger keys exist to prevent.
        var a = await InsertLekLessLedgerAsync();
        var b = await InsertLekLessLedgerAsync();

        await NewRepository().BackfillMissingWrappedLeksAsync(Keys);

        var wrappedA = (await ReadAsync(a)).Wrapped;
        var wrappedB = (await ReadAsync(b)).Wrapped;

        Assert.NotNull(wrappedA);
        Assert.NotNull(wrappedB);
        Assert.False(wrappedA!.SequenceEqual(wrappedB!));
    }
}
