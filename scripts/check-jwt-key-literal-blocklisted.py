#!/usr/bin/env python3
"""#931 CI guard: fail the build if a fixed JWT signing-key literal, long enough to pass
JwtOption.IsWeakSigningKey's length floor on its own, appears anywhere in the tree without
being in JwtOption.KnownPublicKeys.

WHY: #923 fixed the demo appsettings.json values that shipped this way; cross-vendor
review of #923 itself (#931) found four more fixed literals still in the tree, three of
them added BY #923's own commits, and a repo-wide sweep for the same pattern found three
more pre-existing ones (this repo's own security posture: a confirmed issue class gets a
full-codebase sweep, not just the reported instances -- see CLAUDE.md). `test/` is NOT
excluded from the public GitHub mirror sync (.sync/github-excludes.txt), so a test-only
literal is exactly as public as a demo config value. This guard exists so the next one
gets caught by CI instead of by the next review round.

RULE: for each tracked text file, for each line, find every occurrence of the literal text
"SecurityKey" followed by optional whitespace, an optional closing quote (the JSON key
form, `"SecurityKey": "..."`), `=` or `:`, optional whitespace, and a double-quoted string
-- i.e. `SecurityKey"?\\s*[=:]\\s*"([^"]*)"`. This covers a direct C# property/object-initializer
assignment (`SecurityKey = "..."`, `.SecurityKey = "..."`) and a JSON key-value pair
(`"SecurityKey": "..."`) uniformly, including an identifier that merely ENDS in
"SecurityKey" (e.g. `TestSecurityKey = "..."`), since the substring search does not anchor
on a word boundary before the match.

For each captured literal that is 32 UTF-8 bytes or longer (matching or exceeding
JwtOption.IsWeakSigningKey's own length floor -- a SHORTER literal is already caught by
that length check regardless of blocklist membership, so it is not this guard's concern),
the value (trimmed, compared case-insensitively -- matching JwtOption.KnownPublicKeys'
own OrdinalIgnoreCase HashSet) must appear in the SAME blocklist JwtOption.cs itself uses.
That blocklist is extracted directly from JwtOptions.cs's own `baseKeys` array at scan
time -- a second, hand-maintained Python copy of the same list is exactly the drift risk
this repo's own mutation-gate tooling was rewritten to avoid (issue #855 defect 5: "a
second, hand-copied inline python block"), so there is deliberately only one source of
truth here, not two.

SCOPE, STATED PRECISELY: this is a lexical tripwire for a DIRECT literal assignment to
something named (or ending in) "SecurityKey", not a complete prohibition on ever using a
fixed 32+-byte string anywhere near JWT code. It does NOT catch a literal assigned to an
intermediate variable and used indirectly (`const string myKey = "..."; opt.SecurityKey =
myKey;`) -- closing that gap needs either a much broader "any 32+-byte string constant"
scan (which is far too noisy: it would flag every unrelated GUID/connection-string/test-
fixture literal in the whole tree, including deliberately-not-blocklisted boundary-value
test constants like JwtOptionTests.cs's exactly-32-byte positive control) or real static
data-flow analysis, both out of scope for a repo-wide text lint. This mirrors the accepted,
documented scope boundary of scripts/check-gitea-token-not-sourced.py (#924), which
similarly does not catch a token path held in a variable.

NOT-A-LITERAL EXCLUSION (added after this guard's first commit broke its own CI, #931
follow-up): the captured double-quoted string is not necessarily static text. A shell/CI/
template author routinely writes `KEY="$(some command)"` or `KEY="${VAR}"` where the quotes
delimit an EXPRESSION, not the runtime value -- the actual secret is never present in the
source at all. The guard's own docs/Dockerfile carried exactly this shape from the day this
guard was added: `JwtOptions__SecurityKey="$(cat /run/secrets/jwt_security_key)"` (a Docker-
secret injection example) is 36 UTF-8 bytes and is not in KnownPublicKeys, so the first
version of this guard flagged it -- a false positive against its own shipping commit,
found by CI on that exact commit. `INTERPOLATION_RE` below excludes a captured value
containing ANY of:
  - `$`               -- covers `$VAR`, `${VAR}` (POSIX/bash parameter expansion), and
                          `$(...)` (command substitution, e.g. the Dockerfile's own
                          `$(openssl rand -base64 32)` / `$(cat /run/secrets/...)`
                          examples -- CONFIRMED present in this repo today, the exact
                          case that broke CI). Also covers GitHub/internal CI'
                          `${{ secrets.X }}` expression syntax, which starts with `$`.
  - backtick (`` ` ``) -- legacy POSIX `sh` command substitution, the same semantic class
                          as `$(...)` (a subshell's stdout becomes the value) under older
                          shell-script convention. Not currently present in this repo's
                          SecurityKey-shaped lines, but this guard's own Dockerfile section
                          documents `sh`-compatible deployment shapes elsewhere in the
                          repo's docs, so a future POSIX-`sh` example is a realistic next
                          occurrence, not a hypothetical one.
  - `%VAR%`            -- Windows/cmd.exe environment-variable syntax (percent, one or
                          more identifier characters, percent -- deliberately word-shaped
                          so it cannot match a literal that merely starts and ends with a
                          stray `%`). Not currently present, but
                          `docs/wtm-developer-manual.md` documents IIS/Windows-hosted
                          deployment, so a future Windows injection example is plausible.
  - `{{`               -- template-engine interpolation opener (Helm `values.yaml`,
                          Jinja2, Ansible, Mustache -- `${{ }}` is already covered by the
                          leading `$` above, this catches the bare `{{ }}` form those
                          templating systems also use without a `$`). Not currently
                          present.
  - `<...>`            -- an angle-bracket placeholder token, this repo's own established
                          convention for "replace this yourself" (the Dockerfile's `<image>`
                          on the very same lines as the false-positive fix). Not currently
                          present inside a SecurityKey-shaped value, but the pattern is
                          already in active use one token to the right of every match this
                          guard would ever see in that file.
Verified against the CURRENT tree before this exclusion was added: running the bare
CANDIDATE_RE (no exclusion) across every tracked file finds exactly ONE match >= 32 bytes
that is not blocklisted -- the Dockerfile `$(cat ...)` line above -- and zero occurrences of
backtick, `%VAR%`, `{{`, or `<...>` inside any SecurityKey-shaped captured value anywhere in
the tree. The last four categories are added defensively, for documented reasons, not
because fixing today's false positive required them; `$` alone would have been sufficient
to fix the one real occurrence in this commit.

WHICH WAY A MIXED VALUE FALLS: if a captured value contains an interpolation marker
ANYWHERE in it, the ENTIRE value is treated as not-a-literal and skipped -- even if part of
the same string is static text (e.g. `"prefix-$(cmd)"`). Reasoning: (1) this guard's actual
purpose is to catch a value that IS the secret when read from source -- once any part of
the string is computed at runtime, reading the source does not hand an attacker a working
key, which is the harm this guard exists to prevent; a half-static prefix does not change
that. (2) In THIS codebase's real sinks (C# object initializers, JSON config), a string
containing literal `$`/backtick/`%`/`{{`/`<>` characters does not shell-expand or template-
render at runtime -- .NET does not interpret those as directives -- so every real occurrence
of this pattern in the tree today is prose/shell-example/doc context, never an actual
config value the app reads; there is no live C#/JSON case where "mostly literal, tagged
with a trailing `$` to dodge the guard" is a coherent attack against WTM's actual JWT
signing path. (3) This is an accepted, EXPLICITLY documented gap, matching the guard's own
existing indirection blind spot above: a deliberately obfuscated literal (a real secret
with a decorative `$` appended purely to evade this lint) would slip through. This guard is
a lexical tripwire for accidental disclosure, not a defense against a hostile committer
deliberately defeating a text lint -- the same limitation scripts/check-gitea-token-not-
sourced.py already accepts for its own indirection gap.

Exit codes: 0 clean, 1 violation(s) found (printed above the summary line), 2 scanner
error (couldn't list/read tracked files, or couldn't extract the blocklist from
JwtOptions.cs) -- distinct from 1 so a scanner failure is never silently reported as a
clean tree.
"""
import re
import subprocess
import sys

