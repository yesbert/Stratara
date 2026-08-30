#!/usr/bin/env bash
#
# Local validation gauntlet — what every PR should pass before push.
#
# Build covers Stratara.slnx (full repo: src + samples + tests, including
# Stratara.Benchmarks). Tests + pack-check still operate on Stratara.Publish.slnf
# — the same solution filter the CI and release workflows use. Building more than
# CI does is intentional — catches regressions in samples / benchmark projects
# before they reach a follow-up PR.
#
# Usage:
#   ./scripts/local-gauntlet.sh
#   ./scripts/local-gauntlet.sh --skip-pack          # skip the pack-check step
#   ./scripts/local-gauntlet.sh --simulate-tag-mode  # pack with empty VersionSuffix
#                                                    # (catches stable-release issues
#                                                    # like NU5104 that only fire when
#                                                    # the package version is stable)
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
SLNX="${ROOT_DIR}/Stratara.slnx"
SLNF="${ROOT_DIR}/Stratara.Publish.slnf"

SKIP_PACK=0
SIMULATE_TAG_MODE=0
for arg in "$@"; do
    case "${arg}" in
        --skip-pack) SKIP_PACK=1 ;;
        --simulate-tag-mode) SIMULATE_TAG_MODE=1 ;;
        *) echo "Unknown arg: ${arg}" >&2; exit 1 ;;
    esac
done

cd "${ROOT_DIR}"

step() {
    echo ""
    echo "=== $1 ==="
}

step "Build (Release, full solution)"
dotnet build "${SLNX}" -c Release --nologo

step "Tests"
# MTP-based test projects: invoke the dll directly (matches CI). dotnet test on
# the csproj is supported by MTP but with Stratara's config produces "Zero tests
# ran" — switching to direct dll execution avoids that.
failed=0
for test_proj in tests/*/*.csproj; do
    name="$(basename "$(dirname "${test_proj}")")"
    case "${name}" in
        Stratara.Benchmarks) continue ;;  # benchmark project, not a test runner
        *IntegrationTests) continue ;;    # require Docker; run via the integration pipeline
    esac
    dll="${ROOT_DIR}/tests/${name}/bin/Release/net10.0/${name}.dll"
    if [[ ! -f "${dll}" ]]; then
        echo "--- ${name} ---  (no dll at ${dll}, skipping)"
        continue
    fi
    echo ""
    echo "--- ${name} ---"
    if ! dotnet "${dll}"; then
        failed=1
    fi
done
if [[ ${failed} -ne 0 ]]; then
    echo "Tests failed." >&2
    exit 1
fi

# The documentation checks — snippet compilation, framework type shape, configuration section
# names, messaging topic defaults, log-event allocation, option defaults, the registration
# inventory, the shipped <example> blocks and the generated llms-full.txt — all live in
# Stratara.Documentation.Tests and run through the loop above. The loop skips a project whose dll
# is missing, which for a verification project would retire the gate without saying so.
doc_tests_dll="${ROOT_DIR}/tests/Stratara.Documentation.Tests/bin/Release/net10.0/Stratara.Documentation.Tests.dll"
if [[ ! -f "${doc_tests_dll}" ]]; then
    echo "Documentation checks did not run — ${doc_tests_dll} is missing." >&2
    exit 1
fi

if command -v docfx >/dev/null 2>&1; then
    step "DocFX (warningsAsErrors)"
    docfx metadata "${ROOT_DIR}/docs/docfx.json"
    docfx build "${ROOT_DIR}/docs/docfx.json" --warningsAsErrors
else
    echo ""
    echo "=== DocFX (skipped — install with: dotnet tool install -g docfx) ==="
fi

step "Doc-symbol check (fabricated-API scan)"
if command -v python3 >/dev/null 2>&1; then
    python3 "${ROOT_DIR}/scripts/check-doc-symbols.py" \
        "${ROOT_DIR}/src" "${ROOT_DIR}/docs" "${ROOT_DIR}/README.md"
else
    echo "=== Doc-symbol check (skipped — python3 not found) ==="
fi

if [[ ${SKIP_PACK} -eq 0 ]]; then
    pack_args=("${SLNF}" -c Release --no-build --nologo)
    if [[ ${SIMULATE_TAG_MODE} -eq 1 ]]; then
        step "Pack-check (tag-mode simulation, VersionSuffix='')"
        # Empty VersionSuffix overrides the `dev` default from Directory.Build.props,
        # producing stable-versioned nupkgs (e.g. 1.0.0 instead of 1.0.0-dev). NU5104
        # and other stable-only validations fire here — exactly what the tag-driven
        # publish pipeline (#32) runs in CI.
        pack_args+=(-p:VersionSuffix=)
    else
        step "Pack-check (sanity)"
    fi
    PACK_DIR="$(mktemp -d)"
    pack_args+=(-o "${PACK_DIR}")
    dotnet pack "${pack_args[@]}"
    pkg_count=$(ls "${PACK_DIR}"/*.nupkg 2>/dev/null | wc -l | tr -d ' ')
    echo "Produced ${pkg_count} .nupkg files in ${PACK_DIR}"

    step "Test-support guard check (pack-and-consume)"
    "${SCRIPT_DIR}/check-test-support-guard.sh" "${PACK_DIR}"

    rm -rf "${PACK_DIR}"
fi

echo ""
echo "✅ Local gauntlet green."
