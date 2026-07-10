// Tests for Issue #645 — ff.ChainChange vs ff.LoadComboItems race on a field
// that is BOTH the target of a chain link (link-field/link-id -> wtm-linkto)
// AND has its own item-url (islandified since #633 to dispatch its
// ff.LoadComboItems fetch at DOMContentLoaded). On an edit page (a
// preselected value present), ComboBoxTagHelper also emits a setTimeout that
// fires ff.ChainChange(TriggerUrl, self, true) ~100ms after load. Both code
// paths independently $.get() and, on success, mutate the SAME target
// element with no generation/cancellation guard — whichever ajax response
// lands last silently wins, even when that means the stale/unfiltered
// item-url list clobbers the correct filtered chain result.
//
// The fix (both in framework_layui.js): the moment ff.ChainChange applies
// items to `target` (any of its tree/transfer/combo/checkbox/radio branches,
// regardless of usedefaultvalue), it stamps `data-wtm-chain-applied` on that
// element. ff.LoadComboItems checks the SAME element for that marker before
// applying its own fetched items and skips (with a quiet console.debug note)
// once claimed. This makes the outcome order-independent: whichever of the
// two $.get calls resolves LAST, the chain's result always wins once it has
// fired at all; a page with no chain link never sees the marker and
// item-url behaves exactly as before.
//
// This file drives the REAL ff.ChainChange and ff.LoadComboItems end-to-end
// (fresh vm instance of the actual source, real DOM via jsdom) — not a
// source-sweep or a hand-rolled reimplementation of the fix. $.get is mocked
// as a queue so each test can resolve the two competing ajax calls in
// whichever order it wants to exercise, mirroring the real race.

'use strict';

// Security note (test-only code, no untrusted input):
//   - `.innerHTML = ''` / `.innerHTML = str` below only ever clear the jsdom
//     test fixture or mirror the jQuery `.html()` mock already used by
//     framework_layui_638_chainchange_defaults_guard.test.js — `str` is
//     always a hardcoded literal from this file's own test bodies, never
//     external/user input, so there is no injection surface.
//   - The literal substring "eval(" appears only inside a regex
//     (`/\beval\(/g`) and a test description string, asserting the ACTIVE
//     eval-call count in framework_layui.js stays at the #633-established
//     baseline of 1 — the same invariant check already used unmodified by
//     framework_layui_633_loadcomboitems_island.test.js. No eval() is
//     executed anywhere in this file.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

