# 0107 — Connections are what people hold; registrations are what they connect through

* Status: Accepted (amends 0063, 0081 D5, 0105; supersedes [0093](0093-mcp-connection-identity.md) D3)
* Date: 2026-10-24
* Related: [0063](0063-mcp-server.md) (the MCP server and its OAuth server),
  [0081](0081-mcp-write-control-plane.md) D5 (admin oversight of clients),
  [0093](0093-mcp-connection-identity.md) D3 (operator labels on registrations: replaced here),
  [0105](0105-a-cookie-alone-is-not-enough.md) (a person ends their own connections).

## Context

A person reaches `/mcp` in one of two ways: a personal token they minted, or an app they
signed in to through OAuth. Admin oversight (0081 D5) was built around the OAuth **client
registration**, and that is the wrong unit:

- **A registration is shared.** Every person who connects through one client uses the one
  registration, so the admin list could not say whose connection was whose, and revoking a
  registration ended every person's connection through it at once.
- **A name could not tell two connections apart.** A client registers under a name of its
  own choosing, and every install of one client reports the same string. The only name an
  admin could set lived on the registration, so it was one name for every person's
  connection through it.
- **OAuth connections recorded no use.** A personal token carries `last_used_at`;
  OpenIddict records nothing of the kind on an authorization, so an admin could not tell a
  connection in daily use from one nobody had touched in months.
- **Two verbs for one act.** Account → Security ended a token with *Revoke* and a sign-in
  with *Disconnect*.

## Decision

### D1 — The admin's unit is the connection

System → MCP lists one row per live connection, token or OAuth: the owner, the app, read or
write, when it was connected, **when it was last used**, and when it expires. An admin may
rename or revoke any of them. Revoking someone else's connection is written to the admin
audit (`mcp.connection-revoked`, naming the kind and the owner, never the token), like the
other privilege changes (FU-090).

The registration list stays, demoted to what only it can do: revoke a registration (ending
every connection made through it, and the confirmation says how many) and prune the ones
nobody ever signed in through. It no longer carries a name.

### D2 — A name belongs to a connection

A token's name is its label and is renamed in place; it cannot be blank. An OAuth
connection's name is its owner's word for it, kept apart from the app's own registered name,
which stays visible beside it because it says which software this is. A blank name clears
it. The owner renames their own; an admin may rename any. The registration label is retired:
this replaces 0093 D3, which put an operator label on the registration in OpenIddict's
`Properties` bag and named a table as the answer if labels ever mattered. They did, and the
table is D3 below, keyed on the authorization (one person's connection) rather than the
registration (everyone's).

### D3 — OAuth use is stamped on the request, in a table of our own

`oauth_connection_meta` holds, per OpenIddict authorization, the name and `last_used_at`.
It is a table, not OpenIddict's `Properties` bag, because the stamp is written on every
authenticated `/mcp` request and the bag is one JSON column: stamping it is a
read-modify-write that loses a concurrent write and races OpenIddict's own updates to the
row. The stamp runs after the authorization middleware, so a request the `RequireMcp`
policy refused is not a use, and it is not throttled, which is how a token behaves. A
failed stamp is logged and never fails the call.

The row references the authorization with `ON DELETE CASCADE`: removing an authorization
takes its record with it, and a stamp for one that does not exist is refused, not orphaned.
Per-user RLS as for tokens; the stamp and the admin list use the service role with the user
named explicitly.

Nothing is backfilled: there is no honest value to derive, and a stamped `now()` would
claim a use that did not happen. A connection that predates the table shows **no use
recorded** until it is next used. A personal token with no time shows **never used**,
because for a token that is true.

### D4 — One verb: Revoke

Account → Security says *Revoke* for tokens and sign-ins alike, and each has a *Rename*. For
the owner there is nothing else to do: they can only end their own authorization, and the
registration is not theirs. Ending a registration is the admin's act (D1).

## Consequences

- One extra single-row UPDATE per OAuth `/mcp` request.
- A name stored on a registration under OpenIddict's `coffer:label` property is no longer
  read. Nothing else wrote it, and leaving it in place is harmless.
- A name belongs to one authorization, so it does not survive revoke-and-reconnect: the new
  connection is a new authorization and starts unnamed. 0093 accepted the same for
  registrations; it is the same lifetime as the consent the name annotates.
- Last-used for OAuth connections is blank until each is next used after upgrading; the
  words say so rather than guessing.
- Admins see the display name of every person who holds a connection. That is the point of
  the page, and it is as visible as the existing write-audit log.
