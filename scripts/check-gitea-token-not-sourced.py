#!/usr/bin/env python3
"""#924 CI guard: fail the build if any tracked file contains a lexical
instance of `source` or `.` applied to a path containing "gitea-token",
outside of a backtick-delimited inline code span.

WHY: sourcing a bare-token file makes the shell execute the token as a
command; the resulting "command not found: <token>" prints the plaintext
token to stdout/CI logs. That is how a token leaked on 2026-07-11. Every site
that reads .local-token-file must extract it with scripts/resolve-gitea-token.py
(grep-based, never `source`s the file) instead.

RULE: for each tracked file, for each line, strip every backtick-delimited
inline code span (`` `...` ``), then check the remainder for `source` (as a
whole word) or a standalone `.` (surrounded by whitespace/line-boundaries, so
it can't match the dot inside ".local-token-file" itself) followed by whitespace
and a token containing "gitea-token". There is no path-based exception -- an
earlier version of this guard exempted two specific docs by file path, which
was a blind spot: a real `source .local-token-file` added to either file later
would never be scanned again, and neither file's PR would trigger ci-build.yml
either (paths-ignore covers .claude/**). Requiring backticks instead makes the
exemption apply uniformly, including to files that don't exist yet: wrap the
literal command in backticks when writing prose about it, and it is excluded
by construction; write it un-backticked next to a real gitea-token path
anywhere in the tree, and it is flagged.

SCOPE, STATED PRECISELY (see #924 review, findings 1 and 2): this is a lexical
tripwire for the literal command, not a complete prohibition on ever reading
the file unsafely. It DOES catch `source`/`.` immediately preceding a
gitea-token path in any surrounding text -- start of line, after `;`/`&&`/a
keyword, inside a `bash -c '...'` string, inside a YAML `run:` step, inside a
comment in any language -- as long as it is not wrapped in backticks. It does
NOT catch indirection: a path held in a variable
(`f=.local-token-file; source "$f"`), a shell alias, string concatenation, or a
command name assembled at runtime. Closing that gap needs real shell parsing,
out of scope for a repo-wide text lint. It also does not catch a *real*
executing line that happens to be wrapped in backticks by mistake -- backticks
are trusted as the "this is prose, not code" signal, on the theory that nobody
backtick-wraps a line specifically to defeat a linter they don't know exists;
if that theory ever breaks in practice, tighten this rule then.

Exit codes: 0 clean, 1 violation(s) found (printed above the summary line),
2 scanner error (couldn't list or read tracked files) -- distinct from 1 so a
scanner failure is never silently reported as a clean tree.
"""
import re
import subprocess
import sys

SOURCE_RE = re.compile(r'\bsource\b[ \t]+\S*gitea-token')
DOT_RE = re.compile(r'(?:^|[ \t])\.[ \t]+\S*gitea-token')
BACKTICK_SPAN_RE = re.compile(r'`[^`\n]*`')

SCANNER_ERROR = 2


def is_binary(data: bytes) -> bool:
    # Same NUL-in-first-chunk heuristic as `git grep -I` and .sync/'s own
    # leak-gate (see .sync/README.md) -- keeps this guard from being defeated
    # by hiding the pattern in a file type nobody thought to exempt/lint.
    return b"\x00" in data[:8192]


def list_tracked_files():
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        check=True,
        capture_output=True,
    )
    return [p for p in result.stdout.split(b"\x00") if p]


def line_is_violation(line: str) -> bool:
    stripped = BACKTICK_SPAN_RE.sub("", line)
    return bool(SOURCE_RE.search(stripped) or DOT_RE.search(stripped))


def scan_file(path_bytes: bytes):
    import os
    path = os.fsdecode(path_bytes)
    with open(path, "rb") as fh:
        data = fh.read()
    if is_binary(data):
        return []
    text = data.decode("utf-8", errors="replace")
    return [
        (path, lineno, line)
        for lineno, line in enumerate(text.splitlines(), start=1)
        if line_is_violation(line)
    ]


def main() -> int:
    try:
        files = list_tracked_files()
    except (subprocess.CalledProcessError, OSError) as exc:
        print(f"::error::guard scanner failed to list tracked files: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    violations = []
    try:
        for path_bytes in files:
            violations.extend(scan_file(path_bytes))
    except OSError as exc:
        print(f"::error::guard scanner failed while reading a tracked file: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    if violations:
        for path, lineno, line in violations:
            print(f"{path}:{lineno}:{line}")
        print(
            "::error::Found a command that sources/dot-executes a gitea-token "
            "file (shown above), outside backticks. Read the token with "
            "scripts/resolve-gitea-token.py instead -- see "
            "docs/ci-operations.md. If this is prose describing the "
            "forbidden command, wrap it in backticks.",
            file=sys.stderr,
        )
        return 1

    print("OK: no tracked file sources/dot-executes a gitea-token file outside backticks.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
