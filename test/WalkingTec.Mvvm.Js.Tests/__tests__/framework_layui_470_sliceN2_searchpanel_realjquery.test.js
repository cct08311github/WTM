// Adversarial-review regression suite for Issue #470 Slice N2
// (<wt:searchpanel>/SearchPanelTagHelper's opt-in delegated click/myclick
// wiring — WtmUIOptions.UseSelectIslandRender, default OFF).
//
// WHY THIS FILE EXISTS (separate from framework_layui_470_sliceN2_searchpanel.test.js):
// that file's makeRealishJQuery() mock implements .trigger() as an
// unconditional walk from the target node up to `document`, invoking every
// matching handler at every level regardless of whether an earlier handler
// called e.stopPropagation() (its `stopPropagation: jest.fn()` is a no-op
// spy, not a real propagation gate — see that file's own header comment).
// That is NOT how real DOM bubbling or real jQuery event dispatch behaves,
// and it is exactly the gap that let a real regression ship: the
// searchPanelInit island's collapse-guard handler
// ($('#'+titleId+' .layui-btn').on('click', e => e.stopPropagation())) is
// bound DIRECTLY on the search button — a real e.stopPropagation() call
// there halts native bubbling before a document-level DELEGATED listener
// (jQuery's own single native `document` listener) ever runs, so a genuine
// user click silently never reached ff._searchPanelClick. The mock-based
// suite could not have caught this because its trigger() never models
// "stopPropagation blocks ancestor delegation."
//
// This file drives the REAL, vendored jQuery 3.2.1 (already committed at
// demo/WalkingTec.Mvvm.Demo/wwwroot/jquery.min.js — the same version/library
// the adversarial review used to reproduce the bug) against the REAL jsdom
// `document` (jest's default testEnvironment) and the REAL, unmodified
// framework_layui.js source, so genuine DOM bubbling/stopPropagation
// semantics are in play. It reproduces the exact production markup shape
// (SearchPanelTagHelper.cs's <div id="{titleId}"> wrapping the search+reset
// buttons, itself inside <h2 class="layui-colla-title">) and fires a REAL
// native click via `btnEl.click()` — not a hand-rolled trigger — plus a real
// jQuery `.trigger('myclick', true)` for the ff.RefreshGrid contract.
//
// Security note: every DOM fixture built here via innerHTML is a hardcoded,
// test-authored literal (mirroring SearchPanelTagHelper.cs's own emitted
// shape) — never external/untrusted input.

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const layuiJsPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const layuiSrc = fs.readFileSync(layuiJsPath, 'utf8');

const jqueryPath = path.resolve(
  __dirname,
  '../../../demo/WalkingTec.Mvvm.Demo/wwwroot/jquery.min.js'
);
const jquerySrc = fs.readFileSync(jqueryPath, 'utf8');

function makeSearchPanelLayui(overrides) {
  return Object.assign(
    {
      use: jest.fn((mods, cb) => cb()),
      setter: { pageTabs: false },
      element: { init: jest.fn(), on: jest.fn() },
      table: { reload: jest.fn() },
    },
    overrides || {}
  );
}

// Builds a fresh vm context: real jQuery (bound to the REAL jsdom
// `document`) + the real framework_layui.js source, exactly as the browser
// would load them (jquery.min.js first, then framework_layui.js referencing
// the resulting window.$).
function loadRealFf(opts) {
  opts = opts || {};
  const layui = opts.layui || makeSearchPanelLayui();
  const ctxInit = {
    document,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    layui,
    navigator: global.navigator,
    location: global.location,
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  };
  const ctx = vm.createContext(ctxInit);
  ctx.window = ctx; // jQuery's UMD wrapper resolves `window` to this self-reference
  new vm.Script(jquerySrc, { filename: 'jquery.min.js' }).runInContext(ctx);
  if (typeof ctx.$ !== 'function') {
    throw new Error('real jQuery failed to attach to the vm context (window.$)');
  }
  new vm.Script(layuiSrc, { filename: 'framework_layui.js' }).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx, layui, $: ctx.$ };
}

afterEach(() => {
  document.body.innerHTML = '';
});

