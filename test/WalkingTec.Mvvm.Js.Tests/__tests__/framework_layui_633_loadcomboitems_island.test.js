// Tests for Issue #633 (#470-F, widget-islandification slice 2):
// ComboBoxTagHelper / CheckBoxTagHelper / RadioTagHelper / TransferTagHelper's
// ItemUrl branch migrates off the inline
// <script>ff.LoadComboItems(...)</script> call onto the eval-free
// wtm-dialog-init JSON island — riding the 'loadComboItems' DispatchAction
// case the #551 foundation already shipped (framework_layui.js had the case
// but no server emitter yet; #633 is the server emitter).
//
// What this file locks in place:
//   1. The 'loadComboItems' case additively threads a `disabled` field through
//      as ff.LoadComboItems's 7th positional arg (parity with CheckBoxTagHelper's
//      legacy inline call, which always passed an explicit true/false there).
//      Existing 5-field payloads (combo/radio/transfer — never set `disabled`)
//      keep getting `undefined` for that arg, identical to before.
//   2. Dispatching the island calls the exact SAME ff.LoadComboItems function
//      the inline <script> called — same ajax fetch, same DOM mutation, same
//      layui.form.render / layui.transfer.reload / window[id].update calls —
//      for all four controlTypes (combo/checkbox/radio/transfer).
//   3. The payload works through both the page-ready consumer contract (bare
//      {type:'loadComboItems',...} action, normalized like every other #552-era
//      island) and the dialog-path collect/replay pipeline
//      (ff._collectInitFromHtml / ff._replayInitFromHtml), exercised against
//      the REAL functions (not a reimplementation) for the dialog-path test.
//   4. framework_layui.js active-code eval( count remains exactly 1.
//
// Following the same convention as framework_layui_552_static_widgets_island.test.js
// / framework_layui_556_laydate_island.test.js: source-sweep tests assert the
// real file structure; page-ready coverage uses a hand-rolled stub (mirroring
// #556's own "behavioral stub (real DOM)" block) because the module-level `ff`
// loaded by setup.js already ran its page-ready consumer once against the
// shared jsdom document at import time. The dialog-path and LoadComboItems
// end-to-end tests load a FRESH instance of the real framework_layui.js into
// its own vm context (mirroring #552's loadFreshFfWithLayui) so they exercise
// the actual ff.DispatchAction / ff.LoadComboItems / ff._collectInitFromHtml /
// ff._replayInitFromHtml code directly.

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
describe('#633 (#470-F) — source sweep', () => {
  test('DispatchAction switch still contains the loadComboItems case', () => {
    expect(active).toMatch(/case\s+['"]loadComboItems['"]/);
  });

  test('loadComboItems case still calls ff.LoadComboItems (no eval)', () => {
    expect(active).toMatch(/case\s+['"]loadComboItems['"][\s\S]{0,900}ff\.LoadComboItems\(/);
  });

  test('loadComboItems case now threads action.disabled through as a 7th positional arg', () => {
    const block = active.match(/case\s+['"]loadComboItems['"][\s\S]{0,900}?break;/)[0];
    expect(block).toMatch(/action\.disabled/);
    // 7 arguments passed to ff.LoadComboItems: controlType, url, id, field,
    // selectVal, cb (always undefined), disabled.
    const callMatch = block.match(/ff\.LoadComboItems\(\s*([\s\S]*?)\);/);
    expect(callMatch).not.toBeNull();
    const argCount = callMatch[1].split(',').length;
    expect(argCount).toBe(7);
  });

  test('Issue #633 comment reference is present near the loadComboItems case', () => {
    expect(src).toMatch(/Issue #633[\s\S]{0,1500}case\s+['"]loadComboItems['"]/);
  });

  test('active-code eval( count is still exactly 1 after #633 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'loadComboItems' DispatchAction case (argument mapping)
// ---------------------------------------------------------------------------
// Mirrors the real case body exactly (locked in by the source sweep above):
// only known keys are mapped onto ff.LoadComboItems's positional parameters.
describe('#633 DispatchAction loadComboItems — behavioral stub (argument mapping)', () => {
  function makeDispatcher(ff) {
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      var actions = payload.actions;
      for (var i = 0; i < actions.length; i++) {
        var action = actions[i];
        if (!action || !action.type) continue;
        switch (action.type) {
          case 'loadComboItems':
            if (typeof ff.LoadComboItems === 'function' && action.url && action.id) {
              ff.LoadComboItems(
                action.controlType || undefined,
                action.url,
                action.id,
                action.field || undefined,
                action.selectVal || undefined,
                undefined,
                (action.disabled === true || action.disabled === false) ? action.disabled : undefined
              );
            }
            break;
          default:
            break;
        }
      }
    };
  }

  test('combo/radio/transfer payloads (no disabled field) call ff.LoadComboItems with disabled=undefined', () => {
    const loadComboItems = jest.fn();
    const dispatch = makeDispatcher({ LoadComboItems: loadComboItems });
    dispatch({
      actions: [{
        type: 'loadComboItems',
        controlType: 'combo',
        url: '/Home/GetItems',
        id: 'MyCombo',
        field: 'MyComboText',
        selectVal: ['1', '2']
      }]
    });
    expect(loadComboItems).toHaveBeenCalledWith(
      'combo', '/Home/GetItems', 'MyCombo', 'MyComboText', ['1', '2'], undefined, undefined
    );
  });

  test('checkbox payload with disabled:true calls ff.LoadComboItems with disabled=true (7th arg)', () => {
    const loadComboItems = jest.fn();
    const dispatch = makeDispatcher({ LoadComboItems: loadComboItems });
    dispatch({
      actions: [{
        type: 'loadComboItems',
        controlType: 'checkbox',
        url: '/Home/GetCheckboxItems',
        id: 'MyCheckbox',
        field: 'MyCheckboxField',
        selectVal: ['a'],
        disabled: true
      }]
    });
    expect(loadComboItems).toHaveBeenCalledWith(
      'checkbox', '/Home/GetCheckboxItems', 'MyCheckbox', 'MyCheckboxField', ['a'], undefined, true
    );
  });

  test('checkbox payload with disabled:false STILL passes explicit false (not undefined)', () => {
    const loadComboItems = jest.fn();
    const dispatch = makeDispatcher({ LoadComboItems: loadComboItems });
    dispatch({
      actions: [{
        type: 'loadComboItems',
        controlType: 'checkbox',
        url: '/Home/GetCheckboxItems',
        id: 'MyCheckbox2',
        field: 'MyCheckboxField2',
        selectVal: [],
        disabled: false
      }]
    });
    expect(loadComboItems.mock.calls[0][6]).toBe(false);
  });

  test('is a no-op when url or id is missing (does not call ff.LoadComboItems)', () => {
    const loadComboItems = jest.fn();
    const dispatch = makeDispatcher({ LoadComboItems: loadComboItems });
    dispatch({ actions: [{ type: 'loadComboItems', controlType: 'combo', id: 'MyCombo' }] });
    dispatch({ actions: [{ type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems' }] });
    expect(loadComboItems).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// Real ff.DispatchAction / ff.LoadComboItems end-to-end (fresh vm instance)
// ---------------------------------------------------------------------------
// A richer jQuery-like mock than setup.js's ($.get / .attr / .append / [0])
// is needed here because ff.LoadComboItems itself (not just DispatchAction)
// is exercised for real — same rationale as framework_layui_sec_332's
// ff._makeInput DOM tests, extended to cover the ajax round trip.
function makeJQueryMock(ajaxData) {
  function wrap(el) {
    return {
      0: el,
      length: el ? 1 : 0,
      attr: function (name) { return el ? el.getAttribute(name) : undefined; },
      append: function (child) { if (el) { el.appendChild(child); } return this; }
    };
  }
  const getSpy = jest.fn(function (url, params, cb) {
    cb(ajaxData, 'success');
  });
  const $ = function (selector) {
    if (typeof selector === 'string' && selector.charAt(0) === '#') {
      return wrap(document.getElementById(selector.slice(1)));
    }
    return wrap(null);
  };
  $.get = getSpy;
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.fn = {};
  return $;
}

function loadFreshFf(layui, $) {
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    // Issue #633: the dialog-path tests below exercise the REAL
    // ff._collectInitFromHtml, which parses html via `new DOMParser()` — must
    // be forwarded from this test file's own jsdom global, same as
    // framework_layui_627_legacy_killswitch.test.js's loadFreshFf.
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx;
}

describe('#633 real ff.DispatchAction -> ff.LoadComboItems (island dispatch calls the SAME underlying fetch/populate as the inline path)', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('combo: dispatch fetches the URL and calls window[id].update(...) — same as the inline call', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Apple', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyCombo';
    document.body.appendChild(el);
    const updateSpy = jest.fn();
    ctx.MyCombo = { update: updateSpy };

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'combo',
        url: '/Home/GetItems', id: 'MyCombo', field: 'ComboField', selectVal: ['1']
      }]
    });

    expect($.get).toHaveBeenCalledWith('/Home/GetItems', {}, expect.any(Function));
    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy.mock.calls[0][0].data[0]).toMatchObject({ value: '1', name: 'Apple' });
  });

  test('checkbox: dispatch rebuilds <input> elements via DOM API and calls layui.form.render', () => {
    const ajaxData = {
      Data: [
        { Value: 'a', Text: 'Alpha', Disabled: false, Selected: false },
        { Value: 'b', Text: 'Beta', Disabled: false, Selected: true }
      ]
    };
    const $ = makeJQueryMock(ajaxData);
    const formRender = jest.fn();
    const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyCheckbox';
    el.setAttribute('lay-filter', 'MyCheckboxFilter');
    document.body.appendChild(el);

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'checkbox',
        url: '/Home/GetCheckboxItems', id: 'MyCheckbox', field: 'CheckboxField',
        selectVal: [], disabled: true
      }]
    });

    expect($.get).toHaveBeenCalledWith('/Home/GetCheckboxItems', {}, expect.any(Function));
    const inputs = el.querySelectorAll('input[type="checkbox"]');
    expect(inputs.length).toBe(2);
    expect(inputs[0].value).toBe('a');
    expect(inputs[0].title).toBe('Alpha');
    expect(inputs[0].disabled).toBe(true); // island's disabled:true parity
    expect(inputs[1].checked).toBe(true);  // item.Selected === true
    expect(formRender).toHaveBeenCalledWith('checkbox', 'MyCheckboxFilterdiv');
  });

  test('checkbox: disabled:false (explicit) produces non-disabled inputs — parity with legacy explicit-false call', () => {
    const ajaxData = { Data: [{ Value: 'a', Text: 'Alpha', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyCheckbox2';
    document.body.appendChild(el);

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'checkbox',
        url: '/Home/GetCheckboxItems', id: 'MyCheckbox2', field: 'CheckboxField2',
        selectVal: [], disabled: false
      }]
    });

    const input = el.querySelector('input[type="checkbox"]');
    expect(input.disabled).toBe(false);
  });

  test('radio: dispatch rebuilds <input> elements via DOM API and calls layui.form.render', () => {
    const ajaxData = { Data: [{ Value: 'x', Text: 'X', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    const formRender = jest.fn();
    const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyRadio';
    el.setAttribute('lay-filter', 'MyRadioFilter');
    document.body.appendChild(el);

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'radio',
        url: '/Home/GetRadioItems', id: 'MyRadio', field: 'RadioField', selectVal: []
      }]
    });

    expect($.get).toHaveBeenCalledWith('/Home/GetRadioItems', {}, expect.any(Function));
    const input = el.querySelector('input[type="radio"]');
    expect(input).not.toBeNull();
    expect(input.value).toBe('x');
    expect(input.checked).toBe(true);
    expect(formRender).toHaveBeenCalledWith('radio', 'MyRadioFilterdiv');
  });

  test('transfer: dispatch calls layui.transfer.reload with the fetched data', () => {
    const ajaxData = { Data: [{ Value: 't1', Text: 'Transfer One', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    const reload = jest.fn();
    const layui = { form: { render: jest.fn() }, transfer: { reload: reload }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyTransfer';
    document.body.appendChild(el);

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'transfer',
        url: '/Home/GetTransferItems', id: 'MyTransfer', field: 'TransferField', selectVal: ['t1']
      }]
    });

    expect($.get).toHaveBeenCalledWith('/Home/GetTransferItems', {}, expect.any(Function));
    expect(reload).toHaveBeenCalledTimes(1);
    expect(reload.mock.calls[0][0]).toBe('MyTransfer');
    expect(reload.mock.calls[0][1].data[0]).toMatchObject({ value: 't1', title: 'Transfer One' });
  });

  test('failed ajax response ("success" !== status) calls layui.layer.alert, never throws', () => {
    const $ = makeJQueryMock({ Data: [] });
    $.get = jest.fn(function (url, params, cb) { cb(null, 'error'); });
    const alertSpy = jest.fn();
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: alertSpy } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyComboErr';
    document.body.appendChild(el);

    expect(() => {
      ctx.ff.DispatchAction({
        actions: [{
          type: 'loadComboItems', controlType: 'combo',
          url: '/Home/GetItems', id: 'MyComboErr', field: 'ComboField', selectVal: []
        }]
      });
    }).not.toThrow();
    expect(alertSpy).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// Review follow-up: partially-islandified widget must degrade with a
// diagnostic, not an uncaught throw, under the #627 kill-switch.
// ---------------------------------------------------------------------------
// With DisableLegacyScriptRehydration enabled, the loadComboItems ISLAND
// above dispatches unconditionally (islands are data), but the widget's own
// render call (xmSelect.render / layui.transfer.render) is still a bare
// inline <script>, which the kill-switch blocks. Before the fix, that left
// window[controlid] unset (combo) or the layui.transfer internal registry
// unpopulated (transfer), so the $.get success callback threw an uncaught
// TypeError on a later tick. checkbox/radio are unaffected — they only ever
// touch the static #Id div, which always exists as markup — and are covered
// here as a regression guard.
describe('#633 review follow-up — unrendered widget degrades with a diagnostic instead of throwing', () => {
  afterEach(() => {
    document.body.innerHTML = '';
    jest.restoreAllMocks();
  });

  const warnMessage = (id) =>
    '[WTM] LoadComboItems: widget "' + id + '" was never rendered — its inline render script did not run. ' +
    'If DisableLegacyScriptRehydration is enabled (#627), this widget still emits a legacy inline render script ' +
    'and is not yet islandified (#470 hard blocker). Items were fetched but could not be applied.';

  test('combo, widget unrendered (window[Id] undefined): no throw, console.warn called once with the actionable message, nothing else mutated', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Apple', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const formRender = jest.fn();
    const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'UnrenderedCombo';
    document.body.appendChild(el);
    // window['UnrenderedCombo'] deliberately left unset — simulates the
    // xmSelect.render(...) inline <script> having been blocked by the #627
    // kill-switch.
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ctx.ff.DispatchAction({
        actions: [{
          type: 'loadComboItems', controlType: 'combo',
          url: '/Home/GetItems', id: 'UnrenderedCombo', field: 'ComboField', selectVal: ['1']
        }]
      });
    }).not.toThrow();

    expect($.get).toHaveBeenCalledWith('/Home/GetItems', {}, expect.any(Function));
    expect(warnSpy).toHaveBeenCalledTimes(1);
    expect(warnSpy).toHaveBeenCalledWith(warnMessage('UnrenderedCombo'));
    // Nothing else fires for the combo branch — no DOM mutation, no
    // layui.form.render call (that's only for checkbox/radio).
    expect(el.innerHTML).toBe('');
    expect(formRender).not.toHaveBeenCalled();
  });

  test('combo, widget rendered (mock with .update): update called with the fetched data exactly as before, warn NOT called (parity guard)', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Apple', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'RenderedCombo';
    document.body.appendChild(el);
    const updateSpy = jest.fn();
    ctx.RenderedCombo = { update: updateSpy };
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'combo',
        url: '/Home/GetItems', id: 'RenderedCombo', field: 'ComboField', selectVal: ['1']
      }]
    });

    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy.mock.calls[0][0].data[0]).toMatchObject({ value: '1', name: 'Apple' });
    expect(warnSpy).not.toHaveBeenCalled();
  });

  test('transfer, unrendered (layui.transfer.reload throws, mirroring the real layui internal-registry miss): no throw, warn called once', () => {
    const ajaxData = { Data: [{ Value: 't1', Text: 'Transfer One', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    // Mirrors real layui.transfer.reload's behavior when `id` was never
    // registered via transfer.render({id: ...}): TypeError reading a
    // property off undefined (r.that[id]).
    const reload = jest.fn(() => {
      throw new TypeError("Cannot read properties of undefined (reading 'reload')");
    });
    const layui = { form: { render: jest.fn() }, transfer: { reload: reload }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'UnrenderedTransfer';
    document.body.appendChild(el);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ctx.ff.DispatchAction({
        actions: [{
          type: 'loadComboItems', controlType: 'transfer',
          url: '/Home/GetTransferItems', id: 'UnrenderedTransfer', field: 'TransferField', selectVal: ['t1']
        }]
      });
    }).not.toThrow();

    expect(reload).toHaveBeenCalledTimes(1);
    expect(warnSpy).toHaveBeenCalledTimes(1);
    expect(warnSpy).toHaveBeenCalledWith(warnMessage('UnrenderedTransfer'));
  });

  test('transfer, rendered: reload called normally, warn not called', () => {
    const ajaxData = { Data: [{ Value: 't1', Text: 'Transfer One', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    const reload = jest.fn();
    const layui = { form: { render: jest.fn() }, transfer: { reload: reload }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'RenderedTransfer';
    document.body.appendChild(el);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'transfer',
        url: '/Home/GetTransferItems', id: 'RenderedTransfer', field: 'TransferField', selectVal: ['t1']
      }]
    });

    expect(reload).toHaveBeenCalledTimes(1);
    expect(reload.mock.calls[0][0]).toBe('RenderedTransfer');
    expect(reload.mock.calls[0][1].data[0]).toMatchObject({ value: 't1', title: 'Transfer One' });
    expect(warnSpy).not.toHaveBeenCalled();
  });

  test('checkbox/radio unaffected — regression guard: static div still populated, warn never called', () => {
    const ajaxData = {
      Data: [
        { Value: 'a', Text: 'Alpha', Disabled: false, Selected: false },
        { Value: 'b', Text: 'Beta', Disabled: false, Selected: true }
      ]
    };
    const $ = makeJQueryMock(ajaxData);
    const formRender = jest.fn();
    const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const checkboxEl = document.createElement('div');
    checkboxEl.id = 'UnaffectedCheckbox';
    checkboxEl.setAttribute('lay-filter', 'UnaffectedCheckboxFilter');
    document.body.appendChild(checkboxEl);
    const radioEl = document.createElement('div');
    radioEl.id = 'UnaffectedRadio';
    radioEl.setAttribute('lay-filter', 'UnaffectedRadioFilter');
    document.body.appendChild(radioEl);
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'checkbox',
        url: '/Home/GetCheckboxItems', id: 'UnaffectedCheckbox', field: 'CheckboxField', selectVal: []
      }]
    });
    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'radio',
        url: '/Home/GetRadioItems', id: 'UnaffectedRadio', field: 'RadioField', selectVal: []
      }]
    });

    expect(checkboxEl.querySelectorAll('input[type="checkbox"]').length).toBe(2);
    expect(radioEl.querySelectorAll('input[type="radio"]').length).toBe(2);
    expect(formRender).toHaveBeenCalledWith('checkbox', 'UnaffectedCheckboxFilterdiv');
    expect(formRender).toHaveBeenCalledWith('radio', 'UnaffectedRadioFilterdiv');
    expect(warnSpy).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// Dialog path: real ff._collectInitFromHtml / ff._replayInitFromHtml
