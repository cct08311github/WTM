#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./scripts/release-gitea-package.sh [--dry-run] [--confirm-gate] <version> [suffix]

Examples:
  ./scripts/release-gitea-package.sh 10.5.1
  ./scripts/release-gitea-package.sh 10.5.1 beta.1
  ./scripts/release-gitea-package.sh --dry-run 10.5.1
  ./scripts/release-gitea-package.sh --confirm-gate 10.5.1   # version.props already 10.5.1

Behavior:
  1. Updates VersionPrefix in version.props
  2. Commits the version change
  3. Pushes to origin/dotnet10
  4. Triggers publish-nuget.yml on Gitea Actions

Notes:
  - If suffix is omitted, a stable release is published.
  - If suffix is provided, a pre-release is published as <version>-<suffix>.
  - --dry-run prints the actions without changing files or triggering workflows.
  - --confirm-gate is REQUIRED when <version> already equals version.props's current
    VersionPrefix (#925). Bumping the version is itself the natural moment to have just
    run .claude/commands/wtm-release-check.md's gate (build, full test suite,
    mutation-gate evidence, LOCAL vulnerability scan); re-triggering a publish for an
    UNCHANGED version has no such ceremony forcing that gate to have just run, so this
    script refuses by default rather than silently dispatching a publish with no local
    signal that verification happened. This script cannot run that gate itself -- the
    full test suite and mutation-gate evidence are too slow to shell out to from here
    (see publish-nuget.yml's own pre-publish gate, #925, for what CAN and does run
    mechanically on every dispatch regardless of this flag).
  - Requires GITEA_TOKEN env var, or a ~/.gitea-token file containing a single
    40-char lowercase-hex token (see scripts/resolve-gitea-token.py).
EOF
}

DRY_RUN=0
CONFIRM_GATE=0
while [[ "${1:-}" == --* ]]; do
  case "$1" in
    --dry-run)
      DRY_RUN=1
      shift
      ;;
    --confirm-gate)
      CONFIRM_GATE=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 1
      ;;
  esac
done

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
GITEA_API="https://mac-mini.tailde842d.ts.net/api/v1"
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

# Resolve Gitea token via scripts/resolve-gitea-token.py: env var first, then
# ~/.gitea-token, parsed (never `source`d -- see docs/ci-operations.md for
# why) and validated as exactly one 40-character lowercase-hex token. Uses an
# `if`/`else` around the assignment, not `cmd || true`, so a real read/parse
# error (exit 2) is distinguished from "not configured yet" (exit 1) instead
# of being swallowed as the same "not set" outcome.
if GITEA_TOKEN="$(python3 "$(dirname "$0")/resolve-gitea-token.py")"; then
  :
else
  RESOLVE_STATUS=$?
  if [[ "$RESOLVE_STATUS" -eq 1 ]]; then
    echo "GITEA_TOKEN is not set. Set it as env var or put a 40-character lowercase-hex token in ~/.gitea-token" >&2
  fi
  # exit 2 (malformed env var or file): resolve-gitea-token.py already printed
  # the specific reason to stderr.
  exit 1
fi

# Verify Gitea reachability
if ! curl -sf -H "Authorization: token ${GITEA_TOKEN}" \
    "${GITEA_API}/repos/${GITEA_OWNER}/${GITEA_REPO}" >/dev/null 2>&1; then
  echo "Cannot reach Gitea API at ${GITEA_API}. Check token and network." >&2
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
    -H "Authorization: token ${GITEA_TOKEN}" \
    -H "Content-Type: application/json" \
    -d "$payload" \
    "${GITEA_API}/repos/${GITEA_OWNER}/${GITEA_REPO}/actions/workflows/publish-nuget.yml/dispatches"
}

if [[ "$CURRENT_VERSION" == "$VERSION" ]]; then
  echo "VersionPrefix is already $VERSION"
  if [[ "$DRY_RUN" -eq 1 ]]; then
    if [[ -n "$SUFFIX" ]]; then
      echo "[dry-run] Would trigger Gitea Packages publish for $VERSION-$SUFFIX"
    else
      echo "[dry-run] Would trigger Gitea Packages publish for $VERSION"
    fi
    if [[ "$CONFIRM_GATE" -ne 1 ]]; then
      echo "[dry-run] NOTE: a real (non-dry-run) run would refuse here without --confirm-gate -- see --help."
    fi
    exit 0
  fi
  # #925: an unchanged version means no version-bump commit is about to happen, so
  # there is no ceremony here that would have just forced a run of
  # .claude/commands/wtm-release-check.md's gate. Refuse rather than silently
  # dispatching -- this cannot run the full gate itself (build + full test suite +
  # mutation-gate evidence are too slow to shell out to from a wrapper script; see
  # publish-nuget.yml's own pre-publish gate for what runs mechanically regardless).
  if [[ "$CONFIRM_GATE" -ne 1 ]]; then
    echo "ERROR: VersionPrefix is unchanged ($VERSION) -- refusing to dispatch a publish without --confirm-gate." >&2
    echo "  Re-triggering a publish for an unchanged version skips the version-bump ceremony that" >&2
    echo "  normally follows running .claude/commands/wtm-release-check.md's gate. Confirm you have" >&2
    echo "  run that gate against the current HEAD, then re-run with --confirm-gate. Use --dry-run" >&2
    echo "  to preview without triggering anything." >&2
    exit 1
  fi
  trigger_gitea_workflow "$BRANCH" "$SUFFIX"
  if [[ -n "$SUFFIX" ]]; then
    echo "Triggered Gitea Packages publish for $VERSION-$SUFFIX"
  else
    echo "Triggered Gitea Packages publish for $VERSION"
  fi
  exit 0
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "[dry-run] Would update VersionPrefix from $CURRENT_VERSION to $VERSION"
  echo "[dry-run] Would commit: chore: release $VERSION"
  echo "[dry-run] Would push to origin/$BRANCH"
  if [[ -n "$SUFFIX" ]]; then
    echo "[dry-run] Would trigger Gitea Packages publish for $VERSION-$SUFFIX"
  else
    echo "[dry-run] Would trigger Gitea Packages publish for $VERSION"
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
  echo "Triggered Gitea Packages publish for $VERSION-$SUFFIX"
else
  echo "Triggered Gitea Packages publish for $VERSION"
fi
