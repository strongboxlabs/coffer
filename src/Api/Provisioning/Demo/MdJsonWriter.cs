using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>The sections of a generated export, in the order they are written.</summary>
public enum MdSection
{
    /// <summary>Nothing written yet.</summary>
    Start,

    /// <summary>The curated base rows.</summary>
    Base,

    /// <summary>The generated <c>olsvc</c> online-banking services.</summary>
    OnlineServices,

    /// <summary>The generated real accounts.</summary>
    Accounts,

    /// <summary>The type-<c>s</c> security holding accounts.</summary>
    HoldingAccounts,

    /// <summary>The security <c>curr</c> rows.</summary>
    Securities,

    /// <summary>The <c>csplit</c> rows.</summary>
    SecuritySplits,

    /// <summary>The <c>csnap</c> price rows.</summary>
    Prices,

    /// <summary>The <c>txn</c> rows.</summary>
    Transactions,

    /// <summary>The <c>reminder</c> rows.</summary>
    Reminders,

    /// <summary>The closing <c>local_settings</c> object.</summary>
    LocalSettings,
}

/// <summary>
/// Writes a Moneydance JSON export, compact, straight into a byte buffer: no DOM,
/// nothing on disk.
/// </summary>
/// <remarks>
/// <para>The document is <c>{"metadata":{…},"all_items":[…],"local_settings":{…}}</c>.
/// Items go out in a fixed section order (<see cref="MdSection"/>), which the writer
/// enforces, and transactions in export order within theirs. Order matters to the
/// importer only for the first spelling of a tag, and a fixed order makes the bytes
/// a pure function of the <see cref="DemoSpec"/>.</para>
///
/// <para>Every Moneydance item value is a string, amounts and dates included; the
/// metadata's build and export date are numbers. Base rows are written verbatim from
/// their raw text.</para>
/// </remarks>
public sealed class MdJsonWriter : IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer;
    private readonly Utf8JsonWriter _json;
    private MdSection _section = MdSection.Start;
    private bool _started;
    private bool _finished;
    private DemoTxn? _lastTxn;

    /// <summary>A writer with room for roughly <paramref name="initialCapacity"/> bytes.</summary>
    public MdJsonWriter(int initialCapacity = 1 << 20)
    {
        _buffer = new ArrayBufferWriter<byte>(initialCapacity);
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = false });
    }

    /// <summary>Open the document and write its metadata.</summary>
    public void WriteMetadata(string exporter, int moneydanceBuild, int exportDate, string fileName)
    {
        if (_started) throw new InvalidOperationException("the metadata is written once, first");
        _started = true;
        _json.WriteStartObject();
        _json.WritePropertyName("metadata");
        _json.WriteStartObject();
        _json.WriteString("exporter", exporter);
        _json.WriteNumber("moneydance_build", moneydanceBuild);
        _json.WriteNumber("export_date", exportDate);
        _json.WriteString("file_name", fileName);
        _json.WriteEndObject();
        _json.WritePropertyName("all_items");
        _json.WriteStartArray();
    }

    /// <summary>Write one base row verbatim.</summary>
    public void WriteBaseItem(JsonElement item)
    {
        Enter(MdSection.Base);
        _json.WriteRawValue(item.GetRawText());
    }

    /// <summary>Write one generated account: a real account, or a holding account
    /// in the holdings section. <paramref name="openingBalance"/> is the
    /// <c>sbal</c> written, which a short span raises above the book's.</summary>
    public void WriteAccount(DemoAccount account, DemoBase baseData, long openingBalance)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(baseData);
        Enter(account.IsHolding ? MdSection.HoldingAccounts : MdSection.Accounts);

        _json.WriteStartObject();
        _json.WriteString("obj_type", "acct");
        _json.WriteString("id", account.Id);
        _json.WriteString("type", account.MdType);
        _json.WriteString("name", account.Name);
        _json.WriteString("parentid", account.ParentId ?? baseData.RootAccountId);
        _json.WriteString("currid", account.CurrencyId ?? baseData.BaseCurrencyId);
        _json.WriteString("sbal", Invariant(openingBalance));
        if (!account.IsHolding)
        {
            _json.WriteString("date_created", Invariant(DemoCalendar.MdDate(account.DateCreated)));
            _json.WriteString("is_inactive", account.IsInactive ? "1" : "0");
        }
        if (account.BankName is { } bank) _json.WriteString("bank_name", bank);
        if (account.BankAccountNumber is { } number) _json.WriteString("bank_account_number", number);
        foreach (var (key, value) in account.ExtraFields) _json.WriteString(key, value);
        _json.WriteString("ts", Invariant(DemoCalendar.NoonEpochMillis(account.DateCreated)));
        _json.WriteEndObject();
    }

    /// <summary>Write one generic item: an online service, a security, a split, a
    /// price, a reminder.</summary>
    public void WriteItem(MdSection section, string objType, string id, IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(objType);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(fields);
        if (section is MdSection.Start or MdSection.Base or MdSection.Accounts or MdSection.HoldingAccounts
            or MdSection.Transactions or MdSection.LocalSettings)
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "that section has its own writer");
        }
        Enter(section);

        _json.WriteStartObject();
        _json.WriteString("obj_type", objType);
        _json.WriteString("id", id);
        foreach (var (key, value) in fields) _json.WriteString(key, value);
        _json.WriteEndObject();
    }

    /// <summary>
    /// Write one posted transaction. Rows must come in export order. Its
    /// <c>dtentered</c> is noon UTC on its date plus a per-row offset, moved on by
    /// <see cref="DemoTxn.EnteredDaysLater"/> (a feed row arrives a day or two
    /// late); its <c>ts</c>, the last change, is a second after entry. Both are
    /// clamped to the end of AsOf.
    /// </summary>
    public void WriteTxn(DemoTxn txn, DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(txn);
        ArgumentNullException.ThrowIfNull(calendar);
        if (txn.Id.Length == 0) throw new ArgumentException("only a posted row is written", nameof(txn));
        if (!txn.Exported) throw new ArgumentException($"row {txn.Id} is simulated only", nameof(txn));
        Enter(MdSection.Transactions);
        if (_lastTxn is { } last && DemoBook.CompareExportOrder(last, txn) > 0)
            throw new InvalidOperationException($"row {txn.Id} is out of export order");
        _lastTxn = txn;

        var entered = Clamp(
            DemoCalendar.NoonEpochMillis(txn.Date) + RowOffsetMillis(txn) + txn.EnteredDaysLater * 86_400_000L, calendar);
        var ts = Clamp(entered + 1_000L, calendar);

        _json.WriteStartObject();
        _json.WriteString("obj_type", "txn");
        _json.WriteString("id", txn.Id);
        _json.WriteString("acctid", txn.AccountId);
        _json.WriteString("desc", txn.Description);
        _json.WriteString("dt", Invariant(DemoCalendar.MdDate(txn.Date)));
        _json.WriteString("td", Invariant(DemoCalendar.MdDate(txn.TaxDate ?? txn.Date)));
        _json.WriteString("dtentered", Invariant(entered));
        _json.WriteString("ts", Invariant(ts));
        _json.WriteString("stat", txn.Status);
        if (txn.Route == DemoRoute.Investment) _json.WriteString("invest.txntype", txn.InvestTxnType);
        _json.WriteString("xfer_type", txn.XferType);
        WriteIfSet("memo", txn.Memo);
        WriteIfSet("chk", txn.CheckNumber);
        WriteIfSet("ol_fi_id", txn.OlFiId);
        WriteIfSet("ol_fitid_1", txn.OlFitid);
        WriteIfSet("ol.orig-payee", txn.OlOrigPayee);
        WriteIfSet("ol.orig-memo", txn.OlOrigMemo);
        WriteIfSet("tags", txn.Tags);

        for (var i = 0; i < txn.Splits.Count; i++)
        {
            var split = txn.Splits[i];
            var prefix = Invariant(i) + ".";
            _json.WriteString(prefix + "id", split.Id);
            _json.WriteString(prefix + "obj_type", string.Empty);
            _json.WriteString(prefix + "acctid", split.AccountId);
            _json.WriteString(prefix + "samt", Invariant(split.SplitAmount));
            _json.WriteString(prefix + "pamt", Invariant(split.ParentAmount));
            _json.WriteString(prefix + "desc", split.Description ?? txn.Description);
            WriteIfSet(prefix + "stat", split.Status);
            WriteIfSet(prefix + "tags", split.Tags);
            WriteIfSet(prefix + "invest.splittype", split.SplitType);
        }
        _json.WriteEndObject();
    }

    /// <summary>Close the item list and write <c>local_settings</c> verbatim (an
    /// empty object when there is none).</summary>
    public void WriteLocalSettings(JsonElement? localSettings)
    {
        Enter(MdSection.LocalSettings);
        _json.WriteEndArray();
        _json.WritePropertyName("local_settings");
        if (localSettings is { } settings) _json.WriteRawValue(settings.GetRawText());
        else
        {
            _json.WriteStartObject();
            _json.WriteEndObject();
        }
    }

    /// <summary>Close the document and return its bytes. Call once, after
    /// <see cref="WriteLocalSettings"/>.</summary>
    public byte[] ToArray()
    {
        if (_section != MdSection.LocalSettings) throw new InvalidOperationException("local_settings closes the document");
        if (!_finished)
        {
            _json.WriteEndObject();
            _json.Flush();
            _finished = true;
        }
        return _buffer.WrittenSpan.ToArray();
    }

    /// <inheritdoc />
    public void Dispose() => _json.Dispose();

    private void Enter(MdSection section)
    {
        if (!_started) throw new InvalidOperationException("the metadata comes first");
        if (_finished) throw new InvalidOperationException("the document is closed");
        if (section < _section)
            throw new InvalidOperationException($"the {section} section cannot follow the {_section} section");
        if (section == MdSection.LocalSettings && _section == MdSection.LocalSettings)
            throw new InvalidOperationException("local_settings is written once");
        _section = section;
    }

    private void WriteIfSet(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) _json.WriteString(name, value);
    }

    // A per-row offset within a few hours of noon, from the row's own coordinates,
    // so rows on one day are stamped apart and no other pattern's rows move it.
    private static long RowOffsetMillis(DemoTxn txn) =>
        ((txn.Ordinal * 37L + txn.PatternRank * 600L) % 14_400L) * 1000L;

    private static long Clamp(long millis, DemoCalendar calendar) =>
        millis > calendar.LatestEpochMillis ? calendar.LatestEpochMillis : millis;

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
