// Tests for Issue #470 Slice O2 — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF — the SAME flag #470 Slices J-O1 use) eval-free grid toolbar/
// row-button descriptor dispatch + ff.gridTemplets.actionCol row-action
// registry, completing the toolbar/row-button islandification O1 deferred
// (invariant 7). Design authority: internal infrastructure issue #470 comment 18118 ("Slice O
// design brief", §2 O2).
//
// This file drives the REAL ff._gridToolDispatch / ff._buildGridActionRegistry
// / ff.gridTemplets.actionCol / ff._buttonAction (toolbarButton/removeGridRow/
// analysisToggle) / ff._renderGridAction end-to-end against a FRESH vm
// instance of the actual shipped framework_layui.js source (real jsdom
// `document`) — not a source-sweep-only or a hand-rolled reimplementation —
// following the same convention as framework_layui_470_sliceO1_rendergrid.test.js
// / framework_layui_470_sliceM_buttons.test.js. layui.table is stubbed (a real
// layui.use(...) module in production, never loaded under jsdom); ff.Download/
// ff.OpenDialog/ff.LoadPage/ff.BgRequest/ff.DownloadExcelOrPdf/ff.AddGridRow/
// ff.RemoveGridRow/ff.GetSelections/ff.GetSelectionData are the REAL framework
// functions (only their $.ajax/layui dependencies are stubbed), so this proves
// the dispatcher reaches the actual fixed framework actions.

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
  function wrap(el) {
    const w = {
      find: jest.fn(() => wrap(el)),
      css: jest.fn(() => w),
      attr: jest.fn(() => w),
      addClass: jest.fn(() => w),
      removeClass: jest.fn(() => w),
      toggleClass: jest.fn(() => w),
      parents: jest.fn(() => wrap(null)),
      not: jest.fn(() => wrap(el)),
      children: jest.fn(() => wrap(el)),
      closest: jest.fn(() => wrap(el)),
      is: jest.fn(() => false),
      has: jest.fn(() => wrap(null)),
      on: jest.fn(() => w),
      click: jest.fn(() => w),
      // ff.LoadPage's newwindow=true branch does `$(child.document).ready(...)`
      // on the window.open() return value — stubbed so that (pre-existing,
      // unrelated-to-#470) branch doesn't emit a console.warn noise line
      // inside ff._gridToolDispatch's own try/catch.
      ready: jest.fn(function (cb) { if (typeof cb === 'function') { cb(); } return w; }),
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
  $.extend = function () {
    const args = Array.prototype.slice.call(arguments);
    if (typeof args[0] === 'boolean') { args.shift(); }
    const target = args.shift() || {};
    args.forEach((s) => { if (s) { Object.assign(target, s); } });
    return target;
  };
  $.fn = { on: jest.fn(), jquery: '3.x' };
  $.get = jest.fn();
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.param = jest.fn(() => '');
  return $;
}

function makeGridLayui(overrides) {
  const layer = {
    confirm: jest.fn(),
    alert: jest.fn(),
    msg: jest.fn(),
    load: jest.fn(() => 1),
    close: jest.fn()
  };
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    table: {
      render: jest.fn(() => ({ __fake: 'tableInstance' })),
      reload: jest.fn(),
      on: jest.fn(),
      checkStatus: jest.fn(() => ({ data: [] })),
      cache: {}
    },
    layer: layer,
    form: { on: jest.fn(), render: jest.fn() },
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
    // Legacy AddSubButton's PromptMessage wrap (and ff._gridToolDispatch,
    // which reproduces it) calls bare `layer.confirm(...)` — the generated
    // case body runs at TOP-LEVEL page scope (a plain <script> block, never
    // wrapped in layui.use(...)), relying on the GLOBAL `window.layer` layui
    // itself exposes once its 'layer' module loads. Aliasing to the same
    // mock object as layui.layer lets assertions check either reference.
    layer: layui.layer,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    DOMParser: global.DOMParser,
    CustomEvent: global.CustomEvent,
    // ff.LoadPage reads/writes `location.hash` and calls `window.open(...)`
    // (redirect/newwindow branches) — jsdom's real Window isn't fully backed
    // inside a bare vm.createContext sandbox, so both are stubbed minimally.
    location: { hash: '' },
    open: jest.fn(() => ({ document: { ready: jest.fn() } })),
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  // The script's own top-level `$.ajax({url:'/_framework/GetScriptLanguage',...})`
  // (unrelated to #470 — a pre-existing localized-text bootstrap call) fires
  // the instant the script runs, contaminating any `$.ajax` call-count
  // assertion a test makes afterward. Clearing here keeps that pre-existing
  // behavior intact while giving every test a clean slate to assert from.
  if (typeof $.ajax.mockClear === 'function') { $.ajax.mockClear(); }
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

// A representative renderGrid action with gridActions[]/toolbarHtml/actionMsgs
// — shaped like DataTableTagHelper.Island.cs's BuildRenderGridAction would
// emit for ActionsMatrixListVM (mirrors RenderGridToolbar470SliceO2Tests.cs's
// own fixture on the C# side). NOTE: the descriptor array is named
// `gridActions`, NOT `actions` — see the CRITICAL FIX comment on
// RenderGridIslandAction.Actions (DataTableTagHelper.Island.cs) for why:
// `actions` collides with ff._normalizeIslandPayload's
// `Array.isArray(parsed.actions)` batch-shape probe, which silently made the
// whole grid dead code whenever a real (non-bypassed) page-ready dispatch
// carried this payload — see the "real page-ready entry point" describe
// block below, which is the regression test that would have caught it.
function makeBaseAction(overrides) {
  const base = {
    type: 'renderGrid',
    gridId: 'wtTable_O2',
    tableJsVar: 'wtVar_wtTable_O2',
    elem: '#wtTable_O2',
    id: 'wtTable_O2',
    text: { none: 'No Data' },
    request: { pageName: 'Page', limitName: 'Limit' },
    defaultToolbar: [],
    totalRow: false,
    where: {},
    method: 'post',
    page: { rpptext: 'r', totaltext: 't', recordtext: 'rec', gototext: 'g', pagetext: 'p', oktext: 'ok' },
    limit: 20,
    limits: [10, 20, 50],
    cols: [[
      { type: 'checkbox', rowspan: 1, fixed: 'left', unresize: true },
      { type: 'numbers', rowspan: 1, fixed: 'left', unresize: true },
      { field: 'LoginName', title: 'Login', templet: { tpl: 'plain', field: 'LoginName', random: 'abc123', hasFormat: false } },
      { field: '', title: 'Op', templet: { tpl: 'actionCol' } }
    ]],
    exportFileName: 'wtTable_O2',
    enableClientExport: false,
    isInSelector: false,
    searchPanelId: 'wtForm_O2',
    fieldPre: 'Searcher',
    autoSearch: true,
    mobileLayout: true,
    done: { heightAuto: true, maxDepth: 1, multiLine: false, enableHeaderFilter: false, titleError: 'Error', titleColumnFilter: 'Filter', titlePrint: 'Print' },
    toolbarHtml: '<div id="wtTable_O2buttons"></div>',
    gridActions: [
      { event: 'ActNoId', paramType: 'noId', url: '/A/NoId', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'NoId', showInRow: false },
      { event: 'ActSingle', paramType: 'singleId', url: '/A/Single', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'Single', showInRow: false },
      { event: 'ActMulti', paramType: 'multiIds', url: '/A/Multi', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'Multi', showInRow: false },
      { event: 'ActSingleNull', paramType: 'singleIdWithNull', url: '/A/SingleNull', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'SingleNull', showInRow: false },
      { event: 'ActMultiNull', paramType: 'multiIdWithNull', url: '/A/MultiNull', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'MultiNull', showInRow: false },
      { event: 'ActAddRow', paramType: 'addRow', url: '', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'AddRow', showInRow: false, addRowJson: { ID: '00000000-0000-0000-0000-000000000000', LoginName: '' } },
      { event: 'ActRemoveRow', paramType: 'removeRow', url: '', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: true, name: 'Remove', showInRow: true },
      { event: 'ActDownload', paramType: 'noId', url: '/A/Download', download: true, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'Download', showInRow: false },
      { event: 'ActDialog', paramType: 'singleId', url: '/A/Dialog', download: false, export: false, showDialog: true, dialogWidth: 700, dialogHeight: 500, dialogTitle: 'Edit', dialogGuid: 'abc123def456abc123def456abc123d', max: true, redirect: false, forcePost: false, removeRow: false, name: 'Dialog', showInRow: false },
      { event: 'ActRedirectDialog', paramType: 'singleId', url: '/A/RedirDialog', download: false, export: false, showDialog: true, dialogTitle: 'RedirDialog', max: false, redirect: true, forcePost: false, removeRow: false, name: 'RedirDialog', showInRow: false },
      { event: 'ActExport', paramType: 'multiIdWithNull', url: '/A/Export', download: false, export: true, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'Export', showInRow: false },
      { event: 'ActRedirect', paramType: 'singleIdWithNull', url: '/A/Redirect', download: false, export: false, showDialog: false, dialogTitle: 'Redir', max: false, redirect: true, forcePost: false, removeRow: false, name: 'Redirect', showInRow: false },
      { event: 'ActForcePost', paramType: 'noId', url: '/A/ForcePost', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: true, removeRow: false, name: 'ForcePost', showInRow: false },
      { event: 'ActPlainPost', paramType: 'multiIds', url: '/A/PlainPost', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'PlainPost', showInRow: false },
      { event: 'ActOnClick', paramType: 'singleId', url: '/A/OnClick', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, onClickFn: 'myGridOnClickHandler', removeRow: false, name: 'OnClick', showInRow: false },
      { event: 'ActPrompt', paramType: 'noId', url: '/A/Prompt', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, prompt: 'Are you sure?', removeRow: false, name: 'Prompt', showInRow: false },
      { event: 'ActWhereStr', paramType: 'singleId', url: '/A/WhereStr', whereStr: ['LoginName'], download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'WhereStr', showInRow: false },
      { event: 'ActRowVisible', paramType: 'singleId', url: '/A/RowVisible', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'RowVisible', class: 'layui-btn-normal', visibleField: 'IsValid', showInRow: true }
    ],
    actionMsgs: { selectOneRow: 'select one', selectOneRowMax: 'select max one', selectOneRowMin: 'select at least one', infoTitle: 'Info' }
  };
  return Object.assign({}, base, overrides || {});
}

function dispatchGrid(ff, overrides) {
  ff.DispatchAction({ actions: [makeBaseAction(overrides)] });
}

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice O2 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('#627 kill-switch stays EXACTLY 4 gated execution points after #470 Slice O2', () => {
    const matches = active.match(/ff\._isLegacyRehydrationDisabled\(\)/g) || [];
    expect(matches).toHaveLength(4);
  });

  test('ff.gridTemplets registry now covers 7 tpl values, including actionCol', () => {
    const block = active.match(/gridTemplets:\s*\{[\s\S]*?\n\s{4}\},/)[0];
    expect(block).toMatch(/actionCol:\s*function/);
  });

  test('ff._buttonAction has toolbarButton/removeGridRow/analysisToggle entries', () => {
    const block = active.match(/window\.ff\._buttonAction\s*=\s*\{[\s\S]*?\n\};/)[0];
    expect(block).toMatch(/toolbarButton:\s*function/);
    expect(block).toMatch(/removeGridRow:\s*function/);
    expect(block).toMatch(/analysisToggle:\s*function/);
  });

  test('_gridToolDispatch never uses eval/new Function', () => {
    const block = active.match(/_gridToolDispatch:\s*function[\s\S]*?\n\s{4}\},\n\n/)[0];
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });
});

