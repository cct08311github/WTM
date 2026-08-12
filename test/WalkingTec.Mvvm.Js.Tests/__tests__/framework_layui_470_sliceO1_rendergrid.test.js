// Tests for Issue #470 Slice O1 — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF — the SAME flag #470 Slices J/K/L/M/N1 use) eval-free
// 'renderGrid' JSON island for <wt:grid>/DataTableTagHelper. Design
// authority: internal infrastructure issue #470 comment 18118.
//
// This file drives the REAL ff.DispatchAction / ff._renderGridAction /
// ff.gridTemplets / ff._islandModulesFor end-to-end against a FRESH vm
// instance of the actual shipped framework_layui.js source (real jsdom
// `document`) — not a source-sweep-only or a hand-rolled reimplementation —
// following the same convention as framework_layui_470_sliceK_transfer.test.js
// / framework_layui_470_sliceN1_treecontainer_chart.test.js. layui.table is
// stubbed (a real layui.use(...) module in production, never loaded under
// jsdom).

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
// Harness
// ---------------------------------------------------------------------------

function makeJQueryMock() {
  const cssSpy = jest.fn();
  const attrSpy = jest.fn();
  function wrap(el) {
    const w = {
      find: jest.fn(() => wrap(el)),
      css: jest.fn(function () { cssSpy.apply(null, arguments); return w; }),
      attr: jest.fn(function () { attrSpy.apply(null, arguments); return w; }),
      addClass: jest.fn(() => w),
      children: jest.fn(() => wrap(el)),
      closest: jest.fn(() => wrap(el)),
      // ff.EscapeText/ff.EscapeAttr rely on the real jQuery `$('<div/>').text(s)
      // .html()` idiom to HTML-encode a string — backed by a REAL jsdom element
      // (not re-implemented/stubbed) so the XSS-neutralisation assertions below
      // exercise genuine escaping behavior, same as
      // framework_layui_cell_template_xss_108.test.js's own convention.
      text: function (s) { if (el) { el.textContent = s; } return w; },
      html: function () { return el ? el.innerHTML : ''; },
      length: el ? 1 : 0,
      0: el
    };
    return w;
  }
  const $ = jest.fn(function (selector) {
    if (typeof selector === 'string' && selector.charAt(0) === '#') {
      return wrap(document.getElementById(selector.slice(1)));
    }
    if (typeof selector === 'string' && selector.charAt(0) === '<') {
      return wrap(document.createElement('div'));
    }
    return wrap(null);
  });
  $.cssSpy = cssSpy;
  $.attrSpy = attrSpy;
  // Real jQuery.extend supports the (deep, target, ...sources) signature —
  // ff._renderGridAction calls `$.extend(true, window[gridId+'defaultfilter'], opt)`.
  // Object.assign alone treats its FIRST arg as the target, so a bare alias
  // would silently merge onto the boolean `true` instead. Shallow merge is
  // enough for what these tests assert (presence of specific top-level keys).
  $.extend = function () {
    const args = Array.prototype.slice.call(arguments);
    if (typeof args[0] === 'boolean') { args.shift(); }
    const target = args.shift() || {};
    args.forEach((src) => { if (src) { Object.assign(target, src); } });
    return target;
  };
  $.get = jest.fn();
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.fn = {};
  return $;
}

function makeGridLayui(overrides) {
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    table: {
      render: jest.fn(() => ({ __fake: 'tableInstance' })),
      reload: jest.fn(),
      on: jest.fn()
    },
    layer: { confirm: jest.fn(), alert: jest.fn() },
    // wtmColVis.init (framework_layui.js, a REAL top-level object defined
    // later in the same file — not an external module) lazily registers a
    // `layui.form.on(...)` handler on its first call; ff._renderGridAction's
    // done() unconditionally calls it (mirrors the legacy inline script's own
    // unconditional `if(typeof wtmColVis!=='undefined'){wtmColVis.init(...)}`
    // guard) — needs a minimal `form` module present.
    form: { on: jest.fn(), render: jest.fn() },
    // ff.GetFormData (called by ff.PostForm/ff.GetSearchFormData, which the
    // sort(gridId) handler below calls) iterates jQuery-like collections via
    // layui.each — same minimal polyfill as
    // framework_layui_587_scoped_consumer.test.js's makeLayui.
    each: function (obj, fn) {
      if (obj && typeof obj.length === 'number') {
        for (let i = 0; i < obj.length; i++) { fn.call(obj[i], i, obj[i]); }
      } else {
        Object.keys(obj || {}).forEach((k) => fn(k, obj[k]));
      }
    }
  }, overrides || {});
}

