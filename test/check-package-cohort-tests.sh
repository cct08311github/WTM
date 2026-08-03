#!/usr/bin/env bash
# #925 cross-vendor review finding 3: local test harness for
# scripts/check-package-cohort.py -- the pre-publish check that refuses to proceed if a
# release version is PARTIALLY published in a registry (some but not all of the six WTM
# packages already exist at that version), the exact "package N of 6 failed last time"
# scenario the finding describes. Spins up a throwaway `python3 -m http.server` serving
# a fabricated NuGet V3 service index + flat-container layout on localhost -- real HTTP
# calls, no mocking of the script itself, but no dependency on the actual internal infrastructure/GitHub
# registries or even nuget.org (this script also has its own separate live smoke check
# against nuget.org; see its header comment for why that was done and what it does not
# cover).
#
# #967: a SECOND fixture server is started alongside the plain `python3 -m http.server`
# above -- a small custom http.server subclass (see SERVER_SCRIPT below) that models
# the real internal infrastructure/GitHub Packages behavior the plain server does NOT reproduce: HEAD on
# the flat-container .nupkg path returns 405 Method Not Allowed regardless of whether
# the version exists, while GET behaves correctly (200/404). That gap in the plain
# server is exactly why the original HEAD-based package_exists() defect was never
# caught by this suite. It also simulates a genuine non-404 GET failure (500) for one
# reserved version, to pin fail-closed behavior for anomalies that are NOT the
# HEAD/405-masking issue.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/check-package-cohort.py"

TMP_DIR="$(mktemp -d)"
SERVER_PID=""
REGISTRY_SERVER_PID=""
cleanup() {
  if [[ -n "$SERVER_PID" ]]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [[ -n "$REGISTRY_SERVER_PID" ]]; then
    kill "$REGISTRY_SERVER_PID" 2>/dev/null || true
    wait "$REGISTRY_SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

REGISTRY_ROOT="$TMP_DIR/registry"
mkdir -p "$REGISTRY_ROOT/v3-flatcontainer"

# Minimal NuGet V3 service index advertising a PackageBaseAddress resource pointing at
# our flat-container directory -- this is the standardized discovery mechanism
# check-package-cohort.py uses, not a hardcoded internal infrastructure/GitHub URL shape.
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
  local index_url="$3"
  shift 3
  echo "[case] $name"
  local status=0
  python3 "$SCRIPT_UNDER_TEST" "$index_url" "$@" > "$TMP_DIR/out.txt" 2>&1 || status=$?
  cat "$TMP_DIR/out.txt"
  if [[ "$status" -ne "$expected_exit" ]]; then
    echo "FAIL: $name -- expected exit $expected_exit, got $status" >&2
    exit 1
  fi
  cp "$TMP_DIR/out.txt" "$TMP_DIR/last_out.txt"
}

# ── Case 1: all present (idempotent re-run, e.g. --skip-duplicate would no-op every
# package) -- must pass (exit 0). ────────────────────────────────────────────────────
run_case "all packages already exist -- clean idempotent re-run" 0 "$INDEX_URL" \
  "10.21.0" WalkingTec.Mvvm.Core WalkingTec.Mvvm.Mvc
grep -q "2/2 already published" "$TMP_DIR/last_out.txt"

# ── Case 2: none present (fresh first publish) -- must pass (exit 0). ───────────────
run_case "no packages exist yet -- fresh first publish" 0 "$INDEX_URL" \
  "10.99.0" WalkingTec.Mvvm.Core WalkingTec.Mvvm.Mvc
grep -q "0/2 already published" "$TMP_DIR/last_out.txt"

# ── Case 3: PARTIAL cohort -- Core and Mvc exist at 9.9.9, LayUI does not (fabricated
# to look like WorkFlow/Etl/S3 having failed mid-publish last time). This is the exact
# defect scenario the finding describes -- must be REFUSED (exit 1). ────────────────
run_case "mixed/partial cohort is refused" 1 "$INDEX_URL" \
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

# ═══════════════════════════════════════════════════════════════════════════════════
# #967: a second fixture registry, served by a custom handler that models the real
# internal infrastructure/GitHub Packages behavior the plain `python3 -m http.server` above does NOT --
# HEAD on the flat-container .nupkg path returns 405 Method Not Allowed no matter
# whether the version exists, while GET behaves correctly (200 if present, 404 if
# absent). This is the handler shape that made the live internal-host internal registry return
# "HTTP Error 405: Method Not Allowed" for every HEAD-based existence check regardless
# of outcome -- the defect the plain SimpleHTTPRequestHandler-based server above can
# never reproduce, because it (unlike real internal infrastructure/GitHub) implements HEAD correctly.
# ═══════════════════════════════════════════════════════════════════════════════════

REGISTRY_REGISTRY_ROOT="$TMP_DIR/gitea-registry"
mkdir -p "$REGISTRY_REGISTRY_ROOT/v3-flatcontainer"

