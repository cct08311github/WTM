#!/usr/bin/env python3
"""Issue #973 selftest: mutation-gate.yml's `gate` job has NO `actions/checkout` step,
by design -- see that job's own header comment ("INVARIANT (issue #973)"). Every step
in it must be pure bash/arithmetic over `needs.*.outputs`, never a reference to a
repo-relative file.

CONCRETE INCIDENT this guards against: issue #968's first version of the "selected vs
executed" reconciliation shelled out to `python3 test/mutants/gate_lib.py reconcile`
from inside the `gate` job's "Summarize mutation gate result" step. PR #973's own
first real CI run exposed it immediately: `changes`/`mutants`/`meta-selftest` all
reported success with a fully reconciled 61-selected/61-executed count, and `gate`
still failed --

    python3: can't open file '/workspace/chiu0831/WTM/test/mutants/gate_lib.py':
    [Errno 2] No such file or directory
    MUTATION_GATE_RESULT: FAIL

-- because the `gate` job never checks out the repository. Every local demonstration
built for #968 ran gate_lib.py directly (as a function call or via its own CLI), which
proves the SELECTION logic but says nothing about whether the `gate` job's own script
can execute in the environment it actually runs in. This selftest closes that
specific gap: it extracts the `gate` job's ACTUAL `run:` script from the live
.github/workflows/mutation-gate.yml (never a hand-copied text duplicate that could
drift from what CI really runs, and never a rewritten copy that could accidentally
"fix" a bug the real file still has) and executes it -- via the same
`bash --noprofile --norc -eo pipefail <script>` invocation GitHub Actions/internal infrastructure uses
for an unqualified `shell: bash` step -- under controlled env vars covering:

**Extraction method, and why (second round, #973 follow-up)**: this script's first
version used PyYAML to read the workflow file. That failed in real CI with
`ModuleNotFoundError: No module named 'yaml'` -- the `changes` job (where this
selftest itself runs, per mutation-gate.yml's own guard step) has no
`actions/setup-python`/`pip install` step, by the same deliberate design
scripts/audit-workflow-timeouts.py's own "NO PyYAML" module note already explains for
itself. Exactly the same failure CLASS as #973 one layer up: sound on the machine that
authored it, absent a dependency in the environment that runs it. Rather than write a
THIRD hand-rolled YAML-subset reader for this file family (scripts/audit-workflow-timeouts.py
already has one, purpose-built and cross-checked against a PyYAML reference), this
selftest imports that module directly (via `importlib`, since its filename is not a
valid Python identifier) and calls its `extract_run_body(workflow_path, job_id,
step_name)` -- a small addition to that module, composed entirely from its own
existing traversal, added specifically for this use. `extract_gate_summarize_script()`
below still reads the REAL file through that shared parser; it does not duplicate the
script under test, satisfying the same requirement the first version did, just without
the missing dependency. See both files' own docstrings for the full account.

  1. all three upstream jobs succeeded, has_selection=true, counts MATCH -> exit 0.
  2. same, but EXECUTED < SELECTED -> exit 1, naming both totals (issue #968/#1001:
     the dangerous direction -- something selected to run did not run -- stays a
     hard failure).
  3. same, but EXECUTED > SELECTED -> exit 0 WITH A PRINTED WARNING naming both
     totals (issue #1001, the new behaviour this selftest exists to prove: the safe
     direction -- more ran than believed required -- is tolerated, not fatal, but
     must not be silent either).
  4. has_selection=false, changes succeeded -> exit 0 (the legitimate "0 selected,
     nothing to reconcile" pass path, issue #968).
  5. the changes job itself failed -> exit 1, regardless of what the counts say.
  6. THE ACTUAL #973 REGRESSION TEST: scenario 1's env vars, executed with cwd set to
     an EMPTY temporary directory that does not contain a checkout of this repository
     at all (no .git, no test/mutants/, nothing). Before the #973 fix this reproduces
     the incident exactly (`No such file or directory`); after the fix it must still
     exit 0, because this job legitimately needs nothing from the tree.

Run directly: python3 test/mutants/_selftest/selftest_gate_job_reconciliation.py
Exits 0 if every case below passes, 1 otherwise.
"""
from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import tempfile
from pathlib import Path

