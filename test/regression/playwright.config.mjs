// Issue #565 — Playwright config for the TagHelper<->LayUI browser regression
// suite (Phase-2 #566 gate). See test/manual/regression/README.md.
//
// Deliberately minimal: one project (headless Chromium), no retries (the
// suite is a deterministic gate — a flaky pass here would defeat its
// purpose), and a generous per-test timeout because several sections poll
// for async layui module loads with bounded (multi-second) fallback timers.
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
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
  webServer: {
    command: 'node ./static-server.mjs',
    url: 'http://127.0.0.1:4565/test/manual/regression/565-taghelper-layui-regression.html',
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },
});
