using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

public sealed class SecurityMapperTests
{
    private static readonly Guid TestLedgerId = TestLedger.Id;

    private static MdItem ItemFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdItemReader.ReadString(wrapped).AllItems.Single();
    }

    private static MdCurr CurrFromJson(string fragment) => MdCurr.From(ItemFromJson(fragment));

    /// <summary>The security-type parameters of a type-s account, where Moneydance writes them.</summary>
    private static MdSecurityFields AcctFieldsFromJson(string fragment) =>
        MdAcct.From(ItemFromJson(fragment)).Security!;

    [Fact]
    public void Map_returns_null_for_non_security_curr()
    {
        var curr = CurrFromJson("""
            {"obj_type":"curr","id":"c-usd","name":"US Dollar","currid":"USD","isbase":"y"}
            """);
        Assert.Null(SecurityMapper.Map(curr, MdSecurityFields.None, TestLedgerId));
    }

    [Fact]
    public void Map_translates_a_typical_mutual_fund()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-1","name":"Fund A Emerging Markets",
              "currid":"^OEFQ","type":"s",
              "ticker":"IDXE","curr_id.CUSIP":"999888666",
              "hide_in_ui":"n"
            }
            """);
        var fields = AcctFieldsFromJson("""
            {
              "obj_type":"acct","id":"s-1","name":"Fund A Emerging Markets","type":"s",
              "parentid":"brk","currid":"sec-1",
              "sec_type":"2","sec_subtype":"International","sec_exchange":"NYSE"
            }
            """);

        var row = SecurityMapper.Map(curr, fields, TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal("sec-1", row!.ExternalId);
        Assert.Equal("IDXE", row.Ticker);
        Assert.Equal("999888666", row.Cusip);
        Assert.Equal("Fund A Emerging Markets", row.Name);
        Assert.Null(row.AssetClass);                       // a fund's class is unknown at import
        Assert.Equal("mutual_fund", row.VehicleType);
        Assert.Equal("import", row.ClassificationSource);
        Assert.Equal("assumed", row.ClassificationConfidence);
        Assert.Equal("NYSE", row.Exchange);
        Assert.True(row.IsActive);
        Assert.NotEqual(Guid.Empty, row.Id);
    }

    [Fact]
    public void Map_ignores_type_fields_left_on_the_curr()
    {
        // A curr carrying sec_type is not where Moneydance writes it; only the
        // account's parameters classify the security.
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-c","name":"Fund","currid":"^F","type":"s","ticker":"F",
              "sec_type":"3","sec_exchange":"NYSE"
            }
            """);

        var row = SecurityMapper.Map(curr, MdSecurityFields.None, TestLedgerId);

        Assert.NotNull(row);
        Assert.Null(row!.VehicleType);
        Assert.Null(row.AssetClass);
        Assert.Null(row.ClassificationSource);
        Assert.Null(row.ClassificationConfidence);
        Assert.Null(row.Exchange);
    }

    [Fact]
    public void Map_with_an_unrecognised_code_gives_vehicle_other()
    {
        var curr = CurrFromJson("""
            {"obj_type":"curr","id":"sec-o","name":"Odd","currid":"^O","type":"s","ticker":"O"}
            """);

        var row = SecurityMapper.Map(curr, new MdSecurityFields("7", null, null), TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal("other", row!.VehicleType);
        Assert.Null(row.AssetClass);
        Assert.Equal("import", row.ClassificationSource);
    }

    [Fact]
    public void Map_marks_hidden_securities_inactive()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-2","name":"Old Fund",
              "currid":"^Old","type":"s","ticker":"OLD","hide_in_ui":"y"
            }
            """);

        var row = SecurityMapper.Map(curr, new MdSecurityFields(MdSecurityType.MutualFund, null, null), TestLedgerId);

        Assert.NotNull(row);
        Assert.False(row!.IsActive);
    }

    [Fact]
    public void Map_falls_back_to_unnamed_for_blank_name()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-3","name":"",
              "currid":"^X","type":"s","ticker":"X"
            }
            """);

        var row = SecurityMapper.Map(curr, new MdSecurityFields(MdSecurityType.Stock, null, null), TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal("(unnamed)", row!.Name);
    }

    [Fact]
    public void Map_normalizes_empty_strings_to_null()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-4","name":"Test",
              "currid":"^T","type":"s","ticker":""
            }
            """);
        var fields = AcctFieldsFromJson("""
            {"obj_type":"acct","id":"s-4","name":"Test","type":"s","currid":"sec-4","sec_type":"3","sec_exchange":""}
            """);

        var row = SecurityMapper.Map(curr, fields, TestLedgerId);

        Assert.NotNull(row);
        Assert.Null(row!.Ticker);
        Assert.Null(row.Exchange);
    }

    [Theory]
    [InlineData(4, 4)]    // typical stock / ETF
    [InlineData(5, 5)]    // typical mutual fund
    [InlineData(9, 9)]    // some fund families' share classes
    [InlineData(0, 0)]    // edge: integer-only
    [InlineData(12, 12)]  // edge: matches schema upper bound post-migration 050
    [InlineData(null, 4)] // missing falls back to default
    [InlineData(13, 4)]   // out-of-bounds falls back rather than reject the row
    [InlineData(-1, 4)]
    public void Map_propagates_share_decimals_clamped_to_schema_bounds(int? mdDec, int expected)
    {
        // Per-security precision: MD's `dec` (MdCurr.Decimals) must reach the
        // investment mapper. A hardcoded divisor of 10^4 would silently lose
        // precision on any security whose `dec` differs.
        //
        // The schema CHECK allows [0,12] (migration 050) so a security with
        // dec=9 can round-trip correctly. The clamp follows in lockstep —
        // values 7..12 propagate through unchanged instead of falling back to 4.
        //
        // MD writes `dec` as a string-quoted integer in the export.
        var decFragment = mdDec is null ? string.Empty : $"\"dec\":\"{mdDec.Value}\",";
        var curr = CurrFromJson($$"""
            {
              "obj_type":"curr","id":"sec-dec","name":"Test",
              "currid":"^T","type":"s","ticker":"T",
              {{decFragment}}
              "hide_in_ui":"n"
            }
            """);

        var row = SecurityMapper.Map(curr, new MdSecurityFields(MdSecurityType.MutualFund, null, null), TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal(expected, row!.ShareDecimals);
    }
}
