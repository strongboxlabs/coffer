import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';

import { ContextMenu, type ContextMenuItem } from './ContextMenu';

// Behaviour we lock down for the ContextMenu primitive:
//
//   * Items render with their label + optional shortcut hint.
//   * ↓/↑ move highlight; wraps at ends; skips disabled items.
//   * Enter / Space activate the highlighted item and close the menu
//     (so the caller's onClose fires + the item's onSelect fires).
//   * Escape closes without activating, and gives focus back to where it
//     was when the menu opened.
//   * Outside pointerdown closes the menu.
//   * Clicking a disabled item is a no-op.

function Harness({
    items,
    initialOpen = true,
}: {
    items: readonly ContextMenuItem[];
    initialOpen?: boolean;
}) {
    const [open, setOpen] = useState(initialOpen);
    return (
        <div>
            {open ? (
                <ContextMenu
                    anchor={{ x: 50, y: 50 }}
                    items={items}
                    onClose={() => setOpen(false)}
                />
            ) : null}
            <button type="button" data-testid="outside">outside</button>
            <span data-testid="open-state">{open ? 'open' : 'closed'}</span>
        </div>
    );
}

describe('ContextMenu', () => {
    it('renders each item label and shortcut hint', () => {
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'Duplicate', shortcutHint: '⌘D', onSelect: () => {} },
            { id: 'b', label: 'Delete', danger: true, onSelect: () => {} },
        ];
        render(<Harness items={items} />);
        expect(screen.getByRole('menuitem', { name: /Duplicate/ })).toBeInTheDocument();
        expect(screen.getByText('⌘D')).toBeInTheDocument();
        expect(screen.getByRole('menuitem', { name: /Delete/ })).toBeInTheDocument();
    });

    it('activates the highlighted item on Enter and fires onSelect + closes', async () => {
        const user = userEvent.setup();
        const duplicate = vi.fn();
        const del = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'Duplicate', onSelect: duplicate },
            { id: 'b', label: 'Delete', onSelect: del },
        ];
        render(<Harness items={items} />);
        // First item is highlighted by default; ArrowDown moves to second.
        await user.keyboard('{ArrowDown}');
        await user.keyboard('{Enter}');
        expect(duplicate).not.toHaveBeenCalled();
        expect(del).toHaveBeenCalledTimes(1);
        expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
    });

    it('skips disabled items in keyboard nav', async () => {
        const user = userEvent.setup();
        const ok1 = vi.fn();
        const ok2 = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'First', onSelect: ok1 },
            { id: 'b', label: 'Disabled', disabled: true, onSelect: () => {} },
            { id: 'c', label: 'Last', onSelect: ok2 },
        ];
        render(<Harness items={items} />);
        // From first → arrow-down should land on Last (skipping Disabled).
        await user.keyboard('{ArrowDown}');
        await user.keyboard('{Enter}');
        expect(ok2).toHaveBeenCalledTimes(1);
        expect(ok1).not.toHaveBeenCalled();
    });

    it('wraps highlight at the end and start', async () => {
        const user = userEvent.setup();
        const onSelectA = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'A', onSelect: onSelectA },
            { id: 'b', label: 'B', onSelect: () => {} },
        ];
        render(<Harness items={items} />);
        // From first item, ArrowUp wraps to last (B). ArrowDown from
        // last wraps back to first (A). Then Enter activates A.
        await user.keyboard('{ArrowUp}');
        await user.keyboard('{ArrowDown}');
        await user.keyboard('{Enter}');
        expect(onSelectA).toHaveBeenCalledTimes(1);
    });

    it('closes on Escape without firing any onSelect', async () => {
        const user = userEvent.setup();
        const onSelect = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'A', onSelect },
        ];
        render(<Harness items={items} />);
        await user.keyboard('{Escape}');
        expect(onSelect).not.toHaveBeenCalled();
        expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
    });

    it('marks its Escape handled and stops it, so nothing around it hears it', async () => {
        const user = userEvent.setup();
        const parentEscapes: boolean[] = [];
        const escapes: KeyboardEvent[] = [];
        const windowEscapes: KeyboardEvent[] = [];
        const capture = (e: KeyboardEvent) => { if (e.key === 'Escape') escapes.push(e); };
        const bubble = (e: KeyboardEvent) => { if (e.key === 'Escape') windowEscapes.push(e); };
        window.addEventListener('keydown', capture, true);
        window.addEventListener('keydown', bubble);
        try {
            render(
                <div onKeyDown={(e) => { if (e.key === 'Escape') parentEscapes.push(e.defaultPrevented); }}>
                    <Harness items={[{ id: 'a', label: 'A', onSelect: () => {} }]} />
                </div>,
            );

            await user.keyboard('{Escape}');

            expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
            expect(escapes.map((e) => e.defaultPrevented)).toEqual([true]);
            expect(parentEscapes).toEqual([]);
            expect(windowEscapes).toEqual([]);
        } finally {
            window.removeEventListener('keydown', capture, true);
            window.removeEventListener('keydown', bubble);
        }
    });

    it('stays open on an Escape something nearer already marked handled', async () => {
        // The menu is further out than whatever marked it, so it leaves it.
        // A capture listener stands in for that nearer handler: nothing inside
        // the menu handles a key before it does.
        const user = userEvent.setup();
        const markFirst = (e: KeyboardEvent) => { if (e.key === 'Escape') e.preventDefault(); };
        window.addEventListener('keydown', markFirst, true);
        try {
            render(<Harness items={[{ id: 'a', label: 'A', onSelect: () => {} }]} />);

            await user.keyboard('{Escape}');

            expect(screen.getByTestId('open-state')).toHaveTextContent('open');
            expect(screen.getByRole('menu')).toBeInTheDocument();
        } finally {
            window.removeEventListener('keydown', markFirst, true);
        }
    });

    it('gives focus back to its opener on Escape', async () => {
        // Focus in the menu would fall to the page when it unmounts, and an
        // editor around the opener would never see the next Escape.
        function OpenerHarness() {
            const [anchor, setAnchor] = useState<{ x: number; y: number } | null>(null);
            return (
                <div>
                    <button type="button" onClick={() => setAnchor({ x: 10, y: 10 })}>Opener</button>
                    {anchor !== null ? (
                        <ContextMenu
                            anchor={anchor}
                            items={[{ id: 'a', label: 'A', onSelect: () => {} }]}
                            onClose={() => setAnchor(null)}
                        />
                    ) : null}
                </div>
            );
        }
        const user = userEvent.setup();
        render(<OpenerHarness />);
        const opener = screen.getByRole('button', { name: 'Opener' });

        await user.click(opener);
        expect(screen.getByRole('menu')).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(screen.queryByRole('menu')).toBeNull();
        expect(opener).toHaveFocus();
    });

    it('still gives focus back to its opener after moving to a new anchor', async () => {
        // Moving re-runs the placement, and by then the menu holds focus
        // itself; that is not where an Escape should send it.
        const user = userEvent.setup();
        const onClose = vi.fn();
        const items: ContextMenuItem[] = [{ id: 'a', label: 'A', onSelect: () => {} }];
        const at = (x: number) => (
            <div>
                <button type="button">Opener</button>
                <ContextMenu anchor={{ x, y: 10 }} items={items} onClose={onClose} />
            </div>
        );
        const { rerender } = render(<div><button type="button">Opener</button></div>);
        const opener = screen.getByRole('button', { name: 'Opener' });
        opener.focus();

        rerender(at(10));
        expect(screen.getByRole('menu')).toHaveFocus();
        rerender(at(20));
        expect(screen.getByRole('menu')).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(onClose).toHaveBeenCalledTimes(1);
        expect(opener).toHaveFocus();
    });

    it('closes on outside pointerdown', async () => {
        const user = userEvent.setup();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'A', onSelect: () => {} },
        ];
        render(<Harness items={items} />);
        await user.click(screen.getByTestId('outside'));
        expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
    });
});

