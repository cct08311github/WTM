#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./scripts/release-github-package.sh <version> [suffix]

Examples:
  ./scripts/release-github-package.sh 8.2.2
  ./scripts/release-github-package.sh 8.2.2 beta.1

Behavior:
  1. Updates VersionPrefix in version.props
  2. Commits the version change
  3. Pushes to origin/dotnet8
  4. Triggers publish-nuget.yml

Notes:
  - If suffix is omitted, a stable release is published.
  - If suffix is provided, a pre-release is published as <version>-<suffix>.
EOF
}

if [[ $# -lt 1 || $# -gt 2 ]]; then
  usage
  exit 1
fi

if ! command -v git >/dev/null 2>&1; then
  echo "git is required" >&2
  exit 1
fi

if ! command -v gh >/dev/null 2>&1; then
  echo "gh is required" >&2
  exit 1
fi

VERSION="$1"
SUFFIX="${2:-}"
BRANCH="dotnet8"
VERSION_FILE="version.props"

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "Cannot find $VERSION_FILE" >&2
  exit 1
fi

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must look like 8.2.1" >&2
  exit 1
fi

CURRENT_BRANCH="$(git branch --show-current)"
if [[ "$CURRENT_BRANCH" != "$BRANCH" ]]; then
  echo "Current branch is '$CURRENT_BRANCH'. Switch to '$BRANCH' first." >&2
  exit 1
fi

if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "Working tree has tracked changes. Commit or stash them first." >&2
  exit 1
fi

python3 - <<'PY' "$VERSION_FILE" "$VERSION"
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
version = sys.argv[2]
text = path.read_text()
updated, count = re.subn(
    r"(<VersionPrefix>)([^<]+)(</VersionPrefix>)",
    rf"\g<1>{version}\g<3>",
    text,
    count=1,
)
if count != 1:
    raise SystemExit("Failed to update VersionPrefix")
path.write_text(updated)
PY

git add "$VERSION_FILE"
git commit -m "chore: release $VERSION"
git push origin "$BRANCH"

if [[ -n "$SUFFIX" ]]; then
  gh workflow run publish-nuget.yml --ref "$BRANCH" -f "version_suffix=$SUFFIX"
  echo "Triggered GitHub Packages publish for $VERSION-$SUFFIX"
else
  gh workflow run publish-nuget.yml --ref "$BRANCH"
  echo "Triggered GitHub Packages publish for $VERSION"
fi

