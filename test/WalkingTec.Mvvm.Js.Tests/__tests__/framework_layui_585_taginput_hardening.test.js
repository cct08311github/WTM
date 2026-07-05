// Tests for Issue #585: native TagInput (#571) hardening bundle.
//
// Three defects fixed here:
//
// (B) MEDIUM separator/Max bypass + no-trim — _tiAddTag previously counted a
//     pasted/typed string containing the separator as ONE tag against
//     _tiMax (e.g. max=5 with 4 existing tags, entering "a,b,c" passed the
//     `4 < 5` guard but produced 7 effective tags once the written-back
//     value was re-split by _tiCurrentTags). Separator handling was also
//     only ever exercised on keydown — paste/blur never sanitized. Initial
//     server values like "a, b" rendered with leading whitespace (the
//     legacy BuildTagsJson implementation trimmed; the native
//     _tiCurrentTags did not).
//     FIX: _tiAddTag now splits the raw input on the separator, trims each
//     piece, drops empties, and enforces _tiMax against the RESULTING total
//     tag count; _tiCurrentTags now trims every split piece too.
//
// (C) SECURITY-CONSISTENCY missing #578 form-containment gate — unlike
//     _renderSliderAction/_renderRateAction/_renderColorpickerAction (#578),
//     _renderTagInputAction resolved the hidden input page-wide with no
//     formEl.contains() check, and cleared an arbitrary opts.elem container
//     subtree unconditionally. Under the #462/#552 smuggled-island threat
//     model, an island like
//     {"type":"tagInput","opts":{"elem":"#anyContainer"},
//     "valueFieldId":"anyHiddenInput"} could wipe #anyContainer and bind
//     writes to any hidden input page-wide.
//     FIX: action.formId (server-sourced from the same ambient
//     context.Items["formid"] key FormTagHelper publishes — see
//     TagInputTagHelper.cs) gates BOTH the opts.elem container and the
//     hidden value input behind formEl.contains(...) before any
//     clearing/writing happens. Absent formId (back-compat) skips the gate
//     entirely, exactly like slider/rate/colorpicker.
//
// (D) LOW chip-remove click race — clicking a chip's × while the entry
//     input held pending text raced: mousedown -> the entry input's blur
//     handler fires first (browser default focus-shift) -> _tiAddTag
//     commits the pending text -> _tiRenderChips() rebuilds every chip node
//     from scratch, detaching the very × node mid-click -> the browser
//     suppresses the click on a since-removed target -> the removal is
//     silently swallowed.
//     FIX: chip removal is now bound to 'mousedown' (with preventDefault())
//     instead of 'click', so removal happens atomically before any blur
//     cascade can rebuild the chip DOM out from underneath it.
//
// Following the same convention as framework_layui_578_writeback_containment
// .test.js and framework_layui_571_taginput_island.test.js: loads a FRESH
// instance of the real framework_layui.js into its own vm context (real
// jsdom `document`) and calls the REAL ff.DispatchAction directly, so these
// tests exercise the actual shipped code, not a hand-rolled reimplementation.

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

