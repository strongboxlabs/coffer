using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Migrations;

internal sealed partial class LegNotesBackfill
{
    /// <summary>
    /// What this step writes, as migration 264 leaves it: the <c>notes_*</c> columns of
    /// <c>txn_legs</c> (with the leg's key and header, read only) and the rows of
    /// <c>txn_leg_note_tags</c>. Private to this step, so the application's model, and
    /// <see cref="LegNotes"/>' own store with it, can change after 264 without changing
    /// what this step writes on an upgrade that runs it before that change.
    /// </summary>
    private sealed class Store(DbContextOptions<Store> options) : DbContext(options)
    {
        public DbSet<StoredLeg> Legs => Set<StoredLeg>();
        public DbSet<StoredTag> Tags => Set<StoredTag>();

        public static Store On(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var db = new Store(new DbContextOptionsBuilder<Store>()
                .UseNpgsql(connection, o => o.MaxBatchSize(1000))
                .Options);
            db.Database.SetCommandTimeout(600);
            db.Database.UseTransaction(transaction);
            return db;
        }

        /// <summary>
        /// Store <paramref name="computed"/> for <paramref name="headerIds"/>: every leg's
        /// columns where they differ, and the headers' tag rows replaced by the computed
        /// ones, so a rerun after a partial run writes the same rows again.
        /// </summary>
        public async Task WriteAsync(
            IReadOnlyCollection<Guid> headerIds, IReadOnlyList<LegNotes.Computed> computed,
            CancellationToken cancellationToken)
        {
            var ids = headerIds.ToArray();
            var byLeg = computed.ToDictionary(c => c.Notes.LegId);
            var legIds = byLeg.Keys.ToList();

            foreach (var row in await Legs.Where(l => legIds.Contains(l.Id))
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
                row.Set(byLeg[row.Id].Notes);
            Tags.RemoveRange(await Tags.Where(t => ids.Contains(t.HeaderId) || legIds.Contains(t.LegId))
                .ToListAsync(cancellationToken).ConfigureAwait(false));
            await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ChangeTracker.Clear();

            Tags.AddRange(computed.SelectMany(c => c.Tags).Select(t => new StoredTag
            {
                LegId = t.LegId,
                TagId = t.TagId,
                HeaderId = t.HeaderId,
                LedgerId = t.LedgerId,
                Shown = t.Shown,
                InPlay = t.InPlay,
                Inherited = t.Inherited,
                TransactionLevel = t.TransactionLevel,
            }));
            await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ChangeTracker.Clear();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StoredLeg>(b =>
            {
                b.ToTable("txn_legs");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).HasColumnName("id");
                b.Property(x => x.HeaderId).HasColumnName("header_id");
                b.Property(x => x.Kind).HasColumnName("notes_kind").HasConversion<string>();
                b.Property(x => x.IsReadOnly).HasColumnName("notes_is_read_only");
                b.Property(x => x.GroupsByHeader).HasColumnName("notes_groups_by_header");
                b.Property(x => x.Memo).HasColumnName("notes_memo");
                b.Property(x => x.MemoInherited).HasColumnName("notes_memo_inherited");
                b.Property(x => x.InheritedMemo).HasColumnName("notes_inherited_memo");
                b.Property(x => x.InheritFromAccountId).HasColumnName("notes_inherit_from_account_id");
                b.Property(x => x.TagsInherited).HasColumnName("notes_tags_inherited");
                b.Property(x => x.HeaderMemo).HasColumnName("notes_header_memo");
                b.Property(x => x.HeaderInPlay).HasColumnName("notes_header_in_play");
            });
            modelBuilder.Entity<StoredTag>(b =>
            {
                b.ToTable("txn_leg_note_tags");
                b.HasKey(x => new { x.LegId, x.TagId });
                b.Property(x => x.LegId).HasColumnName("leg_id");
                b.Property(x => x.TagId).HasColumnName("tag_id");
                b.Property(x => x.HeaderId).HasColumnName("header_id");
                b.Property(x => x.LedgerId).HasColumnName("ledger_id");
                b.Property(x => x.Shown).HasColumnName("shown");
                b.Property(x => x.InPlay).HasColumnName("in_play");
                b.Property(x => x.Inherited).HasColumnName("inherited");
                b.Property(x => x.TransactionLevel).HasColumnName("transaction_level");
            });
        }
    }

    /// <summary>A leg's stored presentation columns, as 264 adds them.</summary>
    private sealed class StoredLeg
    {
        public Guid Id { get; init; }
        public Guid HeaderId { get; init; }
        public LegKind? Kind { get; set; }
        public bool? IsReadOnly { get; set; }
        public bool? GroupsByHeader { get; set; }
        public string? Memo { get; set; }
        public bool? MemoInherited { get; set; }
        public string? InheritedMemo { get; set; }
        public Guid? InheritFromAccountId { get; set; }
        public bool? TagsInherited { get; set; }
        public string? HeaderMemo { get; set; }
        public bool? HeaderInPlay { get; set; }

        public void Set(LegNotes.LegValues v)
        {
            Kind = v.Kind;
            IsReadOnly = v.IsReadOnly;
            GroupsByHeader = v.GroupsByHeader;
            Memo = v.Memo;
            MemoInherited = v.MemoInherited;
            InheritedMemo = v.InheritedMemo;
            InheritFromAccountId = v.InheritFromAccountId;
            TagsInherited = v.TagsInherited;
            HeaderMemo = v.HeaderMemo;
            HeaderInPlay = v.HeaderInPlay;
        }
    }

    /// <summary>A tag-role row, as 264 creates the table.</summary>
    private sealed class StoredTag
    {
        public Guid LegId { get; init; }
        public Guid TagId { get; init; }
        public Guid HeaderId { get; init; }
        public Guid LedgerId { get; init; }
        public bool Shown { get; init; }
        public bool InPlay { get; init; }
        public bool Inherited { get; init; }
        public bool TransactionLevel { get; init; }
    }
}
