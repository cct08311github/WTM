#!/usr/bin/env bash
# Manual local publish to Gitea NuGet registry.
# Use this when Gitea Actions runner is unavailable.
#
# Usage:
#   ./scripts/publish-to-gitea.sh [--suffix <pre-release>] [--dry-run]
#
# Token source (first match wins):
#   1. GITEA_TOKEN env var
#   2. source ~/.gitea-token  (must contain: export GITEA_TOKEN='...')

set -euo pipefail

GITEA_NUGET_SOURCE="https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json"
VERSION_FILE="version.props"
OUTPUT_DIR="nupkgs"
SUFFIX=""
DRY_RUN=0

PROJECTS=(
  "src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj"
  "src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj"
  "src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj"
)

usage() {
  cat <<'EOF'
Usage:
  ./scripts/publish-to-gitea.sh [--suffix <pre-release>] [--dry-run]

Options:
  --suffix <pre-release>  Optional pre-release suffix (e.g. beta.1, rc1)
  --dry-run               Print commands without executing

Examples:
  ./scripts/publish-to-gitea.sh
  ./scripts/publish-to-gitea.sh --suffix beta.1
  ./scripts/publish-to-gitea.sh --dry-run
EOF
}

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --suffix)
      SUFFIX="${2:?'--suffix requires a value'}"
      shift 2
      ;;
    --dry-run)
      DRY_RUN=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

# Resolve token
if [[ -z "${GITEA_TOKEN:-}" ]]; then
  if [[ -f "$HOME/.gitea-token" ]]; then
    # shellcheck source=/dev/null
    source "$HOME/.gitea-token"
  fi
fi
if [[ -z "${GITEA_TOKEN:-}" ]]; then
  echo "ERROR: GITEA_TOKEN is not set." >&2
  echo "  Set it as an env var, or create ~/.gitea-token with: export GITEA_TOKEN='your-token'" >&2
  exit 1
fi

# Read version from version.props
if [[ ! -f "$VERSION_FILE" ]]; then
  echo "ERROR: $VERSION_FILE not found. Run from repo root." >&2
  exit 1
fi
BASE_VERSION="$(python3 - <<'PY' "$VERSION_FILE"
from pathlib import Path
import re, sys
text = Path(sys.argv[1]).read_text()
match = re.search(r'<VersionPrefix>([^<]+)</VersionPrefix>', text)
if not match:
    raise SystemExit('Cannot find VersionPrefix in ' + sys.argv[1])
print(match.group(1).strip())
PY
)"

if [[ -n "$SUFFIX" ]]; then
  PKG_VERSION="${BASE_VERSION}-${SUFFIX}"
else
  PKG_VERSION="${BASE_VERSION}"
fi

echo "==> Publish to Gitea NuGet"
echo "    Version : $PKG_VERSION"
echo "    Source  : $GITEA_NUGET_SOURCE"
echo "    Dry-run : $DRY_RUN"
echo ""

mask_token() {
  # Replace occurrences of GITEA_TOKEN in a string with first-6 + ***
  local prefix="${GITEA_TOKEN:0:6}"
  printf '%s' "${1//$GITEA_TOKEN/${prefix}***}"
}

run() {
  if [[ "$DRY_RUN" -eq 1 ]]; then
    echo "[dry-run] $(mask_token "$*")"
  else
    "$@"
  fi
}

# Pack
mkdir -p "$OUTPUT_DIR"
for proj in "${PROJECTS[@]}"; do
  echo "--> Packing $proj"
  run dotnet pack "$proj" -c Release -o "$OUTPUT_DIR" /p:Version="$PKG_VERSION"
done

echo ""

# Push
echo "--> Pushing to Gitea"
run dotnet nuget push "${OUTPUT_DIR}/*.nupkg" \
  --api-key "$GITEA_TOKEN" \
  --source "$GITEA_NUGET_SOURCE" \
  --skip-duplicate

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo ""
  echo "[dry-run] No files changed or pushed."
else
  echo ""
  echo "Done. Packages published: $PKG_VERSION"
fi