_THIS_DIR = Path(__file__).resolve().parent
_REPO_ROOT = _THIS_DIR.parent.parent.parent
_WORKFLOW_PATH = _REPO_ROOT / ".github" / "workflows" / "mutation-gate.yml"
_AUDIT_SCRIPT_PATH = _REPO_ROOT / "scripts" / "audit-workflow-timeouts.py"
_JOB_ID = "gate"
_STEP_NAME = "Summarize mutation gate result"


def _load_audit_module():
    """Loads scripts/audit-workflow-timeouts.py by file path (via importlib, not a
    plain `import` -- its filename has hyphens, not a valid Python identifier).
    Any failure here (missing file, syntax error in that module, an unexpected
    import-time exception) is a precondition failure for THIS selftest, not a
    reason to let a raw traceback escape -- see main()'s own top-level handling."""
    if not _AUDIT_SCRIPT_PATH.is_file():
        raise RuntimeError(
            f"{_AUDIT_SCRIPT_PATH} does not exist -- has it moved or been renamed? "
            "This selftest depends on its extract_run_body() (issue #973)."
        )
    spec = importlib.util.spec_from_file_location(
        "audit_workflow_timeouts", _AUDIT_SCRIPT_PATH
    )
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not build an import spec for {_AUDIT_SCRIPT_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not hasattr(module, "extract_run_body"):
        raise RuntimeError(
            f"{_AUDIT_SCRIPT_PATH} loaded but has no extract_run_body() -- has it "
            "been refactored since this selftest was written? (issue #973)"
        )
    return module

# A "fully reconciled, everything succeeded" baseline -- each check below overrides
# only the specific variables its scenario needs to differ.
BASE_ENV = {
    "CHANGES_RESULT": "success",
    "HAS_SELECTION": "true",
    "SELECTED_SECURITY_COUNT": "61",
    "SELECTED_SELFTEST_COUNT": "6",
    "TOTAL_SECURITY_ENTRIES": "61",
    "MUTANTS_RESULT": "success",
    "SECURITY_COUNT": "61",
    "SELFTEST_RESULT": "success",
    "SELFTEST_COUNT": "6",
}


def extract_gate_summarize_script() -> str:
    """Loads the ACTUAL `run:` text of the `gate` job's summarize step from the live
    workflow file -- never a hand-copied duplicate, so this selftest can never drift
    from what CI actually executes and can never accidentally test a fixed-up copy of
    a bug the real file still has. Uses
    scripts/audit-workflow-timeouts.py's extract_run_body() (PyYAML-free, stdlib
    only -- see this file's own module docstring for why) rather than a third
    hand-rolled YAML-subset reader."""
    audit = _load_audit_module()
    if not _WORKFLOW_PATH.is_file():
        raise RuntimeError(f"{_WORKFLOW_PATH} does not exist")
    try:
        return audit.extract_run_body(_WORKFLOW_PATH, _JOB_ID, _STEP_NAME)
    except audit.WorkflowParseError as exc:
        raise RuntimeError(
            f"could not extract job='{_JOB_ID}' step='{_STEP_NAME}' from "
            f"{_WORKFLOW_PATH}: {exc}. Has the step been renamed or restructured? "
            "Update _JOB_ID/_STEP_NAME above to match."
        ) from exc


def run_script(script: str, env_overrides: dict, cwd: Path) -> subprocess.CompletedProcess:
    """Runs the extracted step body the same way GitHub Actions/internal infrastructure's act_runner
    does for an unqualified `shell: bash` step: `bash --noprofile --norc -eo pipefail
    <scriptfile>`. Written to a real temp file (not `bash -c "$script"`) for the
    closest possible fidelity to how the actual runner invokes it."""
    env = dict(os.environ)
    env.update(BASE_ENV)
    env.update(env_overrides)
    with tempfile.NamedTemporaryFile("w", suffix=".sh", delete=False) as f:
        f.write(script)
        script_path = f.name
    try:
        return subprocess.run(
            ["bash", "--noprofile", "--norc", "-eo", "pipefail", script_path],
            cwd=str(cwd),
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
        )
    finally:
        os.unlink(script_path)


def check_match_passes(script: str) -> bool:
    result = run_script(script, {}, cwd=_REPO_ROOT)
    ok = result.returncode == 0 and "MUTATION_GATE_RESULT: PASS" in result.stdout
    print(
        f"{'PASS' if ok else 'FAIL'}: matched counts (67 selected == 67 executed) -> "
        f"exit={result.returncode} (expected 0)"
    )
    if not ok:
        print("  stdout:", result.stdout, "  stderr:", result.stderr)
    return ok


def check_shortfall_fails(script: str) -> bool:
    # issue #1001: EXECUTED < SELECTED stays a hard failure -- the dangerous
    # direction (something selected to run did not run). SELECTED_TOTAL stays
    # 61+6=67; EXECUTED_TOTAL becomes 60+6=66.
    result = run_script(script, {"SECURITY_COUNT": "60"}, cwd=_REPO_ROOT)
    ok = (
        result.returncode == 1
        and "MUTATION_GATE_RESULT: FAIL" in result.stdout
        and "67" in result.stdout
        and "66" in result.stdout
    )
    print(
        f"{'PASS' if ok else 'FAIL'}: executed < selected (66 executed < 67 selected) "
        f"-> exit={result.returncode} (expected 1), totals named in output: "
        f"{'67' in result.stdout and '66' in result.stdout}"
    )
    if not ok:
        print("  stdout:", result.stdout, "  stderr:", result.stderr)
    return ok


def check_surplus_tolerated_with_warning(script: str) -> bool:
    # issue #1001, THE new behaviour this fix adds: EXECUTED > SELECTED is now the
    # SAFE direction -- tolerated as a PASS, but must still print a visible warning
    # naming both totals so a diverging selection is not silently invisible.
    # SELECTED_TOTAL stays 61+6=67; EXECUTED_TOTAL becomes 62+6=68.
    result = run_script(script, {"SECURITY_COUNT": "62"}, cwd=_REPO_ROOT)
    ok = (
        result.returncode == 0
        and "MUTATION_GATE_RESULT: PASS" in result.stdout
        and "WARNING" in result.stdout
        and "68" in result.stdout
        and "67" in result.stdout
    )
    print(
        f"{'PASS' if ok else 'FAIL'}: executed > selected (68 executed > 67 selected) "
        f"-> exit={result.returncode} (expected 0), WARNING printed: "
        f"{'WARNING' in result.stdout}, totals named in output: "
        f"{'68' in result.stdout and '67' in result.stdout}"
    )
    if not ok:
        print("  stdout:", result.stdout, "  stderr:", result.stderr)
    return ok


def check_zero_selected_legit_pass(script: str) -> bool:
    result = run_script(
        script,
        {
            "HAS_SELECTION": "false",
            "SELECTED_SECURITY_COUNT": "0",
            "MUTANTS_RESULT": "skipped",
            "SECURITY_COUNT": "",
            "SELFTEST_RESULT": "skipped",
            "SELFTEST_COUNT": "",
        },
        cwd=_REPO_ROOT,
    )
    ok = (
        result.returncode == 0
        and "nothing to reconcile" in result.stdout
        and "MUTATION_GATE_RESULT: PASS" in result.stdout
    )
    print(
        f"{'PASS' if ok else 'FAIL'}: has_selection=false (legitimate 0-selected, "
        f"mutants/meta-selftest skipped) -> exit={result.returncode} (expected 0)"
    )
    if not ok:
        print("  stdout:", result.stdout, "  stderr:", result.stderr)
    return ok


def check_changes_failure_fails(script: str) -> bool:
    result = run_script(script, {"CHANGES_RESULT": "failure"}, cwd=_REPO_ROOT)
    ok = result.returncode == 1 and "MUTATION_GATE_RESULT: FAIL" in result.stdout
    print(
        f"{'PASS' if ok else 'FAIL'}: changes job itself failed -> "
        f"exit={result.returncode} (expected 1)"
    )
    if not ok:
        print("  stdout:", result.stdout, "  stderr:", result.stderr)
    return ok


def check_extraction_sane(script: str) -> bool:
    """A fast, narrowly-scoped sanity check on the EXTRACTION itself, separate from
    the five behavioural checks below -- if extract_run_body() ever returns
    something subtly wrong (truncated, off-by-one on indentation, an empty string),
    the behavioural checks below would still fail, but their failure messages talk
    about exit codes and reconciliation math, not about extraction. This check
    fails loudly and specifically at that layer instead, so a future break is easy
    to tell apart: did extraction get the wrong text, or did the gate job's actual
    logic regress?"""
    markers = ["MUTATION_GATE_RESULT: PASS", "MUTATION_GATE_RESULT: FAIL", "is_ok", "SELECTED_TOTAL", "EXECUTED_TOTAL"]
    missing = [m for m in markers if m not in script]
    ok = len(script.strip()) > 0 and not missing
    print(
        f"{'PASS' if ok else 'FAIL'}: extracted script is non-empty ({len(script)} "
        f"chars) and contains all expected markers {markers} "
        f"(missing: {missing if missing else 'none'})"
    )
    return ok


def check_no_checkout_needed(script: str) -> bool:
    """THE #973 regression test. Before the fix, this exact scenario reproduced the
    real incident (`python3: can't open file '.../gate_lib.py': No such file or
    directory`) because the step shelled out to a repo-relative script; the `gate` job
    never checks out the repo. After the fix, the step is pure bash/arithmetic and
    must succeed regardless of cwd."""
    with tempfile.TemporaryDirectory() as tmp:
        result = run_script(script, {}, cwd=Path(tmp))
    ok = result.returncode == 0 and "MUTATION_GATE_RESULT: PASS" in result.stdout
    print(
        f"{'PASS' if ok else 'FAIL'}: matched counts, run from an EMPTY directory "
        f"with NO repository checked out at all (issue #973 regression test) -> "
        f"exit={result.returncode} (expected 0)"
    )
    if not ok:
        print("  stdout:", result.stdout, "  stderr:", result.stderr)
    return ok


# issue #973 (this selftest's own second-round bug, found on this branch's own next
# CI run after the first #973 fix): distinct from the normal "one or more checks
# failed" exit(1) below. A precondition failure here -- the workflow file or
# audit-workflow-timeouts.py missing/unreadable, the target step renamed, a
# ModuleNotFoundError from a dependency this environment doesn't have -- must never
# read the same as "ran all five checks and some failed". A bare traceback would
# still exit non-zero, which is enough for a shell `if` to notice SOMETHING is wrong,
# but not enough for a human or a future guard to tell "the test found a real
# reconciliation bug" apart from "the test itself could not even start" without
# reading the full log. This is exactly the distinction #968's `select` subcommand
# already makes for its own empty-selection case (exit 0 legitimate vs exit 2
# broken) -- the same shape, applied to this script's own startup.
SCANNER_ERROR = 2


def main() -> int:
    try:
        script = extract_gate_summarize_script()
    except Exception as exc:  # noqa: BLE001 -- deliberately broad: ANY precondition
        # failure (missing file, import error, a dependency this environment lacks,
        # an unexpected exception from audit-workflow-timeouts.py) must exit 2, not
        # traceback -- see the module-level comment above SCANNER_ERROR for why a
        # ModuleNotFoundError must never be indistinguishable from "checks ran and
        # failed" to anything reading only the exit code.
        print(
            f"::error::selftest_gate_job_reconciliation.py could not extract the "
            f"gate job's script -- refusing to report a pass/fail verdict without "
            f"having tested anything. {type(exc).__name__}: {exc}",
            file=sys.stderr,
        )
        return SCANNER_ERROR

    results = [
        check_extraction_sane(script),
        check_match_passes(script),
        check_shortfall_fails(script),
        check_surplus_tolerated_with_warning(script),
        check_zero_selected_legit_pass(script),
        check_changes_failure_fails(script),
        check_no_checkout_needed(script),
    ]
    ok = all(results)
    print(
        "SELFTEST issue-973/1001 (gate job needs no checkout; selected-vs-executed "
        f"reconciliation stays inline bash, now asymmetric): {'PASS' if ok else 'FAIL'}"
    )
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
