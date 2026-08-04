using System.Data;
using Dapper;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Bridges <see cref="DateOnly"/> to Postgres's <c>DATE</c> column type via
/// Dapper. Dapper doesn't auto-handle <see cref="DateOnly"/>, so calendar-
/// day columns (`recurring_transactions.start_date`, `end_date`, …) need an
/// explicit handler that converts to and from <see cref="DateTime"/> at the
/// driver boundary.
/// </summary>
/// <remarks>
/// <para><c>DateOnly</c> is the right C# representation for a column that
/// has no time component — using <c>DateTime</c> here would invite timezone
/// confusion and silent rounding. The handler reads back DATE values that
/// Npgsql surfaces as <c>DateTime</c> (with <c>Kind=Unspecified</c> and
/// time = midnight) and re-projects them to <c>DateOnly</c>.</para>
/// <para>Registered once per process via <see cref="Register"/>; the
/// importer calls this from its CLI entry point.</para>
/// </remarks>
public sealed class DapperDateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DapperDateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableHandler());
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateTime dt   => DateOnly.FromDateTime(dt),
        DateOnly d    => d,
        string s      => DateOnly.Parse(s),
        _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly"),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value  = value.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>
    /// Companion handler so <see cref="DateOnly?"/> parameters and reads
    /// also work without each row having to special-case nullability.
    /// </summary>
    private sealed class NullableHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override DateOnly? Parse(object? value) => value switch
        {
            null          => null,
            DBNull        => null,
            DateTime dt   => DateOnly.FromDateTime(dt),
            DateOnly d    => d,
            string s      => DateOnly.Parse(s),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly?"),
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value  = value is null ? DBNull.Value : (object)value.Value.ToDateTime(TimeOnly.MinValue);
        }
    }
}