CANDIDATE_RE = re.compile(r'SecurityKey"?\s*[=:]\s*"([^"]*)"')
# See "NOT-A-LITERAL EXCLUSION" above for the derivation and reasoning behind each branch.
INTERPOLATION_RE = re.compile(r'\$|`|%[A-Za-z_]\w*%|\{\{|<[^<>]*>')
BLOCKLIST_SOURCE = "src/WalkingTec.Mvvm.Core/ConfigOptions/JwtOptions.cs"
BASE_KEYS_ARRAY_RE = re.compile(r'string\[\]\s*baseKeys\s*=\s*\{(.*?)\};', re.DOTALL)
STRING_LITERAL_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')
MIN_WEAK_LENGTH_BYTES = 32  # must match JwtOption.IsWeakSigningKey's own floor

SCANNER_ERROR = 2


def is_binary(data: bytes) -> bool:
    # Same NUL-in-first-chunk heuristic as `git grep -I`, .sync/'s own leak-gate, and
    # scripts/check-gitea-token-not-sourced.py.
    return b"\x00" in data[:8192]


def list_tracked_files():
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        check=True,
        capture_output=True,
    )
    return [p for p in result.stdout.split(b"\x00") if p]


def read_text_file(path_bytes: bytes):
    """Returns the decoded text, or None if the file is binary."""
    import os
    path = os.fsdecode(path_bytes)
    with open(path, "rb") as fh:
        data = fh.read()
    if is_binary(data):
        return None, path
    return data.decode("utf-8", errors="replace"), path


