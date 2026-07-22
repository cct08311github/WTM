// Tests for Issue #470 Slice O3 — LAST grid slice: opt-in
// (WtmUIOptions.UseSelectIslandRender, default OFF — the SAME flag #470
// Slices J-O2 use) eval-free grid `localData` island + delegated cell-change
// dispatch + `foldPanel` island, completing the grid islandification campaign
// (comment 18118 §2 O3).
//
// This file drives the REAL ff._consumePageReadyIslands / _normalizeIslandPayload
// / DispatchAction / ff._renderGridAction / ff.LoadLocalData / ff.AddGridRow /
// ff.RemoveGridRow / ff.SetGridCellDate / ff.gridcellchange / ff.grid.isIsland /
// ff.grid.state / ff._renderFoldPanelAction end-to-end against a FRESH vm
// instance of the actual shipped framework_layui.js source (real jsdom
// `document`) — not a source-sweep-only or a hand-rolled reimplementation —
// following the same convention as framework_layui_470_sliceO2_grid_toolbar.test.js.
// layui.table/layui.element are stubbed (real layui.use(...) modules in
// production, never loaded under jsdom); ff.gridcellchange/ff.AddGridRow/
// ff.RemoveGridRow/ff.LoadLocalData/ff.SetGridCellDate are the REAL framework
// functions (only their layui/$ dependencies are stubbed).

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
// Harness — same shape as framework_layui_470_sliceO2_grid_toolbar.test.js
// ---------------------------------------------------------------------------

