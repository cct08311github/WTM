/**
 * @jest-environment node
 *
 * See framework_layui_594_router_path_normalize.test.js for why this file loads its own isolated
 * jsdom windows (helpers/layuiAdminAppLoader) and runs under plain `node` rather than Jest's
 * built-in jsdom testEnvironment.
 */

// Tests for issue #615: layuiadmin's index.js SPA render function `s()` captures the route it was
// invoked for (`r`) at the top of the function, then kicks off an async view-fetch. Its
// `.then()`/`.done()` callbacks never checked whether the current hash still matched `r` by the
// time they actually ran. A slow EARLIER render (e.g. the home "/" render kicked off at login)
// resolving AFTER a faster, newer navigation (e.g. clicking into /Student/Index) would clobber
// it: the stale `.then()` unconditionally called `element.tabChange(c, r)` +
// `admin.tabsBodyChange(n.index)`, switching the visible tab back to home while the address bar
// hash stayed on the page the user actually navigated to. On layui-next (2.13.8) the home
// render's fetch reliably resolves late enough that this reproduced deterministically; on 2.6.3
// the same race only opened under load (believed to be the real cause of the #596 flakes).
//
// The shipped fix adds a guard, immediately before the tab-switch calls in `.then()` and at the
// top of `.done()`:
//
//   if (i.correctRouter(layui.router().path.join("/")) !== r) return;
//
// IMPORTANT placement note (this is the part an earlier, naive version of this fix got wrong,
// caught only by browser-level testing, not by reasoning about the diff): the guard in `.then()`
// must come AFTER `this.container=i.tabsBody(n.index)` runs, not before it. That line reassigns
// the view instance's `.container` from its default (`#LAY_app_body` as a whole) to this route's
// own per-tab pane div. view.js's `parse()` -- called by the SAME ajax success handler right
// after `.then()`, unconditionally -- does `s.container.html(fetchedContent)`. If the container
// reassignment is skipped for a stale render (e.g. by returning at the very top of `.then()`),
// `parse()` targets `#LAY_app_body` directly and *replaces the entire tab-body DOM*, silently
// destroying every other open tab's pane -- a strictly worse bug than the one #615 set out to fix.
// The tests below cover both failure modes.

const fs = require('fs');
const path = require('path');
const { loadAdminApp, fakeViewHtml, FAKE_XHR, DEMO_WWWROOT } = require('./helpers/layuiAdminAppLoader');

const FIXED_INDEX_JS_PATH = path.join(DEMO_WWWROOT, 'layuiadmin/index.js');

/**
 * Reverses the exact #615 patch to recover the pre-fix source, for the "regression
 * demonstration" test below -- without maintaining a separate frozen fixture file that could
 * silently drift from the real one. Throws loudly (rather than silently testing a stale
 * assumption) if the guard's exact text ever changes shape.
 */
function stripStaleRenderGuard(fixedSource) {
  const guardedThen =
    'a.pageTabs||this.container.scrollTop(0);/* WTM #615: stale-render guard — container reassignment above MUST still run (view.js\'s parse() targets this.container), only the visible tab switch below is skipped for a stale render (see #596/#573) */if(i.correctRouter(layui.router().path.join("/"))!==r)return;t.tabChange(c,r)';
  const preFixThen = 'a.pageTabs||this.container.scrollTop(0),t.tabChange(c,r)';
  const guardedDone =
    '.done(function(){/* WTM #615: stale-render guard, see #596/#573 */if(i.correctRouter(layui.router().path.join("/"))!==r)return;layui.use(';
  const preFixDone = '.done(function(){layui.use(';

  if (!fixedSource.includes(guardedThen) || !fixedSource.includes(guardedDone)) {
    throw new Error(
      'stripStaleRenderGuard: expected WTM #615 guard markers not found verbatim -- index.js ' +
        'has changed shape since this test was written and needs to be updated in tandem.'
    );
  }
  return fixedSource.replace(guardedThen, preFixThen).replace(guardedDone, preFixDone);
}

/**
 * Simulates the exact #615 race using the real render()/then()/done() pipeline: kicks off a HOME
 * render (as if at login), then a STUDENT render (as if the user clicked a sidebar link before
 * home resolved) -- both via the real `layui.index.render()` -- resolves STUDENT's fetch first
 * (the newer, faster navigation), then resolves HOME's fetch last (the stale, slow one), and
 * reports the resulting tab/pane DOM state.
 */
