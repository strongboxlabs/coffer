import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { deleteCsvMapping, fetchCsvMappings, updateCsvMapping } from '@/lib/api';
import type { CsvMapping } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { LayoutNameDialog } from '@/components/imports/LayoutNameDialog';

/**
 * The saved delimited-file layouts, where they can be pruned.
 *
 * <b>Management lives here rather than in the import dialog</b> — deliberately, and by
 * the user's call. A layout you want to rename or delete is usually one you do NOT want
 * to import with, and cramming both into a dialog you can only reach by starting an
 * import means the only route to housekeeping runs through an action you did not want
 * to take. The import dialog keeps the three things that need a file in front of them:
 * reuse, override, and save-as-new.
 *
 * <b>"Layout" here, "mapping" in the code and the API.</b> It is the word the user
 * reaches for, and the API path shipped in 0.76.0 — renaming that would break MCP
 * clients for a vocabulary preference.
 *
 * The document is shown verbatim behind a disclosure instead of being summarised. A
 * summary would need a second reader — the browser does not parse YAML, and should not
 * start — and since the wizard writes its options out as comments, the document already
 * explains itself to anyone who opens it.
 */
export function ImportLayoutsPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const query = useQuery({
        queryKey: ['csv-mappings', ledgerId],
        queryFn: () => fetchCsvMappings(ledgerId),
    });
    const layouts = useMemo(() => query.data ?? [], [query.data]);

    const [renaming, setRenaming] = useState<CsvMapping | null>(null);
    const [deleting, setDeleting] = useState<CsvMapping | null>(null);

    const refresh = () =>
        void queryClient.invalidateQueries({ queryKey: ['csv-mappings', ledgerId] });

    const remove = useMutation({
        mutationFn: (id: string) => deleteCsvMapping(ledgerId, id),
        onSuccess: () => { refresh(); setDeleting(null); },
    });

    // Renaming sends the document back UNCHANGED alongside the new name: the update
    // endpoint replaces the whole row, so omitting it would blank the thing being
    // renamed.
    const rename = useMutation({
        mutationFn: (name: string) => updateCsvMapping(
            ledgerId, renaming!.id, name, renaming!.definitionYaml),
        onSuccess: () => { refresh(); setRenaming(null); },
    });

    return (
        <Panel>
            <PanelHead>
                <span className="font-medium">Import layouts</span>
            </PanelHead>
            <PanelBody>
                <p className="mb-3 text-xs text-text-muted">
                    How a delimited file from one institution is read — which column is the
                    date, which is the amount, and so on. Saved when you import, and offered
                    again next time. Shared across every account in this ledger, because two
                    cards from the same issuer export the same shape.
                </p>

                {query.isPending ? (
                    <p className="text-sm text-text-muted">Loading…</p>
                ) : query.isError ? (
                    <p role="alert" className="text-sm text-state-danger">
                        {errorMessage(query.error, 'Could not load the saved layouts.')}
                    </p>
                ) : layouts.length === 0 ? (
                    <p className="text-sm text-text-muted">
                        None saved yet. Import a CSV and choose “Save as…” to keep the
                        layout you describe.
                    </p>
                ) : (
                    <ul className="divide-y divide-border">
                        {layouts.map((layout) => (
                            <li key={layout.id} className="py-2">
                                <div className="flex items-center justify-between gap-3">
                                    <span className="min-w-0">
                                        <span className="block truncate text-sm text-text">
                                            {layout.name}
                                        </span>
                                        <span className="block text-[0.6875rem] text-text-subtle">
                                            Updated {new Date(layout.updatedAt).toLocaleDateString()}
                                        </span>
                                    </span>
                                    {/* Plain buttons, not a right-click menu. This list is
                                        short and the actions are the whole point of the
                                        panel; an affordance nobody knows to look for is one
                                        this feature has already been bitten by. */}
                                    <span className="flex shrink-0 gap-1">
                                        <Button
                                            type="button"
                                            variant="ghost"
                                            size="sm"
                                            onClick={() => setRenaming(layout)}
                                        >
                                            Rename
                                        </Button>
                                        <Button
                                            type="button"
                                            variant="ghost"
                                            size="sm"
                                            onClick={() => setDeleting(layout)}
                                        >
                                            Delete
                                        </Button>
                                    </span>
                                </div>
                                <details className="mt-1">
                                    <summary className="cursor-pointer text-[0.6875rem] text-text-subtle">
                                        Show document
                                    </summary>
                                    <pre className="mt-1 overflow-x-auto rounded border border-border bg-surface p-2 font-mono text-[0.6875rem] text-text-muted">
                                        {layout.definitionYaml}
                                    </pre>
                                </details>
                            </li>
                        ))}
                    </ul>
                )}
            </PanelBody>

            {renaming !== null ? (
                <LayoutNameDialog
                    title="Rename layout"
                    label="Name"
                    initialName={renaming.name}
                    submitLabel="Rename"
                    pending={rename.isPending}
                    error={rename.isError
                        ? errorMessage(rename.error, 'Could not rename the layout.')
                        : null}
                    // Its OWN name is allowed here — renaming to it is a no-op, not a
                    // clash. That is the whole reason the rule belongs to the caller
                    // rather than to the dialog: saving a copy may not reuse any name,
                    // renaming may keep one.
                    validateName={(n) => (layouts
                        .filter((l) => l.id !== renaming.id)
                        .some((l) => l.name.toLowerCase() === n.toLowerCase())
                        ? 'A layout is already called that.'
                        : null)}
                    onSubmit={(n) => rename.mutate(n)}
                    onClose={() => { setRenaming(null); rename.reset(); }}
                />
            ) : null}

            {deleting !== null ? (
                <ConfirmDialog
                    open
                    variant="danger"
                    title={`Delete “${deleting.name}”?`}
                    confirmLabel="Delete"
                    isConfirming={remove.isPending}
                    body={
                        <>
                            {/* Worth saying plainly: nothing references a layout once an
                                import has run, so this cannot reach the transactions it
                                produced. Someone who is not sure will otherwise keep dead
                                layouts forever rather than risk their register. */}
                            This removes the saved layout only. Transactions already imported
                            with it are unaffected — you just won’t be offered it next time.
                            {remove.isError ? (
                                <p className="mt-2 text-state-danger">
                                    {errorMessage(remove.error, 'Could not delete the layout.')}
                                </p>
                            ) : null}
                        </>
                    }
                    onConfirm={() => remove.mutate(deleting.id)}
                    onCancel={() => setDeleting(null)}
                />
            ) : null}
        </Panel>
    );
}
