#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/release-gitea-package.sh"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

TEST_REPO="$TMP_DIR/repo"
FAKE_BIN="$TMP_DIR/bin"
mkdir -p "$TEST_REPO/scripts" "$FAKE_BIN"
cp "$SCRIPT_UNDER_TEST" "$TEST_REPO/scripts/release-gitea-package.sh"
chmod +x "$TEST_REPO/scripts/release-gitea-package.sh"

cat > "$TEST_REPO/version.props" <<'EOF'
<Project>
  <PropertyGroup>
    <VersionPrefix>10.5.1</VersionPrefix>
  </PropertyGroup>
</Project>
EOF

cat > "$FAKE_BIN/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  branch)
    echo "dotnet10"
    ;;
  status)
    exit 0
    ;;
  add|commit|push)
    echo "git:$*" >> "${FAKE_GIT_LOG}"
    ;;
  *)
    echo "unexpected git command: $*" >&2
    exit 1
    ;;
esac
EOF
chmod +x "$FAKE_BIN/git"

cat > "$FAKE_BIN/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
# Intercept all curl calls; log POST dispatches, succeed on GET (reachability check)
for arg in "$@"; do
  case "$arg" in
    */dispatches)
      echo "curl:$*" >> "${FAKE_CURL_LOG}"
      exit 0
      ;;
  esac
done
# Default: reachability check — succeed silently
exit 0
EOF
chmod +x "$FAKE_BIN/curl"

export PATH="$FAKE_BIN:$PATH"
export GITEA_TOKEN="fake-token-for-tests"
export FAKE_GIT_LOG="$TMP_DIR/git.log"
export FAKE_CURL_LOG="$TMP_DIR/curl.log"
touch "$FAKE_GIT_LOG" "$FAKE_CURL_LOG"

run_case() {
  local name="$1"
  shift
  echo "[case] $name"
  (
    cd "$TEST_REPO"
    ./scripts/release-gitea-package.sh "$@"
  )
}

run_case "dry-run same version" --dry-run 10.5.1 > "$TMP_DIR/out1.txt"
grep -q '\[dry-run\] Would trigger Gitea Packages publish for 10.5.1' "$TMP_DIR/out1.txt"
if [[ -s "$FAKE_GIT_LOG" || -s "$FAKE_CURL_LOG" ]]; then
  echo "dry-run same version unexpectedly executed git/curl actions" >&2
  exit 1
fi

run_case "same version triggers workflow" 10.5.1 > "$TMP_DIR/out2.txt"
grep -q 'Triggered Gitea Packages publish for 10.5.1' "$TMP_DIR/out2.txt"
grep -q 'dispatches' "$FAKE_CURL_LOG"

: > "$FAKE_CURL_LOG"
: > "$FAKE_GIT_LOG"

run_case "dry-run version bump" --dry-run 10.5.2 beta.1 > "$TMP_DIR/out3.txt"
grep -q '\[dry-run\] Would update VersionPrefix from 10.5.1 to 10.5.2' "$TMP_DIR/out3.txt"
grep -q '\[dry-run\] Would trigger Gitea Packages publish for 10.5.2-beta.1' "$TMP_DIR/out3.txt"
if [[ -s "$FAKE_GIT_LOG" || -s "$FAKE_CURL_LOG" ]]; then
  echo "dry-run version bump unexpectedly executed git/curl actions" >&2
  exit 1
fi

echo "release-script-tests: PASS"
