using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Migrations;

/// <summary>
/// The upgrade step that fills the stored memo-and-tags presentation (migration 264)
/// for existing data: the C# counterpart of a data migration, because the rule it
/// stores is defined in C# only and a SQL copy of it is not allowed.
/// </summary>
/// <remarks>
/// <para><b>When it runs.</b> DbUp runs it once, journaled as <see cref="Name"/>, which
/// sorts directly after <see cref="AfterScript"/> and before any later migration
/// (<see cref="MigrationRunner.CSharpSteps"/>); a failure stops the upgrade and journals
/// nothing, so the next start runs it again.</para>
/// <para><b>What it does.</b> Every header of every ledger, <see cref="BatchSize"/>
/// headers per transaction in id order: it reads the rule's inputs, computes what the
/// rule produces (<see cref="LegNotes.ComputeFrom"/>) and stores it. Each batch stores
/// what it computes, so a rerun after a partial run converges to the same values. It
/// reads only stored rows, so it is deterministic. It updates each leg once, so
/// <c>txn_legs</c> is left with a dead version of each row for the autovacuum to
/// reclaim; nothing compacts the table, which would take an exclusive lock.</para>
/// <para><b>Frozen at 264, both ways.</b> It reads through <see cref="Projection"/>, a
/// model private to this step that maps only the columns the rule reads, and writes
/// through <see cref="Store"/>, a model private to this step that maps the stored
/// presentation exactly as migration 264 creates it; never through the application's
/// model. An upgrade runs this step directly after 264 and before every later
/// migration, so a later change to a mapped table, the stored columns or the tag-role
/// table included, cannot break an upgrade that spans it (<c>StoredNotesWriteCatalogTests</c>
/// holds both models to 264's columns).</para>
/// <para>DbUp executes the text this returns; the work is done before it returns, so the
/// text is a no-op statement.</para>
/// </remarks>
internal sealed partial class LegNotesBackfill : MigrationRunner.RecomputeStep
{
    /// <summary>The journal name.</summary>
    public const string Name = "264_stored_leg_notes_backfill";

    /// <summary>The SQL script that adds the columns and the table this step fills, and that it directly follows.</summary>
    public const string AfterScript = "264_annotation_data.sql";

    /// <summary>Headers per transaction.</summary>
    public const int BatchSize = 2000;

    private readonly string _connectionString;
    private readonly ILogger _logger;

    public LegNotesBackfill(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public override string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        if (!Computes)
        {
            _logger.LogInformation("Stored notes not computed here: a later recompute step of this upgrade computes them.");
            return "SELECT 1;";
        }
        var headers = RunAsync(_connectionString, ledgerId: null, CancellationToken.None).GetAwaiter().GetResult();
        _logger.LogInformation("Stored notes computed for {Headers} transaction(s).", headers);
        return "SELECT 1;";
    }

