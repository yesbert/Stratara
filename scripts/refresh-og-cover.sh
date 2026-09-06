#!/usr/bin/env bash
#
# Render docs/assets/og-cover.svg to docs/assets/og-cover.png — the image og:image and
# twitter:image point at, so a link shared into Slack, Bluesky or LinkedIn renders as a card
# rather than a bare URL.
#
# The PNG is committed rather than built during the deploy, for the same reason the badges under
# docs/assets/badges/ are: DocFX copies resources, it does not run converters, and adding a
# rendering toolchain to the deploy workflow to produce a file that changes once a year is a bad
# trade. The cost is that the PNG can fall behind the SVG, which is what this script fixes.
#
# Open Graph needs a raster format — no major consumer renders SVG — which is why the SVG alone
# will not do.
#
# Usage: ./scripts/refresh-og-cover.sh

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ASSET_DIR="${ROOT_DIR}/docs/assets"

if ! command -v rsvg-convert >/dev/null 2>&1; then
    echo "Error: rsvg-convert not found. Install it with:" >&2
    echo "    brew install librsvg" >&2
    exit 1
fi

# rsvg resolves the SVG's relative <image href> against the SVG's own directory, so the working
# directory has to be that directory rather than the repository root.
cd "${ASSET_DIR}"
rsvg-convert --width 1200 --height 630 --output og-cover.png og-cover.svg

echo "Wrote docs/assets/og-cover.png"
sips -g pixelWidth -g pixelHeight og-cover.png 2>/dev/null | tail -2
