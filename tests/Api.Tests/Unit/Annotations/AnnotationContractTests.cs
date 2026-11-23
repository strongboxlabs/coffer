using System.Reflection;
using System.Text.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Unit.Annotations;

/// <summary>
/// The frozen memo-and-tags contract. The readers, the writers and the SPA are
/// built against it separately, so a renamed field here is a break in code that
/// cannot see it: these tests make the rename deliberate.
/// </summary>
public sealed class AnnotationContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_row_notes_fields_have_the_frozen_wire_names()
    {
        Assert.Equal(
            ["headerMemo", "headerTags", "inheritFromAccountId", "inheritedMemo", "inheritedTags", "isReadOnly",
             "memo", "memoInherited", "notesKind", "tags", "tagsInherited"],
            WireNames(typeof(IRegisterRowNotes)));
        Assert.Equal(["savedReportsRepointed", "transactionsRepointed"], WireNames(typeof(IMergeTagResponseNotes)));
        Assert.Equal(["memo", "tags", "transactionMemo"], WireNames(typeof(ITransactionLineNotes)));
    }

    /// <summary>The fields every register row carries; a bank row carries these and no others.</summary>
    private static readonly string[] RowFields =
    [
        "accountId", "accountPostingsOnHeader", "amount", "balanceAfter", "balanceCurrencyCode", "checkNumber",
        "clearedAt", "clearedByUserId", "counterpartyAccountId", "counterpartyAccountName", "counterpartyAccountType",
        "counterpartyAmount", "counterpartyCurrencyCode", "counterpartyId", "createdAt", "currencyCode",
        "derivedAction", "externalId", "headerAccountNetAmount", "headerId", "headerMemo", "headerTags",
        "headerTotalPostings", "id", "importSource", "inheritFromAccountId", "inheritedMemo", "inheritedTags", "isHidden",
        "isMergeWinner", "isPending", "isReadOnly", "kind", "legIndex", "memo", "memoInherited", "needsReview", "notesKind",
        "onlineMatchFiId", "onlineMatchFitid", "origin", "originalAmount", "payee", "postedAt", "providerKey",
        "providerRawPayload", "reviewClearedAmount", "reviewClearedByMatch", "roundedAtImport", "status", "tags",
        "tagsInherited", "transactedAt", "txnGroupId",
    ];

    [Fact]
    public void A_register_row_carries_its_wire_fields_and_none_of_the_raw_notes()
    {
        // The raw per-leg notes (legMemo, legTags) were never the row's to show, and the
        // override flag has no reader in the SPA or MCP: none of the three is sent.
        Assert.Equal(RowFields, JsonNames(new BankRowDto
        {
            Id = Guid.Empty, AccountId = Guid.Empty, Amount = 0m, PostedAt = default, Status = "uncleared",
            IsHidden = false, Origin = "manual", IsPending = false, CounterpartyId = Guid.Empty, LegIndex = 0,
            Tags = [], HeaderId = Guid.Empty, CreatedAt = default, NeedsReview = false, IsMergeWinner = false,
            AccountPostingsOnHeader = 1, HeaderTotalPostings = 1,
        }));
        string[] investmentOnly =
        [
            "categoryAccountId", "categoryAccountName", "categoryAccountType", "feeAmount", "feeCategoryId",
            "feeCategoryName", "ingestActionHint", "ingestAmount", "ingestFee", "ingestSecurityId", "ingestSecurityTickerHint",
            "ingestShares", "ingestUnitPrice", "investmentAction", "postingRole", "quantity", "securityId", "securityName",
            "securityTicker", "settledAmount", "transferAccountId", "transferAccountName", "transferAccountType",
            "transferAmount", "transferCurrencyCode", "unitPrice",
        ];
        Assert.Equal(RowFields.Concat(investmentOnly).Order(StringComparer.Ordinal), JsonNames(new InvestmentRowDto
        {
            Id = Guid.Empty, AccountId = Guid.Empty, Amount = 0m, PostedAt = default, Status = "uncleared",
            IsHidden = false, Origin = "manual", IsPending = false, CounterpartyId = Guid.Empty, LegIndex = 0,
            Tags = [], HeaderId = Guid.Empty, CreatedAt = default, NeedsReview = false, IsMergeWinner = false,
            AccountPostingsOnHeader = 1, HeaderTotalPostings = 1,
        }));
    }

    /// <summary>The JSON property names <paramref name="row"/> serializes with, as the register sends it, in ordinal order.</summary>
    private static IEnumerable<string> JsonNames(RegisterRowDto row)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row, Web));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Every_kind_has_its_own_wire_name()
    {
        var names = Enum.GetValues<LegKind>().Select(NotesKinds.Of).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ["eventCounterpartOne", "eventOpposite", "eventSide", "onePosting", "splitOpposite", "splitSide"],
            names.Order(StringComparer.Ordinal));
        Assert.Equal("onePosting", NotesKinds.Of(LegKind.OnePosting));
        Assert.Equal("eventCounterpartOne", NotesKinds.Of(LegKind.EventCounterpartOne));
        Assert.Equal("splitSide", NotesKinds.Of(LegKind.SplitSide));
        Assert.Equal("splitOpposite", NotesKinds.Of(LegKind.SplitOpposite));
        Assert.Equal("eventSide", NotesKinds.Of(LegKind.EventSide));
        Assert.Equal("eventOpposite", NotesKinds.Of(LegKind.EventOpposite));
    }

    [Theory]
    [InlineData("""{"side":"00000000-0000-0000-0000-000000000001"}""", FieldEditKind.Leave, null)]
    [InlineData("""{"side":"00000000-0000-0000-0000-000000000001","memo":null}""", FieldEditKind.Inherit, null)]
    [InlineData("""{"side":"00000000-0000-0000-0000-000000000001","memo":""}""", FieldEditKind.Inherit, null)]
    [InlineData("""{"side":"00000000-0000-0000-0000-000000000001","memo":" \t"}""", FieldEditKind.Inherit, null)]
    [InlineData("""{"side":"00000000-0000-0000-0000-000000000001","memo":"trip"}""", FieldEditKind.Set, "trip")]
    [InlineData("""{"side":"00000000-0000-0000-0000-000000000001","memo":" trip\t"}""", FieldEditKind.Set, "trip")]
    public void An_omitted_memo_leaves_and_a_null_or_blank_one_inherits(string json, FieldEditKind kind, string? value)
    {
        var notes = JsonSerializer.Deserialize<TransactionNotesRequest>(json, Web)!;

        var edit = notes.MemoEdit();

        Assert.Equal(kind, edit.Kind);
        Assert.Equal(value, edit.Value);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), notes.Side);
    }

    [Theory]
    [InlineData("""{"index":1}""", FieldEditKind.Leave, 0)]
    [InlineData("""{"index":1,"tags":null}""", FieldEditKind.Inherit, 0)]
    [InlineData("""{"index":1,"tags":[]}""", FieldEditKind.Inherit, 0)]
    [InlineData("""{"index":1,"tags":["car","trip"]}""", FieldEditKind.Set, 2)]
    public void An_omitted_tag_list_leaves_and_a_null_or_empty_one_inherits(string json, FieldEditKind kind, int count)
    {
        var line = JsonSerializer.Deserialize<TransactionLineNotesRequest>(json, Web)!;

        var edit = line.TagNamesEdit();

        Assert.Equal(kind, edit.Kind);
        Assert.Equal(count, edit.Value?.Count ?? 0);
        Assert.Equal(1, line.Index);
        Assert.Equal(FieldEditKind.Leave, line.MemoEdit().Kind);
    }

    [Fact]
    public void A_notes_body_without_a_side_is_refused_as_malformed_not_read_as_the_empty_account()
    {
        // An omitted side would otherwise bind to Guid.Empty and surface as
        // "side-not-on-transaction", which tells the client the wrong thing.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TransactionNotesRequest>("""{"memo":"trip"}""", Web));
    }

    [Fact]
    public void A_serialized_request_omits_what_it_does_not_set()
    {
        // As on PatchTransactionRequest: a .NET caller that builds the DTO must not
        // send explicit nulls that would CLEAR the fields it did not name.
        var json = JsonSerializer.Serialize(new TransactionNotesRequest { Side = Guid.Empty, Memo = "trip" }, Web);

        Assert.Equal("""{"side":"00000000-0000-0000-0000-000000000000","memo":"trip"}""", json);
    }

    [Fact]
    public void The_investment_memo_is_presence_tracked_and_its_tags_keep_their_meaning()
    {
        Assert.Equal(["hasMemo", "memo", "tags"], WireNames(typeof(IInvestmentWriteNotes)));

        Assert.Equal(FieldEditKind.Leave, InvestmentNotesEdits.Memo(false, null).Kind);
        Assert.Equal(FieldEditKind.Inherit, InvestmentNotesEdits.Memo(true, null).Kind);
        Assert.Equal(FieldEditKind.Inherit, InvestmentNotesEdits.Memo(true, " ").Kind);
        Assert.Equal(FieldEdit<string>.Set("dividend"), InvestmentNotesEdits.Memo(true, " dividend "));

        Assert.Equal(FieldEditKind.Leave, InvestmentNotesEdits.Tags(null).Kind);
        Assert.Equal(FieldEditKind.Inherit, InvestmentNotesEdits.Tags([]).Kind);
        Assert.Equal(FieldEditKind.Set, InvestmentNotesEdits.Tags(["income"]).Kind);
    }

    [Fact]
    public void Set_transaction_tags_names_its_account_its_refusals_and_where_tags_land()
    {
        Assert.Equal("accountId", SetTransactionTagsContract.AccountIdParameter);
        Assert.Equal(BusinessError.Codes.ReadOnlyRow, SetTransactionTagsContract.ReadOnlyRowReason);
        Assert.Equal(BusinessError.Codes.SideNotOnTransaction, SetTransactionTagsContract.SideNotOnTransactionReason);
        Assert.Equal(["landsOn", "refused", "transactionId"], WireNames(typeof(ISetTransactionTagsPlacement)));

        Assert.Equal("side", SetTransactionTagsContract.LandsOn(Scenarios.OneSplit(Scenarios.Bank, Scenarios.Bank, Scenarios.Groceries)));
        Assert.Equal("side", SetTransactionTagsContract.LandsOn(Scenarios.XfrToBank()));
        Assert.Equal("transaction", SetTransactionTagsContract.LandsOn(
            Scenarios.MultiSplit(Scenarios.Bank, Scenarios.Bank, Scenarios.Groceries, Scenarios.Fuel)));
        Assert.Equal("transaction", SetTransactionTagsContract.LandsOn(Scenarios.BuyWithFee()));
    }

    [Fact]
    public void The_api_spells_every_violation_code_the_planner_raises()
    {
        var planner = Constants(typeof(AnnotationViolationCodes));
        var api = Constants(typeof(BusinessError.Codes));

        Assert.All(planner, code => Assert.Contains(code, api));
        Assert.Equal(BusinessError.Codes.TransactionTagsTooMany, AnnotationViolationCodes.TagsTooMany);
    }

    private static List<string> WireNames(Type contract) =>
        [.. contract.GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order(StringComparer.Ordinal)];

    private static List<string> Constants(Type holder) =>
        [.. holder.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
}
