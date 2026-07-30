#!/usr/bin/env python3
"""Resolve the Gitea PAT for scripts/release-gitea-package.sh and
scripts/publish-to-gitea.sh.

Precedence: the GITEA_TOKEN environment variable, then the ~/.gitea-token file
(or $GITEA_TOKEN_FILE, used only by this repo's tests to avoid ever touching a
real token file). Never `source`s the file -- see issue #924 and
docs/ci-operations.md: sourcing a bare-token file makes the shell execute the
token as a command, and the resulting "command not found: <token>" error
prints the plaintext token to stdout/CI logs. That is how a token leaked on
2026-07-11.

Exit codes (the caller must treat these as three distinct outcomes, not two --
see review finding 5 on issue #924: collapsing "malformed" into "not set"
would hide a broken/tampered file behind the same generic message a normal
not-yet-configured machine gets):
  0 -- resolved. The 40-character lowercase-hex token is printed to stdout,
       nothing else is written there.
  1 -- not set. No GITEA_TOKEN env var, and the file is missing, empty, or has
       no line that looks like a token at all. This is the ordinary
       "nobody has configured a token here yet" state; the caller prints its
       own friendly setup hint.
  2 -- malformed. The env var or the file has *something*, but it fails
       validation: more than one candidate token line, a value that is not
       exactly 40 lowercase-hex characters (wrong length, wrong case, or
       truncated/garbled), or the file exists but could not be read. This is
       a state a human must fix, not a normal "not configured" state, so it
       is reported with a specific reason on stderr and never silently
       treated as "not set".

File format: a candidate token line is either a bare 40-character hex string
on its own line, or a `[export ]GITEA_TOKEN=<value>` assignment (value may be
single- or double-quoted). Blank lines and lines starting with `#` (after
stripping leading whitespace) are ignored, matching real shell comment
semantics -- so a commented-out old token next to a live assignment leaves
exactly one candidate, not two. Uppercase hex is deliberately NOT accepted:
Gitea tokens are conventionally lowercase, and this matches the same
convention already used by
test/WalkingTec.Mvvm.Core.Test/Security/ProductionReadinessBaselineDriftTests863.cs's
own `[a-f0-9]{40}` extraction. If the token file format changes to include
uppercase hex, this regex and that C# test must be updated together.
"""
import os
import re
import sys

ASSIGNMENT_RE = re.compile(r'^(?:export\s+)?GITEA_TOKEN=(.*)$')
BARE_CANDIDATE_RE = re.compile(r'^[0-9a-fA-F]+$')
VALID_TOKEN_RE = re.compile(r'^[a-f0-9]{40}$')

NOT_SET = 1
MALFORMED = 2


def _unquote(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in ('"', "'"):
        return value[1:-1]
    return value


def _find_candidates(text: str):
    """Return [(lineno, value, raw_line), ...] for every non-comment,
    non-blank line that looks like it was meant to carry the token."""
    candidates = []
    for lineno, raw_line in enumerate(text.splitlines(), start=1):
        line = raw_line.strip()
        if not line or line.startswith('#'):
            continue
        m = ASSIGNMENT_RE.match(line)
        if m:
            candidates.append((lineno, _unquote(m.group(1)), raw_line))
            continue
        if BARE_CANDIDATE_RE.match(line):
            candidates.append((lineno, line, raw_line))
    return candidates


def resolve_from_file(path: str):
    """Returns (exit_code, token_or_None). Never raises -- every failure mode
    is converted to (NOT_SET, None) or (MALFORMED, None) with a reason already
    printed to stderr for the MALFORMED case."""
    try:
        with open(path, 'rb') as fh:
            data = fh.read()
    except FileNotFoundError:
        return NOT_SET, None
    except OSError as exc:
        print(f"{path}: cannot read ({exc})", file=sys.stderr)
        return MALFORMED, None

    # errors='replace' rather than 'strict': a garbled/binary file must fail
    # validation below, not crash the resolver with a traceback.
    text = data.decode('utf-8', errors='replace')
    candidates = _find_candidates(text)

    if not candidates:
        return NOT_SET, None

    if len(candidates) > 1:
        lines = ', '.join(str(c[0]) for c in candidates)
        print(
            f"{path}: found {len(candidates)} candidate token lines "
            f"(lines {lines}) -- expected exactly 1; remove the extra line(s)",
            file=sys.stderr,
        )
        return MALFORMED, None

    lineno, value, raw_line = candidates[0]
    if not VALID_TOKEN_RE.match(value):
        print(
            f"{path}:{lineno}: not a 40-character lowercase-hex token "
            f"(got {len(value)} characters): {raw_line.strip()!r}",
            file=sys.stderr,
        )
        return MALFORMED, None

    return 0, value


def main() -> int:
    # The env var is passed through as-is, not validated against
    # VALID_TOKEN_RE: this matches the scripts' original behavior (any
    # non-empty GITEA_TOKEN was accepted verbatim), and the issue #924 review
    # findings that motivated the strict `^[a-f0-9]{40}$` check were all about
    # the *file* extractor's `grep -oE` behavior (multiple matches silently
    # concatenating, truncated over-long matches, comment lines not being
    # skipped) -- none of that applies to an env var, which is a single,
    # complete, caller-controlled string with no parsing step to get wrong.
    # Validating it too would be a new, unrequested constraint that could
    # reject a currently-working value nobody asked to change.
    env_token = os.environ.get('GITEA_TOKEN', '').strip()
    if env_token:
        print(env_token)
        return 0

    token_file = os.environ.get('GITEA_TOKEN_FILE') or os.path.expanduser('~/.gitea-token')
    code, token = resolve_from_file(token_file)
    if code == 0:
        print(token)
        return 0
    if code == NOT_SET:
        print(f"GITEA_TOKEN is not set and no usable token found in {token_file}", file=sys.stderr)
        return NOT_SET
    return MALFORMED  # resolve_from_file already printed the specific reason


if __name__ == '__main__':
    sys.exit(main())
