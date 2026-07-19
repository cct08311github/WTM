// Tests for Issue #470 Slice J — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF) eval-free 'renderSelect' JSON island for <wt:combobox>/
// <wt:tree>, plus the always-on ff.ChainChange existence-guard hardening
// (Part 1a) that ships regardless of the flag.
//
// This file drives the REAL ff.DispatchAction / ff.ChainChange / ff._render-
// SelectAction / ff._readFieldDefaults end-to-end against a FRESH vm instance
// of the actual shipped framework_layui.js source (real jsdom `document`) —
// not a source-sweep or a hand-rolled reimplementation — following the same
// convention as framework_layui_470_sliceH_laydate.test.js and
// framework_layui_645_chainchange_itemurl_race.test.js. xmSelect.render is
// stubbed to a sentinel object (xmSelect itself is a plain <script src>
// global in production, never loaded under jsdom).

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
// jQuery-compatible mock — union of framework_layui_645_chainchange_itemurl_
// race.test.js's makeQueuedJQueryMock (find/attr-getter+setter/html/append)
// with an IMMEDIATE (not queued) $.get, matching framework_layui_638_
// chainchange_defaults_guard.test.js's simpler synchronous style — this file
// never needs to interleave two competing in-flight requests.
// ---------------------------------------------------------------------------
function makeJQueryMock(ajaxData) {
  function wrap(el) {
    return {
      0: el,
      length: el ? 1 : 0,
      attr: function (name, value) {
        if (!el) { return value === undefined ? undefined : this; }
        if (value === undefined) {
          return el.getAttribute(name) === null ? undefined : el.getAttribute(name);
        }
        el.setAttribute(name, value);
        return this;
      },
      html: function (str) { if (el) { el.innerHTML = str; } return this; },
      append: function (child) { if (el) { el.appendChild(child); } return this; },
      find: function (selector) { return wrap(el ? el.querySelector(selector) : null); }
    };
  }
  const getSpy = jest.fn(function (url, params, cb) {
    cb(ajaxData, 'success');
  });
  const $ = function (selector) {
    if (typeof selector === 'string' && selector.charAt(0) === '#') {
      return wrap(document.getElementById(selector.slice(1)));
    }
    return wrap(null);
  };
  $.get = getSpy;
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.fn = {};
  return $;
}

const SENTINEL = { __fake: 'xmSelectInstance' };

function loadFreshFf(opts) {
  opts = opts || {};
  const xmSelectRender = jest.fn(() => SENTINEL);
  const $ = opts.$ || makeJQueryMock(opts.ajaxData);
  const layui = opts.layui || {
    form: { render: jest.fn() },
    transfer: { reload: jest.fn() },
    layer: { alert: jest.fn() }
  };
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    xmSelect: { render: xmSelectRender },
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx, xmSelectRender, $ };
}

function appendDiv(id, attrs) {
  const el = document.createElement('div');
  el.id = id;
  if (attrs) {
    Object.keys(attrs).forEach((k) => el.setAttribute(k, attrs[k]));
  }
  document.body.appendChild(el);
  return el;
}

function buildChainSourceDom(sourceId, targetId, controltype) {
  document.body.innerHTML = '';
  const form = document.createElement('form');
  form.id = 'TestForm';
  const source = document.createElement('div');
  source.id = sourceId;
  source.setAttribute('wtm-linkto', targetId);
  const target = document.createElement('div');
  target.id = targetId;
  target.setAttribute('wtm-ctype', controltype);
  target.setAttribute('wtm-name', 'TargetField');
  form.appendChild(source);
  form.appendChild(target);
  document.body.appendChild(form);
  return { form, source, target };
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice J — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice J changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderSelect' case delegates to ff._renderSelectAction, no inline body", () => {
    const block = active.match(/case\s+['"]renderSelect['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderSelectAction\(\s*action\s*\)/);
  });

  test('_renderSelectAction resolves changeFunc through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = active.match(/_renderSelectAction:\s*function[\s\S]*?\n\s{4}\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFunc\s*\)/);
    expect(block[0]).not.toMatch(/\beval\(/);
    expect(block[0]).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderSelect' has NO _islandModulesFor entry — xmSelect is a plain <script src>, not a layui.use(...) module", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(fn).not.toMatch(/renderSelect/);
  });
});

