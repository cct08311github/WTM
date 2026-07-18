// Tests for Issue #470 Slice I: Slider ChangeFunc/OnTipsFunc + ColorPicker
// ChangeFunc callback island migration — closing out the last two inline
// <script> callback fallbacks SliderTagHelper/ColorPickerTagHelper still
// emitted after #552 (#470-E).
//
// Prior to this slice, SliderTagHelper's 'slider' island (#552) and
// ColorPickerTagHelper's 'colorpicker' island (#552) were callback-free
// ONLY — any ChangeFunc/OnTipsFunc forced the field back onto an inline
// <script>. This slice extends both islands so a PLAIN-IDENTIFIER callback
// name (the only shape ff._resolveGuardedWindowFn can safely resolve by
// name) migrates too — mirroring DateTimeTagHelper's Slice H laydate
// migration exactly:
//
//   1. _renderSliderAction resolves action.changeFn/action.onTipsFn through
//      the SAME #558/#601/Slice-H guarded ff._resolveGuardedWindowFn lookup,
//      and wires them AFTER the built-in write-back with the exact same
//      argument shape the legacy inline <script> passed:
//      changeFn(value, sliderIns), onTipsFn(value, sliderIns) via setTips.
//   2. _renderColorpickerAction resolves action.changeFn the same way and
//      wires it AFTER the built-in hidden-field write-back with the single
//      "data" argument FormatFuncName's legacy "(data)" call used.
//   3. A failed resolution (non-identifier / denylisted / missing / not a
//      function) silently skips JUST that one callback — never throws,
//      never eval()s.
//   4. framework_layui.js's active-code eval( count is unchanged (still
//      exactly 1).
//
// Following the same convention as framework_layui_470_sliceH_laydate.test.js:
// loads a FRESH instance of the real framework_layui.js into its own vm
// context (real jsdom `document`) and calls the REAL ff.DispatchAction
// directly, so these tests exercise the actual shipped code, not a
// hand-rolled reimplementation. The OpenDialog2 end-to-end test mirrors
// framework_layui_633_opendialog2_selector_panel.test.js's harness.

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
describe('#470 Slice I — source sweep', () => {
  function fnBlock(name) {
    const re = new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ':\\s*function[\\s\\S]{0,8000}?\\n    \\},');
    const block = active.match(re);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('_renderSliderAction resolves changeFn/onTipsFn through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = fnBlock('_renderSliderAction');
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFn\s*\)/);
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.onTipsFn\s*\)/);
  });

  test('_renderColorpickerAction resolves changeFn through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = fnBlock('_renderColorpickerAction');
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFn\s*\)/);
  });

  test('_renderSliderAction never contains eval( or new Function(', () => {
    const block = fnBlock('_renderSliderAction');
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });

  test('_renderColorpickerAction never contains eval( or new Function(', () => {
    const block = fnBlock('_renderColorpickerAction');
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });

  test('active-code eval( count is still exactly 1 after #470 Slice I changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Real ff.DispatchAction harness (mirrors #601's/Slice H's loadFreshFf)
// ---------------------------------------------------------------------------
const FAKE_SLIDER_INS = { __fake: 'sliderIns' };

function loadFreshFf() {
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const sliderRender = jest.fn(() => FAKE_SLIDER_INS);
  const colorpickerRender = jest.fn();
  const ctx = vm.createContext({
    window: {},
    document,
    layui: {
      slider: { render: sliderRender },
      colorpicker: { render: colorpickerRender },
    },
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jqueryMock,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx, sliderRender, colorpickerRender };
}

function appendHidden(id) {
  const el = document.createElement('input');
  el.type = 'hidden';
  el.id = id;
  document.body.appendChild(el);
  return el;
}

afterEach(() => { document.body.innerHTML = ''; });

describe('#470 Slice I — real ff.DispatchAction slider callback behavior', () => {
  test('(a) changeFn identifier: resolved window fn is called with (value, sliderIns) AFTER the built-in write-back', () => {
    const { ff, windowObj, sliderRender } = loadFreshFf();
    const el0 = appendHidden('SF0');
    const spy = jest.fn(() => {
      // The built-in write-back must already be visible when the caller
      // callback runs — proving write-back-then-chain ordering.
      expect(el0.value).toBe('42');
    });
    windowObj.myChange = spy;

    ff.DispatchAction({
      actions: [{
        type: 'slider', opts: { elem: '#S1' },
        fieldId0: 'SF0', changeFn: 'myChange'
      }],
    });

    expect(sliderRender).toHaveBeenCalledTimes(1);
    const opts = sliderRender.mock.calls[0][0];
    expect(typeof opts.change).toBe('function');
    opts.change(42);
    expect(el0.value).toBe('42');
    expect(spy).toHaveBeenCalledWith(42, FAKE_SLIDER_INS);
  });

  test('(b) onTipsFn identifier: resolved window fn backs setTips and its return value is used', () => {
    const { ff, windowObj, sliderRender } = loadFreshFf();
    const spy = jest.fn((value, sliderIns) => {
      expect(sliderIns).toBe(FAKE_SLIDER_INS);
      return 'tip:' + value;
    });
    windowObj.myTips = spy;

    ff.DispatchAction({
      actions: [{ type: 'slider', opts: { elem: '#S2' }, onTipsFn: 'myTips' }],
    });

    const opts = sliderRender.mock.calls[0][0];
    expect(typeof opts.setTips).toBe('function');
    const result = opts.setTips(7);
    expect(spy).toHaveBeenCalledWith(7, FAKE_SLIDER_INS);
    expect(result).toBe('tip:7');
  });

  test('both changeFn and onTipsFn can be wired independently on the same field', () => {
    const { ff, windowObj, sliderRender } = loadFreshFf();
    const changeSpy = jest.fn();
    const tipsSpy = jest.fn(() => 'tip');
    windowObj.c1 = changeSpy;
    windowObj.t1 = tipsSpy;

    ff.DispatchAction({
      actions: [{
        type: 'slider', opts: { elem: '#S3' },
        changeFn: 'c1', onTipsFn: 't1'
      }],
    });

    const opts = sliderRender.mock.calls[0][0];
    opts.change(5);
    opts.setTips(5);
    expect(changeSpy).toHaveBeenCalledWith(5, FAKE_SLIDER_INS);
    expect(tipsSpy).toHaveBeenCalledWith(5, FAKE_SLIDER_INS);
  });

  test('range slider changeFn: built-in split write-back runs first, then changeFn receives the array value', () => {
    const { ff, windowObj, sliderRender } = loadFreshFf();
    const el0 = appendHidden('RF0');
    const el1 = appendHidden('RF1');
    const spy = jest.fn(() => {
      expect(el0.value).toBe('10');
      expect(el1.value).toBe('60');
    });
    windowObj.rangeChange = spy;

    ff.DispatchAction({
      actions: [{
        type: 'slider', opts: { elem: '#S4', range: true },
        fieldId0: 'RF0', fieldId1: 'RF1', changeFn: 'rangeChange'
      }],
    });

    const opts = sliderRender.mock.calls[0][0];
    opts.change([10, 60]);
    expect(spy).toHaveBeenCalledWith([10, 60], FAKE_SLIDER_INS);
  });

  describe('(d) adversarial changeFn/onTipsFn names — gate is skipped, never throws', () => {
    test('dotted name "a.b" is rejected — no change callback wired beyond the built-in write-back', () => {
      const { ff, windowObj, sliderRender } = loadFreshFf();
      const spy = jest.fn();
      windowObj.a = { b: spy };

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'slider', opts: { elem: '#S5' }, changeFn: 'a.b' }],
        });
      }).not.toThrow();

      const opts = sliderRender.mock.calls[0][0];
      expect(() => opts.change(1)).not.toThrow();
      expect(spy).not.toHaveBeenCalled();
    });

    test('denylisted global name "eval" is rejected even though own+callable+identifier', () => {
      const { ff, windowObj, sliderRender } = loadFreshFf();
      const spy = jest.fn();
      windowObj.eval = spy;

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'slider', opts: { elem: '#S6' }, onTipsFn: 'eval' }],
        });
      }).not.toThrow();

      const opts = sliderRender.mock.calls[0][0];
      expect(opts.setTips).toBeUndefined();
      expect(spy).not.toHaveBeenCalled();
    });

    test('non-function value is rejected', () => {
      const { ff, windowObj, sliderRender } = loadFreshFf();
      windowObj.notAFunction = 'just a string';

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'slider', opts: { elem: '#S7' }, changeFn: 'notAFunction' }],
        });
      }).not.toThrow();

      const opts = sliderRender.mock.calls[0][0];
      expect(() => opts.change(1)).not.toThrow();
    });

    test('missing/undefined callback names simply wire nothing beyond the built-in write-back — no throw', () => {
      const { ff, sliderRender } = loadFreshFf();
      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'slider', opts: { elem: '#S8' } }] });
      }).not.toThrow();
      const opts = sliderRender.mock.calls[0][0];
      expect(opts.setTips).toBeUndefined();
      expect(() => opts.change(1)).not.toThrow();
    });

    test('this file never causes a new eval( or new Function( call site to appear', () => {
      const matches = active.match(/\beval\(/g) || [];
      expect(matches).toHaveLength(1);
      expect(active).not.toMatch(/new\s+Function\s*\(/);
    });
  });

  test('a bare slider island with no changeFn/onTipsFn behaves exactly as the pre-Slice-I #552 case', () => {
    const { ff, sliderRender } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'slider', opts: { elem: '#S9', min: 0, max: 100 } }] });
    expect(sliderRender).toHaveBeenCalledTimes(1);
    const opts = sliderRender.mock.calls[0][0];
    expect(opts.elem).toBe('#S9');
    expect(opts.min).toBe(0);
    expect(opts.max).toBe(100);
    expect(opts.setTips).toBeUndefined();
    expect(typeof opts.change).toBe('function');
  });
});

