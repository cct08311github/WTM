// Tests for Issue #470 Slice K — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF — the SAME flag #470 Slice J's 'renderSelect' island uses for
// <wt:combobox>/<wt:tree>) eval-free 'renderTransfer' JSON island for
// <wt:transfer>.
//
// This file drives the REAL ff.DispatchAction / ff._renderTransferAction /
// ff._islandModulesFor / ff._dispatchIslandWhenReady end-to-end against a
// FRESH vm instance of the actual shipped framework_layui.js source (real
// jsdom `document`) — not a source-sweep or a hand-rolled reimplementation —
// following the same convention as
// framework_layui_470_sliceJ_renderselect.test.js. layui.transfer.render/
// getData are stubbed (layui.transfer is a real layui.use(...) module in
// production, never loaded under jsdom).

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

function makeJQueryMock() {
  const $ = function () { return { 0: null, length: 0 }; };
  $.get = jest.fn();
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.fn = {};
  return $;
}

function makeTransferLayui(overrides) {
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    form: { render: jest.fn() },
    transfer: {
      render: jest.fn(() => ({ __fake: 'transferInstance' })),
      getData: jest.fn(() => [])
    }
  }, overrides || {});
}

function loadFreshFf(opts) {
  opts = opts || {};
  const $ = opts.$ || makeJQueryMock();
  const layui = opts.layui || makeTransferLayui();
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

function appendTransferDom(id) {
  document.body.innerHTML = '';
  const widget = document.createElement('div');
  widget.id = id;
  const container = document.createElement('div');
  container.id = id + 'div';
  document.body.appendChild(widget);
  document.body.appendChild(container);
  return { widget, container };
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice K — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice K changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderTransfer' case delegates to ff._renderTransferAction, no inline body", () => {
    const block = active.match(/case\s+['"]renderTransfer['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderTransferAction\(\s*action\s*\)/);
  });

  test('_renderTransferAction resolves changeFunc through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = active.match(/_renderTransferAction:\s*function[\s\S]*?\n\s{4}\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFunc\s*\)/);
    expect(block[0]).not.toMatch(/\beval\(/);
    expect(block[0]).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'renderTransfer' HAS an _islandModulesFor entry — layui.transfer IS a layui.use(...) module, unlike xm-select", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(fn).toMatch(/renderTransfer/);
    expect(fn).toMatch(/needed\.transfer\s*=\s*true/);
    expect(fn).toMatch(/mods\.push\('transfer'\)/);
  });
});

// ---------------------------------------------------------------------------
// (1) dispatch renderTransfer -> layui.transfer.render called with expected
// config; 'transfer' added to needed modules
// ---------------------------------------------------------------------------
describe('#470 Slice K — renderTransfer basic render', () => {
  test('dispatch calls layui.transfer.render with the expected config', () => {
    appendTransferDom('TR1');
    const { ff, layui } = loadFreshFf();

    ff.DispatchAction({
      actions: [{
        type: 'renderTransfer', id: 'TR1', el: '#TR1', name: 'Field1',
        title: ['Left', 'Right'],
        data: [{ value: '1', title: 'One', disabled: false, checked: false }],
        defaultValue: []
      }]
    });

    expect(layui.transfer.render).toHaveBeenCalledTimes(1);
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(cfg.elem).toBe('#TR1');
    expect(cfg.id).toBe('TR1');
    expect(cfg.title).toEqual(['Left', 'Right']);
    expect(cfg.data).toEqual([{ value: '1', title: 'One', disabled: false, checked: false }]);
    expect(cfg.value).toBeUndefined();
  });

  test('module deferral: ff._dispatchIslandWhenReady defers renderTransfer through layui.use(["transfer"], cb)', () => {
    appendTransferDom('TR1b');
    const { ff, layui } = loadFreshFf();
    const payload = {
      actions: [{ type: 'renderTransfer', id: 'TR1b', el: '#TR1b', name: 'F' }]
    };

    ff._dispatchIslandWhenReady(payload);

    expect(layui.use).toHaveBeenCalledTimes(1);
    expect(layui.use.mock.calls[0][0]).toEqual(['transfer']);
    expect(layui.transfer.render).toHaveBeenCalledTimes(1);
  });

  test('defaultValue non-empty: cfg.value carries it', () => {
    appendTransferDom('TR2');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR2', el: '#TR2', name: 'F', defaultValue: ['a', 'b'] }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(cfg.value).toEqual(['a', 'b']);
  });

  test('layui.transfer not loaded: skips with a console.warn, never throws', () => {
    appendTransferDom('TR3');
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
      ctx.ff.DispatchAction({ actions: [{ type: 'renderTransfer', id: 'TR3', el: '#TR3' }] });
    }).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('layui.transfer is not loaded'));
    warnSpy.mockRestore();
  });

  test('missing id/el: safe no-op, never throws', () => {
    const { ff, layui } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'renderTransfer' }] });
    }).not.toThrow();
    expect(layui.transfer.render).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// (2) onchange wiring — defaultFunc write-back THEN guarded changeFn, same
