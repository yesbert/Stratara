#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ALLOWLIST_FILE="$SCRIPT_DIR/nuget-audit-allowlist.txt"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m'

usage() {
    echo "Usage: $0"
    echo ""
    echo "Runs the .NET (NuGet) vulnerability scan for the Stratara framework."
    echo "Advisories listed in nuget-audit-allowlist.txt are accepted (not fatal)."
    exit 1
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
fi

# Matches a GitHub Security Advisory id, e.g. GHSA-2m69-gcr7-jv3q.
GHSA_RE='GHSA(-[0-9a-zA-Z]+){3}'

TOTAL_ISSUES=0

echo ""
echo -e "${BLUE}════════════════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}  Stratara Security Audit${NC}"
echo -e "${BLUE}════════════════════════════════════════════════════════════════${NC}"
echo ""

echo -e "${BLUE}━━━ .NET NuGet Packages ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

cd "$ROOT_DIR"
DOTNET_OUTPUT=$(dotnet list package --vulnerable --include-transitive 2>&1) || true

# Advisory ids reported by the live scan, and the ones we explicitly accept.
FOUND_ADVISORIES=$(echo "$DOTNET_OUTPUT" | grep -oE "$GHSA_RE" | sort -u || true)
ACCEPTED_ADVISORIES=$(grep -oE "$GHSA_RE" "$ALLOWLIST_FILE" 2>/dev/null | sort -u || true)

# Live findings that are NOT on the allowlist → real, must fail the build.
UNACCEPTED=$(comm -23 <(printf '%s\n' "$FOUND_ADVISORIES") <(printf '%s\n' "$ACCEPTED_ADVISORIES") | grep -E "$GHSA_RE" || true)
# Allowlist entries no longer reported by the scan → stale, should be removed.
STALE=$(comm -13 <(printf '%s\n' "$FOUND_ADVISORIES") <(printf '%s\n' "$ACCEPTED_ADVISORIES") | grep -E "$GHSA_RE" || true)

if [[ -n "$UNACCEPTED" ]]; then
    echo -e "${RED}VULNERABLE packages found (not on the audit allowlist):${NC}"
    echo "$DOTNET_OUTPUT" | grep -A 100 "has the following vulnerable packages"
    echo ""
    echo -e "${RED}Unaccepted advisories:${NC}"
    echo "$UNACCEPTED"
    TOTAL_ISSUES=$((TOTAL_ISSUES + 1))
elif [[ -n "$FOUND_ADVISORIES" ]]; then
    echo -e "${GREEN}No unaccepted vulnerabilities.${NC} The following are accepted via"
    echo -e "scripts/nuget-audit-allowlist.txt (no patched version available):"
    echo "$FOUND_ADVISORIES" | sed 's/^/  - /'
else
    echo -e "${GREEN}No known vulnerabilities found in NuGet packages.${NC}"
fi
echo ""

if [[ -n "$STALE" ]]; then
    # A stale entry is a failure, not a note. An allowlist entry outliving its advisory is a
    # suppression nobody is deciding to keep: it silently covers whatever fires under that id
    # next, and the longer it sits the less anyone remembers why it is there.
    echo -e "${RED}STALE allowlist entries — no longer reported by the scan, so a patch shipped.${NC}"
    echo -e "${RED}Delete these from scripts/nuget-audit-allowlist.txt:${NC}"
    echo "$STALE" | sed 's/^/  - /'
    echo ""
    TOTAL_ISSUES=$((TOTAL_ISSUES + 1))
fi

echo -e "${BLUE}━━━ Summary ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

if [[ $TOTAL_ISSUES -eq 0 ]]; then
    echo -e "${GREEN}All checks passed. No unaccepted vulnerabilities.${NC}"
    echo ""
    exit 0
else
    echo -e "${RED}Unaccepted vulnerabilities found — see output above.${NC}"
    echo ""
    exit 1
fi