// ---------------------------------------------------------------------------
// ff._gridToolDispatch — per-ParameterType selection guards
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — _gridToolDispatch ParameterType selection guards', () => {
  test("'noId': dispatches immediately, no selection check", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActNoId', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('/A/NoId') }));
  });

  test("'singleId' with row data (obj.data set): uses data.ID directly, no selection check", () => {
    appendTable('wtTable_O2');
    const { ff, layui, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingle', { ID: 'row-1' }, {});
    expect(layui.table.checkStatus).not.toHaveBeenCalled();
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('&id=row-1') }));
  });

  test("'singleId' toolbar click (no row data): 0 selections -> selectOneRow message, no request", () => {
    appendTable('wtTable_O2');
    const { ff, layui, $ } = loadFreshFf({ layui: makeGridLayui({ table: Object.assign(makeGridLayui().table, { checkStatus: jest.fn(() => ({ data: [] })) }) }) });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingle', undefined, undefined);
    expect(layui.layer.msg).toHaveBeenCalledWith('select one');
    expect($.ajax).not.toHaveBeenCalled();
  });

  test("'singleId' toolbar click: >1 selections -> selectOneRowMax message, no request", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'a' }, { ID: 'b' }] }));
    const { ff, $ } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingle', undefined, undefined);
    expect(layui.layer.msg).toHaveBeenCalledWith('select max one');
    expect($.ajax).not.toHaveBeenCalled();
  });

  test("'singleId' toolbar click: exactly 1 selection -> proceeds with that id", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'sel-1' }] }));
    const { ff, $ } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingle', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('&id=sel-1') }));
  });

  test("'multiIds': 0 selections -> selectOneRowMin message, no request", () => {
    appendTable('wtTable_O2');
    const { ff, layui, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActMulti', undefined, undefined);
    expect(layui.layer.msg).toHaveBeenCalledWith('select at least one');
    expect($.ajax).not.toHaveBeenCalled();
  });

  test("'multiIds': >=1 selections -> proceeds (isPost path)", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'x' }] }));
    const { ff, $ } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActMulti', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ type: 'Post' }));
  });

  test("'singleIdWithNull' with null row data: falls back to selection, 0 selected is fine (no id param)", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingleNull', null, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.not.stringContaining('&id=') }));
  });

  test("'singleIdWithNull' with row data.ID set: uses it directly", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingleNull', { ID: 'row-9' }, {});
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('&id=row-9') }));
  });

  test("'singleIdWithNull': >1 selections -> selectOneRowMax, no request", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'a' }, { ID: 'b' }] }));
    const { ff, $ } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActSingleNull', undefined, undefined);
    expect(layui.layer.msg).toHaveBeenCalledWith('select max one');
    expect($.ajax).not.toHaveBeenCalled();
  });

  test("'multiIdWithNull': 0 selections still proceeds (no min-check, unlike multiIds)", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActMultiNull', undefined, undefined);
    expect($.ajax).toHaveBeenCalled();
  });

  test('an unknown gridId or event is a silent no-op (no throw)', () => {
    appendTable('wtTable_O2');
    const { ff } = loadFreshFf();
    dispatchGrid(ff);
    expect(() => ff._gridToolDispatch('nope', 'ActNoId', undefined, undefined)).not.toThrow();
    expect(() => ff._gridToolDispatch('wtTable_O2', 'NotAnEvent', undefined, undefined)).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// AddRow / Download / OpenDialog / LoadPage / BgRequest / DownloadExcelOrPdf
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — action dispatch bodies', () => {
  test("'addRow' calls ff.AddGridRow with a FRESH clone of addRowJson each time (no cross-click mutation)", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.cache.wtTable_O2 = [];
    const { ff, windowObj } = loadFreshFf({ layui });
    dispatchGrid(ff);
    windowObj.wtTable_O2option = { url: null };

    ff._gridToolDispatch('wtTable_O2', 'ActAddRow', undefined, undefined);
    ff._gridToolDispatch('wtTable_O2', 'ActAddRow', undefined, undefined);

    expect(layui.table.cache.wtTable_O2.length).toBe(2);
    // Each pushed row got its OWN freshly-generated guid ID (ff.AddGridRow's
    // own ID-reassignment) — if the SAME object were reused/mutated, both
    // rows would (at best) coincidentally differ only by the ID overwrite,
    // but a shared-reference bug would show up as identical object IDENTITY.
    expect(layui.table.cache.wtTable_O2[0]).not.toBe(layui.table.cache.wtTable_O2[1]);
    expect(layui.table.cache.wtTable_O2[0].ID).not.toBe(layui.table.cache.wtTable_O2[1].ID);
  });

  test("'removeRow' dispatched via table.on('tool') is a no-op (never reaches AddGridRow/RemoveGridRow)", () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();
    dispatchGrid(ff);
    expect(() => ff._gridToolDispatch('wtTable_O2', 'ActRemoveRow', { ID: 'x' }, {})).not.toThrow();
    expect(layui.table.render).not.toHaveBeenCalledTimes(2); // only the initial render() from dispatchGrid
  });

  test("download: false + no dialog/export/redirect/forcePost -> ff.BgRequest (plain BgRequest path)", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActNoId', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('/A/NoId'), type: 'GET' }));
  });

  test("download: true -> ff.Download (POST form submit, not $.ajax)", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    const submitSpy = jest.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
    ff._gridToolDispatch('wtTable_O2', 'ActDownload', undefined, undefined);
    expect(submitSpy).toHaveBeenCalled();
    expect($.ajax).not.toHaveBeenCalled();
    submitSpy.mockRestore();
  });

  test('showDialog + not redirect -> ff.OpenDialog with dialogGuid/width/height/title/max', () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'sel-1' }] }));
    const { ff, $ } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActDialog', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('/A/Dialog') }));
    // ff.OpenDialog reads/writes the windowids cookie via $.cookie — proves the
    // real ff.OpenDialog ran (not a stand-in), same pattern sliceM's tests use.
    expect($.cookie).toHaveBeenCalled();
  });

  test('showDialog + redirect -> ff.LoadPage(url, true, title, ...) (newwindow=true)', () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'sel-1' }] }));
    const { ff, windowObj } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActRedirectDialog', undefined, undefined);
    expect(windowObj.open).toHaveBeenCalledWith(expect.stringContaining('/A/RedirDialog'));
  });

  test('export -> ff.DownloadExcelOrPdf (blob xhrFields, formId from actionMsgs.searchPanelId)', () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActExport', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({
      url: expect.stringContaining('/A/Export'),
      xhrFields: { responseType: 'blob' }
    }));
  });

  test('redirect (no dialog) -> ff.LoadPage(url, false, title, ...) (newwindow=false)', () => {
    appendTable('wtTable_O2');
    const { ff, windowObj } = loadFreshFf();
    dispatchGrid(ff);
    // newwindow=false takes the location.hash path, not window.open.
    ff._gridToolDispatch('wtTable_O2', 'ActRedirect', { ID: 'r1' }, {});
    expect(windowObj.location.hash).toContain('/A/Redirect');
  });

  test('forcePost -> ff.BgRequest with unconditional {Ids:ids} (even with ids undefined -> undefined para is fine)', () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActForcePost', undefined, undefined);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ type: 'GET', url: expect.stringContaining('/A/ForcePost') }));
  });

  test('plain multiIds BgRequest -> POST with {Ids:ids} once selection passes', () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'p1' }] }));
    const { ff, $ } = loadFreshFf({ layui });
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActPlainPost', undefined, undefined);
    // ff.BgRequest's internal getpost flag is the literal string 'Post'
    // (mixed-case — see BgRequest's `getpost = "Post"` when para !== undefined),
    // not the uppercase HTTP-method spelling.
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ type: 'Post', data: { Ids: ['p1'] } }));
  });

  test('whereStr appends &field=value from the resolved row/selection data', () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActWhereStr', { ID: 'w1', LoginName: 'alice' }, {});
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('&LoginName=alice') }));
  });
});

