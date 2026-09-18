#!/usr/bin/env bash
# Fails when a package's release notes are longer than nuget.org accepts (35000 characters), so the
# refusal shows up at pack time rather than as a failed push after the release approval.
#
# Usage: ./scripts/check-release-notes.sh <directory with .nupkg files>
set -euo pipefail

dir="${1:?usage: check-release-notes.sh <nupkg directory>}"
limit=35000
failed=0
for pkg in "${dir}"/*.nupkg; do
    length=$(unzip -p "${pkg}" '*.nuspec' | python3 -c '
import sys, xml.etree.ElementTree as ET
root = ET.fromstring(sys.stdin.buffer.read())
notes = next((e.text or "" for e in root.iter() if e.tag.endswith("releaseNotes")), "")
print(len(notes))')
    if (( length > limit )); then
        echo "$(basename "${pkg}"): release notes are ${length} characters; nuget.org accepts ${limit}." >&2
        failed=1
    fi
done

if (( failed )); then
    exit 1
fi
echo "Every package's release notes fit nuget.org's limit."
