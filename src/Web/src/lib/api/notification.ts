// Admin notification endpoints (ADR-0096): providers, delivery targets, events.

import { request } from './_request';
import type {
    NotificationProvider,
    NotificationSubscribersResponse,
    LedgerNotificationSettings,
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