function makeJQueryMock() {
  function wrap(el) {
    const w = {
      find: jest.fn(() => wrap(el)),
      css: jest.fn(() => w),
      attr: jest.fn(() => (el && typeof el.getAttribute === 'function' ? el.getAttribute('lay-filter') : undefined)),
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
      // Supports simple '#id' and '#id .class' forms (foldPanel's
      // '#{searchPanelId} .layui-collapse' lookup) — strips a trailing
      // ' .layui-collapse' descendant selector and queries the DOM for real.
      var idPart = selector.split(' ')[0].slice(1);
      var rest = selector.slice(('#' + idPart).length).trim();
      var base = document.getElementById(idPart);
      if (!rest) { return wrap(base); }
      var found = base ? base.querySelector(rest) : null;
      return wrap(found);
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
    element: {
      init: jest.fn(),
      on: jest.fn(),
      fold: jest.fn()
    },
    laydate: {
      render: jest.fn()
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
    layer: layui.layer,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    DOMParser: global.DOMParser,
    CustomEvent: global.CustomEvent,
    Event: global.Event,
    location: { hash: '' },
    open: jest.fn(() => ({ document: { ready: jest.fn() } })),
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  if (typeof $.ajax.mockClear === 'function') { $.ajax.mockClear(); }
  return { ff: ctx.ff, windowObj: ctx, layui, $ };
}

function appendTable(id, island) {
  const table = document.createElement('table');
  table.id = id;
  if (island) { table.setAttribute('data-wtm-grid-id', id); }
  document.body.appendChild(table);
  return table;
}

// Mirrors the REAL DOM shape layui's table module produces at render time
// (verified against the vendored layui.js: `Init()` does
// `a.id = a.id || a.elem.attr('id')`, then `e.elem = ...render({VIEW_CLASS,
// data: a, ...})` with the view template's root carrying
// `lay-id="{{ d.data.id }}"`, followed by `l.after(o)` — the view is
// inserted as a SIBLING of the original `<table id={gridid}>`, and the
// rendered cell lives INSIDE that view, never inside the original table
// element itself). The delegated cellchange listener's containment guard
// (#470 Slice O3 review polish) checks against exactly this shape, so tests
// that exercise the guard construct it directly rather than the bare
// `document.body.appendChild(cell)` earlier O3 tests used.
function appendTableView(gridid, cellEl) {
  const view = document.createElement('div');
  view.className = 'layui-table-view';
  view.setAttribute('lay-id', gridid);
  view.appendChild(cellEl);
  document.body.appendChild(view);
  return view;
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

// A representative renderGrid action carrying `localData` — shaped like
// DataTableTagHelper.Island.cs's BuildRenderGridAction would emit for a
// UseLocalData=true grid (mirrors RenderGridLocalData470SliceO3Tests.cs's own
// fixture shape on the C# side).
function makeLocalDataAction(overrides) {
  const base = {
    type: 'renderGrid',
    gridId: 'wtTable_O3',
    tableJsVar: 'wtVar_wtTable_O3',
    elem: '#wtTable_O3',
    id: 'wtTable_O3',
    text: { none: 'No Data' },
    request: { pageName: 'Page', limitName: 'Limit' },
    defaultToolbar: [],
    totalRow: false,
    method: 'post',
    limit: 2,
    cols: [[
      { type: 'checkbox', rowspan: 1, fixed: 'left', unresize: true },
      { type: 'numbers', rowspan: 1, fixed: 'left', unresize: true },
      { field: 'LoginName', title: 'Login', templet: { tpl: 'plain', field: 'LoginName', random: 'abc123', hasFormat: false } }
    ]],
    exportFileName: 'wtTable_O3',
    enableClientExport: false,
    isInSelector: false,
    searchPanelId: 'wtForm_O3',
    fieldPre: 'Searcher',
    autoSearch: true,
    mobileLayout: false,
    done: { heightAuto: true, maxDepth: 1, multiLine: false, enableHeaderFilter: false, titleError: 'Error', titleColumnFilter: 'Filter', titlePrint: 'Print' },
    localData: [
      { ID: 'row-1', LoginName: 's1' },
      { ID: 'row-2', LoginName: 's2' }
    ]
  };
  return Object.assign({}, base, overrides || {});
}

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice O3 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('#627 kill-switch stays EXACTLY 4 gated execution points after #470 Slice O3', () => {
    const matches = active.match(/ff\._isLegacyRehydrationDisabled\(\)/g) || [];
    expect(matches).toHaveLength(4);
  });

  test('ff.grid.state/isIsland accessor exists', () => {
    const block = active.match(/grid:\s*\{[\s\S]*?\n\s{4}\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/state:\s*function\s*\(gridId\)/);
    expect(block[0]).toMatch(/isIsland:\s*function\s*\(gridId\)/);
    expect(block[0]).toMatch(/data-wtm-grid-id/);
  });

  test('_islandModulesFor maps foldPanel -> the element module', () => {
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,3300}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]foldPanel['"][\s\S]{0,60}needed\.element\s*=\s*true/);
  });
});

// ---------------------------------------------------------------------------
// (a) MANDATORY real-entry-point test — the O2-collision-guard pattern,
// applied to localData: proves it does NOT re-trigger
// ff._normalizeIslandPayload's Array.isArray(parsed.actions) batch-shape probe.
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — real page-ready entry point (localData does not collide with the O2 batch-heuristic)', () => {
  test('a renderGrid island WITH localData, dispatched through the REAL _consumePageReadyIslands -> _normalizeIslandPayload -> DispatchAction chain, loads local data (layui.table.render called TWICE: shell render + ff.LoadLocalData render)', () => {
    appendTable('wtTable_O3', true);
    const { ff, layui } = loadFreshFf();

    // The RAW server payload — a BARE single-action object, exactly what
    // DataTableTagHelper.Island.cs's BuildTableIslandScript serializes.
    // Deliberately NOT pre-wrapped in {actions:[...]} — that wrap is
    // ff._normalizeIslandPayload's OWN job.
    appendPageReadyIsland(makeLocalDataAction());

    ff._consumePageReadyIslands();

    // First render: the "shell" call ff._renderGridAction always makes
    // (table.render(opt), no data/url yet). Second render: ff.LoadLocalData's
    // own internal layui.table.render(option) call, WITH the local rows.
    expect(layui.table.render).toHaveBeenCalledTimes(2);
    const secondCallOpt = layui.table.render.mock.calls[1][0];
    expect(secondCallOpt.data).toHaveLength(2);
    expect(secondCallOpt.data[0].LoginName).toBe('s1');
    expect(secondCallOpt.limit).toBe(9999);
    expect(secondCallOpt.url).toBeNull();

    // Proves the island reached case 'renderGrid' at all (would be silently
    // skipped if localData collided with the batch-shape probe the same way
    // the original `actions` naming did in O2).
    expect(ff.grid.state('wtTable_O3').option).toBeDefined();
  });

  test('sanity: renaming localData to "actions" reproduces the O2-class collision — layui.table.render is NEVER called', () => {
    // Pins the FAILURE MODE itself, independent of what the C# serializer
    // currently emits — proving the naming choice matters, so nobody
    // "simplifies" this field back to a colliding name later. Do NOT fix by
    // renaming — this test documents why the name must stay `localData`.
    appendTable('wtTable_O3', true);
    const { ff, layui } = loadFreshFf();

    const badPayload = makeLocalDataAction();
    badPayload.actions = badPayload.localData;
    delete badPayload.localData;
    appendPageReadyIsland(badPayload);

    ff._consumePageReadyIslands();

    expect(layui.table.render).not.toHaveBeenCalled();
  });

  test('the whole renderGrid+localData payload is never a bare top-level array', () => {
    const payload = makeLocalDataAction();
    expect(Array.isArray(payload)).toBe(false);
    expect(Array.isArray(payload.localData)).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// (b) Delegated cellchange listener parity vs. the direct ff.gridcellchange
// call the legacy onchange attribute used to make.
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — delegated data-wtm-cellchange listener parity', () => {
  function freshCache(cellHtml) {
    return [{ LoginName: cellHtml }];
  }

  test('input cell (celltype=0): delegated change listener produces the SAME cache mutation as a direct ff.gridcellchange call', () => {
    const { ff, layui } = loadFreshFf();
    const initialHtml = "<input value='old' />";

    // Path 1: direct call (the legacy onchange attribute's exact invocation shape).
    layui.table.cache.wtTable_Direct = freshCache(initialHtml);
    ff.gridcellchange({ value: 'newval' }, 'wtTable_Direct', 0, 'LoginName', 0);
    const directResult = layui.table.cache.wtTable_Direct[0].LoginName;

    // Path 2: the REAL delegated document-level 'change' listener, driven by
    // a genuine DOM event dispatched on an element carrying the
    // data-wtm-cellchange-* attributes ff.AddGridRow/ff.LoadLocalData stamp,
    // placed inside the REAL rendered `.layui-table-view[lay-id=...]` shape
    // the containment guard (review polish) requires.
    layui.table.cache.wtTable_Delegated = freshCache(initialHtml);
    const input = document.createElement('input');
    input.value = 'newval';
    input.setAttribute('data-wtm-cellchange', '1');
    input.setAttribute('data-wtm-cellchange-grid', 'wtTable_Delegated');
    input.setAttribute('data-wtm-cellchange-row', '0');
    input.setAttribute('data-wtm-cellchange-col', 'LoginName');
    input.setAttribute('data-wtm-cellchange-celltype', '0');
    appendTableView('wtTable_Delegated', input);
    input.dispatchEvent(new Event('change', { bubbles: true }));
    const delegatedResult = layui.table.cache.wtTable_Delegated[0].LoginName;

    expect(delegatedResult).toBe(directResult);
    expect(delegatedResult).toContain("value='newval'");
  });

  test('select cell (celltype=1): delegated change listener produces the SAME cache mutation as a direct ff.gridcellchange call', () => {
    const { ff, layui } = loadFreshFf();
    const initialHtml = "<select><option value='a' selected>A</option><option value='b'>B</option></select>";

    layui.table.cache.wtTable_Direct2 = freshCache(initialHtml);
    ff.gridcellchange({ value: 'b' }, 'wtTable_Direct2', 0, 'LoginName', 1);
    const directResult = layui.table.cache.wtTable_Direct2[0].LoginName;

    layui.table.cache.wtTable_Delegated2 = freshCache(initialHtml);
    const select = document.createElement('select');
    select.setAttribute('data-wtm-cellchange', '1');
    select.setAttribute('data-wtm-cellchange-grid', 'wtTable_Delegated2');
    select.setAttribute('data-wtm-cellchange-row', '0');
    select.setAttribute('data-wtm-cellchange-col', 'LoginName');
    select.setAttribute('data-wtm-cellchange-celltype', '1');
    appendTableView('wtTable_Delegated2', select);
    // jsdom's <select>.value assignment without matching <option> children is
    // a no-op for selection state, but the delegated listener only reads
    // el.value (matching legacy's `ele.value`) — set it directly.
    Object.defineProperty(select, 'value', { value: 'b', writable: true });
    select.dispatchEvent(new Event('change', { bubbles: true }));
    const delegatedResult = layui.table.cache.wtTable_Delegated2[0].LoginName;

    expect(delegatedResult).toBe(directResult);
    expect(delegatedResult).toContain("<option value='b' selected>");
  });

  test('a change event on an element WITHOUT data-wtm-cellchange is a complete no-op (byte-inert on non-island pages)', () => {
    const { layui } = loadFreshFf();
    layui.table.cache.wtTable_Plain = freshCache("<input value='old' />");
    const input = document.createElement('input');
    input.value = 'shouldnotpropagate';
    document.body.appendChild(input);
    input.dispatchEvent(new Event('change', { bubbles: true }));
    // Untouched — no data-wtm-cellchange means the listener returns early.
    expect(layui.table.cache.wtTable_Plain[0].LoginName).toBe("<input value='old' />");
  });
});

// ---------------------------------------------------------------------------
// (b2) Containment guard (#470 Slice O3 review polish, security MEDIUM):
// confused-deputy hardening — a forged element carrying data-wtm-cellchange-*
// attributes must actually sit inside the rendered
// `.layui-table-view[lay-id={gridid}]` view for the grid it claims, or the
// delegated listener must reject it.
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — delegated cellchange containment guard (confused-deputy hardening)', () => {
  function freshCache(cellHtml) {
    return [{ LoginName: cellHtml }];
  }

  test('a legit cell placed inside its own grid\'s .layui-table-view[lay-id=...] IS accepted (feature works)', () => {
    const { layui } = loadFreshFf();
    layui.table.cache.wtTable_Contained = freshCache("<input value='old' />");
    const input = document.createElement('input');
    input.value = 'legit-edit';
    input.setAttribute('data-wtm-cellchange', '1');
    input.setAttribute('data-wtm-cellchange-grid', 'wtTable_Contained');
    input.setAttribute('data-wtm-cellchange-row', '0');
    input.setAttribute('data-wtm-cellchange-col', 'LoginName');
    input.setAttribute('data-wtm-cellchange-celltype', '0');
    appendTableView('wtTable_Contained', input);

    input.dispatchEvent(new Event('change', { bubbles: true }));

    expect(layui.table.cache.wtTable_Contained[0].LoginName).toContain("value='legit-edit'");
  });

  test('a forged element carrying the SAME data-wtm-cellchange-* attributes but NOT inside any .layui-table-view is REJECTED — cache untouched', () => {
    const { layui } = loadFreshFf();
    layui.table.cache.wtTable_Forged = freshCache("<input value='old' />");
    const forged = document.createElement('input');
    forged.value = 'forged-edit';
    forged.setAttribute('data-wtm-cellchange', '1');
    forged.setAttribute('data-wtm-cellchange-grid', 'wtTable_Forged');
    forged.setAttribute('data-wtm-cellchange-row', '0');
    forged.setAttribute('data-wtm-cellchange-col', 'LoginName');
    forged.setAttribute('data-wtm-cellchange-celltype', '0');
    // Deliberately appended directly to <body>, mimicking an attacker-planted
    // element (e.g. injected into an unrelated widget) that never went
    // through ff.AddGridRow/ff.LoadLocalData and is not a descendant of any
    // .layui-table-view at all.
    document.body.appendChild(forged);

    forged.dispatchEvent(new Event('change', { bubbles: true }));

    expect(layui.table.cache.wtTable_Forged[0].LoginName).toBe("<input value='old' />");
  });

  test('a forged element inside a DIFFERENT grid\'s .layui-table-view (mismatched lay-id) is REJECTED — cache untouched', () => {
    const { layui } = loadFreshFf();
    layui.table.cache.wtTable_RealTarget = freshCache("<input value='old' />");
    layui.table.cache.wtTable_OtherGrid = freshCache("<input value='other-old' />");
    const forged = document.createElement('input');
    forged.value = 'cross-grid-forged-edit';
    forged.setAttribute('data-wtm-cellchange', '1');
    // Claims to belong to wtTable_RealTarget, but is physically rendered
    // inside wtTable_OtherGrid's own view — a confused-deputy attempt to
    // reuse a legitimate OTHER grid's DOM subtree to smuggle a forged claim.
    forged.setAttribute('data-wtm-cellchange-grid', 'wtTable_RealTarget');
    forged.setAttribute('data-wtm-cellchange-row', '0');
    forged.setAttribute('data-wtm-cellchange-col', 'LoginName');
    forged.setAttribute('data-wtm-cellchange-celltype', '0');
    appendTableView('wtTable_OtherGrid', forged);

    forged.dispatchEvent(new Event('change', { bubbles: true }));

    expect(layui.table.cache.wtTable_RealTarget[0].LoginName).toBe("<input value='old' />");
    expect(layui.table.cache.wtTable_OtherGrid[0].LoginName).toBe("<input value='other-old' />");
  });
});

