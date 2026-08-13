# Upgrading an existing install

Day-to-day running lives in [operations.md](operations.md). This doc is only about
moving a **live install** from one Coffer version to the next — the operations
where the install already has state, so getting the order wrong can leave it worse
than either version.

> Written after a v0.44.0 upgrade went badly on a real host: `install.sh` moved the
> database role passwords out of `.env`, then failed to resolve the compose file it
> had just fetched, leaving the install half-migrated. Everything below either
> prevents that shape of failure or tells you how to recognise it.
> That path is now covered by an automated upgrade rehearsal before each release.

---

## Before you start

**Take a backup and move it off the host.** Schema migrations apply on the first
boot of the new version, and they are not reversible — see *Rollback* below.

**Per-ledger snapshots do not survive a schema change.** `LedgerSnapshotsRepository`
refuses a restore whose snapshot schema doesn't match live, and snapshot payloads are
not migrated forward. If a snapshot is doing safety-net duty for you, act on it
first. Whole-DB `.cofferbak` backups are unaffected — they carry their schema and
migrate forward on restore.

**Check what you're jumping.** `docker compose images api` shows the version you're
on. Read the release notes between that and the target, because a migration that
tightens a constraint is the kind that can fail on real data.

---

## The upgrade

```bash
cd ~/coffer
# 1. back up (UI: System → Backups → Create, then Download), and copy it off the host
# 2. keep the current config recoverable
cp -a docker-compose.yml docker-compose.yml.bak
cp -a .env .env.bak
# 3. upgrade
bash install.sh            # choose "Upgrade in place"
```

Run `install.sh` **as your normal user, not under `sudo`.** It escalates the
individual commands that need it. Under `sudo`, `$HOME` becomes `/root` and you get
a second install at `/root/coffer`, plus a root-owned `secrets/` you cannot read.

### Installing from a private fork

`install.sh` fetches its config — `docker-compose.yml`, `db/init/` — from
`COFFER_REPO`, which **defaults to the public repo**. If you fetched the script itself
from your own fork, set the variable too, or you get your script and someone else's
compose file:

```bash
COFFER_REPO=<owner>/<repo> COFFER_GH_TOKEN=<pat> bash install.sh
```

**Getting the script in the first place**, when the fork is private: anonymous
`raw.githubusercontent.com` returns 404, so fetch it over the authenticated contents
API and pass the same token through, which also covers the ghcr login for the image:

```bash
T='ghp_your_classic_pat'
COFFER_GH_TOKEN=$T COFFER_REPO=<owner>/<repo> bash <(curl -fsSL -H "Authorization: Bearer $T" -H "Accept: application/vnd.github.raw"   "https://api.github.com/repos/<owner>/<repo>/contents/scripts/install.sh?ref=main")
```

Fetch to a file first if you'd rather see a failure than have an empty script piped
into bash — a DNS or token problem otherwise runs nothing, silently.

A PAT needs `repo` (private config) and `read:packages` (private image). The script
warns when a token is present but `COFFER_REPO` is still the default.

`COFFER_IMAGE` is required by the compose file — there is no default, because the
release workflow publishes to `ghcr.io/<the building repo's owner>/coffer` and no
single value is right for every fork. `install.sh` derives it from `COFFER_REPO` and
writes it into `.env`, backfilling it on upgrade if your install predates the
variable, so you normally don't touch it.

Set it by hand only if you mirror the image or push it to a private registry — and
in `.env`, never in `docker-compose.yml`, since `install.sh` replaces that file:

```bash
echo 'COFFER_IMAGE=ghcr.io/<owner>/coffer' >> .env
```

---

## What to expect on the first boot

- **Migrations apply.** `Applied N migration script(s)` in the log; the dump-borne
  schema is brought forward to the running version.
- **All sessions end.** DataProtection keys are not persisted outside the container,
  so a recreate invalidates auth cookies. Sign in again with your passkey.
- **Secrets sealed under the master KEK survive** as long as the KEK does — bank-feed
  tokens keep working, the stored backup passphrase stays usable, Drive stays
  connected. If any of those come back needing re-auth after a *same-key* upgrade,
  something went wrong: stop and check the key file.

