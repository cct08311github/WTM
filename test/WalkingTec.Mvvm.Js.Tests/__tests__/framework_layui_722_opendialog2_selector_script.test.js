// Tests for Issue #722: ff.OpenDialog2 was stripping Views/_Framework/
// Selector.cshtml's OWN top-level <script> block (submitSelect/
// gridCheckedFunc) — and the wt:grid TagHelper's own inline
// table.render(...) init script, also a top-level <script> in that same
// response body — with NO restoration path. ff.OpenDialog2 only rehydrated
// the CALLING page's $$script$$ search-panel tokens (page-local #Temp{Id}
// template text); it never extracted+replayed the ajax RESPONSE body's own
// <script> elements the way ff.OpenDialog does for its response body. The
// picker dialog opened, but its grid never called table.render, so
// GetPagingData never fired and the grid stayed empty. Found live by the
// #681 e2e suite, present with the #627 kill-switch both on and off.
//
// The fix (bounded, reuses existing machinery, no new eval): OpenDialog2 now
// calls the SAME shared ff._collectInitFromHtml/_replayInitFromHtml helper
// pair ff.OpenDialog already uses for its own same-origin response body
// (factored out by #587) — collecting BEFORE ff.SafeHtml/DOMPurify strips
// <script> elements, replaying AFTER the sanitized content is in the live
// DOM, gated by the same #627 kill-switch (ff._isLegacyRehydrationDisabled).
//
// This file drives ff.OpenDialog2 against a response shaped exactly like the
// REAL Views/_Framework/Selector.cshtml output: a top-level <script> with
// submitSelect/gridCheckedFunc-shaped code, PLUS a separate top-level
// <script> matching the wt:grid TagHelper's `wtVar_X = table.render(Xoption)`
// marker shape ff.OpenDialog2's own regGridVar regex looks for — proving both
// survive end-to-end through the real (non-mocked) ff.SafeHtml/DOMPurify
// sanitize step, in both kill-switch states.

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

const purifyPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/dompurify.js'
);
const purifySource = fs.readFileSync(purifyPath, 'utf8');

function loadRealDomPurify() {
  // Same technique as framework_layui_635_opendialog2_island_dispatch.test.js
  // / framework_layui_phase3b_html_sanitize.test.js: the vendored
  // dompurify.js is a UMD bundle; running it attaches DOMPurify to whatever
  // global/window it resolves to in this Node+jsdom environment.
  // Safety note: `purifySource` is read from this repo's own vendored,
  // version-pinned src/WalkingTec.Mvvm.Mvc/dompurify.js — trusted,
  // repo-controlled source, not external or attacker-influenced input.
  // `new Function` is used (not eval) purely to get an isolated top-level
  // scope for the UMD bundle.
  const runInWindow = new Function('window', 'document', purifySource);
  runInWindow(global.window || global, global.document || {});
  return (global.window && global.window.DOMPurify) || global.DOMPurify;
}

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

function makeAjaxSuccess(responseHtml, headers) {
  return jest.fn((opts) => {
    const request = {
      getResponseHeader: (name) =>
        Object.prototype.hasOwnProperty.call(headers || {}, name) ? headers[name] : null
    };
    opts.success(responseHtml, 'success', request);
  });
}

