// Issue #565 — headless assertion pass over the TagHelper<->LayUI browser
// regression harness. This IS the Phase-2 (#566) gate: it navigates to the
// combined harness page, waits for every section to finish reporting into
// window.__regressionResults, and asserts every entry passed — except any
// entry explicitly flagged knownGap:true. There is no hardcoded per-name
// exemption list here — this spec generically filters on each result's own
// `knownGap` field, whatever it is (or isn't) at the time. As of #581, the
// suite carries ZERO knownGap entries: the last one (tagInput, which used
// to target a never-shipped Layui 2.8+ module) was retired once #571
// reimplemented TagInputTagHelper as a native, dependency-free widget — it
// is now a real must-pass section on both projects below, same as every
// other widget.
//
// #566 update: this spec now runs under TWO Playwright projects (see
// playwright.config.mjs) — `layui-263` (the original bundled 2.6.3 tree,
// no query string) and `layui-next` (the opt-in vendored 2.13.8 tree, via
// `?layui=next`). Each project supplies its own `layuiVariant` fixture
// option; the harness page itself maps that query param to one of two
// hardcoded literal asset-tree base paths (see the HEAD LOADER comment in
// 565-taghelper-layui-regression.html) — this spec never builds URLs from
// untrusted input, only from the fixed '263'/'next' option values.
import { test, expect } from './fixtures.mjs';

const HARNESS_PATH = '/test/manual/regression/565-taghelper-layui-regression.html';
const TOTAL_EXPECTED = 15;

test.describe('#565 TagHelper <-> LayUI regression suite', () => {
  test('every non-knownGap widget section initializes correctly', async ({ page, layuiVariant }) => {
    const consoleErrors = [];
    page.on('pageerror', (err) => consoleErrors.push(String(err)));

    const url = layuiVariant === 'next' ? `${HARNESS_PATH}?layui=next` : HARNESS_PATH;
    await page.goto(url);

    // Wait for the harness's own completion flag rather than a fixed sleep —
    // every section resolves via layui module-load callbacks or a bounded
    // internal fallback timer (see the harness script), so this converges
    // deterministically once the last section reports.
    await page.waitForFunction(
      () => window.__regressionDone === true,
      { timeout: 45_000 }
    );

    const results = await page.evaluate(() => window.__regressionResults);

    expect(results.length, 'unexpected number of regression sections reported').toBe(TOTAL_EXPECTED);

    const failures = results.filter((r) => !r.knownGap && !r.pass);
    const knownGaps = results.filter((r) => r.knownGap);

    // Always surface every section's detail message (not just failures) so
    // a run's console output alone is enough to see what each widget did —
    // useful when diffing the layui-263 baseline against layui-next.
    // eslint-disable-next-line no-console
    console.log(
      `[wtm-regression][${layuiVariant}] section results:\n` +
      results.map((r) => `  - [${r.knownGap ? 'GAP' : (r.pass ? 'PASS' : 'FAIL')}] ${r.name}: ${r.detail}`).join('\n')
    );

    if (knownGaps.length > 0) {
      // eslint-disable-next-line no-console
      console.log(
        `[wtm-regression][${layuiVariant}] documented known gaps (not blocking this gate):\n` +
        knownGaps.map((g) => `  - ${g.name}: ${g.detail}`).join('\n')
      );
    }

    if (failures.length > 0) {
      const detail = failures.map((f) => `  - ${f.name}: ${f.detail}`).join('\n');
      throw new Error(`[${layuiVariant}] ${failures.length} regression assertion(s) failed:\n${detail}`);
    }

    if (consoleErrors.length > 0) {
      // eslint-disable-next-line no-console
      console.log(`[wtm-regression][${layuiVariant}] uncaught page errors (non-fatal, informational):\n` + consoleErrors.join('\n'));
    }

    expect(failures, 'all non-knownGap sections must pass').toEqual([]);
  });
});
