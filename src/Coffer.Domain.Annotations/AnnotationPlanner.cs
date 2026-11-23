namespace Coffer.Domain.Annotations;

/// <summary>
/// The one write primitive: every memo and tag write, from the API and the
/// importer, is planned here and only persisted by an adapter.
/// </summary>
/// <remarks>
/// <para><b>Placement.</b> A one-posting result keeps its values on legs: the
/// writing side's own leg, or the brokerage leg of an event; its header ends empty,
/// because a one-split never has header notes. A multi-posting result keeps the
/// transaction's value on the header and each line's on its originating-side leg;
/// only the originating account may write it. An event's investment-side legs never
/// carry a value: the event shows one memo and one tag set.</para>
/// <para><b>Order.</b> The shape transition is applied first, then the intent is laid
/// over it with "set" semantics that replace and never join again. That is why the
/// editor can pre-join a collapse into its fields and send the result back
/// unchanged, with no notice, and why a set the transition grew past the tag limit
/// still saves unchanged.</para>
/// <para><b>Ownership.</b> The plan carries the originating account the write
/// leaves behind, so every adapter stores it from one place.</para>
/// <para>A result that breaks an ownership or placement invariant (see
/// <c>Broken</c>) is a bug here, not a user error, and throws.</para>
/// </remarks>
public static class AnnotationPlanner
{
    /// <summary>The tag limit per set, header or leg. Every API writer refuses a tag set through the plan, so this is the only cap.</summary>
    public const int MaxTagsPerSet = 20;

    /// <summary>Plan a write to an existing transaction, with or without a change of shape.</summary>
    public static AnnotationPlan<TTag> Plan<TTag>(
        ShapeChange<TTag> change, AnnotationIntent<TTag> intent, TagLimit limit = TagLimit.Enforce)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(intent);
        if (change.Before.Legs.Count > 0 && change.Before.OriginatingAccountId is null)
            throw new ArgumentException("A transaction with legs has an originating account.", nameof(change));

        var violations = new List<AnnotationViolation>();
        CheckWriter(change.Before, change.After, intent, violations);
        if (violations.Count > 0)
            return new(change.Current, change.Before.OriginatingAccountId, violations);

        var owner = Owner(change, intent.Side);
        var shaped = change.After.WithOriginating(owner);
        var baseline = Transition(change, intent.Side, shaped);
        var result = Overlay(shaped, baseline, intent, limit, violations);

