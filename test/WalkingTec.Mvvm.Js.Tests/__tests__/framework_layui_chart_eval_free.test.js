// Tests for issue #789 Phase 1: chart-related eval() sites in framework_layui.js
// must be replaced with safe window[] property access.
//
// These tests verify two things:
//   1) framework_layui.js no longer contains chart-related eval() calls
//   2) The new window[id+'Chart'] lookup pattern works for ResizeChart
//      when the chart global exists and is safely a no-op when it does not.

const fs = require('fs');
const path = require('path');

describe('#789 Phase 1 — framework_layui.js chart eval removal', () => {
  const srcPath = path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js');
  const src = fs.readFileSync(srcPath, 'utf8');

  // Strip line comments so commented-out `eval(data)` does not count.
  const stripLineComments = (text) =>
    text
      .split('\n')
      .map((line) => {
        const idx = line.indexOf('//');
        return idx === -1 ? line : line.slice(0, idx);
      })
      .join('\n');

  const active = stripLineComments(src);

  test('no chart resize eval — eval(...Chart.resize...) must be gone', () => {
    const bad = /eval\s*\([^)]*Chart\.resize/;
    expect(active).not.toMatch(bad);
  });

  test('no chart setOption eval — eval(...Chart.setOption...) must be gone', () => {
    const bad = /eval\s*\([^)]*Chart\.setOption/;
    expect(active).not.toMatch(bad);
  });

  test('no RefreshChart global-access eval — eval(chartid + "ChartUrl"|"ChartType"|"ChartLegend") must be gone', () => {
    const url = /eval\s*\(\s*chartid\s*\+\s*["\']ChartUrl["\']/;
    const type = /eval\s*\(\s*chartid\s*\+\s*["\']ChartType["\']/;
    const legend = /eval\s*\(\s*chartid\s*\+\s*["\']ChartLegend["\']/;
    expect(active).not.toMatch(url);
    expect(active).not.toMatch(type);
    expect(active).not.toMatch(legend);
  });

  test('new safe pattern is in place — window[... + "Chart"] appears multiple times', () => {
    const matches = active.match(/window\[[^\]]*\+\s*['"]Chart['"]\]/g) || [];
    // 5 resize sites + 2 setOption wrappers = at least 7 chart-variable lookups
    expect(matches.length).toBeGreaterThanOrEqual(7);
  });

  test('new safe pattern is in place — window[... + "ChartUrl|ChartType|ChartLegend"] appears for RefreshChart', () => {
    expect(active).toMatch(/window\[chartid\s*\+\s*["\']ChartUrl["\']\]/);
    expect(active).toMatch(/window\[chartid\s*\+\s*["\']ChartType["\']\]/);
    expect(active).toMatch(/window\[chartid\s*\+\s*["\']ChartLegend["\']\]/);
  });
});

describe('#789 Phase 1 — safe window[] chart lookup semantics', () => {
  // Simulates the exact pattern the refactor now uses inside .each() callbacks.
  // The code reads window[id + 'Chart'] and calls .resize() only if it exists
  // and exposes a function — matching the rewritten sites.
  function safeResize(id) {
    const chart = global[id + 'Chart'];
    if (chart && typeof chart.resize === 'function') chart.resize();
  }

  afterEach(() => {
    delete global.fooChart;
    delete global.malicious_alertChart;
  });

  test('calls resize when a real chart instance is registered', () => {
    const resize = jest.fn();
    global.fooChart = { resize };
    safeResize('foo');
    expect(resize).toHaveBeenCalledTimes(1);
  });

  test('is a no-op when the chart variable is undefined', () => {
    // No chart registered — must not throw.
    expect(() => safeResize('ghost')).not.toThrow();
  });

  test('is a no-op when the chart variable is not an object with resize()', () => {
    // Simulates a legacy typo / wrong type occupying the global.
    global.fooChart = 'not a chart';
    expect(() => safeResize('foo')).not.toThrow();
  });

  test('is a no-op for ids containing injection payloads — does not execute payload', () => {
    // Key insight: window[id + 'Chart'] quotes the id as a property key, so
    // an id like "x;alert(1);var y" becomes a lookup on a string key, never
    // evaluated as JavaScript. This is the exact case the old eval() broke on.
    const payload = "x;alert(1);var y";
    // Neither a matching global nor code execution — just a property miss.
    expect(() => safeResize(payload)).not.toThrow();
  });
});
