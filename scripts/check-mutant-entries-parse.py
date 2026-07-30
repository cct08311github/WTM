#!/usr/bin/env python3
"""CI guard: validate every test/mutants/entries/*.json file the SAME way
test/mutants/run_mutant.py's own discover_entries() does, but report every broken file
at once instead of stopping at the first one.

WHY: discover_entries() raises SystemExit on the FIRST file that fails to (a) parse as
JSON, (b) declare a non-empty 'id', (c) have 'id' equal its own filename stem, or (d)
declare a 'kind' in VALID_KINDS. That is correct behaviour for the runner itself -- it
must refuse to proceed rather than silently drop a broken entry -- but it means the
mutation-gate CI run that discovers a malformed entry aborts before executing a single
mutant (#855 defect 4's own protection: refuse to report a pass without evaluating
anything). The failure is real and correctly blocks the gate; the diagnostics are not:
a contributor sees one file named in one error, fixes it, reruns, and discovers the
NEXT broken file one CI round later. This guard exists so every broken entry is named
in the same CI run, the first time.

CONCRETE INCIDENT this guard exists to catch earlier: two entries
(jwt923-devgen-rng-zeroing-neutralize.json, jwt923-rawkey-padding-bypass-reintroduce.json)
were committed containing `\\'`, an invalid JSON escape -- the 4-character bash idiom for
embedding a literal quote in a single-quoted shell string (`'\\''`) had been pasted
verbatim into a JSON string field instead of being reduced to the one character it
represents. `python3 -m json.tool` or `json.load()` reports "Invalid \\escape" for
exactly this shape. Both files were hand-edited after their content was last verified,
and the corruption went undetected until CI's mutation-gate 'mutants' job aborted with
zero mutants run.

RULE, mirroring discover_entries() exactly (VALID_KINDS is extracted from
run_mutant.py's own source at scan time -- a second, hand-maintained Python copy of
that set is exactly the drift risk this repo's tooling was already rewritten once to
avoid, see scripts/check-jwt-key-literal-blocklisted.py's identical reasoning for
JwtOption.KnownPublicKeys): for every test/mutants/entries/*.json file,
  1. it must parse as JSON;
  2. the parsed value must be a JSON object with a non-empty string 'id' field;
  3. that 'id' must equal the file's own name minus the '.json' suffix;
  4. it must have a 'kind' field (or omit it, defaulting to 'security', matching
     discover_entries()'s own `entry.get("kind", "security")`) whose value is one of
     VALID_KINDS.
Every file is checked independently; a failure in one does not stop the others from
being checked. All failures are collected and printed together before this script
exits.

SCOPE: this guard does NOT re-implement or duplicate any of run_mutant.py's PATCH or
TEST-EXECUTION validation (red_tests non-empty, assertion-pattern overbreadth, patch
scope, etc.) -- those require actually applying a patch and running dotnet test, which
is exactly what the expensive 'mutants'/'meta-selftest' jobs do downstream, gated on
this cheap job. This guard's only job is to make sure discover_entries() itself will
not abort on file #1 of N, hiding whatever is wrong with files #2..N.

Exit codes: 0 clean, 1 one or more entries invalid (each printed above the summary
line), 2 scanner error (couldn't list the entries directory, or couldn't extract
VALID_KINDS from run_mutant.py) -- distinct from 1 so a scanner failure is never
silently reported as a clean tree.
"""
import json
import re
import sys
from pathlib import Path

ENTRIES_DIR = Path("test/mutants/entries")
RUN_MUTANT_SOURCE = Path("test/mutants/run_mutant.py")
VALID_KINDS_RE = re.compile(r'VALID_KINDS\s*=\s*\{([^}]*)\}')
STRING_LITERAL_RE = re.compile(r'"([^"\\]*(?:\\.[^"\\]*)*)"')
DEFAULT_KIND = "security"  # matches discover_entries()'s entry.get("kind", "security")

SCANNER_ERROR = 2


def load_valid_kinds() -> set[str]:
    """Extracts VALID_KINDS directly from run_mutant.py's own source -- the single
    source of truth this guard cross-checks against, not a second hand-maintained
    copy (see module docstring)."""
    if not RUN_MUTANT_SOURCE.is_file():
        raise RuntimeError(f"could not find {RUN_MUTANT_SOURCE} to extract VALID_KINDS")
    text = RUN_MUTANT_SOURCE.read_text(encoding="utf-8")
    m = VALID_KINDS_RE.search(text)
    if not m:
        raise RuntimeError(
            f"could not find a `VALID_KINDS = {{ ... }}` assignment in {RUN_MUTANT_SOURCE} "
            "-- has it been renamed or restructured? Update VALID_KINDS_RE to match."
        )
    kinds = set(STRING_LITERAL_RE.findall(m.group(1)))
    if not kinds:
        raise RuntimeError(
            f"found VALID_KINDS in {RUN_MUTANT_SOURCE} but extracted zero string "
            "literals from it -- the extraction regex is not matching its actual contents."
        )
    return kinds


def check_entry(path: Path, valid_kinds: set[str]) -> list[str]:
    """Returns a list of problem descriptions for this one file (empty if it's valid).
    Mirrors discover_entries()'s four checks, but never raises/stops early."""
    problems: list[str] = []

    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        return [f"could not read file: {exc}"]

    try:
        entry = json.loads(text)
    except json.JSONDecodeError as exc:
        return [f"invalid JSON: {exc}"]

    if not isinstance(entry, dict):
        return [f"top-level JSON value is a {type(entry).__name__}, expected an object"]

    entry_id = entry.get("id")
    if not entry_id:
        problems.append("missing (or empty) 'id' field")
    elif entry_id != path.stem:
        problems.append(
            f"declares id '{entry_id}', but filename stem is '{path.stem}' -- "
            "the filename must equal the id"
        )

    kind = entry.get("kind", DEFAULT_KIND)
    if kind not in valid_kinds:
        problems.append(
            f"kind '{kind}' is not one of {sorted(valid_kinds)}"
        )

    return problems


def main() -> int:
    try:
        valid_kinds = load_valid_kinds()
    except RuntimeError as exc:
        print(f"::error::guard scanner failed to load VALID_KINDS: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    if not ENTRIES_DIR.is_dir():
        print(f"::error::guard scanner: '{ENTRIES_DIR}' does not exist", file=sys.stderr)
        return SCANNER_ERROR

    paths = sorted(ENTRIES_DIR.glob("*.json"))
    if not paths:
        print(
            f"::error::guard scanner: no entry files found under '{ENTRIES_DIR}' "
            "(glob '*.json' matched nothing) -- refusing to report a clean scan of "
            "nothing, matching discover_entries()'s own refusal.",
            file=sys.stderr,
        )
        return SCANNER_ERROR

    all_problems: dict[str, list[str]] = {}
    for path in paths:
        problems = check_entry(path, valid_kinds)
        if problems:
            all_problems[str(path)] = problems

    if all_problems:
        for path_str, problems in all_problems.items():
            for problem in problems:
                print(f"{path_str}: {problem}")
        print(
            f"::error::{len(all_problems)} of {len(paths)} mutant entry file(s) failed "
            "validation (shown above) -- test/mutants/run_mutant.py's discover_entries() "
            "will abort the ENTIRE mutation gate on the first one of these it loads, "
            "reporting zero mutants run rather than a false pass. Fix every file listed "
            "above, not just the first.",
            file=sys.stderr,
        )
        return 1

    print(f"OK: all {len(paths)} mutant entry file(s) under {ENTRIES_DIR} parse and validate.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
