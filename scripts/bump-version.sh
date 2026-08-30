#!/usr/bin/env bash
#
# Bump the Stratara lockstep <VersionPrefix> in Directory.Build.props,
# commit, and tag. Pushing the commit + tag is the caller's job
# (do it via `git push && git push origin refs/tags/v<x.y.z>`).
#
# Usage:
#   ./scripts/bump-version.sh patch   # 0.1.0 → 0.1.1
#   ./scripts/bump-version.sh minor   # 0.1.0 → 0.2.0
#   ./scripts/bump-version.sh major   # 0.1.0 → 1.0.0
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROPS_FILE="${ROOT_DIR}/Directory.Build.props"

usage() {
    cat <<EOF
Usage: $0 <major|minor|patch>

Examples:
  $0 patch   # 0.1.0 → 0.1.1
  $0 minor   # 0.1.0 → 0.2.0
  $0 major   # 0.1.0 → 1.0.0
EOF
    exit 1
}

[[ $# -ne 1 ]] && usage

BUMP_TYPE="$1"
case "${BUMP_TYPE}" in
    major|minor|patch) ;;
    *) usage ;;
esac

# Cross-platform sed -i: BSD/macOS needs the in-place suffix arg (`-i ''`),
# GNU/Linux does not accept a separate arg. Detect once via dialect probe.
sed_inplace() {
    if sed --version >/dev/null 2>&1; then
        sed -i "$@"
    else
        sed -i '' "$@"
    fi
}

CURRENT="$(sed -n 's/.*<VersionPrefix>\([^<]*\)<\/VersionPrefix>.*/\1/p' "${PROPS_FILE}")"
if [[ -z "${CURRENT}" ]]; then
    echo "Error: Could not read <VersionPrefix> from ${PROPS_FILE}" >&2
    exit 1
fi

IFS='.' read -r MAJOR MINOR PATCH <<< "${CURRENT}"

case "${BUMP_TYPE}" in
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0 ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0 ;;
    patch) PATCH=$((PATCH + 1)) ;;
esac

NEW_VERSION="${MAJOR}.${MINOR}.${PATCH}"

echo "Bumping <VersionPrefix>: ${CURRENT} → ${NEW_VERSION}"

sed_inplace "s|<VersionPrefix>${CURRENT}</VersionPrefix>|<VersionPrefix>${NEW_VERSION}</VersionPrefix>|" "${PROPS_FILE}"

# Verify
ACTUAL="$(sed -n 's/.*<VersionPrefix>\([^<]*\)<\/VersionPrefix>.*/\1/p' "${PROPS_FILE}")"
if [[ "${ACTUAL}" != "${NEW_VERSION}" ]]; then
    echo "Error: sed bump failed (props still shows ${ACTUAL})" >&2
    exit 1
fi

cd "${ROOT_DIR}"

if [[ -n "$(git status --porcelain Directory.Build.props)" ]]; then
    git add Directory.Build.props
    git commit -m "chore: bump version to ${NEW_VERSION}"
    git tag -a "v${NEW_VERSION}" -m "Release ${NEW_VERSION}"
    echo ""
    echo "Bumped to ${NEW_VERSION}. Tag v${NEW_VERSION} created."
    echo ""
    echo "Push with:"
    echo "  git push && git push origin refs/tags/v${NEW_VERSION}"
else
    echo "Error: Directory.Build.props was modified but git sees no change. Aborting." >&2
    exit 1
fi