        if (Broken(shaped, result) is { } bug)
            throw new InvalidOperationException($"Annotation planner bug: {bug}");
        return new(result, owner, violations);
    }

    /// <summary>Plan the notes of a new transaction, entered from <c>intent.Side</c>.</summary>
    public static AnnotationPlan<TTag> Seed<TTag>(
        TxnShape shape, AnnotationIntent<TTag> intent, TagLimit limit = TagLimit.Enforce)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(intent);

        var violations = new List<AnnotationViolation>();
        CheckWriter(TxnShape.None, shape, intent, violations);
        if (violations.Count > 0)
            return new(Annotations<TTag>.Empty, null, violations);

        var owner = shape.Legs.Count == 0 ? (Guid?)null : intent.Side;
        var shaped = shape.WithOriginating(owner);
        var result = Overlay(shaped, Annotations<TTag>.Empty, intent, limit, violations);

        if (Broken(shaped, result) is { } bug)
            throw new InvalidOperationException($"Annotation planner bug: {bug}");
        return new(result, owner, violations);
    }

    /// <summary>
    /// Store <paramref name="values"/> as given, normalised: a clone (a reminder
    /// firing) or an import that has already placed every value. Never capped; a
    /// value the shape cannot hold fails closed.
    /// </summary>
    public static AnnotationPlan<TTag> Seed<TTag>(TxnShape shape, Annotations<TTag> values)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(values);

        var result = Normalised(values.Header, values.Legs);
        if (Broken(shape, result) is { } problem)
            throw new ArgumentException($"These notes do not fit the transaction: {problem}", nameof(values));
        return new(result, shape.OriginatingAccountId, []);
    }

    /// <summary>
    /// Plan the notes of the in-kind transfer that replaces a sell in one brokerage and
    /// a buy in another (convert in-kind). The transfer's value is the sell's joined
    /// with the buy's (<see cref="AnnotationText.JoinDistinctMemos"/>, sell first) and the union
    /// of their tags. It is a system merge: never capped, nothing dropped.
    /// </summary>
    /// <param name="transfer">The resulting <c>transfer_shares</c> shape, owned by its source brokerage.</param>
    public static AnnotationPlan<TTag> ConvertInKind<TTag>(
        TxnShape sell, Annotations<TTag> sellNotes, TxnShape buy, Annotations<TTag> buyNotes, TxnShape transfer)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(sell);
        ArgumentNullException.ThrowIfNull(sellNotes);
        ArgumentNullException.ThrowIfNull(buy);
        ArgumentNullException.ThrowIfNull(buyNotes);
        ArgumentNullException.ThrowIfNull(transfer);
        if (!sell.IsEvent || !buy.IsEvent)
            throw new ArgumentException("Convert in-kind replaces two investment events.", nameof(sell));
        if (!transfer.IsTransferShares)
            throw new ArgumentException("Convert in-kind produces an in-kind transfer.", nameof(transfer));

        // A transfer of several postings keeps the value on its header; a one-posting
        // one on its source brokerage's leg, like any one-posting event.
        var merged = Merge(EventValue(sell, sellNotes), EventValue(buy, buyNotes));
        var target = !transfer.HeaderHeld && transfer.OriginatingAccountId is { } o ? transfer.FirstLegOn(o) : null;
        var result = target is null
            ? Normalised(merged, new Dictionary<LegKey, Notes<TTag>>())
            : Normalised(Notes<TTag>.Empty, new Dictionary<LegKey, Notes<TTag>> { [target.Key] = merged });

        if (Broken(transfer, result) is { } bug)
            throw new InvalidOperationException($"Annotation planner bug: {bug}");
        return new(result, transfer.OriginatingAccountId, []);
    }

    /// <summary>What an event's own register shows as its one memo and tag set.</summary>
    private static Notes<TTag> EventValue<TTag>(TxnShape shape, Annotations<TTag> notes)
        where TTag : notnull
    {
        if (shape.OriginatingAccountId is not { } owner || shape.FirstLegOn(owner) is not { } leg)
            return shape.HeaderHeld ? notes.Header : Notes<TTag>.Empty;
        var shown = MemoTagRules.PresentInMemory(shape, notes, leg.Key);
        return new Notes<TTag>(shown.Memo, shown.Tags);
    }

    /// <summary>
    /// The originating account after legs move to another account (recategorize, a
    /// category or account merge, a move, the move that clears an action). It stays
    /// unless no leg remains on it; then it follows to <paramref name="targetAccountId"/>.
    /// No annotation is rewritten: values sit on legs, and legs keep their ids.
    /// </summary>
    public static Guid? Repoint(TxnShape before, TxnShape after, Guid targetAccountId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (after.Legs.Count == 0) return null;

        var owner = before.OriginatingAccountId is { } o && after.Has(o) ? o : targetAccountId;
        if (!after.Has(owner))
            throw new ArgumentException("The repoint target is not on the transaction.", nameof(targetAccountId));
        if (Broken(after.WithOriginating(owner), Annotations<string>.Empty) is { } bug)
            throw new InvalidOperationException($"Annotation planner bug: {bug}");
        return owner;
    }

    // ---------------------------------------------------------------------
    // Who may write
    // ---------------------------------------------------------------------

    private static void CheckWriter<TTag>(
        TxnShape before, TxnShape after, AnnotationIntent<TTag> intent, List<AnnotationViolation> violations)
        where TTag : notnull
    {
        var side = intent.Side;
        var existing = before.Legs.Count > 0;

        if (after.IsEvent)
        {
            // The investment editor writes from the brokerage it names, which may be
            // new to the transaction (an event moved between brokerages).
            if (!after.Has(side) && !before.Has(side))
                violations.Add(new(AnnotationViolationCodes.SideNotOnTransaction, "The account is not on this transaction."));
            else if (!after.Legs.Any(l => l.Key.AccountId == side && IsEventBrokerage(after, l)))
                violations.Add(new(AnnotationViolationCodes.ReadOnlyRow,
                    "Only the event's brokerage edits its memo and tags; an in-kind transfer's is its source brokerage."));
        }
        else if (existing && before.Has(side) && before.Legs.Where(l => l.Key.AccountId == side).All(l => IsReadOnly(before, l.Key)))
        {
            // Checked before presence: a read-only row that tries to drop its own
            // line is refused as read-only, which is what the user can act on.
            violations.Add(new(AnnotationViolationCodes.ReadOnlyRow, "This row shows the transaction read-only; edit it from the account it was entered from."));
        }
        else if (!after.Has(side) || (existing && !before.Has(side)))
        {
            violations.Add(new(AnnotationViolationCodes.SideNotOnTransaction, "The account is not on this transaction."));
        }
        else if (after.HeaderHeld && !before.HeaderHeld && !after.IsOnEveryPosting(side))
        {
            // A new or newly split transaction is owned by the side writing it, which
            // is then the split side of every line.
            violations.Add(new(AnnotationViolationCodes.SideNotOnTransaction, "A split's account must be on every line."));
        }

        if (intent.Lines is { Count: > 0 } lines)
        {
            if (!after.HeaderHeld)
                violations.Add(new(AnnotationViolationCodes.LineNotesOnOnePosting, "A one-posting transaction has no lines to annotate."));
            else if (after.IsEvent)
                violations.Add(new(AnnotationViolationCodes.LineNotesInvalid,
                    "An investment event has no line notes; its memo and tags are the whole event's."));
            else if (lines.GroupBy(l => l.PostingIndex).FirstOrDefault(g => g.Count() > 1) is { } twice)
                violations.Add(new(AnnotationViolationCodes.LineNotesInvalid, $"Line {twice.Key} is given more than once."));
        }
    }

    private static bool IsReadOnly(TxnShape shape, LegKey leg) =>
        MemoTagRules.PresentInMemory(shape, Annotations<string>.Empty, leg).Presentation.IsReadOnly;

    /// <summary>
    /// The leg is on the account that writes and owns the event: a brokerage on the
    /// investment side, and for an in-kind transfer the source brokerage. A holdings
    /// account and a transfer's destination are on the investment side too, and are
    /// neither.
    /// </summary>
    private static bool IsEventBrokerage(TxnShape shape, ShapeLeg leg) =>
        leg.OnInvestmentSide && leg.IsBrokerage && (!shape.IsTransferShares || leg.IsTransferSource);

    // ---------------------------------------------------------------------
    // Ownership: who the transaction belongs to after the write
    // ---------------------------------------------------------------------

    private static Guid? Owner<TTag>(ShapeChange<TTag> change, Guid side)
        where TTag : notnull
    {
        var (before, after) = (change.Before, change.After);
        if (after.Legs.Count == 0) return null;

        // Setting an action, or moving an event, hands it to the brokerage the
        // investment editor writes from; a split hands it to the side that splits.
        if (after.IsEvent || before.Legs.Count == 0 || (!before.HeaderHeld && after.HeaderHeld))
            return side;

        // Same shape, N to M, collapse: kept, unless no leg remains on it; then it
        // follows its legs to wherever they went.
        var owner = before.OriginatingAccountId!.Value;
        if (after.Has(owner)) return owner;
        return change.Survivors
            .Where(p => p.Value.AccountId == owner)
            .OrderBy(p => p.Key.PostingIndex)
            .Select(p => (Guid?)p.Key.AccountId)
            .FirstOrDefault() ?? side;
    }

    // ---------------------------------------------------------------------
    // Shape transitions: a change of shape never changes what a register shows,
    // except for the lines the user removes
    // ---------------------------------------------------------------------

    private static Annotations<TTag> Transition<TTag>(ShapeChange<TTag> change, Guid side, TxnShape after)
        where TTag : notnull
    {
        var (before, current) = (change.Before, change.Current);
        if (after.Legs.Count == 0) return Annotations<TTag>.Empty;
        var owner = after.OriginatingAccountId!.Value;

        // A one-posting event keeps its one value on the brokerage leg, but the value
        // is the event's, not an override of that leg: an action change can give the
        // leg another role and a move can put it on another brokerage, and neither
        // leg survives. So that leg is left out of the survivors, and the value is
        // placed on the new editable slot below whatever survives.
        var eventLeg = before.IsEvent && !before.HeaderHeld && before.OriginatingAccountId is { } eventOwner
            ? before.FirstLegOn(eventOwner)
            : null;

        // Values follow their legs; a removed line takes its values with it.
        var legs = new Dictionary<LegKey, Notes<TTag>>();
        foreach (var leg in after.Legs)
            if (change.Survivors.TryGetValue(leg.Key, out var was) && was != eventLeg?.Key) legs[leg.Key] = current.For(was);
        var header = before.HeaderHeld ? current.Header : Notes<TTag>.Empty;

        // The values merged onto a leg so far, in order. A later merge onto the same leg
        // joins all of them at once, so each distinct memo is kept once however many
        // values arrive there (the event rule, ADR-0108 D11), not once per pair.
        var merged = new Dictionary<LegKey, List<Notes<TTag>>>();
        List<Notes<TTag>> PartsOf(LegKey key) =>
            merged.TryGetValue(key, out var parts) ? parts : [legs.GetValueOrDefault(key) ?? Notes<TTag>.Empty];
        void Place(LegKey key, List<Notes<TTag>> parts)
        {
            merged[key] = parts;
            legs[key] = Merge(parts);
        }

        if (before.IsEvent)
        {
            // The event's value: the header of a multi-posting event, else its
            // brokerage leg's. Crossing from one posting to several copies what the
            // brokerage SHOWED, as a split does, so a brokerage that was inheriting
            // its counterpart's value does not go blank.
            Notes<TTag> value;
            if (before.HeaderHeld) value = current.Header;
            else if (eventLeg is null) value = Notes<TTag>.Empty;
            else if (after.HeaderHeld)
            {
                var shown = MemoTagRules.PresentInMemory(before, current, eventLeg.Key);
                value = new Notes<TTag>(shown.Memo, shown.Tags);
            }
            else value = current.For(eventLeg.Key);

            header = Notes<TTag>.Empty;
            if (after.HeaderHeld) header = value;
            else
            {
                var target = after.FirstLegOn(owner)
                             ?? throw new InvalidOperationException("Annotation planner bug: the owner has no leg on a one-posting result.");
                Place(target.Key, [value, .. PartsOf(target.Key)]);
            }
        }
        else if (before.Legs.Count > 0 && !before.HeaderHeld && after.HeaderHeld)
        {
            // One posting to several. The value the writing side SHOWED becomes the
            // transaction's, so neither register changes what it shows.
            var source = before.FirstLegOn(before.Has(side) ? side : before.OriginatingAccountId!.Value)!;
            var shown = MemoTagRules.PresentInMemory(before, current, source.Key);
            header = new Notes<TTag>(shown.Memo, shown.Tags);

            // On a bank split the same value is copied onto line 1, the original
            // posting found by leg identity; afterwards the two are independent. On an
            // event the brokerage leg is investment side and is cleared below.
            if (!after.IsEvent)
            {
                var lineOne = after.Legs.FirstOrDefault(l =>
                    l.Key.AccountId == owner
                    && change.Survivors.TryGetValue(l.Key, out var was) && was == source.Key);
                if (lineOne is not null) legs[lineOne.Key] = header;
            }
        }
        else if (before.HeaderHeld && !after.HeaderHeld)
        {
            // Several postings to one: the remaining line's editable leg gets
            // "header · line" and the union of their tags. The opposite override, if
            // any, stays on its leg; without one, that side inherits this value.
            var target = after.FirstLegOn(owner)
                         ?? throw new InvalidOperationException("Annotation planner bug: the owner has no leg after a collapse.");
            Place(target.Key, [header, .. PartsOf(target.Key)]);
        }

        if (!after.HeaderHeld) header = Notes<TTag>.Empty;

        if (after.IsEvent)
        {
            // An event's investment side carries nothing but its editable leg (one
            // posting). Whatever such a leg held moves onto the editable side rather
            // than being dropped: nothing is ever dropped by a change of shape. Each
            // distinct memo is kept once (the event rule, ADR-0108 D11).
            var editable = after.HeaderHeld ? null : after.FirstLegOn(owner);
            var moved = new List<Notes<TTag>>();
            foreach (var leg in after.Legs.Where(l => l.OnInvestmentSide && l.Key != editable?.Key).OrderBy(l => l.Key.PostingIndex))
                if (legs.Remove(leg.Key, out var left) && !left.IsEmpty) moved.Add(left);
            if (moved.Count > 0)
            {
                if (editable is null) header = Merge([header, .. moved]);
                else Place(editable.Key, [.. PartsOf(editable.Key), .. moved]);
            }
        }

        return new Annotations<TTag>(header, legs);
    }

    private static Notes<TTag> Merge<TTag>(Notes<TTag> first, Notes<TTag> second)
        where TTag : notnull =>
        Merge([first, second]);

    /// <summary>
    /// Values merged in order: each distinct memo once (<see cref="AnnotationText.JoinDistinctMemos"/>)
    /// and the union of the tags.
    /// </summary>
    private static Notes<TTag> Merge<TTag>(IReadOnlyList<Notes<TTag>> parts)
        where TTag : notnull =>
        new(AnnotationText.JoinDistinctMemos(parts.Select(p => p.Memo)),
            AnnotationText.NormalizeTags(parts.SelectMany(p => p.Tags)));

    // ---------------------------------------------------------------------
    // The intent, over the transition's result
    // ---------------------------------------------------------------------

    private static Annotations<TTag> Overlay<TTag>(
        TxnShape shape, Annotations<TTag> baseline, AnnotationIntent<TTag> intent, TagLimit limit,
        List<AnnotationViolation> violations)
        where TTag : notnull
    {
        var header = baseline.Header;
        var legs = baseline.Legs.ToDictionary(p => p.Key, p => p.Value);
        if (shape.Legs.Count == 0) return Normalised(header, legs);
        var owner = shape.OriginatingAccountId!.Value;

        if (shape.HeaderHeld)
        {
            header = Apply(header, intent.Memo, intent.Tags, limit, violations);
            foreach (var line in intent.Lines ?? [])
            {
                if (shape.LegOn(line.PostingIndex, owner) is not { } leg)
                {
                    violations.Add(new(AnnotationViolationCodes.LineNotesInvalid,
                        $"There is no line {line.PostingIndex} on the account the transaction is split from."));
                    continue;
                }
                legs[leg.Key] = Apply(legs.GetValueOrDefault(leg.Key) ?? Notes<TTag>.Empty, line.Memo, line.Tags, limit, violations);
            }
        }
        else
        {
            // An event's value is its brokerage leg's; a plain one-split's is the
            // writing side's own.
            var target = shape.FirstLegOn(shape.IsEvent ? owner : intent.Side)!;
            legs[target.Key] = Apply(legs.GetValueOrDefault(target.Key) ?? Notes<TTag>.Empty, intent.Memo, intent.Tags, limit, violations);
        }

        return Normalised(header, legs);
    }

    private static Notes<TTag> Apply<TTag>(
        Notes<TTag> current, FieldEdit<string> memo, FieldEdit<IReadOnlyList<TTag>> tags, TagLimit limit,
        List<AnnotationViolation> violations)
        where TTag : notnull
    {
        var newMemo = memo.Kind switch
        {
            FieldEditKind.Set => AnnotationText.NormalizeMemo(memo.Value),
            FieldEditKind.Inherit => null,
            _ => current.Memo,
        };
        var newTags = tags.Kind switch
        {
            FieldEditKind.Set => AnnotationText.NormalizeTags(tags.Value ?? []),
            FieldEditKind.Inherit => [],
            _ => current.Tags,
        };

        // Refuse only GROWING a set past the limit. The baseline is what
        // the leg or header holds after the transition, so an unchanged or smaller
        // over-limit set (from a system merge or a collapse) saves.
        if (limit == TagLimit.Enforce && tags.Kind == FieldEditKind.Set
            && newTags.Count > MaxTagsPerSet && newTags.Count > AnnotationText.NormalizeTags(current.Tags).Count)
        {
            violations.Add(new(AnnotationViolationCodes.TagsTooMany,
                $"This tag set can't grow past {MaxTagsPerSet} tags."));
        }

        return new Notes<TTag>(newMemo, newTags);
    }

    private static Annotations<TTag> Normalised<TTag>(Notes<TTag> header, IReadOnlyDictionary<LegKey, Notes<TTag>> legs)
        where TTag : notnull
    {
        static Notes<TTag> Clean(Notes<TTag> n) =>
            new(AnnotationText.NormalizeMemo(n.Memo), AnnotationText.NormalizeTags(n.Tags));

        return new Annotations<TTag>(
            Clean(header),
            legs.Select(p => (p.Key, Notes: Clean(p.Value)))
                .Where(p => !p.Notes.IsEmpty)
                .ToDictionary(p => p.Key, p => p.Notes));
    }

    // ---------------------------------------------------------------------
    // Invariants: placement (no header notes on a one-split, nothing on an event's
    // investment side, no blank memo) and ownership
    // ---------------------------------------------------------------------

    /// <summary>The first invariant <paramref name="result"/> breaks on <paramref name="shape"/>, or null.</summary>
    private static string? Broken<TTag>(TxnShape shape, Annotations<TTag> result)
        where TTag : notnull
    {
        var keys = shape.Legs.Select(l => l.Key).ToHashSet();
        if (result.Legs.Keys.Any(k => !keys.Contains(k)))
            return "a value sits on a leg the transaction does not have";
        if (!shape.HeaderHeld && !result.Header.IsEmpty)
            return "a one-posting transaction has header notes";
        if (shape.IsEvent && shape.HeaderHeld && shape.Legs.Any(l => l.OnInvestmentSide && !result.For(l.Key).IsEmpty))
            return "an event's investment side carries a value";
        if (new[] { result.Header }.Concat(result.Legs.Values).Any(n => n.Memo is not null && string.IsNullOrWhiteSpace(n.Memo)))
            return "a blank memo is stored";

        var owner = shape.OriginatingAccountId;
        if (shape.Legs.Count == 0)
            return owner is null ? null : "a transaction without legs names an originating account";
        if (owner is not { } o || !shape.Has(o))
            return "the originating account is not on the transaction";
        if (shape.IsTransferShares)
            return shape.Legs.Any(l => l.Key.AccountId == o && IsEventBrokerage(shape, l))
                ? null : "an in-kind transfer is not owned by its source brokerage";
        if (shape.IsEvent)
            return shape.IsOnEveryPosting(o) && shape.Legs.Any(l => l.Key.AccountId == o && IsEventBrokerage(shape, l))
                ? null : "an event is not owned by a brokerage on every posting";
        if (shape.HeaderHeld && shape.Legs.Select(l => l.Key.AccountId).Distinct().Any(shape.IsOnEveryPosting)
            && !shape.IsOnEveryPosting(o))
            return "a multi-split is not owned by an account on every line";
        return null;
    }
}
