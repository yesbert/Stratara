#!/usr/bin/env bash
#
# Render build/social-card/social-card.html to docs/assets/social-card.png at exactly 1280x640.
#
# One image serves two places: og:image on the documentation site, so a stratara.tech link shared
# into Slack, LinkedIn or Bluesky renders as a card, and the repository's social preview, which
# GitHub shows for the same reason. They were two images until 2026-09-06, which is how one of them
# came to advertise docs.stratara.tech hours after that host was retired.
#
# The PNG is committed rather than rendered during the deploy, for the reason the badges under
# docs/assets/badges/ are: DocFX copies resources, it does not run a headless browser, and adding
# one to the deploy workflow to produce a file that changes once a year is a bad trade. The cost is
# that the PNG can fall behind its source, which is what this script fixes.
#
# Renders at 2x for crisp text, then downscales — macOS only, because of sips. Needs Google Chrome
# and puppeteer-core; both are checked below.
#
# After running it, the repository's social preview has to be re-uploaded by hand: GitHub's REST API
# exposes no endpoint for it. Settings -> General -> Social preview.
#
# Usage: ./scripts/refresh-social-card.sh

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="${ROOT_DIR}/build/social-card"
OUT="${ROOT_DIR}/docs/assets/social-card.png"

CHROME_BIN="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
if [[ ! -x "${CHROME_BIN}" ]]; then
    echo "Error: Google Chrome not found at ${CHROME_BIN}." >&2
    echo "       Set CHROME=<path-to-chrome> and re-run." >&2
    exit 1
fi

# puppeteer-core comes from the npx cache. Populate it if this machine has never rendered anything.
find_pup() { find "${HOME}/.npm/_npx" -maxdepth 5 -type d -name puppeteer-core 2>/dev/null | head -1; }
PUP_DIR="$(find_pup)"
if [[ -z "${PUP_DIR}" ]]; then
    echo "puppeteer-core not in the npx cache; fetching it once..."
    npx -y -p puppeteer-core node -e "0" >/dev/null 2>&1 || true
    PUP_DIR="$(find_pup)"
fi
if [[ -z "${PUP_DIR}" ]]; then
    echo "Error: puppeteer-core could not be fetched. Check network access to the npm registry." >&2
    exit 1
fi
export NODE_PATH="$(dirname "${PUP_DIR}")"

# Chrome resolves the card's relative <img src> against the HTML file, so render from its directory.
cd "${SRC_DIR}"
CHROME="${CHROME_BIN}" node render.js social-card.html social-card@2x.png
sips --resampleHeightWidth 640 1280 social-card@2x.png --out "${OUT}" >/dev/null
rm -f social-card@2x.png

bytes=$(stat -f%z "${OUT}")
echo "Wrote docs/assets/social-card.png ($((bytes / 1024)) KB)"
sips -g pixelWidth -g pixelHeight "${OUT}" 2>/dev/null | tail -2
