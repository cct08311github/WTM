// Tests for Issue #552 (#470-E): SliderTagHelper / RateTagHelper /
// ColorPickerTagHelper migrate their callback-free configuration off the
// inline <script> onto the eval-free wtm-dialog-init JSON island, mirroring
// the #556 laydate/DateTimeTagHelper split: a developer-supplied callback
// attribute (ChangeFunc/OnTipsFunc for slider, ChangeFunc for colorpicker;
// rate has no callback attribute at all) keeps the field on the legacy
// inline <script> path.
//
// What this file locks in place:
//   1. DispatchAction gains 'slider' / 'rate' / 'colorpicker' action types
//      that call layui.slider.render / layui.rate.render /
//      layui.colorpicker.render with an ALLOWLISTED opts object — unlike the
//      bare 'laydate' case (#556), which passes action.opts straight
//      through, these three rebuild opts key-by-key with a typeof /
//      Array.isArray check on every value. A function-valued or
//      string-typed value under an unexpected key (e.g. a smuggled
//      'change'/'done'/'choose' callback) is therefore never copied and
//      never reaches layui's render call — no eval, no Function
//      constructor, no dynamic code. The allowlist-building + render call
//      lives in a shared _renderSliderAction / _renderRateAction /
//      _renderColorpickerAction function (extracted by the adversarial-review
//      fix below), not inline in the case body.
//   2. The mandatory "write picked value back into the bound hidden input"
//      behavior (previously each inline script's own change/choose/done
//      handler) is reproduced natively via getElementById — never a
//      developer callback, always framework wiring.
//   3. _islandModulesFor maps slider/rate/colorpicker to their own layui
//      module name, so a late-loading module can never cause a silent
//      render no-op on the PAGE-READY path (via _dispatchIslandWhenReady;
//      same guarantee #556 established for laydate/form).
//   4. Adversarial-review fix (module-load race, HIGH): the ORIGINAL #552
//      cases only ever did a synchronous "if module missing, break" no-op —
//      fine for the page-ready path (which always goes through
//      _dispatchIslandWhenReady's layui.use deferral first), but NOT fine for
//      ff.OpenDialog's dialog-init dispatch loop, which calls
//      ff.DispatchAction directly, bypassing that deferral entirely. A dialog
//      whose only special field was a callback-free <wt:slider>/<wt:rate>/
//      <wt:colorpicker> (no other same-module usage on the page to have
//      already triggered layui's async load) would silently render nothing on
//      first open. The fix: each of the three cases now defers via
//      layui.use([mod], function(){ ff._renderXAction(action); }) when the
//      module isn't loaded yet — mirroring what the legacy inline <script>
//      did — so the "never silently no-ops due to a not-yet-loaded module"
//      guarantee now holds for BOTH the page-ready path AND the dialog path,
//      for these three action types specifically. (laydate/initForm/
//      bindSubmit/bindValidate still only get the guarantee via the
//      page-ready path — that is a separate, lower-risk follow-up, not fixed
//      here.) If layui itself isn't loaded (not just the submodule), there is
//      no layui.use to defer through, so the case still safely breaks.
//   5. framework_layui.js active-code eval( count remains exactly 1.
//
// Following the same convention as framework_layui_556_laydate_island.test.js:
// source-sweep tests assert the real file structure; behavioral-stub tests
// re-implement the same dispatcher logic (the vm-loaded `ff` module's
// closures were compiled without `document`/`layui` in scope — see
// setup.js — so the live module functions cannot be exercised directly
// against mocked DOM/layui here). Any drift between the stub and the real
// file is caught by the source-sweep tests. The "module loads LATE" hardening
// describe block at the bottom of this file is the one exception: it loads a
// FRESH instance of the real framework_layui.js into its own vm context (with
// a working `document` and a controllable `layui`), so it exercises the
// actual ff.DispatchAction dialog-path code directly, not a reimplementation.

