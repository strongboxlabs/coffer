# 0110 — Upgrade from the app: a host updater runs `upgrade.sh`, and the API never touches Docker

* Status: Accepted (amends 0062, 0075, 0105)
* Date: 2026-10-31
* Related: [0075](0075-linux-install-script.md) (the install and upgrade scripts),
  [0059](0059-deployment-packaging.md) (packaging),
  [0062](0062-google-drive-backup-sync.md) (the other opt-in egress),
  [0054](0054-market-data-quote-provider.md) (market data, the first),
  [0060](0060-whole-db-backup-and-admin-role.md) (whole-database backups),
  [0092](0092-kek-lifecycle-in-the-ui.md) (the master key and what it seals),
  [0105](0105-a-cookie-alone-is-not-enough.md) (the passkey tap for a sensitive action),
  [0023](0023-ui-ux-interaction-conventions.md) (the UI design is approved as its own step).

## Context

Upgrading an install meant running `upgrade.sh` in a terminal on the host. An admin using the
app could not see that a newer release existed, and could not start the upgrade without
shell access. The app cannot do it itself: an upgrade pulls an image and recreates the
API's own container, which needs the Docker socket, and a Docker socket in the API container
is root on the host. Nothing in the container image, the capabilities or the user was going to
change for this feature.

## Decision

### D1 — A host updater runs the existing upgrade; the API container gains one volume

A small script on the host, `app-upgrades.sh`, runs from the install user's crontab every
minute (no systemd, no sudo). When a valid request is waiting it starts `upgrade.sh <version>`
detached, which fetches that release's own `install.sh` at its tag, exactly as an upgrade by
hand does. Each release's volume hand-over, digest pin and compose changes therefore still
apply ([0075](0075-linux-install-script.md)). The API container gets no Docker socket, no
capability and no new user. Linux with a cron daemon is first. macOS, WSL2, a host where docker
needs sudo, and a host with no cron daemon keep the host command, and the tab names the
recorded reason.

### D2 — The channel is a dedicated volume holding empty files whose names carry the message

`coffer_mailbox`, mounted at `/app/mailbox`, is separate from `coffer_data` so the host reads it
without ever mounting the data or the keys. The Dockerfile creates the directory owned by uid
1654 with mode 0700, so Docker seeds a new volume with that ownership.

- A request is an empty file named `req_v1_<32 hex id>_<x.y.z>_<10-digit epoch>`. One anchored
  pattern accepts it.
- The host writes status as `st_v1_<id>_<state>_<from>_<to>_<epoch>`, and its own state as one
  replaced name, `hb_v1_<state>_<epoch>` (`running` or `disabled`).
- The host reads and writes through one hardened throwaway container per tick, started from the
  API's own image ID so nothing is pulled: uid 1654, no network, read-only root, all
  capabilities dropped, memory capped, an in-container timeout, and a listing cut at a byte
  bound inside the container. It lists regular files only, NUL-delimited, and keeps names
  matching the pattern exactly, under the C locale. It deletes a consumed request by its exact
  validated name and does nothing else.
- A volume subpath and `--volumes-from` were rejected. A subpath drops the 0700 parent and makes
  the root daemon resolve a path the API controls. `--volumes-from` also mounts the key volume.

### D3 — The host decides everything itself; the request is only a hint

The API can write any well-formed name, so a request carries no authority. The host acts on one
only when all of these hold:

- the version equals the latest release the host resolved itself, with its own tokens;
- it is strictly newer than what is installed, and anything unparseable fails closed;
- it was issued in the last 15 minutes and is not dated more than a minute ahead;
- its id is not in the consume-once set, which the host keeps outside the volume. The host
  records the id durably, then deletes the request, then starts the run, so a crash in between
  loses a request and never starts one twice;
- the target `install.sh` carries the contract marker (D4).

The tick is 60 seconds, the shortest cron allows. A heartbeat is stale after three missed
ticks, and the tab then says the host is not responding. Ticks continue while an upgrade runs.
The updater never kills a running upgrade: when the readiness wait times out the migration may
still be writing, so it reports that and keeps observing.

A run marks itself: a `running` file naming the boot, its process and that process's start time,
and the request. A tick that finds the marker's run gone (the host restarted, or the process was
killed) has no exit status to report, and nothing says how far the run got. It records exit 12,
past the first change, so the admin is told and the next upgrade waits for `ack`. A reused PID
does not pass for the run, because the start time and the boot must match too. A marker the run
has not yet written to counts as alive for two minutes.