// Mirrors SearchPanelTagHelper.cs's emitted shape closely enough to exercise
// real bubbling: the search/reset buttons sit inside `#titleId`, which sits
// inside `h2.layui-colla-title`, which sits inside the collapse wrapper,
// which sits inside the owning `<form>`. The whole thing is wrapped in a
// `dialogId` container div — ff.RefreshGrid(dialogid) looks for
// `#dialogid form a[IsSearchButton]` (a NESTED form under the container),
// so dialogId must be distinct from formId, exactly like a real page
// (`LAY_app_body`) or dialog (`#dialogXYZ`) container wraps its own form.
function buildRealPanel({ dialogId, titleId, resetBtnId, searchBtnId, formId, gridId }) {
  document.body.innerHTML =
    '<div id="' + dialogId + '">' +
    '<form id="' + formId + '">' +
    '  <div class="layui-collapse" lay-filter="' + titleId + 'x">' +
    '    <div class="layui-colla-item">' +
    '      <h2 class="layui-colla-title">Search Condition' +
    '        <div id="' + titleId + '">' +
    '          <a href="javascript:void(0)" class="layui-btn layui-btn-sm" id="' +
    searchBtnId +
    '" IsSearchButton data-wtm-search data-wtm-search-grids="' +
    gridId +
    '" data-wtm-form="' +
    formId +
    '" data-wtm-fieldpre="">Search</a>' +
    '          <button type="button" class="layui-btn layui-btn-sm" id="' +
    resetBtnId +
    '">Reset</button>' +
    '        </div>' +
    '      </h2>' +
    '      <div class="layui-colla-content"></div>' +
    '    </div>' +
    '  </div>' +
    '</form>' +
    '<table id="' + gridId + '"></table>' +
    '</div>';
}

