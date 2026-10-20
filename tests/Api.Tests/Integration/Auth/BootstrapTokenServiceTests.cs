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
/// Integration tests for <see cref="BootstrapTokenService"/>. The
/// <c>bootstrap_tokens</c> table is global (not ledger-scoped), so the
/// tests that assert on row counts truncate it up front. Tokens are
/// always random per-test plaintext so concurrent tests can't collide on
/// hash.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BootstrapTokenServiceTests
{
    private readonly PostgresFixture _fixture;

    public BootstrapTokenServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private BootstrapTokenService NewService(SyntheticLedger ledger) =>
        // Bootstrap tokens aren't tied to any ledger, but the SyntheticLedger
        // arg is taken so the test arrange step matches every other
        // integration test (per-test atomic setup). The service uses
        // its own ServiceDbContextFactory internally — the fixture's
        // factory binds to the service-role connection, which is the
        // only role that has access to bootstrap_tokens after PR 3.8.
        new(_fixture.NewServiceFactory(),
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();

        var service = NewService(ledger);
        var issued = await service.EnsureBootstrapTokenAsync();
        Assert.True(issued);

        await using var db = _fixture.NewDbContext();
        var unconsumed = await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null);
        Assert.Equal(1, unconsumed);
    }

    [Fact]
    public async Task EnsureBootstrapToken_is_idempotent_while_a_valid_token_exists()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();
        var service = NewService(ledger);

        var first = await service.EnsureBootstrapTokenAsync();
        var second = await service.EnsureBootstrapTokenAsync();
        Assert.True(first);
        Assert.False(second);

        await using var db = _fixture.NewDbContext();
        var rows = await db.BootstrapTokens.CountAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task EnsureBootstrapToken_skips_once_setup_has_completed()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();
        await MarkSetupCompletedAsync();

        var service = NewService(ledger);
        var issued = await service.EnsureBootstrapTokenAsync();
        Assert.False(issued);
    }

    [Fact]
    public async Task A_completed_install_with_no_passkey_left_does_not_rearm_setup()
    {
        // The 2026-10-12 audit's S3: setup used to re-arm whenever NO PASSKEY existed,
        // so a user who deleted their last passkey (keeping recovery codes) put pre-auth
        // setup — and its whole-database restore — back on offer. Zero passkeys here,
        // setup completed: nothing may be issued.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();          // no passkeys anywhere
        await MarkSetupCompletedAsync();

        var service = NewService(ledger);
        Assert.False(await service.EnsureBootstrapTokenAsync());
        Assert.Null(await service.ReissueSetupUrlAsync());

        await using var db = _fixture.NewDbContext();
        Assert.Equal(0, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null));
    }

    [Fact]
    public async Task Consume_succeeds_once_and_then_fails()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var service = NewService(ledger);

        // Insert a known-plaintext token directly so we can present its
        // value to ConsumeAsync — the EnsureBootstrapTokenAsync path
        // only logs the plaintext, never returns it.
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        await using (var db = _fixture.NewDbContext())
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var service = NewService(ledger);
        Assert.False(await service.ConsumeAsync("totally-not-a-real-token"));
    }

    [Fact]
    public async Task Consume_rejects_expired_token()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        await using (var db = _fixture.NewDbContext())
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var service = NewService(ledger);
        Assert.False(await service.ConsumeAsync(plaintext!));
    }

    [Fact]
    public async Task Reissue_returns_setup_url_and_mints_a_token()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();

        var service = NewService(ledger);
        var url = await service.ReissueSetupUrlAsync();

        Assert.NotNull(url);
        // The first configured Fido2 origin (set in NewService) + the SPA
        // /setup/{token} route.
        Assert.StartsWith("http://localhost:8080/setup/", url);

        await using var db = _fixture.NewDbContext();
        var unconsumed = await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null);
        Assert.Equal(1, unconsumed);
    }

    [Fact]
    public async Task Reissue_revokes_a_prior_unconsumed_token_then_mints_fresh()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();
        var service = NewService(ledger);

        Assert.True(await service.EnsureBootstrapTokenAsync()); // token A
        var url = await service.ReissueSetupUrlAsync();          // revoke A, mint B
        Assert.NotNull(url);

        await using var db = _fixture.NewDbContext();
        // Exactly one unconsumed token remains (the freshly-minted one) so the
        // printed URL is the only one that works; the prior token was revoked.
        Assert.Equal(1, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null));
        Assert.Equal(2, await db.BootstrapTokens.CountAsync());
    }

    [Fact]
    public async Task Reissue_returns_null_once_setup_has_completed()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ClearBootstrapStateAsync();
        await MarkSetupCompletedAsync();

        var service = NewService(ledger);
        Assert.Null(await service.ReissueSetupUrlAsync());
    }

    /// <summary>Record that setup has happened, as setup itself does.</summary>
    private async Task MarkSetupCompletedAsync()
    {
        await using var db = _fixture.NewDbContext();
        db.SystemSettings.Add(new SystemSettingRow
        {
            Key = SystemSettingsRepository.SetupCompletedKey,
            ValueJson = "true",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Wipe the global tables that drive <c>EnsureBootstrapTokenAsync</c>'s
    /// preconditions. Bootstrap is inherently global ("on first start, when
    /// no credentials exist…") so these tests must own the state for the
    /// duration of their run — synthetic-ledger isolation can't make
    /// "credentials exist anywhere in the DB" go away. Tests in other
    /// classes (Users, Credentials) don't read these globals, so the
    /// truncation only affects the bootstrap suite.
    /// </summary>
    /// <remarks>
    /// TRUNCATE is the cleanest expression of "wipe these tables fast";
    /// EF doesn't have a native equivalent, so we route the SQL through
    /// the DbContext's connection. <c>ExecuteSqlRawAsync</c> participates
    /// in the same connection pool as the rest of the test code.
    /// </remarks>
    private async Task ClearBootstrapStateAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE bootstrap_tokens, webauthn_credentials CASCADE;");
        // "Setup has happened" is the marker now, not "a passkey exists": clear it too,
        // so each test starts from a fresh install.
        await db.SystemSettings
            .Where(s => s.Key == SystemSettingsRepository.SetupCompletedKey)
            .ExecuteDeleteAsync();
    }
}
