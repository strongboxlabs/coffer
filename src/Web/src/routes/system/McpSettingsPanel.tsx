import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { fetchMcpSetting, setMcpSetting } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';

const MCP_SETTING_KEY = ['admin-mcp-setting'] as const;

/**
 * Admin MCP toggle (ADR-0063 §D8). Enables/disables the MCP server (the AI
 * connector surface) deployment-wide. The change is persisted immediately but
 * takes effect only on the next API restart — MCP is gated at startup so that
 * when off the OAuth/`/mcp` endpoints are absent, not merely 404 (ADR-0063 §D7).
 * Admin-only; the endpoint is the boundary, this panel is UX.
 */
export function McpSettingsPanel() {
    const queryClient = useQueryClient();
    const [copyState, setCopyState] = useState<'idle' | 'copied' | 'unsupported'>('idle');
    const query = useQuery({ queryKey: MCP_SETTING_KEY, queryFn: fetchMcpSetting });
    const mutation = useMutation({
        mutationFn: (next: { enabled: boolean; writesEnabled: boolean }) =>
            setMcpSetting(next.enabled, next.writesEnabled),
        onSuccess: (saved) => queryClient.setQueryData(MCP_SETTING_KEY, saved),
    });

    const setting = query.data;
    const pendingRestart = setting ? setting.enabled !== setting.active : false;

    return (
        <section className="space-y-3">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">MCP server</h2>
                <p className="text-sm text-text-muted">
                    Lets AI clients (Claude, Gemini) read your financial reports over an
                    authenticated connection — and, if you enable AI writes below, make
                    AI-assisted edits. Off by default. Enabling MCP takes effect after the
                    server restarts; the AI-writes switch takes effect immediately.
                </p>
            </header>
            <Panel>
                <PanelBody>
                    {query.isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : query.isError || !setting ? (
                        <p className="text-sm text-state-danger">
                            {errorMessage(query.error, 'Could not load the MCP setting.')}
                        </p>
                    ) : (
                        <div className="space-y-3">
                            <div className="flex items-center justify-between gap-4">
                                <div className="text-sm">
                                    <span className="font-medium">
                                        MCP is {setting.active ? 'running' : 'stopped'}
                                    </span>
                                    {setting.active !== setting.enabled ? (
                                        <span className="text-text-muted">
                                            {' '}· will be {setting.enabled ? 'enabled' : 'disabled'} after restart
                                        </span>
                                    ) : null}
                                </div>
                                <Button
                                    onClick={() => mutation.mutate({
                                        enabled: !setting.enabled,
                                        // Disabling MCP also turns writes off (writes imply MCP on).
                                        writesEnabled: setting.enabled ? false : setting.writesEnabled,
                                    })}
                                    disabled={mutation.isPending || setting.configForced}
                                >
                                    {setting.enabled ? 'Disable MCP' : 'Enable MCP'}
                                </Button>
                            </div>

                            {pendingRestart ? (
                                <p className="rounded-md bg-state-warning-soft px-3 py-2 text-xs text-text">
                                    Saved. Restart the API for this to take effect.
                                </p>
                            ) : null}

                            {setting.configForced ? (
                                <p className="text-xs text-text-muted">
                                    MCP is forced on by configuration
                                    (<code>COFFER_API__Mcp__Enabled</code>); this toggle can't disable it.
                                </p>
                            ) : null}

                            {/* The address to paste into a client. Shown only while MCP is
                                actually running: an address for a server that isn't
                                answering is an invitation to debug the wrong thing. */}
                            {setting.active && setting.publicUrl ? (
                                <div className="space-y-1 border-t border-border pt-3">
                                    <div className="text-sm font-medium">Connect a client to</div>
                                    <div className="flex items-center gap-2">
                                        <code className="min-w-0 flex-1 truncate rounded-md bg-surface-muted px-2 py-1 text-xs">
                                            {setting.publicUrl}/mcp
                                        </code>
                                        <Button
                                            variant="secondary"
                                            onClick={() => {
                                                // Same approach as the recovery-codes copy: the
                                                // standard API, and a visible outcome either way.
                                                // Silent failure here would leave the operator
                                                // pasting whatever was already on the clipboard.
                                                navigator.clipboard
                                                    ?.writeText(`${setting.publicUrl}/mcp`)
                                                    .then(() => setCopyState('copied'))
                                                    .catch(() => setCopyState('unsupported'));
                                            }}
                                        >
                                            {copyState === 'copied' ? 'Copied' : 'Copy'}
                                        </Button>
                                    </div>
                                    {copyState === 'unsupported' ? (
                                        <p className="text-xs text-text-muted">
                                            Couldn't reach the clipboard — select the address and copy it
                                            manually.
                                        </p>
                                    ) : null}
                                </div>
                            ) : null}

                            {/* Write tools (ADR-0068) — a second, narrower opt-in, only
                                meaningful while MCP is on. */}
                            {setting.enabled ? (
                                <div className="space-y-2 border-t border-border pt-3">
                                    <div className="flex items-center justify-between gap-4">
                                        <div className="text-sm">
                                            <span className="font-medium">
                                                AI writes are {setting.writesActive ? 'enabled' : 'disabled'}
                                            </span>
                                        </div>
                                        <Button
                                            variant={setting.writesEnabled ? 'secondary' : 'primary'}
                                            onClick={() => mutation.mutate({
                                                enabled: setting.enabled,
                                                writesEnabled: !setting.writesEnabled,
                                            })}
                                            disabled={mutation.isPending || setting.writesConfigForced}
                                        >
                                            {setting.writesEnabled ? 'Disable AI writes' : 'Enable AI writes'}
                                        </Button>
                                    </div>

                                    {setting.writesEnabled ? (
                                        <p className="rounded-md border-2 border-state-danger bg-state-danger-soft px-3 py-2 text-sm font-semibold text-state-danger">
                                            ⚠ AI writes are ON. A connected AI agent can <strong>modify, merge,
                                            reclassify, and delete</strong> your accounts, securities,
                                            categories, and transactions — in bulk. There is no per-change
                                            confirmation. You are responsible for reviewing what you ask it to
                                            do; take a snapshot first if you want a restore point. Turn this
                                            off when you're done cleaning up.
                                        </p>
                                    ) : (
                                        <p className="text-xs text-text-muted">
                                            Off by default. When on, MCP also exposes mutating tools (set /
                                            merge / delete / recategorize / convert) for AI-assisted cleanup.
                                        </p>
                                    )}

                                    {setting.writesConfigForced ? (
                                        <p className="text-xs text-text-muted">
                                            AI writes are forced on by configuration
                                            (<code>COFFER_API__Mcp__WritesEnabled</code>).
                                        </p>
                                    ) : null}
                                </div>
                            ) : null}

                            {mutation.isError ? (
                                <p className="text-xs text-state-danger">
                                    {errorMessage(mutation.error, 'Could not save the setting.')}
                                </p>
                            ) : null}
                        </div>
                    )}
                </PanelBody>
            </Panel>
        </section>
    );
}
