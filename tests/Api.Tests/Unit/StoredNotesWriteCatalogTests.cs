using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Coffer.Api.Db;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Every writer of an input of the memo-and-tags rule recomputes what is stored from
/// it (<c>LegNotes</c>), fail closed: a new writer that skips the recompute fails here
/// before any reader can show a stale value.
/// </summary>
/// <remarks>
/// <para>What the inputs are is not taken on trust: the rule's input records, the
/// columns <c>LegNotes.ComputeAsync</c> reads and the columns the save interceptor
/// watches must each match <see cref="Inputs"/>, so a rule that starts reading
/// something new fails here until the catalog names it.</para>
/// <para>Four ways to write an input, four checks:</para>
/// <list type="bullet">
///   <item><b>Through the change tracker</b>: the save interceptor recomputes. Checked by
///   the interceptor handling every input entity type and watching exactly the input
///   columns.</item>
///   <item><b>Set-based</b> (<c>ExecuteUpdateAsync</c>, <c>ExecuteDeleteAsync</c>): every
///   such statement in <c>src/Api</c> whose root is an input table, and that deletes rows,
///   sets an input column or builds its setter outside the statement, must be followed in
///   its method by a <c>LegNotes.Recompute*</c> call, or sit in a helper listed below
///   whose every call site is listed and recomputes after the call. A statement whose
///   root this scan cannot name must be listed with its reason.</item>
///   <item><b>Database functions</b>: every function <c>AppDbContext</c> maps is
///   classified; one that writes an input must be followed at every call site by its
///   recompute, and one classified as writing no input is checked against its latest
///   SQL definition for a direct write of an input table.</item>
///   <item><b>The Moneydance import</b>: every call of <c>ImportAsync</c> must pass the
///   in-transaction recompute, or be listed as not wired. The API is its only caller; the
///   CLI no longer imports (ADR-0015, 2026-10-25 amendment), and its sources are scanned too, so an import call
///   there fails the same way.</item>
///   <item><b>Migrations</b>: a script from 264 on that changes an input (in its own
///   statements, its DO blocks or the functions it calls) must be followed by a C#
///   migration step that recomputes, or be listed with its reason.</item>
/// </list>
/// <para>And the other way round, the output: nothing but the recompute writes the
/// stored presentation (the <c>txn_legs.notes_*</c> columns and
/// <c>txn_leg_note_tags</c>). No EF model but the recompute's private one maps a stored
/// column for writing, and every keyless one that maps it (as a table or a view) is a
/// listed read mapping; no source outside <c>LegNotes</c> names a stored column except
/// as a read mapping, starts a write from a read mapping (directly or through the
/// locals a statement uses) or builds a tag row; and no SQL writes them (UPDATE,
/// INSERT with or without a column list or alias, MERGE, COPY FROM, TRUNCATE) except a
/// whole-row copy that is listed with its reason.</para>
/// <para>Not covered: SQL that writes through dynamic statements (<c>EXECUTE</c>), a
/// recompute on only some branches of a method, a local reassigned in a loop or passed
/// through another method, an input written outside <c>src/Api</c> other than by the
/// import, and a stored column written outside <c>src</c> (the raw-SQL test seeds). The SQL scans follow the functions a function or script calls, at their
/// latest definitions.</para>
/// </remarks>
public sealed partial class StoredNotesWriteCatalogTests
{
    /// <summary>The input tables, by DbSet, and the columns of each the rule reads.</summary>
    private static readonly Dictionary<string, string[]> Inputs = new(StringComparer.Ordinal)
    {
        ["TxnHeaders"] = ["Memo", "Action", "OriginatingAccountId"],
        ["TxnLegs"] = ["AccountId", "PostingIndex", "LegMemo", "HeaderId"],
        ["TxnHeaderTags"] = ["HeaderId", "TagId"],
        ["TxnLegTags"] = ["LegId", "TagId"],
        ["Tags"] = [],
        ["Accounts"] = ["HoldingsAccountId"],
    };

    /// <summary>The SQL tables of <see cref="Inputs"/>.</summary>
    private static readonly string[] InputTables =
        ["txn_headers", "txn_legs", "txn_header_tags", "txn_leg_tags", "tags", "accounts"];

    /// <summary>
    /// Every field of the rule's input records, with the input it is read from: a new
    /// field is a new input and fails here until it is catalogued (and the interceptor
    /// and the set-based scan know it).
    /// </summary>
    private static readonly Dictionary<string, string> RecordFields = new(StringComparer.Ordinal)
    {
        ["NotesHeaderInput.HeaderId"] = "key",
        ["NotesHeaderInput.Action"] = "TxnHeaders.Action",
        ["NotesHeaderInput.OriginatingAccountId"] = "TxnHeaders.OriginatingAccountId",
        ["NotesHeaderInput.Memo"] = "TxnHeaders.Memo",
        ["NotesHeaderInput.TagIds"] = "TxnHeaderTags",
        ["NotesHeaderInput.Legs"] = "TxnLegs",
        ["NotesLegInput.LegId"] = "key",
        ["NotesLegInput.AccountId"] = "TxnLegs.AccountId",
        ["NotesLegInput.PostingIndex"] = "TxnLegs.PostingIndex",
        ["NotesLegInput.Memo"] = "TxnLegs.LegMemo",
        ["NotesLegInput.TagIds"] = "TxnLegTags",
    };

    /// <summary>
    /// Methods that write an input set-based and leave the recompute to their callers,
    /// each with every method that calls it (file, method), all of which must recompute.
    /// </summary>
    private static readonly Dictionary<string, (string File, string Method)[]> Delegated = new(StringComparer.Ordinal)
    {
        ["src/Api/Db/AnnotationWriter.cs:FollowRepointAsync"] =
        [
            ("src/Api/Db/Repositories/BulkTransactionsRepository.cs", "BulkMoveAccountAsync"),
            ("src/Api/Db/Repositories/AccountsRepository.cs", "MergeCategoryAsync"),
        ],
        ["src/Api/Db/Repositories/BulkTransactionsRepository.cs:ClearInvestmentEventAsync"] =
        [
            ("src/Api/Db/Repositories/BulkTransactionsRepository.cs", "BulkMoveAccountAsync"),
        ],
    };

    /// <summary>Input writes that need no recompute, each with its reason.</summary>
    private static readonly Dictionary<string, string> NeedsNone = new(StringComparer.Ordinal)
    {
        ["src/Api/Db/Repositories/TagsRepository.cs:CleanupUnusedAsync"] =
            "deletes only tags no header or leg carries, which no stored presentation names",
        ["src/Api/Db/Repositories/AccountsRepository.cs:DeleteCategoryAsync"] =
            "deletes only a category no leg references, which no stored presentation reads",
        ["src/Api/Db/Repositories/BulkTransactionsRepository.cs:BulkDeleteAsync"] =
            "deletes whole headers, whose stored rows cascade with their legs; the rows it un-merges keep every input",
    };

    /// <summary>Statements whose root the scan cannot name, each with what it writes.</summary>
    private static readonly Dictionary<string, string> UnresolvedRoots = new(StringComparer.Ordinal)
    {
        ["src/Api/Endpoints/SnapshotsEndpoints.cs:RestoreAsync"] = "the consistency verdict, not an input",
    };

    /// <summary>
    /// The third class of mapped function: it writes inputs and the stored presentation
    /// together, copying both back verbatim from a snapshot, and must NOT recompute (a
    /// restore refuses a snapshot of another schema version, so the copied values are
    /// the rule's for the copied inputs).
    /// </summary>
    private const string VerbatimRestoreCopy = "verbatim restore copy, no recompute";

    /// <summary>The functions allowed in <see cref="VerbatimRestoreCopy"/>: exactly the two snapshot restores.</summary>
    private static readonly string[] VerbatimRestoreCopies = ["LedgerSnapshotRestore", "LedgerSnapshotRestoreStored"];

    /// <summary>
    /// Every database function <c>AppDbContext</c> maps, by its C# name: the recompute
    /// each call site must make after it, <see cref="VerbatimRestoreCopy"/>, or null for a
    /// function that writes no input.
    /// </summary>
    private static readonly Dictionary<string, string?> DbFunctions = new(StringComparer.Ordinal)
    {
        // A restore replaces the ledger's rows, the stored presentation with them.
        ["LedgerSnapshotRestore"] = VerbatimRestoreCopy,
        ["LedgerSnapshotRestoreStored"] = VerbatimRestoreCopy,
        ["AccountBalanceAsOfInstants"] = null,
        ["AccountBalanceWalk"] = null,
        ["AccountBalancesAsOfNow"] = null,
        ["HoldingsAsOfNow"] = null,
        ["HoldingsCostBasisAsOf"] = null,
        ["HoldingsFifoOpenLots"] = null,
        ["HoldingsOpenLotsAsOf"] = null,
        ["RealizedGainLots"] = null,
        ["HoldingsFifoShortfall"] = null,
        ["HoldingsFifoShortfalls"] = null,
        ["HoldingsMarketValueAsOfSet"] = null,
        ["LedgerDelete"] = null,
        ["LedgerPayeeSuggestions"] = null,
        ["LedgerSnapshotPayload"] = null,
        ["LedgerSnapshotStoredSizes"] = null,
        ["LedgerSnapshotWrite"] = null,
        ["RealizedGainsWalk"] = null,
        ["RecomputeBalancesForAccount"] = null,
        ["RecomputeHoldingsForAccountSecurity"] = null,
        ["RecomputeHoldingsForBrokerage"] = null,
        ["RecomputePostingCountsForHeader"] = null,
        ["ReminderEstimateSamples"] = null,
        ["ReminderOccurrenceLock"] = null,
        // The ledger write lock (migration 275), in its transaction and session forms: an
        // advisory lock and a transaction-local setting, no row.
        ["LedgerWriteLockFn"] = null,
        ["LedgerWriteSessionLockFn"] = null,
        ["LedgerWriteSessionUnlockFn"] = null,
        // Row locks only: the recompute's own, and the ones a writer takes before it reads
        // which headers carry a tag or have a leg on an account.
        ["LockTxnHeaders"] = null,
        ["LockTags"] = null,
        ["LockAccounts"] = null,
        // Row locks a trade-price writer takes on the accounts whose currency it reads, and
        // the read of which accounts those are.
        ["LockTradePriceAccounts"] = null,
        ["TradePriceAccounts"] = null,
        // The row lock merge_securities takes on the source before it reads its lines.
        ["LockSecurities"] = null,
        ["RestoreReadiness"] = null,
        ["SavedReportsRepointTag"] = null,
        ["SecurityPriceUpsertFromTrade"] = null,
        ["SecurityPricesAsOfNow"] = null,
        ["SnapshotLedgerLock"] = null,
        ["SnapshotLedgerUnlock"] = null,
        ["TagNameKey"] = null,
        ["TagNameKeys"] = null,
        ["TradePriceCheck"] = null,
        ["TradePriceReseed"] = null,
        ["UnbackedPriceDates"] = null,
        ["UnbackedPriceSecurities"] = null,
    };

