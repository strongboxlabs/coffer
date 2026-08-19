using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// End-to-end checks for ADR-0031 Phase 4 — the OFX/QFX file
/// import (slice 1: bank + credit card; investment lands in slice
/// 2). Exercises both the preview and import endpoints against
/// real-shape OFX 1.x SGML, OFX 2.x XML, and QFX wire formats.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OfxIngestTests
{
    private readonly PostgresFixture _fixture;

    public OfxIngestTests(PostgresFixture fixture)
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
        string ofxBody,
        Guid? accountId = null,
        string? providerAccountId = null)
    {
        var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(Encoding.UTF8.GetBytes(ofxBody));
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(stream, "file", "statement.qfx");
        if (accountId is not null)
            content.Add(new StringContent(accountId.Value.ToString()), "accountId");
        if (providerAccountId is not null)
            content.Add(new StringContent(providerAccountId), "providerAccountId");
        return content;
    }

    // -------------------------------------------------------------
    // Fixtures: real-shape minimal OFX/QFX bodies.
    // -------------------------------------------------------------

    /// <summary>
    /// OFX 1.x (SGML) bank statement. Single account, two
    /// transactions. The header block + unclosed tags are
    /// real-world (typical retail-bank export shape).
    /// </summary>
    private const string Ofx1BankSingleAccount = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <BANKMSGSRSV1>
        <STMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <STMTRS>
        <CURDEF>USD
        <BANKACCTFROM>
        <BANKID>021000021
        <ACCTID>1234567890
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260105
        <TRNAMT>-12.34
        <FITID>FITID-COFFEE-1
        <NAME>STARBUCKS
        <MEMO>STARBUCKS NYC
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20260115
        <TRNAMT>2500.00
        <FITID>FITID-PAYROLL-1
        <NAME>PAYROLL
        </STMTTRN>
        </BANKTRANLIST>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        </OFX>
        """;

    /// <summary>
    /// QFX (OFX 1.x + Intuit's INTU.BID header). Multi-account:
    /// bank checking + credit card. Used to verify the preview
    /// surfaces both blocks and the import correctly filters by
    /// providerAccountId.
    /// </summary>
    private const string QfxMultiAccount = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        <INTU.BID>12345
        </SONRS>
        </SIGNONMSGSRSV1>
        <BANKMSGSRSV1>
        <STMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <STMTRS>
        <CURDEF>USD
        <BANKACCTFROM>
        <BANKID>021000021
        <ACCTID>1111111111
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260110
        <TRNAMT>-50.00
        <FITID>FITID-BANK-A
        <NAME>GROCERY
        </STMTTRN>
        </BANKTRANLIST>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        <CREDITCARDMSGSRSV1>
        <CCSTMTTRNRS>
        <TRNUID>1
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <CCSTMTRS>
        <CURDEF>USD
        <CCACCTFROM>
        <ACCTID>4111111111111111
        </CCACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260112
        <TRNAMT>-19.99
        <FITID>FITID-CARD-A
        <NAME>NETFLIX
        </STMTTRN>
        </BANKTRANLIST>
        </CCSTMTRS>
        </CCSTMTTRNRS>
        </CREDITCARDMSGSRSV1>
        </OFX>
        """;

    // -------------------------------------------------------------
    // Preview
    // -------------------------------------------------------------

    [Fact]
    public async Task Preview_returns_discovered_accounts_for_multi_account_qfx()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/preview",
            FileUpload(QfxMultiAccount));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var preview = await resp.Content.ReadFromJsonAsync<FileIngestPreviewResponse>();
        Assert.NotNull(preview);
        Assert.Equal(2, preview!.Accounts.Count);

        var bank = Assert.Single(preview.Accounts, a => a.AccountType == "bank");
        Assert.Equal("021000021:1111111111", bank.ProviderAccountId);
        Assert.Equal(1, bank.TransactionCount);
        Assert.Equal("USD", bank.Currency);

        var card = Assert.Single(preview.Accounts, a => a.AccountType == "credit_card");
        Assert.Equal("card:4111111111111111", card.ProviderAccountId);
        Assert.Equal(1, card.TransactionCount);
    }

    [Fact]
    public async Task Preview_rejects_empty_upload_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/preview",
            new MultipartFormDataContent
            {
                { new StringContent("not a file"), "stray", "stray.txt" },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Preview_rejects_garbage_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/preview",
            FileUpload("this is not an OFX file at all"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    // -------------------------------------------------------------
    // Import
    // -------------------------------------------------------------

    [Fact]
    public async Task Import_persists_bank_transactions_with_ofx_origin_and_fitid()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(Ofx1BankSingleAccount, accountId: bank.Id, providerAccountId: "021000021:1234567890"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.TransactionsForReview);
        Assert.Equal(0, result.AlreadyKnown);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.Origin == "file_import" && h.ProviderKey == "ofx")
            .OrderBy(h => h.PostedAt)
            .ToListAsync();
        Assert.Equal(2, headers.Count);

        var coffee = headers[0];
        Assert.Equal("FITID-COFFEE-1", coffee.ExternalId);
        // OFX-protocol fields populated natively (mig 105: OFX
        // providers own this surface; SimpleFIN doesn't write here).
        Assert.Equal("FITID-COFFEE-1", coffee.OnlineMatchFitid);
        Assert.Equal("021000021", coffee.OnlineMatchFiId);
        Assert.Equal("STARBUCKS", coffee.Payee);
        Assert.True(coffee.NeedsReview);
    }

    [Fact]
    public async Task Import_dispatches_only_the_requested_provider_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Multi-account file; import only the bank block. The card
        // transaction must NOT be persisted.
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(QfxMultiAccount, accountId: bank.Id, providerAccountId: "021000021:1111111111"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.Origin == "file_import" && h.ProviderKey == "ofx")
            .Select(h => h.ExternalId)
            .ToListAsync();
        Assert.Single(headers);
        Assert.Equal("FITID-BANK-A", headers[0]);
    }

    [Fact]
    public async Task Reimport_dedups_against_existing_ofx_fitid()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // First import.
        var first = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(Ofx1BankSingleAccount, accountId: bank.Id, providerAccountId: "021000021:1234567890"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<FileIngestImportResponse>())!.TransactionsForReview);

        // Second import — same file, same FITIDs.
        var second = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(Ofx1BankSingleAccount, accountId: bank.Id, providerAccountId: "021000021:1234567890"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var result = await second.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(0, result!.TransactionsForReview);
        Assert.Equal(2, result.AlreadyKnown);

        // DB end-state: exactly 2 rows, not 4.
        await using var db = _fixture.NewDbContext();
        var count = await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == ledger.LedgerId && h.Origin == "file_import" && h.ProviderKey == "ofx");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Import_dedups_against_md_preserved_online_match_fitid_cross_source()
    {
        // Cross-source FITID dedup (the real double-entry bug). An
        // MD-imported ledger preserves OFX state on online_match_fi_id
        // (BANKID) + online_match_fitid (FITID) under a DIFFERENT
        // provider_key. The external_id/provider_key branch alone
        // misses it, so re-importing the same bank's OFX would
        // double-enter the row. The OFX-only online-match OR-branch
        // must recognise it as already-known.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var uncategorized = await ledger.AddBankAccountAsync("seed-counterparty");

        // Seed an MD-imported header carrying the same BANKID +
        // FITID the Ofx1BankSingleAccount fixture's first row uses
        // (BANKID 021000021, FITID FITID-COFFEE-1), under a non-ofx
        // provider_key and is_merged_into = null (a live header).
        await using (var seed = _fixture.NewDbContext())
        {
            var headerId = Guid.NewGuid();
            seed.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = headerId,
                LedgerId = ledger.LedgerId,
                Origin = "online_import",
                ProviderKey = "moneydance",        // != "ofx" — the cross-source case
                Payee = "STARBUCKS",
                PostedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), TransactedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),                IsPending = false,
                NeedsReview = false,
                ExternalId = "md-coffee-1",        // MD's own id — NOT the OFX FITID
                OnlineMatchFiId = "021000021",     // preserved OFX BANKID
                OnlineMatchFitid = "FITID-COFFEE-1", // preserved OFX FITID
                IsMergedInto = null,
            });
            seed.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledger.LedgerId,
                AccountId = bank.Id, PostingIndex = 0, Amount = -12.34m,
            });
            seed.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledger.LedgerId,
                AccountId = uncategorized.Id, PostingIndex = 0, Amount = 12.34m,
            });
            await seed.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Import the OFX file: its FITID-COFFEE-1 row matches the
        // seeded MD row by (BANKID, FITID); FITID-PAYROLL-1 is new.
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(Ofx1BankSingleAccount, accountId: bank.Id, providerAccountId: "021000021:1234567890"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.NotNull(result);
        // The coffee row is deduped against MD's preserved OFX state;
        // only the payroll row is inserted.
        Assert.Equal(1, result!.TransactionsForReview);
        Assert.Equal(1, result.AlreadyKnown);

        // No second coffee header was inserted under the ofx
        // provider_key — the OFX-origin set has exactly the one new
        // payroll row.
        await using var db = _fixture.NewDbContext();
        var ofxHeaders = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "ofx")
            .Select(h => h.ExternalId)
            .ToListAsync();
        Assert.Equal(new[] { "FITID-PAYROLL-1" }, ofxHeaders);

        // The coffee FITID exists exactly once across the whole
        // ledger (the seeded MD row) — never double-entered.
        var coffeeCount = await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == ledger.LedgerId
                             && h.OnlineMatchFitid == "FITID-COFFEE-1");
        Assert.Equal(1, coffeeCount);
    }

    // -------------------------------------------------------------
    // Slice 2: Investment (INVSTMTMSGSRSV1) — action mapping +
    // SECLIST CUSIP→ticker + skipped-type warnings.
    // -------------------------------------------------------------

    /// <summary>
    /// OFX investment statement covering one of each supported
    /// transaction type plus two skipped TRANSFERs — one whole-unit
    /// IN, one fractional OUT — with a SECLIST entry resolving the
    /// security's CUSIP to a ticker.
    /// Synthesised — no real account names, FIIDs, or CUSIPs.
    /// </summary>
    private const string OfxInvestmentStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <INVSTMTMSGSRSV1>
        <INVSTMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <INVSTMTRS>
        <DTASOF>20260131120000
        <CURDEF>USD
        <INVACCTFROM>
        <BROKERID>brokerX
        <ACCTID>INV-0001
        </INVACCTFROM>
        <INVTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <BUYSTOCK>
        <INVBUY>
        <INVTRAN>
        <FITID>INV-FITID-BUY-1
        <DTTRADE>20260105
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <UNITS>10
        <UNITPRICE>50.00
        <COMMISSION>1.00
        <TOTAL>-501.00
        <SUBACCTSEC>CASH
        <SUBACCTFUND>CASH
        </INVBUY>
        <BUYTYPE>BUY
        </BUYSTOCK>
        <SELLSTOCK>
        <INVSELL>
        <INVTRAN>
        <FITID>INV-FITID-SELL-1
        <DTTRADE>20260110
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <UNITS>5
        <UNITPRICE>55.00
        <TOTAL>275.00
        <SUBACCTSEC>CASH
        <SUBACCTFUND>CASH
        </INVSELL>
        <SELLTYPE>SELL
        </SELLSTOCK>
        <INCOME>
        <INVTRAN>
        <FITID>INV-FITID-DIV-1
        <DTTRADE>20260115
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <INCOMETYPE>DIV
        <TOTAL>3.50
        <SUBACCTSEC>CASH
        <SUBACCTFUND>CASH
        </INCOME>
        <REINVEST>
        <INVTRAN>
        <FITID>INV-FITID-REINV-1
        <DTTRADE>20260120
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <INCOMETYPE>DIV
        <TOTAL>-12.00
        <SUBACCTSEC>CASH
        <UNITS>0.2
        <UNITPRICE>60.00
        </REINVEST>
        <TRANSFER>
        <INVTRAN>
        <FITID>INV-FITID-XFR-1
        <DTTRADE>20260125
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <SUBACCTSEC>CASH
        <UNITS>1
        <TFERACTION>IN
        <POSTYPE>LONG
        </TRANSFER>
        <TRANSFER>
        <INVTRAN>
        <FITID>INV-FITID-XFR-2
        <DTTRADE>20260131
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <SUBACCTSEC>OTHER
        <UNITS>-0.000980392
        <TFERACTION>OUT
        <POSTYPE>LONG
        </TRANSFER>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        <SECLISTMSGSRSV1>
        <SECLIST>
        <STOCKINFO>
        <SECINFO>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <SECNAME>Fake Test Stock
        <TICKER>FAKE
        </SECINFO>
        </STOCKINFO>
        </SECLIST>
        </SECLISTMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public async Task Preview_surfaces_investment_block_with_supported_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/preview",
            FileUpload(OfxInvestmentStatement));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var preview = await resp.Content.ReadFromJsonAsync<FileIngestPreviewResponse>();
        Assert.NotNull(preview);
        var inv = Assert.Single(preview!.Accounts);
        Assert.Equal("investment", inv.AccountType);
        Assert.Equal("inv:brokerX:INV-0001", inv.ProviderAccountId);
        // 4 supported (BUY/SELL/INCOME/REINVEST) + 0 INVBANKTRAN + 0
        // skipped (both TRANSFERs warn but aren't counted toward import).
        Assert.Equal(4, inv.TransactionCount);
        // TRANSFER surfaces as a warning so the user knows it was
        // skipped — slice 2 does not yet support share-only moves.
        var warnings = preview.Errors
            .Where(e => e.Code == "ofx_investment_type_unsupported")
            .Select(e => e.Message)
            .ToList();
        Assert.Equal(2, warnings.Count);

        // The warning has to let the user find the row on their own
        // statement, so it carries the wire tag + direction, the
        // resolved ticker, the units and the trade date — and NOT the
        // OfxNet class name or the FITID (see DescribeUnsupported).
        Assert.Contains(
            "OFX TRANSFER (IN) row skipped (FAKE, 1 units, 2026-01-25). "
                + "Share-only moves aren't imported in this slice.",
            warnings);
        // Fractional units keep full 12dp precision without trailing
        // zeros — a sub-cent residual sweep is the real-world shape
        // that motivated the message (a 401(k) recordkeeper moving
        // 0.00098 shares out).
        Assert.Contains(
            "OFX TRANSFER (OUT) row skipped (FAKE, -0.000980392 units, 2026-01-31). "
                + "Share-only moves aren't imported in this slice.",
            warnings);
        Assert.DoesNotContain(warnings, m => m.Contains("OfxTransfer", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, m => m.Contains("INV-FITID-XFR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_persists_investment_actions_with_ticker_hints()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(OfxInvestmentStatement, accountId: brokerage.Id, providerAccountId: "inv:brokerX:INV-0001"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(4, result!.TransactionsForReview);
        // TRANSFER stays in the Errors list (warning, not failure).
        Assert.Contains(result.Errors, e => e.Code == "ofx_investment_type_unsupported");

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "ofx")
            .OrderBy(h => h.PostedAt)
            .ToListAsync();
        Assert.Equal(4, headers.Count);

        // Per-row classification: action hint persisted on every
        // supported row.
        var actionsByFit = headers.ToDictionary(h => h.ExternalId!, h => h.IngestActionHint);
        Assert.Equal("buy",                actionsByFit["INV-FITID-BUY-1"]);
        Assert.Equal("sell",               actionsByFit["INV-FITID-SELL-1"]);
        Assert.Equal("dividend_cash",      actionsByFit["INV-FITID-DIV-1"]);
        Assert.Equal("dividend_reinvest",  actionsByFit["INV-FITID-REINV-1"]);

        // No provider_security_mapping was pre-populated, so the
        // resolved view's LEFT JOIN against provider_security_mappings
        // (ADR-0038, mig 115) returns null for every leg of every row.
        // The editor's Phase 3d upgrade flow lets the user create
        // the mapping at first-use.
        await using (var rv = _fixture.NewDbContext())
        {
            var resolvedIds = await rv.ResolvedTransactions.AsNoTracking()
                .Where(r => headers.Select(h => h.Id).Contains(r.HeaderId))
                .Select(r => r.IngestSecurityId)
                .ToListAsync();
            Assert.NotEmpty(resolvedIds);
            Assert.All(resolvedIds, id => Assert.Null(id));
        }

        // Mig 113: investment-row prefill carriers populated from
        // the OFX wire. The fixture's BUYSTOCK has UNITS=10,
        // UNITPRICE=50, COMMISSION=1 → ingest_fee = 1. SELLSTOCK
        // has UNITS=5, UNITPRICE=55, no fees → ingest_fee = null
        // (zero collapses to null per ExtractFee). INCOME has no
        // shares (cash-only) → all three null. REINVEST has
        // UNITS=0.2, UNITPRICE=60 → fees null.
        var buy = headers.Single(h => h.ExternalId == "INV-FITID-BUY-1");
        Assert.Equal(10m, buy.IngestShares);
        Assert.Equal(50m, buy.IngestUnitPrice);
        Assert.Equal(1m,  buy.IngestFee);

        var sell = headers.Single(h => h.ExternalId == "INV-FITID-SELL-1");
        Assert.Equal(5m,  sell.IngestShares);
        Assert.Equal(55m, sell.IngestUnitPrice);
        Assert.Null(sell.IngestFee);

        var dividend = headers.Single(h => h.ExternalId == "INV-FITID-DIV-1");
        Assert.Null(dividend.IngestShares);
        Assert.Null(dividend.IngestUnitPrice);
        Assert.Null(dividend.IngestFee);

        var reinvest = headers.Single(h => h.ExternalId == "INV-FITID-REINV-1");
        Assert.Equal(0.2m, reinvest.IngestShares);
        Assert.Equal(60m,  reinvest.IngestUnitPrice);
        Assert.Null(reinvest.IngestFee);

        // OFX REINVEST is a net-zero cash event on the brokerage:
        // the dividend income IS the buy funding; no cash actually
        // lands in or leaves the account. The bank-shape leg's
        // amount must be 0 (NOT the OFX wire's `Total`, which is the
        // dividend's dollar value with a misleading negative sign).
        // Otherwise the user's running cash balance walks down by
        // the dividend on every reinvest, which is wrong. The
        // editor recovers the buy's magnitude from `IngestShares *
        // IngestUnitPrice` when upgrading to the investment shape;
        // income + buy legs net to zero on the brokerage cash side
        // per ADR-0028 — matching this bank-shape contract.
        var reinvestLegs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == reinvest.Id)
            .ToListAsync();
        Assert.NotEmpty(reinvestLegs);
        Assert.All(reinvestLegs, l => Assert.Equal(0m, l.Amount));

        // Mig 114: the SECLIST-resolved ticker is persisted on every
        // security-bearing row. The fixture's SECLIST maps CUSIP
        // FAKE0001 → ticker FAKE, so every supported row carries
        // "FAKE" — feeds the SPA's Accept-time
        // provider_security_mapping record.
        Assert.All(headers, h => Assert.Equal("FAKE", h.IngestSecurityTickerHint));
    }

    [Fact]
    public async Task Upserting_a_provider_security_mapping_resolves_ingest_security_id_on_already_imported_rows()
    {
        // A user's "accept one BNDA row, every other BNDA row's
        // picker stays empty" scenario. Mig 114 persists the ticker
        // hint at ingest; ADR-0038 / mig 115 has resolved_transactions
        // LEFT JOIN provider_security_mappings on it — recording a
        // mapping makes the resolved id appear on every matching row
        // on the next read, with no backfill anywhere in the code.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Import 4 OFX investment rows; all reference the same
        // CUSIP, all map to ticker "FAKE" via SECLIST. No mapping
        // pre-populated, so the view returns null for ingest_security_id
        // on every leg of every row.
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(OfxInvestmentStatement,
                accountId: brokerage.Id,
                providerAccountId: "inv:brokerX:INV-0001"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        List<Guid> headerIds;
        await using (var db = _fixture.NewDbContext())
        {
            headerIds = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId
                            && h.ProviderKey == "ofx"
                            && h.IngestSecurityTickerHint == "FAKE")
                .Select(h => h.Id)
                .ToListAsync();
            Assert.Equal(4, headerIds.Count);

            var preResolution = await db.ResolvedTransactions.AsNoTracking()
                .Where(r => headerIds.Contains(r.HeaderId))
                .Select(r => r.IngestSecurityId)
                .ToListAsync();
            Assert.NotEmpty(preResolution);
            Assert.All(preResolution, id => Assert.Null(id));
        }

        // Simulate the user accepting the first row's editor flow —
        // they pick a security in the typeahead and the
        // InvestmentTransactionsEndpoint forwards the
        // ProviderSecurityHint to UpsertAsync.
        var securityId = await ledger.AddSecurityAsync("Fake Test Stock", ticker: "FAKE");
        await using (var ctx = _fixture.NewDbContext())
        {
            var repo = new Coffer.Api.Db.Repositories.ProviderSecurityMappingsRepository(ctx);
            await repo.UpsertAsync(
                ledger.LedgerId,
                providerKey: "ofx",
                providerSecurityId: "FAKE",
                securityId: securityId,
                createdByUserId: null);
        }

        // The mapping now exists; the view's JOIN resolves it on the
        // next read for every header carrying the matching ticker
        // hint — no backfill needed, including for the four rows the
        // user hasn't opened yet.
        await using (var db = _fixture.NewDbContext())
        {
            var postResolution = await db.ResolvedTransactions.AsNoTracking()
                .Where(r => headerIds.Contains(r.HeaderId))
                .Select(r => r.IngestSecurityId)
                .ToListAsync();
            Assert.NotEmpty(postResolution);
            Assert.All(postResolution, id => Assert.Equal(securityId, id));
        }
    }

    [Fact]
    public async Task Import_resolves_seclist_ticker_against_existing_provider_mapping()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        // Pre-populate a (provider_key='ofx', provider_security_id='FAKE') →
        // security_id mapping so the SECLIST resolver's ticker hint
        // lands on a real Coffer security at insert time.
        var securityId = await ledger.AddSecurityAsync("Fake Test Stock", ticker: "FAKE");
        await using (var db = _fixture.NewDbContext())
        {
            db.ProviderSecurityMappings.Add(new Coffer.Api.Db.Entities.ProviderSecurityMappingRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                ProviderKey = "ofx",
                ProviderSecurityId = "FAKE",
                SecurityId = securityId,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import",
            FileUpload(OfxInvestmentStatement, accountId: brokerage.Id, providerAccountId: "inv:brokerX:INV-0001"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var verify = _fixture.NewDbContext();
        var headerIds = await verify.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "ofx")
            .Select(h => h.Id)
            .ToListAsync();
        // With the mapping in place, the SECLIST resolver lifts
        // CUSIP `FAKE0001` to ticker `FAKE`; the resolved view's
        // JOIN against provider_security_mappings (ADR-0038)
        // returns `securityId` for every leg of every row.
        var resolvedIds = await verify.ResolvedTransactions.AsNoTracking()
            .Where(r => headerIds.Contains(r.HeaderId))
            .Select(r => r.IngestSecurityId)
            .ToListAsync();
        Assert.NotEmpty(resolvedIds);
        Assert.All(resolvedIds, id => Assert.Equal(securityId, id));
    }

    [Fact]
    public async Task Import_422s_when_account_does_not_belong_to_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var bobAccount = await bob.AddBankAccountAsync("bobs-checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        // alice's session uploading a file targeted at bob's account.
        var resp = await client.PostAsync(
            $"/api/ledgers/{alice.LedgerId}/ingest/ofx/import",
            FileUpload(Ofx1BankSingleAccount, accountId: bobAccount.Id, providerAccountId: "021000021:1234567890"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}

