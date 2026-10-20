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

Back up first (System → Backups → Create, then Download, and copy it off the host). Then:

```bash
bash ~/coffer/upgrade.sh            # the latest release
bash ~/coffer/upgrade.sh 0.123.0    # a particular one
```

It fetches the `install.sh` of the release you are moving to and runs it as an
in-place upgrade. That release's script carries that release's upgrade steps (the
database passwords into files, the key volume, the volume hand-over), so an old copy is
never reused. `install.sh` replaces `upgrade.sh` on every run, so the command is always
the current release's. From a private fork it asks for two tokens; see below. It saves
the outgoing `.env` as `.env.bak` (`install.sh` keeps `docker-compose.yml.bak`) and
checks `/readyz` at the end.

`COFFER_VERSION` makes `install.sh` install that release and nothing else (FU-094), and
`upgrade.sh` always sets it:

- the compose file and `db/init` come from the release's tag;
- the image is pulled by the **digest the release recorded** when it was built, which
  `install.sh` writes into `.env` as `COFFER_IMAGE_DIGEST`, beside `COFFER_IMAGE_TAG`.
  Every later `docker compose` command then runs that exact image, whatever the tag
  points at by then.

**An install from before 0.123.0 has no `upgrade.sh` yet.** Upgrade it once by hand, and
the run puts `upgrade.sh` in place. `V` is a version the repo you install from has a
release for.

```bash
cd ~/coffer
# 1. back up (UI: System → Backups → Create, then Download), and copy it off the host
# 2. keep the current config recoverable
cp -a docker-compose.yml docker-compose.yml.bak
cp -a .env .env.bak
# 3. the release to move to
V=0.123.0
# 4. that release's install.sh, then run it; choose "Upgrade in place"
f=$(mktemp) && curl -fsSL "https://raw.githubusercontent.com/strongboxlabs/coffer/v$V/scripts/install.sh" -o "$f" \
  && COFFER_VERSION=$V bash "$f"; rm -f "$f"
```

Without `COFFER_VERSION` the script installs the repo's latest release.
`COFFER_VERSION=latest` restores the old, unpinned behaviour: the compose file from
`main` and the `:latest` image. A release from 0.122.0 on that records no digest is
refused. Releases before 0.122.0 never recorded one, so they install by tag, with a
warning.

**The same version can get a newer image.** Every Monday the latest release is rebuilt
on fresh base images (the .NET runtime and the Postgres client), and its recorded digest
is replaced. To take those patches without changing version, run the same upgrade
again with the same `V`.

Run `install.sh` **as your normal user, not under `sudo`.** It escalates the
individual commands that need it. Under `sudo`, `$HOME` becomes `/root` and you get
a second install at `/root/coffer`, plus a root-owned `secrets/` you cannot read.

It is fetched to a file rather than piped into `bash`, so a DNS or token problem fails
the `curl` and runs nothing, instead of running an empty script without a word.

### Upgrading from a private fork

`install.sh` fetches its config — `docker-compose.yml`, `db/init/` — from
`COFFER_REPO`, which **defaults to the public repo**, so a fork's install sets it, or
gets its own script and someone else's compose file. From 0.123.0 `install.sh` records
it in `.env`, and `upgrade.sh` upgrades from there. A private fork answers an anonymous
request with 404, so `upgrade.sh` then asks for two tokens (or reads `COFFER_GH_TOKEN`
and `COFFER_REGISTRY_TOKEN` when they are set):

- `COFFER_GH_TOKEN`, a **fine-grained** token with *Contents: read* on the fork, fetches
  the script and its config. It is used for the run and stored nowhere.
- `COFFER_REGISTRY_TOKEN`, a **classic** token with `read:packages` and nothing else,
  logs in to ghcr for the image. GitHub documents only classic tokens for its container
  registry.

The first upgrade of a fork's install from before 0.123.0 is by hand, with steps 1–3 as
above. Anonymous `raw.githubusercontent.com` returns 404 for a private fork, so step 4
fetches over the authenticated contents API:

```bash
read -rsp 'Repository token (fine-grained, Contents: read): ' T; echo
read -rsp 'Registry token (classic, read:packages only): ' R; echo
f=$(mktemp) && printf 'Authorization: Bearer %s\n' "$T" | curl -fsSL -H @- \
  -H "Accept: application/vnd.github.raw" \
  "https://api.github.com/repos/<owner>/<repo>/contents/scripts/install.sh?ref=v$V" -o "$f" \
  && COFFER_GH_TOKEN=$T COFFER_REGISTRY_TOKEN=$R COFFER_REPO=<owner>/<repo> COFFER_VERSION=$V bash "$f"
rm -f "$f"; unset T R
```

