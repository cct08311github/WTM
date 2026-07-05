// Tests for Issue #587: ff.ConsumeIslandsIn — a scoped .wtm-dialog-init
// island consumer for DOM subtrees that neither of the two pre-existing
// consumers cover.
//
// Background: framework_layui.js consumed .wtm-dialog-init JSON islands at
// exactly TWO entry points before this change:
//   1. ff._consumePageReadyIslands (DOMContentLoaded, live `document` only,
//      runs exactly ONCE).
//   2. ff.OpenDialog's detached-DOMParser pre-collection (reads islands from
//      a same-origin partial BEFORE ff.SafeHtml/DOMPurify strips them).
//
// Two DOM-insertion paths bypassed BOTH:
//   GAP 1 — a SPA-tab framework (layuiadmin lib/view.js) inserting ajax'd
//   HTML fragments via jQuery .html() well after DOMContentLoaded: islands
//   in the fragment were never scanned by anything.
//   GAP 2 — ff.PostForm's validation-failure form-HTML redraw branch, which
//   had no pre-collection step at all (unlike ff.OpenDialog), so SafeHtml
//   silently stripped any island/script in the redrawn markup.
//
// This file locks in:
//   - ff.ConsumeIslandsIn(rootEl): scoped scan + dispatch, jQuery-unwrap,
//     null/detached no-op, root-self inclusion, idempotent claim-before-
//     dispatch, malformed-island isolation, and — critically — that it is
//     SCOPED ONLY (no document-wide fallback), which is what prevents it
//     from re-dispatching an island something else already handled
//     elsewhere in the page (the BMS-canary double-bindSubmit scenario).
//   - ff._collectInitFromHtml / ff._replayInitFromHtml: the shared
//     extraction+replay helper pair factored out of ff.OpenDialog's
//     #462/#470/#522 logic (OpenDialog's own inline code is left
//     byte-for-byte untouched — see the helper's own comment for why) and
//     reused by ff.PostForm's redraw branch (Gap 2 fix).
//   - the layuiadmin lib/view.js SPA-tab integration (Gap 1 fix) present in
//     every demo copy of the vendored file.

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
describe('#587 ff.ConsumeIslandsIn — source sweep', () => {
  test('window.ff.ConsumeIslandsIn is defined', () => {
    expect(active).toMatch(/window\.ff\.ConsumeIslandsIn\s*=\s*function\s*\(\s*rootEl\s*\)/);
  });

  test('ConsumeIslandsIn unwraps a jQuery-wrapped rootEl via [0]', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/rootEl\s*&&\s*rootEl\.jquery/);
    expect(block[0]).toMatch(/rootEl\s*=\s*rootEl\[0\]/);
  });

  test('ConsumeIslandsIn no-ops on null/undefined/non-Element input, never throws', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/!rootEl\s*\|\|\s*typeof\s+rootEl\.querySelectorAll\s*!==\s*['"]function['"]/);
  });

  test('ConsumeIslandsIn treats a detached rootEl (isConnected === false) as a no-op', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/rootEl\.isConnected\s*===\s*false/);
  });

  test('ConsumeIslandsIn includes rootEl itself when it matches the island selector', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/rootEl\.matches\s*\(\s*SELECTOR\s*\)/);
    expect(block[0]).toMatch(/querySelectorAll\s*\(\s*SELECTOR\s*\)/);
  });

  test('ConsumeIslandsIn excludes already-claimed islands via :not([data-wtm-dispatched])', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/:not\(\[data-wtm-dispatched\]\)/);
  });

  test('ConsumeIslandsIn claims (marks) each island BEFORE scheduling its dispatch', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    const setIdx = block[0].indexOf("setAttribute('data-wtm-dispatched'");
    const dispatchIdx = block[0].indexOf('ff._dispatchIslandWhenReady(');
    expect(setIdx).toBeGreaterThan(-1);
    expect(dispatchIdx).toBeGreaterThan(-1);
    expect(setIdx).toBeLessThan(dispatchIdx);
  });

  test('ConsumeIslandsIn dispatches through ff._dispatchIslandWhenReady (module-load-race-safe), not a bare DispatchAction', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._dispatchIslandWhenReady\s*\(\s*normalized\s*\)/);
    expect(block[0]).not.toMatch(/ff\.DispatchAction\s*\(\s*normalized\s*\)/);
  });

  test('a malformed single island does not abort the loop (per-node try/catch)', () => {
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/try\s*\{[\s\S]{0,200}JSON\.parse\(\s*node\.textContent\s*\)/);
  });

  test('ConsumeIslandsIn is documented as SCOPED-ONLY BY DESIGN with no document-wide fallback', () => {
    // This assertion is documentation-only (a `//` comment block), so it is
    // checked against the RAW source, not the comment-stripped `active` text.
    expect(src).toMatch(/SCOPED-ONLY BY DESIGN/);
    // Regression guard: must never fall back to a bare `document.querySelectorAll`
    // inside ConsumeIslandsIn's ACTIVE code (that would reintroduce the
    // double-dispatch hazard the comment describes).
    const block = active.match(/window\.ff\.ConsumeIslandsIn\s*=\s*function[\s\S]{0,2200}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).not.toMatch(/document\.querySelectorAll/);
  });

  test('ff._collectInitFromHtml and ff._replayInitFromHtml are defined', () => {
    expect(active).toMatch(/_collectInitFromHtml\s*:\s*function/);
    expect(active).toMatch(/_replayInitFromHtml\s*:\s*function/);
  });

  test('_collectInitFromHtml uses a DETACHED DOMParser (not regex) to extract scripts and islands', () => {
    const block = active.match(/_collectInitFromHtml\s*:\s*function[\s\S]{0,2000}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/new DOMParser\(\)\.parseFromString/);
    expect(block[0]).toMatch(/querySelectorAll\s*\(\s*['"]script['"]\s*\)/);
    expect(block[0]).toMatch(/querySelectorAll\s*\(\s*['"]script\[type="application\/json"\]\.wtm-dialog-init['"]\s*\)/);
    expect(block[0]).toMatch(/ff\._normalizeIslandPayload\s*\(\s*parsed\s*\)/);
  });

  test('_replayInitFromHtml re-injects scripts via real <script> elements, then dispatches islands', () => {
    const block = active.match(/_replayInitFromHtml\s*:\s*function[\s\S]{0,900}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/document\.createElement\s*\(\s*['"]script['"]\s*\)/);
    expect(block[0]).toMatch(/document\.body\.appendChild/);
    expect(block[0]).toMatch(/ff\._dispatchIslandWhenReady\s*\(\s*islandPayloads\[pi\]\s*\)/);
    const scriptIdx = block[0].indexOf('document.createElement');
    const dispatchIdx = block[0].indexOf('ff._dispatchIslandWhenReady(');
    expect(scriptIdx).toBeGreaterThan(-1);
    expect(dispatchIdx).toBeGreaterThan(-1);
    expect(scriptIdx).toBeLessThan(dispatchIdx);
  });

  test('ff.OpenDialog own inline extraction is untouched by this change (sanity check)', () => {
    // Full regression coverage for OpenDialog lives in the #462/#470/#522/#556
    // test files; this is just a presence check that this PR did not
    // collateral-edit OpenDialog's own inline variables while adding the
    // shared helper above.
    expect(active).toMatch(/var _initScripts = \[\]/);
    expect(active).toMatch(/var _dialogInitPayloads = \[\]/);
  });

  test('ff.PostForm redraw branch pre-collects via ff._collectInitFromHtml BEFORE building the wrapper, and replays via ff._replayInitFromHtml AFTER inserting it', () => {
    // Extract the PostForm function body by slicing between its own
    // declaration and the next top-level member (BgRequest) — more robust
    // than a length-capped regex against a function this long.
    const startIdx = active.indexOf('PostForm: function');
    const endIdx = active.indexOf('BgRequest:', startIdx);
    expect(startIdx).toBeGreaterThan(-1);
    expect(endIdx).toBeGreaterThan(startIdx);
    const block = active.slice(startIdx, endIdx);

    const collectIdx = block.indexOf('ff._collectInitFromHtml(data)');
    const wrapperIdx = block.indexOf("$('<div/>')");
    const appendIdx = block.indexOf('.parent().empty().append(_wrapper)');
    const replayIdx = block.indexOf('ff._replayInitFromHtml(_pfCollected)');
    expect(collectIdx).toBeGreaterThan(-1);
    expect(wrapperIdx).toBeGreaterThan(-1);
    expect(appendIdx).toBeGreaterThan(-1);
    expect(replayIdx).toBeGreaterThan(-1);
    expect(collectIdx).toBeLessThan(wrapperIdx);
    expect(wrapperIdx).toBeLessThan(appendIdx);
    expect(appendIdx).toBeLessThan(replayIdx);
  });

  test('ff.PostForm other branches (X-WTM-Action JSON, IsScript legacy eval) are untouched', () => {
    expect(active).toMatch(/wtmActionHdr\s*===\s*['"]application\/json['"]/);
    expect(active).toMatch(/request\.getResponseHeader\(\s*['"]IsScript['"]\s*\)\s*===\s*['"]true['"]/);
    expect(active).toMatch(/ff\._legacyScriptEval\(\s*data\s*\)/);
  });

  test('raw eval( token count in the file is unchanged from the pre-#587 baseline (4: 1 real call + 3 comment mentions)', () => {
    // Unlike the active-code sweep below, this counts the RAW file (including
    // comments) — mirrors the repo's own release-gate check
    // (`grep -c "eval(" src/WalkingTec.Mvvm.Mvc/framework_layui.js`).
    const matches = src.match(/eval\(/g) || [];
    expect(matches).toHaveLength(4);
  });

  test('active-code eval( count is still exactly 1 after the #587 change', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// layuiadmin lib/view.js SPA-tab integration — source sweep (Gap 1 fix)
// ---------------------------------------------------------------------------
describe('#587 layuiadmin lib/view.js SPA-tab integration — every demo copy', () => {
  const viewJsPaths = [
    '../../../demo/WalkingTec.Mvvm.Demo/wwwroot/layuiadmin/lib/view.js',
    '../../../demo/WalkingTec.Mvvm.ReactDemo/wwwroot/layuiadmin/lib/view.js',
    '../../../demo/WalkingTec.Mvvm.VueDemo/wwwroot/layuiadmin/lib/view.js',
    '../../../demo/WalkingTec.Mvvm.Vue3Demo/wwwroot/layuiadmin/lib/view.js',
    '../../../demo/WalkingTec.Mvvm.BlazorDemo/WalkingTec.Mvvm.BlazorDemo/wwwroot/layuiadmin/lib/view.js',
  ];

  test.each(viewJsPaths)('%s contains the guarded ff.ConsumeIslandsIn call, marked WTM #587, and stays syntactically valid', (rel) => {
    const p = path.resolve(__dirname, rel);
    const content = fs.readFileSync(p, 'utf8');

    expect(content).toMatch(/WTM #587/);
    expect(content).toMatch(/window\.ff\s*&&\s*typeof\s+window\.ff\.ConsumeIslandsIn\s*===\s*"function"/);
    expect(content).toMatch(/window\.ff\.ConsumeIslandsIn\(this\)/);
    // Regression guard: the guard must NOT be a `//` line comment — this file
    // is a single minified line, so a `//` comment would silently swallow
    // every remaining vendored statement on the line.
    expect(content).not.toMatch(/\/\/\s*WTM #587/);

    // The moved-nodes jQuery collection is captured BEFORE insertion and
    // reused for both the insertion call and the post-insertion scan (so the
    // scan sees the SAME nodes the ternary just moved into the live DOM,
    // regardless of the "html" vs "after" insertion branch).
    expect(content).toMatch(/var _wtm587ins=l\.children\(\);/);
    expect(content).toMatch(/s\.container\[n\?"after":"html"\]\(_wtm587ins\)/);
    expect(content).toMatch(/_wtm587ins\.each\(function\(\)\{window\.ff\.ConsumeIslandsIn\(this\)\}\)/);

    // Lightweight syntax check: vm.Script's constructor compiles the source
    // and throws SyntaxError on malformed JS WITHOUT executing it (execution
    // only happens on an explicit .runInContext/.runInNewContext call, which
    // this test never makes) — the same vm-based loading approach already
    // used throughout this test suite to load framework_layui.js itself.
    expect(() => new vm.Script(content)).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// Behavioral: real ff.ConsumeIslandsIn / ff.PostForm driven end-to-end via a
// vm-loaded fresh instance of the actual source, against the REAL jsdom
// `document` of this test file (so DOM connectivity — isConnected — is
// meaningful) and a controllable layui mock.
//
// Following the same convention as framework_layui_576_opendialog_island_defer.test.js.
// ---------------------------------------------------------------------------

function makeJqMock() {
  function jqMock(sel) {
    const isHtmlFragment = typeof sel === 'string' && sel.indexOf('<') === 0;
    const _el = isHtmlFragment ? document.createElement('div') : null;
    const obj = {
      find: () => ({ length: 0 }),
      parents: () => ({ length: 0 }),
      attr: function (name, val) {
        if (_el && val !== undefined) { _el.setAttribute(name, val); }
        return obj;
      },
      addClass: function (cls) {
        if (_el) { _el.className = cls; }
        return obj;
      },
      html: function (v) {
        if (v !== undefined) {
          if (_el) { _el.innerHTML = v; }
          return obj;
        }
        return _el ? _el.innerHTML : '';
      },
      text: function (v) {
        if (v !== undefined) {
          if (_el) { _el.textContent = String(v); }
          return obj;
        }
        return _el ? _el.textContent : '';
      },
      parent: () => ({
        empty: function () { return this; },
        append: function () { return this; },
      }),
      length: 0,
    };
    return obj;
  }
  jqMock.cookie = function () { return ''; };
  jqMock.fn = {};
  return jqMock;
}

function loadFreshFf(layui, ajaxImpl) {
  const jq = makeJqMock();
  jq.ajax = ajaxImpl || jest.fn();
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jq,
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx.ff;
}

function makeLayui(layerOverrides) {
  return {
    layer: Object.assign({ load: jest.fn(() => 1), close: jest.fn(), alert: jest.fn() }, layerOverrides || {}),
    use: function (mods, cb) { cb(); }, // every module resolves immediately in this harness
    // ff.GetFormData (called by ff.PostForm) iterates jQuery-like collections
    // via layui.each — every collection in these tests has length 0, so the
    // callback is never actually invoked, but the function must exist.
    each: function (obj, fn) {
      if (obj && typeof obj.length === 'number') {
        for (let i = 0; i < obj.length; i++) { fn.call(obj[i], i, obj[i]); }
      } else {
        Object.keys(obj || {}).forEach((k) => fn(k, obj[k]));
      }
    },
  };
}

function makeIslandNode(json, alreadyDispatched) {
  const el = document.createElement('script');
  el.type = 'application/json';
  el.className = 'wtm-dialog-init';
  el.textContent = JSON.stringify(json);
  if (alreadyDispatched) { el.setAttribute('data-wtm-dispatched', '1'); }
  return el;
}

describe('#587 ff.ConsumeIslandsIn — behavioral (real DOM connectivity)', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('(a) an island inside rootEl is dispatched exactly once and marked', () => {
    const layui = makeLayui();
    const ff = loadFreshFf(layui);

    const root = document.createElement('div');
    document.body.appendChild(root);
    const island = makeIslandNode({ actions: [{ type: 'alert', message: 'Inside A' }] });
    root.appendChild(island);

    ff.ConsumeIslandsIn(root);

    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('Inside A', { title: '' });
    expect(island.getAttribute('data-wtm-dispatched')).toBe('1');

    // Idempotent: a second call on the same root must not re-dispatch.
    ff.ConsumeIslandsIn(root);
    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
  });

  test('(b) an island outside rootEl is left completely untouched (scoped, not document-wide)', () => {
    const layui = makeLayui();
    const ff = loadFreshFf(layui);

    const root = document.createElement('div');
    const outside = document.createElement('div');
    document.body.appendChild(root);
    document.body.appendChild(outside);

    const insideIsland = makeIslandNode({ actions: [{ type: 'alert', message: 'Inside B' }] });
    const outsideIsland = makeIslandNode({ actions: [{ type: 'alert', message: 'Outside B' }] });
    root.appendChild(insideIsland);
    outside.appendChild(outsideIsland);

    ff.ConsumeIslandsIn(root);

    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('Inside B', { title: '' });
    expect(insideIsland.getAttribute('data-wtm-dispatched')).toBe('1');
    expect(outsideIsland.getAttribute('data-wtm-dispatched')).toBeNull();
  });

  test('(c) BMS-shim scenario — an already-marked island inside rootEl is skipped, an unmarked island outside is untouched; neither double-dispatches', () => {
    const layui = makeLayui();
    const ff = loadFreshFf(layui);

    const root = document.createElement('div');
    const outside = document.createElement('div');
    document.body.appendChild(root);
    document.body.appendChild(outside);

    const alreadyMarkedInside = makeIslandNode(
      { actions: [{ type: 'alert', message: 'Already handled by OpenDialog' }] },
      true
    );
    const unmarkedOutside = makeIslandNode({ actions: [{ type: 'alert', message: 'Unrelated, elsewhere on the page' }] });
    root.appendChild(alreadyMarkedInside);
    outside.appendChild(unmarkedOutside);

    ff.ConsumeIslandsIn(root);

    expect(layui.layer.alert).not.toHaveBeenCalled();
    expect(alreadyMarkedInside.getAttribute('data-wtm-dispatched')).toBe('1'); // unchanged
    expect(unmarkedOutside.getAttribute('data-wtm-dispatched')).toBeNull(); // unchanged
  });

  test('(d) a malformed JSON island inside rootEl is claimed but skipped, without preventing other islands from dispatching', () => {
    const layui = makeLayui();
    const ff = loadFreshFf(layui);

    const root = document.createElement('div');
    document.body.appendChild(root);

    const bad = document.createElement('script');
    bad.type = 'application/json';
    bad.className = 'wtm-dialog-init';
    bad.textContent = '{not valid json';
    root.appendChild(bad);

    const good = makeIslandNode({ actions: [{ type: 'alert', message: 'Still works' }] });
    root.appendChild(good);

    expect(() => ff.ConsumeIslandsIn(root)).not.toThrow();

    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('Still works', { title: '' });
    expect(bad.getAttribute('data-wtm-dispatched')).toBe('1');
    expect(good.getAttribute('data-wtm-dispatched')).toBe('1');
  });

  test('(e) root-self inclusion: rootEl itself dispatches when it IS a matching island node', () => {
    const layui = makeLayui();
    const ff = loadFreshFf(layui);

    const island = makeIslandNode({ actions: [{ type: 'alert', message: 'Root itself' }] });
    document.body.appendChild(island);

    ff.ConsumeIslandsIn(island);

    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
    expect(island.getAttribute('data-wtm-dispatched')).toBe('1');
  });

  describe('(e) no-op cases — never throws', () => {
    test('null / undefined rootEl', () => {
      const layui = makeLayui();
      const ff = loadFreshFf(layui);
      expect(() => ff.ConsumeIslandsIn(null)).not.toThrow();
      expect(() => ff.ConsumeIslandsIn(undefined)).not.toThrow();
      expect(layui.layer.alert).not.toHaveBeenCalled();
    });

    test('an empty jQuery-like collection ([0] undefined)', () => {
      const layui = makeLayui();
      const ff = loadFreshFf(layui);
      const emptyJq = { jquery: '3.6.0', length: 0 };
      expect(() => ff.ConsumeIslandsIn(emptyJq)).not.toThrow();
      expect(layui.layer.alert).not.toHaveBeenCalled();
    });

    test('a jQuery-wrapped CONNECTED element is unwrapped via [0] and scanned normally', () => {
      const layui = makeLayui();
      const ff = loadFreshFf(layui);

      const root = document.createElement('div');
      document.body.appendChild(root);
      const island = makeIslandNode({ actions: [{ type: 'alert', message: 'Via jQuery wrapper' }] });
      root.appendChild(island);

      const jqWrapped = { jquery: '3.6.0', 0: root, length: 1 };
      ff.ConsumeIslandsIn(jqWrapped);

      expect(layui.layer.alert).toHaveBeenCalledTimes(1);
      expect(island.getAttribute('data-wtm-dispatched')).toBe('1');
    });

    test('a DETACHED rootEl (never inserted into the document) is a true no-op — not even claimed', () => {
      const layui = makeLayui();
      const ff = loadFreshFf(layui);

      const detached = document.createElement('div'); // never appended anywhere
      const island = makeIslandNode({ actions: [{ type: 'alert', message: 'Detached' }] });
      detached.appendChild(island);

      expect(detached.isConnected).toBe(false);
      ff.ConsumeIslandsIn(detached);

      expect(layui.layer.alert).not.toHaveBeenCalled();
      expect(island.getAttribute('data-wtm-dispatched')).toBeNull();
    });

    test('a plain object without querySelectorAll', () => {
      const layui = makeLayui();
      const ff = loadFreshFf(layui);
      expect(() => ff.ConsumeIslandsIn({})).not.toThrow();
      expect(layui.layer.alert).not.toHaveBeenCalled();
    });
  });
});

// ---------------------------------------------------------------------------
// Behavioral: ff.PostForm validation-failure form-HTML redraw branch (Gap 2)
// ---------------------------------------------------------------------------
describe('#587 ff.PostForm form-HTML redraw — dispatches islands and re-executes legacy scripts', () => {
  afterEach(() => {
    document.body.innerHTML = '';
    delete window.__587log;
  });

  function makeAjaxSuccess(responseHtml, headers) {
    return jest.fn((opts) => {
      const request = {
        getResponseHeader: (name) =>
          Object.prototype.hasOwnProperty.call(headers || {}, name) ? headers[name] : null,
      };
      opts.success(responseHtml, 'success', request);
    });
  }

  function formHtmlWithIslandAndScript(elemSelector, legacyScriptBody) {
    const island = JSON.stringify({
      actions: [{ type: 'laydate', opts: { elem: elemSelector, type: 'date' } }],
    });
    const legacy = legacyScriptBody ? '<script>' + legacyScriptBody + '</script>' : '';
    return (
      '<div class="frm-body">' + legacy +
      '<input id="' + elemSelector.replace('#', '') + '">' +
      '<script type="application/json" class="wtm-dialog-init">' + island + '</script>' +
      '</div>'
    );
  }

  test('(f) redraw branch replays the legacy script THEN dispatches the collected island, in that order', () => {
    window.__587log = [];
    const laydateRender = jest.fn(function (opts) { window.__587log.push('island:' + opts.elem); });
    const layui = makeLayui({});
    layui.laydate = { render: laydateRender };

    const html = formHtmlWithIslandAndScript('#587Date', "window.__587log.push('legacy');");
    const ajax = makeAjaxSuccess(html, {});
    const ff = loadFreshFf(layui, ajax);

    ff.PostForm('/api/save', 'form1', 'div1');

    expect(window.__587log).toEqual(['legacy', 'island:#587Date']);
    expect(laydateRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledWith({ elem: '#587Date', type: 'date' });
    expect(layui.layer.close).toHaveBeenCalled();
  });

  test('redraw branch with no island/script present is unaffected (no errors, nothing extra dispatched)', () => {
    const layui = makeLayui();
    const ajax = makeAjaxSuccess('<div class="frm-body">plain content, no islands</div>', {});
    const ff = loadFreshFf(layui, ajax);

    expect(() => ff.PostForm('/api/save', 'form1', 'div1')).not.toThrow();
    expect(layui.layer.alert).not.toHaveBeenCalled();
    expect(layui.layer.close).toHaveBeenCalled();
  });

  test('X-WTM-Action JSON branch is unaffected by the #587 redraw change', () => {
    const layui = makeLayui();
    const ajax = jest.fn((opts) => {
      const request = { getResponseHeader: (name) => (name === 'X-WTM-Action' ? 'application/json' : null) };
      opts.success(JSON.stringify({ actions: [{ type: 'alert', message: 'From JSON action' }] }), 'success', request);
    });
    const ff = loadFreshFf(layui, ajax);

    ff.PostForm('/api/save', 'form1', 'div1');

    expect(layui.layer.alert).toHaveBeenCalledWith('From JSON action', { title: '' });
  });
});
