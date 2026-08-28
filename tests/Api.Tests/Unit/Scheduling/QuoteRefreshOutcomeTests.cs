using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Scheduling;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Tests.Unit.Scheduling;

/// <summary>
/// The trap this whole outcome channel exists for.
/// </summary>
/// <remarks>
/// <para>
/// <c>QuoteOrchestrator</c> catches every provider exception into its error list and
/// returns normally. So a scheduled quote refresh in which EVERY provider was down
/// completed without throwing, the scheduler recorded a clean run, and a dead-man's
/// switch wired to "the handler did not throw" would have pinged its check green over a
/// ledger whose prices had not moved in weeks.
/// </para>
/// <para>
/// That is worse than having no monitor: it manufactures exactly the confidence the
/// monitor was supposed to earn. These tests pin the classification that prevents it,
/// and the boundary that keeps it from crying wolf.
/// </para>
/// </remarks>
public sealed class QuoteRefreshOutcomeTests
{
    private static QuoteRunOutcome Outcome(string[] providerKeys, params QuoteError[] errors) =>
        new(
            ProviderKeys: providerKeys,
            PricesInserted: 0,
            PricesUpdated: 0,
            PricesWrittenBySource: new Dictionary<string, int>(),
            SecuritiesUnresolved: Array.Empty<Guid>(),
            Errors: errors);

    private static QuoteError ProviderDown(string key) => new(
        SecurityId: null,
        Ticker: string.Empty,
        Code: QuoteRefreshJobHandler.ProviderExceptionCode,
        Message: $"{key}: connection refused");

    [Fact]
    public void A_run_where_every_provider_threw_is_not_a_success()
    {
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo", "simplefin-holdings"],
            ProviderDown("yahoo"),
            ProviderDown("simplefin-holdings")));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.Contains("No quote provider succeeded", result.Message);
        Assert.Contains("2 of 2", result.Message);

        // The provider's own words do NOT travel. QuoteOrchestrator builds these as
        // "{providerKey}: {ex.Message}", so joining them put raw exception text into a
        // summary that goes out to a configured webhook. The orchestrator logs each one
        // in full on this side of the wire.
        Assert.DoesNotContain("connection refused", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_provider_down_out_of_several_is_still_worth_saying()
    {
        // The run did work, so this is not a failure. It is still reported, because the
        // same provider failing every day is how prices go stale while the schedule
        // keeps reporting healthy runs.
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo", "simplefin-holdings"],
            ProviderDown("yahoo")));

        Assert.Equal(JobRunResult.Degraded, result.Result);
        Assert.Contains("A quote provider failed", result.Message);
    }

    [Fact]
    public void A_security_no_provider_covers_is_an_ordinary_healthy_run()
    {
        // The boundary that stops the monitor crying wolf. A delisted ticker produces an
        // error on a perfectly healthy refresh; classifying that as degraded every day
        // is how a monitor teaches its owner to ignore it. Only provider-exception counts.
        var result = QuoteRefreshJobHandler.Classify(Outcome(
            ["yahoo"],
            new QuoteError(
                SecurityId: Guid.NewGuid(),
                Ticker: "DELISTED",
                Code: "not-found",
                Message: "no quote for DELISTED")));

        Assert.Equal(JobRunResult.Ok, result.Result);
        Assert.Null(result.Message);
    }

    [Fact]
    public void A_clean_run_is_ok()
    {
        Assert.Equal(JobRunResult.Ok, QuoteRefreshJobHandler.Classify(Outcome(["yahoo"])).Result);
    }
}
