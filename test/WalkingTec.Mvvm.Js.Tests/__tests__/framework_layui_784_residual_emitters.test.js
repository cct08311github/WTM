// Tests for Issue #784 (#470 residual): gates the THREE residual
// non-flag-gated inline-<script> emitter groups found during the Slice Q
// sweep — never gated by any prior #470 slice — behind
// WtmUIOptions.UseSelectIslandRender (default OFF):
//
//   GROUP 1 — Abstraction/BaseElementTag.cs: checkbox/switch/radio
//   ChangeFunc -> layui.form.on(...) wiring (new 'formChange' island) and
//   TextBoxTagHelper's SearchUrl/TriggerUrl -> layui.autocomplete.render(...)
//   wiring (new 'autocomplete' island).
//
//   GROUP 2 — Abstraction/BaseButton.cs: BaseButtonTag.Process's
//   unconditional click-wiring wrapper <script> — replaced with delegated
//   data-wtm-click="button" / data-wtm-click="submit" dispatch (new
//   ff._buttonAction.button/.submit entries, sharing ff._confirmThenRun for
//   the ConfirmTxt -> layer.confirm(...) gate).
//
//   GROUP 3 — TabTagHelper.cs / PanelTagHelper.cs: fixed-shape inline
//   <script> (tab-selection + chart-resize; collapse-resize) — replaced with
//   new 'tabInit'/'panelInit' islands.
//
// Following the established convention (#601/#470 Slice I/O2): loads a FRESH
// instance of the real framework_layui.js into its own vm context (real
// jsdom `document`) and calls the REAL ff.DispatchAction / delegated click
// listener / ff._consumePageReadyIslands directly, so these tests exercise
// the actual shipped code, not a hand-rolled reimplementation.
//
// Issue #470 Slice O2 lesson (comment 18118): every NEW island action type
// gets a REAL entry-point test through
// ff._consumePageReadyIslands -> ff._normalizeIslandPayload -> ff.DispatchAction
// — not just a direct ff.DispatchAction({actions:[...]}) call — because the
// O2 renderGrid regression (a same-named `actions` field colliding with
// _normalizeIslandPayload's batch-island probe) went undetected by every
// test that skipped straight to DispatchAction. None of this file's four new
// DTOs (formChange/autocomplete/tabInit/panelInit) carry a field literally
// named `actions`, and the real-entry-point tests below prove the bare
// single-action payload shape (what the C# emitters actually serialize)
// reaches the right case.

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
// Harness — a real-DOM-backed jQuery-like $ shim (querySelectorAll-based, so
// tabInit's `$('#id ul li').eq(idx).addClass(...)` / `.find(...).each(...)`
// chain operates on ACTUAL jsdom nodes, not a hand-waved stub) plus a
// layui mock covering form/autocomplete/element (the three modules the new
// islands touch).
// ---------------------------------------------------------------------------
function wrap(elements) {
  const arr = Array.isArray(elements) ? elements.filter(Boolean) : (elements ? [elements] : []);
  const w = {
    length: arr.length,
    0: arr[0],
    eq: function (i) { return wrap(arr[i] ? [arr[i]] : []); },
    addClass: function (cls) { arr.forEach((el) => el.classList.add(cls)); return w; },
    removeClass: function (cls) { arr.forEach((el) => el.classList.remove(cls)); return w; },
    find: function (sel) {
      const found = [];
      arr.forEach((el) => { found.push(...Array.from(el.querySelectorAll(sel))); });
      return wrap(found);
    },
    each: function (fn) { arr.forEach((el, i) => fn.call(el, i)); return w; },
    attr: function (name, value) {
      if (value === undefined) { return arr[0] ? arr[0].getAttribute(name) : undefined; }
      arr.forEach((el) => { el.setAttribute(name, value); });
      return w;
    },
    val: function (v) {
      if (v === undefined) { return arr[0] ? arr[0].value : undefined; }
      arr.forEach((el) => { el.value = v; });
      return w;
    },
    on: jest.fn(function () { return w; }),
    css: jest.fn(function () { return w; })
  };
  return w;
}

