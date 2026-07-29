"""
Regression tests for issue #886 (PR #897) review rounds 3-4.

What this guards against
=========================
run_tests() launches a browser, then runs every TC, then closes the browser —
none of that lifecycle work sat inside a try/except until round 3, and even
after round 3's fix, two escape hatches remained (found by review round 4's
fault injection, closed in the same round):

  1. The guard sat *inside* `async with async_playwright() as p:`, so the
     context manager's own `__aexit__` (driver teardown) was not covered.
  2. The abort handler printed its diagnostic BEFORE appending the synthetic
     result — a `print()` failure (stdout closed -> BrokenPipeError) would
     have skipped `results.append()` entirely, the "diagnostics before
     accounting" mistake round 2's MEDIUM 3 fix was supposed to have
     eliminated for good.

A guard against a false green that nothing tests is exactly the kind of
decoration this PR spent rounds removing elsewhere in this file. This script
is that test, in three parts:

  test_launch_failure_no_false_green()
      Forces `chromium.launch()` to raise (the cheapest place — before any
      real browser, page, or HTTP server is ever touched) and asserts the
      emitted summary line does NOT read as green under this repo's own
      documented CI convention, verbatim from /CLAUDE.md's "CI red does not
      mean failed" section (not a paraphrase of it):

          e2e -> `FAIL: 0` **and** `ERROR: 0` in the
          `Total: N | PASS: n | FAIL: n | ERROR: n | SKIP: n` summary line

  test_aexit_failure_does_not_escape()
      Lets `chromium.launch()` and `browser.close()` succeed (with `tc_nums`
      empty so no real TC is attempted), then forces the *context manager's*
      `__aexit__` to raise. Asserts run_tests() still returns normally with a
      synthetic ERROR result, instead of the exception escaping past the
      `try` the way review round 4 proved it did before this fix
      (`async_exit ESCAPED=RuntimeError ... SUMMARY=[]`).

  test_abort_report_print_failure_does_not_lose_result()
      Forces both the launch failure AND the abort handler's own diagnostic
      print to raise. Asserts the synthetic SUITE-ABORT result is still in
      `results` regardless — proving accounting happens before, and does not
      depend on, the diagnostic print succeeding.

Run directly (no demo server, no real browser needed — none of these ever
get past the monkeypatched Playwright layer):

    cd test/e2e && python test_lifecycle_abort_no_false_green.py

Exits 0 and prints "ALL CHECKS PASSED" on success; a failed assertion prints
its message and exits non-zero.
"""

import asyncio
import builtins
import contextlib
import io

import playwright.async_api as pw_api

import wtm_e2e_tests as wtm

LAUNCH_FAILURE_MESSAGE = "forced browser launch failure for #886 review round 3 verification"
AEXIT_FAILURE_MESSAGE = "forced async_playwright exit failure"
ABORT_PRINT_MARKER = "未預期的例外中止了測試迴圈"


class _ForcedLaunchFailure(RuntimeError):
    pass


class _ForcedAexitFailure(RuntimeError):
    pass


class _ForcedAbortPrintFailure(RuntimeError):
    pass


# ─── scenario 1: chromium.launch() raises (row 3 — cheapest to force) ──────

class _LaunchFailsChromium:
    @staticmethod
    async def launch(**kwargs):
        raise _ForcedLaunchFailure(LAUNCH_FAILURE_MESSAGE)


class _LaunchFailsPlaywrightInstance:
    chromium = _LaunchFailsChromium()


class _LaunchFailsCM:
    async def __aenter__(self):
        return _LaunchFailsPlaywrightInstance()

    async def __aexit__(self, exc_type, exc, tb):
        return False  # never suppress — matches the real async_playwright()'s contract


# ─── scenario 2: launch/close succeed, __aexit__ raises (row 12) ───────────

class _FakeBrowser:
    async def close(self):
        pass  # tc_nums=[] below, so nothing ever opened a context on this browser


class _AexitFailsChromium:
    @staticmethod
    async def launch(**kwargs):
        return _FakeBrowser()


class _AexitFailsPlaywrightInstance:
    chromium = _AexitFailsChromium()


class _AexitFailsCM:
    async def __aenter__(self):
        return _AexitFailsPlaywrightInstance()

    async def __aexit__(self, exc_type, exc, tb):
        raise _ForcedAexitFailure(AEXIT_FAILURE_MESSAGE)


@contextlib.contextmanager
def _patched_async_playwright(fake_cm_factory):
    original = pw_api.async_playwright
    # run_tests() does `from playwright.async_api import async_playwright`
    # *inside* the function body, resolved at call time — patching the module
    # attribute before calling it is enough, no need to touch run_tests() itself.
    pw_api.async_playwright = fake_cm_factory
    try:
        yield
    finally:
        pw_api.async_playwright = original


@contextlib.contextmanager
def _print_that_raises_on_marker(marker, exc):
    """Delegates to the real print() for everything except a message containing
    `marker`, which raises `exc` instead — targets exactly one diagnostic call
    site without breaking every other print() in the process (including this
    test's own reporting)."""
    real_print = builtins.print

    def fake_print(*args, **kwargs):
        text = " ".join(str(a) for a in args)
        if marker in text:
            raise exc
        return real_print(*args, **kwargs)

    builtins.print = fake_print
    try:
        yield
    finally:
        builtins.print = real_print


