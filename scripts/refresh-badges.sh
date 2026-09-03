#!/usr/bin/env bash
#
# Regenerate the badge images the documentation site serves from docs/assets/badges/.
#
# The site serves them itself rather than hot-linking a badge service, so that a visitor's browser
# makes no third-party request before that visitor has agreed to anything. The cost of that is
# these files going stale, which is what `LandingBadgeTests` fails on and this script fixes.
#
# The version comes from the newest dated section of CHANGELOG.md — the same source llms.txt uses,
# and for the same reason: <VersionPrefix> names the version being worked toward, which stays
# unreleased for the whole cycle.
#
# Usage: ./scripts/refresh-badges.sh

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BADGE_DIR="${ROOT_DIR}/docs/assets/badges"

# BSD and GNU sed disagree about \+ in a basic regex, so the match is grep -E's job and sed
# only trims the brackets off what it found.
VERSION="$(grep -m1 -E '^## \[[0-9]+\.[0-9]+\.[0-9]+\].*[0-9]{4}-[0-9]{2}-[0-9]{2}' \
    "${ROOT_DIR}/CHANGELOG.md" | sed -e 's/^## \[//' -e 's/\].*$//')"
if [[ -z "${VERSION}" ]]; then
    echo "Error: no dated release section found in CHANGELOG.md" >&2
    exit 1
fi

mkdir -p "${BADGE_DIR}"

fetch() {
    local name="$1" url="$2"
    curl -fsS --retry 3 -o "${BADGE_DIR}/${name}.svg" "${url}"
    echo "  ${name}.svg"
}

echo "Refreshing badges for ${VERSION}:"
fetch nuget       "https://img.shields.io/badge/NuGet-v${VERSION}-007ec6?logo=nuget"
fetch license-mit "https://img.shields.io/badge/license-MIT-blue.svg"
fetch dotnet-10   "https://img.shields.io/badge/.NET-10-512BD4.svg?logo=dotnet"

echo
echo "Done. These files are committed, not fetched at page load — that is the point."
