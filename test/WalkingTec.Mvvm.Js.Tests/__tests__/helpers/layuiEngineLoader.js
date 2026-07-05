// Shared helper for issue #594 regression tests.
//
// Loads the REAL vendored layui engines from the demo scaffolding into isolated jsdom windows
// so the tests exercise the actual laytpl compiler / router() implementation shipped in each
// tree, rather than a hand-reimplementation of their escape/parsing behavior.
//
//   - "old" engine  = wwwroot/layui/            (layui 2.6.3 — modular: core + lay/modules/laytpl.js)
//   - "next" engine = wwwroot/layui-next/       (layui 2.13.8 — single-file bundle, laytpl inlined)
//
// WalkingTec.Mvvm.Demo's copies are used as the canonical reference; the #594 fix is byte-identical
// across all five demo variants (Demo/VueDemo/Vue3Demo/ReactDemo/BlazorDemo), which the accompanying
// source-consistency tests verify independently.
'use strict';

const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

// layui's core self-locates its own base directory via `document.currentScript` (or, when that's
// null -- which it is for eval()'d code -- by scanning the last <script> tag's `src`). A completely
// script-less document makes that fallback crash on `t[n].src` (n === -1 when there are zero <script>
// tags). One inert <script src="..."> element keeps the self-location probe from throwing under jsdom.
// This URL is never fetched: `runScripts: 'outside-only'` (below) disables jsdom's script loader/
// executor entirely, so no network request is made and no remote code ever runs -- the tag exists
// purely as a DOM fixture for layui's `getElementsByTagName('script')` self-location probe.
const SHELL_HTML = '<!doctype html><html><body><script src="http://localhost/layui.js"></script></body></html>';

const DEMO_WWWROOT = path.resolve(__dirname, '../../../../demo/WalkingTec.Mvvm.Demo/wwwroot');

function newWindow() {
  const win = new JSDOM(SHELL_HTML, { runScripts: 'outside-only', url: 'http://localhost/' }).window;
  // jsdom doesn't implement ResizeObserver; layui-next probes for it at load time and logs a
  // console.warn per window when absent. A trivial no-op stub silences that (harmless) noise
  // without affecting any behavior this test suite actually exercises.
  if (typeof win.ResizeObserver === 'undefined') {
    win.ResizeObserver = function () { this.observe = function () {}; this.unobserve = function () {}; this.disconnect = function () {}; };
  }
  return win;
}

// NOTE on eval() below: this loads first-party, repo-vendored layui source files (not
// user input, network responses, or any untrusted/dynamic string) into a disposable, isolated
// jsdom `window` created solely for this test run. It is the only practical way to exercise the
// REAL laytpl compiler/escape behavior of each vendored engine rather than reimplementing it by
// hand; the alternative (require()'ing the files as CommonJS) doesn't work because layui.js is
// written as a browser UMD bundle that expects a `window`/`document` global, not a module scope.

/** Loads the vendored layui 2.6.3 tree (core + laytpl module) into a fresh jsdom window. */
function loadOldEngine() {
  const win = newWindow();
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, 'layui/layui.js'), 'utf8'));
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, 'layui/lay/modules/laytpl.js'), 'utf8'));
  return win;
}

/** Loads the vendored layui-next 2.13.8 tree (single-file bundle, laytpl inlined) into a fresh window. */
function loadNextEngine() {
  const win = newWindow();
  win.eval(fs.readFileSync(path.join(DEMO_WWWROOT, 'layui-next/layui.js'), 'utf8'));
  return win;
}

/**
 * Renders a laytpl template string against `data` using the given loaded engine window.
 *
 * Uses `win.layui.laytpl` directly rather than `win.layui.use('laytpl', cb)`: both engines
 * register the module synchronously on `layui.laytpl` as part of the top-level `layui.define(...)`
 * call executed while the vendored bundle is eval()'d (confirmed empirically -- `typeof
 * win.layui.laytpl === 'function'` immediately after load, on both trees). `layui.use()` is
 * layui's general lazy-module-loader API and resolves its callback asynchronously (it polls
 * internal load-status state via `setTimeout`, even for already-registered modules), which would
 * force every test in this file to go through a real timer tick for no benefit here.
 */
function renderTpl(win, tplSource, data) {
  const laytpl = win.layui.laytpl;
  if (typeof laytpl !== 'function') {
    throw new Error('layui.laytpl was not registered on this engine window');
  }
  return laytpl(tplSource).render(data);
}

/**
 * Parses an HTML fragment and returns its first element, using a disposable jsdom document.
 * (These test files run under `@jest-environment node` -- see the docblock at the top of each --
 * so there is no ambient `document` global; this gives tests a DOM to parse laytpl output with.)
 */
function parseFirstElement(html) {
    const win = newWindow();
    const container = win.document.createElement('div');
    container.innerHTML = html; // eslint-disable-line no-unsanitized/property -- parses trusted, locally-generated laytpl test output, not user input
    return container.firstElementChild;
}

module.exports = { loadOldEngine, loadNextEngine, renderTpl, parseFirstElement, DEMO_WWWROOT };
