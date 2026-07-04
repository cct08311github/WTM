// Issue #566 — shared Playwright fixture that adds a `layuiVariant` test
// option. Each project in playwright.config.mjs sets this option via its own
// `use` block ('263' or 'next'); the spec reads it to build the harness URL.
// Kept in its own file (rather than inlined in the spec) so both the base
// #565 suite and any future spec can opt into the same dual-tree behavior.
import { test as base } from '@playwright/test';

export const test = base.extend({
  // Default matches pre-#566 behavior: the bundled layui 2.6.3 tree, no
  // query string at all.
  layuiVariant: ['263', { option: true }],
});

export { expect } from '@playwright/test';
