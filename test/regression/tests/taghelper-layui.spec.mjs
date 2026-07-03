// Issue #565 — headless assertion pass over the TagHelper<->LayUI browser
// regression harness. This IS the Phase-2 (#566) gate: it navigates to the
// combined harness page, waits for every section to finish reporting into
// window.__regressionResults, and asserts every entry passed — except
// entries explicitly flagged knownGap:true (currently just `tagInput`,
// which targets a Layui 2.8+ module not present in the bundled 2.6.3; see
// the harness HTML comment and test/manual/regression/README.md for why).
import { test, expect } from '@playwright/test';

const HARNESS_PATH = '/test/manual/regression/565-taghelper-layui-regression.html';
const TOTAL_EXPECTED = 14;

test.describe('#565 TagHelper <-> LayUI regression suite', () => {
  test('every non-knownGap widget section initializes correctly', async ({ page }) => {
    const consoleErrors = [];
    page.on('pageerror', (err) => consoleErrors.push(String(err)));

    await page.goto(HARNESS_PATH);

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

    if (knownGaps.length > 0) {
      // eslint-disable-next-line no-console
      console.log(
        '[wtm-regression] documented known gaps (not blocking this gate):\n' +
        knownGaps.map((g) => `  - ${g.name}: ${g.detail}`).join('\n')
      );
    }

    if (failures.length > 0) {
      const detail = failures.map((f) => `  - ${f.name}: ${f.detail}`).join('\n');
      throw new Error(`${failures.length} regression assertion(s) failed:\n${detail}`);
    }

    if (consoleErrors.length > 0) {
      // eslint-disable-next-line no-console
      console.log('[wtm-regression] uncaught page errors (non-fatal, informational):\n' + consoleErrors.join('\n'));
    }

    expect(failures, 'all non-knownGap sections must pass').toEqual([]);
  });
});
