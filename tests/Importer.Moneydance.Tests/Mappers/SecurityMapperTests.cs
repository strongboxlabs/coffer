using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

public sealed class SecurityMapperTests
{
    private static readonly Guid TestLedgerId = TestLedger.Id;

    private static MdCurr CurrFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdCurr.From(MdItemReader.ReadString(wrapped).AllItems.Single());
    }

    [Theory]
    // sec_type → (asset_class, vehicle_type): the vehicle is reliable; a fund's
    // economic class is unknown (null), set later in the editor (ADR-0067).
    [InlineData(null,          null,           null)]
    [InlineData("",            null,           null)]
    [InlineData("Mutual Fund", null,           "mutual_fund")]
    [InlineData("ETF",         null,           "etf")]
    [InlineData("Stock",       "equity",       "stock")]
    [InlineData("Bond",        "fixed_income", "bond")]
    [InlineData("CD",          "cash",         "cd")]
    [InlineData("Money Market","cash",         "money_market")]
    [InlineData("Option",      "alternative",  "option")]
    [InlineData("UnknownType", null,           "other")]
    public void TranslateSecType_splits_class_and_vehicle(string? secType, string? expectedClass, string? expectedVehicle)
    {
        var (assetClass, vehicleType) = SecurityMapper.TranslateSecType(secType);
        Assert.Equal(expectedClass, assetClass);
        Assert.Equal(expectedVehicle, vehicleType);
    }

    [Fact]
    public void Map_returns_null_for_non_security_curr()
    {
        var curr = CurrFromJson("""
            {"obj_type":"curr","id":"c-usd","name":"US Dollar","currid":"USD","isbase":"y"}
            """);
        Assert.Null(SecurityMapper.Map(curr, TestLedgerId));
    }

    [Fact]
    public void Map_translates_a_typical_mutual_fund()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-1","name":"DFA Emerging Markets",
              "currid":"^OEFQ","type":"s",
              "ticker":"DFCEX","curr_id.CUSIP":"233203488",
              "sec_type":"Mutual Fund","sec_exchange":"NYSE",
              "hide_in_ui":"n"
            }
            """);

        var row = SecurityMapper.Map(curr, TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal("sec-1", row!.ExternalId);
        Assert.Equal("DFCEX", row.Ticker);
        Assert.Equal("233203488", row.Cusip);
        Assert.Equal("DFA Emerging Markets", row.Name);
        Assert.Null(row.AssetClass);                       // a fund's class is unknown at import
        Assert.Equal("mutual_fund", row.VehicleType);
        Assert.Equal("import", row.ClassificationSource);
        Assert.Equal("assumed", row.ClassificationConfidence);
        Assert.Equal("NYSE", row.Exchange);
        Assert.True(row.IsActive);
        Assert.NotEqual(Guid.Empty, row.Id);
    }

    [Fact]
    public void Map_marks_hidden_securities_inactive()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-2","name":"Old Fund",
              "currid":"^Old","type":"s","ticker":"OLD",
              "sec_type":"Mutual Fund","hide_in_ui":"y"
            }
            """);

        var row = SecurityMapper.Map(curr, TestLedgerId);

        Assert.NotNull(row);
        Assert.False(row!.IsActive);
    }

    [Fact]
    public void Map_falls_back_to_unnamed_for_blank_name()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-3","name":"",
              "currid":"^X","type":"s","ticker":"X","sec_type":"Stock"
            }
            """);

        var row = SecurityMapper.Map(curr, TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal("(unnamed)", row!.Name);
    }

    [Fact]
    public void Map_normalizes_empty_strings_to_null()
    {
        var curr = CurrFromJson("""
            {
              "obj_type":"curr","id":"sec-4","name":"Test",
              "currid":"^T","type":"s","ticker":"",
              "sec_type":"Stock","sec_exchange":""
            }
            """);

        var row = SecurityMapper.Map(curr, TestLedgerId);

        Assert.NotNull(row);
        Assert.Null(row!.Ticker);
        Assert.Null(row.Exchange);
    }

    [Theory]
    [InlineData(4, 4)]    // typical stock / ETF
    [InlineData(5, 5)]    // typical mutual fund
    [InlineData(9, 9)]    // real-world admiral shares (BNDA et al)
    [InlineData(0, 0)]    // edge: integer-only
    [InlineData(12, 12)]  // edge: matches schema upper bound post-migration 050
    [InlineData(null, 4)] // missing falls back to default
    [InlineData(13, 4)]   // out-of-bounds falls back rather than reject the row
    [InlineData(-1, 4)]
    public void Map_propagates_share_decimals_clamped_to_schema_bounds(int? mdDec, int expected)
    {
        // Regression: the per-security precision bug. MD's `dec` was being
        // parsed (MdCurr.Decimals) but dropped on the floor; the investment
        // mapper hardcoded a divisor of 10^4 and silently lost precision on
        // any security whose `dec` differed.
        //
        // Migration 050 widened the schema CHECK from [0,6] to [0,12] so
        // real-world dec=9 mutual funds (BNDA, …) round-trip
        // correctly. The clamp follows in lockstep — values 7..12 now
        // propagate through unchanged instead of falling back to 4.
        //
        // MD writes `dec` as a string-quoted integer in the export.
        var decFragment = mdDec is null ? string.Empty : $"\"dec\":\"{mdDec.Value}\",";
        var curr = CurrFromJson($$"""
            {
              "obj_type":"curr","id":"sec-dec","name":"Test",
              "currid":"^T","type":"s","ticker":"T","sec_type":"Mutual Fund",
              {{decFragment}}
              "hide_in_ui":"n"
            }
            """);

        var row = SecurityMapper.Map(curr, TestLedgerId);

        Assert.NotNull(row);
        Assert.Equal(expected, row!.ShareDecimals);
    }
}