// ---------------------------------------------------------------------------
// Guarded OnClickFunc + PromptMessage confirm
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — guarded OnClickFunc / PromptMessage confirm', () => {
  test('onClickFn resolves via ff._resolveGuardedWindowFn and is called with (ids, selectionData)', () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.checkStatus = jest.fn(() => ({ data: [{ ID: 'oc-1' }] }));
    const { ff, windowObj } = loadFreshFf({ layui });
    const handler = jest.fn();
    windowObj.myGridOnClickHandler = handler;
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActOnClick', undefined, undefined);
    expect(handler).toHaveBeenCalledWith(['oc-1'], [{ ID: 'oc-1' }]);
  });

  test('onClickFn that does not resolve to a real global function is silently skipped (no throw, no request)', () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff, {
      gridActions: [{ event: 'ActBadFn', paramType: 'noId', url: '/A/Bad', onClickFn: 'thisDoesNotExist', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'Bad', showInRow: false }]
    });
    expect(() => ff._gridToolDispatch('wtTable_O2', 'ActBadFn', undefined, undefined)).not.toThrow();
    expect($.ajax).not.toHaveBeenCalled();
  });

  test('onClickFn on the eval/Function denylist is refused (defense in depth)', () => {
    appendTable('wtTable_O2');
    const { ff, windowObj } = loadFreshFf();
    windowObj.eval = jest.fn();
    dispatchGrid(ff, {
      gridActions: [{ event: 'ActEvilFn', paramType: 'noId', url: '/A/Evil', onClickFn: 'eval', download: false, export: false, showDialog: false, max: false, redirect: false, forcePost: false, removeRow: false, name: 'Evil', showInRow: false }]
    });
    ff._gridToolDispatch('wtTable_O2', 'ActEvilFn', undefined, undefined);
    expect(windowObj.eval).not.toHaveBeenCalled();
  });

  test('prompt wraps the action in layer.confirm — action only runs after confirm callback fires', () => {
    appendTable('wtTable_O2');
    const { ff, layui, $ } = loadFreshFf();
    dispatchGrid(ff);
    ff._gridToolDispatch('wtTable_O2', 'ActPrompt', undefined, undefined);

    expect(layui.layer.confirm).toHaveBeenCalledWith('Are you sure?', { title: 'Info' }, expect.any(Function));
    expect($.ajax).not.toHaveBeenCalled(); // not yet — confirm callback hasn't fired

    const confirmCb = layui.layer.confirm.mock.calls[0][2];
    confirmCb(7);
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('/A/Prompt') }));
    expect(layui.layer.close).toHaveBeenCalledWith(7);
  });
});

