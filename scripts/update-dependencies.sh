#!/usr/bin/env bash
#
# Update every NuGet package pinned in Directory.Packages.props to the latest
# version available on nuget.org. Optional verification of ecosystem groups
# (OpenTelemetry, Serilog, Microsoft.Extensions.*) for inter-package version
# consistency. Build + test verification are deferred to scripts/local-gauntlet.sh
# (call it after this script when you're satisfied with the diff).
#
# Packages listed in scripts/dependency-pins.txt are held at their pinned version
# and reported rather than moved. Before this existed the script walked straight
# past a comment in the props file explaining why a version was deliberately held,
# which is exactly the kind of reason a comment cannot enforce.
#
# Usage:
#   ./scripts/update-dependencies.sh              # update everything
#   ./scripts/update-dependencies.sh --dry-run    # show what would change
#   ./scripts/update-dependencies.sh --skip-verify  # skip ecosystem-group checks
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROPS_FILE="${ROOT_DIR}/Directory.Packages.props"
PINS_FILE="${ROOT_DIR}/scripts/dependency-pins.txt"

# Colors
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly BLUE='\033[0;34m'
readonly CYAN='\033[0;36m'
readonly NC='\033[0m'

WARNINGS=()
ERRORS=()

header()  { echo ""; echo -e "${BLUE}════════════════════════════════════════════════════════════════${NC}"; echo -e "${BLUE}  $1${NC}"; echo -e "${BLUE}════════════════════════════════════════════════════════════════${NC}"; echo ""; }
section() { echo ""; echo -e "${CYAN}━━━ $1 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"; echo ""; }
success() { echo -e "${GREEN}✓ $1${NC}"; }
warn()    { echo -e "${YELLOW}⚠ $1${NC}"; WARNINGS+=("$1"); }
fail()    { echo -e "${RED}✗ $1${NC}"; ERRORS+=("$1"); }
info()    { echo -e "${BLUE}→ $1${NC}"; }

# ─────────────────────────────────────────────────────────────────
# Args
# ─────────────────────────────────────────────────────────────────
DRY_RUN=false
SKIP_VERIFY=false

usage() {
    cat <<EOF
Usage: $0 [options]

Updates every NuGet package in Directory.Packages.props to the latest
nuget.org version. Stratara packages themselves are produced by this
repo (not updated externally) and are skipped. Packages listed in
scripts/dependency-pins.txt are held at their pinned version.

Options:
  --dry-run        Show what would change without writing anything
  --skip-verify    Skip ecosystem-group consistency checks
  -h, --help       Show this help
EOF
    exit 0
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run)     DRY_RUN=true; shift ;;
        --skip-verify) SKIP_VERIFY=true; shift ;;
        -h|--help)     usage ;;
        *)             echo "Unknown option: $1" >&2; usage ;;
    esac
done

# ─────────────────────────────────────────────────────────────────
# Prerequisites
# ─────────────────────────────────────────────────────────────────
for tool in curl jq sed sort; do
    command -v "${tool}" >/dev/null 2>&1 || { echo "Missing required tool: ${tool}" >&2; exit 1; }
done

[[ -f "${PROPS_FILE}" ]] || { echo "Not found: ${PROPS_FILE}" >&2; exit 1; }

# Cross-platform sed -i
sed_inplace() {
    if sed --version >/dev/null 2>&1; then
        sed -i "$@"
    else
        sed -i '' "$@"
    fi
}

# ─────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────

# Extract "PackageName|Version" pairs from a Directory.Packages.props file.
extract_package_info() {
    local file="$1"
    sed -n 's/.*Include="\([^"]*\)".*Version="\([^"]*\)".*/\1|\2/p' "$file"
}

# Read the pinned version of a single package from the props file.
get_version_for_package() {
    local file="$1"
    local package="$2"
    sed -n "s/.*Include=\"${package}\".*Version=\"\([^\"]*\)\".*/\1/p" "${file}" | head -1
}