describe('#470 Slice N2 — real jQuery 3.2.1 + real jsdom bubbling (adversarial-review regression)', () => {
  test('a REAL native click on the search button DOES refresh the grid (the exact regression the review found)', () => {
    buildRealPanel({
      dialogId: 'rjDialog1',
      titleId: 'rjTitle1',
      resetBtnId: 'rjReset1',
      searchBtnId: 'rjSearchBtn1',
      formId: 'rjForm1',
      gridId: 'rjGrid1',
    });
    const { ff, windowObj, layui } = loadRealFf();
    windowObj.rjGrid1defaultfilter = { where: {} };
    windowObj.rjGrid1filterback = { page: { curr: 5 } };
    windowObj.rjGrid1url = '/RJ1/List';
    windowObj.ff.GetSearchFormData = jest.fn(() => ({}));

    ff.DispatchAction({
      actions: [{ type: 'searchPanelInit', titleId: 'rjTitle1', resetBtnId: 'rjReset1', show: true }],
    });

    const btn = document.getElementById('rjSearchBtn1');
    // A REAL native click — goes through the actual browser (jsdom) event
    // dispatch/bubbling machinery, NOT a hand-rolled trigger.
    btn.click();

    expect(layui.table.reload).toHaveBeenCalledTimes(1);
    const [gid, opt] = layui.table.reload.mock.calls[0];
    expect(gid).toBe('rjGrid1');
    expect(opt.url).toBe('/RJ1/List');
    // keeppage=null (native click) resets page.curr to 1.
    expect(opt.page.curr).toBe(1);
  });

  test('a REAL native click does not double-fire the refresh (document-delegated "click" half stays unreachable, as designed)', () => {
    buildRealPanel({
      dialogId: 'rjDialog2',
      titleId: 'rjTitle2',
      resetBtnId: 'rjReset2',
      searchBtnId: 'rjSearchBtn2',
      formId: 'rjForm2',
      gridId: 'rjGrid2',
    });
    const { ff, windowObj, layui } = loadRealFf();
    windowObj.rjGrid2defaultfilter = { where: {} };
    windowObj.rjGrid2filterback = { page: { curr: 1 } };
    windowObj.rjGrid2url = '/RJ2/List';
    windowObj.ff.GetSearchFormData = jest.fn(() => ({}));

    ff.DispatchAction({
      actions: [{ type: 'searchPanelInit', titleId: 'rjTitle2', resetBtnId: 'rjReset2', show: true }],
    });

    document.getElementById('rjSearchBtn2').click();

    expect(layui.table.reload).toHaveBeenCalledTimes(1);
  });

  test('clicking the search button does NOT bubble to an ancestor click handler (collapse-toggle-guard behavior preserved)', () => {
    buildRealPanel({
      dialogId: 'rjDialog3',
      titleId: 'rjTitle3',
      resetBtnId: 'rjReset3',
      searchBtnId: 'rjSearchBtn3',
      formId: 'rjForm3',
      gridId: 'rjGrid3',
    });
    const { ff, windowObj, $ } = loadRealFf();
    windowObj.rjGrid3defaultfilter = { where: {} };
    windowObj.rjGrid3filterback = { page: { curr: 1 } };
    windowObj.rjGrid3url = '/RJ3/List';
    windowObj.ff.GetSearchFormData = jest.fn(() => ({}));

    ff.DispatchAction({
      actions: [{ type: 'searchPanelInit', titleId: 'rjTitle3', resetBtnId: 'rjReset3', show: true }],
    });

    // Simulate layui's own collapse-toggle handler, bound on the ancestor
    // `.layui-colla-title` (the real DOM node the search button sits
    // inside) — this is what the stopPropagation() call in
    // _renderSearchPanelInitAction exists to guard against.
    const collapseToggleSpy = jest.fn();
    $('.layui-colla-title').on('click', collapseToggleSpy);

    document.getElementById('rjSearchBtn3').click();

    expect(collapseToggleSpy).not.toHaveBeenCalled();
  });

  test('ff.RefreshGrid\'s real jQuery sb.trigger("myclick", true) still refreshes via the document-delegated listener, preserving keeppage=true page semantics', () => {
    buildRealPanel({
      dialogId: 'rjDialog4',
      titleId: 'rjTitle4',
      resetBtnId: 'rjReset4',
      searchBtnId: 'rjSearchBtn4',
      formId: 'rjForm4',
      gridId: 'rjGrid4',
    });
    const { ff, windowObj, layui } = loadRealFf();
    windowObj.rjGrid4defaultfilter = { where: {} };
    windowObj.rjGrid4filterback = { page: { curr: 8 } };
    windowObj.rjGrid4url = '/RJ4/List';
    windowObj.ff.GetSearchFormData = jest.fn(() => ({}));

    ff.DispatchAction({
      actions: [{ type: 'searchPanelInit', titleId: 'rjTitle4', resetBtnId: 'rjReset4', show: true }],
    });

    ff.RefreshGrid('rjDialog4');

    expect(layui.table.reload).toHaveBeenCalledTimes(1);
    const [gid, opt] = layui.table.reload.mock.calls[0];
    expect(gid).toBe('rjGrid4');
    // keeppage=true (myclick) preserves page.curr (8), does not reset to 1.
    expect(opt.page.curr).toBe(8);
  });

  test('a real native click and a real myclick trigger each fire exactly once (no cross-path double-fire between the direct click binding and the document-delegated myclick binding)', () => {
    buildRealPanel({
      dialogId: 'rjDialog5',
      titleId: 'rjTitle5',
      resetBtnId: 'rjReset5',
      searchBtnId: 'rjSearchBtn5',
      formId: 'rjForm5',
      gridId: 'rjGrid5',
    });
    const { ff, windowObj, layui, $ } = loadRealFf();
    windowObj.rjGrid5defaultfilter = { where: {} };
    windowObj.rjGrid5filterback = { page: { curr: 1 } };
    windowObj.rjGrid5url = '/RJ5/List';
    windowObj.ff.GetSearchFormData = jest.fn(() => ({}));

    ff.DispatchAction({
      actions: [{ type: 'searchPanelInit', titleId: 'rjTitle5', resetBtnId: 'rjReset5', show: true }],
    });

    const btn = document.getElementById('rjSearchBtn5');
    btn.click();
    expect(layui.table.reload).toHaveBeenCalledTimes(1);

    $(btn).trigger('myclick', true);
    expect(layui.table.reload).toHaveBeenCalledTimes(2);
  });

  test('reset button click still calls ff.resetForm with the real form id (unaffected by the search-button fix)', () => {
    buildRealPanel({
      dialogId: 'rjDialog6',
      titleId: 'rjTitle6',
      resetBtnId: 'rjReset6',
      searchBtnId: 'rjSearchBtn6',
      formId: 'rjForm6',
      gridId: 'rjGrid6',
    });
    const { ff, windowObj } = loadRealFf();
    const resetSpy = jest.fn();
    windowObj.ff.resetForm = resetSpy;

    ff.DispatchAction({
      actions: [{ type: 'searchPanelInit', titleId: 'rjTitle6', resetBtnId: 'rjReset6', show: true }],
    });

    document.getElementById('rjReset6').click();

    expect(resetSpy).toHaveBeenCalledWith('rjForm6');
  });
});
