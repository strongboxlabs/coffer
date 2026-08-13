# 0095 — The master key leaves the setup ceremony for a welcome screen

* Status: Accepted
* Date: 2026-08-12
* Amends: [ADR-0092](0092-kek-lifecycle-in-the-ui.md) D2 (which put the key in the
  setup ceremony behind its own acknowledgement)

## Context

ADR-0092 D2 ended setup with two secrets, chained and each behind an acknowledgement
checkbox: the recovery codes, then the master key. The ordering argument was sound —
codes first, because they are one-time and the only way back in without the
authenticator — but it answered the wrong question. It established which secret should
come *first*, never whether the second belonged in that flow at all.

Two things say it doesn't.

**At setup the key protects nothing yet.** It wraps the per-ledger LEKs, and those seal
bank-feed tokens, the stored backup passphrase and the Drive connection. On a
first-boot install there are none of those, and no backup anywhere to restore. So the
operator is asked to file away a secret whose purpose cannot be stated in the present
tense — the copy has to talk about a migration that may never happen.

**And it is the mildest secret in the system.** A restore needs the `.cofferbak` and its
passphrase; the archive is encrypted under the passphrase, not the KEK. Losing the key
costs three reconnections and no data — not the ledgers, not the passkeys, not the
backups. Gating progress on it put it visually and procedurally on a par with the
recovery codes, which are genuinely unrecoverable, and that mis-teaches severity at the
one moment the operator has no way to calibrate. (This ADR's immediate cause: the docs
had drifted into claiming the key was required for disaster recovery. It never was.)

## Decision

**Setup ends at the recovery codes.** One secret, one acknowledgement, the one that
cannot be recovered.

**A welcome screen follows it**, carrying three things in order: the master key with
copy/download and the reason it exists; the advice to set up backups, which is what
actually protects the data and is where the key's purpose becomes concrete; and the next
step for the ledger itself — look around the Demo ledger, or create/import a first one.

**No acknowledgement on the welcome screen.** It is advice at the moment it is true, not
a ceremony. The key remains viewable under System → Encryption behind a fresh passkey
prompt, so a gate here would be theatre — and a checkbox that claims more urgency than
the situation has is how operators learn to click past warnings.

**The key still rides back in the one-time setup-completion response**, unchanged from
D2, and is rendered from that payload. No new endpoint, no second reveal, no fresh
assertion seconds after enrolling a passkey, and the audit distinction between a
first-run disclosure and a deliberate later reveal survives intact.

## Consequences

The welcome screen is where a first-time operator is told about backups, which is a
better place for it than nowhere — the previous flow dropped them at the ledger hub with
no mention of the thing that protects their data.

It appears once, immediately after setup, and is not reachable again. That is acceptable
because nothing on it is one-time: the key is re-viewable, and the backups advice is a
standing panel under System → Backups. If it ever needs to be revisitable, it becomes a
route with the key section omitted when there is no first-run payload to render.

`install.sh` says the setup link is where the key gets shown; that stays true — the link
opens setup, and the welcome screen follows it in the same visit.

## Alternatives rejected

**Show the key on first backup instead.** Tighter — the key becomes load-bearing exactly
when a backup exists. Rejected because it would need the one-time payload persisted or a
fresh-assertion reveal at that moment, and because an operator who never enables backups
would then never see the key at all, which is the failure D2 was right to avoid.

**Drop the display entirely and let System → Encryption be the only route.** Rejected
for the same reason: a key nobody has been told about is one nobody backs up, and the
first time it matters is a migration, when it is too late to learn.
