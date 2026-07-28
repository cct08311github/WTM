#!/usr/bin/env python3
"""Issue #855 defect 5 selftest: the `changes` job in
.github/workflows/mutation-gate.yml used to treat only each entry's declared
`target_file` and `test/mutants/**` as relevant. That meant a PR touching ONLY the .cs
file backing a declared red/green test (e.g. weakening MvcAuthHolesTests.cs), or ONLY
mutation-gate.yml itself, computed relevant=false: the `mutants`/`meta-selftest` jobs
were skipped, and the required check ('gate') still posted a plain success -- meaning
either the red tests or the gate's own workflow could be weakened under a green check.

test/mutants/gate_lib.py's is_change_relevant() (issue #855 fix) additionally covers
every file that DECLARES a class referenced by any entry's red_tests/green_tests, every
entry's test_project (.csproj -- caught in code review: a PR that only edits the
.csproj, e.g. a <Compile Remove="..."> excluding a declared test's source from the
build, is otherwise invisible to the class-name scan), and the workflow file itself.
This script proves all three of the previously-uncovered cases now compute
relevant=true, using the exact function the workflow's `changes` job actually calls
(`python3 test/mutants/gate_lib.py relevant`) -- not a hand-copied approximation.

Run directly: python3 test/mutants/_selftest/selftest_relevance_covers_tests_and_workflow.py
Exits 0 if every case below is relevant=true, 1 otherwise.
"""
from __future__ import annotations

import sys
from pathlib import Path

_THIS_DIR = Path(__file__).resolve().parent
_MUTANTS_DIR = _THIS_DIR.parent
if str(_MUTANTS_DIR) not in sys.path:
    sys.path.insert(0, str(_MUTANTS_DIR))

import gate_lib  # noqa: E402

# issue #855 defect 5's own reproduction table: a PR touching ONLY one of these paths
# used to compute relevant=false and skip the gate entirely.
CASES = {
    "a red-test source file (backs a declared red_tests FQN, but is not any entry's "
    "target_file)": "test/WalkingTec.Mvvm.Api.Test/MvcAuthHolesTests.cs",
    "the mutation-gate.yml workflow file itself": ".github/workflows/mutation-gate.yml",
    "a declared test_project .csproj (not any entry's target_file or a class-bearing "
    ".cs file)": "test/WalkingTec.Mvvm.Api.Test/WalkingTec.Mvvm.Api.Test.csproj",
}


def main() -> int:
    entries = gate_lib.load_entries()
    ok = True
    for description, path in CASES.items():
        relevant = gate_lib.is_change_relevant([path], entries)
        status = "PASS" if relevant else "FAIL"
        print(f"{status}: changed={{'{path}'}} ({description}) -> relevant={relevant}")
        ok = ok and relevant

    # Negative control: an unrelated file must still compute relevant=false, so this
    # selftest cannot be satisfied by an accidentally-always-true implementation.
    unrelated = "docs/some-unrelated-doc.md"
    relevant = gate_lib.is_change_relevant([unrelated], entries)
    status = "PASS" if not relevant else "FAIL"
    print(f"{status}: changed={{'{unrelated}'}} (negative control, unrelated doc) -> relevant={relevant}")
    ok = ok and not relevant

    print(f"SELFTEST defect-5 (relevance covers test files and the workflow itself): {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
