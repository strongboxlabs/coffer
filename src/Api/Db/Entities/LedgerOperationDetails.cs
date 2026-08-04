using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coffer.Api.Db.Entities;

/// <summary>
/// Ingest-family run detail (ADR-0055), (de)serialized to/from
/// <c>ledger_operations.details</c>. JSON keys are snake_case to match the jsonb
/// the migration backfilled.
/// </summary>
internal sealed record IngestRunDetails
{
    [JsonPropertyName("txns_fetched")]       public int TxnsFetched { get; init; }
    [JsonPropertyName("txns_inserted")]      public int TxnsInserted { get; init; }
    [JsonPropertyName("txns_skipped")]       public int TxnsSkipped { get; init; }
    [JsonPropertyName("txns_promoted")]      public int TxnsPromoted { get; init; }
    [JsonPropertyName("txns_already_known")] public int TxnsAlreadyKnown { get; init; }
    [JsonPropertyName("txns_still_pending")] public int TxnsStillPending { get; init; }
}

/// <summary>
/// Quote-family run detail (ADR-0055 slice B), stored in
/// <c>ledger_operations.details</c>.
/// </summary>
internal sealed record QuoteRunDetails
{
    [JsonPropertyName("prices_inserted")]       public int PricesInserted { get; init; }
    [JsonPropertyName("prices_updated")]        public int PricesUpdated { get; init; }
    [JsonPropertyName("securities_unresolved")] public int SecuritiesUnresolved { get; init; }
    // Per-source attribution (ADR-0070 sources): how many of the written prices
    // came from the market-data fetch (Yahoo) vs the SimpleFIN feed. Surfaced in
    // the activity log so a quote refresh says WHICH provider moved the prices.
    [JsonPropertyName("prices_from_fetch")]     public int PricesFromFetch { get; init; }
    [JsonPropertyName("prices_from_simplefin")] public int PricesFromSimplefin { get; init; }
}

/// <summary>
/// (De)serialization for the open-ended <c>ledger_operations.details</c> jsonb
/// (ADR-0055). Each provider family round-trips its own typed record; the DB
/// stays generic, the C# stays typed at the edges.
/// </summary>
internal static class LedgerOperationDetails
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T details) =>
        JsonSerializer.Serialize(details, Options);

    public static T Deserialize<T>(string? json) where T : new() =>
        string.IsNullOrWhiteSpace(json)
            ? new T()
            : JsonSerializer.Deserialize<T>(json, Options) ?? new T();
}