// ---------------------------------------------------------------------------
// (c) AddGridRow / RemoveGridRow / LoadLocalData cache-mutation parity —
// island markup (data-wtm-cellchange) vs legacy markup (onchange attribute),
// same [n]/_n_ index-rewrite + splice/push semantics either way.
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — AddGridRow/RemoveGridRow/LoadLocalData cache mutation parity', () => {
  test('AddGridRow: legacy table (no data-wtm-grid-id) stamps onchange="ff.gridcellchange(...)"; island table (data-wtm-grid-id) stamps data-wtm-cellchange-* instead — same row pushed, same [n]/_n_ rewriting either way', () => {
    appendTable('wtTable_Legacy', false);
    appendTable('wtTable_Island', true);
    const { ff, layui } = loadFreshFf();

    layui.table.cache.wtTable_Legacy = [];
    layui.table.cache.wtTable_Island = [];
    const legacyOption = {};
    const islandOption = {};
    // ID must be present (any value) for AddGridRow to assign a fresh guid —
    // its `for (val in data) { if (val === "ID") { ... } }` loop only fires
    // for a key that already exists on the object.
    const rowData = () => ({ ID: null, LoginName: "<input value='x' />" });

    ff.AddGridRow('wtTable_Legacy', legacyOption, rowData());
    ff.AddGridRow('wtTable_Island', islandOption, rowData());

    const legacyHtml = layui.table.cache.wtTable_Legacy[0].LoginName;
    const islandHtml = layui.table.cache.wtTable_Island[0].LoginName;

    expect(legacyHtml).toMatch(/onchange="ff\.gridcellchange\(this,'wtTable_Legacy',0,'LoginName',0\)"/);
    expect(legacyHtml).not.toMatch(/data-wtm-cellchange/);

    expect(islandHtml).toMatch(/data-wtm-cellchange-grid="wtTable_Island"/);
    expect(islandHtml).toMatch(/data-wtm-cellchange-row="0"/);
    expect(islandHtml).toMatch(/data-wtm-cellchange-col="LoginName"/);
    expect(islandHtml).toMatch(/data-wtm-cellchange-celltype="0"/);
    expect(islandHtml).not.toMatch(/onchange=/);

    // Both got exactly one row pushed with a fresh guid ID assigned.
    expect(layui.table.cache.wtTable_Legacy).toHaveLength(1);
    expect(layui.table.cache.wtTable_Island).toHaveLength(1);
    expect(layui.table.cache.wtTable_Legacy[0].ID).toBeDefined();
    expect(layui.table.cache.wtTable_Island[0].ID).toBeDefined();
  });

  test('RemoveGridRow: island-mode row-index reindex rewrites data-wtm-cellchange-row (not onchange) after a splice, mirroring the legacy onchange rebuild', () => {
    appendTable('wtTable_Island2', true);
    const { ff, layui } = loadFreshFf();

    layui.table.cache.wtTable_Island2 = [
      { LoginName: "<input value='r0' data-wtm-cellchange=\"1\" data-wtm-cellchange-grid=\"wtTable_Island2\" data-wtm-cellchange-row=\"0\" data-wtm-cellchange-col=\"LoginName\" data-wtm-cellchange-celltype=\"0\" />" },
      { LoginName: "<input value='r1' data-wtm-cellchange=\"1\" data-wtm-cellchange-grid=\"wtTable_Island2\" data-wtm-cellchange-row=\"1\" data-wtm-cellchange-col=\"LoginName\" data-wtm-cellchange-celltype=\"0\" />" },
      { LoginName: "<input value='r2' data-wtm-cellchange=\"1\" data-wtm-cellchange-grid=\"wtTable_Island2\" data-wtm-cellchange-row=\"2\" data-wtm-cellchange-col=\"LoginName\" data-wtm-cellchange-celltype=\"0\" />" }
    ];

    // Remove index 1 (1-based, matching legacy's `index - 1` splice call —
    // AddSubButton's RemoveRow onclick always passed the laytpl LAY_INDEX,
    // which is 1-based).
    ff.RemoveGridRow('wtTable_Island2', {}, 2);

    expect(layui.table.cache.wtTable_Island2).toHaveLength(2);
    // Remaining rows (originally r0, r2) get reindexed to 0/1.
    expect(layui.table.cache.wtTable_Island2[0].LoginName).toMatch(/data-wtm-cellchange-row="0"/);
    expect(layui.table.cache.wtTable_Island2[0].LoginName).toContain("value='r0'");
    expect(layui.table.cache.wtTable_Island2[1].LoginName).toMatch(/data-wtm-cellchange-row="1"/);
    expect(layui.table.cache.wtTable_Island2[1].LoginName).toContain("value='r2'");
  });

  test('LoadLocalData (island, detail grid i.e. isnormaltable=false): stamps data-wtm-cellchange on every row, then renders with limit=9999/url=null', () => {
    appendTable('wtTable_Island3', true);
    const { ff, layui } = loadFreshFf();

    const datas = [
      { LoginName: "<input value='a' />" },
      { LoginName: "<input value='b' />" }
    ];
    const option = {};
    ff.LoadLocalData('wtTable_Island3', option, datas, false);

    expect(datas[0].LoginName).toMatch(/data-wtm-cellchange-grid="wtTable_Island3"/);
    expect(datas[0].LoginName).toMatch(/data-wtm-cellchange-row="0"/);
    expect(datas[1].LoginName).toMatch(/data-wtm-cellchange-row="1"/);
    expect(datas[0].LoginName).not.toMatch(/onchange=/);

    expect(layui.table.render).toHaveBeenCalledTimes(1);
    const renderedOpt = layui.table.render.mock.calls[0][0];
    expect(renderedOpt.limit).toBe(9999);
    expect(renderedOpt.url).toBeNull();
    expect(renderedOpt.data).toBe(datas);
  });

  test('LoadLocalData (isnormaltable=true, a plain non-detail grid): no cellchange markup of either kind is stamped', () => {
    appendTable('wtTable_Island4', true);
    const { ff } = loadFreshFf();
    const datas = [{ LoginName: "<input value='a' />" }];
    ff.LoadLocalData('wtTable_Island4', {}, datas, true);
    expect(datas[0].LoginName).toBe("<input value='a' />");
  });
});

