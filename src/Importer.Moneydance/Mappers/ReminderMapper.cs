using Coffer.Domain.Reminders;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Translates a Moneydance <see cref="MdReminder"/> into a recurring-reminder
/// SERIES (ADR-0047 / migration 124): a <b>template</b> <c>txn_header</c> +
/// <c>txn_legs</c> (the transaction shape, flagged
/// <c>is_recurring_template</c>) plus a slim <see cref="RecurringTransactionRow"/>
/// carrying recurrence metadata (rrule / source_payload / auto-commit) and a
/// pointer to the template header.
/// </summary>
/// <remarks>
/// <para>The template header+legs are built exactly like
/// <see cref="TransactionMapper"/> builds a live txn (origin leg on the source
/// account + counterpart on each split's target, shared posting_index) — one
/// validated construction path, no second SQL builder (ADR-0048 D6). The
/// header carries a synthetic <c>external_id</c> (<c>"mdreminder:{id}"</c>) so
/// the bulk upsert dedups it idempotently; <c>origin='manual'</c> +
/// <c>provider_key=null</c> satisfies the provider-key CHECK while the non-null
/// external_id satisfies the external-id-or-manual CHECK.</para>
///
/// <para><see cref="RecurringTransactionRow.TemplateHeaderId"/> is set to the
/// proposed header id here; the import step remaps it to the PERSISTED id after
/// upserting the header (it differs from the proposed id on re-import).</para>
/// </remarks>
public static class ReminderMapper
{
    public enum SkipReason
    {
        NoTemplate,             // reminder has no embedded txn
        UnknownSourceAccount,   // txn.acct doesn't resolve to a Coffer account
        UnknownSplitAccount,    // a split's acct doesn't resolve
        NoSplits,               // template has no (emittable) splits
        UnparseableStartDate,   // sdt missing or malformed
    }

    public sealed record MapResult(
        TxnHeaderRow? Header,
        IReadOnlyList<TxnLegRow> Legs,
        RecurringTransactionRow? Row,
        SkipReason? Skip);

    public static MapResult Map(
        MdReminder reminder,
        IReadOnlyDictionary<string, AccountRef> accountByMdId,
        Guid ledgerId,
        string importSource,
        string rawJson)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentNullException.ThrowIfNull(accountByMdId);

        if (reminder.Txn is null) return Skip(SkipReason.NoTemplate);
        var txn = reminder.Txn;
        if (!accountByMdId.TryGetValue(txn.AcctId, out var sourceRef))
            return Skip(SkipReason.UnknownSourceAccount);
        if (txn.Splits.Count == 0) return Skip(SkipReason.NoSplits);
        foreach (var split in txn.Splits)
            if (!accountByMdId.ContainsKey(split.AcctId)) return Skip(SkipReason.UnknownSplitAccount);

        var startDate = ParseMdDateOnly(reminder.StartDate);
        if (startDate is null) return Skip(SkipReason.UnparseableStartDate);

        // Drop self-referential splits (target == source) before emitting legs
        // — same guard as TransactionMapper (they'd collide on the per-posting
        // account unique index).
        var emittable = new List<MdSplit>(txn.Splits.Count);
        foreach (var split in txn.Splits)
            if (accountByMdId[split.AcctId].Id != sourceRef.Id) emittable.Add(split);
        if (emittable.Count == 0) return Skip(SkipReason.NoSplits);