Verify before moving on:

```bash
sudo docker compose exec -T postgres psql -U coffer -d coffer -tAc \
  "select max(substring(scriptname from '^[0-9]+')::int) from __schema_migrations"
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/readyz
```

---

## Two migrations that don't apply themselves

Both only affect installs created before the relevant release. Both are idempotent,
and both refuse to act unless they can first prove your current credentials work.

**Role passwords into files.** `install.sh` does this on its upgrade path. Standalone:

```bash
COFFER_DIR=~/coffer bash scripts/migrate-db-secrets.sh
```

It leaves `.env.pre-secrets` behind, which still contains every password — `shred` it
once the install is confirmed healthy.

**`pg_hba` hardening.** `initdb` runs exactly once, so the hardened default in
`docker-compose.yml` does nothing for an existing database: it keeps `trust` on its
unix socket and loopback. `install.sh` fetches the remediation and prints the command
when it detects those rules:

```bash
sudo bash ~/coffer/scripts/harden-pg-hba.sh
```

No restart, no downtime. Afterwards `psql` needs a password:

```bash
sudo docker compose exec -T -e PGPASSWORD="$(cat ~/coffer/secrets/postgres_password)" \
  postgres psql -U coffer -d coffer -tAc 'select 1'
```

---

## The master key: from `.env` to a file

ADR-0092 moved the master key out of `COFFER_MASTER_KEK_BASE64` into a file, honouring
the variable for one release and copying it into the file on first boot.
[ADR-0094](decisions/0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md) closed
that window: **the variable is no longer read at all.**

**Coming from 0.43.0 or later, there is nothing to do.** Your first boot on 0.43.x
already wrote the key file, and the file has been authoritative since.

**Coming from 0.42.x or earlier, do this before upgrading.** Your key exists only in
`.env`, so the new build finds no key over a database full of wrapped material and
**refuses to start** — correctly, because minting a fresh key would orphan it. Put the
key in the file first (the `coffer_data` volume is mounted at `/app/data`):

```bash
grep '^COFFER_MASTER_KEK_BASE64=' .env | cut -d= -f2- > master.key
docker compose cp master.key api:/app/data/master.key && rm master.key
```

Then upgrade. If you find out the hard way — the container refusing to boot — the same
command fixes it, and the startup error names it too. The fallback it also names,
`--adopt-new-kek`, mints a fresh key and **abandons** the three sealed secrets (feed
tokens, stored backup passphrase, Drive connection); it is for when the old key is
genuinely lost, and it exits after writing so you must remove the flag before starting
normally. Ledger data and passkeys survive either route.

**Then remove the line from `.env` — but not before the new compose file is in place.**
Compose files from before ADR-0092 declare the variable **required** (`:?`), so deleting
it while one of those is active breaks interpolation and every `docker compose` command
with it. The app keeps running, but you cannot manage it.

```bash
sed -i '/^COFFER_MASTER_KEK_BASE64=/d' .env
sudo docker compose config >/dev/null && echo "compose still resolves"
```

Back up the key from **System → Encryption → Show key**, not from `.env` — after a
rotation the `.env` copy opens nothing, and now nothing reads it either.

---

## Rollback

**Schema migrations are not reversible, and retagging the image is not a rollback.**
Older application code can violate constraints a newer migration added — as one
example, migration 189 made `txn_headers.transacted_at` NOT NULL, and code that
predates it does not populate that column, so inserts fail.

So the real rollback is: restore the backup you took, running the version it came
from. Which is why the first step of this document is taking one.

Config-only changes *are* reversible: `docker-compose.yml.bak` and `.env.bak` from
the steps above, plus `pg_hba.conf.pre-harden` inside the Postgres volume if the
hardening needs undoing.

---

## If an upgrade fails midway

`install.sh` is built so this shouldn't strand you: it proves the fetched compose
resolves before removing anything from `.env`, and puts the previous compose back if
it doesn't. If it stops with *"The fetched docker-compose.yml does not resolve"*:

- Your `.env` is untouched and the install is as it was.
- `secrets/` may have been written. Nothing reads it yet, so that's inert.
- The usual cause is config from the wrong repo — re-run with `COFFER_REPO` set.
