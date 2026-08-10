using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// A per-leg reconciliation-status seed for the bank import path (ADR-0082).
/// <see cref="LegId"/> is the PROPOSED leg id; <see cref="TransactionsRepository.BulkUpsertAsync"/>
/// translates it to the persisted id. Only non-'uncleared' legs need a seed
/// (absent ⇒ uncleared). The bank mapper emits these so each leg carries its
/// OWN Moneydance status — the origin leg from the parent txn's <c>stat</c>, a
/// counterparty leg from that split's own <c>stat</c> — instead of one header
/// status fanned across every leg (which flattened a transfer cleared in one
/// account but uncleared in the other). Single-status paths (investment,
/// reminders) pass none and keep the header-fan.
/// </summary>
public sealed record LegReconSeed(Guid LegId, string Status, DateTimeOffset? ClearedAt);

/// <summary>
/// Dapper-backed gateway to the normalised <c>txn_headers</c> +
/// <c>txn_legs</c> tables (ADR-0022). One header per event, two legs
/// per posting, paired structurally via shared
/// <c>(header_id, posting_index)</c>.
/// </summary>
/// <remarks>
/// <para>The canonical write path is <see cref="BulkUpsertAsync"/>,
/// which inserts headers first and then legs in two statements per
/// chunk. Headers come back from the RETURNING clause keyed by
/// <c>(ledger_id, external_id)</c> so legs can rebind their FK to the
/// persisted header id.</para>
///
/// <para>Seed-once (ADR-0052 D2): the Moneydance importer only ever
/// seeds an EMPTY ledger — <see cref="ImportCommand"/> refuses to run
/// against a ledger that already holds transactions, because MD re-keys
/// <c>txn.Id</c> on online-merge and a second import would resurrect
/// hidden/merged rows as duplicates. With that guard in place these
/// writes are always fresh inserts, so the former ON CONFLICT … DO
/// UPDATE idempotency machinery (and the leg-replace pre-delete) is
/// gone — both statements are plain INSERTs. The RETURNING / id-map
/// plumbing is retained: it now resolves identity (proposed == persisted
/// on every fresh insert) but still wires legs / lots / tags to the
/// persisted header and leg ids.</para>
/// </remarks>
public sealed class TransactionsRepository
{
    private readonly NpgsqlConnection _connection;

