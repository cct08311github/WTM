// Tests for Issue #633 (#470-F, widget-islandification slice 2) x #635
// (#470 prerequisite): end-to-end proof that a loadComboItems island emitted
// by ComboBoxTagHelper/CheckBoxTagHelper/RadioTagHelper/TransferTagHelper's
// ItemUrl branch, when that field is used inside a <wt:selector> search
// panel, actually reaches ff.LoadComboItems through the REAL
// ff.OpenDialog2 code path — not a synthetic/hand-rolled dispatcher.
//
// Why this file exists (redo of #633 — prior attempt's HIGH regression):
// before #635 landed, ff.OpenDialog2 had NO island machinery at all — a
// wtm-dialog-init island tokenized into the #Temp{Id} search-panel template
// would round-trip into an inert <script type="application/json"> tag that
// was never dispatched, AND SelectorTagHelper's pre-#635 tokenization left
// the island's lone "</script>" as an orphaned $$#script$$ token with no
// matching open, corrupting the composed dialog HTML with an unclosed
// <script> that silently swallowed everything rendered after it. #635 (now
// in this branch's base) fixed both halves:
//   1. SelectorTagHelper.cs tokenizes a wtm-dialog-init island's own open+
//      close tag as ONE matched $$dialoginit$$/$$#dialoginit$$ pair, BEFORE
//      the bare-<script> escape (see
//      test/WalkingTec.Mvvm.Core.Test/TagHelpers/
//      SelectorTagHelperLoadComboItemsIsland633Tests.cs for the C#-side
//      proof against REAL ComboBoxTagHelper/CheckBoxTagHelper output).
//   2. framework_layui.js's OpenDialog2 rehydrates those tokens back into a
//      real island <script> and, via a new layer.open `success` callback,
//      calls ff.ConsumeIslandsIn(layero) to dispatch it.
//
// This file is the JS-side half: it hand-crafts an ALREADY-TOKENIZED
// $$dialoginit$$...$$#dialoginit$$ fixture carrying the EXACT bare
// loadComboItems payload shape ComboBoxTagHelper/CheckBoxTagHelper emit
// (matching this repo's established convention — see the #635
// framework_layui_635_opendialog2_island_dispatch.test.js file header for
// why hand-crafting the tokenized fixture, rather than re-running the C#
// TagHelper, is the right split here), drives the REAL ff.OpenDialog2, and
// asserts ff.LoadComboItems is actually invoked with the correct positional
// arguments — closing the "never proven end-to-end" gap the #633 redo
// brief called out.

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

// ---------------------------------------------------------------------------
// Harness — mirrors framework_layui_635_opendialog2_island_dispatch.test.js's
// loadFreshFf/makeOpenDialog2LayuiWithDomInsertion/makeOpenDialog2TempEl
// exactly, with the jQuery mock widened (relative to #635's
// makeSelectorJqueryFactory) to also support ff.LoadComboItems's own
// `$("#"+controlid).attr(...)` / `$.get(...)` calls — #635's own harness
// never needed those because its fixtures used the 'alert' action, not
// 'loadComboItems'.
// ---------------------------------------------------------------------------

function makeJQueryMock(comboAjaxData) {
  function wrap(el) {
    return {
      0: el,
      length: el ? 1 : 0,
      attr: function (name) { return el ? el.getAttribute(name) : undefined; },
      append: function (child) { if (el) { el.appendChild(child); } return this; },
      text: function (s) { this._text = s; return this; },
      html: function () { return this._text; }
    };
  }
  const $ = function (selector) {
    if (typeof selector === 'string' && selector.charAt(0) === '#') {
      return wrap(document.getElementById(selector.slice(1)));
    }
    return wrap(null);
  };
  // OpenDialog2's OWN outer request (fetches the search-panel HTML).
  $.ajax = jest.fn((opts) => {
    const request = { getResponseHeader: () => null };
    opts.success(OPEN_DIALOG2_RESPONSE_HTML, 'success', request);
  });
  // ff.LoadComboItems's inner request (fetches the combo/checkbox/radio/
  // transfer item data) — a SEPARATE ajax call from the one above, with a
  // different response shape (matches framework_layui_633_loadcomboitems_
  // island.test.js's makeJQueryMock convention).
  $.get = jest.fn((url, params, cb) => { cb(comboAjaxData, 'success'); });
  $.cookie = jest.fn();
  $.fn = {};
  return $;
}

