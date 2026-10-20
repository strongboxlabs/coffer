# 0014 — Encryption at rest: layered model

* Status: Accepted
* Date: 2026-05-08

## Context

Coffer stores years of financial history, OAuth tokens for bank feeds (SimpleFIN, with a major brokerage via MX), and per-account credentials. The data is high-value to its owner and to anyone who steals it. We need a deliberate position on what gets encrypted at rest, where, and against which threats.

The realistic threats for a self-hosted single-user deployment, in roughly decreasing likelihood:

1. **Loss or theft of the host machine.** Laptop stolen, server walked off with, drive sold without wiping.
2. **Backup leak.** Off-host backup ends up on a laptop, a cloud bucket, or a tape that gets misplaced.
3. **Database compromise.** Attacker gains read access to the DB without compromising the API process — e.g., a misconfigured network exposure, a stolen DB password, or a snapshot extracted from a backup.
4. **Full host compromise.** Attacker has shell or memory access to the running host. At-rest encryption doesn't help here; key material is in memory.

Postgres has **no built-in TDE** in the open-source distribution. Vendor forks (EnterpriseDB, Crunchy Data) offer one; we don't use them. Whole-DB column encryption via `pgcrypto` is theoretically possible but in practice forces the key into the database (defeating the purpose) or into per-query parameters (reinventing application-level encryption with extra steps and worse query ergonomics).

## Decision

Encryption at rest is layered. Each layer addresses a specific threat and is independent of the others.

### Layer 1 — Host-level disk encryption (deployment requirement)

The host running the Docker stack **must** have full-disk or full-volume encryption enabled. Acceptable mechanisms include LUKS (Linux), BitLocker (Windows), FileVault (macOS), and ZFS native encryption.

This is a deployment posture documented in [operations.md](../operations.md), not enforced by application code. It addresses threat (1) — physical theft — and partially (2) when the backup destination is also encrypted.

### Layer 2 — Encrypted backups

When backups land (Phase 2 operations work), they are encrypted before being written off-host. Two acceptable patterns:

- **`pgbackrest` with a repo cipher** (`--repo-cipher-type=aes-256-cbc`).
- **`pg_dump` piped through `age`** (`pg_dump … | age -e -r <recipient>` on the way out, `age -d -i <key>` on restore).

The decryption key (age private key, or `pgbackrest` cipher passphrase) is stored in the operator's password manager. It is **not** stored on the same disk as the backups. Restore drills (per [operations.md](../operations.md)) verify the key is recoverable.

This addresses threat (2) — backup leak.

### Layer 3 — Application-level envelope encryption for high-value secrets

Bank-feed OAuth tokens (SimpleFIN, Plaid if ever added, MX-fronted brokerage) are encrypted by the .NET API before they hit the database. Concretely:

- A **data encryption key** (DEK) is generated per record, used to encrypt the token with `AES-GCM-256` (`System.Security.Cryptography.AesGcm`).
- The DEK is wrapped (encrypted) by a **key encryption key** (KEK) loaded from the runtime environment.
- The DB stores: `(ciphertext, nonce, wrapped_dek, kek_id)`. Never the plaintext, never the KEK, never an unwrapped DEK.
- KEK rotation is supported by tagging each row with `kek_id`; a new KEK can be introduced without a flag day, and re-wrapping is a background job.

This addresses threat (3) — DB compromise without API-process compromise. It does *not* protect against threat (4); we accept that limit.

**Scope discipline:** envelope encryption is applied **only** to high-value secrets (bank tokens, future webhook secrets, future encrypted backup passphrases stored in the DB). It is **not** applied to bulk transaction data. Reasons:

- Encrypted columns can't be queried meaningfully (no GIN trigram on ciphertext, no useful indexing).
- The threat-model improvement is marginal — an attacker with DB access who lacks API-process access is a narrow scenario, and the cost of encrypting bulk data is paid on every read.
- Disk-level encryption (Layer 1) and backup encryption (Layer 2) already cover the realistic threats against bulk data.

### Layer 4 — KEK source (deployment-time choice)

The KEK is loaded at API process startup. Three acceptable sources, in increasing security and friction:

| Source | Trade-off |
|---|---|
| Environment variable (`COFFER_KEK_BASE64`) | Simplest. Container restarts work unattended. Vulnerable to env dump or `/proc` read by another root process. |
| Passphrase entered at startup (interactive `docker compose run` or systemd `LoadCredential`) | Container won't auto-restart without operator input. Trades availability for one extra layer against unattended-boot theft (already covered by Layer 1, so this is belt-and-braces). |
| Hardware-backed (TPM 2.0 sealed key, or YubiKey-derived via PIV) | Strongest. Most friction. Reasonable long-term direction. |

Phase 5 (when bank tokens first appear) starts with the env-var KEK; the wrapped-DEK schema is identical regardless of KEK source, so graduation to a hardware-backed KEK is a deployment change, not a schema change.

## Consequences

**Positive**
- Each threat has a layer that addresses it; no single layer is asked to do everything.
- The bulk of the schema and queries stay plaintext, keeping query ergonomics and dev velocity intact.
- KEK source can evolve independently of the DB schema.
- Backup security and live-data security are both addressed.

**Negative**
- Host-level disk encryption is a deployment requirement we can't enforce from the app. We document it and trust the operator (the owner) to configure it. A future "first-run check" could read `/proc/mounts` or `cryptsetup status` to warn loudly if the host volume isn't encrypted, but that's a Phase 10 polish, not a Phase 1 concern.
- A user who loses the KEK can't recover their bank tokens — they re-link the bank feeds, which is a one-time annoyance, not data loss. Bulk transaction data is unaffected (only Layer 1/2 keys matter for that).
- We're explicitly not encrypting bulk data at the column level. An attacker with DB-only read who doesn't have the API process can read the user's transaction history. Mitigation: don't let that happen — the database is bound to the internal Docker network, and any external exposure goes through the auth layer ([0013-webauthn-passkey-auth.md](0013-webauthn-passkey-auth.md)).

## Alternatives considered

- **PostgreSQL TDE.** Not in the OSS distribution. Vendor forks add a licensing dependency we don't want. Disk-level encryption gives equivalent threat coverage. Rejected.
- **Whole-DB `pgcrypto` column encryption.** Either the key lives in the DB (defeats the purpose) or in per-query parameters (worse than application-level encryption with no upside). Rejected for bulk data; envelope encryption in the application is the right shape for the small set of secrets that actually need it.
- **Encrypt every column at the application layer.** Forces every read through a decrypt path, breaks indexing on payees and memos, makes reports painful. The cost is real and constant; the benefit is narrow. Rejected.
- **Skip backup encryption** and rely on the backup destination's access controls. Not adequate for a finance app — backup destinations get misplaced, mis-permissioned, or restored to wrong machines. Rejected.
- **Skip host disk encryption** because the app is "behind a VPN". Network posture and at-rest encryption are independent (per ADR-0013's framing). A stolen drive doesn't care about the VPN. Rejected.
