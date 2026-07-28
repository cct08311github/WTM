#!/usr/bin/env python3
"""Mutation-gate runner for issue #834.

Mutants are declared one-per-file under test/mutants/entries/<id>.json (issue #854:
a single shared 'mutants' array in manifest.json made every registering PR conflict at
the same insertion point). test/mutants/manifest.json itself now holds only shared
registry metadata (schema_version, $comment) -- see load_manifest()/discover_entries()
below for how the two are combined back into one in-memory manifest dict.

For ONE mutant declared under test/mutants/entries/, this script:

  1. Validates the mutant's patch is scoped to its declared production file only
     (rejects anything touching test/, appsettings*.json, or seed data -- belt and
     suspenders on top of the single-file allowlist check).
  2. Applies the patch to the working tree.
  3. Asserts the patched tree still COMPILES. A mutant that breaks the build is an
     "invalid mutant" and is reported as a gate FAILURE, never a pass (it proves
     nothing about test effectiveness -- see issue #834 rule #3).
  4. Runs the mutant's "green" test filter (the positive control) and asserts it
     stays green. A positive control failure means either the mutation had broader
     effects than declared, or the environment itself is broken -- either way the
     result is untrustworthy and is reported as a gate FAILURE.
  5. Runs the mutant's "red" test filter and, for each declared test, checks:
       - did it fail at all? If it stayed green, the mutant SURVIVED (the test
         cannot detect the guard's absence) -- gate FAILURE.
       - if it failed, does the failure message match the manifest's expected
         assertion pattern for that test? A failure for an unrelated reason
         (NullReferenceException, fixture crash, wrong assertion) is NOT a proof
         of detection -- it is reported as UNEXPECTED_RED, also a gate FAILURE.
       - only a failure whose message matches the expected pattern counts as
         KILLED (the test genuinely detected the mutant).
  6. ALWAYS reverts the patch before exiting, even on error, so the working tree is
     left exactly as it was found.

Exit code is 0 only when every check above resolves to the expected outcome
(normally: KILLED). Everything else -- SURVIVED, UNEXPECTED_RED, a build failure,
a scope violation, or a positive-control failure -- exits non-zero.

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

# The only verdict that represents a genuinely effective, correctly-scoped test.
PASSING_VERDICTS = {VERDICT_KILLED}


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


def touched_paths(patch_text: str) -> list[str]:
    paths = []
    for line in patch_text.splitlines():
        if line.startswith("diff --git a/"):
            # "diff --git a/<path> b/<path>"
            rest = line[len("diff --git a/"):]
            # split on " b/" -- safe because git always re-quotes paths containing
            # a literal " b/" sequence; a plain split is fine for this repo's paths.
            a_path = rest.split(" b/", 1)[0]
            paths.append(a_path)
    return paths


def validate_scope(mutant: dict, patch_text: str) -> None:
    target_file = mutant["target_file"]
    paths = touched_paths(patch_text)
    if not paths:
        raise GateError(
            VERDICT_INVALID_SCOPE,
            f"Patch for mutant '{mutant['id']}' does not touch any file "
            "(no 'diff --git' header found) -- refusing to apply.",
        )
    for p in paths:
        if p != target_file:
            raise GateError(
                VERDICT_INVALID_SCOPE,
                f"Mutant '{mutant['id']}' patch touches '{p}', but its manifest "
                f"entry declares target_file='{target_file}'. A mutant patch must be "
                "constrained to exactly its declared production file.",
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
        for p in touched_paths(patch_path.read_text(encoding="utf-8")):
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
    patch_path = repo_root / "test" / "mutants" / mutant["patch"]
    patch_text = patch_path.read_text(encoding="utf-8")

    validate_scope(mutant, patch_text)
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