### D4 — `install.sh` is a callable contract

Upgrade mode stays the single path that changes the application install. It gains:

- an unattended mode (`COFFER_UNATTENDED=1`, an in-place upgrade to a named release, no sudo and
  no prompt; anything that would ask stops the run);
- a preflight before the first change: the release's files are fetched into a temporary
  directory, the fetched compose file must resolve against this host's `.env` (which also
  catches a Docker Compose too old for it), and the image must pull (which also fails when it
  does not fit on disk). A failure there leaves `.env` and the compose file byte-identical;
- distinct exit codes: 10 the preflight failed and nothing changed, 11 failed before the first
  change, 12 failed after it, 14 the readiness wait timed out, and 13 from `upgrade.sh` when the
  fetched `install.sh` predates the contract;
- `coffer-phase:` lines on stdout, and a contract version marker the host checks. The marker is
  a compatibility check and proves nothing about integrity.

`.env.bak` and `docker-compose.yml.bak` stay one generation beside their files. They are
replaced only when the previous run finished, so repeating a failed run no longer overwrites the
state it started from. A marker written after readiness records that.

### D5 — The release check is the API's, opt-in, and the host never reads it

The API asks the GitHub API for the latest release of the install's repository (the same
`COFFER_REPO` the host uses). It checks lazily, at most every six hours, and only while the
automatic check is on (default off). **Check now** always works. This is outbound traffic to
`api.github.com`, off until an admin switches it on or presses Check now, and it joins market
data ([0054](0054-market-data-quote-provider.md)) and Google Drive
([0062](0062-google-drive-backup-sync.md)) as documented egress. *(Amended 2026-11-13:
exchange rates from Frankfurter, opt-in per user and off by default, are a fourth;
[ADR-0114](0114-benchmarks-and-currency-conversion.md) D4, D8.)*

What the check finds (version, notes, date, link) is for display. Notes are plain text, cut at
a fixed length and rendered escaped, because the page sits in front of a privileged action.
The link is kept only when it points at github.com. The host resolves the release again for
itself.

A public repository needs no token. A private fork's repository token is entered in the app and
sealed under the master KEK in `upgrade_repo_token`. It joins the three places a master-sealed
value is registered ([0092](0092-kek-lifecycle-in-the-ui.md)): rotation re-wraps it,
post-restore reconciliation discards one this install cannot open, and the key probe counts it.
It goes to GitHub only, and no error names it.

*Amended 2026-11-02: an available release is announced, and the check is not tied to the tab.*

- **A notification, like any problem.** A check that finds a release newer than the install announces
  it as a deployment-scope notification: topic `upgrade`, subject `upgrade.`, `upgrade.available` at
  warning, cleared by `upgrade.current` at info ([0096](0096-notification-bus-and-two-event-scopes.md),
  [0102](0102-a-problem-you-are-not-looking-at.md)). Warning, because the badge takes warning and above
  and a third visual state is ruled out; nothing is dismissible, so an install that stays behind on
  purpose shows a standing dot. It is announced once per version. A manual check and the automatic
  one reach the same code, so the indicator does not depend on how a release was found.
- **The all-clear needs no check.** It is published when the installed version reaches what was
  announced (or a check finds nothing ahead), from the scheduler's tick and from the admin read
  behind the navigation, so an install that upgraded with the automatic check off does not keep a dot
  for a release it runs.
- **The last good result is stored** (`system_settings`, `upgrade.last_check`) and read back once
  after a restart, so the tab and the dot show what they showed before. `upgrade.announced_version`
  remembers what was announced.
- **The automatic check runs wherever an admin is.** The admin read behind the System-settings gear
  runs the same lazy check, so with the automatic check on, any page an admin has open can check,
  at most every six hours. It is still opt-in, and with it off nothing is sent except by Check now.
- **Outside the app it is opt-in per destination.** `upgrade` is the first notification topic that a
  destination hears only by naming it ([0096](0096-notification-bus-and-two-event-scopes.md)).

### D6 — Every upgrade takes a fresh verified backup, held with an upgrade pin

There is no skip. The API starts a backup off the request, waits for it to be verified (read back
and decrypted), then pins it and only then writes the request. With no backup passphrase set the
gate blocks and says so. `backup_pins` gains a `kind` (`admin` or `upgrade`) and a `label`
("before upgrade 0.128.1 to 0.129.0"), shown in the backup list without the word pin.

