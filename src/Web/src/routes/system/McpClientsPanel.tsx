import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    fetchMcpClients,
    revokeMcpClient,
    setMcpClientLabel,
    pruneMcpClients,
    type McpClient,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';

const CLIENTS_KEY = ['admin-mcp-clients'] as const;

/**
 * Admin — the OAuth clients that can reach the MCP server (ADR-0081 D5): revoke one
 * (its tokens stop immediately) or prune clients that never signed in. Writes aren't
 * gated per-client (the kill-switch + guard are the gate), so there's no per-client
 * toggle here. Admin-only; the endpoints are the boundary, this is UX. Renders inside
 * the MCP tab; when MCP is off the endpoint 404s and we show a hint rather than an error.
 */
export function McpClientsPanel() {
    const queryClient = useQueryClient();
    const query = useQuery({ queryKey: CLIENTS_KEY, queryFn: fetchMcpClients, retry: false });
    const invalidate = () => queryClient.invalidateQueries({ queryKey: CLIENTS_KEY });

    const revoke = useMutation({
        mutationFn: (clientId: string) => revokeMcpClient(clientId),
        onSuccess: invalidate,
    });
    const prune = useMutation({ mutationFn: () => pruneMcpClients(), onSuccess: invalidate });
    const relabel = useMutation({
        mutationFn: (v: { clientId: string; label: string }) =>
            // Blank clears the label rather than storing an empty name — the row then
            // falls back to whatever the client registered itself as.
            setMcpClientLabel(v.clientId, v.label.trim() === '' ? null : v.label.trim()),
        onSuccess: () => {
            setEditing(null);
            invalidate();
        },
    });

    const [editing, setEditing] = useState<string | null>(null);
    const [draft, setDraft] = useState('');
    const [revokeTarget, setRevokeTarget] = useState<McpClient | null>(null);
    const [pruneOpen, setPruneOpen] = useState(false);

    const clients = query.data;

    return (
        <section className="space-y-3">
            <header className="flex items-start justify-between gap-4">
                <div className="space-y-1">
                    <h2 className="text-base font-semibold">Connected AI clients</h2>
                    <p className="text-sm text-text-muted">
                        OAuth clients that registered to reach the MCP server. Revoke one, or
                        prune clients that never signed in.
                    </p>
                </div>
                {clients && clients.length > 0 ? (
                    <Button variant="secondary" size="sm" onClick={() => setPruneOpen(true)}>
                        Prune unused
                    </Button>
                ) : null}
            </header>
            <Panel>
                <PanelBody>
                    {query.isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : query.isError ? (
                        <p className="text-sm text-text-muted">
                            Couldn&rsquo;t load clients — make sure MCP is enabled above.
                        </p>
                    ) : !clients || clients.length === 0 ? (
                        <p className="text-sm text-text-muted">No AI clients have connected yet.</p>
                    ) : (
                        <ul className="divide-y divide-border">
                            {clients.map((c) => (
                                <li
                                    key={c.clientId}
                                    className="flex items-center justify-between gap-4 py-2"
                                >
                                    <div className="min-w-0 text-sm">
                                        {editing === c.clientId ? (
                                            <form
                                                className="flex items-center gap-2"
                                                onSubmit={(e) => {
                                                    e.preventDefault();
                                                    relabel.mutate({ clientId: c.clientId, label: draft });
                                                }}
                                            >
                                                <input
                                                    autoFocus
                                                    aria-label={`Name for ${c.displayName}`}
                                                    className="w-56 rounded-md border border-border px-2 py-1 text-sm"
                                                    value={draft}
                                                    placeholder={c.displayName}
                                                    onChange={(e) => setDraft(e.target.value)}
                                                    onKeyDown={(e) => {
                                                        if (e.key === 'Escape') setEditing(null);
                                                    }}
                                                />
                                                <Button type="submit" size="sm" disabled={relabel.isPending}>
                                                    Save
                                                </Button>
                                                <Button
                                                    type="button"
                                                    variant="secondary"
                                                    size="sm"
                                                    onClick={() => setEditing(null)}
                                                >
                                                    Cancel
                                                </Button>
                                            </form>
                                        ) : (
                                            <div className="font-medium">
                                                {c.label ?? c.displayName}
                                                {/* Registered name kept visible when renamed: the
                                                    label is the operator's word for it, but the
                                                    client's own name is what identifies WHICH
                                                    software connected. */}
                                                {c.label ? (
                                                    <span className="ml-2 font-normal text-text-muted">
                                                        ({c.displayName})
                                                    </span>
                                                ) : null}
                                            </div>
                                        )}
                                        <div className="truncate text-xs text-text-muted">
                                            {c.clientType} · {c.activeAuthorizations} authorization(s)
                                            {c.redirectUris.length > 0
                                                ? ` · ${c.redirectUris.join(', ')}`
                                                : ''}
                                        </div>
                                    </div>
                                    <div className="flex shrink-0 items-center gap-2">
                                        {editing === c.clientId ? null : (
                                            <Button
                                                variant="secondary"
                                                size="sm"
                                                onClick={() => {
                                                    setEditing(c.clientId);
                                                    setDraft(c.label ?? '');
                                                }}
                                            >
                                                Rename
                                            </Button>
                                        )}
                                        <Button
                                            variant="danger"
                                            size="sm"
                                            onClick={() => setRevokeTarget(c)}
                                            disabled={revoke.isPending}
                                        >
                                            Revoke
                                        </Button>
                                    </div>
                                </li>
                            ))}
                        </ul>
                    )}
                    {revoke.isError || prune.isError ? (
                        <p className="mt-2 text-xs text-state-danger">
                            {errorMessage(revoke.error ?? prune.error, 'Action failed.')}
                        </p>
                    ) : null}
                    {prune.isSuccess ? (
                        <p className="mt-2 text-xs text-text-muted">
                            Pruned {prune.data.pruned} unused client(s).
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <ConfirmDialog
                open={revokeTarget !== null}
                title={`Revoke “${revokeTarget?.displayName ?? ''}”?`}
                body="Its access tokens stop working immediately, and it will have to register again."
                confirmLabel="Revoke"
                variant="danger"
                isConfirming={revoke.isPending}
                onConfirm={() => {
                    if (revokeTarget) {
                        revoke.mutate(revokeTarget.clientId, { onSuccess: () => setRevokeTarget(null) });
                    }
                }}
                onCancel={() => setRevokeTarget(null)}
            />
            <ConfirmDialog
                open={pruneOpen}
                title="Prune unused clients?"
                body="Removes every client that has never signed in (no authorizations). Clients in use are kept."
                confirmLabel="Prune"
                variant="danger"
                isConfirming={prune.isPending}
                onConfirm={() => prune.mutate(undefined, { onSuccess: () => setPruneOpen(false) })}
                onCancel={() => setPruneOpen(false)}
            />
        </section>
    );
}