describe('a scroll strands the menu, so a scroll closes it', () => {
    // The menu is position:fixed at coordinates captured once, at open. It
    // cannot follow the row that spawned it, so after a scroll it floats over
    // an UNRELATED row while still offering the original row's actions —
    // Remove split on the wrong split. Closing is the only honest response.
    it('closes on a scroll from a nested scrollport', async () => {
        render(<Harness items={[{ id: 'a', label: 'Alpha', onSelect: vi.fn() }]} />);
        expect(screen.getByTestId('open-state')).toHaveTextContent('open');

        // Dispatched on an inner element, NOT on window. Scroll events do not
        // bubble, and in this app the document never scrolls — the shell is
        // h-dvh overflow-hidden, so every scrollport that can move a row is an
        // inner element. A bubble-phase window listener would miss all of them,
        // which is why the component listens capture-phase on document.
        const inner = screen.getByTestId('outside');
        inner.dispatchEvent(new Event('scroll', { bubbles: false }));

        expect(await screen.findByTestId('open-state')).toHaveTextContent('closed');
    });

    it('stops listening once closed', () => {
        // A listener that outlived the menu would call onClose on every scroll
        // in the app, forever.
        const onClose = vi.fn();
        const { unmount } = render(
            <ContextMenu anchor={{ x: 10, y: 10 }} items={[]} onClose={onClose} />,
        );
        unmount();
        document.body.dispatchEvent(new Event('scroll', { bubbles: false }));
        expect(onClose).not.toHaveBeenCalled();
    });
});

