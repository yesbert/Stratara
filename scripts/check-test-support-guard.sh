#!/usr/bin/env bash
#
# Verifies the build-time guard that keeps test-support packages out of projects
# that are not test projects.
#
# The guard ships as an MSBuild target inside the package, so it only fires
# through a PackageReference. Nothing inside this repository can reach it — the
# test projects here consume the test-support assemblies by ProjectReference,
# and package build targets do not flow across a project reference. Verifying it
# therefore means packing, consuming the package for real, and asserting the
# build fails.
#
# Usage: check-test-support-guard.sh <directory-containing-the-nupkgs>

set -euo pipefail

PACK_DIR="${1:?usage: check-test-support-guard.sh <pack-dir>}"
PACKAGE="Stratara.Testing"
EXPECTED_CODE="STRATARA1001"

nupkg="$(ls "${PACK_DIR}/${PACKAGE}".[0-9]*.nupkg 2>/dev/null | head -1 || true)"
if [[ -z "${nupkg}" ]]; then
    echo "No ${PACKAGE} package found in ${PACK_DIR}" >&2
    exit 1
fi

version="$(basename "${nupkg}")"
version="${version#"${PACKAGE}".}"
version="${version%.nupkg}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

cat > "${WORK_DIR}/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear/>
    <add key="local-pack" value="${PACK_DIR}"/>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>
  </packageSources>
</configuration>
EOF

cat > "${WORK_DIR}/NotATestProject.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="${PACKAGE}" Version="${version}"/>
  </ItemGroup>
</Project>
EOF

echo "class Placeholder;" > "${WORK_DIR}/Placeholder.cs"

echo "Consuming ${PACKAGE} ${version} from a project that is not a test project..."
build_log="${WORK_DIR}/build.log"
if dotnet build "${WORK_DIR}/NotATestProject.csproj" --nologo -v:q > "${build_log}" 2>&1; then
    echo "FAIL: the build succeeded. The guard did not fire." >&2
    tail -30 "${build_log}" >&2
    exit 1
fi

if ! grep -q "${EXPECTED_CODE}" "${build_log}"; then
    echo "FAIL: the build failed, but not with ${EXPECTED_CODE}. Something else broke." >&2
    tail -30 "${build_log}" >&2
    exit 1
fi
echo "  ✓ build fails with ${EXPECTED_CODE}"

echo "Re-building with the documented opt-out..."
optout_log="${WORK_DIR}/optout.log"
if ! dotnet build "${WORK_DIR}/NotATestProject.csproj" --nologo -v:q \
        -p:StrataraAllowTestSupportOutsideTests=true > "${optout_log}" 2>&1; then
    echo "FAIL: the opt-out did not suppress the guard." >&2
    tail -30 "${optout_log}" >&2
    exit 1
fi
echo "  ✓ opt-out suppresses it"

echo "Building as a test project..."
testlike_log="${WORK_DIR}/testlike.log"
if ! dotnet build "${WORK_DIR}/NotATestProject.csproj" --nologo -v:q \
        -p:IsTestProject=true > "${testlike_log}" 2>&1; then
    echo "FAIL: a test project was refused. The guard is too broad." >&2
    tail -30 "${testlike_log}" >&2
    exit 1
fi
echo "  ✓ a test project builds"
