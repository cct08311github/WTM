// Issue #565 — Playwright config for the TagHelper<->LayUI browser regression
// suite (Phase-2 #566 gate). See test/manual/regression/README.md.
//
// Deliberately minimal: headless Chromium, no retries (the suite is a
// deterministic gate — a flaky pass here would defeat its purpose), and a
// generous per-test timeout because several sections poll for async layui
// module loads with bounded (multi-second) fallback timers.
//
// #566: two projects run the SAME spec (test/regression/tests/taghelper-layui.spec.mjs)
// against the two vendored layui trees — `layui-263` (bundled 2.6.3, the
// default/baseline) and `layui-next` (opt-in 2.13.8). Each project supplies
// its own `layuiVariant` fixture value ('263' | 'next'); the spec maps that
// to a `?layui=next` query param only for the 'next' project, and the
// harness page itself maps that param to one of two hardcoded asset-tree
// base paths — see the HEAD LOADER comment in
// 565-taghelper-layui-regression.html. `layui-263` must stay green as the
// baseline; `layui-next` is the #566 compat-fix target.
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:4565',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'layui-263', use: { ...devices['Desktop Chrome'], layuiVariant: '263' } },
    { name: 'layui-next', use: { ...devices['Desktop Chrome'], layuiVariant: 'next' } },
  ],
  webServer: {
    command: 'node ./static-server.mjs',
    url: 'http://127.0.0.1:4565/test/manual/regression/565-taghelper-layui-regression.html',
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },
});