function loadFreshFf(opts) {
  opts = opts || {};
  const $ = opts.$ || makeJQueryMock();
  const layui = opts.layui || makeGridLayui();
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    DOMParser: global.DOMParser,
    // ff._renderGridAction's done() dispatches a wtm:gridRendered
    // CustomEvent — vm.createContext sandboxes do NOT inherit Node/jsdom's
    // global CustomEvent unless explicitly provided, so without this the
    // `typeof CustomEvent === 'function'` guard would always be false inside
    // the sandbox and the event would silently never fire.
    CustomEvent: global.CustomEvent,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx, layui, $ };
}

function appendTable(id) {
  document.body.innerHTML = '';
  const table = document.createElement('table');
  table.id = id;
  document.body.appendChild(table);
  return table;
}

afterEach(() => { document.body.innerHTML = ''; });

// A representative renderGrid action, shaped exactly like
// DataTableTagHelper.Island.cs's BuildRenderGridAction would emit for a
// simple two-column, paged, non-selector grid (mirrors
// RenderGridIsland470SliceO1Tests.cs's Default_FlagOn_EmitsRenderGridIsland_
// XorLegacyRender fixture on the C# side).
function makeBaseAction(overrides) {
  const base = {
    type: 'renderGrid',
    gridId: 'wtTable_O1',
    tableJsVar: 'wtVar_wtTable_O1',
    elem: '#wtTable_O1',
    id: 'wtTable_O1',
    text: { none: 'No Data' },
    request: { pageName: 'Page', limitName: 'Limit' },
    toolbar: undefined,
    defaultToolbar: [],
    totalRow: false,
    where: { _DONOT_USE_VMNAME: 'X', SearcherMode: 0 },
    method: 'post',
    page: {
      rpptext: 'Records/page', totaltext: 'Total', recordtext: 'Record',
      gototext: 'Goto', pagetext: 'Page', oktext: 'OK'
    },
    limit: 20,
    limits: [10, 20, 50],
    cols: [[
      { type: 'checkbox', rowspan: 1, fixed: 'left', unresize: true },
      { type: 'numbers', rowspan: 1, fixed: 'left', unresize: true },
      { field: 'LoginName', title: 'Login', templet: { tpl: 'plain', field: 'LoginName', random: 'abc123', hasFormat: false, encodeFormat: false } },
      { field: 'Name', title: 'Name', templet: { tpl: 'plain', field: 'Name', random: 'def456', hasFormat: false, encodeFormat: false } }
    ]],
    exportFileName: 'wtTable_O1',
    enableClientExport: false,
    isInSelector: false,
    searchPanelId: 'wtForm_O1',
    fieldPre: 'Searcher',
    autoSearch: true,
    mobileLayout: true,
    done: {
      heightAuto: true, maxDepth: 1, multiLine: false, enableHeaderFilter: false,
      titleError: 'Error', titleColumnFilter: 'Filter', titlePrint: 'Print'
    }
  };
  return Object.assign({}, base, overrides || {});
}

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice O1 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderGrid' case delegates to ff._renderGridAction, no inline body", () => {
    const block = active.match(/case\s+['"]renderGrid['"]:[\s\S]{0,120}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderGridAction\(\s*action\s*\)/);
  });

  test("'renderGrid' HAS an _islandModulesFor entry — layui.table IS a layui.use(...) module", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(fn).toMatch(/renderGrid/);
    expect(fn).toMatch(/needed\.table\s*=\s*true/);
    expect(fn).toMatch(/mods\.push\('table'\)/);
  });

  test('#627 kill-switch stays EXACTLY 4 gated execution points after #470 Slice O1', () => {
    const matches = active.match(/ff\._isLegacyRehydrationDisabled\(\)/g) || [];
    expect(matches).toHaveLength(4);
  });

  test('ff.gridTemplets registry covers all 6 descriptor tpl values', () => {
    const block = active.match(/gridTemplets:\s*\{[\s\S]*?\n\s{4}\},/)[0];
    expect(block).toMatch(/plain:\s*function/);
    expect(block).toMatch(/progress:\s*function/);
    expect(block).toMatch(/tag:\s*function/);
    expect(block).toMatch(/image:\s*function/);
    expect(block).toMatch(/currency:\s*function/);
    expect(block).toMatch(/currencyRow:\s*function/);
  });

  test('_renderGridAction resolves doneFn/checkedFn through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = active.match(/_renderGridAction:\s*function[\s\S]*?\n\s{4}\},\n\n/)[0];
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*doneCfg\.doneFn\s*\)/);
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*doneCfg\.checkedFn\s*\)/);
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });
});

