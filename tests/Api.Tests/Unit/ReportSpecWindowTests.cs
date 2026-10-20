using Coffer.Api.Contracts;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// <see cref="ReportSpec.ResolveWindow"/> — ADR-0104 D5. A saved report's date
/// range is a MODE, not a pair of dates, because "Groceries, 12 months" means
/// the last twelve from today and a frozen Oct–Sep window returns a
/// progressively staler answer every month while looking like it worked.
/// </summary>
/// <remarks>
/// Resolution takes the instant as an argument rather than reading the clock,
/// which is what lets these be pure. There is no clock abstraction in the API
/// and this did not need one.
/// </remarks>
public sealed class ReportSpecWindowTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 14, 30, 0, DateTimeKind.Utc);

    private static ReportSpec Spec(Action<SpecBuilder> configure)
    {
        var b = new SpecBuilder();
        configure(b);
        return b.Build();
    }

    private sealed class SpecBuilder
    {
        public ReportDateMode Mode { get; set; } = ReportDateMode.Static;
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? Count { get; set; }
        public ReportWindowUnit Unit { get; set; } = ReportWindowUnit.Month;
        public bool Align { get; set; }

        public ReportSpec Build() => new()
        {
            LedgerId = Guid.NewGuid(),
            DateMode = Mode,
            FromUtc = From,
            ToUtc = To,
            WindowCount = Count,
            WindowUnit = Unit,
            WindowAlignToPeriod = Align,
        };
    }

    [Fact]
    public void Static_mode_hands_back_exactly_what_it_was_given()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var (resolvedFrom, resolvedTo) = Spec(b =>
        {
            b.From = from;
            b.To = to;
        }).ResolveWindow(Now);

        // Default mode is Static precisely so every caller written before
        // ADR-0104 keeps the behaviour it had.
        Assert.Equal(from, resolvedFrom);
        Assert.Equal(to, resolvedTo);
    }

    [Fact]
    public void A_rolling_window_counts_back_from_the_instant_it_runs()
    {
        var (from, to) = Spec(b =>
        {
            b.Mode = ReportDateMode.Sliding;
            b.Count = 12;
        }).ResolveWindow(Now);

        Assert.Equal(new DateTime(2025, 10, 3, 14, 30, 0, DateTimeKind.Utc), from);
        Assert.Equal(Now, to);
    }

    [Fact]
    public void An_aligned_window_snaps_to_the_period_so_the_series_has_whole_columns()
    {
        var (from, to) = Spec(b =>
        {
            b.Mode = ReportDateMode.Sliding;
            b.Count = 12;
            b.Align = true;
        }).ResolveWindow(Now);

        // Twelve months INCLUDING the current one: back to the start of
        // November 2025, not to 3 October 2025. The rolling reading would make
        // a monthly series show thirteen columns, two of them partial.
        Assert.Equal(new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(Now, to);
    }

    [Theory]
    [InlineData(1, 2026, 10, 1)]   // this quarter only → Oct 1
    [InlineData(2, 2026, 7, 1)]    // this and the previous → Jul 1
    [InlineData(4, 2026, 1, 1)]    // a year of quarters → Jan 1
    public void Quarter_alignment_lands_on_a_quarter_boundary(
        int count, int year, int month, int day)
    {
        var (from, _) = Spec(b =>
        {
            b.Mode = ReportDateMode.Sliding;
            b.Count = count;
            b.Unit = ReportWindowUnit.Quarter;
            b.Align = true;
        }).ResolveWindow(Now);

        Assert.Equal(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), from);
    }

    [Fact]
    public void A_sliding_window_without_a_count_is_refused_rather_than_guessed()
    {
        // Defaulting to some N would make a malformed saved row return a
        // plausible wrong answer forever. The caller is code, and code can be
        // fixed.
        var spec = Spec(b => b.Mode = ReportDateMode.Sliding);

        Assert.Throws<ReportRequestException>(() => spec.ResolveWindow(Now));
    }
}
