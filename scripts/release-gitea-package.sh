#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./scripts/release-gitea-package.sh [--dry-run] <version> [suffix]

Examples:
  ./scripts/release-gitea-package.sh 10.5.1
  ./scripts/release-gitea-package.sh 10.5.1 beta.1
  ./scripts/release-gitea-package.sh --dry-run 10.5.1

Behavior:
  1. Updates VersionPrefix in version.props
  2. Commits the version change
  3. Pushes to origin/dotnet10
  4. Triggers publish-nuget.yml on internal CI

Notes:
  - If suffix is omitted, a stable release is published.
  - If suffix is provided, a pre-release is published as <version>-<suffix>.
  - --dry-run prints the actions without changing files or triggering workflows.
  - Requires REGISTRY_TOKEN env var or export in <private-token-file>.
EOF
}

DRY_RUN=0
if [[ "${1:-}" == "--dry-run" ]]; then
  DRY_RUN=1
  shift
fi

if [[ $# -lt 1 || $# -gt 2 ]]; then
  usage
  exit 1
fi

if ! command -v git >/dev/null 2>&1; then
  echo "git is required" >&2
  exit 1
fi

VERSION="$1"
SUFFIX="${2:-}"
BRANCH="dotnet10"
VERSION_FILE="version.props"
GITEA_API="https://<internal-registry-host>/api/v1"
GITEA_OWNER="chiu0831"
GITEA_REPO="WTM"

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "Cannot find $VERSION_FILE" >&2
  exit 1
fi

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must look like 10.5.1" >&2
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

# Resolve internal infrastructure token: prefer env var, then <private-token-file> file
if [[ -z "${REGISTRY_TOKEN:-}" ]]; then
  if [[ -f "$HOME/.gitea-token" ]]; then
    # shellcheck source=/dev/null
    source "$HOME/.gitea-token"
  fi
fi
if [[ -z "${REGISTRY_TOKEN:-}" ]]; then
  echo "REGISTRY_TOKEN is not set. Set it as env var or put 'export REGISTRY_TOKEN=...' in <private-token-file>" >&2
  exit 1
fi

# Verify internal infrastructure reachability
if ! curl -sf -H "Authorization: token ${REGISTRY_TOKEN}" \
    "${GITEA_API}/repos/${GITEA_OWNER}/${GITEA_REPO}" >/dev/null 2>&1; then
  echo "Cannot reach internal infrastructure API at ${GITEA_API}. Check token and network." >&2
  exit 1
fi

CURRENT_VERSION="$(python3 - <<'PY' "$VERSION_FILE"
from pathlib import Path
import re
import sys

text = Path(sys.argv[1]).read_text()
match = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", text)
if not match:
    raise SystemExit("Cannot find VersionPrefix")
print(match.group(1))
PY
)"

trigger_gitea_workflow() {
  local ref="$1"
  local suffix="$2"
  local payload
  if [[ -n "$suffix" ]]; then
    payload="{\"ref\":\"${ref}\",\"inputs\":{\"version_suffix\":\"${suffix}\"}}"
  else
    payload="{\"ref\":\"${ref}\",\"inputs\":{}}"
  fi
  curl -sf -X POST \
    -H "Authorization: token ${REGISTRY_TOKEN}" \
    -H "Content-Type: application/json" \
    -d "$payload" \
    "${GITEA_API}/repos/${GITEA_OWNER}/${GITEA_REPO}/actions/workflows/publish-nuget.yml/dispatches"
}

if [[ "$CURRENT_VERSION" == "$VERSION" ]]; then
  echo "VersionPrefix is already $VERSION"
  if [[ "$DRY_RUN" -eq 1 ]]; then
    if [[ -n "$SUFFIX" ]]; then
      echo "[dry-run] Would trigger internal package registry publish for $VERSION-$SUFFIX"
    else
      echo "[dry-run] Would trigger internal package registry publish for $VERSION"
    fi
    exit 0
  fi
  trigger_gitea_workflow "$BRANCH" "$SUFFIX"
  if [[ -n "$SUFFIX" ]]; then
    echo "Triggered internal package registry publish for $VERSION-$SUFFIX"
  else
    echo "Triggered internal package registry publish for $VERSION"
  fi
  exit 0
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "[dry-run] Would update VersionPrefix from $CURRENT_VERSION to $VERSION"
  echo "[dry-run] Would commit: chore: release $VERSION"
  echo "[dry-run] Would push to origin/$BRANCH"
  if [[ -n "$SUFFIX" ]]; then
    echo "[dry-run] Would trigger internal package registry publish for $VERSION-$SUFFIX"
  else
    echo "[dry-run] Would trigger internal package registry publish for $VERSION"
  fi
  exit 0
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

trigger_gitea_workflow "$BRANCH" "$SUFFIX"
if [[ -n "$SUFFIX" ]]; then
  echo "Triggered internal package registry publish for $VERSION-$SUFFIX"
else
  echo "Triggered internal package registry publish for $VERSION"
fi