// ---------------------------------------------------------------------------
// ff.gridTemplets.actionCol — row-action anchor registry parity vs legacy laytpl
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — ff.gridTemplets.actionCol registry parity', () => {
  const XSS_SCRIPT = '<script>alert(document.cookie)<\/script>';
  const XSS_ATTR_BREAKOUT = '" onmouseover="alert(1)';

  function renderInto(html) {
    const container = document.createElement('div');
    container.innerHTML = html;
    return container;
  }

  test('non-removeRow row action renders a lay-event anchor (layui tool-dispatch surface)', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActEdit', name: 'Edit', class: 'layui-btn-normal' }]
    });
    const html = templet({ LAY_INDEX: 1 });
    const container = renderInto(html);
    const a = container.querySelector('a');
    expect(a.getAttribute('lay-event')).toBe('ActEdit');
    expect(a.getAttribute('data-wtm-click')).toBeNull();
    expect(a.className).toContain('layui-btn-normal');
    expect(a.textContent).toBe('Edit');
  });

  test('removeRow entry renders data-wtm-click="removeGridRow" with the real per-row LAY_INDEX, no lay-event', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActRemoveRow', name: 'Del', removeRow: true }]
    });
    const html = templet({ LAY_INDEX: 4 });
    const container = renderInto(html);
    const a = container.querySelector('a');
    expect(a.getAttribute('data-wtm-click')).toBe('removeGridRow');
    expect(a.getAttribute('data-wtm-grid')).toBe('wtTable_O2');
    expect(a.getAttribute('data-wtm-row-index')).toBe('4');
    expect(a.getAttribute('lay-event')).toBeNull();
  });

  test('conditional visibility (visibleField): true/"true"/"True" show the button, everything else hides it — laytpl {{# if }} parity', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActCond', name: 'Cond', visibleField: 'IsValid' }]
    });
    expect(templet({ LAY_INDEX: 0, IsValid: true })).toContain('lay-event="ActCond"');
    expect(templet({ LAY_INDEX: 0, IsValid: 'true' })).toContain('lay-event="ActCond"');
    expect(templet({ LAY_INDEX: 0, IsValid: 'True' })).toContain('lay-event="ActCond"');
    expect(templet({ LAY_INDEX: 0, IsValid: false })).toBe('');
    expect(templet({ LAY_INDEX: 0, IsValid: 'false' })).toBe('');
    expect(templet({ LAY_INDEX: 0, IsValid: undefined })).toBe('');
    expect(templet({ LAY_INDEX: 0 })).toBe('');
  });

  test('conditional visibility uses LOOSE (==) equality, matching the retired legacy laytpl `d.{field} == true` conditional — a numeric 1 must show (same fidelity class as Slices N1/O1)', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActLoose', name: 'Loose', visibleField: 'IsValid' }]
    });
    // Numeric 1 is loosely `1 == true` (true) but not strictly `1 === true`
    // / `1 === 'true'` / `1 === 'True'` (all false) — this is the exact
    // strict-vs-loose divergence that made a strict `===` a regression vs
    // the retired laytpl conditional (DataTableTagHelper.cs AddSubButton,
    // line ~1062: `d.{field} == true || d.{field} == 'true' || d.{field} ==
    // 'True'`). Reverting the production code's `==` back to `===` makes
    // this assertion fail (the button would be hidden instead of shown).
    expect(templet({ LAY_INDEX: 0, IsValid: 1 })).toContain('lay-event="ActLoose"');
    // null must still hide (null == true/'true'/'True' are all false too,
    // same as legacy) — included so the loose-eq fix isn't mistaken for an
    // "always show" regression.
    expect(templet({ LAY_INDEX: 0, IsValid: null })).toBe('');
  });

  test('removeRow entries ignore visibleField (always shown, matching AddSubButton\'s unconditional RemoveRow anchor)', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActRemoveRow', name: 'Del', removeRow: true, visibleField: 'NeverSetByServer' }]
    });
    // Server-side AddIslandActionDescriptor never sets visibleField for
    // removeRow entries (always null) — but even if it somehow were set, the
    // registry builder's removeRow branch does not consult it at all.
    const html = templet({ LAY_INDEX: 0, NeverSetByServer: false });
    expect(html).toContain('data-wtm-click="removeGridRow"');
  });

  test('multiple row actions render in declaration order, each independently gated by its own visibleField', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [
        { event: 'First', name: 'First' },
        { event: 'Second', name: 'Second', visibleField: 'ShowSecond' },
        { event: 'ActRemoveRow', name: 'Del', removeRow: true }
      ]
    });
    const html = templet({ LAY_INDEX: 2, ShowSecond: false });
    const container = renderInto(html);
    const anchors = Array.from(container.querySelectorAll('a'));
    expect(anchors.map((a) => a.textContent)).toEqual(['First', 'Del']);
  });

  test('row action name is HTML-escaped (#108-style XSS guard applied to descriptor.name too)', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActX', name: XSS_SCRIPT }]
    });
    const html = templet({ LAY_INDEX: 0 });
    const container = renderInto(html);
    expect(container.querySelectorAll('script').length).toBe(0);
    expect(container.textContent).toContain('script');
  });

  test('row action class is attribute-escaped (breakout payload neutralised)', () => {
    const { ff } = loadFreshFf();
    const templet = ff.gridTemplets.actionCol({
      gridId: 'wtTable_O2',
      rowActions: [{ event: 'ActX', name: 'X', class: XSS_ATTR_BREAKOUT }]
    });
    const html = templet({ LAY_INDEX: 0 });
    const container = renderInto(html);
    const a = container.querySelector('a');
    expect(a.getAttribute('onmouseover')).toBeNull();
  });

  test('empty/absent rowActions renders an empty string (no throw)', () => {
    const { ff } = loadFreshFf();
    expect(ff.gridTemplets.actionCol({ gridId: 'g' })({ LAY_INDEX: 0 })).toBe('');
    expect(ff.gridTemplets.actionCol(undefined)({ LAY_INDEX: 0 })).toBe('');
  });
});