function appendForm(formId) {
  const form = document.createElement('form');
  form.id = formId;
  document.body.appendChild(form);
  return form;
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#585 — source sweep', () => {
  test('_renderTagInputAction reads action.formId and gates BOTH the container and hidden input on .contains(...)', () => {
    const block = active.match(/_renderTagInputAction\s*:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(block).toMatch(/action\.formId/);
    expect(block).toMatch(/_tiFormEl\.contains\(/);
    // Containment must gate the container BEFORE the destructive clear
    // (container.firstChild removal marks the clear). Note: `block` comes
    // from stripLineComments'd `active`, so comment text (e.g. the
    // "Idempotent" marker in the source) is not searchable here — use the
    // actual clearing code as the marker instead.
    const containmentIdx = block.indexOf('_tiContained(container)');
    const clearIdx = block.indexOf('container.firstChild');
    expect(containmentIdx).toBeGreaterThan(-1);
    expect(clearIdx).toBeGreaterThan(-1);
    expect(containmentIdx).toBeLessThan(clearIdx);
  });

  test('_tiAddTag splits on the separator, trims, drops empties, and checks Max against the resulting total', () => {
    const block = active.match(/function _tiAddTag[\s\S]*?\n\s{12}\}/)[0];
    expect(block).toMatch(/split\(_tiSeparator\)/);
    expect(block).toMatch(/_tiMax/);
  });

  test('_tiCurrentTags trims each split piece', () => {
    const block = active.match(/function _tiCurrentTags[\s\S]*?\n\s{12}\}/)[0];
    expect(block).toMatch(/replace\(\/\^\\s\+\|\\s\+\$\/g/);
  });

  test('chip removal is bound to mousedown (not click), with preventDefault', () => {
    const block = active.match(/_renderTagInputAction\s*:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(block).toMatch(/_tiClose\.addEventListener\(\s*['"]mousedown['"]/);
    expect(block).not.toMatch(/_tiClose\.addEventListener\(\s*['"]click['"]/);
    expect(block).toMatch(/e\.preventDefault\(\)/);
  });

  test('active-code eval( count is still exactly 1 after #585 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// (B) separator/Max post-split enforcement + trim
// ---------------------------------------------------------------------------
describe('#585 (B) — separator/Max post-split enforcement and trim', () => {
  test('pasting "a,b,c" as one blur commit respects Max counted against the POST-SPLIT total', () => {
    // max=5, 4 existing tags -> adding the 3-piece pasted string would
    // produce 7 total, which must be rejected in full (no partial add).
    makeIslandDom('b1', 'b1_val', 'p,q,r,s');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#b1', separator: ',', max: 5 }, valueFieldId: 'b1_val' }],
    });
    const entry = document.querySelector('#b1 .wtm-taginput-entry');
    entry.value = 'a,b,c';
    entry.dispatchEvent(new window.Event('blur'));

    const hidden = document.getElementById('b1_val');
    expect(hidden.value).toBe('p,q,r,s');
    expect(document.querySelectorAll('#b1 .wtm-taginput-chip').length).toBe(4);
  });

  test('pasting a multi-value string that fits within Max adds every piece as a separate tag', () => {
    makeIslandDom('b2', 'b2_val', '');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#b2', separator: ',', max: 5 }, valueFieldId: 'b2_val' }],
    });
    const entry = document.querySelector('#b2 .wtm-taginput-entry');
    entry.value = 'a,b,c';
    entry.dispatchEvent(new window.Event('blur'));

    const hidden = document.getElementById('b2_val');
    expect(hidden.value).toBe('a,b,c');
    const chips = document.querySelectorAll('#b2 .wtm-taginput-chip');
    expect(chips.length).toBe(3);
    expect(Array.from(chips).map((c) => c.firstChild.textContent)).toEqual(['a', 'b', 'c']);
  });

  test('pasted pieces are trimmed and empty pieces are dropped (e.g. "a, b, ,c")', () => {
    makeIslandDom('b3', 'b3_val', '');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#b3', separator: ',' }, valueFieldId: 'b3_val' }],
    });
    const entry = document.querySelector('#b3 .wtm-taginput-entry');
    entry.value = 'a, b, ,c';
    entry.dispatchEvent(new window.Event('blur'));

    const hidden = document.getElementById('b3_val');
    expect(hidden.value).toBe('a,b,c');
    const chips = document.querySelectorAll('#b3 .wtm-taginput-chip');
    expect(Array.from(chips).map((c) => c.firstChild.textContent)).toEqual(['a', 'b', 'c']);
  });

  test('an initial server value with untrimmed whitespace ("a, b") renders trimmed chips', () => {
    makeIslandDom('b4', 'b4_val', 'a, b');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#b4', separator: ',' }, valueFieldId: 'b4_val' }],
    });
    const chips = document.querySelectorAll('#b4 .wtm-taginput-chip');
    expect(chips.length).toBe(2);
    expect(Array.from(chips).map((c) => c.firstChild.textContent)).toEqual(['a', 'b']);
  });

  test('a single (non-separator) value over Max is still rejected outright (no regression)', () => {
    makeIslandDom('b5', 'b5_val', 'a,b');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#b5', separator: ',', max: 2 }, valueFieldId: 'b5_val' }],
    });
    const entry = document.querySelector('#b5 .wtm-taginput-entry');
    entry.value = 'c';
    entry.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));

    const hidden = document.getElementById('b5_val');
    expect(hidden.value).toBe('a,b');
    expect(document.querySelectorAll('#b5 .wtm-taginput-chip').length).toBe(2);
  });
});

