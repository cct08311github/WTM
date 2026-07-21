// Tests for Issue #470 Slice N1 — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF — the SAME flag #470 Slices J/K/L/M use) eval-free islandification
// of <wt:treecontainer> ('renderTreeContainer') and <wt:chart> ('renderChart').
//
// This file drives the REAL ff.DispatchAction / ff._renderTreeContainerAction /
// ff._renderChartAction / ff._islandModulesFor / ff._dispatchIslandWhenReady
// end-to-end against a FRESH vm instance of the actual shipped
// framework_layui.js source (real jsdom `document`) — not a source-sweep or a
// hand-rolled reimplementation — following the same convention as
// framework_layui_470_sliceK_transfer.test.js /
// framework_layui_470_sliceL_upload.test.js. layui.tree.render/layui.table.reload
// are stubbed (layui.tree is a real layui.use(...) module in production, never
// loaded under jsdom); echarts is stubbed the same way (a plain <script src>
// global, never loaded under jsdom either).

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

// A minimal jQuery-alike: $(selector) returns a fresh chainable wrapper with
// .find()/.css()/.click() — .css/.click calls are tracked on SHARED spies so
// assertions can check "some element got clicked/styled" without needing to
// track individual wrapper identities (mirrors the legacy inline script's own
// use of these three jQuery methods only, nothing else).
function makeJQueryMock() {
  const cssSpy = jest.fn();
  const clickSpy = jest.fn();
  function wrap() {
    return {
      find: jest.fn(() => wrap()),
      css: cssSpy,
      click: clickSpy,
      length: 1
    };
  }
  const $ = jest.fn(() => wrap());
  $.cssSpy = cssSpy;
  $.clickSpy = clickSpy;
  $.get = jest.fn();
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.fn = {};
  return $;
}

// A fake jQuery-alike "elem" object for a layui tree click callback's
// data.elem — same .find()/.css() chainable shape the real layui.tree click
// callback passes (see the legacy inline script's
// `data.elem.find('.layui-tree-main:first')` followed by
// `ele.find('.layui-tree-txt').css(...)` — TWO chained .find() calls, so the
// wrapper returned by .find() must itself support both .find() and .css()).
function makeFakeElem() {
  const cssSpy = jest.fn();
  function wrap() {
    return { find: jest.fn(() => wrap()), css: cssSpy };
  }
  const elem = { find: jest.fn(() => wrap()) };
  elem.cssSpy = cssSpy;
  return elem;
}

function makeTreeLayui(overrides) {
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    tree: {
      render: jest.fn(() => ({ config: { setSelected: jest.fn() } }))
    },
    table: {
      reload: jest.fn()
    }
  }, overrides || {});
}

function makeEcharts(overrides) {
  return Object.assign({
    init: jest.fn(() => ({ setOption: jest.fn(), resize: jest.fn() }))
  }, overrides || {});
}

function loadFreshFf(opts) {
  opts = opts || {};
  const $ = opts.$ || makeJQueryMock();
  const layui = opts.layui || makeTreeLayui();
  const ctxInit = {
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
  };
  if (Object.prototype.hasOwnProperty.call(opts, 'echarts')) {
    ctxInit.echarts = opts.echarts;
  }
  const ctx = vm.createContext(ctxInit);
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx, layui, $ };
}

