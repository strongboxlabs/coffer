using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Pure translation from a Moneydance non-investment <see cref="MdTxn"/>
/// into Coffer's normalised <see cref="TxnHeaderRow"/> + <see cref="TxnLegRow"/>
/// shape (ADR-0022). Investment transactions (those with a non-null
/// <see cref="MdTxn.InvestTxnType"/>) are out of scope here and live in
/// <see cref="InvestmentTransactionMapper"/>.
/// </summary>
/// <remarks>
/// <para>One MD txn produces exactly one header. Each MD split produces
/// two <c>txn_legs</c> rows: an <em>origin</em> leg on the txn's primary
/// account carrying <c>parent_amount</c> (cash impact on the bank side),
/// and a <em>counterpart</em> leg on the split's target account carrying
/// <c>split_amount</c> (sign-flipped). Both legs of one posting share the
/// same <c>PostingIndex</c> (the MD split index) so the pair can be
/// found structurally — no <c>counterparty_id</c> denormalisation.</para>
///
/// <para>Header fields are populated once from the MD txn-level
/// metadata. Per-leg memo (<see cref="MdSplit.Description"/>) lives on
/// both legs of a posting, applied only on multi-split events. On
/// single-split events MD typically defaults <c>0.desc</c> to the
/// parent's <c>desc</c> (the payee), so using it as a leg memo there
/// would echo the payee — the register would render the parent payee
/// twice. Single-split rows take NULL leg_memo and the view falls back
/// to the header memo.</para>
///
/// <para>Sign convention follows Moneydance: <see cref="MdSplit.ParentAmount"/>
/// is the cash impact on the primary account; <see cref="MdSplit.SplitAmount"/>
/// is the impact on the target. Their pair-by-pair sum is zero for
/// same-currency txns. See
/// <see href="../decisions/0022-txn-headers-and-legs.md">ADR-0022</see>.</para>
/// </remarks>
public static class TransactionMapper
{
    public enum SkipReason
    {
        InvestmentTxn,
        UnknownPrimaryAccount,
        UnknownSplitAccount,
        NoSplits,
    }

    /// <summary>
    /// Outcome of mapping one Moneydance non-investment transaction.
    /// <see cref="Header"/> is the umbrella event row; <see cref="Legs"/>
    /// contains origin + counterpart legs in alternating order (origin
    /// first, then counterpart) per posting. The pipeline upserts the
    /// header first, then the legs in one bulk statement.
    /// </summary>
    public sealed record MapResult(
        TxnHeaderRow? Header,
        IReadOnlyList<TxnLegRow> Legs,
        IReadOnlyList<LegReconSeed> LegRecons,
        IReadOnlyList<string> Tags,
        SkipReason? Skip);