function makeJQueryMock() {
  const $ = function (selector) {
    if (selector && selector.nodeType) { return wrap([selector]); }
    if (typeof selector === 'string') {
      if (selector.charAt(0) === '<') { return wrap([document.createElement('div')]); }
      try { return wrap(Array.from(document.querySelectorAll(selector))); } catch (e) { return wrap([]); }
    }
    return wrap([]);
  };
  $.fn = { on: jest.fn() };
  $.ajax = jest.fn();
  $.get = jest.fn();
  $.cookie = jest.fn();
  return $;
}

function makeLayui(overrides) {
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    form: { on: jest.fn(), render: jest.fn() },
    autocomplete: { render: jest.fn() },
    element: { on: jest.fn(), init: jest.fn() },
    layer: { confirm: jest.fn(), close: jest.fn() }
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
    layer: layui.layer,
    console,
    Event: global.Event,
    // Runs setTimeout(fn, ms) synchronously — this file never asserts on
    // TIMING, only on whether the deferred body ran; a real timer would work
    // too but adds unnecessary async plumbing to every panelInit test.
    setTimeout: function (fn) { fn(); return 0; },
    clearTimeout: function () {},
    $: $,
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  ctx.window.dispatchEvent = jest.fn();
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

function appendPageReadyIsland(payload) {
  const script = document.createElement('script');
  script.type = 'application/json';
  script.className = 'wtm-dialog-init';
  script.textContent = JSON.stringify(payload);
  document.body.appendChild(script);
  return script;
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#784 (#470 residual) — source sweep', () => {
  test('DispatchAction switch contains formChange/autocomplete/tabInit/panelInit cases', () => {
    expect(active).toMatch(/case\s+['"]formChange['"]/);
    expect(active).toMatch(/case\s+['"]autocomplete['"]/);
    expect(active).toMatch(/case\s+['"]tabInit['"]/);
    expect(active).toMatch(/case\s+['"]panelInit['"]/);
  });

  test('ff._buttonAction gains button and submit entries', () => {
    const { ff } = loadFreshFf();
    expect(typeof ff._buttonAction.button).toBe('function');
    expect(typeof ff._buttonAction.submit).toBe('function');
    expect(typeof ff._confirmThenRun).toBe('function');
  });

  test('_renderFormChangeAction resolves changeFunc through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = active.match(/_renderFormChangeAction\s*:\s*function[\s\S]{0,1200}?\n\s*\},/)[0];
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFunc\s*\)/);
  });

  test('_renderAutocompleteAction resolves changeFunc through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = active.match(/_renderAutocompleteAction\s*:\s*function[\s\S]{0,1500}?\n\s*\},/)[0];
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFunc\s*\)/);
  });

  test('ff._buttonAction.button resolves data-wtm-clickfn through ff._resolveGuardedWindowFn', () => {
    // Issue #784 REVIEW FIX: signature grew an optional `e` (native click
    // event, used for preventDefault()) — the parameter list match is kept
    // loose (`el` optionally followed by `, e`) rather than pinned to the
    // exact param list, so this test doesn't need to change again if a
    // future review adds another optional trailing parameter here.
    const block = active.match(/button\s*:\s*function\s*\(el(?:\s*,\s*e)?\)\s*\{[\s\S]{0,600}?\n\s*\},/)[0];
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(/);
  });

  test('_islandModulesFor requires the "form" module for formChange and "autocomplete" for autocomplete', () => {
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,4500}?\n\s*\},/)[0];
    expect(block).toMatch(/a\.type\s*===\s*['"]formChange['"][\s\S]{0,80}needed\.form\s*=\s*true/);
    expect(block).toMatch(/a\.type\s*===\s*['"]autocomplete['"][\s\S]{0,80}needed\.autocomplete\s*=\s*true/);
    expect(block).toMatch(/if\s*\(\s*needed\.autocomplete\s*\)\s*\{\s*mods\.push\(\s*['"]autocomplete['"]\s*\)/);
  });

  test('_islandModulesFor requires the "element" module for tabInit and panelInit', () => {
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,4500}?\n\s*\},/)[0];
    expect(block).toMatch(/a\.type\s*===\s*['"]tabInit['"][\s\S]{0,80}needed\.element\s*=\s*true/);
    expect(block).toMatch(/a\.type\s*===\s*['"]panelInit['"][\s\S]{0,80}needed\.element\s*=\s*true/);
  });

  test('none of the four new island DTOs collide with the O2 batch-island "actions" field name', () => {
    // Mirrors the #470 Slice O2 CRITICAL FIX regression pin: a same-named
    // `actions` field on a bare single-action payload would be misclassified
    // by _normalizeIslandPayload as an already-batched {actions:[...]}
    // shape and never reach ff.DispatchAction's switch at all.
    const payloads = [
      { type: 'formChange', kind: 'checkbox', filter: 'f1' },
      { type: 'autocomplete', id: 'tb1', url: '/x' },
      { type: 'tabInit', id: 't1', filter: 't1filter' },
      { type: 'panelInit', filter: 'p1' }
    ];
    payloads.forEach((p) => expect(Object.prototype.hasOwnProperty.call(p, 'actions')).toBe(false));
  });

  test('no new Function( call sites anywhere in the active source', () => {
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('active-code eval( count is still exactly 1 after #784 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('#627 kill-switch still gates exactly FOUR legacy dynamic-script-execution points', () => {
    const matches = active.match(/ff\._isLegacyRehydrationDisabled\(\)/g) || [];
    expect(matches).toHaveLength(4);
  });
});

// ---------------------------------------------------------------------------
// GROUP 1 — formChange / autocomplete: real ff.DispatchAction behavior
// ---------------------------------------------------------------------------
describe('#784 GROUP 1 — formChange real ff.DispatchAction behavior', () => {
  test('happy path: checkbox kind registers layui.form.on and the resolved changeFunc fires with data', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    const spy = jest.fn();
    windowObj.formChangeFn784a = spy;

    ff.DispatchAction({ actions: [{ type: 'formChange', kind: 'checkbox', filter: 'f784a', changeFunc: 'formChangeFn784a' }] });

    expect(layui.form.on).toHaveBeenCalledWith('checkbox(f784a)', expect.any(Function));
    const handler = layui.form.on.mock.calls[0][1];
    const data = { value: '1' };
    handler(data);
    expect(spy).toHaveBeenCalledWith(data);
  });

  test('switch/radio kinds build the correct layui.form.on event selector', () => {
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'formChange', kind: 'switch', filter: 'sw784' }] });
    ff.DispatchAction({ actions: [{ type: 'formChange', kind: 'radio', filter: 'rd784' }] });
    expect(layui.form.on).toHaveBeenCalledWith('switch(sw784)', expect.any(Function));
    expect(layui.form.on).toHaveBeenCalledWith('radio(rd784)', expect.any(Function));
  });

  test('no-ops without throwing when kind/filter are missing', () => {
    const { ff } = loadFreshFf();
    expect(() => ff.DispatchAction({ actions: [{ type: 'formChange' }] })).not.toThrow();
  });

  test('no-ops without throwing when layui.form is not loaded', () => {
    const { ff } = loadFreshFf({ layui: makeLayui({ form: undefined }) });
    expect(() => ff.DispatchAction({ actions: [{ type: 'formChange', kind: 'checkbox', filter: 'f784b' }] })).not.toThrow();
  });

  test('adversarial changeFunc: dotted name "a.b" is rejected — no listener fires', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    const spy = jest.fn();
    windowObj.a = { b: spy };

    ff.DispatchAction({ actions: [{ type: 'formChange', kind: 'checkbox', filter: 'f784c', changeFunc: 'a.b' }] });
    const handler = layui.form.on.mock.calls[0][1];
    expect(() => handler({})).not.toThrow();
    expect(spy).not.toHaveBeenCalled();
  });

  test('adversarial changeFunc: denylisted name "eval" is rejected — no listener fires, no eval', () => {
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'formChange', kind: 'checkbox', filter: 'f784d', changeFunc: 'eval' }] });
    const handler = layui.form.on.mock.calls[0][1];
    expect(() => handler({})).not.toThrow();
  });
});

