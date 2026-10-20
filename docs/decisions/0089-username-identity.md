# 0089 — Username identity: permissive charset, case-insensitive in storage

* Status: Accepted
* Date: 2026-08-02
* Relates: [ADR-0013](0013-webauthn-passkey-auth.md) (passkey auth), [ADR-0083](0083-multi-user-management-invites.md) (invites), [ADR-0088](0088-setup-asks-one-question.md) (setup flow)

## Context

Setup refused `ada.reyes@example.com` as a username, disabled its submit button, and
said nothing about why. Investigating that turned up four separate problems.

**1. The rule existed in exactly one place and nothing enforced it.**
`SetupPage.tsx` tested `^[a-z0-9_-]{3,32}$` client-side. Meanwhile:

| Layer | What it actually enforced |
|---|---|
| `POST /setup/{token}/begin` | non-empty + not taken. No pattern. |
| `POST /invites/{token}/begin` | non-empty. No pattern. |
| `users.username` | a unique index. No `CHECK`. |

So any API client, and **every invited user**, could already register an email
address. Only the first user was blocked — by a regex the rest of the system
ignored. An email address is also WebAuthn's canonical example for
`user.name` (the "human-palatable identifier"), which is what we pass it as.

**2. `aria-invalid` was invisible.** The form set it, but the `Input` primitive
had no matching visual treatment — announced to screen readers, nothing rendered.
Combined with a disabled submit and a grey hint, an invalid username was
indistinguishable from a broken application.

**3. Comparison was case-sensitive.** `u.Username == username` compiles to SQL
`=` on a `text` column in the database's default collation. Two live bugs:

- **Login lockout.** Register as `Ada`, type `ada` at sign-in, and
  `UsersRepository.GetByUsernameAsync` returns null — "user not found" for an
  account that exists. With a passkey the username is frequently the only thing
  the user types, so there is no other way in.
- **Duplicate identities.** `uq_users_username` is case-sensitive, so `Ada` and
  `ada` are two accounts. That is an impersonation and confusion vector, and
  with email usernames they are the same person to any mail provider.

**4. There was no NFC normalisation.** `é` arrives as U+00E9 or as `e` + U+0301
depending on keyboard and OS. Identical on screen, different bytes — two
accounts, and a login typed on another machine can miss the row.

## Decision

**Permissive charset, validated server-side. Case folded in the database.**

1. **`UsernamePolicy` (`src/Api/Auth/`) is the single source of truth**, used by
   both `/setup/begin` and `/invites/begin`. It normalises (trim + NFC) and then
   rejects only what harms a login identifier:
   - whitespace — invisible padding, and copy/paste variants that look identical
   - Unicode control/format characters (`\p{C}`) — U+202E RIGHT-TO-LEFT OVERRIDE
     lets one username render as another; zero-width characters make distinct
     usernames indistinguishable
   - length outside 3–254 (254 = RFC 5321's email ceiling)

   Emails, handles, and names in any script are all acceptable.

2. **Case-insensitivity lives in the column, not in C#.** Migration 187 adds an
   ICU collation and applies it to `users.username`:

   ```sql
   CREATE COLLATION username_ci (
       provider = icu, locale = 'und-u-ks-level2', deterministic = false);
   ALTER TABLE users ALTER COLUMN username TYPE text COLLATE username_ci;
   ```

   `=` and `uq_users_username` then fold case for **every** caller, including
   ones added later. `ALTER COLUMN TYPE` rebuilds the existing unique index under
   the new collation, so no second index and no query changes.

3. **The folding is culture-independent, by construction.** `locale = 'und'` is
   "undetermined" — deliberately nobody's locale. This matters because per-user
   language/culture selection is planned (see follow-ups): if a user's culture
   drove folding, the same username would resolve differently depending on who
   was signing in. Identity is global; culture is presentation.

4. **`aria-invalid` renders.** The `Input` primitive gained a danger border for
   it, fixing every form at once, and the setup and invite forms now state the
   reason inline instead of relying on a passive hint.

## Alternatives rejected

**C#-side `ToLower()` before comparing.** Culture-dependent (`"I".ToLower()` is
dotless `ı` under tr-TR, so an account created on one host becomes unreachable
from another), and one forgotten call site from silently breaking. Storage-level
folding cannot be bypassed.

**A `lower(username)` functional index.** `lower()` uses the database's ctype,
which is baked in at `initdb` from the container/host locale — so two installs
would disagree about whether `İSTANBUL` and `istanbul` are the same user.
Demonstrable anywhere: `SELECT lower('İSTANBUL'), lower('İSTANBUL' COLLATE "C");`
returns `istanbul` and `İstanbul`.

**`COLLATE "C"` for determinism.** Byte-deterministic and portable, but folds
ASCII only — `JOSÉ` and `josé` would remain separate accounts, and non-Latin
scripts would not fold at all. Unacceptable with locale selection planned.

**Keeping a handle-only charset but enforcing it properly.** Defensible, and
rejected: it means telling a user their email address is not an acceptable name
for reasons that serve nothing. The permissive rule accommodates both.

## Consequences

**A `LIKE`/pattern operator on `users.username` will now fail.** Non-deterministic
collations do not support them. Audited before the change: every use is equality
— nothing sorts, searches, or pattern-matches on username. A future username
*search* must compare against an explicitly-collated expression rather than
adding `LIKE` to this column.

**Migration 187 refuses to run on an install that already has case-colliding
usernames.** It pre-checks with the same ICU semantics the index will enforce and
raises with the offending pairs listed. It deliberately does **not** auto-merge:
two accounts differing only by case may be two different people, and silently
picking a survivor would hand one person's ledgers to the other.

**Globalization analyzers are now build-breaking.** `.editorconfig` escalates
CA1304/CA1305/CA1310/CA1311 to warnings, and `Api.csproj` already sets
`TreatWarningsAsErrors`. Enabling them found four real culture-dependent call
sites (number and date formatting in tests), all fixed. CA1304/1310/1311 found
none — there was no culture-dependent case folding anywhere, so the invariant
starts clean and can no longer regress silently.

**Display case is preserved.** Usernames store as typed and compare folded, the
standard convention — so `AdaReyes` still renders the way the user wrote it.