// (data,index,transferIns) argument shape
// ---------------------------------------------------------------------------
describe('#470 Slice K — renderTransfer onchange wires defaultFunc + guarded changeFn', () => {
  test('onchange: defaultFunc write-back runs, replacing hidden inputs under #{id}div with getData() results', () => {
    const { container } = appendTransferDom('TR4');
    // Pre-seed a stale hidden input that must be removed.
    const stale = document.createElement('input');
    stale.type = 'hidden';
    stale.name = 'Field1';
    stale.value = 'stale';
    container.appendChild(stale);

    const layui = makeTransferLayui({
      transfer: {
        render: jest.fn(() => ({ __fake: 'ins' })),
        getData: jest.fn(() => [{ value: 'x' }, { value: 'y' }])
      }
    });
    const { ff } = loadFreshFf({ layui });

    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR4', el: '#TR4', name: 'Field1' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    cfg.onchange({ some: 'data' }, 0);

    const inputs = container.querySelectorAll('input[name="Field1"]');
    expect(inputs.length).toBe(2);
    expect(Array.from(inputs).map((i) => i.value)).toEqual(['x', 'y']);
    expect(inputs[0].value).not.toBe('stale');
  });

  test('onchange: defaultFunc runs FIRST, then the guarded changeFn(data,index,transferIns)', () => {
    appendTransferDom('TR5');
    const layui = makeTransferLayui({
      transfer: {
        render: jest.fn(() => ({ __fake: 'ins' })),
        getData: jest.fn(() => [])
      }
    });
    const { ff, windowObj } = loadFreshFf({ layui });
    const callOrder = [];
    windowObj.myChangeFunc = jest.fn(() => { callOrder.push('changeFunc'); });
    layui.transfer.getData.mockImplementation(() => { callOrder.push('defaultFunc'); return []; });

    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR5', el: '#TR5', name: 'F', changeFunc: 'myChangeFunc' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    const payload = { arr: [] };
    cfg.onchange(payload, 1);

    expect(callOrder).toEqual(['defaultFunc', 'changeFunc']);
    expect(windowObj.myChangeFunc).toHaveBeenCalledWith(payload, 1, { __fake: 'ins' });
  });

  test('no changeFunc: onchange still runs defaultFunc, never throws', () => {
    appendTransferDom('TR6');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR6', el: '#TR6', name: 'F' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(() => cfg.onchange({ arr: [] }, 0)).not.toThrow();
    expect(layui.transfer.getData).toHaveBeenCalledWith('TR6');
  });
});

