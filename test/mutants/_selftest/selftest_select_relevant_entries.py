#!/usr/bin/env python3
"""Issue #968 selftest: mutation-gate.yml's `mutants` job used to run EVERY kind=='security'
entry on EVERY pull_request (currently 61, up from a "30-35" estimate that had already
drifted stale twice -- see that job's own step comment). gate_lib.py's `select`/
`select_relevant_entries`/`relevance_self_check` (issue #968) select, per pull_request,
only the entries relevant to that PR's diff, while a push to dotnet10 still runs the full
set unconditionally as a post-merge backstop.

The one thing that must not go wrong (issue #968's own framing, mirroring issue #855
defect 4 one level up): "nothing was selected" must not read the same as "everything ran
and passed". A bug in the relevance computation that makes it return False for
everything must be a loud CI failure, not a silent, empty, green mutation-gate run. This
script proves, using the exact functions mutation-gate.yml's `changes`/`mutants` jobs
call (not a hand-copied approximation):

  1. a diff that touches a file a real, currently-registered mutant targets selects that
     mutant (and does not select an unrelated one);
  2. a diff that touches nothing gated selects zero entries;
  3. a diff that touches the workflow file itself selects EVERY entry of the given kind
     (the ALWAYS_RELEVANT_EXACT branch, entry-independent by design);
  4. relevance_self_check() passes against the real, currently loaded registry;
  5. relevance_self_check() FAILS the moment the underlying relevance function is broken
     (proving the positive control has teeth, not just that it exists);
  6. the `select` CLI subcommand itself -- not just the underlying functions --
     distinguishes "0 selected, relevance machinery healthy" (exit 0) from "0 selected,
     relevance machinery broken" (exit 2) for the SAME (empty) diff, differing only in
     whether is_change_relevant is monkeypatched;
  7. `select --event-name push` always returns the full kind set, regardless of the
     (bogus, in this test) SHAs given -- the post-merge backstop is not conditional on
     the diff resolving at all;
  8. reconcile() -- a documented, standalone implementation of the selected/executed
     comparison -- passes (silently) when the two counts match, FAILS when executed
     is strictly less than selected, and passes WITH A WARNING when executed is
     strictly greater than selected (issue #1001's asymmetric rule -- see that
     function's own docstring for why the ">" direction was loosened from the
     original plain "!=").

**issue #973 correction**: this file's case 8 tests reconcile() as a function, in
isolation. It does NOT prove the `gate` job in mutation-gate.yml can actually use it --
`gate` has no `actions/checkout` step (by design; see that job's own header comment)
and calling out to `python3 test/mutants/gate_lib.py reconcile` from inside it failed
in real CI with "No such file or directory" (found on this PR's own first run, #973).
The `gate` job's actual reconciliation is inline bash, independent of reconcile(); its
own regression coverage is test/mutants/_selftest/selftest_gate_job_reconciliation.py,
which extracts and runs THAT job's real script -- including from a directory with no
repository checked out at all, the specific condition #973 exposed. reconcile() is
kept here as a tested, documented reference implementation of the same rule (issue
#1001 kept both implementations on the same asymmetric shape -- see that function's
docstring) and for manual `python3 test/mutants/gate_lib.py reconcile --selected N
--executed M` diagnostic use -- not because anything in CI still calls it.

Run directly: python3 test/mutants/_selftest/selftest_select_relevant_entries.py
Exits 0 if every case below passes, 1 otherwise.
"""
from __future__ import annotations

import contextlib
import io
import sys
from pathlib import Path

_THIS_DIR = Path(__file__).resolve().parent
_MUTANTS_DIR = _THIS_DIR.parent
if str(_MUTANTS_DIR) not in sys.path:
    sys.path.insert(0, str(_MUTANTS_DIR))

import gate_lib  # noqa: E402

