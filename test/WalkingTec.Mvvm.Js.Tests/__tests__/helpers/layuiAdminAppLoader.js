// Shared helper for issue #615 regression tests.
//
// Loads the REAL vendored layuiAdmin SPA stack -- layui engine (either tree) + lib/view.js +
// config.js + lib/admin.js + a given index.js -- into one isolated jsdom window, with jQuery's
// $.ajax intercepted so tests can control exactly when each simulated navigation's view-render
// "resolves" instead of racing real network requests. This lets #615's regression tests exercise
// the actual, unmodified render()/then()/done() pipeline (index.js's stale-render guard, admin.js's
// tabChange/tabsBodyChange, view.js's container-reassignment + parse()/html() insertion) rather
// than a hand-reimplementation of it.
'use strict';

const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

// See helpers/layuiEngineLoader.js (issue #594) for why a script tag is needed for layui's
// self-location probe, and why runScripts:'outside-only' makes that safe (no code executes from
// it -- it's an inert DOM fixture, not a real script load).
const SHELL_HTML = '<!doctype html><html><body><script src="http://localhost/layui.js"></script></body></html>';

const DEMO_WWWROOT = path.resolve(__dirname, '../../../../demo/WalkingTec.Mvvm.Demo/wwwroot');

// Real production tab-shell markup (copied from demo/WalkingTec.Mvvm.Demo/Views/Home/Layout.cshtml)
// -- the `.layui-tab[lay-filter=...]` / `#LAY_app_tabsheader` / `#LAY_app_body` structure that
// layui.element's tabAdd/tabChange and admin.js's tabsBody/tabsBodyChange operate on.
const TAB_SHELL_HTML = `
  <div class="layui-tab" lay-unauto lay-allowclose="true" lay-filter="layadmin-layout-tabs">
    <ul class="layui-tab-title" id="LAY_app_tabsheader">
      <li lay-id="/" class="layui-this"><i class="layui-icon layui-icon-home"></i></li>
    </ul>
  </div>
  <div class="layui-body" id="LAY_app_body">
    <div class="layadmin-tabsbody-item layui-show"></div>
  </div>
`;

function newWindow() {
  const win = new JSDOM(SHELL_HTML, { runScripts: 'outside-only', url: 'http://localhost/' }).window;
  // jsdom doesn't implement ResizeObserver; layui-next probes for it at load time. See
  // layuiEngineLoader.js for the identical, harmless stub.
  if (typeof win.ResizeObserver === 'undefined') {
    win.ResizeObserver = function () { this.observe = function () {}; this.unobserve = function () {}; this.disconnect = function () {}; };
  }
  // index.js's own `layui.extend({setter:"config",admin:"lib/admin",view:"lib/view"})` always
  // logs a "module already exists" message here, because loadAdminApp() already loaded those
  // modules by their real paths (it can't rely on layui's dynamic <script>-based loader, which
  // jsdom's runScripts:'outside-only' disables). Harmless and expected on every call; silenced so
  // test output isn't dominated by it. The two engines log this via different console methods
  // (2.6.3 uses console.error, 2.13.8 uses console.warn) -- both silenced.
  win.console.warn = function () {};
  win.console.error = function () {};
  return win;
}

/**
 * Loads the real layuiAdmin SPA stack into one isolated jsdom window.
 *
 * @param {object} opts
 * @param {'old'|'next'} opts.engine - which vendored layui tree to load ('old' = 2.6.3 at
 *   wwwroot/layui/, 'next' = 2.13.8 at wwwroot/layui-next/)
 * @param {string} [opts.indexJsPath] - absolute path to the index.js to eval. Ignored if
 *   `indexJsSource` is given.
 * @param {string} [opts.indexJsSource] - index.js source text to eval directly (lets a test
 *   exercise a derived/pre-fix variant without writing a temp file).
 * @returns {{ win: Window, ajaxCalls: Array<object> }} - `ajaxCalls[i]` is the options object
 *   passed to the intercepted `$.ajax(...)` call for the i-th `render()` invocation; call
 *   `ajaxCalls[i].success(html, 'success', FAKE_XHR)` to resolve it on demand, in whatever order
 *   the test wants to simulate.
 */
