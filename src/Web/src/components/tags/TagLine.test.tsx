import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

import { TagLine } from './TagLine';
import { TagColorsValueProvider } from './TagColorsContext';

// The register stacks a category Chip over a TagLine in one cell. The chip is
// px-1.5, so its text sits 6px in, and the line has to match that inset whether
// or not its first tag carries a colour. It did not: the colour stripe (2px
// plus a 4px margin = 6px) was the only thing providing it, so an uncoloured
// tag started flush against the cell edge and the column read as crooked.
function renderLine(
    tags: readonly string[],
    colors: ReadonlyArray<{ name: string; color: string | null }>,
) {
    return render(
        <TagColorsValueProvider colors={colors}>
            <TagLine tags={tags} inset />
        </TagColorsValueProvider>,
    );
}

/** The element holding the names — the one that carries the inset. */
function namesOf(container: HTMLElement): HTMLElement {
    const el = container.querySelector<HTMLElement>('span > span');
    expect(el).not.toBeNull();
    return el!;
}

describe('the left inset matches the category chip above it', () => {
    it('insets the names when the first tag has no colour', () => {
        const { container } = renderLine(['alpha'], [{ name: 'alpha', color: null }]);

        expect(screen.getByTitle('alpha')).toBeInTheDocument();
        // No stripe to supply the inset, so the line must carry it itself.
        expect(container.querySelectorAll('[aria-hidden]')).toHaveLength(0);
        expect(namesOf(container).className).toContain('pl-1.5');
    });

    it('starts a coloured tag at the same inset, stripe included', () => {
        const { container } = renderLine(['alpha'], [{ name: 'alpha', color: '#64748b' }]);

        expect(namesOf(container).className).toContain('pl-1.5');

        // The stripe is part of the tag, not part of the margin. Pulling it
        // back out would line the NAMES up at the cost of the tags themselves
        // beginning in two different places.
        const stripes = container.querySelectorAll<HTMLElement>('[aria-hidden]');
        expect(stripes).toHaveLength(1);
        expect(stripes[0]!.className).not.toContain('-ml-');
    });

    it('leaves the inset off where the container already pads its content', () => {
        // TagSelect's resting strip is px-2. Both would put the names 6px right
        // of the placeholder that replaces them when the line is empty.
        const { container } = render(
            <TagColorsValueProvider colors={[{ name: 'alpha', color: null }]}>
                <TagLine tags={['alpha']} />
            </TagColorsValueProvider>,
        );
        expect(screen.getByTitle('alpha')).toBeInTheDocument();
        expect(namesOf(container).className).not.toContain('pl-1.5');
    });

    it('treats every stripe in the line alike', () => {
        const { container } = renderLine(
            ['alpha', 'beta'],
            [{ name: 'alpha', color: '#64748b' }, { name: 'beta', color: '#64748b' }],
        );

        const stripes = container.querySelectorAll<HTMLElement>('[aria-hidden]');
        expect(stripes).toHaveLength(2);
        expect(stripes[0]!.className).toBe(stripes[1]!.className);
    });
});

// ---------------------------------------------------------------------------
// Dimension 25 of the fixture audit: 8+ tags with 50-character names.
//
// This is the case the component was BUILT for — its own doc comment says
// chips "fall apart at eight" and that 50-character names are "the normal case
// here, not an outlier" — and until the demo sample grew one, no fixture and no
// test had ever produced a row with more than a single tag.
//
// What actually matters is that ROW HEIGHT does not grow with tag count: the
// defect that killed chips was one register row standing three times as tall as
// its neighbours, which breaks the only reading pattern a register has. jsdom
// has no layout engine, so a height assertion here would pass against anything
// and prove nothing. These assert the STRUCTURE that delivers the height
// instead — one truncating line box whatever the count — and leave pixels to
// the browser harness.
const LONG = [
    'Rail fare booked through the corporate travel desk',
    'Advance purchase - non refundable after departure',
    'Carbon offset purchased with this ticket at source',
    'Split across two cost centres by prior arrangement',
    'Claimed at the standard mileage equivalent instead',
    'Paid on the travel card rather than the joint card',
    'Receipt emailed',
    'Rail',
];

/** Half coloured, half not — a real row is mixed, and an uncoloured tag takes
 *  a different render path (no stripe). */
const MIXED = LONG.map((name, i) => ({
    name,
    color: i % 2 === 0 ? '#64748b' : null,
}));

describe('eight tags with 50-character names', () => {
    it('puts every name in ONE truncating line box, not one box per tag', () => {
        const { container } = render(
            <TagColorsValueProvider colors={MIXED}>
                <TagLine tags={LONG} inset />
            </TagColorsValueProvider>,
        );

        // The height guarantee, as far as a DOM can state it. Eight chips would
        // be eight independently-wrapping boxes; this is one line that clips.
        const lines = container.querySelectorAll('.truncate');
        expect(lines).toHaveLength(1);
        expect(lines[0]!.className).toContain('min-w-0');

        // All eight names are IN that one box — truncation is visual, so
        // nothing may be dropped from the DOM to make the line fit.
        for (const name of LONG) {
            expect(lines[0]!.textContent).toContain(name);
        }
    });

    it('does not grow a line box per tag as tags are added', () => {
        // The discriminating comparison. A single-count assertion above would
        // still pass if the component rendered one box per tag and happened to
        // have one tag; this pins that going from 1 to 8 adds no boxes.
        const one = render(
            <TagColorsValueProvider colors={MIXED}>
                <TagLine tags={[LONG[0]!]} inset />
            </TagColorsValueProvider>,
        );
        const atOne = one.container.querySelectorAll('.truncate').length;
        one.unmount();

        const { container } = render(
            <TagColorsValueProvider colors={MIXED}>
                <TagLine tags={LONG} inset />
            </TagColorsValueProvider>,
        );
        expect(container.querySelectorAll('.truncate')).toHaveLength(atOne);
    });

    it('counts the total rather than claiming a "+N hidden"', () => {
        render(
            <TagColorsValueProvider colors={MIXED}>
                <TagLine tags={LONG} inset />
            </TagColorsValueProvider>,
        );

        // How many names fit depends on the column's pixel width, which the
        // component cannot know without measuring — so the badge is the TOTAL.
        // A "+2" that is really "+4" is worse than no number at all.
        const badge = screen.getByLabelText(`${LONG.length} tags`);
        expect(badge.textContent).toBe(String(LONG.length));
    });

    it('keeps every name reachable through the title when the line clips', () => {
        render(
            <TagColorsValueProvider colors={MIXED}>
                <TagLine tags={LONG} inset />
            </TagColorsValueProvider>,
        );

        // 8 names averaging 44 characters cannot fit a register's category
        // column, so the title is the only thing that answers "which tags?".
        expect(screen.getByTitle(LONG.join(', '))).toBeInTheDocument();
    });

    it('gives a stripe to the coloured tags only', () => {
        const { container } = render(
            <TagColorsValueProvider colors={MIXED}>
                <TagLine tags={LONG} inset />
            </TagColorsValueProvider>,
        );

        // An uncoloured tag gets no placeholder stripe — the stripe means
        // "this tag has a colour" and a grey stand-in would claim otherwise.
        expect(container.querySelectorAll('[aria-hidden]')).toHaveLength(
            MIXED.filter((c) => c.color !== null).length,
        );
    });
});