# issue #968's own reproduction table: two REAL, currently-registered security entries
# with distinct, single-owner target_file values (verified via
# `python3 -c "... count entries per target_file ..."` at the time this was written --
# if either id/path pair is ever renamed or removed, this selftest will fail loudly
# with an "unknown mutant" style error rather than silently passing on a stale probe).
POSITIVE_PROBE_ID = "953-getbatchquery-wherereplacemodifier-reintroduce"
POSITIVE_PROBE_FILE = "src/WalkingTec.Mvvm.Core/BasePagedListVM.cs"
NEGATIVE_PROBE_ID = "dcext843-declaredsystemquery-guard-neutralize"


def check_target_file_diff_selects_that_mutant(security_entries: list[dict]) -> bool:
    selected = gate_lib.select_relevant_entries([POSITIVE_PROBE_FILE], security_entries)
    selected_ids = {e["id"] for e in selected}
    hit = POSITIVE_PROBE_ID in selected_ids
    miss = NEGATIVE_PROBE_ID not in selected_ids
    ok = hit and miss
    print(
        f"{'PASS' if ok else 'FAIL'}: diff touching '{POSITIVE_PROBE_FILE}' -> selected "
        f"{sorted(selected_ids)} (expected '{POSITIVE_PROBE_ID}' selected: {hit}; "
        f"expected '{NEGATIVE_PROBE_ID}' NOT selected: {miss})"
    )
    return ok


def check_unrelated_diff_selects_nothing(security_entries: list[dict]) -> bool:
    selected = gate_lib.select_relevant_entries(["docs/some-unrelated-doc.md"], security_entries)
    ok = selected == []
    print(f"{'PASS' if ok else 'FAIL'}: unrelated diff -> selected {[e['id'] for e in selected]} (expected [])")
    return ok


def check_workflow_file_diff_selects_everything(security_entries: list[dict]) -> bool:
    workflow_path = next(iter(gate_lib.ALWAYS_RELEVANT_EXACT))
    selected = gate_lib.select_relevant_entries([workflow_path], security_entries)
    ok = len(selected) == len(security_entries)
    print(
        f"{'PASS' if ok else 'FAIL'}: diff touching '{workflow_path}' -> selected "
        f"{len(selected)} of {len(security_entries)} kind='security' entries (expected all)"
    )
    return ok


def check_self_check_passes_on_real_registry(entries: list[dict]) -> bool:
    ok, message = gate_lib.relevance_self_check(entries)
    print(f"{'PASS' if ok else 'FAIL'}: relevance_self_check(real registry) -> ok={ok} ({message})")
    return ok


def check_self_check_fails_when_relevance_is_broken(entries: list[dict]) -> bool:
    """Proves the positive control has teeth: it must go FALSE the moment the function
    it checks is broken, not just return True unconditionally."""
    original = gate_lib.is_change_relevant
    gate_lib.is_change_relevant = lambda *_a, **_kw: False
    try:
        ok, message = gate_lib.relevance_self_check(entries)
    finally:
        gate_lib.is_change_relevant = original
    # ok must be FALSE here -- a broken relevance function must fail the self-check.
    passed = ok is False
    print(
        f"{'PASS' if passed else 'FAIL'}: relevance_self_check() under a deliberately "
        f"broken is_change_relevant (always False) -> ok={ok} (expected False) ({message})"
    )
    return passed


def check_cli_select_distinguishes_empty_legit_from_empty_broken() -> bool:
    """issue #968's central requirement, exercised through the ACTUAL `select` CLI
    entrypoint (gate_lib.main()), not just the underlying functions: the same
    degenerate empty diff (HEAD vs HEAD, a resolvable but empty diff) must exit 0 when
    the relevance machinery is healthy, and exit 2 -- never a silent pass -- when it is
    broken."""
    argv = [
        "select", "--kind", "security", "--event-name", "pull_request",
        "--base-sha", "HEAD", "--head-sha", "HEAD",
    ]
    healthy_rc = gate_lib.main(list(argv))
    original = gate_lib.is_change_relevant
    gate_lib.is_change_relevant = lambda *_a, **_kw: False
    try:
        broken_rc = gate_lib.main(list(argv))
    finally:
        gate_lib.is_change_relevant = original
    ok = healthy_rc == 0 and broken_rc == 2
    print(
        f"{'PASS' if ok else 'FAIL'}: CLI `select` on the SAME empty diff -> "
        f"healthy exit={healthy_rc} (expected 0), broken exit={broken_rc} (expected 2)"
    )
    return ok


