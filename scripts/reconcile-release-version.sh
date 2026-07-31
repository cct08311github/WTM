#!/usr/bin/env bash
# #925: single source of truth for "what version are we about to publish, and is it
# legitimate to publish it". Shared between .github/workflows/publish-nuget.yml (the real
# pre-publish gate -- runs before the first `nuget push` to any registry) and
# test/reconcile-release-version-tests.sh (the local harness that proves this logic
# without a real publish, since a real publish cannot be exercised from this repo).
#
# Before this script existed, publish-nuget.yml's "Determine package version" step read
# ONLY version.props and a workflow_dispatch input -- it never looked at the tag name that
# triggered the run at all. Pushing tag `v10.21.0-rc.1` therefore published a STABLE
# `10.21.0` package (the suffix silently vanished), and nothing checked that the tag,
# version.props, and CHANGELOG.md agreed with each other or with reality (#925).
#
# Inputs (environment variables):
#   EVENT_NAME       "push" (tag push) or "workflow_dispatch". Required.
#   REF_NAME         For EVENT_NAME=push: the tag name exactly as Actions/Gitea Actions
#                     expose it via `github.ref_name` (e.g. "v10.21.0" or
#                     "v10.21.0-rc.1"). Required when EVENT_NAME=push; ignored otherwise.
#   VERSION_SUFFIX   workflow_dispatch's `version_suffix` input. Empty string (or unset)
#                     for a stable release. Ignored when EVENT_NAME=push -- the RC path
#                     for a tag-triggered release is the tag's OWN suffix, not this input.
#   VERSION_FILE     Path to version.props. Default: version.props.
#   CHANGELOG_FILE   Path to CHANGELOG.md. Default: CHANGELOG.md.
#
# Output: on success, prints exactly these two lines to stdout and exits 0:
#   version=<X.Y.Z or X.Y.Z-suffix>
#   is_prerelease=<true|false>
# On any disagreement or malformed input, prints a human-readable "ERROR: ..." line to
# stderr and exits 1. Never partially prints `version=`/`is_prerelease=` on failure, so a
# caller that only checks stdout for the `version=` line can't be fooled by a truncated
# success line into treating a failed run as having produced a version.
#
# What this script does NOT check (see .github/workflows/publish-nuget.yml and the #925
# PR description for the full list of what is proven vs. assumed at publish time): it does
# not run a build, does not run tests, does not scan for vulnerabilities, and does not
# install/exercise any package. It is purely a text-reconciliation gate over the tag/input,
# version.props, and CHANGELOG.md.
set -euo pipefail

VERSION_FILE="${VERSION_FILE:-version.props}"
CHANGELOG_FILE="${CHANGELOG_FILE:-CHANGELOG.md}"
EVENT_NAME="${EVENT_NAME:-}"
REF_NAME="${REF_NAME:-}"
VERSION_SUFFIX="${VERSION_SUFFIX:-}"

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

[[ -f "$VERSION_FILE" ]] || fail "cannot find $VERSION_FILE"
[[ -f "$CHANGELOG_FILE" ]] || fail "cannot find $CHANGELOG_FILE"

# Portable across the Linux CI runner and a macOS dev shell (BSD grep has no -P) --
# matches the parsing style scripts/release-gitea-package.sh already uses.
BASE_VERSION="$(python3 - "$VERSION_FILE" <<'PY'
import re
import sys

text = open(sys.argv[1], encoding="utf-8").read()
match = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", text)
if not match:
    raise SystemExit("Cannot find VersionPrefix in " + sys.argv[1])
print(match.group(1).strip())
PY
)"
if [[ -z "$BASE_VERSION" ]]; then
  fail "VersionPrefix in $VERSION_FILE is empty"