# Read the pinned version for a package from scripts/dependency-pins.txt, or
# print nothing when the package is not pinned.
pinned_version_for() {
    local package="$1"
    [[ -f "${PINS_FILE}" ]] || return 0
    awk -v pkg="${package}" '
        /^[[:space:]]*#/ { next }
        /^[[:space:]]*$/ { next }
        $1 == pkg { print $2; exit }
    ' "${PINS_FILE}"
}

# Read the reason recorded for a pinned package.
pin_reason_for() {
    local package="$1"
    [[ -f "${PINS_FILE}" ]] || return 0
    awk -v pkg="${package}" '
        /^[[:space:]]*#/ { next }
        /^[[:space:]]*$/ { next }
        $1 == pkg { sub(/^[^#]*#[[:space:]]*/, ""); print; exit }
    ' "${PINS_FILE}"
}

# Every pinned package, one id per line.
pinned_packages() {
    [[ -f "${PINS_FILE}" ]] || return 0
    awk '/^[[:space:]]*#/ { next } /^[[:space:]]*$/ { next } { print $1 }' "${PINS_FILE}"
}

# Write a new version into the props file for an existing package entry.
#
# The pin guard lives HERE rather than at the call sites, because there are two
# of them — the bulk update and the ecosystem-alignment step — and a guard that
# only covers the one you remembered is how a pin gets overwritten anyway.
set_package_version() {
    local props_file="$1"
    local package="$2"
    local old_version="$3"
    local new_version="$4"

    local pin
    pin=$(pinned_version_for "${package}")
    if [[ -n "${pin}" ]]; then
        fail "Refused to move ${package} ${old_version} → ${new_version}: pinned at ${pin} in scripts/dependency-pins.txt"
        return 1
    fi

    local escaped_pkg
    escaped_pkg=$(printf '%s' "${package}" | sed 's/\./\\./g')
    sed_inplace "s/Include=\"${escaped_pkg}\" Version=\"${old_version}\"/Include=\"${package}\" Version=\"${new_version}\"/g" "${props_file}"
}

# Validate scripts/dependency-pins.txt against the props file before touching
# anything: a pin that has drifted from the file it constrains is worse than no
# pin, because it reads as authoritative while describing a version nobody uses.
validate_pins() {
    [[ -f "${PINS_FILE}" ]] || { info "No pin file at ${PINS_FILE} — nothing to validate"; return 0; }

    local count=0
    while read -r package; do
        [[ -z "${package}" ]] && continue
        count=$((count + 1))
        local pin actual
        pin=$(pinned_version_for "${package}")
        actual=$(get_version_for_package "${PROPS_FILE}" "${package}")

        if [[ -z "${actual}" ]]; then
            fail "${package} is pinned at ${pin} but does not appear in Directory.Packages.props"
        elif [[ "${actual}" != "${pin}" ]]; then
            fail "${package} is pinned at ${pin} but Directory.Packages.props says ${actual} — the pin and the props file have drifted"
        else
            printf '    %s: held at %s\n' "${package}" "${pin}"
        fi
    done < <(pinned_packages)

    # Not `(( count == 0 )) && info ...`: under `set -e` an arithmetic test that
    # evaluates false returns non-zero, and as the last command of a function that
    # aborts the whole script.
    if (( count == 0 )); then
        info "Pin file is empty — every package tracks the latest release"
    fi
}

# Query the NuGet V3 flat-container API for a package's latest version.
# For stable packages the latest non-prerelease is returned; for already-
# prerelease packages the absolute latest version is returned so we don't
# regress from a preview to a stale stable.
get_latest_nuget_version() {
    local package="$1"
    local current_version="$2"

    local package_lower
    package_lower=$(echo "${package}" | tr '[:upper:]' '[:lower:]')

    local response
    response=$(curl -sS --connect-timeout 5 --max-time 10 \
        "https://api.nuget.org/v3-flatcontainer/${package_lower}/index.json" 2>/dev/null) || return 1
    [[ -n "${response}" ]] || return 1

    local is_prerelease=false
    case "${current_version}" in *-*) is_prerelease=true ;; esac

    if [[ "${is_prerelease}" == "true" ]]; then
        echo "${response}" | jq -r '.versions[-1] // empty' 2>/dev/null
    else
        echo "${response}" | jq -r '[.versions[] | select(test("-") | not)][-1] // empty' 2>/dev/null
    fi
}

# Update every package in the props file. Stratara.* packages are skipped
# (they're produced by this repo, not consumed externally).
update_packages_props() {
    local props_file="$1"

    local tmp_dir
    tmp_dir=$(mktemp -d)

    local packages=()
    while IFS='|' read -r package version; do
        [[ -z "${package}" || -z "${version}" ]] && continue
        # Skip Stratara packages — those are this repo's own output, the version
        # comes from <VersionPrefix> in Directory.Build.props, and consumers pin
        # them via $(StrataraVersion) — there's no nuget.org-sourced upgrade to
        # apply.
        case "${package}" in Stratara.*) continue ;; esac
        # Skip MSBuild variable references like $(SomeVar).
        case "${version}" in \$\(*) continue ;; esac
        packages+=("${package}|${version}")
    done < <(extract_package_info "${props_file}")

    local total=${#packages[@]}
    info "Querying nuget.org for ${total} packages..."

    local max_parallel=10
    local queried=0

    for ((i = 0; i < total; i += max_parallel)); do
        local batch_end=$((i + max_parallel))
        (( batch_end > total )) && batch_end=${total}

        for ((j = i; j < batch_end; j++)); do
            local entry="${packages[$j]}"
            local package="${entry%%|*}"
            local version="${entry#*|}"

            (
                local latest
                latest=$(get_latest_nuget_version "${package}" "${version}" 2>/dev/null) || latest=""
                if [[ -n "${latest}" ]]; then
                    echo "${package}|${version}|${latest}" > "${tmp_dir}/${j}"
                else
                    echo "fail|${package}" > "${tmp_dir}/${j}"
                fi
            ) &
        done
        wait

        queried=$((queried + (batch_end - i)))
        printf "\r  Progress: %d/%d packages checked" "${queried}" "${total}"
    done
    echo ""

    local update_count=0 fail_count=0 up_to_date_count=0 pinned_count=0

    for ((k = 0; k < total; k++)); do
        local result_file="${tmp_dir}/${k}"
        [[ -f "${result_file}" ]] || continue
        local line
        line=$(cat "${result_file}")

        case "${line}" in
            "fail|"*)
                fail_count=$((fail_count + 1))
                continue
                ;;
        esac

        local package current latest
        package="${line%%|*}"
        local rest="${line#*|}"
        current="${rest%%|*}"
        latest="${rest#*|}"

        local pin
        pin=$(pinned_version_for "${package}")
        if [[ -n "${pin}" ]]; then
            if [[ -n "${latest}" && "${latest}" == "${pin}" ]]; then
                warn "${package}: pin at ${pin} holds nothing back — nuget.org has nothing newer. Delete it from scripts/dependency-pins.txt"
            else
                echo -e "  ${YELLOW}⊝${NC} ${package}: held at ${pin} (latest ${latest:-unknown}) — $(pin_reason_for "${package}")"
            fi
            pinned_count=$((pinned_count + 1))
            continue
        fi

        if [[ "${current}" == "${latest}" || -z "${latest}" ]]; then
            up_to_date_count=$((up_to_date_count + 1))
            continue
        fi

        if [[ "${DRY_RUN}" == "true" ]]; then
            echo -e "  [dry-run] ${package}: ${current} → ${latest}"
        else
            set_package_version "${props_file}" "${package}" "${current}" "${latest}"
            echo -e "  ${GREEN}↑${NC} ${package}: ${current} → ${latest}"
        fi
        update_count=$((update_count + 1))
    done

    rm -rf "${tmp_dir}"

    if (( update_count == 0 )); then
        success "All ${up_to_date_count} packages are up to date"
    else
        success "Updated ${update_count} packages (${up_to_date_count} already current)"
    fi
    if (( pinned_count > 0 )); then
        info "${pinned_count} packages held by scripts/dependency-pins.txt"
    fi
    if (( fail_count > 0 )); then
        warn "${fail_count} packages could not be queried from nuget.org"
    fi
}

# Compare versions; returns 0 iff $1 >= $2 by semver sort.
version_gte() {
    local highest
    highest=$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)
    [[ "${highest}" == "$1" ]]
}

