# 0093 — Naming the MCP connection: role-named config, a shown address, operator labels

* Status: Accepted
* Date: 2026-08-07
* Related: ADR-0063 (MCP server, `/mcp`, off by default), ADR-0081 (write control-plane, D5 client management), ADR-0013 (WebAuthn origins), ADR-0092 (config that names its role rather than its channel)

## Context

Three unrelated-looking papercuts turned out to share a cause: nothing in the
deployment says *where the MCP server is* or *which connection is which*. An
operator connecting a client has to know, from outside the product, what address
to use; and once several clients are connected, the admin list cannot tell them
apart.

**1. The MCP host was a positional slot.** `Api:Fido2:Origins` is an allowed-origin
array for WebAuthn. Slot 0 is the main site; slot 1 came to mean "the MCP host" —
but only by a convention described in a compose comment. Nothing enforced it and
nothing read meaning into the index, so it cost nothing while it stayed that way.

**2. There was nowhere to read the client address from.** The admin UI could turn
MCP on and off without ever saying what to point a client at. `https://host/mcp` is
guessable on a single-host install and simply unavailable to an operator whose MCP
server answers on its own subdomain.

**3. DCR display names are client-supplied and not unique.** Every install of a
given client registers under the same string, so two laptops running Claude appear
as two identical rows. Revoking the wrong one is indistinguishable from revoking
the right one until something stops working.

## Decision

### D1 — Name the origin entries for their role

```
COFFER_WEB_URL  → Api:Fido2:Origins__0
COFFER_MCP_URL  → Api:Fido2:Origins__1  AND  Api:Mcp:PublicUrl
```

`COFFER_WEB_ORIGIN_0` / `_1` keep working: compose resolves
`${NEW:-${OLD:-default}}`, so an existing `.env` is untouched and an upgrade
changes nothing until the operator chooses to.

The trigger is D2 below. An ordinal carries no contract — `Origins__1` is an
allowed-origins array whose second entry *happens* to be the MCP host. That was
tolerable while nothing consumed it; adding a second consumer would have promoted
an undeclared convention to load-bearing, and code that reads meaning into an index
breaks silently when someone reorders the array for an unrelated reason.

`COFFER_RP_ID` is deliberately **not** derived from these URLs. For a subdomain
split the RP ID must be the parent domain, and working that out from a URL requires
a public-suffix list. It stays explicit.

### D2 — `Api:Mcp:PublicUrl`, shown in the admin UI, falling back to the request

The MCP panel shows the address to hand a client, with a copy button. It uses the
configured value; when unset it falls back to the origin of the request being
served.

Configured must win. Request-derived is correct for a single-host install and
**wrong for a split one**: the admin UI is browsed on the web host, so a
request-derived answer hands out the web address for a server answering on its own
hostname — plausible-looking and unusable, which is worse than showing nothing.
The fallback exists only so the common case needs no configuration.

Two display rules, both about not sending an operator to debug the wrong thing:
the address is shown **only while MCP is actually running** (an address for a
stopped server reads as a client problem), and it includes the `/mcp` path rather
than a bare origin (an origin alone fails at the first request).

### D3 — Operator labels on clients, stored in OpenIddict's `Properties` bag

`PATCH /api/admin/mcp/clients/{clientId}` sets a label; the UI shows
`label ?? displayName`, and keeps the registered name visible in parentheses when
a label is set — the label says *which connection*, the registered name says
*which software*, and an operator deciding what to revoke needs both.

The bag rather than a table: this is one nullable string hanging off a row
OpenIddict already owns. A table would mean a migration, an entity, a repository,
a schema-drift-guard entry, and a join — to carry a name.

**The cost, accepted:** the label lives with the client registration, so revoking a
client and letting it re-register through DCR loses it. That is the same lifetime
as the consent it annotates, which matches what "rename this connection" implies —
but it is not durable identity and is not offered as such.

## Consequences

- Existing installs need no `.env` change. `install.sh` writes the new names for
  new installs and reads either when reporting the URL back.
- A split-host install must set `COFFER_MCP_URL` to get a correct address in the
  UI. Unset, it shows the web origin — which is right for the single-host case
  that most installs are.
- Labels do not survive revoke-and-reconnect. If durable per-connection identity is
  ever wanted, that is a table, and this ADR is the record of why it wasn't one
  yet.
- A third allowed origin still goes in as `COFFER_WEB_ORIGIN_2` + `Origins__2`.
  Only the two slots with fixed real-world meanings were promoted to names; the
  array remains an array.

## Alternatives considered

**Derive the MCP URL from `Origins[1]`.** Free, and exactly the coupling D1 exists
to avoid.

**Require `COFFER_MCP_URL`, no fallback.** Correct in every case, at the cost of
breaking every single-host install that never needed to think about it. The
fallback is wrong only where the operator has already split their hosts and is
therefore already configuring URLs.

**A `mcp_client_labels` table.** Durable across re-registration, which is the one
thing the `Properties` bag doesn't give. Rejected for now as a schema object per
nullable string; revisit if the labels turn out to be load-bearing rather than
convenience.
