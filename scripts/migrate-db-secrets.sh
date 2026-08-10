#!/usr/bin/env bash
# Move an existing install's database passwords out of .env into secret files.
#
# The compose file now passes all three Postgres passwords as *_FILE paths
# pointing at ./secrets/ (docker-compose `secrets:`), because an environment
# variable is readable via `docker inspect`, /proc/<pid>/environ, any child
# process's environment and crash dumps -- the same reasoning ADR-0092 D1
# applied to the master KEK.
#
# This does NOT rotate anything. It copies the values already in .env into files
# and comments out the .env lines, so the credentials the database already knows
# keep working. Rotating on top of a migration would mean two things could fail
# at once, and you would not know which.
#
# Idempotent: re-running when the files already exist and .env is already
# migrated is a no-op.
#
# Usage:
#   scripts/migrate-db-secrets.sh              # migrate ./.env  -> ./secrets/
#   ENV_FILE=g.env SECRETS_DIR=secrets-g scripts/migrate-db-secrets.sh
#
# Run this BEFORE `docker compose up -d` with the new compose file. Compose
# treats a missing secret file as a hard error, so the failure mode if you skip
# it is loud rather than silent.
set -euo pipefail

# The directory to operate on. Defaults to the repo root (dev), but an install
# created by install.sh has no repo — just ~/coffer with a compose file, db/init
# and .env — so this has to be pointable at an arbitrary install directory or it
# is unusable exactly where the migration is needed.
INSTALL_DIR="${COFFER_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
cd "$INSTALL_DIR" || { echo "[migrate-db-secrets] ERROR: no such directory: $INSTALL_DIR" >&2; exit 1; }

ENV_FILE="${ENV_FILE:-.env}"
SECRETS_DIR="${SECRETS_DIR:-secrets}"

die() { echo "[migrate-db-secrets] ERROR: $*" >&2; exit 1; }
info() { echo "[migrate-db-secrets] $*"; }

[ -f "$ENV_FILE" ] || die "$ENV_FILE not found (run from the install directory)."

# 0700: other local users can't traverse in, so they can't read the files even
# though the files themselves have to be readable by the container uid.
mkdir -p "$SECRETS_DIR"
chmod 700 "$SECRETS_DIR" 2>/dev/null || true

read_env() { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2- || true; }

# secret_file <env-var> <secret-name>
# Writes the value from .env into the secret file. Never prints the value.
migrated=0
skipped=0
write_secret() {
    local var="$1" name="$2" path="$SECRETS_DIR/$2" value
    value="$(read_env "$var")"

    if [ -s "$path" ]; then
        info "$name: already present, left alone."
        skipped=$((skipped + 1))
        return 0
    fi
    if [ -z "$value" ]; then
        die "$var is not set in $ENV_FILE and $path does not exist. Nothing to migrate for $name — set one or the other before running this."
    fi

    # printf, not echo: no trailing newline surprises, and no interpretation of
    # backslashes in a password.
    printf '%s' "$value" > "$path"
    chmod 644 "$path" 2>/dev/null || true
    info "$name: written ($(wc -c < "$path") bytes)."
    migrated=$((migrated + 1))
}

write_secret POSTGRES_PASSWORD        postgres_password
write_secret COFFER_SERVICE_PASSWORD  coffer_service_password
write_secret COFFER_APP_PASSWORD      coffer_app_password

# Comment out the .env lines rather than deleting them: if something about the
# new arrangement is wrong, the operator can see what the value WAS and put it
# back. The compose file no longer reads these, so leaving them live would just
# mean two copies of the same secret, which is the thing being fixed.
if grep -qE '^(POSTGRES_PASSWORD|COFFER_SERVICE_PASSWORD|COFFER_APP_PASSWORD)=' "$ENV_FILE"; then
    cp "$ENV_FILE" "$ENV_FILE.pre-secrets"
    chmod 600 "$ENV_FILE.pre-secrets" 2>/dev/null || true
    tmp="$(mktemp)"
    # Delimiter is @, not | — | is the alternation operator here, so using it as
    # the delimiter too would end the pattern at the first branch.
    sed -E 's@^(POSTGRES_PASSWORD|COFFER_SERVICE_PASSWORD|COFFER_APP_PASSWORD)=@# moved to '"$SECRETS_DIR"'/ by migrate-db-secrets.sh: \1=@' \
        "$ENV_FILE" > "$tmp"
    mv "$tmp" "$ENV_FILE"
    chmod 600 "$ENV_FILE" 2>/dev/null || true
    info "commented the three password lines out of $ENV_FILE (backup: $ENV_FILE.pre-secrets)."
    info "Delete that backup once the stack is confirmed healthy — it still contains the passwords."
else
    info "$ENV_FILE has no password lines left to comment out."
fi

info "done — $migrated written, $skipped already present."
info "Next: docker compose up -d   (recreates the containers with the file-based secrets)"
