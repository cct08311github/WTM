#!/usr/bin/env python3
"""CI guard + inventory tool: every `run:` step and every `uses:` step across this
repo's GitHub/Gitea Actions workflows must declare its own `timeout-minutes:`.

WHY (issue #926 cross-vendor review, finding #7): a prior version of this repo's
timeout work claimed "64 real-work steps checked, 13 documented exemptions, 0
unexplained gaps" in its commit message and PR body. That denominator (64) was
constructed by counting only `run:` steps plus `actions/upload-artifact`/
`actions/cache` `uses:` steps -- silently excluding every `actions/checkout`,
`actions/setup-dotnet`, `actions/setup-node`, and `actions/setup-python` step (27 of
them) from being counted as "real work" at all, even though every one of those is a
network call that can hang. `continue-on-error: true` (present on the cache steps)
does not fix this either: that flag tolerates a step that *returns* an error: it does
nothing for a step that never returns. Six of the missing 13 were `actions/cache@v4`
steps -- exactly the scenario the original #926 issue exists to bound.

RULE: the denominator is EVERY step in EVERY workflow under .github/workflows/ that
has a `run:` key or a `uses:` key, in every job, full stop -- no category of `uses:`
action is excluded a priori. The one and only exclusion is a whole FILE:
`.github/workflows/publish-nuget.yml`, and it is excluded for a stated, temporal
reason (see EXCLUDED_FILES below), not because its steps aren't "real work". This
script prints that exclusion and the reason on every run so the exemption cannot be
read as broader than it is.

There is deliberately no per-step exemption list. Earlier drafts of this timeout work
carried 13 "documented exemptions" (cache steps under continue-on-error, `if:
failure()`-only diagnostics, a background-launch step); every one of those now has
its own timeout-minutes instead of an exemption, so the passing state of this script
is "every step in scope has a timeout", not "every step in scope has a timeout or a
written excuse". If a future step genuinely cannot carry a timeout, add it to
EXEMPT_STEPS below with an inline reason -- do not silently narrow the denominator
loop to make it disappear the way the 64-count did.

NOTE ON SCOPE, read together with docs/ci-operations.md's timeout-coverage section:
this script only proves step-level `timeout-minutes:` coverage. It cannot and does
not prove the JOB itself is bounded end-to-end -- `services:` containers (see
integration-test.yml's `mssql` service) are pulled/started/health-checked by the
runner BEFORE the first step executes, so no step's timeout-minutes reaches back to
cover that phase. That is a real, separate gap, documented where it lives, not
something this script's PASS result papers over.

NO PyYAML (2026-07-31, wiring follow-up): this script is invoked from
mutation-gate.yml's `changes` job, which is deliberately the cheap, dependency-free
job that runs on every push/PR with no path filter and gates three expensive jobs
downstream -- its three sibling guards (check-gitea-token-not-sourced.py,
check-jwt-key-literal-blocklisted.py, check-mutant-entries-parse.py) are all stdlib
+ git only, on purpose, so that job stays a few seconds and never depends on a
package-index round-trip on a capacity-2 runner. `pip install pyyaml` was tried
first and rejected: it would add a network call to every PR and make the whole gate
fail whenever the index is unreachable, trading a correct guard for a flaky one.

Instead this module hand-parses the small structural subset of YAML these files
actually use, keyed on indentation depth (see `_leading_spaces`/`_is_blank_or_comment`
and `_scan_sequence` below). This is deliberately NOT a general YAML parser -- it
only needs to find, per workflow file: the `jobs:` mapping, each job's `steps:`
sequence, each step's own top-level keys (`run`/`uses`/`name`/`timeout-minutes`), and
nothing nested inside those keys' own values (a `with:` sub-mapping, a `run: |` block
scalar's body, an `env:` block). The load-bearing invariant is INDENTATION EXACTNESS:
a step's own keys are matched only at the exact column established by that step's
first key (right after its `- `), never by searching for a string anywhere inside
the step's line range. That is what stops a `with:` parameter or a `run:` heredoc
line that merely CONTAINS the text "timeout-minutes:" from being mistaken for the
step's own declaration, and what stops a commented-out `# timeout-minutes: 5` from
counting (a comment line starts with `#`, not with the key text, at any indentation).
Cross-checked line-for-line against a PyYAML-based reference implementation run
locally (not committed -- see the PR/commit history for that session's transcript);
any future doubt about a specific file should be re-verified the same way rather than
trusted by inspection alone.

Exit codes: 0 clean (every in-scope step has timeout-minutes), 1 one or more in-scope
steps are missing timeout-minutes (each printed above the summary), 2 scanner error
(a workflow file failed to parse, or the workflows directory could not be listed) --
distinct from 1 so a scanner failure can never be silently reported as a clean tree.
"""
import glob
import re
import sys
from pathlib import Path

