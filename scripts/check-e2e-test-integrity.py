#!/usr/bin/env python3
"""CI guard (issue #917): stop a test/e2e/wtm_e2e_tests.py TC from being able to pass
without ever having checked anything.

WHY: this repo's own e2e suite has repeatedly reported PASS when the thing under test
was broken -- 12 sites swallowed an exception and passed unconditionally (#898), three
security-themed TCs asserted nothing at all (#905), and this repo's own #886 shipped a
guard whose success path accidentally made a zero-tests-executed abort read as
"Total: 0 | FAIL: 0 | ERROR: 0", satisfying CLAUDE.md's own CI-reading rule. Every merge
decision in this repo cites "e2e green" as evidence (CLAUDE.md "CI red does not mean
failed"); a TC that structurally cannot fail turns that evidence hollow. #898 and #905
fixed the individual instances found by hand. This script exists to catch the CLASS,
mechanically, on every PR -- not to re-litigate those two issues' inventories.

THIS IS AN ABSOLUTE RULE, NOT A RATCHET (2026-07 design review, binding): there is no
baseline file, no exemption list, no frozen violation count. A count-based ratchet was
explicitly rejected -- it has a swap hole (delete one old violation, add one new one,
the count is unchanged and the gate stays green), and this repo's own coverage ratchet
has already demonstrated that a manually-raised threshold just stagnates. Every
violation this script can see must be fixed before the PR that introduces (or leaves)
it can merge, full stop.

FOUR CHECKS, run against TEST_TARGET (test/e2e/wtm_e2e_tests.py by default; the first
positional argument overrides it, used for the historical replay demonstration below):

  1. Every TC function registered in TC_REGISTRY has at least one `assert` statement
     reachable from its own body (descending into if/for/while/with/try -- including
     try bodies, handler bodies, and finally blocks -- but NEVER into a nested
     def/async def/lambda/class, which starts a new scope this check does not enter).

  2. The ONLY exemption from (1) is structural, matched by SHAPE, never by function
     name (no allowlist): a body consisting of exactly a docstring `Expr` followed by
     exactly one unconditional `raise TestSkipped(...)` statement -- tc_36's current
     shape (issue #681: no HasMainHost demo scenario exists to test tenant-switch
     against, so it deliberately raises TestSkipped instead of faking a pass). A
     function with a docstring and a raise buried inside an `if`, or with any other
     statement alongside them, does not match and falls back to needing a real assert.

  3. No exception handler that can catch AssertionError may fail to re-raise, in any
     `try` block anywhere in the file whose OWN body (same non-crossing-scope walk as
     check 1) contains a literal `assert`. "Can catch AssertionError" covers a bare
     `except:`, `except Exception:`, `except AssertionError:`, and any tuple form
     containing one of those (`except (ValueError, AssertionError):`, etc.). "Re-raise"
     means a bare `raise` (no expression) appears anywhere in the handler's own body --
     `raise TestSkipped(...)` does NOT count: it launders the original AssertionError
     into a different, non-failing exception instead of propagating it, and this is
     specifically flagged (`except AssertionError: raise TestSkipped(...)` turns a real
     FAIL into a SKIP -- exactly the shape that made #886's own summary line lie).

  4. Every top-level (module-level) `tc_*` function must be referenced from some
     TC_REGISTRY entry. A `tc_*` function that exists but was never wired into the
     registry never runs and never fails -- the e2e counterpart of #855 defect 4
     ("written but never wired up").

PLUS, independent of the above and flagged on its own: `assert <constant>` where the
constant is truthy (`assert True`, `assert 1`, `assert "msg"` used as the whole test
expression, not as the `assert x, "msg"` message) is always a violation, anywhere in
the file -- it is provably a no-op assertion, not a heuristic guess.

TC_REGISTRY is read as a literal `ast.Dict` -- this script never imports
wtm_e2e_tests.py (which would require Playwright and a running demo app just to lint
source text). A TC's registration is recognized by finding any `ast.Name` reference
inside its registry-entry tuple/list that resolves to a top-level function definition;
this script does not assume the tuple's positional shape (`(label, func, priority)`)
beyond "the function is referenced by bare name somewhere in the entry value".

WHAT THIS DELIBERATELY DOES NOT CATCH (2026-07 design review's accepted bypasses --
named here so a clean run is never read as "the suite is now trustworthy", only as
"these four specific defect shapes are absent"):

  - Tautological asserts that are not literal constants, e.g. `assert x == x` or
    `assert value` where `value` was just set two lines above to something guaranteed
    truthy. Whether an assert is tautological in that sense is undecidable by static
    syntax alone (it depends on data flow this script does not model); only the
    narrow, provable case of `assert <constant truthy>` is caught (see PLUS above).

  - An assert that lives in a helper function called BY a TC, with none in the TC's
    own body. Check 1 only looks at the TC's own scope (see its docstring above) and
    does not follow calls. This is intentional, not an oversight: the fix for a
    flagged TC is "add one top-level assert", which is supposed to be low-friction --
    chasing every call graph to credit a helper's assert back to its caller would
    both weaken the signal (a helper's assert may not even run on every TC path) and
    make the fix for a real violation "refactor your helper", which is a worse
    incentive than "add an assert".

  - Multi-level indirect swallowing: an inner try/except re-raises cleanly, but an
    OUTER try/except two levels up swallows what the inner one re-raised. Check 3
    inspects each `try` node independently by whether ITS OWN body contains a literal
    assert; it does not trace an exception's propagation path across nested try
    blocks. Every incident found in this repo so far (#898, #905) has been
    single-level (the try guarding the assert is the same try whose handler swallows
    it) -- this script matches that shape, not a hypothetical deeper one.

  - An author editing this script and its own `--selftest` fixtures in the same PR to
    make a real violation invisible. That edit is fully visible in the PR diff; no
    mechanism in this script (or any lint) can stop a reviewer-less rewrite of the
    lint itself. Out of scope for a static analysis tool by construction.

Exit codes, matching the four existing guards already wired into this workflow's
`changes` job (check-gitea-token-not-sourced.py, check-jwt-key-literal-blocklisted.py,
check-mutant-entries-parse.py, audit-workflow-timeouts.py): 0 clean, 1 one or more
violations found (each printed above the summary line), 2 could not analyse the target
at all -- parse failure, file missing, or no `TC_REGISTRY = {...}` assignment found at
module level. 2 is distinct from 0 specifically so a scanner failure is never read as
"clean tree" by a caller that only checks `== 0`.
"""
from __future__ import annotations