function loadAdminApp({ engine, indexJsPath, indexJsSource }) {
  const win = newWindow();
  const engineFile = engine === 'old' ? 'layui/layui.js' : 'layui-next/layui.js';
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, engineFile), 'utf8'));

  // Real HTML pages load jQuery globally before layuiAdmin's own files run; config.js reads the
  // bare `$` global (not `layui.$`) for its pageTabs cookie check.
  win.$ = win.layui.$;
  win.$.cookie = function (name) { return name === 'pagemode' ? 'Tab' : undefined; };

  // Load order matters: view.js and admin.js each capture `layui.setter` as a plain variable at
  // module-factory execution time (not lazily) -- config.js must run first, or that capture sees
  // undefined instead of the real setter object.
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, 'layuiadmin/config.js'), 'utf8'));
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, 'layuiadmin/lib/view.js'), 'utf8'));
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, 'layuiadmin/lib/admin.js'), 'utf8'));

  // admin.js's own init deletes setter.pageTabs when $(window).width() reports a phone-sized
  // screen (`C.screen()<1`). jsdom has no real layout engine, so $(window).width() is always 0 --
  // unlike any real desktop browser TC-04 (or a real user) runs against, which always looks like a
  // phone here. Restore it so the test exercises the tabbed/pageTabs=true code path index.js
  // actually ships and #615 is about.
  win.layui.setter.pageTabs = true;

  win.document.body.innerHTML = TAB_SHELL_HTML;

  // Intercept $.ajax so each render()'s view-fetch is controlled by the test instead of racing a
  // real (nonexistent, in jsdom) network round-trip.
  const ajaxCalls = [];
  win.layui.$.ajax = function (opts) { ajaxCalls.push(opts); return {}; };

  // index.js's bootstrap does `layui.link(cssUrl, function(){u()}, ...)` to load admin.css, and
  // that callback triggers the OUTER dispatcher u() -- an extra, uncontrolled render() the test
  // doesn't expect and didn't ask for (it would run before, and get mixed in with, the
  // test-driven renders below, throwing off ajaxCalls' indexing). Tests drive render() directly
  // (bypassing index.js's own hash-driven bootstrap) and don't need admin.css or its callback, so
  // this is stubbed to capture-and-never-invoke rather than a no-op that still fires it.
  win.layui.link = function () {};

  // "common" is WTM's own app-level module (not part of this test's vendored stack); index.js's
  // non-stale `.done()` calls `layui.use("common", ...)` unconditionally. Resolve it as a
  // harmless no-op rather than loading unrelated app code.
  const origUse = win.layui.use.bind(win.layui);
  win.layui.use = function (mods) {
    if (mods === 'common') {
      var cb = arguments[1];
      if (typeof cb === 'function') cb();
      return win.layui;
    }
    return origUse.apply(win.layui, arguments);
  };
  win.layui.cache = win.layui.cache || {};
  win.layui.cache.callback = win.layui.cache.callback || {};
  win.layui.cache.callback.common = function () {};

  const src = indexJsSource !== undefined ? indexJsSource : fs.readFileSync(indexJsPath, 'utf8');
  win.eval(src);

  return { win, ajaxCalls };
}

/** Builds a fake view-fetch HTML response body with the given title and marker body text. */
function fakeViewHtml(title, bodyMarker) {
  return '<title>' + title + '</title><div class="probe-body">' + bodyMarker + '</div>';
}

/** Fake jqXHR passed as the 3rd `success` callback arg; only getResponseHeader is ever touched. */
const FAKE_XHR = { getResponseHeader: function () { return null; } };

module.exports = { loadAdminApp, fakeViewHtml, FAKE_XHR, DEMO_WWWROOT };