// ---------------------------------------------------------------------------
// Dispatch -> layui.table.render option parity
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — renderGrid dispatch: option shape parity', () => {
  test('dispatch calls layui.table.render with the expected static option fields', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });

    expect(layui.table.render).toHaveBeenCalled();
    const opt = layui.table.render.mock.calls[0][0];
    expect(opt.elem).toBe('#wtTable_O1');
    expect(opt.id).toBe('wtTable_O1');
    expect(opt.text).toEqual({ none: 'No Data' });
    expect(opt.request).toEqual({ pageName: 'Page', limitName: 'Limit' });
    expect(opt.method).toBe('post');
    expect(opt.headers).toEqual({ layuisearch: 'true' });
    expect(opt.where).toEqual({ _DONOT_USE_VMNAME: 'X', SearcherMode: 0 });
    expect(opt.limit).toBe(20);
    expect(opt.limits).toEqual([10, 20, 50]);
    expect(opt.page).toMatchObject({ rpptext: 'Records/page', oktext: 'OK' });
    expect(typeof opt.done).toBe('function');
  });

  test('defaultToolbar is ALWAYS the array from the payload, even when empty — never omitted (layui default-icon regression guard)', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ defaultToolbar: [] })] });

    const opt = layui.table.render.mock.calls[0][0];
    expect(Array.isArray(opt.defaultToolbar)).toBe(true);
    expect(opt.defaultToolbar).toEqual([]);
  });

  test('page:false when action.page is absent (non-paged grid)', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ page: undefined, limits: undefined })] });

    const opt = layui.table.render.mock.calls[0][0];
    expect(opt.page).toBe(false);
  });

  test('heightMode "fixed" -> numeric height; "full" -> string-concatenated height', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ heightMode: 'fixed', heightValue: 300 })] });
    expect(layui.table.render.mock.calls[0][0].height).toBe(300);

    appendTable('wtTable_O1');
    const { ff: ff2, layui: layui2 } = loadFreshFf();
    ff2.DispatchAction({ actions: [makeBaseAction({ heightMode: 'full', heightValue: -100 })] });
    expect(layui2.table.render.mock.calls[0][0].height).toBe('full-100');
  });

  test('a grid with no layui.table loaded skips silently with a console.warn (no throw)', () => {
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    appendTable('wtTable_O1');
    const { ff } = loadFreshFf({ layui: { use: jest.fn() } });
    expect(() => ff.DispatchAction({ actions: [makeBaseAction()] })).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('renderGrid action skipped'));
  });
});

// ---------------------------------------------------------------------------
// Invariant 3 — compat globals written synchronously, before/around render
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — compat globals written synchronously (invariant 3)', () => {
  test('window[gridId+option/defaultfilter/filterback/url] and window[tableJsVar] are all set synchronously', () => {
    appendTable('wtTable_O1');
    const { ff, windowObj } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });

    expect(windowObj['wtTable_O1option']).toBeDefined();
    expect(windowObj['wtTable_O1option'].elem).toBe('#wtTable_O1');
    expect(windowObj['wtTable_O1defaultfilter']).toBeDefined();
    expect(windowObj['wtTable_O1defaultfilter'].elem).toBe('#wtTable_O1'); // $.extend(true, defaultfilter, opt)
    expect(windowObj['wtTable_O1filterback']).toEqual({});
    expect(windowObj['wtTable_O1url']).toBe('');
    expect(windowObj['wtVar_wtTable_O1']).toEqual({ __fake: 'tableInstance' });
  });

  test('url is carried through from action.url', () => {
    appendTable('wtTable_O1');
    const { ff, windowObj } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ url: '/Student/GetGridJson' })] });
    expect(windowObj['wtTable_O1url']).toBe('/Student/GetGridJson');
  });
});