function appendDiv(id) {
  document.body.innerHTML = '';
  const div = document.createElement('div');
  div.id = id;
  document.body.appendChild(div);
  return div;
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice N1 — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice N1 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderTreeContainer' case delegates to ff._renderTreeContainerAction, no inline body", () => {
    const block = active.match(/case\s+['"]renderTreeContainer['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderTreeContainerAction\(\s*action\s*\)/);
  });

  test("'renderChart' case delegates to ff._renderChartAction, no inline body", () => {
    const block = active.match(/case\s+['"]renderChart['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderChartAction\(\s*action\s*\)/);
  });

  test('_renderTreeContainerAction resolves clickFunc through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = active.match(/_renderTreeContainerAction:\s*function[\s\S]*?_renderChartAction:/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.clickFunc\s*\)/);
    expect(block[0]).not.toMatch(/\beval\(/);
    expect(block[0]).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderTreeContainer' HAS an _islandModulesFor entry — layui.tree IS a layui.use(...) module", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n {4}\},/)[0];
    expect(fn).toMatch(/renderTreeContainer/);
    expect(fn).toMatch(/needed\.tree\s*=\s*true/);
    expect(fn).toMatch(/mods\.push\('tree'\)/);
  });

  test("'renderChart' is intentionally ABSENT from _islandModulesFor's dispatch table — echarts is a plain <script src> global, not a layui.use(...) module", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n {4}\},/)[0];
    expect(fn).not.toMatch(/renderChart/);
  });

  test('_renderChartAction contains no eval/Function and degrades via console.warn', () => {
    const block = active.match(/_renderChartAction:\s*function[\s\S]*?\n {4}\},/);
    expect(block).not.toBeNull();
    expect(block[0]).not.toMatch(/\beval\(/);
    expect(block[0]).not.toMatch(/new\s+Function\s*\(/);
    expect(block[0]).toMatch(/console\.warn/);
  });
});

