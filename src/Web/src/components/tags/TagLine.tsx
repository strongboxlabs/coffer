import { cn } from '@/lib/cn';

import { useTagColor } from './TagColorsContext';

/**
 * A row's tags on ONE line: comma-separated names, each preceded by its palette
 * colour as a small stripe, truncated, with the count beside them.
 *
 * Replaces a wrapping strip of {@link TagChip}s. Chips are fine at two tags and
 * fall apart at eight — they wrap, so row height became a function of how many
 * tags a transaction carried and how long their names were. One register row
 * grew to three times its neighbours and the amount column stopped lining up,
 * which is the only reading pattern a register has. Moneydance users tag
 * heavily and names run to 50 characters, so that is the normal case here, not
 * an outlier.
 *
 * THE COLOUR IS A VERTICAL STRIPE, NOT THE TEXT. Vertical because horizontal
 * cost 7px plus its margin per tag, and at seven tags that is most of a chip's
 * worth of width in a field that has about 150px to give. A 2px rule before the
 * name costs almost nothing and reads as a marker rather than as a hyphen,
 * which a horizontal bar did inside a comma-separated list. The palette hexes are chip FILLS —
 * `tagChipStyle` pairs each with black or white by `readableOn`, so the label
 * stays legible whatever the hue. Nothing guarantees that for foreground text
 * on the row's own surface: slate `#64748b` on the dark theme's `#1e293b` is
 * about 2:1. A stripe puts the hex back in the role it was chosen for while the
 * name keeps the theme's text colour, so the palette survives the move off
 * chips without importing a contrast defect.
 *
 * A tag with no colour gets no stripe rather than a placeholder one: the stripe
 * means "this tag has a colour", and a grey stand-in would claim otherwise.
 *
 * THE 6px INSET IS THE LINE'S, NOT THE STRIPE'S. A category {@link Chip} is
 * `px-1.5`, so its text sits 6px into the cell. The stripe is 2px wide with a
 * 4px margin — also exactly 6px — so a coloured tag used to line up under the
 * chip by COINCIDENCE, with the stripe doing double duty as the inset. An
 * uncoloured tag rendered no stripe and started flush at 0, 6px adrift, which
 * in a register column reads as the whole row being crooked without saying
 * which element moved.
 *
 * The inset is `inset`'s job and the stripe is part of the tag, not part of the
 * margin: every tag begins at the same place, whether it begins with a stripe
 * or with its name. Pulling a leading stripe back out would align the names at
 * the cost of the tags themselves starting in two different places, which is
 * the same crookedness one level down.
 *
 * It is a PROP rather than a constant because the other caller is
 * {@link TagSelect}'s resting strip, which supplies its own padding — applying
 * both put the names 6px right of the placeholder that replaces them when the
 * line is empty.
 *
 * The count is the TOTAL, not a "+N hidden". How many names fit depends on the
 * column's pixel width, which this cannot know without measuring, and a "+2"
 * that is really "+4" is worse than no number. The title carries every name for
 * whatever the line cuts.
 */
export function TagLine(
    { tags, inset }: {
        tags: readonly string[];
        /**
         * Pad the names to a category {@link Chip}'s 6px text inset. Set where
         * the line is stacked UNDER such a chip in one register cell; left off
         * inside a control that already pads its own content.
         */
        inset?: boolean;
    },
) {
    if (tags.length === 0) return null;

    return (
        <span className="flex min-w-0 items-center gap-1" title={tags.join(', ')}>
            <span
                className={cn(
                    'min-w-0 truncate text-[0.6875rem] leading-[1.25] text-text-muted',
                    inset === true && 'pl-1.5',
                )}
            >
                {tags.map((tag, i) => (
                    <TaggedName
                        key={`${tag.toLowerCase()}_${i}`}
                        name={tag}
                        last={i === tags.length - 1}
                    />
                ))}
            </span>
            {tags.length > 1 ? (
                <span
                    aria-label={`${tags.length} tags`}
                    className="shrink-0 rounded bg-surface-hover px-1 font-mono text-[0.625rem] leading-[1.1rem] text-text-subtle"
                >
                    {tags.length}
                </span>
            ) : null}
        </span>
    );
}

/** One name, its colour stripe, and the separator that follows it. */
function TaggedName({ name, last }: { name: string; last: boolean }) {
    const color = useTagColor(name);
    return (
        <>
            {color === null ? null : (
                <span
                    aria-hidden
                    className="mr-1 inline-block h-[0.625rem] w-[2px] shrink-0 rounded-[1px] align-middle"
                    style={{ backgroundColor: color }}
                />
            )}
            {name}
            {last ? null : ', '}
        </>
    );
}
