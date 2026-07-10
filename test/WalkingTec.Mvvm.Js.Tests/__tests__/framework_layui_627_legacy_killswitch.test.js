// Tests for Issue #627: opt-in kill-switch for FOUR legacy
// dynamic-script-execution points in framework_layui.js:
//   1. ff._legacyScriptEval (the deprecated IsScript response-header eval()
//      fallback).
//   2. ff._replayInitFromHtml's initScripts re-injection loop (SPA-fragment /
//      PostForm-redraw path, #587).
//   3. ff.OpenDialog's layer.open success callback's _initScripts
//      re-injection loop (#522).
//   4. ff.OpenDialog2's $$script$$/$$#script$$ selector search-panel
//      template rehydration (#332) — added by the #627 review follow-up;
//      the original #627 commit only gated points 1-3.
//
// The switch is opt-in via EITHER ff.DisableLegacyScriptRehydration === true
// (strict boolean — truthy strings do not count) OR a
// <meta name="wtm-disable-legacy-script-rehydration" content="true"> element
// in the document (exact string 'true'). Both default to absent, so the
// default behaviour is completely unchanged (zero-behaviour-change red line
// — see CLAUDE.md). Script/island EXTRACTION is never touched by this
// change — only the EXECUTION of previously-collected legacy inline
// scripts is gated; island payload dispatch always runs.
//
// Following the same convention as
// framework_layui_576_opendialog_island_defer.test.js: most coverage here
// exercises the REAL framework_layui.js source loaded fresh into a vm
// context per test, with `document` forwarded from this file's own jsdom
// environment (so DOMParser extraction, script-element injection, and
// document.querySelector('meta[...]') all interact with a genuine DOM) and
// a fully-controllable `layui` mock.

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
describe('#627 legacy-rehydration kill-switch — source sweep', () => {
  test('ff._isLegacyRehydrationDisabled is defined', () => {
    expect(active).toMatch(/_isLegacyRehydrationDisabled\s*:\s*function/);
  });

  test('_legacyScriptEval checks the kill-switch before eval(', () => {
    const block = active.match(/_legacyScriptEval\s*:\s*function[\s\S]{0,900}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/_isLegacyRehydrationDisabled\(\)/);
    const guardIdx = block[0].indexOf('_isLegacyRehydrationDisabled()');
    const evalIdx = block[0].indexOf('eval(code)');
    expect(guardIdx).toBeGreaterThan(-1);
    expect(evalIdx).toBeGreaterThan(-1);
    expect(guardIdx).toBeLessThan(evalIdx);
  });

  test('active-code eval( count is still exactly 1 after the #627 change', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('raw eval( token count in the file is unchanged from the pre-#627 baseline (4: 1 real call + 3 comment mentions)', () => {
    const matches = src.match(/eval\(/g) || [];
    expect(matches).toHaveLength(4);
  });

  test('OpenDialog2 body calls ff._isLegacyRehydrationDisabled (4th gated point, #627 review follow-up — mutation guard)', () => {
    // Bounded window from OpenDialog2's declaration up to the next top-level
    // method (CloseDialog) — mirrors the _legacyScriptEval block match above.
    const block = active.match(/OpenDialog2\s*:\s*function[\s\S]*?\n\s*CloseDialog\s*:\s*function/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/_isLegacyRehydrationDisabled\(\)/);
  });
});

// ---------------------------------------------------------------------------
// Behavioral harness — loads a fresh instance of the real source per test,
// isolated from the real Node global (window self-references the vm
// context), but with `document` forwarded so DOM-touching code (DOMParser
// extraction, script-element injection, meta-tag lookup) works against a
// genuine DOM. Mirrors framework_layui_576_opendialog_island_defer.test.js's
// loadFreshFfWithLayui exactly.
// ---------------------------------------------------------------------------

// `jqueryFactory` and `domPurifyImpl` are optional overrides used by the
// OpenDialog2 gating tests below, which need $(selector) to resolve against
// the real DOM (OpenDialog2 reads $(tempId)[0].innerHTML) and ff.SafeHtml to
// not fail-closed to '' (OpenDialog2 asserts on the rendered `content`
// string). Every other call site in this file omits both — behaviour is
// unchanged from before this was added (DOMPurify stays undefined, same as
// an absent key in the context object literal; the default jQuery mock is
// the same fixed-shape stub as before).
function loadFreshFf(layui, ajaxImpl, domPurifyImpl, jqueryFactory) {
  const jqueryMock = Object.assign(
    jqueryFactory || function () {
      return {
        cookie: jest.fn(),
        text: function (s) { this._text = s; return this; },
        html: function () { return this._text; }
      };
    },
    { ajax: ajaxImpl || jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui: layui || { use: jest.fn(), layer: { alert: jest.fn(), msg: jest.fn() } },
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jqueryMock,
    // Extraction (DOMParser) and meta-tag lookup (document.querySelector) both
    // operate on the genuine jsdom DOM of this test file.
    DOMParser: global.DOMParser,
    DOMPurify: domPurifyImpl,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, ctx: ctx };
}

// A simple layui mock whose layer.open invokes its `success` callback
// synchronously (same convention as #576's makeLateLayuiWithDialog) and
// whose `.use` resolves immediately — sufficient here because the islands
// used below ('alert') have no layui module dependency, so layui.use is
// never actually invoked by ff._dispatchIslandWhenReady's real code path.
function makeDialogLayui() {
  const layer = {
    load: jest.fn(() => 1),
    close: jest.fn(),
    alert: jest.fn(),
    full: jest.fn(),
    open: jest.fn((opts) => {
      if (typeof opts.success === 'function') { opts.success(); }
      return 'mockWinId';
    })
  };
  return { layer: layer, use: jest.fn((mods, cb) => cb()) };
}

function makeAjaxSuccess(responseHtml, headers) {
  return jest.fn((opts) => {
    const request = {
      getResponseHeader: (name) =>
        Object.prototype.hasOwnProperty.call(headers || {}, name) ? headers[name] : null
    };
    opts.success(responseHtml, 'success', request);
  });
}

function dialogHtmlWithScriptAndAlertIsland(scriptBody, alertMessage) {
  const island = JSON.stringify({ actions: [{ type: 'alert', message: alertMessage }] });
  return (
    '<div class="dlg-body">' +
    '<script>' + scriptBody + '<\/script>' +
    '<script type="application/json" class="wtm-dialog-init">' + island + '<\/script>' +
    '</div>'
  );
}

// ---------------------------------------------------------------------------
// 1-3, 9: ff._legacyScriptEval — property flag + live-read behaviour
// ---------------------------------------------------------------------------
describe('#627 ff._legacyScriptEval — DisableLegacyScriptRehydration property', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  test('1. default OFF: no flag/meta set — code still executes (zero behaviour change)', () => {
    const { ff, ctx } = loadFreshFf();
    ff._legacyScriptEval('window.__k627a = 1;');
    expect(ctx.__k627a).toBe(1);
  });

  test('2. property ON: DisableLegacyScriptRehydration === true blocks execution and logs console.error', () => {
    const { ff, ctx } = loadFreshFf();
    const errSpy = jest.spyOn(console, 'error').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff._legacyScriptEval('window.__k627b = 1;');

    expect(ctx.__k627b).toBeUndefined();
    expect(errSpy).toHaveBeenCalledWith(
      '[WTM] Legacy IsScript script-body response BLOCKED by DisableLegacyScriptRehydration. ' +
      'Migrate the server action to FFResultJson() (X-WTM-Action). See #627/#470.'
    );
  });

  test.each([
    ['the string "true"', 'true'],
    ['the number 1', 1],
  ])('3. truthy-but-not-true property (%s) does NOT block execution (strict === true check)', (_label, val) => {
    const { ff, ctx } = loadFreshFf();
    ff.DisableLegacyScriptRehydration = val;
    ff._legacyScriptEval('window.__k627c = 1;');
    expect(ctx.__k627c).toBe(1);
  });

  test('9. live read: toggling the property between calls is honored immediately (no caching)', () => {
    const { ff, ctx } = loadFreshFf();

    ff.DisableLegacyScriptRehydration = true;
    ff._legacyScriptEval('window.__k627i = 1;');
    expect(ctx.__k627i).toBeUndefined();

    ff.DisableLegacyScriptRehydration = false;
    ff._legacyScriptEval('window.__k627i = 1;');
    expect(ctx.__k627i).toBe(1);
  });
});

// ---------------------------------------------------------------------------
// 4: ff._legacyScriptEval — <meta> mechanism
// ---------------------------------------------------------------------------
describe('#627 ff._legacyScriptEval — <meta name="wtm-disable-legacy-script-rehydration"> mechanism', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document
      .querySelectorAll('meta[name="wtm-disable-legacy-script-rehydration"]')
      .forEach((m) => m.parentNode && m.parentNode.removeChild(m));
  });

  test('4a. meta content="true" blocks execution', () => {
    const meta = document.createElement('meta');
    meta.setAttribute('name', 'wtm-disable-legacy-script-rehydration');
    meta.setAttribute('content', 'true');
    document.head.appendChild(meta);

    const { ff, ctx } = loadFreshFf();
    ff._legacyScriptEval('window.__k627d = 1;');

    expect(ctx.__k627d).toBeUndefined();
  });

  test.each([
    ['TRUE', 'TRUE'],
    ['1', '1'],
  ])('4b. meta content=%s does NOT block (exact "true" string match only)', (_label, val) => {
    const meta = document.createElement('meta');
    meta.setAttribute('name', 'wtm-disable-legacy-script-rehydration');
    meta.setAttribute('content', val);
    document.head.appendChild(meta);

    const { ff, ctx } = loadFreshFf();
    ff._legacyScriptEval('window.__k627e = 1;');

    expect(ctx.__k627e).toBe(1);
  });

  test('4c. property unset/false + meta absent — unaffected baseline (sanity)', () => {
    const { ff, ctx } = loadFreshFf();
    ff.DisableLegacyScriptRehydration = false;
    ff._legacyScriptEval('window.__k627f = 1;');
    expect(ctx.__k627f).toBe(1);
  });
});