// ---------------------------------------------------------------------------
describe('#633 dialog path — real ff._collectInitFromHtml / ff._replayInitFromHtml', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('a loadComboItems island inside a same-origin dialog partial is collected and dispatched to ff.LoadComboItems', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Apple', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'DialogCombo';
    document.body.appendChild(el);
    const updateSpy = jest.fn();
    ctx.DialogCombo = { update: updateSpy };

    const html = `
      <div id="DialogCombo" wtm-ctype="combo"></div>
      <script type="application/json" class="wtm-dialog-init">${JSON.stringify({
        type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems',
        id: 'DialogCombo', field: 'ComboField', selectVal: ['1']
      })}</script>
    `;

    const collected = ctx.ff._collectInitFromHtml(html);
    expect(collected.islandPayloads).toHaveLength(1);
    expect(collected.islandPayloads[0].actions[0].type).toBe('loadComboItems');

    ctx.ff._replayInitFromHtml(collected);

    expect($.get).toHaveBeenCalledWith('/Home/GetItems', {}, expect.any(Function));
    expect(updateSpy).toHaveBeenCalledTimes(1);
  });

  test('legacy inline scripts and the loadComboItems island are BOTH extracted when they coexist in the same partial', () => {
    // Mirrors ComboBoxTagHelper's real shape post-#633: the unconditional
    // xmSelect render <script> stays a legacy inline script; only the
    // ItemUrl LoadComboItems call becomes an island. Extraction must collect
    // both, independently, from the same partial.
    //
    // The "legacy scripts replay BEFORE islands dispatch" ordering guarantee
    // itself is locked in by source position assertions in
    // framework_layui_587_scoped_consumer.test.js (scriptIdx < dispatchIdx
    // inside ff._replayInitFromHtml) — not re-derived here. Re-deriving it
    // behaviorally in THIS file would require the re-injected <script> to
    // execute against the SAME global realm ff.LoadComboItems's `window[id]`
    // lookup resolves against; ff._replayInitFromHtml's script re-injection
    // targets the real, shared `document` (by design — see its own comment),
    // which is a different realm than this test's isolated vm.createContext
    // sandbox, so that particular interleaving isn't observable from here.
    const ajaxData = { Data: [{ Value: '1', Text: 'Apple', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'DialogCombo2';
    document.body.appendChild(el);
    const updateSpy = jest.fn();
    ctx.DialogCombo2 = { update: updateSpy };

    const html = `
      <div id="DialogCombo2" wtm-ctype="combo"></div>
      <script type="application/json" class="wtm-dialog-init">${JSON.stringify({
        type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems',
        id: 'DialogCombo2', field: 'ComboField', selectVal: ['1']
      })}</script>
      <script>var xmSelectStandIn = 1;</script>
    `;

    const collected = ctx.ff._collectInitFromHtml(html);
    expect(collected.initScripts).toHaveLength(1);
    expect(collected.initScripts[0]).toContain('xmSelectStandIn');
    expect(collected.islandPayloads).toHaveLength(1);
    expect(collected.islandPayloads[0].actions[0]).toMatchObject({
      type: 'loadComboItems', controlType: 'combo', id: 'DialogCombo2'
    });

    ctx.ff._replayInitFromHtml(collected);

    // The island still dispatches correctly even with a sibling legacy
    // script present in the same partial.
    expect($.get).toHaveBeenCalledWith('/Home/GetItems', {}, expect.any(Function));
    expect(updateSpy).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// Page-ready path: bare loadComboItems island is compatible with the generic
// page-ready consumer contract (hand-rolled stub — see file header for why).
// ---------------------------------------------------------------------------
describe('#633 page-ready path — loadComboItems island shape compatibility', () => {
  function consumePageReadyIslands(dispatchFn) {
    var nodes = document.querySelectorAll(
      'script[type="application/json"].wtm-dialog-init:not([data-wtm-dispatched])'
    );
    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      node.setAttribute('data-wtm-dispatched', '1');
      if (!node.textContent) { continue; }
      try {
        var parsed = JSON.parse(node.textContent);
        var normalized = (function (p) {
          if (!p || typeof p !== 'object') { return null; }
          if (Array.isArray(p.actions)) { return p; }
          if (p.type) { return { actions: [p] }; }
          return null;
        })(parsed);
        if (normalized !== null) { dispatchFn(normalized); }
      } catch (e) { /* malformed island → skip */ }
    }
  }

  afterEach(() => { document.body.innerHTML = ''; });

  function addIsland(json) {
    const el = document.createElement('script');
    el.type = 'application/json';
    el.className = 'wtm-dialog-init';
    el.textContent = JSON.stringify(json);
    document.body.appendChild(el);
    return el;
  }

  test('a bare loadComboItems island present at page load is normalized and dispatched', () => {
    addIsland({
      type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems',
      id: 'PageCombo', field: 'ComboField', selectVal: ['1', '2']
    });
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(1);
    expect(dispatchFn).toHaveBeenCalledWith({
      actions: [{
        type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems',
        id: 'PageCombo', field: 'ComboField', selectVal: ['1', '2']
      }]
    });
  });

  test('a loadComboItems island coexists with other action types on the same page, each dispatched once', () => {
    addIsland({ type: 'loadComboItems', controlType: 'checkbox', url: '/Home/GetA', id: 'A', field: 'F', selectVal: [] });
    addIsland({ type: 'laydate', opts: { elem: '#PageDate' } });
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(2);
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(2); // idempotent — no re-dispatch
  });
});

// ---------------------------------------------------------------------------
// Payload field validation
// ---------------------------------------------------------------------------
describe('#633 payload field validation — ff._normalizeIslandPayload (real function)', () => {
  test('a bare loadComboItems action is wrapped into {actions:[...]}', () => {
    const parsed = {
      type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems',
      id: 'C1', field: 'F1', selectVal: ['1']
    };
    const normalized = global.ff._normalizeIslandPayload(parsed);
    expect(normalized).toEqual({ actions: [parsed] });
  });

  test('an already-wrapped {actions:[...]} loadComboItems payload passes through unchanged', () => {
    const parsed = { actions: [{ type: 'loadComboItems', controlType: 'radio', url: '/x', id: 'R1', field: 'F', selectVal: [] }] };
    const normalized = global.ff._normalizeIslandPayload(parsed);
    expect(normalized).toBe(parsed);
  });

  test('malformed payload (no type, no actions array) is rejected', () => {
    expect(global.ff._normalizeIslandPayload({ controlType: 'combo' })).toBeNull();
    expect(global.ff._normalizeIslandPayload(null)).toBeNull();
    expect(global.ff._normalizeIslandPayload('not an object')).toBeNull();
  });
});
