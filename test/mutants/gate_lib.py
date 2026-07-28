#!/usr/bin/env python3
"""Shared helpers for .github/workflows/mutation-gate.yml (issue #855 defects 4 and 5).

Before this file existed, the workflow computed "which mutant/selftest ids should run"
and "did this PR touch anything the gate needs to protect" via THREE separate inline
`python3 -c "..."` blocks embedded directly in the YAML (one in the `changes` job, one in
`mutants`, one in `meta-selftest`). That logic was never imported, never unit tested, and
had already drifted out of sync with itself once (issue #834's history note in
mutation-gate.yml). This module is that logic, extracted so it can be exercised the exact
same way in test/mutants/_selftest/*.py that the workflow runs it in production --
`python3 test/mutants/gate_lib.py <subcommand>` is the ONLY thing either the workflow or a
selftest ever calls.

Entry loading is delegated to run_mutant.discover_entries(), the single source of truth
for what a "discovered" entry is (including issue #855 defect 4's kind validation) --
this module never re-implements that check, so it can never drift from it again.

Subcommands:
  ids --kind {security,selftest}   print matching entry ids, one per line
  selftests                        print "<id>\\t<expect_verdict>" for every selftest entry
  count                            print the total number of discovered entries
  relevant                         read changed file paths (one per line) from stdin,
                                    print "true" or "false" (issue #855 defect 5)

Every subcommand exits non-zero with an error on stderr if entries cannot be loaded at
all (see run_mutant.discover_entries()) -- never silently prints an empty/partial result.
"""
from __future__ import annotations

import argparse
import glob
import re
import sys
from pathlib import Path

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

import run_mutant  # noqa: E402  (path must be adjusted first, see above)

# issue #855 defect 5: paths that are ALWAYS relevant regardless of any entry's own
# declarations -- the gate's own workflow file and everything under test/mutants/ (the
# runner, this module, every entry/patch/selftest fixture). A PR that only touches either
# of these must never compute relevant=false; that would let the gate skip evaluating a
# change to itself.
ALWAYS_RELEVANT_PREFIXES = ("test/mutants/",)
ALWAYS_RELEVANT_EXACT = {".github/workflows/mutation-gate.yml"}


def load_entries() -> list[dict]:
    """Discover every entry via run_mutant.discover_entries() -- the same loader
    run_mutant.py itself uses, kind validation (issue #855 defect 4) included, so this
    module's counts can never be more permissive than the runner's own."""
    repo_root = Path(
        __import__("subprocess")
        .run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True)
        .stdout.strip()
    )
    entries_dir = repo_root / "test" / "mutants" / "entries"
    return run_mutant.discover_entries(entries_dir)


def relevant_target_files(entries: list[dict]) -> set[str]:
    return {e["target_file"] for e in entries if e.get("target_file")}


def relevant_test_project_files(entries: list[dict]) -> set[str]:
    """issue #855 defect 5 (gap caught in code review): every entry's own
    `test_project` .csproj must also be treated as relevant -- a PR that edits ONLY the
    .csproj (e.g. a `<Compile Remove="...">` that silently excludes a declared red/green
    test's source file from the build, or drops the whole test project from the
    solution) would otherwise still compute relevant=false, since it touches neither a
    target_file nor any .cs file relevant_test_source_files() can see."""
    return {e["test_project"] for e in entries if e.get("test_project")}


def relevant_test_source_files(entries: list[dict], search_root: str = "test") -> set[str]:
    """issue #855 defect 5: every .cs file that DECLARES a class named in any entry's
    red_tests/green_tests must also be treated as relevant -- otherwise a PR that only
    edits the test file backing a red/green assertion (e.g. weakens the very test a
    mutant depends on) computes relevant=false and the gate skips itself.

    Matched by scanning source for `class <Name>` rather than assuming a <ClassName>.cs
    filename convention, so a future class-name/file-name mismatch does not silently
    drop coverage. Deliberately over-inclusive: if a class name happens to appear in more
    than one file, ALL of them are marked relevant -- the failure mode this guards
    against (an entry's test file changing without the gate noticing) is far more
    dangerous than running the gate on a few extra PRs.
    """
    class_names: set[str] = set()
    for e in entries:
        for fqn in (e.get("red_tests") or []) + (e.get("green_tests") or []):
            parts = fqn.split(".")
            if len(parts) >= 2:
                class_names.add(parts[-2])
    if not class_names:
        return set()

    files: set[str] = set()
    for path in glob.glob(f"{search_root}/**/*.cs", recursive=True):
        try:
            with open(path, encoding="utf-8", errors="ignore") as f:
                content = f.read()
        except OSError:
            continue
        for cls in class_names:
            if re.search(r"\bclass\s+" + re.escape(cls) + r"\b", content):
                files.add(path)
                break
    return files


def is_change_relevant(changed_files: list[str], entries: list[dict]) -> bool:
    changed = set(changed_files)
    if any(f.startswith(ALWAYS_RELEVANT_PREFIXES) for f in changed):
        return True
    if changed & ALWAYS_RELEVANT_EXACT:
        return True
    if changed & relevant_target_files(entries):
        return True
    if changed & relevant_test_source_files(entries):
        return True
    if changed & relevant_test_project_files(entries):
        return True
    return False


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_ids = sub.add_parser("ids", help="print entry ids for a given kind, one per line")
    p_ids.add_argument("--kind", required=True, choices=sorted(run_mutant.VALID_KINDS))

    sub.add_parser("selftests", help="print '<id>\\t<expect_verdict>' for every selftest entry")
    sub.add_parser("count", help="print the total number of discovered entries")
    sub.add_parser(
        "relevant",
        help="read changed file paths (one per line) from stdin, print true/false",
    )

    args = parser.parse_args(argv)

    try:
        entries = load_entries()
    except SystemExit as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

    if args.cmd == "ids":
        for e in entries:
            if e.get("kind", "security") == args.kind:
                print(e["id"])
        return 0

    if args.cmd == "selftests":
        for e in entries:
            if e.get("kind", "security") != "selftest":
                continue
            expect_verdict = e.get("expect_verdict")
            if not expect_verdict:
                print(
                    f"ERROR: selftest entry '{e['id']}' has no expect_verdict field.",
                    file=sys.stderr,
                )
                return 1
            print(f"{e['id']}\t{expect_verdict}")
        return 0

    if args.cmd == "count":
        print(len(entries))
        return 0

    if args.cmd == "relevant":
        changed = [line.strip() for line in sys.stdin if line.strip()]
        print("true" if is_change_relevant(changed, entries) else "false")
        return 0

    return 1


if __name__ == "__main__":
    sys.exit(main())