    public TransactionsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Count of transaction headers already in the ledger. The Moneydance
    /// importer seeds a fresh ledger exactly ONCE (ADR-0052 D2): a non-zero
    /// count means the ledger is already populated, so the import is refused.
    /// It is a seed, not a re-import or sync - MD re-keys <c>txn.Id</c> on
    /// online-merge, so seeding a populated ledger would resurrect
    /// hidden/merged rows as duplicates. To (re)seed, use a new ledger or a
    /// wiped-empty one (prune-batch / Demo refresh).
    /// </summary>
    public async Task<int> CountTransactionHeadersAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM txn_headers WHERE ledger_id = @LedgerId",
            new { LedgerId = ledgerId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    /// Outcome of <see cref="BulkUpsertAsync"/>. Headers are keyed by
    /// <c>(LedgerId, ExternalId)</c> → persisted header id. Legs are
    /// keyed by the proposed leg id (from the caller's payload) →
    /// persisted leg id. Seed-once (ADR-0052 D2) means every write is a
    /// fresh insert, so the proposed and persisted ids always match; the
    /// maps remain so callers (lots, tags) can rebind their FKs to the
    /// persisted ids without special-casing.
    /// </summary>
    public sealed record UpsertResult(
        IReadOnlyDictionary<(Guid LedgerId, string ExternalId), Guid> Headers,
        IReadOnlyDictionary<Guid, Guid> Legs);

    /// <summary>
    /// Bulk-insert a batch of (header + legs) units in two statements
    /// per chunk (seed-once, ADR-0052 D2 — every write is fresh). Returns
    /// the proposed → persisted id maps for both headers and legs so
    /// callers (lots, etc.) can rebind their FKs to the persisted ids.
    /// </summary>
    public async Task<UpsertResult> BulkUpsertAsync(
        IReadOnlyList<TxnHeaderRow> headers,
        IReadOnlyList<TxnLegRow> legs,
        IReadOnlyList<LegReconSeed>? legRecons = null,
        int chunkSize = 5000,
        CancellationToken cancellationToken = default)
    {
        var persistedHeaderIds = new Dictionary<(Guid, string), Guid>(headers.Count);
        var persistedLegIds = new Dictionary<Guid, Guid>(legs.Count);

        if (headers.Count == 0)
            return new UpsertResult(persistedHeaderIds, persistedLegIds);

        // -- Header insert (seed-once, ADR-0052 D2) -------------------
        // Plain INSERT: the ledger is empty when the importer runs, so
        // no row can conflict. RETURNING id + key (ledger_id,
        // external_id) so the caller can resolve proposed → persisted
        // header id (identity on a fresh insert) before issuing the leg
        // insert. Rows without external_id (manual entries) come back
        // keyed by their proposed id only.
        const string headerSql = """
            INSERT INTO txn_headers (
                id, ledger_id, origin, external_id,
                payee, memo, posted_at, transacted_at,
                check_number,
                is_pending, is_hidden, is_merged_into,
                import_source,
                online_match_fitid, online_match_fi_id,
                action,
                provider_key,
                provider_raw_payload,
                is_recurring_template
            )
            SELECT * FROM unnest(
                @Ids::uuid[],
                @LedgerIds::uuid[],
                @Origins::text[],
                @ExternalIds::text[],
                @Payees::text[],
                @Memos::text[],
                @PostedAts::timestamptz[],
                @TransactedAts::timestamptz[],
                @CheckNumbers::text[],
                @IsPendings::bool[],
                @IsHiddens::bool[],
                @IsMergedIntos::uuid[],
                @ImportSources::text[],
                @OnlineMatchFitids::text[],
                @OnlineMatchFiIds::text[],
                @Actions::text[],
                @ProviderKeys::text[],
                @ProviderRawPayloads::jsonb[],
                @IsRecurringTemplates::bool[]
            )
            RETURNING id, ledger_id, external_id;
            """;

        for (var offset = 0; offset < headers.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, headers.Count - offset);
            var parameters = BuildHeaderUnnestParameters(headers, offset, length);

            var command = new CommandDefinition(headerSql, parameters, cancellationToken: cancellationToken);
            var returned = await _connection.QueryAsync<(Guid Id, Guid LedgerId, string? ExternalId)>(command)
                                            .ConfigureAwait(false);
            foreach (var row in returned)
            {
                if (row.ExternalId is not null)
                    persistedHeaderIds[(row.LedgerId, row.ExternalId)] = row.Id;
            }
        }

        if (legs.Count == 0)
            return new UpsertResult(persistedHeaderIds, persistedLegIds);

        // Seed-once (ADR-0052 D2): every header was freshly inserted, so
        // the proposed header id IS the persisted id and each leg's
        // header_id FK already points at the surviving header. No
        // re-mapping and no leg-replace pre-delete is needed — the
        // ledger was empty, so there are no prior legs/lots to clear.
        var remappedLegs = legs;

        const string legSql = """
            INSERT INTO txn_legs (
                id, header_id, ledger_id, account_id, posting_index,
                leg_memo, amount,
                security_id, quantity, unit_price, posting_role
            )
            SELECT * FROM unnest(
                @Ids::uuid[],
                @HeaderIds::uuid[],
                @LedgerIds::uuid[],
                @AccountIds::uuid[],
                @PostingIndexes::int[],
                @LegMemos::text[],
                @Amounts::numeric[],
                @SecurityIds::uuid[],
                @Quantities::numeric[],
                @UnitPrices::numeric[],
                @PostingRoles::text[]
            )
            RETURNING id, header_id, posting_index, account_id;
            """;

        // To build proposed → persisted leg id map, we walk the input
        // chunk in lockstep with a natural-key lookup from the RETURNING
        // rows. Each input leg's (header_id, posting_index, account_id)
        // is the natural key; the returned id is what's persisted. Under
        // seed-once (ADR-0052 D2) every leg is a fresh insert so the
        // returned id equals the proposed id, but the lookup keeps the
        // map robust to the DB assigning ids.
        for (var offset = 0; offset < remappedLegs.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, remappedLegs.Count - offset);
            var parameters = BuildLegUnnestParameters(remappedLegs, offset, length);

            var command = new CommandDefinition(legSql, parameters, cancellationToken: cancellationToken);
            var returned = await _connection
                .QueryAsync<(Guid Id, Guid HeaderId, int PostingIndex, Guid AccountId)>(command)
                .ConfigureAwait(false);
            var persistedByKey = returned.ToDictionary(
                r => (r.HeaderId, r.PostingIndex, r.AccountId),
                r => r.Id);

            for (var i = 0; i < length; i++)
            {
                var proposed = legs[offset + i];          // original payload
                var remapped = remappedLegs[offset + i];  // identity alias (seed-once)
                if (persistedByKey.TryGetValue(
                        (remapped.HeaderId, remapped.PostingIndex, remapped.AccountId),
                        out var persistedId))
                {
                    persistedLegIds[proposed.Id] = persistedId;
                }
            }
        }

