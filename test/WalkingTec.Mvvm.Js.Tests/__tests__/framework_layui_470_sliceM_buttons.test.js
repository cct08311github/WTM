// Tests for Issue #470 Slice M — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF — the SAME flag #470 Slices J/K/L use) delegated data-wtm-click
// dispatch that replaces LayuiUIService.Make*'s generated per-button
// <script>function x{guid}click(){...}</script> + onclick, MakeDateTime's
// onclick='ff.SetGridCellDate(...)' grid-cell wiring, and
// SubmitButtonTagHelper's generated <script>function f_{Id}Click(){...}</script>
// handshake.
//
// This file drives the REAL ff._buttonAction / ff._submitButtonClick /
// ff._resolveGuardedWindowFn / the delegated document click listener
// end-to-end against a FRESH vm instance of the actual shipped
// framework_layui.js source (real jsdom `document`) — not a source-sweep or
// a hand-rolled reimplementation — following the same convention as
// framework_layui_470_sliceL_upload.test.js. ff.OpenDialog/ff.RunAction/
// ff.BgRequest/ff.LoadPage/ff.SetGridCellDate/ff.PostForm are the REAL
// framework functions (only their $.ajax/layui dependencies are stubbed),
// so this proves the delegated dispatch reaches the actual fixed framework
// actions, not a mock standing in for them.

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

function makeJQueryMock(overrides) {
  const $ = function () { return { 0: null, length: 0, trigger: function () {} }; };
  $.get = jest.fn();
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.ajaxSettings = { xhr: jest.fn(() => ({})) };
  $.fn = {};
  return Object.assign($, overrides || {});
}

function makeLayui(overrides) {
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    form: { render: jest.fn() },
    layer: {
      load: jest.fn(() => 1),
      close: jest.fn(),
      msg: jest.fn(),
      alert: jest.fn(),
      photos: jest.fn(),
      open: jest.fn()
    }
  }, overrides || {});
}

function loadFreshFf(opts) {
  opts = opts || {};
  const $ = opts.$ || makeJQueryMock();
  const layui = opts.layui || makeLayui();
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
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
  return { ff: ctx.ff, windowObj: ctx, layui, $ };
}

function appendEl(tag, attrs) {
  const el = document.createElement(tag || 'a');
  Object.keys(attrs || {}).forEach((k) => el.setAttribute(k, attrs[k]));
  document.body.appendChild(el);
  return el;
}

function click(el) {
  el.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice M — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice M changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('ff._buttonAction and ff._submitButtonClick exist on the real object', () => {
    const { ff } = loadFreshFf();
    expect(typeof ff._buttonAction).toBe('object');
    expect(typeof ff._buttonAction.openDialog).toBe('function');
    expect(typeof ff._buttonAction.runAction).toBe('function');
    expect(typeof ff._buttonAction.bgRequest).toBe('function');
    expect(typeof ff._buttonAction.loadPage).toBe('function');
    expect(typeof ff._buttonAction.view).toBe('function');
    expect(typeof ff._buttonAction.dateClick).toBe('function');
    expect(typeof ff._buttonAction.scriptCall).toBe('function');
    expect(typeof ff._submitButtonClick).toBe('function');
  });
});

