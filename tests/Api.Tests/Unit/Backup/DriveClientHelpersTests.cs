using Coffer.Api.Backup.Drive;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// The two pure decisions inside the real Google Drive client (FU-066), which the SDK call
/// around them cannot be tested without a network: reading a listing to its last page, and
/// accepting an upload only if Drive holds what was sent.
/// </summary>
public sealed class DriveClientHelpersTests
{
    private static Func<string?, CancellationToken, Task<(IReadOnlyList<int> Items, string? Next)>> Pages(
        params (int[] Items, string? Next)[] pages)
    {
        var calls = new List<string?>();
        return (token, _) =>
        {
            calls.Add(token);
            // Page n is served for the token page n-1 issued; the first call has none.
            var index = token is null ? 0 : int.Parse(token.Replace("t", ""), System.Globalization.CultureInfo.InvariantCulture);
            var (items, next) = pages[index];
            return Task.FromResult(((IReadOnlyList<int>)items, next));
        };
    }

    [Fact]
    public async Task A_listing_is_read_through_every_page_not_just_the_first()
    {
        // Before: one page of 1000 and the next-page token ignored, so a folder of 1001
        // looked 1000 files long and the mirror acted on a remote set it had not fully seen.
        var all = await DrivePaging.ReadAllAsync(
            Pages(([1, 2], "t1"), ([3, 4], "t2"), ([5], null)), default);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, all);
    }

    [Fact]
    public async Task A_single_page_is_one_request()
    {
        var requested = 0;
        var all = await DrivePaging.ReadAllAsync<int>((_, _) =>
        {
            requested++;
            return Task.FromResult(((IReadOnlyList<int>)[7, 8], (string?)null));
        }, default);

        Assert.Equal(new[] { 7, 8 }, all);
        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task An_empty_folder_is_an_empty_listing()
    {
        Assert.Empty(await DrivePaging.ReadAllAsync(Pages(([], null)), default));
    }

    [Fact]
    public async Task A_listing_that_repeats_a_page_token_fails_instead_of_looping()
    {
        // A listing that cannot be trusted is not one to delete against.
        await Assert.ThrowsAsync<DriveOAuthException>(() => DrivePaging.ReadAllAsync(
            Pages(([1], "t1"), ([2], "t1")), default));
    }

    private static readonly DriveContentCheck Expected = new(1234, "d41d8cd98f00b204e9800998ecf8427e");

    [Fact]
    public void An_upload_that_matches_in_size_and_checksum_is_accepted()
    {
        Assert.Null(DriveUploadVerification.Mismatch(1234, "d41d8cd98f00b204e9800998ecf8427e", Expected));
    }

    [Fact]
    public void The_checksum_compares_case_insensitively()
    {
        Assert.Null(DriveUploadVerification.Mismatch(1234, "D41D8CD98F00B204E9800998ECF8427E", Expected));
    }

    [Fact]
    public void An_upload_of_the_wrong_length_is_rejected()
    {
        // Status "Completed" is what was accepted on before: a truncated file passed it.
        var why = DriveUploadVerification.Mismatch(1000, "d41d8cd98f00b204e9800998ecf8427e", Expected);

        Assert.NotNull(why);
        Assert.Contains("1000", why);
    }

    [Fact]
    public void An_upload_with_a_different_checksum_is_rejected()
    {
        Assert.NotNull(DriveUploadVerification.Mismatch(1234, "ffffffffffffffffffffffffffffffff", Expected));
    }

    [Theory]
    [InlineData(null, "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData(1234L, null)]
    [InlineData(1234L, "")]
    public void An_upload_Drive_will_not_describe_is_not_verified(long? size, string? md5)
    {
        // Silence is not a match: an upload that cannot be checked has not been verified.
        Assert.NotNull(DriveUploadVerification.Mismatch(size, md5, Expected));
    }
}
