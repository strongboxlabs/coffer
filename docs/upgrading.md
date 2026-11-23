# Upgrading an existing install

Day-to-day running lives in [operations.md](operations.md). This doc is only about
moving a **live install** from one Coffer version to the next — the operations
where the install already has state, so getting the order wrong can leave it worse
than either version.

> An upgrade that stops partway can leave an install half-migrated: for example, the
> database role passwords moved out of `.env` while the fetched compose file does not
> resolve. Everything below either prevents that shape of failure or tells you how to
> recognise it. Before each release, an automated drill upgrades an install of the
> previous release to the new one.

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
the outgoing `.env` as `.env.bak` (`install.sh` keeps `docker-compose.yml.bak`), one
generation of each. A run that did not finish leaves both as they are, so repeating it
keeps the state from before the first attempt; they are replaced only once a run has finished.

**Both scripts then wait for `/readyz`**, which answers only once the new release's
migrations have run: up to `COFFER_READY_TIMEOUT_SECONDS`, default 600 seconds (the API's
own limit on one migration step), printing progress every 30 seconds. Set it on the
command to wait longer:

```bash
COFFER_READY_TIMEOUT_SECONDS=1800 bash ~/coffer/upgrade.sh
```

A timeout ends only the wait, not the migration: the API carries on, and the script exits
with an error that says so. Follow it with `cd ~/coffer && sudo docker compose logs -f api`.
An `upgrade.sh` from before 0.128.0 does not wait itself: it runs the target release's
`install.sh`, which waits from 0.128.0 on, and then checks `/readyz` once. So an upgrade
from 0.127.0 to 0.128.0 waits in 0.128.0's `install.sh`, and the variable still applies.

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

