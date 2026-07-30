#!/usr/bin/env bash
# Shared GITEA_TOKEN file-fallback test matrix (#924 review finding 6).
#
# Sourced by test/release-script-tests.sh and test/publish-to-gitea-tests.sh so both
# scripts that call scripts/resolve-gitea-token.py get the same coverage of its
# file-parsing rules, without duplicating the fixture-generation logic. Every fixture
# uses a fabricated 40-hex string in a temp dir -- this must never read, write, or
# reference the real ~/.gitea-token.
#
# The caller must, before sourcing this file, define:
#   run_with_token_file <token_file_path>
#       Runs the script under test with GITEA_TOKEN unset and
#       GITEA_TOKEN_FILE=<token_file_path>, in a mode that succeeds when the token
#       resolves (e.g. --dry-run). Must print combined stdout+stderr and return the
#       script's exit code.
#   SUCCESS_MARKER
#       A string that appears in output only when the script actually proceeded past
#       token resolution.

FAKE_TOKEN="a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"

_gtfc_fail() {
  echo "FAIL [$1]: $2" >&2
  shift 2
  if [[ $# -gt 0 ]]; then
    echo "--- output ---" >&2
    printf '%s\n' "$1" >&2
  fi
  exit 1
}

assert_token_success_case() {
  local name="$1" file="$2"
  local out ec
  # `if out="$(...)"; then ec=0; else ec=$?; fi`, not `out="$(...)"; ec=$?` --
  # the latter is a plain assignment statement outside any conditional, so
  # under this file's callers' `set -e` a nonzero exit from run_with_token_file
  # would abort the whole test script right here instead of letting this
  # function report a proper FAIL (the same class of bug review finding 5
  # flagged in the scripts themselves).
  if out="$(run_with_token_file "$file" 2>&1)"; then
    ec=0
  else
    ec=$?
  fi
  if [[ "$ec" -ne 0 ]]; then
    _gtfc_fail "$name" "expected success (exit 0), got exit $ec" "$out"
  fi
  if ! grep -qF "$SUCCESS_MARKER" <<<"$out"; then
    _gtfc_fail "$name" "expected success marker '$SUCCESS_MARKER' in output" "$out"
  fi
  echo "  ok: $name"
}

assert_token_failure_case() {
  local name="$1" file="$2" expect_substr="$3"
  local out ec
  if out="$(run_with_token_file "$file" 2>&1)"; then
    ec=0
  else
    ec=$?
  fi
  if [[ "$ec" -eq 0 ]]; then
    _gtfc_fail "$name" "expected failure (nonzero exit), got exit 0" "$out"
  fi
  if ! grep -qF "$expect_substr" <<<"$out"; then
    _gtfc_fail "$name" "expected output to contain '$expect_substr'" "$out"
  fi
  echo "  ok: $name"
}

# Runs all 8 cases from #924 review finding 6 against whatever
# run_with_token_file/SUCCESS_MARKER the caller defined. $1 = scratch dir to write
# fixture files into (caller owns cleanup, e.g. via its own TMP_DIR trap).
run_gitea_token_file_matrix() {
  local case_dir="$1"
  mkdir -p "$case_dir"

  printf '%s\n' "$FAKE_TOKEN" > "$case_dir/bare.txt"
  assert_token_success_case "bare token" "$case_dir/bare.txt"

  printf "export GITEA_TOKEN='%s'\n" "$FAKE_TOKEN" > "$case_dir/export.txt"
  assert_token_success_case "export GITEA_TOKEN='...' form" "$case_dir/export.txt"

  printf '%s\r\n' "$FAKE_TOKEN" > "$case_dir/crlf.txt"
  assert_token_success_case "CRLF line ending" "$case_dir/crlf.txt"

  : > "$case_dir/empty.txt"
  assert_token_failure_case "empty file" "$case_dir/empty.txt" "GITEA_TOKEN is not set"

  assert_token_failure_case "missing file" "$case_dir/does-not-exist.txt" "GITEA_TOKEN is not set"

  { printf '%s\n' "$FAKE_TOKEN"; printf '%s\n' "$FAKE_TOKEN"; } > "$case_dir/two-tokens.txt"
  assert_token_failure_case "two tokens" "$case_dir/two-tokens.txt" "expected exactly 1"

  printf '%sa\n' "$FAKE_TOKEN" > "$case_dir/too-long.txt"
  assert_token_failure_case "41-char hex" "$case_dir/too-long.txt" "not a 40-character"

  printf '%s\n' "$FAKE_TOKEN" > "$case_dir/unreadable.txt"
  chmod 000 "$case_dir/unreadable.txt"
  if [[ -r "$case_dir/unreadable.txt" ]]; then
    # Running as a user (e.g. root, or a filesystem that ignores POSIX perms)
    # that can still read a chmod-000 file -- can't exercise the unreadable
    # path in this environment. Not a failure of the assertion itself.
    echo "  skip: unreadable file (this user/filesystem can still read chmod 000)"
  else
    assert_token_failure_case "unreadable file" "$case_dir/unreadable.txt" "cannot read"
  fi
  chmod 600 "$case_dir/unreadable.txt"
}