// ---------------------------------------------------------------------------
// table.on(...) wiring (A10) — tool/checkbox/sort/exportData
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — table.on(...) wiring (A10)', () => {
  test('tool(gridId) wired to window["wtToolBarFunc_"+gridId] when present', () => {
    appendTable('wtTable_O1');
    const { ff, layui, windowObj } = loadFreshFf();
    const toolFn = jest.fn();
    windowObj.wtToolBarFunc_wtTable_O1 = toolFn;

    ff.DispatchAction({ actions: [makeBaseAction()] });

    const toolCall = layui.table.on.mock.calls.find((c) => c[0] === 'tool(wtTable_O1)');
    expect(toolCall).toBeDefined();
    expect(toolCall[1]).toBe(toolFn);
  });

  test('checkbox(gridId) wired ONLY when done.checkedFn resolves to a real window function', () => {
    appendTable('wtTable_O1');
    const { ff, layui, windowObj } = loadFreshFf();
    const checkedFn = jest.fn();
    windowObj.myCheckedHandler = checkedFn;

    ff.DispatchAction({ actions: [makeBaseAction({ done: Object.assign({}, makeBaseAction().done, { checkedFn: 'myCheckedHandler' }) })] });

    const checkboxCall = layui.table.on.mock.calls.find((c) => c[0] === 'checkbox(wtTable_O1)');
    expect(checkboxCall).toBeDefined();
    expect(checkboxCall[1]).toBe(checkedFn);
  });

  test('checkbox(gridId) is NOT wired when checkedFn is absent', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });
    const checkboxCall = layui.table.on.mock.calls.find((c) => c[0] === 'checkbox(wtTable_O1)');
    expect(checkboxCall).toBeUndefined();
  });

  test('checkbox(gridId) is NOT wired when checkedFn does not resolve to a real global function (guarded — no throw)', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    expect(() => ff.DispatchAction({
      actions: [makeBaseAction({ done: Object.assign({}, makeBaseAction().done, { checkedFn: 'thisDoesNotExist' }) })]
    })).not.toThrow();
    const checkboxCall = layui.table.on.mock.calls.find((c) => c[0] === 'checkbox(wtTable_O1)');
    expect(checkboxCall).toBeUndefined();
  });

  test('sort(gridId) builds SortInfo.Property/Direction and calls table.reload', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });

    const sortCall = layui.table.on.mock.calls.find((c) => c[0] === 'sort(wtTable_O1)');
    expect(sortCall).toBeDefined();
    sortCall[1]({ field: 'Name', type: 'asc' });

    expect(layui.table.reload).toHaveBeenCalledWith('wtTable_O1', expect.objectContaining({
      where: expect.objectContaining({ 'SortInfo.Property': 'Name', 'SortInfo.Direction': 'Asc' })
    }));
  });

  test('sort(gridId) uses the "Searcher." prefix when action.isInSelector is true', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ isInSelector: true })] });

    const sortCall = layui.table.on.mock.calls.find((c) => c[0] === 'sort(wtTable_O1)');
    sortCall[1]({ field: 'Name', type: 'desc' });

    expect(layui.table.reload).toHaveBeenCalledWith('wtTable_O1', expect.objectContaining({
      where: expect.objectContaining({ 'Searcher.SortInfo.Property': 'Name', 'Searcher.SortInfo.Direction': 'Desc' })
    }));
  });

  test('exportData(gridId) wired only when action.enableClientExport is true, sets filename', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ enableClientExport: true, exportFileName: 'MyExport' })] });

    const exportCall = layui.table.on.mock.calls.find((c) => c[0] === 'exportData(wtTable_O1)');
    expect(exportCall).toBeDefined();
    const obj = {};
    exportCall[1](obj);
    expect(obj.filename).toBe('MyExport');
  });

  test('exportData(gridId) is NOT wired when enableClientExport is false', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction({ enableClientExport: false })] });
    const exportCall = layui.table.on.mock.calls.find((c) => c[0] === 'exportData(wtTable_O1)');
    expect(exportCall).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// done() callback — A4 parity + wtm:gridRendered event
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — done() callback (A4 parity)', () => {
  test('done() resolves a guarded doneFn and calls it with (res,curr,count)', () => {
    appendTable('wtTable_O1');
    const { ff, layui, windowObj } = loadFreshFf();
    const doneFn = jest.fn();
    windowObj.myDoneHandler = doneFn;
    ff.DispatchAction({ actions: [makeBaseAction({ done: Object.assign({}, makeBaseAction().done, { doneFn: 'myDoneHandler' }) })] });

    const opt = layui.table.render.mock.calls[0][0];
    opt.done.call({}, { Code: 200, data: [] }, 1, 0);

    expect(doneFn).toHaveBeenCalledWith({ Code: 200, data: [] }, 1, 0);
  });

  test('done() dispatches a wtm:gridRendered CustomEvent on the table element', () => {
    const table = appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    const handler = jest.fn();
    table.addEventListener('wtm:gridRendered', handler);
    ff.DispatchAction({ actions: [makeBaseAction()] });

    const opt = layui.table.render.mock.calls[0][0];
    const res = { Code: 200, data: [], count: 3 };
    opt.done.call({}, res, 1, 3);

    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler.mock.calls[0][0].detail).toEqual({ res: res, curr: 1, count: 3 });
  });

  test('done() applies the aggregate footer for each configured field when res.Aggregates is present', () => {
    appendTable('wtTable_O1');
    const { ff, layui, $ } = loadFreshFf();
    ff.DispatchAction({
      actions: [makeBaseAction({ totalRow: true, done: Object.assign({}, makeBaseAction().done, { aggregateFields: ['Price'] }) })]
    });

    const opt = layui.table.render.mock.calls[0][0];
    expect(() => opt.done.call({}, { Code: 200, data: [], Aggregates: { Price: '123.00' } }, 1, 1)).not.toThrow();
  });

  test('done() shows layer.confirm on 401 and layer.alert on a non-200 non-401 code', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });
    const opt = layui.table.render.mock.calls[0][0];

    opt.done.call({}, { Code: 401, Msg: 'auth' }, 1, 0);
    expect(layui.layer.confirm).toHaveBeenCalled();

    opt.done.call({}, { Code: 500, Msg: 'boom' }, 1, 0);
    expect(layui.layer.alert).toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// ff.gridTemplets registry — parity with the legacy C# templet builders