// ---------------------------------------------------------------------------
// (1) dispatch renderSelect → window[id] === sentinel; defaultvalues global set
// ---------------------------------------------------------------------------
describe('#470 Slice J — renderSelect basic render', () => {
  test('dispatch sets window[id] to the xmSelect.render() return value and window[id+"defaultvalues"]', () => {
    appendDiv('RS1');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RS1', el: '#RS1',
        name: 'Field1', tips: 'pick one', disabled: false, language: 'en',
        autoRow: false, filterable: false, multiSelect: true,
        items: [{ id: '1', title: 'One' }],
        defaultValues: ['1']
      }]
    });

    expect(xmSelectRender).toHaveBeenCalledTimes(1);
    expect(windowObj.RS1).toBe(SENTINEL);
    expect(windowObj.RS1defaultvalues).toEqual(['1']);
    const cfg = xmSelectRender.mock.calls[0][0];
    expect(cfg.el).toBe('#RS1');
    expect(cfg.data).toEqual([{ id: '1', title: 'One' }]);
  });

  test('missing defaultValues degrades to an empty array, never undefined/null', () => {
    appendDiv('RS2');
    const { ff, windowObj } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'tree', id: 'RS2', el: '#RS2', multiSelect: false }]
    });
    expect(windowObj.RS2defaultvalues).toEqual([]);
  });

  test('xmSelect not loaded: skips with a console.warn, never throws', () => {
    appendDiv('RS3');
    const xmSelectRender = jest.fn();
    const ctx = vm.createContext({
      window: {}, document, layui: {}, console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: makeJQueryMock(), DOMParser: global.DOMParser,
      DONOTUSE_TABLAYID: undefined, DONOTUSE_COOKIEPRE: '', DONOTUSE_WINDOWGUID: '',
      // xmSelect intentionally omitted from the context (undefined).
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ctx.ff.DispatchAction({ actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS3', el: '#RS3' }] });
    }).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('xmSelect is not loaded'));
    warnSpy.mockRestore();
  });
});