// ---------------------------------------------------------------------------
// renderTreeContainer — basic render + globals
// ---------------------------------------------------------------------------
describe('#470 Slice N1 — renderTreeContainer basic render', () => {
  test('dispatch calls layui.tree.render with the expected config', () => {
    appendDiv('divTC1');
    const { ff, layui } = loadFreshFf();

    ff.DispatchAction({
      actions: [{
        type: 'renderTreeContainer', id: 'TC1', elemId: 'divTC1', gridDivId: 'div_TC1',
        showLine: true, idFieldName: 'notsetid', levelFieldName: 'notsetlevel',
        data: [{ id: 'v1', title: 'Node 1' }], clickMode: 'default'
      }]
    });

    expect(layui.tree.render).toHaveBeenCalledTimes(1);
    const cfg = layui.tree.render.mock.calls[0][0];
    expect(cfg.id).toBe('treeTC1');
    expect(cfg.elem).toBe('#divTC1');
    expect(cfg.onlyIconControl).toBe(true);
    expect(cfg.showCheckbox).toBe(false);
    expect(cfg.showLine).toBe(true);
    expect(cfg.data).toEqual([{ id: 'v1', title: 'Node 1' }]);
  });

  test('showLine:false is honored', () => {
    appendDiv('divTC1b');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC1b', elemId: 'divTC1b', gridDivId: 'div_TC1b', showLine: false, data: [] }]
    });
    expect(layui.tree.render.mock.calls[0][0].showLine).toBe(false);
  });

  test('module deferral: ff._dispatchIslandWhenReady defers renderTreeContainer through layui.use(["tree"], cb)', () => {
    appendDiv('divTC1c');
    const { ff, layui } = loadFreshFf();
    ff._dispatchIslandWhenReady({
      actions: [{ type: 'renderTreeContainer', id: 'TC1c', elemId: 'divTC1c', gridDivId: 'div_TC1c', data: [] }]
    });
    expect(layui.use).toHaveBeenCalledTimes(1);
    expect(layui.use.mock.calls[0][0]).toEqual(['tree']);
    expect(layui.tree.render).toHaveBeenCalledTimes(1);
  });

  test('creates window["top"+id+"selected"] = {} when no selectedItem', () => {
    appendDiv('divTC2');
    const { ff, windowObj } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC2', elemId: 'divTC2', gridDivId: 'div_TC2', data: [] }]
    });
    expect(windowObj.topTC2selected).toEqual({});
  });

  test('creates window["top"+id+"selected"] pre-populated when selectedItem present', () => {
    appendDiv('divTC3');
    const { ff, windowObj } = loadFreshFf();
    ff.DispatchAction({
      actions: [{
        type: 'renderTreeContainer', id: 'TC3', elemId: 'divTC3', gridDivId: 'div_TC3',
        idFieldName: 'xId', levelFieldName: 'level', data: [],
        selectedItem: { id: 'v9', level: 2 }
      }]
    });
    expect(windowObj.topTC3selected).toEqual({ xId: 'v9', level: 2 });
  });

  test('sets window["treecontainer"+id] to the tree render return value', () => {
    appendDiv('divTC4');
    const fakeInstance = { config: { setSelected: jest.fn() }, __fake: 'treeInstance' };
    const layui = makeTreeLayui({ tree: { render: jest.fn(() => fakeInstance) } });
    const { ff, windowObj } = loadFreshFf({ layui });
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC4', elemId: 'divTC4', gridDivId: 'div_TC4', data: [] }]
    });
    expect(windowObj.treecontainerTC4).toBe(fakeInstance);
  });

  test('calls treeInstance.config.setSelected({data: selectedItem}) when selectedItem present', () => {
    appendDiv('divTC5');
    const setSelectedSpy = jest.fn();
    const layui = makeTreeLayui({ tree: { render: jest.fn(() => ({ config: { setSelected: setSelectedSpy } })) } });
    const { ff } = loadFreshFf({ layui });
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC5', elemId: 'divTC5', gridDivId: 'div_TC5', data: [], selectedItem: { id: 'v1', level: 0 } }]
    });
    expect(setSelectedSpy).toHaveBeenCalledWith({ data: { id: 'v1', level: 0 } });
  });

  test('does NOT call setSelected when no selectedItem', () => {
    appendDiv('divTC6');
    const setSelectedSpy = jest.fn();
    const layui = makeTreeLayui({ tree: { render: jest.fn(() => ({ config: { setSelected: setSelectedSpy } })) } });
    const { ff } = loadFreshFf({ layui });
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC6', elemId: 'divTC6', gridDivId: 'div_TC6', data: [] }]
    });
    expect(setSelectedSpy).not.toHaveBeenCalled();
  });

  test('autoLoadUrl set: calls ff.LoadPage1(autoLoadUrl, gridDivId) at render time', () => {
    appendDiv('divTC7');
    const { ff, windowObj } = loadFreshFf();
    const loadSpy = jest.fn();
    windowObj.ff.LoadPage1 = loadSpy;
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC7', elemId: 'divTC7', gridDivId: 'div_TC7', data: [], autoLoadUrl: '/Home/First' }]
    });
    expect(loadSpy).toHaveBeenCalledWith('/Home/First', 'div_TC7');
  });

  test('autoLoadUrl absent: ff.LoadPage1 not called at render time', () => {
    appendDiv('divTC8');
    const { ff, windowObj } = loadFreshFf();
    const loadSpy = jest.fn();
    windowObj.ff.LoadPage1 = loadSpy;
    ff.DispatchAction({
      actions: [{ type: 'renderTreeContainer', id: 'TC8', elemId: 'divTC8', gridDivId: 'div_TC8', data: [] }]
    });
    expect(loadSpy).not.toHaveBeenCalled();
  });

  test('missing id/elemId: safe no-op, never throws', () => {
    const { ff, layui } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'renderTreeContainer' }] });
    }).not.toThrow();
    expect(layui.tree.render).not.toHaveBeenCalled();
  });

  test('layui.tree not loaded: skips with a console.warn, never throws', () => {
    appendDiv('divTC9');
    const ctx = vm.createContext({
      window: {}, document, layui: {}, console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: makeJQueryMock(), DOMParser: global.DOMParser,
      DONOTUSE_TABLAYID: undefined, DONOTUSE_COOKIEPRE: '', DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ctx.ff.DispatchAction({ actions: [{ type: 'renderTreeContainer', id: 'TC9', elemId: 'divTC9', gridDivId: 'div_TC9', data: [] }] });
    }).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('layui.tree is not loaded'));
    warnSpy.mockRestore();
  });
});

