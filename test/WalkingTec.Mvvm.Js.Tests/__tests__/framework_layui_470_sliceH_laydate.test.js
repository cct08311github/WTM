// Tests for Issue #470 Slice H: laydate ReadyFunc/ChangeFunc/DoneFunc +
// two-hidden-input range write-back island migration.
//
// Prior to this slice, DateTimeTagHelper's 'laydate' island (#556) was
// callback-free ONLY — any ReadyFunc/ChangeFunc/DoneFunc, or the
// two-hidden-input IsRange mode (which always needs its own built-in `done`
// split), forced the field back onto an inline <script>. This slice extends
// the island so a PLAIN-IDENTIFIER callback name (the only shape
// ff._resolveGuardedWindowFn can safely resolve by name) migrates too:
//
//   1. DispatchAction's 'laydate' case resolves action.readyFn/changeFn/
//      doneFn through the SAME #558/#601 guarded ff._resolveGuardedWindowFn
//      lookup bindSubmit/bindInput use, and wires them to
//      action.opts.ready/change/done with the exact same argument shape the
//      legacy inline <script> passed: ready(value,dateIns),
//      change(value,date,endDate,dateIns), done(value,date,endDate,dateIns).
//   2. When action.rangeStartId/rangeEndId are present (the two-hidden-input
//      IsRange path), a built-in `done` splits the picked value on
//      action.rangeSplitStr (default ' - ') and writes the two hidden
//      inputs — reproducing the legacy inline range <script>'s done: body
//      exactly — then chains any resolved doneFn AFTER the split.
//   3. A failed resolution (non-identifier / denylisted / missing / not a
//      function) silently skips JUST that one callback — never throws,
//      never eval()s.
//   4. framework_layui.js's active-code eval( count is unchanged (still
//      exactly 1).
//
// Following the same convention as framework_layui_601_bindinput_island
// .test.js: loads a FRESH instance of the real framework_layui.js into its
// own vm context (real jsdom `document`) and calls the REAL
// ff.DispatchAction directly, so these tests exercise the actual shipped
// code, not a hand-rolled reimplementation. The OpenDialog2 end-to-end test
// mirrors framework_layui_633_opendialog2_selector_panel.test.js's harness.

'use strict';

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
describe('#470 Slice H — source sweep', () => {
  function laydateBlock() {
    const block = active.match(/case\s+['"]laydate['"]:[\s\S]{0,3500}?\n\s*break;/);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('laydate case resolves readyFn/changeFn/doneFn through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = laydateBlock();
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.readyFn\s*\)/);
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFn\s*\)/);
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.doneFn\s*\)/);
  });

  test('laydate case resolves the range hidden inputs via document.getElementById', () => {
    const block = laydateBlock();
    expect(block).toMatch(/document\.getElementById\(\s*action\.rangeStartId\s*\)/);
    expect(block).toMatch(/document\.getElementById\(\s*action\.rangeEndId\s*\)/);
  });

  test('laydate case still calls layui.laydate.render(action.opts) — the opts object itself is mutated in place, never rebuilt', () => {
    const block = laydateBlock();
    expect(block).toMatch(/layui\.laydate\.render\(\s*action\.opts\s*\)/);
  });

  test('laydate case never contains eval( or new Function(', () => {
    const block = laydateBlock();
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });

  test('active-code eval( count is still exactly 1 after #470 Slice H changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Real ff.DispatchAction harness (mirrors #601's loadFreshFf)
// ---------------------------------------------------------------------------
const FAKE_DATE_INS = { __fake: 'dateIns' };

