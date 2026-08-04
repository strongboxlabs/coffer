namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>reminder</c> item — a recurring transaction
/// template. Reminders embed an entire transaction definition under the
/// <c>txn.*</c> prefix (with split fields under <c>txn.0.*</c>, <c>txn.1.*</c>,
/// ...); we surface it as a separate nested record for clarity.
/// </summary>
public sealed record MdReminder(
    string Id,
    string Description,
    string? Memo,
    string Type,
    int StartDate,
    int? AcknowledgedDate,
    int? LastDate,
    int? AckDays,
    int? MonthlyDay,
    int? MonthlyMod,
    int? WeeklyDay,
    int? WeeklyMod,
    int? Daily,
    int? Yearly,
    bool IsLoanReminder,
    string? Tags,
    MdReminderTxn? Txn)
{
    public static MdReminder From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ObjType != "reminder")
            throw new ArgumentException(
                $"MdItem.obj_type is '{item.ObjType}', expected 'reminder'.", nameof(item));

        return new MdReminder(
            Id: item.Id,
            Description: item.GetString("desc") ?? string.Empty,
            Memo: item.GetString("memo"),
            Type: item.GetString("type") ?? string.Empty,
            StartDate: item.GetInt("sdt") ?? 0,
            AcknowledgedDate: item.GetInt("ackdt"),
            LastDate: item.GetInt("ldt"),
            AckDays: item.GetInt("acdays"),
            MonthlyDay: item.GetInt("monthlydays"),
            MonthlyMod: item.GetInt("monthlymod"),
            WeeklyDay: item.GetInt("weeklydays"),
            WeeklyMod: item.GetInt("weeklymod"),
            Daily: item.GetInt("daily"),
            Yearly: item.GetInt("yearly"),
            IsLoanReminder: item.GetBool("is_loan_reminder") ?? false,
            Tags: item.GetString("txn.tags"),
            Txn: ExtractTxn(item));
    }

    private static MdReminderTxn? ExtractTxn(MdItem item)
    {
        // A reminder's embedded txn lives under "txn.*" keys. Treat its absence
        // (no txn.acctid) as "no template defined".
        var acctId = item.GetString("txn.acctid");
        if (string.IsNullOrEmpty(acctId)) return null;

        var splits = MdTxn.ExtractSplits(item, prefix: "txn.");

        return new MdReminderTxn(
            AcctId: acctId,
            Description: item.GetString("txn.desc") ?? string.Empty,
            Memo: item.GetString("txn.memo"),
            Date: item.GetInt("txn.dt"),
            TransactedDate: item.GetInt("txn.td"),
            DateEnteredMillis: item.GetLong("txn.dtentered"),
            CheckNumber: item.GetString("txn.chk"),
            Splits: splits);
    }
}

/// <summary>
/// The transaction template embedded within a <see cref="MdReminder"/>.
/// Same shape as a <see cref="MdTxn"/> but lacks the online-banking metadata
/// fields and is reached via the <c>txn.*</c> key prefix.
/// </summary>
public sealed record MdReminderTxn(
    string AcctId,
    string Description,
    string? Memo,
    int? Date,
    int? TransactedDate,
    long? DateEnteredMillis,
    string? CheckNumber,
    IReadOnlyList<MdSplit> Splits);