// layer.open mock that ACTUALLY inserts `content` into the live jsdom
// document and executes any JS-typed <script> elements found in it (mirrors
// jQuery's domManip/DOMEval behavior — a raw element.innerHTML assignment
// would leave such scripts inert per the HTML spec's "already started"
// flag) — matches framework_layui_635_opendialog2_island_dispatch.test.js's
// makeOpenDialog2LayuiWithDomInsertion exactly, since #722's replay
// mechanism (like #635's island dispatch) is driven by a REAL DOM subtree.
// Safety note: `opts.content` here is ALWAYS a string produced by the real
// ff.OpenDialog2 source under test from THIS FILE's own hard-coded fixture
// text (makeSelectorResponseHtml below) — never external/attacker-controlled
// input — so this innerHTML assignment and the `new Function(s.textContent)`
// call are safe test-mock plumbing that emulates real browser/jQuery script
// execution, not a real sanitization boundary and not a production code
// path. The real sanitization boundary (ff.SafeHtml/DOMPurify) is exercised
// for real inside ff.OpenDialog2 itself, before this mock ever sees
// `content` — see test 1's assertion that `content` contains no <script>.
function makeOpenDialog2LayuiWithDomInsertion() {
  return {
    use: jest.fn((mods, cb) => cb()),
    layer: {
      load: jest.fn(() => 1),
      close: jest.fn(),
      alert: jest.fn(),
      full: jest.fn(),
      open: jest.fn((opts) => {
        var container = document.createElement('div');
        container.innerHTML = opts.content;
        document.body.appendChild(container);
        var scripts = container.querySelectorAll('script');
        for (var i = 0; i < scripts.length; i++) {
          var s = scripts[i];
          var type = (s.getAttribute('type') || '').toLowerCase();
          var isJs = type === '' || type === 'text/javascript' || type === 'application/javascript';
          if (isJs) {
            (new Function(s.textContent))();
          }
        }
        if (typeof opts.success === 'function') { opts.success(container); }
        return 'mockWinId';
      })
    }
  };
}

function makeOpenDialog2TempEl(id, innerHtml) {
  var el = document.createElement('div');
  el.id = id;
  el.innerHTML = innerHtml;
  document.body.appendChild(el);
  return el;
}

// Shaped like the REAL Views/_Framework/Selector.cshtml + wt:grid TagHelper
// output, in the SAME document order the real .cshtml renders them:
// Selector.cshtml's own top-level <script> (submitSelect/gridCheckedFunc —
// issue #722's primary victim) comes FIRST in the real view (see
// src/WalkingTec.Mvvm.Mvc/Views/_Framework/Selector.cshtml lines 9-97,
// BEFORE the <wt:container>/<wt:grid> markup), followed by the <table
// lay-filter> and wt:grid TagHelper's own inline
// `wtVar_X = table.render(Xoption)` init script (issue #722's second
// victim, and the exact marker ff.OpenDialog2's own regGridVar regex looks
// for), then the $$SearchPanel$$ placeholder the #Temp{Id} template gets
// spliced into. Both push onto window.__722initOrder so ordering can be
// asserted directly, matching the pre-existing #576/#635 ordering-proof
// convention in this suite rather than relying on incidental co-occurrence.
function makeSelectorResponseHtml() {
  return (
    '<script>' +
    'window.__722initOrder = (window.__722initOrder || []);' +
    'function submitSelect() { window.__722submit = true; }' +
    'function gridCheckedFunc(obj) { window.__722checked = true; }' +
    'window.__722initOrder.push("selector-script");' +
    '</script>' +
    '<table id="wtTable_g1" lay-filter="f1"></table>' +
    '<script>' +
    'var wtVar_g1 = table.render(g1option);' +
    'window.__722grid = (window.__722grid || 0) + 1;' +
    'window.__722initOrder.push("grid-render");' +
    '</script>' +
    '$$SearchPanel$$'
  );
}

