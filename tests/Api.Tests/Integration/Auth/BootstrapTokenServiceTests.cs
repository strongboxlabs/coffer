using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Integration tests for <see cref="BootstrapTokenService"/>. Each test starts
/// on its own database as a fresh install: no bootstrap token, no passkey, setup
/// not completed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BootstrapTokenServiceTests : DatabaseTest
{
    public BootstrapTokenServiceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private BootstrapTokenService NewService(SyntheticLedger ledger) =>
        // Bootstrap tokens aren't tied to any ledger, but the SyntheticLedger
        // arg is taken so the test arrange step matches every other
        // integration test (per-test atomic setup). The service uses
        // its own ServiceDbContextFactory internally — the fixture's
        // factory binds to the service-role connection, which is the
        // only role that has access to bootstrap_tokens after PR 3.8.
        new(Db.NewServiceFactory(),
            Options.Create(new ApiOptions
            {
                Bootstrap = new BootstrapOptions { TokenLifetimeHours = 1 },
                // Origins now defaults to empty (config must own it), so set the
                // canonical browser origin explicitly for the setup-URL assertion.
                Fido2 = new Fido2Options { Origins = new[] { "http://localhost:8080" } },
            }),
            NullLogger<BootstrapTokenService>.Instance, TimeProvider.System);

    [Fact]
    public async Task EnsureBootstrapToken_issues_when_no_credentials_exist()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var service = NewService(ledger);
        var issued = await service.EnsureBootstrapTokenAsync();
        Assert.True(issued);

        await using var db = Db.NewDbContext();
        var unconsumed = await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null);
        Assert.Equal(1, unconsumed);
    }

    [Fact]
    public async Task EnsureBootstrapToken_is_idempotent_while_a_valid_token_exists()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var service = NewService(ledger);

        var first = await service.EnsureBootstrapTokenAsync();
        var second = await service.EnsureBootstrapTokenAsync();
        Assert.True(first);
        Assert.False(second);

        await using var db = Db.NewDbContext();
        var rows = await db.BootstrapTokens.CountAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task EnsureBootstrapToken_skips_once_setup_has_completed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await MarkSetupCompletedAsync();

        var service = NewService(ledger);
        var issued = await service.EnsureBootstrapTokenAsync();
        Assert.False(issued);
    }

    [Fact]
    public async Task A_completed_install_with_no_passkey_left_does_not_rearm_setup()
    {
        // Setup that re-arms whenever NO PASSKEY exists lets a user who deletes their
        // last passkey (keeping recovery codes) put pre-auth setup — and its
        // whole-database restore — back on offer (ADR-0105 D7). Zero
        // passkeys here, setup completed: nothing may be issued.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await MarkSetupCompletedAsync();

        var service = NewService(ledger);
        Assert.False(await service.EnsureBootstrapTokenAsync());
        Assert.Null(await service.ReissueSetupUrlAsync());

        await using var db = Db.NewDbContext();
        Assert.Equal(0, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null));
    }

    [Fact]
    public async Task Consume_succeeds_once_and_then_fails()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var service = NewService(ledger);

        // Insert a known-plaintext token directly so we can present its
        // value to ConsumeAsync — the EnsureBootstrapTokenAsync path
        // only logs the plaintext, never returns it.
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        await using (var db = Db.NewDbContext())
        {
            db.BootstrapTokens.Add(new BootstrapTokenRow
            {
                TokenHash = hash,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await service.ConsumeAsync(plaintext));
        Assert.False(await service.ConsumeAsync(plaintext));
    }

    [Fact]
    public async Task Consume_rejects_unknown_token()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var service = NewService(ledger);
        Assert.False(await service.ConsumeAsync("totally-not-a-real-token"));
    }

    [Fact]
    public async Task Consume_rejects_expired_token()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        await using (var db = Db.NewDbContext())
        {
            db.BootstrapTokens.Add(new BootstrapTokenRow
            {
                TokenHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var service = NewService(ledger);
        Assert.False(await service.ConsumeAsync(plaintext));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Consume_rejects_empty_input(string? plaintext)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var service = NewService(ledger);
        Assert.False(await service.ConsumeAsync(plaintext!));
    }

    [Fact]
    public async Task Reissue_returns_setup_url_and_mints_a_token()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        var service = NewService(ledger);
        var url = await service.ReissueSetupUrlAsync();

        Assert.NotNull(url);
        // The first configured Fido2 origin (set in NewService) + the SPA
        // /setup/{token} route.
        Assert.StartsWith("http://localhost:8080/setup/", url);

        await using var db = Db.NewDbContext();
        var unconsumed = await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null);
        Assert.Equal(1, unconsumed);
    }

    [Fact]
    public async Task Reissue_revokes_a_prior_unconsumed_token_then_mints_fresh()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var service = NewService(ledger);

        Assert.True(await service.EnsureBootstrapTokenAsync()); // token A
        var url = await service.ReissueSetupUrlAsync();          // revoke A, mint B
        Assert.NotNull(url);

        await using var db = Db.NewDbContext();
        // Exactly one unconsumed token remains (the freshly-minted one) so the
        // printed URL is the only one that works; the prior token was revoked.
        Assert.Equal(1, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null));
        Assert.Equal(2, await db.BootstrapTokens.CountAsync());
    }

    [Fact]
    public async Task Reissue_returns_null_once_setup_has_completed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await MarkSetupCompletedAsync();

        var service = NewService(ledger);
        Assert.Null(await service.ReissueSetupUrlAsync());
    }

    /// <summary>Record that setup has happened, as setup itself does.</summary>
    private async Task MarkSetupCompletedAsync()
    {
        await using var db = Db.NewDbContext();
        db.SystemSettings.Add(new SystemSettingRow
        {
            Key = SystemSettingsRepository.SetupCompletedKey,
            ValueJson = "true",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