// ---------------------------------------------------------------------------
// 5-6: ff.OpenDialog success callback — _initScripts rehydration loop
// ---------------------------------------------------------------------------
describe('#627 ff.OpenDialog — _initScripts rehydration loop gating', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    document
      .querySelectorAll('meta[name="wtm-disable-legacy-script-rehydration"]')
      .forEach((m) => m.parentNode && m.parentNode.removeChild(m));
  });

  test('5. flag ON: inline <script> NOT executed, dialog-init island IS dispatched, console.warn called with count', () => {
    const layui = makeDialogLayui();
    const html = dialogHtmlWithScriptAndAlertIsland('window.__k627g = 1;', 'dialog init on');
    const ajax = makeAjaxSuccess(html, {});
    const { ff } = loadFreshFf(layui, ajax);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog('/some/edit/url', 'w627a', 'Title', 500, 400);

    expect(window.__k627g).toBeUndefined();
    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('dialog init on', { title: '' });
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in dialog were NOT executed')
    );

    delete window.__k627g;
  });

  test('5b. flag ON via <meta> (property left unset): inline <script> NOT executed, island IS dispatched, console.warn called with count — mutation guard for meta support in this loop', () => {
    const meta = document.createElement('meta');
    meta.setAttribute('name', 'wtm-disable-legacy-script-rehydration');
    meta.setAttribute('content', 'true');
    document.head.appendChild(meta);

    const layui = makeDialogLayui();
    const html = dialogHtmlWithScriptAndAlertIsland('window.__k627g2 = 1;', 'dialog init meta');
    const ajax = makeAjaxSuccess(html, {});
    const { ff } = loadFreshFf(layui, ajax);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    // Note: DisableLegacyScriptRehydration is intentionally left
    // false/undefined here — only the <meta> mechanism is set.
    ff.OpenDialog('/some/edit/url', 'w627a2', 'Title', 500, 400);

    expect(window.__k627g2).toBeUndefined();
    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('dialog init meta', { title: '' });
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in dialog were NOT executed')
    );

    delete window.__k627g2;
  });

  test('6. flag OFF: inline <script> executes AND island dispatched (regression guard for ordering path)', () => {
    const layui = makeDialogLayui();
    const html = dialogHtmlWithScriptAndAlertIsland('window.__k627h = 1;', 'dialog init off');
    const ajax = makeAjaxSuccess(html, {});
    const { ff } = loadFreshFf(layui, ajax);

    ff.OpenDialog('/some/edit/url', 'w627b', 'Title', 500, 400);

    expect(window.__k627h).toBe(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('dialog init off', { title: '' });

    delete window.__k627h;
  });
});

