using Npgsql;

using Coffer.Api.Crypto;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// The lock every start that may mint a master key takes (<see cref="MasterKeyMintLock"/>).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MasterKeyMintLockTests : DatabaseTest
{
    public MasterKeyMintLockTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_held_lock_does_not_block_the_restore_swap_of_the_database_it_guards()
    {
        // A first start holds the lock while it applies a staged restore, and the restore
        // swaps the new database in with ALTER DATABASE ... RENAME, which Postgres refuses
        // while any session is connected to the database being renamed. Done here as the
        // restore does it: pools cleared, then the renames from the maintenance database.
        using var mintLock = MasterKeyMintLock.Acquire(Db.ServiceConnectionString, TimeSpan.FromSeconds(30));
        Assert.True(mintLock.IsStillHeld());
        TestDatabaseCluster.ClearPool(Db.ServiceConnectionString);
        TestDatabaseCluster.ClearPool(Db.AppConnectionString);
        TestDatabaseCluster.ClearPool(Db.SuperuserConnectionString);

        var maintenance = new NpgsqlConnectionStringBuilder(Db.ServiceConnectionString)
        {
            Database = MasterKeyMintLock.MaintenanceDatabase,
            Pooling = false,
        }.ConnectionString;
        var moved = $"{Db.Name}_swapped";
        await using (var connection = new NpgsqlConnection(maintenance))
        {
            await connection.OpenAsync();
            await using (var away = new NpgsqlCommand($"ALTER DATABASE \"{Db.Name}\" RENAME TO \"{moved}\"", connection))
                await away.ExecuteNonQueryAsync();
            await using (var back = new NpgsqlCommand($"ALTER DATABASE \"{moved}\" RENAME TO \"{Db.Name}\"", connection))
                await back.ExecuteNonQueryAsync();
        }

        Assert.True(mintLock.IsStillHeld());
    }

    [Fact]
    public async Task Two_starts_on_one_database_wait_on_each_other_and_another_database_does_not()
    {
        using var held = MasterKeyMintLock.Acquire(Db.ServiceConnectionString, TimeSpan.FromSeconds(30));

        Assert.Throws<TimeoutException>(
            () => MasterKeyMintLock.Acquire(Db.ServiceConnectionString, TimeSpan.FromSeconds(1)));

        var sibling = await Db.CreateSiblingAsync();
        using var other = MasterKeyMintLock.Acquire(sibling.ServiceConnectionString, TimeSpan.FromSeconds(5));
        Assert.True(other.IsStillHeld());
        Assert.NotEqual(held.Key, other.Key);
    }
}