        // ADR-0082: reconciliation status is a per-account per-leg overlay
        // (txn_leg_recon), not a header column. cleared_by_user_id is NULL (the
        // importer isn't a real user); the accounts join drops category legs
        // (never reconciled); absent row ⇒ uncleared (so only non-'uncleared'
        // rows are written).
        if (legRecons is not null)
        {
            // Bank + investment path: the caller computed each leg's OWN status,
            // so this list is AUTHORITATIVE — even when empty (all-uncleared) it
            // wins and the header-fan below is NOT used. That matters when a
            // header's status was derived from a split whose stat differs from
            // the per-leg model (e.g. an investment sec-split cleared under an
            // uncleared parent): falling back to the fan would re-flatten. Seeds
            // key on the PROPOSED leg id; translate to the persisted id (skipping
            // any seed whose leg didn't persist, e.g. a FITID-deduped header).
            const string legReconSql = """
                INSERT INTO txn_leg_recon (leg_id, ledger_id, status, cleared_at, cleared_by_user_id)
                SELECT l.id, l.ledger_id, rs.status, rs.cleared_at, NULL
                FROM unnest(@LegIds::uuid[], @Statuses::text[], @ClearedAts::timestamptz[])
                     AS rs(leg_id, status, cleared_at)
                JOIN txn_legs l ON l.id = rs.leg_id
                JOIN accounts a ON a.id = l.account_id
                WHERE a.account_type <> 'category';
                """;
            var resolved = new List<(Guid LegId, string Status, DateTime? ClearedAt)>(legRecons.Count);
            foreach (var seed in legRecons)
                if (persistedLegIds.TryGetValue(seed.LegId, out var persistedId))
                    resolved.Add((persistedId, seed.Status, seed.ClearedAt?.UtcDateTime));

            for (var offset = 0; offset < resolved.Count; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, resolved.Count - offset);
                var legIds     = new Guid[length];
                var statuses   = new string[length];
                var clearedAts = new DateTime?[length];
                for (var i = 0; i < length; i++)
                {
                    var r = resolved[offset + i];
                    legIds[i]     = r.LegId;
                    statuses[i]   = r.Status;
                    clearedAts[i] = r.ClearedAt;
                }
                var command = new CommandDefinition(
                    legReconSql,
                    new { LegIds = legIds, Statuses = statuses, ClearedAts = clearedAts },
                    cancellationToken: cancellationToken);
                await _connection.ExecuteAsync(command).ConfigureAwait(false);
            }
        }
        else
        {
            // Single-status paths (investment, reminders): Moneydance carries
            // one status for the whole event, so fan it onto every real-account
            // leg — the same shape migration 171's backfill uses. (Reminders are
            // always 'uncleared', so they contribute nothing here.)
            var reconHeaders = headers.Where(h => h.Status != "uncleared").ToList();
            if (reconHeaders.Count > 0)
            {
                const string reconSql = """
                    INSERT INTO txn_leg_recon (leg_id, ledger_id, status, cleared_at, cleared_by_user_id)
                    SELECT l.id, l.ledger_id, hs.status, hs.cleared_at, NULL
                    FROM unnest(@HeaderIds::uuid[], @Statuses::text[], @ClearedAts::timestamptz[])
                         AS hs(header_id, status, cleared_at)
                    JOIN txn_legs l ON l.header_id = hs.header_id
                    JOIN accounts a ON a.id = l.account_id
                    WHERE a.account_type <> 'category';
                    """;
                for (var offset = 0; offset < reconHeaders.Count; offset += chunkSize)
                {
                    var length = Math.Min(chunkSize, reconHeaders.Count - offset);
                    var headerIds  = new Guid[length];
                    var statuses   = new string[length];
                    var clearedAts = new DateTime?[length];
                    for (var i = 0; i < length; i++)
                    {
                        var h = reconHeaders[offset + i];
                        headerIds[i]  = h.Id;
                        statuses[i]   = h.Status;
                        clearedAts[i] = h.ClearedAt?.UtcDateTime;
                    }
                    var command = new CommandDefinition(
                        reconSql,
                        new { HeaderIds = headerIds, Statuses = statuses, ClearedAts = clearedAts },
                        cancellationToken: cancellationToken);
                    await _connection.ExecuteAsync(command).ConfigureAwait(false);
                }
            }
        }

        return new UpsertResult(persistedHeaderIds, persistedLegIds);
    }

    public async Task<int> CountHeadersAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM txn_headers;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    public async Task<int> CountLegsAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM txn_legs;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    private static object BuildHeaderUnnestParameters(
        IReadOnlyList<TxnHeaderRow> rows, int offset, int length)
    {
        var ids                  = new Guid[length];
        var ledgerIds            = new Guid[length];
        var origins              = new string[length];
        var externalIds          = new string?[length];
        var payees               = new string?[length];
        var memos                = new string?[length];
        var postedAts            = new DateTime[length];
        var transactedAts        = new DateTime?[length];
        var checkNumbers         = new string?[length];
        var isPendings           = new bool[length];
        var isHiddens            = new bool[length];
        var isMergedIntos        = new Guid?[length];
        var importSources        = new string?[length];
        var onlineMatchFitids    = new string?[length];
        var onlineMatchFiIds     = new string?[length];
        var actions              = new string?[length];
        var providerKeys         = new string?[length];
        var providerRawPayloads  = new string?[length];
        var isRecurringTemplates = new bool[length];

        for (var i = 0; i < length; i++)
        {
            var row = rows[offset + i];
            ids[i]                  = row.Id;
            ledgerIds[i]            = row.LedgerId;
            origins[i]              = row.Origin;
            externalIds[i]          = row.ExternalId;
            payees[i]               = row.Payee;
            memos[i]                = row.Memo;
            postedAts[i]            = row.PostedAt.UtcDateTime;
            // NOT NULL since mig 189. MD sets `td` on almost every txn, but
            // reminder-derived rows carry none — those store the posted date,
            // which is what "no distinct tax date" means.
            transactedAts[i]        = (row.TransactedAt ?? row.PostedAt).UtcDateTime;
            checkNumbers[i]         = row.CheckNumber;
            isPendings[i]           = row.IsPending;
            isHiddens[i]            = row.IsHidden;
            isMergedIntos[i]        = row.IsMergedInto;
            importSources[i]        = row.ImportSource;
            onlineMatchFitids[i]    = row.OnlineMatchFitid;
            onlineMatchFiIds[i]     = row.OnlineMatchFiId;
            actions[i]              = row.Action;
            providerKeys[i]         = row.ProviderKey;
            providerRawPayloads[i]  = row.ProviderRawPayload;
            isRecurringTemplates[i] = row.IsRecurringTemplate;
        }

        return new
        {
            Ids                  = ids,
            LedgerIds            = ledgerIds,
            Origins              = origins,
            ExternalIds          = externalIds,
            Payees               = payees,
            Memos                = memos,
            PostedAts            = postedAts,
            TransactedAts        = transactedAts,
            CheckNumbers         = checkNumbers,
            IsPendings           = isPendings,
            IsHiddens            = isHiddens,
            IsMergedIntos        = isMergedIntos,
            ImportSources        = importSources,
            OnlineMatchFitids    = onlineMatchFitids,
            OnlineMatchFiIds     = onlineMatchFiIds,
            Actions              = actions,
            ProviderKeys         = providerKeys,
            ProviderRawPayloads  = providerRawPayloads,
            IsRecurringTemplates = isRecurringTemplates,
        };
    }

    private static object BuildLegUnnestParameters(
        IReadOnlyList<TxnLegRow> rows, int offset, int length)
    {
        var ids                = new Guid[length];
        var headerIds          = new Guid[length];
        var ledgerIds          = new Guid[length];
        var accountIds         = new Guid[length];
        var postingIndexes     = new int[length];
        var legMemos           = new string?[length];
        var amounts            = new decimal[length];
        var securityIds        = new Guid?[length];
        var quantities         = new decimal?[length];
        var unitPrices         = new decimal?[length];
        var postingRoles       = new string?[length];

        for (var i = 0; i < length; i++)
        {
            var row = rows[offset + i];
            ids[i]                = row.Id;
            headerIds[i]          = row.HeaderId;
            ledgerIds[i]          = row.LedgerId;
            accountIds[i]         = row.AccountId;
            postingIndexes[i]     = row.PostingIndex;
            legMemos[i]           = row.LegMemo;
            amounts[i]            = row.Amount;
            securityIds[i]        = row.SecurityId;
            quantities[i]         = row.Quantity;
            unitPrices[i]         = row.UnitPrice;
            postingRoles[i]       = row.PostingRole;
        }

        return new
        {
            Ids                = ids,
            HeaderIds          = headerIds,
            LedgerIds          = ledgerIds,
            AccountIds         = accountIds,
            PostingIndexes     = postingIndexes,
            LegMemos           = legMemos,
            Amounts            = amounts,
            SecurityIds        = securityIds,
            Quantities         = quantities,
            UnitPrices         = unitPrices,
            PostingRoles       = postingRoles,
        };
    }
}
