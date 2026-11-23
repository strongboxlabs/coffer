namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// What the seeding step needs to find in the imported Demo, named by Moneydance id
/// or by value, never by a Coffer id (those exist only after the import).
/// </summary>
/// <remarks>
/// Every export row it names lies in the trailing two full months, so a short span
/// of three months or more carries them all. The Electric omissions are not export
/// rows and are exempt. A field a generator stage has not filled yet is empty.
/// </remarks>
public sealed record DemoManifest
{
    /// <summary>Account MD ids by role (<see cref="DemoAccountRoles"/>).</summary>
    public IReadOnlyDictionary<string, string> AccountIds { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Category paths by role.</summary>
    public IReadOnlyDictionary<string, DemoCategoryRef> Categories { get; init; } = new Dictionary<string, DemoCategoryRef>(StringComparer.Ordinal);

    /// <summary>The two feed rows the bank re-sent, by MD txn id.</summary>
    public IReadOnlyList<string> DuplicateTxnIds { get; init; } = [];

    /// <summary>The rows the seeder renames beside the bank's original.</summary>
    public IReadOnlyList<DemoEditCandidate> EditCandidates { get; init; } = [];

    /// <summary>The checking statement the seeder half-reconciles.</summary>
    public DemoReconcileTarget? CheckingReconcile { get; init; }

    /// <summary>The card statement the CSV carries.</summary>
    public DemoCardStatementRef? CardStatement { get; init; }

    /// <summary>The Electric occurrences left out of the export for the seeder to
    /// post.</summary>
    public IReadOnlyList<DemoDatedAmount> ElectricOmissions { get; init; } = [];

    /// <summary>The Electric bill's nominal day of the month: the reminder's monthly
    /// rule. Each omission's date is that day moved to the next business day.</summary>
    public int? ElectricDayOfMonth { get; init; }

    /// <summary>The target-date fund's look-through weights by ticker; they sum to 1.</summary>
    public IReadOnlyDictionary<string, decimal> TargetDateLookThrough { get; init; } = new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>The stable-value fund's month-end unit values.</summary>
    public IReadOnlyList<DemoUnitValue> StableValueMonthEnds { get; init; } = [];

    /// <summary>Budget targets per (category, month), from the generator's own actuals.</summary>
    public IReadOnlyList<DemoBudgetTarget> BudgetTargets { get; init; } = [];

    /// <summary>The MD id of the monthly Internet reminder.</summary>
    public string? InternetReminderId { get; init; }

    /// <summary>The account folders and their members.</summary>
    public IReadOnlyList<DemoFolder> Folders { get; init; } = [];
}

/// <summary>A base category by Moneydance type and segment array. A value, not a
/// lookup key: its segments compare by reference.</summary>
public sealed record DemoCategoryRef(MdCategoryType Type, IReadOnlyList<string> Segments)
{
    /// <summary>The path as <c>e Vacation &gt; Travel</c>, for messages.</summary>
    public override string ToString() => DemoBase.Code(Type) + " " + string.Join(" > ", Segments);
}

/// <summary>A row to rename, and the payee it gets.</summary>
public sealed record DemoEditCandidate(string TxnId, string NewPayee);

/// <summary>A statement period and the rows to mark reconciling.</summary>
public sealed record DemoReconcileTarget(DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<string> ReconcilingTxnIds);

/// <summary>A card statement's close and the exported rows the CSV repeats.</summary>
public sealed record DemoCardStatementRef(DateOnly CloseDate, IReadOnlyList<DemoCardStatementRow> ExportedRows);

/// <summary>An exported row the CSV repeats, with its CSV date and amount.</summary>
public sealed record DemoCardStatementRow(string TxnId, DateOnly Date, long Amount);

/// <summary>A date and an amount in cents.</summary>
public sealed record DemoDatedAmount(DateOnly Date, long Amount);

/// <summary>A unit value on a date.</summary>
public sealed record DemoUnitValue(DateOnly Date, decimal UnitValue);

/// <summary>A budget target for one category and month, in cents.</summary>
public sealed record DemoBudgetTarget(DemoCategoryRef Category, int Year, int Month, long Amount);

/// <summary>An account folder: its name, the one Coffer account type it holds, and
/// its members' MD ids.</summary>
public sealed record DemoFolder(string Name, string AccountType, IReadOnlyList<string> AccountIds);