// ---------------------------------------------------------------------------
// Delegated data-wtm-click dispatch — one test per fixed framework action
// ---------------------------------------------------------------------------
describe('#470 Slice M — delegated data-wtm-click dispatch', () => {
  test("data-wtm-click='openDialog' click -> ff.OpenDialog called with the carried params", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'OpenDialog').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-click': 'openDialog',
      'data-wtm-url': '/Some/Url',
      'data-wtm-winid': 'w1',
      'data-wtm-title': 'My Title',
      'data-wtm-width': '600',
      'data-wtm-height': '400',
      'data-wtm-max': 'true'
    });
    click(el);
    expect(spy).toHaveBeenCalledWith('/Some/Url', 'w1', 'My Title', 600, 400, undefined, true);
    spy.mockRestore();
  });

  test("data-wtm-click='openDialog' with no width/height -> passed through as undefined, not NaN", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'OpenDialog').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-click': 'openDialog',
      'data-wtm-url': '/u',
      'data-wtm-winid': 'w2',
      'data-wtm-title': '',
      'data-wtm-max': 'false'
    });
    click(el);
    expect(spy).toHaveBeenCalledWith('/u', 'w2', '', undefined, undefined, undefined, false);
    spy.mockRestore();
  });

  test("data-wtm-click='runAction' click -> ff.RunAction called with the carried url", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'RunAction').mockImplementation(() => {});
    const el = appendEl('a', { 'data-wtm-click': 'runAction', 'data-wtm-url': '/Run/It' });
    click(el);
    expect(spy).toHaveBeenCalledWith('/Run/It');
    spy.mockRestore();
  });

  test("data-wtm-click='bgRequest' click -> ff.BgRequest called with (url, undefined, divid)", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'BgRequest').mockImplementation(() => {});
    const el = appendEl('a', { 'data-wtm-click': 'bgRequest', 'data-wtm-url': '/Bg', 'data-wtm-divid': 'mydiv' });
    click(el);
    expect(spy).toHaveBeenCalledWith('/Bg', undefined, 'mydiv');
    spy.mockRestore();
  });

  // NOTE: these two 'loadPage' cases call ff._buttonAction.loadPage(el)
  // directly (the EXACT function the delegated click listener invokes —
  // see the 'nested ancestor'/'fragment insertion' tests above for full
  // click-dispatch coverage of that wiring) rather than dispatching a real
  // click event. Reason: ff.LoadPage's real implementation calls
  // window.open(...)/sets `location.hash`, browser globals jsdom's Window
  // doesn't fully back here; MORE IMPORTANTLY, `document` (and therefore any
  // click listener registered on it by an earlier loadFreshFf() call in this
  // same test file) is shared/accumulates across tests, so a real
  // document-level click would also re-invoke EVERY EARLIER test's
  // (unmocked) ff.LoadPage on this same element — calling the dispatch-map
  // function directly sidesteps that cross-test listener accumulation
  // entirely while still exercising the real production dispatch code.
  test("data-wtm-click='loadPage' (newwindow=true) -> ff.LoadPage called with (url, true, title)", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'LoadPage').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-click': 'loadPage', 'data-wtm-url': '/Lp', 'data-wtm-title': 'T',
      'data-wtm-newwindow': 'true'
    });
    ff._buttonAction.loadPage(el);
    expect(spy).toHaveBeenCalledWith('/Lp', true, 'T');
    spy.mockRestore();
  });

  test("data-wtm-click='loadPage' (newwindow=false) -> ff.LoadPage called with (url, false, title)", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'LoadPage').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-click': 'loadPage', 'data-wtm-url': '/Lp2', 'data-wtm-title': 'T2',
      'data-wtm-newwindow': 'false'
    });
    ff._buttonAction.loadPage(el);
    expect(spy).toHaveBeenCalledWith('/Lp2', false, 'T2');
    spy.mockRestore();
  });

  test("data-wtm-click='view' click -> layui.layer.photos called with a single-item photo array", () => {
    const { ff, layui } = loadFreshFf();
    const el = appendEl('img', { 'data-wtm-click': 'view', 'data-wtm-url': '/_Framework/GetFile/abc' });
    click(el);
    expect(layui.layer.photos).toHaveBeenCalledWith({
      photos: { data: [{ src: '/_Framework/GetFile/abc' }] },
      anim: 5
    });
  });

  test("data-wtm-click='view' click when layui.layer.photos is unavailable: no-op, never throws", () => {
    const { ff } = loadFreshFf({ layui: { use: jest.fn((m, cb) => cb()) } });
    const el = appendEl('img', { 'data-wtm-click': 'view', 'data-wtm-url': '/x' });
    expect(() => click(el)).not.toThrow();
  });

  // NOTE: calls ff._buttonAction.dateClick(el) directly — same rationale as
  // the 'loadPage' tests above (ff.SetGridCellDate's real implementation
  // needs a real layui.laydate module, and a real click would also
  // re-trigger every earlier test's accumulated document-level listener).
  test("data-wtm-click='dateClick' -> ff.SetGridCellDate called with (id, type)", () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'SetGridCellDate').mockImplementation(() => {});
    const el = appendEl('input', {
      'data-wtm-click': 'dateClick', 'data-wtm-date-id': 'Field1', 'data-wtm-date-type': 'date'
    });
    ff._buttonAction.dateClick(el);
    expect(spy).toHaveBeenCalledWith('Field1', 'date');
    spy.mockRestore();
  });

  test('click on a plain element without data-wtm-click: no-op, never throws', () => {
    const { ff } = loadFreshFf();
    const el = appendEl('div', {});
    expect(() => click(el)).not.toThrow();
  });

  test('data-wtm-click on a NESTED ancestor still dispatches (closest() resolution)', () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'RunAction').mockImplementation(() => {});
    const outer = appendEl('a', { 'data-wtm-click': 'runAction', 'data-wtm-url': '/Nested' });
    const inner = document.createElement('span');
    outer.appendChild(inner);
    click(inner);
    expect(spy).toHaveBeenCalledWith('/Nested');
    spy.mockRestore();
  });

  test('delegation works for elements inserted into the DOM AFTER ff loaded (fragment/dialog insertion, mirrors OpenDialog-style late insertion)', () => {
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff, 'RunAction').mockImplementation(() => {});
    // No element exists yet when ff (and its document-level listener) loaded.
    const container = document.createElement('div');
    container.innerHTML = "<a data-wtm-click='runAction' data-wtm-url='/Late'>go</a>";
    document.body.appendChild(container);
    const el = container.querySelector('a');
    click(el);
    expect(spy).toHaveBeenCalledWith('/Late');
    spy.mockRestore();
  });
});

