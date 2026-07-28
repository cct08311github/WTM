#!/usr/bin/env python3
"""Issue #855 defect 4 selftest: an entry with an unrecognized `kind` must be rejected at
load time, not silently loaded and then simply excluded from both CI jobs in
.github/workflows/mutation-gate.yml (the `mutants` job selects kind=='security',
`meta-selftest` selects kind=='selftest') while everything else stays green. This also
checks the belt-and-suspenders invariant the `gate` job in mutation-gate.yml enforces
after both of those jobs run for real: entries discovered must equal entries executed.

This is a standalone script, not a `--mutant <id>` fixture like the other issue #855
selftests -- the bug it guards against is about the LOADER (discover_entries(), used by
both run_mutant.py and test/mutants/gate_lib.py) seeing every entry in the registry, not
about any single mutant's own verdict, so it does not fit the --mutant/--expect-verdict
shape.

Run directly:
    python3 test/mutants/_selftest/selftest_kind_validation_and_accounting.py

Exits 0 if every check below passes, 1 otherwise (mirrors run_mutant.py's PASS/FAIL
style). Never touches the real test/mutants/entries/ registry -- the load-time check
below uses its own isolated temp directory.
"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

_THIS_DIR = Path(__file__).resolve().parent
_MUTANTS_DIR = _THIS_DIR.parent
if str(_MUTANTS_DIR) not in sys.path:
    sys.path.insert(0, str(_MUTANTS_DIR))

import run_mutant  # noqa: E402

MINIMAL_GOOD_ENTRY = {
    "id": "good-entry",
    "kind": "security",
    "target_file": "irrelevant/for/this/check.cs",
    "patch": "irrelevant.patch",
    "test_project": "irrelevant.csproj",
    "red_test_filter": "FullyQualifiedName=Irrelevant.Test",
    "red_tests": ["Irrelevant.Test"],
    "red_expected_assertion_patterns": {"Irrelevant.Test": "irrelevant"},
    "green_test_filter": "FullyQualifiedName=Irrelevant.Green",
    "green_tests": ["Irrelevant.Green"],
}


def _write_entry(entries_dir: Path, entry: dict) -> None:
    (entries_dir / f"{entry['id']}.json").write_text(json.dumps(entry), encoding="utf-8")


def check_unknown_kind_rejected_at_load_time() -> bool:
    """The load-time half of defect 4: discover_entries() must refuse to load a
    directory containing an entry whose kind is neither 'security' nor 'selftest',
    instead of loading it and letting it silently vanish from both CI jobs."""
    with tempfile.TemporaryDirectory() as tmp:
        entries_dir = Path(tmp) / "entries"
        entries_dir.mkdir()
        _write_entry(entries_dir, MINIMAL_GOOD_ENTRY)
        typo_entry = dict(MINIMAL_GOOD_ENTRY, id="typo-kind-entry", kind="scurity")  # deliberate typo
        _write_entry(entries_dir, typo_entry)

        try:
            entries = run_mutant.discover_entries(entries_dir)
        except SystemExit as e:
            message = str(e)
            ok = "typo-kind-entry" in message and "scurity" in message
            if ok:
                print(f"PASS: discover_entries() rejected kind='scurity' at load time: {message}")
            else:
                print(
                    "FAIL: discover_entries() raised SystemExit, but the message does "
                    f"not identify the bad entry id/kind: {message}"
                )
            return ok

        print(
            f"FAIL: discover_entries() returned {len(entries)} entries WITHOUT "
            "rejecting kind='scurity' -- issue #855 defect 4: this entry would now "
            "silently exist in the registry but never be selected by either CI job "
            "('mutants' selects kind=='security', 'meta-selftest' selects "
            "kind=='selftest'), while everything else stays green."
        )
        return False


def check_discovered_equals_executed_for_real_registry() -> bool:
    """The accounting half of defect 4: total entries discovered under the real
    test/mutants/entries/ must equal (# of kind=='security' ids) + (# of
    kind=='selftest' ids) -- the exact reconciliation .github/workflows/mutation-gate.yml's
    `gate` job performs after the `mutants` and `meta-selftest` jobs both run for real.
    Since discover_entries() now rejects any kind outside {security, selftest} at load
    time (the check above), this holds by construction whenever loading succeeds at all
    -- this check keeps proving that specific claim true against the real registry,
    rather than assuming it, so a future change to the kind/selection logic that
    reintroduces a gap gets caught here too, independent of the load-time check."""
    repo_root = Path(
        __import__("subprocess")
        .run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True)
        .stdout.strip()
    )
    entries = run_mutant.discover_entries(repo_root / "test" / "mutants" / "entries")
    total = len(entries)
    security_ids = [e["id"] for e in entries if e.get("kind", "security") == "security"]
    selftest_ids = [e["id"] for e in entries if e.get("kind", "security") == "selftest"]
    executed = len(security_ids) + len(selftest_ids)
    ok = total == executed
    print(
        f"{'PASS' if ok else 'FAIL'}: discovered={total} executed={executed} "
        f"(security={len(security_ids)} selftest={len(selftest_ids)})"
    )
    return ok


def main() -> int:
    results = [
        check_unknown_kind_rejected_at_load_time(),
        check_discovered_equals_executed_for_real_registry(),
    ]
    ok = all(results)
    print(f"SELFTEST defect-4 (unknown kind rejected / discovered-vs-executed): {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
