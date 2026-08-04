using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to <c>tags</c> and <c>txn_header_tags</c>. Tags are
/// modelled at the transaction level (per ADR-0009); the importer aggregates
/// any per-split tags up to the parent transaction during the
/// <see cref="Mappers.TransactionMapper"/> step.
/// </summary>
public sealed class TagsRepository
{
    private readonly NpgsqlConnection _connection;

    public TagsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Resolve a tag by name within a ledger, inserting it if absent. Returns
    /// the tag's id. Idempotent across re-runs because
    /// <c>(ledger_id, name)</c> is unique per ADR-0020 Phase A.
    /// </summary>
    public async Task<Guid> EnsureTagAsync(Guid ledgerId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name must not be empty.", nameof(name));

        const string sql = """
            INSERT INTO tags (id, ledger_id, name) VALUES (gen_random_uuid(), @ledgerId, @name)
            ON CONFLICT (ledger_id, name) DO UPDATE SET name = EXCLUDED.name
            RETURNING id;
            """;
        var command = new CommandDefinition(sql, new { ledgerId, name }, cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<Guid>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Replace the set of tags attached to a transaction with the supplied
    /// set. Used by the importer so re-runs reflect the source-of-truth tag
    /// list rather than accreting old tags.
    /// </summary>
    public async Task SetTagsForHeaderAsync(
        Guid ledgerId,
        Guid headerId,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        const string deleteSql = "DELETE FROM txn_header_tags WHERE header_id = @headerId;";
        await _connection.ExecuteAsync(new CommandDefinition(
            deleteSql, new { headerId }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (tagIds.Count == 0) return;

        const string insertSql = """
            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id)
            VALUES (@TransactionId, @TagId, @LedgerId)
            ON CONFLICT DO NOTHING;
            """;
        var rows = tagIds.Select(tagId =>
            new { TransactionId = headerId, TagId = tagId, LedgerId = ledgerId }).ToList();
        await _connection.ExecuteAsync(new CommandDefinition(
            insertSql, rows, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a set of tag names to ids within a ledger in a single round
    /// trip. Inserts any missing names and returns every name's id (existing
    /// on conflict, new on insert). Idempotent across re-runs because
    /// <c>(ledger_id, name)</c> is unique per ADR-0020 Phase A.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Guid>> EnsureTagsAsync(
        Guid ledgerId,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default)
    {
        if (names.Count == 0)
            return new Dictionary<string, Guid>(StringComparer.Ordinal);

        var trimmed = names
            .Select(n => n?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (trimmed.Length == 0)
            return new Dictionary<string, Guid>(StringComparer.Ordinal);

        const string sql = """
            WITH input AS (
                SELECT unnest(@names::text[]) AS name
            ),
            inserted AS (
                INSERT INTO tags (id, ledger_id, name)
                SELECT gen_random_uuid(), @ledgerId, name FROM input
                ON CONFLICT (ledger_id, name) DO NOTHING
                RETURNING id, name
            )
            SELECT id, name FROM inserted
            UNION ALL
            SELECT t.id, t.name FROM tags t
            JOIN input ON input.name = t.name
            WHERE t.ledger_id = @ledgerId
              AND NOT EXISTS (SELECT 1 FROM inserted i WHERE i.name = t.name);
            """;

        var command = new CommandDefinition(sql,
            new { ledgerId, names = trimmed }, cancellationToken: cancellationToken);
        var rows = await _connection.QueryAsync<(Guid Id, string Name)>(command).ConfigureAwait(false);
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows)
            result[row.Name] = row.Id;
        return result;
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

    public async Task<int> CountTagsAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM tags;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }
}
