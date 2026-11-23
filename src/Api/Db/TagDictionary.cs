using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// The ledger's tag dictionary: names in, ids out, created on first use.
/// </summary>
/// <remarks>
/// <para>Two names are the same tag when their <c>tag_name_key</c> is equal (migration
/// 263): lower() of the name with leading and trailing white space removed, under the
/// database collation. The key is computed in SQL, for the request's names and for the
/// ledger's tags alike, so no name is ever folded in .NET; a fold that disagreed with
/// the database's would let a name match here and not there.</para>
///
/// <para>Separate from the annotation writer (ADR-0103). Resolving a name against
/// the ledger's vocabulary is the same operation whether the tag is about to be
/// attached to a transaction or to one posting.</para>
/// </remarks>
internal static class TagDictionary
{
    /// <summary>
    /// Resolve tag NAMES to their tags in the ledger's dictionary, creating a row on
    /// first use (tracked; INSERTed on the next SaveChanges). Each name is trimmed and
    /// an empty one is skipped; the result maps every remaining trimmed name to its
    /// tag id.
    /// </summary>
    /// <remarks>
    /// <para>One round trip keys every requested name, and every name this unit of work
    /// has added but not saved, and finds the ledger's tags with a matching key.</para>
    ///
    /// <para>Names that share a key resolve to one tag. A new tag takes the first
    /// spelling in input order. The per-ledger unique index on the key (migration 264)
    /// keeps a second tag of one key from existing; the order below (earliest created,
    /// then lowest id) is the one the upgrade's merge kept.</para>
    /// <para>Two units of work that create one new name at the same time both add it;
    /// the index refuses the later save, and <see cref="AppDbContext"/> then re-reads
    /// it (<see cref="ReResolvePendingAsync"/>) and saves once more.</para>
    ///
    /// <para>Resolve ONCE per unit of work where you can. Tracker-pending rows from an
    /// earlier call in the same unit of work are found too, so two calls that introduce
    /// the same brand-new name still land on ONE dictionary row.</para>
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, Guid>> ResolveAsync(
        AppDbContext db,
        Guid ledgerId,
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var requested = names
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (requested.Count == 0) return result;

        var pending = db.ChangeTracker.Entries<TagRow>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Where(t => t.LedgerId == ledgerId)
            .ToList();
        var probe = requested.Concat(pending.Select(p => p.Name)).Distinct(StringComparer.Ordinal).ToArray();

        var rows = await (
                from k in db.TagNameKeys(probe)
                from t in db.Tags
                    .Where(t => t.LedgerId == ledgerId && AppDbContext.TagNameKey(t.Name) == k.Key)
                    .DefaultIfEmpty()
                orderby k.Key, t!.CreatedAt, t.Id
                select new { k.Name, k.Key, TagId = (Guid?)t.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var tagOfKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            keyOf.TryAdd(row.Name, row.Key);
            // Rows arrive in the database's pick order within each key.
            if (row.TagId is { } id) tagOfKey.TryAdd(row.Key, id);
        }
        foreach (var tag in pending)
            if (keyOf.TryGetValue(tag.Name, out var key)) tagOfKey.TryAdd(key, tag.Id);

        foreach (var name in requested)
        {
            var key = keyOf[name];
            if (!tagOfKey.TryGetValue(key, out var id))
            {
                var fresh = new TagRow
                {
                    Id = Guid.NewGuid(),
                    LedgerId = ledgerId,
                    Name = name, // the first spelling a request used
                };
                db.Tags.Add(fresh);
                tagOfKey[key] = id = fresh.Id;
            }
            result[name] = id;
        }
        return result;
    }

    /// <summary>
    /// A save refused because a tag this unit of work was creating already exists: the
    /// unique index on the name key (<c>uq_tags_name_key_per_ledger</c>, migration 264),
    /// or the exact-name index it implies, which the database may check first.
    /// </summary>
    internal static bool IsNameConflict(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            ConstraintName: NameKeyIndex or NameIndex,
        };

    /// <summary>The per-ledger unique index on the name key (migration 264).</summary>
    internal const string NameKeyIndex = "uq_tags_name_key_per_ledger";

    /// <summary>The per-ledger unique index on the exact name (migration 014).</summary>
    internal const string NameIndex = "uq_tags_name_per_ledger";

    /// <summary>
    /// After a save lost a race for a new tag's name: re-resolve every tag this unit of
    /// work was creating by its key, and where another writer has since committed a tag
    /// with that key, use that tag instead: every pending link to the new tag is moved
    /// to it and the new tag is no longer created. True when anything changed, so the
    /// caller saves again (once).
    /// </summary>
    /// <remarks>
    /// <para>Two requests that resolve one new name at the same time both create it;
    /// the unique index lets one in and refuses the other's save. Re-reading turns that
    /// refusal into the outcome a slower request would have had: the second request
    /// links the tag the first created. The key is the database's (tag_name_key), so
    /// the re-read finds a tag spelled differently that shares it.</para>
    /// <para>Only links still waiting to be inserted are moved: a link already in the
    /// database cannot name a tag that was never saved.</para>
    /// </remarks>
    internal static async Task<bool> ReResolvePendingAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var fresh = db.ChangeTracker.Entries<TagRow>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();
        var changed = false;
        foreach (var ledger in fresh.GroupBy(t => t.LedgerId))
        {
            var ledgerId = ledger.Key;
            var names = ledger.Select(t => t.Name).Distinct(StringComparer.Ordinal).ToArray();
            var rows = await (
                    from k in db.TagNameKeys(names)
                    from t in db.Tags
                        .Where(t => t.LedgerId == ledgerId && AppDbContext.TagNameKey(t.Name) == k.Key)
                    orderby k.Key, t.CreatedAt, t.Id
                    select new { k.Name, TagId = t.Id })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var row in rows) existing.TryAdd(row.Name, row.TagId);

            foreach (var tag in ledger)
            {
                if (!existing.TryGetValue(tag.Name, out var winner) || winner == tag.Id) continue;
                AnnotationWriter.RepointPendingTag(db, tag.Id, winner);
                db.Entry(tag).State = EntityState.Detached;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// <see cref="ResolveAsync"/> for callers that need only the set of tags the
    /// names name.
    /// </summary>
    public static async Task<HashSet<Guid>> ResolveIdsAsync(
        AppDbContext db,
        Guid ledgerId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(db, ledgerId, tags, cancellationToken).ConfigureAwait(false);
        return resolved.Values.ToHashSet();
    }
}
