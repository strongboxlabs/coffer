using System.Reflection;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Presentation;

namespace Coffer.Api.Tests.Unit.Annotations;

public sealed class StoredNotesQueryTests
{
    /// <summary>
    /// The stored presentation's columns, which a reader reads as the presentation, not
    /// as the row.
    /// </summary>
    private static readonly string[] Notes =
    [
        .. typeof(ResolvedTransactionView).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n.StartsWith("Notes", StringComparison.Ordinal)
                        || n.StartsWith("CounterpartyNotes", StringComparison.Ordinal)),
    ];

    [Fact]
    public void Without_notes_copies_every_view_column_but_the_stored_presentation()
    {
        // A column added to the view is copied unless it is named here: without this,
        // a new column would come back as its default on every register row.
        var source = new ResolvedTransactionView();
        var properties = typeof(ResolvedTransactionView).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
            property.SetValue(source, Distinct(property.PropertyType));

        var copy = StoredNotesQuery.WithoutNotes.Compile()(source);

        var wrong = properties
            .Where(p => Notes.Contains(p.Name)
                ? Equals(p.GetValue(copy), p.GetValue(source))
                : !Equals(p.GetValue(copy), p.GetValue(source)))
            .Select(p => p.Name)
            .ToList();
        Assert.True(wrong.Count == 0, "copied wrongly: " + string.Join(", ", wrong));
        Assert.Equal(20, Notes.Length);
    }

    /// <summary>A value of <paramref name="type"/> that no default and no other column's value equals.</summary>
    private static object Distinct(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(Guid)) return Guid.NewGuid();
        if (underlying == typeof(string)) return "value-" + Guid.NewGuid().ToString("N");
        if (underlying == typeof(decimal)) return 12.34m;
        if (underlying == typeof(int)) return 7;
        if (underlying == typeof(long)) return 9L;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(DateTime)) return new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        if (underlying == typeof(string[])) return new[] { "value" };
        if (underlying == typeof(Coffer.Domain.Annotations.LegKind)) return Coffer.Domain.Annotations.LegKind.SplitOpposite;
        throw new InvalidOperationException($"No test value for a view column of type {type}; add one.");
    }
}