// ---------------------------------------------------------------------------
// (3) guarded changeFunc resolution — adversarial names never throw/eval
// ---------------------------------------------------------------------------
describe('#470 Slice K — changeFunc adversarial resolution (ff._resolveGuardedWindowFn)', () => {
  test('dotted name "a.b" is rejected — onchange never calls it, never throws', () => {
    appendTransferDom('TR7');
    const { ff, windowObj, layui } = loadFreshFf();
    const spy = jest.fn();
    windowObj.a = { b: spy };
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR7', el: '#TR7', name: 'F', changeFunc: 'a.b' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(() => cfg.onchange({ arr: [] }, 0)).not.toThrow();
    expect(spy).not.toHaveBeenCalled();
  });

  test('denylisted global name "eval" is rejected even though own+callable+identifier', () => {
    appendTransferDom('TR8');
    const { ff, windowObj, layui } = loadFreshFf();
    const spy = jest.fn();
    windowObj.eval = spy;
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR8', el: '#TR8', name: 'F', changeFunc: 'eval' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(() => cfg.onchange({ arr: [] }, 0)).not.toThrow();
    expect(spy).not.toHaveBeenCalled();
  });

  test('non-own (prototype-chain) name "toString" is rejected', () => {
    appendTransferDom('TR9');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR9', el: '#TR9', name: 'F', changeFunc: 'toString' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(() => cfg.onchange({ arr: [] }, 0)).not.toThrow();
  });

  test('non-function value is rejected', () => {
    appendTransferDom('TR10');
    const { ff, windowObj, layui } = loadFreshFf();
    windowObj.notAFunction = 'just a string';
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR10', el: '#TR10', name: 'F', changeFunc: 'notAFunction' }]
    });
    const cfg = layui.transfer.render.mock.calls[0][0];
    expect(() => cfg.onchange({ arr: [] }, 0)).not.toThrow();
  });

  test('this file never causes a new eval( or new Function( call site to appear', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });
});

// ---------------------------------------------------------------------------
// (4) init default value — hidden inputs appended for each pre-selected value
// ---------------------------------------------------------------------------
describe('#470 Slice K — renderTransfer init default value hidden inputs', () => {
  test('non-empty defaultValue: one hidden input per value is appended to #{id}div', () => {
    const { container } = appendTransferDom('TR11');
    const { ff } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR11', el: '#TR11', name: 'Field1', defaultValue: ['1', '2', '3'] }]
    });
    const inputs = container.querySelectorAll('input[name="Field1"]');
    expect(inputs.length).toBe(3);
    expect(Array.from(inputs).map((i) => i.value)).toEqual(['1', '2', '3']);
    expect(Array.from(inputs).every((i) => i.type === 'hidden')).toBe(true);
  });

  test('empty/missing defaultValue: no hidden inputs appended', () => {
    const { container } = appendTransferDom('TR12');
    const { ff } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR12', el: '#TR12', name: 'Field1' }]
    });
    expect(container.querySelectorAll('input[name="Field1"]').length).toBe(0);
  });

  test('missing #{id}div container: does not throw (init block silently skipped)', () => {
    document.body.innerHTML = '';
    const widget = document.createElement('div');
    widget.id = 'TR13';
    document.body.appendChild(widget);
    // No 'TR13div' container appended.
    const { ff } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({
        actions: [{ type: 'renderTransfer', id: 'TR13', el: '#TR13', name: 'Field1', defaultValue: ['1'] }]
      });
    }).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// (5) Disabled post-render tweak
// ---------------------------------------------------------------------------
describe('#470 Slice K — renderTransfer Disabled post-render tweak', () => {
  test('disabled:true — checkbox/input descendants of the widget are disabled, and transfer.render() is re-invoked with no args', () => {
    const { widget } = appendTransferDom('TR14');
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    widget.appendChild(cb);
    const textInput = document.createElement('input');
    textInput.type = 'text';
    widget.appendChild(textInput);

    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR14', el: '#TR14', name: 'F', disabled: true }]
    });

    expect(cb.disabled).toBe(true);
    expect(textInput.disabled).toBe(true);
    // First call is the real render(opts); second is the legacy no-arg tweak.
    expect(layui.transfer.render).toHaveBeenCalledTimes(2);
    expect(layui.transfer.render.mock.calls[1]).toEqual([]);
  });

  test('disabled:false (default) — no post-render tweak, render() called exactly once', () => {
    appendTransferDom('TR15');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'renderTransfer', id: 'TR15', el: '#TR15', name: 'F' }]
    });
    expect(layui.transfer.render).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// (6) dispatch reaches ff._renderTransferAction via the OpenDialog2 (selector
