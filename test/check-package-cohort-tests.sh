#!/usr/bin/env bash
# #925 cross-vendor review finding 3: local test harness for
# scripts/check-package-cohort.py -- the pre-publish check that refuses to proceed if a
# release version is PARTIALLY published in a registry (some but not all of the six WTM
# packages already exist at that version), the exact "package N of 6 failed last time"
# scenario the finding describes. Spins up a throwaway `python3 -m http.server` serving
# a fabricated NuGet V3 service index + flat-container layout on localhost -- real HTTP
# calls, no mocking of the script itself, but no dependency on the actual Gitea/GitHub
# registries or even nuget.org (this script also has its own separate live smoke check
# against nuget.org; see its header comment for why that was done and what it does not
# cover).

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/check-package-cohort.py"

TMP_DIR="$(mktemp -d)"
SERVER_PID=""
cleanup() {
  if [[ -n "$SERVER_PID" ]]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

REGISTRY_ROOT="$TMP_DIR/registry"
mkdir -p "$REGISTRY_ROOT/v3-flatcontainer"

# Minimal NuGet V3 service index advertising a PackageBaseAddress resource pointing at
# our flat-container directory -- this is the standardized discovery mechanism
# check-package-cohort.py uses, not a hardcoded Gitea/GitHub URL shape.
cat > "$REGISTRY_ROOT/index.json" <<EOF
{
  "version": "3.0.0",
  "resources": [
    {"@id": "http://127.0.0.1:__PORT__/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0"}
  ]
}
EOF

# Pick a free port and substitute it into the fixture index (the @id must be absolute).
PORT=$(python3 -c "import socket; s=socket.socket(); s.bind(('127.0.0.1', 0)); print(s.getsockname()[1]); s.close()")
sed -i.bak "s/__PORT__/${PORT}/" "$REGISTRY_ROOT/index.json" && rm -f "$REGISTRY_ROOT/index.json.bak"

# "Publish" a package version by creating the flat-container file NuGet's protocol
# expects: {base}/{id-lower}/{version-lower}/{id-lower}.{version-lower}.nupkg
publish_fixture_package() {
  local id_lower="$1"
  local version_lower="$2"
  local dir="$REGISTRY_ROOT/v3-flatcontainer/${id_lower}/${version_lower}"
  mkdir -p "$dir"
  echo "fake nupkg bytes" > "${dir}/${id_lower}.${version_lower}.nupkg"
}

publish_fixture_package "walkingtec.mvvm.core" "10.21.0"
publish_fixture_package "walkingtec.mvvm.mvc" "10.21.0"
publish_fixture_package "walkingtec.mvvm.core" "9.9.9"
publish_fixture_package "walkingtec.mvvm.mvc" "9.9.9"
publish_fixture_package "walkingtec.mvvm.layui" "9.9.9"

python3 -m http.server "$PORT" --directory "$REGISTRY_ROOT" --bind 127.0.0.1 \
  > "$TMP_DIR/server.log" 2>&1 &
SERVER_PID=$!

# Wait for the server to actually accept connections before running any case.
for _ in $(seq 1 50); do
  if curl -sf "http://127.0.0.1:${PORT}/index.json" > /dev/null 2>&1; then
    break
  fi
  sleep 0.1
done
if ! curl -sf "http://127.0.0.1:${PORT}/index.json" > /dev/null 2>&1; then
  echo "FAIL: fixture HTTP server never came up" >&2
  cat "$TMP_DIR/server.log" >&2
  exit 1
fi

INDEX_URL="http://127.0.0.1:${PORT}/index.json"

run_case() {
  local name="$1"
  local expected_exit="$2"
  shift 2
  echo "[case] $name"
  local status=0
  python3 "$SCRIPT_UNDER_TEST" "$INDEX_URL" "$@" > "$TMP_DIR/out.txt" 2>&1 || status=$?
  cat "$TMP_DIR/out.txt"
  if [[ "$status" -ne "$expected_exit" ]]; then
    echo "FAIL: $name -- expected exit $expected_exit, got $status" >&2
    exit 1
  fi
  cp "$TMP_DIR/out.txt" "$TMP_DIR/last_out.txt"
}

# ── Case 1: all present (idempotent re-run, e.g. --skip-duplicate would no-op every
# package) -- must pass (exit 0). ────────────────────────────────────────────────────
run_case "all packages already exist -- clean idempotent re-run" 0 \
  "10.21.0" WalkingTec.Mvvm.Core WalkingTec.Mvvm.Mvc
grep -q "2/2 already published" "$TMP_DIR/last_out.txt"

# ── Case 2: none present (fresh first publish) -- must pass (exit 0). ───────────────
run_case "no packages exist yet -- fresh first publish" 0 \
  "10.99.0" WalkingTec.Mvvm.Core WalkingTec.Mvvm.Mvc
grep -q "0/2 already published" "$TMP_DIR/last_out.txt"

# ── Case 3: PARTIAL cohort -- Core and Mvc exist at 9.9.9, LayUI does not (fabricated
# to look like WorkFlow/Etl/S3 having failed mid-publish last time). This is the exact
# defect scenario the finding describes -- must be REFUSED (exit 1). ────────────────
run_case "mixed/partial cohort is refused" 1 \
  "9.9.9" WalkingTec.Mvvm.Core WalkingTec.Mvvm.Mvc WalkingTec.Mvvm.LayUI WalkingTec.Mvvm.WorkFlow
grep -q "MIXED cohort" "$TMP_DIR/last_out.txt"

# ── Case 4: unreachable/bad service index URL -- fail closed (exit 2), never
# silently "0 exist, safe to publish". ───────────────────────────────────────────────
echo "[case] bad service index URL fails closed"
status=0
python3 "$SCRIPT_UNDER_TEST" "http://127.0.0.1:${PORT}/nonexistent-index.json" "10.21.0" \
  WalkingTec.Mvvm.Core > "$TMP_DIR/out_badurl.txt" 2>&1 || status=$?
cat "$TMP_DIR/out_badurl.txt"
if [[ "$status" -ne 2 ]]; then
  echo "FAIL: bad service index URL -- expected exit 2, got $status" >&2
  exit 1
fi

echo "check-package-cohort-tests: PASS"