import ast
import sys
from dataclasses import dataclass
from pathlib import Path

DEFAULT_TARGET = Path("test/e2e/wtm_e2e_tests.py")
REGISTRY_NAME = "TC_REGISTRY"
SKIP_EXCEPTION_NAME = "TestSkipped"

SCANNER_ERROR = 2

# A new scope starts here -- check 1's and check 3's "own body" walks never descend
# past one of these, matching the "helper function" and "multi-level" accepted
# bypasses documented in the module docstring above.
_SCOPE_BOUNDARY = (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda, ast.ClassDef)

# Type names that, if named in an `except`, can catch an AssertionError. `BaseException`
# is also here for the (unusual but legal) `except BaseException:` spelling; the bare
# `except:` case is handled separately (see _handler_catches_assertion_error).
_CATCHES_ASSERTION_ERROR = {"AssertionError", "Exception", "BaseException"}


@dataclass(frozen=True)
class Violation:
    check: str
    lineno: int
    message: str

    def render(self, label: str) -> str:
        return f"{label}:{self.lineno}: [{self.check}] {self.message}"


@dataclass(frozen=True)
class AnalysisResult:
    scanner_error: str | None
    violations: tuple[Violation, ...]

    @property
    def ok(self) -> bool:
        return self.scanner_error is None and not self.violations


def exit_code_for(result: AnalysisResult) -> int:
    if result.scanner_error is not None:
        return SCANNER_ERROR
    if result.violations:
        return 1
    return 0


def _iter_own_scope(stmts: list[ast.stmt]):
    """Yields every statement reachable from `stmts` by descending into compound
    statements' own bodies (if/for/while/with/try -- including a Try's body, each
    handler's body, orelse, and finalbody), WITHOUT crossing into a nested
    def/async def/lambda/class, which starts a new scope this walk does not enter."""
    for stmt in stmts:
        yield stmt
        if isinstance(stmt, _SCOPE_BOUNDARY):
            continue
        for field in ("body", "orelse", "finalbody"):
            child = getattr(stmt, field, None)
            if child:
                yield from _iter_own_scope(child)
        for handler in getattr(stmt, "handlers", None) or ():
            yield from _iter_own_scope(handler.body)


def _has_own_assert(stmts: list[ast.stmt]) -> bool:
    return any(isinstance(s, ast.Assert) for s in _iter_own_scope(stmts))


