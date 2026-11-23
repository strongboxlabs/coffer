using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// The sidebar's folders (mig 258, FU-006) end to end:
/// <c>/api/ledgers/{ledgerId}/account-folders</c>, filing an account with
/// <c>PUT …/accounts/{accountId}/folder</c>, and the sidebar balances read
/// <c>GET …/accounts/balances</c> (FU-052). Pins nesting and its refusals (a cycle,
/// another type's folder), a delete that moves its contents up one level and deletes
/// nothing else, the role gate (a viewer reads, an editor writes), the database
/// backstops under the API's checks, and that the balances are the dashboard's.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountFoldersEndpointsTests : DatabaseTest
{
    public AccountFoldersEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // ---- helpers -----------------------------------------------------------------

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>A signed-in client for a second member, as OwnerOnlyAccessTests makes one.</summary>
    private async Task<HttpClient> MemberClientAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
        await using (var db = Db.NewDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    private static string Folders(Guid ledgerId) => $"/api/ledgers/{ledgerId}/account-folders";

    private static Task<HttpResponseMessage> PostFolderAsync(
        HttpClient client, Guid ledgerId, string accountType, string name, Guid? parentId = null) =>
        client.PostAsJsonAsync(Folders(ledgerId),
            new CreateAccountFolderRequest { AccountType = accountType, Name = name, ParentId = parentId });

    private static async Task<Guid> CreateFolderAsync(
        HttpClient client, Guid ledgerId, string accountType, string name, Guid? parentId = null)
    {
        var response = await PostFolderAsync(client, ledgerId, accountType, name, parentId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var folder = (await response.Content.ReadFromJsonAsync<AccountFolderDto>())!;
        return folder.Id;
    }

    private static async Task<AccountFolderDto[]> ListAsync(HttpClient client, Guid ledgerId)
    {
        var response = await client.GetAsync(Folders(ledgerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountFolderDto[]>())!;
    }

    private static Task<HttpResponseMessage> MoveFolderAsync(
        HttpClient client, Guid ledgerId, Guid folderId, Guid? parentId) =>
        client.PutAsJsonAsync($"{Folders(ledgerId)}/{folderId}/parent",
            new MoveAccountFolderRequest { ParentId = parentId });

    private static Task<HttpResponseMessage> FileAccountAsync(
        HttpClient client, Guid ledgerId, Guid accountId, Guid? folderId) =>
        client.PutAsJsonAsync($"/api/ledgers/{ledgerId}/accounts/{accountId}/folder",
            new SetAccountFolderRequest { FolderId = folderId });

    private static Task<HttpResponseMessage> RenameAsync(
        HttpClient client, Guid ledgerId, Guid folderId, string name) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{Folders(ledgerId)}/{folderId}")
        {
            Content = JsonContent.Create(new RenameAccountFolderRequest { Name = name }),
        });

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }

    // ---- CRUD --------------------------------------------------------------------

    [Fact]
    public async Task Create_list_rename_and_delete_a_folder()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Empty(await ListAsync(client, ledger.LedgerId));

        var created = await PostFolderAsync(client, ledger.LedgerId, "bank", "  Folder B  ");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<AccountFolderDto>())!;
        Assert.Equal("Folder B", dto.Name);   // trimmed
        Assert.Equal("bank", dto.AccountType);
        Assert.Null(dto.ParentId);
        Assert.Empty(dto.AccountIds);
        Assert.Equal($"{Folders(ledger.LedgerId)}/{dto.Id}", created.Headers.Location?.ToString());

        var other = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");

        // Flat and ordered by name.
        var listed = await ListAsync(client, ledger.LedgerId);
        Assert.Equal(["Folder A", "Folder B"], listed.Select(f => f.Name));

        var renamed = await RenameAsync(client, ledger.LedgerId, dto.Id, "Folder C");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Folder C", (await renamed.Content.ReadFromJsonAsync<AccountFolderDto>())!.Name);

        var deleted = await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{dto.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal([other], (await ListAsync(client, ledger.LedgerId)).Select(f => f.Id));

        await AssertProblemAsync(
            await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{dto.Id}"), "account-folder-not-found");
        await AssertProblemAsync(
            await RenameAsync(client, ledger.LedgerId, dto.Id, "gone"), "account-folder-not-found");
    }

    [Fact]
    public async Task Names_are_required_bounded_and_unique_among_siblings_ignoring_case()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AssertProblemAsync(
            await PostFolderAsync(client, ledger.LedgerId, "bank", "   "), "account-folder-name-required");
        await AssertProblemAsync(
            await PostFolderAsync(client, ledger.LedgerId, "bank", new string('x', 81)), "account-folder-name-too-long");
        Assert.Equal(HttpStatusCode.Created,
            (await PostFolderAsync(client, ledger.LedgerId, "bank", new string('x', 80))).StatusCode);
        await AssertProblemAsync(
            await PostFolderAsync(client, ledger.LedgerId, "category", "Folder A"), "account-folder-type-invalid");

        var top = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        // Two top-level folders of one type collide (the index's NULLS NOT DISTINCT)...
        await AssertProblemAsync(
            await PostFolderAsync(client, ledger.LedgerId, "bank", "FOLDER a"), "account-folder-name-taken");
        // ...but the same name is free under another parent, or in another type's section.
        Assert.Equal(HttpStatusCode.Created,
            (await PostFolderAsync(client, ledger.LedgerId, "bank", "Folder A", top)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await PostFolderAsync(client, ledger.LedgerId, "loan", "Folder A")).StatusCode);

        var second = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B");
        await AssertProblemAsync(
            await RenameAsync(client, ledger.LedgerId, second, "folder a"), "account-folder-name-taken");
        // Renaming to its own name in another case is not a clash with itself.
        Assert.Equal(HttpStatusCode.OK, (await RenameAsync(client, ledger.LedgerId, second, "FOLDER B")).StatusCode);
    }

    // ---- nesting -----------------------------------------------------------------

    [Fact]
    public async Task Folders_nest_move_and_refuse_a_cycle_or_another_types_parent()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var a = await CreateFolderAsync(client, ledger.LedgerId, "investment", "Folder A");
        var b = await CreateFolderAsync(client, ledger.LedgerId, "investment", "Folder B", a);
        var c = await CreateFolderAsync(client, ledger.LedgerId, "investment", "Folder C", b);
        var other = await CreateFolderAsync(client, ledger.LedgerId, "investment", "Folder D");
        var card = await CreateFolderAsync(client, ledger.LedgerId, "credit_card", "Folder E");

        var listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Equal(b, listed[c].ParentId);
        Assert.Equal(a, listed[b].ParentId);

        // A sub-folder must have its parent's type, at create and at move.
        await AssertProblemAsync(
            await PostFolderAsync(client, ledger.LedgerId, "bank", "Folder F", a), "account-folder-type-mismatch");
        await AssertProblemAsync(
            await MoveFolderAsync(client, ledger.LedgerId, b, card), "account-folder-type-mismatch");
        await AssertProblemAsync(
            await PostFolderAsync(client, ledger.LedgerId, "investment", "Folder F", Guid.NewGuid()),
            "account-folder-not-found");

        // Not into itself, nor into a child or a grandchild.
        await AssertProblemAsync(await MoveFolderAsync(client, ledger.LedgerId, a, a), "account-folder-cycle");
        await AssertProblemAsync(await MoveFolderAsync(client, ledger.LedgerId, a, b), "account-folder-cycle");
        await AssertProblemAsync(await MoveFolderAsync(client, ledger.LedgerId, a, c), "account-folder-cycle");

        // A real move, a subtree going with its root, then back to the top level.
        Assert.Equal(HttpStatusCode.NoContent, (await MoveFolderAsync(client, ledger.LedgerId, b, other)).StatusCode);
        listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Equal(other, listed[b].ParentId);
        Assert.Equal(b, listed[c].ParentId);

        Assert.Equal(HttpStatusCode.NoContent, (await MoveFolderAsync(client, ledger.LedgerId, b, null)).StatusCode);
        Assert.Null((await ListAsync(client, ledger.LedgerId)).Single(f => f.Id == b).ParentId);

        // Moving beside a sibling of the same name is a name clash, not a silent merge.
        await CreateFolderAsync(client, ledger.LedgerId, "investment", "Folder C");
        await AssertProblemAsync(await MoveFolderAsync(client, ledger.LedgerId, c, null), "account-folder-name-taken");
    }

    [Fact]
    public async Task Deleting_a_folder_moves_its_folders_and_accounts_up_one_level_and_deletes_nothing_else()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var inA = await ledger.AddBankAccountAsync("Account A");
        var inB = await ledger.AddBankAccountAsync("Account B");
        var inC = await ledger.AddBankAccountAsync("Account C");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A ⊃ B ⊃ { C, a sub-folder named B as well, Account B }; Account A in A; Account C in C.
        var a = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var b = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", a);
        var c = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C", b);
        var sameName = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", b);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, inA.Id, a)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, inB.Id, b)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, inC.Id, c)).StatusCode);

        // B's contents go to A, including the sub-folder that shares B's own name.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{b}")).StatusCode);
        var listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Equal(new[] { a, c, sameName }.Order(), listed.Keys.Order());
        Assert.Equal(a, listed[c].ParentId);
        Assert.Equal(a, listed[sameName].ParentId);
        Assert.Equal("Folder B", listed[sameName].Name);
        Assert.Equal(new[] { inA.Id, inB.Id }.Order(), listed[a].AccountIds.Order());
        Assert.Equal([inC.Id], listed[c].AccountIds);

        // A top-level folder's contents go to the top level: no parent, no folder.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{a}")).StatusCode);
        listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Equal(new[] { c, sameName }.Order(), listed.Keys.Order());
        Assert.All(listed.Values, f => Assert.Null(f.ParentId));
        Assert.Equal([inC.Id], listed[c].AccountIds);

        await using var db = Db.NewDbContext();
        Assert.False(await db.AccountFolderMembers.AnyAsync(m => m.AccountId == inA.Id || m.AccountId == inB.Id));
        Assert.Equal(3, await db.Accounts.CountAsync(x => x.Id == inA.Id || x.Id == inB.Id || x.Id == inC.Id));
    }

    [Fact]
    public async Task A_delete_whose_sub_folder_would_clash_on_moving_up_is_refused_and_changes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var b = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B");
        await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C");
        var nestedC = await CreateFolderAsync(client, ledger.LedgerId, "bank", "folder c", b);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, b)).StatusCode);

        await AssertProblemAsync(
            await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{b}"), "account-folder-name-taken");

        var listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Equal(3, listed.Count);
        Assert.Equal(b, listed[nestedC].ParentId);
        Assert.Equal([account.Id], listed[b].AccountIds);
    }

    // ---- concurrent writes -------------------------------------------------------

    /// <summary>How long a request is given to reach a row lock another transaction
    /// holds. A request that is not waiting on the lock finishes well inside it.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_delete_that_waits_on_a_concurrent_move_moves_the_contents_to_the_new_parent()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var oldParent = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var newParent = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B");
        var doomed = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C", oldParent);
        var child = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder D", doomed);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, doomed)).StatusCode);

        // Another editor's move of the folder, still open when the delete arrives.
        await using var mover = Db.NewDbContext();
        await using var moving = await mover.Database.BeginTransactionAsync();
        Assert.Equal(1, await mover.AccountFolders.Where(f => f.Id == doomed)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, newParent)));

        var deleting = client.DeleteAsync($"{Folders(ledger.LedgerId)}/{doomed}");
        await Task.Delay(LockWait);
        Assert.False(deleting.IsCompleted, "the delete should wait for the move's lock on the folder");
        await moving.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await deleting).StatusCode);
        // The contents go where the folder is now, not where it was when the delete began.
        var listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Equal(new[] { oldParent, newParent, child }.Order(), listed.Keys.Order());
        Assert.Equal(newParent, listed[child].ParentId);
        Assert.Equal([account.Id], listed[newParent].AccountIds);
        Assert.Empty(listed[oldParent].AccountIds);
    }

    [Fact]
    public async Task Filing_into_a_folder_that_is_being_deleted_waits_and_is_then_refused_as_not_found()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var doomed = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");

        // A delete in progress: its first statement locks the folder's row, its last
        // deletes it.
        await using var deleter = Db.NewDbContext();
        await using var deleting = await deleter.Database.BeginTransactionAsync();
        Assert.Equal(1, await deleter.AccountFolders.Where(f => f.Id == doomed)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, f => f.Name)));

        var filing = FileAccountAsync(client, ledger.LedgerId, account.Id, doomed);
        await Task.Delay(LockWait);
        Assert.False(filing.IsCompleted, "the filing should wait for the delete's lock on the folder");
        Assert.Equal(1, await deleter.AccountFolders.Where(f => f.Id == doomed).ExecuteDeleteAsync());
        await deleting.CommitAsync();

        // Not a 500 from a broken reference, and not filed into a folder that is gone.
        await AssertProblemAsync(await filing, "account-folder-not-found");
        await using var db = Db.NewDbContext();
        Assert.False(await db.AccountFolderMembers.AnyAsync(m => m.AccountId == account.Id));
    }

    /// <summary>The backend a lock held by <paramref name="holderPid"/> is keeping
    /// waiting, or null when none is.</summary>
    private async Task<int?> BlockedByAsync(int holderPid)
    {
        await using var connection = Db.OpenServiceConnection();
        await using var cmd = new NpgsqlCommand(
            "SELECT pid FROM pg_stat_activity WHERE @holder = ANY (pg_blocking_pids(pid))", connection);
        cmd.Parameters.AddWithValue("holder", holderPid);
        return await cmd.ExecuteScalarAsync() is int pid ? pid : null;
    }

    /// <summary>Poll until <paramref name="done"/> holds: a wait on Postgres's own
    /// record of who blocks whom, not on a guessed delay.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(20);
        }
    }

    private static int BackendPid(AppDbContext db) => ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

    [Fact]
    public async Task Two_concurrent_moves_that_only_together_make_a_cycle_end_with_one_refused_and_no_cycle()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A ⊃ D and C ⊃ B. Moving A into B, and C into D, is each fine on its own; both
        // make A ⊂ B ⊂ C ⊂ D ⊂ A. The two moves name no folder in common.
        var a = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var d = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder D", a);
        var c = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C");
        var b = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", c);

        // The barrier. An open transaction holding a folder of A's name inside B makes the
        // first move wait at its write, after its cycle check has passed, until the
        // holder rolls back; the sibling-name index waits for it.
        await using var holder = Db.NewDbContext();
        await using var holding = await holder.Database.BeginTransactionAsync();
        holder.AccountFolders.Add(new AccountFolderRow
        {
            Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, AccountType = "bank", Name = "Folder A", ParentId = b,
        });
        await holder.SaveChangesAsync();
        var holderPid = BackendPid(holder);

        var first = MoveFolderAsync(client, ledger.LedgerId, a, b);
        int? firstPid = null;
        await WaitUntilAsync(async () => (firstPid = await BlockedByAsync(holderPid)) is not null,
            "the first move waits at its write");

        // The second move starts while the first is past its check. Without a lock the
        // two share, it passes its own check against the tree as it was and finishes.
        var second = MoveFolderAsync(client, ledger.LedgerId, c, d);
        await WaitUntilAsync(async () => second.IsCompleted || await BlockedByAsync(firstPid!.Value) is not null,
            "the second move finishes or waits for the first");

        await holding.RollbackAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await first).StatusCode);
        await AssertProblemAsync(await second, "account-folder-cycle");

        // And the table holds no cycle: every folder's walk up ends at the top level.
        await using var db = Db.NewDbContext();
        var parents = await db.AccountFolders.AsNoTracking()
            .Where(f => f.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(f => f.Id, f => f.ParentId);
        foreach (var id in parents.Keys)
        {
            Guid? cursor = id;
            for (var steps = 0; cursor is { } at; steps++)
            {
                Assert.True(steps <= parents.Count, "a folder is its own ancestor");
                cursor = parents[at];
            }
        }
        Assert.Equal(b, parents[a]);
        Assert.Null(parents[c]);
    }

    [Fact]
    public async Task A_delete_waits_for_a_move_in_progress_anywhere_in_its_section()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var parent = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var doomed = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", parent);
        var child = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C", doomed);
        var bystander = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder D");
        var otherType = await CreateFolderAsync(client, ledger.LedgerId, "loan", "Folder E");

        // A move in progress holds every folder of its section; holding one the delete
        // does not otherwise touch is enough to show the delete waits for all of them.
        await using var mover = Db.NewDbContext();
        await using var moving = await mover.Database.BeginTransactionAsync();
        Assert.Equal(1, await mover.AccountFolders.Where(f => f.Id == bystander)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, f => f.Name)));
        var moverPid = BackendPid(mover);

        // Another section is not held up.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{otherType}")).StatusCode);

        var deleting = client.DeleteAsync($"{Folders(ledger.LedgerId)}/{doomed}");
        await WaitUntilAsync(async () => deleting.IsCompleted || await BlockedByAsync(moverPid) is not null,
            "the delete finishes or waits for the move");
        Assert.False(deleting.IsCompleted, "the delete re-parents a sub-folder, so it waits for the section's moves");
        await moving.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await deleting).StatusCode);
        Assert.Equal(parent, (await ListAsync(client, ledger.LedgerId)).Single(f => f.Id == child).ParentId);
    }

    // ---- filing accounts ---------------------------------------------------------

    [Fact]
    public async Task An_account_is_filed_moved_and_cleared()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var first = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var second = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", first);

        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, first)).StatusCode);
        Assert.Equal([account.Id], (await ListAsync(client, ledger.LedgerId)).Single(f => f.Id == first).AccountIds);

        // One folder per account: filing it again moves it.
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, second)).StatusCode);
        var listed = (await ListAsync(client, ledger.LedgerId)).ToDictionary(f => f.Id);
        Assert.Empty(listed[first].AccountIds);
        Assert.Equal([account.Id], listed[second].AccountIds);

        // An inactive account keeps its place and can still be moved.
        await ledger.SetIsActiveAsync(account.Id, false);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, first)).StatusCode);
        Assert.Equal([account.Id], (await ListAsync(client, ledger.LedgerId)).Single(f => f.Id == first).AccountIds);

        // Null takes it out of every folder, and doing so twice changes nothing more.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, null)).StatusCode);
            Assert.All(await ListAsync(client, ledger.LedgerId), f => Assert.Empty(f.AccountIds));
        }
    }

    [Fact]
    public async Task Filing_refuses_another_types_folder_a_category_a_system_account_and_unknown_ids()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var other = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Account B");
        var otherAccount = await other.AddBankAccountAsync("Account C");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var cardFolder = await CreateFolderAsync(client, ledger.LedgerId, "credit_card", "Folder A");
        var investmentFolder = await CreateFolderAsync(client, ledger.LedgerId, "investment", "Folder B");
        var bankFolder = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C");

        await AssertProblemAsync(
            await FileAccountAsync(client, ledger.LedgerId, bank.Id, cardFolder), "account-folder-type-mismatch");
        await AssertProblemAsync(
            await FileAccountAsync(client, ledger.LedgerId, category.Id, bankFolder), "account-is-category");
        await AssertProblemAsync(
            await FileAccountAsync(client, ledger.LedgerId, brokerage.HoldingsAccountId!.Value, investmentFolder),
            "account-is-system");
        await AssertProblemAsync(
            await FileAccountAsync(client, ledger.LedgerId, Guid.NewGuid(), bankFolder), "account-not-in-ledger");
        await AssertProblemAsync(
            await FileAccountAsync(client, ledger.LedgerId, otherAccount.Id, bankFolder), "account-not-in-ledger");
        await AssertProblemAsync(
            await FileAccountAsync(client, ledger.LedgerId, bank.Id, Guid.NewGuid()), "account-folder-not-found");

        // The brokerage itself goes in its type's folder.
        Assert.Equal(HttpStatusCode.NoContent,
            (await FileAccountAsync(client, ledger.LedgerId, brokerage.Id, investmentFolder)).StatusCode);
        Assert.All((await ListAsync(client, ledger.LedgerId)).Where(f => f.Id != investmentFolder),
            f => Assert.Empty(f.AccountIds));
    }

    // ---- roles -------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_reads_folders_and_is_refused_every_write_while_an_editor_writes()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        var viewerId = await ledger.AddMemberAsync("viewer");
        var editorId = await ledger.AddMemberAsync("editor");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var owner = await AuthedClientAsync(factory, ledger);
        using var viewer = await MemberClientAsync(factory, viewerId);
        using var editor = await MemberClientAsync(factory, editorId);

        var folder = await CreateFolderAsync(owner, ledger.LedgerId, "bank", "Folder A");

        Assert.Equal([folder], (await ListAsync(viewer, ledger.LedgerId)).Select(f => f.Id));
        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts/balances")).StatusCode);

        const string NotWritable = "ledger-not-writable";
        await AssertProblemAsync(await PostFolderAsync(viewer, ledger.LedgerId, "bank", "Folder B"), NotWritable);
        await AssertProblemAsync(await RenameAsync(viewer, ledger.LedgerId, folder, "Folder B"), NotWritable);
        await AssertProblemAsync(await MoveFolderAsync(viewer, ledger.LedgerId, folder, null), NotWritable);
        await AssertProblemAsync(await FileAccountAsync(viewer, ledger.LedgerId, account.Id, folder), NotWritable);
        await AssertProblemAsync(await viewer.DeleteAsync($"{Folders(ledger.LedgerId)}/{folder}"), NotWritable);
        var unchanged = Assert.Single(await ListAsync(owner, ledger.LedgerId));
        Assert.Equal("Folder A", unchanged.Name);
        Assert.Empty(unchanged.AccountIds);

        var nested = await CreateFolderAsync(editor, ledger.LedgerId, "bank", "Folder B", folder);
        Assert.Equal(HttpStatusCode.OK, (await RenameAsync(editor, ledger.LedgerId, nested, "Folder C")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await MoveFolderAsync(editor, ledger.LedgerId, nested, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(editor, ledger.LedgerId, account.Id, nested)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"{Folders(ledger.LedgerId)}/{folder}")).StatusCode);
        Assert.Equal([account.Id], Assert.Single(await ListAsync(owner, ledger.LedgerId)).AccountIds);
    }

    [Fact]
    public async Task A_ledger_without_a_grant_is_not_visible()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        await AssertProblemAsync(await bobClient.GetAsync(Folders(alice.LedgerId)), "ledger-not-visible");
        await AssertProblemAsync(
            await bobClient.GetAsync($"/api/ledgers/{alice.LedgerId}/accounts/balances"), "ledger-not-visible");
    }

    // ---- the database underneath -------------------------------------------------

    [Fact]
    public async Task The_database_refuses_a_type_mismatch_the_api_did_not_check()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), card = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            db.AccountFolders.AddRange(
                new AccountFolderRow { Id = a, LedgerId = ledger.LedgerId, AccountType = "bank", Name = "Folder A" },
                new AccountFolderRow { Id = b, LedgerId = ledger.LedgerId, AccountType = "bank", Name = "Folder B", ParentId = a },
                new AccountFolderRow { Id = c, LedgerId = ledger.LedgerId, AccountType = "bank", Name = "Folder C", ParentId = b },
                new AccountFolderRow { Id = card, LedgerId = ledger.LedgerId, AccountType = "credit_card", Name = "Folder D" });
            await db.SaveChangesAsync();
        }

        static PostgresException Pg(Exception ex) =>
            ex as PostgresException ?? ex.InnerException as PostgresException
            ?? throw new Xunit.Sdk.XunitException($"not a Postgres refusal: {ex}");

        // The composite references: a sub-folder of another type, and a member whose
        // account is not of its folder's type. The parent reference is deferred, so it
        // fires at commit.
        await using (var db = Db.NewDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.AccountFolders.Where(f => f.Id == c).ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, card));
            var ex = Pg(await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync()));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
            Assert.Equal("fk_account_folders_parent", ex.ConstraintName);
        }
        await using (var db = Db.NewDbContext())
        {
            db.AccountFolderMembers.Add(new AccountFolderMemberRow
            {
                AccountId = account.Id, LedgerId = ledger.LedgerId, FolderId = card, AccountType = "credit_card",
            });
            var ex = Pg(await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync()));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
            Assert.Equal("fk_account_folder_members_account", ex.ConstraintName);
        }

        // The positive control: the same writes, well-formed, land.
        await using (var db = Db.NewDbContext())
        {
            Assert.Equal(1, await db.AccountFolders.Where(f => f.Id == c).ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, a)));
            db.AccountFolderMembers.Add(new AccountFolderMemberRow
            {
                AccountId = account.Id, LedgerId = ledger.LedgerId, FolderId = c, AccountType = "bank",
            });
            Assert.Equal(1, await db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task No_trigger_guards_folders_after_migration_261_and_only_the_txn_headers_guards_remain()
    {
        // ADR-0032: the cycle rule is the API's (MoveAsync under the section lock), so
        // migration 261 dropped mig 258's trigger and its function. The list of what is
        // left is FU-099's: a new trigger has to be justified there, not slipped in.
        await using var connection = Db.OpenServiceConnection();

        async Task<List<string>> NamesAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            return names;
        }

        Assert.Empty(await NamesAsync("""
            SELECT p.proname FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace
               AND p.proname = 'fn_account_folders_refuse_cycle'
            """));
        Assert.Equal(
            ["txn_headers.trg_reject_txn_headers_created_at_update", "txn_headers.trg_reject_txn_headers_seq_update"],
            await NamesAsync("""
                SELECT c.relname || '.' || t.tgname
                  FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                 WHERE NOT t.tgisinternal AND c.relnamespace = 'public'::regnamespace
                 ORDER BY 1
                """));
    }

    [Fact]
    public async Task A_snapshot_restore_brings_back_a_nested_folder_tree_and_its_accounts()
    {
        // Restore replays the folders as stored, with no check of its own: the tree it
        // replays was acyclic when it was captured, and the parent key is deferred, so the
        // order the rows land in does not matter.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var a = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var b = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", a);
        var c = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder C", b);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, account.Id, c)).StatusCode);
        var before = (await ListAsync(client, ledger.LedgerId)).OrderBy(f => f.Id).ToArray();

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("folders"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var snapshotId = (await created.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!.Id;

        // Reshape the tree after the snapshot: a move, a delete, a new folder.
        Assert.Equal(HttpStatusCode.NoContent, (await MoveFolderAsync(client, ledger.LedgerId, c, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Folders(ledger.LedgerId)}/{a}")).StatusCode);
        await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder D", c);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snapshotId}/restore", content: null)).StatusCode);

        var after = (await ListAsync(client, ledger.LedgerId)).OrderBy(f => f.Id).ToArray();
        Assert.Equal(before.Select(f => (f.Id, f.ParentId, f.Name)), after.Select(f => (f.Id, f.ParentId, f.Name)));
        Assert.Equal([account.Id], after.Single(f => f.Id == c).AccountIds);
    }

    // ---- balances and MCP --------------------------------------------------------

    [Fact]
    public async Task Sidebar_balances_equal_the_dashboards_for_every_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddAccountAsync("Account A", "bank", openingBalance: 1_000m);
        var savings = await ledger.AddAccountAsync("Account B", "bank", openingBalance: 250m);
        var card = await ledger.AddAccountAsync("Card X", "credit_card", openingBalance: -200m);
        var loan = await ledger.AddAccountAsync("Loan Y", "loan", openingBalance: -5_000m);
        var asset = await ledger.AddAccountAsync("Asset Z", "asset", openingBalance: 3_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", openingBalance: 100m);
        var security = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, security, 10m, 500m);
        await ledger.AddSecurityPriceAsync(security, 60m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(
            checking.Id, savings.Id, 300m, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));
        // Inactive accounts are valued too (ADR-0085): the sidebar's "show inactive"
        // reads the same response.
        await ledger.SetIsActiveAsync(asset.Id, false);
        var category = await ledger.AddCategoryAsync("Category A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var balancesResponse = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts/balances");
        Assert.Equal(HttpStatusCode.OK, balancesResponse.StatusCode);
        var balances = (await balancesResponse.Content.ReadFromJsonAsync<AccountBalanceDto[]>())!
            .ToDictionary(b => b.AccountId);
        var overview = (await client.GetFromJsonAsync<LedgerOverviewDto>($"/api/ledgers/{ledger.LedgerId}/overview"))!;
        var dashboard = overview.AccountGroups.SelectMany(g => g.Accounts).ToDictionary(a => a.Id);

        // The same accounts, each with the same figure and currency.
        Assert.Equal(dashboard.Keys.Order(), balances.Keys.Order());
        foreach (var (id, row) in dashboard)
        {
            Assert.Equal(row.Balance, balances[id].Balance);
            Assert.Equal(row.CurrencyCode, balances[id].CurrencyCode);
        }

        // And the figures are the rule, not two copies of one mistake: cash plus market
        // value for the brokerage, liabilities negative, no holdings sibling, no category.
        Assert.Equal(700m, balances[brokerage.Id].Balance);
        Assert.Equal(1_300m, balances[checking.Id].Balance);
        Assert.Equal(-200m, balances[card.Id].Balance);
        Assert.Equal(-5_000m, balances[loan.Id].Balance);
        Assert.Equal(3_000m, balances[asset.Id].Balance);
        Assert.False(balances.ContainsKey(brokerage.HoldingsAccountId!.Value));
        Assert.False(balances.ContainsKey(category.Id));
    }

    [Fact]
    public async Task Mcp_list_accounts_carries_the_folder_an_account_sits_in()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var filed = await ledger.AddBankAccountAsync("Account A");
        var loose = await ledger.AddBankAccountAsync("Account B");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var outer = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder A");
        var inner = await CreateFolderAsync(client, ledger.LedgerId, "bank", "Folder B", outer);
        Assert.Equal(HttpStatusCode.NoContent, (await FileAccountAsync(client, ledger.LedgerId, filed.Id, inner)).StatusCode);

        // As an MCP bearer runs it: the tool over coffer_app, RLS scoped to the user.
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var accounts = (await AccountsTools.ListAccounts(NewReportingRepository(db), ledger.LedgerId, includeCategories: true))
            .ToDictionary(a => a.Id);

        // The folder it sits in directly, by its own name, not its path.
        Assert.Equal(inner, accounts[filed.Id].FolderId);
        Assert.Equal("Folder B", accounts[filed.Id].FolderName);
        Assert.Null(accounts[loose.Id].FolderId);
        Assert.Null(accounts[loose.Id].FolderName);
        Assert.Null(accounts[category.Id].FolderId);
    }

    private static AccountsReportingRepository NewReportingRepository(AppDbContext db)
    {
        var balances = new AccountBalancesRepository(db, TimeProvider.System);
        var investment = new InvestmentReportingRepository(db, TimeProvider.System);
        var overview = new OverviewRepository(db, balances, investment);
        return new AccountsReportingRepository(db, balances, investment, overview);
    }
}