// ---------------------------------------------------------------------------
// SetGridCellDate (#470 Slice O3, gated by review polish): a NON-island cell
// (no data-wtm-cellchange markup) keeps the EXACT legacy direct `.onchange()`
// call; an island cell (carries the markup) dispatches a genuine bubbling
// 'change' Event so the delegated data-wtm-cellchange listener fires. The
// gate is `el.hasAttribute('data-wtm-cellchange')` — the SAME feature
// detection AddGridRow/LoadLocalData/RemoveGridRow and the delegated
// listener itself already use.
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — SetGridCellDate dispatches by island feature-detection', () => {
  test('legacy cell (no data-wtm-cellchange markup, flag-OFF parity): invokes a directly-assigned el.onchange handler, NOT a real dispatched Event', () => {
    const { ff, layui } = loadFreshFf();
    const input = document.createElement('input');
    input.id = 'dateCellLegacy';
    document.body.appendChild(input);
    const onchangeSpy = jest.fn();
    input.onchange = onchangeSpy;
    // A real addEventListener('change', ...) handler — this is what a
    // genuine dispatchEvent() would ALSO invoke (event bubbling reaches
    // listeners a direct `.onchange()` call never touches). Its absence of
    // invocation below is the parity proof: the legacy path never goes
    // through the real event system at all.
    const bubbleSpy = jest.fn();
    document.addEventListener('change', bubbleSpy);

    expect(input.hasAttribute('data-wtm-cellchange')).toBe(false);

    ff.SetGridCellDate('dateCellLegacy', 'date');
    expect(layui.laydate.render).toHaveBeenCalledTimes(1);
    // Drive the REAL done callback SetGridCellDate registered (laydate itself
    // is a stubbed layui.use(...) module under jsdom — this invokes exactly
    // the code path production laydate would call once the user picks a date).
    const laydateOpts = layui.laydate.render.mock.calls[0][0];
    laydateOpts.done('2026-01-01', {}, {});

    expect(input.value).toBe('2026-01-01');
    expect(onchangeSpy).toHaveBeenCalledTimes(1);
    // Flag-OFF parity: no real 'change' Event was dispatched, so a
    // document-level bubbling listener (which a real dispatchEvent() call
    // WOULD have reached) never fires for a legacy cell.
    expect(bubbleSpy).not.toHaveBeenCalled();

    document.removeEventListener('change', bubbleSpy);
  });

  test('island cell (carries data-wtm-cellchange markup): dispatches a real bubbling Event, reaching BOTH the delegated listener and any other change listener (unlike the legacy direct-call path)', () => {
    const { ff, layui } = loadFreshFf();
    layui.table.cache.wtTable_DateIsland = [{ LoginName: "<input value='old' />" }];
    const input = document.createElement('input');
    input.id = 'dateCellIsland';
    input.setAttribute('data-wtm-cellchange', '1');
    input.setAttribute('data-wtm-cellchange-grid', 'wtTable_DateIsland');
    input.setAttribute('data-wtm-cellchange-row', '0');
    input.setAttribute('data-wtm-cellchange-col', 'LoginName');
    input.setAttribute('data-wtm-cellchange-celltype', '0');
    // Placed inside the real rendered `.layui-table-view[lay-id=...]` shape
    // the delegated listener's containment guard (review polish) requires.
    appendTableView('wtTable_DateIsland', input);
    // No onchange handler assigned on this element — the delegated listener
    // (document.addEventListener('change', ...)) is the ONLY consumer of the
    // dispatched event, exactly the island scenario.
    const bubbleSpy = jest.fn();
    document.addEventListener('change', bubbleSpy);

    ff.SetGridCellDate('dateCellIsland', 'date');
    const laydateOpts = layui.laydate.render.mock.calls[0][0];
    laydateOpts.done('2026-06-15', {}, {});

    expect(input.value).toBe('2026-06-15');
    expect(layui.table.cache.wtTable_DateIsland[0].LoginName).toContain("value='2026-06-15'");
    // A genuine bubbling Event was dispatched for the island path.
    expect(bubbleSpy).toHaveBeenCalledTimes(1);

    document.removeEventListener('change', bubbleSpy);
  });
});

