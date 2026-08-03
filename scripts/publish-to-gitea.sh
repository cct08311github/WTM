#!/usr/bin/env bash
# Manual local publish to Gitea NuGet registry.
#
# #925 cross-vendor review finding 4: this script's non-dry-run path is DISABLED. It
# was previously recommended (docs/ci-operations.md, docs/gitea-packages.md) as the
# fallback "when the Gitea Actions runner is unavailable" -- but it only packs 3 of the
# 5 published packages (Core, Mvc, TagHelpers.LayUI -- never WorkFlow, Etl), and
# performs NONE of publish-nuget.yml's gate: no five-package
# reconciliation, no smoke test, no local vulnerability scan, no version-cohort check.
# A runner outage would have made this the OFFICIALLY DOCUMENTED way to ship 3 of 5
# packages with every one of those checks skipped.
#
# Chosen fix (of the two offered by the finding: disable, or bring this script up to
# parity with the full gate): DISABLE. Reimplementing the full five-package
# smoke+vulnerability+cohort gate here would duplicate real, security-relevant logic in
# two places that must then be kept in permanent lockstep -- exactly the drift risk
# .claude/rules/dependency-management.md and this repo's own history (the SIGPIPE
# false-green in publish-nuget.yml's vulnerability scan, discovered and fixed alongside
# this) warn against. The reliable fallback for "the runner is down" is documented in
# docs/gitea-packages.md's tag-object-dedup recovery SOP (push a fresh annotated tag
# object) -- fix the trigger, don't bypass the gate it protects.
#
# Usage:
#   ./scripts/publish-to-gitea.sh --dry-run [--suffix <pre-release>]
#
# A real (non-dry-run) publish always refuses -- see the check immediately after
# argument parsing below. --dry-run remains fully functional for previewing what a
# publish WOULD do (version resolution, package list, masked push command) without
# executing anything.
#
# Token source (first match wins), resolved by scripts/resolve-gitea-token.py:
#   1. GITEA_TOKEN env var
#   2. ~/.gitea-token -- a single 40-char lowercase-hex token, bare or as
#      `[export] GITEA_TOKEN=...`. Never sourced (see docs/ci-operations.md
#      for why); see resolve-gitea-token.py for the exact validation rules.

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

#925: the non-dry-run path is DISABLED (see the header comment for why) -- every
invocation below except --dry-run now refuses immediately.

Examples:
  ./scripts/publish-to-gitea.sh --dry-run
  ./scripts/publish-to-gitea.sh --dry-run --suffix beta.1
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

# #925 cross-vendor review finding 4: refuse any real publish outright -- see the
# header comment for the full rationale (3-of-5 packages, no gate at all). This check
# runs before token resolution or any network/dotnet call so it is cheap and safe to
# hit accidentally.
if [[ "$DRY_RUN" -ne 1 ]]; then
  echo "ERROR: scripts/publish-to-gitea.sh's real (non-dry-run) publish path is disabled (#925)." >&2
  echo "  It packs only 3 of the 5 published packages (never WorkFlow/Etl)" >&2
  echo "  and runs none of publish-nuget.yml's gate (smoke test, vulnerability scan," >&2
  echo "  version-cohort check). Use --dry-run to preview. To actually publish, fix the" >&2
  echo "  Gitea Actions trigger instead -- see docs/gitea-packages.md's tag-object-dedup" >&2
  echo "  recovery SOP ('推一個全新的 annotated tag object')." >&2
  exit 1
fi

# Resolve token via scripts/resolve-gitea-token.py: env var first, then
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
    echo "ERROR: GITEA_TOKEN is not set." >&2
    echo "  Set it as an env var, or put a 40-character lowercase-hex token in ~/.gitea-token" >&2
  fi
  # exit 2 (malformed env var or file): resolve-gitea-token.py already printed
  # the specific reason to stderr.
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
#
# #925: /p:Version= ALONE does not change the packed nupkg's version in this repo --
# version.props unconditionally sets <PackageVersion>$(VersionPrefix)</PackageVersion> in
# its Release PropertyGroup, and NuGet's pack target reads PackageVersion (not Version)
# for the nuspec. Without /p:PackageVersion= too, a --suffix here was silently dropped
# and every local publish shipped the bare version.props VersionPrefix regardless of
# $PKG_VERSION. See the identical fix + verification note on
# .github/workflows/publish-nuget.yml's pack steps.
#
# #925 cross-vendor review finding 4: this never cleaned $OUTPUT_DIR before packing --
# a stale .nupkg left over from a previous run (a different version, or the same
# version repacked after a code change) would sit alongside the new ones and the glob
# push below (`${OUTPUT_DIR}/*.nupkg`) would ship it too. Retained even though the real
# publish path above is now disabled, so this does not silently regress back in if that
# guard is ever revisited.
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"
for proj in "${PROJECTS[@]}"; do
  echo "--> Packing $proj"
  run dotnet pack "$proj" -c Release -o "$OUTPUT_DIR" /p:Version="$PKG_VERSION" /p:PackageVersion="$PKG_VERSION"
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