async def _run(tc_nums):
    captured = io.StringIO()
    with contextlib.redirect_stdout(captured):
        results = await wtm.run_tests(tc_nums=tc_nums, headless=True)
    return results, captured.getvalue()


def test_launch_failure_no_false_green():
    print("=== test_launch_failure_no_false_green ===")
    with _patched_async_playwright(_LaunchFailsCM):
        results, output = asyncio.run(_run([1, 2, 3]))

    summary_lines = [line for line in output.splitlines() if line.strip().startswith("Total:")]
    assert len(summary_lines) == 1, (
        f"expected exactly one 'Total: ...' summary line, found {len(summary_lines)}:\n{output}"
    )
    summary_line = summary_lines[0]
    print(f"Captured summary line: {summary_line!r}")

    # --- the actual CI convention, verbatim from CLAUDE.md, not a paraphrase ---
    fail_reads_zero = "FAIL: 0" in summary_line
    error_reads_zero = "ERROR: 0" in summary_line
    would_read_green = fail_reads_zero and error_reads_zero
    print(f"'FAIL: 0' in summary line: {fail_reads_zero}")
    print(f"'ERROR: 0' in summary line: {error_reads_zero}")
    assert not would_read_green, (
        "FALSE GREEN: an aborted run (browser never launched, zero TCs ran) "
        f"still reads as passing under this repo's own CI convention: {summary_line!r}"
    )

    abort_entries = [r for r in results if r.get("tc") == "SUITE-ABORT"]
    assert len(abort_entries) == 1, f"expected exactly one SUITE-ABORT entry, got {abort_entries}"
    abort_entry = abort_entries[0]
    assert abort_entry["status"] == "ERROR", abort_entry
    assert LAUNCH_FAILURE_MESSAGE in abort_entry["error"], (
        f"synthetic entry doesn't mention what actually aborted: {abort_entry['error']!r}"
    )
    assert "3/3" in abort_entry["error"] or "[1, 2, 3]" in abort_entry["error"], (
        f"synthetic entry doesn't say how many TCs never ran: {abort_entry['error']!r}"
    )
    print(f"Synthetic result: {abort_entry}")

    # --- main()'s exit-code logic, replicated exactly (line for line from main()) ---
    failed = sum(1 for r in results if r["status"] in ("FAIL", "ERROR"))
    assert failed > 0, "main() would exit 0 on an aborted run — sys.exit(1 if failed > 0 else 0)"
    print(f"main()'s exit-code check: failed={failed} -> sys.exit(1) (non-zero, correct)")
    print("PASS\n")


def test_aexit_failure_does_not_escape():
    print("=== test_aexit_failure_does_not_escape ===")
    escaped = None
    results = None
    try:
        with _patched_async_playwright(_AexitFailsCM):
            # tc_nums=[]: no real TC is attempted, isolating this test to
            # exactly one thing — does __aexit__'s own exception get caught?
            results, output = asyncio.run(_run([]))
    except Exception as e:  # noqa: BLE001 — this IS the failure mode under test
        escaped = e

    assert escaped is None, (
        f"async_exit ESCAPED={type(escaped).__name__}: {escaped} "
        f"SUMMARY={results if results is not None else '[]'}"
    )
    print("async_exit did not escape run_tests()")

    abort_entries = [r for r in results if r.get("tc") == "SUITE-ABORT"]
    assert len(abort_entries) == 1, f"expected exactly one SUITE-ABORT entry, got {abort_entries}"
    assert AEXIT_FAILURE_MESSAGE in abort_entries[0]["error"], abort_entries[0]
    print(f"Synthetic result: {abort_entries[0]}")
    print("PASS\n")


def test_abort_report_print_failure_does_not_lose_result():
    print("=== test_abort_report_print_failure_does_not_lose_result ===")
    escaped = None
    results = None
    try:
        with _patched_async_playwright(_LaunchFailsCM):
            with _print_that_raises_on_marker(ABORT_PRINT_MARKER, _ForcedAbortPrintFailure(
                "forced abort-report print failure"
            )):
                results, output = asyncio.run(_run([1, 2, 3]))
    except Exception as e:  # noqa: BLE001 — this IS the failure mode under test
        escaped = e

    assert escaped is None, (
        f"ABORT_PRINT ESCAPED={type(escaped).__name__}: {escaped} "
        f"SUMMARY={results if results is not None else '[]'}"
    )
    print("abort-report print failure did not escape run_tests()")

    abort_entries = [r for r in results if r.get("tc") == "SUITE-ABORT"]
    assert len(abort_entries) == 1, (
        "RESULT LOST: the synthetic ERROR entry is missing after a forced print "
        f"failure — accounting depended on diagnostics succeeding. results={results}"
    )
    assert abort_entries[0]["status"] == "ERROR", abort_entries[0]
    print(f"Synthetic result survived the print failure: {abort_entries[0]}")
    print("PASS\n")


def main():
    test_launch_failure_no_false_green()
    test_aexit_failure_does_not_escape()
    test_abort_report_print_failure_does_not_lose_result()
    print("ALL CHECKS PASSED — an aborted run cannot read as a CI-green pass, "
          "and no lifecycle or diagnostic failure along that path can lose the result.")


if __name__ == "__main__":
    main()