describe('#470 Slice I — real ff.DispatchAction colorpicker callback behavior', () => {
  test('(c) changeFn identifier: value field is written AND changeFn is called with (data) AFTER the write-back', () => {
    const { ff, windowObj, colorpickerRender } = loadFreshFf();
    const el = appendHidden('CPF1');
    const spy = jest.fn(() => {
      expect(el.value).toBe('#ff0000');
    });
    windowObj.myColorChanged = spy;

    ff.DispatchAction({
      actions: [{
        type: 'colorpicker', opts: { elem: '#CP1' },
        valueFieldId: 'CPF1', changeFn: 'myColorChanged'
      }],
    });

    expect(colorpickerRender).toHaveBeenCalledTimes(1);
    const opts = colorpickerRender.mock.calls[0][0];
    expect(typeof opts.done).toBe('function');
    opts.done('#ff0000');
    expect(el.value).toBe('#ff0000');
    expect(spy).toHaveBeenCalledWith('#ff0000');
  });

  test('no changeFn: only the built-in write-back runs, no throw for the missing callback', () => {
    const { ff, colorpickerRender } = loadFreshFf();
    const el = appendHidden('CPF2');

    ff.DispatchAction({
      actions: [{ type: 'colorpicker', opts: { elem: '#CP2' }, valueFieldId: 'CPF2' }],
    });

    const opts = colorpickerRender.mock.calls[0][0];
    expect(() => opts.done('#00ff00')).not.toThrow();
    expect(el.value).toBe('#00ff00');
  });

  describe('adversarial changeFn names — gate is skipped, never throws', () => {
    test('dotted name is rejected — write-back still happens, callback is not called', () => {
      const { ff, windowObj, colorpickerRender } = loadFreshFf();
      const el = appendHidden('CPF3');
      const spy = jest.fn();
      windowObj.obj = { myColorChanged: spy };

      expect(() => {
        ff.DispatchAction({
          actions: [{
            type: 'colorpicker', opts: { elem: '#CP3' },
            valueFieldId: 'CPF3', changeFn: 'obj.myColorChanged'
          }],
        });
      }).not.toThrow();

      const opts = colorpickerRender.mock.calls[0][0];
      expect(() => opts.done('#123456')).not.toThrow();
      expect(el.value).toBe('#123456');
      expect(spy).not.toHaveBeenCalled();
    });

    test('denylisted global name "Function" is rejected', () => {
      const { ff, colorpickerRender } = loadFreshFf();

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'colorpicker', opts: { elem: '#CP4' }, changeFn: 'Function' }],
        });
      }).not.toThrow();

      const opts = colorpickerRender.mock.calls[0][0];
      expect(() => opts.done('#abcdef')).not.toThrow();
    });

    test('this file never causes a new eval( or new Function( call site to appear', () => {
      const matches = active.match(/\beval\(/g) || [];
      expect(matches).toHaveLength(1);
      expect(active).not.toMatch(/new\s+Function\s*\(/);
    });
  });
});

