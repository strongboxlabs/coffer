# 0113 — A connection sits under its app

* Status: Accepted (amends [0107](0107-connections-are-what-people-hold.md) D1, [0081](0081-mcp-write-control-plane.md) D5)
* Date: 2026-11-16
* Related: [0063](0063-mcp-server.md) (the MCP server and its OAuth server),
  [0107](0107-connections-are-what-people-hold.md) (connections are what people hold),
  [0023](0023-ui-ux-interaction-conventions.md) §L (where focus goes when a row goes).

## Context

0107 D1 made the connection the admin's unit and kept the registration list beside it,
demoted to revoke and prune. The two lists described one thing from two sides with nothing
joining them:

- **No link between a connection and its registration.** A connection carried the app's
  name but not which registration it was made through, so two registrations of one app (two
  installs, or one connector added twice) could not be matched to their connections.
- **Two rules for "connection".** The connection list showed live authorizations: valid and
  inside their 90-day age. A registration's count, and prune, counted every authorization of
  any status. A registration could read "1 connection" with no connection under it, and one
  whose every sign-in had been revoked or had expired was never pruned. Nothing else removes
  those records, so such registrations stayed for ever and counted against the dynamic
  registration cap.
- **Nothing to tell two registrations apart.** A registration showed its client type, a raw
  callback URL and that count. OpenIddict records no creation time for an application.

## Decision

### D1 — One list: each app with the sign-ins made through it (amends 0107 D1)

System → MCP shows one **Connected apps** list. Each registration is a row, and the live
OAuth sign-ins made through it are listed under it. Personal tokens are a group of their own,
since they belong to no registration. A sign-in whose registration is missing from the list
is shown under "Removed app" rather than dropped.

A sign-in row always names the person who holds it, then its own name when it has one, then
when it was connected, last used and expires. An admin renames or revokes a sign-in there,
as in 0107. Read-only is shown only where it differs from the deployment: with AI writes on,
on a connection without the write scope. With writes off nothing can write, so the mark
would be on every row and say nothing.

Each app has one status: **Active** with its latest recorded use, **No use recorded** (live
sign-ins, none with a recorded use, in 0107 D3's words), or **No live sign-in**. A sign-in
that expires within seven days says so, and an app whose last live sign-in does says when
its access ends. Apps are listed most recently used first and apps with no live sign-in last.

An app is described by where it asks to be sent back after sign-in. Every callback on a
loopback address means an app on a person's device, shown with host and port. Any other
callback means a hosted service, shown with that host. The raw callback addresses sit behind
a disclosure. The registration's own action is **Disconnect app…**: the confirmation says
how many sign-ins it ends and whose.

### D2 — Live is one rule (amends 0081 D5)

An authorization is live when it is valid and inside its age (`OAuthGrants.IsLiveAsync`).
The connection list, a registration's count (`liveConnections`) and prune all use this one
predicate, so they cannot disagree.

Prune, now **Remove never-used apps**, removes every registration with no live sign-in. That
covers one nobody signed in through and one whose every sign-in was revoked or ran past its
age. It deletes the registration's tokens and authorizations with it, as revoking a
registration does. The confirmation names each app it will remove. An app that still holds a
removed registration registers again on its next connect.

### D3 — A registration records when it was made

The registration endpoint writes the registration time to the application's `Properties`
bag under `coffer:registered_at`, the same instant it returns as `client_id_issued_at`.
0107 D3 kept last-used out of the bag because that stamp is rewritten on every request. This
one is written once, when the registration is created, so the bag's read-modify-write hazard
does not arise. Nothing is backfilled: a registration made before this has no time, and the
list shows none rather than a guess.

## Consequences

- The admin connection DTO carries `clientId`. The registration DTO's `activeAuthorizations`
  becomes `liveConnections`, and it gains `registeredAt`. The API has no consumer but the
  SPA, so the rename is not a break.
- A registration that read "1 connection" with nothing under it now reads 0 and is offered
  for removal.
- Registrations from before this release show no registration date. Their sign-ins' dates
  and callback hosts still tell them apart.
- A client that caches its registration and cannot register again on `invalid_client` needs
  removing and adding once after its registration is pruned.
- A restore revokes every authorization and keeps the registrations (FU-076), so straight
  after one every app reads No live sign-in and is offered for removal. Leaving them lets a
  connector sign in again on its existing registration; removing them makes it register anew.
  Removal is never automatic, and its confirmation names what it removes.