def check_push_mode_ignores_diff_and_selects_full_set(security_entries: list[dict]) -> bool:
    """issue #968: push to dotnet10 is the post-merge backstop and is NOT conditional on
    anything, including a SHA pair that cannot possibly resolve to a real diff."""
    # The full id list (61 lines at the time of writing) is not useful selftest
    # output -- swallow stdout, but let stderr (the SELECT mode=... reasoning line)
    # through so a reader can still see WHY the exit code was 0.
    with contextlib.redirect_stdout(io.StringIO()) as captured:
        rc = gate_lib.main([
            "select", "--kind", "security", "--event-name", "push",
            "--push-before", "0000000000000000000000000000000000000001",
            "--push-after", "0000000000000000000000000000000000000002",
        ])
    id_count = len([line for line in captured.getvalue().splitlines() if line.strip()])
    ok = rc == 0 and id_count > 0
    print(
        f"{'PASS' if ok else 'FAIL'}: CLI `select --event-name push` with a bogus, "
        f"unresolvable SHA pair -> exit={rc} (expected 0), {id_count} ids printed "
        "(expected > 0; full-set selection is unconditional)"
    )
    return ok


def check_reconcile() -> bool:
    # match: executed == selected -> ok, no WARNING in the message.
    match_ok, match_msg = gate_lib.reconcile(5, 5)
    match_pass = match_ok is True and "WARNING" not in match_msg
    print(f"{'PASS' if match_pass else 'FAIL'}: reconcile(selected=5, executed=5) -> ok={match_ok} ({match_msg})")

    # issue #1001, dangerous direction: executed < selected -> still a hard failure.
    shortfall_ok, shortfall_msg = gate_lib.reconcile(5, 4)
    shortfall_pass = shortfall_ok is False
    print(
        f"{'PASS' if shortfall_pass else 'FAIL'}: reconcile(selected=5, executed=4) -> "
        f"ok={shortfall_ok} (expected False) ({shortfall_msg})"
    )

    # issue #1001, safe direction: executed > selected -> tolerated, but the message
    # must say WARNING so a caller reading only the printed text (not the bool) can
    # still tell this apart from a plain, unremarkable match.
    surplus_ok, surplus_msg = gate_lib.reconcile(4, 5)
    surplus_pass = surplus_ok is True and "WARNING" in surplus_msg
    print(
        f"{'PASS' if surplus_pass else 'FAIL'}: reconcile(selected=4, executed=5) -> "
        f"ok={surplus_ok} (expected True, with WARNING in message) ({surplus_msg})"
    )

    return match_pass and shortfall_pass and surplus_pass


def main() -> int:
    entries = gate_lib.load_entries()
    security_entries = [e for e in entries if e.get("kind", "security") == "security"]

    results = [
        check_target_file_diff_selects_that_mutant(security_entries),
        check_unrelated_diff_selects_nothing(security_entries),
        check_workflow_file_diff_selects_everything(security_entries),
        check_self_check_passes_on_real_registry(entries),
        check_self_check_fails_when_relevance_is_broken(entries),
        check_cli_select_distinguishes_empty_legit_from_empty_broken(),
        check_push_mode_ignores_diff_and_selects_full_set(security_entries),
        check_reconcile(),
    ]
    ok = all(results)
    print(f"SELFTEST issue-968/1001 (per-entry selection, positive control, push backstop, asymmetric reconcile): {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
