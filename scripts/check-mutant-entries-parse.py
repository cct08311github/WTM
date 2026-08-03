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

SCOPE: this guard does NOT re-implement or duplicate any of run_mutant.py's
TEST-EXECUTION validation (red_tests non-empty, assertion-pattern overbreadth, the
scope check that walks `git apply --numstat` against target_file, etc.) -- those
require actually applying a patch and running dotnet test, which is exactly what the
expensive 'mutants'/'meta-selftest' jobs do downstream, gated on this cheap job. It
DOES now duplicate exactly ONE step of run_mutant.py's PATCH validation -- see
"PATCH-APPLY CHECK (issue #1005)" below -- because that one step (`git apply --check`)
is a dry run: it writes nothing to the working tree or the index and costs
milliseconds per patch, unlike a build or a test run, so it carries none of the flaky
failure modes that keep the rest of PATCH/TEST-EXECUTION validation downstream.

PATCH-APPLY CHECK (issue #1005): a mutant patch's fixed context can be broken by an
UNRELATED edit from another PR that never touches the patch file itself. Concrete
incident: #1000 inserted a `LogPrincipalKeyRejection(...)` call between the rejection
`if` and the `throw` in FileAttachmentSaveChangesGuard.cs -- a line landing inside the
fixed context of test/mutants/patches/fileattachmentguard985-principal-key-check-
neutralize.patch, a patch #985 itself shipped to pin its own fix. `git apply --check`
on #985 alone succeeds; on #985 + #1000 together it fails with "patch does not
apply". Neither PR's own CI could see this: this repo's internal infrastructure PR CI checks out
`refs/pull/N/head` for the `pull_request` event -- never a merge ref, the opposite of
GitHub (docs/ci-operations.md, "actions/checkout@v5 在 pull_request 事件只 checkout PR
自己的 head, 不是 base+head 的 merge") -- so no CI run for either PR ever has both
branches' trees checked out at once. run_mutant.py's own apply_patch() runs exactly
this `git apply --check` and raises GateError(VERDICT_PATCH_DID_NOT_APPLY);
PASSING_VERDICTS excludes that verdict, so the downstream mutation-gate job correctly
fails -- but only once both branches finally share a tree, which on this repo's
checkout model is the first `push` run on dotnet10 after merge, one gate cycle later
than either PR's own CI.

Rule: every `test/mutants/patches/*.patch` file must `git apply --check` cleanly
against the CURRENT tree (the tree this job checked out, exactly as
run_mutant.py's apply_patch() would see it). The file list comes from globbing
`test/mutants/patches/*.patch` directly, NOT from walking entries' own `patch` fields
-- so every patch is checked whether or not any `entries/*.json` file references it
(issue #1005 acceptance criterion 4). A patch no entry references (an "orphan") is
reported separately, informationally: run_mutant.py only ever applies a patch a
selected entry points at, so an orphan's own staleness would otherwise never be
noticed by anything in this repo's tooling -- this scan is deliberately the one place
that still looks at it.

HONEST SCOPE OF THIS CHECK, STATED PLAINLY (do not oversell it): running `git apply
--check` here, in the `changes` job that already runs on every PR against that PR's
own single tree, moves the check one merge earlier than the mutation gate would catch
it, and catches the SINGLE-BRANCH case -- a PR whose own commits break one of its own
patches' fixed context -- outright, before any expensive build runs. It does NOT
catch, and structurally cannot catch, the CROSS-BRANCH case that produced #1005
itself: two PRs each green alone, whose trees are never checked out together by any CI
run before they are merged. That gap is a property of this repo's internal infrastructure PR-CI
checkout model (no run ever sees two open PRs' trees at once), not something a script
running inside one PR's own job can close. It DOES fully close the gap on `push` to
dotnet10 (post-merge, full tree, no other open PR to be blind to) and on every PR that
happens to touch a patch's own fixed-context lines directly.

Usage:
    python3 scripts/check-mutant-entries-parse.py             # the real scan
    python3 scripts/check-mutant-entries-parse.py --selftest  # embedded-fixture proof
        that check_patch_applies()/check_git_usable() actually fire (see
        run_selftest()'s own docstring) -- run this FIRST in CI, `set -e` before the
        real scan, matching scripts/check-e2e-test-integrity.py's identical pattern.
        A `--selftest` failure means THIS SCRIPT is broken, never that a real patch
        under test/mutants/patches/ does not apply.

Exit codes: 0 clean, 1 one or more entries invalid AND/OR one or more patches under
test/mutants/patches/ do not apply to the current tree (each printed above its own
summary line; under --selftest, one or more embedded expectations were not met), 2
scanner error (couldn't list the entries or patches directory, couldn't extract
VALID_KINDS from run_mutant.py, `git` itself is unavailable, the current directory is
not inside a git work tree, or a patch file could not be read; under --selftest, the
scratch-repo setup itself failed) -- distinct from 1 so a scanner failure is never
silently reported as a clean tree, and distinct from "this patch genuinely does not
apply" so a git/filesystem precondition failure (acceptance criterion 3) is never
misreported as a content finding, or vice versa.
"""
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ENTRIES_DIR = Path("test/mutants/entries")
PATCHES_DIR = Path("test/mutants/patches")
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


def check_git_usable() -> str | None:
    """Preconditions for the patch-apply check below (issue #1005): `git` must be on
    PATH and the current directory must be inside a git work tree. Returns None if
    both hold, otherwise a message describing which one failed.

    Both are 'could not analyse' conditions (exit code SCANNER_ERROR), never 'a patch
    does not apply' (exit code 1) -- acceptance criterion 3: git being absent or this
    not being a repo says nothing about whether any individual patch's content is
    stale, and must never be conflated with a real finding."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--is-inside-work-tree"],
            capture_output=True, text=True,
        )
    except OSError as exc:
        return f"'git' executable is not available on PATH: {exc}"
    if result.returncode != 0 or result.stdout.strip() != "true":
        detail = result.stderr.strip() or f"exit code {result.returncode}"
        return f"current directory is not inside a git work tree: {detail}"
    return None


def describe_patch_target(patch_path: Path) -> str:
    """Best-effort: names the file(s) a patch touches, via `git apply --numstat`
    (parses the patch's own ---/+++ hunk headers without requiring the patch to
    actually apply cleanly -- the same technique run_mutant.py's
    git_apply_touched_paths() uses, and for the identical reason given there: a
    text-level `diff --git a/<path> b/<path>` summary line is not authoritative,
    git's own header parser is). Used only to make a failure message name a concrete
    file (acceptance criterion 2) -- never used to decide pass/fail; any problem here
    degrades to an "unknown" note, not a scanner error, since the --check result
    above is already the authoritative verdict for this patch."""
    try:
        result = subprocess.run(
            ["git", "apply", "--numstat", str(patch_path)],
            capture_output=True, text=True,
        )
    except OSError:
        return "target file: unknown (git apply --numstat could not run)"
    if result.returncode != 0 or not result.stdout.strip():
        return "target file: unknown (git apply --numstat could not parse this patch either)"
    targets = [line.split("\t")[-1] for line in result.stdout.strip().splitlines() if line.strip()]
    return "target: " + ", ".join(targets) if targets else "target file: unknown"


def check_patch_applies(patch_path: Path) -> tuple[str | None, str | None]:
    """Runs `git apply --check` for ONE patch file against the current tree, exactly
    as run_mutant.py's own apply_patch() does: a dry run that validates the patch
    would apply cleanly WITHOUT writing anything to the working tree or the index.

    Returns (violation, scanner_problem); at most one is non-None (both None means
    the patch applies cleanly):
      - violation: git itself rejected the patch content (stale/broken fixed
        context, corrupt hunk, etc.) -- a genuine finding. Contributes to exit code 1.
      - scanner_problem: the patch file itself could not even be handed to git (not
        readable on disk). Contributes to exit code SCANNER_ERROR, NEVER 1 --
        acceptance criterion 3: an unreadable patch must never be conflated with
        "this patch genuinely does not apply".
    """
    if not os.access(patch_path, os.R_OK):
        return None, f"{patch_path}: patch file is not readable (missing or permission denied)"

    try:
        result = subprocess.run(
            ["git", "apply", "--check", str(patch_path)],
            capture_output=True, text=True,
        )
    except OSError as exc:
        return None, f"{patch_path}: could not invoke `git apply --check`: {exc}"

    if result.returncode != 0:
        target = describe_patch_target(patch_path)
        reason = result.stderr.strip() or f"git apply --check exited {result.returncode} with no stderr output"
        return f"{patch_path} does not apply to the current tree ({target}) -- {reason}", None

    return None, None


def find_orphan_patches(entry_paths: list[Path], patch_names: list[str]) -> list[str]:
    """Cross-references every patches/*.patch filename against every entry's own
    declared "patch" field. Entries that fail to parse are silently skipped HERE
    (check_entry() above already reports them on their own terms -- this function
    must not double-report a broken entry file as if it were an orphan-patch
    finding). Returns the patch filenames (e.g. "foo.patch", matching PATCHES_DIR.glob
    output) that no entry references -- issue #1005 acceptance criterion 4."""
    referenced: set[str] = set()
    prefix = f"{PATCHES_DIR.name}/"
    for path in entry_paths:
        try:
            entry = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if not isinstance(entry, dict):
            continue
        patch = entry.get("patch")
        if isinstance(patch, str) and patch.startswith(prefix):
            referenced.add(patch[len(prefix):])
    return sorted(name for name in patch_names if name not in referenced)


def run_selftest() -> int:
    """Embedded, no-external-files self-test for the patch-apply behaviour issue #1005
    added (check_patch_applies()/check_git_usable()) -- mirrors
    scripts/check-e2e-test-integrity.py's `--selftest` pattern: runs against synthetic
    fixtures built fresh in a scratch git repo, not against any file this repo ships,
    so it is re-verified by machine on every CI run rather than asserted once in a PR
    body and left to rot. This is the guard that answers acceptance criterion 1's own
    demand ("a guard proven only in the passing direction is exactly the defect class
    #1005 is about") on every future run, not just the one that introduced it.

    Returns 0 if every case behaved as expected, 1 (naming which case failed)
    otherwise. Never returns SCANNER_ERROR for a failed expectation -- that code is
    reserved for "this run's scratch-repo setup itself broke" (git init/chdir
    failing), a different condition from "the guard's logic gave the wrong answer".
    """
    failures: list[str] = []

    def expect(condition: bool, description: str) -> None:
        if not condition:
            failures.append(description)

    with tempfile.TemporaryDirectory(prefix="check-mutant-entries-parse-selftest-") as tmp:
        tmp_path = Path(tmp)
        init = subprocess.run(["git", "init", "-q", "."], cwd=tmp_path, capture_output=True, text=True)
        if init.returncode != 0:
            print(
                f"SELFTEST SCANNER ERROR: could not `git init` a scratch repo for the "
                f"selftest fixtures: {init.stderr.strip()}",
                file=sys.stderr,
            )
            return SCANNER_ERROR

        (tmp_path / "fixture.txt").write_text("line1\nline2\nline3\n", encoding="utf-8")

        # A normal, well-formed patch matching the fixture exactly -- the positive
        # control. Without this, a check_patch_applies() that always reported a
        # violation would pass every negative case below.
        (tmp_path / "clean.patch").write_text(
            "diff --git a/fixture.txt b/fixture.txt\n"
            "index 0000000..1111111 100644\n"
            "--- a/fixture.txt\n"
            "+++ b/fixture.txt\n"
            "@@ -1,3 +1,3 @@\n"
            " line1\n"
            "-line2\n"
            "+CHANGED\n"
            " line3\n",
            encoding="utf-8",
        )

        # Same shape, but one context line no longer matches the fixture -- a
        # miniature reproduction of the #1005 incident itself (an unrelated edit
        # landing inside a patch's fixed context, so the PATCH is unchanged but the
        # TREE under it moved).
        (tmp_path / "stale.patch").write_text(
            "diff --git a/fixture.txt b/fixture.txt\n"
            "index 0000000..1111111 100644\n"
            "--- a/fixture.txt\n"
            "+++ b/fixture.txt\n"
            "@@ -1,3 +1,3 @@\n"
            " line1\n"
            "-THIS-CONTEXT-LINE-DOES-NOT-MATCH-THE-FIXTURE\n"
            "+CHANGED\n"
            " line3\n",
            encoding="utf-8",
        )
        # "missing.patch" is deliberately never written -- an unreadable-by-construction
        # fixture (os.access() on a nonexistent path is always False, portably, even
        # for a process running as root -- unlike a chmod 000 fixture, which a
        # root-running CI container would still be able to read).

        cwd = Path.cwd()
        try:
            os.chdir(tmp_path)
        except OSError as exc:
            print(f"SELFTEST SCANNER ERROR: could not chdir into scratch repo: {exc}", file=sys.stderr)
            return SCANNER_ERROR

        try:
            violation, scanner_problem = check_patch_applies(Path("clean.patch"))
            expect(
                violation is None and scanner_problem is None,
                "positive control: a well-formed patch against a matching fixture "
                f"must apply cleanly (got violation={violation!r}, "
                f"scanner_problem={scanner_problem!r})",
            )

            violation, scanner_problem = check_patch_applies(Path("stale.patch"))
            expect(
                violation is not None and scanner_problem is None,
                "negative control: a patch whose context no longer matches the tree "
                "must be reported as a VIOLATION (exit-1 class), never a scanner "
                f"problem (got violation={violation!r}, scanner_problem={scanner_problem!r})",
            )
            if violation is not None:
                expect("stale.patch" in violation, f"violation message must name the patch file, got: {violation!r}")
                expect("fixture.txt" in violation, f"violation message must name the target file, got: {violation!r}")

            violation, scanner_problem = check_patch_applies(Path("missing.patch"))
            expect(
                violation is None and scanner_problem is not None,
                "a missing/unreadable patch file must be a SCANNER problem (exit-2 "
                "class), never reported as 'this patch does not apply' -- acceptance "
                f"criterion 3 (got violation={violation!r}, scanner_problem={scanner_problem!r})",
            )

            expect(
                check_git_usable() is None,
                "positive control: check_git_usable() must report clean (None) "
                "inside a real git work tree",
            )
        finally:
            os.chdir(cwd)

    # check_git_usable()'s negative path (git executable not found) is exercised by
    # temporarily pointing PATH at a directory guaranteed to contain no `git` binary
    # (a freshly created empty tempdir, never an empty string -- some libc/exec
    # implementations fall back to a compiled-in default search path for an empty
    # PATH, which could still contain a real `git` and make this case flaky).
    # Restored in `finally` no matter what, never left pointed at the empty dir for
    # anything else in this process.
    with tempfile.TemporaryDirectory(prefix="check-mutant-entries-parse-selftest-nogit-") as empty_dir:
        real_path = os.environ.get("PATH", "")
        try:
            os.environ["PATH"] = empty_dir
            msg = check_git_usable()
            expect(
                msg is not None and "git" in msg.lower(),
                f"negative control: check_git_usable() with no `git` on PATH must "
                f"report git as unavailable, got: {msg!r}",
            )
        finally:
            os.environ["PATH"] = real_path

    if failures:
        print("SELFTEST FAILED:", file=sys.stderr)
        for f in failures:
            print(f"  - {f}", file=sys.stderr)
        return 1

    print(
        "SELFTEST OK: a well-formed patch against a matching fixture applies cleanly "
        "(positive control); a patch whose context no longer matches the tree is "
        "reported as a violation naming both the patch and the target file, never as "
        "a scanner problem; a missing patch file is reported as a scanner problem, "
        "never as 'does not apply'; check_git_usable() reports clean inside a real "
        "repo and correctly reports git as unavailable with no git on PATH."
    )
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return run_selftest()

    try:
        valid_kinds = load_valid_kinds()
    except RuntimeError as exc:
        print(f"::error::guard scanner failed to load VALID_KINDS: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    if not ENTRIES_DIR.is_dir():
        print(f"::error::guard scanner: '{ENTRIES_DIR}' does not exist", file=sys.stderr)
        return SCANNER_ERROR

    entry_paths = sorted(ENTRIES_DIR.glob("*.json"))
    if not entry_paths:
        print(
            f"::error::guard scanner: no entry files found under '{ENTRIES_DIR}' "
            "(glob '*.json' matched nothing) -- refusing to report a clean scan of "
            "nothing, matching discover_entries()'s own refusal.",
            file=sys.stderr,
        )
        return SCANNER_ERROR

    entry_problems: dict[str, list[str]] = {}
    for path in entry_paths:
        problems = check_entry(path, valid_kinds)
        if problems:
            entry_problems[str(path)] = problems

    # --- issue #1005: every patch under test/mutants/patches/ must still `git apply
    # --check` cleanly against the tree this job checked out. See the module
    # docstring's "PATCH-APPLY CHECK" / "HONEST SCOPE" sections for the incident that
    # motivated this, the exact mechanism, and what this deliberately does not catch.
    git_precondition_error = check_git_usable()
    if git_precondition_error is not None:
        print(f"::error::guard scanner: {git_precondition_error}", file=sys.stderr)
        return SCANNER_ERROR

    if not PATCHES_DIR.is_dir():
        print(f"::error::guard scanner: '{PATCHES_DIR}' does not exist", file=sys.stderr)
        return SCANNER_ERROR

    patch_paths = sorted(PATCHES_DIR.glob("*.patch"))
    if not patch_paths:
        print(
            f"::error::guard scanner: no patch files found under '{PATCHES_DIR}' "
            "(glob '*.patch' matched nothing) -- refusing to report a clean scan of "
            "nothing.",
            file=sys.stderr,
        )
        return SCANNER_ERROR

    patch_violations: list[str] = []
    patch_scanner_problems: list[str] = []
    for patch_path in patch_paths:
        violation, scanner_problem = check_patch_applies(patch_path)
        if violation:
            patch_violations.append(violation)
        if scanner_problem:
            patch_scanner_problems.append(scanner_problem)

    if patch_scanner_problems:
        for problem in patch_scanner_problems:
            print(f"::error::{problem}", file=sys.stderr)
        print(
            f"::error::{len(patch_scanner_problems)} of {len(patch_paths)} mutant "
            f"patch file(s) under {PATCHES_DIR} could not even be analysed (shown "
            "above) -- whether they apply to the current tree is UNKNOWN, not "
            "confirmed clean, and must not be reported as either. Distinct from a "
            "patch that genuinely does not apply (exit 1) -- acceptance criterion 3.",
            file=sys.stderr,
        )
        return SCANNER_ERROR

    orphan_patches = find_orphan_patches(entry_paths, [p.name for p in patch_paths])
    if orphan_patches:
        print(
            f"INFO: {len(orphan_patches)} of {len(patch_paths)} patch file(s) under "
            f"{PATCHES_DIR} have no entries/*.json referencing them (issue #1005 "
            "acceptance criterion 4 -- reported, not silently skipped; they were "
            "still git-apply-checked above like every other patch): "
            + ", ".join(orphan_patches)
        )
    else:
        print(f"INFO: 0 orphan patch file(s) under {PATCHES_DIR} (every patch is referenced by an entry).")

    if entry_problems or patch_violations:
        if entry_problems:
            for path_str, problems in entry_problems.items():
                for problem in problems:
                    print(f"{path_str}: {problem}")
            print(
                f"::error::{len(entry_problems)} of {len(entry_paths)} mutant entry "
                "file(s) failed validation (shown above) -- "
                "test/mutants/run_mutant.py's discover_entries() will abort the "
                "ENTIRE mutation gate on the first one of these it loads, reporting "
                "zero mutants run rather than a false pass. Fix every file listed "
                "above, not just the first.",
                file=sys.stderr,
            )
        if patch_violations:
            for violation in patch_violations:
                print(violation)
            print(
                f"::error::{len(patch_violations)} of {len(patch_paths)} mutant "
                f"patch file(s) under {PATCHES_DIR} do NOT apply to the current tree "
                "(named above, one per line, each with its target file and git's own "
                "reason) -- test/mutants/run_mutant.py's apply_patch() will raise "
                "GateError(VERDICT_PATCH_DID_NOT_APPLY) for each of these the moment "
                "its entry is selected, failing the mutation-gate "
                "'mutants'/'meta-selftest' job. The most common cause (issue #1005): "
                "an unrelated commit -- often from another PR whose own CI could not "
                "see this patch at all, since internal infrastructure PR CI checks out "
                "refs/pull/N/head, never a merge ref (docs/ci-operations.md) -- "
                "edited a line INSIDE one of these patches' fixed context. "
                "Regenerate the patch(es) named above against the current tree.",
                file=sys.stderr,
            )
        return 1

    print(
        f"OK: all {len(entry_paths)} mutant entry file(s) under {ENTRIES_DIR} parse "
        f"and validate; all {len(patch_paths)} mutant patch file(s) under "
        f"{PATCHES_DIR} apply cleanly to the current tree."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
