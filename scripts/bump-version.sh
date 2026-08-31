#!/usr/bin/env bash
#
# Bump the Stratara lockstep <VersionPrefix> in Directory.Build.props,
# commit, and tag. Pushing the commit + tag is the caller's job
# (do it via `git push && git push origin refs/tags/v<x.y.z>`).
#
# The prerelease form tags a build of the version the tree is already working
# toward, so it changes no file and creates no commit — <VersionPrefix> stays put
# for the whole cycle and only the tag differs. That is what release.yml expects:
# it compares the tag's release part against the props and passes the identifier
# through as VersionSuffix.
#
# Usage:
#   ./scripts/bump-version.sh patch              # 0.1.0 → 0.1.1
#   ./scripts/bump-version.sh minor              # 0.1.0 → 0.2.0
#   ./scripts/bump-version.sh major              # 0.1.0 → 1.0.0
#   ./scripts/bump-version.sh prerelease rc.1    # tag v<current>-rc.1, no file changes
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROPS_FILE="${ROOT_DIR}/Directory.Build.props"

usage() {
    cat <<EOF
Usage: $0 <major|minor|patch>
       $0 prerelease <identifier>

Examples:
  $0 patch                # 0.1.0 → 0.1.1
  $0 minor                # 0.1.0 → 0.2.0
  $0 major                # 0.1.0 → 1.0.0
  $0 prerelease preview.1 # tag v<current>-preview.1, changing no file

The prerelease form does not touch Directory.Build.props and creates no commit.
It tags the current commit as a build of the version already declared there.
EOF
    exit 1
}

BUMP_TYPE="${1:-}"
case "${BUMP_TYPE}" in
    major|minor|patch) [[ $# -ne 1 ]] && usage ;;
    prerelease)        [[ $# -ne 2 ]] && usage ;;
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

# --- prerelease: tag only, no file changes ---------------------------------
#
# Every segment of the identifier must be all-digits or all-letters, never a mix.
# SemVer compares a purely numeric segment numerically and anything else as a
# string, so `preview.10` sorts after `preview.9` while `preview10` sorts before
# `preview9`. A published order cannot be corrected, only added to.
if [[ "${BUMP_TYPE}" == "prerelease" ]]; then
    IDENTIFIER="$2"

    if [[ ! "${IDENTIFIER}" =~ ^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$ ]]; then
        echo "Error: '${IDENTIFIER}' is not a valid SemVer prerelease identifier." >&2
        exit 1
    fi
    IFS='.' read -ra SEGMENTS <<< "${IDENTIFIER}"
    for segment in "${SEGMENTS[@]}"; do
        if [[ "${segment}" =~ [0-9] && "${segment}" =~ [A-Za-z] ]]; then
            echo "Error: segment '${segment}' mixes letters and digits, which orders as a string." >&2
            echo "Separate them with a dot — 'preview.1', not 'preview1' — or SemVer sorts" >&2
            echo "'${segment%%[0-9]*}10' before '${segment%%[0-9]*}9'. A published order cannot be fixed." >&2
            exit 1
        fi
    done

    TAG="v${CURRENT}-${IDENTIFIER}"

    if git rev-parse -q --verify "refs/tags/${TAG}" >/dev/null; then
        echo "Error: tag ${TAG} already exists. A prerelease identity is never reused." >&2
        exit 1
    fi
    if [[ -n "$(git status --porcelain)" ]]; then
        echo "Error: the working tree is dirty. A tag must name a committed state." >&2
        exit 1
    fi

    git tag -a "${TAG}" -m "Prerelease ${CURRENT}-${IDENTIFIER}"
    echo ""
    echo "Tagged ${TAG} at $(git rev-parse --short HEAD). Nothing was committed — ${PROPS_FILE##*/} still says ${CURRENT}."
    echo ""
    echo "Push with:"
    echo "  git push origin refs/tags/${TAG}"
    echo ""
    echo "That starts release.yml. Its pack job holds no credential; the push to nuget.org"
    echo "waits for a required reviewer in the nuget-org environment."
    exit 0
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
