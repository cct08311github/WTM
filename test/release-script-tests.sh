#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/release-github-package.sh"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

TEST_REPO="$TMP_DIR/repo"
FAKE_BIN="$TMP_DIR/bin"
mkdir -p "$TEST_REPO/scripts" "$FAKE_BIN"
cp "$SCRIPT_UNDER_TEST" "$TEST_REPO/scripts/release-github-package.sh"
chmod +x "$TEST_REPO/scripts/release-github-package.sh"

cat > "$TEST_REPO/version.props" <<'EOF'
<Project>
  <PropertyGroup>
    <VersionPrefix>8.2.2</VersionPrefix>
  </PropertyGroup>
</Project>
EOF

cat > "$FAKE_BIN/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  branch)
    echo "dotnet8"
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

cat > "$FAKE_BIN/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  auth)
    exit 0
    ;;
  workflow)
    echo "gh:$*" >> "${FAKE_GH_LOG}"
    ;;
  *)
    echo "unexpected gh command: $*" >&2
    exit 1
    ;;
esac
EOF
chmod +x "$FAKE_BIN/gh"

export PATH="$FAKE_BIN:$PATH"
export FAKE_GIT_LOG="$TMP_DIR/git.log"
export FAKE_GH_LOG="$TMP_DIR/gh.log"
touch "$FAKE_GIT_LOG" "$FAKE_GH_LOG"

run_case() {
  local name="$1"
  shift
  echo "[case] $name"
  (
    cd "$TEST_REPO"
    ./scripts/release-github-package.sh "$@"
  )
}

run_case "dry-run same version" --dry-run 8.2.2 > "$TMP_DIR/out1.txt"
grep -q '\[dry-run\] Would trigger GitHub Packages publish for 8.2.2' "$TMP_DIR/out1.txt"
if [[ -s "$FAKE_GIT_LOG" || -s "$FAKE_GH_LOG" ]]; then
  echo "dry-run same version unexpectedly executed git/gh actions" >&2
  exit 1
fi

run_case "same version triggers workflow" 8.2.2 > "$TMP_DIR/out2.txt"
grep -q 'Triggered GitHub Packages publish for 8.2.2' "$TMP_DIR/out2.txt"
grep -q 'gh:workflow run publish-nuget.yml --ref dotnet8' "$FAKE_GH_LOG"

: > "$FAKE_GH_LOG"
: > "$FAKE_GIT_LOG"

run_case "dry-run version bump" --dry-run 8.2.3 beta.1 > "$TMP_DIR/out3.txt"
grep -q '\[dry-run\] Would update VersionPrefix from 8.2.2 to 8.2.3' "$TMP_DIR/out3.txt"
grep -q '\[dry-run\] Would trigger GitHub Packages publish for 8.2.3-beta.1' "$TMP_DIR/out3.txt"
if [[ -s "$FAKE_GIT_LOG" || -s "$FAKE_GH_LOG" ]]; then
  echo "dry-run version bump unexpectedly executed git/gh actions" >&2
  exit 1
fi

echo "release-script-tests: PASS"
