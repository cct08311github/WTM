"""
Regression tests for issue #886 (PR #897) review rounds 3-5.

What this guards against
=========================
run_tests() launches a browser, then runs every TC, then closes the browser,
then prints a summary report — none of that sat inside a try/except until
round 3, and even after round 3's fix, three more escape hatches remained
(found by fault injection in later review rounds, closed in the same round
each was found):

  1. (round 4) The guard sat *inside* `async with async_playwright() as p:`,
     so the context manager's own `__aexit__` (driver teardown) was not
     covered.
  2. (round 4) The abort handler printed its diagnostic BEFORE appending the
     synthetic result — a `print()` failure (stdout closed -> BrokenPipeError)
     would have skipped `results.append()` entirely, the "diagnostics before
     accounting" mistake round 2's MEDIUM 3 fix was supposed to have
     eliminated for good.
  3. (round 5) The summary-report section itself — printed AFTER the
     browser-lifecycle guard, on every path including the all-green one —
     was still unguarded. A failure there (print(), or `_write_junit_xml()`'s
     file I/O) escaped uncaught, on what review round 5 flagged as the *most
     likely* path to actually hit it: an otherwise fully successful run
     (more output printed = more surface for a broken pipe or full disk).

A guard against a false green that nothing tests is exactly the kind of
decoration this PR spent rounds removing elsewhere in this file. This script
is that test, in four parts:

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

  test_summary_report_failure_on_green_run_preserves_result()
      Lets ONE fake TC run to a genuine PASS (no real browser/server needed —
      a fully faked context/page and a no-op TC function), then forces the
      summary-report print to fail from its very first line. Asserts:
      `results` still comes back with the correct PASS entry, the exit-code
      computation `main()` performs is still 0 (a real pass, not a false
      abort-flavored one), and — this is the design decision review round 5
      asked to be made deliberately, not left to fall out — the one
      CI-critical "Total: ..." line still surfaces, via the stderr fallback,
      even though stdout-based printing failed outright.

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
SUMMARY_PRINT_MARKER = "測試報告彙總"
FAKE_GREEN_TC_NUM = 9001


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


# ─── scenario 4: a genuinely successful run, summary print fails ───────────

class _FakePage:
    def set_default_timeout(self, ms):
        pass

    def on(self, event, handler):
        pass


class _FakeContext:
    async def new_page(self):
        return _FakePage()

    async def close(self):
        pass


class _GreenRunBrowser:
    async def new_context(self, **kwargs):
        return _FakeContext()

    async def close(self):
        pass


class _GreenRunChromium:
    @staticmethod
    async def launch(**kwargs):
        return _GreenRunBrowser()


class _GreenRunPlaywrightInstance:
    chromium = _GreenRunChromium()


class _GreenRunCM:
    async def __aenter__(self):
        return _GreenRunPlaywrightInstance()

    async def __aexit__(self, exc_type, exc, tb):
        return False


async def _fake_instant_pass_tc(page, **_):
    return  # no real browser interaction needed — an instant, genuine PASS


@contextlib.contextmanager
def _patched_tc_registry_with_fake_green_tc():
    """Adds one fake, instantly-passing TC to the real TC_REGISTRY for the
    duration of the `with` block, so run_tests() can produce a genuinely
    successful `results` entry without touching a real browser or server."""
    original = dict(wtm.TC_REGISTRY)
    wtm.TC_REGISTRY[FAKE_GREEN_TC_NUM] = (
        "fake instant-pass TC for #886 review round 5 verification",
        _fake_instant_pass_tc,
        "P0",
    )
    try:
        yield
    finally:
        wtm.TC_REGISTRY.clear()
        wtm.TC_REGISTRY.update(original)


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


async def _run_capturing_both(tc_nums):
    captured_out = io.StringIO()
    captured_err = io.StringIO()
    with contextlib.redirect_stdout(captured_out), contextlib.redirect_stderr(captured_err):
        results = await wtm.run_tests(tc_nums=tc_nums, headless=True)
    return results, captured_out.getvalue(), captured_err.getvalue()


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


def test_summary_report_failure_on_green_run_preserves_result():
    print("=== test_summary_report_failure_on_green_run_preserves_result ===")
    escaped = None
    results = None
    output = ""
    stderr_output = ""
    try:
        with _patched_tc_registry_with_fake_green_tc():
            with _patched_async_playwright(_GreenRunCM):
                # Fails on the summary section's very first print — the
                # closest thing to "the whole section can't reach stdout".
                with _print_that_raises_on_marker(
                    SUMMARY_PRINT_MARKER, RuntimeError("forced summary-report print failure")
                ):
                    results, output, stderr_output = asyncio.run(
                        _run_capturing_both([FAKE_GREEN_TC_NUM])
                    )
    except Exception as e:  # noqa: BLE001 — this IS the failure mode under test
        escaped = e

    assert escaped is None, (
        f"SUMMARY_PRINT ESCAPED={type(escaped).__name__}: {escaped} "
        f"RESULTS={results if results is not None else '[]'}"
    )
    print("summary-report print failure did not escape run_tests()")

    # --- design decision, point 1: `results` — and therefore the exit code
    # main() computes from it — is always correct, regardless of what could
    # be printed. This is a REAL pass (one fake TC, no failures anywhere),
    # not the abort scenarios above — the point of this test is that a
    # print failure must not corrupt or lose data that was never wrong.
    assert results is not None and len(results) == 1, f"expected exactly one result, got {results}"
    fake_result = results[0]
    assert fake_result["tc"] == FAKE_GREEN_TC_NUM, fake_result
    assert fake_result["status"] == "PASS", (
        f"RESULT CORRUPTED: expected a genuine PASS, got {fake_result} — a "
        "summary-report failure must not change what actually happened"
    )
    print(f"Result preserved: {fake_result}")

    failed = sum(1 for r in results if r["status"] in ("FAIL", "ERROR"))
    assert failed == 0, (
        f"main() would exit non-zero on a genuinely passing run — failed={failed}. "
        "A print failure must never change the exit code."
    )
    print(f"main()'s exit-code check: failed={failed} -> sys.exit(0) (correct — a real pass)")

    # --- design decision, point 2: the one CI-critical line still surfaces,
    # via a stderr fallback, even though stdout-based printing failed
    # outright. This is the deliberate choice review round 5 asked for —
    # silence is the last resort, not the first one, and a pass that could
    # not print its summary must not read as indistinguishable from a pass
    # that printed nothing because nothing ran.
    stdout_total_lines = [line for line in output.splitlines() if "Total:" in line]
    assert stdout_total_lines == [], (
        "test setup problem: the forced print failure didn't actually prevent "
        f"the stdout Total line, this proof is invalid. stdout={output!r}"
    )
    stderr_total_lines = [line for line in stderr_output.splitlines() if "Total:" in line]
    assert len(stderr_total_lines) == 1, (
        "DESIGN DECISION VIOLATED: when the summary can't reach stdout, the "
        "CI-critical Total line must still be attempted on stderr as a "
        f"fallback — none was found. stderr={stderr_output!r}"
    )
    fallback_line = stderr_total_lines[0]
    print(f"Fallback stderr line: {fallback_line!r}")
    assert "PASS: 1" in fallback_line and "FAIL: 0" in fallback_line and "ERROR: 0" in fallback_line, (
        f"fallback line has wrong counts for a 1-TC all-green run: {fallback_line!r}"
    )
    print("PASS\n")


def main():
    test_launch_failure_no_false_green()
    test_aexit_failure_does_not_escape()
    test_abort_report_print_failure_does_not_lose_result()
    test_summary_report_failure_on_green_run_preserves_result()
    print("ALL CHECKS PASSED — an aborted run cannot read as a CI-green pass, "
          "no lifecycle or diagnostic failure along that path can lose the "
          "result, and a summary-report failure on a genuine pass neither "
          "corrupts the result nor goes completely silent.")


if __name__ == "__main__":
    main()