- The newest two upgrade pins are kept, trimmed once per request when an upgrade succeeds, so a
  failed attempt cannot push out the backup that predates it.
- An attempt that ended before the first change (host exits 10, 11 and 13, or a refusal) releases
  its pin at once and the backup returns to normal retention. An attempt that failed after it,
  or ended unknown, keeps it, and new upgrades stay blocked until the host runs `ack`.
- An admin pin on an upgrade-kind backup converts the row to `admin`, which exempts it from the
  trim. Retention and the Drive mirror are otherwise unchanged.
- A verified backup can be read back. That does not prove it restores, and the tab says so.
- Rollback is the documented restore of the labelled backup on the release it came from. It loses
  writes made after the backup, and nothing rolls back automatically, because migrations are not
  reversible.

### D7 — Starting one needs the admin cookie, a typed phrase and a passkey tap

Every upgrade route is `RequireAdmin`, which is the cookie session and not an MCP bearer, so a
connected app cannot start one whatever it was granted. A test pins the routes to that policy.
Starting one needs the phrase `upgrade` (trimmed, case-insensitive, checked server-side before
the tap) and a fresh passkey tap for the new step-up action `upgrade`
([0105](0105-a-cookie-alone-is-not-enough.md), migration 270). The server re-reads the latest
release at that moment and refuses a version that is not the latest and newer. The tap comes
first and the backup runs after the request has answered, as a tap is valid for two minutes and a
backup can take longer.

### D8 — The in-app switch gates the API, and the host has its own switch

An admin setting, on by default, controls whether the app may start upgrades. The host has
`enable` and `disable`; `disable` leaves the cron line and the tick in place, so the tab can tell
disabled from broken, and `install.sh` never re-enables a disabled host. The cron machinery is
installed by default on every Linux install that has a cron daemon and where the install user
can run docker without sudo, and `install.sh` records the platform and the reason when it cannot
be.

### D9 — Tokens for a private fork

The repository token (fine-grained, Contents: read) is held by the app (D5) and by the host as a
0600 file. The registry token (classic, `read:packages` only) is held by the host only, in a
0600 file set with `app-upgrades.sh set-token`. Both files live in `upgrades/secrets/` under the
install directory (mode 0700), and the updater passes them to `upgrade.sh` for the run only.
Neither is mounted into a container or placed in `.env`, argv, logs or status. `docker login`
saves the registry login where docker keeps its config, so a run with a stored token gets a Docker
config of its own (`upgrades/docker-cfg.<id>`, mode 0700, with the install user's Compose plugin
and contexts linked in). The run logs out of ghcr.io and deletes it when it ends, and the next tick
deletes one a killed run left behind, unless a run is in progress. If the directory cannot be
made, the tokens are withheld and the upgrade stops in its preflight, changing nothing. A fork
without stored tokens keeps the install user's config. Upgrading by hand keeps its login, which is
why that token can read packages and nothing else
([0075](0075-linux-install-script.md), 2026-10-19 amendment). That amendment said these tokens
are stored nowhere; for a fork that wants upgrades from the app, they are now stored on the host.

### Amendment (2026-11): the state of the tab, and the checks before the tap

D7 put the tap first so that nothing the screen could see would spend one. That left everything
only the server can see: a release newer than the one the screen shows, a backup already running,
a request still waiting, no backup passphrase, a host that refuses. Each of these refuses only
after the tap, or after the backup and a fifteen-minute wait, unless it is checked first. These
now are.

- **A preflight before the tap.** `POST /api/admin/upgrade/preflight` takes the same body as start
  and gives every refusal that needs no tap: the phrase, an unavailable or blocked host, no backup
  passphrase, something running, and a version that is not the latest (read fresh). The tab asks it
  first, and on any refusal reads the status again, so the screen shows what the server saw. Start
  runs the same checks, since time passes between the two.
- **The host says when it is blocked, or cannot tell what is installed.** After a run fails past
  its first change the heartbeat reads `blocked` until `ack` (which now runs a tick, so it changes
  at once), so the tab keeps its button off with the reason and the command, and stops showing the
  failure once `ack` has been run. Before, the app never learned of the block: it forgot it after
  24 hours, and the next upgrade died as expired. An install whose `COFFER_IMAGE_TAG` is not a
  version (it tracks `latest`) reads `unpinned`: the host cannot tell what it runs, so the tab
  says to upgrade by hand instead of spending a tap and a backup on a request the host refuses.