def _has_bare_reraise(stmts: list[ast.stmt]) -> bool:
    return any(
        isinstance(s, ast.Raise) and s.exc is None for s in _iter_own_scope(stmts)
    )


def _except_type_names(expr: ast.expr | None) -> set[str]:
    """Returns the set of exception-type names an `except` clause names. A bare
    `except:` (expr is None) returns a sentinel that always intersects
    _CATCHES_ASSERTION_ERROR, since a bare except catches everything."""
    if expr is None:
        return {"BaseException"}
    if isinstance(expr, ast.Tuple):
        names: set[str] = set()
        for elt in expr.elts:
            names |= _except_type_names(elt)
        return names
    if isinstance(expr, ast.Name):
        return {expr.id}
    if isinstance(expr, ast.Attribute):
        return {expr.attr}
    return set()


def _handler_catches_assertion_error(handler: ast.ExceptHandler) -> bool:
    return bool(_except_type_names(handler.type) & _CATCHES_ASSERTION_ERROR)


def _is_docstring_expr(stmt: ast.stmt) -> bool:
    return (
        isinstance(stmt, ast.Expr)
        and isinstance(stmt.value, ast.Constant)
        and isinstance(stmt.value.value, str)
    )


def _is_skip_exemption_shape(body: list[ast.stmt]) -> bool:
    """Check 2: the ONLY exemption from check 1, matched by shape -- a docstring
    followed by exactly one unconditional `raise TestSkipped(...)`. Deliberately not
    name-based: any function whose body reduces to this exact two-statement shape is
    exempt, regardless of what it is called."""
    if len(body) != 2 or not _is_docstring_expr(body[0]):
        return False
    stmt = body[1]
    if not isinstance(stmt, ast.Raise) or not isinstance(stmt.exc, ast.Call):
        return False
    func = stmt.exc.func
    name = (
        func.id
        if isinstance(func, ast.Name)
        else func.attr
        if isinstance(func, ast.Attribute)
        else None
    )
    return name == SKIP_EXCEPTION_NAME


def _is_constant_truthy(expr: ast.expr) -> bool:
    if not isinstance(expr, ast.Constant):
        return False
    try:
        return bool(expr.value)
    except Exception:
        return False


def _find_registry(tree: ast.Module) -> ast.Dict | None:
    for node in tree.body:
        if not isinstance(node, ast.Assign):
            continue
        if not any(
            isinstance(t, ast.Name) and t.id == REGISTRY_NAME for t in node.targets
        ):
            continue
        if isinstance(node.value, ast.Dict):
            return node.value
    return None


def _registered_names(registry: ast.Dict) -> set[str]:
    """Extracts every bare-name function reference inside each registry entry's
    value, without assuming its positional shape -- any `ast.Name` found anywhere
    inside a `(label, func, priority)`-shaped tuple/list entry counts."""
    names: set[str] = set()
    for value in registry.values:
        elts = value.elts if isinstance(value, (ast.Tuple, ast.List)) else [value]
        for elt in elts:
            if isinstance(elt, ast.Name):
                names.add(elt.id)
    return names