Both tokens are prompted for and the header reaches `curl` on stdin, so neither lands in
shell history or a command line; the script passes its own fetches' header from an
owner-only file for the same reason. It warns when a token is present but
`COFFER_REPO` is still the default.

The ghcr login is saved in the Docker client's `config.json` (`/root/.docker/` under
`sudo`), base64-encoded rather than encrypted, so later pulls keep working. That is why
the registry token can read packages and nothing else. To keep it out of the file
entirely, configure a [credential helper](https://docs.docker.com/reference/cli/docker/login/#credential-stores)
and run `docker logout ghcr.io` before the upgrade, so the copy already in the file goes
and the upgrade's login lands in the helper. `docker logout ghcr.io` on its own removes a
stored login, and the next upgrade logs in again. Without `COFFER_REGISTRY_TOKEN` the login falls back to `COFFER_GH_TOKEN`,
which is how installs worked before the split, and the script warns about it.

#### Replacing a `repo`-scope token

An install set up with the earlier instructions stored one classic token with `repo` +
`read:packages`. Whoever can read that `config.json` can then read every private
repository the token reaches. To replace it:

1. Create the two tokens above.
2. Upgrade with them, as shown above. The new login replaces the stored one.
3. Revoke the old token at <https://github.com/settings/tokens>. This is the step that
   matters: until it is revoked, any copy of it still works.
4. Remove it from shell history, where the earlier `T='ghp_…'` line put it:
   `grep -n 'ghp_' ~/.bash_history`, then delete those lines with an editor (and run
   `history -c` in any shell that is still open).

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
- **Sessions usually survive.** They live in the database, not in the container, so a
  recreate keeps everyone signed in. The exception is the upgrade that brings migration
  248 ([ADR-0105](decisions/0105-a-cookie-alone-is-not-enough.md)): the session cookie
  is renamed to `__Host-coffer.session`, so everyone signs in again once. That upgrade
  also starts asking for a passkey tap on sensitive actions; see
  [operations.md](operations.md#passkey-taps-and-signing-people-out).
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

It leaves `.env.pre-secrets` behind, which still contains every password. **You no
longer have to remember to remove it:** the NEXT run of the same script deletes it,
overwriting the file first, once `secrets/` holds all three passwords. The first run
keeps it deliberately — if anything about the file-based arrangement is wrong, that
copy is how you get your passwords back — so the cleanup happens one run later
rather than immediately. Shred it by hand if you would rather not wait.

**`pg_hba` hardening.** `initdb` runs exactly once, so the hardened default in
`docker-compose.yml` does nothing for an existing database: it keeps `trust` on its
unix socket and loopback. **`install.sh` now APPLIES the remediation** when it detects
those rules, rather than printing the command and leaving it to you. No restart, no
downtime, and nothing is left half-applied: the script proves both roles can
authenticate before it edits the file and restores the original if they cannot
afterwards.

If it fails, install.sh says so and prints the command to run by hand:

```bash
sudo bash ~/coffer/scripts/harden-pg-hba.sh
``` Afterwards `psql` needs a password:

```bash
sudo docker compose exec -T postgres sh -c \
  'PGPASSWORD="$(cat "$POSTGRES_PASSWORD_FILE")" psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "select 1"'
```

The password is read inside the container from the secret file compose mounts there.
`-e PGPASSWORD="$(cat …)"` would put it on the `docker` command line, where anyone
listing the host's processes can read it.

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

## The master key moves to its own volume (0.121.0)

The key file sat at `/app/data/master.key` on `coffer_data`, the volume that also holds
every backup, so a copy of that one volume carried both. The current `docker-compose.yml`
adds a volume of its own, `coffer_keys`, mounted at `/app/keys`, and points the key path
at it.

**The upgrade does the move.** `install.sh` fetches the new compose file, and the first
start copies `master.key` and the `master.key.*.bak` files rotation kept to the new
volume, checks each copy, then deletes the original; the API log lists what it moved.
Nothing else changes, and the key is the same key.

- **Your own compose file?** Add the `coffer_keys` volume and the
  `COFFER_API__MasterKey__Path` line from the current one, or nothing moves (the key
  stays where it was, which still works).
- **`COFFER_MASTER_KEY_PATH` set?** Nothing moves: your path wins.
- **Host-level backups of `coffer_data`** no longer contain the key. Back up
  `coffer_keys` as well, or keep the key from System → Encryption → Show key.
- **The start refuses, with instructions,** in two cases, and touches neither file: the
  configured location is not on a volume of its own (the path is set but the volume is
  not mounted, so a new container would lose the key), or a different key is already
  there.

Going back to an earlier release after this, as part of the backup-restoring rollback
below, put the key back where that release looks for it before starting it:

```bash
docker compose run --rm --no-deps --entrypoint sh api -c 'cp -p /app/keys/master.key* /app/data/'
```

(run with the current compose file, which mounts both volumes).

## The API runs unprivileged (0.122.0)

Until 0.122.0 the API container ran as root, so a bug in a parser that reads an upload
was root inside it. It now runs as the base image's `app` user, uid 1654. Everything an
earlier version wrote to `coffer_data` and `coffer_keys` belongs to root, so the new
version cannot read its own master key until those files are handed over.

**The upgrade does it.** `install.sh` runs `scripts/own-api-volumes.sh` after pulling and
before starting the new version. The script mounts the volumes from the container about
to be replaced and gives them to uid 1654. It prints how many files it changed, and it
changes nothing on later runs. If it fails, the installer stops with the running
version untouched.

- **Upgraded some other way,** such as a new tag in `.env` under the old compose file?
  The new version refuses to start and keeps restarting. Its log names the files it
  cannot use and prints the command that hands them over, aimed at its own container.
  For a default install it is:

  ```bash
  sudo docker run --rm --user 0 --cap-drop ALL --cap-add CHOWN --cap-add DAC_READ_SEARCH --network none --volumes-from coffer-api --entrypoint chown "$(sudo docker inspect coffer-api --format '{{.Image}}')" -R 1654:1654 /app/data /app/keys && sudo docker restart coffer-api
  ```

  A one-off container takes the API container's volumes and image, and gives `/app/data`
  and `/app/keys` to uid 1654. It runs as root with only the two capabilities `chown`
  needs, and no network. Then the API restarts. With `COFFER_CONTAINER_PREFIX` set, the
  container is `<prefix>-api`. If there is no API container at all,
  `sudo docker compose up -d api` creates one, which refuses the same way; then run the
  command. Afterwards, upgrade with a freshly fetched `install.sh` (above): it also pins
  the image digest and brings the compose file up to the release.
- **Bind mounts instead of volumes?** The same applies: the host directories must belong
  to uid 1654.
- **Rolling back** to an earlier release needs nothing extra for this: it runs as root
  and reads files of any owner.

## Access that narrows (0.122.0)

Nothing to run. Some people will notice the difference
([ADR-0105](decisions/0105-a-cookie-alone-is-not-enough.md) and
[ADR-0083](decisions/0083-multi-user-management-invites.md), 2026-10-19 amendments).

- **Apps connected through MCP sign-in more than 90 days ago ask to connect again**, on
  their next token refresh. A connection now lasts 90 days from approval.
- **An app whose connection holds no `coffer.read` is refused** with 403
  `insufficient_scope` and must connect again. Connecting again grants it.
- **Editors can no longer connect or disconnect a bank, map an account to one, or add
  or remove a notification target.** Those are the owner's now. Editors still sync.
- **An admin's pending invite into a ledger they don't own stops working.** Only an
  owner of a ledger can invite someone into it. Have an owner send a new one.
- **`/openapi/v1.json` is not served** outside Development.

---

## A sale cannot take more shares than were held (0.124.0)

Nothing to run. Two things change ([ADR-0064](decisions/0064-fifo-cost-basis.md),
2026-10-20 amendment):

- **An investment save that would sell more shares than the account held at that date is
  refused** (`investment-txn-insufficient-shares`), as is one that makes an existing
  shortfall worse. Before, it was accepted, the holding went negative and the gain was
  overstated.
- **Positions already short are reported.** After the upgrade, the consistency check lists
  any position a past sale or an import left short, under Settings → General, and the
  monitor's warning names it. They are not repaired automatically, since the purchase
  they are missing is not in the data: correct the sale, or enter that purchase.

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
