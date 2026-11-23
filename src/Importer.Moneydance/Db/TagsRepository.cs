using Dapper;
using Npgsql;

using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>The tag names for one header (<paramref name="IsHeader"/>) or one leg of an import batch.</summary>
public sealed record ImportTagSet(Guid TargetId, bool IsHeader, IReadOnlyList<string> Names);

/// <summary>
/// Dapper-backed gateway to <c>tags</c>, <c>txn_header_tags</c> and
/// <c>txn_leg_tags</c>. Which header or leg a Moneydance tag lands on is the
/// mappers' plan (<see cref="Mappers.MoneydanceNotes"/>); this persists it.
/// </summary>
public sealed class TagsRepository
{
    private readonly NpgsqlConnection _connection;

    public TagsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Resolve tag names to ids within a ledger in a single round trip, creating
    /// the ones that do not exist. Returns an id for every given name (after
    /// <see cref="TagName.Normalize"/>), keyed by that name.
    /// </summary>
    /// <remarks>
    /// <para><b>One key.</b> Two names are the same tag when the database's
    /// <c>tag_name_key</c> says so (case and surrounding white space folded), the
    /// one definition every name lookup shares; nothing here folds a name.</para>
    /// <para><b>Which tag.</b> A key matching an existing tag resolves to it (the
    /// earliest created, then the lowest id, should several ever share it). A new key
    /// is created once, spelled as its first occurrence in <paramref name="names"/>.</para>
    /// <para><b>Conflict target.</b> The per-ledger unique index on the key
    /// (<c>uq_tags_name_key_per_ledger</c>, migration 264): a name another writer
    /// created in between, in any spelling, is not inserted again, and is read back
    /// by its key in a second statement, which sees it.</para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, Guid>> EnsureTagsAsync(
        Guid ledgerId,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        // Distinct AFTER normalizing: two source names that truncate to the
        // same 64 characters are one name from here on. Order is kept, because
        // it decides the spelling a new tag is created with.
        var normalized = new List<string>(names.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (TagName.Normalize(name) is { } n && seen.Add(n))
                normalized.Add(n);
        if (normalized.Count == 0)
            return new Dictionary<string, Guid>(StringComparer.Ordinal);

        const string sql = """
            WITH input AS (
                SELECT n.name, n.ord, public.tag_name_key(n.name) AS key
                  FROM unnest(@names::text[]) WITH ORDINALITY AS n(name, ord)
            ),
            wanted AS (
                SELECT DISTINCT ON (i.key) i.key, i.name
                  FROM input i
                 ORDER BY i.key, i.ord
            ),
            existing AS (
                SELECT DISTINCT ON (public.tag_name_key(t.name)) public.tag_name_key(t.name) AS key, t.id
                  FROM tags t
                 WHERE t.ledger_id = @ledgerId
                   AND public.tag_name_key(t.name) IN (SELECT w.key FROM wanted w)
                 ORDER BY public.tag_name_key(t.name), t.created_at, t.id
            ),
            inserted AS (
                INSERT INTO tags (id, ledger_id, name)
                SELECT gen_random_uuid(), @ledgerId, w.name
                  FROM wanted w
                 WHERE NOT EXISTS (SELECT 1 FROM existing e WHERE e.key = w.key)
                ON CONFLICT (ledger_id, (public.tag_name_key(name))) DO NOTHING
                RETURNING id, name
            ),
            resolved AS (
                SELECT e.key, e.id FROM existing e
                UNION ALL
                SELECT public.tag_name_key(n.name), n.id FROM inserted n
            )
            SELECT i.name, r.id
              FROM input i
              JOIN resolved r ON r.key = i.key;
            """;

        var command = new CommandDefinition(sql,
            new { ledgerId, names = normalized.ToArray() }, cancellationToken: cancellationToken);
        var rows = await _connection.QueryAsync<(string Name, Guid Id)>(command).ConfigureAwait(false);
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows)
            result[row.Name] = row.Id;

        // A name whose insert met the key index (created by another writer after this
        // statement's snapshot) is read back by key; this statement sees it.
        var unresolved = normalized.Where(n => !result.ContainsKey(n)).ToArray();
        if (unresolved.Length > 0)
        {
            var reread = new CommandDefinition("""
                SELECT n.name, t.id
                  FROM unnest(@names::text[]) AS n(name)
                  JOIN LATERAL (
                        SELECT x.id FROM tags x
                         WHERE x.ledger_id = @ledgerId AND public.tag_name_key(x.name) = public.tag_name_key(n.name)
                         ORDER BY x.created_at, x.id
                         LIMIT 1) t ON TRUE;
                """, new { ledgerId, names = unresolved }, cancellationToken: cancellationToken);
            foreach (var row in await _connection.QueryAsync<(string Name, Guid Id)>(reread).ConfigureAwait(false))
                result[row.Name] = row.Id;
        }

