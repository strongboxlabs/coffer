using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Json;

public sealed class MdAcctTests
{
    private static MdItem ReadOnlyAcct(string json)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{json}} ]
            }
            """;
        return MdItemReader.ReadString(wrapped).AllItems.Single();
    }

    [Fact]
    public void Parses_a_typical_bank_account()
    {
        var item = ReadOnlyAcct("""
            {
              "obj_type": "acct", "id": "bank-1", "name": "Northwind Checking",
              "type": "b", "currid": "USD",
              "is_inactive": "n", "hide": "no",
              "sbal": "850317",
              "bank_account_number": "xxx0000", "bank_name": "Northwind",
              "ofx_bank_id": "031176110",
              "comment": "primary checking"
            }
            """);

        var acct = MdAcct.From(item);

        Assert.Equal("bank-1", acct.Id);
        Assert.Equal("Northwind Checking", acct.Name);
        Assert.Equal("b", acct.TypeCode);
        Assert.Equal("USD", acct.CurrId);
        Assert.False(acct.IsInactive);
        Assert.False(acct.IsHidden);
        Assert.Equal(850317, acct.StartingBalance);
        Assert.Equal("xxx0000", acct.BankAccountNumber);
        Assert.Equal("Northwind", acct.BankName);
        Assert.Equal("031176110", acct.OfxBankId);
        Assert.Equal("primary checking", acct.Comment);
        Assert.False(acct.IsRoot);
        Assert.False(acct.IsSecuritySubAccount);
    }

    [Fact]
    public void Recognizes_security_sub_account_and_root()
    {
        var sec = MdAcct.From(ReadOnlyAcct("""
            {"obj_type":"acct","id":"sec-1","name":"IDXB","type":"s","currid":"sec-cur-1"}
            """));
        Assert.True(sec.IsSecuritySubAccount);
        Assert.False(sec.IsRoot);

        var root = MdAcct.From(ReadOnlyAcct("""
            {"obj_type":"acct","id":"root-1","name":"","type":"r","currid":"USD"}
            """));
        Assert.True(root.IsRoot);
        Assert.False(root.IsSecuritySubAccount);
    }

    [Fact]
    public void Parent_id_and_inactive_flag_round_trip()
    {
        var inactiveChild = MdAcct.From(ReadOnlyAcct("""
            {
              "obj_type":"acct","id":"a-2","name":"Old Loan","type":"l","currid":"USD",
              "parentid":"a-1","is_inactive":"y"
            }
            """));

        Assert.Equal("a-1", inactiveChild.ParentId);
        Assert.True(inactiveChild.IsInactive);
    }

    // ---- date_created → OpenedOn (ADR-0050 / mig 127) ---------------------
    //
    // MD records a Start Date for every account and the column has existed since
    // migration 127, but the importer never read it — so every imported account
    // had opened_on NULL while the schema doc and ADR-0050 both claimed it was
    // "seeded from MD on import".

    [Fact]
    public void Reads_the_account_start_date_from_date_created()
    {
        var item = ReadOnlyAcct("""
            {
              "obj_type": "acct", "id": "inv-1", "name": "Demo Investment Account",
              "type": "v", "currid": "USD",
              "date_created": "20260101",
              "creation_date": "1767286800000"
            }
            """);

        // Both present and in agreement — the tidy integer wins, no conversion.
        Assert.Equal(new DateOnly(2026, 1, 1), MdAcct.From(item).OpenedOn);
    }

    [Fact]
    public void Falls_back_to_creation_date_when_date_created_is_absent()
    {
        // MD is inconsistent about which it writes. On a real 781-account export,
        // creation_date covers 181 accounts (including all 50 investment ones)
        // while date_created covers only 64 — so reading the integer alone would
        // have left most accounts NULL.
        var item = ReadOnlyAcct("""
            {
              "obj_type": "acct", "id": "inv-2", "name": "Brokerage",
              "type": "v", "currid": "USD",
              "creation_date": "1767286800000"
            }
            """);

        Assert.Equal(new DateOnly(2026, 1, 1), MdAcct.From(item).OpenedOn);
    }

    [Fact]
    public void Epoch_start_dates_are_stable_across_timezones()
    {
        // MD stamps creation_date at LOCAL NOON — 1767286800000 is 17:00Z, i.e.
        // 12:00 US-Eastern. That convention is what makes taking the UTC date
        // safe: the instant sits far enough from either midnight that no
        // plausible offset moves it onto an adjacent day. Verified on the real
        // export: all 64 accounts carrying both fields agree, at every offset
        // from UTC-12 to UTC+2.
        var noonEastern = ReadOnlyAcct("""
            {"obj_type":"acct","id":"a","name":"x","type":"v","currid":"USD",
             "creation_date":"1767286800000"}
            """);
        Assert.Equal(new DateOnly(2026, 1, 1), MdAcct.From(noonEastern).OpenedOn);

        // The same wall-clock date stamped during daylight time (16:00Z).
        var noonEasternDst = ReadOnlyAcct("""
            {"obj_type":"acct","id":"b","name":"y","type":"v","currid":"USD",
             "creation_date":"1751385600000"}
            """);
        Assert.Equal(new DateOnly(2025, 7, 1), MdAcct.From(noonEasternDst).OpenedOn);
    }

    [Theory]
    [InlineData("\"date_created\": \"0\"")]         // MD's "unset"
    [InlineData("\"date_created\": \"\"")]          // empty
    [InlineData("\"date_created\": \"20261301\"")]  // month 13
    [InlineData("\"date_created\": \"20260230\"")]  // 30 February
    [InlineData("\"date_created\": \"not-a-date\"")]
    [InlineData("\"creation_date\": \"0\"")]        // unset on the epoch field too
    [InlineData("\"creation_date\": \"\"")]
    [InlineData("\"creation_date\": \"not-a-number\"")]
    // A malformed integer must not stop the fallback from rescuing the epoch one.
    [InlineData("\"date_created\": \"garbage\", \"creation_date\": \"0\"")]
    [InlineData("\"name\": \"no date at all\"")]    // both keys absent
    public void Leaves_the_start_date_null_when_md_has_no_usable_one(string field)
    {
        var item = ReadOnlyAcct($$"""
            {
              "obj_type": "acct", "id": "a-1", "name": "x",
              "type": "b", "currid": "USD",
              {{field}}
            }
            """);

        Assert.Null(MdAcct.From(item).OpenedOn);
    }

    [Fact]
    public void Loan_first_payment_date_uses_the_same_two_source_read()
    {
        // MdLoanFields derives FirstPaymentDate from the account's creation
        // stamp and read only `date_created`. On a real export just 2 of 6 loans
        // carry that field, so the other 4 amortized with no first-payment date.
        var item = ReadOnlyAcct("""
            {
              "obj_type": "acct", "id": "loan-1", "name": "Mortgage",
              "type": "o", "currid": "USD",
              "creation_date": "1767286800000",
              "int_rate": "0.035", "num_payments": "360", "pmts_per_year": "12"
            }
            """);

        var acct = MdAcct.From(item);
        Assert.NotNull(acct.Loan);
        Assert.Equal(new DateOnly(2026, 1, 1), acct.Loan!.FirstPaymentDate);
        // And the account's own Start Date comes from the same stamp.
        Assert.Equal(new DateOnly(2026, 1, 1), acct.OpenedOn);
    }

    [Fact]
    public void Throws_when_obj_type_is_wrong()
    {
        var item = ReadOnlyAcct("""
            {"obj_type":"curr","id":"x","name":"y","type":"s","currid":"z"}
            """);
        Assert.Throws<ArgumentException>(() => MdAcct.From(item));
    }

    [Fact]
    public void Throws_when_required_type_field_is_missing()
    {
        var item = ReadOnlyAcct("""
            {"obj_type":"acct","id":"a-1","name":"no type","currid":"USD"}
            """);
        Assert.Throws<InvalidDataException>(() => MdAcct.From(item));
    }
}
