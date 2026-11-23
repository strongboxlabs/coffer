import { describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';

import { useRegisterFocusHome } from './useRegisterFocusHome';

// The register's focus home (ADR-0023 §L): focus lost as an editor closes or
// the selection clears goes to the row list; while the list is not on the page
// it goes to the add control, else the search box, and a list that comes onto
// the page takes it back while nothing else has.

/** A register in miniature: an editor that can close, and a list that can be absent. */
function Register({ listShown: initiallyShown, add = 'none', search = false }: {
    listShown: boolean;
    add?: 'none' | 'enabled' | 'disabled';
    search?: boolean;
}) {
    const [editing, setEditing] = useState(true);
    const [listShown, setListShown] = useState(initiallyShown);
    const [list, setList] = useState<HTMLDivElement | null>(null);
    useRegisterFocusHome(list, { editing, hasSelection: false });
    return (
        <>
            <button type="button" onClick={() => setListShown(true)}>Show the list</button>
            <button type="button" onClick={() => setListShown(false)}>Take the list away</button>
            {add !== 'none' ? (
                <button type="button" data-register-home="add" disabled={add === 'disabled'}>+ New transaction</button>
            ) : null}
            {search ? <input type="search" aria-label="Search transactions" data-register-home="search" /> : null}
            {editing ? (
                <button type="button" onClick={() => setEditing(false)}>Save</button>
            ) : null}
            {listShown ? <div ref={setList} role="grid" aria-label="Transactions" tabIndex={-1} /> : null}
        </>
    );
}

const closeTheEditor = () => {
    const save = screen.getByRole('button', { name: 'Save' });
    act(() => save.focus());
    act(() => save.click());
};
const grid = () => screen.getByRole('grid', { name: 'Transactions' });

describe('useRegisterFocusHome', () => {
    it('gives focus lost as the editor closes to the row list', () => {
        render(<Register listShown />);

        closeTheEditor();

        expect(grid()).toHaveFocus();
    });

    it('gives it to a list that comes onto the page later, when nothing has taken it since', () => {
        // An empty register shows no list until its first transaction is saved.
        render(<Register listShown={false} />);
        closeTheEditor();
        expect(document.activeElement).toBe(document.body);

        act(() => screen.getByRole('button', { name: 'Show the list' }).click());

        expect(grid()).toHaveFocus();
    });

    it('leaves focus that something took before the list came', () => {
        render(<Register listShown={false} />);
        closeTheEditor();
        const show = screen.getByRole('button', { name: 'Show the list' });
        act(() => show.focus());

        act(() => show.click());

        expect(show).toHaveFocus();
    });
});

// The real Virtuoso. The setup file mocks it with a list that draws every row in
// the page's own commit, which is not the order a browser sees: Virtuoso takes
// the page's row renderer in a layout effect and redraws its rows in a commit
// of its own, so an editor drawn as a row leaves the page after the commit that
// closed it.
const { Virtuoso } = await vi.importActual<typeof import('react-virtuoso')>('react-virtuoso');

/**
 * A register whose existing row is edited in place: the editor is drawn inside
 * the list by Virtuoso's row renderer, as both registers draw it. Given
 * `initialItemCount`, Virtuoso draws its rows with no layout to measure.
 */
function VirtualRegister() {
    const [editing, setEditing] = useState(true);
    const [list, setList] = useState<HTMLDivElement | null>(null);
    useRegisterFocusHome(list, { editing, hasSelection: false });
    const close = () => setEditing(false);
    return (
        <div ref={setList} role="grid" aria-label="Transactions" tabIndex={-1}>
            {list !== null ? (
                <Virtuoso
                    customScrollParent={list}
                    data={['edited', 'next']}
                    initialItemCount={2}
                    itemContent={(_, row) => (editing && row === 'edited' ? (
                        <div>
                            <input aria-label="Date" onKeyDown={(e) => { if (e.key === 'Escape') close(); }} />
                            <button type="button" onClick={close}>Cancel</button>
                        </div>
                    ) : (
                        <div>
                            <span>{`Row ${row}`}</span>
                            {/* A control of a row that stays, which a press outside the editor lands on. */}
                            {row === 'next' ? <button type="button" onClick={close}>Expand</button> : null}
                        </div>
                    ))}
                />
            ) : null}
        </div>
    );
}

describe('useRegisterFocusHome with an editor drawn as a row', () => {
    it('gives focus to the row list when Escape closes the editor', async () => {
        render(<VirtualRegister />);
        const date = await screen.findByRole('textbox', { name: 'Date' });
        act(() => date.focus());

        fireEvent.keyDown(date, { key: 'Escape' });
        await act(async () => {});

        expect(screen.getByText('Row edited')).toBeInTheDocument();
        expect(grid()).toHaveFocus();
    });

    it('gives it to the row list when a click on Cancel closes the editor', async () => {
        render(<VirtualRegister />);
        const cancel = await screen.findByRole('button', { name: 'Cancel' });
        act(() => cancel.focus());

        fireEvent.click(cancel);
        await act(async () => {});

        expect(screen.getByText('Row edited')).toBeInTheDocument();
        expect(grid()).toHaveFocus();
    });

    it('leaves focus on a control of a row that stays on the page', async () => {
        render(<VirtualRegister />);
        const expand = await screen.findByRole('button', { name: 'Expand' });
        act(() => expand.focus());

        fireEvent.click(expand);
        await act(async () => {});

        expect(screen.getByText('Row edited')).toBeInTheDocument();
        expect(expand).toHaveFocus();
    });
});

describe('useRegisterFocusHome while the list is not on the page', () => {
    it('gives focus to "+ New transaction" when the editor closes on an empty register', () => {
        render(<Register listShown={false} add="enabled" search />);

        closeTheEditor();

        expect(screen.getByRole('button', { name: '+ New transaction' })).toHaveFocus();
    });

    it('gives it to the search box when there is no add control, or it is disabled', () => {
        render(<Register listShown={false} add="disabled" search />);

        closeTheEditor();

        expect(screen.getByRole('searchbox', { name: 'Search transactions' })).toHaveFocus();
    });

    it('gives it to the home when the list leaves the page holding focus', () => {
        render(<Register listShown add="enabled" />);
        closeTheEditor();
        expect(grid()).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'Take the list away' }).click());

        expect(screen.getByRole('button', { name: '+ New transaction' })).toHaveFocus();
    });

    it('hands focus put on the home to the list when it comes back, if the reader pressed nothing', () => {
        render(<Register listShown={false} add="enabled" />);
        closeTheEditor();
        expect(screen.getByRole('button', { name: '+ New transaction' })).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'Show the list' }).click());

        expect(grid()).toHaveFocus();
    });

    it('leaves it on the home once the reader has pressed a key there', () => {
        render(<Register listShown={false} add="enabled" />);
        closeTheEditor();
        const add = screen.getByRole('button', { name: '+ New transaction' });
        expect(add).toHaveFocus();
        fireEvent.keyDown(add, { key: 'Shift' });

        act(() => screen.getByRole('button', { name: 'Show the list' }).click());

        expect(add).toHaveFocus();
    });
});