An upgrade started from the app keeps no login: its run uses a Docker config of its own and logs
out of ghcr.io when it ends ([Upgrading from the app](#upgrading-from-the-app)).

### Upgrading from the app

On a Linux host with a cron daemon, where your user can run docker without `sudo`, an admin
can start the same upgrade from **System → Upgrade** instead of a terminal. It takes a fresh
backup first, labelled "before upgrade X to Y", then a small updater on the host runs
`upgrade.sh` for it. Everything above still applies: it is the same script, and each release's
`install.sh` still carries its own upgrade steps. [operations.md](operations.md#upgrading-from-the-app)
has the commands and what to do when it fails, and
[ADR-0110](decisions/0110-upgrade-from-the-app.md) the reasons.

- **The first upgrade of an existing install to a release that has this is by hand**, with
  `upgrade.sh`. That release's `install.sh` puts the updater in place, adds the `coffer_mailbox`
  volume through the new compose file, and installs the cron line. Until then the tab says it
  has not heard from a host updater, and shows the manual command.
- **What changes on your host:** one line in the install user's crontab, a directory
  `~/coffer/upgrades/` for the updater's state, and one more Docker volume. Nothing is added to
  the API container beyond that volume: no Docker socket, no capability, no new user.
- **Turn it off** with `bash ~/coffer/app-upgrades.sh disable`, or switch off *Allow upgrades from
  this app* in the tab. The first stops the host acting on any request, and the second stops the
  app writing one.
- **Behind it is a trust you already extend** when you run `upgrade.sh` by hand: the release's
  `install.sh` runs on your host. The difference is that an admin's passkey tap now starts it.
  Anyone who can run code as the install user, or in the docker group, can start one anyway.
- **A private fork** needs the host's two tokens set once with
  `bash ~/coffer/app-upgrades.sh set-token`, stored as `0600` files in `~/coffer/upgrades/secrets/`
  (the tokens above, "used for the run and stored nowhere", are the by-hand path). An upgrade
  started from the app does not leave the registry login in the install user's `config.json`: its
  run uses a Docker config of its own and logs out of ghcr.io when it ends. An upgrade by hand
  still keeps its login there.

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
  schema is brought forward to the running version. They run before the API answers
  anything, so `/readyz` refuses the connection until they finish. An upgrade that
  rewrites data across every ledger makes that wait longer, and its section below says
  so (0.128.0 is one).
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
`--adopt-new-kek`, mints a fresh key and **abandons** the sealed secrets (feed tokens,
stored backup passphrase, Drive connection, a private fork's upgrade repository token, notification
targets' URLs and tokens, and the OAuth server keys MCP sign-in uses), clearing them so nothing is left that the new key cannot open,
irreversibly. It is for when the old key is genuinely lost; run it as a one-shot container
(see [Starting without the master key](#starting-without-the-master-key-01270)). Ledger
data and passkeys survive either route.

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

## Sidebar tabs are removed, and folders are new (0.126.0)

Nothing to run. The sidebar changes ([ADR-0106](decisions/0106-folders-replace-tabs.md)).

- **Every user's sidebar tabs are deleted by the upgrade** (migration 258), with the
  list of accounts each one held. Tabs only filtered the account list, so no account,
  transaction, balance or category is touched. They are not turned into folders: a tab
  could hold an account another tab also held, and tabs were private to one user while
  folders are shared. To get a grouping back, make it as folders.
- **Folders are new.** Inside each account type's section, folders hold accounts and
  other folders, to any depth. Everyone with access to the ledger sees the same folders.
  Owners and editors make a top-level folder with the new-folder button (a folder with a
  plus) on a section heading, and a sub-folder with "New folder inside…" on a folder's
  menu. They move accounts and folders by dragging them, or with "Move to" on the row
  menu. Deleting a folder moves what it held up a level; it never deletes an account.
- **The sidebar shows balances**, and a total on each folder and each section.
- **The sidebar collapses and resizes.** It starts at its usual width. Its width, whether
  it is collapsed and which folders are open are remembered per browser, not per user, so
  another browser or machine starts at the default.
- **For anything calling the API directly:** the `…/account-groups` endpoints are gone.
  MCP `list_accounts` returns two new fields per account, `folderId` and `folderName`.
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see *Before you start*).

---

## Balances stop where the register's Scheduled begins (0.126.1)

Nothing to run. A current balance now counts exactly what the register does not show as
Scheduled ([ADR-0056](decisions/0056-ledger-overview-dashboard.md), 2026-10-21
amendment), and an unpriced position is valued at its last trade price
([ADR-0084](decisions/0084-trade-derived-security-prices.md), 2026-10-21 amendment).

- **Balances stop where the register's Scheduled begins.** A transaction the register
  shows as Scheduled (dated after your own date) no longer counts in a current balance;
  the moment your date reaches it, the register stops calling it Scheduled and it counts,
  with nothing to run. This is the sidebar's accounts, folders and section totals, the
  dashboard's accounts and net worth, the portfolio card, the securities catalog and a
  security's detail figures, the "you hold" value on the unbacked-price advisory in
  ledger settings, and MCP `list_accounts`, `net_worth` and `account_portfolio`. Until
  now they counted every row entered, whatever its date,
  so an account with a scheduled payment or a trade entered ahead will show a different
  figure after the upgrade. Nothing is recalculated or stored by the upgrade.
- **"Your date" is the browser's.** The app sends its local date with every read of a
  current value, as the register already did, so the two agree in every time zone at
  every hour. MCP and anything else that sends no date get the register's own fallback,
  the server's date in UTC. MCP
  `holdings_snapshot` and `allocation`, and the allocation report, asked for no instant,
  now value the end of that day rather than the moment of the request, so a transaction
  stamped later today counts there as it does everywhere else.
- **Investments value what is held today.** A buy, sale, split or price dated after your
  date does not move today's value. A price older than a split since is restated onto
  the split basis, as the net-worth history already did. Which securities the quote
  refresh fetches follows the same rule: one sold by a future-dated sale is still fetched
  until that date, and one bought by a future-dated buy from that date.
- **Unpriced positions use the last trade price.** A position with no price dated on or
  before today is valued at the unit price of that security's most recent trade in the
  ledger, and only if it never traded at a price, at its cost basis. Before, the
  dashboard and the other current figures carried it at cost basis, and the net-worth
  history (and so returns, allocation and MCP `holdings_snapshot`) at the last price on
  any of that account's transactions, share movements included, or at zero. Both now use
  the one rule, so the history's points for such a holding change, and its last point
  agrees with the dashboard for every position, priced or not. The securities catalog and
  a security's detail show that last trade price, dated by the trade.
- **A managed loan payment's split is priced as of the payment's own date**
  ([ADR-0078](decisions/0078-managed-loan-payment-reminder.md), 2026-10-21 amendment), not
  on the loan's latest balance: a payment dated after it no longer counts against it,
  whenever or however it is posted.
- **Unchanged:** the register still lists a future-dated row, as Scheduled, with the
  running balance it will leave. An auto-posted reminder no longer lowers today's
  balances before its due date.
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see *Before you start*).

---

## Starting without the master key (0.127.0)

Nothing to run. Two changes to what happens when the key file is missing
([ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md) D3, 2026-10-27 amendments).

- **The start refuses once the install has started before.** The check that decides
  whether a missing key may be replaced by a new one now counts the OAuth server keys MCP
  sign-in uses. The first start stores them, sealed under the master key, so any install
  that has started once holds sealed material, and a start without the key **refuses**,
  naming the key path and `--adopt-new-kek`. Before, an install with no ledger, backup
  schedule, Drive connection or notification target minted a new key instead, and MCP
  sign-in could no longer open its keys.
- **`--adopt-new-kek` clears what the lost key sealed.** It now replaces the ledger keys
  and clears the bank-feed tokens, the stored backup passphrase, the Drive connection,
  the notification targets' URLs and tokens, and the OAuth server keys, records that in the admin audit
  log, lists how many of each it abandoned, and only then saves the new key and exits.
  Before, it left all of them in place: MCP sign-in stayed off and every key rotation was
  refused. **This is irreversible:** once adopted, the old key turning up later recovers
  nothing.
- **A start with `--adopt-new-kek` and a key in place refuses** and says to remove the
  flag. Before, a restart with the flag still set served normally, and a later start that
  found no key would have minted and wiped again.
- **Starts that could mint a key take turns.** Two at once (two adopt runs, or an adopt
  run and a restarting container) that share the key volume now end on one key that opens
  everything; one that cannot see the other's key file finds its secrets sealed and refuses.
- **If you meet the refusal,** put the install's key back at the configured path (System →
  Encryption → Show key is where you saved it from). Only if the key is lost, adopt a new
  one in a one-shot container, which runs once and is removed (`run` gives it no restart
  policy, so nothing reruns the flag):

  ```bash
  sudo docker compose stop api
  sudo docker compose run --rm api --adopt-new-kek
  sudo docker compose up -d api
  ```

  The run stores OAuth server keys under the new master key before it exits: the key files
  of a release before 0.121.0 if they are still on the data volume (MCP clients stay signed
  in), otherwise a new pair (every MCP client connects again). If a key file IS present but
  is the wrong one (a mis-mounted volume, another install's key), the run refuses: move
  that file aside, keeping it, then run it again. Given the flag, a start never serves:
  with nothing sealed to wipe, it saves its key, says so, and exits.
- **If `--adopt-new-kek` fails,** it exits non-zero and says why, and no key is saved.
  Run the same command again once the cause is fixed; that is safe whichever step failed.
- **A brand-new install** still creates its own key on its first start, and now saves it
  only once it has stored its OAuth server keys under it. A first start that cannot store
  them saves no key and exits with an error; its restart tries again.

---

## Ledgers imported from Moneydance are brought up to the current import (0.128.0)

Nothing to run, but **the first start after this upgrade takes longer** (see *The first
start* below). Migrations 260 to 264 and 266 run on the first boot. An install on 0.127.0
already has migration 265; the others run after it, which is safe because 265 touches
none of their tables.

Migration 260 touches only ledgers created by the Moneydance import, bringing them to
what an import made today would write
([ADR-0027](decisions/0027-investment-action-catalog.md) and
[ADR-0100](decisions/0100-canonical-holds-current-sidecar-holds-original.md), 2026-10-21
amendments). It fills only what the import left empty: anything you have set since is
kept. Migrations 263 and 264 touch every ledger: a transaction's memo and tags become
each side's own ([ADR-0108](decisions/0108-memo-and-tags-per-side.md)), and Moneydance
ledgers' memos and tags are brought to where the import places them today. Migration 266
removes what the old way of reading them needed.

### Moneydance ledgers (migration 260), and the folder trigger (migration 261)

- **Brokerage cash moves get the Xfr action.** A single cash move in or out of a
  brokerage that Moneydance recorded as a plain or QIF-imported cash row (a deposit, a
  withdrawal, a contribution) had no action, so where its other side was a category the
  register showed none and the editor asked you to pick one. It is now an Xfr. A move
  split across several accounts or categories is left as it was.
- **Investment returns can change for those brokerages.** An Xfr whose other side is a
  category, such as an employer contribution arriving, now counts as money put in or
  taken out, not as the portfolio's own gain or loss, so the returns of a brokerage with
  such moves can differ after the upgrade.
- **Those rows are edited from the brokerage.** Like every other investment transaction,
  they are now read-only in the other side's register (the category's, or the bank
  account's): edit or delete them from the brokerage register. A category register can
  still move one to another category.
- **Brokerage reminders are investment reminders.** A Moneydance reminder on a
  brokerage with a single split (and no investment action of its own) is now an Xfr
  reminder, like the cash move it creates, and fires from the brokerage. Occurrences
  already fired from such a reminder get the Xfr action too, unless you have since
  changed their accounts or split them.
- **Investment rows get Moneydance's check number back.** The import dropped it on
  investment transactions. They now show it under the action, as Moneydance had it
  (Auto, for example, or the action word of a QIF import). As in Moneydance, it also
  appears in a bank register's Check # column on an investment row that touches that
  bank account.
- **The bank's original payees and memos are recorded.** Where Moneydance kept the bank's
  own text beside the payee or memo you cleaned up, it is stored as the transaction's
  original in `txn_header_originals`, as if your cleanup had been an edit in Coffer.
  Payee suggestions can then match on the bank's text. What the register shows does not
  change.
- **New imports from this version on** also put a blank-named top-level Moneydance
  category that has no sub-categories on the ledger's Uncategorized category (an expense
  category, whether the Moneydance one was income or expense). A blank-named category
  that has sub-categories, or that sits under another category, keeps its own
  "(unnamed)" row. An existing ledger keeps its "(unnamed)" category.
- **Migration 261 drops the sidebar folders' cycle trigger**; nothing to run. The API
  still refuses moving a folder into itself or one of its sub-folders, as before
  ([ADR-0106](decisions/0106-folders-replace-tabs.md), 2026-10-22 amendment).

### Memo and tags are per side (migrations 263, 264 and 266)

- **Each side of a transaction has its own memo and tags.** On a transaction between two
  accounts, or an account and a category, each register shows that side's own memo and
  tags, and a side with none of its own shows the other side's, marked with a small
  arrow. Typing a memo or picking tags on one side changes only that side; clearing it
  shows the other side's again. The upgrade moves existing values onto the sides so that
  every register shows what it showed before, except for the changes listed here.
- **A split keeps a memo and tags for the whole transaction**, on the row of the account
  that split it, beside each line's own; a line with nothing of its own shows nothing
  there. Seen from a line's other account, **every line is now its own row**, read-only:
  the register no longer groups a split's lines into one row, and it offers no Edit,
  Duplicate or Delete on such a row (Delete was not offered on a read-only row before
  either). Change the transaction from the account that split it.
- **An investment event has one memo and one tag set**, edited from the brokerage. Where
  values sat on the legs of an event with several postings, they join the event's own:
  every distinct memo, joined in posting order with " · ", and every tag. No memo is
  lost. A Moneydance event's split texts join its memo by the same rule, on this upgrade
  and on a new import: each memo once, where it first appears, so "a", "b", "a" become
  "a · b". Memos are compared after trimming and with case kept, so "Trip" and "trip"
  are both kept. An edit that gathers memos onto an event (setting an action that
  changes its postings, or converting an in-kind transfer) joins them the same way.
- **A split line seen from its category no longer falls back to the transaction's memo
  and tags.** It shows the line's own, or nothing; the register's tag filter there
  follows what the row shows.
- **A memo or tags held on one side's leg now show on the other side, inherited**, where
  that side showed nothing before. A one-split's own leg tags, which no register showed,
  now show on that side.
- **Blank memos are cleared.** A memo of only spaces hid what the row would otherwise
  show; it now counts as no memo, and the row shows what it inherits. Memos are stored
  trimmed from now on.
- **Tags that differ only by case become one tag**, the one created first. Every
  transaction keeps the tag, under that spelling, and saved reports filtered on a merged
  tag follow it, every member's. From now on a tag named like an existing one but for
  case or surrounding spaces is that tag.
- **Merging tags in Settings repoints saved reports** filtered on the merged-away tag,
  every member's included.
- **Moneydance memos and tags move to where the import places them today**, on rows not
  edited since: a one-split's on each side, a split line's tags with the transaction's
  merged in, an event's onto the event. Tags Moneydance kept on investment transactions
  and reminder templates, which earlier imports dropped, are added, as new tags where
  needed, including a tag you have since deleted in Coffer.
- **A set the system merges can hold more tags than the editor allows** (this upgrade, an
  import, collapsing a split to one line, converting an in-kind transfer). Nothing is
  dropped: a save refuses only a set that grows past the limit, and saving such a set
  unchanged or smaller succeeds.
- **Reminders keep tags.** The reminder form's Tags field is saved and loaded, and each
  fired occurrence carries the template's tags.
- **An in-kind share transfer with no brokerage leg becomes a sale at its cost.** A
  transfer of shares out of a brokerage whose other side is a category or another
  non-brokerage account, holding no brokerage leg of its own, names no brokerage to own
  it. Migration 263 rebuilds each as a sale with a transfer to that same account, priced
  at what the shares it moved cost by their lots ([ADR-0108](decisions/0108-memo-and-tags-per-side.md)
  D18). Shares and cost basis do not change, and no gain or loss is booked: the
  transaction now reads as a sale, and realized-gain reports list it with a gain of
  zero (one cent where that cost ends in exactly half a cent). Where a purchase of
  those shares was edited after the transfer, the transfer's amount, and the balance
  of the account on its other side, move to that cost. Where the shares were held partly over a year and partly under, at different
  costs, the long-term and short-term parts show equal and opposite figures, as for any
  sale at cost. Edit it from the brokerage like any other sale.

### Other changes in this release

- **Auto-post sets aside a reminder it cannot post and names it.** A reminder whose
  template has no lines, or a bank reminder with a line on an inactive account, is no
  longer attempted; the run's notice, webhooks included, names up to three of each with
  what to do. Posting one by hand is refused with the same reason.
- **An edit keeps an inactive account the transaction or reminder already carries**, and
  refuses one newly added. Creating or firing onto an inactive account is refused.
- **A save that keeps the date keeps the stored time of day**; only a changed date
  changes it.
- **Accepting or merging a row puts it at its sorted place** and refreshes the running
  balances, as a reload would.
- **The bulk-action footer counts read-only rows.** A selection's count includes the rows
  the register shows read-only and names them beside it, as in "15 selected · 3 read-only".
  The total beside it sums those same rows, read-only rows included, each by its own amount
  in the register (with sub-categories shown, theirs too), so a select-all and an explicit
  selection of the same rows show the same total. What each bulk action changes is as before.
- **Select-all no longer reaches a reminder's template.** On an account a reminder posts to,
  selecting every row with no filter counted the reminder's template, which the register
  does not show, and a bulk delete of that selection failed. The selection now covers only
  the rows the register shows.
- **A read-only row's badge moves out of the payee cell.** The "↗ Split" or "↗ Investment"
  badge now sits in the column after the date, so the payee and memo keep the whole cell.
  In a bank or category register it sits above the check number, and the Check # column
  is wider to hold it; in a brokerage register it sits on a line of its own above the
  action.
- **The split editor is laid out like the one-split editor.** Its "Memo (whole
  transaction)" and "Tags (whole transaction)" fields sit side by side, in the columns the
  one-split editor's Memo and Tags use. In both editors the Similar and Merge candidates
  hints under Payee take a row of their own, so Tax date, Memo and Tags stay level
  whatever shows there.
- **An editor's tag field reads like its memo field.** Tag names the side holds itself
  show in the normal text colour; inherited ones keep the arrow and the lighter colour.
  An empty tag field reads "Tags (optional)". The register's tag filter is unchanged.

### Importing from Moneydance

- **The in-app wizard is the only way to import.** The `coffer-import-moneydance` tool's
  `import` and `prune-batch` commands are removed; its read-only `audit` and `reconcile`
  diagnostics stay.
- **An import always creates a new ledger**, owned only by the user who imports, even
  under the name of a ledger that user already has.
- **The wizard's last step lists the import's failed checks and the transactions it
  skipped.**
- **The wizard shows an import as finished only once it is.** The status could show
  "succeeded" a moment before the import was done, when the new ledger's Activity entry
  could still be missing and a new upload could be refused as already running. The
  status now changes last, after both are settled, for a failed import as well.

### The first start

- **It takes longer than usual.** Migration 264 is followed by a C# step,
  `264_stored_leg_notes_backfill`, that computes the stored memo-and-tags presentation of
  every transaction in every ledger. The API answers nothing until the migrations and
  that step have finished: `/readyz` refuses the connection meanwhile. 0.128.0's
  `install.sh`, which the upgrade runs whichever `upgrade.sh` starts it, waits for
  `/readyz` for up to `COFFER_READY_TIMEOUT_SECONDS`, default 600 seconds, showing
  progress while it waits (see *The upgrade*). A timeout stops only the wait, not the
  migration, and the run exits with an error. The log shows
  `Stored notes computed for N transaction(s).` when the step ends, then
  `Applied N migration script(s)`; `cd ~/coffer && sudo docker compose logs -f api`
  follows it until then.
- **Restoring a whole-database backup from an earlier release** migrates it forward
  through the same steps, so its first start takes as long.
- **The upgrade stops rather than start on data it cannot read.** Migrations 263 and 264
  end by checking, over every transaction, the invariants the new rules rely on (a
  one-posting transaction holds no transaction-level memo or tags, the investment side
  of a multi-posting event holds none of its own, every transaction's originating
  account is on one of its legs). A failure stops the start; the log names the
  migration and what broke, and that migration changes nothing. The backfill step
  likewise journals nothing when it fails, and the next start runs it again.
- **Migration 263 also stops on in-kind transfers it cannot rebuild.** Its message
  counts, as (c), the in-kind transfers with no brokerage leg that are not the one shape
  it rebuilds as a sale (see *An in-kind share transfer with no brokerage leg* above).
  Then 263 changes nothing, and nothing after it runs: restore the backup you
  took and run the release it came from (see *Rollback*), and report the counts from the
  log with the issue.

### Snapshots

- **Snapshots grow.** A snapshot now also carries each leg's stored memo-and-tags
  presentation and its tag rows, and every table is written in chunks.
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see *Before you start*). A later release that changes how memo and tags
  are presented retires older snapshots the same way.

### For anything calling the API directly

- **`GET …/transactions` requires `account_id`** and answers 422 without it; there is no
  ledger-wide listing.
- **Bulk selections require `accountId`.** `…/transactions/selection-summary`,
  `bulk-recon-status`, `bulk-delete`, `bulk-unhide` and `bulk-move-account` answer 422
  (`account-not-in-ledger`) to a selection without one; there is no ledger-wide selection.
- **An explicit selection that names a row the register shows read-only** is refused
  whole by `bulk-delete`, `bulk-unhide` and `bulk-move-account`, with 422
  (`selection-includes-read-only`), and nothing is changed. `bulk-recon-status` is the
  exception: a row's status is the account's own, so it accepts read-only rows. A
  select-all leaves read-only rows out of delete, unhide and move, and includes them in
  recon status.
- **`selection-summary` answers `{ count, totalCount, readOnlyCount, sumOnAccount }`.**
  `count` is still the rows a bulk change acts on. `totalCount` is every row the selection
  covers and `readOnlyCount` how many of those are read-only, and `sumOnAccount` now sums
  every row `totalCount` counts, read-only rows included. No selection matches a
  reminder's template.
- **Memo and tags travel in one `notes` member** on the bank transaction and bank reminder
  create, edit and fire bodies (`side`, `memo`, `tags`, and per line `lines`), replacing
  every top-level and per-posting memo and tag field. An omitted key leaves the value;
  `null`, `""` or `[]` clears it, which on a one-split means "inherit the other side's".
- **A bank or investment PATCH, approve and merge answer `{ entries }`**: every entry the
  transaction has in the register named by `account_id` (one, or one per line on a
  split's other side), instead of a single entry. With no register named, or no entry
  of the transaction in it, 204.
- **`hasOverrides` is gone** from the register row.
- **The plain `…/fire` route is gone**; use `…/fire/bank` or `…/fire/investment`.
- **A reminder edit whose source account differs from the stored one** is refused with
  422 (`reminder-source-immutable`).
- **`InvestmentMergeCandidateDto` gains `derivedAction`, `counterpartyAccountId` and
  `counterpartyAccountName`**, and its amount and shares are now the register row's
  values. The bank `MergeCandidateDto` keeps its shape; its memo and tags are now the
  ones the source register shows, and each posting's `legMemo` is that line's memo.
- **MCP `set_transaction_tags` requires `accountId`**, the register the tags are written
  from. It reports per transaction where the tags land (`side` or `transaction`), and it
  refuses the whole call, tagging nothing, if any target is a row that register shows
  read-only.
- **MCP `list_transactions` without `accountId` returns one line per category line**: a
  split with several category lines appears once per line, where it appeared once. Each
  line's `memo` is what that line's register shows, `transactionMemo` carries the
  transaction's memo on the split side's lines, and `tags` and the `tagId` filter follow
  the tags in play there.

---

## The Overview becomes a home screen (0.129.0)

### The new Overview

- **The Overview answers what needs you first.** A line under the title counts open
  problems, rows to review, overdue reminders and reminders to post in the next week;
  each count links to where it is dealt with. Below it, in a main column: Problems, Bills
  & income (post or skip a reminder from here), To review, This month and Cash flow. In
  a side column: Feeds & checks, Net worth (with month-end bars) and Investments (when
  the ledger has an investment account).
- **Customise is on the page.** It edits the layout in place: order, column, what is
  shown, and each widget's settings. **Settings → Dashboard is gone**; an old link to it
  opens General.
- **Your layout carries over; nothing to do.** A layout you never changed becomes the
  new default. A customised one keeps its order and what you hid, the new widgets are
  added in their places, and Bills & income keeps looking 30 days ahead, as Upcoming did.
  The first change you save stores it in the new format.
- **Accounts is hidden after the upgrade**, whether or not you had customised the
  layout: the sidebar lists every account with its balance. To show it again, choose
  Customise and add it from the hidden widgets.
- **A total that spans currencies shows "mixed"**, as the sidebar does, instead of a sum
  in one currency's format.
- **Viewers** see the same counts and problems, and can customise their own layout;
  posting, skipping and syncing are for owners and editors.

### A switched-off job's problem clears (migration 267)

- **A job that switched itself off stops being a standing problem once it works
  again.** After repeated failures a scheduled job turns itself off and raises a
  Critical problem. That problem now clears when you turn the job back on and its next
  run succeeds, in the sidebar, the bell and the events panel alike. Two jobs switched
  off at once are two problems.
- **Nothing to do on upgrade.** Migration 267 moves the existing ones to the same
  footing, so one already raised clears the same way.
- **Webhooks:** the event key is now `<job type>.disabled` (`feed-sync.disabled`,
  `backup.disabled`, and so on) instead of `scheduler.job-disabled`. The topic
  (`scheduler`) and the severity are unchanged. Update any filter written against the
  old key.

### Other changes in this release

- **Red only for a negative that is news, and nothing is green.** The investment
  register shows a debit in red and a credit in the normal text colour, as the bank
  register does. Holdings and Security detail show a gain in the normal colour and a
  loss in red, and a percentage carries no leading plus. Reminder amounts, and the lines
  of a future-dated split, are muted.
- **This month's Spent on the Budget page includes rows dated later this month, and
  says how much.** It counted them before without saying so; the hero and the Total row
  now read "incl. $Y scheduled". The projection carries your pace so far and adds each
  scheduled bill on its own date, marked on the curve, instead of spreading it across the
  month. "Day D of N" follows your date, not the server's. In a ledger that spans
  currencies, the hero and the Total row read "mixed", as the sidebar and the Overview
  do, instead of a sum with a caption, and the hero draws no curve.
- **Reports end "the last 12 months" at your today**, not the server's UTC date. The
  Total row is the server's total. Grouped by category kind, a report reads "Income vs
  expense", with rows "Income", "Expenses" and "Adjustments".
- **Reports in a ledger that spans currencies read "mixed"** where the Overview's Cash
  flow does: the Total row, and the rows of an income-vs-expense report. Other rows keep
  their figures. A chart of totals (by kind, or stacked) says the totals span currencies
  instead; grouped bars and the ranked view still draw. A chart's figures and axis now
  use the ledger's currency, not always dollars.

### For anything calling the API directly

- **`GET …/preferences/dashboard`** returns the stored document as stored (version 1 or
  2) with the row's `updatedAt`. **PUT** takes a version 2 document and the `updatedAt`
  it read: 422 `dashboard-prefs-stale` if the layout changed since, 422
  `dashboard-prefs-invalid` for a malformed document or a version 1 body. **DELETE**
  resets the layout.
- **`GET …/overview`** takes `since` (a month-end before `today`, which it then
  requires) and adds `comparison`; 422 `overview-since-invalid` otherwise. `portfolio`
  gains `cashBalance` and `mixedCurrency`, and each account `isActive`.
- **`GET …/reports/net-worth-history`** reports the ledger's display currency in
  `currencyCode`, no longer always `USD`, and adds `mixedCurrency`, a `noHistory` flag
  per point and an optional `today`.
- **`GET …/reports/summary`** takes an optional `today` and adds `periodTotals`,
  `groupTotals`, `currencyCode` and `mixedCurrency`.
- **`GET …/budget/progress`** takes an optional `today` and adds `scheduledTotal`.
- **`GET …/reminders/upcoming`** occurrences add `sourceAccountId`, `sourceAccountName`,
  `currencyCode` and `autoPost`.
- **`GET …/reports/returns` and `…/reports/allocation`** take an optional `today`.
- **New reads:** `GET …/notifications/outstanding`, `GET …/schedules` and
  `GET …/balances/consistency/state`.
- **MCP:** `net_worth_history` reports the ledger's display currency and `noHistory`;
  `list_upcoming_reminders` occurrences carry their source account, currency and
  auto-post verdict.

---

## The home screen acts on review (0.132.0)

### Before you upgrade from 0.131.0

- **Migrations 273, 274 and 275 run on boot and rewrite no row.** 273 re-issues four
  database functions: the rule that decides which trades price a security's day, its
  checker, and the two that list the prices no trade backs. It drops a fifth,
  `fn_price_is_unbacked`, which nothing calls. 274 makes that rule name the currency of
  the brokerage that made the trade that prices the day, has the writer store it and
  the checker compare it, and drops two functions nothing calls any more: the writer's
  old form, which stored every trade price in dollars, and `fn_trade_price_for_day`, the
  rule's price alone. 275 adds one write lock per ledger (`fn_ledger_write_lock`, and a
  session form for a snapshot restore), which every change that can recompute a ledger's
  running balances or positions takes before anything else; re-issues the two functions
  that rewrite running balances and positions so that each takes that lock and refuses a
  change that wrote without it; and re-issues the snapshot lock so that a snapshot create
  comes before it.
  There is nothing to run by hand. They are migrations, so the backup
  [Before you start](#before-you-start) asks for is the way back ([Rollback](#rollback)).
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see [Before you start](#before-you-start)). Migrations 273, 274 and 275
  change no row, but a snapshot is tied to the schema it was taken on.
- **The scheduled consistency check may report a problem once after the upgrade.** It
  runs every 12 hours. This release lets it see figures an earlier version could leave
  stale or unstored, and stops the writes that left them, but rewrites none of them on
  boot. What it can report on an existing install:
  - **balances**: a running balance that was never stored, or one left stale by
    deleting a transaction another was merged into;
  - **holdings**: a position that was never stored, a stored one still showing shares
    of a closed position, or one left stale by a single delete or restore of an
    imported investment row, or by deleting a transaction another was merged into;
  - **trade prices**: a day priced by a trade since deleted, hidden, merged away, moved
    or re-dated while another trade remains on the day; a day a re-date or a merge left
    unpriced; a day an earlier Moneydance import priced from a share movement while a
    trade on that day sets the price; a day priced in dollars by a trade in a brokerage
    kept in another currency;
  - **holdings currency**: a brokerage whose holdings are still recorded in the currency
    it had before its currency was changed in an earlier version. The row reads
    "Holdings currency — N of M brokerages out of step", and lists each one as
    "*brokerage* · its holdings are recorded in X, but the brokerage is in Y".

  **Check consistency** under Settings → General → Maintenance names them, and the
  Overview's Problems links there as "Check and repair →". Each one's **Repair** clears
  it: it writes what the transactions imply, except **Repair holdings currency**, which
  records each brokerage's holdings in its own currency; nothing is converted, and no
  price or transaction changes, a trade price being in its brokerage's currency
  already. The two can be repaired in either order; the Overview's
  Problems keeps the problem, saying what is left, until the last one is repaired. Nothing
  else is needed. A day with no trade left keeps its price and is listed under prices no
  trade backs, which is not a problem.

### On the home screen

- **To review lists the rows waiting for review.** Under the per-account counts, the
  newest rows across the ledger, each drawn as its register draws it. **Review…** opens
  the row's own editor in a dialog, so you accept it, fold it into its match or correct
  it without leaving the Overview; the row then leaves the list and the counts drop.
  Its settings choose how many rows show and whether they are grouped by account, and
  "+N more" links to the rows the list leaves out, per account.
- **Two new widgets, hidden until you add them** from Customise's hidden widgets:
  **Recent transactions**, the newest entries across the ledger, each opening its
  register at the row (settings: how many, and all, money out or money in), and
  **Watchlist**, up to eight categories against their marks this month. Adding
  Watchlist opens its settings to choose them; cancelling adds nothing.
- **Investments leads its return with "Gain incl. income"**, the change in value over
  the window that money in and out does not explain. Its figures are in the investment
  accounts' own currency rather than the ledger's, and when the investment accounts
  span currencies the gain, both rates and the allocation read "mixed". The one-year
  return no longer fails on Feb 29.
- **The Overview's return for a window counts what is dated on its first day.** A
  dividend, interest or a fee dated the day the window opens is that window's return;
  it was taken into the starting value and counted in no window. The figure is computed
  on every read.
- **Over the mark means past it by any amount.** This month's rows over the mark, the
  Budget page's count of categories over target and its table's Difference, Watchlist
  and the mark bars call a category over as soon as its spending, as printed, is past
  its mark, where an overspend of less than a whole unit was left out or shown as "—".
  The Difference shows "+" in red whenever a category is over; a projection landing
  exactly on the mark is at it, not past it. A month whose marks add up to zero is
  judged the same way: any spending is past the mark, and the Budget page and This month
  date the crossing as for any other mark, where they named none and said the month
  stays inside it.
- **"On track for" counts a payment that leaves early in the month once.** The Budget
  page's "on track for, by month end", its dashed line and its crossing date, and This
  month's dashed line and projected date, are now what you have spent so far plus the
  larger of the bills still scheduled this month and what you usually spend over the
  rest of the month, read from the same months the shaded band draws, less every refund
  still scheduled this month, in full and on its date. They carried your
  spending so far forward as a daily rate, which multiplied a monthly payment made in
  the first days, or a one-off, across the whole month, so early in a month they ran
  too high and named a crossing date too early. A month running hotter than usual still
  projects higher, a ledger with no history projects what is spent plus what is
  scheduled (the month's Spent), a scheduled refund's date gets a circle on the curve
  like a bill's, and the hero's sentence reads "If the rest of the month goes as usual, you
  cross it on the …". The crossing date, on the Budget page and on This month, is the
  day the month goes past the ceiling for good: a month that passes it and comes back
  under, to end inside it, names none, and the hero then reads "On track to finish
  inside it." A later month, which has not started, shows no outlook, where it said "On
  track to stay inside it." even with its scheduled rows already past the ceiling.
  In a month shorter than the months before it, the shaded band
  and the projection count what those months spent on the days this month does not
  have on its last day, where the band left it out. Nothing is stored; the figures are
  computed on every read.
- **The spend curve keeps every point inside its frame**, on the Budget page and on This
  month. Some points were drawn past the frame's edge and cut off: a projection or a
  running total below zero, a running total that peaked earlier in the month and then
  fell with a refund, and a shaded band that is highest before the month ends. The
  scale now spans every point it draws.
- **A new ledger's usual figures are what it spends, not a fraction of it.** The Budget
  page's typical figures, the mark of every category without a target, its "usually"
  total, the shaded band and the projection's usual rest of the month, and This month's
  and Watchlist's marks, averaged the trailing months whether or not the ledger existed
  in them, so a ledger younger than the window read low in its first months. Each day
  of the month now averages only the months in which the ledger already existed on that
  day, counting from its first transaction. The captions say what was averaged: "your
  last month, same days", "unset = average of the 2 months before …", "Fill from 2-month
  average" and "typical, 2 months" when the history starts on a month's first day;
  "your months since …" and "typical, since …" when it starts mid-month; "unset = no
  history before …" in the ledger's first month. A ledger with the whole window of
  history sees no change. Nothing is stored; the figures are computed on every read.
- **The Overview follows your edits**: back on it after a save, an accept, a merge, a
  delete, a bulk action or an import in a register, or after a change to a security or
  its prices, an account's active switch, a feed's account bindings or a tag, its
  figures and lists are the new ones, as they already were after a sync or a category
  change.
- **The Overview shows what Check consistency finds.** Check consistency under Settings →
  General → Maintenance records its verdict as the scheduled check does: a problem it finds
  is on the Overview's Problems at once, and "consistency checked …" says when you ran it.
  Both kept what the scheduled check had last found, up to twelve hours earlier, so the
  Overview could read "No open problems" beside a check that had just found one. The check
  still repairs nothing, and running it again with nothing changed sends no notification.
- **A repair leaves the problem on the Overview until nothing disagrees.** Repairing one of
  two figures that disagree cleared the Overview's Problems row while the other was still
  out of step. The row now stays, saying what was repaired and what is still to fix, until
  the last repair. The notice a repair sends says what it rewrote and what remains: it could
  say nothing needed changing after rewriting a price, or that a figure remained after a
  repair that had fixed it. That notice is a warning while something is still to fix, so a
  notification destination that takes warnings hears it, and the all-clear once nothing is
  left. After a repair the panel shows the result at once, without checking again.
  Closing the page while a repair runs no longer loses its record. Once a repair has
  rewritten the figures, they stay rewritten. If the check that follows the repair fails,
  the panel says the repair stands and that what still disagrees is not known until the
  ledger is checked again, instead of "Could not repair." The Overview keeps the problem
  and reads "not checked yet" until the next check, which the scheduled check runs within
  fifteen minutes. A repair that fails partway still shows its error, but what it had
  already rewritten stays rewritten. The panel drops the report taken before the repair,
  with its Repair buttons, and says the repair did not finish, that part of it may stand,
  and that Check consistency shows what still disagrees; that check brings the Repair
  buttons back. The registers show what it rewrote at once, and the Overview reads "not
  checked yet" until that next check. The panel kept the report taken before the repair,
  and the registers and the Overview what they showed before it. When no answer comes back
  from a repair, because the connection dropped or a proxy timed out, the repair still runs
  to the end: the panel drops the report and says the repair may still be running and that
  it is recorded when it finishes. One repair of a ledger runs at a time: a repair pressed
  while another of the same ledger runs, from another tab or after an answer that never
  came back, is refused; before, two repairs of the running balances or of the positions
  at once could fail one of them partway. A repair refused before it starts, such as one sent
  after the session expired or while another runs, rewrites nothing: the panel keeps the
  report, with its Repair buttons, and shows why. Check consistency
  waits while a repair runs, so a check no longer reads the ledger partway through the
  rewrite and shows that report after the repair has answered, and the error of a repair
  before it goes as the check starts, where it stayed under the new report. A viewer is
  no longer offered Repair, which the server refused, and the panel's drift notice no
  longer tells a viewer that each figure can be repaired there. Under Settings →
  Notifications, an
  unresolved drift finding in Recent problems reads "Details" for a viewer and opens
  General without running the check, as the Overview's Problems does, where it offered
  "Check and repair".
- **Customising:** the hidden widgets are listed in one fixed order; Esc inside a
  widget's settings cancels only those settings; a widget's open settings stay open
  while it moves to the other column; focus moves into the settings when they open and
  back to the widget when they close; and while the layout is being edited a right-click
  on a row opens no menu. While Done saves, every control of the layout edit is pending
  and Esc does nothing: Reset to default, Cancel, the columns, each widget's move, hide
  and settings, Add, and an open panel's Apply. Customise is pending while the layout
  loads, and disabled only if loading it failed. A grip that cannot drag shows no grab
  cursor.
- **Viewers** get Open → on each row to review, into a register that is read-only for
  them (below).
- **A page scrolls inside its pane, never the whole window.** Past the end of some
  pages, the Overview among them, the window scrolled too and took the top bar and the
  rail with it.
- **The Overview's short dates, without the year, have no leading zero on the day**, as
  the registers write a date: "overdue since Nov 5" and "Nothing due through Nov 5",
  where they read "Nov 05".

### Across the app

- **A button or field whose request is in flight is pending, not disabled.** It keeps
  focus and its place in the tab order, refuses a second press, Enter, Space and a
  form's submit, never changes size, and keeps its error until the new result replaces
  it. A field refuses typing and changes while its form's request is in flight, so
  nothing typed meanwhile is dropped when the request lands and clears or closes the
  form; a rename form's Cancel and Esc wait for its save. Disabled now means
  unavailable, not busy.
- **Focus never falls to the page when a control goes away.** When a row is removed
  from its own menu or button, focus goes to the same control on the next row, else the
  previous row, else the list's add control; a list with no add control takes focus
  itself. A control that replaces itself hands focus to what takes its place: a rename
  form or a report's Save as… form, closing, to the button that opened it; a Drive
  disconnect to Connect Google Drive; a removed upgrade token to the token field;
  hiding the backup passphrase to Show; the budget's Target cell between its button and
  its number field. Under Settings → General → Maintenance, a repair that takes the
  report away hands focus from whichever control in the report held it (a Repair or a
  link to what disagrees) to Check consistency; a repair that heals its figures takes
  only its own Repair away, with its row's links, and focus goes from them to the next
  Repair, else the previous one, else Check consistency. Security prices and sleeves
  can now be reached by keyboard.
- **A dialog opens on the field its content focuses.** Add security opens on Name, Add
  price on Price, and a reminder's Post or series editor, once what it shows has
  loaded, on the transaction's Date, where they opened on the ticker, the date and the
  dialog's Close. A dialog that opens as another closes does the same.
- **A control that waits on a read is pending while the read runs.** The budget's Copy
  last month is pending from the press, so pressing it again no longer posts another
  fill. Under System → Backups, Create backup is pending while the backup schedule is
  read, and the passphrase status says Loading… until it is known, where it said Not
  set.

- **Lists keep one order.** Accounts with the same name could come back in a different
  order on each load, and so could other rows that tie on what a list is sorted by: ledgers
  with the same name, securities, reminders with the same payee on the same day, saved
  reports, budget categories, bank connections, sessions, passkeys, MCP connections and the
  MCP and admin audit logs, provider runs and their errors, merge and payee suggestions, and
  the rows of a report or an investment summary. The tie is now broken by a fixed key, so
  every load shows the same order.

### In the registers and dialogs

- **A memo fits its text.** An empty memo whose hint runs to two lines, or a memo of
  more than one line, showed a scrollbar it did not need, in the register's editor and
  in the dialogs. It is fitted again when the window, the rail or a phone's orientation
  changes its width.
- **The editors no longer offer "Use *account*'s".** Over a memo or a tag set this side
  has of its own, the Memo and Tags captions offered a link that cleared it, so the other
  side's showed. Emptying the field does the same, and is now the one way back: the field
  then shows the other side's value, captioned "from *account*".
- **"From *account*" names the account in the field, not the one the row was stored
  with.** The caption, the arrow in the field and the tag control named the stored
  account: on a row awaiting review, Uncategorized while the field held the category
  you picked, and the old category after you changed it. In the bank and category
  registers the other line moves to the new category or account with its memo and tags,
  so the editor now shows them from there. In the investment editor the inherited memo
  and tags show only while the transaction still saves as one posting across from the
  account they come from. Change the transfer or category to another account, pick an
  action that posts elsewhere or writes several postings, such as Buy, Sell or
  Reinvest, or add a fee, and they no longer show: a save onto another account does
  not keep them, and with several postings the save makes the shown value the
  transaction's own, as on a split.
- **Viewers see no control they cannot use**: no New transaction or N key, no inline
  edit, only Show other side and Show raw data in the row menu, no bulk actions, no
  Import or Sync, and a status badge that does not cycle. Selecting rows shows their
  count and sum, and a viewer's sum is now the selection's, where it read 0.
- **The reminder dialogs and Review… lay the editor out for a dialog**: Date, Check #,
  Payee, Category and Amount across on a wide screen, and one field per row with larger
  fields and buttons on a phone, where a split's lines scroll sideways.
- **Esc closes one thing at a time.** In an open payee, security, category or tags
  list, the first Esc closes the list and keeps the edit; the next cancels the edit, or
  closes the dialog. Esc in a dialog closes that dialog only, never one behind it or the
  edit that opened it; a menu's Esc puts focus back where it was; and an Esc that
  cancelled an edit no longer also clears the register's selection. Enter in Add
  security, opened from the investment editor, creates the security instead of saving
  the transaction. A list inside a dialog is no longer cut off at its edge.
- **The Filter, Show and Sort popovers and a report's range close on Esc** and give
  focus back to their button, after a pick too. Before, they closed only on a press
  outside.
- **The date jump is a popover, not a dialog.** It has no backdrop; a press outside
  closes it and still reaches what it landed on, and a dialog opened over it sits above
  it.
- **Tab out of the date jump goes on from where you opened it.** Tab from Jump moves to
  the control after the one that had focus when you pressed Ctrl/Cmd+J, and Shift+Tab
  from the date field goes back to that control, as Tab does through the Filter, Show
  and Sort popovers from their buttons. Tab from that control goes back into the date
  jump. Before, Tab from Jump left nothing focused. Opened from the transaction list,
  Shift+Tab goes back to the list itself, not to a row's checkbox or status. The date
  jump stays open while focus is elsewhere, until Esc, a press outside or the shortcut
  closes it. When what you opened it from leaves the page, it stays open with your date
  and then sits at the start of the page, as it does when opened with nothing focused.
  That happens to the transaction list, a row's controls, an inline edit's buttons and
  the bulk-action bar's buttons when typing a filter, changing the sort or a refresh
  replaces the list, and while the list is loading or empty; to a row's controls when
  the row scrolls out of view; and to a control that takes itself away, such as a
  filter chip you remove, the bulk-action bar's buttons when the selection clears, or
  an editor's buttons when the edit closes. Opened from a control those leave in place,
  such as New transaction or the Filter button, it stays right after that control.
  Ctrl/Cmd+J does nothing in a field, so it never opens from the search box. Sitting at
  the start of the page, it is reached by Tab from the browser's address bar or past
  the page's last control, into its date field, and by Shift+Tab from the page's first
  control, into Jump. Sitting right after what you opened it from when nothing on the
  page follows that, such as the transaction list with nothing after it or the
  bulk-action bar's last button, it is reached by Shift+Tab from the address bar, into
  Jump. Closed by Esc, Cancel, Jump or the shortcut when it was opened with nothing
  focused or what you opened it from has gone, it gives focus to the transaction list,
  or, when the list is not there, to New transaction or the search box, and the list
  takes it back when it arrives if you have pressed nothing since. Focus you moved
  elsewhere first stays there. A press outside leaves focus where the press put it, and
  the list does not take that focus when it arrives.
- **Register shortcuts leave a key pressed in a popup to it.** N, the arrows, Enter
  and Ctrl/Cmd+J do nothing when the key was pressed in a dialog, a menu or a list.
- **The register editors refuse Esc while their save is in flight**, and the bank
  editor a press outside too, so the edit is not cancelled under a save that lands.
- **The category picker, opened on a value it does not offer** (the system
  Uncategorized category, an inactive category or account), shows the full list with
  its text selected until you type.
- **A Filter edit that changes nothing keeps the register's selection.**
- **Focus lost in a register goes to its row list**, or to New transaction or the
  search box when the list is not there.
- **The Uncategorized category's chip and a file import's icon are grey.** Uncategorized
  is where unclassified rows land, not a category you chose, and amber only ever means
  a warning.
- **More figures are in their account's currency, not dollars by default.** A
  register, its selection sum and the reminder dialogs wait for their account instead
  of showing "$" for a moment, and New transaction waits with them. The investment
  register's price, fee and settled amount, a Post or skip subtitle, a security's page
  and its trades (a closed brokerage's included), the securities catalog and a tag's
  transactions are each in their own account's currency, and a total over several
  reads "mixed". Structure's Categories tab shows each category's total in the
  ledger's currency, as the Budget page and Reports do, since a category has no
  currency of its own, and a kind's total reads "mixed" when the ledger's accounts span
  currencies. A reminder's amount on
  the calendar, the list and Bills is in its source account's currency; a series with
  no single source shows a dash. The unbacked-price advisory under Settings → General
  names each holding value's currency, and shows none for one held across currencies.
  The Accounts widget's subtotal reads "mixed" when a closed account it counts is in
  another currency. A sale's lot preview is in the brokerage's currency. The register's
  amount filter chip reads "Amount: min – max", with no "$". A register whose account cannot be found says so instead of loading for
  good.
- **A security's page names each trade under the brokerage it belongs to.** A
  transaction that touches two brokerages, an in-kind transfer between them or a trade
  funded from another brokerage's cash, could be listed under either one and in its
  currency. Shares leaving now show under the source brokerage, shares arriving under
  the destination, and a funded trade under the brokerage it was made in, each in that
  brokerage's currency.
- **A security's unrealized gain that prints as a zero is a plain zero**, where a
  holding priced at exactly what was paid could show a red negative zero. When the gain
  reads "mixed" because only the price is in another currency, its hover text says so,
  where it said the accounts span currencies.
- **A merge candidate's hint names the row it keeps** by where that row came from: your
  own entry, or a row imported earlier.
- **The register and the editor say Dividend, Reinvest and DividendXfr** for the
  dividend actions they called Div, DivReinvest and DivXfr, and so does a merge
  candidate: each action has one word, the one the Overview's new lists show, everywhere
  it is shown. An in-kind share transfer's chip reads Transfer shares, the editor's word
  for it, where it showed `transfer_shares`. A security's page names the action of each
  of its transactions with the same words, where it showed the stored value
  (`dividend_reinvest`, `divx`, …). Only the words change; what is stored, the action
  value the API returns and the words an imported file uses are as they were.
- **The investment register's Action sort is alphabetical by the word each row
  shows**, Buy to Xfr, where it followed the stored value, which put Reinvest between
  Dividend and DividendXfr.
- **The investment register writes a date as the bank register does**, with no
  leading zero on the day: May 1, 2026, where it wrote May 01, 2026.
- **Prices worth a look, under Settings → General → Maintenance, write their days as
  the registers write a date**: "on Dec 1, 2026", where they read "on 2026-12-01".
- **An account's count of rows to review is the number of rows its register lists
  awaiting review.** A split awaiting review with two lines on one other account is two
  rows in that account's register, and now counts two there, where it counted one. The
  sidebar mark, the Overview's To review, the "N to review" on Structure's Accounts tab
  and the register's needs-review count agree on every account. The count is read on
  every request. The sidebar mark's tooltip and the words a screen reader hears say
  "N to review" too, where they said "N transactions to review" ("1 transactions" to a
  screen reader).
- **Scrolling back up a long register no longer skips a row** at the edge of each
  page it loads.
- **The register's amount filter and amount sort read the amount each row shows.** A
  split entered from another account with several lines on this one is a row per line
  here; each line's row is now filtered and sorted by its own amount, as it is drawn,
  where all of them read the transaction's net on this account.

### Balances, holdings and trade prices

What an existing install may hold from before is listed under
[Before you upgrade from 0.131.0](#before-you-upgrade-from-01310); these are the changes
behind it.

- **The consistency check reports a running balance that was never stored.** It
  compared only the stored rows that exist, so a transaction whose balance was never
  written checked healthy. Such a row is now reported, as missing, and the balances
  repair writes it.
- **The consistency check reports a holding that was never stored, and a stored
  holding that still shows shares of a closed position.** It compared only the
  positions both the stored holdings and the walk of the transactions held, so both
  checked healthy. The first is now reported as missing and the second as a quantity
  that should be 0, and the holdings repair writes what the transactions imply.
- **Deleting an imported investment row takes its shares out of the position, and
  restoring it puts them back.** Deleting one row that came from an import or a feed
  hides it, and restoring one unhides it; neither recomputed the holdings (the bulk
  delete and restore did), so the Portfolio and the Overview kept the shares of a
  deleted row. Both now recompute the position as they save, as a merge and a date
  edit do. A date edit or a merge that moves a trade to another day now also prices
  that day.
- **A trade leaving a day leaves the day priced by the trades that remain.** With two
  trades of a security on one day, deleting the later one, moving it to another day or
  another security, or bulk-deleting it kept the departed trade's price on the day,
  while the consistency check expected the remaining trade's, so the scheduled check
  reported a trade-price problem after ordinary edits. Each of these now re-derives the
  day as it saves; a day with no trade left keeps its price, as before, and the panel
  lists it under prices no trade backs. A security merge now also prices the keeper's
  days from the trades it moves, which it left unpriced.
- **A deleted imported trade no longer prices its day.** Deleting a row that came from
  an import or a feed hides it, and a hidden trade, like one merged into another, was
  already left out of every valuation but still set its day's price; deleting a trade
  entered by hand, which removes it, did not. Now a hidden or merged-away trade prices
  no day: deleting such a row, restoring it, merging it and deleting the row it was
  merged into, singly or in bulk, each re-derive the day from the trades that remain.
  Migration 273 is this rule.
- **A trade price is in the currency of the brokerage that made the trade.** Every
  price a trade set was stored in dollars, whatever currency the brokerage is kept in,
  so a security priced by a trade in another currency showed that price in dollars on
  its page, in the securities catalog and in the unbacked-price advisory. A trade now
  prices its day in its brokerage's currency; when one security trades in two
  brokerages on one day, the day takes the currency of the one whose trade sets its
  price. The consistency check compares the currency as well as the price. Nothing
  converts. Migration 274 is this rule.
- **Changing a brokerage's currency carries to its holdings and its trades' prices.**
  The shares of a brokerage sit on a holdings account of its own, which you never edit;
  changing the brokerage's currency left that account in the old currency. The edit now
  moves it too and re-prices the brokerage's trade days in the new currency, and the
  consistency check reports a brokerage whose holdings are in another currency than its
  own.
- **A Moneydance import prices no day from a share movement.** The import's own pass
  over its trades still let a ShrsIn or ShrsOut set its day's price, though every other
  writer and the consistency check have left share movements out since they stopped
  counting as trades. Only a new import is affected.
- **Deleting a transaction another one was merged into brings that one back with its
  balances.** The merged row returns to the register when the row it was folded into
  is deleted, singly, in bulk or by converting a sale and a purchase into an in-kind
  transfer, and each delete now recomputes it: the balances of every account it has a
  line on, a feed row's Uncategorized line included, and the position of any shares it
  holds. Before, only the deleted row's own accounts were recomputed.
- **A bulk delete or restore acts only on the rows it found.** Each read the selection
  again at each step, so under a filtered select-all a row another request moved into
  the filter while it ran could be deleted, hidden or, from the Hidden view, restored
  with none of its balances, positions or trade prices recomputed. The selection is now
  read once and every step acts on those rows.
- **Changes to one ledger take turns.** When two changes to the same ledger arrive
  together, the second now waits for the first to finish. A change here means saving,
  deleting or restoring a transaction, a bulk change, an import or a feed sync, a
  reminder posting, a merge, a snapshot restore or a repair. Two saves at once, or a
  save during a repair, could fail with an error and save nothing, or store a running
  balance that left the other one out; now both are saved with the right figures.
  Reading, taking a snapshot and changes to another ledger do not wait. A change that
  would wait longer than about 15 seconds is turned away with "Another change to this
  ledger is still being saved. Nothing was changed; try again in a moment.", and trying
  again works. A feed sync turned away this way saves nothing it fetched, and the next
  sync fetches the same rows; an automatic reminder posting turned away this way is
  posted on the next run. A repair turned away partway keeps what it had already
  repaired, and says so instead: "Another change to this ledger is still being saved, so
  the repair stopped. Anything it had already repaired stays repaired; repair again to
  finish." Migration 275 is this change.
- **Tags, budget fills, bank feed connections and ledgers take their turn too.** Merging,
  deleting or cleaning up tags, a budget's Copy last month and Fill from average, deleting
  a bank feed connection and deleting a ledger recompute nothing, but each locks or writes
  rows another change to the ledger can be holding while it waits for one of theirs.
  Deleting a connection while its sync was saving, merging a tag while a transaction was
  being tagged with it, or filling a budget while a category was being merged could fail
  with an error and change nothing. Each now waits for the change in flight, and past
  about 15 seconds is turned away with the same sentence.
- **An automatic reminder posting that both failed and was turned away says how many of
  each.** A run in which some occurrences failed and others waited too long for another
  change to the ledger counted only the failures, as if they were all there was: "all 1
  due occurrence(s) failed" for a run of three. It now reads, for example, "Auto-post
  posted nothing: 1 of 3 due occurrence(s) failed, and 2 were deferred because another
  change to this ledger was still being saved (the next run posts them). See the server
  log for per-occurrence detail."
- **A bank feed sync or a file import whose save fails closes its run.** A sync whose save of what it
  fetched failed for any reason but another change being saved left its run showing as
  running for up to 10 minutes, during which the connection could not be synced again, and
  a scheduled sync's failure could stop the scheduler's pass there, leaving the jobs due
  after it to the next pass. The run now closes as failed with "The sync could not save
  what it fetched, so nothing it fetched was saved; the next sync fetches it again.", and
  the scheduled pass goes on. A file import whose save failed that way left its run showing
  as running for good; it now closes as failed with "The import could not save what it
  read, so nothing it read was saved; import the file again."
- **Repairing posting counts while a transaction is saved keeps the right counts.** A
  repair that reached a transaction while a save adding a split to it was in flight could
  write the counts from before the save over the save's, which the next check reported.
- **Turning trade commission on or off changes the setting and the positions together.**
  A recompute of the brokerage's positions that failed left the setting changed and the
  positions as they were.

### For anything calling the API directly

- **A write to a ledger can answer 422 with the new code `ledger-write-busy`.** Every
  write that can recompute a ledger's running balances or positions, every consistency
  repair and a snapshot restore take the ledger's write lock first and wait for another
  write to the same ledger that holds it, at most half the database command timeout (15
  seconds by default). Past that the request answers 422 `ledger-write-busy` with the
  detail "Another change to this ledger is still being saved. Nothing was changed; try
  again in a moment.": nothing was written, and sending the same request again once the
  other write has finished succeeds. Reads, snapshot creates and writes to other ledgers
  never answer it. A consistency repair takes the lock one piece at a time and keeps the
  pieces it rewrote before a refusal, so its 422 `ledger-write-busy` carries the detail
  "Another change to this ledger is still being saved, so the repair stopped. Anything it
  had already repaired stays repaired; repair again to finish." `POST …/sync-all` answers
  200 and names such a connection with `failureCode` `ledger-write-busy`, carrying on to
  the next one.
- **`POST …/tags/{tagId}/merge`, `DELETE …/tags/{tagId}`, `DELETE …/tags/unused`,
  `POST …/budget/targets/fill`, `DELETE …/feed-connections/{connectionId}` and
  `DELETE /api/ledgers/{ledgerId}`** take the ledger's write lock first too, so each can
  answer 422 `ledger-write-busy` with nothing changed. A tag merge checks that both tags
  exist after it has the lock, so a tag deleted while it waited answers its not-found
  refusal.
- **`PATCH …/accounts/{accountId}`** reads the account after it takes the ledger's write
  lock, so every field the request does not name is written back as the last edit of the
  account committed it. An edit of the same account saved while this one was being made
  could be undone by it, field by field.
- **`POST …/balances/consistency`** replaces `GET …/balances/consistency`, which no longer
  answers: the check records its verdict. It writes the ledger's stored verdict
  (`GET …/balances/consistency/state`) and, when that verdict is not already what the
  stored verdict or the ledger's outstanding notices say, the scheduled check's own
  `consistency.drift` or `consistency.ok` event. It heals nothing. Same `today`, same
  report, same access: any grant on the ledger. The stored `lastCheckedAt` is when a check
  began to read the ledger, where it was when the verdict was written. A check, scheduled or
  not, that began before the stored verdict's check does not overwrite it or announce.
- **`POST …/balances/consistency/{projection}/repair`** answers `{ projection, repaired,
  after }`, where it answered one projection's report, the check before the repair for
  every projection but `trade_prices`, which answered the check after it. `repaired` is the
  check the repair worked from, what it rewrote; `after` is the whole report, every
  projection checked again once the repair committed. It takes `today` as the check does,
  and records `after` as the ledger's verdict with `lastRepairedAt`, where it stamped
  `lastRepairedAt` alone. Its `consistency.repaired` event is info only when `after` is
  healthy and a warning otherwise, so the ledger's `consistency.` subject stays outstanding
  until nothing disagrees. Its detail has `projection`, `rewritten` (what the repair
  rewrote), `remaining` (that projection after the repair), `healthy` (the ledger after it)
  and, while unhealthy, `still_to_fix`. Once the repair has begun, the rewrite, the check
  after it and the record run to the end even if the client hangs up. When the check after
  it fails, the answer is still a 200 with `after` null. `lastRepairedAt` is stamped and
  the verdict taken before the repair is cleared (`healthy` and `lastCheckedAt` null, as
  after a restore). The event is a warning that says what was rewritten and that what still
  disagrees is not known; its detail has `projection`, `rewritten` and `checked: "false"`.
  When a check that began after the repair has recorded first, `after` is not written and
  the event follows the verdict that check recorded: info only when it is healthy, a summary
  saying what that check found, and a detail of `projection`, `rewritten`,
  `superseded: "true"`, `healthy` and, while unhealthy, `still_to_fix` as the stored
  projection names without counts. When a restore or a later repair has cleared the verdict
  since, the event is a warning saying what still disagrees is not known, its detail
  `projection`, `rewritten` and `superseded: "true"`. A verdict a repair cleared is cleared
  as of its rewrite, so a check or a repair that began after the rewrite writes its verdict
  over it, whenever it finishes. The event is written together with the verdict it follows,
  so no other check or repair records between the two. When the rewrite itself throws
  partway, the answer is still the error, and what it committed stands: the verdict taken
  before it is cleared all the same, as of when the rewrite stopped, while `lastRepairedAt`
  keeps the last repair that completed and no event is recorded, so the subject stays as it
  was. A repair sent while another repair of the same ledger runs, of any projection,
  answers 422 with the new code `consistency-repair-in-progress` and rewrites nothing.
- **List reads break ties by id**, so two reads list tied rows alike: `GET …/accounts` (same
  name), `GET /api/ledgers`, `GET …/securities`, `GET …/reminders` and
  `GET …/reminders/upcoming`, `GET …/reports/saved`, `GET …/budget/progress` rows,
  `GET …/feed-connections`, the sessions, passkeys, MCP connections and audit, the admin
  audit, the provider runs and a run's errors and promotions, the merge and similar-payee
  suggestions, `GET …/reports/summary` rows, group totals and its top N, and
  `GET …/reports/allocation` and `GET …/reports/returns`.
- **New read: `GET …/transactions/entries`** (`needs_review`, `direction`
  `all`/`inflow`/`outflow`, `today`, `limit` 1 to 50, default 50): one first page,
  newest first, of the register entries of every account a person reads (not a
  category, not a holdings sibling, inactive accounts included), each as its own
  account's register returns it, with that account's id and currency, and `hasMore`.
  There is no cursor. `direction` is a cash-flow direction, by the sign of the amount
  the entry's row shows; anything else is 422 `transaction-entries-direction-invalid`.
  A `limit` outside its range is 422 `register-limit-invalid`. `today` leaves out the
  entries dated after it. `GET …/transactions` still requires `account_id`.
- **`GET …/accounts`** `needsReviewCount` counts an account's register entries awaiting
  review, as the register's needs-review status count does, no longer distinct
  transactions.
- **`GET …/transactions`** (one register): `amount_min`, `amount_max` and
  `sort=amount` read the amount each entry's row shows, so a line of another account's
  split on this one is filtered and sorted by its own amount, not the transaction's net
  here. A `direction=after` page no longer drops the entry next to its cursor. On a
  brokerage's register, `sort=action` orders by the word each row's Action chip shows,
  where it ordered by the stored action value; the `action` each entry returns is
  unchanged.
- **`POST …/transactions/selection-summary`** is a read: a viewer gets the count and
  sum, where it was refused with 422 `ledger-not-writable`. The bulk writes beside it
  still refuse a viewer.
- **`GET …/transactions/{headerId}/merge-candidates` and
  `GET …/investment-transactions/{headerId}/merge-candidates`** add `origin` to each
  candidate (`manual`, `online_import` or `file_import`), as its register row carries.
  The bank read's candidates drop `memo`, `tags` and `daysDelta`, and their postings
  `amount` and `legMemo`: each carries what its chip shows, the posted date, the payee,
  the posting's counterparty account and the origin.
- **`GET …/budget/transactions`** lines add `legId`, the line's own leg: a split's lines
  share their header and account, and nothing else told them apart. They also add
  `currencyCode`, the currency of the line's own account, which its `amount` is in,
  unconverted.
- **`GET …/tags/{tagId}/transactions`** lines add the same `legId` and `currencyCode`:
  they are the same lines, from the same query.
- **`GET …/budget/progress`** adds `historyFrom`, the day of the ledger's first
  transaction when it falls after the window's first day, and null otherwise. When it
  is set, `typical`, `typicalTotal` and `dailyNormal` average each day of the month
  over only the window's months in which the ledger already existed on that day, where
  they divided by the whole window. `windowMonths` stays the window asked for.
- **`GET …/categories`** returns `{ categories, currencyCode, mixedCurrency }`, where it
  returned the list alone: `categories` is that list, unchanged, and every category's
  `total` is in `currencyCode`, the ledger's display currency, by the rule the budget
  month uses. `mixedCurrency` is true when the ledger's accounts span currencies;
  nothing is converted.
- **`GET …/reports/returns` adds `gain`**: `endValue − startValue − netContributions`,
  the window's change in value that contributions and withdrawals do not explain
  (price movement, realized or not, plus dividends and interest, less fees and
  investment expenses; not annualized; for the whole scope). The `accounts` rows do
  not carry it.
- **`GET …/reports/returns` counts each edge of a window once.** `from` is inclusive:
  `startValue` is the portfolio just before it, so anything dated `from` is inside the
  window, a contribution there as a flow. `to` is exclusive: `endValue` is the
  portfolio just before it, so a flow dated `to` belongs to the next window. Before,
  an event dated `from` sat in `startValue`, and a flow dated `to` was in `endValue`
  but not in the flows. Bounds compare at the microsecond. With no `to`, the end is
  still the last instant of `today` the register counts, and a flow dated at that
  instant is now in the flows as well as in `endValue`. Since-inception's start is
  unchanged.
- **An allocation reconciles with a window that has a `to`** at `to` less one
  microsecond, the instant that window's `endValue` is taken. With no instant on
  either, both value the end of `today`, as before.
- **`GET …/overview`** `portfolio` adds `currencyCode`, the display currency of the
  investment accounts alone, by the same rule as the ledger's `currencyCode`. A
  ledger mixed by its bank accounts can hold brokerages that all agree; the
  investment figures belong under the portfolio's code.
- **`GET …/securities/{securityId}`** adds `priceCurrencyCode`, the currency of
  `latestPrice` and so of the market value (the close it was read from; for a last
  trade's price the security's newest price row's; with no price row the holders'),
  `holdingsCurrencyCode`, the currency of the accounts holding it that day and of
  `totalCostBasis`, and `holdingsMixedCurrency`, true when those accounts span
  currencies.
- **`GET …/securities`** rows add `priceCurrencyCode`, by the detail's rule: the
  currency of each row's price and amount.
- **`GET …/securities/{securityId}/transactions`** rows add `currencyCode`, the
  currency of the brokerage the row names, which its `unitPrice` and `amount` are in; a
  closed brokerage keeps its own. `accountId` and `accountName` name the brokerage that
  owns the row's own line, never another brokerage on the same transaction (the other
  side of an in-kind transfer, or the brokerage whose cash funded the trade). When more
  than one brokerage names the holdings account that line sits on, they name the one
  whose currency the line's trade price is in: the first by currency code, then by id.
- **`GET …/reminders`** adds `currencyCode`, the series' source account's currency,
  as `GET …/reminders/upcoming` occurrences already carry it; `null` for a series with
  no single source.
- **`POST …/balances/consistency`**: each `unbackedPrices` row adds `currencyCode`, the
  currency of its holding value (the price's when the security has a current price,
  else the holding accounts'), and `mixedCurrency`, true when that holding value is a
  cost basis summed across currencies.
- **`POST …/transactions/{id}/merge` and `POST …/investment-transactions/{id}/merge`**
  refuse a reminder's template as `fromHeaderId`, with 422 `merge-source-invalid` as
  for a hidden or merged row. The merge candidates never offered one; a direct call
  could fold a feed row into it.
- **`GET …/balances/health` and `POST …/balances/repair` are removed.** The balance check
  is the `balances` projection of `POST …/balances/consistency`, and the balance repair is
  `POST …/balances/consistency/balances/repair`, which rewrites every stored running
  balance as the old repair did and answers `{ projection, repaired, after }`. The
  projection's `checked` is what `rowsChecked` was; how many accounts were walked is not
  reported. Each drifted row is a `balances` mismatch: `stored` where `storedBefore` was,
  `expected` where `recomputedAfter` was, `diff`, `accountId`, `headerId`, and the account
  and day in `scope`. Where `drifted` listed every drifted row, `mismatches` lists at most
  100, as for every projection, and `mismatchedCount` counts them all, as `driftedCount`
  did; the repair still rewrites every one, and its `repaired` lists at most 100 of them
  likewise. The check reports a walked row with no stored row as drift: a `balances`
  mismatch carries `reason`, `value` (a stored row holds another balance) or `missing`
  (none is stored, `stored` 0, `diff` the whole walked balance), and a missing one's
  `field` says it is not stored.
- **`POST …/balances/consistency`**: a `holdings` mismatch carries `reason`, `value` or
  `missing`. A position the walk holds with no stored row is two `missing` mismatches,
  `field` `quantity, not stored` and `cost_basis, not stored`, each `stored` 0; a stored
  row for a position holding no shares is a `value` mismatch on `quantity` with
  `expected` 0. The projection's `checked` counts the positions with no stored row too.
- **`POST …/balances/consistency`**: a `trade_prices` row stored in another currency than
  the brokerage that made the trade that prices its day is a `value` mismatch whose
  `field` names both codes, with `stored` and `expected` 0; a row that also holds another
  price is a second mismatch, as before. A new seventh projection, `holdings_currency`,
  reports a brokerage's holdings account in another currency than the brokerage: one
  mismatch per holdings account, `scope` the brokerage's name, `accountId` the
  brokerage, `field` `its holdings are recorded in X, but the brokerage is in Y` with
  both codes, `stored` and `expected` 0, no `reason`. It is repaired through the same
  route, `POST …/balances/consistency/holdings_currency/repair`, and is listed in the
  422 `consistency-projection-unknown` message.
- **The consistency notifications' summaries** count `holdings_currency` in the
  Maintenance panel's words, `holdings_currency: N of M brokerages out of step`
  (`brokerage` when M is 1), and a repair of it says `recorded the holdings of the N
  brokerages out of step in their own currency` (`brokerage` and `its` for one). Every
  other projection reads `… disagree with the transactions`, as before. The healthy one
  reads `Every projection agrees.`, where it read `Projections agree with the
  transactions.`.
- **New 422 code:** `transaction-entries-direction-invalid`, for `GET
  …/transactions/entries` only. `register-limit-invalid` now also answers that read's
  `limit`, and `merge-source-invalid` a template named as a merge's winner.

### MCP

- **Rows that tie on what a tool sorts by come back in one order**, the tie broken by id:
  `list_accounts` and `net_worth` (accounts of the same name), `list_securities`,
  `holdings_snapshot`, `allocation`, `investment_income`, `realized_gains`, `returns` and
  `find_in_kind_transfer_candidates`. No tool checks or repairs consistency, so the
  check's change above reaches none.
- **`returns`** carries `gain`, and its `fromUtc` and `toUtc` count the edges as above;
  **`allocation`** reconciles with a window that has a `toUtc` one microsecond before
  it.
- **`list_transactions`** lines carry `legId` and `currencyCode`, and its description no
  longer says USD: each amount is in its line's `currencyCode`, its account's currency,
  or with a category as `accountId` the currency of the account on the line's other
  side (a category holds no money of its own; a line between two categories takes the
  category's code).
- **No description says USD any more.** Those of `allocation`, `holdings_snapshot`,
  `account_portfolio`, `investment_income`, `realized_gains`, `returns`, `activity`,
  `list_accounts` and `transaction_summary` say their money is in the accounts' own
  currencies, unconverted, as it always was.
- **`merge_securities`** prices the target's days from the trades it moves onto it,
  which it left unpriced.
- **`merge_securities`** rebuilds the holdings only of the accounts that hold the source
  or carry its shares on a transaction, and the account count in its answer, a dry run's
  included, counts only those. An account whose only lines naming the source carry no
  shares (the cash line of a dividend, a fee or a miscellaneous event) or belong to a
  reminder, which it counted and on which it stored an empty holding of both securities,
  is no longer counted or given one. The count of lines it moves is unchanged. Its
  description says what each count counts.
- **`set_account_taxstatus` and `rename_category`** leave every other field of the account
  as the last edit committed it, where an edit of the same account saved at the same
  moment could be undone by them.
- **A write refused because another write to the ledger is still being saved** answers
  an error result whose text is `ledger-write-busy: ` followed by the sentence the API's
  422 carries, where it answered only that an error occurred. Nothing was written, and the
  same call succeeds once the other write has committed.
- **`set_transaction_category` and `set_split_posting_category`** save each transaction on
  its own, so a transaction refused because another change to the ledger was still being
  saved is listed in `rejects` with reason `ledger-write-busy`, unchanged, and the call
  goes on to the next. Before, the refusal ended the call with "Nothing was changed"
  although the transactions before it had been moved; `recategorized` and `moved` now count
  them. Their descriptions name the new reason.
- **`merge_tags`, `delete_tag` and `cleanup_unused_tags`** take the ledger's write lock
  first, so each can answer the `ledger-write-busy` error result.
- **`budget_progress`** carries `historyFrom` and averages a young ledger's `typical`
  and `typicalTotal` as `GET …/budget/progress` does above; its `windowMonths` stays the
  window asked for. Its description names the totals it returns, `actualTotal` and
  `typicalTotal`, where it named a `markTotal` it never returned.

---

## Report tiles and Cash ahead on the Overview (0.133.0)

### Before you upgrade from 0.132.0

- **Migration 276 runs on boot and rewrites no row.** It redefines
  `account_balances_as_of_now`, the database function every current balance is read
  through, so that each account's balance is found by walking the register's own index
  rather than by scanning the ledger's transactions once per account. No index is added
  and nothing else changes. There is nothing to run by hand. It is a migration, so the
  backup [Before you start](#before-you-start) asks for is the way back
  ([Rollback](#rollback)).
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see [Before you start](#before-you-start)). Migration 276 changes no row,
  but a snapshot is tied to the schema it was taken on.
- **Migration 277 runs on boot and rewrites no row** either. It redefines the database
  function that recalculates an account's running balances after a save, so that it
  starts from, and leaves behind, only running balances the transactions produce, and
  adds one view the calculation reads. There is nothing to run by hand.
- **Migration 278 runs on boot and changes only a database comment**, the one on a bank
  feed connection's name, so it says what sets the name. There is nothing to run by hand.
- **Every balance is the figure it was** on an install whose stored running balances
  agree with its transactions. On one where a deleted or merged-away transaction still
  carries a stored running balance, a current balance now leaves it out, as the register
  always has, and the consistency check reports it (below).

### On the home screen

- **Report tiles.** Any saved or catalog report can sit on the Overview, up to six at a
  time and each report once. Choose Customise, then **Add a report…** in the hidden
  widgets, and pick one; "N of 6" beside it says how many are shown, and a report
  already shown is marked "added". A tile shows the report over its own window as a
  chart or a table, compact or tall (its settings), and **Open report →** opens it in
  Reports. A tile and a sidebar pin are separate: adding one does not add the other. A
  tile whose saved report is deleted, or no longer shared with you, disappears from the
  Overview; while you customise it reads "Report no longer exists · Remove".
- **Cash ahead, hidden until you add it** from Customise's hidden widgets. For every
  account a reminder is paid from, it shows today's balance and the lowest balance your
  scheduled transactions and reminders take it to over the next 14, 30 or 60 days, with
  the day, with a step chart of the accounts combined or of the one account its
  settings choose. "≈" marks a lowest that rests on an estimated reminder amount, and the
  caption says when estimates and overdue reminders are included. "N reminders not
  projected" names each reminder it could not place, and each name opens that
  reminder's editor on the Reminders page. It offers no buttons and is never counted in
  the line under the title. When the accounts are in more than one currency there is no
  combined chart: it says the totals span currencies, and each account keeps its own
  figures.
- **Bills marks an estimating reminder's amount "≈" before it has posted.** A reminder
  set to estimate its amount from history shows the amount entered until it can make
  an estimate, and that amount stands in for the bill rather than being it. Bills now
  marks it "≈", as it marked an estimate, and marks an overdue row's amount the same
  way, where it marked none. Hovering the "≈" says which it is: an estimate from the
  last few occurrences, or the amount entered. A reminder with a fixed amount carries no
  mark.

### In Reports

- **Charts take new colours.** Over time, each series has a colour of its own that no
  other series in the chart shares: a category keeps its own colour family, the one its
  chip has, and a payee, account, tag or kind chart takes colours by size. The shades
  are chart shades new in this release, in all four themes, each far enough from the
  others to tell apart side by side. The ranked view (no time bucket) draws every bar in
  one neutral colour, since each bar has its name beside it. A category chart shows
  Running… until it knows the ledger's Uncategorized category, so no colour changes a
  moment after it draws.
- **Past nine groups, a chart folds the rest into "Other (N)"**, in grey, or names the
  group when only one is left over; its figures come from the server. The table still
  lists every group. In a ledger that spans currencies Other has no bar and reads
  "mixed".
- **A rolled-up chart draws a parent category's own transactions**, as "*parent*
  (direct)", where it drew them nowhere. A chart that is not rolled up draws a parent
  category that has transactions of its own, where it left the parent out. The table is
  unchanged.
- **A chart grouped by income vs expense names its series "Income", "Expenses" and
  "Adjustments"**, as the table does, where its legend and bars showed the stored names.
- **Switching between Table and Chart keeps the controls in place** while the report
  runs again.
- **A chart's period labels never overlap.** Each label sits under its own column. As a
  chart narrows, the labels thin to every second, fourth and so on, always keeping the
  first and the last; a wide chart labels every period that fits, where it used to stop
  at twelve. A label no longer wraps onto two lines.
- **Gridlines are drawn under the bars**, and the zero line over them, so a bar never
  hides the line its sign is read against.
- **A ranking keeps its bars and figures at phone width**, and a chart's legend shows
  whole lines only, keeping the "Other" line in view.

### In Settings → Bank feeds

- **A connection can be renamed.** An owner presses the pencil after a connection's
  name, types, and presses Enter or clicks away; Esc, or a blank name, leaves the name as
  it was. A name is 1 to 80 characters, and two connections may share one. The new name
  shows wherever the connection is named: the Connected list, the Disconnect dialog and
  the Overview's Feeds & checks. A sync never changes it. Editors and viewers see no
  pencil, as they see no Disconnect.
- **A new connection takes its institution's name only when it reaches one
  institution.** One that reaches accounts at several institutions starts as
  "SimpleFIN", where it took the name of the first institution SimpleFIN listed, until
  you rename it. Connections that already exist keep the name they have.

### Across the app

- **Balances load faster on a large ledger.** The sidebar's balances, the Overview's
  figures and Cash ahead find each account's current balance through the register's own
  index (migration 276); the figures are the same.
- **The consistency check finds one more kind of stale running balance**: one stored on
  a transaction the register skips (a deleted or merged-away import, a reminder's
  template) or on an account with no transactions. Settings → General → Maintenance lists
  it under Running balances as "stored *x*, none expected", with no link, and Repair
  removes it; it changes no transaction. An install whose running balances agree with its
  transactions has none, and on one that has, the scheduled check reports them after the
  upgrade until they are repaired. A later save no longer starts its recalculation from
  such a balance.
- **A link can open a reminder's editor.** The Reminders page opens the reminder a link
  names, as Cash ahead's list of reminders not projected does. Closing it leaves you on
  the Reminders page, and Back does not open it again. A link to a reminder that no
  longer exists opens nothing.
- **The Reminders page marks an estimating reminder's amount as Bills does.** Its list
  and its calendar mark the amount "≈" before the reminder has posted, as they marked an
  estimate, and the dialog that posts or skips an occurrence marks it too, where it
  marked none. Hovering the "≈" says which it is.

### For anything calling the API directly

- **`GET …/reports/summary` with `topN` of 1 or more** marks every row with `drawn`, and
  adds `rest` `{ count, total, byPeriod, only }`: what no drawn row holds, so the drawn
  rows plus `rest.byPeriod` equal `periodTotals` in every period. `only`
  `{ groupId, groupName, isDirect }` names the group when `count` is 1. With `rollup`, a
  kept parent that has a kept row below it is `drawn: false`, and its own postings come
  as new rows with `isDirect: true`, one per period, under its own `groupId`; a caller
  that lists groups skips those rows, as Reports' table does. In a ledger whose accounts
  span currencies `rest.total` and `rest.byPeriod` are null. Without `topN` the response
  is unchanged.
- **New read: `GET …/accounts/balance-forecast`**, with an optional `today` and `days`
  (default 30, clamped to 1–60). For each account that is the source of an active
  reminder, inactive accounts included, `accounts` gives `today` (the register's current
  balance), `points` (the balance at the end of each day) and `lowest`
  `{ amount, date, estimated }`; `combined` sums them when they share one currency and is
  null otherwise. `includesEstimates`, `overdueOnDay1` and `excluded` (each reminder it
  could not place, with a `reason` of `no-lines`, `override-not-placeable` or
  `cross-currency`) say what the projection rests on. A reminder that estimates its
  amount is placed at the amount `GET …/reminders/upcoming` gives its occurrence, the one
  the Bills widget shows: its estimate, or the amount entered while nothing has posted to
  estimate from; either counts in `includesEstimates` and `estimated`. Any member can
  read it; 422 `ledger-not-visible` otherwise.
- **The `balances` projection of `POST …/balances/consistency` has a third `reason`,
  `extra`**: a stored running balance the walk does not produce, with `stored` its
  balance, `expected` 0 and `field` `balance_after, not derived`.
  `POST …/balances/consistency/balances/repair` removes such rows and lists them the same
  way. `value` and `missing` are unchanged.
- **A dashboard layout can hold report tiles and Cash ahead**: widgets with key `report`
  and an `id` of `report:saved:<uuid>` or `report:catalog:<key>`, and the key
  `cash-forecast`. `…/preferences/dashboard` checks structure only, as before.
- **New write: `PATCH …/feed-connections/{connectionId}`** with `{ name }` renames a
  connection and answers its summary. The name is trimmed and must be 1 to 80
  characters, else 422 `feed-connection-name-required` or `feed-connection-name-too-long`;
  422 `feed-connection-not-found` for a connection not in the ledger. It is an owner's,
  as `DELETE` on the same path is (422 `ledger-not-owner`).
- **`POST …/feed-connections` sets `institutionName` only when SimpleFIN's connection
  list names one institution**, trimmed, and null otherwise, where it took the first
  name listed.

### MCP

- **`transaction_summary` returns what `GET …/reports/summary` returns**, through the
  same code: it adds `currencyCode` and `mixedCurrency`, and with `topN` each row's
  `drawn`, the `isDirect` rows and `rest`, whose `total` and `byPeriod` are null when the
  ledger's accounts span currencies. Its description says how to read them.

## Balances are fast again on long-lived ledgers (0.133.1)

Nothing to run.

- **The Overview, the sidebar's balances and Cash ahead are fast again on a long-lived
  ledger**, as are MCP `list_accounts` and `net_worth`. In 0.133.0 each account's current
  balance was found by walking the ledger's transactions back from today until one
  touched the account. That walk is short for an account in use, but for an account with
  no transactions, or none for years (a category never used, a closed account, a
  brokerage never traded in), it reads nearly the whole ledger, so a ledger holding many
  such accounts was slow to show its Net worth and Investments. The walk now looks at
  no more of the ledger's transactions than the account itself has, and when it finds
  none of the account's among them the balance is read from the account's own running
  balances, so its cost follows the account,
  never the size of the ledger, and stays short for an account in use. The figures are
  the same.
- **Migration 279 runs on boot and rewrites no row.** It redefines
  `account_balances_as_of_now`, the database function every current balance is read
  through. No index is added and nothing else changes. There is nothing to run by hand.
  It is a migration, so the backup [Before you start](#before-you-start) asks for is the
  way back ([Rollback](#rollback)).
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see [Before you start](#before-you-start)). Migration 279 changes no row,
  but a snapshot is tied to the schema it was taken on.

## Returns over time, and share transfers at market (0.134.0)

Nothing to run. No migration; no row is rewritten.

### In Reports

- **A Returns report is in the catalog.** It draws how an investment scope has done
  over time as a line per month, quarter or year end: an index that starts at 100 at the
  first invested point (or the cumulative gain), for the ledger and for each account.
  Each point is the time-weighted return to that date, not annualised, and the last
  point equals the Returns figure for the same window. The table also shows the covered
  days and the money-weighted return to date, which is blank until a year is covered.
  A period with no figure reads as a gap with its reason, never as zero or as the
  previous value. In a ledger that spans currencies the ledger line is not drawn and
  the report says so; each account still has its line. A saved report that predates
  this reads as before.
- **A share transfer between brokerages is valued at market** in returns. It was read at
  the shares' carried cost basis while the account's value is at market, so an account
  scope (or a ledger scope with a brokerage outside the scope) showed a gain or a loss
  equal to market less basis at the transfer. Time-weighted and money-weighted returns,
  the gain and the net contributions of an affected window change; holdings, cost basis
  and realized gains do not.

### For anything calling the API directly

- **New read: `GET …/reports/returns-series`** takes `accountId`, `includeAccounts`,
  `interval` (`month`, `quarter` or `year`; 400 for `day`), `from`, `to`, `today` and the
  sliding-window parameters of `…/reports/summary`. It answers a series per account and,
  when the scope is one currency, a ledger-wide series; each point carries the index,
  covered days, cumulative gain and money-weighted return to date, and a null carries a
  reason code. More than 600 period ends in one series is a 400.
- **`GET …/reports/returns`** and MCP `returns` change only through the in-kind valuation
  above.

### MCP

- **New read tool: `returns_series`**, the same result as `GET …/reports/returns-series`,
  beside `returns`.

## Benchmarks, and totals in one currency (0.136.0)

Nothing to run by hand. Five migrations run on boot.

### Before you upgrade from 0.135.x

- **Migration 280 gives every ledger a reporting currency**, the one its totals are shown
  in (`ledgers.currency_code`). It is set from the rule that chose the display currency
  until now: the first currency, in code order, among the accounts net worth counts, else
  USD. So every ledger shows its totals in the same currency after the upgrade as before.
  The migration also stamps every budget target with its ledger's new reporting currency,
  as the currency the target was typed in, and adds five empty tables for benchmark growth
  and exchange rates. Nothing else is rewritten.
- **Migrations 281 and 282 rewrite no row.** 281 redefines the valuation functions so
  that each names the currency of the price it valued a position at; 282 adds two reads of
  a position's FIFO lots with their acquisition dates, so a cost basis and a realized gain
  can convert lot by lot.
- **Migration 300 adds `securities.quote_currency_confirmed_at`**, empty on every security
  until a quote refresh confirms its currency (below).
- **Migration 301 lets money follow its currency's places** (below). It
  replaces the check that held every transaction amount to 2 places with one that allows
  at most 3, which every existing amount passes, and widens budget targets and realized
  gains to 3 places. It also gives a transaction's originals, the feed values kept beside
  an edited row, two new columns, the amount (left empty on every existing row) and
  whether an import rounded it (left clear); gives a transaction two columns for why a
  bank's cleared figure sent it back to review (left empty); lifts the four-place limit on
  the fee and total an investment import carries for the editor; and recreates four
  transaction views over those columns, each with its columns as before;
  `resolved_transactions` and `live_txn_headers` also gain the new ones. It rounds
  nothing and changes no stored figure. Widening rewrites the `realized_gains` and `budget_targets` tables under an
  exclusive lock, which lengthens the first boot's wait for migrations
  ([What to expect on the first boot](#what-to-expect-on-the-first-boot)). It is
  deterministic, so a full backup taken on an earlier release migrates forward through it
  at restore.
- **Snapshots taken before the upgrade cannot be restored after it**, as for any migration
  (see [Before you start](#before-you-start)). The new tables are captured in every
  snapshot taken after it.

### Amounts at each currency's places

- **Every amount is shown, typed and stored at its own currency's places**: 0, 2 or 3,
  ISO 4217's minor units for the currency, instead of 2 for every currency. A 2-place
  currency shows and takes amounts as before. A money field takes no more places than its
  currency has: a typed amount finer than that is refused, naming the currency and its
  places, where it used to fail as a server error or fail the whole import. A figure Coffer computes, such as a
  loan payment's split or an estimate, is rounded once at its currency's places, half away
  from zero; the loan payment itself rounds up to one minor unit. Prices, share quantities and exchange rates keep their own places.
- **Existing amounts are kept as they are.** An amount stored before the upgrade with more
  places than its currency has is not rounded, and an edit that leaves it unchanged saves.
  Settings → General's Maintenance panel lists every such amount as an advisory, beside
  the check's result and not counted in it, with nothing to repair; an owner or editor
  changes one by editing it. A transaction merged into another is not listed, as no
  screen shows or opens it; deleting the transaction it was merged into, where that
  delete removes it rather than hiding it, brings it back, and it is then listed like any
  other. A realized gain is derived, so on an account
  whose currency has 0 or 3 places the check reports one not at those places under
  `realized_gains`, and its Repair re-derives it.
- **Account and ledger currencies are ISO 4217 currencies.** A new account or ledger, or a
  currency change, takes a code with 0, 2 or 3 places; a code ISO does not give places
  for, and the 4-place fund units `CLF` and `UYW`, are refused. The currency pickers offer
  every accepted code, the ones offered before first. An account or ledger that already
  has a code now refused, for any of these reasons, keeps it and keeps working at 2
  places, and the Maintenance panel lists it in the same advisory. A price's currency is
  not limited this way.
- **A currency change or a move that would not fit is refused.** Changing an account's
  currency, or moving a transaction into an account in another currency, is refused when
  a stored figure has more places than the new currency allows. The message says how many
  transactions do not fit, and names the opening balance and the loan terms when they do
  not. A figure on a transaction merged into another does not count.
- **An imported amount finer than its account's currency is rounded and flagged.** From a
  bank feed or a file (SimpleFIN, OFX/QFX, QIF, CSV, a brokerage's CSV export, and a
  pending row that clears), such an amount is rounded to the currency's places, half away
  from zero; the row lands needing review, and keeps the bank's figure as sent, with its
  originals. In the Overview's To review list, and under Amount in the editor Review… opens, it reads
  "Rounded from <bank figure> as sent by the bank", the bank's figure with no currency
  symbol, in the row's own sign. A pending row that clears records its cleared figure the
  same way.
- **A pending row you split keeps its lines when the bank clears it.** It used to write
  the whole cleared amount onto the first line and leave the others, so the account
  counted the transaction more than once. Now every line stays as you split it; when the
  bank clears it at another figure, the row goes back to review and the account shows your
  lines' total until you adjust them. In the Overview's To review list, a row held this
  way, or because a transaction merged into it cleared at another figure, says what the
  bank cleared, in place of a rounded note left from its own import.
- **A transaction's first edit now also keeps its amount from before the edit**, beside
  the payee, memo, dates and check number it already kept. A row edited before the
  upgrade has no amount kept. A row that already had originals, edited before the
  upgrade or seeded by a Moneydance import, keeps none from a later edit. A Moneydance import made
  after the upgrade keeps the amount as imported on the rows it seeds.

### CSV and QIF amounts with a decimal comma

- **A CSV layout has a decimal separator**, point or comma, under Amount on the wizard's
  "Describe this file" step. A new layout's is guessed from the sample and says "Guessed
  from the sample."; a saved layout keeps the one it was saved with and is never guessed
  again. A layout saved before the upgrade reads with a point, as it always did. A layout
  written without a sample, by hand or through the MCP tools, must name its separator.
  A row the chosen separator cannot read is reported on the "Confirm
  the import" step, naming the line, and is not counted.
- **This fixes a defect.** CSV and QIF reading used to take every comma as a thousands
  separator, so a file written with a decimal comma was read a hundred times too large,
  with no warning.
- **A QIF amount that can be read two ways is refused**, naming the line. QIF has no
  layout to say which separator the file uses, so a comma is read only where it groups the
  whole part, in thousands or lakh-style with groups of two before a last group of three,
  by the same rule as a CSV layout, and a file imported before matches its earlier rows on
  a re-import. Any other comma is refused, commas grouped from a first group of more than
  three digits or of one starting with a zero among them, as a CSV layout refuses them.
  An account whose currency has 3 places refuses any comma in an amount. Lakh-style
  grouping and that refusal were decided 2026-11-17. An amount that cannot be read at all
  fails its row, never imports as zero.
- **Brokerage CSV files: a cash cell with more than four decimal places is now carried to
  the import exactly**, where it used to be rounded to four, so the row's identity is
  computed from its exact figure. Re-importing a file imported before this release can
  duplicate such a row where its old four-place reading fitted two places; the duplicate
  lands needing review, marked as rounded. Delete the duplicate, or import only
  statements dated after the last import. Every other row keeps its identity.

Settled by Claude, 2026-11-16 ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)
D15): the named refusal of a typed amount, one rounding of a computed figure half away
from zero and the loan payment's rounding up, the listing's being an advisory not counted
in the verdict, and the 2 places of a stored code now refused for any reason, the
re-derivation of an existing realized gain by its Repair, the decimal separator a layout
without a sample must name, the refusal of a currency change or move that would not fit,
the Moneydance places rule below, the per-row error for an unreadable QIF amount, the
currency that the loan payment preview request and a sync run's promotions now carry, and
which amount a transaction's originals keep, in which sign, that a row's originals from
before the upgrade get no amount, and how a pending row that clears updates it.

### The first quote refresh after the upgrade

- **Fetched prices get their real currency.** Before this release every price fetched from
  Yahoo was stamped USD, whatever the fund is quoted in. The first refresh asks Yahoo once
  for the currency of every security with fetched prices, held or not: one request each,
  under the Yahoo opt-in of the user the refresh runs as, storing no price. Where Yahoo
  names another currency, the security's older fetched prices are re-stamped with it and
  the prices are left as they are. When Yahoo quotes the symbol in a minor unit (`GBp`,
  pence), those older prices hold pence, so price, high and low are divided by the unit's
  factor and rounded to 4 places, and the major unit (`GBP`) is stamped; volume is kept.
  Hand-entered, trade, imported and SimpleFIN prices are not touched. A security whose
  lookup fails is asked again on the next refresh.
- **Until then the consistency check reports it.** Settings → General's Maintenance panel
  lists, under `quote_currency`, each security with fetched prices whose currency Yahoo has
  not confirmed. Its Repair runs the same lookup and needs your Yahoo opt-in.
- **A fund quoted in another currency than its account now needs a rate.** Its price is
  in its own currency, and its value converts into the account's at the day's rate. Until
  the ledger has that rate, the value is marked as rate unavailable instead of a figure.

### Totals in one currency

- **A total that combines accounts converts** into the ledger's reporting currency: net
  worth, the month's spending, budget totals, report totals and rows, investment totals,
  a category's total and the others that read "mixed" on a ledger whose accounts span
  currencies. A balance converts at its day's rate, a transaction at its date's, a trade at
  its trade date's, and a current value at the latest rate. Every figure for one account
  stays in its own currency, so a report's rows grouped by account do not convert. A
  report's chart is not drawn when its rows are not all in the reporting currency, such as
  rows by account on a ledger whose accounts span currencies, and the page says why. On a
  ledger whose accounts and prices are all in one currency nothing changes.
- **A missing rate is never guessed.** A figure that needs a rate the ledger does not have
  is shown as rate unavailable, naming the currency pair and the day, and no account is
  left out of a total. On a ledger that spans currencies this is what the totals show right
  after the upgrade, until rates are fetched or typed in.
- **Rates come from Frankfurter, or by hand.** Turn on Frankfurter on Settings → Quotes
  (below), and the ledger's rates are fetched at once and then with each quote refresh.
  The exchange-rates tab on Settings, after Quotes, lists the pairs the ledger needs, how
  far each is covered, and lets an owner or editor type a rate for a day that has none.
- **The reporting currency can be changed** by an owner on Settings → General. A new
  ledger starts in USD unless another is chosen when it is created, and a Moneydance import
  starts in the file's base currency. A budget target keeps the currency it was typed in: a
  target in another currency than the ledger's converts like any other total, and changing
  the currency back reads every target as it was typed.
- **One note under the page header** says a ledger's totals are converted into its
  reporting currency; a figure with a missing rate is also marked on itself.
- **A category's split paid from accounts in different currencies reads "mixed"** on the
  category's register, on its collapsed row and in the editor's totals, instead of a sum
  of its lines printed in the first line's currency. Each line keeps its own amount, in
  the currency of the account that paid it.

### Frankfurter, a new opt-in destination

- **Exchange rates are a fourth outbound destination**, beside Yahoo, Google Drive and the
  release check. Frankfurter is a provider on Settings → Quotes, off by default for every
  user, and nothing is sent until someone turns it on. A request names currency codes and
  the days a ledger needs; nothing else of the ledger. A bank sync never triggers one.
- **The default is the public service**, `https://api.frankfurter.dev`. To keep these
  requests on your own network, run a Frankfurter container and set `COFFER_FX_RATES_URL`
  in `.env` to its address ([operations.md](operations.md#where-coffer-connects-on-its-own)).
  The release's `docker-compose.yml`, which the upgrade fetches, passes it to the API.

### Benchmarks

- **A Returns report can be compared with a fund.** Settings → Quotes gains a Benchmarks
  section under the Yahoo switch: up to three fund or ETF symbols per ledger, checked with
  Yahoo when added, so adding one needs your Yahoo opt-in; an index symbol is refused. Its
  history is fetched back to the ledger's first brokerage funding at once. The Returns
  report's Benchmark control draws one beside the line, as its total return from the same
  first invested point; the pick is saved with the report.
- **Removing a benchmark asks first.** A saved report that compares with it draws no line
  until it is added again, and keeps its pick; the fetched history is kept.

### Reminders

- **Posting an estimated reminder by hand starts at its estimate.** In the dialog that
  posts or skips an occurrence, the Amount starts at the "≈" figure shown above it, which
  is also what automatic posting writes, for a reminder in one currency as for one between
  two. It used to start at the amount entered on the reminder. Until there is an estimate,
  it still does. The amount can be changed before posting, as before.
- **A reminder whose stored amount has more places than its currency is not posted
  automatically** until the reminder (or, for a loan payment, the loan terms) is edited:
  posting it would write that amount. Automatic posting sets it aside without counting it
  as a failure, the reminder-backlog warning counts and names it, and Settings → General's
  Maintenance panel lists its amount among those that do not fit.
- **A managed loan payment whose paying or escrow account is in another currency than the
  loan's can no longer be set up, and one set up before is never posted**; the
  reminder-backlog warning names it. Enter such a payment as a transfer with both amounts.

### Ledgers imported from Moneydance

- **A ledger imported before this release keeps USD** on its accounts and its imported
  prices. The import did not keep the file's currency records, so there is nothing to
  correct them from. An import made now names each account's and each price's currency
  from the file, so importing the file again into a new ledger gives its real currencies.
  Those earlier imports held every amount at 2 places in USD, so they stay consistent.
- **A 3-place currency now imports.** An account currency imports when the file's own
  places for it are at most Coffer's for that code, so every amount fits exactly; one with
  more is refused at preview, naming the account and both place counts. A loan's escrow and
  fixed payment are checked the same way. So is a category that moves money to another
  category, against the new ledger's reporting currency, since every category is kept in
  it: one kept at more places is refused, naming the category and both place counts. The
  wizard's preview checks it against the file's base currency, the wizard's default, since
  it does not send the chosen currency with the preview; with another reporting currency
  chosen in the wizard, the refusal comes when the import starts rather than at preview.

### For anything calling the API directly

- **No `mixedCurrency`.** The mixed flags are gone from every read (`mixedCurrency`,
  `holdingsMixedCurrency`, the `mixed_currency` reason). A figure that combines accounts is
  in the ledger's reporting currency, named by `currencyCode`, and is null when it needs a
  missing rate, with `rateUnavailable` listing
  `{"reason":"rate_unavailable","fromCurrency":…,"toCurrency":…,"date":…}` for each.
- **`GET …/budget/progress` rows add `targetCurrencyCode`**, the currency the stored
  target was typed in, null when none is stored. A null `target` with a
  `targetCurrencyCode` is a stored target that needs a missing rate, not an unset one.
- **`POST …/transactions/selection-summary` adds `currencyCode`**, the currency
  `sumOnAccount` is in. On a category's register whose selected rows are in more than one
  currency, `sumOnAccount` converts each leg at its date into the reporting currency, and
  is null with `rateUnavailable` when a rate is missing.
- **`PATCH /api/ledgers/{id}`** takes `currencyCode` (owner only;
  `ledger-currency-invalid`), and creating a ledger takes one.
- **New routes:** `GET`, `POST /api/ledgers/{id}/benchmarks` and
  `DELETE /api/ledgers/{id}/benchmarks/{benchmarkId}`; `GET`, `POST
  /api/ledgers/{id}/fx-rates` and `GET /api/ledgers/{id}/fx-rates/{from}/{to}`.
  `GET …/reports/returns-series` takes `benchmark`. `GET /api/ledgers/{id}/accounts/subtotals`
  returns the sidebar's section and folder totals, converted.
- **A summary row names its currency.** Every row and group total of
  `GET …/reports/summary` carries `currencyCode`. Grouped by account, a row and its group
  total are the account's own figure, in its own currency, unconverted; every other row,
  and the total, period totals and rest, are in the reporting currency.
- **A register row names its currencies and the bank's figures.** Every row of
  `GET /api/ledgers/{id}/transactions` adds `currencyCode`, the currency its amount is
  in (on a category, the real account's on the other side of the posting), and
  `balanceCurrencyCode`, the currency `balanceAfter` is in (on a category, the reporting
  currency). A bank row adds `counterpartyAmount` and `counterpartyCurrencyCode`, the
  other leg's own amount and currency on a transfer between currencies (null when the
  pair mirrors); `originalAmount`, the bank's figure as the row's originals keep it, a
  JSON string, exactly as sent; `roundedAtImport`, true when an import rounded that
  figure to the currency's places; and `reviewClearedAmount` (a JSON string) with
  `reviewClearedByMatch`, the cleared figure that sent the row back to review and whether
  it was a merged row's. An investment row adds `transferAmount` and
  `transferCurrencyCode`, the transfer account's own leg when it is in another currency.
- **A hand-entered price names its currency**: `currencyCode` on the price write, defaulted
  from the security's latest price, else its holding accounts' when they agree, else
  refused with `security-price-currency-required`.
- **The Moneydance import preview returns `baseCurrencyCode`**, and the preview and the
  start refuse a file whose currencies cannot be read with `import-currency-unresolved`.
- **A money amount with more places than its currency has is a 422** with a named error
  code that names the currency and its places, on every write that takes one; it was a
  500 before. An account or ledger currency outside the accepted ISO 4217 codes is
  refused. The loan payment preview request carries the loan account's currency, and a
  sync run's promotions carry the synced account's.
- **A CSV layout's YAML has a decimal-separator setting**, point or comma, which the
  validator checks (`POST …/csv-mappings/validate`, and the create and update). It is
  required in mapping schema version 2, the validator's new current version; a version-1
  document, stored or sent, is still accepted and reads with a point.
- **`POST …/transactions/{headerId}/move-account` is removed.** Move one transaction with
  `POST …/transactions/bulk-move-account` and an explicit selection of one
  (`{"kind":"explicit","accountId":<source>,"headerIds":[<id>]}`). It answers
  `{ moved, becameTwoAmount }`, where `moved` counts the legs moved onto the target, not
  transactions. Against the removed route:
  - an id that names no transaction in the ledger, or one merged into another, is
    ignored, not refused: the answer is 200 with `moved: 0` where it was 422
    `transaction-not-in-ledger`, so a caller checks `moved`;
  - a transaction with no leg on `selection.accountId` is refused with 422
    `selection-includes-read-only` where it was `transaction-source-account-mismatch`;
  - every other refusal keeps its code (a move across the account domains, a target that
    is not valid, inactive or the source itself, an investment shape, a split to or from
    an investment account, a collision, and the relabel refusals); its message is worded
    for a selection.
- **A reminder occurrence's `autoPost.excludedReason` has two new values**:
  `loan-across-currencies` and `template-amount-does-not-fit`, judged after `no-lines`,
  `investment` and `inactive-account`, in that order. The `reminder-backlog.deferred`
  event's detail gains `managed_loan_across_currencies`, `template_amount_does_not_fit` and
  `estimate_unavailable`, and its summary names up to three reminders for each of the
  first two, after those with no lines or an inactive account.
- **`POST …/transactions/bulk-unhide` counts only the rows it restores.** A row in the
  selection that is not hidden, in an explicit selection or a select-all over a view other
  than Hidden, is left as it is and no longer counted in `unhidden`.
- **`POST /api/ledgers/{ledgerId}/transactions/{headerId}/unhide` is removed.**
  `POST …/transactions/bulk-unhide` with a selection of one replaces it
  (`{"selection":{"kind":"explicit","accountId":<account>,"headerIds":[<id>]}}`): 200 with
  the unhidden count, `{ unhidden }`, where the removed route answered 204. An unknown,
  merged-away or not-hidden id unhides nothing (`unhidden: 0`) where the removed route
  answered an unknown or merged-away id with 422 `transaction-not-in-ledger`; a
  transaction with no editable leg on `selection.accountId` is refused with 422
  `selection-includes-read-only`.
- **A transaction merged into another answers as one that does not exist.** Every write
  that names one transaction by its id (`PATCH`, `DELETE`, `approve` and `recon-status`
  on `…/transactions/{headerId}`, and `PATCH`, `DELETE` and `approve` on
  `…/investment-transactions/{headerId}`) answers it with that route's own
  `transaction-not-in-ledger`, and changes nothing; `GET …/transactions/{headerId}/legs`
  answers `[]`, and `…/in-kind-transfers/convert` answers `in-kind-transfer-sell-not-found`
  or `in-kind-transfer-buy-not-found`. A merge naming one is still refused with
  `merge-source-invalid`.

### MCP

- **Every tool that combines accounts converts** into the ledger's reporting currency and
  says how in its description: `net_worth`, `net_worth_history`, `budget_progress`,
  `transaction_summary`, `holdings_snapshot`, `allocation`, `investment_income`,
  `realized_gains`, `returns` and `returns_series`. A figure that needs a missing rate is
  null with `rate_unavailable`. `transaction_summary` with `groupBy='account'` keeps each
  row and group total in its account's own currency, named by its `currencyCode`.
- **`budget_progress` rows add `targetCurrencyCode`**, as `GET …/budget/progress` does.
- **`returns_series` takes `benchmark`**, one of the ledger's benchmark symbols, and
  `add_price` takes a currency.
- **`activity` rows add `transferAmount` and `transferCurrencyCode`**: on a transfer
  between accounts in two currencies, the amount the other account received or sent, in
  its own currency, as the investment register shows it. Both are null on a transfer in
  one currency and when there is no transfer.
- **The CSV layout tools teach the decimal separator**: `validate_csv_mapping`'s
  description and example, and `save_csv_mapping`, name the setting and its two values.
  A layout written through them has no sample, so it must name the separator (schema
  version 2).
- **A transaction merged into another is an unknown id to the write tools**:
  `set_transaction_category` and `set_split_posting_category` reject it as `not-in-ledger`,
  `set_transaction_tags` reports it among the ids not in the ledger, and
  `convert_in_kind_transfer` answers as for a header it cannot find.

## Run details in Activity, and a quote refresh switch that watches rates (0.137.0)

Nothing to run. No migration; no row is rewritten.

### In Settings → Activity

- **A run opens to show what went wrong.** A row with errors, cleared pending charges, or
  a failed or needs-reauth status has an expander. A failed run shows its failure
  message, and errors show their code and message. A needs-reauth run says the bank
  refused the stored access and to re-connect in Settings → Bank feeds. Promotions list
  the charges that cleared at a different amount and count the ones that cleared at their
  held figure. Quote refreshes open the same way, so a refresh's "4 errors" can now be read.
- **A feed sync's row names its connection**, as "SimpleFIN · Example Bank", when the
  connection has a name of its own.
- **The Provider filter lists "Rates and benchmarks backfill".**

### In Settings → Bank feeds

- **The Sync activity strip under each connection is gone.** Its runs are in
  Settings → Activity, where they can now be opened.

### In Settings → Notifications

- **A heartbeat bound to `quote-refresh` now watches exchange rates too.** Rates and
  benchmarks are fetched by the quote refresh (ADR-0114 D11), so that switch is the one
  that watches them, and the Watches list and the target's line now say so. A scheduled
  refresh whose rate days were not stored now reports a failure (`/fail`) instead of a
  success. Until now it reported success, so the check stayed green while conversions
  went stale. If the rate source has been down, an existing check can turn red on the
  first refresh after the upgrade. That is the switch working. The run's errors are in
  Settings → Activity.

### Elsewhere

- **The tag filter in the register lists every tag**, and the list scrolls. It showed
  the first eight.

### For anything calling the API directly

- **`GET …/sync-runs?connectionId=` is gone.** Runs are listed by
  `GET …/ledger-operations` (filter with `provider=simplefin`), and each row now carries
  `feedConnectionId`, null for any other operation and for a sync whose connection was
  removed. One run's detail is still `GET …/sync-runs/{runId}`, for a run of any family.

## A Demo from the hub, and reminders that auto-post get their job (0.138.0)

Nothing to run. One migration, 302, which changes constraints and functions only; no row
is rewritten on boot.

### The Demo moves from setup to the hub

- **Setup no longer offers a Demo ledger**, and creates no ledger at all. This changes
  nothing on an install that is already set up, since setup does not run again.
- **The ledger hub has a Demo ledger button**, between Import from Moneydance and New
  ledger, for every signed-in user. Each press adds a new ledger with five years of
  made-up history ending on that day, named "Demo", or "Demo 2", "Demo 3" and so on at the
  lowest free number. A Demo you already have, from the old setup box, is an ordinary
  ledger: it is not changed or replaced, and it counts for the numbering. The Demo builds
  in the background as an import, so it is refused, with the hub saying another import is
  running, while an import of yours runs, or while any import on the install is being
  read or run. It is generated and read in memory as an uploaded export is, so on an API
  container whose memory limit is too small for that the build fails: the hub says it
  failed and nothing was kept, and the server log says why, naming
  `COFFER_API_MEM_LIMIT`, as an upload's refusal does. Its row shows how far it has got
  and, if the build fails, or the server restarts while its history is imported, says so
  with Try again; a failed build keeps nothing. Once its history is imported, the Demo
  gains the examples an export cannot carry, such as folders, budget targets, renamed, hidden and half-reconciled register
  rows, a card statement imported through a saved CSV layout, its rows to review and the
  ones already typed in merged, reminders, saved reports, a Home layout and a snapshot
  holding them all.
  Activity lists them as their own entry, Demo examples, beside the card statement's own
  file import entry, and a Demo whose examples did not
  all finish, a restart during them included, opens as usual, with a line in the hub
  naming the ones it lacks. Its Demo examples entry in Activity opens, as a run with
  errors does, to say which examples were not added and why.
- **For anything calling the API directly:** `POST /api/auth/setup/{token}/complete` no
  longer reads `includeDemo` (a request that sends it is not refused) and its response has
  no `ledgerId` or `ledgerName`. New: `POST /api/ledgers/demo?today=yyyy-MM-dd` starts a
  Demo build and returns its job. It refuses with 422 `import-already-running`, as an
  upload does, while the caller's own import runs or another export is being read; with
  the new code `demo-date-in-future` when `today` is more than a day after the server's
  date; and with `import-parse-failed` when the server lacks the memory to read the Demo.
  That refusal, and a 500 when the Demo could not be started after its name was chosen,
  carry the chosen name as a `ledgerName` extension. `GET /api/ledgers/demo/job` returns
  the caller's latest Demo job or 204, and each item of `GET /api/ledgers` gains
  `demoExamplesMissing`, an array of names or null. A Moneydance import's Activity entry for a Demo is recorded as
  started by hand (`triggeredVia` `manual`). Its examples are a second entry in
  `GET /api/ledgers/{id}/ledger-operations`, provider `demo` (also a `provider` filter
  value), which `POST .../undo-import` refuses with `operation-not-an-import`. A third
  entry, provider `csv-generic`, is the card statement's import. It carries `triggeredVia`
  `manual`, as the Demo's Moneydance import's entry does, since nothing was uploaded; a
  file import a user uploads still carries `file-upload`. `POST .../undo-import` accepts
  it: the statement's rows still in review are removed, and ones merged into typed-in rows
  stay merged.

### Reminder auto-post

- **The consistency check may report Reminder auto-post once after the upgrade.** A ledger
  with a reminder set to auto-post and no auto-post job, because it was imported from
  Moneydance, or the reminder was saved, by a version that did not create the job, is now
  a finding: Settings → General → Maintenance shows "Reminder auto-post — the auto-post job
  is not set up", with how many reminders it leaves unposted, and the consistency
  notification names `reminder_auto_post`. **Repair reminder auto-post** creates the job,
  enabled at 05:00 local and configured by you, and the finding clears. From its next run
  the job posts what is due, as it would have all along, including occurrences already past
  due within its catch-up window that nobody posted or skipped; post or skip those on the
  Reminders page first if you do not want them posted. A job row that is already there is
  never reported, whatever its state, and the Repair leaves it as it is.
- **For anything calling the API directly:** `POST …/balances/consistency` returns one
  more projection, `reminder_auto_post`, whose `checked` is 1 (the ledger) and whose one
  mismatch, when there is one, has `scope` `Auto-post job`, a `field` naming how many
  reminders it leaves unposted, and `stored` and `expected` 0. It repairs through the same
  route, `POST …/balances/consistency/reminder_auto_post/repair`, and is listed in the
  unknown-projection error. The consistency notifications' summaries say
  `reminder_auto_post: the auto-post job is not set up, so reminders set to auto-post are
  never posted`, and a repair's `Repaired reminder_auto_post: turned the auto-post job on.`

### Moneydance imports

Each of these changes every Moneydance import from this release on. A ledger imported
earlier is not changed: no row is rewritten.

- **A Moneydance import that brings a reminder set to auto-commit turns the ledger's
  auto-post job on**, enabled at 05:00 local and configured by whoever imported, as saving
  a reminder with Auto-post ticked does. Before, such a reminder was shown as auto-posting
  and nothing posted it. A ledger imported earlier is not changed by the upgrade.
- **A bank transaction the import drops is listed, with its reason.** A transaction the
  bank route declines (its account, or a split's account, is unknown, or it has no splits)
  and a second copy of an OFX transaction id, dropped as a duplicate, were only counted
  before. Each is now listed with its date and reason, and the no-dropped-transactions
  check fails on them. The import is still saved, and the wizard says it completed with
  warnings; the check's box lists each dropped row, with a dash for the security and
  shares of a bank row.
- **Securities are classified.** A security's type, subtype and exchange are now read from
  its Moneydance security account, where Moneydance keeps them, so imported securities
  arrive classified instead of unclassified. A type code Coffer does not know imports as
  Other, and the new security-types-recognised check warns about it; the vehicle can be
  set with Edit security. Securities imported earlier stay as they are.
- **Accounts keep their tax-status guess.** The import's guess at each account's tax
  status from its name and type was dropped on the way in, so every imported account
  arrived with none. It is now kept, and it can be changed in the account editor as
  before. Accounts imported earlier keep the tax status they have.
- **For anything calling the API directly:** an import job's `skippedTransactions` can now
  carry the reasons `UnknownPrimaryAccount`, `UnknownSplitAccount`, `NoSplits` and
  `DuplicateFitid`, and its `warnings` can name `security-types-recognised`.

### Snapshots

- **A snapshot taken on 0.138.0 or later restores after audit retention has pruned the
  Activity entries of the imports and syncs it holds.** Retention deletes Activity entries
  older than `Api:AuditRetentionDays` (180 days by default; see
  [Audit retention](operations.md#audit-retention)), and the restore of a snapshot holding
  a transaction stamped with such an entry, or a cleared-at-a-different-amount record of
  one, failed. The restore now does to those rows what the prune did to the live ones: the
  transaction is kept without its import stamp, so it cannot be undone as part of that
  import, as on the live ledger, and the cleared-amount record is dropped. Rows whose
  Activity entry is still kept restore unchanged.
- **Migration 302 runs on boot and rewrites no row.** It changes three foreign-key
  constraints and the restore functions only. There is nothing to run by hand. It is a
  migration, so the backup [Before you start](#before-you-start) asks for is the way back
  ([Rollback](#rollback)).
- **Snapshots taken before the upgrade cannot be restored after it**, as for any
  migration (see [Before you start](#before-you-start)). Migration 302 changes no row,
  but a snapshot is tied to the schema it was taken on. If a snapshot is doing
  safety-net duty for you, take a new one after upgrading.

### Other changes in this release

- **New ledgers start with a few more categories**, among them the starter taxonomy's
  first category of the third kind, adjustment, for a valuation or other entry that is
  neither income nor spending, and its first third-level branch. An API or MCP caller that
  lists a new ledger's categories can now meet the `adjustment` kind. Existing ledgers are
  untouched.
- **A file import that is cancelled before it saves**, by the server stopping or a
  caller giving up, now closes its Activity entry as failed with "The import was
  cancelled before it finished", where it could stay listed as running for good. Nothing
  it read is saved, as before.
- **Activity names a CSV file import by its reader**, CSV import or Fidelity CSV import,
  instead of by its raw key, and so does a CSV-imported row's mark in the register. The
  Activity Provider filter offers those two in place of File import, which matched only
  file runs recorded before runs carried their reader's key; such runs are still listed
  under All operations, as File import.

## Subcategories in reports are named by their path (0.140.0)

Nothing to run. No migration; no row is rewritten.

### In Reports and on the home screen

- **A subcategory shows its parent categories wherever nothing beside it does**, as
  "Parent/Child", joined with `/` as the register joins them. A chart's legend, ranked
  bars, tooltips and Other name every subcategory that way, on Reports and in a home
  report tile. In the table an indented row keeps its own name, since its parent is the
  row above; a row at the top level whose parent has no row (roll-up off, or a parent
  with nothing in range) shows the whole path, and the top level sorts by it. Where a
  name is cut to fit, the parent part gives way first and the subcategory's own name
  stays; the whole name is on hover.

### For anything calling the API directly

- **`GET …/reports/summary` adds `groupPath`** to every row, every group total and
  `rest.only`: a category's names from its top-level category down to its own, or the
  one name for any other grouping. It is a list, so a name containing `/` is never split.
  `groupName` is unchanged, still the group's own name.

### MCP

- **`transaction_summary` returns `groupPath`** wherever it returns `groupName`, and its
  description asks the client to name a category to the user by its path.

## Returns: a money-weighted figure for every row, and a chart you can follow (0.141.0)

Nothing to run. No migration; no row is rewritten.

### In Reports and on the home screen

- **Money-weighted is the 12 months ending at each row.** The Returns table's
  money-weighted column was the annualised rate from the start of the range, shown only
  once a year was covered, so the first year of any range read "under a year covered"
  even with years of history before it. Each row now shows the money-weighted return of
  the year ending there, computed from history before the range where the year reaches
  back past it. The column reads "Money-weighted, 12 mo". A row still reads "under a year
  covered" when nothing was invested as its year began.
- **An opening balance counts from the account's start date.** An investment account
  opened with an opening balance was counted as holding that balance before its start
  date too. For the whole ledger, a range that began before such an account opened carried
  its balance, idle, from the range's start, so until it opened the index and both rates
  were pulled toward zero, a gain reading smaller and a loss too. The balance now comes in
  on the start date as money in, like a contribution, and a range that starts on that
  date shows it the same way, as contributed on its first day. If the account has
  transactions dated before its start date, the balance counts from the first of them,
  as the register shows it. Gain is unchanged, except that for an account in another
  currency the ledger no longer counts the exchange rate's move on that balance before
  the account opened. An account opened with a zero balance, as most imported ones are,
  is unaffected, and so is an account's own report since inception.
- **The first row of a range of dates no longer says "First invested point."** It is
  where the range starts. Since inception, the first invested point is still named.
- **The Returns chart lights a line.** Hovering a name in the legend, or a line on the
  chart, lights that line and dims the rest, as the category charts do; over a line, a
  tooltip names it, the nearest period end and its value.
- **The Returns legend matches the other reports.** Each line has the same square
  colour chip, and its latest value beside its name. The coloured values stacked at the
  chart's right edge are gone; they read like an axis.
- **The Returns table scrolls on its own.** It no longer adds a second scrollbar to the
  page beside its own.
- **A category's path in Reports is one colour.** In 0.140.0 the parent part of
  "Parent/Child" was shown lighter than the subcategory's own name, which read as the
  subcategory being highlighted. The whole name now takes the colour of the text around
  it, in the chart's legend, the ranked bars and the table. Where a name is cut to fit,
  the parent part still gives way first.

### For anything calling the API directly

- **`GET …/reports/returns-series` renames `mwrToDate` to `mwrTrailingYear`**, on every
  point, and changes what it is: the money-weighted return of the 12 months ending at
  that point, which is what `GET …/reports/returns` gives for that year as its window.
  `mwrUnavailableReason` keeps its codes; `under_a_year_covered` now means nothing was
  invested as the year opened. A rate missing only for the year before the range leaves
  this figure alone null with `rate_unavailable`, and the point's `rateUnavailable` names
  it.
- **`GET …/reports/returns` and `…/returns-series` count a funded opening balance from its
  start date**, or from the account's first transaction when that is earlier. A window
  valued before then (one that opens before it, or whose `from` is that day) values the
  brokerage without the balance and takes it as a flow at that instant:
  `netContributions` includes it, in `contributionsIn` (or `contributionsOut` for a
  negative balance), and `contributionsBySource` lists it under a new source,
  `opening_balances`. The account's `accounts` row starts from what its postings held.
  A since-inception call no longer anchors on a funded start date after its `to`, where
  it read the balance as a loss. `gain` is unchanged but for the currency move noted above.

### MCP

- **`returns_series` returns `mwrTrailingYear` in place of `mwrToDate`**, with the
  meaning above, and its description says so.
- **`returns` and `returns_series` count a funded opening balance from its start date**
  (or the account's first transaction, if earlier), as above; `returns` lists
  `opening_balances` among the contribution sources and its description says when.

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

An upgrade started from the app has the labelled backup ("before upgrade X to Y", in the
Backups list) to restore. It was taken before the upgrade, so the restore loses what was
entered after it.

---

## If an upgrade fails midway

`install.sh` is built so this shouldn't strand you: it proves the fetched compose
resolves before removing anything from `.env`, and puts the previous compose back if
it doesn't. If it stops with *"The fetched docker-compose.yml does not resolve"*:

- Your `.env` is untouched and the install is as it was.
- `secrets/` may have been written. Nothing reads it yet, so that's inert.
- The usual cause is config from the wrong repo — re-run with `COFFER_REPO` set.
