using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Pipeline;

/// <summary>
/// Unit tests for <see cref="TransactionImportStep.DedupByFitid"/>. The
/// dedup keeps the first occurrence of each <c>(online_match_fi_id,
/// online_match_fitid)</c> tuple and drops the rest along with their
/// legs. Headers without a complete FITID pair pass through untouched
/// (the DB's <c>uq_txn_headers_online_match</c> partial index only
/// enforces uniqueness when both fields are set).
/// </summary>
public sealed class DedupByFitidTests
{
    private static readonly Guid LedgerId = Guid.NewGuid();

    [Fact]
    public void Drops_second_occurrence_of_a_FITID_pair_and_its_legs()
    {
        var firstId  = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var headers = new List<TxnHeaderRow>
        {
            MakeHeader(firstId,  fiId: "fi-1", fitid: "fit-1"),
            MakeHeader(secondId, fiId: "fi-1", fitid: "fit-1"),
        };
        var legs = new List<TxnLegRow>
        {
            MakeLeg(firstId),
            MakeLeg(secondId),
        };

        var result = TransactionImportStep.DedupByFitid(headers, legs);

        Assert.Single(result.Headers);
        Assert.Equal(firstId, result.Headers[0].Id);
        Assert.Single(result.Legs);
        Assert.Equal(firstId, result.Legs[0].HeaderId);
        Assert.Equal(1, result.SkippedDuplicates);
    }

    [Fact]
    public void Passes_through_headers_with_no_FITID_pair()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var headers = new List<TxnHeaderRow>
        {
            MakeHeader(a, fiId: null, fitid: null),
            MakeHeader(b, fiId: null, fitid: null),
        };
        var legs = new List<TxnLegRow>
        {
            MakeLeg(a), MakeLeg(b),
        };

        var result = TransactionImportStep.DedupByFitid(headers, legs);

        Assert.Equal(2, result.Headers.Count);
        Assert.Equal(2, result.Legs.Count);
        Assert.Equal(0, result.SkippedDuplicates);
    }

    [Fact]
    public void Treats_FITID_without_fiId_as_no_pair()
    {
        // Partial unique index requires both fields. If only one is
        // set the constraint doesn't fire and we pass the row through.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var headers = new List<TxnHeaderRow>
        {
            MakeHeader(a, fiId: null,    fitid: "fit-1"),
            MakeHeader(b, fiId: "fi-1",  fitid: null),
        };

        var result = TransactionImportStep.DedupByFitid(headers, Array.Empty<TxnLegRow>());

        Assert.Equal(2, result.Headers.Count);
        Assert.Equal(0, result.SkippedDuplicates);
    }

    [Fact]
    public void Distinguishes_FITIDs_across_different_fi_ids()
    {
        // Same fitid string against different fi_ids = different
        // institutions emitting the same internal id. Both kept.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var headers = new List<TxnHeaderRow>
        {
            MakeHeader(a, fiId: "bank-A", fitid: "fit-1"),
            MakeHeader(b, fiId: "bank-B", fitid: "fit-1"),
        };

        var result = TransactionImportStep.DedupByFitid(headers, Array.Empty<TxnLegRow>());

        Assert.Equal(2, result.Headers.Count);
        Assert.Equal(0, result.SkippedDuplicates);
    }

    [Fact]
    public void Preserves_first_occurrence_order_when_dropping_duplicates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var headers = new List<TxnHeaderRow>
        {
            MakeHeader(a, fiId: "fi-1", fitid: "fit-1"),
            MakeHeader(b, fiId: "fi-2", fitid: "fit-2"),
            MakeHeader(c, fiId: "fi-1", fitid: "fit-1"),    // duplicate of a
        };

        var result = TransactionImportStep.DedupByFitid(headers, Array.Empty<TxnLegRow>());

        Assert.Equal(new[] { a, b }, result.Headers.Select(h => h.Id));
        Assert.Equal(1, result.SkippedDuplicates);
    }

    [Fact]
    public void Empty_input_returns_empty_result()
    {
        var result = TransactionImportStep.DedupByFitid(
            Array.Empty<TxnHeaderRow>(),
            Array.Empty<TxnLegRow>());

        Assert.Empty(result.Headers);
        Assert.Empty(result.Legs);
        Assert.Equal(0, result.SkippedDuplicates);
    }

    private static TxnHeaderRow MakeHeader(Guid id, string? fiId, string? fitid) =>
        new(
            Id:                  id,
            LedgerId:            LedgerId,
            Origin:              "manual",
            ExternalId:          $"md-{id:N}",
            Payee:               null,
            Memo:                null,
            PostedAt:            DateTimeOffset.UtcNow,
            TransactedAt:        null,
            Status:              "cleared",
            CheckNumber:         null,
            IsPending:           false,
            IsHidden:            false,
            IsMergedInto:        null,
            ImportSource:        "test",
            ClearedAt:           DateTimeOffset.UtcNow,
            ClearedByUserId:     null,
            OnlineMatchFitid:    fitid,
            OnlineMatchFiId:     fiId,
            Action:              null);

    private static TxnLegRow MakeLeg(Guid headerId) =>
        new(
            Id:           Guid.NewGuid(),
            HeaderId:     headerId,
            LedgerId:     LedgerId,
            AccountId:    Guid.NewGuid(),
            PostingIndex: 0,
            LegMemo:      null,
            Amount:       1m,
            SecurityId:   null,
            Quantity:     null,
            UnitPrice:    null,
            PostingRole:  null);
}
