namespace Coffer.Domain.Annotations;

/// <summary>A leg's place in a transaction: its posting and its account.</summary>
/// <remarks>
/// Addresses a leg within ONE shape. Across a reshape, legs are matched by a
/// <see cref="ShapeChange{TTag}.Survivors"/> map instead, because a leg can move
/// posting (reorder) or account (recategorize) and still be the same leg.
/// </remarks>
public readonly record struct LegKey(int PostingIndex, Guid AccountId);

/// <summary>One leg of a <see cref="TxnShape"/>.</summary>
/// <param name="Key">Posting and account.</param>
/// <param name="LegId">The stored leg's id; null for a leg the write is about to create.</param>
/// <param name="Role">The investment posting role, or null.</param>
/// <param name="OnInvestmentSide">The leg's account is on the event's investment side (see <see cref="LegFacts.ViewingOnInvestmentSide"/>).</param>
/// <param name="IsBrokerage">The leg's account is a brokerage: an account that names a holdings
/// account. Only a brokerage writes and owns an investment event; its holdings account, though
/// on the investment side, never does.</param>
/// <param name="IsTransferSource">On an in-kind transfer, the leg's account is the source
/// brokerage. The adapter sets it from the one shared definition (the brokerage paired in the
/// same posting with the negative-quantity holdings leg, else any brokerage whose holdings
/// account carries that leg), which needs quantities the shape does not hold.</param>
public sealed record ShapeLeg(
    LegKey Key,
    Guid? LegId = null,
    string? Role = null,
    bool OnInvestmentSide = false,
    bool IsBrokerage = false,
    bool IsTransferSource = false);

/// <summary>The structure of one transaction, without its money or its notes.</summary>
public sealed class TxnShape
{
    /// <summary>
    /// The action <c>LedgerActions.TransferShares</c> spells. Repeated here because
    /// the shared project has no dependencies; a unit test holds the two together.
    /// </summary>
    public const string TransferSharesAction = "transfer_shares";

    public TxnShape(Guid? originatingAccountId, string? action, IReadOnlyList<ShapeLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Select(l => l.Key).Distinct().Count() != legs.Count)
            throw new ArgumentException("Two legs of one shape share a posting and an account.", nameof(legs));

        OriginatingAccountId = originatingAccountId;
        Action = action;
        Legs = legs;
    }

    /// <summary>An empty shape: what a create starts from.</summary>
    public static TxnShape None { get; } = new(null, null, []);

    public Guid? OriginatingAccountId { get; }
    public string? Action { get; }
    public IReadOnlyList<ShapeLeg> Legs { get; }

    public bool IsEvent => Action is not null;
    public bool IsTransferShares => Action == TransferSharesAction;
    public int PostingCount => Legs.Select(l => l.Key.PostingIndex).Distinct().Count();

    /// <summary>The transaction-level value sits on the header: several postings.</summary>
    public bool HeaderHeld => PostingCount > 1;

    public TxnShape WithOriginating(Guid? accountId) => new(accountId, Action, Legs);

    public bool Has(Guid accountId) => Legs.Any(l => l.Key.AccountId == accountId);

    public bool IsOnEveryPosting(Guid accountId) =>
        Legs.GroupBy(l => l.Key.PostingIndex).All(p => p.Any(l => l.Key.AccountId == accountId));

    /// <summary>The leg on <paramref name="accountId"/> in the lowest posting that has one.</summary>
    public ShapeLeg? FirstLegOn(Guid accountId) =>
        Legs.Where(l => l.Key.AccountId == accountId).OrderBy(l => l.Key.PostingIndex).FirstOrDefault();

    public ShapeLeg? LegOn(int postingIndex, Guid accountId) =>
        Legs.FirstOrDefault(l => l.Key.PostingIndex == postingIndex && l.Key.AccountId == accountId);

    /// <summary>The other leg of <paramref name="key"/>'s posting.</summary>
    public ShapeLeg? OtherOf(LegKey key) =>
        Legs.FirstOrDefault(l => l.Key.PostingIndex == key.PostingIndex && l.Key != key);
}

/// <summary>A memo and a tag set: what a header or one leg carries.</summary>
public sealed class Notes<TTag> : IEquatable<Notes<TTag>>
    where TTag : notnull
{
    public Notes(string? memo, IReadOnlyList<TTag>? tags = null)
    {
        Memo = memo;
        Tags = tags ?? [];
    }

    public static Notes<TTag> Empty { get; } = new(null);

    public string? Memo { get; }
    public IReadOnlyList<TTag> Tags { get; }
    public bool IsEmpty => Memo is null && Tags.Count == 0;

    public bool Equals(Notes<TTag>? other) =>
        other is not null && Memo == other.Memo && Tags.SequenceEqual(other.Tags);

    public override bool Equals(object? obj) => Equals(obj as Notes<TTag>);

    public override int GetHashCode() => HashCode.Combine(Memo, Tags.Count);

    public override string ToString() => $"memo={Memo ?? "null"} tags=[{string.Join(", ", Tags)}]";
}

/// <summary>The stored or planned notes of one transaction: its header and each leg.</summary>
public sealed class Annotations<TTag> : IEquatable<Annotations<TTag>>
    where TTag : notnull
{
    public Annotations(Notes<TTag>? header, IReadOnlyDictionary<LegKey, Notes<TTag>>? legs = null)
    {
        Header = header ?? Notes<TTag>.Empty;
        Legs = legs ?? new Dictionary<LegKey, Notes<TTag>>();
    }

    public static Annotations<TTag> Empty { get; } = new(null);

    public Notes<TTag> Header { get; }
    public IReadOnlyDictionary<LegKey, Notes<TTag>> Legs { get; }

    public Notes<TTag> For(LegKey key) => Legs.TryGetValue(key, out var notes) ? notes : Notes<TTag>.Empty;

    /// <summary>Equal when the header and every leg carry the same notes; an absent leg equals an empty one.</summary>
    public bool Equals(Annotations<TTag>? other)
    {
        if (other is null || !Header.Equals(other.Header)) return false;
        var keys = Legs.Keys.Concat(other.Legs.Keys).Distinct();
        return keys.All(k => For(k).Equals(other.For(k)));
    }

    public override bool Equals(object? obj) => Equals(obj as Annotations<TTag>);

    public override int GetHashCode() => Header.GetHashCode();

    public override string ToString() =>
        $"header({Header}) " + string.Join(" ", Legs.Where(l => !l.Value.IsEmpty)
            .OrderBy(l => l.Key.PostingIndex).Select(l => $"[{l.Key.PostingIndex}:{l.Key.AccountId:N}]({l.Value})"));
}