// ---------------------------------------------------------------------------
// 7-8: ff._replayInitFromHtml — initScripts rehydration loop gating
// ---------------------------------------------------------------------------
describe('#627 ff._replayInitFromHtml — initScripts rehydration loop gating', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    delete window.__k627k;
    delete window.__k627l;
    delete window.__k627n;
    document
      .querySelectorAll('meta[name="wtm-disable-legacy-script-rehydration"]')
      .forEach((m) => m.parentNode && m.parentNode.removeChild(m));
  });

  test('7. flag ON: collected script NOT executed, island IS dispatched, console.warn called with count', () => {
    const layui = makeDialogLayui();
    const { ff } = loadFreshFf(layui);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff._replayInitFromHtml({
      initScripts: ['window.__k627k = 1;'],
      islandPayloads: [{ actions: [{ type: 'alert', message: 'replay init on' }] }]
    });

    expect(window.__k627k).toBeUndefined();
    expect(layui.layer.alert).toHaveBeenCalledWith('replay init on', { title: '' });
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in fragment were NOT executed')
    );
  });

  test('7b. flag ON via <meta> (property left unset): collected script NOT executed, island IS dispatched, console.warn called with count — mutation guard for meta support in this loop', () => {
    const meta = document.createElement('meta');
    meta.setAttribute('name', 'wtm-disable-legacy-script-rehydration');
    meta.setAttribute('content', 'true');
    document.head.appendChild(meta);

    const layui = makeDialogLayui();
    const { ff } = loadFreshFf(layui);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    // Note: DisableLegacyScriptRehydration is intentionally left
    // false/undefined here — only the <meta> mechanism is set.
    ff._replayInitFromHtml({
      initScripts: ['window.__k627n = 1;'],
      islandPayloads: [{ actions: [{ type: 'alert', message: 'replay init meta' }] }]
    });

    expect(window.__k627n).toBeUndefined();
    expect(layui.layer.alert).toHaveBeenCalledWith('replay init meta', { title: '' });
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in fragment were NOT executed')
    );
  });

  test('8. flag OFF: both collected script and island run (regression guard)', () => {
    const layui = makeDialogLayui();
    const { ff } = loadFreshFf(layui);

    ff._replayInitFromHtml({
      initScripts: ['window.__k627l = 1;'],
      islandPayloads: [{ actions: [{ type: 'alert', message: 'replay init off' }] }]
    });

    expect(window.__k627l).toBe(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('replay init off', { title: '' });
  });

  test('empty initScripts with flag ON: no warn emitted (count-gated)', () => {
    const layui = makeDialogLayui();
    const { ff } = loadFreshFf(layui);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff._replayInitFromHtml({
      initScripts: [],
      islandPayloads: [{ actions: [{ type: 'alert', message: 'no scripts' }] }]
    });

    expect(warnSpy).not.toHaveBeenCalled();
    expect(layui.layer.alert).toHaveBeenCalledWith('no scripts', { title: '' });
  });
});

