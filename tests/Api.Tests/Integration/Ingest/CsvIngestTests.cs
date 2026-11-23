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
/// End-to-end delimited import (ADR-0031 Phase 5, mig 222): author a mapping document,
/// then read a file with it.
/// </summary>
/// <remarks>
/// The fixture is SYNTHETIC and follows a card-issuer export shape:
/// UTF-8 with a BOM, TAB-delimited despite a .csv name, no header row, a currency symbol
/// in the amount, and amounts signed from the issuer's perspective so a purchase is
/// positive. Invented merchants and digits throughout.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CsvIngestTests : DatabaseTest
{
    public CsvIngestTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string CardIssuerYaml = """
        version: 1
        delimiter: tab
        header_rows: 0
        date:
          column: 1
          format: MM/dd/yyyy
        payee:
          column: 3
        amount:
          shape: signed
          column: 2
          invert: true
        """;

    /// <summary>Two purchases and a RETURN, which is negative in the file.</summary>
    private static string CardIssuerFile() =>
        "﻿"
        + Row("09/02/2026", "$12.34", "SAMPLE.COM              ANYTOWN      ST")
        + Row("09/03/2026", "$7.99", "SAMPLE STORE            ANYTOWN      ST")
        + Row("09/04/2026", "-$23.50", "SAMPLE STORE            ANYTOWN      ST");

    private static string Row(string date, string amount, string description) =>
        string.Join('\t', date, amount, description, "purchase") + "\n";

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static MultipartFormDataContent Upload(
        Guid? accountId = null, Guid? mappingId = null, string? mappingYaml = null,
        string? file = null)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(file ?? CardIssuerFile()));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(body, "file", "statement.csv");
        if (accountId is not null)
        {
            content.Add(new StringContent(accountId.Value.ToString()), "accountId");
            content.Add(new StringContent("csv"), "providerAccountId");
        }
        if (mappingId is not null)
            content.Add(new StringContent(mappingId.Value.ToString()), "mappingId");
        if (mappingYaml is not null)
            content.Add(new StringContent(mappingYaml), "mappingYaml");
        return content;
    }

    private static async Task<Guid> SaveMappingAsync(
        HttpClient client, Guid ledgerId, string name, string yaml)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/csv-mappings",
            new CsvMappingWriteRequest { Name = name, DefinitionYaml = yaml });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<CsvMappingDto>();
        return dto!.Id;
    }

    [Fact]
    public async Task A_saved_mapping_reads_a_real_shaped_file_with_the_signs_the_right_way_round()
    {
        // The whole slice in one test. The file signs from the ISSUER's perspective — a
        // purchase positive because it increases what you owe — and Coffer signs from the
        // account holder's, where a purchase is money out. invert: true is what reconciles
        // them, and it must hold for the RETURN too: negative in the file, money in here.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var card = await ledger.AddBankAccountAsync("Credit card");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var mappingId = await SaveMappingAsync(
            client, ledger.LedgerId, "Credit card statement", CardIssuerYaml);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            Upload(accountId: card.Id, mappingId: mappingId));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-generic")
            .OrderBy(h => h.PostedAt)
            .ToListAsync();
        Assert.Equal(3, headers.Count);

        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => headers.Select(h => h.Id).Contains(l.HeaderId) && l.AccountId == card.Id)
            .OrderBy(l => l.Amount)
            .Select(l => l.Amount)
            .ToListAsync();

        // Two charges out, one refund in.
        Assert.Equal(new[] { -12.34m, -7.99m, 23.50m }, legs.OrderBy(a => a).ToArray().Order().ToArray());
    }

    [Fact]
    public async Task An_imported_rows_memo_is_the_imported_accounts_own()
    {
        // A one-split's memo sits on the side that entered it, never the header; an
        // imported row is entered from the account imported into, which owns it.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var card = await ledger.AddBankAccountAsync("Credit card");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var mappingId = await SaveMappingAsync(
            client, ledger.LedgerId, "Card statement with memo", CardIssuerYaml + "\nmemo:\n  column: 4\n");

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            Upload(accountId: card.Id, mappingId: mappingId));
        Assert.True(resp.StatusCode == HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-generic")
            .ToListAsync();
        Assert.Equal(3, headers.Count);
        foreach (var header in headers)
        {
            Assert.Null(header.Memo);
            Assert.Equal(card.Id, header.OriginatingAccountId);
            var notes = await ledger.LoadNotesAsync(header.Id);
            Assert.Equal("purchase", notes.Legs.Single(l => l.Key.AccountId == card.Id).Value.Memo);
            Assert.All(notes.Legs.Where(l => l.Key.AccountId != card.Id), l => Assert.True(l.Value.IsEmpty));
        }
        await OriginatingAccountCheck.AssertHoldsAsync(Db, ledger.LedgerId);
    }

    [Fact]
    public async Task A_draft_mapping_can_preview_a_file_without_ever_being_saved()
    {
        // The iteration path, for the wizard and for an assistant composing YAML from a
        // few sample lines. Requiring a saved mapping first would mean storing half-built
        // rows just to find out the delimiter was wrong.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/preview",
            Upload(mappingYaml: CardIssuerYaml));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var preview = await resp.Content.ReadFromJsonAsync<FileIngestPreviewResponse>();
        var account = Assert.Single(preview!.Accounts);
        Assert.Equal(3, account.TransactionCount);

        // Nothing was stored — not the mapping, not the rows.
        await using var db = Db.NewDbContext();
        Assert.Empty(await db.FeedCsvMappings.AsNoTracking()
            .Where(m => m.LedgerId == ledger.LedgerId).ToListAsync());
        Assert.Empty(await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-generic")
            .ToListAsync());
    }

    [Fact]
    public async Task An_invalid_document_is_refused_with_every_problem_named()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/csv-mappings",
            new CsvMappingWriteRequest
            {
                Name = "Broken",
                // A typo the strict validator must catch: `ammount` would otherwise be
                // ignored and the amount left unmapped.
                DefinitionYaml = CardIssuerYaml.Replace("amount:", "ammount:", StringComparison.Ordinal),
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var verdict = await resp.Content.ReadFromJsonAsync<CsvMappingValidationResponse>();
        Assert.False(verdict!.Valid);
        Assert.Contains(verdict.Errors, e => e.Path == "ammount");

        // And nothing was stored, so an invalid draft leaves no debris.
        await using var db = Db.NewDbContext();
        Assert.Empty(await db.FeedCsvMappings.AsNoTracking()
            .Where(m => m.LedgerId == ledger.LedgerId).ToListAsync());
    }

    [Fact]
    public async Task Validating_never_saves_and_answers_200_even_when_the_answer_is_no()
    {
        // An invalid document is a valid question. Returning 422 here would make an
        // iterating client treat a normal step as a failure.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/csv-mappings/validate",
            new CsvMappingWriteRequest { Name = "n/a", DefinitionYaml = "delimiter: sideways" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var verdict = await resp.Content.ReadFromJsonAsync<CsvMappingValidationResponse>();
        Assert.False(verdict!.Valid);
        Assert.NotEmpty(verdict.Errors);
    }

    [Fact]
    public async Task An_upload_must_name_exactly_one_mapping()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Neither.
        var none = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/preview", Upload());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);

        // Both — refused rather than silently preferring one, which would mean reading
        // the file with a mapping the caller did not choose.
        var mappingId = await SaveMappingAsync(
            client, ledger.LedgerId, "Credit card statement", CardIssuerYaml);
        var both = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/preview",
            Upload(mappingId: mappingId, mappingYaml: CardIssuerYaml));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
    }

    [Fact]
    public async Task Two_mappings_on_one_ledger_cannot_share_a_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await SaveMappingAsync(client, ledger.LedgerId, "Credit card", CardIssuerYaml);

        // Case-insensitively: "Credit card" and "credit CARD" are the same choice in a list.
        var clash = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/csv-mappings",
            new CsvMappingWriteRequest { Name = "credit CARD", DefinitionYaml = CardIssuerYaml });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
    }

    [Fact]
    public async Task A_stored_document_comes_back_exactly_as_it_was_written()
    {
        // The point of storing YAML text rather than a parsed structure: comments and
        // formatting belong to whoever wrote it, and a document meant to be hand-edited
        // has to survive a round trip through the database unchanged.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var annotated = "# Credit card: tab-separated despite the .csv name\n" + CardIssuerYaml;
        var id = await SaveMappingAsync(client, ledger.LedgerId, "Credit card", annotated);

        var fetched = await client.GetFromJsonAsync<CsvMappingDto>(
            $"/api/ledgers/{ledger.LedgerId}/csv-mappings/{id}");

        Assert.Equal(annotated, fetched!.DefinitionYaml);
    }

    // ----- the decimal separator (ADR-0114 D15) -----------------------------------

    /// <summary>
    /// A version-2 layout for a file that writes decimals after a comma and groups
    /// thousands with a point, semicolon-delimited as such exports usually are.
    /// </summary>
    private const string CommaYaml = """
        version: 2
        delimiter: semicolon
        header_rows: 1
        date:
          column: 1
          format: dd.MM.yyyy
        payee:
          column: 2
        amount:
          shape: signed
          column: 3
          decimal_separator: comma
        """;

    /// <summary>
    /// A charge with a comma decimal, a deposit grouped with a point, and a second charge
    /// whose trailing zero is part of the figure. Synthetic.
    /// </summary>
    private const string CommaFile =
        "Date;Payee;Amount\n"
        + "02.09.2026;SAMPLE STORE;-12,34\n"
        + "03.09.2026;SAMPLE.COM;1.234,50\n"
        + "04.09.2026;SAMPLE STORE;-7,50\n";

    [Fact]
    public async Task A_comma_layout_is_stored_as_version_2_and_imports_each_amount_with_its_decimals()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Checking");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var mappingId = await SaveMappingAsync(client, ledger.LedgerId, "Comma statement", CommaYaml);
        var stored = await client.GetFromJsonAsync<CsvMappingDto>(
            $"/api/ledgers/{ledger.LedgerId}/csv-mappings/{mappingId}");
        Assert.Equal(2, stored!.SchemaVersion);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            Upload(accountId: checking.Id, mappingId: mappingId, file: CommaFile));
        Assert.True(resp.StatusCode == HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var headerIds = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "csv-generic")
            .Select(h => h.Id)
            .ToListAsync();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => headerIds.Contains(l.HeaderId) && l.AccountId == checking.Id)
            .Select(l => l.Amount)
            .ToListAsync();

        // Read as grouping, each of these would have landed a far larger figure.
        Assert.Equal(new[] { -12.34m, -7.50m, 1234.50m }, legs.Order().ToArray());
    }

    [Fact]
    public async Task A_version_1_layout_is_stored_as_version_1_and_reads_back_with_a_point()
    {
        // Stored layouts are re-validated on every use, so version 1 stays accepted, keeps
        // its stored version, and the guided form reopens it with a point.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var id = await SaveMappingAsync(client, ledger.LedgerId, "Card statement", CardIssuerYaml);
        var stored = await client.GetFromJsonAsync<CsvMappingDto>(
            $"/api/ledgers/{ledger.LedgerId}/csv-mappings/{id}");
        Assert.Equal(1, stored!.SchemaVersion);

        foreach (var (yaml, version, separator) in new[]
                 {
                     (CardIssuerYaml, 1, "point"),
                     (CommaYaml, 2, "comma"),
                 })
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/csv-mappings/validate",
                new CsvMappingWriteRequest { Name = "n/a", DefinitionYaml = yaml });
            var verdict = await resp.Content.ReadFromJsonAsync<CsvMappingValidationResponse>();
            Assert.True(verdict!.Valid);
            Assert.Equal(version, verdict.Mapping!.Version);
            Assert.Equal(separator, verdict.Mapping.DecimalSeparator);
        }
    }

    [Fact]
    public async Task A_preview_names_a_cell_that_does_not_read_and_counts_only_the_rows_that_do()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A point followed by two digits, in a layout that names a comma.
        var file = CommaFile + "05.09.2026;SAMPLE.COM;12.34\n";

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/csv/preview",
            Upload(mappingYaml: CommaYaml, file: file));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var preview = await resp.Content.ReadFromJsonAsync<FileIngestPreviewResponse>();
        Assert.Equal(3, Assert.Single(preview!.Accounts).TransactionCount);
        var warning = Assert.Single(preview.Errors);
        Assert.Equal("csv_row_unreadable", warning.Code);
        Assert.Equal(
            "Line 5: '12.34' is not an amount with a comma as the decimal separator. The row was skipped.",
            warning.Message);
    }

    [Fact]
    public async Task Another_ledgers_mapping_cannot_be_used_or_read_from_here()
    {
        var mine = await SyntheticLedger.CreateAsync(Db);
        var theirs = await SyntheticLedger.CreateAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var theirClient = await AuthedClientAsync(factory, theirs);
        var theirMapping = await SaveMappingAsync(
            theirClient, theirs.LedgerId, "Credit card", CardIssuerYaml);

        using var myClient = await AuthedClientAsync(factory, mine);
        var read = await myClient.GetAsync(
            $"/api/ledgers/{mine.LedgerId}/csv-mappings/{theirMapping}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, read.StatusCode);

        var used = await myClient.PostAsync(
            $"/api/ledgers/{mine.LedgerId}/ingest/csv/preview",
            Upload(mappingId: theirMapping));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, used.StatusCode);
    }
}
