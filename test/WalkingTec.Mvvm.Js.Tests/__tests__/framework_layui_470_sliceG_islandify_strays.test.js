// Tests for Issue #470 Slice G (the bounded, zero-risk first slice of the
// #470 eval-retirement / islandification G→Q plan):
//
//   1. Tree item-url (the #633 miss) — TreeTagHelper's ItemUrl branch now
//      emits the SAME loadComboItems island the four #633 sibling controls
//      (combo/checkbox/radio/transfer) already emit, instead of an inline
//      <script>ff.LoadComboItems('tree',...)</script>. The 'loadComboItems'
//      DispatchAction case (and ff.LoadComboItems itself) already handled
//      controlType 'tree' — this was simply never wired to a server emitter.
//   2. ueditor / layedit render — UEditorTagHelper / RichTextBoxTagHelper now
//      emit NEW 'ueditor' / 'layedit' JSON islands instead of an inline
//      <script> calling layui.use([...], function(){...}). Both new
//      DispatchAction cases perform their OWN layui.use([...], cb) call
//      (mirroring the legacy inline script exactly, which always did the
//      same), so no new eval, no new callback-name resolution machinery.
//   3. TextArea counter — TextAreaTagHelper's ShowCounter now emits a
//      data-wtm-counter="<id>" attribute plus a SINGLE document-level
//      delegated 'input' listener (registered once, at script load) instead
//      of a per-widget inline <script> calling wtmCounter.init(...).
//
// Following the established convention (#556/#633/#601): source-sweep tests
// assert the real file structure; a fresh vm instance of the actual
// framework_layui.js (real jsdom `document`) is loaded so end-to-end tests
// exercise the REAL ff.DispatchAction / ff.LoadComboItems / delegated
// listener code, not a hand-rolled reimplementation.

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
describe('#470 Slice G — source sweep', () => {
  // Window bumped 300 -> 1300 (follow-up fix, Refs #470): the tree branch now
  // carries the same window[controlid]-readiness guard as the sibling combo
  // branch (see the new assertions below), which pushes the window[controlid]
  // update()/cb() calls further from the "controltype === \"tree\"" anchor.
  test('ff.LoadComboItems still has a tree branch calling window[controlid].update, with optional cb', () => {
    const block = active.match(/LoadComboItems\s*:\s*function[\s\S]{0,1200}?controltype === "tree"[\s\S]{0,1300}/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/window\[controlid\]\.update\(\s*\{\s*data:\s*da\s*\}\s*\)/);
    expect(block[0]).toMatch(/cb\s*!==\s*undefined\s*&&\s*cb\s*!=\s*null/);
  });

  // Follow-up fix (Refs #470): the tree branch is now exercised via the same
  // island dispatch path as combo (Slice G routed TreeTagHelper's ItemUrl
  // path through ff.LoadComboItems's tree branch), so it needs the identical
  // window[controlid]-readiness guard the combo branch already has — same
  // existence + typeof-function check, same degrade-with-a-warning behavior.
  test('the tree branch now guards window[controlid] before calling .update, identically to the combo branch', () => {
    const block = active.match(/controltype === "tree"[\s\S]{0,1300}?controltype == "transfer"/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/if\s*\(\s*!window\[controlid\]\s*\|\|\s*typeof\s+window\[controlid\]\.update\s*!==\s*'function'\s*\)/);
    expect(block[0]).toMatch(/console\.warn\(\s*'\[WTM\] LoadComboItems: widget "'\s*\+\s*controlid\s*\+\s*'" was never rendered/);
    expect(block[0]).toMatch(/\}\s*else\s*\{\s*window\[controlid\]\.update\(\s*\{\s*data:\s*da\s*\}\s*\)/);
  });

  test('DispatchAction switch contains ueditor and layedit cases', () => {
    expect(active).toMatch(/case\s+['"]ueditor['"]/);
    expect(active).toMatch(/case\s+['"]layedit['"]/);
  });

  test('ueditor case delegates to ff._renderUEditorAction', () => {
    const block = active.match(/case\s+['"]ueditor['"][\s\S]{0,120}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderUEditorAction\(\s*action\s*\)/);
  });

  test('layedit case delegates to ff._renderLayeditAction', () => {
    const block = active.match(/case\s+['"]layedit['"][\s\S]{0,120}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderLayeditAction\(\s*action\s*\)/);
  });

  test('_renderUEditorAction mirrors the legacy layui.use(["ueditorconfig"], ...) call', () => {
    const block = active.match(/_renderUEditorAction\s*:\s*function[\s\S]{0,900}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.use\(\s*\[\s*['"]ueditorconfig['"]\s*\]/);
    expect(block[0]).toMatch(/layui\.ueditor\.loadEditor\(\s*action\.id\s*\)\.ready\(/);
    expect(block[0]).toMatch(/this\.setContent\(\s*action\.content\s*\|\|\s*['"]['"]\s*\)/);
  });

  test('_renderLayeditAction mirrors the legacy layui.use("layedit", ...) call', () => {
    const block = active.match(/_renderLayeditAction\s*:\s*function[\s\S]{0,1400}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.use\(\s*['"]layedit['"]/);
    expect(block[0]).toMatch(/layedit\.set\(\s*\{\s*uploadImage:\s*\{\s*url:\s*action\.uploadUrl/);
    expect(block[0]).toMatch(/layedit\.build\(\s*action\.id\s*,\s*opts\s*\)/);
    expect(block[0]).toMatch(/setAttribute\(\s*['"]layeditindex['"]\s*,\s*index\s*\)/);
  });

  test('_islandModulesFor maps ueditor -> ueditorconfig module and layedit -> layedit module', () => {
    // Issue #470 Slice N1: bound bumped 2200 -> 2600 — the function grew with
    // the new 'renderTreeContainer' module-deferral branch.
    // Issue #470 Slice O3: bound bumped 2600 -> 3300 — the function grew
    // with the new 'foldPanel' module-deferral branch (comment 18118 §2 O3).
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,3300}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]ueditor['"][\s\S]{0,60}needed\.ueditorconfig\s*=\s*true/);
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]layedit['"][\s\S]{0,60}needed\.layedit\s*=\s*true/);
    expect(block[0]).toMatch(/mods\.push\(\s*['"]ueditorconfig['"]\s*\)/);
    expect(block[0]).toMatch(/mods\.push\(\s*['"]layedit['"]\s*\)/);
  });

  test('a single document-level delegated input listener drives data-wtm-counter (no per-widget inline script)', () => {
    expect(active).toMatch(/document\.addEventListener\(\s*['"]input['"]\s*,\s*function/);
    expect(active).toMatch(/getAttribute\(\s*['"]data-wtm-counter['"]\s*\)/);
    expect(active).toMatch(/field\.maxLength/);
  });

  test('active-code eval( count is still exactly 1 after Slice G changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Real ff.DispatchAction end-to-end (fresh vm instance, real jsdom document)
// ---------------------------------------------------------------------------
// Minimal jQuery mock for when a test doesn't care about $.get/.ajax (e.g. the
// ueditor/layedit/counter tests never issue an ajax call) — framework_layui.js
// calls $.ajax(...) unconditionally at top-level script load (i18n bootstrap),
// so `$` must always be a callable with an .ajax method, never undefined.
function defaultJQueryMock() {
  const fn = Object.assign(function () { return { cookie: jest.fn() }; }, {
    ajax: jest.fn(),
    cookie: jest.fn(),
    fn: {},
  });
  return fn;
}

function loadFreshFf(layui, $) {
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $ || defaultJQueryMock(),
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx;
}

function makeJQueryMock(ajaxData) {
  function wrap(el) {
    return {
      0: el,
      length: el ? 1 : 0,
      attr: function (name) { return el ? el.getAttribute(name) : undefined; },
    };
  }
  const getSpy = jest.fn(function (url, params, cb) { cb(ajaxData, 'success'); });
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

describe('#470 Slice G — tree loadComboItems island (the #633 miss)', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('DispatchAction("loadComboItems", controlType:"tree") fetches the URL and calls window[id].update — same as the old inline call', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Root', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyTree';
    document.body.appendChild(el);
    const updateSpy = jest.fn();
    ctx.MyTree = { update: updateSpy };

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'tree',
        url: '/Home/GetTreeItems', id: 'MyTree', field: 'TreeField', selectVal: ['1']
      }]
    });

    expect($.get).toHaveBeenCalledWith('/Home/GetTreeItems', {}, expect.any(Function));
    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy.mock.calls[0][0].data[0]).toMatchObject({ value: '1', name: 'Root' });
  });

  test('the omitted 6th positional cb arg (always undefined for island callers) never throws — matches the legacy no-op function() {}', () => {
    const ajaxData = { Data: [{ Value: 'x', Text: 'X', Disabled: false, Selected: false }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'MyTree2';
    document.body.appendChild(el);
    ctx.MyTree2 = { update: jest.fn() };

    expect(() => {
      ctx.ff.DispatchAction({
        actions: [{
          type: 'loadComboItems', controlType: 'tree',
          url: '/Home/GetTreeItems', id: 'MyTree2', field: 'TreeField', selectVal: []
        }]
      });
    }).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// Follow-up fix (Refs #470): the tree branch is now exercised via the same
// island dispatch path as combo (Slice G above), so it needs the identical
// window[controlid]-readiness guard the combo branch already has (#633
// review follow-up) — before this fix, the tree branch called
// window[controlid].update(...) with no guard at all, so a widget whose own
// xmSelect.render(...) inline <script> was blocked by the #627 kill-switch
// (or simply hadn't run yet) would throw an uncaught TypeError on the $.get
// success callback. Mirrors framework_layui_633_loadcomboitems_island.test.js's
// "review follow-up — unrendered widget degrades with a diagnostic instead of
// throwing" describe block exactly, adapted for controlType 'tree'.
// ---------------------------------------------------------------------------
describe('#470 tree LoadComboItems branch — unrendered widget degrades with a diagnostic instead of throwing (Refs #470)', () => {
  afterEach(() => {
    document.body.innerHTML = '';
    jest.restoreAllMocks();
  });

  const warnMessage = (id) =>
    '[WTM] LoadComboItems: widget "' + id + '" was never rendered — its inline render script did not run. ' +
    'If DisableLegacyScriptRehydration is enabled (#627), this widget still emits a legacy inline render script ' +
    'and is not yet islandified (#470 hard blocker). Items were fetched but could not be applied.';

  test('tree, widget unrendered (window[Id] undefined): no throw, console.warn called once with the actionable message, cb never invoked', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Root', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'UnrenderedTree';
    document.body.appendChild(el);
    // window['UnrenderedTree'] deliberately left unset — simulates the
    // xmSelect.render(...) inline <script> (TreeTagHelper) having been
    // blocked by the #627 kill-switch, or simply not having run yet.
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ctx.ff.DispatchAction({
        actions: [{
          type: 'loadComboItems', controlType: 'tree',
          url: '/Home/GetTreeItems', id: 'UnrenderedTree', field: 'TreeField', selectVal: ['1']
        }]
      });
    }).not.toThrow();

    expect($.get).toHaveBeenCalledWith('/Home/GetTreeItems', {}, expect.any(Function));
    expect(warnSpy).toHaveBeenCalledTimes(1);
    expect(warnSpy).toHaveBeenCalledWith(warnMessage('UnrenderedTree'));
  });

  test('tree, widget rendered (mock with .update): update called with the fetched data exactly as before, warn NOT called (parity guard)', () => {
    const ajaxData = { Data: [{ Value: '1', Text: 'Root', Disabled: false, Selected: true }] };
    const $ = makeJQueryMock(ajaxData);
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFf(layui, $);
    const el = document.createElement('div');
    el.id = 'RenderedTree';
    document.body.appendChild(el);
    const updateSpy = jest.fn();
    ctx.RenderedTree = { update: updateSpy };
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ctx.ff.DispatchAction({
      actions: [{
        type: 'loadComboItems', controlType: 'tree',
        url: '/Home/GetTreeItems', id: 'RenderedTree', field: 'TreeField', selectVal: ['1']
      }]
    });

    expect(updateSpy).toHaveBeenCalledTimes(1);
    expect(updateSpy.mock.calls[0][0].data[0]).toMatchObject({ value: '1', name: 'Root' });
    expect(warnSpy).not.toHaveBeenCalled();
  });
});

describe('#470 Slice G — ueditor island', () => {
  test('DispatchAction("ueditor") replays layui.use(["ueditorconfig"], ...) -> loadEditor(id).ready(...).setContent(content)', () => {
    const setContentSpy = jest.fn();
    const readySpy = jest.fn(function (cb) { cb.call({ setContent: setContentSpy }); });
    const loadEditorSpy = jest.fn(function () { return { ready: readySpy }; });
    const useSpy = jest.fn(function (mods, cb) { cb(); }); // module already "loaded"
    const layui = { use: useSpy, ueditor: { loadEditor: loadEditorSpy } };
    const ctx = loadFreshFf(layui, undefined);

    ctx.ff.DispatchAction({
      actions: [{ type: 'ueditor', id: 'MyEditor', content: 'Hello <b>World</b>' }]
    });

    expect(useSpy).toHaveBeenCalledWith(['ueditorconfig'], expect.any(Function));
    expect(loadEditorSpy).toHaveBeenCalledWith('MyEditor');
    expect(setContentSpy).toHaveBeenCalledWith('Hello <b>World</b>');
  });

  test('DispatchAction("ueditor") with an EMPTY content still calls setContent with "" (parity with the legacy call)', () => {
    const setContentSpy = jest.fn();
    const readySpy = jest.fn(function (cb) { cb.call({ setContent: setContentSpy }); });
    const layui = {
      use: jest.fn(function (mods, cb) { cb(); }),
      ueditor: { loadEditor: jest.fn(function () { return { ready: readySpy }; }) }
    };
    const ctx = loadFreshFf(layui, undefined);

    ctx.ff.DispatchAction({ actions: [{ type: 'ueditor', id: 'EmptyEditor' }] });

    expect(setContentSpy).toHaveBeenCalledWith('');
  });

  test('render is DEFERRED (not dropped) when ueditorconfig has not finished loading, then fires once layui.use resolves', () => {
    const setContentSpy = jest.fn();
    const readySpy = jest.fn(function (cb) { cb.call({ setContent: setContentSpy }); });
    const loadEditorSpy = jest.fn(function () { return { ready: readySpy }; });
    let queuedCb = null;
    const useSpy = jest.fn(function (mods, cb) { queuedCb = cb; }); // deferred — cb not called yet
    const layui = { use: useSpy, ueditor: { loadEditor: loadEditorSpy } };
    const ctx = loadFreshFf(layui, undefined);

    ctx.ff.DispatchAction({ actions: [{ type: 'ueditor', id: 'LateEditor', content: 'late' }] });
    expect(loadEditorSpy).not.toHaveBeenCalled();

    queuedCb(); // ueditorconfig module finishes loading
    expect(loadEditorSpy).toHaveBeenCalledWith('LateEditor');
    expect(setContentSpy).toHaveBeenCalledWith('late');
  });

  test('missing action.id is a no-op — never calls layui.use', () => {
    const useSpy = jest.fn();
    const layui = { use: useSpy, ueditor: { loadEditor: jest.fn() } };
    const ctx = loadFreshFf(layui, undefined);
    expect(() => {
      ctx.ff.DispatchAction({ actions: [{ type: 'ueditor', content: 'x' }] });
    }).not.toThrow();
    expect(useSpy).not.toHaveBeenCalled();
  });
});

describe('#470 Slice G — layedit island', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('DispatchAction("layedit") replays layedit.set(uploadImage) + layedit.build(id) and writes layeditindex back onto the element', () => {
    const el = document.createElement('textarea');
    el.id = 'MyRich';
    document.body.appendChild(el);

    const setSpy = jest.fn();
    const buildSpy = jest.fn(() => 42);
    const useSpy = jest.fn(function (mod, cb) { cb(); });
    const layui = { use: useSpy, layedit: { set: setSpy, build: buildSpy } };
    const ctx = loadFreshFf(layui, undefined);

    ctx.ff.DispatchAction({
      actions: [{ type: 'layedit', id: 'MyRich', uploadUrl: '/Upload/RichText' }]
    });

    expect(useSpy).toHaveBeenCalledWith('layedit', expect.any(Function));
    expect(setSpy).toHaveBeenCalledWith({ uploadImage: { url: '/Upload/RichText' } });
    // No `height` key on the action -> build() called with opts === undefined,
    // matching the legacy call's single-argument form layedit.build('{Id}').
    expect(buildSpy).toHaveBeenCalledWith('MyRich', undefined);
    expect(el.getAttribute('layeditindex')).toBe('42');
  });

  test('DispatchAction("layedit") with height threads {height:...} through to layedit.build as the 2nd arg', () => {
    const el = document.createElement('textarea');
    el.id = 'MyRich2';
    document.body.appendChild(el);

    const buildSpy = jest.fn(() => 7);
    const layui = {
      use: jest.fn(function (mod, cb) { cb(); }),
      layedit: { set: jest.fn(), build: buildSpy }
    };
    const ctx = loadFreshFf(layui, undefined);

    ctx.ff.DispatchAction({
      actions: [{ type: 'layedit', id: 'MyRich2', uploadUrl: '/x', height: 300 }]
    });

    expect(buildSpy).toHaveBeenCalledWith('MyRich2', { height: 300 });
  });

  test('missing action.id is a no-op — never calls layui.use', () => {
    const useSpy = jest.fn();
    const layui = { use: useSpy, layedit: { set: jest.fn(), build: jest.fn() } };
    const ctx = loadFreshFf(layui, undefined);
    expect(() => {
      ctx.ff.DispatchAction({ actions: [{ type: 'layedit', uploadUrl: '/x' }] });
    }).not.toThrow();
    expect(useSpy).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// TextArea counter — delegated document-level 'input' listener
// ---------------------------------------------------------------------------
describe('#470 Slice G — TextArea counter delegated listener', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('an input event on a data-wtm-counter textarea updates the matching counter span text (same "N/Max" format as wtmCounter.init)', () => {
    loadFreshFf({ use: jest.fn() }, undefined);

    const ta = document.createElement('textarea');
    ta.id = 'MyTextArea';
    ta.maxLength = 100;
    ta.setAttribute('data-wtm-counter', 'MyTextArea_counter');
    const counter = document.createElement('span');
    counter.id = 'MyTextArea_counter';
    counter.textContent = '0/100';
    document.body.appendChild(ta);
    document.body.appendChild(counter);

    ta.value = 'hello';
    ta.dispatchEvent(new Event('input', { bubbles: true }));

    expect(counter.textContent).toBe('5/100');
  });

  test('a textarea WITHOUT data-wtm-counter is ignored (no throw, no unrelated counter mutated)', () => {
    loadFreshFf({ use: jest.fn() }, undefined);

    const ta = document.createElement('textarea');
    ta.id = 'PlainTextArea';
    document.body.appendChild(ta);
    const counter = document.createElement('span');
    counter.id = 'SomeOtherCounter';
    counter.textContent = 'untouched';
    document.body.appendChild(counter);

    expect(() => {
      ta.value = 'abc';
      ta.dispatchEvent(new Event('input', { bubbles: true }));
    }).not.toThrow();
    expect(counter.textContent).toBe('untouched');
  });

  test('a stale data-wtm-counter pointing at a non-existent span is a no-op (no throw)', () => {
    loadFreshFf({ use: jest.fn() }, undefined);

    const ta = document.createElement('textarea');
    ta.id = 'OrphanTextArea';
    ta.maxLength = 50;
    ta.setAttribute('data-wtm-counter', 'DoesNotExist');
    document.body.appendChild(ta);

    expect(() => {
      ta.value = 'x';
      ta.dispatchEvent(new Event('input', { bubbles: true }));
    }).not.toThrow();
  });

  test('works for a textarea inserted into the DOM AFTER script load — no re-init/re-scan needed (the whole point of delegation)', () => {
    loadFreshFf({ use: jest.fn() }, undefined);

    // Simulates a dialog/SPA-tab fragment inserted well after page-ready.
    const ta = document.createElement('textarea');
    ta.id = 'LateTextArea';
    ta.maxLength = 20;
    ta.setAttribute('data-wtm-counter', 'LateTextArea_counter');
    const counter = document.createElement('span');
    counter.id = 'LateTextArea_counter';
    counter.textContent = '0/20';
    document.body.appendChild(ta);
    document.body.appendChild(counter);

    ta.value = 'abcdefghij';
    ta.dispatchEvent(new Event('input', { bubbles: true }));

    expect(counter.textContent).toBe('10/20');
  });
});