// search-panel) path too — mirrors framework_layui_470_sliceJ_renderselect
// .test.js's harness.
// ---------------------------------------------------------------------------
describe('#470 Slice K — dispatch reaches layui.transfer.render via ff.OpenDialog2 (selector path)', () => {
  function makeJQueryMockForOpenDialog2() {
    function wrap(el) { return { 0: el, length: el ? 1 : 0 }; }
    const $ = function (selector) {
      if (typeof selector === 'string' && selector.charAt(0) === '#') {
        return wrap(document.getElementById(selector.slice(1)));
      }
      return wrap(null);
    };
    $.ajax = jest.fn((opts) => {
      const request = { getResponseHeader: () => null };
      opts.success(OPEN_DIALOG2_RESPONSE_HTML, 'success', request);
    });
    $.cookie = jest.fn();
    $.fn = {};
    return $;
  }

  function loadFreshFfForOpenDialog2(layui, $) {
    const ctx = vm.createContext({
      window: {},
      document,
      layui: layui,
      console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: $,
      DOMParser: global.DOMParser,
      DOMPurify: { sanitize: (html) => (html === undefined || html === null ? '' : html) },
      DONOTUSE_TABLAYID: undefined,
      DONOTUSE_COOKIEPRE: '',
      DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    return { ff: ctx.ff, windowObj: ctx };
  }

  function makeOpenDialog2Layui(transferRender) {
    return {
      use: jest.fn((mods, cb) => cb()),
      form: { render: jest.fn() },
      transfer: { render: transferRender, getData: jest.fn(() => []) },
      layer: {
        load: jest.fn(() => 1),
        close: jest.fn(),
        alert: jest.fn(),
        full: jest.fn(),
        open: jest.fn((opts) => {
          const container = document.createElement('div');
          container.innerHTML = opts.content;
          document.body.appendChild(container);
          if (typeof opts.success === 'function') { opts.success(container); }
          return 'mockWinId';
        })
      }
    };
  }

  function makeOpenDialog2TempEl(id, innerHtml) {
    const el = document.createElement('div');
    el.id = id;
    el.innerHTML = innerHtml;
    document.body.appendChild(el);
    return el;
  }

  const OPEN_DIALOG2_RESPONSE_HTML =
    '<div>wtVar_x = table.render(xoption);</div>' +
    '<table id="g1" lay-filter="f1"></table>' +
    '$$SearchPanel$$';

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
  });

  test('a renderTransfer island tokenized into the search panel reaches layui.transfer.render end-to-end', () => {
    const transferRender = jest.fn(() => ({ __fake: 'ins' }));
    const layui = makeOpenDialog2Layui(transferRender);
    const $ = makeJQueryMockForOpenDialog2();
    const payload = {
      type: 'renderTransfer', id: 'SelectorTransfer470k', el: '#SelectorTransfer470k', name: 'F',
      data: [{ value: '1', title: 'One', disabled: false, checked: false }]
    };
    makeOpenDialog2TempEl(
      'Temp470k',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$' +
      '<div id="SelectorTransfer470k"></div><div id="SelectorTransfer470kdiv"></div>'
    );
    const { ff } = loadFreshFfForOpenDialog2(layui, $);

    ff.OpenDialog2('/some/search/url', 'w470k', 'Title', 500, 400, '#Temp470k');

    expect(transferRender).toHaveBeenCalledTimes(1);
    // Module deferral proven via layui.use(['transfer'], ...).
    expect(layui.use).toHaveBeenCalledWith(['transfer'], expect.any(Function));
  });
});