    public static MapResult Map(
        MdTxn txn,
        IReadOnlyDictionary<string, AccountRef> accountByMdId,
        Guid ledgerId,
        string importSource)
    {
        ArgumentNullException.ThrowIfNull(txn);
        ArgumentNullException.ThrowIfNull(accountByMdId);

        if (txn.IsInvestmentShape)
            return Skip(SkipReason.InvestmentTxn);

        if (!accountByMdId.TryGetValue(txn.AcctId, out var primaryRef))
            return Skip(SkipReason.UnknownPrimaryAccount);

        if (txn.Splits.Count == 0)
            return Skip(SkipReason.NoSplits);

        // Verify every split resolves to a known account up front. Doing
        // this before emitting any rows keeps partial output out of the
        // pipeline's batch on a malformed txn.
        foreach (var split in txn.Splits)
        {
            if (!accountByMdId.ContainsKey(split.AcctId))
                return Skip(SkipReason.UnknownSplitAccount);
        }

        var posted        = ResolvePostedAt(txn);
        var transacted    = ParseMdDate(txn.TransactedDate);
        // Precedence: Description / Memo are what the user sees in MD's
        // register (the cleaned-up, possibly-merged values). OlOrigPayee
        // / OlOrigMemo are the raw OFX-original values from before any
        // user edits. We want what the user curated; the raw is a
        // fallback only when the curated value is empty.
        var headerPayee   = NullIfEmpty(txn.Description) ?? NullIfEmpty(txn.OlOrigPayee);
        var headerMemo    = NullIfEmpty(txn.Memo)        ?? NullIfEmpty(txn.OlOrigMemo);
        var (headerStatus, headerIsCleared) = NormalizeMdStatus(txn.Status);
        // cleared_at is required by the DB CHECK whenever status='cleared'.
        // For MD-imported already-cleared rows we don't know the original
        // moment of clearing, so the row's posted_at is the best honest
        // proxy ("cleared no later than this point in calendar time").
        var headerClearedAt = headerIsCleared ? posted : (DateTimeOffset?)null;
        var checkNumber   = NullIfEmpty(txn.CheckNumber);

        // Filter out self-referential splits (target == primary) before
        // emitting legs — these would emit two legs on the same account
        // for one posting and collide on the (header_id, posting_index,
        // account_id) unique index. They're vanishingly rare in real
        // data and were skipped under the prior model too.
        var emittable = new List<MdSplit>(txn.Splits.Count);
        foreach (var split in txn.Splits)
        {
            var targetRef = accountByMdId[split.AcctId];
            if (targetRef.Id == primaryRef.Id) continue;
            emittable.Add(split);
        }
        if (emittable.Count == 0)
            return Skip(SkipReason.NoSplits);

        var headerId = Guid.NewGuid();
        // Mig 107 decompose: derive origin + provider_key from MD's
        // per-row metadata. The MD JSON bootstrap is not itself an
        // origin — it's the mechanism that brought rows into Coffer.
        // import_source records the bootstrap; origin records the
        // transaction's actual source mechanism.
        // Mig 110: pass the row's primary account so the classifier
        // can read `olbfi` / `ofx_import_acct_num` and discriminate
        // online OFX from QFX file imports — the per-txn signal alone
        // can't.
        accountByMdId.TryGetValue(txn.AcctId, out var primaryAccount);
        var (origin, providerKey) = DecomposeOrigin(txn, primaryAccount);
        // Mig 109 dropped is_user_defined; the mig-105 CHECK was
        // rewritten as `external_id IS NOT NULL OR origin = 'manual'`.
        // MD-imported rows always carry external_id (mig 105), so
        // the CHECK passes via the first branch regardless of origin.
        var header = new TxnHeaderRow(
            Id:                  headerId,
            LedgerId:            ledgerId,
            Origin:              origin,
            ExternalId:          txn.Id,
            Payee:               headerPayee,
            Memo:                headerMemo,
            PostedAt:            posted,
            TransactedAt:        transacted,
            Status:              headerStatus,
            CheckNumber:         checkNumber,
            IsPending:           false,
            IsHidden:            false,
            IsMergedInto:        null,
            ImportSource:        importSource,
            ClearedAt:           headerClearedAt,
            ClearedByUserId:     null,
            // Migration 034 — preserve the OFX online-match identity
            // (composite dedup key) so SimpleFIN sync can dedupe and
            // the user's bank-feed work survives the bootstrap. The
            // audit-only ol.match-status / ol.match-type / ol.orig-txn
            // fields were dropped in mig 109 (ADR-0035 §4); they
            // remain available inside ProviderRawPayload below.
            OnlineMatchFitid:    NullIfEmpty(txn.OlFitid),
            OnlineMatchFiId:     NullIfEmpty(txn.OlFiId),
            // Bank/credit/category txns have no investment action.
            Action:              null,
            // Mig 107.
            ProviderKey:         providerKey,
            IsMergeWinner:       false,
            // Mig 109 / ADR-0035 §3: persist the per-row JSON
            // verbatim so future classifier refinements can be pure
            // SQL against this column instead of needing the source
            // file. RawJson is the verbatim JSON the MD parser saw
            // for this `txn` item.
            ProviderRawPayload:  txn.RawJson);

        var legs = new List<TxnLegRow>(emittable.Count * 2);
        // Per-leg reconciliation seeds (ADR-0082). MD tracks status per side:
        // the parent txn's `stat` (headerStatus) is the primary/source
        // account's reconciliation state; each split's own `stat` is that
        // counterparty account's. Seed each leg from its OWN side so a transfer
        // cleared in one account stays uncleared in the other — no flattening.
        // cleared_at proxies to posted for cleared legs (same as the header-era
        // backfill); category legs are dropped at persist.
        var legRecons = new List<LegReconSeed>();
        var isMultiSplit = emittable.Count > 1;

        foreach (var split in emittable)
        {
            var targetRef = accountByMdId[split.AcctId];
            // Per-leg memo, applied only on multi-split events. MD's
            // Edit Splits dialog surfaces per-leg memos when there are
            // multiple legs to differentiate ("Salary", "Federal Tax",
            // ...). On single-split events MD typically defaults
            // 0.desc to the parent's description, so a leg memo there
            // would echo the payee into the memo column. Single-split
            // rows leave leg_memo NULL and the view falls back to the
            // header memo.
            var legMemo = isMultiSplit
                ? NullIfEmpty(split.Description)
                : null;

            var originLeg = new TxnLegRow(
                Id:                Guid.NewGuid(),
                HeaderId:          headerId,
                LedgerId:          ledgerId,
                AccountId:         primaryRef.Id,
                PostingIndex:      split.Index,
                LegMemo:           legMemo,
                Amount:            AccountMapper.MinorUnitsToDecimal(split.ParentAmount),
                SecurityId:        null,
                Quantity:          null,
                UnitPrice:         null);

            var counterpartLeg = new TxnLegRow(
                Id:                Guid.NewGuid(),
                HeaderId:          headerId,
                LedgerId:          ledgerId,
                AccountId:         targetRef.Id,
                PostingIndex:      split.Index,
                LegMemo:           legMemo,
                Amount:            AccountMapper.MinorUnitsToDecimal(split.SplitAmount),
                SecurityId:        null,
                Quantity:          null,
                UnitPrice:         null);

            legs.Add(originLeg);
            legs.Add(counterpartLeg);

            // Origin leg ← the parent txn's status; counterparty leg ← this
            // split's own status. Only non-'uncleared' legs need an overlay row.
            if (headerStatus != "uncleared")
                legRecons.Add(new LegReconSeed(
                    originLeg.Id, headerStatus, headerIsCleared ? posted : (DateTimeOffset?)null));

            var (splitStatus, splitIsCleared) = NormalizeMdStatus(split.Status);
            if (splitStatus != "uncleared")
                legRecons.Add(new LegReconSeed(
                    counterpartLeg.Id, splitStatus, splitIsCleared ? posted : (DateTimeOffset?)null));
        }

        return new MapResult(header, legs, legRecons, ExtractTags(txn), Skip: null);
    }