// ---------------------------------------------------------------------------
// (2) data-wtm-defaults is readable BEFORE any island dispatch (decoupled)
// ---------------------------------------------------------------------------
describe('#470 Slice J — data-wtm-defaults decoupled from render timing', () => {
  test('ff._readFieldDefaults reads the attribute with ZERO islands dispatched', () => {
    const el = appendDiv('RS4', { 'data-wtm-defaults': '["a","b"]' });
    const { ff, $ } = loadFreshFf();
    // Wrap exactly the way ChainChange resolves its own `target` — via the
    // jQuery mock, never a raw DOM node — proving the read works
    // independently of any DispatchAction/renderSelect call.
    const wrapped = $('#RS4');
    expect(ff._readFieldDefaults(wrapped, 'RS4')).toEqual(['a', 'b']);
    // No render call was ever dispatched for RS4 — window.RS4 stays unset.
    expect(el.id in {}).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// (3) on: handler wiring — ChainChange gate + changeFunc-returning-false
// suppression
// ---------------------------------------------------------------------------
describe('#470 Slice J — renderSelect on: handler wires ff.ChainChange', () => {
  test('linkTo present, no changeFunc: on(data) always calls ff.ChainChange with the built triggerUrl', () => {
    appendDiv('RS5');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RS5', el: '#RS5',
        linkTo: 'SomeTarget', triggerUrl: '/trigger'
      }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    cfg.on({ arr: [{ value: 'x' }, { value: 'y' }] });

    expect(chainSpy).toHaveBeenCalledTimes(1);
    const [url, el] = chainSpy.mock.calls[0];
    expect(url).toMatch(/^\/trigger\?t=\d+&id=x&id=y$/);
    expect(el.id).toBe('RS5');
  });

  test('linkTo present + changeFunc returns false: ff.ChainChange is SUPPRESSED', () => {
    appendDiv('RS6');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;
    windowObj.gateFalse = jest.fn(() => false);

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RS6', el: '#RS6',
        linkTo: 'SomeTarget', triggerUrl: '/trigger', changeFunc: 'gateFalse'
      }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    cfg.on({ arr: [] });

    expect(windowObj.gateFalse).toHaveBeenCalledTimes(1);
    expect(chainSpy).not.toHaveBeenCalled();
  });

  test('linkTo present + changeFunc returns (non-false): ff.ChainChange still fires', () => {
    appendDiv('RS6b');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;
    windowObj.gateTrue = jest.fn(() => true);

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RS6b', el: '#RS6b',
        linkTo: 'SomeTarget', triggerUrl: '/trigger', changeFunc: 'gateTrue'
      }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    cfg.on({ arr: [] });

    expect(windowObj.gateTrue).toHaveBeenCalledTimes(1);
    expect(chainSpy).toHaveBeenCalledTimes(1);
  });

  test('no linkTo, bare changeFunc identifier: on(data) calls changeFn(data) directly, never ff.ChainChange', () => {
    appendDiv('RS7');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;
    windowObj.plainChange = jest.fn();

    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS7', el: '#RS7', changeFunc: 'plainChange' }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    const payload = { arr: [] };
    cfg.on(payload);

    expect(windowObj.plainChange).toHaveBeenCalledWith(payload);
    expect(chainSpy).not.toHaveBeenCalled();
  });

  test('no linkTo, no changeFunc: on(data) is a safe no-op', () => {
    appendDiv('RS8');
    const { ff, xmSelectRender } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'renderSelect', widget: 'tree', id: 'RS8', el: '#RS8' }] });
    const cfg = xmSelectRender.mock.calls[0][0];
    expect(() => cfg.on({ arr: [] })).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// (3b) LOOSE (!=) gate semantics — HIGH defect fix: the gate must match the
// legacy inline render's `if ({ChangeFunc} != false)` EXACTLY, not a strict
// (!==) comparison. A changeFunc returning 0, '', or '0' is `== false` (loose)
// but NOT `=== false` (strict) — legacy BLOCKS the chain for all three; the
// island must now match, not fire it. null/undefined must still FIRE the
// chain under loose comparison (both `!= false`), same as legacy's
// fallthrough for "no gate value returned".
// ---------------------------------------------------------------------------
describe('#470 Slice J follow-up — gate uses LOOSE (!=) inequality, matching legacy exactly', () => {
  function dispatchWithGate(id, gateReturnValue) {
    appendDiv(id);
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;
    windowObj.gateFn = jest.fn(() => gateReturnValue);

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: id, el: '#' + id,
        linkTo: 'SomeTarget', triggerUrl: '/trigger', changeFunc: 'gateFn'
      }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    cfg.on({ arr: [] });
    return chainSpy;
  }

  test.each([
    ['0 (number)', 0],
    ["'' (empty string)", ''],
    ["'0' (string)", '0'],
    ['false (boolean)', false],
  ])('changeFunc returns %s (== false, loose): ff.ChainChange is BLOCKED, matching legacy', (_label, value) => {
    const chainSpy = dispatchWithGate('RSGate_' + JSON.stringify(value).replace(/\W/g, ''), value);
    expect(chainSpy).not.toHaveBeenCalled();
  });

  test.each([
    ['true (boolean)', true, 'RSGate2_true'],
    ['1 (number)', 1, 'RSGate2_1'],
    ["'x' (non-empty string)", 'x', 'RSGate2_x'],
    ['undefined (no return)', undefined, 'RSGate2_undefined'],
  ])('changeFunc returns %s (!= false): ff.ChainChange FIRES, matching legacy', (_label, value, id) => {
    const chainSpy = dispatchWithGate(id, value);
    expect(chainSpy).toHaveBeenCalledTimes(1);
  });

  test('null gate value (!= false, loose): ff.ChainChange FIRES — same fallthrough as legacy', () => {
    const chainSpy = dispatchWithGate('RSGateNull', null);
    expect(chainSpy).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// (3c) required-validation carried inside the island — HIGH defect fix:
// action.layVerify/action.layReqText must be applied via window[id].update(...)
// IMMEDIATELY AFTER xmSelect.render(...), the same call BaseFieldTag.cs's
// (now island-path-skipped) inline <script> used to make. Absent action.
// layVerify (non-required fields), update() must NOT be called for this
// purpose at all.
// ---------------------------------------------------------------------------
describe('#470 Slice J follow-up — renderSelect island applies layVerify/layReqText after render', () => {
  test('action.layVerify present: window[id].update({layVerify, layReqText}) is called once, after render', () => {
    appendDiv('RSReq1');
    const updateSpy = jest.fn();
    const xmSelectRender = jest.fn(() => ({ update: updateSpy }));
    const ctx = vm.createContext({
      window: {}, document, layui: { form: { render: jest.fn() } }, console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: makeJQueryMock(), DOMParser: global.DOMParser,
      xmSelect: { render: xmSelectRender },
      DONOTUSE_TABLAYID: undefined, DONOTUSE_COOKIEPRE: '', DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);

    ctx.ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RSReq1', el: '#RSReq1',
        layVerify: 'required', layReqText: 'This field is required'
      }]
    });

    expect(xmSelectRender).toHaveBeenCalledTimes(1);
    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy).toHaveBeenCalledWith({ layVerify: 'required', layReqText: 'This field is required' });
    // Ordering: update() must be called AFTER render() — verified by both
    // having been invoked (update() is only reachable once window[id] is
    // assigned the render() return value).
  });

  test('action.layVerify absent (non-required field): update() is never called', () => {
    appendDiv('RSReq2');
    const { ff, xmSelectRender } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RSReq2', el: '#RSReq2' }]
    });
    // The default stub xmSelect.render() returns SENTINEL (a plain object,
    // no update method) — if the code tried to call .update() unconditionally
    // it would throw. Not throwing proves update() was never invoked.
    expect(xmSelectRender).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// (4) guarded changeFunc resolution — adversarial names never throw/eval
// ---------------------------------------------------------------------------
describe('#470 Slice J — changeFunc adversarial resolution (ff._resolveGuardedWindowFn)', () => {
  test('dotted name "a.b" is rejected — cfg.on never calls it, never throws', () => {
    appendDiv('RS9');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const spy = jest.fn();
    windowObj.a = { b: spy };
    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS9', el: '#RS9', changeFunc: 'a.b' }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    expect(() => cfg.on({ arr: [] })).not.toThrow();
    expect(spy).not.toHaveBeenCalled();
  });

  test('denylisted global name "eval" is rejected even though own+callable+identifier', () => {
    appendDiv('RS10');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const spy = jest.fn();
    windowObj.eval = spy;
    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS10', el: '#RS10', changeFunc: 'eval' }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    expect(() => cfg.on({ arr: [] })).not.toThrow();
    expect(spy).not.toHaveBeenCalled();
  });

  test('non-own (prototype-chain) name "toString" is rejected', () => {
    appendDiv('RS11');
    const { ff, xmSelectRender } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS11', el: '#RS11', changeFunc: 'toString' }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    expect(() => cfg.on({ arr: [] })).not.toThrow();
  });

  test('non-function value is rejected', () => {
    appendDiv('RS12');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    windowObj.notAFunction = 'just a string';
    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS12', el: '#RS12', changeFunc: 'notAFunction' }]
    });
    const cfg = xmSelectRender.mock.calls[0][0];
    expect(() => cfg.on({ arr: [] })).not.toThrow();
  });

  test('this file never causes a new eval( or new Function( call site to appear', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });
});

