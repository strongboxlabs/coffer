# 0101 — A backup too big to send: restore what we already hold, and split what we ship

* Status: Accepted
* Date: 2026-09-29
* Amends: [ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md)
  (the "raise your proxy's limit" remedy, and its rejection of a second restore path)

## Context

ADR-0094 removed the restore CLI and made the UI the only path, on the grounds that a
proxy body cap is the operator's environment to configure:

> A restore over ~4 GiB, or through a proxy that caps bodies, needs operator action —
> raise `client_max_body_size` or equivalent, or install on localhost with no proxy in
> front.

That remedy does not exist for the deployment it was written against. **Cloudflare caps
proxied request bodies at 100 MB on Free and Pro, and it is not a setting.** The
alternatives are an Enterprise contract or taking the site off the proxy — not a config
line. A real `.cofferbak` on a modest personal ledger is 141 MB. Restore through the
front door returned 413 before a byte reached the app; the same request sent directly at
the API returned 401, which is the app answering. So the app was never the constraint
and raising `MultipartBodyLengthLimit` to 4 GiB (D2 there) bought nothing.

Two distinct situations were failing, and they need different answers:

| | Where the artifact is | What restore needs |
|---|---|---|
| **Roll back this install** | on this server's disk, written by this server | to be told WHICH one |
| **Disaster recovery** | on a laptop, fetched from Google Drive | to be sent, through the proxy |

The first was a pure round trip: Coffer uploading a file to Coffer, to reach a file
Coffer was already sitting on. The second genuinely needs a transfer, and that transfer
is the one the cap refuses.

The Drive copy had a second, independent problem: one artifact was one Drive file, so an
upload interrupted at 90% restarted from zero, every time, for as long as the connection
kept dropping.

## Decision

**D1 — Restore accepts the id of a backup this install already holds.** The form lists
them; choosing one uploads nothing. The id is resolved through `BackupStore`, never by
pasting it into a path, so an id the store does not own resolves to nothing rather than
traversing.

**D2 — An artifact pushed off-host goes as parts of the configured size**, named
`{id}.cofferbak.002-of-003`. The count is in the name, so a Drive folder is
self-describing: order and completeness are readable without a manifest to lose. A
single-part artifact keeps the plain `{id}.cofferbak` name, so nothing about existing
backups changes. Only MISSING parts are uploaded, so an interrupted push resumes.

**D3 — Restore accepts an artifact as an ordered sequence of parts**, sent to
`POST /api/admin/backups/restore/parts` and then named by a separate, confirmed restore
call. The web form produces that sequence two ways: by slicing one large file, or by
taking a set of `*-of-*` files exactly as they were downloaded from Drive. Reassembling
them locally first would have asked the operator to concatenate files by hand on the one
day they have no patience for it.

**D3a — The same applies to the PRE-AUTH bootstrap restore**
(`POST /api/auth/setup/{token}/restore/parts`), which is the path that needs it most.
Rolling a running install back can read the artifact off its own disk (D1); a fresh
install cannot, because the machine it replaces is gone. That is the deployment where an
upload is the only route in, through a proxy the operator has just stood up. Both paths
share one implementation (`RestorePartProtocol`): they must assemble bytes identically,
and nothing would catch them drifting short of a restore that fails to decrypt.

**D4 — The stored artifact stays ONE file, and downloads stay whole.** Splitting is a
property of the transfer, not of the backup. A download is a RESPONSE, which no proxy
caps the way it caps a request body, and a single file is what an operator copying one
out by hand wants. This keeps local retention, pins and the `BackupStore` id model
completely untouched.

**D5 — The part size is configurable (`Api:Backup:PartSizeMb`), defaulting to 49.**

This ADR was first written with it as a constant, on the reasoning that it exists to
clear one specific unraisable cap so nobody should have a number to get wrong. Two
things were wrong with that.

The caps differ. 49 MB clears Cloudflare's 100 MB *and* the common 50 MB limits, but
there is no value that clears every proxy, and several of those are no more raisable
than Cloudflare's. A fixed size answers one deployment and leaves the next one with a
restore that cannot run and no lever at all.

More decisive: a constant made the feature untestable. On any install whose backup is
under the threshold — which is most of them, including this project's own development
one — the split path cannot fire, so nobody sees it work until the day it is needed.
Lowering the setting is how the path gets exercised at all. A feature that cannot be
tried without recompiling does not get tried, and shipping a green test suite over code
that has never once run is a failure mode this project has already hit.

The setting is safe to change whenever, because nothing reads it back: a part set
already written off-host carries its own count in its filenames (D2), and an upload in
flight is validated against the size the CLIENT declares, not the server's current one.

## Consequences

**The order and size of parts are checked on arrival, not trusted.** Every part but the
last is exactly one part-size, so part N can only begin at `(N-1) x partSize` — and that
offset is computed from the bytes already on disk, so an interrupted upload resumes
across a process restart with nothing held in memory. It also means a re-sent final part
is refused: once the short last part has landed, no part number satisfies the offset,
which matters because retrying after a lost response is exactly what a client does.
Part 1 always restarts the upload, both the retry-from-scratch path and the only way out
of an abandoned one.

**The part size in that arithmetic is the CLIENT's, declared per request.** Validating
against the server's own setting would reject a set cut before someone changed it —
including parts downloaded from off-host storage months earlier, which is the case the
whole path exists for. Taking it from the client is not trust: a wrong value fails the
offset check on the very next part, and on part 1 there is nothing yet to corrupt. The
browser gets the size to cut at from the server (`/restore/limits` when authenticated,
the setup `/info` response before any account exists), so lowering the setting actually
changes what the browser sends rather than being decoration.

This checking is not defensive habit. **A mis-assembled archive fails to decrypt, and a
decrypt failure is reported as a wrong passphrase.** An operator restoring a backup at
the worst moment of their year would be sent looking at their passphrase for a fault in
the transfer.

**The part upload is deliberately not behind the typed confirmation.** Nothing about it
is destructive — an upload never followed by a confirmed restore leaves a file in staging
and no more. Gating it would mean typing the phrase before a transfer that takes minutes,
and then acting on a confirmation given long before the thing it confirmed existed.

**An artifact already in Drive as one whole file stays that way.** It is present, so the
mirror leaves it alone rather than spending its whole size in bandwidth to arrive at the
same bytes in a different shape.

**ADR-0094's `MultipartBodyLengthLimit` of 4 GiB stays**, now as what it always was: a
backstop on an unbounded stream, not a capability. The cap that mattered was never ours.

## Alternatives rejected

**Document "put Coffer on a domain that bypasses the proxy for /api/admin".** Makes the
product's core recovery operation depend on the operator correctly carving an exception
out of their CDN, for the one operation where a mistake is unrecoverable.

**Split the artifact at rest.** One representation, no reassembly logic in two places —
and it would have touched `List`, `OpenRead`, `Delete`, retention, pins and the id model,
to solve a problem that does not exist locally. The boundary that needs splitting is the
transfer; splitting anything else is cost without a reason.

**Have restore pull from Google Drive directly.** Removes the operator's machine from the
path entirely, and is the better answer for the DR case. Rejected for now, not forever: it
makes restore depend on a connected Drive whose OAuth token is sealed under the master
KEK — which, in the cross-install restore that ADR-0092 D4 exists for, is precisely the
key you do not have.

**Keep the part size a constant.** Rejected after first accepting it; the reasoning is
in D5.
