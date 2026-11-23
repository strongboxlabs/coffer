import { afterEach, describe, it, expect, vi } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';

import { StyleGuidePage } from './StyleGuidePage';

/** A section of the page, by its heading. */
const sectionOf = (name: RegExp) => screen.getByRole('heading', { level: 2, name }).closest('section')!;
const memoTagsSection = () => sectionOf(/Memo and tags per side/);
const currenciesSection = () => sectionOf(/Currency decimals and transfers between currencies/);

// Smoke test for the dev-only style-guide route. We assert it renders
// without throwing and surfaces the key sections — the page is the
// audit surface for the ADR-0021 design tokens, so a render regression
// here means the tokens broke.

describe('StyleGuidePage', () => {
    afterEach(() => { vi.unstubAllGlobals(); });

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
            /Segmented control/,
            /Mark bar/,
            /Skeleton/,
            /Chart bars and point selection/,
            /Drag handle and insertion line/,
            /Editor dialog/,
            /Currency decimals and transfers between currencies/,
            /Category chips/,
            /Chart shades/,
        ]) {
            expect(
                screen.getByRole('heading', { level: 2, name }),
            ).toBeInTheDocument();
        }
    });

    it('draws Cleared with the register\'s own badge, in the accent, and labels green with what it marks', () => {
        render(<StyleGuidePage />);
        const section = screen.getByRole('heading', { level: 2, name: 'Register row preview' }).closest('section')!;
        const cleared = within(section).getByRole('img', { name: 'Cleared' });
        expect(cleared.className).toContain('bg-accent-soft');
        expect(cleared.className).not.toMatch(/state-success/);
        // The success swatch says what green is for, a status, and not Cleared.
        const states = screen.getByRole('heading', { level: 2, name: /Status & semantic states/ }).closest('section')!;
        expect(within(states).getByText('✓ Succeeded').className).toContain('text-state-success');
        expect(within(states).queryByText(/Cleared/)).toBeNull();
    });

    it('shows every button size and variant, the in-row xs and link included', () => {
        render(<StyleGuidePage />);
        for (const name of ['Danger', 'Extra small', 'Post or skip…']) {
            expect(screen.getByRole('button', { name })).toBeInTheDocument();
        }
        expect(screen.getByRole('button', { name: 'Post or skip…' }).className).toContain('h-control-28px');
    });

    it('lands every sidebar link on a section of the page', () => {
        const { container } = render(<StyleGuidePage />);
        const hrefs = [...container.querySelectorAll('a[href^="#"]')].map((a) => a.getAttribute('href')!);

        // Guard the guard: the nav has links, so finding none means the query broke.
        expect(hrefs.length).toBeGreaterThan(5);

        const dead = hrefs.filter((h) => container.querySelector(h) === null);
        expect(dead).toEqual([]);
    });

    it('renders the memo-and-tags samples: own, inherited, read-only and the editor states', () => {
        render(<StyleGuidePage />);

        expect(screen.getByRole('heading', { level: 2, name: /Memo and tags per side/ })).toBeInTheDocument();
        const memoTags = within(memoTagsSection());
        // Inherited values carry the marker naming the account they come from.
        expect(memoTags.getAllByRole('img', { name: 'From Checking' }).length).toBeGreaterThan(0);
        expect(memoTags.getAllByRole('img', { name: 'From Brokerage' }).length).toBeGreaterThan(0);
        // Read-only rows are badged.
        expect(memoTags.getAllByText('↗ Split').length).toBeGreaterThan(0);
        expect(memoTags.getAllByText('↗ Investment').length).toBeGreaterThan(0);
        // The editor states: inheriting, own over inherited (the label alone,
        // with no control to go back), and the split captions.
        expect(memoTags.getAllByText('from Checking').length).toBeGreaterThan(0);
        expect(memoTags.getByDisplayValue('Client lunch')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^Use\b/i })).not.toBeInTheDocument();
        // Two split editors: without a hint, and with one.
        expect(memoTags.getAllByText('Memo (whole transaction)')).toHaveLength(2);
        expect(memoTags.getAllByText('Tags (whole transaction)')).toHaveLength(2);
        // The hints under Payee, from the seeded placeholder hits.
        expect(memoTags.getByText('Similar:')).toBeInTheDocument();
        expect(memoTags.getAllByText('Merge candidates:')).toHaveLength(2);
    });

    describe('the currency decimals and transfers between currencies samples (ADR-0114 D13, D14, D15)', () => {
        /** One sample cell of the money-places table, by currency and kind. */
        const placesCell = (currency: string, kind: 'amount' | 'signed') =>
            currenciesSection().querySelector(`[data-places-sample="${currency}"] [data-sample="${kind}"]`)!.textContent;

        it('prints a JPY figure with no fraction and a KWD figure at three places', () => {
            render(<StyleGuidePage />);

            // Read off the rendered cells, never through lib/money, so a formatter
            // pinned to two places again fails here rather than agreeing with itself.
            expect(placesCell('JPY', 'amount')).toBe('¥4,820');
            expect(placesCell('JPY', 'signed')).toBe('-¥4,820');
            expect(placesCell('KWD', 'amount')).toMatch(/^KWD\s18\.750$/);
            expect(placesCell('KWD', 'signed')).toMatch(/^-KWD\s18\.750$/);
            expect(placesCell('USD', 'amount')).toBe('$4,820.50');
        });

        it('shows the money input\'s symbol before the figure and after it, and each currency\'s step', () => {
            render(<StyleGuidePage />);
            const inputs = within(currenciesSection().querySelector('#money-inputs') as HTMLElement);
            const affixOf = (name: string) =>
                inputs.getByRole('spinbutton', { name }).parentElement!.querySelector('[data-money-affix]');

            expect(affixOf('USD · symbol before')).toHaveAttribute('data-money-affix', 'before');
            expect(affixOf('USD · symbol before')).toHaveTextContent('$');
            expect(affixOf('EUR, de-DE · symbol after')).toHaveAttribute('data-money-affix', 'after');
            expect(affixOf('EUR, de-DE · symbol after')).toHaveTextContent('€');
            expect(affixOf('CAD · a wide symbol')).toHaveTextContent('CA$');
            // Anchored on the input, which is there: no symbol beside it.
            expect(inputs.getByRole('spinbutton', { name: 'Currency not known yet · no symbol' })).toHaveAttribute('step', 'any');
            expect(affixOf('Currency not known yet · no symbol')).toBeNull();

            expect(inputs.getByRole('spinbutton', { name: 'JPY · whole units, step 1' })).toHaveAttribute('step', '1');
            expect(inputs.getByRole('spinbutton', { name: 'JPY · whole units, step 1' })).toHaveValue(4820);
            expect(inputs.getByRole('spinbutton', { name: 'KWD · three places, step 0.001' })).toHaveAttribute('step', '0.001');
            expect(inputs.getByRole('spinbutton', { name: 'KWD · three places, step 0.001' })).toHaveAttribute('placeholder', '0.000');
            expect(inputs.getByRole('spinbutton', { name: 'Price in USD · any step, never rounded' })).toHaveAttribute('step', 'any');
        });

        it('shows the other leg\'s amount on both sides of a transfer between currencies, and a category register\'s amounts at each paying currency\'s places', () => {
            render(<StyleGuidePage />);
            const section = currenciesSection();

            const otherLegs = [...section.querySelectorAll('[data-other-leg]')].map((el) => el.textContent);
            expect(otherLegs).toEqual(['€90.00', '-$100.00']);
            // The category register's rows, each in its paying account's currency.
            const register = within(section.querySelector('#category-register') as HTMLElement);
            expect(register.getByText('¥4,820')).toBeInTheDocument();
            expect(register.getByText(/^KWD\s18\.750$/)).toBeInTheDocument();
            expect(register.getByText('$42.18')).toBeInTheDocument();
        });

        it('shows the second amount prefilled, as stored, and with one or both rate legs missing, each with its rate line', async () => {
            render(<StyleGuidePage />);
            const section = within(currenciesSection());

            // Prefilled from the day's rate, in the one-line editor and on a split line.
            const received = section.getByRole('spinbutton', { name: 'Amount received in Test EUR Bank, in EUR' });
            await waitFor(() => expect(received).toHaveValue(90));
            expect(section.getAllByText('Received').length).toBeGreaterThan(0);
            expect(section.getAllByText('Rate for Nov 12, 2026: 1 USD = 0.9000 EUR')).toHaveLength(2);
            await waitFor(() => expect(section.getByRole('spinbutton', { name: 'Posting 2: Amount received in Test EUR Bank, in EUR' })).toHaveValue(90));

            // Opened from the other side: Sent, the figure negative, the line the ratio.
            expect(section.getByRole('spinbutton', { name: 'Amount sent from Test USD Bank, in USD' })).toHaveValue(-100);
            expect(section.getByText('Sent')).toBeInTheDocument();
            expect(section.getByText(/^1 EUR = 1\.1111\d* USD, from the two amounts$/)).toBeInTheDocument();

            // No rate: the field is empty, and the line names each missing leg.
            const unrated = section.getAllByRole('spinbutton', { name: 'Amount received in Test KWD Bank, in KWD' });
            expect(unrated).toHaveLength(2);
            for (const field of unrated) expect(field).toHaveValue(null);
            expect(section.getByText('No KWD to USD rate for Nov 12, 2026')).toBeInTheDocument();
            expect(section.getByText('No CAD or KWD to USD rate for Nov 12, 2026')).toBeInTheDocument();
        });

        it('shows the Review editor\'s rounded-at-import note as the bank sent the figure, under a whole-yen Amount', () => {
            render(<StyleGuidePage />);
            const section = within(currenciesSection());

            const note = section.getByText('Rounded from 1,234.5 as sent by the bank');
            const amount = section.getByDisplayValue('1235');
            expect(amount).toHaveAttribute('step', '1');
            expect(amount).toHaveAccessibleDescription('Rounded from 1,234.5 as sent by the bank');
            expect(note).toBeInTheDocument();
        });
    });

    it('sends no request: every sample, the real editors included, renders offline', async () => {
        const fetchMock = vi.fn((input: RequestInfo | URL) => Promise.reject(new Error(`the style guide fetched ${String(input)}`)));
        vi.stubGlobal('fetch', fetchMock);

        render(<StyleGuidePage />);

        // Anchor on the last editor sample of each section, so every editor's queries
        // have mounted: the memo-and-tags split editors, and the currency samples'
        // split line with its derived rate.
        expect(await within(memoTagsSection()).findAllByText('Tags (whole transaction)')).toHaveLength(2);
        expect(await within(currenciesSection()).findAllByText('Rate for Nov 12, 2026: 1 USD = 0.9000 EUR')).toHaveLength(2);
        // A query starts its fetch from an effect after mount; give it the turn.
        await act(async () => { await new Promise((r) => setTimeout(r, 50)); });

        expect(fetchMock.mock.calls.map(([input]) => String(input))).toEqual([]);
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
