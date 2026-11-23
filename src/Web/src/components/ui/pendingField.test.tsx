import { useState, type ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';

import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { DateField } from '@/routes/ledgers/components/DateField';
import type { AccountSummary } from '@/lib/types';

import { Checkbox } from './Checkbox';
import { Input } from './Input';
import { Typeahead } from './Typeahead';
import { PendingScope, pendingChoiceProps, pendingTextProps, usePendingChoiceProps } from './pending';

// A field whose request is pending keeps focus and refuses input (ADR-0023 §L,
// pending.ts): the button rule applies to fields. Native disabled would drop a
// focused field's focus to the page in a browser; jsdom leaves it on the
// field, so wherever focus is checked here, the field's not being natively
// disabled is asserted beside it.

function TextField({ pending, onChange }: { pending: boolean; onChange?: (v: string) => void }) {
    const [value, setValue] = useState('kept');
    return (
        <Input
            aria-label="Name"
            value={value}
            onChange={(e) => { setValue(e.target.value); onChange?.(e.target.value); }}
            pending={pending}
        />
    );
}

function Box({ pending, onChange }: { pending: boolean; onChange: (v: boolean) => void }) {
    const [on, setOn] = useState(false);
    return (
        <Checkbox
            label="Sample"
            checked={on}
            onChange={(e) => { setOn(e.target.checked); onChange(e.target.checked); }}
            pending={pending}
        />
    );
}

function Choice({ pending, onChange }: { pending: boolean; onChange: (v: string) => void }) {
    const [value, setValue] = useState('a');
    return (
        <select
            aria-label="Role"
            value={value}
            {...pendingChoiceProps<HTMLSelectElement>(pending, {
                onChange: (e) => { setValue(e.target.value); onChange(e.target.value); },
            })}
        >
            <option value="a">A</option>
            <option value="b">B</option>
        </select>
    );
}

function Radios({ pending, onChange }: { pending: boolean; onChange: (v: string) => void }) {
    const [value, setValue] = useState('never');
    const radio = (v: string) => (
        <label>
            <input
                type="radio"
                name="ends"
                checked={value === v}
                {...pendingChoiceProps<HTMLInputElement>(pending, { onChange: () => { setValue(v); onChange(v); } })}
            />
            {v}
        </label>
    );
    return <>{radio('never')}{radio('on')}</>;
}

describe('a pending text field', () => {
    it('keeps focus, is read-only, and says it is busy, not disabled', async () => {
        const onChange = vi.fn();
        render(<TextField pending onChange={onChange} />);
        const field = screen.getByRole('textbox', { name: 'Name' });
        field.focus();
        await userEvent.type(field, 'x');
        expect(field).toHaveValue('kept');
        expect(onChange).not.toHaveBeenCalled();
        expect(field).toHaveFocus();
        expect(field).not.toBeDisabled();
        expect(field).toHaveAttribute('readonly');
        expect(field).toHaveAttribute('aria-busy', 'true');
        expect(field).toHaveAttribute('aria-disabled', 'true');
    });

    it('takes typing again at rest, with nothing added', async () => {
        render(<TextField pending={false} />);
        const field = screen.getByRole('textbox', { name: 'Name' });
        await userEvent.type(field, 'x');
        expect(field).toHaveValue('keptx');
        expect(field).not.toHaveAttribute('readonly');
        expect(field).not.toHaveAttribute('aria-busy');
        expect(field).not.toHaveAttribute('aria-disabled');
    });

    it('adds nothing for a field outside the mechanism', () => {
        expect(pendingTextProps(undefined)).toEqual({});
        expect(pendingTextProps(false)).toEqual({});
    });
});

describe('a pending choice', () => {
    it('refuses a click and Space on a checkbox, and on its label, and keeps focus', async () => {
        const onChange = vi.fn();
        render(<Box pending onChange={onChange} />);
        const box = screen.getByRole('checkbox', { name: 'Sample' });
        await userEvent.click(box);
        await userEvent.click(screen.getByText('Sample'));
        box.focus();
        await userEvent.keyboard(' ');
        expect(box).not.toBeChecked();
        expect(onChange).not.toHaveBeenCalled();
        expect(box).toHaveFocus();
        expect(box).not.toBeDisabled();
        expect(box).toHaveAttribute('aria-disabled', 'true');
        expect(box).toHaveAttribute('aria-busy', 'true');
    });

    it('toggles a checkbox at rest', async () => {
        const onChange = vi.fn();
        render(<Box pending={false} onChange={onChange} />);
        await userEvent.click(screen.getByRole('checkbox', { name: 'Sample' }));
        expect(onChange).toHaveBeenCalledWith(true);
    });

    it('refuses a select\'s keys, its press and a change that arrives anyway, and keeps focus', async () => {
        const onChange = vi.fn();
        render(<Choice pending onChange={onChange} />);
        const select = screen.getByRole('combobox', { name: 'Role' });
        select.focus();
        for (const key of ['ArrowDown', ' ', 'Enter', 'b']) {
            expect(fireEvent.keyDown(select, { key }), key).toBe(false);
        }
        expect(fireEvent.keyDown(select, { key: 'Tab' })).toBe(true);
        expect(fireEvent.keyDown(select, { key: 'Escape' })).toBe(true);
        expect(fireEvent.mouseDown(select)).toBe(false);
        fireEvent.change(select, { target: { value: 'b' } });
        expect(onChange).not.toHaveBeenCalled();
        expect(select).toHaveValue('a');
        expect(select).toHaveFocus();
        expect(select).not.toBeDisabled();
    });

    it('changes a select at rest', () => {
        const onChange = vi.fn();
        render(<Choice pending={false} onChange={onChange} />);
        fireEvent.change(screen.getByRole('combobox', { name: 'Role' }), { target: { value: 'b' } });
        expect(onChange).toHaveBeenCalledWith('b');
    });

    it('refuses a radio\'s click and arrows', async () => {
        const onChange = vi.fn();
        render(<Radios pending onChange={onChange} />);
        const on = screen.getByRole('radio', { name: 'on' });
        await userEvent.click(on);
        const never = screen.getByRole('radio', { name: 'never' });
        expect(fireEvent.keyDown(never, { key: 'ArrowDown' })).toBe(false);
        expect(on).not.toBeChecked();
        expect(never).toBeChecked();
        expect(onChange).not.toHaveBeenCalled();
        expect(on).not.toBeDisabled();
    });

    it('passes the handlers of a choice outside the mechanism on as they are', () => {
        const onChange = vi.fn();
        expect(pendingChoiceProps(undefined, { onChange })).toEqual({ onChange });
        expect(pendingChoiceProps(false, { onChange })).toEqual({ onChange });
    });
});

describe('a pending field looks as a disabled one does', () => {
    // Every disabled: style of the primitives has its aria-disabled: twin.
    const twins = (className: string) => {
        const disabled = className.split(/\s+/).filter((c) => c.startsWith('disabled:')).map((c) => c.slice('disabled:'.length));
        const aria = className.split(/\s+/).filter((c) => c.startsWith('aria-disabled:')).map((c) => c.slice('aria-disabled:'.length));
        return { disabled, aria };
    };

    it('maps the Input\'s disabled look', () => {
        render(<TextField pending />);
        const { disabled, aria } = twins(screen.getByRole('textbox', { name: 'Name' }).className);
        expect(disabled.length).toBeGreaterThan(0);
        expect(aria).toEqual(expect.arrayContaining(disabled));
    });

    it('maps the Checkbox\'s disabled look', () => {
        render(<Box pending onChange={() => {}} />);
        const { disabled, aria } = twins(screen.getByRole('checkbox', { name: 'Sample' }).className);
        expect(disabled.length).toBeGreaterThan(0);
        expect(aria).toEqual(expect.arrayContaining(disabled));
    });
});

describe('a pending field component', () => {
    it('Typeahead: read-only, keeps focus, and shows nothing to pick', async () => {
        const onChange = vi.fn();
        render(
            <Typeahead
                aria-label="Payee"
                items={['Alpha', 'Beta']}
                value=""
                onChange={onChange}
                getKey={(s) => s}
                getLabel={(s) => s}
                pending
            />,
        );
        const field = screen.getByRole('combobox', { name: 'Payee' });
        field.focus();
        await userEvent.keyboard('{ArrowDown}');
        await userEvent.type(field, 'A');
        expect(screen.queryByRole('listbox')).toBeNull();
        expect(onChange).not.toHaveBeenCalled();
        expect(field).toHaveFocus();
        expect(field).not.toBeDisabled();
        expect(field).toHaveAttribute('aria-busy', 'true');
    });

    it('AccountCategoryPicker: opens no list, commits nothing, and keeps focus', async () => {
        const onChangeId = vi.fn();
        const accounts = [
            { id: 'c1', name: 'Groceries', accountType: 'category', isActive: true, isSystem: false, parentId: null },
        ] as unknown as AccountSummary[];
        render(
            <AccountCategoryPicker
                accounts={accounts}
                isEligible={() => true}
                valueId={null}
                onChangeId={onChangeId}
                ariaLabel="Category"
                pending
            />,
        );
        const field = screen.getByRole('combobox', { name: 'Category' });
        field.focus();
        await userEvent.keyboard('{ArrowDown}{Enter}');
        expect(field).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByText('Groceries')).toBeNull();
        expect(onChangeId).not.toHaveBeenCalled();
        expect(field).toHaveFocus();
        expect(field).not.toBeDisabled();
        expect(field).toHaveAttribute('readonly');
    });

    it('DateField: read-only, and its shortcuts change nothing', async () => {
        const onChange = vi.fn();
        render(<DateField label="Date" value="2026-01-02" onChange={onChange} pending />);
        const field = screen.getByLabelText('Date');
        field.focus();
        await userEvent.keyboard('t+-y');
        expect(onChange).not.toHaveBeenCalled();
        expect(field).toHaveFocus();
        expect(field).not.toBeDisabled();
        expect(field).toHaveAttribute('readonly');
    });
});