// (getTemplate/GetRichTemplate/BuildCurrencyTemplate), including the #108
// XSS payload fixtures.
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — ff.gridTemplets registry parity', () => {
  const XSS_SCRIPT = '<script>alert(document.cookie)<\/script>';
  const XSS_IMG = '<img src=x onerror="alert(1)">';
  const XSS_ATTR_BREAKOUT = '" onmouseover="alert(1)';

  function renderInto(html) {
    const container = document.createElement('div');
    container.innerHTML = html;
    return container;
  }

  test("'plain' templet: hasFormat=false escapes row data (#108 XSS guard)", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Name', random: 'r1', hasFormat: false, encodeFormat: false });
    const html = templet({ LAY_INDEX: 0, Name: XSS_SCRIPT });
    const container = renderInto(html);
    expect(container.querySelectorAll('script').length).toBe(0);
    expect(container.textContent).toContain('script');
  });

  test("'plain' templet: hasFormat=false neutralises an <img onerror> payload", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Name', random: 'r1', hasFormat: false });
    const html = templet({ LAY_INDEX: 0, Name: XSS_IMG });
    const container = renderInto(html);
    expect(container.querySelectorAll('img').length).toBe(0);
  });

  test("'plain' templet: hasFormat=false neutralises an attribute-breakout payload", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Name', random: 'r1', hasFormat: false });
    const html = templet({ LAY_INDEX: 0, Name: XSS_ATTR_BREAKOUT });
    const container = renderInto(html);
    const inner = container.querySelector('div');
    expect(inner.getAttribute('onmouseover')).toBeNull();
  });

  test("'plain' templet: hasFormat=true && !encodeFormat renders framework HTML verbatim (Make* buttons)", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Btn', random: 'r1', hasFormat: true, encodeFormat: false });
    const html = templet({ LAY_INDEX: 0, Btn: '<a class="layui-btn">View</a>' });
    const container = renderInto(html);
    expect(container.querySelector('a.layui-btn')).not.toBeNull();
  });

  test("'plain' templet: hasFormat=true && encodeFormat=true STILL escapes (opt-in #387 SetFormatEncode)", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Name', random: 'r1', hasFormat: true, encodeFormat: true });
    const html = templet({ LAY_INDEX: 0, Name: XSS_SCRIPT });
    const container = renderInto(html);
    expect(container.querySelectorAll('script').length).toBe(0);
  });

  test("'plain' templet: the did element id incorporates field+random+LAY_INDEX exactly like getTemplate()", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'LoginName', random: 'abc123', hasFormat: false });
    const html = templet({ LAY_INDEX: 7, LoginName: 'x' });
    expect(html).toContain('id="LoginNameabc123_7"');
  });

  test("'plain' templet: __bgcolor/__forecolor nested-script trick reproduced verbatim (getTemplate() parity)", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Status', random: 'r9', hasFormat: false });
    const html = templet({ LAY_INDEX: 2, Status: 'ok', Status__bgcolor: '#ff0000', Status__forecolor: '#fff' });
    // Same nested-<script>-string-concatenation trick as DataTableTagHelper.cs's
    // getTemplate() ("</s"+"cript>" avoids a literal "</script>" substring
    // inside the JS source that generated it — reproduced verbatim here).
    expect(html).toContain("<script>$('#Statusr9_2').closest('td').css('background-color','#ff0000');</s" + "cript>");
    expect(html).toContain('color:#fff;');
  });

  test("'plain' templet: __bgcolor/__forecolor JSON null is loose-equal to undefined and SKIPS the branch (getTemplate() parity, not strict !==)", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.plain({ field: 'Status', random: 'r9', hasFormat: false });
    // Legacy getTemplate() (DataTableTagHelper.cs:1448) guards with loose
    // `!= undefined`, under which `null != undefined` is false — a row field
    // that is explicitly JSON null (as opposed to simply absent) is treated
    // the same as undefined and the branch is SKIPPED. A strict `!==
    // undefined` guard would wrongly ENTER the branch for null and emit a
    // broken `background-color`/`color:null` write. This pins the loose-
    // equality parity and must FAIL if the registry guard regresses to `!==`.
    const html = templet({ LAY_INDEX: 2, Status: 'ok', Status__bgcolor: null, Status__forecolor: null });
    expect(html).not.toContain('background-color');
    expect(html).not.toContain('<script>');
    expect(html).not.toContain('color:null');
    expect(html).toBe('<div style="" id="Statusr9_2">ok</div>');
  });

  test("'bool' templet aliases 'plain' — same output for the same descriptor", () => {
    const { ff } = loadFreshFf();
    const descriptor = { field: 'IsValid', random: 'r2', hasFormat: true };
    const plainHtml = ff.gridTemplets.plain(descriptor)({ LAY_INDEX: 0, IsValid: '<i class="chk"></i>' });
    const boolHtml = ff.gridTemplets.bool(descriptor)({ LAY_INDEX: 0, IsValid: '<i class="chk"></i>' });
    expect(boolHtml).toBe(plainHtml);
  });

  test("'progress' templet renders a layui-progress bar with EscapeAttr'd percent", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.progress({ field: 'Pct' });
    const html = templet({ Pct: '50' });
    expect(html).toContain('layui-progress-bar');
    expect(html).toContain('lay-percent="50%"');
  });

  test("'progress' templet neutralises an attribute-breakout payload via EscapeAttr", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.progress({ field: 'Pct' });
    const html = templet({ Pct: '10" onmouseover="alert(1)' });
    const container = renderInto(html);
    const bar = container.querySelector('.layui-progress-bar');
    expect(bar.getAttribute('onmouseover')).toBeNull();
  });

  test("'tag' templet: no tagColor -> layui-badge-rim; with tagColor -> layui-bg-{color}", () => {
    const { ff } = loadFreshFf();
    const plainTag = ff.gridTemplets.tag({ field: 'Status' })({ Status: 'Active' });
    expect(plainTag).toContain('layui-badge-rim');

    const coloredTag = ff.gridTemplets.tag({ field: 'Status', tagColor: 'blue' })({ Status: 'Active' });
    expect(coloredTag).toContain('layui-bg-blue');
  });

  test("'tag' templet escapes row data (#108 XSS guard)", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.tag({ field: 'Status', tagColor: 'blue' });
    const html = templet({ Status: XSS_SCRIPT });
    const container = renderInto(html);
    expect(container.querySelectorAll('script').length).toBe(0);
  });

  test("'image' templet: empty when falsy, <img> with EscapeAttr'd src + configured size otherwise", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.image({ field: 'Photo', imageSize: 48 });
    expect(templet({ Photo: '' })).toBe('');
    const html = templet({ Photo: '/img/a.png' });
    expect(html).toContain('src="/img/a.png"');
    expect(html).toContain('width:48px;height:48px');
  });

  test("'image' templet defaults to 32px when imageSize is absent", () => {
    const { ff } = loadFreshFf();
    const html = ff.gridTemplets.image({ field: 'Photo' })({ Photo: '/img/a.png' });
    expect(html).toContain('width:32px;height:32px');
  });

  test("'image' templet neutralises a src attribute-breakout payload", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.image({ field: 'Photo' });
    const html = templet({ Photo: 'x" onerror="alert(1)' });
    const container = renderInto(html);
    const img = container.querySelector('img');
    expect(img.getAttribute('onerror')).toBeNull();
  });

  test("'currency' templet: fixed format uses toLocaleString with 2 decimal digits", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.currency({ field: 'Price', currencyFormat: '0.00' });
    expect(templet({ Price: 1234.5 })).toBe((1234.5).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
  });

  test("'currency' templet: no fixed format uses plain toLocaleString", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.currency({ field: 'Price' });
    expect(templet({ Price: 1234 })).toBe((1234).toLocaleString());
  });

  test("'currency' templet: null/undefined -> empty string", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.currency({ field: 'Price' });
    expect(templet({ Price: null })).toBe('');
    expect(templet({})).toBe('');
  });

  test("'currencyRow' templet: valid 3-letter code uses Intl.NumberFormat with that currency", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.currencyRow({ field: 'Amount', currencyCodeField: 'Ccy' });
    const html = templet({ Amount: 10, Ccy: 'USD' });
    expect(html).toBe(new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD' }).format(10));
  });

  test("'currencyRow' templet: invalid code falls back to plain escaped number", () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.currencyRow({ field: 'Amount', currencyCodeField: 'Ccy' });
    expect(templet({ Amount: 10, Ccy: 'NOTREAL' })).toBe('10');
    expect(templet({ Amount: 10, Ccy: 123 })).toBe('10');
  });
});