// ---------------------------------------------------------------------------
// scriptCall — guarded developer identifier resolution (3-way decision)
// ---------------------------------------------------------------------------
describe("#470 Slice M — data-wtm-click='scriptCall' guarded resolution", () => {
  afterEach(() => { delete global.window; });

  test('a plain identifier resolves via ff._resolveGuardedWindowFn and is invoked', () => {
    const { ff, windowObj } = loadFreshFf();
    windowObj.myScriptFn = jest.fn();
    const el = appendEl('a', { 'data-wtm-click': 'scriptCall', 'data-wtm-fn': 'myScriptFn' });
    click(el);
    expect(windowObj.myScriptFn).toHaveBeenCalledTimes(1);
  });

  test('a denylisted name (e.g. eval) is rejected without throwing and without being invoked', () => {
    const { ff } = loadFreshFf();
    const el = appendEl('a', { 'data-wtm-click': 'scriptCall', 'data-wtm-fn': 'eval' });
    expect(() => click(el)).not.toThrow();
  });

  test('an undefined window function is rejected without throwing', () => {
    const { ff } = loadFreshFf();
    const el = appendEl('a', { 'data-wtm-click': 'scriptCall', 'data-wtm-fn': 'thisFunctionDoesNotExist470M' });
    expect(() => click(el)).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// ff._submitButtonClick — the SubmitButtonTagHelper f_{Id}Click handshake
// ---------------------------------------------------------------------------
describe('#470 Slice M — ff._submitButtonClick submit handshake', () => {
  test('no checkfn attribute (Click unset, ConfirmTxt-only case): check defaults to true, proceeds to validate/PostForm', () => {
    // Simulate the hidden #{formid}hidesubmit button's bound layui submit
    // handler synchronously flipping window['f1validate'] = true on trigger,
    // exactly like layui.form's real submit-filter binding does.
    const jq = function (selector) {
      if (selector === '#f1hidesubmit') {
        return { trigger: function () { windowObj['f1validate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff, windowObj } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});

    const el = appendEl('a', {
      'data-wtm-submit-formid': 'f1',
      'data-wtm-submit-divid': 'divA'
    });
    const rv = ff._submitButtonClick(el);

    expect(postFormSpy).toHaveBeenCalledWith('', 'f1', 'divA');
    expect(rv).toBe(false);
    postFormSpy.mockRestore();
  });

  test('checkfn resolves to a function returning false: PostForm never called, returns false', () => {
    const { ff, windowObj } = loadFreshFf();
    windowObj.myCheckFalse = jest.fn(() => false);
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-submit-formid': 'f2', 'data-wtm-submit-divid': 'divB',
      'data-wtm-submit-checkfn': 'myCheckFalse'
    });

    const rv = ff._submitButtonClick(el);

    expect(windowObj.myCheckFalse).toHaveBeenCalledTimes(1);
    expect(postFormSpy).not.toHaveBeenCalled();
    expect(rv).toBe(false);
    postFormSpy.mockRestore();
  });

  // Regression: check-gate must use LOOSE (`==`) equality against
  // undefined/false, exactly like SubmitButtonTagHelper's legacy generated
  // f_{Id}Click() (`if(check == undefined || check == false){return false;}`
  // — see SubmitButtonTagHelper.cs). A checkFn returning any other
  // JS-falsy-but-not-strictly-false value (0, "", "0", null) must ALSO block
  // submission, since `0 == false`, `"" == false`, `"0" == false`, and
  // `null == undefined` are all true under `==`. Strict (`===`) equality
  // would incorrectly let these fall through the gate.
  //
  // Uses the SAME hidesubmit-flips-validate-to-true jq mock as the
  // 'returning true' case below, so that IF the check-gate were wrongly
  // strict (letting the falsy value fall through), PostForm WOULD be
  // called — the assertion below only passes because the gate itself
  // blocked before ever reaching the validate/PostForm path, not merely
  // because nothing downstream happened to flip validate.
  test.each([
    ['0 (number)', 0],
    ["'' (empty string)", ''],
    ["'0' (string zero)", '0'],
    ['null', null]
  ])('checkfn returns falsy-but-not-strictly-false %s: still blocks submission like the legacy == handshake', (_label, falsyValue) => {
    const jq = function (selector) {
      if (selector === '#f2bhidesubmit') {
        return { trigger: function () { windowObj['f2bvalidate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff, windowObj } = loadFreshFf({ $: jq });
    windowObj.myCheckFalsy = jest.fn(() => falsyValue);
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-submit-formid': 'f2b', 'data-wtm-submit-divid': 'divB2',
      'data-wtm-submit-checkfn': 'myCheckFalsy'
    });

    const rv = ff._submitButtonClick(el);

    expect(windowObj.myCheckFalsy).toHaveBeenCalledTimes(1);
    expect(postFormSpy).not.toHaveBeenCalled();
    expect(rv).toBe(false);
    postFormSpy.mockRestore();
  });

  test('checkfn resolves to a function returning true: proceeds, PostForm called after successful validate trigger', () => {
    const jq = function (selector) {
      if (selector === "#f3hidesubmit") {
        return { trigger: function () { windowObj['f3validate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff, windowObj } = loadFreshFf({ $: jq });
    windowObj.myCheckTrue = jest.fn(() => true);
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-submit-formid': 'f3', 'data-wtm-submit-divid': 'divC',
      'data-wtm-submit-checkfn': 'myCheckTrue'
    });

    ff._submitButtonClick(el);

    expect(windowObj.myCheckTrue).toHaveBeenCalledTimes(1);
    expect(postFormSpy).toHaveBeenCalledWith('', 'f3', 'divC');
    postFormSpy.mockRestore();
  });

  test('checkfn does not resolve (unknown/denylisted): falls back to check=true (mirrors bindSubmit beforeSubmit precedent), proceeds', () => {
    const jq = function (selector) {
      if (selector === '#f4hidesubmit') {
        return { trigger: function () { windowObj['f4validate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff, windowObj } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', {
      'data-wtm-submit-formid': 'f4', 'data-wtm-submit-divid': 'divD',
      'data-wtm-submit-checkfn': 'thisFunctionDoesNotExist470MSubmit'
    });

    expect(() => ff._submitButtonClick(el)).not.toThrow();
    expect(postFormSpy).toHaveBeenCalledWith('', 'f4', 'divD');
    postFormSpy.mockRestore();
  });

  test('validation fails (hidesubmit trigger never flips validate to true): PostForm never called', () => {
    const jq = function (selector) {
      if (selector === '#f5hidesubmit') {
        return { trigger: function () { /* validation failed: no flip */ } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', { 'data-wtm-submit-formid': 'f5', 'data-wtm-submit-divid': 'divE' });

    ff._submitButtonClick(el);

    expect(postFormSpy).not.toHaveBeenCalled();
    postFormSpy.mockRestore();
  });

  test('hidesubmit trigger throws (element missing): catch sets validate=true, PostForm still called — mirrors legacy try/catch fallback exactly', () => {
    const jq = function (selector) {
      if (selector === '#f6hidesubmit') {
        return { trigger: function () { throw new Error('no such element'); } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', { 'data-wtm-submit-formid': 'f6', 'data-wtm-submit-divid': 'divF' });

    expect(() => ff._submitButtonClick(el)).not.toThrow();
    expect(postFormSpy).toHaveBeenCalledWith('', 'f6', 'divF');
    postFormSpy.mockRestore();
  });

  test('missing/malformed element: safe no-op, never throws', () => {
    const { ff } = loadFreshFf();
    expect(() => ff._submitButtonClick(null)).not.toThrow();
    expect(ff._submitButtonClick(null)).toBe(false);
    expect(() => ff._submitButtonClick({})).not.toThrow();
  });

  test("a DOM element argument (legacy 'this' call shape) still resolves directly — no id lookup needed", () => {
    // ff._submitButtonClick still tolerates being handed an element directly
    // (skipping the getElementById lookup) so any caller that already holds
    // one — including `this` in a plain jQuery-bound handler, which is what
    // the CURRENT emitted Click never relies on, but what old cached/rendered
    // pages may still be running until republished — keeps working.
    const jq = function (selector) {
      if (selector === '#f7hidesubmit') {
        return { trigger: function () { windowObj['f7validate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff, windowObj } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', { 'data-wtm-submit-formid': 'f7', 'data-wtm-submit-divid': 'divG' });
    el.addEventListener('click', function () {
      (function () { return ff._submitButtonClick(this); }).call(el);
    });

    click(el);

    expect(postFormSpy).toHaveBeenCalledWith('', 'f7', 'divG');
    postFormSpy.mockRestore();
  });

  // Regression for the #470 Slice M HIGH: the CURRENT emitted Click is
  // "ff._submitButtonClick('{Id}');" — a string id, resolved via
  // document.getElementById. This is what makes the handshake reachable when
  // Click is invoked from INSIDE a plain nested function (the exact shape
  // BaseButtonTag.Process wraps Click in when ConfirmTxt is set:
  // `layer.confirm(txt, opts, function(index){ <Click>; layer.close(index); })`)
  // where `this` is NOT the clicked button (it's window/undefined in
  // non-strict mode, undefined in strict mode) — a plain `this`-based call
  // would hit the `!el || typeof el.getAttribute !== 'function'` guard and
  // silently return false, never reaching validate/hidesubmit/PostForm.
  test('string id resolves via document.getElementById — proves the handshake IS reached from inside a layer.confirm-wrapped nested function(index){...} callback where `this` is not the button', () => {
    const jq = function (selector) {
      if (selector === '#f8hidesubmit') {
        return { trigger: function () { windowObj['f8validate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff, windowObj } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    const el = appendEl('a', {
      id: 'sbP',
      'data-wtm-submit-formid': 'f8',
      'data-wtm-submit-divid': 'divH'
    });

    // Simulate layer.confirm's real invocation shape: a plain (non-arrow)
    // function(index){...} called with no explicit `this` binding — exactly
    // as layer.confirm itself calls its 3rd-argument callback. `this` inside
    // is therefore the global object (sloppy mode) or undefined (strict
    // mode) — never `el` — which is precisely the scenario the fix closes.
    var confirmCallback = function (index) {
      // This is the EXACT emitted Click for a SubmitButtonTagHelper with
      // Id="sbP" under UseSelectIslandRender ON: ff._submitButtonClick('sbP');
      expect(this).not.toBe(el);
      return ff._submitButtonClick('sbP');
    };

    confirmCallback(0);

    expect(postFormSpy).toHaveBeenCalledWith('', 'f8', 'divH');
    postFormSpy.mockRestore();
  });

  test('an element-`this` based call (the OLD, buggy Click shape) would fail the same layer.confirm-wrapped scenario — documents the failure this fix closes', () => {
    // This test intentionally exercises what the PRE-FIX
    // "ff._submitButtonClick(this);" Click would have done inside a
    // layer.confirm nested function(index){...} callback: `this` is not the
    // button, so the element guard silently short-circuits and PostForm is
    // never reached — no error, no validate, no submit.
    const jq = function (selector) {
      if (selector === '#f9hidesubmit') {
        return { trigger: function () { windowObj['f9validate'] = true; } };
      }
      return { 0: null, length: 0 };
    };
    Object.assign(jq, makeJQueryMock());
    const { ff } = loadFreshFf({ $: jq });
    const postFormSpy = jest.spyOn(ff, 'PostForm').mockImplementation(() => {});
    appendEl('a', { id: 'sbQ', 'data-wtm-submit-formid': 'f9', 'data-wtm-submit-divid': 'divI' });

    var oldStyleConfirmCallback = function (index) {
      // The OLD (buggy) Click body: "ff._submitButtonClick(this);"
      return ff._submitButtonClick(this);
    };

    oldStyleConfirmCallback(0);

    expect(postFormSpy).not.toHaveBeenCalled();
    postFormSpy.mockRestore();
  });
});