// ---------------------------------------------------------------------------
// foldPanel island action
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — foldPanel island action', () => {
  test('ff._renderFoldPanelAction calls layui.element.fold with the filter read off #{searchPanelId} .layui-collapse[lay-filter]', () => {
    const panel = document.createElement('div');
    panel.id = 'wtForm_O3';
    const collapse = document.createElement('div');
    collapse.className = 'layui-collapse';
    collapse.setAttribute('lay-filter', 'foldFilter123');
    panel.appendChild(collapse);
    document.body.appendChild(panel);

    const { ff, layui } = loadFreshFf();
    ff._renderFoldPanelAction({ type: 'foldPanel', searchPanelId: 'wtForm_O3', fold: true });

    jest.runAllTimers ? null : null; // no fake timers in this suite; setTimeout(...,0) runs via real timers below
    return new Promise((resolve) => {
      setTimeout(() => {
        expect(layui.element.fold).toHaveBeenCalledWith('foldFilter123', true);
        resolve();
      }, 10);
    });
  });

  test('a foldPanel island dispatched through the REAL _consumePageReadyIslands -> DispatchAction chain reaches ff._renderFoldPanelAction', () => {
    const panel = document.createElement('div');
    panel.id = 'wtForm_O3b';
    const collapse = document.createElement('div');
    collapse.className = 'layui-collapse';
    collapse.setAttribute('lay-filter', 'foldFilterB');
    panel.appendChild(collapse);
    document.body.appendChild(panel);

    const { ff, layui } = loadFreshFf();
    appendPageReadyIsland({ type: 'foldPanel', searchPanelId: 'wtForm_O3b', fold: false });
    ff._consumePageReadyIslands();

    return new Promise((resolve) => {
      setTimeout(() => {
        expect(layui.element.fold).toHaveBeenCalledWith('foldFilterB', false);
        resolve();
      }, 10);
    });
  });

  test('a missing lay-filter attribute is a safe no-op (matches legacy: `if (filter) { ... }`)', () => {
    const panel = document.createElement('div');
    panel.id = 'wtForm_O3c';
    document.body.appendChild(panel);

    const { ff, layui } = loadFreshFf();
    ff._renderFoldPanelAction({ type: 'foldPanel', searchPanelId: 'wtForm_O3c', fold: true });

    return new Promise((resolve) => {
      setTimeout(() => {
        expect(layui.element.fold).not.toHaveBeenCalled();
        resolve();
      }, 10);
    });
  });
});

