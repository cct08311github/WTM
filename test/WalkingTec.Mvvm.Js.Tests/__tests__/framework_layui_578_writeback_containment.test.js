// Tests for Issue #578: form-containment for the slider/rate/colorpicker
// island write-back handlers (#552), mirroring the containment guard #564's
// highlightErrors already applies.
//
// Threat model (#462/#552): a smuggled/attacker-crafted island descriptor
// inside a dialog partial could previously spoof-target an arbitrary hidden
// input ANYWHERE on the page by id — the slider/rate/colorpicker
// change/choose/done write-back handlers resolved their target via a bare
// document.getElementById(fieldId) with no containment check at all. #564's
// highlightErrors already gated its own field writes on
// formEl.contains(fieldEl); this fix plumbs an equivalent action.formId
// (server-sourced from the SAME ambient context.Items["formid"] key
// FormTagHelper publishes — see SliderTagHelper.cs/RateTagHelper.cs/
// ColorPicker.cs) through to the client and applies the identical
// containment gate before every write-back.
//
// Behavior matrix asserted below (per widget):
//   1. formId present + form found + field contained      -> write proceeds (regression)
//   2. formId present + form found + field NOT contained   -> write silently skipped (security)
//   3. formId present + form NOT found in the DOM          -> write silently skipped (fail-closed)
//   4. formId absent/undefined (old/back-compat island)    -> write proceeds unguarded (back-compat)
//
// Following the same convention as framework_layui_552_static_widgets_island.test.js's
// "dialog-path module-load race" section: this file loads a FRESH instance of
// the real framework_layui.js into its own vm context (real jsdom `document`,
// a controllable `layui` mock) and calls the REAL ff.DispatchAction directly,
// so the containment gate is exercised against the actual shipped code, not a
// hand-rolled reimplementation.

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
describe('#578 — source sweep', () => {
  test('slider case reads action.formId and gates the write-back on .contains(...)', () => {
    const block = active.match(/_renderSliderAction\s*:\s*function[\s\S]*?\n\s*\},/)[0];
    expect(block).toMatch(/action\.formId/);
    expect(block).toMatch(/_slFormEl\.contains\(/);
  });

  test('rate case reads action.formId and gates the write-back on .contains(...)', () => {
    const block = active.match(/_renderRateAction\s*:\s*function[\s\S]*?\n\s*\},/)[0];
    expect(block).toMatch(/action\.formId/);
    expect(block).toMatch(/_rtFormEl\.contains\(/);
  });

  test('colorpicker case reads action.formId and gates the write-back on .contains(...)', () => {
    const block = active.match(/_renderColorpickerAction\s*:\s*function[\s\S]*?\n\s*\},/)[0];
    expect(block).toMatch(/action\.formId/);
    expect(block).toMatch(/_cpFormEl\.contains\(/);
  });

  test('active-code eval( count is still exactly 1 after #578 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Real ff.DispatchAction harness (mirrors #552's loadFreshFfWithLayui)
// ---------------------------------------------------------------------------
function loadFreshFfWithLayui(layui) {
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

// All three modules are already "loaded" (no deferral) so the render call
// (and the change/choose/done callback it wires up) fires synchronously.
function makeLoadedLayui(modName, render) {
  const layui = { use: function (mods, cb) { cb(); } };
  layui[modName] = { render: render };
  return layui;
}

function appendForm(formId) {
  const form = document.createElement('form');
  form.id = formId;
  document.body.appendChild(form);
  return form;
}

function appendHiddenInput(parent, id) {
  const el = document.createElement('input');
  el.id = id;
  parent.appendChild(el);
  return el;
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// slider
// ---------------------------------------------------------------------------
describe('#578 slider write-back containment (real ff.DispatchAction)', () => {
  test('regression: formId present, field inside the form -> write proceeds', () => {
    const form = appendForm('wtForm_s1');
    const el = appendHiddenInput(form, '_sliderA_v0');
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('slider', render));

    ff.DispatchAction({
      actions: [{ type: 'slider', opts: { elem: '#_sliderA' }, fieldId0: '_sliderA_v0', formId: 'wtForm_s1' }]
    });
    render.mock.calls[0][0].change(77);

    expect(el.value).toBe('77');
  });

  test('SECURITY: formId present, field id resolves OUTSIDE the form -> write silently skipped', () => {
    appendForm('wtForm_s2');
    const outsider = appendHiddenInput(document.body, 'attackerTarget');
    outsider.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('slider', render));

    ff.DispatchAction({
      actions: [{ type: 'slider', opts: { elem: '#_sliderB' }, fieldId0: 'attackerTarget', formId: 'wtForm_s2' }]
    });
    expect(() => render.mock.calls[0][0].change(999)).not.toThrow();

    expect(outsider.value).toBe('untouched');
  });

  test('fail-closed: formId present but the form element does not exist in the DOM -> write silently skipped', () => {
    const el = appendHiddenInput(document.body, '_sliderC_v0');
    el.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('slider', render));

    ff.DispatchAction({
      actions: [{ type: 'slider', opts: { elem: '#_sliderC' }, fieldId0: '_sliderC_v0', formId: 'wtForm_does_not_exist' }]
    });
    render.mock.calls[0][0].change(123);

    expect(el.value).toBe('untouched');
  });

  test('back-compat: formId absent (old island) -> write proceeds unguarded exactly as before', () => {
    const el = appendHiddenInput(document.body, '_sliderD_v0');
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('slider', render));

    ff.DispatchAction({
      actions: [{ type: 'slider', opts: { elem: '#_sliderD' }, fieldId0: '_sliderD_v0' }]
    });
    render.mock.calls[0][0].change(55);

    expect(el.value).toBe('55');
  });

  test('range slider: containment is checked independently per fieldId0/fieldId1', () => {
    const form = appendForm('wtForm_s3');
    const el0 = appendHiddenInput(form, '_sliderR_v0');
    // el1 lives OUTSIDE the form — simulates a spoofed fieldId1.
    const el1 = appendHiddenInput(document.body, '_sliderR_v1');
    el1.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('slider', render));

    ff.DispatchAction({
      actions: [{
        type: 'slider',
        opts: { elem: '#_sliderR', range: true },
        fieldId0: '_sliderR_v0',
        fieldId1: '_sliderR_v1',
        formId: 'wtForm_s3'
      }]
    });
    render.mock.calls[0][0].change([10, 60]);

    expect(el0.value).toBe('10');
    expect(el1.value).toBe('untouched');
  });
});

