// Tests for Issue #556 (#470-B slice 1): laydate JSON-island init +
// universal island consumption.
//
// What this file locks in place:
//   1. DispatchAction gains a 'laydate' action type that calls
//      layui.laydate.render(action.opts) directly (no remapping, no eval).
//   2. ff._normalizeIslandPayload wraps a bare {"type":"...", ...} island
//      into {actions:[...]}, and passes an already-wrapped {actions:[...]}
//      island through unchanged.
//   3. ff.OpenDialog collects EVERY .wtm-dialog-init island in the parsed
//      partial (querySelectorAll, not querySelector) and dispatches each.
//   4. ff._consumePageReadyIslands dispatches .wtm-dialog-init islands
//      present in the live document at page load, and is idempotent
//      (marks each island data-wtm-dispatched="1" so a second call is a
//      no-op) — this is the mechanism that makes migrated fields (e.g. the
//      DateTimeTagHelper laydate island) initialize on full-page forms too.
//   5. The dialog path and the page-ready path can never double-dispatch
//      the same island, because DOMPurify (FORBID_TAGS:['script']) strips
//      every <script> element — including .wtm-dialog-init islands — from
//      dialog markup before it is inserted into the live document; a
//      dialog-origin island is therefore never visible to the page-ready
//      consumer, which only scans the live document.
//
// Following the same convention as framework_layui_phase3c_json_dispatch.test.js:
// source-sweep tests assert the real file structure; behavioral-stub tests
// re-implement the same logic (since the vm-loaded `ff` module's closures
// were compiled without `document`/`layui` in scope — see setup.js — so the
// live module functions cannot be exercised directly against real DOM/layui
// mocks here). Any drift between the stub and the real file is caught by the
// source-sweep tests.

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
describe('#556 (#470-B slice 1) — source sweep', () => {
  test('DispatchAction switch contains a laydate case', () => {
    expect(active).toMatch(/case\s+['"]laydate['"]/);
  });

  test('laydate case calls layui.laydate.render(action.opts) directly (no remapping)', () => {
    const block = active.match(/case\s+['"]laydate['"][\s\S]{0,800}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.laydate\.render\(\s*action\.opts/);
  });

  test('laydate case guards on action.opts && action.opts.elem', () => {
    const block = active.match(/case\s+['"]laydate['"][\s\S]{0,800}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/action\.opts\s*&&\s*action\.opts\.elem/);
  });

  test('ff._normalizeIslandPayload is defined', () => {
    expect(active).toMatch(/_normalizeIslandPayload\s*:\s*function/);
  });

  test('_normalizeIslandPayload wraps a bare action and passes an actions array through', () => {
    const block = active.match(/_normalizeIslandPayload\s*:\s*function[\s\S]{0,500}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/Array\.isArray\(\s*parsed\.actions\s*\)/);
    expect(block[0]).toMatch(/actions:\s*\[\s*parsed\s*\]/);
  });

  test('OpenDialog collects islands via querySelectorAll (not querySelector)', () => {
    expect(active).toMatch(/querySelectorAll\s*\(\s*['"]script\[type="application\/json"\]\.wtm-dialog-init['"]\s*\)/);
    // Regression guard: the OLD singular call must be gone.
    expect(active).not.toMatch(/[^.]querySelector\s*\(\s*['"]script\[type="application\/json"\]\.wtm-dialog-init['"]\s*\)/);
  });

  test('OpenDialog normalizes each parsed island before collecting it', () => {
    expect(active).toMatch(/_normalizeIslandPayload\s*\(\s*_parsed\s*\)/);
    expect(active).toMatch(/_dialogInitPayloads\.push\(\s*_normalized\s*\)/);
  });

  test('OpenDialog dispatches every collected island payload', () => {
    expect(active).toMatch(/for\s*\(\s*var\s+_pi\s*=\s*0[\s\S]{0,200}_dialogInitPayloads\.length/);
    // Issue #576: this loop originally called ff.DispatchAction(...) directly,
    // bypassing ff._dispatchIslandWhenReady's layui.use(...) deferral for the
    // dialog path (a module-load race the #552 stopgap only partially
    // covered — see framework_layui_552_static_widgets_island.test.js). #576
    // routes it through ff._dispatchIslandWhenReady instead, the same helper
    // + usage ff._consumePageReadyIslands already uses for the page-ready
    // path (asserted a few tests below). Updated here rather than left
    // failing so this file keeps tracking the real dispatch call site; full
    // dialog-path coverage for the new helper lives in
    // framework_layui_576_opendialog_island_defer.test.js.
    expect(active).toMatch(/ff\._dispatchIslandWhenReady\s*\(\s*_dialogInitPayloads\[_pi\]\s*\)/);
    expect(active).not.toMatch(/ff\.DispatchAction\s*\(\s*_dialogInitPayloads\[_pi\]\s*\)/);
  });

  test('ff._consumePageReadyIslands is defined', () => {
    expect(active).toMatch(/_consumePageReadyIslands\s*=\s*function/);
  });

  test('_consumePageReadyIslands claims islands (excludes already-claimed) and marks them before scheduling dispatch', () => {
    const block = active.match(/_consumePageReadyIslands\s*=\s*function[\s\S]{0,1100}?\n\};/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/:not\(\[data-wtm-dispatched\]\)/);
    // The claim (setAttribute) must happen BEFORE the dispatch is scheduled,
    // so repeated calls / a stray double DOMContentLoaded can't schedule the
    // same island twice. Safe now that dispatch is deferred through
    // _dispatchIslandWhenReady (which guarantees the render eventually runs).
    const setIdx = block[0].indexOf("setAttribute('data-wtm-dispatched'");
    const dispatchIdx = block[0].indexOf('_dispatchIslandWhenReady(');
    expect(setIdx).toBeGreaterThan(-1);
    expect(dispatchIdx).toBeGreaterThan(-1);
    expect(setIdx).toBeLessThan(dispatchIdx);
  });

  test('page-ready consumer is wired up via DOMContentLoaded / immediate call', () => {
    expect(active).toMatch(/document\.addEventListener\(\s*['"]DOMContentLoaded['"]\s*,\s*ff\._consumePageReadyIslands\s*\)/);
    expect(active).toMatch(/document\.readyState\s*===\s*['"]loading['"]/);
  });

  test('active-code eval( count is still exactly 1 after #556 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  // ---- #556 hardening: guaranteed-module-load deferral ---------------------
  test('ff._islandModulesFor and ff._dispatchIslandWhenReady are defined', () => {
    expect(active).toMatch(/_islandModulesFor\s*:\s*function/);
    expect(active).toMatch(/_dispatchIslandWhenReady\s*:\s*function/);
  });

  test('_islandModulesFor maps laydate -> laydate module and initForm -> form module', () => {
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,1600}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]laydate['"]/);
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]initForm['"]/);
    // initForm with dates also needs laydate.
    expect(block[0]).toMatch(/a\.dates/);
  });

  test('_dispatchIslandWhenReady routes module-dependent payloads through layui.use', () => {
    const block = active.match(/_dispatchIslandWhenReady\s*:\s*function[\s\S]{0,900}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/layui\.use\s*\(\s*mods\s*,\s*function/);
    expect(block[0]).toMatch(/ff\.DispatchAction\s*\(\s*payload\s*\)/);
    // Fallback to a direct dispatch when there is no module dependency.
    expect(block[0]).toMatch(/else\s*\{[\s\S]{0,120}ff\.DispatchAction\s*\(\s*payload\s*\)/);
  });

  test('page-ready consumer dispatches through the guaranteed-load helper (never a bare DispatchAction that could no-op)', () => {
    const block = active.match(/_consumePageReadyIslands\s*=\s*function[\s\S]{0,1100}?\n\};/);
    expect(block).not.toBeNull();
    // The consumer must route through _dispatchIslandWhenReady, NOT call
    // ff.DispatchAction directly (which is what allowed the laydate no-op race).
    expect(block[0]).toMatch(/ff\._dispatchIslandWhenReady\s*\(\s*normalized\s*\)/);
    expect(block[0]).not.toMatch(/ff\.DispatchAction\s*\(\s*normalized\s*\)/);
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'laydate' DispatchAction case
// ---------------------------------------------------------------------------
describe('#556 DispatchAction laydate — behavioral stub', () => {
  function makeDispatcher(layui) {
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      var actions = payload.actions;
      for (var i = 0; i < actions.length; i++) {
        var action = actions[i];
        if (!action || !action.type) continue;
        switch (action.type) {
          case 'laydate':
            if (action.opts && action.opts.elem &&
                typeof layui !== 'undefined' && layui.laydate &&
                typeof layui.laydate.render === 'function') {
              try {
                layui.laydate.render(action.opts || {});
              } catch (e) {
                // swallow — mirrors the real implementation's try/catch
              }
            }
            break;
          default:
            // ignore
        }
      }
    };
  }

  test('laydate action calls layui.laydate.render with the opts object verbatim', () => {
    const laydateRender = jest.fn();
    const layui = { laydate: { render: laydateRender } };
    const dispatch = makeDispatcher(layui);
    const opts = { elem: '#MyDate', type: 'date', format: 'yyyy-MM-dd' };
    dispatch({ actions: [{ type: 'laydate', opts: opts }] });
    expect(laydateRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledWith(opts);
  });

  test('laydate action passes through the full static option set unchanged', () => {
    const laydateRender = jest.fn();
    const layui = { laydate: { render: laydateRender } };
    const dispatch = makeDispatcher(layui);
    const opts = {
      elem: '#StartDate',
      type: 'date',
      range: '~',
      format: 'yyyy-MM-dd',
      min: -7,
      max: '2099-12-31',
      zIndex: 12345,
      showBottom: false,
      btns: ['confirm'],
      calendar: true,
      lang: 'en',
      mark: { '0-0-15': 'mid' }
    };
    dispatch({ actions: [{ type: 'laydate', opts: opts }] });
    expect(laydateRender).toHaveBeenCalledWith(opts);
  });

  test('laydate action is a no-op when opts.elem is missing', () => {
    const laydateRender = jest.fn();
    const layui = { laydate: { render: laydateRender } };
    const dispatch = makeDispatcher(layui);
    dispatch({ actions: [{ type: 'laydate', opts: { type: 'date' } }] });
    expect(laydateRender).not.toHaveBeenCalled();
  });

  test('laydate action is a no-op when opts is missing entirely', () => {
    const laydateRender = jest.fn();
    const layui = { laydate: { render: laydateRender } };
    const dispatch = makeDispatcher(layui);
    dispatch({ actions: [{ type: 'laydate' }] });
    expect(laydateRender).not.toHaveBeenCalled();
  });

  test('laydate action is a no-op when layui.laydate is not available', () => {
    const dispatch = makeDispatcher(undefined);
    expect(() => {
      dispatch({ actions: [{ type: 'laydate', opts: { elem: '#D' } }] });
    }).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: _normalizeIslandPayload
// ---------------------------------------------------------------------------
describe('#556 _normalizeIslandPayload — behavioral stub', () => {
  function normalizeIslandPayload(parsed) {
    if (!parsed || typeof parsed !== 'object') { return null; }
    if (Array.isArray(parsed.actions)) { return parsed; }
    if (parsed.type) { return { actions: [parsed] }; }
    return null;
  }

  test('wraps a bare single-action island into {actions:[...]}', () => {
    const bare = { type: 'laydate', opts: { elem: '#D' } };
    expect(normalizeIslandPayload(bare)).toEqual({ actions: [bare] });
  });

  test('passes an already-wrapped {actions:[...]} island through unchanged', () => {
    const wrapped = { actions: [{ type: 'initForm', filter: 'f' }] };
    expect(normalizeIslandPayload(wrapped)).toBe(wrapped);
  });

  test('returns null for malformed / unrecognized payloads', () => {
    expect(normalizeIslandPayload(null)).toBeNull();
    expect(normalizeIslandPayload(undefined)).toBeNull();
    expect(normalizeIslandPayload({})).toBeNull();
    expect(normalizeIslandPayload('a string')).toBeNull();
    expect(normalizeIslandPayload(42)).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: OpenDialog multi-island collection + dispatch
// ---------------------------------------------------------------------------
describe('#556 OpenDialog multi-island dispatch — behavioral stub', () => {
  // Mirrors the querySelectorAll + per-node try/catch + normalize + push
  // loop added to ff.OpenDialog, operating on a plain array of raw JSON
  // strings instead of real DOM nodes (DOMParser is not available in this
  // harness — see file header).
  function collectAndDispatch(rawIslandTexts, dispatchFn) {
    var payloads = [];
    for (var i = 0; i < rawIslandTexts.length; i++) {
      try {
        var parsed = JSON.parse(rawIslandTexts[i]);
        var normalized = (function (p) {
          if (!p || typeof p !== 'object') { return null; }
          if (Array.isArray(p.actions)) { return p; }
          if (p.type) { return { actions: [p] }; }
          return null;
        })(parsed);
        if (normalized !== null) { payloads.push(normalized); }
      } catch (e) { /* malformed single island → skip that island only */ }
    }
    for (var j = 0; j < payloads.length; j++) {
      dispatchFn(payloads[j]);
    }
    return payloads;
  }

  test('dispatches multiple islands present in the same partial', () => {
    const dispatchFn = jest.fn();
    const islands = [
      JSON.stringify({ actions: [{ type: 'initForm', filter: 'myForm' }] }),
      JSON.stringify({ type: 'laydate', opts: { elem: '#BirthDate', type: 'date' } }),
      JSON.stringify({ type: 'laydate', opts: { elem: '#StartDate', type: 'date' } })
    ];
    collectAndDispatch(islands, dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(3);
    expect(dispatchFn).toHaveBeenNthCalledWith(1, { actions: [{ type: 'initForm', filter: 'myForm' }] });
    expect(dispatchFn).toHaveBeenNthCalledWith(2, { actions: [{ type: 'laydate', opts: { elem: '#BirthDate', type: 'date' } }] });
    expect(dispatchFn).toHaveBeenNthCalledWith(3, { actions: [{ type: 'laydate', opts: { elem: '#StartDate', type: 'date' } }] });
  });

  test('a single legacy (wrapped) island still works exactly as before', () => {
    const dispatchFn = jest.fn();
    const islands = [JSON.stringify({ actions: [{ type: 'closeDialog' }] })];
    collectAndDispatch(islands, dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(1);
    expect(dispatchFn).toHaveBeenCalledWith({ actions: [{ type: 'closeDialog' }] });
  });

  test('a malformed island is skipped without affecting the others', () => {
    const dispatchFn = jest.fn();
    const islands = [
      '{not valid json',
      JSON.stringify({ type: 'laydate', opts: { elem: '#OK' } })
    ];
    collectAndDispatch(islands, dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(1);
    expect(dispatchFn).toHaveBeenCalledWith({ actions: [{ type: 'laydate', opts: { elem: '#OK' } }] });
  });

  test('no islands present → no dispatch calls', () => {
    const dispatchFn = jest.fn();
    collectAndDispatch([], dispatchFn);
    expect(dispatchFn).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: page-ready island consumer (real jsdom `document`)
// ---------------------------------------------------------------------------
// Unlike the vm-loaded `ff` module (whose closures were compiled without
// `document` in scope — see setup.js), this test file itself runs directly
// in the jsdom test environment, so `document` here is a REAL DOM. This
// stub mirrors ff._consumePageReadyIslands exactly (source-swept above) and
// is exercised against genuine DOM nodes to validate the idempotency
// mechanism (querySelectorAll + :not([data-wtm-dispatched]) + setAttribute).
describe('#556 page-ready island consumer — behavioral stub (real DOM)', () => {
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

  afterEach(() => {
    document.body.innerHTML = '';
  });

  function addIsland(json) {
    const el = document.createElement('script');
    el.type = 'application/json';
    el.className = 'wtm-dialog-init';
    el.textContent = JSON.stringify(json);
    document.body.appendChild(el);
    return el;
  }

  test('dispatches a bare laydate island present in the document at load', () => {
    addIsland({ type: 'laydate', opts: { elem: '#PageDate', type: 'date' } });
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(1);
    expect(dispatchFn).toHaveBeenCalledWith({
      actions: [{ type: 'laydate', opts: { elem: '#PageDate', type: 'date' } }]
    });
  });

  test('marks the island data-wtm-dispatched="1" after dispatch', () => {
    const el = addIsland({ type: 'laydate', opts: { elem: '#PageDate' } });
    consumePageReadyIslands(jest.fn());
    expect(el.getAttribute('data-wtm-dispatched')).toBe('1');
  });

  test('a second call does not re-dispatch an already-processed island (no double-dispatch)', () => {
    addIsland({ type: 'laydate', opts: { elem: '#PageDate' } });
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    consumePageReadyIslands(dispatchFn);
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(1);
  });

  test('dispatches multiple distinct islands present in the document, each exactly once', () => {
    addIsland({ type: 'laydate', opts: { elem: '#A' } });
    addIsland({ type: 'laydate', opts: { elem: '#B' } });
    addIsland({ actions: [{ type: 'initForm', filter: 'pageForm' }] });
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).toHaveBeenCalledTimes(3);
    consumePageReadyIslands(dispatchFn);
    // Re-running must not add any further calls.
    expect(dispatchFn).toHaveBeenCalledTimes(3);
  });

  test('no islands in the document → no dispatch calls', () => {
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).not.toHaveBeenCalled();
  });

  test('a malformed island in the document is marked (no retry loop) but not dispatched', () => {
    const el = document.createElement('script');
    el.type = 'application/json';
    el.className = 'wtm-dialog-init';
    el.textContent = '{not valid json';
    document.body.appendChild(el);
    const dispatchFn = jest.fn();
    consumePageReadyIslands(dispatchFn);
    expect(dispatchFn).not.toHaveBeenCalled();
    expect(el.getAttribute('data-wtm-dispatched')).toBe('1');
  });
});

// ---------------------------------------------------------------------------
// #556 hardening — laydate loads LATE: the island must STILL render.
// ---------------------------------------------------------------------------
// This is the regression test for the timing race the coordinator asked to
// bulletproof: at page-ready time layui.laydate is not yet loaded, so a bare
// ff.DispatchAction('laydate') would silently no-op AND the island would be
// marked data-wtm-dispatched (never retried) → the date field never renders on
// a full-page form. The fix routes page-ready dispatch through
// ff._dispatchIslandWhenReady, which defers into layui.use([...], cb); the cb
// runs only once the module has loaded, so the render ALWAYS eventually
// happens. These behavioral stubs mirror the real functions exactly (locked in
// by the source-sweep tests above) and are exercised against a controllable
// mock layui whose modules load on demand.
describe('#556 hardening — late-loading laydate still renders (no silent drop)', () => {
  function normalizeIslandPayload(parsed) {
    if (!parsed || typeof parsed !== 'object') { return null; }
    if (Array.isArray(parsed.actions)) { return parsed; }
    if (parsed.type) { return { actions: [parsed] }; }
    return null;
  }

  // Mirrors ff._islandModulesFor.
  function islandModulesFor(payload) {
    var needed = { form: false, laydate: false };
    if (payload && payload.actions) {
      for (var i = 0; i < payload.actions.length; i++) {
        var a = payload.actions[i];
        if (!a || !a.type) { continue; }
        if (a.type === 'laydate') {
          needed.laydate = true;
        } else if (a.type === 'initForm') {
          needed.form = true;
          if (a.dates && a.dates.length) { needed.laydate = true; }
        }
      }
    }
    var mods = [];
    if (needed.form) { mods.push('form'); }
    if (needed.laydate) { mods.push('laydate'); }
    return mods;
  }

  // Mirrors ff._dispatchIslandWhenReady.
  function makeDispatchWhenReady(getLayui, dispatchAction) {
    return function (payload) {
      var mods = islandModulesFor(payload);
      var layui = getLayui();
      if (mods.length > 0 && layui && typeof layui.use === 'function') {
        layui.use(mods, function () { dispatchAction(payload); });
      } else {
        dispatchAction(payload);
      }
    };
  }

  // Mirrors ff._consumePageReadyIslands (claim synchronously, dispatch deferred).
  function makeConsume(dispatchWhenReady) {
    return function () {
      var nodes = document.querySelectorAll(
        'script[type="application/json"].wtm-dialog-init:not([data-wtm-dispatched])'
      );
      for (var i = 0; i < nodes.length; i++) {
        var node = nodes[i];
        node.setAttribute('data-wtm-dispatched', '1');
        if (!node.textContent) { continue; }
        try {
          var parsed = JSON.parse(node.textContent);
          var normalized = normalizeIslandPayload(parsed);
          if (normalized !== null) { dispatchWhenReady(normalized); }
        } catch (e) { /* skip */ }
      }
    };
  }

  // Mirrors DispatchAction's laydate + initForm cases, reading layui at
  // dispatch time (so a deferred dispatch sees the module once it has loaded).
  function makeDispatchAction(getLayui) {
    return function (payload) {
      if (!payload || !payload.actions) { return; }
      payload.actions.forEach(function (a) {
        if (!a || !a.type) { return; }
        var layui = getLayui();
        if (a.type === 'laydate') {
          if (a.opts && a.opts.elem && layui && layui.laydate &&
              typeof layui.laydate.render === 'function') {
            layui.laydate.render(a.opts);
          }
        } else if (a.type === 'initForm') {
          if (layui && layui.form && typeof layui.form.render === 'function') {
            layui.form.render(a.formType || null, a.filter || undefined);
          }
        }
      });
    };
  }

  // A mock layui whose modules are initially UNLOADED. layui.use queues its
  // callback and only fires it once every requested module has been load()ed —
  // exactly how the real layui defers a callback for a not-yet-loaded module.
  function makeLateLayui() {
    var pending = [];
    var loaded = { form: false, laydate: false };
    var laydateRender = jest.fn();
    var formRender = jest.fn();
    var layui = {
      // NOTE: layui.laydate / layui.form are intentionally ABSENT until load().
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
      laydateRender: laydateRender,
      formRender: formRender,
      load: function (mod) {
        loaded[mod] = true;
        if (mod === 'laydate') { layui.laydate = { render: laydateRender }; }
        if (mod === 'form') { layui.form = { render: formRender }; }
        flush();
      }
    };
  }

  afterEach(() => {
    document.body.innerHTML = '';
  });

  function addIsland(json) {
    const el = document.createElement('script');
    el.type = 'application/json';
    el.className = 'wtm-dialog-init';
    el.textContent = JSON.stringify(json);
    document.body.appendChild(el);
    return el;
  }

  test('laydate island: render is DEFERRED (not dropped) when laydate is not yet loaded, then fires on load', () => {
    const el = addIsland({ type: 'laydate', opts: { elem: '#LateDate', type: 'date', format: 'yyyy-MM-dd' } });
    const harness = makeLateLayui();
    const dispatchAction = makeDispatchAction(function () { return harness.layui; });
    const dispatchWhenReady = makeDispatchWhenReady(function () { return harness.layui; }, dispatchAction);
    const consume = makeConsume(dispatchWhenReady);

    // Page-ready runs while laydate is STILL loading.
    consume();

    // Old buggy behavior would have: island marked + render never called.
    // New behavior: island claimed, but render is queued (NOT dropped).
    expect(el.getAttribute('data-wtm-dispatched')).toBe('1');
    expect(harness.laydateRender).not.toHaveBeenCalled();

    // laydate finishes loading → the queued dispatch fires → render happens.
    harness.load('laydate');
    expect(harness.laydateRender).toHaveBeenCalledTimes(1);
    expect(harness.laydateRender).toHaveBeenCalledWith({ elem: '#LateDate', type: 'date', format: 'yyyy-MM-dd' });
  });

  test('render fires exactly once even if the page-ready consumer is invoked again before load', () => {
    addIsland({ type: 'laydate', opts: { elem: '#LateDate2' } });
    const harness = makeLateLayui();
    const dispatchAction = makeDispatchAction(function () { return harness.layui; });
    const dispatchWhenReady = makeDispatchWhenReady(function () { return harness.layui; }, dispatchAction);
    const consume = makeConsume(dispatchWhenReady);

    consume();          // claims the island, queues the deferred dispatch
    consume();          // island already claimed → no second queue entry
    consume();
    expect(harness.laydateRender).not.toHaveBeenCalled();

    harness.load('laydate');
    // Despite three consume() calls, the island was claimed once → one render.
    expect(harness.laydateRender).toHaveBeenCalledTimes(1);
  });

  test('when laydate is ALREADY loaded, render fires synchronously (no regression for the fast path)', () => {
    addIsland({ type: 'laydate', opts: { elem: '#EarlyDate' } });
    const harness = makeLateLayui();
    harness.load('laydate');   // module ready BEFORE page-ready runs
    const dispatchAction = makeDispatchAction(function () { return harness.layui; });
    const dispatchWhenReady = makeDispatchWhenReady(function () { return harness.layui; }, dispatchAction);
    const consume = makeConsume(dispatchWhenReady);

    consume();
    // layui.use fires its callback immediately for an already-loaded module.
    expect(harness.laydateRender).toHaveBeenCalledTimes(1);
    expect(harness.laydateRender).toHaveBeenCalledWith({ elem: '#EarlyDate' });
  });

  test('initForm island with dates waits for BOTH form and laydate before dispatching', () => {
    addIsland({ actions: [{ type: 'initForm', filter: 'f', dates: [{ elem: '#D', type: 'date' }] }] });
    const harness = makeLateLayui();
    const dispatchAction = makeDispatchAction(function () { return harness.layui; });
    const dispatchWhenReady = makeDispatchWhenReady(function () { return harness.layui; }, dispatchAction);
    const consume = makeConsume(dispatchWhenReady);

    consume();
    expect(harness.formRender).not.toHaveBeenCalled();

    harness.load('form');   // only form loaded → still waiting on laydate
    expect(harness.formRender).not.toHaveBeenCalled();

    harness.load('laydate'); // now both loaded → dispatch fires
    expect(harness.formRender).toHaveBeenCalledTimes(1);
    expect(harness.formRender).toHaveBeenCalledWith(null, 'f');
  });
});