    /// <summary>
    /// The step's work over every ledger or one: what the upgrade runs, and what a seed
    /// that writes past the API runs over its ledger (or over the headers it wrote), so
    /// what a seed stores is what the upgrade stores. Returns the headers done.
    /// </summary>
    internal static async Task<int> RunAsync(
        string connectionString, Guid? ledgerId, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? headerIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(connection, ledgerId, cancellationToken, headerIds).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="RunAsync(string, Guid?, CancellationToken, IReadOnlyCollection{Guid}?)"/> on
    /// an open connection that holds no transaction: for a test that writes its rows on that
    /// connection.
    /// </summary>
    internal static async Task<int> RunAsync(
        NpgsqlConnection connection, Guid? ledgerId, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? headerIds = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<Guid> all;
        await using (var read = Projection.On(connection))
        {
            var query = read.Headers.AsNoTracking();
            if (ledgerId is { } ledger) query = query.Where(h => h.LedgerId == ledger);
            if (headerIds is not null)
            {
                var only = headerIds.ToArray();
                query = query.Where(h => only.Contains(h.Id));
            }
            all = await query.OrderBy(h => h.Id).Select(h => h.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var batch in all.Chunk(BatchSize))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var read = Projection.On(connection, transaction))
            {
                var (inputs, holdingsOf) = await ReadAsync(read, batch, cancellationToken).ConfigureAwait(false);
                var computed = LegNotes.ComputeFrom(inputs, holdingsOf);
                await using var store = Store.On(connection, transaction);
                await store.WriteAsync(batch, computed, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return all.Count;
    }

    /// <summary>The rule's inputs for <paramref name="ids"/>, from the stored rows.</summary>
    private static async Task<(List<LegNotes.HeaderInput> Inputs, Dictionary<Guid, Guid> HoldingsOf)> ReadAsync(
        Projection db, Guid[] ids, CancellationToken cancellationToken)
    {
        var headers = await db.Headers.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.LedgerId, h.Action, h.OriginatingAccountId, h.Memo })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var legs = await db.Legs.AsNoTracking()
            .Where(l => ids.Contains(l.HeaderId))
            .Select(l => new { l.Id, l.HeaderId, l.AccountId, l.PostingIndex, l.LegMemo })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var headerTags = (await db.HeaderTags.AsNoTracking()
                .Where(t => ids.Contains(t.HeaderId))
                .Select(t => new { t.HeaderId, t.TagId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(t => t.HeaderId, t => t.TagId);
        var legIds = legs.Select(l => l.Id).ToList();
        var legTags = (await db.LegTags.AsNoTracking()
                .Where(t => legIds.Contains(t.LegId))
                .Select(t => new { t.LegId, t.TagId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(t => t.LegId, t => t.TagId);
        var accountIds = legs.Select(l => l.AccountId)
            .Concat(headers.Where(h => h.OriginatingAccountId != null).Select(h => h.OriginatingAccountId!.Value))
            .Distinct().ToList();
        var holdingsOf = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.HoldingsAccountId != null)
            .Select(a => new { a.Id, Holdings = a.HoldingsAccountId!.Value })
            .ToDictionaryAsync(a => a.Id, a => a.Holdings, cancellationToken)
            .ConfigureAwait(false);

        var legsOf = legs.ToLookup(l => l.HeaderId);
        var inputs = headers.Select(h => new LegNotes.HeaderInput(
                new NotesHeaderInput(
                    h.Id, h.Action, h.OriginatingAccountId, h.Memo,
                    [.. headerTags[h.Id]],
                    [.. legsOf[h.Id].Select(l => new NotesLegInput(l.Id, l.AccountId, l.PostingIndex, l.LegMemo, [.. legTags[l.Id]]))]),
                h.LedgerId))
            .ToList();
        return (inputs, holdingsOf);
    }

    /// <summary>
    /// The rows this step reads, as migration 264 leaves them: only the columns the
    /// rule reads, read only. Private to this step, so the application's model can change
    /// without changing what an older upgrade reads.
    /// </summary>
    private sealed class Projection(DbContextOptions<Projection> options) : DbContext(options)
    {
        public DbSet<HeaderRow> Headers => Set<HeaderRow>();
        public DbSet<LegRow> Legs => Set<LegRow>();
        public DbSet<HeaderTagRow> HeaderTags => Set<HeaderTagRow>();
        public DbSet<LegTagRow> LegTags => Set<LegTagRow>();
        public DbSet<AccountRow> Accounts => Set<AccountRow>();

        public static Projection On(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
        {
            var db = new Projection(new DbContextOptionsBuilder<Projection>().UseNpgsql(connection).Options);
            db.Database.SetCommandTimeout(600);
            if (transaction is not null) db.Database.UseTransaction(transaction);
            return db;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HeaderRow>(b =>
            {
                b.ToTable("txn_headers");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).HasColumnName("id");
                b.Property(x => x.LedgerId).HasColumnName("ledger_id");
                b.Property(x => x.Action).HasColumnName("action");
                b.Property(x => x.OriginatingAccountId).HasColumnName("originating_account_id");
                b.Property(x => x.Memo).HasColumnName("memo");
            });
            modelBuilder.Entity<LegRow>(b =>
            {
                b.ToTable("txn_legs");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).HasColumnName("id");
                b.Property(x => x.HeaderId).HasColumnName("header_id");
                b.Property(x => x.AccountId).HasColumnName("account_id");
                b.Property(x => x.PostingIndex).HasColumnName("posting_index");
                b.Property(x => x.LegMemo).HasColumnName("leg_memo");
            });
            modelBuilder.Entity<HeaderTagRow>(b =>
            {
                b.ToTable("txn_header_tags");
                b.HasNoKey();
                b.Property(x => x.HeaderId).HasColumnName("header_id");
                b.Property(x => x.TagId).HasColumnName("tag_id");
            });
            modelBuilder.Entity<LegTagRow>(b =>
            {
                b.ToTable("txn_leg_tags");
                b.HasNoKey();
                b.Property(x => x.LegId).HasColumnName("leg_id");
                b.Property(x => x.TagId).HasColumnName("tag_id");
            });
            modelBuilder.Entity<AccountRow>(b =>
            {
                b.ToTable("accounts");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).HasColumnName("id");
                b.Property(x => x.HoldingsAccountId).HasColumnName("holdings_account_id");
            });
        }
    }

    private sealed class HeaderRow
    {
        public Guid Id { get; init; }
        public Guid LedgerId { get; init; }
        public string? Action { get; init; }
        public Guid? OriginatingAccountId { get; init; }
        public string? Memo { get; init; }
    }

    private sealed class LegRow
    {
        public Guid Id { get; init; }
        public Guid HeaderId { get; init; }
        public Guid AccountId { get; init; }
        public int PostingIndex { get; init; }
        public string? LegMemo { get; init; }
    }

    private sealed class HeaderTagRow
    {
        public Guid HeaderId { get; init; }
        public Guid TagId { get; init; }
    }

    private sealed class LegTagRow
    {
        public Guid LegId { get; init; }
        public Guid TagId { get; init; }
    }

    private sealed class AccountRow
    {
        public Guid Id { get; init; }
        public Guid? HoldingsAccountId { get; init; }
    }
}