// A choice whose change writes holds from the change, as a button holds from
// the press (pending.ts): a second change before the pending state renders,
// of the same choice or another in its group, sends no second request.
describe('a choice whose change writes', () => {
    /** A request that stays in flight until the test settles it. */
    function inFlight() {
        let release: () => void = () => {};
        const send = vi.fn((_body?: unknown) => new Promise<void>((resolve) => { release = resolve; }));
        return { send, release: () => release() };
    }

    /** Microtasks only: the mutation function has run, the pending state has not rendered. */
    const ticks = async () => { for (let i = 0; i < 10; i += 1) await Promise.resolve(); };

    function withClient(ui: ReactNode) {
        const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
        return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
    }

    function Switches({ send, grouped }: { send: (v: string[]) => Promise<void>; grouped: boolean }) {
        const [on, setOn] = useState<string[]>([]);
        const save = useMutation({ mutationFn: send, onSuccess: (_d, v) => setOn(v) });
        const boxes = ['a', 'b'].map((key) => (
            <Checkbox
                key={key}
                label={`Switch ${key}`}
                checked={on.includes(key)}
                pending={save.isPending}
                onChange={(e) => save.mutate(e.target.checked ? [...on, key] : on.filter((k) => k !== key))}
            />
        ));
        return grouped ? <PendingScope>{boxes}</PendingScope> : <>{boxes}</>;
    }

    it('sends one request for the same switch changed twice before it renders pending, and puts it back', async () => {
        const { send } = inFlight();
        withClient(<Switches send={send} grouped={false} />);
        const a = screen.getByRole('checkbox', { name: 'Switch a' });

        fireEvent.click(a);
        fireEvent.click(a);
        await ticks();

        expect(send).toHaveBeenCalledTimes(1);
        expect(send.mock.calls[0]![0]).toEqual(['a']);
    });

    it('sends one request for two switches of one group changed back to back, so the second never drops the first', async () => {
        const { send } = inFlight();
        withClient(<Switches send={send} grouped />);

        fireEvent.click(screen.getByRole('checkbox', { name: 'Switch a' }));
        fireEvent.click(screen.getByRole('checkbox', { name: 'Switch b' }));
        await ticks();

        expect(send).toHaveBeenCalledTimes(1);
        expect(send.mock.calls[0]![0]).toEqual(['a']);
    });

    it('sends one each for two switches outside a group', async () => {
        const { send } = inFlight();
        withClient(<Switches send={send} grouped={false} />);

        fireEvent.click(screen.getByRole('checkbox', { name: 'Switch a' }));
        fireEvent.click(screen.getByRole('checkbox', { name: 'Switch b' }));
        await ticks();

        expect(send).toHaveBeenCalledTimes(2);
    });

    it('takes changes again once the request has settled', async () => {
        const { send, release } = inFlight();
        withClient(<Switches send={send} grouped />);
        fireEvent.click(screen.getByRole('checkbox', { name: 'Switch a' }));
        await ticks();
        release();
        await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Switch a' })).toBeChecked());
        await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Switch b' })).not.toHaveAttribute('aria-busy'));

        fireEvent.click(screen.getByRole('checkbox', { name: 'Switch b' }));
        await ticks();

        expect(send).toHaveBeenCalledTimes(2);
        expect(send.mock.calls[1]![0]).toEqual(['a', 'b']);
    });

    it('holds a raw select on the hook as it holds Checkbox', async () => {
        const { send } = inFlight();
        function Role() {
            const pendingChoiceProps = usePendingChoiceProps();
            const save = useMutation({ mutationFn: send });
            return (
                <select aria-label="Role" value="a" {...pendingChoiceProps<HTMLSelectElement>(save.isPending, {
                    onChange: () => save.mutate(),
                })}>
                    <option value="a">A</option>
                    <option value="b">B</option>
                </select>
            );
        }
        withClient(<Role />);
        const role = screen.getByRole('combobox', { name: 'Role' });

        fireEvent.change(role, { target: { value: 'b' } });
        fireEvent.change(role, { target: { value: 'b' } });
        await ticks();

        expect(send).toHaveBeenCalledTimes(1);
    });
});