        // Fail closed: a name with no id would drop its links silently.
        if (normalized.FirstOrDefault(n => !result.ContainsKey(n)) is { } missing)
            throw new InvalidOperationException(
                $"The tag name '{missing}' resolved to no tag in ledger {ledgerId}; the import is stopped.");
        return result;
    }

    /// <summary>
    /// Link each header and leg of an import batch to its tag names, creating the tags
    /// that do not exist yet. <paramref name="sets"/> is in the batch's encounter order,
    /// which decides the spelling a new name is created with.
    /// </summary>
    /// <remarks>
    /// Fails closed: a name that resolves to no tag stops the import instead of
    /// silently dropping the link.
    /// </remarks>
    public async Task LinkImportedAsync(
        Guid ledgerId,
        IReadOnlyList<ImportTagSet> sets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
            foreach (var name in set.Names)
                if (seen.Add(name)) names.Add(name);
        if (names.Count == 0) return;

        var tagIdByName = await EnsureTagsAsync(ledgerId, names, cancellationToken).ConfigureAwait(false);

        Guid IdOf(string name) => TagName.Normalize(name) is { } normalized && tagIdByName.TryGetValue(normalized, out var id)
            ? id
            : throw new InvalidOperationException($"The tag name '{name}' resolved to no tag; the import is stopped.");

        var headerIds = new HashSet<Guid>();
        var headerLinks = new List<(Guid HeaderId, Guid TagId)>();
        var legIds = new HashSet<Guid>();
        var legLinks = new List<(Guid LegId, Guid TagId)>();
        foreach (var set in sets)
        {
            if (set.Names.Count == 0) continue;
            if (set.IsHeader)
            {
                headerIds.Add(set.TargetId);
                foreach (var name in set.Names) headerLinks.Add((set.TargetId, IdOf(name)));
            }
            else
            {
                legIds.Add(set.TargetId);
                foreach (var name in set.Names) legLinks.Add((set.TargetId, IdOf(name)));
            }
        }

        await BulkSetTagsAsync(ledgerId, headerIds, headerLinks, cancellationToken).ConfigureAwait(false);
        await BulkSetLegTagsAsync(ledgerId, legIds, legLinks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replace the per-header tag links wholesale: delete existing
    /// links for every supplied header id and insert the new ones.
    /// Two bulk statements regardless of input size.
    /// </summary>
    public async Task BulkSetTagsAsync(
        Guid ledgerId,
        IReadOnlyCollection<Guid> headerIdsToReset,
        IReadOnlyCollection<(Guid HeaderId, Guid TagId)> links,
        CancellationToken cancellationToken = default)
    {
        if (headerIdsToReset.Count > 0)
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM txn_header_tags WHERE header_id = ANY(@ids);",
                new { ids = headerIdsToReset.ToArray() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (links.Count == 0) return;

        var hdrIds = new Guid[links.Count];
        var tagIds = new Guid[links.Count];
        var i = 0;
        foreach (var link in links)
        {
            hdrIds[i] = link.HeaderId;
            tagIds[i] = link.TagId;
            i++;
        }

        const string sql = """
            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id)
            SELECT hdr, tag, @LedgerId
              FROM unnest(@HdrIds::uuid[], @TagIds::uuid[]) AS u(hdr, tag)
            ON CONFLICT DO NOTHING;
            """;
        var command = new CommandDefinition(sql,
            new { HdrIds = hdrIds, TagIds = tagIds, LedgerId = ledgerId },
            cancellationToken: cancellationToken);
        await _connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Replace the per-LEG tag links wholesale (ADR-0103, mig 243) — the
    /// posting-level counterpart of <see cref="BulkSetTagsAsync"/>. Two bulk
    /// statements regardless of input size.
    /// </summary>
    public async Task BulkSetLegTagsAsync(
        Guid ledgerId,
        IReadOnlyCollection<Guid> legIdsToReset,
        IReadOnlyCollection<(Guid LegId, Guid TagId)> links,
        CancellationToken cancellationToken = default)
    {
        if (legIdsToReset.Count > 0)
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM txn_leg_tags WHERE leg_id = ANY(@ids);",
                new { ids = legIdsToReset.ToArray() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (links.Count == 0) return;

        var legIds = new Guid[links.Count];
        var tagIds = new Guid[links.Count];
        var i = 0;
        foreach (var link in links)
        {
            legIds[i] = link.LegId;
            tagIds[i] = link.TagId;
            i++;
        }

        const string sql = """
            INSERT INTO txn_leg_tags (leg_id, tag_id, ledger_id)
            SELECT leg, tag, @LedgerId
              FROM unnest(@LegIds::uuid[], @TagIds::uuid[]) AS u(leg, tag)
            ON CONFLICT DO NOTHING;
            """;
        var command = new CommandDefinition(sql,
            new { LegIds = legIds, TagIds = tagIds, LedgerId = ledgerId },
            cancellationToken: cancellationToken);
        await _connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    public async Task<int> CountTagsAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM tags;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }
}
