using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// Tiered grandfather-father-son retention selection (ADR-0060). Pure +
/// clock-injected, so the bucketing is exercised with synthetic timestamps and
/// no filesystem. A fixed "now" + same-calendar-day timestamps keep the ISO
/// week / month buckets deterministic regardless of the real date.
/// </summary>
public sealed class BackupRetentionTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetentionPolicy Policy = new(DailyDays: 7, WeeklyWeeks: 8, MonthlyMonths: 12);

    private static BackupFileInfo At(string id, DateTime when) => new(id, 100, when);

    private static IReadOnlyList<string> ToDelete(params BackupFileInfo[] backups) =>
        BackupStore.SelectForDeletion(backups, Now, Policy);

    [Fact]
    public void Keeps_everything_inside_the_daily_window()
    {
        var del = ToDelete(
            At("a", Now),
            At("b", Now.AddDays(-3)),
            At("c", Now.AddDays(-6)),
            At("d", Now.AddDays(-6).AddHours(-2)));   // same day as c, still daily
        Assert.Empty(del);
    }

    [Fact]
    public void Pinned_artifacts_are_never_deleted()
    {
        // Two old artifacts that retention would normally prune; pin one.
        var pinnedOld = At("p-old", Now.AddYears(-3));
        var prunableOld = At("u-old", Now.AddYears(-3).AddHours(-1));
        var pinned = new HashSet<string>(["p-old"], StringComparer.Ordinal);

        var del = BackupStore.SelectForDeletion([pinnedOld, prunableOld], Now, Policy, pinned);

        Assert.DoesNotContain("p-old", del);   // pinned → kept
        Assert.Contains("u-old", del);          // unpinned + ancient → pruned
    }

    [Fact]
    public void Weekly_tier_keeps_only_the_newest_in_a_week_past_the_daily_window()
    {
        // ~10 days ago: outside daily (>7d), inside weekly (<56d). Two on the
        // same calendar day → same ISO week → only the newest survives.
        var older = At("w-old", Now.AddDays(-10).AddHours(-3));
        var newer = At("w-new", Now.AddDays(-10));
        var del = ToDelete(older, newer);
        Assert.Equal(new[] { "w-old" }, del);
    }

    [Fact]
    public void Monthly_tier_keeps_only_the_newest_in_a_month_past_the_weekly_window()
    {
        // ~100 days ago: outside weekly (>56d), inside 12 months. Two on the
        // same calendar day → same month → only the newest survives.
        var older = At("m-old", Now.AddDays(-100).AddHours(-4));
        var newer = At("m-new", Now.AddDays(-100));
        var del = ToDelete(older, newer);
        Assert.Equal(new[] { "m-old" }, del);
    }

    [Fact]
    public void Prunes_backups_older_than_the_monthly_window()
    {
        var del = ToDelete(
            At("recent", Now),
            At("ancient", Now.AddDays(-400)));   // > 12 months → no tier
        Assert.Contains("ancient", del);
        Assert.DoesNotContain("recent", del);
    }

    [Fact]
    public void A_lone_old_backup_is_kept_as_its_week_and_month_representative()
    {
        // The only backup in its week + month, 10 days old → kept (deleting it
        // would lose that period entirely).
        Assert.Empty(ToDelete(At("solo", Now.AddDays(-10))));
    }

    [Fact]
    public void Mixed_history_keeps_one_representative_per_tier_bucket()
    {
        // A realistic spread: dailies (all kept), two in one old week (1 kept),
        // two in one old month (1 kept), one ancient (pruned).
        var del = ToDelete(
            At("today", Now),
            At("yesterday", Now.AddDays(-1)),
            At("week-a", Now.AddDays(-20)),            // same day → same week
            At("week-b", Now.AddDays(-20).AddHours(-5)),
            At("month-a", Now.AddDays(-120)),          // same day → same month
            At("month-b", Now.AddDays(-120).AddHours(-6)),
            At("ancient", Now.AddDays(-500)));

        // Pruned: the older duplicate in the week, the older in the month, the
        // ancient one. Kept: both dailies, the week rep, the month rep.
        Assert.Equal(
            new[] { "week-b", "month-b", "ancient" }.OrderBy(x => x),
            del.OrderBy(x => x));
    }
}