// ---------------------------------------------------------------------------
// Dispatch works via the OpenDialog2 (selector search-panel) path too —
// mirrors framework_layui_633_opendialog2_selector_panel.test.js's harness
// and Slice H's own OpenDialog2 proof, showing the SAME generic
// wtm-dialog-init rehydration/dispatch mechanism (#635) carries the new
// changeFn/onTipsFn island fields.
// ---------------------------------------------------------------------------
describe('#470 Slice I — dispatch reaches layui.slider.render/layui.colorpicker.render via ff.OpenDialog2 (selector path)', () => {
  function makeJQueryMock() {
    function wrap(el) {
      return { 0: el, length: el ? 1 : 0 };
    }
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

  function makeOpenDialog2Layui(sliderRender, colorpickerRender) {
    return {
      use: jest.fn((mods, cb) => cb()),
      form: { render: jest.fn() },
      slider: { render: sliderRender },
      colorpicker: { render: colorpickerRender },
      layer: {
        load: jest.fn(() => 1),
        close: jest.fn(),
        alert: jest.fn(),
        full: jest.fn(),
        open: jest.fn((opts) => {
          const container = document.createElement('div');
          // Safety note (mirrors #633's identical mock): opts.content here is
          // ALWAYS a string built by this file's own fixtures below — never
          // external/attacker-controlled input.
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

  test('a slider island with a changeFn, tokenized into the search panel, reaches layui.slider.render and wires changeFn end-to-end', () => {
    const sliderRender = jest.fn(() => FAKE_SLIDER_INS);
    const colorpickerRender = jest.fn();
    const layui = makeOpenDialog2Layui(sliderRender, colorpickerRender);
    const $ = makeJQueryMock();
    const payload = {
      type: 'slider',
      opts: { elem: '#SelectorSlider470' },
      changeFn: 'selectorSliderChange470'
    };
    makeOpenDialog2TempEl(
      'Temp470i',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$<div>panel</div>'
    );
    const { ff, windowObj } = loadFreshFfForOpenDialog2(layui, $);
    const changeSpy = jest.fn();
    windowObj.selectorSliderChange470 = changeSpy;

    ff.OpenDialog2('/some/search/url', 'w470i', 'Title', 500, 400, '#Temp470i');

    expect(sliderRender).toHaveBeenCalledTimes(1);
    const opts = sliderRender.mock.calls[0][0];
    expect(opts.elem).toBe('#SelectorSlider470');
    expect(typeof opts.change).toBe('function');
    opts.change(33);
    expect(changeSpy).toHaveBeenCalledWith(33, FAKE_SLIDER_INS);
  });

  test('a colorpicker island with a changeFn, tokenized into the search panel, reaches layui.colorpicker.render and wires changeFn end-to-end', () => {
    const sliderRender = jest.fn();
    const colorpickerRender = jest.fn();
    const layui = makeOpenDialog2Layui(sliderRender, colorpickerRender);
    const $ = makeJQueryMock();
    const payload = {
      type: 'colorpicker',
      opts: { elem: '#SelectorCp470' },
      valueFieldId: 'SelectorCpField470',
      changeFn: 'selectorCpChange470'
    };
    makeOpenDialog2TempEl(
      'Temp470j',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$' +
      '<input type="hidden" id="SelectorCpField470">'
    );
    const { ff, windowObj } = loadFreshFfForOpenDialog2(layui, $);
    const changeSpy = jest.fn();
    windowObj.selectorCpChange470 = changeSpy;

    ff.OpenDialog2('/some/search/url', 'w470j', 'Title', 500, 400, '#Temp470j');

    expect(colorpickerRender).toHaveBeenCalledTimes(1);
    const opts = colorpickerRender.mock.calls[0][0];
    expect(opts.elem).toBe('#SelectorCp470');
    expect(typeof opts.done).toBe('function');
    opts.done('#00ffcc');
    expect(document.getElementById('SelectorCpField470').value).toBe('#00ffcc');
    expect(changeSpy).toHaveBeenCalledWith('#00ffcc');
  });
});
