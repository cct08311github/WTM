#!/usr/bin/env bash
# #924 review finding 6: scripts/publish-to-gitea.sh had no test coverage at all (only
# release-gitea-package.sh did, in release-script-tests.sh). Mirrors that file's
# structure: copy the script under test into an isolated repo, exercise its --dry-run
# path (which never shells out to dotnet -- see publish-to-gitea.sh's run() wrapper),
# then exercise the ~/.gitea-token file-fallback matrix via the shared library in
# test/lib/token-file-fixtures.sh.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/publish-to-gitea.sh"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

TEST_REPO="$TMP_DIR/repo"
mkdir -p "$TEST_REPO/scripts"
cp "$SCRIPT_UNDER_TEST" "$TEST_REPO/scripts/publish-to-gitea.sh"
chmod +x "$TEST_REPO/scripts/publish-to-gitea.sh"
# The script resolves its token via a co-located resolve-gitea-token.py (#924); it must
# ship alongside the copy under test the same way it ships alongside the real script.
cp "$ROOT_DIR/scripts/resolve-gitea-token.py" "$TEST_REPO/scripts/resolve-gitea-token.py"

cat > "$TEST_REPO/version.props" <<'EOF'
<Project>
  <PropertyGroup>
    <VersionPrefix>10.5.1</VersionPrefix>
  </PropertyGroup>
</Project>
EOF

export GITEA_TOKEN="fake-token-for-tests"

run_case() {
  local name="$1"
  shift
  echo "[case] $name"
  (
    cd "$TEST_REPO"
    ./scripts/publish-to-gitea.sh "$@"
  )
}

run_case "dry-run stable release" --dry-run > "$TMP_DIR/out1.txt"
grep -q 'Version : 10.5.1' "$TMP_DIR/out1.txt"
grep -q '\[dry-run\] No files changed or pushed.' "$TMP_DIR/out1.txt"

run_case "dry-run pre-release suffix" --dry-run --suffix beta.1 > "$TMP_DIR/out2.txt"
grep -q 'Version : 10.5.1-beta.1' "$TMP_DIR/out2.txt"

# #924 review finding 6: the cases above always ran with GITEA_TOKEN set in the
# environment, so the ~/.gitea-token file-fallback path scripts/resolve-gitea-token.py
# implements had no coverage. Exercise it directly, with GITEA_TOKEN unset and
# GITEA_TOKEN_FILE pointed at fabricated fixture files -- never the real
# ~/.gitea-token.
# shellcheck source=test/lib/token-file-fixtures.sh
source "$ROOT_DIR/test/lib/token-file-fixtures.sh"

run_with_token_file() {
  local file="$1"
  (
    cd "$TEST_REPO"
    unset GITEA_TOKEN
    GITEA_TOKEN_FILE="$file" ./scripts/publish-to-gitea.sh --dry-run
  )
}
# shellcheck disable=SC2034 # read by assert_token_success_case in the sourced lib
SUCCESS_MARKER='[dry-run] No files changed or pushed.'

echo "[case] GITEA_TOKEN file-fallback matrix (publish-to-gitea.sh)"
run_gitea_token_file_matrix "$TMP_DIR/token-cases-publish"

echo "publish-to-gitea-tests: PASS"