// ---------------------------------------------------------------------------
// (C) #578-style form-containment gate
// ---------------------------------------------------------------------------
describe('#585 (C) — tagInput form-containment (mirrors #578)', () => {
  test('regression: formId present, container + hidden input both inside the form -> renders and writes normally', () => {
    const form = appendForm('wtForm_ti1');
    const container = document.createElement('div');
    container.id = 'c1';
    form.appendChild(container);
    const hidden = document.createElement('input');
    hidden.type = 'hidden';
    hidden.id = 'c1_val';
    hidden.value = 'red,green';
    form.appendChild(hidden);

    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#c1', separator: ',' }, valueFieldId: 'c1_val', formId: 'wtForm_ti1' }],
    });

    expect(document.querySelectorAll('#c1 .wtm-taginput-chip').length).toBe(2);
  });

  test('SECURITY: formId present, opts.elem container resolves OUTSIDE the form -> container is never cleared, no chips render', () => {
    appendForm('wtForm_ti2');
    const outsiderContainer = document.createElement('div');
    outsiderContainer.id = 'attackerContainer';
    // Test-only fixture literal (not attacker/user-controlled data) used
    // purely as a DOM sentinel to assert the container subtree is never
    // wiped by the containment gate under test.
    outsiderContainer.innerHTML = '<span class="sentinel">untouched</span>';
    document.body.appendChild(outsiderContainer);
    const outsiderHidden = document.createElement('input');
    outsiderHidden.id = 'attackerHidden';
    outsiderHidden.value = 'untouched';
    document.body.appendChild(outsiderHidden);

    const ff = loadFreshFf();
    expect(() => {
      ff.DispatchAction({
        actions: [{
          type: 'tagInput',
          opts: { elem: '#attackerContainer', separator: ',' },
          valueFieldId: 'attackerHidden',
          formId: 'wtForm_ti2',
        }],
      });
    }).not.toThrow();

    // Container subtree must be untouched — never wiped.
    expect(outsiderContainer.querySelector('.sentinel')).not.toBeNull();
    expect(outsiderContainer.querySelectorAll('.wtm-taginput-chips').length).toBe(0);
    expect(outsiderHidden.value).toBe('untouched');
  });

  test('SECURITY: formId present, container inside the form but hidden input resolves OUTSIDE the form -> no write-back binding', () => {
    const form = appendForm('wtForm_ti3');
    const container = document.createElement('div');
    container.id = 'c3';
    form.appendChild(container);
    // Hidden input deliberately placed OUTSIDE the form (spoofed target).
    const outsiderHidden = document.createElement('input');
    outsiderHidden.id = 'c3_val_outside';
    outsiderHidden.value = 'untouched';
    document.body.appendChild(outsiderHidden);

    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{
        type: 'tagInput',
        opts: { elem: '#c3', separator: ',' },
        valueFieldId: 'c3_val_outside',
        formId: 'wtForm_ti3',
      }],
    });

    // The render is skipped entirely (fail-closed) — no chip UI is built,
    // and the outsider hidden input is never touched.
    expect(document.querySelectorAll('#c3 .wtm-taginput-chips').length).toBe(0);
    expect(outsiderHidden.value).toBe('untouched');
  });

  test('fail-closed: formId present but the form element does not exist in the DOM -> no render, no write', () => {
    const container = document.createElement('div');
    container.id = 'c4';
    document.body.appendChild(container);
    const hidden = document.createElement('input');
    hidden.id = 'c4_val';
    hidden.value = 'untouched';
    document.body.appendChild(hidden);

    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{
        type: 'tagInput',
        opts: { elem: '#c4', separator: ',' },
        valueFieldId: 'c4_val',
        formId: 'wtForm_does_not_exist',
      }],
    });

    expect(document.querySelectorAll('#c4 .wtm-taginput-chips').length).toBe(0);
    expect(hidden.value).toBe('untouched');
  });

  test('back-compat: formId absent (old island) -> renders and writes unguarded exactly as before', () => {
    makeIslandDom('c5', 'c5_val', 'red,green');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#c5', separator: ',' }, valueFieldId: 'c5_val' }],
    });

    expect(document.querySelectorAll('#c5 .wtm-taginput-chip').length).toBe(2);

    const entry = document.querySelector('#c5 .wtm-taginput-entry');
    entry.value = 'blue';
    entry.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    expect(document.getElementById('c5_val').value).toBe('red,green,blue');
  });
});