# Ecosystem-group verification: every package in the group should share a
# version. "strict" auto-aligns to the highest available; "linked" only reports.
align_strict_group() {
    local label="$1"
    shift
    local versions=("$@")
    local max_version
    max_version=$(printf '%s\n' "${versions[@]}" | sed 's/.*=//' | sort -V | tail -1)
    info "${label}: aligning to ${max_version}"
    local fully_aligned=true
    for entry in "${versions[@]}"; do
        local pkg_name="${entry%%=*}"
        local pkg_ver="${entry#*=}"
        [[ "${pkg_ver}" == "${max_version}" ]] && { printf '    %s: %s (ok)\n' "${pkg_name}" "${pkg_ver}"; continue; }

        local latest
        latest=$(get_latest_nuget_version "${pkg_name}" "${pkg_ver}" 2>/dev/null) || latest=""
        if [[ -n "${latest}" ]] && version_gte "${latest}" "${max_version}"; then
            if [[ "${DRY_RUN}" == "true" ]]; then
                echo -e "    ${YELLOW}[dry-run]${NC} ${pkg_name}: ${pkg_ver} → ${max_version}"
            else
                set_package_version "${PROPS_FILE}" "${pkg_name}" "${pkg_ver}" "${max_version}"
                echo -e "    ${GREEN}↑${NC} ${pkg_name}: ${pkg_ver} → ${max_version}"
            fi
            continue
        fi
        printf '    %s: %s (latest available: %s)\n' "${pkg_name}" "${pkg_ver}" "${latest:-unknown}"
        fully_aligned=false
    done
    if [[ "${fully_aligned}" == "true" ]]; then
        success "${label}: all aligned to ${max_version}"
    else
        warn "${label}: partial alignment only — some packages don't have ${max_version} yet"
    fi
}