    /// <summary>
    /// Functions classified as writing no input whose SQL does write an input table
    /// directly, each with why no stored presentation changes.
    /// </summary>
    private static readonly Dictionary<string, string> WritesInputTableNotInput = new(StringComparer.Ordinal)
    {
        ["ledger_delete"] = "deletes the whole ledger; its stored rows go with it",
        ["recompute_posting_counts_for_header"] =
            "sets txn_legs' posting counts, which the rule does not read (it counts the postings itself)",
    };

    /// <summary>Import calls that do not compute the stored rows, each with its reason.
    /// Empty: the background import job (ImportJobRunner, which the hub's Demo also runs
    /// through) is the only caller, and it computes (the CLI keeps only its read-only
    /// diagnostics, ADR-0015).</summary>
    private static readonly Dictionary<string, string> ImportNotWired = new(StringComparer.Ordinal);

    /// <summary>What an import call must pass to compute the stored rows in its transaction.</summary>
    private const string ImportRecompute = "beforeCommit: LegNotes.RecomputeLedgerOnAsync";

    /// <summary>
    /// The same, as a closure that composes more pre-commit work after it: its first
    /// statement awaits the recompute on the transaction and ledger the closure was handed,
    /// so the stored rows are still computed in the import's own transaction. A closure
    /// that passes anything else, or does other work first, does not match.
    /// </summary>
    private static readonly Regex ImportRecomputeClosure = new(
        @"beforeCommit:\s*async\s*\(\s*(?<tx>\w+)\s*,\s*(?<ledger>\w+)\s*,\s*(?<ct>\w+)\s*\)\s*=>\s*\{\s*"
        + @"await\s+LegNotes\.RecomputeLedgerOnAsync\(\s*\k<tx>\s*,\s*\k<ledger>\s*,\s*\k<ct>\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// The entity type of each input table, read from the application context's DbSet of
    /// that name: what the save interceptor must handle is what the catalog names, so a
    /// table added to <see cref="Inputs"/> is checked without a second list to keep.
    /// </summary>
    private static Dictionary<string, Type> InputEntities()
    {
        var sets = typeof(AppDbContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(p => p.Name, p => p.PropertyType.GetGenericArguments()[0], StringComparer.Ordinal);
        var missing = Inputs.Keys.Where(k => !sets.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"the catalog names [{string.Join(", ", missing)}], which AppDbContext has no DbSet for");
        return Inputs.Keys.ToDictionary(k => k, k => sets[k], StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\.Execute(Update|Delete)Async\(")]
    private static partial Regex SetBased();

    [GeneratedRegex(@"\b_?db\.(\w+)\b")]
    private static partial Regex Root();

    [GeneratedRegex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.(\w+)")]
    private static partial Regex SetColumn();

    [GeneratedRegex(@"(?:public|private|internal|protected)[^;{}=]*?\s(\w+)\s*(?:<[^>]*>)?\s*\(")]
    private static partial Regex MethodHeader();

    [GeneratedRegex(@"GetMethod\(nameof\((\w+)\)[\s\S]*?\.HasName\(""(\w+)""\)")]
    private static partial Regex DbFunctionMapping();

    [Fact]
    public void The_rules_inputs_are_the_catalogs_inputs()
    {
        // The input records: every field is catalogued.
        var fields = new[] { typeof(NotesHeaderInput), typeof(NotesLegInput) }
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract")
                .Select(p => $"{t.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(RecordFields.Keys.Order(StringComparer.Ordinal), fields);
        foreach (var source in RecordFields.Values.Where(v => v.Contains('.', StringComparison.Ordinal)))
        {
            var (set, column) = (source.Split('.')[0], source.Split('.')[1]);
            Assert.Contains(column, Inputs[set]);
        }

        // The compute takes the header and the brokerages' holdings links, nothing else.
        var compute = typeof(StoredNotes).GetMethod(nameof(StoredNotes.Compute))!;
        Assert.Equal(
            new[] { typeof(NotesHeaderInput), typeof(IReadOnlyDictionary<Guid, Guid>) },
            compute.GetParameters().Select(p => p.ParameterType));

        // What LegNotes reads from each table is exactly its input columns.
        var root = RepoRoot();
        var legNotes = CodeOnly(File.ReadAllText(Path.Combine(root, "src", "Api", "Db", "LegNotes.cs")));
        var body = MethodBody(legNotes, "ReadInputsAsync");
        var read = Regex.Matches(body, @"db\.(\w+)\.AsNoTracking\(\)[\s\S]*?\.Select\(\s*(\w+)\s*=>\s*new\s*\{([^}]*)\}")
            .ToDictionary(
                m => m.Groups[1].Value,
                m => Regex.Matches(m.Groups[3].Value, $@"\b{m.Groups[2].Value}\.(\w+)")
                    .Select(c => c.Groups[1].Value)
                    .Where(c => c is not ("Id" or "LedgerId"))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        foreach (var (set, columns) in Inputs.Where(i => i.Key != "Tags"))
        {
            Assert.True(read.TryGetValue(set, out var actual), $"LegNotes.ReadInputsAsync does not read {set}");
            Assert.True(actual.SetEquals(columns),
                $"LegNotes.ReadInputsAsync reads {set}.[{string.Join(", ", actual.Order())}]; the catalog lists [{string.Join(", ", columns)}]");
        }
        Assert.Empty(read.Keys.Except(Inputs.Keys));

        // The upgrade's frozen projection maps exactly the input columns of each input
        // table, beside the keys: what it can read is what the rule reads.
        var backfill = CodeOnly(File.ReadAllText(Path.Combine(root, "src", "Api", "Migrations", "LegNotesBackfill.cs")));
        var mapped = Regex.Matches(backfill, @"ToTable\(""(\w+)""\)([\s\S]*?)\}\);")
            .ToDictionary(
                m => m.Groups[1].Value,
                m => Regex.Matches(m.Groups[2].Value, @"HasColumnName\(""(\w+)""\)")
                    .Select(c => c.Groups[1].Value)
                    .Where(c => c is not ("id" or "ledger_id"))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        foreach (var (table, columns) in InputColumns.Where(i => i.Key != "tags"))
        {
            Assert.True(mapped.TryGetValue(table, out var actual), $"the backfill's projection does not map {table}");
            Assert.True(actual.SetEquals(columns),
                $"the backfill's projection maps {table}.[{string.Join(", ", actual.Order())}]; the catalog lists [{string.Join(", ", columns)}]");
        }
        Assert.Empty(mapped.Keys.Except(InputColumns.Keys));
        // The C# and SQL catalogs name the same columns.
        foreach (var (set, columns) in Inputs)
        {
            var table = Regex.Replace(set, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
            Assert.Equal(
                columns.Select(c => Regex.Replace(c, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()).Order(StringComparer.Ordinal),
                InputColumns[table].Order(StringComparer.Ordinal));
        }

        // The interceptor watches exactly the input columns of every modified input row
        // whose catalogued columns are not just its key: a link row's key is its whole
        // value, and a tag is an input only by its deletion.
        var interceptor = CodeOnly(File.ReadAllText(Path.Combine(root, "src", "Api", "Db", "LegNotesRecomputeInterceptor.cs")));
        using var model = ModelOnly(typeof(AppDbContext))!;
        var watchedSets = 0;
        foreach (var (set, type) in InputEntities())
        {
            var key = model.Model.FindEntityType(type)!.FindPrimaryKey()!.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (Inputs[set].All(key.Contains)) continue;
            watchedSets++;
            var entity = type.Name;
            var start = interceptor.IndexOf($"case {entity} ", StringComparison.Ordinal);
            Assert.True(start >= 0, $"the interceptor has no case for {entity}");
            var end = interceptor.IndexOf("break;", start, StringComparison.Ordinal);
            var watched = Regex.Matches(interceptor[start..end], $@"Modified\(entry, nameof\({entity}\.(\w+)\)\)")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(watched.SetEquals(Inputs[set]),
                $"the interceptor watches {entity}.[{string.Join(", ", watched.Order())}]; the catalog lists [{string.Join(", ", Inputs[set])}]");
        }
        Assert.True(watchedSets >= 3, $"checked the watched columns of only {watchedSets} input tables; the check is not finding them");
    }

    [Fact]
    public void Every_set_based_write_of_an_input_recomputes_the_stored_notes()
    {
        var root = RepoRoot();
        var code = SourceFiles(root, Path.Combine("src", "Api"));
        Assert.True(code.Count > 300, $"scanned only {code.Count} files; the guard is not looking at the API");

        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var setBased = 0;
        foreach (var (file, text) in code)
        {
            foreach (Match m in SetBased().Matches(text))
            {
                setBased++;
                var statement = Statement(text, m.Index);
                var methodName = EnclosingMethod(text, m.Index);
                var method = $"{file}:{methodName}";
                var rootSet = RootOf(text, m.Index, statement);
                if (rootSet is null)
                {
                    seen.Add(method);
                    if (!UnresolvedRoots.ContainsKey(method))
                        violations.Add($"{method}: a set-based write whose root the scan cannot name; list it with what it writes\n    {Flat(statement)}");
                    continue;
                }
                if (!Inputs.TryGetValue(rootSet, out var columns)) continue;
                var deletes = m.Groups[1].Value == "Delete";
                var setters = SetColumn().Matches(statement).Select(c => c.Groups[1].Value).ToList();
                // A setter built outside the statement cannot be read here, so it counts as
                // writing an input until it is listed.
                var opaque = !deletes && setters.Count == 0;
                var setsInput = setters.Any(c => columns.Contains(c, StringComparer.Ordinal));
                if (!deletes && !setsInput && !opaque) continue;

                seen.Add(method);
                if (NeedsNone.ContainsKey(method)) continue;
                if (Delegated.TryGetValue(method, out var callers))
                {
                    CheckDelegated(code, file, methodName, callers, violations);
                    continue;
                }
                if (!RecomputesAfter(text, m.Index))
                    violations.Add($"{method}: writes {rootSet} set-based{(opaque ? " (setter built outside the statement)" : "")} and does not call LegNotes.Recompute* after it\n    {Flat(statement)}");
            }
        }

        Assert.True(setBased > 50, $"found only {setBased} set-based writes; the scan is not matching them");
        var stale = Delegated.Keys.Concat(NeedsNone.Keys).Concat(UnresolvedRoots.Keys).Where(k => !seen.Contains(k)).ToList();
        violations.AddRange(stale.Select(k => $"{k}: listed here but no such write exists any more; remove the entry"));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void Every_mapped_database_function_is_classified_and_a_writer_of_an_input_recomputes()
    {
        var root = RepoRoot();
        var context = CodeOnly(File.ReadAllText(Path.Combine(root, "src", "Api", "Db", "AppDbContext.cs")));
        var mapped = DbFunctionMapping().Matches(context)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        Assert.True(mapped.Count > 30, $"found only {mapped.Count} mapped functions; the scan is not matching them");

        var violations = new List<string>();
        violations.AddRange(mapped.Keys.Except(DbFunctions.Keys)
            .Select(f => $"{f} ({mapped[f]}): a mapped database function the catalog does not classify"));
        violations.AddRange(DbFunctions.Keys.Except(mapped.Keys)
            .Select(f => $"{f}: classified here but no longer mapped; remove the entry"));

        var migrations = Directory.GetFiles(Path.Combine(root, "db", "migrations"), "*.sql")
            .Order(StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToList();
        var writesInput = WritesInputTable();
        var defined = migrations
            .SelectMany(m => Regex.Matches(m.Text, @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?(\w+)\s*\(", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var flagged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (function, sqlName) in mapped)
        {
            if (!DbFunctions.TryGetValue(function, out var recompute) || recompute is not null) continue;
            if (LatestDefinition(migrations, sqlName) is null)
            {
                violations.Add($"{function}: no definition of {sqlName} found in db/migrations");
                continue;
            }
            // The function and every function it calls, at their latest definitions.
            var reached = new HashSet<string>(StringComparer.Ordinal) { sqlName };
            var pending = new Queue<string>(reached);
            var writes = false;
            while (pending.Count > 0)
            {
                var body = LatestDefinition(migrations, pending.Dequeue());
                if (body is null) continue;
                writes |= writesInput.IsMatch(body);
                foreach (Match call in Regex.Matches(body, @"\b(\w+)\s*\("))
                {
                    var callee = call.Groups[1].Value.ToLowerInvariant();
                    if (defined.Contains(callee) && reached.Add(callee)) pending.Enqueue(callee);
                }
            }
            if (!writes) continue;
            flagged.Add(sqlName);
            if (!WritesInputTableNotInput.ContainsKey(sqlName))
                violations.Add($"{function} ({sqlName}) is classified as writing no input, but its SQL writes an input table: classify it or give the reason");
        }
        violations.AddRange(WritesInputTableNotInput.Keys.Where(k => !flagged.Contains(k))
            .Select(k => $"{k}: listed as writing an input table but its SQL no longer does; remove the entry"));

        // The verbatim class admits exactly the two restores, and their call sites do not
        // recompute what they copied back.
        var code = SourceFiles(root, Path.Combine("src", "Api"));
        var verbatim = DbFunctions.Where(f => f.Value == VerbatimRestoreCopy).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
        if (!verbatim.SequenceEqual(VerbatimRestoreCopies.Order(StringComparer.Ordinal)))
            violations.Add($"[{string.Join(", ", verbatim)}] are classified as a {VerbatimRestoreCopy}; only "
                           + $"[{string.Join(", ", VerbatimRestoreCopies)}] may be");
        foreach (var function in verbatim)
        {
            var calls = 0;
            foreach (var (file, text) in code)
            {
                foreach (Match m in Regex.Matches(text, $@"\.{function}\("))
                {
                    calls++;
                    if (RecomputesAfter(text, m.Index))
                        violations.Add($"{file}:{EnclosingMethod(text, m.Index)} recomputes after {function}, which copies the stored presentation back verbatim");
                }
            }
            if (calls == 0) violations.Add($"{function}: no call site found; the scan is not matching it");
        }

        // Every call of a function that writes an input recomputes after it.
        foreach (var (function, recompute) in DbFunctions.Where(f => f.Value is not null && f.Value != VerbatimRestoreCopy))
        {
            var calls = 0;
            foreach (var (file, text) in code)
            {
                foreach (Match m in Regex.Matches(text, $@"\.{function}\("))
                {
                    calls++;
                    if (!RecomputesAfter(text, m.Index, recompute!))
                        violations.Add($"{file}:{EnclosingMethod(text, m.Index)} calls {function} without {recompute} after it");
                }
            }
            if (calls == 0) violations.Add($"{function}: no call site found; the scan is not matching it");
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>A statement that writes rows of an input table, in any of SQL's forms.</summary>
    private static Regex WritesInputTable() => new(
        $@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE(?:\s+TABLE)?|COPY)\s+(?:ONLY\s+)?(?:public\.)?(?:{string.Join('|', InputTables)})\b",
        RegexOptions.IgnoreCase);

    /// <summary>The SQL columns the rule reads, by input table: an UPDATE that sets one changes an input.</summary>
    internal static readonly Dictionary<string, string[]> InputColumns = new(StringComparer.Ordinal)
    {
        ["txn_headers"] = ["memo", "action", "originating_account_id"],
        ["txn_legs"] = ["account_id", "posting_index", "leg_memo", "header_id"],
        ["txn_header_tags"] = ["header_id", "tag_id"],
        ["txn_leg_tags"] = ["leg_id", "tag_id"],
        ["tags"] = [],
        ["accounts"] = ["holdings_account_id"],
    };

    /// <summary>
    /// Migrations from 264 on whose statements change an input of the rule with no
    /// recompute step after them, each with why no stored value changes.
    /// </summary>
    private static readonly Dictionary<string, string> MigrationsWritingInputsWithoutRecompute = new(StringComparer.Ordinal);

    /// <summary>
    /// How <paramref name="sql"/>, a migration script, changes an input of the rule: an
    /// insert, delete, merge, truncate or copy of an input table, or an UPDATE whose SET
    /// names an input column; in its own statements, its DO blocks, or the functions they
    /// call. The bodies of functions it only defines run later and are not its writes.
    /// </summary>
    private static IEnumerable<string> InputWrites(
        string sql, List<(string Name, string Text)> migrations, HashSet<string> defined)
    {
        var code = Regex.Replace(Regex.Replace(sql, @"/\*[\s\S]*?\*/", " "), @"--[^\n]*", " ");
        // A function definition's body runs when called, not when the script runs.
        code = Regex.Replace(code,
            @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\b[\s\S]*?(\$\w*\$)[\s\S]*?\1", " ", RegexOptions.IgnoreCase);
        var bodies = new List<(string Where, string Sql)> { ("script", code) };
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([code]);
        while (pending.Count > 0)
        {
            foreach (Match call in Regex.Matches(pending.Dequeue(), @"\b(\w+)\s*\("))
            {
                var callee = call.Groups[1].Value.ToLowerInvariant();
                if (!defined.Contains(callee) || !reached.Add(callee)) continue;
                var body = LatestDefinition(migrations, callee);
                if (body is null) continue;
                bodies.Add(($"function {callee}", body));
                pending.Enqueue(body);
            }
        }
        const RegexOptions i = RegexOptions.IgnoreCase;
        foreach (var (where, body) in bodies)
        {
            foreach (Match m in WritesInputTable().Matches(body))
            {
                var statement = body[m.Index..(body.IndexOf(';', m.Index) is var end and >= 0 ? end : body.Length)];
                var update = Regex.Match(statement,
                    $@"^UPDATE\s+(?:ONLY\s+)?(?:public\.)?({string.Join('|', InputTables)})\b[^;]*?\bSET\b([\s\S]*?)(?:\bWHERE\b|\bFROM\b|\bRETURNING\b|$)", i);
                if (update.Success)
                {
                    var columns = InputColumns[update.Groups[1].Value.ToLowerInvariant()];
                    if (columns.Length == 0 || !Regex.IsMatch(update.Groups[2].Value, $@"\b(?:{string.Join('|', columns)})\s*=", i))
                        continue;
                }
                else if (Regex.IsMatch(statement, @"^COPY\b[^;]*?\bTO\b", i))
                {
                    continue;
                }
                yield return $"{where}: {Flat(statement)}";
            }
        }
    }

    /// <summary>The C# migration steps, by the script each runs after, that recompute the stored notes.</summary>
    private static Dictionary<string, string> RecomputeSteps(string root)
    {
        // A recompute step is a C# step the runner registers (its one list) whose class
        // stores what the rule computes.
        var types = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(Coffer.Api.Migrations.MigrationRunner.RecomputeStep).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in Coffer.Api.Migrations.MigrationRunner.CSharpSteps)
        {
            var type = types.SingleOrDefault(t =>
                t.GetField("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string == step.Name);
            var file = type is null ? null
                : Directory.EnumerateFiles(Path.Combine(root, "src", "Api"), $"{type.Name}.cs", SearchOption.AllDirectories).FirstOrDefault();
            if (file is null) continue;
            var code = CodeOnly(File.ReadAllText(file));
            if (code.Contains("LegNotes.ComputeFrom(", StringComparison.Ordinal) || code.Contains("LegNotes.Recompute", StringComparison.Ordinal))
                result[step.AfterScript] = step.Name;
        }
        return result;
    }

    [Fact]
    public void Every_migration_that_changes_an_input_is_followed_by_a_recompute_step()
    {
        // A data migration that rewrites a memo, moves legs or changes tags changes what
        // the rule computes on every install it runs on; its stored values are then stale
        // until something recomputes them, so a C# step after it must, or it is listed.
        var root = RepoRoot();
        var migrations = Directory.GetFiles(Path.Combine(root, "db", "migrations"), "*.sql")
            .Order(StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToList();
        var defined = migrations
            .SelectMany(m => Regex.Matches(m.Text, @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?(\w+)\s*\(", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var steps = RecomputeSteps(root);
        Assert.True(steps.ContainsKey("264_annotation_data.sql"), "the backfill step after 264 is not found as a recompute step; the check is not finding steps");

        var violations = new List<string>();
        var scanned = 0;
        foreach (var (name, text) in migrations.Where(m => string.CompareOrdinal(m.Name, "264") >= 0))
        {
            scanned++;
            var writes = InputWrites(text, migrations, defined).ToList();
            if (writes.Count == 0 || steps.ContainsKey(name) || MigrationsWritingInputsWithoutRecompute.ContainsKey(name)) continue;
            violations.Add($"{name} changes an input of the stored notes with no recompute step after it:\n    "
                           + string.Join("\n    ", writes));
        }
        Assert.True(scanned > 0, "no migration from 264 on was scanned");
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_migration_scan_finds_each_way_to_change_an_input()
    {
        var migrations = new List<(string Name, string Text)>
        {
            ("100_x.sql", "CREATE FUNCTION fn_move(p uuid) RETURNS void LANGUAGE sql AS $$ UPDATE txn_legs SET account_id = p; $$;"),
        };
        var defined = new HashSet<string>(["fn_move"], StringComparer.Ordinal);
        Assert.Single(InputWrites("UPDATE txn_headers SET memo = trim(memo) WHERE memo <> trim(memo);", migrations, defined));
        Assert.Single(InputWrites("UPDATE txn_legs l SET header_id = x.h FROM moved x WHERE x.id = l.id;", migrations, defined));
        Assert.Single(InputWrites("DO $$ BEGIN DELETE FROM txn_leg_tags WHERE tag_id IS NULL; END $$;", migrations, defined));
        Assert.Single(InputWrites("INSERT INTO txn_header_tags (header_id, tag_id, ledger_id) SELECT 1, 2, 3;", migrations, defined));
        Assert.Single(InputWrites("MERGE INTO tags t USING s ON s.id = t.id WHEN MATCHED THEN DELETE;", migrations, defined));
        // Through a function the script calls.
        Assert.Single(InputWrites("SELECT fn_move(id) FROM accounts;", migrations, defined));
        // Not inputs: another column, a filter on an input column, a function only defined, a read.
        Assert.Empty(InputWrites("UPDATE txn_legs SET amount = 1 WHERE account_id = $1;", migrations, defined));
        Assert.Empty(InputWrites("UPDATE txn_headers SET needs_review = false WHERE memo = '';", migrations, defined));
        Assert.Empty(InputWrites("CREATE OR REPLACE FUNCTION fn_y() RETURNS void LANGUAGE sql AS $$ DELETE FROM txn_legs; $$;", migrations, defined));
        Assert.Empty(InputWrites("COPY txn_legs TO STDOUT; SELECT memo FROM txn_headers;", migrations, defined));
    }

    [Fact]
    public void Every_moneydance_import_call_computes_the_stored_notes_in_its_transaction()
    {
        var root = RepoRoot();
        var code = SourceFiles(root, Path.Combine("src", "Api"))
            .Concat(SourceFiles(root, Path.Combine("src", "Importer.Moneydance.Cli")))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var calls = 0;
        foreach (var (file, text) in code)
        {
            // The Moneydance import is the one ImportAsync that takes an open connection first.
            foreach (Match m in Regex.Matches(text, @"\.ImportAsync\(\s*connection\b"))
            {
                calls++;
                var method = $"{file}:{EnclosingMethod(text, m.Index)}";
                seen.Add(method);
                var call = Statement(text, m.Index);
                if (call.Contains(ImportRecompute, StringComparison.Ordinal)) continue;
                if (ImportRecomputeClosure.IsMatch(call)) continue;
                if (!ImportNotWired.ContainsKey(method))
                    violations.Add($"{method}: imports without {ImportRecompute}");
            }
        }
        // Fail closed: the background import job is the call every in-app import goes
        // through, the hub's Demo included, so a scan that misses it matches nothing.
        Assert.True(calls >= 1, $"found only {calls} import calls; the scan is not matching them");
        Assert.True(seen.Any(s => s.StartsWith("src/Api/Import/ImportJobRunner.cs:", StringComparison.Ordinal)),
            "the scan did not find the import job's call: " + string.Join(", ", seen));
        violations.AddRange(ImportNotWired.Keys.Where(k => !seen.Contains(k))
            .Select(k => $"{k}: listed as not wired but no such call exists any more; remove the entry"));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_save_interceptor_is_registered_last_wherever_a_context_is_built()
    {
        // It commits the transaction it opened after the save's other SavedChanges work:
        // registered before another recompute interceptor, that one's work would run after
        // the commit, outside the save's transaction.
        //
        // The service factory appends a test's extra interceptors (a fault seam, empty in the
        // app) after production's own, so a trailing spread of them there is not production's
        // list: the check reads the element before it.
        var factory = Path.Combine("src", "Api", "Db", "ServiceDbContextFactory.cs");
        var root = RepoRoot();
        var sites = new[] { Path.Combine("src", "Api", "Program.cs"), factory }
            .SelectMany(f => Regex.Matches(CodeOnly(File.ReadAllText(Path.Combine(root, f))), @"\.AddInterceptors\(([^;]*?)\)\s*(?:;|\.)")
                .Select(m => (File: f, Args: m.Groups[1].Value.Trim().TrimStart('[').TrimEnd(']')
                    .Split(',').Select(a => a.Trim()).ToList())))
            .ToList();
        Assert.True(sites.Count >= 3, $"found only {sites.Count} interceptor registrations; the scan is not matching them");
        foreach (var (file, args) in sites)
        {
            var production = file == factory && Regex.IsMatch(args[^1], @"^\.\.\s*_?extra$") ? args[..^1] : args;
            Assert.True(production.Count > 0 && Regex.IsMatch(production[^1], @"(?i)notes"),
                $"{file}: AddInterceptors({string.Join(", ", args)}) does not register the stored-notes interceptor last");
        }
    }

    [Fact]
    public void The_save_interceptor_handles_every_input_entity()
    {
        var text = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Api", "Db", "LegNotesRecomputeInterceptor.cs")));
        var entities = InputEntities();
        Assert.Equal(Inputs.Count, entities.Count);
        var unhandled = entities.Values.Where(t => !Regex.IsMatch(text, $@"case {t.Name}\b")).Select(t => t.Name).ToList();
        Assert.True(unhandled.Count == 0, $"the save interceptor has no case for the input entities [{string.Join(", ", unhandled)}]");
    }

    /// <summary>The table of the stored presentation's tags.</summary>
    internal const string StoredTagTable = "txn_leg_note_tags";

    /// <summary>The file that writes the stored presentation at run time.</summary>
    private const string Writer = "src/Api/Db/LegNotes.cs";

    /// <summary>The upgrade's backfill's own store, frozen at 264: the one other writer.</summary>
    private const string BackfillWriter = "src/Api/Migrations/LegNotesBackfill.Store.cs";

    /// <summary>
    /// SQL that copies whole rows of the stored presentation (<c>txn_legs</c> with its
    /// stored columns, or <c>txn_leg_note_tags</c>), by the function it sits in: each with
    /// why it is no write the recompute misses. Every mapped function that reaches one
    /// must be a <see cref="VerbatimRestoreCopy"/> or recompute.
    /// </summary>
    private static readonly Dictionary<string, string> CopiesWholeLegRows = new(StringComparer.Ordinal)
    {
        ["fn_ledger_snapshot_restore"] =
            "a restore copies the captured rows back as they were, the stored presentation with the inputs it was computed from",
        ["fn_snapshot_restore_part"] =
            "the same, one stored chunk at a time",
    };

    /// <summary>SQL that deletes a whole ledger's stored tag rows, by function, each with its reason.</summary>
    private static readonly Dictionary<string, string> DeletesLedgerTagRows = new(StringComparer.Ordinal)
    {
        ["fn_ledger_snapshot_clear"] = "a restore clears the ledger's rows before it copies the snapshot's back",
        ["fn_ledger_delete"] = "deletes the whole ledger",
    };

    /// <summary>The stored presentation's columns on <c>txn_legs</c>, as migration 264 adds them.</summary>
    internal static string[] StoredColumns(string root)
    {
        var sql = File.ReadAllText(Path.Combine(root, "db", "migrations", "264_annotation_data.sql"));
        var columns = Regex.Matches(sql, @"ADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?(notes_\w+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(columns.Length >= 10, $"found only {columns.Length} stored columns in 264; the scan is not reading it");
        return columns;
    }

    [GeneratedRegex(@"\.(?:Add|AddRange|Remove|RemoveRange|Update|UpdateRange|Attach|AttachRange|Execute(?:Update|Delete)Async)\(")]
    private static partial Regex WriteCall();

    [Fact]
    public void No_model_but_the_recompute_maps_the_stored_presentation_for_writing()
    {
        var stored = StoredColumns(RepoRoot()).ToHashSet(StringComparer.Ordinal);
        var contexts = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.Contains(typeof(AppDbContext), contexts);
        var store = Assert.Single(contexts, t => t.DeclaringType == typeof(LegNotes));
        Assert.True(store.IsNestedPrivate, "the recompute's context must be private to LegNotes");
        var backfillStore = BackfillStore(contexts);

        var violations = new List<string>();
        var storeWrites = 0;
        var insertOnly = 0;
        var readMappings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in contexts)
        {
            using var context = ModelOnly(type);
            if (context is null)
            {
                violations.Add($"{type.FullName}: a context whose model this check cannot build; make it constructible from options or check it here");
                continue;
            }
            foreach (var entity in StoredMappings(context.Model, stored))
            {
                // A keyless entity cannot be added, attached or removed, but a set-based
                // write can still start from it: it must be a listed read mapping, and the
                // source scan holds its DbSet to reads.
                if (type == store || type == backfillStore) storeWrites++;
                else if (type == typeof(AppDbContext) && entity.ClrType.Name == InsertOnlyEntity)
                {
                    // The leg itself, whose stored columns may be set before its insert
                    // only: each throws when set after the save, and has no public setter.
                    insertOnly++;
                    foreach (var property in StoredProperties(entity, stored))
                    {
                        if (property.GetAfterSaveBehavior() != PropertySaveBehavior.Throw)
                            violations.Add($"{entity.DisplayName()}.{property.Name} maps a stored column without an after-save behaviour of Throw; it would be written by an update");
                        if (property.PropertyInfo?.SetMethod is { IsPublic: true } or { IsAssembly: true })
                            violations.Add($"{entity.DisplayName()}.{property.Name} has a setter outside the entity; only its StampNotes may set it");
                    }
                }
                else if (entity.FindPrimaryKey() is not null)
                    violations.Add($"{type.Name} maps {entity.DisplayName()} on {StoreName(entity)} with a stored column for writing; only LegNotes writes them");
                else if (!ReadMappings.ContainsKey(entity.ClrType.Name))
                    violations.Add($"{type.Name} maps {entity.DisplayName()} on {StoreName(entity)}, keyless, with a stored column: list it as a read mapping with its reason");
                else readMappings.Add(entity.ClrType.Name);
            }
        }
        // The recompute's own model and the backfill's map both, and the leg maps its
        // stored columns insert-only, so the check looks at what it means to.
        Assert.Equal(4, storeWrites);
        Assert.Equal(1, insertOnly);
        violations.AddRange(ReadMappings.Keys.Where(k => !readMappings.Contains(k))
            .Select(k => $"{k}: listed as a read mapping of the stored presentation but no model maps it so; remove the entry"));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>The upgrade backfill's own store: the context private to <c>LegNotesBackfill</c> that maps the stored presentation.</summary>
    private static Type BackfillStore(IReadOnlyCollection<Type> contexts)
    {
        var stored = StoredColumns(RepoRoot()).ToHashSet(StringComparer.Ordinal);
        var writers = contexts
            .Where(t => t.DeclaringType == typeof(Coffer.Api.Migrations.LegNotesBackfill))
            .Where(t =>
            {
                using var context = ModelOnly(t);
                return context is not null && StoredMappings(context.Model, stored).Any();
            })
            .ToList();
        var store = Assert.Single(writers);
        Assert.True(store.IsNestedPrivate, "the backfill's store must be private to the step");
        return store;
    }

    [Fact]
    public void The_backfill_writes_through_a_store_of_its_own_frozen_at_264s_columns()
    {
        // The step runs directly after 264 on every upgrade that spans it, before any later
        // migration. Writing through the application's model, a later rename or a new NOT
        // NULL column of the stored presentation would make it write columns 264 never
        // created, and the upgrade would fail on every start. Its own store maps exactly
        // what 264 creates.
        var root = RepoRoot();
        var sql = File.ReadAllText(Path.Combine(root, "db", "migrations", "264_annotation_data.sql"));
        var table = Regex.Match(sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?txn_leg_note_tags\s*\(([\s\S]*?)\n\);", RegexOptions.IgnoreCase);
        Assert.True(table.Success, "264 does not create txn_leg_note_tags as the scan reads it");
        var tagColumns = Regex.Matches(table.Groups[1].Value, @"^\s+(\w+)\s+(?:uuid|boolean|text)\b", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(tagColumns.Count >= 8, $"found only [{string.Join(", ", tagColumns)}] in 264's txn_leg_note_tags");
        var legColumns = StoredColumns(root).Append("id").Append("header_id").ToHashSet(StringComparer.Ordinal);

        var contexts = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        var store = BackfillStore(contexts);
        Assert.NotEqual(typeof(LegNotes), store.DeclaringType);
        using var context = ModelOnly(store)!;
        var mapped = context.Model.GetEntityTypes().ToDictionary(
            e => e.GetTableName()!,
            e => e.GetProperties()
                .Select(p => p.GetColumnName(StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema()))!)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.Equal(["txn_leg_note_tags", "txn_legs"], mapped.Keys.Order(StringComparer.Ordinal));
        Assert.True(mapped["txn_legs"].SetEquals(legColumns),
            $"the backfill's store maps txn_legs.[{string.Join(", ", mapped["txn_legs"].Order())}]; 264 creates [{string.Join(", ", legColumns.Order())}]");
        Assert.True(mapped["txn_leg_note_tags"].SetEquals(tagColumns),
            $"the backfill's store maps txn_leg_note_tags.[{string.Join(", ", mapped["txn_leg_note_tags"].Order())}]; 264 creates [{string.Join(", ", tagColumns.Order())}]");

        // And it writes through that store only, never through LegNotes' writers.
        var code = string.Join('\n', new[] { "LegNotesBackfill.cs", "LegNotesBackfill.Store.cs" }
            .Select(f => CodeOnly(File.ReadAllText(Path.Combine(root, "src", "Api", "Migrations", f)))));
        Assert.DoesNotMatch(new Regex(@"\bLegNotes\.(?:Recompute\w*|Stamp|Store\w*)\s*\("), code);
    }

    /// <summary>
    /// Keyless entities of the application's model that map a stored column or the tag
    /// table, as a table or as a view, each with what it is read for. Their DbSets are
    /// never the root of a write (<see cref="StoredWrites"/>).
    /// </summary>
    private static readonly Dictionary<string, string> ReadMappings = new(StringComparer.Ordinal)
    {
        ["TxnLegNoteTagRow"] = "the tag filters, tag cells and tag names read the stored tag rows",
        ["StoredLegNotesView"] = "the listing's text search narrows by the stored memos before it joins, and the reminder readers read a template's legs, which the view leaves out",
    };

    /// <summary>The entities of <paramref name="model"/> that map a stored column of txn_legs or the tag table, as a table or a view.</summary>
    private static IEnumerable<IEntityType> StoredMappings(IModel model, IReadOnlySet<string> stored)
    {
        foreach (var entity in model.GetEntityTypes())
        {
            StoreObjectIdentifier id;
            if (entity.GetTableName() is { } table) id = StoreObjectIdentifier.Table(table, entity.GetSchema());
            else if (entity.GetViewName() is { } view) id = StoreObjectIdentifier.View(view, entity.GetViewSchema());
            else continue;
            var columns = entity.GetProperties().Select(p => p.GetColumnName(id)).OfType<string>().ToList();
            if ((id.Name == "txn_legs" && columns.Any(stored.Contains)) || id.Name == StoredTagTable)
                yield return entity;
        }
    }

    private static string StoreName(IEntityType entity) => entity.GetTableName() ?? entity.GetViewName() ?? "?";

    /// <summary>The leg entity, which maps the stored columns insert-only (set by <c>LegNotes.Stamp</c>).</summary>
    private const string InsertOnlyEntity = "TxnLegRow";

    /// <summary>The properties of <paramref name="entity"/> that map a stored column of txn_legs.</summary>
    private static IEnumerable<IProperty> StoredProperties(IEntityType entity, IReadOnlySet<string> stored)
    {
        var id = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        return entity.GetProperties().Where(p => p.GetColumnName(id) is { } c && stored.Contains(c));
    }

    /// <summary>The application model's DbSets over a read mapping of the stored presentation, and their entity type names.</summary>
    private static string[] ForbiddenRoots(IReadOnlySet<string> stored)
    {
        using var context = ModelOnly(typeof(AppDbContext))!;
        // The leg's own set is not a read mapping: its writes are held to the inputs by
        // the set-based scan and to its stored columns by the setter scan below.
        var types = StoredMappings(context.Model, stored).Where(e => e.ClrType.Name != InsertOnlyEntity)
            .Select(e => e.ClrType).ToHashSet();
        var sets = typeof(AppDbContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)
                        && types.Contains(p.PropertyType.GetGenericArguments()[0]))
            .Select(p => p.Name);
        var roots = sets.Concat(types.Select(t => $"Set<{t.Name}>")).ToArray();
        Assert.True(roots.Length >= 4, $"found only [{string.Join(", ", roots)}] as read mappings of the stored presentation; the check is not finding them");
        return roots;
    }

    [Fact]
    public void No_source_but_the_recompute_writes_the_stored_presentation()
    {
        var root = RepoRoot();
        var stored = StoredColumns(root);
        var code = Directory.GetDirectories(Path.Combine(root, "src"))
            .Where(d => Directory.EnumerateFiles(d, "*.csproj").Any())
            .SelectMany(d => SourceFiles(root, Path.GetRelativePath(root, d)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Assert.True(code.Count > 300, $"scanned only {code.Count} files; the guard is not looking at the sources");
        Assert.Contains(Writer, code.Keys);
        Assert.Contains(BackfillWriter, code.Keys);
        var roots = ForbiddenRoots(stored.ToHashSet(StringComparer.Ordinal));

        var violations = code.Where(kv => kv.Key != Writer && kv.Key != BackfillWriter)
            .SelectMany(kv => StoredWrites(kv.Key, kv.Value, stored, roots))
            .ToList();
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// What in <paramref name="text"/> could write the stored presentation: a stored
    /// column or the tag table named anywhere but as a read mapping's column or view or
    /// table name; a write call on a read mapping of it (<paramref name="roots"/>), in the
    /// statement or through the locals the statement uses; or a tag row built.
    /// </summary>
    private static IEnumerable<string> StoredWrites(
        string file, string text, IReadOnlyCollection<string> stored, IReadOnlyCollection<string> roots)
    {
        var names = new Regex($@"\b(?:{string.Join('|', stored)}|{StoredTagTable})\b");
        foreach (Match m in names.Matches(text))
        {
            var before = text[Math.Max(0, m.Index - 32)..m.Index];
            if (Regex.IsMatch(before, @"(?:HasColumnName|ToTable|ToView)\(\s*""$")) continue;
            yield return $"{file}:{EnclosingMethod(text, m.Index)} names {m.Value} outside a read mapping; only LegNotes writes the stored presentation";
        }
        var root = new Regex($@"(?:\.|\b)(?:{string.Join('|', roots.Select(Regex.Escape))})(?!\w)");
        foreach (Match m in WriteCall().Matches(text))
        {
            if (root.IsMatch(WithLocals(text, m.Index)))
                yield return $"{file}:{EnclosingMethod(text, m.Index)} writes through a read mapping of the stored presentation; only LegNotes writes it";
        }
        foreach (Match m in Regex.Matches(text, @"\bnew\s+TxnLegNoteTagRow\b"))
            yield return $"{file}:{EnclosingMethod(text, m.Index)} builds a stored tag row; only LegNotes does";
        // The leg's insert-only stored values: set only through LegNotes.Stamp, never by a
        // set-based update or through the change tracker's property entries.
        foreach (Match m in Regex.Matches(text, @"\.StampNotes\("))
            yield return $"{file}:{EnclosingMethod(text, m.Index)} stamps a leg's stored values; only LegNotes.Stamp does";
        foreach (Match m in Regex.Matches(text, @"SetProperty\(\s*\w+\s*=>\s*\w+\.Notes[A-Z]\w*"))
            yield return $"{file}:{EnclosingMethod(text, m.Index)} sets a leg's stored value set-based; only LegNotes writes them";
        foreach (Match m in Regex.Matches(text, @"\.Notes[A-Z]\w*\s*\)\s*\.(?:CurrentValue|IsModified)\s*="))
            yield return $"{file}:{EnclosingMethod(text, m.Index)} writes a leg's stored value through the change tracker; only LegNotes writes them";
    }

    /// <summary>
    /// The statement around <paramref name="index"/>, followed by what each local it uses
    /// was last assigned in the method before it, and so on for the locals those use: what
    /// the statement's query can have been built from.
    /// </summary>
    private static string WithLocals(string code, int index)
    {
        var header = MethodHeader().Matches(code[..index]).LastOrDefault();
        var method = code[(header?.Index ?? 0)..index];
        var text = Statement(code, index);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([text]);
        var all = new System.Text.StringBuilder(text);
        while (pending.Count > 0)
        {
            foreach (Match local in Regex.Matches(pending.Dequeue(), @"\b([a-z_]\w*)\s*\."))
            {
                var name = local.Groups[1].Value;
                if (!seen.Add(name)) continue;
                var assigned = Regex.Matches(method, $@"\b{Regex.Escape(name)}\s*=(?![=>])\s*([^;]*)").LastOrDefault();
                if (assigned is null) continue;
                all.Append('\n').Append(assigned.Groups[1].Value);
                pending.Enqueue(assigned.Groups[1].Value);
            }
        }
        return all.ToString();
    }

    [Fact]
    public void No_sql_writes_the_stored_presentation_but_a_listed_whole_row_copy()
    {
        var root = RepoRoot();
        var stored = StoredColumns(root);
        var migrations = Directory.GetFiles(Path.Combine(root, "db", "migrations"), "*.sql")
            .Order(StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToList();

        // Every function at its latest definition, and every statement of 264 and later
        // outside a function: an older script ran before the columns existed.
        var functions = migrations
            .SelectMany(m => Regex.Matches(m.Text, @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?(\w+)\s*\(", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(f => (Where: $"function {f}", Name: f, Sql: LatestDefinition(migrations, f) ?? string.Empty));
        // A script's own statements: the bodies of the functions it defines are scanned
        // above, at their latest definitions.
        var scripts = migrations
            .Where(m => string.CompareOrdinal(m.Name, "264") >= 0)
            .Select(m => (Where: $"script {m.Name}", Name: (string?)null,
                Sql: Regex.Replace(
                    Regex.Replace(Regex.Replace(m.Text, @"/\*[\s\S]*?\*/", " "), @"--[^\n]*", " "),
                    @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\b[\s\S]*?(\$\w*\$)[\s\S]*?\1", " ", RegexOptions.IgnoreCase)));
        var bodies = functions.Select(f => (f.Where, Name: (string?)f.Name, f.Sql)).Concat(scripts).ToList();
        Assert.True(bodies.Count > 100, $"found only {bodies.Count} SQL bodies; the scan is not reading the migrations");

        var violations = new List<string>();
        var copies = new HashSet<string>(StringComparer.Ordinal);
        var deletes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (where, name, sql) in bodies)
        {
            foreach (var finding in StoredSqlWrites(sql, stored))
            {
                if (finding == WholeRowCopy && name is not null && CopiesWholeLegRows.ContainsKey(name))
                {
                    copies.Add(name);
                    continue;
                }
                if (finding == LedgerTagRowsDelete && name is not null && DeletesLedgerTagRows.ContainsKey(name))
                {
                    deletes.Add(name);
                    continue;
                }
                violations.Add($"{where}: {finding}");
            }
        }
        violations.AddRange(CopiesWholeLegRows.Keys.Where(k => !copies.Contains(k))
            .Select(k => $"{k}: listed as copying whole rows but its SQL no longer does; remove the entry"));
        violations.AddRange(DeletesLedgerTagRows.Keys.Where(k => !deletes.Contains(k))
            .Select(k => $"{k}: listed as deleting a ledger's tag rows but its SQL no longer does; remove the entry"));

        // A copy is reached only through a mapped function that copies verbatim or recomputes.
        var context = CodeOnly(File.ReadAllText(Path.Combine(root, "src", "Api", "Db", "AppDbContext.cs")));
        var mapped = DbFunctionMapping().Matches(context)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToLowerInvariant(), StringComparer.Ordinal);
        var defined = bodies.Where(b => b.Name is not null).Select(b => b.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var copy in CopiesWholeLegRows.Keys)
        {
            var reachers = mapped.Where(f => Reaches(migrations, defined, f.Value, copy)).Select(f => f.Key).ToList();
            if (reachers.Count == 0)
                violations.Add($"{copy}: no mapped function reaches it; the scan cannot hold its callers to the recompute");
            violations.AddRange(reachers.Where(f => DbFunctions.GetValueOrDefault(f) is null)
                .Select(f => $"{f} reaches {copy}, which copies whole rows of the stored presentation, but is classified as writing nothing"));
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private const string LedgerTagRowsDelete = "deletes a whole ledger's stored tag rows; list it with its reason";

    /// <summary><paramref name="from"/> calls <paramref name="target"/>, directly or through the functions it calls.</summary>
    private static bool Reaches(List<(string Name, string Text)> migrations, HashSet<string> defined, string from, string target)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Queue<string>(reached);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (name == target) return true;
            var body = LatestDefinition(migrations, name);
            if (body is null) continue;
            foreach (Match call in Regex.Matches(body, @"\b(\w+)\s*\("))
            {
                var callee = call.Groups[1].Value.ToLowerInvariant();
                if (defined.Contains(callee) && reached.Add(callee)) pending.Enqueue(callee);
            }
        }
        return false;
    }

    private const string WholeRowCopy =
        "copies whole rows of the stored presentation (txn_legs with its stored columns, or txn_leg_note_tags); list it with its reason";

    /// <summary>The ways <paramref name="sql"/> writes the stored presentation, one finding per statement.</summary>
    private static IEnumerable<string> StoredSqlWrites(string sql, IReadOnlyCollection<string> stored)
    {
        var column = string.Join('|', stored);
        const RegexOptions i = RegexOptions.IgnoreCase;
        // An alias after the table name, never one of the words that start the rows.
        const string alias = @"(?:\s+(?:AS\s+)?(?!(?:VALUES|SELECT|TABLE|DEFAULT|OVERRIDING|WITH|SET|USING|FROM)\b)\w+)?";
        const string legs = @"(?:ONLY\s+)?(?:public\.)?txn_legs\b";
        foreach (Match m in Regex.Matches(sql, $@"\bUPDATE\s+{legs}[^;]*?\bSET\b[^;]*?\b(?:{column})\b", i))
            yield return $"updates a stored column: {Flat(m.Value)}";
        foreach (Match m in Regex.Matches(sql, $@"\bINSERT\s+INTO\s+{legs}{alias}\s*\([^)]*\b(?:{column})\b", i))
            yield return $"inserts a stored column: {Flat(m.Value)}";
        // MERGE: a stored column named in the statement, or an insert with no column list.
        foreach (Match m in Regex.Matches(sql, $@"\bMERGE\s+INTO\s+{legs}[^;]*?(?:\b(?:{column})\b|\bINSERT\s+(?:VALUES|DEFAULT)\b)", i))
            yield return $"merges into a stored column: {Flat(m.Value)}";
        // COPY FROM with no column list writes every column; with one, those named.
        foreach (Match m in Regex.Matches(sql, $@"\bCOPY\s+{legs}\s*(?:\(([^)]*)\))?\s*FROM\b", i))
        {
            if (!m.Groups[1].Success || Regex.IsMatch(m.Groups[1].Value, $@"\b(?:{column})\b", i))
                yield return $"copies into a stored column: {Flat(m.Value)}";
        }
        const string tags = $@"(?:ONLY\s+)?(?:public\.)?{StoredTagTable}\b";
        foreach (Match m in Regex.Matches(sql,
                     $@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE(?:\s+TABLE)?)\s+{tags}[^;]*|\bCOPY\s+(?:public\.)?{StoredTagTable}\b[^;]*?\bFROM\b", i))
        {
            // Two forms are their own findings, each admitted only where listed: a copy of
            // whole rows (no column list), and the delete of one whole ledger's rows.
            if (Regex.IsMatch(m.Value, $@"^INSERT\s+INTO\s+{tags}{alias}\s+(?:SELECT|TABLE)\b", i))
                yield return WholeRowCopy;
            else if (Regex.IsMatch(m.Value, $@"^DELETE\s+FROM\s+{tags}\s+WHERE\s+ledger_id\s*=\s*p_ledger_id\s*$", i))
                yield return LedgerTagRowsDelete;
            else
                yield return $"writes the stored tag rows: {Flat(m.Value)}";
        }
        // Rows with no column list write every column by position, stored ones included;
        // a dynamic insert names no table either.
        foreach (Match m in Regex.Matches(sql, $@"\bINSERT\s+INTO\s+(?:{legs}{alias}\s+(?:SELECT|TABLE|VALUES|DEFAULT|OVERRIDING|WITH)\b|%I\b)", i))
            yield return WholeRowCopy;
    }

    [Fact]
    public void The_stored_presentation_scans_find_each_kind_of_write()
    {
        string[] stored = ["notes_kind", "notes_memo"];
        // Source: a raw statement, a write call on the tag rows and a built tag row are
        // reported; a read mapping of a column is not.
        const string source = """
            internal sealed class Sample
            {
                public void Map(EntityTypeBuilder<Row> b) => b.Property(x => x.Memo).HasColumnName("notes_memo");
                public Task RawAsync() => _db.Database.ExecuteSqlRawAsync("UPDATE txn_legs SET notes_memo = NULL");
                public Task TagsAsync() => _db.TxnLegNoteTags.Where(t => t.InPlay).ExecuteDeleteAsync();
                public void Build() => Keep(new TxnLegNoteTagRow { LegId = a });
                public void Stamp(TxnLegRow leg) => leg.StampNotes(values);
                public Task BulkAsync() => _db.TxnLegs.ExecuteUpdateAsync(s => s.SetProperty(l => l.NotesMemo, "x"));
                public void Track(TxnLegRow leg) => _db.Entry(leg).Property(l => l.NotesKind).IsModified = true;
                public Task LegMemoAsync() => _db.TxnLegs.ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "x"));
            }
            """;
        var found = StoredWrites("Sample.cs", CodeOnly(source), stored, ["TxnLegNoteTags"]).ToList();
        Assert.Equal(6, found.Count);
        Assert.Contains(found, f => f.Contains("RawAsync names notes_memo", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("TagsAsync writes through a read mapping", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("Build builds a stored tag row", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("Stamp stamps a leg's stored values", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("BulkAsync sets a leg's stored value set-based", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("Track writes a leg's stored value through the change tracker", StringComparison.Ordinal));

        // The tag table's two admitted forms are their own findings; any other write is not.
        Assert.Equal(WholeRowCopy, Assert.Single(StoredSqlWrites(
            "INSERT INTO txn_leg_note_tags SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_note_tags, p);", stored)));
        Assert.Equal(LedgerTagRowsDelete, Assert.Single(StoredSqlWrites(
            "DELETE FROM txn_leg_note_tags WHERE ledger_id = p_ledger_id;", stored)));
        Assert.StartsWith("writes the stored tag rows", Assert.Single(StoredSqlWrites(
            "INSERT INTO txn_leg_note_tags (leg_id, tag_id) SELECT 1, 2;", stored)), StringComparison.Ordinal);
        Assert.StartsWith("writes the stored tag rows", Assert.Single(StoredSqlWrites(
            "DELETE FROM txn_leg_note_tags WHERE ledger_id = p_ledger_id AND in_play;", stored)), StringComparison.Ordinal);

        // SQL: an update, an insert naming a column, a tag-row write and a whole-row copy
        // are reported; reading a column and writing another one are not.
        Assert.Single(StoredSqlWrites("UPDATE txn_legs l SET notes_memo = 'x' WHERE l.id = $1;", stored));
        Assert.Single(StoredSqlWrites("INSERT INTO public.txn_legs (id, notes_kind) VALUES ($1, 'SplitSide');", stored));
        Assert.Single(StoredSqlWrites("DELETE FROM txn_leg_note_tags WHERE leg_id = $1;", stored));
        Assert.Equal(WholeRowCopy, Assert.Single(StoredSqlWrites(
            "INSERT INTO txn_legs SELECT * FROM jsonb_populate_recordset(NULL::txn_legs, p);", stored)));
        // The forms with no column list, an alias, MERGE and COPY.
        Assert.Equal(WholeRowCopy, Assert.Single(StoredSqlWrites(
            "INSERT INTO txn_legs VALUES ($1, $2, 'SplitSide');", stored)));
        Assert.Equal(WholeRowCopy, Assert.Single(StoredSqlWrites(
            "INSERT INTO public.txn_legs AS t SELECT * FROM staged;", stored)));
        Assert.Single(StoredSqlWrites("INSERT INTO txn_legs AS t (id, notes_memo) VALUES ($1, 'x');", stored));
        Assert.Single(StoredSqlWrites(
            "MERGE INTO txn_legs t USING src s ON s.id = t.id WHEN MATCHED THEN UPDATE SET notes_memo = s.m;", stored));
        Assert.Single(StoredSqlWrites(
            "MERGE INTO txn_legs t USING src s ON s.id = t.id WHEN NOT MATCHED THEN INSERT VALUES (s.id, s.h);", stored));
        Assert.Single(StoredSqlWrites("COPY txn_legs FROM STDIN;", stored));
        Assert.Single(StoredSqlWrites("COPY txn_legs (id, notes_kind) FROM STDIN;", stored));
        Assert.Single(StoredSqlWrites("MERGE INTO txn_leg_note_tags t USING src s ON true WHEN NOT MATCHED THEN DO NOTHING;", stored));
        Assert.Single(StoredSqlWrites("TRUNCATE txn_leg_note_tags;", stored));
        Assert.Empty(StoredSqlWrites("SELECT l.notes_memo FROM txn_legs l; UPDATE txn_legs SET amount = 1 WHERE id = $1;", stored));
        Assert.Empty(StoredSqlWrites(
            "INSERT INTO txn_legs (id, amount) VALUES ($1, 1); COPY txn_legs (id, amount) FROM STDIN; COPY txn_legs TO STDOUT;", stored));
        Assert.Empty(StoredSqlWrites(
            "MERGE INTO txn_legs t USING src s ON s.id = t.id WHEN MATCHED THEN UPDATE SET amount = s.a;", stored));
    }

    [Fact]
    public void The_source_scan_finds_a_write_through_a_local_or_a_read_mapping()
    {
        string[] stored = ["notes_kind", "notes_memo"];
        string[] roots = ["TxnLegNoteTags", "StoredLegPresentations", "Set<TxnLegNoteTagRow>", "Set<StoredLegNotesView>"];
        const string source = """
            internal sealed class Sample
            {
                public async Task LocalAsync(Guid a)
                {
                    var q = _db.TxnLegNoteTags.Where(t => t.LegId == a);
                    var r = q.Where(t => t.InPlay);
                    await r.ExecuteDeleteAsync(ct);
                }

                public Task ViewAsync() =>
                    _db.StoredLegPresentations.ExecuteUpdateAsync(s => s.SetProperty(m => m.Memo, "x"));

                public Task SetAsync() => _db.Set<TxnLegNoteTagRow>().Where(t => t.InPlay).ExecuteDeleteAsync();

                public async Task ReadAsync(Guid a)
                {
                    var memos = _db.StoredLegPresentations.Where(m => m.LegId == a).Select(m => m.HeaderId);
                    var other = _db.TxnLegs.Where(l => memos.Contains(l.HeaderId));
                    await _db.TxnHeaders.Where(h => h.Id == a).ExecuteUpdateAsync(s => s.SetProperty(h => h.NeedsReview, true));
                }
            }
            """;
        var found = StoredWrites("Sample.cs", CodeOnly(source), stored, roots).ToList();
        Assert.Equal(3, found.Count);
        Assert.Contains(found, f => f.Contains("LocalAsync writes through a read mapping", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("ViewAsync writes through a read mapping", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("SetAsync writes through a read mapping", StringComparison.Ordinal));
    }

    /// <summary>
    /// The context <paramref name="type"/> on a model-only Npgsql connection, or null when no
    /// constructor can be called without a live dependency. Two shapes are called: one that
    /// takes only its options, and one that configures itself from a connection string
    /// followed by value-type arguments (each given its default). Building the model opens
    /// no connection either way.
    /// </summary>
    internal static DbContext? ModelOnly(Type type)
    {
        const string connectionString = "Host=model-only;Database=model-only";
        var ctors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var fromOptions = ctors.FirstOrDefault(c =>
            c.GetParameters() is [var p] && typeof(DbContextOptions).IsAssignableFrom(p.ParameterType));
        if (fromOptions is not null)
        {
            var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(type))!;
            builder.UseNpgsql(connectionString);
            return (DbContext)fromOptions.Invoke([builder.Options]);
        }
        var fromConnectionString = ctors.FirstOrDefault(c =>
            c.GetParameters() is [var first, .. var rest]
            && first.ParameterType == typeof(string)
            && rest.All(p => p.ParameterType.IsValueType));
        if (fromConnectionString is null) return null;
        object?[] args = [connectionString, .. fromConnectionString.GetParameters().Skip(1)
            .Select(p => Activator.CreateInstance(p.ParameterType))];
        return (DbContext)fromConnectionString.Invoke(args);
    }

    [Fact]
    public void The_scan_finds_a_write_that_skips_the_recompute_or_recomputes_before_it()
    {
        // The rule over a synthetic file: a leg repoint with no recompute is reported,
        // and so is one whose recompute comes before the write.
        const string source = """
            internal sealed class Sample
            {
                public async Task MoveAsync(Guid a, Guid b)
                {
                    await _db.TxnLegs.Where(l => l.AccountId == a)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, b));
                }

                public async Task EarlyAsync(Guid a, Guid b)
                {
                    await LegNotes.RecomputeAsync(_db, [a], ct);
                    await _db.TxnLegs.Where(l => l.AccountId == a)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, b));
                }

                public async Task RightAsync(Guid a, Guid b)
                {
                    await _db.TxnLegs.Where(l => l.AccountId == a)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, b));
                    await LegNotes.RecomputeAsync(_db, [a], ct);
                }
            }
            """;
        var writes = SetBased().Matches(source).ToList();
        Assert.Equal(3, writes.Count);
        var statement = Statement(source, writes[0].Index);
        Assert.Equal("TxnLegs", Root().Match(statement).Groups[1].Value);
        Assert.Equal("AccountId", SetColumn().Match(statement).Groups[1].Value);
        Assert.Equal("MoveAsync", EnclosingMethod(source, writes[0].Index));
        Assert.False(RecomputesAfter(source, writes[0].Index));
        Assert.Equal("EarlyAsync", EnclosingMethod(source, writes[1].Index));
        Assert.False(RecomputesAfter(source, writes[1].Index));
        Assert.Equal("RightAsync", EnclosingMethod(source, writes[2].Index));
        Assert.True(RecomputesAfter(source, writes[2].Index));
    }

    /// <summary>
    /// A helper that leaves the recompute to its callers: every call of it is in a listed
    /// caller, and every listed caller recomputes after the call.
    /// </summary>
    private static void CheckDelegated(
        Dictionary<string, string> code, string helperFile, string helper,
        (string File, string Method)[] callers, List<string> violations)
    {
        var listed = callers.ToHashSet();
        foreach (var (file, text) in code)
        {
            var declarations = MethodHeader().Matches(text).ToList();
            foreach (Match m in Regex.Matches(text, $@"\b{helper}\("))
            {
                // Its declaration, not a call.
                if (declarations.Any(h => h.Groups[1].Value == helper && h.Index <= m.Index && m.Index < h.Index + h.Length))
                    continue;
                var caller = EnclosingMethod(text, m.Index);
                if (!listed.Contains((file, caller)))
                {
                    violations.Add($"{helperFile}:{helper} is called from {file}:{caller}, which is not among its listed callers");
                    continue;
                }
                if (!RecomputesAfter(text, m.Index))
                    violations.Add($"{helperFile}:{helper} leaves the recompute to {file}:{caller}, which does not call LegNotes.Recompute* after it");
            }
        }
        foreach (var (file, method) in callers)
        {
            if (!code.TryGetValue(file, out var text) || !Regex.IsMatch(MethodBody(text, method), $@"\b{helper}\("))
                violations.Add($"{helperFile}:{helper}: listed caller {file}:{method} no longer calls it; remove the entry");
        }
    }

    /// <summary>The method around <paramref name="index"/> calls <paramref name="recompute"/> after that point.</summary>
    private static bool RecomputesAfter(string code, int index, string recompute = "LegNotes.Recompute")
    {
        var headers = MethodHeader().Matches(code).ToList();
        var next = headers.FirstOrDefault(h => h.Index > index);
        var end = next?.Index ?? code.Length;
        var at = code.IndexOf(recompute, index, StringComparison.Ordinal);
        return at >= 0 && at < end;
    }

    /// <summary>The body of the latest definition of <paramref name="sqlName"/>, between its dollar quotes.</summary>
    internal static string? LatestDefinition(List<(string Name, string Text)> migrations, string sqlName)
    {
        var create = new Regex($@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?{sqlName}\s*\(", RegexOptions.IgnoreCase);
        for (var i = migrations.Count - 1; i >= 0; i--)
        {
            var m = create.Match(migrations[i].Text);
            if (!m.Success) continue;
            var text = migrations[i].Text;
            var open = Regex.Match(text[m.Index..], @"\$(\w*)\$");
            if (!open.Success) return text[m.Index..];
            var start = m.Index + open.Index + open.Length;
            var close = text.IndexOf(open.Value, start, StringComparison.Ordinal);
            var body = close < 0 ? text[start..] : text[start..close];
            // Comments name tables they do not write.
            return Regex.Replace(Regex.Replace(body, @"/\*[\s\S]*?\*/", " "), @"--[^\n]*", " ");
        }
        return null;
    }

    /// <summary>
    /// The DbSet a set-based statement starts from: the first one it names, or, when it
    /// starts from a local query, the DbSet that local was built from in the same method.
    /// Null when neither names one.
    /// </summary>
    private static string? RootOf(string code, int index, string statement)
    {
        var direct = Root().Matches(statement).Select(r => r.Groups[1].Value).ToList();
        var first = direct.FirstOrDefault(Inputs.ContainsKey) ?? direct.FirstOrDefault();
        var local = Regex.Match(statement, @"(?:await|return)\s+(\w+)\s*\.");
        if (local.Success && local.Groups[1].Value is not ("_db" or "db"))
        {
            var header = MethodHeader().Matches(code[..index]).LastOrDefault();
            var before = code[(header?.Index ?? 0)..index];
            var assigned = Regex.Matches(before, $@"\b{local.Groups[1].Value}\s*=\s*[^;]*?\b_?db\.(\w+)").LastOrDefault();
            if (assigned is not null) return assigned.Groups[1].Value;
            return null;
        }
        return first;
    }

    private static Dictionary<string, string> SourceFiles(string root, string relativeDir) =>
        Directory.EnumerateFiles(Path.Combine(root, relativeDir), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Rel(root, f), f => CodeOnly(File.ReadAllText(f)), StringComparer.Ordinal);

    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>The source with whole-line comments blanked, so a comment never satisfies or trips a rule.</summary>
    internal static string CodeOnly(string source) =>
        string.Join('\n', source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

    /// <summary>The statement around <paramref name="index"/>: from the previous statement end to the next.</summary>
    private static string Statement(string code, int index)
    {
        var start = code.LastIndexOfAny([';', '{', '}'], index) + 1;
        var end = code.IndexOf(';', index);
        return end < 0 ? code[start..] : code[start..(end + 1)];
    }

    private static string EnclosingMethod(string code, int index)
    {
        var header = MethodHeader().Matches(code[..index]).LastOrDefault();
        return header?.Groups[1].Value ?? "(none)";
    }

    /// <summary>The text from <paramref name="method"/>'s first declaration to the next method's.</summary>
    private static string MethodBody(string code, string method)
    {
        var headers = MethodHeader().Matches(code).ToList();
        var at = headers.FindIndex(h => h.Groups[1].Value == method);
        if (at < 0) return string.Empty;
        var end = at + 1 < headers.Count ? headers[at + 1].Index : code.Length;
        return code[headers[at].Index..end];
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"no repository root above {AppContext.BaseDirectory}");
    }
}