function loadFreshFf() {
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const laydateRender = jest.fn(() => FAKE_DATE_INS);
  const ctx = vm.createContext({
    window: {},
    document,
    layui: { laydate: { render: laydateRender } },
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
  return { ff: ctx.ff, windowObj: ctx, laydateRender };
}

function appendHidden(id) {
  const el = document.createElement('input');
  el.type = 'hidden';
  el.id = id;
  document.body.appendChild(el);
  return el;
}

afterEach(() => { document.body.innerHTML = ''; });

describe('#470 Slice H — real ff.DispatchAction laydate behavior', () => {
  // -------------------------------------------------------------------------
  // (a)/(b)/(c) resolved identifiers are wired with the correct argument shape
  // -------------------------------------------------------------------------
  test('(a) readyFn identifier: resolved window fn is called with (value, dateIns) on ready', () => {
    const { ff, windowObj, laydateRender } = loadFreshFf();
    const spy = jest.fn();
    windowObj.myReady = spy;

    ff.DispatchAction({
      actions: [{ type: 'laydate', opts: { elem: '#D1', type: 'date' }, readyFn: 'myReady' }],
    });

    expect(laydateRender).toHaveBeenCalledTimes(1);
    const opts = laydateRender.mock.calls[0][0];
    expect(typeof opts.ready).toBe('function');
    opts.ready('2024-01-01');
    expect(spy).toHaveBeenCalledWith('2024-01-01', FAKE_DATE_INS);
  });

  test('(b) changeFn identifier: resolved window fn is called with (value, date, endDate, dateIns) on change', () => {
    const { ff, windowObj, laydateRender } = loadFreshFf();
    const spy = jest.fn();
    windowObj.myChange = spy;

    ff.DispatchAction({
      actions: [{ type: 'laydate', opts: { elem: '#D2', type: 'date' }, changeFn: 'myChange' }],
    });

    const opts = laydateRender.mock.calls[0][0];
    expect(typeof opts.change).toBe('function');
    opts.change('2024-02-02', { year: 2024 }, { year: 2024 });
    expect(spy).toHaveBeenCalledWith('2024-02-02', { year: 2024 }, { year: 2024 }, FAKE_DATE_INS);
  });

  test('(c) doneFn identifier (non-range field): resolved window fn is called with (value, date, endDate, dateIns) on done', () => {
    const { ff, windowObj, laydateRender } = loadFreshFf();
    const spy = jest.fn();
    windowObj.myDone = spy;

    ff.DispatchAction({
      actions: [{ type: 'laydate', opts: { elem: '#D3', type: 'date' }, doneFn: 'myDone' }],
    });

    const opts = laydateRender.mock.calls[0][0];
    expect(typeof opts.done).toBe('function');
    opts.done('2024-03-03', { year: 2024 }, { year: 2024 });
    expect(spy).toHaveBeenCalledWith('2024-03-03', { year: 2024 }, { year: 2024 }, FAKE_DATE_INS);
  });

  test('all three callbacks can be wired independently on the same field', () => {
    const { ff, windowObj, laydateRender } = loadFreshFf();
    const readySpy = jest.fn();
    const changeSpy = jest.fn();
    const doneSpy = jest.fn();
    windowObj.r4 = readySpy;
    windowObj.c4 = changeSpy;
    windowObj.d4 = doneSpy;

    ff.DispatchAction({
      actions: [{
        type: 'laydate', opts: { elem: '#D4', type: 'date' },
        readyFn: 'r4', changeFn: 'c4', doneFn: 'd4'
      }],
    });

    const opts = laydateRender.mock.calls[0][0];
    opts.ready('v');
    opts.change('v', 'd', 'e');
    opts.done('v', 'd', 'e');
    expect(readySpy).toHaveBeenCalledWith('v', FAKE_DATE_INS);
    expect(changeSpy).toHaveBeenCalledWith('v', 'd', 'e', FAKE_DATE_INS);
    expect(doneSpy).toHaveBeenCalledWith('v', 'd', 'e', FAKE_DATE_INS);
  });

  // -------------------------------------------------------------------------
  // (d) adversarial names — gate is skipped, never throws, never eval
  // -------------------------------------------------------------------------
  describe('(d) adversarial readyFn/changeFn/doneFn names — gate is skipped, never throws', () => {
    test('dotted name "a.b" is rejected — no ready callback is even wired', () => {
      const { ff, windowObj, laydateRender } = loadFreshFf();
      const spy = jest.fn();
      windowObj.a = { b: spy };

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'laydate', opts: { elem: '#D5', type: 'date' }, readyFn: 'a.b' }],
        });
      }).not.toThrow();

      const opts = laydateRender.mock.calls[0][0];
      expect(opts.ready).toBeUndefined();
      expect(spy).not.toHaveBeenCalled();
    });

    test('denylisted global name "eval" is rejected even though own+callable+identifier', () => {
      const { ff, windowObj, laydateRender } = loadFreshFf();
      const spy = jest.fn();
      windowObj.eval = spy;

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'laydate', opts: { elem: '#D6', type: 'date' }, doneFn: 'eval' }],
        });
      }).not.toThrow();

      const opts = laydateRender.mock.calls[0][0];
      expect(opts.done).toBeUndefined();
      expect(spy).not.toHaveBeenCalled();
    });

    test('non-function value is rejected', () => {
      const { ff, windowObj, laydateRender } = loadFreshFf();
      windowObj.notAFunction = 'just a string';

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'laydate', opts: { elem: '#D7', type: 'date' }, changeFn: 'notAFunction' }],
        });
      }).not.toThrow();

      const opts = laydateRender.mock.calls[0][0];
      expect(opts.change).toBeUndefined();
    });

    test('missing/undefined callback names simply wire nothing — no throw', () => {
      const { ff, laydateRender } = loadFreshFf();
      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'laydate', opts: { elem: '#D8', type: 'date' } }] });
      }).not.toThrow();
      const opts = laydateRender.mock.calls[0][0];
      expect(opts.ready).toBeUndefined();
      expect(opts.change).toBeUndefined();
      expect(opts.done).toBeUndefined();
    });

    test('this file never causes a new eval( or new Function( call site to appear', () => {
      const matches = active.match(/\beval\(/g) || [];
      expect(matches).toHaveLength(1);
      expect(active).not.toMatch(/new\s+Function\s*\(/);
    });
  });

  // -------------------------------------------------------------------------
  // (e) range write-back — split on picked value, clear on empty value
  // -------------------------------------------------------------------------
  describe('(e) range write-back (built-in done split)', () => {
    test('picking "start - end" writes both hidden inputs', () => {
      const { ff, laydateRender } = loadFreshFf();
      const startEl = appendHidden('RStart1');
      const endEl = appendHidden('REnd1');

      ff.DispatchAction({
        actions: [{
          type: 'laydate',
          opts: { elem: '#D9', type: 'date', range: true },
          rangeStartId: 'RStart1',
          rangeEndId: 'REnd1',
          rangeSplitStr: ' - '
        }],
      });

      const opts = laydateRender.mock.calls[0][0];
      opts.done('2024-01-01 - 2024-01-05');
      expect(startEl.value).toBe('2024-01-01');
      expect(endEl.value).toBe('2024-01-05');
    });

    test('clearing (empty picked value) writes empty to both hidden inputs', () => {
      const { ff, laydateRender } = loadFreshFf();
      const startEl = appendHidden('RStart2');
      const endEl = appendHidden('REnd2');
      startEl.value = 'stale-start';
      endEl.value = 'stale-end';

      ff.DispatchAction({
        actions: [{
          type: 'laydate',
          opts: { elem: '#D10', type: 'date', range: true },
          rangeStartId: 'RStart2',
          rangeEndId: 'REnd2',
          rangeSplitStr: ' - '
        }],
      });

      const opts = laydateRender.mock.calls[0][0];
      opts.done('');
      expect(startEl.value).toBe('');
      expect(endEl.value).toBe('');
    });

    test('missing rangeSplitStr defaults to the legacy hardcoded " - " delimiter', () => {
      const { ff, laydateRender } = loadFreshFf();
      const startEl = appendHidden('RStart3');
      const endEl = appendHidden('REnd3');

      ff.DispatchAction({
        actions: [{
          type: 'laydate',
          opts: { elem: '#D11', type: 'date', range: true },
          rangeStartId: 'RStart3',
          rangeEndId: 'REnd3'
          // rangeSplitStr intentionally omitted.
        }],
      });

      const opts = laydateRender.mock.calls[0][0];
      opts.done('2024-06-01 - 2024-06-10');
      expect(startEl.value).toBe('2024-06-01');
      expect(endEl.value).toBe('2024-06-10');
    });

    // -----------------------------------------------------------------------
    // (f) range + doneFn: split runs FIRST, then the caller's doneFn runs
    // -----------------------------------------------------------------------
    test('(f) range + doneFn: the built-in split runs, THEN the caller doneFn runs, both from one done() call', () => {
      const { ff, windowObj, laydateRender } = loadFreshFf();
      const startEl = appendHidden('RStart4');
      const endEl = appendHidden('REnd4');
      const callOrder = [];
      windowObj.myRangeDone = jest.fn((value, date, endDate, dateIns) => {
        callOrder.push('doneFn:' + startEl.value + '|' + endEl.value);
        expect(dateIns).toBe(FAKE_DATE_INS);
      });

      ff.DispatchAction({
        actions: [{
          type: 'laydate',
          opts: { elem: '#D12', type: 'date', range: true },
          rangeStartId: 'RStart4',
          rangeEndId: 'REnd4',
          rangeSplitStr: ' - ',
          doneFn: 'myRangeDone'
        }],
      });

      const opts = laydateRender.mock.calls[0][0];
      opts.done('2024-07-01 - 2024-07-15', { y: 2024 }, { y: 2024 });

      // The hidden inputs must already be split BEFORE the caller's doneFn
      // observes them — proving split-then-chain ordering, not the reverse.
      expect(startEl.value).toBe('2024-07-01');
      expect(endEl.value).toBe('2024-07-15');
      expect(windowObj.myRangeDone).toHaveBeenCalledWith(
        '2024-07-01 - 2024-07-15', { y: 2024 }, { y: 2024 }, FAKE_DATE_INS
      );
      expect(callOrder).toEqual(['doneFn:2024-07-01|2024-07-15']);
    });

    test('range with no doneFn: split still runs, no throw for the missing chain', () => {
      const { ff, laydateRender } = loadFreshFf();
      const startEl = appendHidden('RStart5');
      const endEl = appendHidden('REnd5');

      ff.DispatchAction({
        actions: [{
          type: 'laydate',
          opts: { elem: '#D13', type: 'date', range: true },
          rangeStartId: 'RStart5',
          rangeEndId: 'REnd5',
          rangeSplitStr: ' - '
        }],
      });

      const opts = laydateRender.mock.calls[0][0];
      expect(() => opts.done('2024-08-01 - 2024-08-02')).not.toThrow();
      expect(startEl.value).toBe('2024-08-01');
      expect(endEl.value).toBe('2024-08-02');
    });
  });

  // -------------------------------------------------------------------------
  // Back-compat / no-op guards
  // -------------------------------------------------------------------------
  test('no-ops without throwing when opts.elem is missing (readyFn present)', () => {
    const { ff, laydateRender } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'laydate', opts: { type: 'date' }, readyFn: 'anything' }] });
    }).not.toThrow();
    expect(laydateRender).not.toHaveBeenCalled();
  });

  test('a bare laydate island with no readyFn/changeFn/doneFn/range fields behaves exactly as the pre-Slice-H #556 case', () => {
    const { ff, laydateRender } = loadFreshFf();
    ff.DispatchAction({ actions: [{ type: 'laydate', opts: { elem: '#D14', type: 'date', format: 'yyyy-MM-dd' } }] });
    expect(laydateRender).toHaveBeenCalledTimes(1);
    const opts = laydateRender.mock.calls[0][0];
    expect(opts.elem).toBe('#D14');
    expect(opts.format).toBe('yyyy-MM-dd');
    expect(opts.ready).toBeUndefined();
    expect(opts.change).toBeUndefined();
    expect(opts.done).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// (g) dispatch works via the OpenDialog2 (selector search-panel) path too —
// mirrors framework_layui_633_opendialog2_selector_panel.test.js's harness,
// proving the SAME generic wtm-dialog-init rehydration/dispatch mechanism
// (#635) carries the new readyFn/changeFn/doneFn/range island fields.
// ---------------------------------------------------------------------------
describe('#470 Slice H — dispatch reaches layui.laydate.render via ff.OpenDialog2 (selector path)', () => {
  // Mirrors framework_layui_633_opendialog2_selector_panel.test.js's
  // makeJQueryMock wrap() helper: OpenDialog2 reads the #Temp{id} search-panel
  // template via `$(tempId)[0].innerHTML` and gates on `$(tempId).length > 0`,
  // so the mock must actually resolve the selector to a real DOM element
  // (not a stub) for the search-panel substitution branch to run at all.
  function makeJQueryMock() {
    function wrap(el) {
      return { 0: el, length: el ? 1 : 0 };
    }
    const $ = function (selector) {
      if (typeof selector === 'string' && selector.charAt(0) === '#') {
        return wrap(document.getElementById(selector.slice(1)));
      }
      return wrap(null);
    };
    $.ajax = jest.fn((opts) => {
      const request = { getResponseHeader: () => null };
      opts.success(OPEN_DIALOG2_RESPONSE_HTML, 'success', request);
    });
    $.cookie = jest.fn();
    $.fn = {};
    return $;
  }

  function loadFreshFfForOpenDialog2(layui, $) {
    const ctx = vm.createContext({
      window: {},
      document,
      layui: layui,
      console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: $,
      DOMParser: global.DOMParser,
      DOMPurify: { sanitize: (html) => (html === undefined || html === null ? '' : html) },
      DONOTUSE_TABLAYID: undefined,
      DONOTUSE_COOKIEPRE: '',
      DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    return { ff: ctx.ff, windowObj: ctx };
  }

  function makeOpenDialog2LayuiWithDomInsertion(laydateRender) {
    return {
      use: jest.fn((mods, cb) => cb()),
      form: { render: jest.fn() },
      laydate: { render: laydateRender },
      layer: {
        load: jest.fn(() => 1),
        close: jest.fn(),
        alert: jest.fn(),
        full: jest.fn(),
        open: jest.fn((opts) => {
          const container = document.createElement('div');
          // Safety note (mirrors #633's identical mock): opts.content here is
          // ALWAYS a string built by this file's own fixtures below — never
          // external/attacker-controlled input.
          container.innerHTML = opts.content;
          document.body.appendChild(container);
          if (typeof opts.success === 'function') { opts.success(container); }
          return 'mockWinId';
        })
      }
    };
  }

  function makeOpenDialog2TempEl(id, innerHtml) {
    const el = document.createElement('div');
    el.id = id;
    el.innerHTML = innerHtml;
    document.body.appendChild(el);
    return el;
  }

  const OPEN_DIALOG2_RESPONSE_HTML =
    '<div>wtVar_x = table.render(xoption);</div>' +
    '<table id="g1" lay-filter="f1"></table>' +
    '$$SearchPanel$$';

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
  });

  test('a laydate island with a readyFn, tokenized into the search panel, reaches layui.laydate.render and wires readyFn end-to-end', () => {
    const laydateRender = jest.fn(() => FAKE_DATE_INS);
    const layui = makeOpenDialog2LayuiWithDomInsertion(laydateRender);
    const $ = makeJQueryMock();
    const payload = {
      type: 'laydate',
      opts: { elem: '#SelectorDate470', type: 'date' },
      readyFn: 'selectorReady470'
    };
    makeOpenDialog2TempEl(
      'Temp470h',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$<div>panel</div>'
    );
    const { ff, windowObj } = loadFreshFfForOpenDialog2(layui, $);
    const readySpy = jest.fn();
    windowObj.selectorReady470 = readySpy;

    ff.OpenDialog2('/some/search/url', 'w470h', 'Title', 500, 400, '#Temp470h');

    expect(laydateRender).toHaveBeenCalledTimes(1);
    const opts = laydateRender.mock.calls[0][0];
    expect(opts.elem).toBe('#SelectorDate470');
    expect(typeof opts.ready).toBe('function');
    opts.ready('2024-09-09');
    expect(readySpy).toHaveBeenCalledWith('2024-09-09', FAKE_DATE_INS);
  });
});