// ---------------------------------------------------------------------------
// 10-12: ff.OpenDialog2 — selector search-panel $$script$$ rehydration
// gating (#332 template tokens; the 4th gated point, added by the #627
// review follow-up — the original #627 commit only gated points 1-3 above).
//
// layer.open is mocked in this suite and never actually inserts `content`
// into the live document, so — unlike the OpenDialog/_replayInitFromHtml
// loops above, which append real <script> elements to the real jsdom
// document (runScripts:'dangerously', so they truly execute and are
// asserted via window.__k...) — these tests assert directly on the
// `content` string handed to the layer.open mock; nothing here ever runs.
//
// ff.SafeHtml fails closed to '' when window.DOMPurify is undefined (by
// design — see the Phase 3B tests), which loadFreshFf's context never
// provides by default. A passthrough sanitize is enough here since none of
// the fixture HTML below needs actual stripping. OpenDialog2 also reads
// $(tempId)[0].innerHTML, which the default fixed-shape jQuery stub can't
// serve (it ignores its selector argument entirely) — a small ID-selector-
// aware jQuery mock is used instead for this describe only.
// ---------------------------------------------------------------------------

function makePassthroughDomPurify() {
  return { sanitize: function (html) { return html === undefined || html === null ? '' : html; } };
}

function makeSelectorJqueryFactory() {
  return function (selector) {
    if (typeof selector === 'string' && selector.charAt(0) === '#') {
      var el = document.getElementById(selector.slice(1));
      return el ? [el] : [];
    }
    return {
      cookie: jest.fn(),
      text: function (s) { this._text = s; return this; },
      html: function () { return this._text; }
    };
  };
}

