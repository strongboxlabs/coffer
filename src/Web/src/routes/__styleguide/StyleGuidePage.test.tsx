import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

import { StyleGuidePage } from './StyleGuidePage';

// Smoke test for the dev-only style-guide route. We assert it renders
// without throwing and surfaces the key sections — the page is the
// audit surface for the ADR-0021 design tokens, so a render regression
// here means the tokens broke.

describe('StyleGuidePage', () => {
    it('renders all token sections', () => {
        render(<StyleGuidePage />);

        expect(
            screen.getByRole('heading', { level: 1, name: /style guide/i }),
        ).toBeInTheDocument();
        // Sections share text with sidebar nav links (Typography,
        // Buttons, etc.) — query by heading role + level=2 to
        // disambiguate.
        for (const name of [
            /Surfaces/,
            /Accent/,
            /Typography/,
            /Buttons/,
            /Category chips/,
        ]) {
            expect(
                screen.getByRole('heading', { level: 2, name }),
            ).toBeInTheDocument();
        }
    });

    it('renders all ten category chips', () => {
        render(<StyleGuidePage />);

        for (const label of [
            'Groceries',
            'Dining',
            'Housing',
            'Utilities',
            'Subscriptions',
            'Transport',
            'Salary',
            'Transfer',
            'Phone',
            'Recreation',
        ]) {
            expect(
                screen.getAllByText(label).length,
                `chip label "${label}" should render`,
            ).toBeGreaterThan(0);
        }
    });
});
