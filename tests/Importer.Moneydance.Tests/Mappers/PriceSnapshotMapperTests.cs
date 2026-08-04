using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Unit tests for the csnap → security_prices translation introduced in
/// PR 2.7. Asserts that snapshots resolve to the right security, that
/// OHLCV fields ride through, and that snapshots whose currid points at a
/// plain currency (not a security) are skipped rather than fabricated as
/// rows.
/// </summary>
public sealed class PriceSnapshotMapperTests
{
    private static MdCsnap CsnapFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdCsnap.From(MdItemReader.ReadString(wrapped).AllItems.Single());
    }

    private static IReadOnlyDictionary<string, SecurityRef> SecurityMap(string mdId, Guid securityId, int decimals = 4) =>
        new Dictionary<string, SecurityRef>(StringComparer.Ordinal) { [mdId] = new(securityId, decimals) };

    [Fact]
    public void Map_translates_a_real_export_csnap_using_urt()
    {
        // Real-world exports use `urt` (unadjusted rate) — Moneydance stores
        // securities as currencies, so urt = 1/share_price. This snapshot
        // (urt=0.01) corresponds to a $100/share price.
        var securityId = Guid.NewGuid();
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-1",
              "curr":"sec-1","dt":"20240115",
              "urt":"0.01"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap, SecurityMap("sec-1", securityId));

        Assert.Null(result.Skip);
        Assert.NotNull(result.Row);
        var row = result.Row!;
        Assert.Equal(securityId, row.SecurityId);
        Assert.Equal(100.0000m,  row.Price);                // 1 / 0.01 = $100
        Assert.Null(row.High);
        Assert.Null(row.Low);
        Assert.Null(row.Volume);
        Assert.Equal(2024, row.PriceDate.Year);
        Assert.Equal(1,    row.PriceDate.Month);
        Assert.Equal(15,   row.PriceDate.Day);
    }

    [Fact]
    public void Map_inverts_OHLCV_swap_when_present()
    {
        // urt is reciprocal: a higher urt means a lower price. So urt.hi maps
        // to price.low and urt.lo maps to price.high. Volume is plain.
        var securityId = Guid.NewGuid();
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-2",
              "curr":"sec-1","dt":"20240115",
              "urt":"0.01","hi":"0.0102","lo":"0.0099","vol":"1234567"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap, SecurityMap("sec-1", securityId));

        Assert.Null(result.Skip);
        var row = result.Row!;
        Assert.Equal(100.0000m, row.Price);                // 1 / 0.01
        // Smaller urt → bigger price: lo=0.0099 → high price = 1/0.0099 ≈ 101.0101
        Assert.Equal(101.0101m, row.High);
        // Bigger urt → smaller price: hi=0.0102 → low price = 1/0.0102 ≈ 98.0392
        Assert.Equal(98.0392m,  row.Low);
        Assert.Equal(1234567L,  row.Volume);
    }

    [Fact]
    public void Map_falls_back_to_legacy_rate_field_when_urt_absent()
    {
        // Older / synthetic exports may write `rate` instead of `urt`.
        var securityId = Guid.NewGuid();
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-legacy",
              "curr":"sec-1","dt":"20240115",
              "rate":"0.01"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap, SecurityMap("sec-1", securityId));

        Assert.Null(result.Skip);
        Assert.Equal(100.0000m, result.Row!.Price);
    }

    [Fact]
    public void Map_prefers_price_date_millis_over_dt_when_both_present()
    {
        var securityId = Guid.NewGuid();
        // 1576644200000 == 2019-12-18 06:03:20 UTC; dt is 20191214 (deliberately
        // different day) so the millis-vs-dt preference is observable.
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-3",
              "curr":"sec-1","dt":"20191214","price_date":"1576644200000",
              "urt":"0.02"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap, SecurityMap("sec-1", securityId));

        Assert.Null(result.Skip);
        Assert.Equal(2019, result.Row!.PriceDate.Year);
        Assert.Equal(12,   result.Row.PriceDate.Month);
        Assert.Equal(18,   result.Row.PriceDate.Day);    // from millis, not dt
    }

    [Fact]
    public void Map_skips_snapshot_for_unknown_security()
    {
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-x",
              "curr":"USD","dt":"20240115","urt":"1"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap,
            new Dictionary<string, SecurityRef>(StringComparer.Ordinal));

        Assert.Null(result.Row);
        Assert.Equal(PriceSnapshotMapper.SkipReason.UnknownSecurity, result.Skip);
    }

    [Fact]
    public void Map_skips_snapshot_with_missing_or_zero_rate()
    {
        // Real-world exports often emit empty csnaps (id + curr + ts only) when MD
        // hasn't recorded a rate for that security on that date — those skip
        // cleanly rather than being faked with a zero price.
        var securityId = Guid.NewGuid();
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-no-rate",
              "curr":"sec-1","dt":"20240115"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap, SecurityMap("sec-1", securityId));

        Assert.Null(result.Row);
        Assert.Equal(PriceSnapshotMapper.SkipReason.MissingPrice, result.Skip);
    }

    [Fact]
    public void Map_skips_snapshot_with_unparseable_date()
    {
        var securityId = Guid.NewGuid();
        var csnap = CsnapFromJson("""
            {
              "obj_type":"csnap","id":"snap-bad-date",
              "curr":"sec-1","urt":"0.01"
            }
            """);

        var result = PriceSnapshotMapper.Map(csnap, SecurityMap("sec-1", securityId));

        Assert.Null(result.Row);
        Assert.Equal(PriceSnapshotMapper.SkipReason.UnparseableDate, result.Skip);
    }
}
