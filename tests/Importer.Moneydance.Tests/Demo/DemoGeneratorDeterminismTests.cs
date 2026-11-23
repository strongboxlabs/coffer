using System.Globalization;
using System.Security.Cryptography;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The generator is a pure function of its <see cref="DemoSpec"/>: the same seed and date
/// give the same bytes on every machine and in every culture, another seed gives
/// other bytes, and each pattern draws from its own streams.
/// </summary>
/// <remarks>
/// <para><b>Golden hashes.</b> The SHA-256 of each test span's export at
/// <see cref="DemoTestExports.DemoTestDate"/> is pinned below, and so are the hashes
/// of the card-statement CSV and of the manifest (budget targets, the rows the
/// seeder acts on), which the seeder writes into the product too. ANY change to the
/// generator's output (a pattern, a constant, the base, the writer, the manifest
/// builder) changes them: that is the point, an unintended change is caught here.
/// After an intended change, replace the values with the ones the failure message
/// prints.</para>
/// </remarks>
public sealed class DemoGeneratorDeterminismTests
{
    /// <summary>SHA-256 of <see cref="DemoTestExports.Full"/>'s JSON.</summary>
    private const string FullSha256 = "07bc0e371edbe9e923f5ca4e9c417a846b0d8e836fa6a4bdf508d0f1d9a9c73a";

    /// <summary>SHA-256 of <see cref="DemoTestExports.Short"/>'s JSON.</summary>
    private const string ShortSha256 = "943d86da9e55dcdf2f5dc68cf32ef3e3b23a76e02d29f6bc9e528c7f3914e432";

    /// <summary>SHA-256 of <see cref="DemoTestExports.Tiny"/>'s JSON.</summary>
    private const string TinySha256 = "cc4bccc2148bee28ae2934090bb71d95c87717a1eaee485d4d2cd64c11509d84";

    /// <summary>SHA-256 of <see cref="DemoTestExports.Full"/>'s card-statement CSV
    /// (UTF-8). The span does not change it (DemoSpanTests), so one value covers all
    /// three.</summary>
    private const string CsvSha256 = "0036c930f75471ffb2c99b597c77492fc13abef40aad78cfe60b1849a8d17003";

    /// <summary>SHA-256 of <see cref="DemoTestExports.Full"/>'s manifest serialized with
    /// <see cref="ManifestJson"/>; as for the CSV, one value covers all three spans.</summary>
    private const string ManifestSha256 = "f29926a02f9c311ab2b4f3cd2b2d2c5fb0b69aec6cbaab9bc8cc074a963da781";