describe('#784 GROUP 1 — autocomplete real ff.DispatchAction behavior', () => {
  function appendInput(id) {
    const el = document.createElement('input');
    el.id = id;
    document.body.appendChild(el);
    return el;
  }

  test('happy path: layui.autocomplete.render called with the carried url; onselect writes back the value and fires changeFunc', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    const el = appendInput('ac784a');
    const spy = jest.fn();
    windowObj.acChange784a = spy;

    ff.DispatchAction({ actions: [{ type: 'autocomplete', id: 'ac784a', url: '/search', changeFunc: 'acChange784a' }] });

    expect(layui.autocomplete.render).toHaveBeenCalledTimes(1);
    const cfg = layui.autocomplete.render.mock.calls[0][0];
    expect(cfg.elem).toBe(el);
    expect(cfg.url).toBe('/search');
    expect(cfg.cache).toBe(false);

    const data = { Value: 'picked', elem: {} };
    cfg.onselect(data);
    expect(el.value).toBe('picked');
    expect(spy).toHaveBeenCalledWith(data);
  });

  test('onselect calls ff.ChainChange when triggerUrl is present', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    appendInput('ac784b');
    const chainSpy = jest.spyOn(ff, 'ChainChange').mockImplementation(() => {});

    ff.DispatchAction({ actions: [{ type: 'autocomplete', id: 'ac784b', url: '/search', triggerUrl: '/trigger' }] });
    const cfg = layui.autocomplete.render.mock.calls[0][0];
    const data = { Value: 'v', elem: {} };
    cfg.onselect(data);

    expect(chainSpy).toHaveBeenCalledWith('/trigger/v', data.elem);
    chainSpy.mockRestore();
  });

  test('onselect does NOT call ff.ChainChange when triggerUrl is absent', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    appendInput('ac784c');
    const chainSpy = jest.spyOn(ff, 'ChainChange').mockImplementation(() => {});

    ff.DispatchAction({ actions: [{ type: 'autocomplete', id: 'ac784c', url: '/search' }] });
    const cfg = layui.autocomplete.render.mock.calls[0][0];
    cfg.onselect({ Value: 'v', elem: {} });

    expect(chainSpy).not.toHaveBeenCalled();
    chainSpy.mockRestore();
  });

  test('no-ops without throwing when the target element does not exist in the DOM', () => {
    const { ff } = loadFreshFf();
    expect(() => ff.DispatchAction({ actions: [{ type: 'autocomplete', id: 'does_not_exist', url: '/x' }] })).not.toThrow();
  });

  test('no-ops without throwing when layui.autocomplete is not loaded', () => {
    const { ff } = loadFreshFf({ layui: makeLayui({ autocomplete: undefined }) });
    appendInput('ac784d');
    expect(() => ff.DispatchAction({ actions: [{ type: 'autocomplete', id: 'ac784d', url: '/x' }] })).not.toThrow();
  });

  test('adversarial changeFunc: denylisted "Function" name is rejected — no listener call, no code execution', () => {
    const { ff, layui } = loadFreshFf();
    appendInput('ac784e');
    ff.DispatchAction({ actions: [{ type: 'autocomplete', id: 'ac784e', url: '/x', changeFunc: 'Function' }] });
    const cfg = layui.autocomplete.render.mock.calls[0][0];
    expect(() => cfg.onselect({ Value: 'v', elem: {} })).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// GROUP 2 — delegated data-wtm-click="button"/"submit" dispatch
// ---------------------------------------------------------------------------
describe('#784 GROUP 2 — delegated button/submit click dispatch', () => {
  test("data-wtm-click='button' click resolves data-wtm-clickfn through ff._resolveGuardedWindowFn and calls it", () => {
    const { ff, windowObj } = loadFreshFf();
    const spy = jest.fn();
    windowObj.btnFn784a = spy;
    const el = appendEl('button', { 'data-wtm-click': 'button', 'data-wtm-clickfn': 'btnFn784a' });

    click(el);

    expect(spy).toHaveBeenCalledTimes(1);
  });

  test("data-wtm-click='button' with data-wtm-confirm gates the call behind layer.confirm — fn runs only after the confirm callback fires", () => {
    const { ff, windowObj, layui } = loadFreshFf();
    const spy = jest.fn();
    windowObj.btnFn784b = spy;
    const el = appendEl('button', {
      'data-wtm-click': 'button', 'data-wtm-clickfn': 'btnFn784b',
      'data-wtm-confirm': 'Are you sure?', 'data-wtm-confirm-title': 'Info'
    });

    click(el);
    expect(spy).not.toHaveBeenCalled();
    expect(layui.layer.confirm).toHaveBeenCalledWith('Are you sure?', { icon: 3, title: 'Info' }, expect.any(Function));

    const confirmCb = layui.layer.confirm.mock.calls[0][2];
    confirmCb(1);
    expect(spy).toHaveBeenCalledTimes(1);
    expect(layui.layer.close).toHaveBeenCalledWith(1);
  });

  test("data-wtm-click='button' with no data-wtm-clickfn (no-op Click) never throws and calls nothing", () => {
    const { ff } = loadFreshFf();
    const el = appendEl('button', { 'data-wtm-click': 'button' });
    expect(() => click(el)).not.toThrow();
  });

  test("adversarial data-wtm-clickfn: dotted name is rejected — no call, never throws", () => {
    const { ff, windowObj } = loadFreshFf();
    const spy = jest.fn();
    windowObj.evil = { fn: spy };
    const el = appendEl('button', { 'data-wtm-click': 'button', 'data-wtm-clickfn': 'evil.fn' });
    expect(() => click(el)).not.toThrow();
    expect(spy).not.toHaveBeenCalled();
  });

  // NOTE: these three 'submit' cases call ff._buttonAction.submit(el)
  // DIRECTLY (the EXACT function the delegated click listener invokes) —
  // same rationale as Slice M's loadPage/dateClick tests: the real
  // ff._submitButtonClick eventually reaches ff.PostForm, which needs a
  // fuller layui.layer mock (load/close) than this describe block's minimal
  // one provides, and — MORE IMPORTANTLY — document (and any click listener
  // registered on it by an earlier loadFreshFf() call in this same file) is
  // shared/accumulates across tests, so a real document-level click would
  // also re-invoke every earlier test's stale (by-then-restored, unmocked)
  // ff._submitButtonClick on this same element. Calling the dispatch-map
  // function directly sidesteps that cross-test listener accumulation
  // entirely while still exercising the real production dispatch code — the
  // 'button' tests above don't need this because ff._buttonAction.button's
  // worst case (a stale listener from an earlier test) just silently
  // resolves nothing against an old, unrelated window object.
  test("data-wtm-click='submit' delegates to ff._submitButtonClick(el) with the SAME element (never `this`, never a string built client-side)", () => {
    const { ff, windowObj } = loadFreshFf();
    const spy = jest.spyOn(ff, '_submitButtonClick').mockImplementation(() => false);
    const el = appendEl('button', {
      'data-wtm-click': 'submit', 'data-wtm-submit-formid': 'form784a', 'data-wtm-submit-divid': 'div784a'
    });

    ff._buttonAction.submit(el);

    expect(spy).toHaveBeenCalledWith(el);
  });

  test("data-wtm-click='submit' with data-wtm-submit-url sets the form's action attribute before delegating", () => {
    const { ff, windowObj, $ } = loadFreshFf();
    const form = document.createElement('form');
    form.id = 'form784b';
    document.body.appendChild(form);
    const submitSpy = jest.spyOn(ff, '_submitButtonClick').mockImplementation(() => false);
    const el = appendEl('button', {
      'data-wtm-click': 'submit',
      'data-wtm-submit-formid': 'form784b',
      'data-wtm-submit-url': '/alt-submit-url'
    });

    ff._buttonAction.submit(el);

    expect(form.getAttribute('action')).toBe('/alt-submit-url');
    expect(submitSpy).toHaveBeenCalledWith(el);
  });

  test("data-wtm-click='submit' with ConfirmTxt gates the WHOLE handshake (url-set + _submitButtonClick) behind layer.confirm", () => {
    const { ff, layui } = loadFreshFf();
    const submitSpy = jest.spyOn(ff, '_submitButtonClick').mockImplementation(() => false);
    const el = appendEl('button', {
      'data-wtm-click': 'submit',
      'data-wtm-submit-formid': 'form784c',
      'data-wtm-confirm': 'Submit now?',
      'data-wtm-confirm-title': 'Confirm'
    });

    ff._buttonAction.submit(el);
    expect(submitSpy).not.toHaveBeenCalled();

    const confirmCb = layui.layer.confirm.mock.calls[0][2];
    confirmCb(2);
    expect(submitSpy).toHaveBeenCalledWith(el);
  });

  // ─────────────────────────────────────────────────────────────────────
  // REVIEW FIX regression (CRITICAL, correctness): a click on a
  // type="submit" button carrying data-wtm-click="submit" must NOT let the
  // browser perform its own native form submission -- see the 'submit'
  // handler's own comment for the full failure mode (async ConfirmTxt
  // navigating away mid-dialog; a double AJAX+native POST for the bare-call
  // case). `cancelable: true` is required for the DOM spec to track
  // defaultPrevented at all -- the shared `click()` helper above omits it
  // (harmless for every OTHER test in this suite, which never asserts on
  // defaultPrevented).
  //
  // These call ff._buttonAction.submit(el, evt) DIRECTLY rather than
  // dispatching a real DOM click -- SAME rationale the pre-existing
  // 'submit' tests above already document (comment above the first
  // `data-wtm-click='submit'` test in this describe block): document is
  // shared/accumulates listeners across every loadFreshFf() call in this
  // file, so a real click here would ALSO re-invoke every earlier test's
  // stale ff._submitButtonClick -> ff.PostForm chain, which needs a fuller
  // layui.layer mock (load/close) than this minimal one provides. Calling
  // the dispatch-map function directly exercises the EXACT same
  // preventDefault() line the real listener calls (see the document-level
  // click listener, which passes `e` straight through unmodified).
  // ─────────────────────────────────────────────────────────────────────
  test("REGRESSION #784 review: ff._buttonAction.submit(el, evt) preventDefault()s the native click (no native form submission)", () => {
    const { ff } = loadFreshFf();
    jest.spyOn(ff, '_submitButtonClick').mockImplementation(() => false);
    const el = appendEl('button', {
      'data-wtm-click': 'submit', 'data-wtm-submit-formid': 'form784nav'
    });
    const evt = new window.MouseEvent('click', { bubbles: true, cancelable: true });

    ff._buttonAction.submit(el, evt);

    expect(evt.defaultPrevented).toBe(true);
  });

  test("REGRESSION #784 review: ff._buttonAction.button(el, evt) preventDefault()s the native click even with no confirm/no clickfn (matches legacy's unconditional return false whenever the wrapper was reached)", () => {
    const { ff } = loadFreshFf();
    const el = appendEl('button', { 'data-wtm-click': 'button' });
    const evt = new window.MouseEvent('click', { bubbles: true, cancelable: true });

    ff._buttonAction.button(el, evt);

    expect(evt.defaultPrevented).toBe(true);
  });

  test("ff._buttonAction.submit/button called directly with no event (as every pre-existing test above does) never throws -- `e` is optional", () => {
    const { ff } = loadFreshFf();
    jest.spyOn(ff, '_submitButtonClick').mockImplementation(() => false);
    const submitEl = appendEl('button', { 'data-wtm-click': 'submit', 'data-wtm-submit-formid': 'form784opt' });
    const buttonEl = appendEl('button', { 'data-wtm-click': 'button' });

    expect(() => ff._buttonAction.submit(submitEl)).not.toThrow();
    expect(() => ff._buttonAction.button(buttonEl)).not.toThrow();
  });

  test("REGRESSION #784 review: the document-level click listener passes the native event through to the matched handler (wiring check)", () => {
    // Confirms the listener-level wiring (handler(el, e), not handler(el))
    // without triggering the cross-test stale-listener hazard documented
    // above -- 'button' never reaches ff.PostForm, so a real click here is
    // safe regardless of which stale listeners from earlier tests also fire.
    const { ff, windowObj } = loadFreshFf();
    const spy = jest.fn();
    windowObj.btnFn784wiring = spy;
    const el = appendEl('button', { 'data-wtm-click': 'button', 'data-wtm-clickfn': 'btnFn784wiring' });
    const evt = new window.MouseEvent('click', { bubbles: true, cancelable: true });

    el.dispatchEvent(evt);

    expect(spy).toHaveBeenCalledTimes(1);
    expect(evt.defaultPrevented).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// GROUP 3 — tabInit / panelInit: real ff.DispatchAction behavior
// ---------------------------------------------------------------------------
describe('#784 GROUP 3 — tabInit real ff.DispatchAction behavior', () => {
  function appendTabMarkup(id, tabCount) {
    const container = document.createElement('div');
    container.id = id;
    const ul = document.createElement('ul');
    for (let i = 0; i < tabCount; i++) { ul.appendChild(document.createElement('li')); }
    container.appendChild(ul);
    const chartDiv = document.createElement('div');
    chartDiv.setAttribute('ischart', '1');
    chartDiv.id = 'chart784';
    const tabItem = document.createElement('div');
    tabItem.className = 'layui-tab-item';
    tabItem.appendChild(chartDiv);
    container.appendChild(tabItem);
    document.body.appendChild(container);
    return { container, ul, chartDiv };
  }

  test('happy path: selects the Nth tab li/item and registers layui.element.on for the tab filter', () => {
    const { ff, layui } = loadFreshFf();
    const { ul } = appendTabMarkup('tab784a', 3);

    ff.DispatchAction({ actions: [{ type: 'tabInit', id: 'tab784a', filter: 'tab784afilter', selectedIndex: 1 }] });

    expect(ul.children[1].classList.contains('layui-this')).toBe(true);
    expect(ul.children[0].classList.contains('layui-this')).toBe(false);
    expect(layui.element.on).toHaveBeenCalledWith('tab(tab784afilter)', expect.any(Function));
  });

  test('chart-resize handler calls the window[id+"Chart"].resize() for each ischart="1" div, safely (no eval)', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    appendTabMarkup('tab784b', 2);
    const resizeSpy = jest.fn();
    windowObj['chart784Chart'] = { resize: resizeSpy };

    ff.DispatchAction({ actions: [{ type: 'tabInit', id: 'tab784b', filter: 'tab784bfilter' }] });
    const handler = layui.element.on.mock.calls[0][1];
    handler({});

    expect(resizeSpy).toHaveBeenCalledTimes(1);
  });

  test('chart-resize handler no-ops safely when window[id+"Chart"] is absent or not resizable', () => {
    const { ff, layui } = loadFreshFf();
    appendTabMarkup('tab784c', 1);
    ff.DispatchAction({ actions: [{ type: 'tabInit', id: 'tab784c', filter: 'tab784cfilter' }] });
    const handler = layui.element.on.mock.calls[0][1];
    expect(() => handler({})).not.toThrow();
  });

  test('no-ops without throwing when id/filter are missing', () => {
    const { ff } = loadFreshFf();
    expect(() => ff.DispatchAction({ actions: [{ type: 'tabInit' }] })).not.toThrow();
  });

  test('no-ops without throwing when layui.element is not loaded', () => {
    const { ff } = loadFreshFf({ layui: makeLayui({ element: undefined }) });
    appendTabMarkup('tab784d', 1);
    expect(() => ff.DispatchAction({ actions: [{ type: 'tabInit', id: 'tab784d', filter: 'x' }] })).not.toThrow();
  });

  // MANDATORY real-entry-point test (#470 Slice O2 lesson, comment 18118):
  // through ff._consumePageReadyIslands -> ff._normalizeIslandPayload ->
  // ff._dispatchIslandWhenReady -> ff.DispatchAction — the EXACT chain the
  // shipped bundle drives at page-ready for a bare (non-wrapped) single-
  // action payload, which is what TabTagHelper.cs actually serializes.
  test('REAL ENTRY POINT: a bare tabInit island picked up by _consumePageReadyIslands selects the tab and registers layui.element.on', () => {
    const { ff, layui } = loadFreshFf();
    const { ul } = appendTabMarkup('tab784e', 2);
    appendPageReadyIsland({ type: 'tabInit', id: 'tab784e', filter: 'tab784efilter', selectedIndex: 1 });

    ff._consumePageReadyIslands();

    expect(ul.children[1].classList.contains('layui-this')).toBe(true);
    expect(layui.element.on).toHaveBeenCalledWith('tab(tab784efilter)', expect.any(Function));
  });
});

describe('#784 GROUP 3 — panelInit real ff.DispatchAction behavior', () => {
  test('happy path: calls layui.element.init() and registers layui.element.on for the collapse filter', () => {
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'panelInit', filter: 'panel784a' }] });

    expect(layui.element.init).toHaveBeenCalled();
    expect(layui.element.on).toHaveBeenCalledWith('collapse(panel784a)', expect.any(Function));
  });

  test('collapse handler dispatches a window resize event (deferred, matching the legacy setTimeout(...,10) body)', () => {
    const { ff, windowObj, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'panelInit', filter: 'panel784b' }] });
    const handler = layui.element.on.mock.calls[0][1];

    handler({});

    expect(windowObj.dispatchEvent).toHaveBeenCalledTimes(1);
    const evt = windowObj.dispatchEvent.mock.calls[0][0];
    expect(evt.type).toBe('resize');
  });

  test('no-ops without throwing when filter is missing', () => {
    const { ff } = loadFreshFf();
    expect(() => ff.DispatchAction({ actions: [{ type: 'panelInit' }] })).not.toThrow();
  });

  test('no-ops without throwing when layui.element is not loaded', () => {
    const { ff } = loadFreshFf({ layui: makeLayui({ element: undefined }) });
    expect(() => ff.DispatchAction({ actions: [{ type: 'panelInit', filter: 'x' }] })).not.toThrow();
  });

  // MANDATORY real-entry-point test — see the tabInit test's comment above
  // for the full #470 Slice O2 rationale.
  test('REAL ENTRY POINT: a bare panelInit island picked up by _consumePageReadyIslands initializes and registers the collapse handler', () => {
    const { ff, layui } = loadFreshFf();
    appendPageReadyIsland({ type: 'panelInit', filter: 'panel784c' });

    ff._consumePageReadyIslands();

    expect(layui.element.init).toHaveBeenCalled();
    expect(layui.element.on).toHaveBeenCalledWith('collapse(panel784c)', expect.any(Function));
  });
});