// The ajax response needs: the `wtVar_x = table.render(xoption)` marker
// regGridVar requires; a <table lay-filter> for the safe DOM-query grid-id
// extraction (#332); and the literal $$SearchPanel$$ placeholder the
// tempId template gets spliced into.
const OPEN_DIALOG2_RESPONSE_HTML =
  '<div>wtVar_x = table.render(xoption);</div>' +
  '<table id="g1" lay-filter="f1"></table>' +
  '$$SearchPanel$$';

function makeOpenDialog2Layui() {
  return {
    use: jest.fn((mods, cb) => cb()),
    layer: {
      load: jest.fn(() => 1),
      close: jest.fn(),
      alert: jest.fn(),
      full: jest.fn(),
      open: jest.fn(() => 'mockWinId')
    }
  };
}

function makeOpenDialog2TempEl(id) {
  var el = document.createElement('div');
  el.id = id;
  el.innerHTML = '$$script$$window.__k627e=1$$#script$$<div>panel</div>';
  document.body.appendChild(el);
  return el;
}

describe('#627 ff.OpenDialog2 — selector search-panel $$script$$ rehydration gating (4th gated point, review follow-up)', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    document
      .querySelectorAll('meta[name="wtm-disable-legacy-script-rehydration"]')
      .forEach((m) => m.parentNode && m.parentNode.removeChild(m));
  });

  test('10. flag OFF: $$script$$ tokens rehydrated into a live <script> tag in the rendered content (regression guard)', () => {
    makeOpenDialog2TempEl('Temp627a');
    const layui = makeOpenDialog2Layui();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/search/url', 'w627od2a', 'Title', 500, 400, '#Temp627a');

    expect(layui.layer.open).toHaveBeenCalledTimes(1);
    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<script>window.__k627e=1</script>');
    expect(content).toContain('<div>panel</div>');
  });

  test('11. flag ON (property): $$script$$ tokens stripped — no <script> tag, no token residue, console.warn called with count 1', () => {
    makeOpenDialog2TempEl('Temp627b');
    const layui = makeOpenDialog2Layui();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/some/search/url', 'w627od2b', 'Title', 500, 400, '#Temp627b');

    expect(layui.layer.open).toHaveBeenCalledTimes(1);
    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<div>panel</div>');
    expect(content).not.toContain('<script>');
    expect(content).not.toContain('$$script$$');
    expect(content).not.toContain('$$#script$$');
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in the selector search-panel template were NOT executed')
    );
  });

  test('12. flag ON via <meta> (property left unset): same as 11 via the meta mechanism — mutation guard for meta support in this gate', () => {
    const meta = document.createElement('meta');
    meta.setAttribute('name', 'wtm-disable-legacy-script-rehydration');
    meta.setAttribute('content', 'true');
    document.head.appendChild(meta);

    makeOpenDialog2TempEl('Temp627c');
    const layui = makeOpenDialog2Layui();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    // Note: DisableLegacyScriptRehydration is intentionally left
    // false/undefined here — only the <meta> mechanism is set.
    ff.OpenDialog2('/some/search/url', 'w627od2c', 'Title', 500, 400, '#Temp627c');

    expect(layui.layer.open).toHaveBeenCalledTimes(1);
    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<div>panel</div>');
    expect(content).not.toContain('<script>');
    expect(content).not.toContain('$$script$$');
    expect(content).not.toContain('$$#script$$');
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in the selector search-panel template were NOT executed')
    );
  });
});
