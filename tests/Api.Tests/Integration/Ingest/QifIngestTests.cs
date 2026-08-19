using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// End-to-end checks for ADR-0042 — the QIF file import (a
/// workplace 401(k) plan and generic QIF). Fixtures mirror a real
/// workplace-plan export shape (MM/DD/YYYY dates, <c>U</c>+<c>T</c>
/// amounts, security names with a trailing parenthetical fund code,
/// memo with trailing pad spaces) but use generic fund names.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QifIngestTests
{
    private readonly PostgresFixture _fixture;

    public QifIngestTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static MultipartFormDataContent FileUpload(
        string qifBody,
        Guid? accountId = null,
        string? providerAccountId = null)
    {
        var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(Encoding.UTF8.GetBytes(qifBody));
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(stream, "file", "export.qif");
        if (accountId is not null)
            content.Add(new StringContent(accountId.Value.ToString()), "accountId");
        if (providerAccountId is not null)
            content.Add(new StringContent(providerAccountId), "providerAccountId");
        return content;
    }

    // -------------------------------------------------------------
    // Fixtures: real-shape minimal QIF bodies (generic fund names).
    // -------------------------------------------------------------

    /// <summary>
    /// workplace-plan-shape investment QIF. Covers the action variety
    /// seen in real data plus a ReinvDiv (to pin the $0-cash
    /// contract) and a StkSplit (unsupported → skip-with-warning):
    ///   Buy (Contribution), ShrsOut (Fees), Sell (Exchange Out),
    ///   ReinvDiv, StkSplit.
    /// Security names carry the workplace-plan trailing parenthetical
    /// fund code; memos carry the trailing pad spaces the exporter
    /// emits (the parser trims them).
    /// </summary>
    private const string InvestmentQif = """
        !Type:Invst
        D01/05/2024
        NBuy
        YGROWTH FUND(AAAA)
        I100.00000
        Q5.000
        U500.00
        T500.00
        MContribution
        ^
        D01/10/2024
        NShrsOut
        YGROWTH FUND(AAAA)
        I100.00000
        Q0.050
        U5.00
        T5.00
        MFees
        ^
        D03/14/2024
        NSell
        YGROWTH FUND(AAAA)
        I120.00000
        Q5.000
        U600.00
        T600.00
        MExchange Out
        ^
        D03/31/2024
        NReinvDiv
        YBOND FUND(BBBB)
        I10.00000
        Q1.500
        U15.00
        T15.00
        MDividend
        ^
        D04/01/2024
        NStkSplit
        YGROWTH FUND(AAAA)
        Q2.000
        ^
        D04/02/2024
        NVest
        YGROWTH FUND(AAAA)
        Q0.000980392
        ^
        """;

    // -------------------------------------------------------------
    // Preview
    // -------------------------------------------------------------

    [Fact]
    public async Task Preview_returns_single_investment_account_with_supported_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/preview",
            FileUpload(InvestmentQif));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var preview = await resp.Content.ReadFromJsonAsync<FileIngestPreviewResponse>();
        Assert.NotNull(preview);
        var account = Assert.Single(preview!.Accounts);
        Assert.Equal("qif", account.ProviderAccountId);
        Assert.Equal("investment", account.AccountType);
        Assert.Null(account.Currency);
        // 4 supported (Buy, ShrsOut, Sell, ReinvDiv); StkSplit + Vest skipped.
        Assert.Equal(4, account.TransactionCount);

        // The warning has to let the user find the row on their own
        // statement, so it carries the action token, the ticker, the
        // quantity and the date, plus why the action was declined
        // (see DescribeUnsupportedAction).
        var warnings = preview.Errors
            .Where(e => e.Code == "qif_investment_action_unsupported")
            .Select(e => e.Message)
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(
            "QIF StkSplit row skipped (AAAA, 2 units, 2024-04-01). "
                + "Stock splits are recorded on the security, not in the register.",
            warnings);
        // Fractional quantities keep full 12dp precision without
        // trailing zeros, matching the OFX provider's formatting.
        Assert.Contains(
            "QIF Vest row skipped (AAAA, 0.000980392 units, 2024-04-02). "
                + "Equity-compensation actions are outside the ADR-0027 action catalog.",
            warnings);
    }

    [Fact]
    public async Task Preview_rejects_empty_upload_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Array.Empty<byte>()), "file", "empty.qif" },
        };
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/preview", content);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    // -------------------------------------------------------------
    // Import
    // -------------------------------------------------------------

    [Fact]
    public async Task Import_persists_investment_actions_with_ticker_hints_and_carriers()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(InvestmentQif, accountId: brokerage.Id, providerAccountId: "qif"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif")
            .OrderBy(h => h.PostedAt)
            .ToListAsync();
        Assert.Equal(4, headers.Count);

        // Action mapping (faithful per ADR-0042): Buy→buy, ShrsOut→
        // sell (plain default; X-variants are the user's manual
        // upgrade), Sell→sell, ReinvDiv→dividend_reinvest.
        var byDate = headers.ToDictionary(
            h => h.PostedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h => h);
        Assert.Equal("buy", byDate["2024-01-05"].IngestActionHint);
        Assert.Equal("sell", byDate["2024-01-10"].IngestActionHint);
        Assert.Equal("sell", byDate["2024-03-14"].IngestActionHint);
        Assert.Equal("dividend_reinvest", byDate["2024-03-31"].IngestActionHint);

        // Ticker hint lifted from the trailing parenthetical fund code.
        Assert.Equal("AAAA", byDate["2024-01-05"].IngestSecurityTickerHint);
        Assert.Equal("BBBB", byDate["2024-03-31"].IngestSecurityTickerHint);
        // Payee is the security name with the parenthetical stripped.
        Assert.Equal("GROWTH FUND", byDate["2024-01-05"].Payee);

        // Prefill carriers from the wire (Q / I).
        Assert.Equal(5.000m, byDate["2024-01-05"].IngestShares);
        Assert.Equal(100.00m, byDate["2024-01-05"].IngestUnitPrice);

        // Memo (M) trailing pad spaces are trimmed.
        Assert.Equal("Contribution", byDate["2024-01-05"].Memo);
    }

    [Fact]
    public async Task Import_leaves_online_match_fitid_null_for_every_qif_row()
    {
        // QIF's external_id is a synthetic qif-<hash> — NOT an OFX
        // FITID. online_match_fitid is the OFX-protocol-only
        // cross-source-dedup substrate; a QIF import must never write
        // its synthetic id there (would pollute the OFX-only column).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(InvestmentQif, accountId: brokerage.Id, providerAccountId: "qif"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif")
            .ToListAsync();
        Assert.Equal(4, headers.Count);
        // external_id is the synthetic qif-<hash>; online_match_fitid
        // stays null on every row.
        Assert.All(headers, h => Assert.StartsWith("qif-", h.ExternalId));
        Assert.All(headers, h => Assert.Null(h.OnlineMatchFitid));
        Assert.All(headers, h => Assert.Null(h.OnlineMatchFiId));
    }

    [Fact]
    public async Task Import_buy_lands_negative_cash_sell_positive_reinvest_zero()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(InvestmentQif, accountId: brokerage.Id, providerAccountId: "qif"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif")
            .ToListAsync();
        var headerIds = headers.Select(h => h.Id).ToList();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => headerIds.Contains(l.HeaderId)
                        && l.AccountId == brokerage.Id)
            .Join(db.TxnHeaders.AsNoTracking(), l => l.HeaderId, h => h.Id,
                (l, h) => new { h.PostedAt, l.Amount })
            .ToListAsync();
        var cashByDate = legs.ToDictionary(
            x => x.PostedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            x => x.Amount);

        // Buy = cash out (negative); Sell = cash in (positive);
        // ReinvDiv = net-zero (ADR-0042, action-level contract).
        Assert.Equal(-500.00m, cashByDate["2024-01-05"]);
        Assert.Equal(600.00m, cashByDate["2024-03-14"]);
        Assert.Equal(0m, cashByDate["2024-03-31"]);
    }

    [Fact]
    public async Task Reimport_of_same_file_dedups_against_synthetic_external_ids()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var first = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(InvestmentQif, accountId: brokerage.Id, providerAccountId: "qif"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.Equal(4, firstResult!.TransactionsForReview);
        Assert.Equal(0, firstResult.AlreadyKnown);

        var second = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(InvestmentQif, accountId: brokerage.Id, providerAccountId: "qif"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondResult = await second.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.Equal(0, secondResult!.TransactionsForReview);
        Assert.Equal(4, secondResult.AlreadyKnown);

        // Still exactly 4 rows in the DB after the re-import.
        await using var db = _fixture.NewDbContext();
        var count = await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif");
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task Import_422s_when_account_does_not_belong_to_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);
        var foreignAccount = await otherLedger.AddInvestmentAccountAsync("foreign");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import",
            FileUpload(InvestmentQif, accountId: foreignAccount.Id, providerAccountId: "qif"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}

