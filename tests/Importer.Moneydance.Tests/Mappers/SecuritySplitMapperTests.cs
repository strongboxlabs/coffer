using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Unit tests for the csplit → security_splits translation (B0.7). Asserts
/// that splits resolve to the right security, that ratio rides through
/// verbatim, and that splits whose curr doesn't resolve to a known security
/// are skipped rather than fabricated as rows.
/// </summary>
public sealed class SecuritySplitMapperTests
{
    private static readonly Guid LedgerId = Guid.NewGuid();

    private static MdCsplit CsplitFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdCsplit.From(MdItemReader.ReadString(wrapped).AllItems.Single());
    }

    private static IReadOnlyDictionary<string, SecurityRef> SecurityMap(string mdId, Guid securityId, int decimals = 4) =>
        new Dictionary<string, SecurityRef>(StringComparer.Ordinal) { [mdId] = new(securityId, decimals) };

    [Fact]
    public void Map_translates_a_real_export_csplit()
    {
        // A 2-for-1 split in the shape a real export writes it.
        var securityId = Guid.NewGuid();
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"8ed56ccd-0ec2-4b34-884a-dfe4b3ecddf0",
              "curr":"a8e7b723-4fcc-4b12-8e1d-60d0883fcba1",
              "dt":"20240610","ratio":"2.0","oldshrs":"2","newshrs":"1",
              "ts":"1718029530123"
            }
            """);

        var result = SecuritySplitMapper.Map(
            csplit,
            SecurityMap("a8e7b723-4fcc-4b12-8e1d-60d0883fcba1", securityId),
            LedgerId);

        Assert.Null(result.Skip);
        Assert.NotNull(result.Row);
        var row = result.Row!;
        Assert.Equal(securityId, row.SecurityId);
        Assert.Equal(LedgerId, row.LedgerId);
        Assert.Equal(2.0m, row.Ratio);
        Assert.Equal(2m,   row.OldShares);
        Assert.Equal(1m,   row.NewShares);
        Assert.Equal("8ed56ccd-0ec2-4b34-884a-dfe4b3ecddf0", row.ExternalId);
    }

    [Fact]
    public void Map_prefers_ts_millis_over_dt_when_both_present()
    {
        var securityId = Guid.NewGuid();
        // ts falls on a later UTC day than dt, so the millis-vs-dt preference
        // is observable.
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"sp-1",
              "curr":"sec-1","dt":"20191214","ts":"1576836000000",
              "ratio":"2.0","oldshrs":"1","newshrs":"2"
            }
            """);

        var result = SecuritySplitMapper.Map(csplit, SecurityMap("sec-1", securityId), LedgerId);

        Assert.Null(result.Skip);
        Assert.Equal(2019, result.Row!.SplitAt.Year);
        Assert.Equal(12,   result.Row.SplitAt.Month);
        Assert.Equal(20,   result.Row.SplitAt.Day);            // from millis, not dt
    }

    [Fact]
    public void Map_falls_back_to_dt_when_ts_absent()
    {
        var securityId = Guid.NewGuid();
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"sp-2",
              "curr":"sec-1","dt":"20240115",
              "ratio":"3.0","oldshrs":"1","newshrs":"3"
            }
            """);

        var result = SecuritySplitMapper.Map(csplit, SecurityMap("sec-1", securityId), LedgerId);

        Assert.Null(result.Skip);
        Assert.Equal(2024, result.Row!.SplitAt.Year);
        Assert.Equal(1,    result.Row.SplitAt.Month);
        Assert.Equal(15,   result.Row.SplitAt.Day);
        Assert.Equal(3.0m, result.Row.Ratio);
    }

    [Fact]
    public void Map_supports_reverse_split_ratio_below_one()
    {
        var securityId = Guid.NewGuid();
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"sp-rev",
              "curr":"sec-1","dt":"20240115",
              "ratio":"0.1","oldshrs":"10","newshrs":"1"
            }
            """);

        var result = SecuritySplitMapper.Map(csplit, SecurityMap("sec-1", securityId), LedgerId);

        Assert.Null(result.Skip);
        Assert.Equal(0.1m, result.Row!.Ratio);
    }

    [Fact]
    public void Map_skips_split_for_unknown_security()
    {
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"sp-x",
              "curr":"USD","dt":"20240115","ratio":"2.0"
            }
            """);

        var result = SecuritySplitMapper.Map(
            csplit,
            new Dictionary<string, SecurityRef>(StringComparer.Ordinal),
            LedgerId);

        Assert.Null(result.Row);
        Assert.Equal(SecuritySplitMapper.SkipReason.UnknownSecurity, result.Skip);
    }

    [Fact]
    public void Map_skips_split_with_non_positive_ratio()
    {
        var securityId = Guid.NewGuid();
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"sp-zero",
              "curr":"sec-1","dt":"20240115","ratio":"0"
            }
            """);

        var result = SecuritySplitMapper.Map(csplit, SecurityMap("sec-1", securityId), LedgerId);

        Assert.Null(result.Row);
        Assert.Equal(SecuritySplitMapper.SkipReason.InvalidRatio, result.Skip);
    }

    [Fact]
    public void Map_skips_split_with_unparseable_date()
    {
        var securityId = Guid.NewGuid();
        var csplit = CsplitFromJson("""
            {
              "obj_type":"csplit","id":"sp-bad-date",
              "curr":"sec-1","ratio":"2.0"
            }
            """);

        var result = SecuritySplitMapper.Map(csplit, SecurityMap("sec-1", securityId), LedgerId);

        Assert.Null(result.Row);
        Assert.Equal(SecuritySplitMapper.SkipReason.UnparseableDate, result.Skip);
    }
}
