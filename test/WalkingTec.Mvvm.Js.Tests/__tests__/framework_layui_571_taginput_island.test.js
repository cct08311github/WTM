// Tests for Issue #571: TagInputTagHelper is reimplemented as a native,
// dependency-free tag/chip input. The previous implementation targeted a
// layui.tagInput module that has never shipped in any bundled layui tree
// (neither the vendored 2.6.3 nor the opt-in 2.13.8 layui-next — see
// test/manual/regression/README.md #14, verified during #566), so
// layui.use(['tagInput'], cb) never resolved and the widget silently
// rendered nothing.
//
// What this file locks in place:
//   1. DispatchAction gains a 'tagInput' case that calls the new
//      ff._renderTagInputAction render body — a PURE DOM widget with NO
//      layui module dependency at all (unlike slider/rate/colorpicker,
//      #552).
//   2. _islandModulesFor deliberately does NOT map 'tagInput' to any layui
//      module — it always dispatches immediately, no layui.use deferral.
//   3. Chips are built with createElement + textContent/createTextNode
//      ONLY — a tag value containing markup (e.g.
//      '<img src=x onerror=alert(1)>') must render as inert text, never
//      as a real DOM element (the #462/#552 stored-XSS threat class).
//   4. Adding a chip (Enter keydown) and removing a chip (click the ×)
//      keep the bound hidden input's joined value in sync.
//   5. A late-created container (island dispatched before its container
//      exists in the DOM) is a safe no-op — never throws.
//   6. framework_layui.js active-code eval( count remains exactly 1.
//
// Following the same convention as framework_layui_552_static_widgets_island
// .test.js: this widget needs no layui module, so — unlike that file, which
// has to fall back to a hand-rolled dispatcher stub for most of its
// assertions because the module-level `ff` in setup.js is compiled without a
// working `document`/`layui` in scope — every behavioral test here loads a
// FRESH instance of the real framework_layui.js into its own vm context that
// DOES provide a working `document` (the real jsdom document for this test
// file), and calls the REAL ff.DispatchAction / ff._renderTagInputAction
// directly. There is no reimplementation to drift out of sync with the
// source file.

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
describe('#571 — source sweep', () => {
  test('DispatchAction switch contains a tagInput case', () => {
    expect(active).toMatch(/case\s+['"]tagInput['"]/);
  });

  test('_renderTagInputAction exists and builds chips with textContent/createTextNode, never innerHTML', () => {
    expect(active).toMatch(/_renderTagInputAction\s*:\s*function/);
    const block = active.match(/_renderTagInputAction\s*:\s*function[\s\S]*?\n\s{4}\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/createTextNode/);
    expect(block[0]).not.toMatch(/\.innerHTML\s*=/);
    expect(block[0]).not.toMatch(/insertAdjacentHTML/);
  });

  test('_islandModulesFor does NOT map tagInput to any layui module (pure DOM widget, no deferral needed)', () => {
    // Issue #470 Slice K: bumped from 1900 — 'renderTransfer' added a new
    // _islandModulesFor branch (layui.transfer IS a layui.use(...) module,
    // unlike xm-select/tagInput/bindInput), growing the function body.
    // Issue #470 Slice N1: bound bumped 2200 -> 2600 — 'renderTreeContainer'
    // added a new _islandModulesFor branch, growing the function body further.
    // Issue #470 Slice O3: bound bumped 2600 -> 3300 — the function grew
    // with the new 'foldPanel' module-deferral branch (comment 18118 §2 O3).
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,3300}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).not.toMatch(/a\.type\s*===\s*['"]tagInput['"]/);
    expect(block[0]).not.toMatch(/mods\.push\(\s*['"]tagInput['"]\s*\)/);
  });

  test('no new eval/new Function call sites introduced (still exactly 1 legacy eval)', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('no stray layui.tagInput / layui.use([\'tagInput\']) reference remains in framework_layui.js', () => {
    expect(active).not.toMatch(/layui\.tagInput/);
    expect(active).not.toMatch(/layui\.use\(\s*\[\s*['"]tagInput['"]/);
  });
});

// ---------------------------------------------------------------------------
// Behavioral: real ff.DispatchAction / ff._renderTagInputAction against a
// fresh vm-loaded instance of the actual source, with a working jsdom document.
// ---------------------------------------------------------------------------
function loadFreshFf() {
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui: undefined,
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

function makeIslandDom(containerId, hiddenId, initialValue) {
  document.body.innerHTML =
    '<div id="' + containerId + '"></div>' +
    '<input type="hidden" id="' + hiddenId + '" value="' + (initialValue || '') + '" />';
}

describe('#571 tagInput island — real ff.DispatchAction', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('renders chips from an initial value (comma-separated)', () => {
    makeIslandDom('ti1', 'ti1_val', 'red,green,blue');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{
        type: 'tagInput',
        opts: { elem: '#ti1', separator: ',' },
        valueFieldId: 'ti1_val',
      }],
    });
    const chips = document.querySelectorAll('#ti1 .wtm-taginput-chip');
    expect(chips.length).toBe(3);
    expect(Array.from(chips).map((c) => c.firstChild.textContent)).toEqual(['red', 'green', 'blue']);
  });

  test('adding a chip via Enter keydown updates the hidden field joined value', () => {
    makeIslandDom('ti2', 'ti2_val', 'red');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#ti2', separator: ',' }, valueFieldId: 'ti2_val' }],
    });
    const entry = document.querySelector('#ti2 .wtm-taginput-entry');
    expect(entry).not.toBeNull();
    entry.value = 'green';
    const evt = new window.KeyboardEvent('keydown', { key: 'Enter', cancelable: true });
    entry.dispatchEvent(evt);

    const hidden = document.getElementById('ti2_val');
    expect(hidden.value).toBe('red,green');
    const chips = document.querySelectorAll('#ti2 .wtm-taginput-chip');
    expect(chips.length).toBe(2);
  });

  // Issue #585 (D): chip removal is bound to 'mousedown' (not 'click') to
  // close a blur/rebuild race — see
  // framework_layui_585_taginput_hardening.test.js for the full race
  // regression coverage. This test dispatches 'mousedown' (not 'click') to
  // match the current implementation.
  test('removing a chip via mousedown on the × control updates the hidden field joined value', () => {
    makeIslandDom('ti3', 'ti3_val', 'red,green,blue');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#ti3', separator: ',' }, valueFieldId: 'ti3_val' }],
    });
    const closeButtons = document.querySelectorAll('#ti3 .wtm-taginput-chip-close');
    expect(closeButtons.length).toBe(3);
    // Remove the middle chip ("green").
    closeButtons[1].dispatchEvent(new window.MouseEvent('mousedown', { bubbles: true, cancelable: true }));

    const hidden = document.getElementById('ti3_val');
    expect(hidden.value).toBe('red,blue');
    const remainingChips = document.querySelectorAll('#ti3 .wtm-taginput-chip');
    expect(remainingChips.length).toBe(2);
    expect(Array.from(remainingChips).map((c) => c.firstChild.textContent)).toEqual(['red', 'blue']);
  });

  // ---- Adversarial: a tag value carrying markup must render as inert text ----
  test('a tag value like "<img src=x onerror=alert(1)>" renders as inert text, never executes / never becomes a real element', () => {
    const payload = '<img src=x onerror=alert(1)>';
    makeIslandDom('ti4', 'ti4_val', encodeURIComponent(payload));
    // The hidden input's initial value is set as a raw attribute above via
    // markup; jsdom parses attribute values as plain text (no HTML parsing
    // inside an attribute value), so decode it back before comparing — this
    // merely sets up the "value already contains a dangerous string" state,
    // exactly like a persisted DB value would.
    document.getElementById('ti4_val').value = payload;

    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#ti4', separator: ',' }, valueFieldId: 'ti4_val' }],
    });

    const container = document.getElementById('ti4');
    // No <img> element was ever created — the payload was never parsed as HTML.
    expect(container.querySelectorAll('img').length).toBe(0);
    expect(container.querySelectorAll('script').length).toBe(0);
    const chip = container.querySelector('.wtm-taginput-chip');
    expect(chip).not.toBeNull();
    // The chip's visible text is the raw payload, verbatim, as inert text.
    expect(chip.firstChild.textContent).toBe(payload);
    expect(chip.firstChild.nodeType).toBe(3); // TEXT_NODE
  });

  test('adding a chip whose value carries markup also renders as inert text (not just initial-value chips)', () => {
    makeIslandDom('ti5', 'ti5_val', '');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#ti5', separator: ',' }, valueFieldId: 'ti5_val' }],
    });
    const entry = document.querySelector('#ti5 .wtm-taginput-entry');
    const payload = '<svg onload=alert(1)>';
    entry.value = payload;
    entry.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));

    const container = document.getElementById('ti5');
    expect(container.querySelectorAll('svg').length).toBe(0);
    const chip = container.querySelector('.wtm-taginput-chip');
    expect(chip.firstChild.textContent).toBe(payload);
  });

  test('readonly: no entry input and no remove control are rendered', () => {
    makeIslandDom('ti6', 'ti6_val', 'red,green');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#ti6', separator: ',', readonly: true }, valueFieldId: 'ti6_val' }],
    });
    expect(document.querySelector('#ti6 .wtm-taginput-entry')).toBeNull();
    expect(document.querySelectorAll('#ti6 .wtm-taginput-chip-close').length).toBe(0);
    expect(document.querySelectorAll('#ti6 .wtm-taginput-chip').length).toBe(2);
  });

  test('max: adding beyond the configured max is a no-op', () => {
    makeIslandDom('ti7', 'ti7_val', 'a,b');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#ti7', separator: ',', max: 2 }, valueFieldId: 'ti7_val' }],
    });
    const entry = document.querySelector('#ti7 .wtm-taginput-entry');
    entry.value = 'c';
    entry.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));

    const hidden = document.getElementById('ti7_val');
    expect(hidden.value).toBe('a,b');
    expect(document.querySelectorAll('#ti7 .wtm-taginput-chip').length).toBe(2);
  });

  // ---- Late-arriving module / late container: still a safe no-op / renders once available ----
  test('dispatching against a container that does not yet exist in the DOM is a safe no-op (never throws)', () => {
    document.body.innerHTML = '';
    const ff = loadFreshFf();
    expect(() => {
      ff.DispatchAction({
        actions: [{ type: 'tagInput', opts: { elem: '#missing' }, valueFieldId: 'missing_val' }],
      });
    }).not.toThrow();
  });

  test('a smuggled function-valued opt is never invoked and never reaches the DOM', () => {
    makeIslandDom('ti8', 'ti8_val', '');
    const ff = loadFreshFf();
    const evilFn = jest.fn();
    expect(() => {
      ff.DispatchAction({
        actions: [{
          type: 'tagInput',
          opts: { elem: '#ti8', separator: ',', placeholder: evilFn },
          valueFieldId: 'ti8_val',
        }],
      });
    }).not.toThrow();
    expect(evilFn).not.toHaveBeenCalled();
    // typeof guard drops the non-string placeholder — entry input has no
    // placeholder attribute set.
    const entry = document.querySelector('#ti8 .wtm-taginput-entry');
    expect(entry.placeholder).toBe('');
  });

  test('tagInput dispatch never requires or touches layui (works with layui entirely undefined)', () => {
    makeIslandDom('ti9', 'ti9_val', 'x,y');
    const ff = loadFreshFf();
    expect(() => {
      ff.DispatchAction({
        actions: [{ type: 'tagInput', opts: { elem: '#ti9', separator: ',' }, valueFieldId: 'ti9_val' }],
      });
    }).not.toThrow();
    expect(document.querySelectorAll('#ti9 .wtm-taginput-chip').length).toBe(2);
  });
});