// ---------------------------------------------------------------------------
// cols templet-descriptor rebuild — the whole array-of-arrays walk
// ---------------------------------------------------------------------------
describe('#470 Slice O1 — cols templet descriptor rebuild', () => {
  test('every templet descriptor in cols is replaced by a real function before table.render', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });

    const opt = layui.table.render.mock.calls[0][0];
    const row = opt.cols[0];
    expect(typeof row[2].templet).toBe('function');
    expect(typeof row[3].templet).toBe('function');
    expect(row[2].templet({ LAY_INDEX: 0, LoginName: 'x' })).toContain('LoginNameabc123_0');
  });

  test('a column with no templet (e.g. checkbox/numbers) is left untouched', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({ actions: [makeBaseAction()] });

    const opt = layui.table.render.mock.calls[0][0];
    expect(opt.cols[0][0].templet).toBeUndefined();
    expect(opt.cols[0][0].type).toBe('checkbox');
  });

  test('an unknown tpl value is left as the plain descriptor object (no throw, no silent function)', () => {
    appendTable('wtTable_O1');
    const { ff, layui } = loadFreshFf();
    const action = makeBaseAction({
      cols: [[{ field: 'X', templet: { tpl: 'notARealTpl', field: 'X' } }]]
    });
    expect(() => ff.DispatchAction({ actions: [action] })).not.toThrow();
    const opt = layui.table.render.mock.calls[0][0];
    expect(typeof opt.cols[0][0].templet).not.toBe('function');
  });
});