// ---------------------------------------------------------------------------
// rate
// ---------------------------------------------------------------------------
describe('#578 rate write-back containment (real ff.DispatchAction)', () => {
  test('regression: formId present, field inside the form -> write proceeds', () => {
    const form = appendForm('wtForm_r1');
    const el = appendHiddenInput(form, 'rateA_val');
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('rate', render));

    ff.DispatchAction({
      actions: [{ type: 'rate', opts: { elem: '#rateA' }, valueFieldId: 'rateA_val', formId: 'wtForm_r1' }]
    });
    render.mock.calls[0][0].choose(4);

    expect(el.value).toBe('4');
  });

  test('SECURITY: formId present, field id resolves OUTSIDE the form -> write silently skipped', () => {
    appendForm('wtForm_r2');
    const outsider = appendHiddenInput(document.body, 'attackerRateTarget');
    outsider.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('rate', render));

    ff.DispatchAction({
      actions: [{ type: 'rate', opts: { elem: '#rateB' }, valueFieldId: 'attackerRateTarget', formId: 'wtForm_r2' }]
    });
    render.mock.calls[0][0].choose(5);

    expect(outsider.value).toBe('untouched');
  });

  test('fail-closed: formId present but the form element does not exist in the DOM -> write silently skipped', () => {
    const el = appendHiddenInput(document.body, 'rateC_val');
    el.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('rate', render));

    ff.DispatchAction({
      actions: [{ type: 'rate', opts: { elem: '#rateC' }, valueFieldId: 'rateC_val', formId: 'wtForm_does_not_exist' }]
    });
    render.mock.calls[0][0].choose(3);

    expect(el.value).toBe('untouched');
  });

  test('back-compat: formId absent (old island) -> write proceeds unguarded exactly as before', () => {
    const el = appendHiddenInput(document.body, 'rateD_val');
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('rate', render));

    ff.DispatchAction({
      actions: [{ type: 'rate', opts: { elem: '#rateD' }, valueFieldId: 'rateD_val' }]
    });
    render.mock.calls[0][0].choose(2);

    expect(el.value).toBe('2');
  });
});

// ---------------------------------------------------------------------------
// colorpicker
// ---------------------------------------------------------------------------
describe('#578 colorpicker write-back containment (real ff.DispatchAction)', () => {
  test('regression: formId present, field inside the form -> write proceeds', () => {
    const form = appendForm('wtForm_c1');
    const el = appendHiddenInput(form, 'cp_A');
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('colorpicker', render));

    ff.DispatchAction({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_cp_A' }, valueFieldId: 'cp_A', formId: 'wtForm_c1' }]
    });
    render.mock.calls[0][0].done('#00ff00');

    expect(el.value).toBe('#00ff00');
  });

  test('SECURITY: formId present, field id resolves OUTSIDE the form -> write silently skipped', () => {
    appendForm('wtForm_c2');
    const outsider = appendHiddenInput(document.body, 'attackerCpTarget');
    outsider.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('colorpicker', render));

    ff.DispatchAction({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_B' }, valueFieldId: 'attackerCpTarget', formId: 'wtForm_c2' }]
    });
    render.mock.calls[0][0].done('#ff0000');

    expect(outsider.value).toBe('untouched');
  });

  test('fail-closed: formId present but the form element does not exist in the DOM -> write silently skipped', () => {
    const el = appendHiddenInput(document.body, 'cp_C');
    el.value = 'untouched';
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('colorpicker', render));

    ff.DispatchAction({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_C' }, valueFieldId: 'cp_C', formId: 'wtForm_does_not_exist' }]
    });
    render.mock.calls[0][0].done('#123456');

    expect(el.value).toBe('untouched');
  });

  test('back-compat: formId absent (old island) -> write proceeds unguarded exactly as before', () => {
    const el = appendHiddenInput(document.body, 'cp_D');
    const render = jest.fn();
    const ff = loadFreshFfWithLayui(makeLoadedLayui('colorpicker', render));

    ff.DispatchAction({
      actions: [{ type: 'colorpicker', opts: { elem: '#cp_D' }, valueFieldId: 'cp_D' }]
    });
    render.mock.calls[0][0].done('#abcdef');

    expect(el.value).toBe('#abcdef');
  });
});