// ---------------------------------------------------------------------------
// cols rebuild: 'actionCol' tpl is special-cased with gridId + showInRow filter
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — cols rebuild wires actionCol from action.gridActions', () => {
  test('the actionCol column templet is rebuilt into a function fed by the SHOWINROW-filtered action list', () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();
    dispatchGrid(ff);

    const opt = layui.table.render.mock.calls[0][0];
    const actionCol = opt.cols[0][3];
    expect(typeof actionCol.templet).toBe('function');

    const html = actionCol.templet({ LAY_INDEX: 3, IsValid: true });
    const container = document.createElement('div');
    container.innerHTML = html;
    const anchors = Array.from(container.querySelectorAll('a'));
    // Only showInRow===true entries from makeBaseAction(): ActRemoveRow + ActRowVisible.
    expect(anchors.length).toBe(2);
    expect(anchors.some((a) => a.getAttribute('data-wtm-click') === 'removeGridRow')).toBe(true);
    expect(anchors.some((a) => a.getAttribute('lay-event') === 'ActRowVisible')).toBe(true);
  });

  test('toolbar-only actions (showInRow false) never appear in the actionCol row output', () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();
    dispatchGrid(ff);
    const opt = layui.table.render.mock.calls[0][0];
    const html = opt.cols[0][3].templet({ LAY_INDEX: 0, IsValid: true });
    expect(html).not.toContain('lay-event="ActSingle"');
    expect(html).not.toContain('lay-event="ActDialog"');
  });
});

