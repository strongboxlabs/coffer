// Admin notification endpoints (ADR-0096): providers, delivery targets, events.

import { request } from './_request';
import type {
    LedgerAttention,
    NotificationProvider,
    NotificationSubscribersResponse,
    LedgerNotificationSettings,
    OutstandingProblem,
    SystemAttention,
    SystemEvent,
} from '../types/notification';

/** GET /api/admin/notifications/providers — what this build can deliver to. */
export function fetchNotificationProviders(): Promise<NotificationProvider[]> {
    return request<NotificationProvider[]>('/api/admin/notifications/providers');
}

/**
 * GET /api/admin/notifications/subscribers — configured targets, plus whether
 * absence detection is covered at all.
 */
export function fetchNotificationSubscribers(): Promise<NotificationSubscribersResponse> {
    return request<NotificationSubscribersResponse>('/api/admin/notifications/subscribers');
}

/** POST /api/admin/notifications/subscribers — the URL is sealed server-side. */
export function createNotificationSubscriber(body: {
    subscriberKey: string;
    displayName?: string;
    url: string;
    token?: string;
    minSeverity: string;
    /** Required for a heartbeat provider, rejected for a message one. */
    monitors?: string;
    /**
     * A topic filter for a message target. Omitted means every topic EXCEPT the opt-in ones, which a
     * target receives only by naming them (ADR-0110: `upgrade`).
     */
    topics?: string[];
}): Promise<unknown> {
    return request<unknown>('/api/admin/notifications/subscribers', {
        method: 'POST',
        body,
    });
}

/** DELETE /api/admin/notifications/subscribers/{id} */
export function deleteNotificationSubscriber(id: string): Promise<void> {
    return request<void>(
        `/api/admin/notifications/subscribers/${encodeURIComponent(id)}`,
        { method: 'DELETE' },
    );
}

/** GET /api/admin/notifications/events — recent deployment-scope events. */
export function fetchSystemEvents(): Promise<SystemEvent[]> {
    return request<SystemEvent[]>('/api/admin/notifications/events');
}

/**
 * GET /api/ledgers/{id}/notifications/events — what this ledger has been TOLD recently.
 *
 * Warnings and problems only, decided server-side. Every scheduled run of every enabled
 * job records an `info` success, so a list of everything would be pages of "completed"
 * with the one failure pushed off the end.
 *
 * Asks for exactly what it renders, unlike fetchSystemEvents() which takes the server's
 * default 50 and lets the panel throw 30 away.
 */
export function fetchLedgerEvents(ledgerId: string, limit = 20): Promise<SystemEvent[]> {
    return request<SystemEvent[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/notifications/events?limit=${limit}`,
    );
}

/**
 * GET /api/ledgers/{id}/notifications/outstanding — this ledger's outstanding problems,
 * one row per subject, critical first. Fetch only when the attention count is above 0:
 * a ledger with nothing outstanding has nothing to list.
 */
export function fetchOutstandingProblems(ledgerId: string): Promise<OutstandingProblem[]> {
    return request<OutstandingProblem[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/notifications/outstanding`,
    );
}

/**
 * GET /api/ledgers/attention — which of my ledgers needs me (FU-049).
 *
 * CROSS-ledger, which is the point: the question has no per-ledger answer, and the
 * navigation renders on every page, so one fetch per ledger would put N requests behind
 * everything. Ledgers with nothing outstanding are absent rather than reported as fine.
 */
export function fetchLedgerAttention(): Promise<LedgerAttention[]> {
    return request<LedgerAttention[]>('/api/ledgers/attention');
}

/**
 * GET /api/ledgers/{id}/notifications/providers — what this build can deliver to.
 * Ledger-scoped on purpose: the admin route requires admin, so a plain holder
 * configuring their own ledger got a 403 and an empty dropdown.
 */
export function fetchLedgerNotificationProviders(
    ledgerId: string,
): Promise<NotificationProvider[]> {
    return request<NotificationProvider[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/notifications/providers`,
    );
}

/** GET /api/ledgers/{id}/notifications — this ledger's own delivery targets. */
export function fetchLedgerNotifications(
    ledgerId: string,
): Promise<LedgerNotificationSettings> {
    return request<LedgerNotificationSettings>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/notifications`,
    );
}

/** POST /api/ledgers/{id}/notifications/subscribers — the URL is sealed server-side. */
export function createLedgerNotificationSubscriber(
    ledgerId: string,
    body: {
        subscriberKey: string;
        displayName?: string;
        url: string;
        minSeverity: string;
        monitors?: string;
    },
): Promise<unknown> {
    return request<unknown>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/notifications/subscribers`,
        { method: 'POST', body },
    );
}

/** DELETE /api/ledgers/{id}/notifications/subscribers/{subscriberId} */
export function deleteLedgerNotificationSubscriber(
    ledgerId: string,
    subscriberId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/notifications/subscribers/`
        + `${encodeURIComponent(subscriberId)}`,
        { method: 'DELETE' },
    );
}

/**
 * GET /api/admin/notifications/attention — the deployment-scope subjects still outstanding, behind
 * the System-settings gear and the System tabs (ADR-0102, amended for ADR-0110).
 *
 * Admin-only. The server runs the lazy release check first, so with the automatic check on this one
 * read, made from any page, is what lets an available release be noticed at all.
 */
export function fetchSystemAttention(): Promise<SystemAttention> {
    return request<SystemAttention>('/api/admin/notifications/attention');
}