function runRaceScenario(win, ajaxCalls) {
  let currentRoute = { path: [''] };
  win.layui.router = () => currentRoute;

  win.layui.index.render(); // home, kicked off at "login"
  expect(ajaxCalls.length).toBe(1);

  currentRoute = { path: ['Student', 'Index'] };
  win.layui.index.render(); // user navigates to Student before home resolves
  expect(ajaxCalls.length).toBe(2);

  // Student (the newer navigation) resolves first -- it was a fast page.
  ajaxCalls[1].success(fakeViewHtml('StudentPage', 'student-grid'), 'success', FAKE_XHR);
  // Home (the stale render) resolves last -- it was the slow one.
  ajaxCalls[0].success(fakeViewHtml('HomePage', 'home-dashboard'), 'success', FAKE_XHR);

  const activeTab = win.document.querySelector('#LAY_app_tabsheader>li.layui-this');
  return {
    paneCount: win.document.querySelectorAll('.layadmin-tabsbody-item').length,
    activeTabId: activeTab ? activeTab.getAttribute('lay-id') : null,
  };
}

describe.each([
  ['old (2.6.3)', 'old'],
  ['next (2.13.8)', 'next'],
])('#615 stale-render guard — %s engine', (_label, engine) => {
  test('regression demonstration: without the guard, the stale home render clobbers the active tab back to home', () => {
    const fixedSource = fs.readFileSync(FIXED_INDEX_JS_PATH, 'utf8');
    const preFixSource = stripStaleRenderGuard(fixedSource);
    const { win, ajaxCalls } = loadAdminApp({ engine, indexJsSource: preFixSource });

    const result = runRaceScenario(win, ajaxCalls);

    // The pre-fix bug: the DOM structure survives (this specific bug doesn't wipe it), but the
    // stale home render flips the active tab back to "/", losing the user's actual navigation.
    expect(result.paneCount).toBe(2);
    expect(result.activeTabId).toBe('/');
  });

  test('fix: the stale home render does not clobber the active tab, and does not wipe the tab-body DOM', () => {
    const { win, ajaxCalls } = loadAdminApp({ engine, indexJsPath: FIXED_INDEX_JS_PATH });

    const result = runRaceScenario(win, ajaxCalls);

    // Regression check for a strictly worse bug an earlier, naive version of this fix introduced:
    // guarding at the very top of `.then()` also skips the container reassignment, so view.js's
    // parse() targets #LAY_app_body directly and wipes BOTH tab panes instead of just refreshing
    // home's own (hidden) one. Both must hold for the fix to be correct.
    expect(result.paneCount).toBe(2);
    expect(result.activeTabId).toBe('/Student/Index');
  });
});

describe('#615 source-consistency sweep — guard applied identically at both then()/done() sites, all surviving demo variants', () => {
  const DEMO_ROOT = path.resolve(DEMO_WWWROOT, '../..');
  // #679 retired VueDemo (Vue2) and ReactDemo (webpack4); only the surviving
  // demo variants are swept here.
  const VARIANTS = [
    'WalkingTec.Mvvm.Demo',
    'WalkingTec.Mvvm.Vue3Demo',
    'WalkingTec.Mvvm.BlazorDemo/WalkingTec.Mvvm.BlazorDemo',
  ];

  function markerCount(src) {
    return (src.match(/WTM #615/g) || []).length;
  }

  test.each(VARIANTS)('%s: index.js has exactly 2 guard sites (.then() and .done())', (variant) => {
    const src = fs.readFileSync(path.join(DEMO_ROOT, variant, 'wwwroot/layuiadmin/index.js'), 'utf8');
    expect(markerCount(src)).toBe(2);
    expect(src).toMatch(/if\(i\.correctRouter\(layui\.router\(\)\.path\.join\("\/"\)\)!==r\)return;/);
  });

  test('all surviving variants have byte-identical index.js', () => {
    const contents = VARIANTS.map((variant) =>
      fs.readFileSync(path.join(DEMO_ROOT, variant, 'wwwroot/layuiadmin/index.js'), 'utf8')
    );
    contents.slice(1).forEach((c) => expect(c).toBe(contents[0]));
  });

  test('pindex.js was deliberately left unguarded: its .then() is empty and its .done() does not call tabChange/tabsBodyChange', () => {
    // index.js's "alone" (non-tabbed) sibling module. Its render function's .then() callback is
    // an empty no-op, and its .done() only attaches scroll/resize handlers + re-renders the
    // breadcrumb -- it never calls element.tabChange or admin.tabsBodyChange, so it cannot
    // reproduce the #615 tab-clobber symptom. Documented here (rather than silently guarded) so a
    // future change to pindex.js's .then()/.done() bodies is forced to re-examine this decision.
    const src = fs.readFileSync(path.join(DEMO_WWWROOT, 'layuiadmin/pindex.js'), 'utf8');
    expect(src).toMatch(/\.then\(function\(e\)\{\}\)/);
    const doneMatch = src.match(/\.done\(function\(\)\{([\s\S]*?)\}\),void s\(\)\)/);
    expect(doneMatch).not.toBeNull();
    expect(doneMatch[1]).not.toMatch(/tabChange|tabsBodyChange/);
  });
});
