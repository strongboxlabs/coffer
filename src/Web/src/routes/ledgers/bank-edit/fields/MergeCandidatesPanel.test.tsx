import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';

import type { MergeCandidateDto, SimilarPayeeDto } from '@/lib/types';

import { MergeCandidatesPanel } from './MergeCandidatesPanel';
import { SimilarPayeesPanel } from './SimilarPayeesPanel';

/**
 * The bank merge chip's tooltip names the survivor: the candidate (the manual
 * row), kept as it was entered. The edited feed row is the one folded away.
 *
 * Both hint strips (Merge candidates, Similar) take the touch size only in a
 * narrow editor dialog: jsdom lays nothing out, so the container-query classes
 * are what is checked here, and the sizes themselves are a browser check.
 */

const CATEGORY = '00000000-0000-0000-0000-000000000300';
const NARROW = '@max-3xl/dialog:';

const CANDIDATE: MergeCandidateDto = {
    headerId: '00000000-0000-0000-0000-000000000ccc', payee: 'Payee One',
    postedAt: '2026-01-05T00:00:00.000Z',
    postings: [{ counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A' }],
    origin: 'manual',
};

const SUGGESTION: SimilarPayeeDto = {
    payee: 'Payee Two', counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A',
    useCount: 2, lastUsedAt: '2026-01-01T00:00:00.000Z',
};

const EXPECTED =
    'Folds this feed row into your manual row of 2026-01-05, which is kept as you entered it. Click again to cancel.';
const EXPECTED_IMPORTED =
    'Folds this feed row into the imported row of 2026-01-05, which is kept as it is. Click again to cancel.';

function renderPanel(selectedHeaderId: string | null, dialog = false, candidate: MergeCandidateDto = CANDIDATE) {
    render(
        <MergeCandidatesPanel
            candidates={[candidate]}
            accountPaths={new Map([[CATEGORY, 'Category A']])}
            selectedHeaderId={selectedHeaderId}
            pending={false}
            dialog={dialog}
            onSelect={vi.fn()}
        />,
    );
    return screen.getByRole('button', { name: /Payee One/ });
}

function renderSimilar(dialog: boolean) {
    render(
        <SimilarPayeesPanel
            suggestions={[SUGGESTION]}
            accountPaths={new Map([[CATEGORY, 'Category A']])}
            pending={false}
            dialog={dialog}
            onApply={vi.fn()}
        />,
    );
    return screen.getByRole('button', { name: /Payee Two/ });
}

/** The touch-size classes a chip and its strip carry, whatever else they carry. */
const narrowClasses = (el: Element) => Array.from(el.classList).filter((c) => c.startsWith(NARROW));

describe('MergeCandidatesPanel', () => {
    it('says the manual row survives, before the chip is picked', () => {
        const chip = renderPanel(null);
        expect(chip).toHaveAttribute('aria-pressed', 'false');
        expect(chip).toHaveAttribute('title', EXPECTED);
    });

    it('says the same once it is picked', () => {
        const chip = renderPanel(CANDIDATE.headerId);
        expect(chip).toHaveAttribute('aria-pressed', 'true');
        expect(chip).toHaveAttribute('title', EXPECTED);
    });

    it.each(['online_import', 'file_import'])('says an imported row survives as it is, for a %s candidate', (origin) => {
        const chip = renderPanel(null, false, { ...CANDIDATE, origin });
        expect(chip).toHaveAttribute('title', EXPECTED_IMPORTED);
    });
});

describe.each([
    { strip: 'Merge candidates', render: (dialog: boolean) => renderPanel(null, dialog) },
    { strip: 'Similar', render: renderSimilar },
])('the $strip strip', ({ render: renderChip }) => {
    it('takes the touch size in a narrow dialog: a 32px chip and 12px text', () => {
        const chip = renderChip(true);
        expect(chip).toHaveClass(`${NARROW}min-h-control-32px`, `${NARROW}items-center`, `${NARROW}px-2`);
        expect(chip.parentElement).toHaveClass(`${NARROW}text-xs`, `${NARROW}gap-2`);
        // The compact chip is still the base, for the wide dialog.
        expect(chip).toHaveClass('px-1.5', 'py-0.5');
        expect(chip.parentElement).toHaveClass('text-[0.625rem]');
    });

    it('keeps the compact chip in the register', () => {
        const chip = renderChip(false);
        expect(chip).toHaveClass('px-1.5', 'py-0.5');
        expect(narrowClasses(chip)).toEqual([]);
        expect(narrowClasses(chip.parentElement!)).toEqual([]);
    });
});