// A jQuery-compatible mock covering everything ff.ChainChange and
// ff.LoadComboItems call on a wrapped set: $(selector) / .find(selector) /
// .attr(name) [getter] / .attr(name, value) [setter, for the #645 marker] /
// .length / [0] / .html(str) / .append(node) — union of
// framework_layui_633_loadcomboitems_island.test.js's and
// framework_layui_638_chainchange_defaults_guard.test.js's mocks, extended
// with an .attr() setter (neither #633 nor #638 needed to WRITE an
// attribute; #645 does).
//
// $.get is a QUEUE, not an immediate callback: each call is recorded and
// held until the test explicitly resolves it via `$._resolve(url, data)`.
// This lets a test choose which of two competing in-flight requests
// resolves first, exactly mirroring the real race the bug report describes.
function makeQueuedJQueryMock() {
  const pending = [];

  function wrap(el) {
    return {
      0: el,
      length: el ? 1 : 0,
      attr: function (name, value) {
        if (!el) { return value === undefined ? undefined : this; }
        if (value === undefined) {
          return el.getAttribute(name) === null ? undefined : el.getAttribute(name);
        }
        el.setAttribute(name, value);
        return this;
      },
      html: function (str) { if (el) { el.innerHTML = str; } return this; },
      append: function (child) { if (el) { el.appendChild(child); } return this; },
      find: function (selector) { return wrap(el ? el.querySelector(selector) : null); }
    };
  }

  const getSpy = jest.fn(function (url, params, cb) {
    pending.push({ url: url, cb: cb });
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
  // Resolves the OLDEST pending $.get registered for `url` with (data, status).
  $._resolve = function (url, data, status) {
    const idx = pending.findIndex(function (p) { return p.url === url; });
    if (idx === -1) {
      throw new Error('makeQueuedJQueryMock: no pending $.get for url "' + url + '"');
    }
    const entry = pending[idx];
    pending.splice(idx, 1);
    entry.cb(data, status || 'success');
  };
  $._pendingCount = function () { return pending.length; };
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

// Builds the DOM shape for a field that is simultaneously a chain TARGET
// (some other SOURCE field's wtm-linkto points at it) and its own item-url
// island subject: a <form> containing a SOURCE div (carries wtm-linkto,
// exactly like ComboBoxTagHelper's `on:` handler / edit-page setTimeout
// passes `$('#{Id}')[0]` as `self`) and a TARGET combo div whose `id`
// equals both `linkto.value` (what ChainChange resolves via
// `$('#'+formid).find('#'+linkto.value)`) and `controlid` (what
// ff.LoadComboItems resolves via `$('#'+controlid)`) — the same string, so
// both functions must land on the identical DOM node.
function buildComboChainDom(targetId) {
  document.body.innerHTML = '';
  const form = document.createElement('form');
  form.id = 'TestForm';
  const source = document.createElement('div');
  source.id = 'SourceCombo';
  source.setAttribute('wtm-linkto', targetId);
  const target = document.createElement('div');
  target.id = targetId;
  target.setAttribute('wtm-ctype', 'combo');
  target.setAttribute('wtm-name', 'TargetField');
  form.appendChild(source);
  form.appendChild(target);
  document.body.appendChild(form);
  return { form, source, target };
}

const unfilteredData = { Data: [
  { Value: '1', Text: 'All-Item-One', Selected: false },
  { Value: '2', Text: 'All-Item-Two', Selected: false },
  { Value: '3', Text: 'All-Item-Three', Selected: false }
] };

const filteredData = { Data: [
  { Value: '2', Text: 'Filtered-Item-Two', Selected: false }
] };

describe('#645 ff.ChainChange vs ff.LoadComboItems — same-target race', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('DOM-node-identity precondition: ChainChange target and LoadComboItems target resolve to the SAME element', () => {
    const $ = makeQueuedJQueryMock();
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const { target } = buildComboChainDom('IdentityCombo');
    ctx.IdentityCombo = { update: jest.fn() };

    // LoadComboItems resolves its target the same way production code does:
    // $('#'+controlid). Compare against the real DOM node ChainChange would
    // resolve via $('#'+formid).find('#'+linkto.value) for the same id.
    const loadComboItemsTargetNode = ctx.window.$('#IdentityCombo')[0];
    const chainChangeTargetNode = ctx.window.$('#TestForm').find('#IdentityCombo')[0];

    expect(loadComboItemsTargetNode).toBe(target);
    expect(chainChangeTargetNode).toBe(target);
    expect(loadComboItemsTargetNode).toBe(chainChangeTargetNode);
  });

  test('scenario 1 — item-url lands first, chain lands last: final items are the filtered/chain set', () => {
    const $ = makeQueuedJQueryMock();
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const { source } = buildComboChainDom('RaceCombo1');
    const updateSpy = jest.fn();
    ctx.RaceCombo1 = { update: updateSpy };

    // item-url island dispatch (DOMContentLoaded) — registers its $.get.
    ctx.ff.LoadComboItems('combo', '/ItemUrl', 'RaceCombo1', 'TargetField', ['1']);
    // edit-page default-value cascade (setTimeout) — registers its $.get.
    ctx.ff.ChainChange('/TriggerUrl', source, true);

    // item-url's ajax response lands FIRST.
    $._resolve('/ItemUrl', unfilteredData);
    expect(updateSpy).toHaveBeenLastCalledWith({ data: expect.arrayContaining([
      expect.objectContaining({ value: '1' }),
      expect.objectContaining({ value: '2' }),
      expect.objectContaining({ value: '3' })
    ]) });

    // chain's ajax response lands LAST — must win.
    $._resolve('/TriggerUrl', filteredData);
    expect(updateSpy).toHaveBeenLastCalledWith({ data: [
      expect.objectContaining({ value: '2', name: 'Filtered-Item-Two' })
    ] });

    const targetEl = document.getElementById('RaceCombo1');
    expect(targetEl.getAttribute('data-wtm-chain-applied')).toBe('1');
  });

  test('scenario 2 — chain lands first, item-url lands last: item-url SKIPS, final items stay the chain set, skip note logged', () => {
    const $ = makeQueuedJQueryMock();
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const { source } = buildComboChainDom('RaceCombo2');
    const updateSpy = jest.fn();
    ctx.RaceCombo2 = { update: updateSpy };
    const debugSpy = jest.spyOn(console, 'debug').mockImplementation(() => {});

    ctx.ff.LoadComboItems('combo', '/ItemUrl', 'RaceCombo2', 'TargetField', ['1']);
    ctx.ff.ChainChange('/TriggerUrl', source, true);

    // chain's ajax response lands FIRST — claims the target.
    $._resolve('/TriggerUrl', filteredData);
    expect(updateSpy).toHaveBeenLastCalledWith({ data: [
      expect.objectContaining({ value: '2', name: 'Filtered-Item-Two' })
    ] });
    const callsAfterChain = updateSpy.mock.calls.length;
    expect(document.getElementById('RaceCombo2').getAttribute('data-wtm-chain-applied')).toBe('1');

    // item-url's stale ajax response lands LAST — must be skipped, not applied.
    $._resolve('/ItemUrl', unfilteredData);
    expect(updateSpy.mock.calls.length).toBe(callsAfterChain); // no new update() call
    expect(updateSpy).toHaveBeenLastCalledWith({ data: [
      expect.objectContaining({ value: '2', name: 'Filtered-Item-Two' })
    ] });
    expect(debugSpy).toHaveBeenCalledTimes(1);
    expect(debugSpy).toHaveBeenCalledWith(
      '[WTM] LoadComboItems: widget "RaceCombo2" already claimed by ff.ChainChange — skipping stale item-url apply (#645).'
    );

    debugSpy.mockRestore();
  });

  test('scenario 3 — add page (no chain fires): item-url applies normally, no marker set, no skip note logged', () => {
    const $ = makeQueuedJQueryMock();
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    // No source/link — a bare combo with only item-url, as on an Add page
    // where the field starts empty and the default cascade never fires.
    document.body.innerHTML = '';
    const target = document.createElement('div');
    target.id = 'AddPageCombo';
    target.setAttribute('wtm-ctype', 'combo');
    document.body.appendChild(target);
    const updateSpy = jest.fn();
    ctx.AddPageCombo = { update: updateSpy };
    const debugSpy = jest.spyOn(console, 'debug').mockImplementation(() => {});

    ctx.ff.LoadComboItems('combo', '/ItemUrl', 'AddPageCombo', 'TargetField', []);
    $._resolve('/ItemUrl', unfilteredData);

    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy).toHaveBeenLastCalledWith({ data: expect.arrayContaining([
      expect.objectContaining({ value: '1' }),
      expect.objectContaining({ value: '2' }),
      expect.objectContaining({ value: '3' })
    ]) });
    expect(document.getElementById('AddPageCombo').getAttribute('data-wtm-chain-applied')).toBeNull();
    expect(debugSpy).not.toHaveBeenCalled();

    debugSpy.mockRestore();
  });

  test('scenario 4 — user-driven refresh (usedefaultvalue=false) also claims the target: a hypothetically-late item-url still yields', () => {
    const $ = makeQueuedJQueryMock();
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const { source } = buildComboChainDom('RaceCombo4');
    const updateSpy = jest.fn();
    ctx.RaceCombo4 = { update: updateSpy };

    // A user-driven chain refresh: usedefaultvalue is falsy, exactly like
    // ComboBoxTagHelper's `on:` change handler calls
    // ff.ChainChange(u, $('#{Id}')[0]) with no 3rd argument.
    ctx.ff.ChainChange('/TriggerUrl', source);
    $._resolve('/TriggerUrl', filteredData);

    expect(document.getElementById('RaceCombo4').getAttribute('data-wtm-chain-applied')).toBe('1');
    expect(updateSpy).toHaveBeenLastCalledWith({ data: [
      expect.objectContaining({ value: '2', name: 'Filtered-Item-Two' })
    ] });
    const callsAfterChain = updateSpy.mock.calls.length;

    // A hypothetically-late item-url fetch for the SAME field (e.g. a
    // straggler request from an earlier dialog replay) must not clobber the
    // user's just-applied selection.
    ctx.ff.LoadComboItems('combo', '/ItemUrl', 'RaceCombo4', 'TargetField', []);
    $._resolve('/ItemUrl', unfilteredData);

    expect(updateSpy.mock.calls.length).toBe(callsAfterChain);
    expect(updateSpy).toHaveBeenLastCalledWith({ data: [
      expect.objectContaining({ value: '2', name: 'Filtered-Item-Two' })
    ] });
  });
});

// ---------------------------------------------------------------------------
// Source sweep — invariants that must hold regardless of behavioral coverage
// ---------------------------------------------------------------------------
describe('#645 — source sweep', () => {
  const stripLineComments = (text) =>
    text
      .split('\n')
      .map((line) => {
        const idx = line.indexOf('//');
        return idx === -1 ? line : line.slice(0, idx);
      })
      .join('\n');
  const active = stripLineComments(src);

  test('ff.ChainChange sets the data-wtm-chain-applied marker', () => {
    expect(active).toMatch(/data-wtm-chain-applied/);
  });

  test('ff.LoadComboItems checks the marker before applying fetched items', () => {
    const fn = active.match(/LoadComboItems:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(fn).toMatch(/data-wtm-chain-applied/);
  });

  test('Issue #645 comment reference is present near both the marker-set and marker-check sites', () => {
    expect(src).toMatch(/Issue #645[\s\S]{0,2000}ChainChange:\s*function/);
    expect(src).toMatch(/Issue #645[\s\S]{0,1500}data-wtm-chain-applied/);
  });

  test('active-code eval( count is still exactly 1 after the #645 fix', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});
