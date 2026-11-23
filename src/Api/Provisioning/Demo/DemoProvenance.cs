using System.Globalization;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>How rows on an account arrive in a given era.</summary>
public enum DemoFeed
{
    /// <summary>Typed in by hand: no feed fields.</summary>
    None,

    /// <summary>Moneydance's text (CSV) import: <c>ol_fi_id</c> <c>md:txtimport</c>,
    /// a <c>mdtxtimport:</c> FITID.</summary>
    Csv,

    /// <summary>An online OFX connection: the account names an online-banking
    /// service (<c>olbfi</c>), rows carry <c>ol_fi_id</c> <c>ofx:&lt;org&gt;:&lt;fid&gt;</c>.</summary>
    OnlineOfx,

    /// <summary>Moneydance+'s direct connection: <c>ol_fi_id</c> <c>mdplus:&lt;id&gt;</c>.</summary>
    MdPlus,

    /// <summary>Downloaded OFX files: the account carries
    /// <c>ofx_import_acct_num</c>, rows carry <c>ol_fi_id</c> <c>ofx:…</c>.</summary>
    FileOfx,
}

/// <summary>A generated <c>olsvc</c> item: an invented institution's online-banking
/// service that accounts name in <c>olbfi</c>. The importer drops the item itself;
/// only the account's reference to it matters.</summary>
/// <param name="Id">The item's MD id.</param>
/// <param name="FiName">The institution's display name.</param>
/// <param name="FiOrg">The OFX <c>ORG</c>.</param>
/// <param name="FiId">The OFX <c>FID</c>.</param>
public sealed record DemoOnlineService(string Id, string FiName, string FiOrg, string FiId);

/// <summary>
/// Provenance: which rows arrived from a bank and how, written as the
/// <c>ol_*</c> fields TransactionMapper.DecomposeOrigin reads, and the account
/// fields that tell online OFX from file OFX.
/// </summary>
/// <remarks>
/// <para><b>Eras.</b> Joint Checking starts on Moneydance's CSV import and moves to
/// an online OFX connection; Savings is online throughout; the original card comes
/// through Moneydance+; the replacement card through downloaded OFX files. Every
/// other account is typed in by hand, and investment-route rows never carry a
/// feed id.</para>
///
/// <para><b>One pass after every pattern.</b> <see cref="Apply"/> walks the exported
/// rows in export order and gives each feed row its fields, so a pattern posts a
/// row and gets its provenance without asking. A pattern marks a cheque or a
/// hand-entered row <see cref="DemoEntry.Manual"/> to keep it off the feed, and may
/// give the bank's raw strings in <see cref="DemoTxn.BankPayee"/> and
/// <see cref="DemoTxn.BankMemo"/>.</para>
///
/// <para><b>FITIDs</b> are an account tag and a seven-digit per-account sequence
/// (or a <c>mdtxtimport:</c> sequence in the CSV era), so the importer's dedup key
/// <c>(ol_fi_id, ol_fitid_1)</c> never meets the same pair twice, and none starts
/// with a date, which the importer would read as a legacy Moneydance+ row.</para>
///
/// <para><b>Feed strings.</b> Only feed rows carry <c>ol.orig-payee</c> and
/// <c>ol.orig-memo</c>. <c>desc</c> stays the cleaned name; a row with no raw payee of
/// its own gets the bank's upper-case form of it. A feed row is entered one or two
/// days after its date; a hand-entered row on its date.</para>
/// </remarks>
public static class DemoProvenance
{
    /// <summary>The last life month of Joint Checking's CSV era.</summary>
    public const int CsvEraLastMonth = 5;

    /// <summary>The <c>ol_fi_id</c> of Moneydance's text import.</summary>
    public const string CsvFiId = "md:txtimport";

    /// <summary>The prefix of a text-import FITID.</summary>
    public const string CsvFitidPrefix = "mdtxtimport:";

    /// <summary>The prefix of a Moneydance+ <c>ol_fi_id</c>.</summary>
    public const string MdPlusPrefix = "mdplus:";

    /// <summary>The prefix of an OFX <c>ol_fi_id</c>.</summary>
    public const string OfxPrefix = "ofx:";

    /// <summary>The account field naming an online-banking service.</summary>
    public const string OnlineServiceField = "olbfi";

    /// <summary>The account field marking OFX file imports.</summary>
    public const string FileImportField = "ofx_import_acct_num";

    private const string HomeBankOrg = "LARCHMERE";
    private const string HomeBankFid = "40217";
    private const string RewardsOrg = "HARROWGATE";
    private const string RewardsFid = "77140";

    /// <summary>The online-banking services the export carries as <c>olsvc</c> items.</summary>
    public static IReadOnlyList<DemoOnlineService> Services(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        return [new DemoOnlineService(book.Ids.Id("olsvc", "home-bank", 0), HouseholdAccounts.HomeBank, HomeBankOrg, HomeBankFid)];
    }