WORKFLOWS_DIR = Path(".github/workflows")

# File-level exclusions. Each entry MUST carry a reason -- this is the one place a
# whole file's steps are allowed to fall outside the denominator, and the reason is
# printed on every run so it stays honest and visible, not a silent carve-out. This
# is a plain module-level dict literal (data the script reads), not a condition
# buried in control flow, on purpose: once PR #937 merges, removing the exclusion is
# a one-line edit here, not a hunt through the scan logic below.
#
# 2026-07-31: PR #937 merged (as 3b894df80). The `publish-nuget.yml` exclusion that
# used to live here has been removed -- the file's final shape is now in scope like
# every other workflow, with its own step-level timeout-minutes added in the same
# commit that removed this entry. Empty on purpose: kept as a dict, not deleted
# outright, so the NEXT time a file genuinely needs a temporary exclusion, the
# mechanism (and this comment recording that it has been used and retired once
# already) is still here to reuse rather than reinvent.
EXCLUDED_FILES: dict[str, str] = {}

# Step-level exemptions. Keyed by (workflow filename, job id, step name-or-uses).
# Empty on purpose (see module docstring) -- kept as a named, auditable mechanism for
# the day a step genuinely cannot carry a timeout, instead of narrowing the scan loop.
EXEMPT_STEPS: dict[tuple[str, str, str], str] = {}

SCANNER_ERROR = 2