// ---------------------------------------------------------------------------
// ff.grid.state / ff.grid.isIsland direct unit coverage
// ---------------------------------------------------------------------------
describe('#470 Slice O3 — ff.grid namespace', () => {
  test('state(gridId) is a pure passthrough over the compat globals', () => {
    const { ff, windowObj } = loadFreshFf();
    windowObj.wtTable_Goption = { foo: 1 };
    windowObj.wtTable_Gdefaultfilter = { where: { a: 1 } };
    windowObj.wtTable_Gfilterback = { page: { curr: 2 } };
    windowObj.wtTable_Gurl = '/some/url';

    const state = ff.grid.state('wtTable_G');
    expect(state.option).toBe(windowObj.wtTable_Goption);
    expect(state.defaultfilter).toBe(windowObj.wtTable_Gdefaultfilter);
    expect(state.filterback).toBe(windowObj.wtTable_Gfilterback);
    expect(state.url).toBe('/some/url');
  });

  test('isIsland(gridId) is true only when the <table> element carries data-wtm-grid-id matching gridId', () => {
    appendTable('wtTable_IsIsland1', true);
    appendTable('wtTable_IsIsland2', false);
    const { ff } = loadFreshFf();

    expect(ff.grid.isIsland('wtTable_IsIsland1')).toBe(true);
    expect(ff.grid.isIsland('wtTable_IsIsland2')).toBe(false);
    expect(ff.grid.isIsland('wtTable_DoesNotExist')).toBe(false);
  });
});
