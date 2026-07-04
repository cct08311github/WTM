// Tests for Issue #576: ff.OpenDialog's dialog-init dispatch loop now routes
// every collected island payload through ff._dispatchIslandWhenReady instead
// of calling ff.DispatchAction directly.
//
// Background: #552 added per-case layui.use(['slider'/'rate'/'colorpicker'],
// cb) deferral directly inside three DispatchAction switch cases, as a
// stopgap for the dialog path (OpenDialog's dialog-init loop bypassed
// ff._dispatchIslandWhenReady entirely — see the #552 comments in
// DispatchAction's 'slider' case and _renderSliderAction, both updated by
// this issue). That stopgap did NOT cover 'initForm' / 'bindSubmit' /
// 'bindValidate' / 'laydate' / 'loadComboItems' — any of those, when
// dispatched from a freshly-opened dialog before their layui submodule
// finished loading, could silently no-op (the switch/case falls through a
// module-not-loaded guard with a bare `break`), exactly like the bug #556
// fixed for the page-ready path via ff._dispatchIslandWhenReady +
// layui.use(...).
//
// The #576 fix: OpenDialog's dialog-init dispatch loop (the
// `for (var _pi = 0; _pi < _dialogInitPayloads.length; _pi++)` loop in the
// layer.open success callback) now calls ff._dispatchIslandWhenReady instead
// of a bare ff.DispatchAction — the EXACT SAME helper + usage pattern as
// ff._consumePageReadyIslands (source-swept in #556's test file), so the
// module-load race is closed generically instead of one DispatchAction case
// at a time.
//
// Ordering: legacy inline <script> rehydration (the _initScripts loop) runs
// BEFORE the dialog-init dispatch loop in the success callback, and that
// loop is a plain, fully-synchronous for-loop with no yield point — so by
// the time the dispatch loop even begins, every legacy script has already
// finished executing. This holds regardless of whether an individual
// island's dispatch turns out to be synchronous (module already loaded) or
// deferred into layui.use (module not yet loaded): a deferred callback can
// only run after the synchronous code that scheduled it — including the
// _initScripts loop, which already completed even earlier — has finished.
// So legacy-script-vs-island relative ordering is unaffected by this fix.
//
// Following the same convention as framework_layui_552_static_widgets_island.test.js
// (the "real DispatchAction via vm" describe block): most of this file's
// coverage exercises the REAL framework_layui.js source loaded fresh into a
// vm context that provides a genuine `document` (this file's own jsdom
// document) and a fully-controllable `layui` mock (including `layui.layer`,
// so the real ff.OpenDialog code path — $.ajax success handler, DOMParser
// island/script extraction, layer.open success callback, dialog-init
// dispatch loop — can be driven end-to-end without reimplementing any of
// OpenDialog's logic in the test itself).

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

const stripLineComments = (text) =>
  text
    .split('\n')
    .map((line) => {
      const idx = line.indexOf('//');
      return idx === -1 ? line : line.slice(0, idx);
    })
    .join('\n');