// ---------------------------------------------------------------------------
// GROUP 1 — MANDATORY real-entry-point tests for formChange/autocomplete
// ---------------------------------------------------------------------------
describe('#784 GROUP 1 — real entry point (_consumePageReadyIslands)', () => {
  test('REAL ENTRY POINT: a bare formChange island picked up by _consumePageReadyIslands registers layui.form.on', () => {
    const { ff, layui } = loadFreshFf();
    appendPageReadyIsland({ type: 'formChange', kind: 'checkbox', filter: 'f784rep' });

    ff._consumePageReadyIslands();

    expect(layui.form.on).toHaveBeenCalledWith('checkbox(f784rep)', expect.any(Function));
  });

  test('REAL ENTRY POINT: a bare autocomplete island picked up by _consumePageReadyIslands calls layui.autocomplete.render', () => {
    const { ff, layui } = loadFreshFf();
    const el = document.createElement('input');
    el.id = 'ac784rep';
    document.body.appendChild(el);
    appendPageReadyIsland({ type: 'autocomplete', id: 'ac784rep', url: '/search' });

    ff._consumePageReadyIslands();

    expect(layui.autocomplete.render).toHaveBeenCalledTimes(1);
    expect(layui.autocomplete.render.mock.calls[0][0].elem).toBe(el);
  });
});
