#!/usr/bin/env python3
"""Mutation-gate runner for issue #834.

Mutants are declared one-per-file under test/mutants/entries/<id>.json (issue #854:
a single shared 'mutants' array in manifest.json made every registering PR conflict at
the same insertion point). test/mutants/manifest.json itself now holds only shared
registry metadata (schema_version, $comment) -- see load_manifest()/discover_entries()
below for how the two are combined back into one in-memory manifest dict.

For ONE mutant declared under test/mutants/entries/, this script:

  1. Rejects a mutant with an empty (or missing) red_tests list outright -- with
     nothing to check, the evaluation loop below can neither detect a survivor nor
     an unexpected failure, and used to fall through to KILLED for having proven
     nothing (issue #855 defect 2). Also rejects any red_expected_assertion_pattern
     that is over-broad enough to match an empty string, or a representative
     unrelated failure message (NullReferenceException-style text, a cancelled
     operation, an unrelated assertion) -- a pattern that loose would match
     essentially any failure, not the guard's own assertion text specifically
     (issue #855 defect 3).
  2. Validates the mutant's patch is scoped to its declared production file only,
     using the paths **git itself** resolves when applying the patch (`git apply
     --numstat`, driven by the `---`/`+++` hunk headers) -- not a text-level parse
     of the patch's `diff --git a/<path> b/<path>` summary line, which a hostile or
     malformed patch can make disagree with what git will actually touch (issue
     #855 defect 1, reproduced and fixed: a patch whose `diff --git` line names a
     production file while its `---`/`+++` headers name a test file used to sail
     through this check and then silently rewrite the test). Also independently
     rejects anything touching test/, appsettings*.json, or seed data -- belt and
     suspenders on top of the single-file allowlist check.
  3. Runs the mutant's declared red tests on the CLEAN, unmutated tree (the
     baseline) and requires every one of them to already be green there, BEFORE
     the patch is applied. Without this, a test that was already failing for a
     reason that has nothing to do with the mutant would get reported KILLED once
     the (irrelevant) patch was applied too -- proving the test is red, not that
     the mutant is what turned it red (issue #855 defect 3).
  4. Applies the patch to the working tree.
  5. Asserts the patched tree still COMPILES. A mutant that breaks the build is an
     "invalid mutant" and is reported as a gate FAILURE, never a pass (it proves
     nothing about test effectiveness -- see issue #834 rule #3).
  6. Runs the mutant's "green" test filter (the positive control) and asserts it
     stays green. A positive control failure means either the mutation had broader
     effects than declared, or the environment itself is broken -- either way the
     result is untrustworthy and is reported as a gate FAILURE.
  7. Runs the mutant's "red" test filter and, for each declared test, checks:
       - did it fail at all? If it stayed green, the mutant SURVIVED (the test
         cannot detect the guard's absence) -- gate FAILURE.
       - if it failed, does the failure message match the manifest's expected
         assertion pattern for that test? A failure for an unrelated reason
         (NullReferenceException, fixture crash, wrong assertion) is NOT a proof
         of detection -- it is reported as UNEXPECTED_RED, also a gate FAILURE.
       - only a failure whose message matches the expected pattern counts as
         KILLED (the test genuinely detected the mutant).
     Also scans the SAME TRX for any undeclared test that also did not pass -- a
     red_test_filter whose blast radius is broader than the entry's declared
     red_tests must not let an extra, silent failure ride along unexamined (issue
     #855 defect 3): that too is reported as UNEXPECTED_RED, a gate FAILURE.
  8. ALWAYS reverts the patch before exiting, even on error, so the working tree is
     left exactly as it was found.

Entries are also rejected at load time (before any of the above runs) if their
declared `kind` is not one of `security`/`selftest` (issue #855 defect 4): an
unrecognized kind used to load without complaint and then simply never be selected
by either CI job in .github/workflows/mutation-gate.yml, leaving the entry
registered but permanently unexecuted while the rest of the gate stayed green.

Exit code is 0 only when every check above resolves to the expected outcome
(normally: KILLED). Everything else -- SURVIVED, UNEXPECTED_RED, a build failure,
a scope violation, a non-green baseline, an empty red_tests list, an over-broad
assertion pattern, or a positive-control failure -- exits non-zero.

BEFORE REGISTERING AN ENTRY -- a KILLED/GATE:PASS verdict from step 6+7 above
proves the green test stayed green and the red test failed with the expected
message. It does NOT prove the green test's pass was caused by anything this
patch left alone. A green_test whose call path reaches the mutated line, and
whose outcome the mutation happens to leave unchanged for that one input, will
still report KILLED -- silently proving nothing (issue #986: a green_test using
a fixture with the exact FK shape the mutant neutralizes returned the same
boolean under the mutant, via a different internal branch, and the gate never
noticed). Step 6's own POSITIVE_CONTROL_FAILED verdict only catches the case
where the coupling flips the assertion (issue #979) -- it cannot catch a
coupled control that happens to still agree. Before writing green_tests, trace
the test's call path through to the mutated condition and show EITHER it is
never reached (a different code path for this input) OR the mutated and
unmutated forms are provably equal for this specific input (a short-circuit
upstream, or an invariant like an exact-type match agreeing under both an
equality check and an IsAssignableFrom check). See .claude/rules/testing.md
"A mutant's positive control must not touch the mutated decision path" for the
worked examples and the full checklist.

Usage:
    python3 test/mutants/run_mutant.py --mutant <id> [--manifest test/mutants/manifest.json]
    python3 test/mutants/run_mutant.py --mutant <id> --expect-verdict INVALID_MUTANT_BUILD_FAILURE

--expect-verdict is for the meta-selftest job only (see entries/*.json's "kind":
"selftest" entries): it asserts on the RUNNER's own verdict instead of enforcing
that the verdict is KILLED, so the runner's own invalid-mutant / survived /
killed detection logic can be regression-tested without touching real security
tests.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile

# security note: this parses TRX XML produced locally by `dotnet test`, which we
# invoke ourselves a few lines above -- it is a first-party build artifact, not an
# externally-supplied/untrusted document, so the classic XXE / DTD-entity-expansion
# threat model (a hostile document controlling its own DOCTYPE/ENTITY declarations)
# does not apply here. We still avoid xml.dom.minidom/pulldom and stick to the
# lighter, well-audited xml.etree.ElementTree reader rather than adding an extra
# third-party dependency (defusedxml) to a CI gate script that has to keep working
# offline on a self-hosted runner.
import xml.etree.ElementTree as ET  # nosec B314 -- see note above: first-party TRX, not untrusted input
from dataclasses import dataclass, field
from pathlib import Path

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

# Defense-in-depth denylist (issue #834 constraint): even though every mutant is
# also required to touch ONLY its declared target_file, independently reject any
# patch that touches these paths so a manifest typo can never smuggle a test/seed/
# config change through as part of a "production" mutant.
DENYLIST_PATTERNS = [
    re.compile(r"^test/"),
    re.compile(r"(^|/)appsettings[^/]*\.json$", re.IGNORECASE),
    re.compile(r"seed", re.IGNORECASE),
]

VERDICT_KILLED = "KILLED"
VERDICT_SURVIVED = "SURVIVED"
VERDICT_UNEXPECTED_RED = "UNEXPECTED_RED"
VERDICT_POSITIVE_CONTROL_FAILED = "POSITIVE_CONTROL_FAILED"
VERDICT_INVALID_SCOPE = "INVALID_MUTANT_SCOPE"
VERDICT_PATCH_DID_NOT_APPLY = "INVALID_MUTANT_PATCH_DID_NOT_APPLY"
VERDICT_BUILD_FAILURE = "INVALID_MUTANT_BUILD_FAILURE"
VERDICT_FILTER_MATCHED_NOTHING = "INVALID_MUTANT_FILTER_MATCHED_NO_TESTS"
# issue #855 defect 2: a mutant with nothing declared in red_tests can never produce a
# survivor or an unexpected result, so it used to fall through evaluate_red() straight
# to KILLED -- reported before any test ever ran.
VERDICT_EMPTY_RED_TESTS = "INVALID_MUTANT_EMPTY_RED_TESTS"
# issue #855 defect 3: a red_expected_assertion_patterns regex loose enough to match an
# empty string, OR a representative unrelated failure message (see PROBE_MESSAGES
# below), would match essentially any failure, defeating the entire point of pinning
# the guard's own assertion text.
VERDICT_OVERBROAD_PATTERN = "INVALID_MUTANT_OVERBROAD_ASSERTION_PATTERN"
# issue #855 defect 3: the declared red tests must already be green on the clean,
# unmutated tree -- otherwise a KILLED verdict cannot distinguish "the mutant broke
# this test" from "this test was already broken".
VERDICT_BASELINE_NOT_GREEN = "INVALID_MUTANT_BASELINE_NOT_GREEN"

# The only verdict that represents a genuinely effective, correctly-scoped test.
PASSING_VERDICTS = {VERDICT_KILLED}

# issue #855 defect 4: entries.json's `kind` field selects which CI job
# (.github/workflows/mutation-gate.yml's `mutants` vs `meta-selftest`) ever executes an
# entry. Any value outside this set must be rejected at load time -- see
# discover_entries() -- so a typo can never leave an entry registered but silently
# excluded from both jobs.
VALID_KINDS = {"security", "selftest"}

# issue #855 defect 3 (see validate_assertion_patterns_not_overbroad): representative,
# realistic MSTest/.NET failure text that has nothing to do with any guard this
# registry pins. A properly-scoped red_expected_assertion_pattern must NOT match any of
# these -- if it does, it is loose enough to also match an unrelated crash.
PROBE_MESSAGES = [
    "Object reference not set to an instance of an object.",
    "The operation was canceled.",
    "Assert.Fail failed. Totally unrelated assertion text for probe purposes only.",
]


class GateError(Exception):
    """Raised for a verdict that ends the run before tests could even be attempted
    (scope violation, patch that won't apply, build failure). Carries the verdict
    string so main() can still print/return it uniformly."""

    def __init__(self, verdict: str, message: str):
        super().__init__(message)
        self.verdict = verdict
        self.message = message


@dataclass
class TestOutcome:
    full_name: str
    ran: bool = False
    passed: bool = False
    message: str = ""


@dataclass
class MutantResult:
    mutant_id: str
    verdict: str
    detail: str = ""
    red_outcomes: list[TestOutcome] = field(default_factory=list)
    green_outcomes: list[TestOutcome] = field(default_factory=list)


def run(cmd: list[str], cwd: Path, timeout: int = 900) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout
    )


def discover_entries(entries_dir: Path) -> list[dict]:
    """Glob test/mutants/entries/*.json and load each as one mutant entry.

    Fails loudly (SystemExit) rather than returning an empty/partial list on any of
    the ways this could go silently wrong: the directory not existing, the glob
    matching nothing, a file that isn't valid JSON, or an entry missing its own 'id'.
    A directory listing that resolves to "found nothing" must never be mistaken for
    "no mutants configured, nothing to do, pass" -- that is exactly the failure mode
    the old single-array manifest could not have (an empty array was at least always
    present and explicit), so the directory-based replacement has to guard against it
    deliberately.
    """
    if not entries_dir.is_dir():
        raise SystemExit(
            f"Mutant entries directory '{entries_dir}' does not exist. Expected one "
            "JSON file per mutant at test/mutants/entries/<id>.json (issue #854)."
        )
    paths = sorted(entries_dir.glob("*.json"))
    if not paths:
        raise SystemExit(
            f"No mutant entry files found under '{entries_dir}' (glob '*.json' matched "
            "nothing). Refusing to proceed as if zero mutants were intentional."
        )
    entries: list[dict] = []
    for path in paths:
        with path.open("r", encoding="utf-8") as f:
            try:
                entry = json.load(f)
            except json.JSONDecodeError as e:
                raise SystemExit(f"Failed to parse mutant entry file '{path}': {e}")
        entry_id = entry.get("id")
        if not entry_id:
            raise SystemExit(
                f"Mutant entry file '{path}' has no (or an empty) 'id' field -- "
                "refusing to silently skip it."
            )
        if entry_id != path.stem:
            raise SystemExit(
                f"Mutant entry file '{path}' declares id '{entry_id}', but its filename "
                f"stem is '{path.stem}'. The filename must equal the id so a mutant can "
                "always be found by id alone -- rename the file or fix the id."
            )
        kind = entry.get("kind", "security")
        if kind not in VALID_KINDS:
            raise SystemExit(
                f"Mutant entry file '{path}' (id '{entry_id}') declares kind='{kind}', "
                f"which is not one of {sorted(VALID_KINDS)}. issue #855 defect 4: an "
                "unrecognized kind used to load without complaint and then simply never "
                "be selected by either CI job (mutation-gate.yml's 'mutants' job selects "
                "kind=='security', 'meta-selftest' selects kind=='selftest') -- leaving "
                "the entry registered but permanently unexecuted while everything else "
                "stayed green. Fix the typo, or add the new kind to VALID_KINDS here AND "
                "give it a job in mutation-gate.yml that actually runs it."
            )
        entries.append(entry)
    return entries


def load_manifest(manifest_path: Path) -> dict:
    with manifest_path.open("r", encoding="utf-8") as f:
        manifest = json.load(f)
    entries_dir = manifest_path.parent / "entries"
    manifest["mutants"] = discover_entries(entries_dir)
    return manifest


def find_mutant(manifest: dict, mutant_id: str) -> dict:
    for m in manifest["mutants"]:
        if m["id"] == mutant_id:
            return m
    known = ", ".join(m["id"] for m in manifest["mutants"])
    raise SystemExit(f"Unknown mutant id '{mutant_id}'. Known ids: {known}")


def clean_stale_demo_db(repo_root: Path) -> list[str]:
    # See test/WalkingTec.Mvvm.Api.Test/DbTestHelpers.cs's "demo.db residual guard" doc
    # comment and CLAUDE.md's release-gate SOP: DemoWebApplicationFactory points at the
    # physical file demo/WalkingTec.Mvvm.Demo/demo.db. EF's EnsureCreated sees a bin/
    # copy that already "exists" and skips both schema creation AND any cleanup, so a
    # row written by an earlier test run (in this process or a previous run_mutant.py
    # invocation against the same build output) is still there on the next run. This
    # was found the hard way while building this manifest entry: with a stale demo.db,
    # UpdateModelProperty_ITCodeField_ReturnsBadRequest reported a false Passed under
    # the blockedFields-emptied mutant, because a leftover row from an earlier run
    # already held the literal ITCode value the test writes, so FrameworkUserVM's
    # duplicate-key check produced a 400 for a completely unrelated reason -- the same
    # "looks red, wrong reason" failure mode UNEXPECTED_RED exists to catch, except
    # inverted: here it looked GREEN for the wrong reason instead of RED. Deleting any
    # demo.db under a bin/ output directory before every run is the documented fix
    # (`find . -name 'demo.db*' -path '*bin*' -delete`), automated here so a mutation
    # run is never silently poisoned by whatever ran immediately before it.
    removed = []
    for path in repo_root.rglob("demo.db*"):
        if "bin" not in path.parts:
            continue
        try:
            path.unlink()
            removed.append(str(path.relative_to(repo_root)))
        except OSError as e:
            print(f"WARNING: could not remove stale {path}: {e}", file=sys.stderr)
    return removed


def assert_target_file_clean(repo_root: Path, target_file: str) -> None:
    # Only the mutant's own target file needs to be pristine -- applying and
    # reverting a patch against it must be exact. We deliberately do NOT require
    # the whole tree to be clean: this script is meant to be run from a normal dev
    # checkout (which legitimately has other untracked/in-progress files, e.g. the
    # mutant registry itself while it's being authored) as well as from a fresh CI
    # checkout.
    result = run(["git", "status", "--porcelain", "--", target_file], cwd=repo_root)
    if result.stdout.strip():
        raise SystemExit(
            f"Refusing to run: '{target_file}' has uncommitted changes.\n"
            "run_mutant.py applies and reverts a patch against this exact file and "
            "must start from a pristine copy so the revert is exact.\n\n" + result.stdout
        )


def git_apply_touched_paths(repo_root: Path, patch_path: Path) -> list[str]:
    """Return the file path(s) `git apply` will ACTUALLY modify when applying this patch.

    issue #855 defect 1 (fixed here): the function this replaced parsed only the
    `diff --git a/<path> b/<path>` summary line out of the patch TEXT. `git apply` does
    NOT use that line to decide what to modify -- it resolves the path from the
    `---`/`+++` hunk headers. Those two can disagree, and `git apply` always honours the
    latter. Reproduced against this exact repo with a patch whose `diff --git` line names
    a production file while its `---`/`+++` headers name a test file:

        $ git apply --check --verbose fake-header.patch
        Checking patch test/WalkingTec.Mvvm.Api.Test/MvcAuthHolesTests.cs...

    i.e. git silently modifies the test file while a `diff --git`-only parse reports the
    production file's name and a scope check built on that text parse never notices. So
    this asks git itself which paths it will touch (`git apply --numstat`, confirmed
    empirically to report the same `---`/`+++`-resolved path `git apply` itself uses, for
    every patch in this registry) instead of re-deriving it from a line a hostile or
    malformed patch can make disagree with reality.
    """
    result = run(["git", "apply", "--numstat", str(patch_path)], cwd=repo_root)
    if result.returncode != 0:
        raise GateError(
            VERDICT_PATCH_DID_NOT_APPLY,
            f"git apply --numstat could not parse {patch_path} (so the path(s) it would "
            f"touch cannot even be determined):\n{result.stderr}",
        )
    paths = []
    for line in result.stdout.splitlines():
        # numstat format: "<added>\t<removed>\t<path>" (added/removed are '-' for a
        # binary file). maxsplit=2 so a path containing a literal tab is never truncated.
        parts = line.split("\t", 2)
        if len(parts) == 3:
            paths.append(parts[2])
    return paths


def validate_scope(repo_root: Path, mutant: dict, patch_path: Path) -> None:
    target_file = mutant["target_file"]
    paths = git_apply_touched_paths(repo_root, patch_path)
    if not paths:
        raise GateError(
            VERDICT_INVALID_SCOPE,
            f"Patch for mutant '{mutant['id']}' does not touch any file according to "
            "`git apply --numstat` -- refusing to apply.",
        )
    for p in paths:
        if p != target_file:
            raise GateError(
                VERDICT_INVALID_SCOPE,
                f"Mutant '{mutant['id']}' patch touches '{p}' (per `git apply "
                "--numstat`, i.e. the path git itself will actually modify), but its "
                f"manifest entry declares target_file='{target_file}'. A mutant patch "
                "must be constrained to exactly its declared production file.",
            )
        for pattern in DENYLIST_PATTERNS:
            if pattern.search(p):
                raise GateError(
                    VERDICT_INVALID_SCOPE,
                    f"Mutant '{mutant['id']}' patch touches '{p}', which matches the "
                    f"denylist pattern '{pattern.pattern}' (test/, appsettings*.json, "
                    "or seed data are never allowed in a mutant patch).",
                )


def apply_patch(repo_root: Path, patch_path: Path) -> None:
    check = run(["git", "apply", "--check", str(patch_path)], cwd=repo_root)
    if check.returncode != 0:
        raise GateError(
            VERDICT_PATCH_DID_NOT_APPLY,
            f"git apply --check failed for {patch_path}:\n{check.stderr}",
        )
    applied = run(["git", "apply", str(patch_path)], cwd=repo_root)
    if applied.returncode != 0:
        raise GateError(
            VERDICT_PATCH_DID_NOT_APPLY,
            f"git apply failed for {patch_path}:\n{applied.stderr}",
        )


def revert_patch(repo_root: Path, patch_path: Path) -> None:
    result = run(["git", "apply", "-R", str(patch_path)], cwd=repo_root)
    if result.returncode != 0:
        # Last-resort fallback: hard-reset just the target file(s) named in the
        # patch, so a partially-applied patch can never be left dirtying the tree.
        # Uses the same git-resolved path list as validate_scope() (issue #855 defect
        # 1) rather than a text-level parse, so the fallback checkout targets whatever
        # git actually modified, not whatever the patch's summary line merely claimed.
        # This runs from a `finally` block during cleanup -- git_apply_touched_paths()
        # can itself raise GateError if --numstat fails, which must never replace/mask
        # whatever real error is already propagating, so it is caught and downgraded to
        # the same WARNING this fallback already prints on any other failure mode.
        try:
            fallback_paths = git_apply_touched_paths(repo_root, patch_path)
        except GateError as e:
            fallback_paths = []
            print(
                f"WARNING: could not determine which paths to fall back to for revert "
                f"({e.message}); working tree may still be dirty.",
                file=sys.stderr,
            )
        for p in fallback_paths:
            run(["git", "checkout", "--", p], cwd=repo_root)
        remaining = run(["git", "status", "--porcelain"], cwd=repo_root)
        if remaining.stdout.strip():
            print(
                "WARNING: failed to cleanly revert mutant patch; "
                f"working tree may still be dirty:\n{remaining.stdout}",
                file=sys.stderr,
            )


def build_project(repo_root: Path, test_project: str) -> None:
    result = run(
        ["dotnet", "build", test_project, "-c", "Release", "--nologo"],
        cwd=repo_root,
        timeout=1800,
    )
    if result.returncode != 0:
        tail = "\n".join((result.stdout + result.stderr).splitlines()[-80:])
        raise GateError(
            VERDICT_BUILD_FAILURE,
            "dotnet build failed with the mutant patch applied -- this is an "
            "INVALID MUTANT (issue #834 rule #3: a mutant that breaks the build "
            "proves nothing about test effectiveness and must be reported as a "
            f"gate FAILURE, not skipped or treated as a pass).\n\n{tail}",
        )


def run_tests(
    repo_root: Path, test_project: str, test_filter: str, trx_dir: Path, trx_name: str
) -> dict[str, TestOutcome]:
    trx_dir.mkdir(parents=True, exist_ok=True)
    result = run(
        [
            "dotnet",
            "test",
            test_project,
            "--no-build",
            "-c",
            "Release",
            "--filter",
            test_filter,
            "--logger",
            f"trx;LogFileName={trx_name}",
            "--results-directory",
            str(trx_dir),
        ],
        cwd=repo_root,
        timeout=1800,
    )
    trx_path = trx_dir / trx_name
    outcomes: dict[str, TestOutcome] = {}
    if not trx_path.exists():
        # dotnet test can exit non-zero with no TRX at all if the filter matched
        # zero tests in some SDK versions, or if the run crashed before any test
        # executed. Surface the raw output so this is never silently treated as
        # "no red tests, therefore fine".
        raise GateError(
            VERDICT_FILTER_MATCHED_NOTHING,
            f"No TRX file was produced for filter '{test_filter}'. "
            f"dotnet test output:\n{result.stdout}\n{result.stderr}",
        )
    tree = ET.parse(trx_path)
    root = tree.getroot()

    class_by_test_id = {}
    for unit_test in root.iter(f"{TRX_NS}UnitTest"):
        test_id = unit_test.get("id")
        method = unit_test.find(f"{TRX_NS}TestMethod")
        if method is not None and test_id:
            class_by_test_id[test_id] = f"{method.get('className')}.{method.get('name')}"

    for utr in root.iter(f"{TRX_NS}UnitTestResult"):
        test_id = utr.get("testId")
        full_name = class_by_test_id.get(test_id)
        if full_name is None:
            full_name = utr.get("testName", "<unknown>")
        outcome_str = utr.get("outcome", "")
        message = ""
        error_info = utr.find(f"{TRX_NS}Output/{TRX_NS}ErrorInfo/{TRX_NS}Message")
        if error_info is not None and error_info.text:
            message = error_info.text
        outcomes[full_name] = TestOutcome(
            full_name=full_name,
            ran=True,
            passed=(outcome_str == "Passed"),
            message=message,
        )
    return outcomes


def validate_red_tests_declared(mutant: dict) -> None:
    # issue #855 defect 2: an empty (or missing) red_tests list gives evaluate_red()
    # nothing to iterate -- it can produce neither a `survived` nor an `unexpected`
    # entry, and used to fall straight through to KILLED, so a comment-only/no-op
    # mutant with red_tests=[] could pass the gate while proving nothing at all.
    if not (mutant.get("red_tests") or []):
        raise GateError(
            VERDICT_EMPTY_RED_TESTS,
            f"Mutant '{mutant['id']}' declares an empty (or missing) red_tests list. "
            "A mutant must name at least one test that is expected to detect it; "
            "otherwise there is nothing for this gate to check.",
        )


def validate_assertion_patterns_not_overbroad(mutant: dict) -> None:
    # issue #855 defect 3: a red_expected_assertion_patterns regex that matches the
    # EMPTY string would match essentially any failure message via re.search() --
    # including one from a completely unrelated bug -- so it can never actually pin the
    # mutant's own guard. re.search(pattern, "") succeeding is a precise, mechanical
    # definition of "this pattern is unconditionally satisfiable": no non-trivial
    # required substring (e.g. "abc.*", "#830: .*") can ever match "".
    #
    # The empty-string check alone is NOT enough, though (caught in code review):
    # a pattern requiring exactly one arbitrary character ('.', '\w', '\S', '.+',
    # '[\s\S]', ...) never matches '' but is exactly as useless in practice -- it
    # matches virtually any real (non-empty) failure message, including a totally
    # unrelated NullReferenceException or fixture crash. PROBE_MESSAGES are
    # representative non-empty failure text that has nothing to do with any guard
    # this registry pins; a pattern is over-broad if it matches ANY of them via
    # re.search(), since a properly-scoped pattern requires specific literal text (an
    # issue number, a guard's own message) that generic probe text cannot contain.
    for name, pattern in (mutant.get("red_expected_assertion_patterns") or {}).items():
        if not pattern:
            raise GateError(
                VERDICT_OVERBROAD_PATTERN,
                f"Mutant '{mutant['id']}': red_expected_assertion_patterns[{name!r}] "
                "is empty, which means it would match essentially any failure "
                "message, not the mutant's own guard specifically. Tighten the "
                "pattern so it can only match the guard's own assertion text.",
            )
        for probe in PROBE_MESSAGES:
            if re.search(pattern, probe) is not None:
                raise GateError(
                    VERDICT_OVERBROAD_PATTERN,
                    f"Mutant '{mutant['id']}': red_expected_assertion_patterns[{name!r}] "
                    f"= {pattern!r} matches an unrelated probe failure message "
                    f"({probe!r}) that has nothing to do with this mutant's own "
                    "guard, which means it would also match essentially any real "
                    "failure message. Tighten the pattern so it can only match the "
                    "guard's own assertion text.",
                )


def evaluate_baseline(mutant: dict, outcomes: dict[str, TestOutcome]) -> None:
    # issue #855 defect 3: run BEFORE apply_patch() -- proves the declared red tests are
    # green on the tree as it exists right now, before any mutant patch touches it. A
    # KILLED verdict computed without this baseline cannot distinguish "the mutant broke
    # this test" from "this test was already broken for an unrelated reason", so an
    # already-red test would previously be reported as a valid kill.
    expected = mutant.get("red_tests") or []
    not_green: list[tuple[str, str]] = []
    for name in expected:
        o = outcomes.get(name)
        if o is None or not o.ran:
            raise GateError(
                VERDICT_FILTER_MATCHED_NOTHING,
                f"Designated red test '{name}' did not appear in the BASELINE TRX "
                "results (clean tree, before the mutant patch was applied) for filter "
                f"'{mutant['red_test_filter']}' -- the filter is not matching the test "
                "the manifest declares.",
            )
        if not o.passed:
            not_green.append((name, o.message or "(outcome recorded as not Passed)"))
    if not_green:
        lines = "; ".join(f"'{n}': {m}" for n, m in not_green)
        raise GateError(
            VERDICT_BASELINE_NOT_GREEN,
            f"Mutant '{mutant['id']}': the following declared red test(s) are NOT "
            "green on the clean, unmutated tree (baseline, before the patch was "
            f"applied) -- a KILLED verdict here would prove nothing: {lines}",
        )


def evaluate_green(mutant: dict, outcomes: dict[str, TestOutcome]) -> list[TestOutcome]:
    expected = mutant.get("green_tests") or []
    results = []
    for name in expected:
        o = outcomes.get(name)
        if o is None or not o.ran:
            raise GateError(
                VERDICT_FILTER_MATCHED_NOTHING,
                f"Positive-control test '{name}' did not appear in the TRX results "
                f"for filter '{mutant['green_test_filter']}' -- the filter is not "
                "matching the test the manifest declares.",
            )
        results.append(o)
        if not o.passed:
            raise GateError(
                VERDICT_POSITIVE_CONTROL_FAILED,
                f"Positive control '{name}' FAILED under mutant '{mutant['id']}'. "
                "This mutant has broader effects than declared, or the test "
                f"environment itself is broken. Failure message:\n{o.message}",
            )
    return results


def evaluate_red(mutant: dict, outcomes: dict[str, TestOutcome]) -> tuple[str, str, list[TestOutcome]]:
    expected = mutant.get("red_tests") or []
    patterns = mutant.get("red_expected_assertion_patterns") or {}
    results: list[TestOutcome] = []
    survived: list[str] = []
    unexpected: list[tuple[str, str, str]] = []
    killed: list[str] = []
    for name in expected:
        o = outcomes.get(name)
        if o is None or not o.ran:
            raise GateError(
                VERDICT_FILTER_MATCHED_NOTHING,
                f"Designated red test '{name}' did not appear in the TRX results "
                f"for filter '{mutant['red_test_filter']}' -- the filter is not "
                "matching the test the manifest declares.",
            )
        results.append(o)
        if o.passed:
            survived.append(name)
            continue
        # Test failed. Was it for the reason the manifest expects?
        pattern = patterns.get(name)
        if not pattern:
            # No pattern on record for this test -- this only happens for entries
            # whose red tests are known/documented to never reach Failed today
            # (see manifest "red_expected_assertion_patterns_provisional"). We
            # cannot claim a match against an assertion pattern we don't have, so
            # this counts as unexpected-red rather than a silent pass.
            unexpected.append((name, o.message, "(no expected-assertion pattern on record)"))
            continue
        if re.search(pattern, o.message or ""):
            killed.append(name)
        else:
            unexpected.append((name, o.message, pattern))

    # issue #855 defect 3: an undeclared test elsewhere in this SAME TRX that also did
    # not pass must not be silently dropped. If the red_test_filter's blast radius is
    # (accidentally or otherwise) broader than this entry's declared red_tests, a
    # failure in that extra test is not proof the DECLARED test detected the mutant for
    # the declared reason -- it is evidence something else in the filter's reach is
    # broken too, and that has to fail the gate exactly like any other UNEXPECTED_RED.
    for full_name, o in outcomes.items():
        if full_name in expected:
            continue
        if o.ran and not o.passed:
            unexpected.append((
                full_name,
                o.message,
                "(undeclared: not in this mutant's red_tests, but present in the same "
                "TRX and not Passed)",
            ))

    if survived:
        detail = "; ".join(
            f"'{n}' stayed GREEN under the mutant" for n in survived
        )
        return (
            VERDICT_SURVIVED,
            "mutant survived; the designated test did not detect it -- " + detail,
            results,
        )
    if unexpected:
        lines = []
        for name, message, pattern in unexpected:
            lines.append(
                f"'{name}' went red, but not for the expected reason "
                f"(expected pattern: {pattern!r}; actual message: {message!r})"
            )
        return (VERDICT_UNEXPECTED_RED, "; ".join(lines), results)
    detail = "all designated red tests failed with their expected assertion message: " + "; ".join(
        f"'{n}'" for n in killed
    )
    return (VERDICT_KILLED, detail, results)


def evaluate_mutant(repo_root: Path, mutant: dict, trx_dir: Path) -> MutantResult:
    mutant_id = mutant["id"]

    # issue #855 defect 2 & 3: cheap, static checks on the manifest data itself, before
    # anything is applied or built.
    validate_red_tests_declared(mutant)
    validate_assertion_patterns_not_overbroad(mutant)

    patch_path = repo_root / "test" / "mutants" / mutant["patch"]
    validate_scope(repo_root, mutant, patch_path)

    # issue #855 defect 3: baseline -- run the declared red tests on the CLEAN,
    # unmutated tree (exactly the tree as it exists right now; apply_patch() below has
    # not run yet) and require them all to already be green, before spending any more
    # effort evaluating the mutant itself.
    build_project(repo_root, mutant["test_project"])
    baseline_outcomes = run_tests(
        repo_root,
        mutant["test_project"],
        mutant["red_test_filter"],
        trx_dir,
        f"{mutant_id}-baseline.trx",
    )
    evaluate_baseline(mutant, baseline_outcomes)

    apply_patch(repo_root, patch_path)
    try:
        build_project(repo_root, mutant["test_project"])

        green_outcomes = run_tests(
            repo_root,
            mutant["test_project"],
            mutant["green_test_filter"],
            trx_dir,
            f"{mutant_id}-green.trx",
        )
        green_results = evaluate_green(mutant, green_outcomes)

        red_outcomes = run_tests(
            repo_root,
            mutant["test_project"],
            mutant["red_test_filter"],
            trx_dir,
            f"{mutant_id}-red.trx",
        )
        verdict, detail, red_results = evaluate_red(mutant, red_outcomes)
        return MutantResult(
            mutant_id=mutant_id,
            verdict=verdict,
            detail=detail,
            red_outcomes=red_results,
            green_outcomes=green_results,
        )
    finally:
        revert_patch(repo_root, patch_path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--mutant", required=True, help="Mutant id (see test/mutants/entries/*.json)"
    )
    parser.add_argument(
        "--manifest",
        default="test/mutants/manifest.json",
        help=(
            "Path to the registry's shared metadata file (schema_version/$comment). "
            "Mutants themselves are discovered by globbing '<manifest's directory>/"
            "entries/*.json', relative to repo root or absolute."
        ),
    )
    parser.add_argument(
        "--repo-root",
        default=None,
        help="Repo root (default: discovered via `git rev-parse --show-toplevel`)",
    )
    parser.add_argument(
        "--trx-dir",
        default=None,
        help="Directory to write TRX results to (default: a temp dir)",
    )
    parser.add_argument(
        "--expect-verdict",
        default=None,
        help=(
            "For meta-selftest use only: assert the runner produces exactly this "
            "verdict for the given mutant, and exit 0/1 on that match instead of "
            "requiring KILLED. Real 'security' mutants must never use this flag."
        ),
    )
    args = parser.parse_args()

    if args.repo_root:
        repo_root = Path(args.repo_root).resolve()
    else:
        top = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            check=True,
        )
        repo_root = Path(top.stdout.strip())

    manifest_path = Path(args.manifest)
    if not manifest_path.is_absolute():
        manifest_path = repo_root / manifest_path
    manifest = load_manifest(manifest_path)
    mutant = find_mutant(manifest, args.mutant)

    assert_target_file_clean(repo_root, mutant["target_file"])

    removed_dbs = clean_stale_demo_db(repo_root)
    if removed_dbs:
        print("Removed stale demo.db residue before running (see clean_stale_demo_db):")
        for p in removed_dbs:
            print(f"  - {p}")
        print()

    trx_dir = Path(args.trx_dir) if args.trx_dir else Path(tempfile.mkdtemp(prefix="mutant-trx-"))
    trx_dir.mkdir(parents=True, exist_ok=True)

    head_sha_result = run(["git", "rev-parse", "HEAD"], cwd=repo_root)
    head_sha = head_sha_result.stdout.strip() or "<unknown>"

    print(f"=== mutant: {mutant['id']} ({mutant.get('kind', 'security')}) ===")
    print(f"target: {mutant['target_file']} :: {mutant.get('target_symbol', '')}")
    print(f"patch:  test/mutants/{mutant['patch']}")
    print(f"red filter:   {mutant['red_test_filter']}")
    print(f"green filter: {mutant['green_test_filter']}")
    print()

    try:
        result = evaluate_mutant(repo_root, mutant, trx_dir)
        verdict = result.verdict
        detail = result.detail
    except GateError as e:
        verdict = e.verdict
        detail = e.message

    print(f"--- VERDICT: {verdict} ---")
    print(detail)
    print()

    gate_pass = verdict in PASSING_VERDICTS

    # Artifacts (issue #834 rule #6: CI uploads head SHA, mutant diff, and TRX --
    # PR bodies link the artifact, they never paste hand-copied output). These are
    # written unconditionally, on both pass and fail, so `if: always()` upload
    # steps in the workflow always have something to attach.
    (trx_dir / f"{mutant['id']}-head-sha.txt").write_text(head_sha + "\n", encoding="utf-8")
    patch_src = repo_root / "test" / "mutants" / mutant["patch"]
    if patch_src.exists():
        (trx_dir / f"{mutant['id']}.patch").write_text(
            patch_src.read_text(encoding="utf-8"), encoding="utf-8"
        )
    result_payload = {
        "mutant_id": mutant["id"],
        "kind": mutant.get("kind", "security"),
        "head_sha": head_sha,
        "target_file": mutant["target_file"],
        "target_symbol": mutant.get("target_symbol", ""),
        "verdict": verdict,
        "detail": detail,
        "gate": "PASS" if gate_pass else "FAIL",
    }
    (trx_dir / f"{mutant['id']}-result.json").write_text(
        json.dumps(result_payload, indent=2) + "\n", encoding="utf-8"
    )

    if args.expect_verdict:
        ok = verdict == args.expect_verdict
        print(f"MUTANT {mutant['id']} SELFTEST: expected={args.expect_verdict} actual={verdict} "
              f"-> {'PASS' if ok else 'FAIL'}")
        return 0 if ok else 1

    print(f"MUTANT {mutant['id']} VERDICT: {verdict}")
    print(f"GATE: {'PASS' if gate_pass else 'FAIL'}")
    return 0 if gate_pass else 1


if __name__ == "__main__":
    sys.exit(main())