def load_blocklist(repo_root_relative_files_text: dict) -> set[str]:
    """Extracts JwtOption.KnownPublicKeys' base entries directly from JwtOptions.cs's own
    `baseKeys` array -- the single source of truth this guard cross-checks against, not a
    second hand-maintained copy. Returns the set of base strings (comparison must still
    trim + lowercase both sides, and separately check each string's own PadRight(32,'x')
    form, matching JwtOption.BuildKnownPublicKeys()'s own logic)."""
    text = repo_root_relative_files_text.get(BLOCKLIST_SOURCE)
    if text is None:
        raise RuntimeError(f"could not read {BLOCKLIST_SOURCE} to extract the blocklist")
    array_match = BASE_KEYS_ARRAY_RE.search(text)
    if not array_match:
        raise RuntimeError(
            f"could not find a `string[] baseKeys = {{ ... }};` array in {BLOCKLIST_SOURCE} "
            "-- has it been renamed or restructured? Update BASE_KEYS_ARRAY_RE to match."
        )
    literals = STRING_LITERAL_RE.findall(array_match.group(1))
    if not literals:
        raise RuntimeError(
            f"found the baseKeys array in {BLOCKLIST_SOURCE} but extracted zero string "
            "literals from it -- the extraction regex is not matching its actual contents."
        )
    return set(literals)


def is_blocklisted(value: str, base_blocklist: set[str]) -> bool:
    trimmed = value.strip().lower()
    for base in base_blocklist:
        if trimmed == base.lower():
            return True
        if trimmed == base.ljust(32, "x").lower():
            return True
    return False


def scan_file(text: str, path: str) -> list[tuple[str, int, str, str]]:
    violations = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        for m in CANDIDATE_RE.finditer(line):
            value = m.group(1)
            if len(value.encode("utf-8")) < MIN_WEAK_LENGTH_BYTES:
                continue  # already caught by IsWeakSigningKey's length guard regardless
            if INTERPOLATION_RE.search(value):
                continue  # not a literal -- see "NOT-A-LITERAL EXCLUSION" in the module docstring
            violations.append((path, lineno, line, value))
    return violations


def main() -> int:
    try:
        files = list_tracked_files()
    except (subprocess.CalledProcessError, OSError) as exc:
        print(f"::error::guard scanner failed to list tracked files: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    texts: dict[str, str] = {}
    try:
        for path_bytes in files:
            text, path = read_text_file(path_bytes)
            if text is not None:
                texts[path] = text
    except OSError as exc:
        print(f"::error::guard scanner failed while reading a tracked file: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    try:
        base_blocklist = load_blocklist(texts)
    except RuntimeError as exc:
        print(f"::error::guard scanner failed to load the blocklist: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    violations = []
    for path, text in texts.items():
        for path_, lineno, line, value in scan_file(text, path):
            if not is_blocklisted(value, base_blocklist):
                violations.append((path_, lineno, line, value))

    if violations:
        for path, lineno, line, value in violations:
            print(f"{path}:{lineno}:{line}")
        print(
            "::error::Found a fixed JWT signing-key literal (shown above), >= 32 UTF-8 "
            "bytes, that is not in JwtOption.KnownPublicKeys. Either generate the value at "
            "runtime instead (see test/WalkingTec.Mvvm.Core.Test/Security/JwtTestKeys.cs / "
            "TokenTestFixture.GeneratedSecurityKey for the pattern), or -- if this value has "
            "already been committed and is therefore already public -- add it to "
            "JwtOptions.cs's baseKeys array so it is rejected everywhere, permanently.",
            file=sys.stderr,
        )
        return 1

    print("OK: no fixed JWT signing-key literal >= 32 bytes found outside JwtOption.KnownPublicKeys.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
