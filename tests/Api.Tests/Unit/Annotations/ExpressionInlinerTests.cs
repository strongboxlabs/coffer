using System.Linq.Expressions;

using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Unit.Annotations;

public sealed class ExpressionInlinerTests
{
    private static readonly Expression<Func<int, bool>> IsPositive = n => n > 0;
    private static readonly Expression<Func<int, bool>> IsSmallPositive = n => IsPositive.Inline(n) && n < 10;

    [Fact]
    public void Expansion_substitutes_each_part_body_including_parts_of_parts()
    {
        Expression<Func<int, string>> rule = n => IsSmallPositive.Inline(n * 2) ? "small" : "other";

        var expanded = rule.Expand();
        var compiled = expanded.Compile();

        Assert.Equal("small", compiled(2));
        Assert.Equal("other", compiled(5));
        Assert.Equal("other", compiled(-1));
        // Nothing is left for a query provider to choke on.
        Assert.DoesNotContain(nameof(ExpressionInliner.Inline), expanded.ToString(), StringComparison.Ordinal);
        Assert.Equal("n => IIF((((n * 2) > 0) AndAlso ((n * 2) < 10)), \"small\", \"other\")", expanded.ToString());
    }

    private sealed class Pair
    {
        public int Left { get; init; }
        public int Right { get; init; }
    }

    private static readonly Expression<Func<Pair, bool>> Ordered = p => p.Left < p.Right;

    [Fact]
    public void A_member_read_from_an_initializer_is_replaced_by_the_value_it_was_given()
    {
        // A part over a small input type is called with a member-init argument. A
        // query provider is left with plain column expressions, not a constructed
        // object to read a member back from.
        Expression<Func<int, bool>> rule = n => Ordered.Inline(new Pair { Left = n, Right = n * 3 });

        var expanded = rule.Expand();

        Assert.Equal("n => (n < (n * 3))", expanded.ToString());
        Assert.True(expanded.Compile()(1));
        Assert.False(expanded.Compile()(-1));
    }

    [Fact]
    public void A_member_the_initializer_does_not_assign_is_left_as_written()
    {
        Expression<Func<int, int>> rule = n => new Pair { Left = n }.Right;

        var expanded = rule.Expand();

        Assert.Equal(0, expanded.Compile()(5));
        Assert.Contains("Right", expanded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpanded_part_refuses_to_run_rather_than_skip_the_rule()
    {
        Expression<Func<int, bool>> rule = n => IsPositive.Inline(n);

        var unexpanded = rule.Compile();

        Assert.Throws<InvalidOperationException>(() => unexpanded(1));
    }
}
