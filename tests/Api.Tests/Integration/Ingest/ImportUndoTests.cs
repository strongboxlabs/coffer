using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// Mig 221 — a transaction records the import that created it, so an import can be
/// undone as a set.
/// </summary>
/// <remarks>
/// This is what file imports have INSTEAD of dedup, and the reason belongs where the
/// tests live: CSV rows carry no issuer-assigned id, so any per-row identity has to be
/// derived from content, and content cannot separate two genuinely identical
/// transactions. A content hash silently collapses them; folding in the row ordinal
/// silently duplicates the file the moment a download window shifts. An exact undo
/// needs no inference at all, which is why it exists instead.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ImportUndoTests
{
    private readonly PostgresFixture _fixture;

    public ImportUndoTests(PostgresFixture fixture) => _fixture = fixture;

    // Two buys, minimal but real-shaped. Synthesised — no real fund or account data.
    private const string TwoBuysQif = """
        !Type:Invst
        D01/05/2024
        NBuy
        YGROWTH FUND(AAAA)
        I100.00000
        Q5.000
        U500.00
        T500.00
        ^
        D01/06/2024
        NBuy
        YGROWTH FUND(AAAA)
        I110.00000
        Q2.000
        U220.00
        T220.00
        ^
        """;

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

    // A SECOND, different file. Needed because QIF is not the no-dedup case: its
    // provider derives a synthetic external_id from (target account + row fields), so
    // re-importing the identical file is idempotent by design (ADR-0042). The
    // no-dedup decision applies to the CSV provider, which has no issuer id to lean on
    // and no defensible way to infer one. Two DIFFERENT files is therefore how this
    // suite gets two imports that both actually write rows.
    private const string TwoMoreBuysQif = """
        !Type:Invst
        D02/05/2024
        NBuy
        YGROWTH FUND(AAAA)
        I120.00000
        Q3.000
        U360.00
        T360.00
        ^
        D02/06/2024
        NBuy
        YGROWTH FUND(AAAA)
        I130.00000
        Q4.000
        U520.00
        T520.00
        ^
        """;

    private static MultipartFormDataContent Upload(Guid accountId, string qif = TwoBuysQif)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(qif));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(body, "file", "export.qif");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent("qif"), "providerAccountId");
        return content;
    }

    private static async Task<FileIngestImportResponse> ImportAsync(
        HttpClient client, Guid ledgerId, Guid accountId, string qif = TwoBuysQif)
    {
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledgerId}/ingest/qif/import", Upload(accountId, qif));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>();
        Assert.NotNull(body);
        return body!;
    }

    private static string UndoUrl(Guid ledgerId, Guid operationId, bool dryRun = false) =>
        $"/api/ledgers/{ledgerId}/ledger-operations/{operationId}/undo-import"
        + (dryRun ? "?dryRun=true" : string.Empty);

    [Fact]
    public async Task An_import_stamps_every_row_it_creates_with_its_own_operation()
    {
        // The whole mechanism. Before 221 a row recorded WHICH PROVIDER wrote it and not
        // WHICH RUN, so a second upload of the same statement produced rows
        // indistinguishable from the first — same provider, same dates, same amounts —
        // and cleanup meant hunting duplicates by eye.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var import = await ImportAsync(client, ledger.LedgerId, brokerage.Id);

        await using var db = _fixture.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif")
            .ToListAsync();

        Assert.Equal(2, headers.Count);
        Assert.All(headers, h => Assert.Equal(import.SyncRunId, h.LedgerOperationId));
    }

    [Fact]
    public async Task Undoing_an_import_removes_exactly_its_own_rows()
    {
        // The point of the stamp, and the assertion that earns it: the FIRST import must
        // survive. A test that only counted the total after undo would pass on an
        // implementation that deleted everything the provider had ever written.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var first = await ImportAsync(client, ledger.LedgerId, brokerage.Id);
        var second = await ImportAsync(
            client, ledger.LedgerId, brokerage.Id, TwoMoreBuysQif);
        Assert.NotEqual(first.SyncRunId, second.SyncRunId);

        // Two different files, so four rows. Re-uploading the SAME qif would have given
        // two — QIF dedups on its own synthetic external_id — which is why this test
        // uses a second file rather than repeating the first.
        await using (var before = _fixture.NewDbContext())
        {
            Assert.Equal(4, await before.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif"));
        }

        var undo = await client.PostAsync(UndoUrl(ledger.LedgerId, second.SyncRunId), null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var result = await undo.Content.ReadFromJsonAsync<UndoImportResult>();
        Assert.Equal(2, result!.Found);

        // REMOVED, not hidden. Hiding was the first design and it was wrong: a hidden
        // row keeps its external_id, and the import dedup matches on external_id with no
        // is_hidden filter, so the same file could never be imported again — see
        // Undoing_an_import_lets_the_same_file_be_imported_again.
        Assert.Equal(2, result.Deleted);

        await using var db = _fixture.NewDbContext();
        var rows = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif")
            .ToListAsync();

        // The load-bearing half: the SECOND import's rows are gone and the FIRST's are
        // untouched and visible. A version that removed everything the provider ever
        // wrote would pass the count above and fail here.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, h => Assert.Equal(first.SyncRunId, h.LedgerOperationId));
        Assert.All(rows, h => Assert.False(h.IsHidden));
    }

    [Fact]
    public async Task Undoing_an_import_lets_the_same_file_be_imported_again()
    {
        // THE PROPERTY THAT MAKES UNDO MEAN ANYTHING. The reason to undo an import is
        // almost always to redo it — wrong account, wrong file, wrong day. If the undone
        // rows still block their own re-import, the user is left in a state the obvious
        // recovery cannot escape.
        //
        // Soft-hiding could not deliver this. The dedup lookup matches on external_id
        // with NO is_hidden filter, and the soft-hide path deliberately "leaves
        // is_hidden alone" so a re-sync cannot undo a manual hide. Together those mean a
        // re-import matches every hidden row, counts it alreadyKnown, inserts nothing,
        // and leaves the register empty while reporting success.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var first = await ImportAsync(client, ledger.LedgerId, brokerage.Id);
        Assert.Equal(2, first.TransactionsForReview);

        var undo = await client.PostAsync(UndoUrl(ledger.LedgerId, first.SyncRunId), null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);

        // Re-import the SAME file, which is what a person does next.
        var second = await ImportAsync(client, ledger.LedgerId, brokerage.Id);

        // Both halves matter. The count proves the rows were treated as new rather than
        // waved through as already-known...
        Assert.Equal(2, second.TransactionsForReview);
        Assert.Equal(0, second.AlreadyKnown);

        // ...and this proves the register actually shows them, which is the claim the
        // user cares about. A version that inserted rows while leaving the originals
        // hidden would pass the counts above and still look empty.
        await using var db = _fixture.NewDbContext();
        var visible = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId
                        && h.ProviderKey == "qif"
                        && !h.IsHidden)
            .ToListAsync();
        Assert.Equal(2, visible.Count);
        Assert.All(visible, h => Assert.Equal(second.SyncRunId, h.LedgerOperationId));
    }

    [Fact]
    public async Task A_dry_run_counts_without_deleting_anything()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var import = await ImportAsync(client, ledger.LedgerId, brokerage.Id);

        var resp = await client.PostAsync(
            UndoUrl(ledger.LedgerId, import.SyncRunId, dryRun: true), null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<UndoImportResult>();

        Assert.Equal(2, result!.Found);
        Assert.Equal(0, result.Deleted);

        // Both halves asserted: a dry run that reported the count and deleted anyway
        // would pass on the count alone.
        await using var db = _fixture.NewDbContext();
        Assert.Equal(2, await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif"));
    }

    [Fact]
    public async Task Undo_refuses_an_operation_that_never_imported_anything()
    {
        // A quote refresh created no transactions, so "undo" on one cannot mean
        // anything. Refused by name rather than returning zero rows, which would read
        // as a successful undo of nothing — the success-shaped non-event this repo keeps
        // finding in its own tooling.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var quoteRunId = Guid.NewGuid();
        await using (var seed = _fixture.NewDbContext())
        {
            seed.LedgerOperations.Add(new LedgerOperationRow
            {
                Id = quoteRunId,
                LedgerId = ledger.LedgerId,
                Family = "quote",
                ProviderKey = "yahoo",
                TriggeredVia = "scheduled",
                TriggeredByUserId = ledger.UserId,
                Status = "completed",
                StartedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var resp = await client.PostAsync(UndoUrl(ledger.LedgerId, quoteRunId), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Undo_of_an_unknown_operation_is_not_silently_a_success()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(UndoUrl(ledger.LedgerId, Guid.NewGuid()), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Undo_reports_how_many_rows_the_user_has_since_edited()
    {
        // An undo that silently discards someone's edits is the kind of helpful delete
        // nobody forgives. The count is reported, never used to refuse — whose edits
        // they are is the user's call.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var import = await ImportAsync(client, ledger.LedgerId, brokerage.Id);

        await using (var seed = _fixture.NewDbContext())
        {
            var one = await seed.TxnHeaders
                .Where(h => h.LedgerId == ledger.LedgerId && h.ProviderKey == "qif")
                .OrderBy(h => h.PostedAt)
                .FirstAsync();
            seed.TxnHeaderOverrides.Add(new TxnHeaderOverrideRow
            {
                HeaderId = one.Id,
                LedgerId = ledger.LedgerId,
                Payee = "renamed by hand",
            });
            await seed.SaveChangesAsync();
        }

        var resp = await client.PostAsync(
            UndoUrl(ledger.LedgerId, import.SyncRunId, dryRun: true), null);
        var result = await resp.Content.ReadFromJsonAsync<UndoImportResult>();

        Assert.Equal(2, result!.Found);
        Assert.Equal(1, result.Edited);
    }

    [Fact]
    public async Task An_import_into_another_ledger_cannot_be_undone_from_this_one()
    {
        // Defence in depth over RLS: the lookup is keyed on ledger_id too, so a guessed
        // operation id from another ledger finds nothing here.
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var theirs = await SyntheticLedger.CreateAsync(_fixture);
        var theirAccount = await theirs.AddInvestmentAccountAsync("brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var theirClient = await AuthedClientAsync(factory, theirs);
        var theirImport = await ImportAsync(theirClient, theirs.LedgerId, theirAccount.Id);

        using var myClient = await AuthedClientAsync(factory, mine);
        var resp = await myClient.PostAsync(
            UndoUrl(mine.LedgerId, theirImport.SyncRunId), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        // ...and their rows are untouched.
        await using var db = _fixture.NewDbContext();
        Assert.Equal(2, await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == theirs.LedgerId && h.ProviderKey == "qif"));
    }
}
