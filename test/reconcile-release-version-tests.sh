#!/usr/bin/env bash
# #925: local test harness for scripts/reconcile-release-version.sh -- the version /
# CHANGELOG reconciliation logic .github/workflows/publish-nuget.yml's "Determine and
# reconcile package version" step runs on every publish. A real publish cannot be
# exercised from this repo (no Gitea/GitHub API calls, no irreversible package push), so
# this harness proves the logic directly against fabricated version.props/CHANGELOG.md
# fixtures instead. Mirrors test/release-script-tests.sh's structure: isolated temp repo,
# `run_case`/`run_case_expect_failure` helpers, output captured to a file rather than
# piped into `grep -q` (docs/ci-operations.md pitfall #3 -- a live pipe into `grep -q`
# SIGPIPEs the writer under this script's own `set -o pipefail` the moment grep closes
# after its first match).

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/reconcile-release-version.sh"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

TEST_REPO="$TMP_DIR/repo"
mkdir -p "$TEST_REPO"

write_fixture() {
  # write_fixture <version_prefix> <changelog-body-lines...>
  local prefix="$1"
  shift
  cat > "$TEST_REPO/version.props" <<EOF
<Project>
  <PropertyGroup>
    <VersionPrefix>${prefix}</VersionPrefix>
  </PropertyGroup>
</Project>
EOF
  {
    echo "# Changelog"
    echo ""
    printf '%s\n' "$@"
  } > "$TEST_REPO/CHANGELOG.md"
}

run_case() {
  local name="$1"
  shift
  echo "[case] $name"
  (
    cd "$TEST_REPO"
    VERSION_FILE=version.props CHANGELOG_FILE=CHANGELOG.md "$SCRIPT_UNDER_TEST"
  )
}

run_case_expect_failure() {
  local name="$1"
  echo "[case] $name"
  local status=0
  (
    cd "$TEST_REPO"
    VERSION_FILE=version.props CHANGELOG_FILE=CHANGELOG.md "$SCRIPT_UNDER_TEST"
  ) > "$TMP_DIR/last_fail_out.txt" 2>&1 || status=$?
  if [[ "$status" -eq 0 ]]; then
    echo "expected failure but got exit 0: $name" >&2
    cat "$TMP_DIR/last_fail_out.txt" >&2
    exit 1
  fi
}

# ── Case 1: matching stable tag ──────────────────────────────────────────────────────
write_fixture "10.21.0" "## [10.21.0] - 2026-07-30" "" "### Fixed" "- stuff"
EVENT_NAME=push REF_NAME=v10.21.0 run_case "matching stable tag" > "$TMP_DIR/out1.txt"
cat "$TMP_DIR/out1.txt"
grep -qx 'version=10.21.0' "$TMP_DIR/out1.txt"
grep -qx 'is_prerelease=false' "$TMP_DIR/out1.txt"

# ── Case 2: mismatched stable tag ────────────────────────────────────────────────────
write_fixture "10.21.0" "## [10.21.0] - 2026-07-30"
EVENT_NAME=push REF_NAME=v10.20.0 run_case_expect_failure "mismatched stable tag"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "VersionPrefix is 10.21.0" "$TMP_DIR/last_fail_out.txt"

# ── Case 3: -rc.N tag (CHANGELOG still under Unreleased -- must NOT be required) ─────
write_fixture "10.21.0" "## [Unreleased]" "" "stuff not yet dated"
EVENT_NAME=push REF_NAME=v10.21.0-rc.1 run_case "-rc.N tag" > "$TMP_DIR/out3.txt"
cat "$TMP_DIR/out3.txt"
grep -qx 'version=10.21.0-rc.1' "$TMP_DIR/out3.txt"
grep -qx 'is_prerelease=true' "$TMP_DIR/out3.txt"

# ── Case 4: workflow_dispatch WITH suffix (RC path) ──────────────────────────────────
write_fixture "10.21.0" "## [Unreleased]"
EVENT_NAME=workflow_dispatch VERSION_SUFFIX=beta.2 run_case "workflow_dispatch with suffix" > "$TMP_DIR/out4.txt"
cat "$TMP_DIR/out4.txt"
grep -qx 'version=10.21.0-beta.2' "$TMP_DIR/out4.txt"
grep -qx 'is_prerelease=true' "$TMP_DIR/out4.txt"

# ── Case 5: workflow_dispatch WITHOUT suffix (stable path) ───────────────────────────
write_fixture "10.21.0" "## [10.21.0] - 2026-07-30"
EVENT_NAME=workflow_dispatch VERSION_SUFFIX='' run_case "workflow_dispatch without suffix" > "$TMP_DIR/out5.txt"
cat "$TMP_DIR/out5.txt"
grep -qx 'version=10.21.0' "$TMP_DIR/out5.txt"
grep -qx 'is_prerelease=false' "$TMP_DIR/out5.txt"

# ── Case 6: missing CHANGELOG heading (stable tag, still under Unreleased) ──────────
write_fixture "10.21.0" "## [Unreleased]" "" "stuff"
EVENT_NAME=push REF_NAME=v10.21.0 run_case_expect_failure "missing CHANGELOG heading"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "is not a dated" "$TMP_DIR/last_fail_out.txt"