# Keys this script cares about at a step's own indentation level. Deliberately a
# small, closed set -- this is not a general key extractor.
_STEP_KEY_RE = re.compile(r"^(run|uses|name|timeout-minutes):(\s|$)")
_BARE_KEY_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_.-]*):\s*(#.*)?$")


class WorkflowParseError(ValueError):
    """Raised when a workflow file doesn't match the structural shape this
    hand-rolled scanner assumes (consistent 2-space-multiple indentation, a
    top-level `jobs:` mapping, bare job-id keys, a `steps:` sequence per job).
    Treated by main() as a scanner error (exit 2), never as "zero steps found"."""


def _leading_spaces(line: str) -> int:
    return len(line) - len(line.lstrip(" "))


def _is_blank_or_comment(line: str) -> bool:
    stripped = line.strip()
    return stripped == "" or stripped.startswith("#")


def _next_meaningful(lines: list[str], start: int) -> int:
    """Returns the index of the next non-blank, non-full-line-comment line at or
    after `start`, or len(lines) if none remains. Only meant for STRUCTURAL
    discovery (finding job ids, a `steps:` key, a sequence's first `- ` item) --
    never used inside a step's own body, where exact-indentation matching alone is
    both sufficient and safer (see module docstring)."""
    i = start
    n = len(lines)
    while i < n and _is_blank_or_comment(lines[i]):
        i += 1
    return i


def _scan_sequence(
    lines: list[str], start: int
) -> tuple[list[tuple[int, int]], int]:
    """Scans a YAML block sequence (here, always a `steps:` list) starting at line
    index `start` (the first line after the `steps:` key). Returns
    (list of (item_start, item_end) line-index ranges, one per `- ` item; index of
    the first line after the whole sequence). The sequence's own indentation is
    established from its first item and every subsequent item is required to sit at
    that exact indentation -- anything shallower ends the sequence, anything deeper
    without first being consumed as part of an item's own range is a shape this
    scanner does not understand and raises WorkflowParseError rather than guess."""
    items: list[tuple[int, int]] = []
    n = len(lines)
    k = _next_meaningful(lines, start)
    if k >= n:
        return items, k
    seq_indent = _leading_spaces(lines[k])
    while k < n:
        k = _next_meaningful(lines, k)
        if k >= n:
            break
        indent = _leading_spaces(lines[k])
        if indent < seq_indent:
            break
        if indent != seq_indent or not lines[k].lstrip(" ").startswith("- "):
            raise WorkflowParseError(
                f"line {k + 1}: expected a `- ` sequence item at indent "
                f"{seq_indent}, got: {lines[k]!r}"
            )
        item_start = k
        k += 1
        while k < n:
            probe = _next_meaningful(lines, k)
            if probe >= n or _leading_spaces(lines[probe]) <= seq_indent:
                k = probe
                break
            k = probe + 1
        item_end = k
        items.append((item_start, item_end))
    return items, k


def _extract_step_keys(
    lines: list[str], item_start: int, item_end: int
) -> dict[str, str]:
    """Given one step's line range (as produced by `_scan_sequence`), returns a dict
    of the step's OWN top-level keys (from the closed set `_STEP_KEY_RE` matches) to
    their inline value text. A key is only recognized at the exact column
    established by the first key on the `- ` line itself -- a `with:`/`env:`
    sub-mapping's keys, or a `run: |` block scalar's body lines, are always MORE
    indented than that column in valid YAML and are therefore never inspected at
    all, regardless of what text they contain. A commented-out
    `# timeout-minutes: 5` line does not match `_STEP_KEY_RE` (it starts with `#`,
    not with the key text) at any indentation, so it is never counted either."""
    first_line = lines[item_start]
    dash_indent = _leading_spaces(first_line)
    after_dash = first_line[dash_indent:]
    if not after_dash.startswith("- "):
        raise WorkflowParseError(
            f"line {item_start + 1}: sequence item does not start with '- ': "
            f"{first_line!r}"
        )
    owned_indent = dash_indent + 2
    found: dict[str, str] = {}

    def consider(content: str) -> None:
        m = _STEP_KEY_RE.match(content)
        if m:
            key = m.group(1)
            value = content[len(key) + 1 :].strip()
            # Strip one layer of matching quotes so a step written `name: "Guard:
            # ..."` (needed in YAML because the label itself contains a `:`) reports
            # the same label text a real YAML parser would -- these files never put
            # an escaped quote inside a quoted step name, so a plain strip is exact,
            # not an approximation; verified via a line-for-line diff against a
            # PyYAML-based reference implementation across every workflow in scope.
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            found.setdefault(key, value)

    consider(after_dash[2:])
    for k in range(item_start + 1, item_end):
        line = lines[k]
        if _is_blank_or_comment(line):
            continue
        if _leading_spaces(line) != owned_indent:
            continue
        consider(line[owned_indent:])
    return found


def iter_steps(workflow_path: Path):
    """Yields (job_id, step_index, keys_dict) for every step in every job of one
    workflow file, where keys_dict maps this script's closed key set
    (`run`/`uses`/`name`/`timeout-minutes`) to inline value text for whichever of
    those the step declares at its own indentation level. Raises
    WorkflowParseError on any structural shape this scanner does not recognize --
    callers treat that as a scanner error (exit 2), not "zero steps found"."""
    text = workflow_path.read_text(encoding="utf-8")
    lines = text.split("\n")
    n = len(lines)

    jobs_idx = None
    i = 0
    while i < n:
        if not _is_blank_or_comment(lines[i]):
            if _leading_spaces(lines[i]) == 0 and lines[i].strip() == "jobs:":
                jobs_idx = i
                break
            if _leading_spaces(lines[i]) == 0:
                pass  # some other top-level key before jobs: -- keep scanning
        i += 1
    if jobs_idx is None:
        raise WorkflowParseError(f"{workflow_path}: no top-level 'jobs:' key found")

    i = jobs_idx + 1
    job_indent: int | None = None
    while i < n:
        i = _next_meaningful(lines, i)
        if i >= n:
            break
        indent = _leading_spaces(lines[i])
        if job_indent is None:
            job_indent = indent
        if indent < job_indent:
            break
        if indent != job_indent:
            raise WorkflowParseError(
                f"{workflow_path}:{i + 1}: expected a job-id key at indent "
                f"{job_indent}, got: {lines[i]!r}"
            )
        m = _BARE_KEY_RE.match(lines[i].strip())
        if not m:
            raise WorkflowParseError(
                f"{workflow_path}:{i + 1}: expected a bare 'job-id:' key, got: "
                f"{lines[i]!r}"
            )
        job_id = m.group(1)

        body_start = i + 1
        j = body_start
        job_body_indent: int | None = None
        steps_after: int | None = None
        while j < n:
            j = _next_meaningful(lines, j)
            if j >= n:
                break
            bindent = _leading_spaces(lines[j])
            if bindent <= job_indent:
                break
            if job_body_indent is None:
                job_body_indent = bindent
            if bindent == job_body_indent and lines[j].strip() == "steps:":
                steps_after = j + 1
                j += 1
                break
            j += 1

        next_job_probe = j
        if steps_after is not None:
            items, after_seq = _scan_sequence(lines, steps_after)
            for step_idx, (item_start, item_end) in enumerate(items):
                keys = _extract_step_keys(lines, item_start, item_end)
                yield job_id, step_idx, keys
            next_job_probe = after_seq
        else:
            # This job has no `steps:` key at all (not expected for any job in
            # scope today, but not this scanner's job to enforce) -- resume the
            # outer job-id search from wherever the body scan stopped.
            next_job_probe = j

        # Resume searching for the NEXT job id from whichever line we stopped at
        # (either right after this job's steps: sequence, or right after its body
        # if it had none) -- must land back at job_indent or shallower, or we've
        # left the jobs: mapping (handled by the loop's own indent<job_indent check
        # on its next iteration).
        i = next_job_probe


def step_label(keys: dict[str, str]) -> str:
    if keys.get("name"):
        return keys["name"]
    if keys.get("uses"):
        return keys["uses"]
    run = keys.get("run", "")
    stripped = run.strip()
    if not stripped:
        return "(empty run:)"
    if stripped in ("|", ">", "|-", "|+", ">-", ">+") or stripped.startswith(
        ("|", ">")
    ):
        return "(multi-line run:)"
    return stripped.splitlines()[0][:60]


def main() -> int:
    if not WORKFLOWS_DIR.is_dir():
        print(f"::error::{WORKFLOWS_DIR} does not exist or is not a directory", file=sys.stderr)
        return SCANNER_ERROR

    all_files = sorted(Path(p) for p in glob.glob(str(WORKFLOWS_DIR / "*.yml")))
    if not all_files:
        print(f"::error::no *.yml files found under {WORKFLOWS_DIR}", file=sys.stderr)
        return SCANNER_ERROR

    scanned_files = [f for f in all_files if f.name not in EXCLUDED_FILES]

    print(f"Workflow files under {WORKFLOWS_DIR}: {len(all_files)}")
    for f in all_files:
        if f.name in EXCLUDED_FILES:
            print(f"  EXCLUDED: {f.name} -- {EXCLUDED_FILES[f.name]}")
        else:
            print(f"  scanned:  {f.name}")
    print()

    total = 0
    with_timeout = 0
    missing: list[tuple[Path, str, str, str]] = []
    exempted: list[tuple[Path, str, str, str]] = []

    try:
        for f in scanned_files:
            for job_id, _idx, keys in iter_steps(f):
                has_run = "run" in keys
                has_uses = "uses" in keys
                if not (has_run or has_uses):
                    continue  # not a "real-work" step: no command, no action call
                total += 1
                label = step_label(keys)
                kind = "uses" if has_uses else "run"
                if "timeout-minutes" in keys:
                    with_timeout += 1
                    continue
                exempt_key = (f.name, job_id, label)
                if exempt_key in EXEMPT_STEPS:
                    exempted.append((f, job_id, label, kind))
                    continue
                missing.append((f, job_id, label, kind))
    except WorkflowParseError as exc:
        print(f"::error::failed to parse a workflow file: {exc}", file=sys.stderr)
        return SCANNER_ERROR

    denom_note = (
        f"DENOMINATOR: every `run:` step and every `uses:` step, in every job, "
        f"across all {len(scanned_files)} scanned workflow file(s) "
        f"({', '.join(f.name for f in scanned_files)}) -- {len(all_files) - len(scanned_files)} "
        f"file excluded (see above)."
    )
    print(denom_note)
    print(f"Total real-work steps in scope: {total}")
    print(f"  with timeout-minutes:    {with_timeout}")
    print(f"  documented exemptions:   {len(exempted)}")
    print(f"  MISSING timeout-minutes: {len(missing)}")
    print()

    if exempted:
        print("Documented exemptions:")
        for f, job_id, label, kind in exempted:
            reason = EXEMPT_STEPS[(f.name, job_id, label)]
            print(f"  {f} :: job={job_id} :: [{kind}] {label} -- {reason}")
        print()

    if missing:
        print("::error::the following real-work steps have NO timeout-minutes and are NOT a documented exemption:")
        for f, job_id, label, kind in missing:
            print(f"  {f} :: job={job_id} :: [{kind}] {label}")
        print()
        print("WORKFLOW_TIMEOUT_AUDIT_RESULT: FAIL")
        return 1

    print("WORKFLOW_TIMEOUT_AUDIT_RESULT: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