fi
if ! [[ "$BASE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  fail "VersionPrefix '$BASE_VERSION' in $VERSION_FILE is not X.Y.Z"
fi

VERSION=""
IS_PRERELEASE=""

case "$EVENT_NAME" in
  push)
    [[ -n "$REF_NAME" ]] || fail "EVENT_NAME=push but REF_NAME is empty"
    # vX.Y.Z or vX.Y.Z-<prerelease>. <prerelease> allows the same charset semver
    # pre-release identifiers use (alphanumerics, '.', '-'); no build-metadata ('+...')
    # segment -- NuGet package versions don't carry one and neither does this repo's
    # existing workflow_dispatch RC path.
    if [[ "$REF_NAME" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)(-([0-9A-Za-z][0-9A-Za-z.-]*))?$ ]]; then
      TAG_BASE="${BASH_REMATCH[1]}"
      TAG_SUFFIX="${BASH_REMATCH[3]:-}"
    else
      fail "tag '$REF_NAME' does not look like vX.Y.Z or vX.Y.Z-<suffix> -- refusing to publish"
    fi
    if [[ "$TAG_BASE" != "$BASE_VERSION" ]]; then
      fail "tag '$REF_NAME' declares base version $TAG_BASE but $VERSION_FILE's VersionPrefix is $BASE_VERSION -- these must match exactly before anything is published"
    fi
    if [[ -n "$TAG_SUFFIX" ]]; then
      VERSION="${TAG_BASE}-${TAG_SUFFIX}"
      IS_PRERELEASE="true"
    else
      VERSION="$TAG_BASE"
      IS_PRERELEASE="false"
    fi
    ;;
  workflow_dispatch)
    if [[ -n "$VERSION_SUFFIX" ]]; then
      # #925 cross-vendor review finding 10: VERSION_SUFFIX comes directly from the
      # workflow_dispatch `version_suffix` input and, before this check, was never
      # validated at all before being embedded in the `version=${VERSION}` line this
      # script writes to $GITHUB_OUTPUT. A value containing a literal newline (e.g.
      # "beta.1\nversion=9.9.9-evil") would produce a SECOND `version=` record in that
      # file -- a classic GitHub/Gitea Actions output-file injection, letting whichever
      # `key=value` line a workflow reads LAST win. The tag-triggered path above already
      # only accepts a suffix matched by a whole-string-anchored regex; this applies the
      # identical semver pre-release charset to the dispatch input. `[0-9A-Za-z.-]`
      # cannot contain a newline or any other control character, so this closes the
      # injection vector by construction, not by trying to enumerate "dangerous" chars.
      if ! [[ "$VERSION_SUFFIX" =~ ^[0-9A-Za-z][0-9A-Za-z.-]*$ ]]; then
        fail "version_suffix '$VERSION_SUFFIX' must start with a letter or digit and contain only [0-9A-Za-z.-] -- refusing to embed an unvalidated value in \$GITHUB_OUTPUT"
      fi
      VERSION="${BASE_VERSION}-${VERSION_SUFFIX}"
      IS_PRERELEASE="true"
    else
      VERSION="$BASE_VERSION"
      IS_PRERELEASE="false"
    fi
    ;;
  *)
    fail "unknown EVENT_NAME '$EVENT_NAME' (expected 'push' or 'workflow_dispatch')"
    ;;
esac

# Stable releases only: the CHANGELOG must already carry a dated heading for the exact
# version being published, and it must be the NEWEST heading in the file -- not merely
# present somewhere further down. This is the same invariant
# .claude/commands/wtm-release-check.md's human/agent gate already checks manually
# ("grep -m1 '^## \[' CHANGELOG.md" must equal version.props); this makes it a hard,
# automated precondition of the publish itself instead of a step someone can forget.
#
# Deliberately NOT enforced for a pre-release/RC (IS_PRERELEASE=true): an RC exists to
# let unreleased work be test-installed BEFORE the CHANGELOG entry is finalized and dated
# -- that is the whole point of cutting one, and is explicitly the workflow_dispatch RC
# path's contract (see publish-nuget.yml). Requiring a dated heading for every RC would
# make RCs impossible to cut from an in-progress [Unreleased] block, which is the only
# realistic time anyone wants one.
if [[ "$IS_PRERELEASE" == "false" ]]; then
  NEWEST_HEADING="$(grep -m1 '^## \[' "$CHANGELOG_FILE" || true)"
  if [[ -z "$NEWEST_HEADING" ]]; then
    fail "$CHANGELOG_FILE has no '## [' version heading at all"
  fi
  ESCAPED_VERSION="${VERSION//./\\.}"
  if [[ "$NEWEST_HEADING" =~ ^\#\#\ \[${ESCAPED_VERSION}\]\ -\ ([0-9]{4}-[0-9]{2}-[0-9]{2})[[:space:]]*$ ]]; then
    HEADING_DATE="${BASH_REMATCH[1]}"
  else
    fail "$CHANGELOG_FILE's newest heading ('$NEWEST_HEADING') is not a dated '## [$VERSION] - YYYY-MM-DD' entry -- publishing a stable release requires the CHANGELOG to already document it. If work for $VERSION is still under '## [Unreleased]', finalize that section (split it if it also contains content for other versions) before tagging."
  fi
  # #925 cross-vendor review finding 10: the regex above only checks the DIGIT SHAPE of
  # the date (4-2-2 digits) -- it accepted "## [$VERSION] - 2026-13-99" as a valid
  # heading. Validate it is an actual calendar date via Python's datetime (portable
  # across the Linux CI runner and a macOS dev shell, matching how this script already
  # parses version.props above rather than reaching for GNU-only `date -d`).
  if ! python3 - "$HEADING_DATE" <<'PY'
import datetime
import sys

y, m, d = sys.argv[1].split("-")
try:
    datetime.date(int(y), int(m), int(d))
except ValueError as exc:
    print(f"not a real calendar date: {exc}", file=sys.stderr)
    sys.exit(1)
PY
  then
    fail "$CHANGELOG_FILE's newest heading date '$HEADING_DATE' is not a real calendar date"
  fi
fi

echo "version=${VERSION}"
echo "is_prerelease=${IS_PRERELEASE}"
