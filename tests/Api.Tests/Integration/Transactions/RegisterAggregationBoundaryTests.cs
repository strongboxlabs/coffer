using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The register's running balance stays exact at money magnitudes a real ledger
/// reaches.
/// </summary>
/// <remarks>
/// <c>balance_after</c> is a running SUM over every prior leg on the account, so it is
/// the one figure in the app whose error ACCUMULATES: a per-row rounding fault is
/// invisible at three figures and compounds over a decade of rows. Existing coverage
/// uses -10 / -20 / -30, where any plausible bug still gives the right answer.
/// <para>
/// The amounts here come from <see cref="Boundary"/>, including
/// <see cref="Boundary.LargeMoney"/>, and the balance is asserted after EACH row
/// rather than only at the end — a drift that cancels out by the final row would
/// otherwise pass.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RegisterAggregationBoundaryTests
{
    private readonly PostgresFixture _fixture;

    public RegisterAggregationBoundaryTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    public static TheoryData<string, decimal> Amounts => new()
    {
        // Three figures — the size existing register fixtures use.
        { "typical", -25.37m },
        // A boundary case's own basis, so the register agrees with the investment
        // suites about what a large position costs.
        { "position-basis", -Boundary.LargeFractional.Basis },
        // Ten figures: large enough that a summed running balance leaves the range
        // where a double-backed accumulator would still look right.
        { "large-money", -Boundary.LargeMoney },
    };

    [Theory]
    [MemberData(nameof(Amounts))]
    public async Task Running_balance_is_exact_after_every_row(string name, decimal amount)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal value)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = value },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        // Five rows of the same amount: the expected balance after row n is n × amount,
        // which is exact in decimal and drifts visibly in anything else.
        var headers = new List<Guid>();
        for (var i = 0; i < 5; i++) headers.Add(await CreateAsync(10 + i, amount));

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances?account_id={bank.Id}",
            new HeaderBalancesRequest { HeaderIds = headers.ToArray() });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var balances = await resp.Content.ReadFromJsonAsync<HeaderBalanceDto[]>();
        Assert.NotNull(balances);
        var byId = balances!.ToDictionary(b => b.HeaderId);

        for (var i = 0; i < headers.Count; i++)
        {
            var expected = amount * (i + 1);
            var row = byId[headers[i]];
            Assert.Equal(amount, row.NetAmount);
            Assert.True(expected == row.BalanceAfter,
                $"{name}: balance after row {i + 1} was {row.BalanceAfter}, expected {expected}");
            // Money stays at the cent: an accumulator that widened the scale would
            // show up here before the value itself was visibly wrong.
            Assert.Equal(decimal.Round(row.BalanceAfter, Boundary.MoneyScale), row.BalanceAfter);
        }
    }
}
