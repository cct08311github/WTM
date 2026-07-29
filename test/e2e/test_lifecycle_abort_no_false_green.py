"""
Regression test for issue #886 (PR #897) review round 3.

What this guards against
=========================
run_tests() launches a browser, then runs every TC, then closes the browser —
none of that lifecycle work sat inside a try/except until round 3. If any of
it raises (browser crash, `context.close()` failing, etc. — see the lifecycle
audit table in the PR), the exception used to escape run_tests() entirely,
skipping the summary report and JUnit XML below it no matter how many TCs had
already completed correctly.

The fix wraps that lifecycle work in a try/except that appends a synthetic
"SUITE-ABORT" ERROR result before falling through to the summary code — but a
guard against a false green that nothing tests is exactly the kind of
decoration this PR spent three rounds removing elsewhere in this file. This
script is that test: it forces an abort as cheaply as possible (monkeypatching
`playwright.async_api.async_playwright` so `chromium.launch()` raises before
any real browser, page, or HTTP server is ever needed) and asserts the emitted
summary line does NOT read as green under this repo's own documented CI
convention — not a paraphrase of it.

That convention, verbatim from /CLAUDE.md's "CI red does not mean failed"
section:

    e2e -> `FAIL: 0` **and** `ERROR: 0` in the
    `Total: N | PASS: n | FAIL: n | ERROR: n | SKIP: n` summary line

So the check here is exactly that: does the summary line contain the literal
substrings "FAIL: 0" and "ERROR: 0"? Before this fix, an abort at
`browser.launch()` produced "Total: 0 | PASS: 0 | FAIL: 0 | ERROR: 0 | SKIP: 0"
— both substrings present, i.e. a suite that never launched read as a perfect
green by this repo's own rule. After the fix, "ERROR: 0" must not appear.

Run directly (no demo server, no real browser needed — this never gets past
the monkeypatched chromium.launch()):

    cd test/e2e && python test_lifecycle_abort_no_false_green.py

Exits 0 and prints "ALL CHECKS PASSED" on success; asserts (non-zero exit,
traceback) on any regression.
"""

import asyncio
import contextlib
import io
import sys

import playwright.async_api as pw_api

import wtm_e2e_tests as wtm

FORCED_MESSAGE = "forced browser launch failure for #886 review round 3 verification"


class _ForcedLaunchFailure(RuntimeError):
    pass


class _FakeChromium:
    @staticmethod
    async def launch(**kwargs):
        # This is "row 3" from the PR's lifecycle audit table: the cheapest
        # place to force an abort, because it fires before run_tests() has
        # created a single context/page or touched a real server.
        raise _ForcedLaunchFailure(FORCED_MESSAGE)


class _FakePlaywrightInstance:
    chromium = _FakeChromium()


class _FakeAsyncPlaywrightCM:
    async def __aenter__(self):
        return _FakePlaywrightInstance()

    async def __aexit__(self, exc_type, exc, tb):
        return False  # never suppress — matches the real async_playwright()'s contract


def _fake_async_playwright():
    return _FakeAsyncPlaywrightCM()


async def _run_forced_abort():
    # run_tests() does `from playwright.async_api import async_playwright`
    # *inside* the function body, resolved at call time — patching the module
    # attribute before calling it is enough, no need to touch run_tests() itself.
    original = pw_api.async_playwright
    pw_api.async_playwright = _fake_async_playwright
    try:
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            results = await wtm.run_tests(tc_nums=[1, 2, 3], headless=True)
        return results, captured.getvalue()
    finally:
        pw_api.async_playwright = original


def main():
    results, output = asyncio.run(_run_forced_abort())

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

    # --- the synthetic result itself ---
    abort_entries = [r for r in results if r.get("tc") == "SUITE-ABORT"]
    assert len(abort_entries) == 1, f"expected exactly one SUITE-ABORT entry, got {abort_entries}"
    abort_entry = abort_entries[0]
    assert abort_entry["status"] == "ERROR", abort_entry
    assert FORCED_MESSAGE in abort_entry["error"], (
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

    print("\nALL CHECKS PASSED — an aborted run cannot read as a CI-green pass.")


if __name__ == "__main__":
    main()