    private static MapResult Skip(SkipReason reason) =>
        new(Header: null, Legs: [], LegRecons: [], Tags: [], Skip: reason);

    public static IReadOnlyList<string> ExtractTags(MdTxn txn)
    {
        var collected = new HashSet<string>(StringComparer.Ordinal);
        AddCommaSeparated(collected, txn.Tags);
        foreach (var split in txn.Splits)
            AddCommaSeparated(collected, split.Tags);
        return [.. collected];
    }

    private static void AddCommaSeparated(HashSet<string> bucket, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var piece in raw.Split(','))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > 0)
                bucket.Add(trimmed);
        }
    }

    public static DateTimeOffset? ParseMdDate(int? yyyymmdd)
    {
        if (yyyymmdd is null or 0) return null;
        var value = yyyymmdd.Value;
        var year = value / 10000;
        var month = (value / 100) % 100;
        var day = value % 100;
        if (year < 1900 || year > 9999) return null;
        if (month < 1 || month > 12) return null;
        if (day < 1 || day > 31) return null;
        try { return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTimeOffset ResolvePostedAt(MdTxn txn)
    {
        // `dt` is the date the user assigned to the transaction in MD. It is
        // the source of truth for posted-at: future-datable, back-datable,
        // and the value the user sees in their register. Prior versions used
        // `dtentered` (the millisecond timestamp of when the user TYPED the
        // transaction) as a fallback — that's the wrong field. It made any
        // pre-/post-dated transaction render on the typing date in our
        // register, hiding scheduled entries the user expected to find on
        // their assigned date.
        return ParseMdDate(txn.Date)
            ?? throw new InvalidDataException(
                $"txn {txn.Id} has unparseable dt={txn.Date}");
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Normalize Moneydance's raw <c>txn.stat</c> letter code into the
    /// 3-state vocabulary stored in the <c>txn_leg_recon</c> overlay
    /// (migration 171, ADR-0082; formerly <c>txn_headers.status</c>,
    /// migration 030). MD's historical conventions:
    /// <c>c</c>/<c>C</c> are paper-check cleared, <c>x</c>/<c>X</c> are
    /// online-banking cleared, <c>R</c> is the rare legacy "reconciled"
    /// code (collapsed to <c>cleared</c> since there's no permanent
    /// reconciled state in our model — MD's "reconciling" is a workflow
    /// aid, not a terminal state). Everything else (NULL, empty, or any
    /// unexpected value) becomes <c>uncleared</c>.
    ///
    /// Returns a tuple: the normalized status string, plus a boolean
    /// signalling whether the caller must stamp <c>cleared_at</c> on the
    /// header to satisfy the DB CHECK
    /// <c>(status='cleared') ⇔ (cleared_at IS NOT NULL)</c>.
    /// </summary>
    /// <summary>
    /// Mig 107 origin decompose. Maps MD per-row metadata to the
    /// canonical (origin, provider_key) pair Coffer persists.
    /// </summary>
    /// <remarks>
    /// <para>Order matters — QIF wins over OL because a QIF-imported
    /// row whose user later online-matched it carries BOTH signals;
    /// the original source is still QIF. After QIF, the OL
    /// classification cascades: explicit MD CSV/text marker first,
    /// then synthesized FITID prefixes, then the legacy date-prefix
    /// FITID format, then mdplus, then ofx, then "FITID present but
    /// no fi_id" (assume real OFX server), then manual.</para>
    /// <para>The lossiness: OFX-FILE imports where MD recognized the
    /// FI populate `ol_fi_id='ofx:...'` identically to OFX-ONLINE,
    /// so those classify as <c>online_import / ofx</c>. Captured
    /// during slice design — see <c>docs/decisions/0035-register-provenance-indicators.md</c>.</para>
    /// </remarks>
    internal static (string Origin, string? ProviderKey) DecomposeOrigin(
        MdTxn txn, AccountRef? account = null)
    {
        // QIF signals — any one indicates the row came from a QIF
        // import inside MD (bank or investment).
        if (!string.IsNullOrEmpty(txn.QifInvstAction)
            || !string.IsNullOrEmpty(txn.QifOrigTxn)
            || !string.IsNullOrEmpty(txn.QifSn))
        {
            return ("file_import", "qif");
        }
        var fiId = NullIfEmpty(txn.OlFiId);
        var fitid = NullIfEmpty(txn.OlFitid);
        // CSV / text-import — MD's modern marker.
        if (fiId == "md:txtimport") return ("file_import", "csv");
        // CSV / text-import — MD's synthesized FITID prefixes.
        if (fitid is not null
            && (fitid.StartsWith("mdtxtimport:", StringComparison.Ordinal)
                || fitid.StartsWith("mdcsvimport:", StringComparison.Ordinal)
                || fitid.StartsWith("mdqifimport:", StringComparison.Ordinal)))
        {
            return ("file_import", "csv");
        }
        // Legacy MD+ format: FITID starts with a date prefix
        // (YYYYMMDD: or YYYY-MM-DD), no fi_id. MD+ used this shape
        // for online-fetched rows during an earlier era and
        // stopped supporting it later; the rows stayed in MD's
        // database unchanged. They are online_import / mdplus
        // despite the absent ol_fi_id prefix. (Earlier classifier
        // draft mis-tagged these as file_import / csv — corrected
        // during ADR-0035 review against a real-world dataset.)
        if (fiId is null && fitid is not null && LooksLikeLegacyMdPlusFitid(fitid))
        {
            return ("online_import", "mdplus");
        }
        // MD+ Direct Connect.
        if (fiId is not null && fiId.StartsWith("mdplus:", StringComparison.Ordinal))
        {
            return ("online_import", "mdplus");
        }
        // ADR-0035 §2 (mig 110 refinement): `ol_fi_id ofx:<INST>:...`
        // looks identical on online OFX and QFX file imports — MD
        // strips them to the same shape. Discriminate via the
        // account's own MD config:
        //   * acct.olbfi set (online OFX server configured) → online
        //   * acct.ofx_import_acct_num set (QFX file import config) → file
        //   * neither set → assume online (matches the pre-mig-110
        //     behaviour for accounts whose payload we never persisted;
        //     visible after re-import gives every account a payload).
        if (fiId is not null && fiId.StartsWith("ofx:", StringComparison.Ordinal))
        {
            if (account is not null)
            {
                if (!string.IsNullOrEmpty(account.OlbFi))
                    return ("online_import", "ofx");
                if (!string.IsNullOrEmpty(account.OfxImportAcctNum))
                    return ("file_import", "ofx");
            }
            return ("online_import", "ofx");
        }
        // FITID present but no fi_id, and didn't look like a
        // synthesized CSV FITID. Could be either:
        //   * Real online OFX server (a real OFX server, pre-MD+ era), OR
        //   * QFX file import where MD didn't preserve the <FI><FID>
        //     header block (observed in modern QFX files from
        //     brokerages that omit it).
        // Discriminate via the row's account config — same as the
        // `ofx:` prefix branch above. If the account has
        // `ofx_import_acct_num` set, this is a QFX file import even
        // without an `ol_fi_id ofx:` prefix on the row.
        if (fitid is not null)
        {
            if (account is not null)
            {
                if (!string.IsNullOrEmpty(account.OfxImportAcctNum))
                    return ("file_import", "ofx");
                if (!string.IsNullOrEmpty(account.OlbFi))
                    return ("online_import", "ofx");
            }
            return ("online_import", "ofx");
        }
        // No ingest signal — typed manually in MD.
        return ("manual", null);
    }

    private static bool LooksLikeLegacyMdPlusFitid(string fitid)
    {
        // YYYYMMDD: prefix (8 digits then colon)
        if (fitid.Length >= 9 && fitid[8] == ':'
            && IsAllDigits(fitid.AsSpan(0, 8)))
        {
            return true;
        }
        // YYYY-MM-DD prefix (digits-dash-digits-dash-digits)
        if (fitid.Length >= 10
            && fitid[4] == '-' && fitid[7] == '-'
            && IsAllDigits(fitid.AsSpan(0, 4))
            && IsAllDigits(fitid.AsSpan(5, 2))
            && IsAllDigits(fitid.AsSpan(8, 2)))
        {
            return true;
        }
        return false;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    internal static (string Status, bool IsCleared) NormalizeMdStatus(string? rawMdStat) =>
        rawMdStat switch
        {
            "c" or "C" or "x" or "X" or "R" => ("cleared", true),
            _                               => ("uncleared", false),
        };
}
