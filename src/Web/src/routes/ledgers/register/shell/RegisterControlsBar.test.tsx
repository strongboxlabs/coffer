import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { RegisterFilterArgs } from '@/lib/api/register';
import { RegisterControlsBar } from './RegisterControlsBar';
import { DEFAULT_SORT } from './registerSort';

// The chips row under the register's controls. Removing a chip removes the
// button that held focus, and Clear all or the last chip removes the row, so
// focus goes on to the control that takes its place (ADR-0023 §L): the next
// chip, else Clear all, and with the row gone, the search box. Left on the
// page, it would sit outside every control of the register's header.

function Bar({ initial }: { initial: RegisterFilterArgs }) {
    const [filter, setFilter] = useState<RegisterFilterArgs>(initial);
    return (
        <RegisterControlsBar
            statusFilter="all"
            onStatusFilterChange={() => {}}
            sort={DEFAULT_SORT}
            onSortChange={() => {}}
            isInvestment={false}
            filter={filter}
            onFilterChange={setFilter}
            accounts={[]}
            currency="USD"
            tags={[]}
            resultCount={3}
            statusCounts={null}
            onNew={() => {}}
            newDisabled={false}
            hideNew
        />
    );
}

async function press(user: ReturnType<typeof userEvent.setup>, button: HTMLElement) {
    act(() => button.focus());
    await user.keyboard('{Enter}');
}

describe('RegisterControlsBar filter chips, where focus goes', () => {
    it('moves focus to the next chip when a chip is removed', async () => {
        const user = userEvent.setup();
        render(<Bar initial={{ search: 'abc', tag: 'Tag A' }} />);

        await press(user, screen.getByRole('button', { name: '"abc"' }));

        expect(screen.queryByRole('button', { name: '"abc"' })).toBeNull();
        expect(screen.getByRole('button', { name: 'Tag: Tag A' })).toHaveFocus();
    });

    it('moves focus to Clear all when the last of several chips is removed', async () => {
        const user = userEvent.setup();
        render(<Bar initial={{ search: 'abc', tag: 'Tag A' }} />);

        await press(user, screen.getByRole('button', { name: 'Tag: Tag A' }));

        expect(screen.queryByRole('button', { name: 'Tag: Tag A' })).toBeNull();
        expect(screen.getByRole('button', { name: 'Clear all' })).toHaveFocus();
    });

    it('moves focus to the search box when the only chip is removed', async () => {
        const user = userEvent.setup();
        render(<Bar initial={{ tag: 'Tag A' }} />);

        await press(user, screen.getByRole('button', { name: 'Tag: Tag A' }));

        expect(screen.queryByRole('button', { name: 'Clear all' })).toBeNull();
        expect(screen.getByRole('searchbox', { name: 'Search transactions' })).toHaveFocus();
    });

    it('moves focus to the search box on Clear all', async () => {
        const user = userEvent.setup();
        render(<Bar initial={{ search: 'abc', tag: 'Tag A' }} />);

        await press(user, screen.getByRole('button', { name: 'Clear all' }));

        expect(screen.queryByRole('button', { name: 'Clear all' })).toBeNull();
        expect(screen.getByRole('searchbox', { name: 'Search transactions' })).toHaveFocus();
    });
});