describe('a menu a keyboard can use', () => {
    it('takes focus while it is visible, because a browser will not focus what it cannot show', () => {
        // jsdom focuses a visibility:hidden element without complaint; a browser does not, and the
        // menu then never receives a key. Watch what the menu looks like at the moment it is focused.
        const seen: Array<string | undefined> = [];
        const real = HTMLElement.prototype.focus;
        const spy = vi.spyOn(HTMLElement.prototype, 'focus').mockImplementation(function (this: HTMLElement, ...args) {
            if (this.getAttribute('role') === 'menu') seen.push(this.style.visibility || undefined);
            return real.apply(this, args);
        });
        try {
            render(<ContextMenu anchor={{ x: 5, y: 5 }} items={[{ id: 'a', label: 'Edit', onSelect: vi.fn() }]} onClose={vi.fn()} />);
        } finally {
            spy.mockRestore();
        }
        expect(seen.length).toBeGreaterThan(0);
        expect(seen).not.toContain('hidden');
    });

    it('is named, and says which item is highlighted, because the menu and not the item holds focus', async () => {
        const user = userEvent.setup();
        render(
            <ContextMenu
                anchor={{ x: 50, y: 50 }}
                label="Actions for Groceries"
                items={[
                    { id: 'a', label: 'Rename', onSelect: vi.fn() },
                    { id: 'b', label: 'Merge', onSelect: vi.fn() },
                ]}
                onClose={vi.fn()}
            />,
        );
        const menu = screen.getByRole('menu', { name: 'Actions for Groceries' });
        expect(menu).toHaveFocus();
        const rename = screen.getByRole('menuitem', { name: 'Rename' });
        const merge = screen.getByRole('menuitem', { name: 'Merge' });
        expect(menu).toHaveAttribute('aria-activedescendant', rename.id);

        await user.keyboard('{ArrowDown}');
        expect(menu).toHaveAttribute('aria-activedescendant', merge.id);
    });

    describe('focus', () => {
        // Opened from a button, as a row's kebab opens it, so there is somewhere to come back to.
        function FromAButton({ onSelect = vi.fn() }: { onSelect?: () => void }) {
            const [open, setOpen] = useState(false);
            return (
                <div>
                    <button type="button" onClick={() => setOpen(true)}>kebab</button>
                    {open ? (
                        <ContextMenu
                            anchor={{ x: 5, y: 5 }}
                            items={[{ id: 'a', label: 'Edit', onSelect }]}
                            onClose={() => setOpen(false)}
                        />
                    ) : null}
                    <input aria-label="elsewhere" />
                </div>
            );
        }

        it('goes back to the control the menu was opened from after Esc', async () => {
            const user = userEvent.setup();
            render(<FromAButton />);
            await user.click(screen.getByRole('button', { name: 'kebab' }));
            expect(screen.getByRole('menu')).toHaveFocus();

            await user.keyboard('{Escape}');

            expect(screen.queryByRole('menu')).not.toBeInTheDocument();
            expect(screen.getByRole('button', { name: 'kebab' })).toHaveFocus();
        });

        it('goes back after a choice', async () => {
            const user = userEvent.setup();
            const onSelect = vi.fn();
            render(<FromAButton onSelect={onSelect} />);
            await user.click(screen.getByRole('button', { name: 'kebab' }));

            await user.keyboard('{Enter}');

            expect(onSelect).toHaveBeenCalledTimes(1);
            expect(screen.getByRole('button', { name: 'kebab' })).toHaveFocus();
        });

        it('ends with focus in the field that was clicked when the menu is dismissed by a click', async () => {
            const user = userEvent.setup();
            render(<FromAButton />);
            await user.click(screen.getByRole('button', { name: 'kebab' }));

            await user.click(screen.getByRole('textbox', { name: 'elsewhere' }));

            expect(screen.queryByRole('menu')).not.toBeInTheDocument();
            expect(screen.getByRole('textbox', { name: 'elsewhere' })).toHaveFocus();
        });

        it('does not take focus back from where a choice’s own handler put it', async () => {
            // The one case the restore stands aside for: the choice moved focus on purpose. Watched as
            // a call, because jsdom shuffles focus again when the menu’s node is removed, so the
            // field that ends up focused cannot tell a restore from none.
            const user = userEvent.setup();
            function Moves() {
                const [open, setOpen] = useState(false);
                return (
                    <div>
                        <button type="button" onClick={() => setOpen(true)}>kebab</button>
                        {open ? (
                            <ContextMenu
                                anchor={{ x: 5, y: 5 }}
                                items={[{ id: 'a', label: 'Jump', onSelect: () => document.getElementById('target')?.focus() }]}
                                onClose={() => setOpen(false)}
                            />
                        ) : null}
                        <input id="target" aria-label="target" />
                    </div>
                );
            }
            render(<Moves />);
            const kebab = screen.getByRole('button', { name: 'kebab' });
            await user.click(kebab);
            const restore = vi.spyOn(kebab, 'focus');

            fireEvent.keyDown(screen.getByRole('menu'), { key: 'Enter' });

            expect(screen.queryByRole('menu')).not.toBeInTheDocument();
            expect(restore).not.toHaveBeenCalled();
        });
    });
});