- **A request the host cannot decide still expires.** The host refuses a request past its expiry,
  and an install whose `COFFER_IMAGE_TAG` is not a version, before it looks the release up. The API
  counts only requests inside that expiry as holding the mailbox, so a dead request does not turn
  every retry into "already in progress".
- **A restart does not lose a run under way.** With nothing in memory the tab reads the newest
  request's status, and one the host has only started is still in progress. An older request's
  result no longer stands in for it.
- **A failed check keeps the release last found.** The screen shows it with the failure beside it;
  a decision (the start) still rests on a release read in that same call.
- **A result is for the page that watched it.** The success card, and a failure that changed
  nothing, appear on the page that saw the run; the success card offers a reload, since that page
  is the old version of the app. A failure that changed the install shows while the host is
  blocked. No result hides the release card or Check now. A finish seen by polling asks for the
  gear and the tab dots again.

## Who can do what

| Actor | Can start an upgrade? | Why |
|---|---|---|
| Viewer, editor, ledger owner | No | `RequireAdmin`. |
| An MCP or connected-apps token, an admin's included | No | The cookie scheme only. |
| A stolen admin cookie, or CSRF | No | Needs a fresh passkey tap, and the same-origin checks apply. |
| A script running in the admin's page | One tap away | It can fill the phrase. The tap is the only barrier, and the step-up challenge is not bound to a version. |
| Any admin | Yes, after the phrase, a tap and a fresh verified backup | The feature. |
| Code running in the API container | Yes, bounded | It can write well-formed requests with no tap and no fresh backup, even when the in-app switch is off. The host runs only the standard upgrade to the latest release it resolved itself, one at a time. It cannot choose code, repository, version or path, and cannot read the host's tokens. It can read the repository token of a private fork, write status and state names (so the tab is the API's word about the host and `app-upgrades.sh status` is authoritative), and fill the mailbox with names that hide requests until it is cleared. |
| Anyone in the install user's account or the docker group | Yes | Root-equivalent. A user-owned crontab is no boundary beyond that. |
| Whoever controls the release pipeline | Yes | The target `install.sh` runs on the host and receives the tokens. Nothing verifies it beyond a text marker and a git tag, and the image is pinned only by the digest asset. That is the trust extended when `upgrade.sh` is run by hand. The difference is that a tap now starts it with nobody watching a terminal. |

## Consequences

- **A path from a web application to a root-equivalent host action now exists** on every install
  that has a cron daemon and docker access, and an existing install gains it when it upgrades to
  the first release that carries it. `disable` ends the effect. The host does not verify the
  admin's passkey: a compromised API serves the web bundle and builds every passkey prompt, so a
  host-held challenge would travel inside an ordinary prompt. The cost is large and the gain
  small, so host-verified approval is a follow-up. The admin audit row cannot prove a request
  came from a tap; the host's history and `app-upgrades.sh status` are the authoritative record.
- The repository token of a private fork is at rest in the database, in every backup and on the
  host. Use a fine-grained token for that one repository, with an expiry.
- An install older than this feature upgrades once by hand, and that release's `install.sh`
  installs the machinery. A target release that predates the contract is refused, and the tab
  shows the manual command.
- The API image must carry `sh`, `bash`, `find` with `-printf`, `head` and `timeout`, which the
  host's reader runs inside it. A CI assertion covers it.
- The compose file gains the `coffer_mailbox` volume, which touches every install.

## Limits

- Latest release only. An older or intermediate release needs a downgrade story the scripts do
  not have.
- No automatic installs. Opting into the check never installs anything, and every upgrade the
  app starts needs the phrase and a tap. The host enforces neither.
- Linux with a cron daemon, where the install user can run docker without sudo. AppArmor and
  SELinux hosts are not supported, and the design is not tested against them.
- The mailbox has no backoff and no clear command. The API can repeat a failing request every
  tick, which costs the host GitHub calls and registry traffic and can reach GitHub's per-IP rate
  limit. A flood of names that match the glob but not the exact pattern is never pruned and can
  hide requests until the mailbox is emptied by hand ([operations](../operations.md#upgrading-from-the-app)).
- Host history is capped, which shortens the window in which a request can be compared against it.

## Follow-ups

- macOS and WSL backends for the updater.
- A host-verified passkey assertion.
