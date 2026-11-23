using System.Linq.Expressions;
using System.Text.Json;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The one word each investment action is shown as (ADR-0027's display words), read
/// from the table the SPA names actions with.
/// </summary>
/// <remarks>
/// <para>One file holds the table, <c>src/Web/src/lib/investmentActionWords.json</c>,
/// keyed by the stored catalog value. The SPA imports it as <c>ACTION_LABEL</c>, and
/// this assembly embeds it, so the register's Action sort, which runs here because the
/// register loads in pages, orders by the words the chips show and cannot drift from
/// them.</para>
/// <para>A value the table does not hold reads as itself, as the SPA's
/// <c>formatInvestmentAction</c> has it: the derived <c>Xfr</c> a cash leg facing an
/// asset account carries (ADR-0036) is its own word.</para>
/// </remarks>
public static class InvestmentActionWords
{
    private const string ResourceName = "Coffer.Api.investment-action-words.json";

    /// <summary>Each stored action and its word.</summary>
    public static IReadOnlyDictionary<string, string> Words { get; } = Load();

    /// <summary>
    /// A stored or derived action's word, as one expression a query provider translates
    /// (a CASE over the table, the value itself otherwise, and null for null).
    /// </summary>
    public static Expression<Func<string?, string?>> WordOf { get; } = Build(Words);

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(InvestmentActionWords).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        var words = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The investment action words deserialized to null.");
        return words;
    }

    private static Expression<Func<string?, string?>> Build(IReadOnlyDictionary<string, string> words)
    {
        var action = Expression.Parameter(typeof(string), "action");
        Expression body = action;
        // Ordinal key order, so the expression, and the SQL it becomes, is the same on
        // every start.
        foreach (var (stored, word) in words.OrderByDescending(w => w.Key, StringComparer.Ordinal))
        {
            body = Expression.Condition(
                Expression.Equal(action, Expression.Constant(stored, typeof(string))),
                Expression.Constant(word, typeof(string)),
                body);
        }
        return Expression.Lambda<Func<string?, string?>>(body, action);
    }
}