// ---------------------------------------------------------------------------
// table.on('tool') wiring routes through ff._gridToolDispatch when gridActions[] present
// ---------------------------------------------------------------------------
describe("#470 Slice O2 — table.on('tool') registration", () => {
  test('gridActions[] present -> tool(gridId) wired to a wrapper calling ff._gridToolDispatch, registry populated', () => {
    appendTable('wtTable_O2');
    const { ff, layui, $ } = loadFreshFf();
    dispatchGrid(ff);

    const toolCall = layui.table.on.mock.calls.find((c) => c[0] === 'tool(wtTable_O2)');
    expect(toolCall).toBeDefined();
    toolCall[1]({ event: 'ActNoId', data: undefined, tr: undefined });
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('/A/NoId') }));
  });

  test('gridActions[] absent -> falls back to window["wtToolBarFunc_"+gridId] when present (schema/back-compat)', () => {
    appendTable('wtTable_O2');
    const { ff, layui, windowObj } = loadFreshFf();
    const toolFn = jest.fn();
    windowObj.wtToolBarFunc_wtTable_O2 = toolFn;
    dispatchGrid(ff, { gridActions: undefined, toolbarHtml: undefined });

    const toolCall = layui.table.on.mock.calls.find((c) => c[0] === 'tool(wtTable_O2)');
    expect(toolCall[1]).toBe(toolFn);
  });

  test('gridActions[] absent and no legacy global -> tool is simply not wired (no throw)', () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();
    expect(() => dispatchGrid(ff, { gridActions: undefined, toolbarHtml: undefined })).not.toThrow();
    const toolCall = layui.table.on.mock.calls.find((c) => c[0] === 'tool(wtTable_O2)');
    expect(toolCall).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// toolbarHtml assignment — literal HTML string, never a selector
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — toolbarHtml assigned as literal opt.toolbar', () => {
  test('toolbarHtml is wrapped in one extra <div> before assignment to opt.toolbar', () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();
    dispatchGrid(ff, { toolbarHtml: '<div id="wtTable_O2buttons"><a>X</a></div>' });
    const opt = layui.table.render.mock.calls[0][0];
    expect(opt.toolbar).toBe('<div><div id="wtTable_O2buttons"><a>X</a></div></div>');
  });

  test('when toolbarHtml is absent, the legacy `toolbar` selector field is still honored (forward/back-compat)', () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();
    dispatchGrid(ff, { toolbarHtml: undefined, gridActions: undefined, toolbar: '#wtToolBar_wtTable_O22' });
    const opt = layui.table.render.mock.calls[0][0];
    expect(opt.toolbar).toBe('#wtToolBar_wtTable_O22');
  });
});

