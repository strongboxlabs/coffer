import { useId, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import {
    fetchMcpClients,
    fetchMcpConnections,
    fetchMcpSetting,
    pruneMcpClients,
    renameMcpConnection,
    revokeMcpClient,
    revokeMcpConnection,
    type McpClient,
    type McpConnection,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { lastUsedText } from '@/lib/mcpConnection';
import {
    appIdentity,
    appIdentityText,
    expiresWithinDays,
    groupConnectedApps,
    inDays,
    signInCount,
    type AppGroup,
} from '@/lib/mcpConnectedApps';
import { RenameConnectionForm } from '@/components/RenameConnectionForm';
import { useRenameFocus } from '@/components/renameFocus';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { Panel, PanelBody } from '@/components/ui/Panel';

const CONNECTIONS_KEY = ['admin-mcp-connections'] as const;
const CLIENTS_KEY = ['admin-mcp-clients'] as const;
// Shared with McpSettingsPanel: whether AI writes are on decides when a row says read-only.
const MCP_SETTING_KEY = ['admin-mcp-setting'] as const;

/** Row identity: a token id and an authorization id are different id spaces. */
const rowKey = (c: Pick<McpConnection, 'kind' | 'id'>) => `${c.kind}:${c.id}`;

const shortDate = (iso: string) => new Date(iso).toLocaleDateString();

/**
 * Admin — every app that can reach the MCP server, with the live sign-ins made through it,
 * and personal tokens beside them (ADR-0113, amending ADR-0107 D1). A registration is the
 * app; a sign-in is one person's connection through it. Revoke a sign-in to end only that
 * person's access; disconnect an app to end every sign-in through it; remove the apps nobody
 * holds a live sign-in through. Admin-only; the endpoints are the boundary, this is UX.
 * Renders inside the MCP tab; when MCP is off the endpoints 404 and we show a hint rather
 * than an error.
 */
export function McpConnectedAppsPanel() {
    const queryClient = useQueryClient();
    const connectionsQuery = useQuery({ queryKey: CONNECTIONS_KEY, queryFn: fetchMcpConnections, retry: false });
    const clientsQuery = useQuery({ queryKey: CLIENTS_KEY, queryFn: fetchMcpClients, retry: false });
    const settingQuery = useQuery({ queryKey: MCP_SETTING_KEY, queryFn: fetchMcpSetting, retry: false });

    const invalidate = async () => {
        await queryClient.invalidateQueries({ queryKey: CONNECTIONS_KEY });
        await queryClient.invalidateQueries({ queryKey: CLIENTS_KEY });
    };

    const [editing, setEditing] = useState<string | null>(null);
    const [revokeTarget, setRevokeTarget] = useState<McpConnection | null>(null);
    const [disconnectTarget, setDisconnectTarget] = useState<AppGroup | null>(null);
    const [removeOpen, setRemoveOpen] = useState(false);
    // The rename form goes on a save that lands, Cancel or Escape: focus goes to
    // the row's Rename, drawn in its place (ADR-0023 §L).
    const renameFocus = useRenameFocus<string>();

    const rename = useMutation({
        mutationFn: (v: { c: McpConnection; name: string }) =>
            renameMcpConnection(v.c.kind, v.c.id, v.name),
        onSuccess: async () => {
            setEditing(null);
            await queryClient.invalidateQueries({ queryKey: CONNECTIONS_KEY });
        },
    });
    const revoke = useMutation({
        mutationFn: (c: McpConnection) => revokeMcpConnection(c.kind, c.id),
        onSuccess: async () => {
            setRevokeTarget(null);
            await invalidate();
        },
    });
    const disconnect = useMutation({
        mutationFn: (clientId: string) => revokeMcpClient(clientId),
        onSuccess: invalidate,
    });
    const remove = useMutation({ mutationFn: () => pruneMcpClients(), onSuccess: invalidate });

    // Expiry is measured from when the lists were read, which is what the rows describe; a
    // refetch moves it on.
    const readAt = Math.max(connectionsQuery.dataUpdatedAt, clientsQuery.dataUpdatedAt);
    const grouped = useMemo(
        () =>
            connectionsQuery.data && clientsQuery.data
                ? groupConnectedApps(clientsQuery.data, connectionsQuery.data, readAt)
                : null,
        [connectionsQuery.data, clientsQuery.data, readAt],
    );
    // Read-only is worth saying only where it differs from the deployment's setting: with AI
    // writes on, a connection without the write scope is the exception; with them off, every
    // connection is read-only and saying so on each row says nothing.
    const writesOn = settingQuery.data?.writesActive === true;
    const unused = grouped?.apps.filter((a) => a.status.kind === 'no-live-sign-in') ?? [];

    // A revoked sign-in or token takes its row's Revoke with it; a disconnected app takes its
    // Disconnect. Focus goes to the same control on the next row, else the previous, else the
    // list itself, which has no add control (ADR-0023 §L).
    const listRef = useRef<HTMLDivElement | null>(null);
    const headingId = useId();
    const rowKeys = grouped
        ? [...grouped.apps.flatMap((a) => a.signIns), ...grouped.orphans, ...grouped.tokens].map(rowKey)
        : [];
    const revokeControl = useRowRemovalFocus(rowKeys, listRef);
    const disconnectControl = useRowRemovalFocus(grouped?.apps.map((a) => a.client.clientId) ?? [], listRef);

    const isPending = connectionsQuery.isPending || clientsQuery.isPending;
    const isError = connectionsQuery.isError || clientsQuery.isError;
    const isEmpty = grouped !== null
        && grouped.apps.length === 0 && grouped.orphans.length === 0 && grouped.tokens.length === 0;

    const renderConnection = (c: McpConnection) => {
        const key = rowKey(c);
        const soon = expiresWithinDays(c.expiresAt, readAt);
        return (
            <li key={key} className="flex items-center justify-between gap-4 py-2">
                <div className="min-w-0 flex-1 text-sm">
                    {editing === key ? (
                        <RenameConnectionForm
                            initial={c.label ?? (c.kind === 'token' ? c.name : '')}
                            label={`Name for ${c.name}`}
                            placeholder={c.appName ?? c.name}
                            allowBlank={c.kind === 'oauth'}
                            pending={rename.isPending}
                            error={rename.error ? errorMessage(rename.error, 'Could not rename it.') : null}
                            onSave={(name) => rename.mutate({ c, name })}
                            onCancel={() => setEditing(null)}
                            {...renameFocus.formRefs}
                        />
                    ) : (
                        <div>
                            {/* Who holds it is always shown: the owner first, then the
                                connection's own name when it has one. A token always has
                                one; an OAuth sign-in only once someone named it. */}
                            <span className="font-medium">{c.user}</span>
                            {c.kind === 'token' || c.label ? (
                                <span className="ml-2 text-text-muted">{c.name}</span>
                            ) : null}
                        </div>
                    )}
                    <div className="truncate text-xs text-text-muted">
                        {`connected ${shortDate(c.connectedAt)}`}
                        {` · ${lastUsedText(c.kind, c.lastUsedAt)}`}
                        {c.expiresAt ? (
                            soon !== null ? (
                                <span className="text-state-warning">{` · expires ${inDays(soon)}`}</span>
                            ) : (
                                ` · expires ${shortDate(c.expiresAt)}`
                            )
                        ) : null}
                        {writesOn && !c.canWrite ? ' · read-only' : null}
                    </div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                    {editing === key ? null : (
                        <Button
                            ref={renameFocus.renameRef(key)}
                            variant="secondary"
                            size="sm"
                            onClick={() => {
                                rename.reset();
                                renameFocus.opened(key);
                                setEditing(key);
                            }}
                        >
                            Rename
                        </Button>
                    )}
                    <Button
                        ref={revokeControl(key)}
                        variant="danger"
                        size="sm"
                        onClick={() => setRevokeTarget(c)}
                        pending={revoke.isPending}
                    >
                        Revoke
                    </Button>
                </div>
            </li>
        );
    };

    const renderApp = (app: AppGroup) => {
        const { client, signIns, status } = app;
        const identity = appIdentity(client.redirectUris);
        return (
            <li key={client.clientId} className="py-3 first:pt-0">
                <div className="flex items-start justify-between gap-4">
                    <div className="min-w-0 text-sm">
                        <div className="flex flex-wrap items-baseline gap-x-2">
                            <span className="font-medium">{client.displayName}</span>
                            <span
                                className={
                                    'text-xs '
                                    + (app.allExpireInDays !== null ? 'text-state-warning' : 'text-text-muted')
                                }
                            >
                                {status.kind === 'active'
                                    ? `Active · last used ${new Date(status.lastUsedAt).toLocaleString([], {
                                        dateStyle: 'medium',
                                        timeStyle: 'short',
                                    })}`
                                    : status.kind === 'no-use-recorded'
                                        ? 'No use recorded'
                                        : 'No live sign-in'}
                                {app.allExpireInDays !== null
                                    ? ` · access ends ${inDays(app.allExpireInDays)}`
                                    : null}
                            </span>
                        </div>
                        <div className="text-xs text-text-muted">
                            {appIdentityText(identity)}
                            {client.registeredAt ? ` · registered ${shortDate(client.registeredAt)}` : null}
                            {` · ${signInCount(signIns.length)}`}
                        </div>
                        {client.redirectUris.length > 0 ? (
                            <details className="mt-1 text-xs text-text-muted">
                                <summary className="cursor-pointer select-none">Callback addresses</summary>
                                <ul className="mt-1 space-y-0.5 break-all">
                                    {client.redirectUris.map((u) => (
                                        <li key={u}>{u}</li>
                                    ))}
                                </ul>
                            </details>
                        ) : null}
                    </div>
                    <Button
                        ref={disconnectControl(client.clientId)}
                        variant="secondary"
                        size="sm"
                        className="shrink-0"
                        onClick={() => setDisconnectTarget(app)}
                        pending={disconnect.isPending}
                    >
                        Disconnect app…
                    </Button>
                </div>
                {signIns.length > 0 ? (
                    <ul
                        aria-label={`Sign-ins through ${client.displayName}`}
                        className="mt-2 divide-y divide-border border-l border-border pl-4"
                    >
                        {signIns.map(renderConnection)}
                    </ul>
                ) : (
                    <p className="mt-2 border-l border-border pl-4 text-xs text-text-muted">
                        Nobody holds a live sign-in through this app. Remove never-used apps removes it.
                    </p>
                )}
            </li>
        );
    };

    return (
        <section className="space-y-3">
            <header className="flex items-start justify-between gap-4">
                <div className="space-y-1">
                    <h2 id={headingId} className="text-base font-semibold">Connected apps</h2>
                    <p className="text-sm text-text-muted">
                        Each app that registered to reach the MCP server, with the people signed in
                        through it, and the personal tokens people pasted into apps. Revoke a
                        sign-in to end only that person’s access; disconnect an app to end every
                        sign-in through it. Rename a sign-in to tell two of the same app apart.
                    </p>
                </div>
                {unused.length > 0 ? (
                    <Button variant="secondary" size="sm" className="shrink-0" onClick={() => setRemoveOpen(true)}>
                        Remove never-used apps
                    </Button>
                ) : null}
            </header>
            <Panel
                ref={listRef}
                role="group"
                aria-labelledby={headingId}
                tabIndex={-1}
                className="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
                <PanelBody>
                    {isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : isError ? (
                        <p className="text-sm text-text-muted">
                            Couldn&rsquo;t load connected apps — make sure MCP is enabled above.
                        </p>
                    ) : isEmpty || grouped === null ? (
                        <p className="text-sm text-text-muted">Nothing is connected yet.</p>
                    ) : (
                        <div className="space-y-4">
                            {grouped.apps.length > 0 || grouped.orphans.length > 0 ? (
                                <ul className="divide-y divide-border">
                                    {grouped.apps.map(renderApp)}
                                    {grouped.orphans.length > 0 ? (
                                        <li className="py-3 first:pt-0">
                                            <div className="text-sm font-medium">Removed app</div>
                                            <div className="text-xs text-text-muted">
                                                Its registration is gone; revoke these sign-ins one by one.
                                            </div>
                                            <ul className="mt-2 divide-y divide-border border-l border-border pl-4">
                                                {grouped.orphans.map(renderConnection)}
                                            </ul>
                                        </li>
                                    ) : null}
                                </ul>
                            ) : null}
                            {grouped.tokens.length > 0 ? (
                                <div
                                    className={
                                        grouped.apps.length > 0 || grouped.orphans.length > 0
                                            ? 'border-t border-border pt-3'
                                            : ''
                                    }
                                >
                                    <h3 className="text-sm font-medium">Personal tokens</h3>
                                    <p className="text-xs text-text-muted">
                                        Minted by a person and pasted into an app; not tied to any app above.
                                    </p>
                                    <ul
                                        aria-label="Personal tokens"
                                        className="mt-2 divide-y divide-border border-l border-border pl-4"
                                    >
                                        {grouped.tokens.map(renderConnection)}
                                    </ul>
                                </div>
                            ) : null}
                        </div>
                    )}
                    {revoke.isError || disconnect.isError || remove.isError ? (
                        <p role="alert" className="mt-2 text-xs text-state-danger">
                            {errorMessage(revoke.error ?? disconnect.error ?? remove.error, 'Action failed.')}
                        </p>
                    ) : null}
                    {remove.isSuccess ? (
                        <p className="mt-2 text-xs text-text-muted">
                            Removed {remove.data.pruned} app{remove.data.pruned === 1 ? '' : 's'}.
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <ConfirmDialog
                open={revokeTarget !== null}
                title={`Revoke “${revokeTarget?.name ?? ''}”?`}
                body={
                    revokeTarget
                        ? `${revokeTarget.user}’s ${revokeTarget.kind === 'token' ? 'token' : 'sign-in'} stops working immediately. They can connect again, and nobody else’s sign-ins are touched.`
                        : null
                }
                confirmLabel="Revoke"
                variant="danger"
                isConfirming={revoke.isPending}
                onConfirm={() => {
                    if (revokeTarget) revoke.mutate(revokeTarget);
                }}
                onCancel={() => setRevokeTarget(null)}
            />
            <ConfirmDialog
                open={disconnectTarget !== null}
                title={`Disconnect “${disconnectTarget?.client.displayName ?? ''}”?`}
                body={disconnectTarget ? <DisconnectBody app={disconnectTarget} /> : null}
                confirmLabel="Disconnect"
                variant="danger"
                isConfirming={disconnect.isPending}
                onConfirm={() => {
                    if (disconnectTarget) {
                        disconnect.mutate(disconnectTarget.client.clientId, {
                            onSuccess: () => setDisconnectTarget(null),
                        });
                    }
                }}
                onCancel={() => setDisconnectTarget(null)}
            />
            <ConfirmDialog
                open={removeOpen}
                title="Remove never-used apps?"
                body={<RemoveBody apps={unused.map((a) => a.client)} />}
                confirmLabel={`Remove ${unused.length} app${unused.length === 1 ? '' : 's'}`}
                variant="danger"
                isConfirming={remove.isPending}
                // A removal that leaves no unused app takes its button away with the
                // rows; focus goes to the list (ADR-0023 §L).
                fallbackFocusRef={listRef}
                onConfirm={() => remove.mutate(undefined, { onSuccess: () => setRemoveOpen(false) })}
                onCancel={() => setRemoveOpen(false)}
            />
        </section>
    );
}

function DisconnectBody({ app }: { app: AppGroup }) {
    const people = [...new Set(app.signIns.map((s) => s.user))];
    return (
        <div className="space-y-2">
            <p>
                {app.signIns.length === 0
                    ? 'Nobody holds a live sign-in through this app.'
                    : `Ends ${signInCount(app.signIns.length)} through this app, for everyone who holds one: ${people.join(', ')}.`}
            </p>
            <p>
                Its access tokens stop working immediately, and the app has to register again before
                anyone can connect through it.
            </p>
        </div>
    );
}

function RemoveBody({ apps }: { apps: McpClient[] }) {
    return (
        <div className="space-y-2">
            <p>
                Removes every app nobody holds a live sign-in through: nobody signed in, or every
                sign-in was revoked or ran out. An app that connects again registers again.
            </p>
            <ul className="list-disc space-y-0.5 pl-5">
                {apps.map((c) => {
                    const identity = appIdentity(c.redirectUris);
                    return (
                        <li key={c.clientId}>
                            {c.displayName}
                            <span className="text-text-muted">
                                {identity.host ? ` · ${identity.host}` : ''}
                                {c.registeredAt ? ` · registered ${shortDate(c.registeredAt)}` : ''}
                            </span>
                        </li>
                    );
                })}
            </ul>
        </div>
    );
}