    /// <summary>Give the feed accounts their account-level provenance: the online
    /// accounts name the home bank's service, the file-import card its account
    /// number.</summary>
    public static void ConfigureAccounts(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var service = Services(book)[0];
        book.Account(DemoAccountRoles.JointChecking).ExtraFields.Add(new(OnlineServiceField, service.Id));
        book.Account(DemoAccountRoles.Savings).ExtraFields.Add(new(OnlineServiceField, service.Id));

        var rewards = book.Account(DemoAccountRoles.RewardsCard);
        rewards.ExtraFields.Add(new(FileImportField,
            rewards.BankAccountNumber ?? throw new InvalidOperationException("the file-import card has no account number")));
    }

    /// <summary>How rows on <paramref name="account"/> dated <paramref name="date"/>
    /// arrive, unless a pattern marked one manual.</summary>
    public static DemoFeed FeedOf(DemoBook book, DemoAccount account, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(account);
        return account.Role switch
        {
            DemoAccountRoles.JointChecking =>
                book.Calendar.LifeMonthOf(date) <= CsvEraLastMonth ? DemoFeed.Csv : DemoFeed.OnlineOfx,
            DemoAccountRoles.Savings => DemoFeed.OnlineOfx,
            DemoAccountRoles.ClassicCard => DemoFeed.MdPlus,
            DemoAccountRoles.RewardsCard => DemoFeed.FileOfx,
            _ => DemoFeed.None,
        };
    }

    /// <summary>True when <paramref name="txn"/> arrives by a feed: an exported
    /// bank-route row, not marked manual, on an account with a feed that day.</summary>
    public static bool IsFeedRow(DemoBook book, DemoTxn txn)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(txn);
        if (!txn.Exported || txn.Route != DemoRoute.Bank || txn.Entry == DemoEntry.Manual) return false;
        var account = book.AccountById(txn.AccountId);
        return account is not null && FeedOf(book, account, txn.Date) != DemoFeed.None;
    }

    /// <summary>
    /// Give every exported feed row its <c>ol_*</c> fields and entry lag, in export
    /// order, and register the provenance self-check. Runs once, after every
    /// pattern has posted.
    /// </summary>
    public static void Apply(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var mdPlusFiId = MdPlusPrefix + book.Ids.Id("olconn", DemoAccountRoles.ClassicCard, 0);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var txn in book.TransactionsInExportOrder())
        {
            if (txn.OlFiId is not null || txn.OlFitid is not null)
                throw new InvalidOperationException($"row {txn.Id} already carries feed fields; provenance is applied once");
            if (!IsFeedRow(book, txn))
            {
                txn.EnteredDaysLater = 0;
                continue;
            }

            var account = book.AccountById(txn.AccountId)!;
            var sequence = sequences.GetValueOrDefault(account.Id) + 1;
            sequences[account.Id] = sequence;

            var (fiId, fitid) = FeedOf(book, account, txn.Date) switch
            {
                DemoFeed.Csv => (CsvFiId, CsvFitidPrefix + sequence.ToString(CultureInfo.InvariantCulture)),
                DemoFeed.OnlineOfx => (OfxPrefix + HomeBankOrg + ":" + HomeBankFid, Fitid(account, sequence)),
                DemoFeed.MdPlus => (mdPlusFiId, Fitid(account, sequence)),
                DemoFeed.FileOfx => (OfxPrefix + RewardsOrg + ":" + RewardsFid, Fitid(account, sequence)),
                _ => throw new InvalidOperationException($"{account.Role} has no feed"),
            };
            txn.OlFiId = fiId;
            txn.OlFitid = fitid;
            txn.OlOrigPayee = txn.BankPayee ?? txn.Description.ToUpperInvariant();
            txn.OlOrigMemo = txn.BankMemo;
            txn.EnteredDaysLater = 1 + (int)(sequence % 2);
        }

        book.AddCheck("provenance", Problems);
    }

    /// <summary>The account tag a FITID starts with.</summary>
    public static string FitidTag(DemoAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Role switch
        {
            DemoAccountRoles.JointChecking => "CHK",
            DemoAccountRoles.Savings => "SAV",
            DemoAccountRoles.ClassicCard => "CLS",
            DemoAccountRoles.RewardsCard => "RWD",
            _ => throw new ArgumentException($"{account.Role} has no feed", nameof(account)),
        };
    }

    private static string Fitid(DemoAccount account, long sequence) =>
        FitidTag(account) + "-" + sequence.ToString("D7", CultureInfo.InvariantCulture);

    // Feed fields only on feed rows, never on an investment-route row, and no
    // (ol_fi_id, ol_fitid_1) pair twice.
    private static IEnumerable<string> Problems(DemoBook book)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var txn in book.Transactions)
        {
            var feed = IsFeedRow(book, txn);
            var carries = txn.OlFiId is not null || txn.OlFitid is not null || txn.OlOrigPayee is not null || txn.OlOrigMemo is not null;
            if (carries && !feed) yield return $"{txn.Pattern} row {txn.Id} carries feed fields but is not a feed row";
            if (feed && (txn.OlFiId is null || txn.OlFitid is null)) yield return $"{txn.Pattern} row {txn.Id} is a feed row with no FITID";
            if (txn.OlFiId is { } fi && txn.OlFitid is { } fitid && !seen.Add(fi + "\u001F" + fitid))
                yield return $"({fi}, {fitid}) is on two rows";
        }
    }
}
