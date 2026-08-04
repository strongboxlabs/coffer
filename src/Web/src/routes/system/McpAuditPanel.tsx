import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { fetchMcpAudit, clearMcpAudit, type McpInvocationStatus } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';

const AUDIT_KEY = ['admin-mcp-audit'] as const;

/**
 * How each lifecycle state (ADR-0086) reads in the log. `ok` is the silent
 * baseline (no marker); the other three are called out because they are the
 * outcomes an oversight log exists to surface — `pending` includes a call whose
 * process died before it could finalize (a visible unknown), and `cancelled` is
 * a client timeout/abort, distinct from a tool error.
 */
const STATUS_UI: Record<
    Exclude<McpInvocationStatus, 'ok'>,
    { marker: string; label: string }
> = {
    error: { marker: '⚠', label: 'error' },
    cancelled: { marker: '⊘', label: 'cancelled' },
    pending: { marker: '⋯', label: 'pending' },
};

/**
 * Admin — the MCP write-tool audit (ADR-0081 D3): who ran which write tool, when,
 * against which ledger, and its lifecycle outcome (ADR-0086: ok / error /
 * cancelled / still-pending). Kept for the configured retention window (default
 * 180 days) then auto-pruned; "Clear log" purges it now. Renders inside the MCP
 * tab; when MCP is off the endpoint 404s and we show a hint.
 */
export function McpAuditPanel() {
    const queryClient = useQueryClient();
    const query = useQuery({ queryKey: AUDIT_KEY, queryFn: () => fetchMcpAudit(100), retry: false });
    const clear = useMutation({
        mutationFn: () => clearMcpAudit(),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: AUDIT_KEY }),
    });
    const [clearOpen, setClearOpen] = useState(false);

    const entries = query.data;

    return (
        <section className="space-y-3">
            <header className="flex items-start justify-between gap-4">
                <div className="space-y-1">
                    <h2 className="text-base font-semibold">AI write activity</h2>
                    <p className="text-sm text-text-muted">
                        Every change an AI client made through the MCP write tools. Kept for 180
                        days, then removed automatically.
                    </p>
                </div>
                {entries && entries.length > 0 ? (
                    <Button variant="secondary" size="sm" onClick={() => setClearOpen(true)}>
                        Clear log
                    </Button>
                ) : null}
            </header>
            <Panel>
                <PanelBody>
                    {query.isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : query.isError ? (
                        <p className="text-sm text-text-muted">
                            Couldn&rsquo;t load the activity log — make sure MCP is enabled above.
                        </p>
                    ) : !entries || entries.length === 0 ? (
                        <p className="text-sm text-text-muted">No AI writes recorded yet.</p>
                    ) : (
                        <ul className="divide-y divide-border">
                            {entries.map((e) => {
                                const ui = e.status === 'ok' ? null : STATUS_UI[e.status];
                                return (
                                    <li key={e.id} className="py-2 text-sm">
                                        <div className="flex items-center justify-between gap-3">
                                            <span className="font-medium">
                                                {ui ? `${ui.marker} ` : ''}
                                                {e.toolName}
                                                {ui ? (
                                                    <span className="ml-1 text-xs font-normal text-text-muted">
                                                        ({ui.label})
                                                    </span>
                                                ) : null}
                                            </span>
                                            <span className="shrink-0 text-xs text-text-muted">
                                                {new Date(e.createdAt).toLocaleString()}
                                            </span>
                                        </div>
                                        <div className="truncate text-xs text-text-muted">
                                            {e.user}
                                            {e.arguments ? ` · ${e.arguments}` : ''}
                                            {(e.status === 'error' || e.status === 'cancelled') && e.result
                                                ? ` · ${e.result}`
                                                : ''}
                                        </div>
                                    </li>
                                );
                            })}
                        </ul>
                    )}
                    {clear.isError ? (
                        <p className="mt-2 text-xs text-state-danger">
                            {errorMessage(clear.error, 'Could not clear the log.')}
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <ConfirmDialog
                open={clearOpen}
                title="Clear the AI write log?"
                body="Permanently removes every recorded MCP write. This can't be undone."
                confirmLabel="Clear log"
                variant="danger"
                isConfirming={clear.isPending}
                onConfirm={() => clear.mutate(undefined, { onSuccess: () => setClearOpen(false) })}
                onCancel={() => setClearOpen(false)}
            />
        </section>
    );
}
