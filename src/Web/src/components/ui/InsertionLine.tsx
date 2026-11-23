// InsertionLine — where a dragged item will land: a 2px accent rule with a dot at
// its start. Extracted from the split editor's posting reorder so every drag in
// the app marks its drop the same way.

export function InsertionLine() {
    return (
        <div aria-hidden className="relative h-fixed-2px bg-accent">
            <span className="absolute -left-0.5 -top-0.5 size-fixed-8px rounded-full bg-accent" />
        </div>
    );
}