// ---------------------------------------------------------------------------
// (5) chainInitial — fake-timers, 100ms advance fires ChainChange(...,true)
// ---------------------------------------------------------------------------
describe('#470 Slice J — chainInitial setTimeout(100) initial fire', () => {
  beforeEach(() => { jest.useFakeTimers(); });
  afterEach(() => { jest.useRealTimers(); });

  test('chainInitial && linkTo: after 100ms, ff.ChainChange(scoped url, el, true) fires with defaultValues appended as &id= (legacy {Id}u/{Id}data parity)', () => {
    appendDiv('RS13');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RS13', el: '#RS13',
        linkTo: 'SomeTarget', triggerUrl: '/initial', chainInitial: true,
        defaultValues: ['101', '205']
      }]
    });
    expect(xmSelectRender).toHaveBeenCalledTimes(1);
    expect(chainSpy).not.toHaveBeenCalled();

    jest.advanceTimersByTime(100);

    expect(chainSpy).toHaveBeenCalledTimes(1);
    const [url, el, useDefault] = chainSpy.mock.calls[0];
    // Mirrors the legacy inline render EXACTLY (ComboBoxTagHelper.cs/
    // TreeTagHelper.cs {Id}u/{Id}data block): triggerUrl + '?t=<timestamp>'
    // (no '?' in triggerUrl) + '&id=' for each pre-selected value — never
    // the bare triggerUrl.
    expect(url).toMatch(/^\/initial\?t=\d+&id=101&id=205$/);
    expect(el.id).toBe('RS13');
    expect(useDefault).toBe(true);
  });

  test('chainInitial && linkTo, triggerUrl already has a query string: no "?t=" cache-buster is appended, defaultValues still appended as &id=', () => {
    appendDiv('RS13b');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'tree', id: 'RS13b', el: '#RS13b',
        linkTo: 'SomeTarget', triggerUrl: '/initial?foo=bar', chainInitial: true,
        defaultValues: ['9']
      }]
    });
    expect(xmSelectRender).toHaveBeenCalledTimes(1);

    jest.advanceTimersByTime(100);

    expect(chainSpy).toHaveBeenCalledTimes(1);
    const [url, el, useDefault] = chainSpy.mock.calls[0];
    expect(url).toBe('/initial?foo=bar&id=9');
    expect(el.id).toBe('RS13b');
    expect(useDefault).toBe(true);
  });

  test('chainInitial && linkTo, missing/empty defaultValues: no &id= params, URL degrades to triggerUrl + cache-buster only', () => {
    appendDiv('RS13c');
    const { ff, windowObj, xmSelectRender } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'combo', id: 'RS13c', el: '#RS13c',
        linkTo: 'SomeTarget', triggerUrl: '/initial', chainInitial: true
      }]
    });
    expect(xmSelectRender).toHaveBeenCalledTimes(1);

    jest.advanceTimersByTime(100);

    expect(chainSpy).toHaveBeenCalledTimes(1);
    const [url, el, useDefault] = chainSpy.mock.calls[0];
    expect(url).toMatch(/^\/initial\?t=\d+$/);
    expect(el.id).toBe('RS13c');
    expect(useDefault).toBe(true);
  });

  test('chainInitial true but linkTo absent: no initial fire (belt-and-suspenders gate)', () => {
    appendDiv('RS14');
    const { ff, windowObj } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;

    ff.DispatchAction({
      actions: [{ type: 'renderSelect', widget: 'combo', id: 'RS14', el: '#RS14', chainInitial: true }]
    });
    jest.advanceTimersByTime(200);
    expect(chainSpy).not.toHaveBeenCalled();
  });

  test('chainInitial false: no initial fire even with linkTo present', () => {
    appendDiv('RS15');
    const { ff, windowObj } = loadFreshFf();
    const chainSpy = jest.fn();
    windowObj.ff.ChainChange = chainSpy;

    ff.DispatchAction({
      actions: [{
        type: 'renderSelect', widget: 'tree', id: 'RS15', el: '#RS15',
        linkTo: 'SomeTarget', triggerUrl: '/x', chainInitial: false
      }]
    });
    jest.advanceTimersByTime(200);
    expect(chainSpy).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// (6) NEW ChainChange existence-guard (Part 1a — flag-independent hardening)
// ---------------------------------------------------------------------------
describe('#470 Slice J — ff.ChainChange existence-guard (combo/tree)', () => {
  ['combo', 'tree'].forEach((controltype) => {
    test(`${controltype}: clear step — window[comboid] undefined → console.warn, no throw`, () => {
      const { source } = buildChainSourceDom('Src_' + controltype, 'Unrendered_' + controltype, controltype);
      const { ff } = loadFreshFf();
      const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

      expect(() => { ff.ChainChange('', source); }).not.toThrow();

      expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('Unrendered_' + controltype));
      warnSpy.mockRestore();
    });

    test(`${controltype}: apply step (post-$.get) — window[comboid] undefined → console.warn, no throw, no update() call`, () => {
      const data = { Data: [{ Value: '1', Text: 'One', Selected: false }] };
      const { source } = buildChainSourceDom('SrcApply_' + controltype, 'UnrenderedApply_' + controltype, controltype);
      const { ff } = loadFreshFf({ ajaxData: data });
      const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

      expect(() => { ff.ChainChange('/some/url', source); }).not.toThrow();

      expect(warnSpy).toHaveBeenCalledWith(
        expect.stringContaining('UnrenderedApply_' + controltype)
      );
      // Never claimed — the marker is only stamped on a successful apply.
      expect(document.getElementById('UnrenderedApply_' + controltype).getAttribute('data-wtm-chain-applied')).toBeNull();
      warnSpy.mockRestore();
    });

    test(`${controltype}: when window[comboid] DOES exist, behavior is unchanged (update() still called, no warn)`, () => {
      const data = { Data: [{ Value: '1', Text: 'One', Selected: false }] };
      const { source } = buildChainSourceDom('SrcOk_' + controltype, 'Rendered_' + controltype, controltype);
      const { ff, windowObj } = loadFreshFf({ ajaxData: data });
      const updateSpy = jest.fn();
      windowObj['Rendered_' + controltype] = { update: updateSpy };
      const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

      ff.ChainChange('/some/url', source);

      expect(updateSpy).toHaveBeenCalled();
      expect(warnSpy).not.toHaveBeenCalled();
      warnSpy.mockRestore();
    });
  });
});

// ---------------------------------------------------------------------------
// (7) dispatch reaches ff._renderSelectAction via the OpenDialog2 (selector
// search-panel) path too — mirrors framework_layui_470_sliceH_laydate.test.js
// / framework_layui_633_opendialog2_selector_panel.test.js's harness.
// ---------------------------------------------------------------------------
describe('#470 Slice J — dispatch reaches xmSelect.render via ff.OpenDialog2 (selector path)', () => {
  function makeJQueryMockForOpenDialog2() {
    function wrap(el) { return { 0: el, length: el ? 1 : 0 }; }
    const $ = function (selector) {
      if (typeof selector === 'string' && selector.charAt(0) === '#') {
        return wrap(document.getElementById(selector.slice(1)));
      }
      return wrap(null);
    };
    $.ajax = jest.fn((opts) => {
      const request = { getResponseHeader: () => null };
      opts.success(OPEN_DIALOG2_RESPONSE_HTML, 'success', request);
    });
    $.cookie = jest.fn();
    $.fn = {};
    return $;
  }

  function loadFreshFfForOpenDialog2(layui, $, xmSelectRender) {
    const ctx = vm.createContext({
      window: {},
      document,
      layui: layui,
      xmSelect: { render: xmSelectRender },
      console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: $,
      DOMParser: global.DOMParser,
      DOMPurify: { sanitize: (html) => (html === undefined || html === null ? '' : html) },
      DONOTUSE_TABLAYID: undefined,
      DONOTUSE_COOKIEPRE: '',
      DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    return { ff: ctx.ff, windowObj: ctx };
  }

  function makeOpenDialog2Layui() {
    return {
      use: jest.fn((mods, cb) => cb()),
      form: { render: jest.fn() },
      layer: {
        load: jest.fn(() => 1),
        close: jest.fn(),
        alert: jest.fn(),
        full: jest.fn(),
        open: jest.fn((opts) => {
          const container = document.createElement('div');
          container.innerHTML = opts.content;
          document.body.appendChild(container);
          if (typeof opts.success === 'function') { opts.success(container); }
          return 'mockWinId';
        })
      }
    };
  }

  function makeOpenDialog2TempEl(id, innerHtml) {
    const el = document.createElement('div');
    el.id = id;
    el.innerHTML = innerHtml;
    document.body.appendChild(el);
    return el;
  }

  const OPEN_DIALOG2_RESPONSE_HTML =
    '<div>wtVar_x = table.render(xoption);</div>' +
    '<table id="g1" lay-filter="f1"></table>' +
    '$$SearchPanel$$';

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
  });

  test('a renderSelect island tokenized into the search panel reaches xmSelect.render end-to-end', () => {
    const xmSelectRender = jest.fn(() => SENTINEL);
    const layui = makeOpenDialog2Layui();
    const $ = makeJQueryMockForOpenDialog2();
    const payload = {
      type: 'renderSelect', widget: 'combo', id: 'SelectorCombo470j', el: '#SelectorCombo470j',
      items: [{ id: '1', title: 'One' }]
    };
    makeOpenDialog2TempEl(
      'Temp470j',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$<div id="SelectorCombo470j"></div>'
    );
    const { ff, windowObj } = loadFreshFfForOpenDialog2(layui, $, xmSelectRender);

    ff.OpenDialog2('/some/search/url', 'w470j', 'Title', 500, 400, '#Temp470j');

    expect(xmSelectRender).toHaveBeenCalledTimes(1);
    expect(windowObj.SelectorCombo470j).toBe(SENTINEL);
  });
});