    // Fixed serializer options, so a change to the runtime's defaults cannot move the
    // manifest hash.
    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void The_same_spec_gives_byte_identical_json_and_csv()
    {
        foreach (var (name, cached) in Builds())
        {
            var again = DemoExportGenerator.Generate(DemoTestExports.Spec(SpanOf(name)));
            Assert.True(cached.Json.AsSpan().SequenceEqual(again.Json), $"{name}: the JSON differs on a second run");
            Assert.Equal(cached.CardStatementCsv, again.CardStatementCsv);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(cached.Manifest),
                System.Text.Json.JsonSerializer.Serialize(again.Manifest));
        }
    }

    [Fact]
    public void Each_test_span_matches_its_golden_hash()
    {
        var actual = Builds().ToDictionary(b => b.Name, b => Sha256(b.Build.Json), StringComparer.Ordinal);
        var full = DemoTestExports.Full.Value;
        actual["Csv"] = Sha256(System.Text.Encoding.UTF8.GetBytes(full.CardStatementCsv));
        actual["Manifest"] = Sha256(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(full.Manifest, ManifestJson));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Full"] = FullSha256,
            ["Short"] = ShortSha256,
            ["Tiny"] = TinySha256,
            ["Csv"] = CsvSha256,
            ["Manifest"] = ManifestSha256,
        };
        Assert.True(
            expected.All(e => actual[e.Key] == e.Value),
            "The generated Demo changed. If the change is intended, update the golden hashes in "
            + nameof(DemoGeneratorDeterminismTests) + ":\n"
            + string.Join("\n", actual.Select(a => $"  {a.Key}Sha256 = \"{a.Value}\"")));
    }

    [Fact]
    public void Another_seed_gives_other_bytes()
    {
        var other = DemoExportGenerator.Generate(new DemoSpec(DemoSpec.DefaultSeed + 1, DemoTestExports.AsOf, DemoSpan.LastMonths(3)));
        Assert.False(other.Json.AsSpan().SequenceEqual(DemoTestExports.Tiny.Value.Json));
        Assert.NotEqual(Sha256(DemoTestExports.Tiny.Value.Json), Sha256(other.Json));
    }

    /// <summary>
    /// Perturbing one pattern's streams moves only that pattern's rows and the rows
    /// that follow balances (the savings simulator sweeps and tops up Joint Checking
    /// from whatever else landed there). Every other pattern draws from streams keyed
    /// by its own name, so its rows are identical.
    /// </summary>
    [Fact]
    public void Perturbing_one_patterns_stream_leaves_every_other_patterns_rows_identical()
    {
        const string perturbed = "tax-settlement";
        var balanceDriven = new HashSet<string>(StringComparer.Ordinal) { "savings" };

        var spec = DemoSpec.Full(DemoTestExports.AsOf);
        var plain = DemoExportGenerator.Run(spec, DemoExportGenerator.Patterns).Book;
        var moved = DemoExportGenerator.Run(spec, market => DemoExportGenerator.Patterns(market)
            .Select(p => p.Name == perturbed ? new OtherSeed(p) : p)
            .ToList()).Book;

        var before = plain.Transactions.ToDictionary(t => t.Id, Shape, StringComparer.Ordinal);
        var after = moved.Transactions.ToDictionary(t => t.Id, Shape, StringComparer.Ordinal);

        // The perturbed pattern really moved.
        var perturbedIds = plain.Transactions.Where(t => t.Pattern == perturbed).Select(t => t.Id).ToList();
        Assert.NotEmpty(perturbedIds);
        Assert.Contains(perturbedIds, id => !after.TryGetValue(id, out var shape) || shape != before[id]);

        // Every other pattern's rows are the same rows, written the same way.
        var compared = 0;
        foreach (var txn in plain.Transactions.Where(t => t.Pattern != perturbed && !balanceDriven.Contains(t.Pattern)))
        {
            Assert.True(after.TryGetValue(txn.Id, out var shape), $"{txn.Pattern} row {txn.Id} is gone");
            Assert.True(shape == before[txn.Id], $"{txn.Pattern} row {txn.Id} changed:\n{before[txn.Id]}\n{shape}");
            compared++;
        }
        Assert.Equal(
            moved.Transactions.Count(t => t.Pattern != perturbed && !balanceDriven.Contains(t.Pattern)), compared);
        Assert.True(compared > 5_000, $"only {compared} rows compared");
    }

    /// <summary>Everything a pattern decides about a row: not the feed ids, which the
    /// provenance pass numbers per account across every pattern's rows.</summary>
    private static string Shape(DemoTxn t) =>
        string.Join("|", t.Pattern, t.AccountId, t.Date, t.TaxDate, t.Description, t.Memo, t.Route, t.InvestTxnType,
            t.XferType, t.Status, t.CheckNumber, t.Tags, t.Entry, t.BankPayee, t.BankMemo, t.Exported)
        + "||" + string.Join("|", t.Splits.Select(s =>
            string.Join(",", s.AccountId, s.ParentAmount, s.SplitAmount, s.Description, s.Status, s.Tags, s.SplitType)));

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void The_output_is_identical_under_another_culture(string culture)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        DemoBuild build;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            build = DemoExportGenerator.Generate(DemoTestExports.Spec(DemoSpan.LastMonths(3)));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }

        Assert.True(build.Json.AsSpan().SequenceEqual(DemoTestExports.Tiny.Value.Json), $"the JSON differs under {culture}");
        Assert.Equal(DemoTestExports.Tiny.Value.CardStatementCsv, build.CardStatementCsv);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(DemoTestExports.Tiny.Value.Manifest, ManifestJson),
            System.Text.Json.JsonSerializer.Serialize(build.Manifest, ManifestJson));
    }

    private static IEnumerable<(string Name, DemoBuild Build)> Builds() =>
    [
        ("Full", DemoTestExports.Full.Value),
        ("Short", DemoTestExports.Short.Value),
        ("Tiny", DemoTestExports.Tiny.Value),
    ];

    private static DemoSpan SpanOf(string name) => name switch
    {
        "Full" => DemoSpan.Full,
        "Short" => DemoSpan.LastMonths(12),
        _ => DemoSpan.LastMonths(3),
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A pattern run with streams from another seed: same names, other draws.</summary>
    private sealed class OtherSeed(IDemoPattern inner) : IDemoPattern
    {
        public string Name => inner.Name;

        public int Rank => inner.Rank;

        public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book) =>
            inner.Emit(calendar, new DemoRngFactory(rng.Seed ^ 0x5EED_5EED_5EED_5EEDUL), book);
    }
}
