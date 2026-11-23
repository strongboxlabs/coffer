namespace Coffer.Api.Ingest;

/// <summary>
/// A file's statement currency differs from the currency of the account it is imported
/// into (ADR-0114 D13, "An account's currency must match what is imported into it";
/// ADR-0031 Phase 4). Thrown by <see cref="IngestOrchestrator.RunFileAsync"/> before the
/// import run opens, so nothing is written; the import endpoint answers it as
/// <c>ofx_statement_currency_mismatch</c> (422).
/// </summary>
/// <remarks>Its own type, not an <see cref="InvalidOperationException"/>, which the
/// endpoint reads as a file that could not be parsed.</remarks>
public sealed class StatementCurrencyMismatchException : Exception
{
    public StatementCurrencyMismatchException(string statementCurrency, string accountCurrency)
        : base($"The statement is in {statementCurrency}, but the account is in {accountCurrency}. "
               + $"Import it into an account in {statementCurrency}.")
    {
        StatementCurrency = statementCurrency;
        AccountCurrency = accountCurrency;
    }

    /// <summary>The currency the statement states, upper-cased.</summary>
    public string StatementCurrency { get; }

    /// <summary>The currency of the account imported into.</summary>
    public string AccountCurrency { get; }
}