verify_group() {
    local label="$1"
    local mode="$2"
    shift 2
    local packages=("$@")

    local versions=()
    for pkg in "${packages[@]}"; do
        local ver
        ver=$(get_version_for_package "${PROPS_FILE}" "${pkg}") || true
        [[ -n "${ver}" ]] || continue
        # A pinned package is not a candidate for alignment — the group should be
        # judged on the packages that are actually free to move.
        local pin
        pin=$(pinned_version_for "${pkg}")
        if [[ -n "${pin}" ]]; then
            printf '    %s: %s (pinned — excluded from alignment)\n' "${pkg}" "${pin}"
            continue
        fi
        versions+=("${pkg}=${ver}")
    done

    (( ${#versions[@]} == 0 )) && return

    local unique_count
    unique_count=$(printf '%s\n' "${versions[@]}" | sed 's/.*=//' | sort -u | wc -l | tr -d ' ')
    if (( unique_count <= 1 )); then
        success "${label}: consistent versions"
        return
    fi

    if [[ "${mode}" == "strict" ]]; then
        align_strict_group "${label}" "${versions[@]}"
        return
    fi

    info "${label}: different version tracks (linked, independent release cycles):"
    for v in "${versions[@]}"; do
        printf '    %s\n' "${v}"
    done
}

# ─────────────────────────────────────────────────────────────────
# Run
# ─────────────────────────────────────────────────────────────────
header "Stratara dependency update"
echo "Props file: ${PROPS_FILE}"
echo "Dry run:    ${DRY_RUN}"
echo ""

section "0. Dependency pins"
validate_pins

section "1. NuGet packages"
update_packages_props "${PROPS_FILE}"

if [[ "${SKIP_VERIFY}" == "false" ]]; then
    section "2. Ecosystem-group verification"

    info "Microsoft.Extensions.* ecosystem"
    MEX_PACKAGES=(
        "Microsoft.Extensions.DependencyInjection"
        "Microsoft.Extensions.DependencyInjection.Abstractions"
        "Microsoft.Extensions.Hosting"
        "Microsoft.Extensions.Hosting.Abstractions"
        "Microsoft.Extensions.Logging.Abstractions"
        "Microsoft.Extensions.Options"
        "Microsoft.Extensions.Options.ConfigurationExtensions"
        "Microsoft.Extensions.Configuration.Abstractions"
        "Microsoft.Extensions.Http"
        "Microsoft.Extensions.Resilience"
        "Microsoft.Extensions.Http.Resilience"
    )
    verify_group "Microsoft.Extensions.*" "strict" "${MEX_PACKAGES[@]}"

    info "Microsoft.EntityFrameworkCore ecosystem"
    EF_PACKAGES=(
        "Microsoft.EntityFrameworkCore"
        "Microsoft.EntityFrameworkCore.Design"
        "Microsoft.EntityFrameworkCore.Relational"
        "Microsoft.EntityFrameworkCore.InMemory"
        "Microsoft.EntityFrameworkCore.Sqlite"
        "Microsoft.EntityFrameworkCore.Tools"
        "Microsoft.AspNetCore.Identity.EntityFrameworkCore"
    )
    verify_group "Microsoft.EntityFrameworkCore" "strict" "${EF_PACKAGES[@]}"

    info "OpenTelemetry ecosystem"
    OTEL_PACKAGES=(
        "OpenTelemetry.Api"
        "OpenTelemetry.Extensions.Hosting"
        "OpenTelemetry.Instrumentation.AspNetCore"
        "OpenTelemetry.Instrumentation.Http"
        "OpenTelemetry.Instrumentation.Runtime"
        "OpenTelemetry.Exporter.OpenTelemetryProtocol"
    )
    verify_group "OpenTelemetry" "strict" "${OTEL_PACKAGES[@]}"

    info "Serilog ecosystem"
    SERILOG_PACKAGES=(
        "Serilog.AspNetCore"
        "Serilog.Extensions.Hosting"
        "Serilog.Extensions.Logging"
        "Serilog.Settings.Configuration"
    )
    verify_group "Serilog" "strict" "${SERILOG_PACKAGES[@]}"

    info "Npgsql ecosystem (linked — independent release cycles)"
    NPGSQL_PACKAGES=(
        "Npgsql"
        "Npgsql.EntityFrameworkCore.PostgreSQL"
    )
    verify_group "Npgsql" "linked" "${NPGSQL_PACKAGES[@]}"
fi

# ─────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────
header "Update summary"

if (( ${#WARNINGS[@]} > 0 )); then
    echo -e "${YELLOW}Warnings (${#WARNINGS[@]}):${NC}"
    for w in "${WARNINGS[@]}"; do echo -e "  ${YELLOW}⚠ ${w}${NC}"; done
    echo ""
fi

if (( ${#ERRORS[@]} > 0 )); then
    echo -e "${RED}Errors (${#ERRORS[@]}):${NC}"
    for e in "${ERRORS[@]}"; do echo -e "  ${RED}✗ ${e}${NC}"; done
    echo ""
    exit 1
fi

echo -e "${GREEN}Done.${NC}"
echo ""
echo "Next steps:"
echo "  1. Inspect changes:           git diff Directory.Packages.props"
echo "  2. Run the gauntlet:          ./scripts/local-gauntlet.sh"
echo "  3. Commit if everything is green:"
echo "       git add Directory.Packages.props"
echo "       git commit -m \"chore: update NuGet dependencies\""
