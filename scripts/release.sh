#!/usr/bin/env bash
# =============================================================================
# release.sh — the SINGLE writer of the release version.
# =============================================================================
#
# The version is duplicated by necessity across three files, because MSBuild and
# npm each need a literal in their own manifest:
#
#   * src/Api/Api.csproj        <Version>   — the assembly version the API reports
#                                             at /api/meta/version + the ghcr image
#                                             tag the release workflow derives.
#   * src/Web/package.json      "version"
#   * src/Web/package-lock.json "version"   (root + the "" package entry)
#
# Duplication in the FILES is unavoidable; drift is not. This script is the one
# way to bump, so the three can never disagree — it writes them all from a single
# argument and then VERIFIES they match, failing loudly on any inconsistency (which
# also surfaces any pre-existing drift). npm owns its two files, so `npm version`
# bumps them (no hand-editing the lock); sed owns the csproj tag.
#
# It commits `chore(release): X.Y.Z` and tags `vX.Y.Z`, but does NOT push — pushing
# the tag triggers the ghcr.io image build (.github/workflows/release.yml), an
# outward action left as an explicit final step the script prints.
#
#   scripts/release.sh 0.38.0            # bump + commit + tag (then push manually)
#   scripts/release.sh 0.38.0 --dry-run  # show the bump diff, change nothing
# =============================================================================
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

csproj="src/Api/Api.csproj"
pkg="src/Web/package.json"
lock="src/Web/package-lock.json"

die() { echo "release: $*" >&2; exit 1; }

new=""
dry_run=0
for arg in "$@"; do
    case "$arg" in
        --dry-run) dry_run=1 ;;
        -*)        die "unknown option: $arg" ;;
        *)         [ -z "$new" ] && new="$arg" || die "unexpected argument: $arg" ;;
    esac
done

[ -n "$new" ] || die "usage: scripts/release.sh <version> [--dry-run]   (e.g. 0.38.0)"
printf '%s' "$new" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' \
    || die "version must be semver X.Y.Z (got '$new')"

cur="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$csproj" | head -1 | tr -d '[:space:]')"
[ -n "$cur" ] || die "could not read current <Version> from $csproj"

# Preconditions (skipped for --dry-run, which mutates nothing durable): a clean
# tree on main, so the release commit is exactly the version bump and nothing else.
if [ "$dry_run" -eq 0 ]; then
    [ -z "$(git status --porcelain)" ] || die "working tree not clean — commit or stash first"
    branch="$(git rev-parse --abbrev-ref HEAD)"
    [ "$branch" = "main" ] || die "not on main (on '$branch') — release from main"
    git rev-parse "v$new" >/dev/null 2>&1 && die "tag v$new already exists"
fi

echo "release: $cur → $new"

# --- Bump every location from the single argument -----------------------------
# npm owns package.json + package-lock.json — let it bump both (no git side-effects).
( cd src/Web && npm version "$new" --no-git-tag-version --allow-same-version >/dev/null )
# csproj <Version> — version-agnostic replace so it bumps regardless of the old value.
sed -i "s:<Version>[^<]*</Version>:<Version>$new</Version>:" "$csproj"

# --- Verify consistency (the anti-drift gate) ---------------------------------
fail=0
grep -q "<Version>$new</Version>" "$csproj" || { echo "release: $csproj not at $new" >&2; fail=1; }
grep -q "<Version>$cur</Version>" "$csproj" && { echo "release: $csproj still has stale $cur" >&2; fail=1; }
pkg_ver="$(sed -n 's/.*"version": "\([^"]*\)".*/\1/p' "$pkg" | head -1)"
[ "$pkg_ver" = "$new" ] || { echo "release: $pkg at '$pkg_ver', expected $new" >&2; fail=1; }
lock_root="$(sed -n 's/.*"version": "\([^"]*\)".*/\1/p' "$lock" | head -1)"
[ "$lock_root" = "$new" ] || { echo "release: $lock at '$lock_root', expected $new" >&2; fail=1; }

if [ "$fail" -ne 0 ]; then
    git checkout -- "$csproj" "$pkg" "$lock" 2>/dev/null || true
    die "consistency check failed — version files reverted, nothing committed"
fi

# --- Dry run: show the diff, revert, done -------------------------------------
if [ "$dry_run" -eq 1 ]; then
    echo "release: --dry-run diff ↓"
    git --no-pager diff --stat -- "$csproj" "$pkg" "$lock"
    git checkout -- "$csproj" "$pkg" "$lock"
    echo "release: dry run — files reverted, no commit/tag."
    exit 0
fi

# --- Commit + tag (push is the operator's explicit step) ----------------------
git add "$csproj" "$pkg" "$lock"
git commit -m "chore(release): $new" >/dev/null
git tag "v$new"

cat <<EOF
release: bumped $cur → $new across $csproj, $pkg, $lock; committed + tagged v$new (NOT pushed).

Publish (this triggers the ghcr.io image build — .github/workflows/release.yml):

    git push origin main && git push origin v$new
EOF