const active = stripLineComments(src);

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#576 OpenDialog dialog-init dispatch loop — source sweep', () => {
  test('the dialog-init dispatch loop calls ff._dispatchIslandWhenReady, not a bare ff.DispatchAction', () => {
    const block = active.match(
      /for\s*\(\s*var\s+_pi\s*=\s*0[\s\S]{0,400}?_dialogInitPayloads\.length[\s\S]{0,400}?\n\s*\}/
    );
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(
      /ff\._dispatchIslandWhenReady\s*\(\s*_dialogInitPayloads\[_pi\]\s*\)/
    );
    // Regression guard: the OLD bare-dispatch call must be gone from this loop.
    expect(block[0]).not.toMatch(
      /ff\.DispatchAction\s*\(\s*_dialogInitPayloads\[_pi\]\s*\)/
    );
  });

  test('the dialog-init dispatch loop still runs strictly after the legacy _initScripts rehydration loop', () => {
    const successBlock = active.match(
      /success:\s*function\s*\(\s*\)\s*\{[\s\S]{0,3200}?_dialogInitPayloads\.length[\s\S]{0,400}?\n\s*\}\s*\n\s*\}/
    );
    expect(successBlock).not.toBeNull();
    const initScriptsIdx = successBlock[0].indexOf('_initScripts.length');
    const dialogInitIdx = successBlock[0].indexOf('_dialogInitPayloads.length');
    expect(initScriptsIdx).toBeGreaterThan(-1);
    expect(dialogInitIdx).toBeGreaterThan(-1);
    expect(initScriptsIdx).toBeLessThan(dialogInitIdx);
  });

  test('ff._dispatchIslandWhenReady and ff._islandModulesFor are unchanged (still defined, still route through layui.use)', () => {
    expect(active).toMatch(/_dispatchIslandWhenReady\s*:\s*function/);
    expect(active).toMatch(/_islandModulesFor\s*:\s*function/);
  });

  test('active-code eval( count is still exactly 1 after the #576 change', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('#558 bindSubmit guard and #564 highlightErrors containment logic are untouched', () => {
    // Just a presence/shape check — this fix must not have modified these
    // sections. Full behavioral coverage lives in their own test files.
    expect(active).toMatch(/case\s+['"]bindSubmit['"]/);
    expect(active).toMatch(/WTM_BEFORESUBMIT_DENYLIST/);
    expect(active).toMatch(/case\s+['"]highlightErrors['"]/);
  });

  test('the #552 per-case slider/rate/colorpicker layui.use guards are still present (kept as redundant defense-in-depth)', () => {
    expect(active).toMatch(/_renderSliderAction\s*:\s*function/);
    expect(active).toMatch(/_renderRateAction\s*:\s*function/);
    expect(active).toMatch(/_renderColorpickerAction\s*:\s*function/);
    expect(active).toMatch(/layui\.use\s*\(\s*\[\s*['"]slider['"]\s*\][\s\S]{0,60}_renderSliderAction/);
  });
});

// ---------------------------------------------------------------------------
// Behavioral: real ff.OpenDialog driven end-to-end via a vm-loaded fresh
// instance of the actual source, with a controllable $.ajax + layui mock.
// ---------------------------------------------------------------------------

function loadFreshFfWithLayui(layui, ajaxImpl) {
  // Minimal jQuery mock: $.ajax drives the OpenDialog success callback with
  // caller-supplied HTML + response headers; $.cookie is a no-op (OpenDialog's
  // cookie bookkeeping is wrapped in try/catch in the real source and is not
  // part of what this file verifies). $('<div/>').text(s).html() is a real
  // passthrough chain — ff.EscapeText (used by the 'alert'/'message'
  // DispatchAction cases) relies on it to HTML-encode plain text.
  const jqueryMock = Object.assign(
    function () {
      return {
        cookie: jest.fn(),
        text: function (s) { this._text = s; return this; },
        html: function () { return this._text; }
      };
    },
    { ajax: ajaxImpl, cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jqueryMock,
    // OpenDialog's #462/#470 island + legacy-script extraction uses the real
    // DOMParser to walk the AJAX response HTML — this vm context is a bare
    // V8 context (no jsdom globals attached automatically), so DOMParser
    // must be forwarded explicitly from this (real jsdom) test environment.
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx.ff;
}

// A mock layui whose named submodule starts UNLOADED. layui.use queues its
// callback and only fires it once every requested module has been load()ed —
// same harness shape as #556's laydate test and #552's dialog-path test.
// Also carries a `.layer` mock so ff.OpenDialog's layer.load/close/open calls
// resolve without needing the real layui.layer.
function makeLateLayuiWithDialog() {
  var pending = [];
  var loadedMods = {};
  var layer = {
    load: jest.fn(() => 1),
    close: jest.fn(),
    alert: jest.fn(),
    full: jest.fn(),
    open: jest.fn((opts) => {
      // Mirrors the real layer.open: the dialog is "rendered" and its
      // success callback fires. Synchronous here (as it effectively is in
      // the real browser once layer.open returns), matching the harness
      // convention already used for _dispatchIslandWhenReady in #552/#556.
      if (typeof opts.success === 'function') { opts.success(); }
      return 'mockWinId';
    })
  };
  var layui = {
    layer: layer,
    use: function (mods, cb) {
      pending.push({ mods: mods, cb: cb });
      flush();
    }
  };
  function flush() {
    pending = pending.filter(function (p) {
      var ready = p.mods.every(function (m) { return loadedMods[m]; });
      if (ready) { cbSafe(p.cb); return false; }
      return true;
    });
  }
  function cbSafe(cb) { cb(); }
  return {
    layui: layui,
    layer: layer,
    load: function (mod, moduleMock) {
      loadedMods[mod] = true;
      layui[mod] = moduleMock;
      flush();
    }
  };
}

// Builds an $.ajax mock that immediately (synchronously) invokes the
// caller's success handler with the given response body + header map —
// mirroring how OpenDialog's own $.ajax call is consumed.
function makeAjaxSuccess(responseHtml, headers) {
  return jest.fn((opts) => {
    var request = {
      getResponseHeader: function (name) {
        return Object.prototype.hasOwnProperty.call(headers || {}, name)
          ? headers[name]
          : null;
      }
    };
    opts.success(responseHtml, 'success', request);
  });
}

function dialogHtmlWithLaydateIsland(elemSelector, legacyScriptBody) {
  var island = JSON.stringify({
    actions: [{ type: 'laydate', opts: { elem: elemSelector, type: 'date' } }]
  });
  var legacy = legacyScriptBody
    ? '<script>' + legacyScriptBody + '</script>'
    : '';
  return (
    '<div class="dlg-body">' + legacy +
    '<input id="' + elemSelector.replace('#', '') + '">' +
    '<script type="application/json" class="wtm-dialog-init">' + island + '</script>' +
    '</div>'
  );
}

describe('#576 ff.OpenDialog dialog-init dispatch — module not yet loaded', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('a dialog-dispatched laydate island whose module is NOT yet loaded still renders once the module "arrives"', () => {
    const harness = makeLateLayuiWithDialog();
    const laydateRender = jest.fn();
    const html = dialogHtmlWithLaydateIsland('#576LateDate');
    const ajax = makeAjaxSuccess(html, {});
    const ff = loadFreshFfWithLayui(harness.layui, ajax);

    ff.OpenDialog('/some/edit/url', 'w576a', 'Title', 500, 400);

    // Dialog opened, dispatch loop ran, but 'laydate' module is not loaded
    // yet — render must be QUEUED, not dropped.
    expect(laydateRender).not.toHaveBeenCalled();

    // The module "arrives" (layui.use's queued callback fires).
    harness.load('laydate', { render: laydateRender });

    expect(laydateRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledWith({ elem: '#576LateDate', type: 'date' });
  });
});

describe('#576 ff.OpenDialog dialog-init dispatch — module already loaded (no regression)', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('an already-loaded module still renders immediately (synchronously) when dispatched via dialog-open', () => {
    const harness = makeLateLayuiWithDialog();
    const laydateRender = jest.fn();
    harness.load('laydate', { render: laydateRender }); // loaded BEFORE the dialog opens

    const html = dialogHtmlWithLaydateIsland('#576EarlyDate');
    const ajax = makeAjaxSuccess(html, {});
    const ff = loadFreshFfWithLayui(harness.layui, ajax);

    ff.OpenDialog('/some/edit/url', 'w576b', 'Title', 500, 400);

    // layui.use fires its callback immediately for an already-loaded module —
    // same effective (synchronous, immediate) timing as the pre-#576 direct
    // ff.DispatchAction call.
    expect(laydateRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledWith({ elem: '#576EarlyDate', type: 'date' });
  });
});

describe('#576 ordering — legacy inline-script rehydration always runs before island dispatch', () => {
  afterEach(() => {
    document.body.innerHTML = '';
    delete window.__576_log;
  });

  test('legacy script runs before a SYNCHRONOUSLY-dispatched island (module already loaded)', () => {
    window.__576_log = [];
    const harness = makeLateLayuiWithDialog();
    const laydateRender = jest.fn(function (opts) {
      window.__576_log.push('island:' + opts.elem);
    });
    harness.load('laydate', { render: laydateRender });

    const html = dialogHtmlWithLaydateIsland(
      '#576OrderA',
      "window.__576_log.push('legacy');"
    );
    const ajax = makeAjaxSuccess(html, {});
    const ff = loadFreshFfWithLayui(harness.layui, ajax);

    ff.OpenDialog('/some/edit/url', 'w576c', 'Title', 500, 400);

    expect(window.__576_log).toEqual(['legacy', 'island:#576OrderA']);
  });

  test('legacy script runs before a DEFERRED island dispatch (module not yet loaded at dialog-open time)', () => {
    window.__576_log = [];
    const harness = makeLateLayuiWithDialog();
    const laydateRender = jest.fn(function (opts) {
      window.__576_log.push('island:' + opts.elem);
    });

    const html = dialogHtmlWithLaydateIsland(
      '#576OrderB',
      "window.__576_log.push('legacy');"
    );
    const ajax = makeAjaxSuccess(html, {});
    const ff = loadFreshFfWithLayui(harness.layui, ajax);

    ff.OpenDialog('/some/edit/url', 'w576d', 'Title', 500, 400);

    // Island dispatch is deferred (module not loaded) — legacy script has
    // already run (it is fully synchronous and precedes the dispatch loop),
    // island has not fired yet.
    expect(window.__576_log).toEqual(['legacy']);

    harness.load('laydate', { render: laydateRender });

    // Once the deferred dispatch fires, the legacy entry is still first.
    expect(window.__576_log).toEqual(['legacy', 'island:#576OrderB']);
  });
});

describe('#576 existing-behavior regression — non-module action (alert) still dispatches synchronously via dialog-open', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('an island with no layui module dependency (e.g. alert) dispatches synchronously with no layui.use involved', () => {
    const harness = makeLateLayuiWithDialog();
    const useSpy = jest.spyOn(harness.layui, 'use');
    const island = JSON.stringify({ actions: [{ type: 'alert', message: 'Saved OK' }] });
    const html =
      '<script type="application/json" class="wtm-dialog-init">' + island + '</script>';
    const ajax = makeAjaxSuccess(html, {});
    const ff = loadFreshFfWithLayui(harness.layui, ajax);

    ff.OpenDialog('/some/edit/url', 'w576e', 'Title', 500, 400);

    // 'alert' has no layui module dependency, so ff._islandModulesFor returns
    // an empty list and ff._dispatchIslandWhenReady's else-branch dispatches
    // it via a direct, synchronous ff.DispatchAction(payload) call — layui.use
    // is never invoked for this payload, and layer.alert fires immediately,
    // matching the pre-#576 timing exactly for module-free actions.
    expect(useSpy).not.toHaveBeenCalled();
    // ff.DispatchAction's 'alert' case always runs action.title through
    // ff.EscapeText(action.title || '') (Issue #805), so a missing title
    // becomes '' (not undefined) by the time it reaches layer.alert.
    expect(harness.layer.alert).toHaveBeenCalledWith('Saved OK', { title: '' });
  });
});
