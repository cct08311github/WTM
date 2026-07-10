// Tests for Issue #638 — ff.ChainChange's checkbox/radio branches read
// window[comboid + "defaultvalues"] directly inside the $.get success
// callback (when usedefaultvalue === true) and immediately call
// df.indexOf(item.Value) on the result, with no null guard.
//
// combo/tree already tolerate an absent global: their consumers
// (ff.getComboItems / ff.getTreeItems) both start with
//   if (svals == undefined || svals == null) { svals = []; }
// checkbox/radio had no equivalent guard, so a ChainChange TARGET
// checkbox/radio that never published its own {comboid}defaultvalues global
// (reachable — e.g. a chained target rendered via item-url, which has zero
// static items and, pre-#638, never emitted the defaultvalues script at all)
// throws `TypeError: df.indexOf is not a function` on iteration 0. By the
// time that throws, `target.html('')` has already cleared the control (the
// unconditional "clear" switch above the $.get call) and
// `form.render(controltype, targetfilter)` — which only runs AFTER the
// item-population loop completes — is never reached: the whole linked
// control renders blank, not just "missing defaults".
//
// This file drives the REAL ff.ChainChange end-to-end: a fresh vm instance
// of the actual source, a real DOM via jsdom, and a jQuery-compatible mock
// sized to exactly what ChainChange calls ($(sel).find(sel).attr/.html/
// .append/[0]/.length, plus $.get) — not a source-sweep or a hand-rolled
// reimplementation of the fix, so the assertions exercise the actual code.

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

// A jQuery-compatible mock covering exactly what ff.ChainChange calls on a
// wrapped set: $(selector) / .find(selector) / .attr(name) / .length / [0] /
// .html(str) / .append(node) — mirrors framework_layui_633_loadcomboitems_
// island.test.js's makeJQueryMock, extended with .find()/.html() (ChainChange
// needs both; LoadComboItems alone does not).
function makeJQueryMock(ajaxData) {
  function wrap(el) {
    return {
      0: el,
      length: el ? 1 : 0,
      attr: function (name) { return el ? el.getAttribute(name) : undefined; },
      html: function (str) { if (el) { el.innerHTML = str; } return this; },
      append: function (child) { if (el) { el.appendChild(child); } return this; },
      find: function (selector) { return wrap(el ? el.querySelector(selector) : null); }
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
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx;
}

// Builds the DOM shape ff.ChainChange expects: a <form> containing a SOURCE
// element (`self` — carries wtm-linkto pointing at the target's id, exactly
// like ComboBoxTagHelper/CheckBoxTagHelper/RadioTagHelper's `on:` handler
// passes `$('#{Id}')[0]` as `self`) and a TARGET element (the chained
// checkbox/radio control — wtm-ctype/wtm-name/lay-filter, matching
// CheckBoxTagHelper/RadioTagHelper's own div-for output).
function buildChainDom(controltype) {
  document.body.innerHTML = '';
  const form = document.createElement('form');
  form.id = 'TestForm';
  const source = document.createElement('div');
  source.id = 'SourceField';
  source.setAttribute('wtm-linkto', 'TargetField');
  const target = document.createElement('div');
  target.id = 'TargetField';
  target.setAttribute('wtm-ctype', controltype);
  target.setAttribute('wtm-name', 'TargetField');
  target.setAttribute('lay-filter', 'TargetFieldfilter');
  form.appendChild(source);
  form.appendChild(target);
  document.body.appendChild(form);
  return { form, source, target };
}

describe.each(['checkbox', 'radio'])(
  '#638 ff.ChainChange(usedefaultvalue=true) — %s target with no published {id}defaultvalues global',
  (controltype) => {
    afterEach(() => { document.body.innerHTML = ''; });

    test('does not throw, renders all items unselected, and calls form.render', () => {
      const ajaxData = {
        Data: [
          { Value: 'a', Text: 'Alpha', Selected: false },
          { Value: 'b', Text: 'Beta', Selected: false }
        ]
      };
      const $ = makeJQueryMock(ajaxData);
      const formRender = jest.fn();
      const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
      const ctx = loadFreshFf(layui, $);
      const { source } = buildChainDom(controltype);

      // The reachable #638 configuration: window['TargetField' + 'defaultvalues']
      // is deliberately never set (e.g. TargetField was itself rendered via
      // item-url, which pre-#638 never published the global at all).
      expect(ctx.TargetFielddefaultvalues).toBeUndefined();

      expect(function () {
        ctx.ff.ChainChange('/Home/GetItems', source, true);
      }).not.toThrow();

      expect($.get).toHaveBeenCalledWith('/Home/GetItems', {}, expect.any(Function));

      const target = document.getElementById('TargetField');
      const inputs = target.querySelectorAll('input[type="' + controltype + '"]');
      expect(inputs.length).toBe(2);
      expect(inputs[0].value).toBe('a');
      expect(inputs[1].value).toBe('b');
      expect(inputs[0].checked).toBe(false);
      expect(inputs[1].checked).toBe(false);
      expect(formRender).toHaveBeenCalledWith(controltype, 'TargetFieldfilterdiv');
    });

    test('DOES apply defaults correctly when the global IS published (regression guard: the #638 fix must not neuter real defaults)', () => {
      const ajaxData = {
        Data: [
          { Value: 'a', Text: 'Alpha', Selected: false },
          { Value: 'b', Text: 'Beta', Selected: false }
        ]
      };
      const $ = makeJQueryMock(ajaxData);
      const formRender = jest.fn();
      const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
      const ctx = loadFreshFf(layui, $);
      const { source } = buildChainDom(controltype);
      ctx.TargetFielddefaultvalues = ['b'];

      ctx.ff.ChainChange('/Home/GetItems', source, true);

      const target = document.getElementById('TargetField');
      const inputs = target.querySelectorAll('input[type="' + controltype + '"]');
      expect(inputs[0].checked).toBe(false);
      expect(inputs[1].checked).toBe(true);
    });
  }
);