describe('#722 ff.OpenDialog2 — Selector.cshtml own init scripts survive through the real ff.SafeHtml/DOMPurify sanitize step', () => {
  beforeEach(() => {
    // The replayed <script> is inserted via document.createElement +
    // document.body.appendChild (ff._replayInitFromHtml), which the browser/
    // jsdom executes in the REAL global scope — not inside the vm.createContext
    // sandbox `ctx` these tests otherwise drive ff through. `table` normally
    // comes from layui's own `var table = layui.table;` pattern on a real
    // page; stub it here so the wt:grid TagHelper's
    // `wtVar_g1 = table.render(g1option)` marker line actually runs instead
    // of throwing a ReferenceError, matching real runtime shape without
    // pulling in the full layui table module. `g1option` mirrors the
    // per-grid options object a real wt:grid TagHelper also emits inline
    // (`window.g1option = {...}`) before its render call.
    window.table = { render: jest.fn(() => ({})) };
    window.g1option = {};
    // A top-level `function submitSelect() {...}` declared by a real
    // <script> element becomes a NON-CONFIGURABLE property of `window` in
    // jsdom (matching real browser behavior) — `delete window.submitSelect`
    // throws once it exists. Reset by REASSIGNMENT instead (still legal for
    // a non-configurable but writable property), both before and after each
    // test, so test 3 (kill-switch ON) can assert "was never (re)defined"
    // against a clean baseline regardless of test execution order.
    window.submitSelect = undefined;
    window.gridCheckedFunc = undefined;
  });

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    document
      .querySelectorAll('meta[name="wtm-disable-legacy-script-rehydration"]')
      .forEach((m) => m.parentNode && m.parentNode.removeChild(m));
    delete window.__722grid;
    delete window.__722submit;
    delete window.__722checked;
    delete window.__722initOrder;
    window.submitSelect = undefined;
    window.gridCheckedFunc = undefined;
    delete window.table;
    delete window.g1option;
  });

  test('1. kill-switch OFF (default): both Selector.cshtml\'s own submitSelect/gridCheckedFunc script and the wt:grid table.render init script run, and the functions are callable afterward', () => {
    makeOpenDialog2TempEl('Temp722a', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeSelectorResponseHtml(), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/_Framework/Selector', 'w722a', 'Pick', 500, 400, '#Temp722a');

    // The grid's own init script actually ran — this is the concrete #722
    // regression: before the fix, table.render's marker script never
    // executed because it was DOMPurify-stripped with no replay.
    expect(window.__722grid).toBe(1);
    // Selector.cshtml's own <script> block ran too, defining both functions.
    // ff._replayInitFromHtml re-injects the collected script text as a real
    // <script> element via document.createElement + document.body.appendChild
    // (native <script> re-injection, not per-script eval — see
    // ff.OpenDialog's #522 rationale, reused here unchanged), which the
    // browser/jsdom executes in the REAL global `window` — not inside the
    // vm.createContext sandbox `ctx` these tests otherwise drive ff through
    // — so the defined functions land on `window`, exactly as they would in
    // a real browser tab.
    expect(typeof window.submitSelect).toBe('function');
    expect(typeof window.gridCheckedFunc).toBe('function');
    window.submitSelect();
    window.gridCheckedFunc({});
    expect(window.__722submit).toBe(true);
    expect(window.__722checked).toBe(true);

    // The sanitized `content` handed to layer.open must NOT contain a live
    // <script> tag (ff.SafeHtml/DOMPurify's FORBID_TAGS still strips it from
    // the MARKUP — only the collect-before-sanitize/replay-after-insert path
    // added by #722 makes the code run, exactly as ff.OpenDialog already
    // does for its own response body).
    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>');
    // Cleanup happens in afterEach via reassignment (delete would throw —
    // see beforeEach's comment on non-configurable window properties).
  });

  test('2. ordering: Selector.cshtml\'s own script runs BEFORE the wt:grid table.render script, matching the real .cshtml\'s document order (native global scope, siblings share state)', () => {
    makeOpenDialog2TempEl('Temp722b', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeSelectorResponseHtml(), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/_Framework/Selector', 'w722b', 'Pick', 500, 400, '#Temp722b');

    expect(window.__722initOrder).toEqual(['selector-script', 'grid-render']);
  });

  test('3. kill-switch ON (property): the grid/selector init scripts are NOT executed, with a diagnostic warning, and the dialog still opens', () => {
    makeOpenDialog2TempEl('Temp722c', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeSelectorResponseHtml(), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/_Framework/Selector', 'w722c', 'Pick', 500, 400, '#Temp722c');

    // Documents the KNOWN LIMITATION when the #627 kill-switch is ON: this
    // is #470/#722-islandification territory (Option B), not something this
    // bounded #722 fix attempts — Selector.cshtml's own script is legacy
    // inline code, same category as every other kill-switch-gated point.
    // The warning text is ff._replayInitFromHtml's own message (the shared
    // helper OpenDialog2 now calls) — "in fragment", not OpenDialog's own
    // inline "in dialog" wording, since OpenDialog2 does not duplicate that
    // loop, it reuses the shared helper.
    expect(window.__722grid).toBeUndefined();
    expect(window.submitSelect).toBeUndefined();
    expect(layui.layer.open).toHaveBeenCalledTimes(1);
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('legacy inline script(s) in fragment were NOT executed')
    );
  });

  test('4. no false positive: a response with no top-level <script> at all does not throw and dispatches nothing extra', () => {
    makeOpenDialog2TempEl('Temp722d', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const responseNoScript =
      '<table id="wtTable_g2" lay-filter="f2"></table>' +
      '<div>wtVar_g2 = table.render(g2option);</div>' +
      '$$SearchPanel$$';
    const ajax = makeAjaxSuccess(responseNoScript, {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    expect(() => {
      ff.OpenDialog2('/_Framework/Selector', 'w722d', 'Pick', 500, 400, '#Temp722d');
    }).not.toThrow();
    expect(layui.layer.open).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// Issue #470 Slice O1 — OpenDialog2's widened island-aware gate (design
// brief comment 18118 §4). A flag-ON renderGrid island carries NO
// `wtVar_...=table.render(...)` text at all — regGridVar alone would never
// widen the :3922-region gate for an island-rendered selector grid response.
// These tests reuse this file's EXISTING #722 harness (real ff.OpenDialog2,
// real DOMPurify) end-to-end, extending its coverage to the island case
// rather than re-deriving a parallel one. NOTE (per the design brief): with
// IsInSelector grids forced legacy in O1 (DataTableTagHelper.Island.cs's
// DetermineGridIslandDecision), this widened branch is DORMANT for
// Selector.cshtml itself today — these tests exercise the mechanism directly
// (a hand-crafted response shaped like a hypothetical future non-selector
// dialog-hosted island grid) since the real Selector.cshtml never reaches it
// yet.
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — ff.OpenDialog2 widened island-aware gate + literal oldgridid rewrite', () => {
  beforeEach(() => {
    // makeOpenDialog2LayuiWithDomInsertion's layer.open mock ACTUALLY
    // executes any JS-typed <script> found in the inserted content (mirrors
    // real jQuery/browser behavior — see that helper's own comment above).
    // Tests 2/3 below rewrite a `table.reload(...)` <script> fragment that
    // then genuinely runs; `table` mirrors the `var table = layui.table;`
    // global a real page's layui.use(['table'], ...) callback establishes —
    // same fixture convention as this file's #722 `beforeEach` above.
    window.table = { reload: jest.fn() };
  });

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.table;
  });

  // Response shaped like an island-rendered grid: the <table> carries
  // data-wtm-grid-id (DataTableTagHelper.Island.cs, island path ONLY) and a
  // renderGrid island — but crucially NO `wtVar_...=table.render(...)` text
  // anywhere (unlike every other fixture in this file), so regGridVar alone
  // can never detect it.
  function makeIslandGridResponseHtml(gridId) {
    return (
      '<table id="' + gridId + '" lay-filter="' + gridId + '" data-wtm-grid-id="' + gridId + '"></table>' +
      '<script type="application/json" class="wtm-dialog-init">' +
      JSON.stringify({ type: 'renderGrid', gridId: gridId }) +
      '</script>' +
      '$$SearchPanel$$'
    );
  }

  test('1. an island-only response (no wtVar_=table.render text) still substitutes $$SearchPanel$$ — the widened gate fires', () => {
    makeOpenDialog2TempEl('TempO1a', '<div>panel <a id="btnO1a">Search</a></div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeIslandGridResponseHtml('wtTable_o1a'), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/dialog/url', 'wO1a', 'Pick', 500, 400, '#TempO1a');

    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('$$SearchPanel$$');
    expect(content).toContain('btnO1a');
  });

  test('2. old-gridid in the calling page\'s cached template is rewritten to the NEW island gridId (literal replace)', () => {
    makeOpenDialog2TempEl(
      'TempO1b',
      '<div>panel</div><script>table.reload(\'oldgrid_g9\',{url:\'/x\'});</script>'
    );
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeIslandGridResponseHtml('wtTable_o1b'), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/dialog/url', 'wO1b', 'Pick', 500, 400, '#TempO1b');

    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain("table.reload('wtTable_o1b',{url:'/x'}");
    expect(content).not.toContain('oldgrid_g9');
  });

  test('3. old-gridid rewrite is LITERAL (split/join), never a RegExp — a "." in the old id does not act as a wildcard', () => {
    // Old code used `new RegExp(oldgridid, "gim")`, so an old id containing a
    // regex metacharacter ('.') would ALSO match-and-replace unrelated
    // substrings that merely LOOK similar (here: "gridXQ" would match the
    // pattern /grid.Q/ derived from the literal id "grid.Q" with "." as a
    // wildcard). With literal split/join, only the EXACT substring is ever
    // touched — "gridXQ" must survive untouched.
    makeOpenDialog2TempEl(
      'TempO1c',
      '<div>keep gridXQ untouched, and grid.Q also untouched here</div>' +
      '<script>table.reload(\'grid.Q\',{url:\'/x\'});</script>'
    );
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeIslandGridResponseHtml('wtTable_o1c'), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/dialog/url', 'wO1c', 'Pick', 500, 400, '#TempO1c');

    const content = layui.layer.open.mock.calls[0][0].content;
    // The exact literal "grid.Q" occurrence (inside table.reload(...)) IS replaced.
    expect(content).toContain("table.reload('wtTable_o1c',{url:'/x'}");
    // The unrelated "gridXQ" text — which a `/grid.Q/` REGEX would ALSO have
    // matched (since "." wildcards any character) — must survive untouched.
    expect(content).toContain('keep gridXQ untouched');
  });

  test('4. a response with NO grid at all (neither legacy nor island) leaves $$SearchPanel$$ un-substituted — no false positive', () => {
    makeOpenDialog2TempEl('TempO1d', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    // A non-grid island (e.g. a colorpicker) must NOT, by itself, widen the
    // grid-substitution gate.
    const responseNoGrid =
      '<script type="application/json" class="wtm-dialog-init">' +
      JSON.stringify({ type: 'colorpicker', opts: { elem: '#cp1' } }) +
      '</script>' +
      '$$SearchPanel$$';
    const ajax = makeAjaxSuccess(responseNoGrid, {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    expect(() => {
      ff.OpenDialog2('/some/dialog/url', 'wO1d', 'Pick', 500, 400, '#TempO1d');
    }).not.toThrow();
    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('$$SearchPanel$$');
  });

  test('5. an island renderGrid action with a malformed (non wtTable_-shaped) gridId does not widen the gate', () => {
    makeOpenDialog2TempEl('TempO1e', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    // gridId shape validation (design brief §4 point 2): only a
    // /^wtTable_[0-9a-zA-Z_]+$/-shaped island gridId is trusted as a
    // widening signal.
    const responseMalformedId =
      '<table id="not-a-wttable-id" lay-filter="f"></table>' +
      '<script type="application/json" class="wtm-dialog-init">' +
      JSON.stringify({ type: 'renderGrid', gridId: 'not-a-wttable-id' }) +
      '</script>' +
      '$$SearchPanel$$';
    const ajax = makeAjaxSuccess(responseMalformedId, {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/dialog/url', 'wO1e', 'Pick', 500, 400, '#TempO1e');

    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('$$SearchPanel$$');
  });

  test('6. #627 kill-switch ON still opens the dialog with an island-only grid response (island dispatch is data, ungated)', () => {
    makeOpenDialog2TempEl('TempO1f', '<div>panel</div>');
    const realDomPurify = loadRealDomPurify();
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(makeIslandGridResponseHtml('wtTable_o1f'), {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.DisableLegacyScriptRehydration = true;
    expect(() => {
      ff.OpenDialog2('/some/dialog/url', 'wO1f', 'Pick', 500, 400, '#TempO1f');
    }).not.toThrow();
    expect(layui.layer.open).toHaveBeenCalledTimes(1);
  });
});