def analyze_source(source: str, label: str) -> AnalysisResult:
    try:
        tree = ast.parse(source, filename=label)
    except SyntaxError as exc:
        return AnalysisResult(
            scanner_error=f"could not parse {label}: {exc}", violations=()
        )

    registry = _find_registry(tree)
    if registry is None:
        return AnalysisResult(
            scanner_error=(
                f"no top-level `{REGISTRY_NAME} = {{...}}` assignment found in {label}"
            ),
            violations=(),
        )

    top_level_defs: dict[str, ast.FunctionDef | ast.AsyncFunctionDef] = {
        node.name: node
        for node in tree.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
    }
    tc_universe = {name for name in top_level_defs if name.startswith("tc_")}
    registered = _registered_names(registry)

    violations: list[Violation] = []

    # Check 4: every top-level tc_* function must be registered.
    for name in sorted(tc_universe - registered):
        node = top_level_defs[name]
        violations.append(
            Violation(
                "unregistered-tc",
                node.lineno,
                f"{name} is a top-level tc_* function but is never referenced from "
                f"{REGISTRY_NAME} -- written but never wired up (see #855 defect 4).",
            )
        )

    # Checks 1 & 2: every REGISTERED function needs its own assert, unless it matches
    # the structural TestSkipped exemption.
    for name in sorted(registered & top_level_defs.keys()):
        node = top_level_defs[name]
        if _has_own_assert(node.body):
            continue
        if _is_skip_exemption_shape(node.body):
            continue
        violations.append(
            Violation(
                "no-assert",
                node.lineno,
                f"{name} is registered in {REGISTRY_NAME} but has no `assert` "
                "reachable from its own body, and does not match the structural "
                f"docstring+`raise {SKIP_EXCEPTION_NAME}(...)` exemption -- it can "
                "never fail.",
            )
        )

    # Check 3: no handler that can catch AssertionError may fail to re-raise, when
    # its try's own body contains a literal assert. Scanned across the WHOLE file
    # (not just registered TCs) -- see module docstring for why.
    for node in ast.walk(tree):
        if not isinstance(node, ast.Try):
            continue
        if not _has_own_assert(node.body):
            continue
        for handler in node.handlers:
            if not _handler_catches_assertion_error(handler):
                continue
            if _has_bare_reraise(handler.body):
                continue
            type_desc = (
                "bare `except:`"
                if handler.type is None
                else f"`except {ast.unparse(handler.type)}:`"
            )
            violations.append(
                Violation(
                    "swallowed-assert",
                    handler.lineno,
                    f"{type_desc} handler for the try at line {node.lineno} (whose "
                    "body contains an assert) does not re-raise with a bare `raise` "
                    "-- it can swallow a real AssertionError, or launder it into a "
                    "different exception (e.g. `except AssertionError: raise "
                    "TestSkipped(...)` turns a FAIL into a SKIP).",
                )
            )

    # Plus: assert <constant truthy>, anywhere -- a provable no-op, independent of
    # the four checks above.
    for node in ast.walk(tree):
        if isinstance(node, ast.Assert) and _is_constant_truthy(node.test):
            violations.append(
                Violation(
                    "constant-assert",
                    node.lineno,
                    f"`assert {ast.unparse(node.test)}` is always truthy -- this "
                    "assertion can never fail regardless of what it guards.",
                )
            )

    violations.sort(key=lambda v: v.lineno)
    return AnalysisResult(scanner_error=None, violations=tuple(violations))


# ─── --selftest: embedded fixtures, no external files ──────────────────────────────
#
# Each fixture is a complete, minimal, standalone module (its own TestSkipped class,
# its own TC_REGISTRY) -- analyze_source() never imports anything, so these are just
# text. The "clean" fixture is the positive control (issue #917 requirement): without
# it, a version of this script that always returned exit 1 would pass every negative
# fixture below and this file would never notice.

_FIXTURE_PREAMBLE = "class TestSkipped(Exception):\n    pass\n\n"

_FIXTURE_ZERO_ASSERT = (
    _FIXTURE_PREAMBLE
    + '''
async def tc_01_no_assert(page, **_):
    """Never asserts anything -- always PASS no matter what happened."""
    print("did something")
    return


TC_REGISTRY = {
    1: ("no assert", tc_01_no_assert, "P0"),
}
'''
)

_FIXTURE_SWALLOWED_ASSERT = (
    _FIXTURE_PREAMBLE
    + '''
async def tc_02_swallowed(page, **_):
    """Has a real assert, but a bare except around it eats the failure silently."""
    try:
        assert False, "should fail"
    except Exception:
        pass


TC_REGISTRY = {
    2: ("swallowed", tc_02_swallowed, "P0"),
}
'''
)

_FIXTURE_LAUNDERED_SKIP = (
    _FIXTURE_PREAMBLE
    + '''
async def tc_03_laundered(page, **_):
    """Its own AssertionError gets laundered into a SKIP instead of a FAIL."""
    try:
        assert False, "should fail"
    except AssertionError:
        raise TestSkipped("pretend this was never applicable")


TC_REGISTRY = {
    3: ("laundered", tc_03_laundered, "P0"),
}
'''
)

_FIXTURE_UNREGISTERED = (
    _FIXTURE_PREAMBLE
    + '''
async def tc_01_ok(page, **_):
    """Registered, and has a real (non-constant) assertion."""
    assert 1 + 1 == 2


async def tc_02_orphan(page, **_):
    """Exists at module level but was never added to TC_REGISTRY."""
    assert 1 + 1 == 2


TC_REGISTRY = {
    1: ("ok", tc_01_ok, "P0"),
}
'''
)