cat > "$REGISTRY_REGISTRY_ROOT/index.json" <<EOF
{
  "version": "3.0.0",
  "resources": [
    {"@id": "http://127.0.0.1:__REGISTRY_PORT__/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0"}
  ]
}
EOF

REGISTRY_PORT=$(python3 -c "import socket; s=socket.socket(); s.bind(('127.0.0.1', 0)); print(s.getsockname()[1]); s.close()")
sed -i.bak "s/__REGISTRY_PORT__/${REGISTRY_PORT}/" "$REGISTRY_REGISTRY_ROOT/index.json" && rm -f "$REGISTRY_REGISTRY_ROOT/index.json.bak"

publish_gitea_fixture_package() {
  local id_lower="$1"
  local version_lower="$2"
  local dir="$REGISTRY_REGISTRY_ROOT/v3-flatcontainer/${id_lower}/${version_lower}"
  mkdir -p "$dir"
  echo "fake nupkg bytes" > "${dir}/${id_lower}.${version_lower}.nupkg"
}

publish_gitea_fixture_package "walkingtec.mvvm.core" "10.21.0"

# Custom http.server subclass: HEAD always 405 (real internal infrastructure/GitHub behavior); GET on the
# reserved version "0.0.500" always 500 (simulates a genuine registry failure that is
# NOT the HEAD/405-masking issue, to pin fail-closed behavior for it); every other GET
# falls through to normal SimpleHTTPRequestHandler file serving (200/404).
REGISTRY_SERVER_SCRIPT="$TMP_DIR/gitea_like_server.py"
cat > "$REGISTRY_SERVER_SCRIPT" <<'PYEOF'
import http.server
import os
import sys

PORT = int(sys.argv[1])
ROOT = sys.argv[2]
os.chdir(ROOT)


class GiteaLikeHandler(http.server.SimpleHTTPRequestHandler):
    def do_HEAD(self):
        self.send_error(405, "Method Not Allowed")

    def do_GET(self):
        if "/0.0.500/" in self.path:
            self.send_error(500, "Internal Server Error (fixture: simulated registry failure)")
            return
        super().do_GET()

    def log_message(self, fmt, *args):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), GiteaLikeHandler).serve_forever()
PYEOF

python3 "$REGISTRY_SERVER_SCRIPT" "$REGISTRY_PORT" "$REGISTRY_REGISTRY_ROOT" \
  > "$TMP_DIR/gitea_server.log" 2>&1 &
REGISTRY_SERVER_PID=$!

for _ in $(seq 1 50); do
  if curl -sf "http://127.0.0.1:${REGISTRY_PORT}/index.json" > /dev/null 2>&1; then
    break
  fi
  sleep 0.1
done
if ! curl -sf "http://127.0.0.1:${REGISTRY_PORT}/index.json" > /dev/null 2>&1; then
  echo "FAIL: internal infrastructure-like fixture HTTP server never came up" >&2
  cat "$TMP_DIR/gitea_server.log" >&2
  exit 1
fi

REGISTRY_INDEX_URL="http://127.0.0.1:${REGISTRY_PORT}/index.json"

# ── Case 5: defect-reproduction case. Against the internal infrastructure-like handler (HEAD->405
# always, GET->200/404 correctly), a version that DOES exist must still be reported as
# published. Before the #967 fix (package_exists() probing with HEAD) this fails: HEAD
# comes back 405, which is neither the 200/exists nor the 404/absent branch, so it
# propagates as an unhandled HTTPError and the script fail-closes with exit 2 instead
# of reporting the version as published. ─────────────────────────────────────────────
run_case "version exists -- detected correctly against HEAD-405 internal infrastructure-like registry" 0 \
  "$REGISTRY_INDEX_URL" "10.21.0" WalkingTec.Mvvm.Core
grep -q "1/1 already published" "$TMP_DIR/last_out.txt"

# ── Case 6: same internal infrastructure-like handler, a version that does NOT exist -- must be reported
# as not yet published (exit 0, 0/1). Proves GET-404 detection also works correctly
# against the HEAD-405 handler, not just the plain one. ─────────────────────────────
run_case "version absent -- detected correctly against HEAD-405 internal infrastructure-like registry" 0 \
  "$REGISTRY_INDEX_URL" "99.99.99" WalkingTec.Mvvm.Core
grep -q "0/1 already published" "$TMP_DIR/last_out.txt"

# ── Case 7: a genuine non-404 HTTP error on GET (500, reserved version "0.0.500") that
# is NOT the HEAD/405-masking issue -- must still fail closed (exit 2, ERROR: message),
# not be silently treated as "exists" or "does not exist". Pins the non-404 HTTPError
# path going forward now that the check is GET-based. ───────────────────────────────
run_case "genuine 500 on GET still fails closed" 2 \
  "$REGISTRY_INDEX_URL" "0.0.500" WalkingTec.Mvvm.Core
grep -q "ERROR:" "$TMP_DIR/last_out.txt"

echo "check-package-cohort-tests: PASS"