// ---------------------------------------------------------------------------
// Delegated data-wtm-click routing — toolbarButton / removeGridRow / analysisToggle
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — delegated data-wtm-click routing', () => {
  function appendClickable(attrs) {
    const el = document.createElement('a');
    Object.keys(attrs).forEach((k) => el.setAttribute(k, attrs[k]));
    document.body.appendChild(el);
    return el;
  }

  // This one test dispatches a REAL click through the document-level
  // delegated listener to prove the WIRING itself (data-wtm-click ->
  // ff._buttonAction[value] -> handler) works end-to-end — safe to do because
  // ff._gridToolDispatch (which toolbarButton delegates straight into) has
  // its own top-level try/catch, so any OTHER still-registered listener from
  // an earlier test in this file (document/`document.addEventListener` is
  // shared/accumulates across tests in the same file — see the NOTE below)
  // that also happens to match 'wtTable_O2'/'ActNoId' cannot propagate an
  // exception into this test.
  test("data-wtm-click='toolbarButton' routes to ff._gridToolDispatch(gridId, event, undefined, undefined)", () => {
    appendTable('wtTable_O2');
    const { ff, $ } = loadFreshFf();
    dispatchGrid(ff);
    const el = appendClickable({ 'data-wtm-click': 'toolbarButton', 'data-wtm-grid': 'wtTable_O2', 'data-wtm-event': 'ActNoId' });
    el.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
    expect($.ajax).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('/A/NoId') }));
  });

  // NOTE: the remaining tests below call ff._buttonAction.X(el) DIRECTLY
  // (the EXACT function the delegated click listener invokes — see the
  // 'toolbarButton' case above for click-dispatch coverage of that wiring)
  // rather than dispatching a real click event. Reason (same rationale as
  // framework_layui_470_sliceM_buttons.test.js's own 'loadPage' cases):
  // `document` is shared/accumulates a NEW click listener on every
  // loadFreshFf() call in this same test file, each bound to THAT test's own
  // ff/layui closure. ff.RemoveGridRow (unlike ff._gridToolDispatch) has NO
  // internal try/catch — an earlier test's stale listener calling it against
  // a `layui.table.cache` that never got THIS test's gridId key would throw
  // and fail the CURRENT test's dispatchEvent call. Calling the dispatch-map
  // function directly sidesteps that cross-test listener accumulation
  // entirely while still exercising the real production dispatch code.
  test("data-wtm-click='removeGridRow' calls ff.RemoveGridRow(gridId, window[gridId+'option'], rowIndex)", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.cache.wtTable_O2 = [{ ID: '1' }, { ID: '2' }, { ID: '3' }];
    const { ff, windowObj } = loadFreshFf({ layui });
    windowObj.wtTable_O2option = { url: null };
    const el = appendClickable({ 'data-wtm-click': 'removeGridRow', 'data-wtm-grid': 'wtTable_O2', 'data-wtm-row-index': '2' });
    ff._buttonAction.removeGridRow(el);
    expect(layui.table.cache.wtTable_O2.length).toBe(2);
  });

  test("data-wtm-click='removeGridRow' with a non-numeric row index is a safe no-op", () => {
    appendTable('wtTable_O2');
    const layui = makeGridLayui();
    layui.table.cache.wtTable_O2 = [{ ID: '1' }];
    const { ff } = loadFreshFf({ layui });
    const el = appendClickable({ 'data-wtm-click': 'removeGridRow', 'data-wtm-grid': 'wtTable_O2', 'data-wtm-row-index': 'notanumber' });
    expect(() => ff._buttonAction.removeGridRow(el)).not.toThrow();
    expect(layui.table.cache.wtTable_O2.length).toBe(1);
  });

  test("data-wtm-click='analysisToggle' calls wtmAnalysis.toggle(gridId, vmName) when wtmAnalysis is present", () => {
    appendTable('wtTable_O2');
    const { ff, windowObj } = loadFreshFf();
    const toggleSpy = jest.fn();
    windowObj.wtmAnalysis = { toggle: toggleSpy };
    const el = appendClickable({ 'data-wtm-click': 'analysisToggle', 'data-wtm-grid': 'wtTable_O2', 'data-wtm-vmname': 'My.Vm.FullName' });
    ff._buttonAction.analysisToggle(el);
    expect(toggleSpy).toHaveBeenCalledWith('wtTable_O2', 'My.Vm.FullName');
  });

  test("data-wtm-click='analysisToggle' is a safe no-op when wtmAnalysis is undefined", () => {
    appendTable('wtTable_O2');
    const { ff } = loadFreshFf();
    const el = appendClickable({ 'data-wtm-click': 'analysisToggle', 'data-wtm-grid': 'wtTable_O2', 'data-wtm-vmname': 'X' });
    expect(() => ff._buttonAction.analysisToggle(el)).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// Button-group one-time delegated binding — scoped, no double-bind with a
// coexisting legacy grid's own unscoped ".downpanel" per-render script
// ---------------------------------------------------------------------------
describe('#470 Slice O2 — button-group delegated open/close binding', () => {
  test("scoped selector is '[data-wtm-btngroup] .layui-select-title' — deliberately NOT '.downpanel[...]' (review polish: a coexisting legacy-fallback grid's own unscoped '.downpanel' per-render script would otherwise be able to bind onto an island button that still carried that class — see the comment above the binding)", () => {
    expect(active).toMatch(/\[data-wtm-btngroup\]\s+\.layui-select-title/);
    // The class name "downpanel" must not appear ANYWHERE in the active
    // (non-legacy-fixture) source any more — not in the selector text, and
    // not smuggled back in some other way.
    expect(active).not.toMatch(/downpanel/);
  });

  test('the delegated binding is registered via jQuery .on, gated on $.fn existing (registered once, unconditionally)', () => {
    // Anchor on the unique '[data-wtm-btngroup]' selector text (rather than
    // the generic `if (typeof $ === 'function' && $.fn...)` guard, which
    // ALSO wraps Slice N2's unrelated SearchPanel binding earlier in the
    // file) to isolate JUST this O2 block from the COMMENT-STRIPPED `active`
    // source.
    const idx = active.indexOf("[data-wtm-btngroup] .layui-select-title");
    expect(idx).toBeGreaterThan(-1);
    const blockStart = active.lastIndexOf('if (typeof $', idx);
    const blockEnd = active.indexOf('\n}\n', idx);
    const block = active.slice(blockStart, blockEnd);
    expect(block).toMatch(/\$\(document\)\.on\('click', '\[data-wtm-btngroup\] \.layui-select-title'/);
    expect(block).toMatch(/\$\(document\)\.on\('click', function \(event\)/);
  });
});

// ---------------------------------------------------------------------------
// CRITICAL FIX REGRESSION TEST (review-caught, real-browser confirmed): drive
// the REAL page-ready entry point instead of the bypass every OTHER test in
// this file uses.
//
// Every test above dispatches via dispatchGrid(), which calls
// `ff.DispatchAction({ actions: [makeBaseAction(overrides)] })` DIRECTLY —
// i.e. it hand-constructs the ALREADY-NORMALIZED batch shape and skips
// ff._normalizeIslandPayload entirely. That bypass is exactly why the
// original shipped O2 payload — which named its own row/toolbar descriptor
// array `actions` — went undetected by all 3 static/jsdom reviewers: the
// renderGrid island's OWN `actions[]` field collided with
// ff._normalizeIslandPayload's `Array.isArray(parsed.actions)` probe (added
// to recognize the pre-existing BATCH island shape {"actions":[{type},...]}
// from DialogInitTagHelper/FormTagHelper), so a REAL page-ready dispatch of
// a renderGrid island silently misclassified the whole payload as an
// already-batched one and never reached `case 'renderGrid'` — the grid never
// rendered. In-browser verification on /City/Index (flag ON, CityListVM, 10
// GridActions) confirmed `layui.table.cache` stayed `{}`. This block drives
// the ACTUAL functions the shipped bundle calls when consuming a
// `<script type="application/json" class="wtm-dialog-init">` island at
// page-ready — ff._consumePageReadyIslands -> JSON.parse ->
// ff._normalizeIslandPayload -> ff._dispatchIslandWhenReady ->
// ff.DispatchAction -> case 'renderGrid' -> ff._renderGridAction — so this
// regression (or its reintroduction, e.g. by renaming `gridActions` back to
// `actions`) can never again slip past the JS test suite the way it did
// before real-browser verification caught it.
describe('#470 Slice O2 CRITICAL FIX — real page-ready entry point (_normalizeIslandPayload -> DispatchAction)', () => {
  function appendPageReadyIsland(payload) {
    const script = document.createElement('script');
    script.type = 'application/json';
    script.className = 'wtm-dialog-init';
    script.textContent = JSON.stringify(payload);
    document.body.appendChild(script);
    return script;
  }

  test('a renderGrid island with a non-empty gridActions[] array renders the grid (layui.table.render called exactly once) through the real _consumePageReadyIslands -> _normalizeIslandPayload -> DispatchAction chain', () => {
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();

    // The RAW server payload — a BARE single-action object, exactly what
    // DataTableTagHelper.Island.cs's BuildTableIslandScript serializes into
    // the <script type="application/json" class="wtm-dialog-init"> tag.
    // Deliberately NOT pre-wrapped in {actions:[...]} — performing that wrap
    // is ff._normalizeIslandPayload's OWN job, which is exactly the step
    // every other test in this file (via dispatchGrid ->
    // ff.DispatchAction({actions:[...]})) skips past.
    appendPageReadyIsland(makeBaseAction());

    // The REAL page-ready entry point — the exact call the shipped bundle
    // makes at DOMContentLoaded (or immediately, if the document is already
    // past 'loading'). loadFreshFf's own top-level script load already
    // triggered one no-op call before this island existed (idempotent —
    // see ff._consumePageReadyIslands's own comment); this second, manual
    // call is what actually picks up and dispatches the island appended
    // above.
    ff._consumePageReadyIslands();

    expect(layui.table.render).toHaveBeenCalledTimes(1);
    const opt = layui.table.render.mock.calls[0][0];
    expect(opt.elem).toBe('#wtTable_O2');

    // Row/toolbar dispatch wiring also proves through end-to-end: the
    // registry got built and table.on('tool', ...) got registered — both
    // gated on the SAME action.gridActions field this fix renamed.
    expect(ff._gridActionRegistry.wtTable_O2).toBeDefined();
    const toolCall = layui.table.on.mock.calls.find((c) => c[0] === 'tool(wtTable_O2)');
    expect(toolCall).toBeDefined();
  });

  test('sanity: a payload using the OLD colliding "actions" field name (pre-fix shape) reproduces the exact shipped regression — layui.table.render is NEVER called', () => {
    // This test pins the FAILURE MODE itself (not the fix), independent of
    // whatever the C# serializer currently emits — proving the collision
    // ff._normalizeIslandPayload has with a field literally named `actions`
    // is real, so nobody re-adds a same-named field to a bare-dispatched
    // island type in the future without re-discovering this the hard way.
    // Do NOT "fix" this test by renaming the field to gridActions — that
    // would defeat its entire purpose.
    appendTable('wtTable_O2');
    const { ff, layui } = loadFreshFf();

    const badPayload = makeBaseAction();
    badPayload.actions = badPayload.gridActions;
    delete badPayload.gridActions;
    appendPageReadyIsland(badPayload);

    ff._consumePageReadyIslands();

    expect(layui.table.render).not.toHaveBeenCalled();
  });
});