_FIXTURE_CLEAN = (
    _FIXTURE_PREAMBLE
    + '''
async def tc_01_ok(page, **_):
    """A normal TC with a real, non-constant assertion."""
    value = await page.title()
    assert value == "expected", f"got {value!r}"


async def tc_02_skip(page, **_):
    """Structurally exempt: docstring + a single unconditional raise TestSkipped(...)."""
    raise TestSkipped("no scenario available in this environment")


TC_REGISTRY = {
    1: ("ok", tc_01_ok, "P0"),
    2: ("skip", tc_02_skip, "SKIP"),
}
'''
)

_FIXTURE_UNPARSEABLE = "def broken(:\n    pass\n"


def _selftest_case(
    name: str,
    source: str,
    *,
    expect_exit: int,
    expect_message_substring: str | None = None,
    expect_check: str | None = None,
) -> list[str]:
    """Runs one embedded fixture through analyze_source() and returns a list of
    problem descriptions (empty if the fixture behaved exactly as expected)."""
    problems: list[str] = []
    result = analyze_source(source, name)
    actual_exit = exit_code_for(result)
    if actual_exit != expect_exit:
        problems.append(
            f"{name}: expected exit {expect_exit}, got {actual_exit} "
            f"(scanner_error={result.scanner_error!r}, "
            f"violations={[v.render(name) for v in result.violations]})"
        )
    if expect_check is not None:
        if not any(v.check == expect_check for v in result.violations):
            problems.append(
                f"{name}: expected a '{expect_check}' violation, got checks "
                f"{[v.check for v in result.violations]}"
            )
    if expect_message_substring is not None:
        if not any(
            expect_message_substring in v.message for v in result.violations
        ):
            problems.append(
                f"{name}: expected a violation message containing "
                f"{expect_message_substring!r}, got "
                f"{[v.message for v in result.violations]}"
            )
    return problems


def run_selftest() -> int:
    problems: list[str] = []

    problems += _selftest_case(
        "zero-assert-tc",
        _FIXTURE_ZERO_ASSERT,
        expect_exit=1,
        expect_check="no-assert",
        expect_message_substring="tc_01_no_assert",
    )
    problems += _selftest_case(
        "swallowed-assert",
        _FIXTURE_SWALLOWED_ASSERT,
        expect_exit=1,
        expect_check="swallowed-assert",
    )
    problems += _selftest_case(
        "laundered-skip",
        _FIXTURE_LAUNDERED_SKIP,
        expect_exit=1,
        expect_check="swallowed-assert",
        expect_message_substring="TestSkipped",
    )
    problems += _selftest_case(
        "unregistered-tc",
        _FIXTURE_UNREGISTERED,
        expect_exit=1,
        expect_check="unregistered-tc",
        expect_message_substring="tc_02_orphan",
    )
    problems += _selftest_case(
        "clean (positive control)",
        _FIXTURE_CLEAN,
        expect_exit=0,
    )
    problems += _selftest_case(
        "unparseable",
        _FIXTURE_UNPARSEABLE,
        expect_exit=2,
    )

    if problems:
        print("SELFTEST FAILED:")
        for p in problems:
            print(f"  - {p}")
        print(f"\n{len(problems)} selftest expectation(s) not met.")
        return 1

    print(
        "SELFTEST OK: zero-assert TC, swallowed-assert handler, "
        "except-AssertionError-raise-TestSkipped laundering, and an unregistered "
        "tc_* function all correctly exit 1 with the right violation named; the "
        "clean fixture (positive control) exits 0; unparseable input exits 2."
    )
    return 0


# ─── main ────────────────────────────────────────────────────────────────────────


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return run_selftest()

    positional = [a for a in argv if not a.startswith("--")]
    target = Path(positional[0]) if positional else DEFAULT_TARGET

    if not target.is_file():
        print(f"::error::{target} does not exist or is not a file", file=sys.stderr)
        return SCANNER_ERROR

    source = target.read_text(encoding="utf-8")
    result = analyze_source(source, str(target))

    if result.scanner_error is not None:
        print(f"::error::{result.scanner_error}", file=sys.stderr)
        return SCANNER_ERROR

    if result.violations:
        for v in result.violations:
            print(v.render(str(target)))
        print(
            f"::error::{len(result.violations)} e2e test integrity violation(s) "
            f"found in {target} (shown above) -- a TC that cannot fail converts a "
            "real failure into silence. See this script's module docstring for "
            "the four checks and what they deliberately do not catch.",
            file=sys.stderr,
        )
        return 1

    print(f"OK: no e2e test integrity violations found in {target}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
