import { useEffect, useRef, useState } from 'react';
import { describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { ContextMenu } from './ContextMenu';
import { Modal } from './Modal';
import { successorOf, useFocusWithinHandOff, useRowRemovalFocus } from './rowRemovalFocus';

// A row deleted or removed from its own menu or button takes that control with
// it (ADR-0023 §L). Focus goes to the same control on the next row, else the
// previous row, else the list's add control, never to the page.

interface ListProps {
    initial: string[];
    disabled?: string[];
    /** How the row's button takes it away: at once, or through a dialog. */
    via?: 'press' | 'dialog-then-remove' | 'remove-then-dialog' | 'remove-with-dialog' | 'menu';
    /** Given a way to take a row away as a list read again would, touching no focus. */
    handle?: { drop: (row: string) => void };
    /** The menu is drawn in its own element after the list, so React takes the
     *  row off the page before the menu closes. */
    menuInList?: boolean;
    /** The menu is drawn inside the row it can remove, after the row's
     *  control, so it goes with the row (a split's leg menu). */
    menuInRow?: boolean;
}

function List({ initial, disabled = [], via = 'press', handle, menuInList = false, menuInRow = false }: ListProps) {
    const [rows, setRows] = useState(initial);
    const [confirming, setConfirming] = useState<string | null>(null);
    const [menuFor, setMenuFor] = useState<string | null>(null);
    const addRef = useRef<HTMLButtonElement | null>(null);
    const control = useRowRemovalFocus(rows, addRef);
    const drop = (row: string) => setRows((r) => r.filter((x) => x !== row));
    useEffect(() => {
        if (handle) handle.drop = (row) => setRows((r) => r.filter((x) => x !== row));
    }, [handle]);
    const menu = menuFor !== null ? (
        <ContextMenu
            anchor={{ x: 0, y: 0 }}
            items={[{ id: 'remove', label: 'Remove', onSelect: () => drop(menuFor) }]}
            onClose={() => setMenuFor(null)}
        />
    ) : null;
    return (
        <>
            <button ref={addRef} type="button">Add</button>
            <ul>
                {rows.map((row) => (
                    <li key={row}>
                        <span>{row}</span>
                        <button
                            ref={control(row)}
                            type="button"
                            disabled={disabled.includes(row)}
                            onClick={() => {
                                if (via === 'press') drop(row);
                                else if (via === 'menu') setMenuFor(row);
                                else setConfirming(row);
                            }}
                        >
                            {`Remove ${row}`}
                        </button>
                        {menuInRow && menuFor === row ? menu : null}
                    </li>
                ))}
            </ul>
            <div>{menuInList ? menu : null}</div>
            <button type="button">Elsewhere</button>
            {confirming !== null ? (
                <Modal open onClose={() => setConfirming(null)} ariaLabel="Confirm">
                    <button
                        type="button"
                        onClick={() => {
                            if (via === 'dialog-then-remove') {
                                setConfirming(null);
                            } else if (via === 'remove-then-dialog') {
                                drop(confirming);
                            } else {
                                drop(confirming);
                                setConfirming(null);
                            }
                        }}
                    >
                        Yes
                    </button>
                    <button type="button" onClick={() => setConfirming(null)}>Close</button>
                    <button type="button" onClick={() => drop(confirming)}>Drop behind</button>
                </Modal>
            ) : null}
            {menuInList || menuInRow ? null : menu}
        </>
    );
}

const removeOf = (row: string) => screen.queryByRole('button', { name: `Remove ${row}` });

describe('a row whose control held focus as it went', () => {
    it('gives focus to the next row\'s control', async () => {
        render(<List initial={['a', 'b', 'c']} />);
        await userEvent.click(removeOf('b')!);
        expect(removeOf('b')).toBeNull();
        expect(removeOf('c')).toHaveFocus();
    });

    it('gives it to the previous row\'s when the last row goes', async () => {
        render(<List initial={['a', 'b', 'c']} />);
        await userEvent.click(removeOf('c')!);
        expect(removeOf('b')).toHaveFocus();
    });

    it('gives it to the add control when no row is left', async () => {
        render(<List initial={['a']} />);
        await userEvent.click(removeOf('a')!);
        expect(screen.getByRole('button', { name: 'Add' })).toHaveFocus();
    });

    it('passes over a row whose control is disabled', async () => {
        render(<List initial={['a', 'b', 'c', 'd']} disabled={['c']} />);
        await userEvent.click(removeOf('b')!);
        expect(removeOf('d')).toHaveFocus();
    });

    it('leaves focus the user has put somewhere else', () => {
        const handle = { drop: (_row: string) => {} };
        render(<List initial={['a', 'b']} via="dialog-then-remove" handle={handle} />);
        const elsewhere = screen.getByRole('button', { name: 'Elsewhere' });
        elsewhere.focus();
        act(() => handle.drop('a'));
        expect(removeOf('a')).toBeNull();
        expect(elsewhere).toHaveFocus();
    });
});

describe('a row taken away through a dialog it opened', () => {
    it('hands focus on when the dialog gives it back before the row goes', async () => {
        const handle = { drop: (_row: string) => {} };
        render(<List initial={['a', 'b', 'c']} via="dialog-then-remove" handle={handle} />);
        await userEvent.click(removeOf('b')!);
        await userEvent.click(screen.getByRole('button', { name: 'Yes' }));
        // The dialog gave focus back to the row's control, still on the page.
        expect(removeOf('b')).toHaveFocus();
        // The list is read again, and the row goes.
        act(() => handle.drop('b'));
        expect(removeOf('b')).toBeNull();
        expect(removeOf('c')).toHaveFocus();
    });

    it('hands focus on when the row goes while the dialog is open, as the dialog closes', async () => {
        render(<List initial={['a', 'b', 'c']} via="remove-then-dialog" />);
        await userEvent.click(removeOf('b')!);
        await userEvent.click(screen.getByRole('button', { name: 'Yes' }));
        expect(removeOf('b')).toBeNull();
        await userEvent.click(screen.getByRole('button', { name: 'Close' }));
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(removeOf('c')).toHaveFocus();
    });

    it('hands focus on when the row goes in the render that closes the dialog', async () => {
        render(<List initial={['a', 'b']} via="remove-with-dialog" />);
        await userEvent.click(removeOf('b')!);
        await userEvent.click(screen.getByRole('button', { name: 'Yes' }));
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(removeOf('a')).toHaveFocus();
    });
});

describe('a row taken away from its own menu', () => {
    it('hands focus on when the menu gives it back before the row goes', async () => {
        render(<List initial={['a', 'b', 'c']} via="menu" />);
        await userEvent.click(removeOf('a')!);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Remove' }));
        expect(removeOf('a')).toBeNull();
        expect(removeOf('b')).toHaveFocus();
    });

    it('hands focus on as the menu closes when the row went first', async () => {
        render(<List initial={['a', 'b', 'c']} via="menu" menuInList />);
        await userEvent.click(removeOf('a')!);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Remove' }));
        expect(removeOf('a')).toBeNull();
        expect(removeOf('b')).toHaveFocus();
    });

    // The menu goes with the row: React takes the row's control away first,
    // then closes the menu while the row is still on the page, then takes the
    // row off it. The menu asks for the successor before giving focus back to
    // an opener that is about to go.
    it('hands focus on when the menu is drawn inside the row it removes', async () => {
        render(<List initial={['a', 'b', 'c']} via="menu" menuInRow />);
        await userEvent.click(removeOf('b')!);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Remove' }));
        expect(removeOf('b')).toBeNull();
        expect(removeOf('c')).toHaveFocus();
    });

    it('hands focus to the previous row from a menu inside the last row', async () => {
        render(<List initial={['a', 'b', 'c']} via="menu" menuInRow />);
        await userEvent.click(removeOf('c')!);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Remove' }));
        expect(removeOf('c')).toBeNull();
        expect(removeOf('b')).toHaveFocus();
    });

    it('gives focus back to the opener from a menu inside a row it leaves in place', async () => {
        render(<List initial={['a', 'b']} via="menu" menuInRow />);
        await userEvent.click(removeOf('a')!);
        await screen.findByRole('menuitem', { name: 'Remove' });
        await userEvent.keyboard('{Escape}');
        expect(screen.queryByRole('menu')).toBeNull();
        expect(removeOf('a')).toHaveFocus();
    });
});

/** Rows that each carry, beside their control, a companion holding a link of its own. */
function RowsWithLinks({ handle }: { handle: { drop: (row: string) => void } }) {
    const [rows, setRows] = useState(['a', 'b', 'c']);
    const addRef = useRef<HTMLButtonElement | null>(null);
    const control = useRowRemovalFocus(rows, addRef);
    useEffect(() => {
        handle.drop = (row) => setRows((r) => r.filter((x) => x !== row));
    }, [handle]);
    return (
        <>
            <button ref={addRef} type="button">Add</button>
            {rows.map((row) => (
                <div key={row}>
                    <ul ref={control(row, 'companion')}>
                        <li><a href={`#${row}`}>{`Link ${row}`}</a></li>
                    </ul>
                    <button ref={control(row)} type="button">{`Remove ${row}`}</button>
                </div>
            ))}
            <button type="button">Elsewhere</button>
        </>
    );
}

describe('a companion that holds controls of its own', () => {
    it('hands focus on to the next row\'s control from a control inside it', () => {
        const handle = { drop: (_row: string) => {} };
        render(<RowsWithLinks handle={handle} />);
        screen.getByRole('link', { name: 'Link b' }).focus();
        act(() => handle.drop('b'));
        expect(screen.queryByRole('link', { name: 'Link b' })).toBeNull();
        expect(removeOf('c')).toHaveFocus();
    });

    it('leaves focus the user has put somewhere else', () => {
        const handle = { drop: (_row: string) => {} };
        render(<RowsWithLinks handle={handle} />);
        screen.getByRole('link', { name: 'Link c' }).focus();
        act(() => handle.drop('b'));
        expect(screen.getByRole('link', { name: 'Link c' })).toHaveFocus();
    });
});

/** A part of the page holding controls of its own, which can be taken away whole. */
function Region({ handle }: { handle: { hide: () => void } }) {
    const [shown, setShown] = useState(true);
    const nextRef = useRef<HTMLButtonElement | null>(null);
    const regionRef = useFocusWithinHandOff(() => nextRef.current);
    useEffect(() => {
        handle.hide = () => setShown(false);
    }, [handle]);
    return (
        <>
            <button ref={nextRef} type="button">Next step</button>
            {shown ? (
                <div ref={regionRef}>
                    <button type="button">Inside</button>
                    <a href="#inside">Link inside</a>
                </div>
            ) : null}
            <button type="button">Elsewhere</button>
        </>
    );
}

describe('useFocusWithinHandOff', () => {
    it.each(['Inside', 'Link inside'])('hands focus on from %s when the part goes', (name) => {
        const handle = { hide: () => {} };
        render(<Region handle={handle} />);
        const inside = screen.getByText(name);
        inside.focus();
        expect(inside).toHaveFocus();
        act(() => handle.hide());
        expect(screen.queryByText(name)).toBeNull();
        expect(screen.getByRole('button', { name: 'Next step' })).toHaveFocus();
    });

    it('leaves focus the user has put somewhere else', () => {
        const handle = { hide: () => {} };
        render(<Region handle={handle} />);
        const elsewhere = screen.getByRole('button', { name: 'Elsewhere' });
        elsewhere.focus();
        act(() => handle.hide());
        expect(elsewhere).toHaveFocus();
    });
});

describe('successorOf', () => {
    it('knows nothing of a control no list took away', () => {
        expect(successorOf(document.createElement('button'))).toBeNull();
    });
});