        var templateHeaderId = Guid.NewGuid();
        var postedAt = new DateTimeOffset(startDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var header = new TxnHeaderRow(
            Id:                  templateHeaderId,
            LedgerId:            ledgerId,
            Origin:              "manual",
            ExternalId:          "mdreminder:" + reminder.Id,
            Payee:               NullIfEmpty(reminder.Description) ?? NullIfEmpty(txn.Description),
            Memo:                NullIfEmpty(reminder.Memo) ?? NullIfEmpty(txn.Memo),
            PostedAt:            postedAt,
            TransactedAt:        null,
            Status:              "uncleared",
            CheckNumber:         NullIfEmpty(txn.CheckNumber),
            IsPending:           false,
            IsHidden:            false,
            IsMergedInto:        null,
            ImportSource:        importSource,
            ClearedAt:           null,
            ClearedByUserId:     null,
            OnlineMatchFitid:    null,
            OnlineMatchFiId:     null,
            Action:              null,
            ProviderKey:         null,
            IsMergeWinner:       false,
            ProviderRawPayload:  null,
            IsRecurringTemplate: true);

        var legs = new List<TxnLegRow>(emittable.Count * 2);
        var isMultiSplit = emittable.Count > 1;
        foreach (var split in emittable)
        {
            var targetRef = accountByMdId[split.AcctId];
            var legMemo = isMultiSplit ? NullIfEmpty(split.Description) : null;

            legs.Add(new TxnLegRow(
                Id:           Guid.NewGuid(),
                HeaderId:     templateHeaderId,
                LedgerId:     ledgerId,
                AccountId:    sourceRef.Id,
                PostingIndex: split.Index,
                LegMemo:      legMemo,
                Amount:       AccountMapper.MinorUnitsToDecimal(split.ParentAmount),
                SecurityId:   null,
                Quantity:     null,
                UnitPrice:    null));

            legs.Add(new TxnLegRow(
                Id:           Guid.NewGuid(),
                HeaderId:     templateHeaderId,
                LedgerId:     ledgerId,
                AccountId:    targetRef.Id,
                PostingIndex: split.Index,
                LegMemo:      legMemo,
                Amount:       AccountMapper.MinorUnitsToDecimal(split.SplitAmount),
                SecurityId:   null,
                Quantity:     null,
                UnitPrice:    null));
        }

        // acdays: -1 = auto-commit OFF (manual); >= 0 = auto-commit that many
        // days before due (verified against a sample MD export).
        int? autoCommitDaysBefore = reminder.AckDays is { } ackDays && ackDays >= 0 ? ackDays : null;

        var rrule = MdReminderRrule.Build(reminder);
        var endDate = ParseMdDateOnly(reminder.LastDate);
        var lastAcknowledged = ParseMdDateOnly(reminder.AcknowledgedDate);
        // next_due is a DERIVED cursor (not seed-only metadata, so the upsert
        // refreshes it every import): the first occurrence after Moneydance's
        // acknowledged date. Computed through the SAME shared calculator the API
        // uses on fire/skip/edit, so a freshly imported reminder's cursor matches
        // what the API would compute (ADR-0051) — and an old series isn't
        // stranded on its 2015 first occurrence.
        var nextDue = NextDueCalculator.NextDue(
            new RecurrenceExpander(), rrule, startDate.Value, endDate,
            consumedThrough: lastAcknowledged);

        var row = new RecurringTransactionRow(
            Id:                   Guid.NewGuid(),
            LedgerId:             ledgerId,
            ExternalId:           reminder.Id,
            Rrule:                rrule,
            SourcePayload:        NullIfEmpty(rawJson),
            AutoCommitDaysBefore: autoCommitDaysBefore,
            TemplateHeaderId:     templateHeaderId,   // proposed; step remaps to persisted
            SourceAccountId:      sourceRef.Id,        // originating account (drives the agenda amount)
            StartDate:            startDate.Value,
            EndDate:              endDate,
            NextDueDate:          nextDue,
            LastAcknowledgedDate: lastAcknowledged,
            IsLoanReminder:       reminder.IsLoanReminder,
            IsActive:             true,
            Origin:               "moneydance_import");

        return new MapResult(header, legs, row, Skip: null);
    }

    private static MapResult Skip(SkipReason reason) => new(null, [], null, reason);

    private static DateOnly? ParseMdDateOnly(int? yyyymmdd)
    {
        if (yyyymmdd is null or 0) return null;
        var v = yyyymmdd.Value;
        var year  = v / 10000;
        var month = (v / 100) % 100;
        var day   = v % 100;
        if (year < 1900 || year > 9999) return null;
        if (month is < 1 or > 12) return null;
        if (day is < 1 or > 31) return null;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