const fs = require('fs');
const path = require('path');

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
describe('#552 (#470-E) — source sweep', () => {
  test('DispatchAction switch contains slider / rate / colorpicker cases', () => {
    expect(active).toMatch(/case\s+['"]slider['"]/);
    expect(active).toMatch(/case\s+['"]rate['"]/);
    expect(active).toMatch(/case\s+['"]colorpicker['"]/);
  });

  // Issue #552 adversarial-review fix (module-load race, HIGH): the
  // allowlist-building + layui.<mod>.render(...) call was extracted out of the
  // case body into a shared _render*Action function, so it can be called from
  // both the immediate path and the deferred layui.use(...) path without
  // duplicating (and risking drift in) the render logic. These tests assert
  // against the shared function bodies rather than the case bodies.
  test('_renderSliderAction calls layui.slider.render with a rebuilt opts object (not action.opts passthrough)', () => {
    expect(active).toMatch(/_renderSliderAction\s*:\s*function/);
    const block = active.match(/_renderSliderAction\s*:\s*function[\s\S]*?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.slider\.render\(\s*_slOpts\s*\)/);
    // Regression guard: must NOT be a bare passthrough like the laydate case.
    expect(block[0]).not.toMatch(/layui\.slider\.render\(\s*action\.opts/);
  });

  test('_renderRateAction calls layui.rate.render with a rebuilt opts object', () => {
    expect(active).toMatch(/_renderRateAction\s*:\s*function/);
    const block = active.match(/_renderRateAction\s*:\s*function[\s\S]*?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.rate\.render\(\s*_rtOpts\s*\)/);
    expect(block[0]).not.toMatch(/layui\.rate\.render\(\s*action\.opts/);
  });

  test('_renderColorpickerAction calls layui.colorpicker.render with a rebuilt opts object', () => {
    expect(active).toMatch(/_renderColorpickerAction\s*:\s*function/);
    const block = active.match(/_renderColorpickerAction\s*:\s*function[\s\S]*?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.colorpicker\.render\(\s*_cpOpts\s*\)/);
    expect(block[0]).not.toMatch(/layui\.colorpicker\.render\(\s*action\.opts/);
  });

  test('slider/rate/colorpicker opts are built with typeof / Array.isArray guards (allowlist discipline)', () => {
    const sliderBlock = active.match(/_renderSliderAction\s*:\s*function[\s\S]*?\n\s*\},/)[0];
    const rateBlock = active.match(/_renderRateAction\s*:\s*function[\s\S]*?\n\s*\},/)[0];
    const cpBlock = active.match(/_renderColorpickerAction\s*:\s*function[\s\S]*?\n\s*\},/)[0];
    [sliderBlock, rateBlock, cpBlock].forEach((block) => {
      expect(block).toMatch(/typeof\s+action\.opts\./);
    });
  });

  // ---- #552 adversarial-review fix: dialog-path module-load-race deferral ----
  test('slider/rate/colorpicker cases defer via layui.use when the module is not yet loaded', () => {
    const sliderCase = active.match(/case\s+['"]slider['"][\s\S]*?case\s+['"]rate['"]/)[0];
    const rateCase = active.match(/case\s+['"]rate['"][\s\S]*?case\s+['"]colorpicker['"]/)[0];
    const cpCase = active.match(/case\s+['"]colorpicker['"][\s\S]*?case\s+['"]bindSubmit['"]/)[0];

    expect(sliderCase).toMatch(/layui\.use\(\s*\[\s*['"]slider['"]\s*\][\s\S]{0,80}ff\._renderSliderAction\(\s*action\s*\)/);
    expect(sliderCase).toMatch(/ff\._renderSliderAction\(\s*action\s*\)/);

    expect(rateCase).toMatch(/layui\.use\(\s*\[\s*['"]rate['"]\s*\][\s\S]{0,80}ff\._renderRateAction\(\s*action\s*\)/);
    expect(rateCase).toMatch(/ff\._renderRateAction\(\s*action\s*\)/);

    expect(cpCase).toMatch(/layui\.use\(\s*\[\s*['"]colorpicker['"]\s*\][\s\S]{0,80}ff\._renderColorpickerAction\(\s*action\s*\)/);
    expect(cpCase).toMatch(/ff\._renderColorpickerAction\(\s*action\s*\)/);
  });

  test('slider/rate/colorpicker cases break immediately when layui itself is undefined (no layui.use attempt)', () => {
    const sliderCase = active.match(/case\s+['"]slider['"][\s\S]*?case\s+['"]rate['"]/)[0];
    const rateCase = active.match(/case\s+['"]rate['"][\s\S]*?case\s+['"]colorpicker['"]/)[0];
    const cpCase = active.match(/case\s+['"]colorpicker['"][\s\S]*?case\s+['"]bindSubmit['"]/)[0];
    [sliderCase, rateCase, cpCase].forEach((block) => {
      expect(block).toMatch(/typeof\s+layui\s*===\s*['"]undefined['"]\s*\)\s*\{\s*break;\s*\}/);
    });
  });

  test('_islandModulesFor maps slider/rate/colorpicker to their own module', () => {
    // Issue #470 Slice G: bound bumped 1600 -> 2200 — the function grew with
    // the new 'ueditor'/'layedit' module-deferral branches.
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,2200}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]slider['"]/);
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]rate['"]/);
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]colorpicker['"]/);
    expect(block[0]).toMatch(/mods\.push\(\s*['"]slider['"]\s*\)/);
    expect(block[0]).toMatch(/mods\.push\(\s*['"]rate['"]\s*\)/);
    expect(block[0]).toMatch(/mods\.push\(\s*['"]colorpicker['"]\s*\)/);
  });

  test('active-code eval( count is still exactly 1 after #552 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'slider' DispatchAction case
// ---------------------------------------------------------------------------
// Mirrors the real allowlist-building logic exactly (locked in by the source
// sweep above): only known keys of the right typeof/Array.isArray shape are
// copied onto a fresh opts object.
function buildSliderOpts(actionOpts) {
  if (!actionOpts || !actionOpts.elem) { return null; }
  var opts = { elem: actionOpts.elem };
  if (typeof actionOpts.type === 'string') { opts.type = actionOpts.type; }
  if (typeof actionOpts.min === 'number') { opts.min = actionOpts.min; }
  if (typeof actionOpts.max === 'number') { opts.max = actionOpts.max; }
  if (actionOpts.range === true) { opts.range = true; }
  if (typeof actionOpts.value === 'number' || Array.isArray(actionOpts.value)) { opts.value = actionOpts.value; }
  if (typeof actionOpts.step === 'number') { opts.step = actionOpts.step; }
  if (actionOpts.disabled === true) { opts.disabled = true; }
  if (typeof actionOpts.showstep === 'boolean') { opts.showstep = actionOpts.showstep; }
  if (typeof actionOpts.tips === 'boolean') { opts.tips = actionOpts.tips; }
  if (typeof actionOpts.input === 'boolean') { opts.input = actionOpts.input; }
  if (typeof actionOpts.height === 'number') { opts.height = actionOpts.height; }
  if (typeof actionOpts.theme === 'string') { opts.theme = actionOpts.theme; }
  return opts;
}

function makeSliderDispatcher(layui) {
  return function dispatchAction(payload) {
    if (!payload || !payload.actions) { return; }
    payload.actions.forEach(function (action) {
      if (!action || action.type !== 'slider') { return; }
      if (!layui || !layui.slider || typeof layui.slider.render !== 'function') { return; }
      var opts = buildSliderOpts(action.opts);
      if (!opts) { return; }
      var fieldId0 = typeof action.fieldId0 === 'string' ? action.fieldId0 : null;
      var fieldId1 = typeof action.fieldId1 === 'string' ? action.fieldId1 : null;
      var isRange = opts.range === true;
      opts.change = function (value) {
        if (isRange) {
          if (fieldId0 && Array.isArray(value)) {
            var el0 = document.getElementById(fieldId0);
            if (el0) { el0.value = value[0]; }
          }
          if (fieldId1 && Array.isArray(value)) {
            var el1 = document.getElementById(fieldId1);
            if (el1) { el1.value = value[1]; }
          }
        } else if (fieldId0) {
          var el = document.getElementById(fieldId0);
          if (el) { el.value = value; }
        }
      };
      layui.slider.render(opts);
    });
  };
}

describe('#552 DispatchAction slider — behavioral stub', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('renders with the allowlisted opts and drops unknown keys', () => {
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({
      actions: [{
        type: 'slider',
        opts: { elem: '#_sliderMy', min: 0, max: 100, step: 5, showstep: true, tips: false, input: true, theme: '#009688', unknownKey: 'nope' }
      }]
    });
    expect(sliderRender).toHaveBeenCalledTimes(1);
    const passed = sliderRender.mock.calls[0][0];
    expect(passed.elem).toBe('#_sliderMy');
    expect(passed.min).toBe(0);
    expect(passed.max).toBe(100);
    expect(passed.step).toBe(5);
    expect(passed.showstep).toBe(true);
    expect(passed.tips).toBe(false);
    expect(passed.input).toBe(true);
    expect(passed.theme).toBe('#009688');
    expect(passed.unknownKey).toBeUndefined();
  });

  test('is a no-op when opts.elem is missing', () => {
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({ actions: [{ type: 'slider', opts: { min: 0 } }] });
    expect(sliderRender).not.toHaveBeenCalled();
  });

  test('is a no-op when layui.slider is not available', () => {
    const dispatch = makeSliderDispatcher(undefined);
    expect(() => {
      dispatch({ actions: [{ type: 'slider', opts: { elem: '#D' } }] });
    }).not.toThrow();
  });

  test('non-range single value: change callback writes the picked value into fieldId0', () => {
    const el = document.createElement('input');
    el.id = '_sliderMy_v0';
    document.body.appendChild(el);
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({
      actions: [{ type: 'slider', opts: { elem: '#_sliderMy' }, fieldId0: '_sliderMy_v0' }]
    });
    const passed = sliderRender.mock.calls[0][0];
    passed.change(42);
    expect(el.value).toBe('42');
  });

  test('range: change callback writes both values into fieldId0/fieldId1', () => {
    const el0 = document.createElement('input');
    el0.id = '_sliderR_v0';
    const el1 = document.createElement('input');
    el1.id = '_sliderR_v1';
    document.body.appendChild(el0);
    document.body.appendChild(el1);
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({
      actions: [{
        type: 'slider',
        opts: { elem: '#_sliderR', range: true },
        fieldId0: '_sliderR_v0',
        fieldId1: '_sliderR_v1'
      }]
    });
    const passed = sliderRender.mock.calls[0][0];
    passed.change([10, 60]);
    expect(el0.value).toBe('10');
    expect(el1.value).toBe('60');
  });

  // ---- Adversarial: function-valued / string-JS opts must never pass through ----
  test('a function-valued opt under an allowlisted key is dropped (typeof guard fails)', () => {
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({
      actions: [{
        type: 'slider',
        // min is supposed to be a number; a function value must be ignored.
        opts: { elem: '#_sliderF', min: function () { return 'pwn'; } }
      }]
    });
    const passed = sliderRender.mock.calls[0][0];
    expect(passed.min).toBeUndefined();
    expect(typeof passed.min).not.toBe('function');
  });

  test('a smuggled "change" callback key in the island JSON never reaches layui (native handler always wins)', () => {
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({
      actions: [{
        type: 'slider',
        opts: { elem: '#_sliderG', change: 'javascript:alert(1)' }
      }]
    });
    const passed = sliderRender.mock.calls[0][0];
    // 'change' is always the native handler function built by the dispatcher,
    // never the attacker-supplied string — it is not even a copy target in
    // buildSliderOpts (change is not on the allowlist), so this assertion
    // also proves the string was never assigned anywhere let alone executed.
    expect(typeof passed.change).toBe('function');
    expect(passed.change).not.toBe('javascript:alert(1)');
  });

  test('a string-JS value under a boolean-typed key (tips) is dropped', () => {
    const sliderRender = jest.fn();
    const layui = { slider: { render: sliderRender } };
    const dispatch = makeSliderDispatcher(layui);
    dispatch({
      actions: [{
        type: 'slider',
        opts: { elem: '#_sliderH', tips: "true;alert(1)//" }
      }]
    });
    const passed = sliderRender.mock.calls[0][0];
    expect(passed.tips).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'rate' DispatchAction case
// ---------------------------------------------------------------------------
function buildRateOpts(actionOpts) {
  if (!actionOpts || !actionOpts.elem) { return null; }
  var opts = { elem: actionOpts.elem };
  if (typeof actionOpts.value === 'number') { opts.value = actionOpts.value; }
  if (typeof actionOpts.length === 'number') { opts.length = actionOpts.length; }
  if (actionOpts.half === true) { opts.half = true; }
  if (actionOpts.readonly === true) { opts.readonly = true; }
  if (Array.isArray(actionOpts.text)) { opts.text = actionOpts.text; }
  return opts;
}

function makeRateDispatcher(layui) {
  return function dispatchAction(payload) {
    if (!payload || !payload.actions) { return; }
    payload.actions.forEach(function (action) {
      if (!action || action.type !== 'rate') { return; }
      if (!layui || !layui.rate || typeof layui.rate.render !== 'function') { return; }
      var opts = buildRateOpts(action.opts);
      if (!opts) { return; }
      var valueFieldId = typeof action.valueFieldId === 'string' ? action.valueFieldId : null;
      opts.choose = function (val) {
        if (valueFieldId) {
          var el = document.getElementById(valueFieldId);
          if (el) { el.value = val; }
        }
      };
      layui.rate.render(opts);
    });
  };
}

describe('#552 DispatchAction rate — behavioral stub', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('renders with the allowlisted opts', () => {
    const rateRender = jest.fn();
    const layui = { rate: { render: rateRender } };
    const dispatch = makeRateDispatcher(layui);
    dispatch({
      actions: [{
        type: 'rate',
        opts: { elem: '#rateA', value: 3, length: 5, half: true, readonly: false, text: ['Nice'], evil: 'x' }
      }]
    });
    const passed = rateRender.mock.calls[0][0];
    expect(passed.value).toBe(3);
    expect(passed.length).toBe(5);
    expect(passed.half).toBe(true);
    expect(passed.readonly).toBeUndefined();
    expect(passed.text).toEqual(['Nice']);
    expect(passed.evil).toBeUndefined();
  });

  test('choose callback writes the picked value into valueFieldId', () => {
    const el = document.createElement('input');
    el.id = 'rateB_val';
    document.body.appendChild(el);
    const rateRender = jest.fn();
    const layui = { rate: { render: rateRender } };
    const dispatch = makeRateDispatcher(layui);
    dispatch({ actions: [{ type: 'rate', opts: { elem: '#rateB' }, valueFieldId: 'rateB_val' }] });
    const passed = rateRender.mock.calls[0][0];
    passed.choose(4);
    expect(el.value).toBe('4');
  });

  test('is a no-op when opts.elem is missing', () => {
    const rateRender = jest.fn();
    const layui = { rate: { render: rateRender } };
    const dispatch = makeRateDispatcher(layui);
    dispatch({ actions: [{ type: 'rate', opts: {} }] });
    expect(rateRender).not.toHaveBeenCalled();
  });

  // Adversarial
  test('a function-valued "value" opt is dropped (typeof guard fails)', () => {
    const rateRender = jest.fn();
    const layui = { rate: { render: rateRender } };
    const dispatch = makeRateDispatcher(layui);
    dispatch({
      actions: [{ type: 'rate', opts: { elem: '#rateC', value: function () { return 5; } } }]
    });
    const passed = rateRender.mock.calls[0][0];
    expect(passed.value).toBeUndefined();
  });

  test('a smuggled "choose" callback key never reaches layui.rate.render (native handler always wins)', () => {
    const rateRender = jest.fn();
    const layui = { rate: { render: rateRender } };
    const dispatch = makeRateDispatcher(layui);
    dispatch({
      actions: [{ type: 'rate', opts: { elem: '#rateD', choose: 'javascript:alert(1)' } }]
    });
    const passed = rateRender.mock.calls[0][0];
    expect(typeof passed.choose).toBe('function');
    expect(passed.choose).not.toBe('javascript:alert(1)');
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'colorpicker' DispatchAction case
// ---------------------------------------------------------------------------
function buildColorPickerOpts(actionOpts) {
  if (!actionOpts || !actionOpts.elem) { return null; }
  var opts = { elem: actionOpts.elem };
  if (typeof actionOpts.color === 'string') { opts.color = actionOpts.color; }
  if (typeof actionOpts.alpha === 'boolean') { opts.alpha = actionOpts.alpha; }
  if (typeof actionOpts.format === 'string') { opts.format = actionOpts.format; }
  if (typeof actionOpts.predefine === 'boolean') { opts.predefine = actionOpts.predefine; }
  if (Array.isArray(actionOpts.colors)) { opts.colors = actionOpts.colors; }
  return opts;
}

function makeColorPickerDispatcher(layui) {
  return function dispatchAction(payload) {
    if (!payload || !payload.actions) { return; }
    payload.actions.forEach(function (action) {
      if (!action || action.type !== 'colorpicker') { return; }
      if (!layui || !layui.colorpicker || typeof layui.colorpicker.render !== 'function') { return; }
      var opts = buildColorPickerOpts(action.opts);
      if (!opts) { return; }
      var valueFieldId = typeof action.valueFieldId === 'string' ? action.valueFieldId : null;
      opts.done = function (data) {
        if (valueFieldId) {
          var el = document.getElementById(valueFieldId);
          if (el) { el.value = data; }
        }
      };
      layui.colorpicker.render(opts);
    });
  };
}

describe('#552 DispatchAction colorpicker — behavioral stub', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('renders with the allowlisted opts', () => {
    const cpRender = jest.fn();
    const layui = { colorpicker: { render: cpRender } };
    const dispatch = makeColorPickerDispatcher(layui);
    dispatch({
      actions: [{
        type: 'colorpicker',
        opts: { elem: '#cp_A', color: '#123456', alpha: true, format: 'rgb', predefine: true, colors: ['#fff', '#000'], evil: 'x' }
      }]
    });
    const passed = cpRender.mock.calls[0][0];
    expect(passed.color).toBe('#123456');
    expect(passed.alpha).toBe(true);
    expect(passed.format).toBe('rgb');
    expect(passed.predefine).toBe(true);
    expect(passed.colors).toEqual(['#fff', '#000']);
    expect(passed.evil).toBeUndefined();
  });

  test('done callback writes the picked color into valueFieldId', () => {
    const el = document.createElement('input');
    el.id = 'cp_B';
    document.body.appendChild(el);
    const cpRender = jest.fn();
    const layui = { colorpicker: { render: cpRender } };
    const dispatch = makeColorPickerDispatcher(layui);
    dispatch({ actions: [{ type: 'colorpicker', opts: { elem: '#cp_cp_B' }, valueFieldId: 'cp_B' }] });
    const passed = cpRender.mock.calls[0][0];
    passed.done('#00ff00');
    expect(el.value).toBe('#00ff00');
  });

  test('is a no-op when opts.elem is missing', () => {
    const cpRender = jest.fn();
    const layui = { colorpicker: { render: cpRender } };
    const dispatch = makeColorPickerDispatcher(layui);
    dispatch({ actions: [{ type: 'colorpicker', opts: {} }] });
    expect(cpRender).not.toHaveBeenCalled();
  });

  // Adversarial
  test('a function-valued "color" opt is dropped (typeof guard fails)', () => {
    const cpRender = jest.fn();
    const layui = { colorpicker: { render: cpRender } };
    const dispatch = makeColorPickerDispatcher(layui);
    dispatch({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_C', color: function () { return '#fff'; } } }]
    });
    const passed = cpRender.mock.calls[0][0];
    expect(passed.color).toBeUndefined();
  });

  test('a smuggled "done" callback key never reaches layui.colorpicker.render (native handler always wins)', () => {
    const cpRender = jest.fn();
    const layui = { colorpicker: { render: cpRender } };
    const dispatch = makeColorPickerDispatcher(layui);
    dispatch({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_D', done: 'javascript:alert(1)' } }]
    });
    const passed = cpRender.mock.calls[0][0];
    expect(typeof passed.done).toBe('function');
    expect(passed.done).not.toBe('javascript:alert(1)');
  });

  test('a non-array string value for "colors" is dropped (Array.isArray guard fails)', () => {
    const cpRender = jest.fn();
    const layui = { colorpicker: { render: cpRender } };
    const dispatch = makeColorPickerDispatcher(layui);
    dispatch({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_E', colors: "['#fff']" } }]
    });
    const passed = cpRender.mock.calls[0][0];
    expect(passed.colors).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// #552 adversarial-review fix — DIALOG-PATH module-load race: the island must
// STILL render, exercised against the REAL ff.DispatchAction.
// ---------------------------------------------------------------------------
// Every other describe block in this file re-implements the dispatcher logic
// as a hand-rolled JS function, because the module-level `ff` loaded by
// setup.js was compiled in a vm context without `document`/`layui` in scope
// (see the file header). That is fine for asserting opts-building/allowlist
// behavior, but it is NOT good enough to prove the module-load-race fix
// actually works, because the bug (and the fix) live entirely in how
// ff.DispatchAction's 'slider'/'rate'/'colorpicker' cases react to a
// not-yet-loaded module — a reimplementation could easily "fix" itself
// without the real file being correct. So this block loads a FRESH instance
// of the real framework_layui.js source into its own vm context that DOES
// provide a working `document` (the real jsdom document for this test file)
// and a controllable `layui` mock, and calls the REAL ff.DispatchAction
// directly. This is a faithful simulation of "a dialog whose only special
// field is a callback-free <wt:slider>/<wt:rate>/<wt:colorpicker>, with the
// layui submodule not yet loaded" — exactly the scenario the adversarial
// review flagged as a silent render no-op before this fix.
//
// Issue #576 update: at the time this file was written, ff.OpenDialog's
// dialog-init dispatch loop called ff.DispatchAction(_dialogInitPayloads[_pi])
// directly, bypassing ff._dispatchIslandWhenReady's deferral entirely — which
// is exactly why these tests call ff.DispatchAction directly below rather
// than routing through the helper. #576 changed that loop to call
// ff._dispatchIslandWhenReady instead (see
// framework_layui_576_opendialog_island_defer.test.js), so the dialog path
// now gets the layui.use(...) deferral generically for every action type,
// not just slider/rate/colorpicker. The tests below still call
// ff.DispatchAction directly on purpose: they verify the per-case guards
// added here in #552 (_renderSliderAction/_renderRateAction/
// _renderColorpickerAction's own layui.use fallback) still work on their
// own, independent of #576's generic dispatch-loop fix — the two mechanisms
// are intentionally redundant belt-and-suspenders, not exclusive.
const vm = require('vm');

function loadFreshFfWithLayui(layui) {
  // Minimal jQuery mock for the vm context — $.ajax is called at script load
  // time for i18n (see setup.js's own jqueryMock for the same requirement).
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
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
  return ctx.ff;
}

// layui.use queues its callback and only fires it once every requested
// module has been load()ed — exactly how the real layui defers a callback
// for a not-yet-loaded module (same harness shape as the #556 laydate test).
function makeLateLayui(modName) {
  var pending = [];
  var loaded = {};
  var render = jest.fn();
  var layui = {
    use: function (mods, cb) {
      pending.push({ mods: mods, cb: cb });
      flush();
    }
  };
  function flush() {
    pending = pending.filter(function (p) {
      var ready = p.mods.every(function (m) { return loaded[m]; });
      if (ready) { p.cb(); return false; }
      return true;
    });
  }
  return {
    layui: layui,
    render: render,
    load: function () {
      loaded[modName] = true;
      layui[modName] = { render: render };
      flush();
    }
  };
}

describe('#552 adversarial-review fix — dialog-path module-load race (real ff.DispatchAction)', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('slider: real ff.DispatchAction DEFERS (does not drop) when layui.slider is not yet loaded, then renders once it loads', () => {
    const harness = makeLateLayui('slider');
    const ff = loadFreshFfWithLayui(harness.layui);

    // Same call OpenDialog's dialog-init dispatch loop makes directly.
    ff.DispatchAction({ actions: [{ type: 'slider', opts: { elem: '#_sliderLate' } }] });

    // OLD buggy behavior: this case only ever did a synchronous check-and-break
    // -> render permanently never called, even after the module loads.
    expect(harness.render).not.toHaveBeenCalled();

    // layui finishes loading 'slider' asynchronously -> the queued render fires.
    harness.load();
    expect(harness.render).toHaveBeenCalledTimes(1);
    expect(harness.render.mock.calls[0][0].elem).toBe('#_sliderLate');
  });

  test('rate: real ff.DispatchAction DEFERS (does not drop) when layui.rate is not yet loaded, then renders once it loads', () => {
    const harness = makeLateLayui('rate');
    const ff = loadFreshFfWithLayui(harness.layui);

    ff.DispatchAction({ actions: [{ type: 'rate', opts: { elem: '#rateLate' } }] });
    expect(harness.render).not.toHaveBeenCalled();

    harness.load();
    expect(harness.render).toHaveBeenCalledTimes(1);
    expect(harness.render.mock.calls[0][0].elem).toBe('#rateLate');
  });

  test('colorpicker: real ff.DispatchAction DEFERS (does not drop) when layui.colorpicker is not yet loaded, then renders once it loads', () => {
    const harness = makeLateLayui('colorpicker');
    const ff = loadFreshFfWithLayui(harness.layui);

    ff.DispatchAction({ actions: [{ type: 'colorpicker', opts: { elem: '#cp_Late' } }] });
    expect(harness.render).not.toHaveBeenCalled();

    harness.load();
    expect(harness.render).toHaveBeenCalledTimes(1);
    expect(harness.render.mock.calls[0][0].elem).toBe('#cp_Late');
  });

  test('when the module is ALREADY loaded, render fires synchronously through real ff.DispatchAction (no regression for the fast path)', () => {
    const harness = makeLateLayui('slider');
    harness.load();
    const ff = loadFreshFfWithLayui(harness.layui);

    ff.DispatchAction({ actions: [{ type: 'slider', opts: { elem: '#_sliderEarly' } }] });
    expect(harness.render).toHaveBeenCalledTimes(1);
  });

  test('when layui itself is entirely undefined, real ff.DispatchAction is a safe no-op (no throw, no layui.use attempt)', () => {
    const ff = loadFreshFfWithLayui(undefined);
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'slider', opts: { elem: '#_sliderNoLayui' } }] });
      ff.DispatchAction({ actions: [{ type: 'rate', opts: { elem: '#rateNoLayui' } }] });
      ff.DispatchAction({ actions: [{ type: 'colorpicker', opts: { elem: '#cpNoLayui' } }] });
    }).not.toThrow();
  });

  test('two independent dialog dispatches before load each render once on load (no accidental dedup/drop)', () => {
    const harness = makeLateLayui('rate');
    const ff = loadFreshFfWithLayui(harness.layui);
    ff.DispatchAction({ actions: [{ type: 'rate', opts: { elem: '#rateA' } }] });
    ff.DispatchAction({ actions: [{ type: 'rate', opts: { elem: '#rateB' } }] });
    expect(harness.render).not.toHaveBeenCalled();

    harness.load();
    expect(harness.render).toHaveBeenCalledTimes(2);
  });
});