function loadFreshFf(layui, $, domPurifyImpl) {
  const ctx = vm.createContext({
    window: {},
    document,
    layui: layui || { use: jest.fn(), layer: { alert: jest.fn(), msg: jest.fn() } },
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    DOMParser: global.DOMParser,
    DOMPurify: domPurifyImpl,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, ctx: ctx };
}

function makePassthroughDomPurify() {
  return { sanitize: function (html) { return html === undefined || html === null ? '' : html; } };
}

// Same shape as #635's makeOpenDialog2LayuiWithDomInsertion: layer.open
// ACTUALLY inserts `content` into the live jsdom document (required because
// #635's dispatch mechanism is driven by a REAL DOM subtree via
// ff.ConsumeIslandsIn(layero)), executes any JS-typed <script> found in it
// (application/json islands are left untouched, exactly as a real browser
// would), THEN calls `success` — see #635's file for the real-layer.js
// ordering verification this mirrors.
function makeOpenDialog2LayuiWithDomInsertion() {
  return {
    use: jest.fn((mods, cb) => cb()),
    form: { render: jest.fn() },
    layer: {
      load: jest.fn(() => 1),
      close: jest.fn(),
      alert: jest.fn(),
      full: jest.fn(),
      open: jest.fn((opts) => {
        var container = document.createElement('div');
        // Safety note (mirrors framework_layui_635_opendialog2_island_dispatch
        // .test.js's identical mock): `opts.content` here is ALWAYS a string
        // built by this same test file's own fixtures (OPEN_DIALOG2_RESPONSE_
        // HTML + the hand-crafted #Temp{id} template above) — never external/
        // attacker-controlled input — so this innerHTML assignment is safe
        // test-mock plumbing, not a real sanitization boundary (the real
        // boundary is ff.SafeHtml/DOMPurify, exercised inside OpenDialog2
        // itself before this mock ever sees `content`).
        container.innerHTML = opts.content;
        document.body.appendChild(container);
        var scripts = container.querySelectorAll('script');
        for (var i = 0; i < scripts.length; i++) {
          var s = scripts[i];
          var type = (s.getAttribute('type') || '').toLowerCase();
          var isJs = type === '' || type === 'text/javascript' || type === 'application/javascript';
          if (isJs) {
            // Safety note: stands in for jQuery's own domManip/DOMEval script-
            // execution step (real jQuery .append()/.html() executes
            // recognized-JS-type <script> elements found in an inserted HTML
            // string). `s.textContent` is this test file's own fixture text
            // (never attacker-controlled) — test-only jQuery-behavior
            // emulation, not a production code path.
            (new Function(s.textContent))();
          }
        }
        if (typeof opts.success === 'function') { opts.success(container); }
        return 'mockWinId';
      })
    }
  };
}

function makeOpenDialog2TempEl(id, innerHtml) {
  var el = document.createElement('div');
  el.id = id;
  el.innerHTML = innerHtml;
  document.body.appendChild(el);
  return el;
}

const OPEN_DIALOG2_RESPONSE_HTML =
  '<div>wtVar_x = table.render(xoption);</div>' +
  '<table id="g1" lay-filter="f1"></table>' +
  '$$SearchPanel$$';

describe('#633 x #635 — ff.OpenDialog2 selector-panel loadComboItems island reaches ff.LoadComboItems end-to-end', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
  });

  test('1. a combo item-url island (bare loadComboItems payload) tokenized into the search panel dispatches ff.LoadComboItems with the correct positional args', () => {
    var comboAjaxData = { Data: [{ Value: '1', Text: 'Apple', Disabled: false, Selected: false }] };
    var $ = makeJQueryMock(comboAjaxData);
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var payload = {
      type: 'loadComboItems',
      controlType: 'combo',
      url: '/Home/GetComboItems',
      id: 'DialogCombo633',
      field: 'ComboField',
      selectVal: ['1']
    };
    makeOpenDialog2TempEl(
      'Temp633a',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff, ctx } = loadFreshFf(layui, $, makePassthroughDomPurify());
    // The combo widget's own global handle, as xmSelect.render(...) would
    // have assigned it BEFORE this island ever dispatches (the #633 timing
    // analysis this redo was asked to re-verify, not re-litigate).
    var updateSpy = jest.fn();
    ctx.DialogCombo633 = { update: updateSpy };
    var loadComboItemsSpy = jest.spyOn(ff, 'LoadComboItems');

    ff.OpenDialog2('/some/search/url', 'w633a', 'Title', 500, 400, '#Temp633a');

    // The island actually reached ff.DispatchAction -> ff.LoadComboItems,
    // with the exact positional args framework_layui.js's 'loadComboItems'
    // DispatchAction case maps them to (controlType, url, id, field,
    // selectVal, cb=undefined, disabled=undefined for combo).
    expect(loadComboItemsSpy).toHaveBeenCalledWith(
      'combo', '/Home/GetComboItems', 'DialogCombo633', 'ComboField', ['1'], undefined, undefined
    );
    // And ff.LoadComboItems itself actually ran to completion: it fetched
    // the URL and populated the widget — proving this is not merely "the
    // dispatcher was invoked" but the REAL end-to-end effect.
    expect($.get).toHaveBeenCalledWith('/Home/GetComboItems', {}, expect.any(Function));
    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy.mock.calls[0][0].data[0]).toMatchObject({ value: '1', name: 'Apple' });
  });

  test('2. a checkbox item-url island (with disabled:true) tokenized into the search panel dispatches ff.LoadComboItems with disabled threaded through as the 7th arg', () => {
    var checkboxAjaxData = {
      Data: [{ Value: 'a', Text: 'Alpha', Disabled: false, Selected: true }]
    };
    var $ = makeJQueryMock(checkboxAjaxData);
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var payload = {
      type: 'loadComboItems',
      controlType: 'checkbox',
      url: '/Home/GetCheckboxItems',
      id: 'DialogCheckbox633',
      field: 'CheckboxField',
      selectVal: [],
      disabled: true
    };
    makeOpenDialog2TempEl(
      'Temp633b',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$<div>panel</div>'
    );
    var checkboxEl = document.createElement('div');
    checkboxEl.id = 'DialogCheckbox633';
    checkboxEl.setAttribute('lay-filter', 'DialogCheckbox633Filter');
    document.body.appendChild(checkboxEl);
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var loadComboItemsSpy = jest.spyOn(ff, 'LoadComboItems');

    ff.OpenDialog2('/some/search/url', 'w633b', 'Title', 500, 400, '#Temp633b');

    expect(loadComboItemsSpy).toHaveBeenCalledWith(
      'checkbox', '/Home/GetCheckboxItems', 'DialogCheckbox633', 'CheckboxField', [], undefined, true
    );
    // Real effect: the <input type="checkbox"> was rebuilt disabled.
    var input = checkboxEl.querySelector('input[type="checkbox"]');
    expect(input).not.toBeNull();
    expect(input.disabled).toBe(true);
    expect(layui.form.render).toHaveBeenCalledWith('checkbox', 'DialogCheckbox633Filterdiv');
  });

  test('3. no island present: ff.LoadComboItems is never called (no false positive)', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl('Temp633c', '<div>panel with no item-url field</div>');
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var loadComboItemsSpy = jest.spyOn(ff, 'LoadComboItems');

    ff.OpenDialog2('/some/search/url', 'w633c', 'Title', 500, 400, '#Temp633c');

    expect(loadComboItemsSpy).not.toHaveBeenCalled();
    expect($.get).not.toHaveBeenCalled();
  });
});
