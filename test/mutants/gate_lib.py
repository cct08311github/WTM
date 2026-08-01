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
                                    print "true" or "false" (issue #855 defect 5) --
                                    a coarse "would ANYTHING be relevant" check; kept for
                                    any other/diagnostic caller, no longer what
                                    mutation-gate.yml itself uses to decide what runs
                                    (see `select` below, issue #968)
  select --kind K --event-name E [--base-sha --head-sha --push-before --push-after]
                                    print the entry ids of kind K that should run for this
                                    event, one per line, to stdout; human-readable
                                    reasoning to stderr (issue #968). event-name=='push'
                                    unconditionally selects every kind-K entry (the
                                    post-merge backstop -- never filtered by relevance,
                                    not even when the diff resolves to "touches nothing").
                                    Any other event selects by PER-ENTRY relevance against
                                    the diff computed from the SHA arguments, falling back
                                    to the full set if that diff cannot be computed (same
                                    fail-open rule `relevant` always had). Exit 0 with a
                                    (possibly empty) id list normally; exit 2 if the
                                    selection computed empty AND the relevance machinery's
                                    own positive control (relevance_self_check()) also
                                    failed -- see that function's docstring. A caller MUST
                                    treat exit-2 as a hard failure, never as "0 selected,
                                    nothing to do": #968's entire point is that those two
                                    must never read the same.
  reconcile --selected N --executed M
                                    exit 0 if N == M (printing an OK line to stdout), exit
                                    1 otherwise (printing the mismatch to stderr) -- a
                                    documented, tested reference implementation of the
                                    "selected == executed" rule (issue #968, successor to
                                    issue #855 defect 4's original discovered-vs-executed
                                    check). NOT what mutation-gate.yml's `gate` job
                                    actually calls (issue #973: that job has no
                                    `actions/checkout` and never will -- see its own
                                    header comment -- so shelling out to this file from
                                    inside it fails with "No such file or directory";
                                    `gate`'s real reconciliation is inline bash, covered
                                    by test/mutants/_selftest/selftest_gate_job_reconciliation.py).
                                    Kept here for its own selftest coverage
                                    (test/mutants/_selftest/selftest_select_relevant_entries.py's
                                    check_reconcile()) and for manual diagnostic use.

Every subcommand exits non-zero with an error on stderr if entries cannot be loaded at
all (see run_mutant.discover_entries()) -- never silently prints an empty/partial result.
"""
from __future__ import annotations

import argparse
import glob
import re
import subprocess
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


# issue #968: entries grow linearly (30-35 -> 45 -> 61 in this registry's own history)
# and mutation-gate.yml's `mutants` job used to run EVERY kind=='security' entry on
# EVERY pull_request, so its own step-level timeout-minutes needed hand-recomputing
# every time the count grew -- and drifted stale twice already (see that step's own
# comment). The functions below select, PER PULL REQUEST, only the entries relevant to
# that PR's own diff, while a push to dotnet10 still runs the full set unconditionally
# as a post-merge backstop (mutation-gate.yml's `mutants` job, `select` CLI subcommand
# below). This does NOT introduce a second relevance implementation: every function
# here is built on is_change_relevant() -- calling it with a ONE-entry list scopes its
# target_file/test_source/test_project checks to that single entry, while its
# ALWAYS_RELEVANT_* checks stay entry-independent, so a change to the workflow file or
# anything under test/mutants/ still selects every entry, exactly as it always has.


def select_relevant_entries(changed_files: list[str], entries: list[dict]) -> list[dict]:
    """issue #968: PER-ENTRY relevance selection. Reuses is_change_relevant() -- see the
    module-level comment above this function -- so there is exactly one implementation
    of "is this file relevant to this entry", not a coarser aggregate one (is_change_relevant
    over the whole registry) and a separate finer per-entry one that could drift apart."""
    return [e for e in entries if is_change_relevant(changed_files, [e])]


def relevance_self_check(entries: list[dict]) -> tuple[bool, str]:
    """issue #968: a machine-checked POSITIVE CONTROL, run whenever a real selection
    computes EMPTY. "0 selected" must never read the same as "everything ran and
    passed" (issue #855 defect 4's own concern, one level up: an entry that exists but
    was never executed must never be a silent pass; here, an entry that SHOULD have
    been selected but wasn't must equally never be a silent pass) -- so an empty result
    is only trusted once this proves the relevance machinery can still return True for
    a case it MUST return True for.

    Exercises two of is_change_relevant()'s three branches against the REAL, currently
    loaded registry (not a synthetic fixture that can drift from it):
      (a) the ALWAYS_RELEVANT_EXACT branch, via the workflow file's own path;
      (b) the target_file branch, via the first discovered entry's own declared
          target_file.

    Does NOT exercise the test_source/test_project branches -- proving those is
    test/mutants/_selftest/selftest_relevance_covers_tests_and_workflow.py's job, wired
    into mutation-gate.yml's `changes` job as its own guard step so it runs once per CI
    invocation, not on every empty selection. STATED LIMITATION (see the #968
    CHANGELOG/production-readiness entries): a regression isolated to only those two
    untested branches, or one that breaks only SOME entries' target_file matching while
    a DIFFERENT entry's still works, is not guaranteed to be caught here -- this check
    can only prove the machinery is not COMPLETELY broken, not that every entry's own
    relevance is individually correct.
    """
    workflow_path = next(iter(ALWAYS_RELEVANT_EXACT))
    if not is_change_relevant([workflow_path], entries):
        return False, (
            f"relevance self-check FAILED: is_change_relevant(['{workflow_path}'], entries) "
            "returned False, but this path is in ALWAYS_RELEVANT_EXACT and must always be "
            "True -- the relevance machinery itself appears broken"
        )
    if entries:
        probe = entries[0]
        target = probe.get("target_file")
        if target and not is_change_relevant([target], [probe]):
            return False, (
                f"relevance self-check FAILED: is_change_relevant(['{target}'], [only entry "
                f"'{probe['id']}']) returned False for that entry's OWN declared target_file, "
                "which must always be True -- the target_file branch of the relevance "
                "machinery appears broken"
            )
    return True, (
        "relevance self-check OK (ALWAYS_RELEVANT_EXACT and target_file branches both "
        "confirmed True against the real registry)"
    )


def resolve_changed_files(
    event_name: str, base_sha: str, head_sha: str, push_before: str, push_after: str
) -> list[str] | None:
    """Returns the changed file paths for this event, or None if they cannot be computed
    (a missing SHA, an unsupported event, or a git error). issue #968: centralizes the
    diff-availability logic mutation-gate.yml's `changes` job used to compute inline in
    bash -- now both `changes` (for its own selected/total accounting and log) and
    `mutants` (to independently re-derive the SAME selection it will actually run,
    rather than trusting a value passed across a job boundary on a runner this repo's
    own history says not to trust for anything more complex than a plain string output --
    see mutation-gate.yml's "NOTE ON SHAPE" comment) call this ONE function."""
    try:
        if event_name == "pull_request" and base_sha and head_sha:
            result = subprocess.run(
                ["git", "diff", "--name-only", base_sha, head_sha],
                capture_output=True, text=True, check=True,
            )
            return [line for line in result.stdout.splitlines() if line]
        if event_name == "push" and push_before and push_before != "0" * 40:
            result = subprocess.run(
                ["git", "diff", "--name-only", push_before, push_after],
                capture_output=True, text=True, check=True,
            )
            return [line for line in result.stdout.splitlines() if line]
    except (subprocess.CalledProcessError, OSError):
        return None
    return None


def reconcile(selected: int, executed: int) -> tuple[bool, str]:
    """issue #968 (successor to issue #855 defect 4's discovered-vs-executed check): a
    documented, tested reference implementation of the cross-check between what
    `changes` decided SHOULD run (selected) and what `mutants`/`meta-selftest`
    actually iterated (executed). issue #855 defect 4's original version compared
    TOTAL DISCOVERED entries against executed, which only worked because every
    discovered entry always ran; #968 makes running a SUBSET on a pull_request
    legitimate, so the comparison baseline had to move from "discovered" to
    "selected".

    issue #973: this function is NOT what mutation-gate.yml's `gate` job calls. It
    briefly was -- the `gate` job shelled out to `python3 test/mutants/gate_lib.py
    reconcile ...` -- but that job has no `actions/checkout` step and architecturally
    never will (it is the one job that must unconditionally post a status; see its own
    header comment), so the call failed in real CI with "No such file or directory"
    even though the actual selected/executed counts were fully reconciled (61/61).
    `gate`'s real reconciliation reverted to inline bash -- the same `!=` comparison,
    duplicated deliberately: unlike is_change_relevant() (real matching logic that
    could genuinely drift if reimplemented), this rule is a single comparison plus a
    message, so the duplication risk is negligible. The inline version's own
    regression coverage is
    test/mutants/_selftest/selftest_gate_job_reconciliation.py, which runs the `gate`
    job's actual script (extracted from this file's own YAML, not a copy) including
    from a directory with no repository checked out at all. This function is kept as
    a tested spec of the rule and for manual `--selected N --executed M` diagnostic
    use."""
    if selected != executed:
        return False, (
            f"entries selected ({selected}) != entries executed ({executed}) -- an entry "
            "was selected to run but not executed (or executed without having been "
            "selected). issue #968: this must never be a silent pass."
        )
    return True, f"entries selected ({selected}) == entries executed ({executed}) -- OK"


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

    p_select = sub.add_parser(
        "select",
        help=(
            "select entry ids for --kind by relevance to the diff computed from the "
            "event args (pull_request), or the full set unconditionally (push) -- "
            "issue #968"
        ),
    )
    p_select.add_argument("--kind", required=True, choices=sorted(run_mutant.VALID_KINDS))
    p_select.add_argument("--event-name", required=True)
    p_select.add_argument("--base-sha", default="")
    p_select.add_argument("--head-sha", default="")
    p_select.add_argument("--push-before", default="")
    p_select.add_argument("--push-after", default="")

    p_reconcile = sub.add_parser(
        "reconcile",
        help="exit 0 if --selected == --executed, 1 otherwise -- issue #968",
    )
    p_reconcile.add_argument("--selected", required=True, type=int)
    p_reconcile.add_argument("--executed", required=True, type=int)

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

    if args.cmd == "select":
        kind_entries = [e for e in entries if e.get("kind", "security") == args.kind]
        total = len(kind_entries)

        if args.event_name == "push":
            # issue #968: the post-merge backstop. Genuinely not conditional on
            # anything -- this branch does not call resolve_changed_files() at all,
            # so there is no diff for a git error or a "touches nothing" result to
            # skip on. Always the full kind==args.kind set, every push to dotnet10.
            for e in kind_entries:
                print(e["id"])
            print(
                f"SELECT mode=push-full: selecting the FULL {total}-entry "
                f"kind='{args.kind}' set, unconditionally (issue #968 post-merge "
                "backstop -- the diff was never even computed for this decision).",
                file=sys.stderr,
            )
            return 0

        changed = resolve_changed_files(
            args.event_name, args.base_sha, args.head_sha, args.push_before, args.push_after
        )
        if changed is not None:
            print(f"Changed files ({args.event_name}):", file=sys.stderr)
            for f in changed:
                print(f"  - {f}", file=sys.stderr)
        else:
            print(
                f"Could not compute a path diff for event '{args.event_name}' (missing "
                "SHA, unsupported event, or a git error).",
                file=sys.stderr,
            )

        if changed is None:
            # issue #855's original fail-open rule, preserved: a diff that cannot be
            # computed defaults to the safe, expensive answer (run everything), never
            # to the cheap, unsafe one.
            for e in kind_entries:
                print(e["id"])
            print(
                f"SELECT mode=fallback-full: selecting the FULL {total}-entry "
                f"kind='{args.kind}' set (diff unavailable -- fail open toward the "
                "safe, expensive answer).",
                file=sys.stderr,
            )
            return 0

        selected = select_relevant_entries(changed, kind_entries)
        if selected:
            for e in selected:
                print(e["id"])
            print(
                f"SELECT mode=relevance: {len(selected)} of {total} kind='{args.kind}' "
                "entries selected for this diff.",
                file=sys.stderr,
            )
            return 0

        # issue #968: the empty-selection case that must not silently read the same as
        # "everything ran and passed". Before trusting "genuinely nothing relevant",
        # prove the relevance machinery itself still works.
        ok, message = relevance_self_check(entries)
        if not ok:
            print(f"ERROR: {message}", file=sys.stderr)
            print(
                f"SELECT mode=relevance: refusing to report a legitimate zero-selection "
                f"for kind='{args.kind}' -- the positive control above failed, so an "
                "empty result cannot be trusted to mean 'nothing relevant' rather than "
                "'relevance computation is broken'. issue #968.",
                file=sys.stderr,
            )
            return 2
        print(
            f"SELECT mode=relevance: 0 of {total} kind='{args.kind}' entries selected "
            f"-- {message}.",
            file=sys.stderr,
        )
        return 0

    if args.cmd == "reconcile":
        ok, message = reconcile(args.selected, args.executed)
        if ok:
            print(message)
            return 0
        print(f"ERROR: {message}", file=sys.stderr)
        return 1

    return 1


if __name__ == "__main__":
    sys.exit(main())