// ---------------------------------------------------------------------------
// (D) chip-remove mousedown race fix
// ---------------------------------------------------------------------------
describe('#585 (D) — chip removal survives the blur/rebuild race', () => {
  test('removing a chip via mousedown succeeds even with pending unconfirmed text in the entry input, and prevents default', () => {
    makeIslandDom('d1', 'd1_val', 'red,green,blue');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#d1', separator: ',' }, valueFieldId: 'd1_val' }],
    });
    const entry = document.querySelector('#d1 .wtm-taginput-entry');
    // Pending, uncommitted text in the entry input — simulates the user
    // having typed something but not yet pressed Enter/left the field.
    entry.value = 'yellow';

    const closeButtons = document.querySelectorAll('#d1 .wtm-taginput-chip-close');
    expect(closeButtons.length).toBe(3);

    const evt = new window.MouseEvent('mousedown', { bubbles: true, cancelable: true });
    closeButtons[1].dispatchEvent(evt); // remove "green"

    expect(evt.defaultPrevented).toBe(true);
    const hidden = document.getElementById('d1_val');
    expect(hidden.value).toBe('red,blue');
    const remainingChips = document.querySelectorAll('#d1 .wtm-taginput-chip');
    expect(remainingChips.length).toBe(2);
  });

  test('regression reproduction: even when a blur-triggered rebuild happens first (old race window), a fresh mousedown on the re-rendered × still removes the correct tag', () => {
    // This mirrors the exact sequence the bug described: blur fires (e.g.
    // from an unrelated focus change) and rebuilds the chip DOM BEFORE the
    // user's mousedown lands. Because removal is now bound to mousedown on
    // whichever × node currently exists (rather than a stale reference
    // captured before the rebuild), the removal still succeeds against the
    // freshly rendered node.
    makeIslandDom('d2', 'd2_val', 'red,green,blue');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#d2', separator: ',' }, valueFieldId: 'd2_val' }],
    });
    const entry = document.querySelector('#d2 .wtm-taginput-entry');
    entry.value = 'yellow';
    // Simulate the default browser blur cascade committing pending text
    // and rebuilding the chip DOM.
    entry.dispatchEvent(new window.Event('blur'));
    expect(document.getElementById('d2_val').value).toBe('red,green,blue,yellow');

    const closeButtons = document.querySelectorAll('#d2 .wtm-taginput-chip-close');
    expect(closeButtons.length).toBe(4);
    closeButtons[1].dispatchEvent(new window.MouseEvent('mousedown', { bubbles: true, cancelable: true }));

    expect(document.getElementById('d2_val').value).toBe('red,blue,yellow');
  });

  test('click alone (no mousedown) no longer triggers removal', () => {
    makeIslandDom('d3', 'd3_val', 'red,green');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#d3', separator: ',' }, valueFieldId: 'd3_val' }],
    });
    const closeButtons = document.querySelectorAll('#d3 .wtm-taginput-chip-close');
    closeButtons[0].dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));

    expect(document.getElementById('d3_val').value).toBe('red,green');
    expect(document.querySelectorAll('#d3 .wtm-taginput-chip').length).toBe(2);
  });

  // Review follow-up: rebinding removal from 'click' to 'mousedown' means it
  // now fires for every mouse button, not just the primary one ('click' only
  // ever fires for the primary button; auxiliary buttons dispatch
  // 'auxclick'). Without a button === 0 guard, right-clicking or
  // middle-clicking the × would silently delete the tag — a regression the
  // old 'click' binding did not have. These pin the guard.
  test('right-click (button: 2) on the close control does NOT remove the tag and does not preventDefault', () => {
    makeIslandDom('d4', 'd4_val', 'red,green');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#d4', separator: ',' }, valueFieldId: 'd4_val' }],
    });
    const closeButtons = document.querySelectorAll('#d4 .wtm-taginput-chip-close');
    const evt = new window.MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 2 });
    closeButtons[0].dispatchEvent(evt);

    expect(evt.defaultPrevented).toBe(false);
    expect(document.getElementById('d4_val').value).toBe('red,green');
    expect(document.querySelectorAll('#d4 .wtm-taginput-chip').length).toBe(2);
  });

  test('middle-click (button: 1) on the close control does NOT remove the tag', () => {
    makeIslandDom('d5', 'd5_val', 'red,green');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#d5', separator: ',' }, valueFieldId: 'd5_val' }],
    });
    const closeButtons = document.querySelectorAll('#d5 .wtm-taginput-chip-close');
    const evt = new window.MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 1 });
    closeButtons[0].dispatchEvent(evt);

    expect(evt.defaultPrevented).toBe(false);
    expect(document.getElementById('d5_val').value).toBe('red,green');
    expect(document.querySelectorAll('#d5 .wtm-taginput-chip').length).toBe(2);
  });

  test('primary click (button: 0, the jsdom default) still removes the tag as before', () => {
    makeIslandDom('d6', 'd6_val', 'red,green');
    const ff = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'tagInput', opts: { elem: '#d6', separator: ',' }, valueFieldId: 'd6_val' }],
    });
    const closeButtons = document.querySelectorAll('#d6 .wtm-taginput-chip-close');
    const evt = new window.MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0 });
    closeButtons[0].dispatchEvent(evt);

    expect(evt.defaultPrevented).toBe(true);
    expect(document.getElementById('d6_val').value).toBe('green');
    expect(document.querySelectorAll('#d6 .wtm-taginput-chip').length).toBe(1);
  });
});
