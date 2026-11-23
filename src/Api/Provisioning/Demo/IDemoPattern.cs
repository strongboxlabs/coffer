namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// One household pattern: paychecks, rent, a card's spending, a security's
/// dividends. Each posts its own rows into the book.
/// </summary>
/// <remarks>
/// <see cref="Name"/> keys the pattern's random streams and its row ids, so renaming
/// a pattern changes its rows and nothing else; it must stay unique. A pattern draws
/// only from streams named after itself, which is what keeps a change to one
/// pattern from moving any other's rows.
/// </remarks>
public interface IDemoPattern
{
    /// <summary>The pattern's stable, unique name.</summary>
    string Name { get; }

    /// <summary>Orders same-day rows across patterns in the export; lower first.</summary>
    int Rank { get; }

    /// <summary>Post the pattern's rows for the whole window.</summary>
    void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book);
}