# ── Extra edge cases (not in the required matrix, cheap to keep given case 1-6 above
# already stood up the fixture plumbing) ─────────────────────────────────────────────

# Malformed tag entirely.
write_fixture "10.21.0" "## [10.21.0] - 2026-07-30"
EVENT_NAME=push REF_NAME=not-a-tag run_case_expect_failure "malformed tag"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "does not look like" "$TMP_DIR/last_fail_out.txt"

# CHANGELOG heading present but for an OLDER, already-released version -- the exact
# "[Unreleased] holds three versions' worth of content" shape #925 describes.
write_fixture "10.21.0" "## [10.18.0] - 2026-07-22"
EVENT_NAME=push REF_NAME=v10.21.0 run_case_expect_failure "newest heading is an older released version"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "is not a dated" "$TMP_DIR/last_fail_out.txt"

# CHANGELOG heading present with a placeholder instead of a real date.
write_fixture "10.21.0" "## [10.21.0] - TBD"
EVENT_NAME=push REF_NAME=v10.21.0 run_case_expect_failure "placeholder date instead of a real one"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "is not a dated" "$TMP_DIR/last_fail_out.txt"

# ── #925 cross-vendor review finding 10 ───────────────────────────────────────────────

# CHANGELOG heading has the right DIGIT SHAPE (4-2-2) but is not a real calendar date
# (month 13, day 99) -- the pre-finding-10 regex only checked shape, not validity.
write_fixture "10.21.0" "## [10.21.0] - 2026-13-99"
EVENT_NAME=push REF_NAME=v10.21.0 run_case_expect_failure "shape-valid but not a real calendar date"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "not a real calendar date" "$TMP_DIR/last_fail_out.txt"

# Real calendar date with a real edge case (leap day) must still be ACCEPTED -- proves
# the new check validates real calendar rules rather than e.g. rejecting Feb 29
# entirely or applying an overly strict day-of-month bound.
write_fixture "10.21.0" "## [10.21.0] - 2028-02-29"
EVENT_NAME=push REF_NAME=v10.21.0 run_case "leap-day date is a real calendar date" > "$TMP_DIR/out_leap.txt"
cat "$TMP_DIR/out_leap.txt"
grep -qx 'version=10.21.0' "$TMP_DIR/out_leap.txt"

# A non-leap year's Feb 29 does NOT exist -- must be rejected.
write_fixture "10.21.0" "## [10.21.0] - 2027-02-29"
EVENT_NAME=push REF_NAME=v10.21.0 run_case_expect_failure "Feb 29 in a non-leap year"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "not a real calendar date" "$TMP_DIR/last_fail_out.txt"

# workflow_dispatch version_suffix containing a literal newline: before this fix,
# nothing validated VERSION_SUFFIX at all, so this string would have been embedded
# verbatim into the `version=...` line written to $GITHUB_OUTPUT, injecting a second
# `version=` record (classic Actions output-file injection). Must now be rejected.
write_fixture "10.21.0" "## [Unreleased]"
EVENT_NAME=workflow_dispatch VERSION_SUFFIX="$(printf 'beta.1\nversion=9.9.9-evil')" \
  run_case_expect_failure "version_suffix with embedded newline is rejected"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "must start with a letter or digit and contain only" "$TMP_DIR/last_fail_out.txt"

# workflow_dispatch version_suffix with a disallowed character (space) -- also rejected
# by the same charset check, not just newlines specifically.
write_fixture "10.21.0" "## [Unreleased]"
EVENT_NAME=workflow_dispatch VERSION_SUFFIX="beta 1" \
  run_case_expect_failure "version_suffix with an embedded space is rejected"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "must start with a letter or digit and contain only" "$TMP_DIR/last_fail_out.txt"

# workflow_dispatch version_suffix starting with a dash is also rejected (matches the
# tag-path regex's requirement that a pre-release identifier start alphanumeric).
write_fixture "10.21.0" "## [Unreleased]"
EVENT_NAME=workflow_dispatch VERSION_SUFFIX="-beta.1" \
  run_case_expect_failure "version_suffix starting with a dash is rejected"
cat "$TMP_DIR/last_fail_out.txt"
grep -q "must start with a letter or digit and contain only" "$TMP_DIR/last_fail_out.txt"

# A legitimate, charset-clean multi-segment suffix must still be ACCEPTED (proves the
# new gate is not simply rejecting everything).
write_fixture "10.21.0" "## [Unreleased]"
EVENT_NAME=workflow_dispatch VERSION_SUFFIX="rc.2" \
  run_case "legitimate version_suffix still accepted" > "$TMP_DIR/out_suffix_ok.txt"
cat "$TMP_DIR/out_suffix_ok.txt"
grep -qx 'version=10.21.0-rc.2' "$TMP_DIR/out_suffix_ok.txt"
grep -qx 'is_prerelease=true' "$TMP_DIR/out_suffix_ok.txt"

echo "reconcile-release-version-tests: PASS"