// ---------------------------------------------------------------------------
// renderTreeContainer — click-mode matrix
// ---------------------------------------------------------------------------
describe('#470 Slice N1 — renderTreeContainer click modes', () => {
  function dispatchAndGetClick(action, opts) {
    appendDiv(action.elemId);
    const { ff, layui, windowObj, $ } = loadFreshFf(opts);
    ff.DispatchAction({ actions: [action] });
    const cfg = layui.tree.render.mock.calls[0][0];
    return { ff, layui, windowObj, $, cfg };
  }

  test("clickMode 'custom': resolved window fn is invoked with data; top{id}selected is NOT written", () => {
    // Unlike _renderTransferAction's changeFunc precedent (resolved ONCE at
    // render time), 'custom' clickFunc is re-resolved via the guarded
    // window[name] lookup on EVERY click (#470 Slice N1 polish — matches the
    // legacy inline handler's late binding, see the next test). Registering
    // the target function before dispatch, as here, still resolves fine.
    appendDiv('divTCc1');
    const { ff, windowObj, layui } = loadFreshFf();
    const clickSpy = jest.fn();
    windowObj.myTreeClickFn = clickSpy;

    ff.DispatchAction({
      actions: [{
        type: 'renderTreeContainer', id: 'TCc1', elemId: 'divTCc1', gridDivId: 'div_TCc1',
        idFieldName: 'notsetid', levelFieldName: 'notsetlevel', data: [],
        clickMode: 'custom', clickFunc: 'myTreeClickFn'
      }]
    });
    const cfg = layui.tree.render.mock.calls[0][0];

    const fakeData = { elem: makeFakeElem(), data: { id: 'n1', level: 1 } };
    cfg.click(fakeData);

    expect(clickSpy).toHaveBeenCalledWith(fakeData);
    expect(windowObj.topTCc1selected).toEqual({});
  });

  test("clickMode 'custom': fn defined AFTER dispatch but BEFORE the click still resolves (late-bound, #470 Slice N1 polish)", () => {
    // The bug this locks in: the legacy inline handler looked up the
    // callback name fresh on every click, so a function defined after page
    // load (e.g. by a script that runs later) still worked. An
    // island-dispatch-time-only resolve would silently no-op here instead.
    appendDiv('divTCc1c');
    const { ff, windowObj, layui } = loadFreshFf();

    ff.DispatchAction({
      actions: [{
        type: 'renderTreeContainer', id: 'TCc1c', elemId: 'divTCc1c', gridDivId: 'div_TCc1c',
        idFieldName: 'notsetid', levelFieldName: 'notsetlevel', data: [],
        clickMode: 'custom', clickFunc: 'lateTreeClickFn'
      }]
    });
    const cfg = layui.tree.render.mock.calls[0][0];

    // Not registered until AFTER dispatch, but before the click fires.
    const clickSpy = jest.fn();
    windowObj.lateTreeClickFn = clickSpy;

    const fakeData = { elem: makeFakeElem(), data: { id: 'n2', level: 4 } };
    cfg.click(fakeData);

    expect(clickSpy).toHaveBeenCalledWith(fakeData);
  });

  test("clickMode 'custom' with a non-window-registered name: silently no-ops (guarded resolver), never throws", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc1b', elemId: 'divTCc1b', gridDivId: 'div_TCc1b',
      data: [], clickMode: 'custom', clickFunc: 'notRegisteredAnywhere'
    };
    const { cfg } = dispatchAndGetClick(action);
    expect(() => cfg.click({ elem: makeFakeElem(), data: { id: 'n1', level: 1 } })).not.toThrow();
  });

  test("clickMode 'searchButton': writes top{id}selected id/level AND clicks the button by id", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc2', elemId: 'divTCc2', gridDivId: 'div_TCc2',
      idFieldName: 'notsetid', levelFieldName: 'notsetlevel', data: [],
      clickMode: 'searchButton', searchButtonId: 'mySearchBtn'
    };
    const { windowObj, $, cfg } = dispatchAndGetClick(action);

    cfg.click({ elem: makeFakeElem(), data: { id: 'nodeA', level: 3 } });

    expect(windowObj.topTCc2selected).toEqual({ notsetid: 'nodeA', notsetlevel: 3 });
    expect($).toHaveBeenCalledWith('#mySearchBtn');
    expect($.clickSpy).toHaveBeenCalledTimes(1);
  });

  test("clickMode 'grid' with gridExtendWhere=true: extends window[gridId+'defaultfilter'].where AT EVENT TIME, then calls layui.table.reload", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc3', elemId: 'divTCc3', gridDivId: 'div_TCc3',
      idFieldName: 'xId', levelFieldName: 'xLevel', data: [],
      clickMode: 'grid', gridId: 'grid1', gridExtendWhere: true
    };
    const { windowObj, layui, cfg } = dispatchAndGetClick(action);

    // Globals set AFTER render, BEFORE the click — proves they're read at
    // event time, not captured at render time.
    windowObj.grid1defaultfilter = { where: { existing: 'kept' } };
    windowObj.grid1url = '/Grid1/List';

    cfg.click({ elem: makeFakeElem(), data: { id: 'nodeB', level: 5 } });

    expect(windowObj.grid1defaultfilter.where).toEqual({ existing: 'kept', xId: 'nodeB', xLevel: 5 });
    expect(windowObj.topTCc3selected).toEqual({});
    expect(layui.table.reload).toHaveBeenCalledWith('grid1', {
      url: '/Grid1/List',
      where: { existing: 'kept', xId: 'nodeB', xLevel: 5 }
    });
  });

  test("clickMode 'grid' with gridExtendWhere=false: writes top{id}selected id/level instead, still reloads with EVENT-TIME globals", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc4', elemId: 'divTCc4', gridDivId: 'div_TCc4',
      idFieldName: 'xId', levelFieldName: 'xLevel', data: [],
      clickMode: 'grid', gridId: 'grid2', gridExtendWhere: false
    };
    const { windowObj, layui, cfg } = dispatchAndGetClick(action);

    windowObj.grid2defaultfilter = { where: { untouched: true } };
    windowObj.grid2url = '/Grid2/List';

    cfg.click({ elem: makeFakeElem(), data: { id: 'nodeC', level: 7 } });

    expect(windowObj.topTCc4selected).toEqual({ xId: 'nodeC', xLevel: 7 });
    expect(windowObj.grid2defaultfilter.where).toEqual({ untouched: true });
    expect(layui.table.reload).toHaveBeenCalledWith('grid2', {
      url: '/Grid2/List',
      where: { untouched: true }
    });
  });

  test("clickMode 'grid' with missing grid globals: degrades — reload still called, where undefined, never throws", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc5', elemId: 'divTCc5', gridDivId: 'div_TCc5',
      data: [], clickMode: 'grid', gridId: 'gridMissing', gridExtendWhere: true
    };
    const { layui, cfg } = dispatchAndGetClick(action);
    expect(() => cfg.click({ elem: makeFakeElem(), data: { id: 'x', level: 0 } })).not.toThrow();
    expect(layui.table.reload).toHaveBeenCalledWith('gridMissing', { url: undefined, where: undefined });
  });

  test("clickMode 'loadPage': calls ff.LoadPage1(data.data.href, gridDivId) when href present", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc6', elemId: 'divTCc6', gridDivId: 'div_TCc6',
      data: [], clickMode: 'loadPage'
    };
    const { windowObj, cfg } = dispatchAndGetClick(action);
    const loadSpy = jest.fn();
    windowObj.ff.LoadPage1 = loadSpy;

    cfg.click({ elem: makeFakeElem(), data: { id: 'n', level: 0, href: '/Node/Detail' } });
    expect(loadSpy).toHaveBeenCalledWith('/Node/Detail', 'div_TCc6');
  });

  test("clickMode 'loadPage': no-op when href absent", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc7', elemId: 'divTCc7', gridDivId: 'div_TCc7',
      data: [], clickMode: 'loadPage'
    };
    const { windowObj, cfg } = dispatchAndGetClick(action);
    const loadSpy = jest.fn();
    windowObj.ff.LoadPage1 = loadSpy;

    cfg.click({ elem: makeFakeElem(), data: { id: 'n', level: 0 } });
    expect(loadSpy).not.toHaveBeenCalled();
  });

  test("clickMode 'default': writes top{id}selected id/level only, no button/grid/loadPage side effects", () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc8', elemId: 'divTCc8', gridDivId: 'div_TCc8',
      idFieldName: 'notsetid', levelFieldName: 'notsetlevel', data: [], clickMode: 'default'
    };
    const { windowObj, layui, cfg } = dispatchAndGetClick(action);
    cfg.click({ elem: makeFakeElem(), data: { id: 'nodeD', level: 1 } });
    expect(windowObj.topTCc8selected).toEqual({ notsetid: 'nodeD', notsetlevel: 1 });
    expect(layui.table.reload).not.toHaveBeenCalled();
  });

  test('click handler highlight toggling never throws even when data.elem is absent (falls back to $ lookup)', () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc9', elemId: 'divTCc9', gridDivId: 'div_TCc9',
      data: [], clickMode: 'default'
    };
    const { cfg } = dispatchAndGetClick(action);
    expect(() => cfg.click({ data: { id: 'nodeE', level: 0 } })).not.toThrow();
  });

  test('setSelected callback only toggles highlight, never runs click-mode side effects', () => {
    const action = {
      type: 'renderTreeContainer', id: 'TCc10', elemId: 'divTCc10', gridDivId: 'div_TCc10',
      idFieldName: 'notsetid', levelFieldName: 'notsetlevel', data: [],
      clickMode: 'searchButton', searchButtonId: 'btnX'
    };
    const { windowObj, $, cfg } = dispatchAndGetClick(action);
    cfg.setSelected({ elem: makeFakeElem(), data: { id: 'nodeF', level: 2 } });
    expect(windowObj.topTCc10selected).toEqual({});
    expect($.clickSpy).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// renderChart
// ---------------------------------------------------------------------------
describe('#470 Slice N1 — renderChart', () => {
  test('echarts.init called on the target element with theme "default" when no theme set; globals set with exact names', () => {
    appendDiv('C1');
    const echarts = makeEcharts();
    const { ff, windowObj } = loadFreshFf({ echarts });

    ff.DispatchAction({
      actions: [{
        type: 'renderChart', id: 'C1', chartType: '"type":"bar"', legend: true,
        url: '/Chart/Data', showTooltip: true, chartTypeName: 'bar', noCartesianAxes: false
      }]
    });

    expect(echarts.init).toHaveBeenCalledTimes(1);
    expect(echarts.init.mock.calls[0][0].id).toBe('C1');
    expect(echarts.init.mock.calls[0][1]).toBe('default');

    expect(windowObj.C1Chart).toBeDefined();
    expect(windowObj.C1ChartType).toBe('"type":"bar"');
    expect(windowObj.C1ChartLegend).toBe('true');
    expect(typeof windowObj.C1ChartLegend).toBe('string');
    expect(windowObj.C1ChartUrl).toBe('/Chart/Data');
  });

  test('theme set: passed through to echarts.init verbatim', () => {
    appendDiv('C2');
    const echarts = makeEcharts();
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C2', theme: 'dark', chartType: '"type":"pie"', legend: false, url: '', showTooltip: false, chartTypeName: 'pie', noCartesianAxes: true }]
    });
    expect(echarts.init.mock.calls[0][1]).toBe('dark');
  });

  test('legend false serializes to the STRING "false" (RefreshChart compares with == "true")', () => {
    appendDiv('C3');
    const echarts = makeEcharts();
    const { ff, windowObj } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C3', chartType: '"type":"bar"', legend: false, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
    });
    expect(windowObj.C3ChartLegend).toBe('false');
  });

  test('setOption: title included only when action.title is set', () => {
    appendDiv('C4');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C4', title: 'Sales', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
    });
    expect(chartInstance.setOption.mock.calls[0][0].title).toEqual({ text: 'Sales' });
  });

  test('setOption: no title key when action.title is absent', () => {
    appendDiv('C5');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C5', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
    });
    expect(chartInstance.setOption.mock.calls[0][0].title).toBeUndefined();
  });

  test('setOption: scatter tooltip.formatter builds the legacy 4-line label string', () => {
    appendDiv('C6');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{
        type: 'renderChart', id: 'C6', chartType: '"type":"scatter"', legend: true, url: '',
        showTooltip: true, chartTypeName: 'scatter', noCartesianAxes: false,
        nameX: 'X', nameY: 'Y', nameAddition: 'A', nameCategory: 'C'
      }]
    });
    const opt = chartInstance.setOption.mock.calls[0][0];
    expect(typeof opt.tooltip.formatter).toBe('function');
    const label = opt.tooltip.formatter({ seriesName: 'S', value: [1, 2, 3, 4] });
    expect(label).toBe("S <br/>X:1 <br/>Y:2 <br/>A:3 <br/>C:4 <br/>");
  });

  test('setOption: line tooltip is {trigger:"axis"}', () => {
    appendDiv('C7');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C7', chartType: '"type":"line"', legend: true, url: '', showTooltip: true, chartTypeName: 'line', noCartesianAxes: false }]
    });
    expect(chartInstance.setOption.mock.calls[0][0].tooltip).toEqual({ trigger: 'axis' });
  });

  test('setOption: other types tooltip is {}', () => {
    appendDiv('C8');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C8', chartType: '"type":"bar"', legend: true, url: '', showTooltip: true, chartTypeName: 'bar', noCartesianAxes: false }]
    });
    expect(chartInstance.setOption.mock.calls[0][0].tooltip).toEqual({});
  });

  test('setOption: showTooltip false omits tooltip entirely', () => {
    appendDiv('C9');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C9', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
    });
    expect(chartInstance.setOption.mock.calls[0][0].tooltip).toBeUndefined();
  });

  test('setOption: noCartesianAxes true omits xAxis/yAxis', () => {
    appendDiv('C10');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C10', chartType: '"type":"pie"', legend: true, url: '', showTooltip: false, chartTypeName: 'pie', noCartesianAxes: true }]
    });
    const opt = chartInstance.setOption.mock.calls[0][0];
    expect(opt.xAxis).toBeUndefined();
    expect(opt.yAxis).toBeUndefined();
  });

  test('setOption: scatter axes use dashed splitLine + scale:true on yAxis', () => {
    appendDiv('C11');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{
        type: 'renderChart', id: 'C11', chartType: '"type":"scatter"', legend: true, url: '',
        showTooltip: false, chartTypeName: 'scatter', noCartesianAxes: false, nameX: 'X', nameY: 'Y'
      }]
    });
    const opt = chartInstance.setOption.mock.calls[0][0];
    expect(opt.xAxis).toEqual({ name: 'X', type: 'value', splitLine: { lineStyle: { type: 'dashed' } } });
    expect(opt.yAxis).toEqual({ name: 'Y', splitLine: { lineStyle: { type: 'dashed' } }, scale: true });
  });

  test('setOption: isHorizontal swaps xAxis/yAxis name+category placement', () => {
    appendDiv('C12');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{
        type: 'renderChart', id: 'C12', chartType: '"type":"bar"', legend: true, url: '',
        showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false, isHorizontal: true, nameX: 'X', nameY: 'Y'
      }]
    });
    const opt = chartInstance.setOption.mock.calls[0][0];
    expect(opt.xAxis).toEqual({ name: 'Y' });
    expect(opt.yAxis).toEqual({ name: 'X', type: 'category' });
  });

  test('setOption: default (not horizontal) axis placement', () => {
    appendDiv('C13');
    const chartInstance = { setOption: jest.fn() };
    const echarts = makeEcharts({ init: jest.fn(() => chartInstance) });
    const { ff } = loadFreshFf({ echarts });
    ff.DispatchAction({
      actions: [{
        type: 'renderChart', id: 'C13', chartType: '"type":"bar"', legend: true, url: '',
        showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false, isHorizontal: false, nameX: 'X', nameY: 'Y'
      }]
    });
    const opt = chartInstance.setOption.mock.calls[0][0];
    expect(opt.xAxis).toEqual({ name: 'X', type: 'category' });
    expect(opt.yAxis).toEqual({ name: 'Y' });
  });

  test('after 100ms, ff.RefreshChart(id) fires', () => {
    jest.useFakeTimers();
    try {
      appendDiv('C14');
      const echarts = makeEcharts();
      const { ff, windowObj } = loadFreshFf({ echarts });
      const refreshSpy = jest.fn();
      windowObj.ff.RefreshChart = refreshSpy;

      ff.DispatchAction({
        actions: [{ type: 'renderChart', id: 'C14', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
      });
      expect(refreshSpy).not.toHaveBeenCalled();
      jest.advanceTimersByTime(100);
      expect(refreshSpy).toHaveBeenCalledWith('C14');
    } finally {
      jest.useRealTimers();
    }
  });

  test('opt-in window[id+"ChartSeriesParser"] registry (#332) is left untouched by renderChart', () => {
    appendDiv('C15');
    const echarts = makeEcharts();
    const { ff, windowObj } = loadFreshFf({ echarts });
    const parserFn = jest.fn();
    windowObj.C15ChartSeriesParser = parserFn;

    ff.DispatchAction({
      actions: [{ type: 'renderChart', id: 'C15', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
    });

    expect(windowObj.C15ChartSeriesParser).toBe(parserFn);
  });

  test('echarts not loaded: skips with a console.warn, never throws, no globals set', () => {
    appendDiv('C16');
    const { ff, windowObj } = loadFreshFf({ echarts: undefined });
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'renderChart', id: 'C16', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }] });
    }).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('echarts is not loaded'));
    expect(windowObj.C16Chart).toBeUndefined();
    warnSpy.mockRestore();
  });

  test('missing target element: skips with a console.warn, never throws', () => {
    // No appendDiv('C17') call — element intentionally absent.
    const echarts = makeEcharts();
    const { ff } = loadFreshFf({ echarts });
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'renderChart', id: 'C17', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }] });
    }).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('not found'));
    expect(echarts.init).not.toHaveBeenCalled();
    warnSpy.mockRestore();
  });

  test('missing id: safe no-op, never throws', () => {
    const echarts = makeEcharts();
    const { ff } = loadFreshFf({ echarts });
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'renderChart' }] });
    }).not.toThrow();
    expect(echarts.init).not.toHaveBeenCalled();
  });

  test('module deferral: renderChart dispatches DIRECTLY (no layui.use call) — echarts is not a layui module', () => {
    appendDiv('C18');
    const echarts = makeEcharts();
    const { ff, layui } = loadFreshFf({ echarts });
    ff._dispatchIslandWhenReady({
      actions: [{ type: 'renderChart', id: 'C18', chartType: '"type":"bar"', legend: true, url: '', showTooltip: false, chartTypeName: 'bar', noCartesianAxes: false }]
    });
    expect(layui.use).not.toHaveBeenCalled();
    expect(echarts.init).toHaveBeenCalledTimes(1);
  });
});
