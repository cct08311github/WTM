// Tests for Issue #635 (#470 prerequisite): ff.OpenDialog2 (the <wt:selector>
// search-panel dialog opener) gains island machinery. Before this change,
// OpenDialog2 had NO _collectInitFromHtml/_dialogInitPayloads/
// _dispatchIslandWhenReady wiring at all — a widget TagHelper nested inside
// a <wt:searchpanel> (e.g. a callback-free <wt:datetime> field) that emits a
// wtm-dialog-init JSON island would round-trip (in legacy/#627-kill-switch-OFF
// mode) into an inert, never-dispatched <script type="application/json"> tag
// sitting in the opened dialog's DOM — silently dead. Worse, with the #627
// kill-switch ON, the island's own closing tag (tokenized to $$#script$$ by
// the PRE-#635 SelectorTagHelper.cs, which only escaped bare <script>/
// </script>) had no matching $$script$$ open token, so the "strip
// $$script$$...$$#script$$ segments" regex could never match it — leaving an
// UNCLOSED <script> in the composed dialog HTML that silently swallowed
// everything rendered after it.
//
// The #635 fix has two parts:
//   1. SelectorTagHelper.cs (src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/
//      SelectorTagHelper.cs) now tokenizes a wtm-dialog-init island's own
//      open+close tag as ONE matched pair, with DEDICATED sentinel tokens
//      ($$dialoginit$$ / $$#dialoginit$$), BEFORE the bare-<script> escape.
//      That is a C# TagHelper change, covered by its own xUnit/MSTest tests
//      in test/WalkingTec.Mvvm.Core.Test/TagHelpers/. This file only proves
//      the framework_layui.js half against the ALREADY-TOKENIZED template
//      text (matching the existing #627 suite's convention of hand-crafting
//      $$script$$/$$#script$$ fixtures rather than re-running the TagHelper).
//   2. framework_layui.js's OpenDialog2:
//        a. rehydrates $$dialoginit$$/$$#dialoginit$$ tokens back into a real
//           <script type="application/json" class="wtm-dialog-init"> tag
//           UNCONDITIONALLY — independent of the #627 kill-switch branch,
//           because these tokens are DATA (JSON.parse + whitelist dispatch),
//           never eval'd code.
//        b. adds a `success` callback to its layer.open(...) call (it had
//           none before) that calls the already-existing, already-tested
//           ff.ConsumeIslandsIn(layero) (#587) — scoped to the just-opened
//           layer's own subtree, claim-before-dispatch idempotent, never a
//           document-wide rescan.
//
// Server-response-island decision (documented in-source at the
// `ff.SafeHtml(str)` call site in OpenDialog2): OpenDialog2's ajax response
// is always the ONE fixed framework view (Views/_Framework/Selector.cshtml),
// which renders only wt:container/wt:grid/wt:row/wt:button — none of which
// are dialog-init island emitters (only the Form/ field TagHelpers and
// <wt:dialog-init> emit them). So, unlike ff.OpenDialog (a GENERIC dialog
// opener for arbitrary developer-authored forms), OpenDialog2 does NOT gain
// a pre-SafeHtml server-response extraction step — there is no reachable
// emitter for it today. Item 7 below proves the two halves of that decision
// empirically: (a) a same-shaped island embedded in the raw response WOULD
// be stripped by ff.SafeHtml's real FORBID_TAGS (so extracting it now would
// have been necessary if this were wired up — it deliberately is not), and
// (b) a bare <script> in the response is not newly executed by this change
// (OpenDialog2 never touched `str`/`safeStr` script handling before #635 and
// still does not after).

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
// Source sweep / mutation guards
// ---------------------------------------------------------------------------
describe('#635 ff.OpenDialog2 island dispatch — source sweep', () => {
  test('OpenDialog2 body registers a success callback that calls ff.ConsumeIslandsIn(layero) (mutation guard)', () => {
    const block = active.match(/OpenDialog2\s*:\s*function[\s\S]*?\n\s*CloseDialog\s*:\s*function/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/success\s*:\s*function\s*\(\s*layero\s*\)/);
    expect(block[0]).toMatch(/ff\.ConsumeIslandsIn\(\s*layero\s*\)/);
  });

  test('OpenDialog2 rehydrates $$dialoginit$$/$$#dialoginit$$ tokens unconditionally, before the kill-switch branch', () => {
    const block = active.match(/OpenDialog2\s*:\s*function[\s\S]*?\n\s*CloseDialog\s*:\s*function/);
    expect(block).not.toBeNull();
    const dialogInitOpenIdx = block[0].indexOf('dialoginit');
    const killSwitchIdx = block[0].indexOf('_isLegacyRehydrationDisabled()');
    expect(dialogInitOpenIdx).toBeGreaterThan(-1);
    expect(killSwitchIdx).toBeGreaterThan(-1);
    expect(dialogInitOpenIdx).toBeLessThan(killSwitchIdx);
    // The rehydration itself must not be gated behind the kill-switch check —
    // i.e. it must not appear only inside the `if (ff._isLegacyRehydrationDisabled())`
    // branch. We already know it appears BEFORE that check textually; this
    // second assertion confirms the LITERAL restoration strings used are the
    // dedicated dialoginit tag shape, not the shared $$script$$ tokens. Uses
    // a plain substring check (not a regex) because the source text itself
    // contains regex-metacharacter-shaped literals ([$]{2}) that would
    // otherwise be mis-parsed as this test's OWN regex syntax rather than as
    // plain characters to match.
    expect(block[0]).toContain('\'<script type="application/json" class="wtm-dialog-init">\'');
    expect(block[0]).toContain('dialoginit');
  });

  test('active-code eval( count is still exactly 1 after the #635 change', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('raw eval( token count in the file is unchanged from the pre-#635 baseline (4: 1 real call + 3 comment mentions)', () => {
    const matches = src.match(/eval\(/g) || [];
    expect(matches).toHaveLength(4);
  });
});

// ---------------------------------------------------------------------------
// Behavioral harness — mirrors framework_layui_627_legacy_killswitch.test.js's
// loadFreshFf/makePassthroughDomPurify/makeSelectorJqueryFactory exactly.
// ---------------------------------------------------------------------------

function loadFreshFf(layui, ajaxImpl, domPurifyImpl, jqueryFactory) {
  const jqueryMock = Object.assign(
    jqueryFactory || function () {
      return {
        cookie: jest.fn(),
        text: function (s) { this._text = s; return this; },
        html: function () { return this._text; }
      };
    },
    { ajax: ajaxImpl || jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui: layui || { use: jest.fn(), layer: { alert: jest.fn(), msg: jest.fn() } },
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jqueryMock,
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

function makeSelectorJqueryFactory() {
  return function (selector) {
    if (typeof selector === 'string' && selector.charAt(0) === '#') {
      var el = document.getElementById(selector.slice(1));
      return el ? [el] : [];
    }
    return {
      cookie: jest.fn(),
      text: function (s) { this._text = s; return this; },
      html: function () { return this._text; }
    };
  };
}

function makeAjaxSuccess(responseHtml, headers) {
  return jest.fn((opts) => {
    const request = {
      getResponseHeader: (name) =>
        Object.prototype.hasOwnProperty.call(headers || {}, name) ? headers[name] : null
    };
    opts.success(responseHtml, 'success', request);
  });
}

// The ajax response needs: the `wtVar_x = table.render(xoption)` marker
// regGridVar requires; a <table lay-filter> for the safe DOM-query grid-id
// extraction (#332); and the literal $$SearchPanel$$ placeholder the tempId
// template gets spliced into. Matches #627's OPEN_DIALOG2_RESPONSE_HTML.
const OPEN_DIALOG2_RESPONSE_HTML =
  '<div>wtVar_x = table.render(xoption);</div>' +
  '<table id="g1" lay-filter="f1"></table>' +
  '$$SearchPanel$$';

// A layer.open mock that ACTUALLY inserts `content` into the live jsdom
// document (unlike the #627 suite's OpenDialog2 mock, which never touches
// the DOM and only asserts on the `content` string) — needed here because
// #635's dispatch mechanism is driven by a REAL DOM subtree via
// ff.ConsumeIslandsIn(layero). It also mimics jQuery's domManip behavior of
// executing bare/JS-typed <script> elements found in an HTML string passed
// to .append()/.html() (unlike a raw element.innerHTML assignment, which
// leaves such scripts inert per the HTML spec's "already started" flag) —
// application/json islands are never a recognized JS type, so they are left
// untouched here exactly as a real browser/jQuery would leave them, to be
// picked up by ConsumeIslandsIn afterward. `success` fires only AFTER this
// insertion/execution step has fully completed, mirroring real layui
// layer.js (creat() appends content, THEN callback() invokes `success`).
function makeOpenDialog2LayuiWithDomInsertion() {
  return {
    use: jest.fn((mods, cb) => cb()),
    layer: {
      load: jest.fn(() => 1),
      close: jest.fn(),
      alert: jest.fn(),
      full: jest.fn(),
      open: jest.fn((opts) => {
        var container = document.createElement('div');
        // Safety note: `opts.content` here is ALWAYS a string built by this
        // same test file's own fixtures (hard-coded template/response HTML
        // below) — never external/attacker-controlled input — so this
        // innerHTML assignment is safe test-mock plumbing, not a real
        // sanitization boundary (the real boundary, ff.SafeHtml/DOMPurify, is
        // exercised separately in the "server-response island decision"
        // describe block below via the actual vendored DOMPurify).
        container.innerHTML = opts.content;
        document.body.appendChild(container);
        var scripts = container.querySelectorAll('script');
        for (var i = 0; i < scripts.length; i++) {
          var s = scripts[i];
          var type = (s.getAttribute('type') || '').toLowerCase();
          var isJs = type === '' || type === 'text/javascript' || type === 'application/javascript';
          if (isJs) {
            // Safety note: `new Function` here stands in for jQuery's own
            // domManip/DOMEval script-execution step (real jQuery .append()/
            // .html() executes recognized-JS-type <script> elements found in
            // an inserted HTML string, unlike a raw element.innerHTML
            // assignment, which the HTML spec marks "already started" so it
            // never executes). `s.textContent` is this test file's own
            // fixture text (never attacker-controlled), so this is safe,
            // test-only jQuery-behavior emulation — not a production code
            // path, and not something real user/server input ever reaches.
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

function islandJson(actions) {
  return JSON.stringify({ actions: actions });
}

describe('#635 ff.OpenDialog2 — template-origin island dispatch', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    document
      .querySelectorAll('meta[name="wtm-disable-legacy-script-rehydration"]')
      .forEach((m) => m.parentNode && m.parentNode.removeChild(m));
    delete window.__635order;
  });

  // ---- 1. Template island dispatches exactly once on dialog open ----------
  test('1. a template-origin island dispatches exactly once on dialog open', () => {
    makeOpenDialog2TempEl(
      'Temp635a',
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'template island' }]) + '$$#dialoginit$$<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.OpenDialog2('/some/search/url', 'w635a', 'Title', 500, 400, '#Temp635a');

    expect(layui.layer.open).toHaveBeenCalledTimes(1);
    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('template island', { title: '' });
  });

  // ---- 2. Ordering: legacy script runs BEFORE the island dispatch that ----
  // ---- reads what it set (kill-switch OFF) ---------------------------------
  // Mock-fidelity note: makeOpenDialog2LayuiWithDomInsertion's layer.open mock
  // inserts `content` into the live DOM and executes any JS-typed <script>
  // elements found in it, THEN calls opts.success(container) — i.e. content
  // insertion/execution completes strictly before `success` fires. This was
  // verified against the REAL vendored layer.js in both trees this repo
  // ships, not assumed:
  //   - demo/WalkingTec.Mvvm.Demo/wwwroot/layui/lay/modules/layer.js (2.6.3):
  //     `s.pt.creat` calls `e.vessel(f, function(n,r,u){ c.append(n[0]); ...
  //     c.append(n[1]); ... })` (content append), and only afterwards, at the
  //     end of the same `creat()` function, calls `e.move().callback()`.
  //     `s.pt.callback` runs `a.success && (2==a.type ? ... :
  //     a.success(n, t.index))` — synchronous for non-iframe types (type 1,
  //     the dialog type OpenDialog2 uses).
  //   - demo/WalkingTec.Mvvm.Demo/wwwroot/layui-next/layui.js (2.13.8): same
  //     shape — `i.pt.creat`'s vessel/append step precedes
  //     `a.move().callback()` at the end of `creat()`, and `i.pt.callback`
  //     runs `o.success && (2==o.type ? ... : o.success(a, n.index, n))`,
  //     again synchronous for non-iframe types.
  // So real layer.js genuinely appends content (running any legacy inline
  // <script>) BEFORE invoking `success` in both shipped versions, which is
  // exactly what this mock reproduces — the mock's sequence is faithful to
  // the real library, not merely a convenient assumption.
  test('2. ordering: a legacy $$script$$ segment runs BEFORE the island dispatch that observes its effect', () => {
    window.__635order = [];
    makeOpenDialog2TempEl(
      'Temp635b',
      '$$script$$window.__635order.push("legacy");$$#script$$' +
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'island' }]) + '$$#dialoginit$$' +
      '<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    // Capture, at the moment the island is dispatched, whether the legacy
    // script's effect had already landed — proves ORDERING, not mere
    // co-occurrence (the static JSON payload cannot itself reference runtime
    // state, so the proof lives in the mock's observation of live state).
    layui.layer.alert = jest.fn(function () {
      window.__635order.push('island:' + window.__635order.indexOf('legacy'));
    });
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/search/url', 'w635b', 'Title', 500, 400, '#Temp635b');

    expect(window.__635order).toEqual(['legacy', 'island:0']);
  });

  // ---- 3. Kill-switch ON: legacy stripped, island still dispatched --------
  test('3. kill-switch ON (property): legacy $$script$$ segment is stripped/not executed AND the island still dispatches', () => {
    window.__635c = 0;
    makeOpenDialog2TempEl(
      'Temp635c',
      '$$script$$window.__635c = 1;$$#script$$' +
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'killswitch on' }]) + '$$#dialoginit$$' +
      '<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/some/search/url', 'w635c', 'Title', 500, 400, '#Temp635c');

    expect(window.__635c).toBe(0);
    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('killswitch on', { title: '' });
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('1 legacy inline script(s) in the selector search-panel template were NOT executed')
    );

    delete window.__635c;
  });

  // ---- 4. Kill-switch OFF: both run, legacy first --------------------------
  test('4. kill-switch OFF (default): both the legacy script and the island run, legacy first', () => {
    window.__635order = [];
    makeOpenDialog2TempEl(
      'Temp635d',
      '$$script$$window.__635order.push("legacy");$$#script$$' +
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'off' }]) + '$$#dialoginit$$' +
      '<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    layui.layer.alert = jest.fn(function () { window.__635order.push('island'); });
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/search/url', 'w635d', 'Title', 500, 400, '#Temp635d');

    expect(window.__635order).toEqual(['legacy', 'island']);
  });

  // ---- 5. No islands present → no dispatch, byte-identical composed content
  test('5. no islands present: no dispatch call, and the composed content matches pre-#635 shape byte-for-byte', () => {
    makeOpenDialog2TempEl(
      'Temp635e',
      '$$script$$window.__k635e=1$$#script$$<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.OpenDialog2('/some/search/url', 'w635e', 'Title', 500, 400, '#Temp635e');

    expect(layui.layer.open).toHaveBeenCalledTimes(1);
    const content = layui.layer.open.mock.calls[0][0].content;
    // Byte-identical shape to the pre-#635 baseline (framework_layui_627's
    // test 10 fixture/assertions): the bare script is rehydrated into a real
    // tag, the panel markup is present, and there is no dialoginit residue
    // anywhere (none was present in the source template).
    expect(content).toContain('<script>window.__k635e=1</script>');
    expect(content).toContain('<div>panel</div>');
    expect(content).not.toContain('dialoginit');
    expect(dispatchSpy).not.toHaveBeenCalled();
    expect(layui.layer.alert).not.toHaveBeenCalled();
  });

  // ---- 6. No double-dispatch ------------------------------------------------
  // This proves the ACTUAL claim mechanism ff.ConsumeIslandsIn uses (read at
  // framework_layui.js ~L3584 before writing this test): it marks each island
  // node `data-wtm-dispatched="1"` SYNCHRONOUSLY, before scheduling its
  // dispatch, and its query selector
  // ('script[type="application/json"].wtm-dialog-init:not([data-wtm-dispatched])')
  // excludes already-claimed nodes. A vacuous version of this test would only
  // ever call ff.OpenDialog2 once and assert dispatchSpy was called once —
  // that would pass even if the claim mechanism were entirely removed (since
  // there is only one island to dispatch in the first place). To actually
  // exercise the claim, this test re-invokes ff.ConsumeIslandsIn a SECOND
  // time against the SAME layero root OpenDialog2's own `success` callback
  // used, and asserts the second call is a genuine no-op.
  test('6. no double-dispatch: a second ff.ConsumeIslandsIn(layero) call on the SAME layer root does not re-dispatch, because the island already carries data-wtm-dispatched="1"', () => {
    makeOpenDialog2TempEl(
      'Temp635f',
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'once only' }]) + '$$#dialoginit$$<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.OpenDialog2('/some/search/url', 'w635f', 'Title', 500, 400, '#Temp635f');

    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledTimes(1);

    // Locate the island node that OpenDialog2's success callback just
    // consumed, and confirm it is already claimed.
    const islandNode = document.querySelector('script[type="application/json"].wtm-dialog-init');
    expect(islandNode).not.toBeNull();
    expect(islandNode.getAttribute('data-wtm-dispatched')).toBe('1');

    // islandNode's direct parent IS the `container` div that
    // makeOpenDialog2LayuiWithDomInsertion's layer.open mock built via
    // `container.innerHTML = opts.content; ... opts.success(container)` — the
    // exact same `layero` root OpenDialog2's success callback passed to
    // ff.ConsumeIslandsIn. Every top-level node in the composed content
    // string (the response HTML's div/table plus the rehydrated dialoginit
    // <script> and the panel <div>) becomes a direct child of `container`
    // when assigned via innerHTML, so islandNode.parentElement === layero.
    const layero = islandNode.parentElement;

    // Re-invoke ff.ConsumeIslandsIn against that SAME root, simulating a
    // stray re-entry. If the data-wtm-dispatched claim were not actually
    // gating re-selection (e.g. only guarding scheduling, or the query
    // selector's `:not([data-wtm-dispatched])` clause were dropped), this
    // would dispatch 'once only' a second time.
    ff.ConsumeIslandsIn(layero);

    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
    expect(islandNode.getAttribute('data-wtm-dispatched')).toBe('1');
  });

  // ---- sanity: the page-ready consumer never sees the tokenized template ---
  test('sanity: the page-ready island consumer does not fire on the raw (still-tokenized) #Temp{Id} template text', () => {
    makeOpenDialog2TempEl(
      'Temp635g',
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'should not fire yet' }]) + '$$#dialoginit$$<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    // loadFreshFf runs the real source, which triggers
    // ff._consumePageReadyIslands() immediately (document.readyState is not
    // 'loading' in this jsdom test environment) — BEFORE OpenDialog2 is ever
    // called. The token text inside the plain <div id="Temp635g"> stand-in is
    // not a real <script> element, so the document-wide page-ready consumer
    // must find nothing.
    loadFreshFf(layui, jest.fn(), makePassthroughDomPurify(), makeSelectorJqueryFactory());

    expect(layui.layer.alert).not.toHaveBeenCalled();
  });

  // ---- 8. Scoping regression guard: layero vs document ---------------------
  // Review finding: no prior test in this file would fail if OpenDialog2's
  // `ff.ConsumeIslandsIn(layero)` call were mutated to
  // `ff.ConsumeIslandsIn(document)`. That mutation would be a real
  // regression — see ff.ConsumeIslandsIn's own "SCOPED-ONLY BY DESIGN"
  // comment (framework_layui.js ~L3574-3583): a document-wide rescan would
  // re-dispatch every un-dispatched `.wtm-dialog-init` island already sitting
  // in the page, including ones an unrelated code path placed there. This
  // test plants exactly such an island directly under document.body, OUTSIDE
  // the dialog layer, and proves ff.OpenDialog2 never touches it.
  test('8. scoping: ff.ConsumeIslandsIn(layero) dispatches only the layer\'s own island, never an un-dispatched island sitting elsewhere in the page (fails if OpenDialog2 were changed to call ConsumeIslandsIn(document))', () => {
    makeOpenDialog2TempEl(
      'Temp635j',
      '$$dialoginit$$' + islandJson([{ type: 'alert', message: 'inside the layer' }]) + '$$#dialoginit$$<div>panel</div>'
    );
    const layui = makeOpenDialog2LayuiWithDomInsertion();
    const ajax = makeAjaxSuccess(OPEN_DIALOG2_RESPONSE_HTML, {});
    const { ff } = loadFreshFf(layui, ajax, makePassthroughDomPurify(), makeSelectorJqueryFactory());

    // An UN-DISPATCHED island planted directly under document.body, OUTSIDE
    // the dialog layer, added AFTER loadFreshFf's one-time
    // ff._consumePageReadyIslands() pass has already run (document.readyState
    // is not 'loading' in jsdom, so that pass fires synchronously during
    // module load, before this node exists — see test "sanity" above for the
    // same timing). This isolates the assertion to ff.ConsumeIslandsIn's OWN
    // rootEl scoping, not the separate page-ready consumer.
    const outsideIsland = document.createElement('script');
    outsideIsland.type = 'application/json';
    outsideIsland.className = 'wtm-dialog-init';
    outsideIsland.textContent = islandJson([{ type: 'alert', message: 'OUTSIDE the layer - must never dispatch' }]);
    document.body.appendChild(outsideIsland);

    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.OpenDialog2('/some/search/url', 'w635j', 'Title', 500, 400, '#Temp635j');

    // Exactly one dispatch — the layer's own island — never the outside one.
    expect(dispatchSpy).toHaveBeenCalledTimes(1);
    expect(dispatchSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        actions: [expect.objectContaining({ message: 'inside the layer' })]
      })
    );
    expect(layui.layer.alert).toHaveBeenCalledTimes(1);
    expect(layui.layer.alert).toHaveBeenCalledWith('inside the layer', { title: '' });
    // The outside island was never even claimed — its missing
    // data-wtm-dispatched attribute is direct proof ff.ConsumeIslandsIn(layero)
    // never visited it (the claim mechanism marks the attribute synchronously
    // BEFORE scheduling any dispatch, so "unclaimed" here is equivalent to
    // "never seen by this scan").
    expect(outsideIsland.hasAttribute('data-wtm-dispatched')).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// 7. Server-response-island decision — empirical proof of both halves.
// ---------------------------------------------------------------------------
describe('#635 ff.OpenDialog2 — server-response island decision (documented as out of scope)', () => {
  const purifyPath = path.resolve(
    __dirname,
    '../../../src/WalkingTec.Mvvm.Mvc/dompurify.js'
  );
  const purifySource = fs.readFileSync(purifyPath, 'utf8');

  function loadRealDomPurify() {
    // Same technique as framework_layui_phase3b_html_sanitize.test.js: the
    // vendored dompurify.js is a UMD bundle; running it attaches DOMPurify to
    // whatever global/window it resolves to in this Node+jsdom environment.
    // Safety note: `purifySource` is read from this repo's own vendored,
    // version-pinned src/WalkingTec.Mvvm.Mvc/dompurify.js (see that file's
    // header comment) — trusted, repo-controlled source, not external or
    // attacker-influenced input. `new Function` is used (not eval) purely to
    // get an isolated top-level scope for the UMD bundle, matching the
    // already-established pattern in framework_layui_phase3b_html_sanitize.test.js.
    const runInWindow = new Function('window', 'document', purifySource);
    runInWindow(global.window || global, global.document || {});
    return (global.window && global.window.DOMPurify) || global.DOMPurify;
  }

  afterEach(() => {
    document.body.innerHTML = '';
  });

  test('7a. a wtm-dialog-init island embedded in the RAW server response would be stripped by real ff.SafeHtml (FORBID_TAGS) — proves extraction would be required IF this were wired up, and it deliberately is not', () => {
    const realDomPurify = loadRealDomPurify();
    expect(realDomPurify && typeof realDomPurify.sanitize).toBe('function');

    const island = '<script type="application/json" class="wtm-dialog-init">' +
      islandJson([{ type: 'alert', message: 'server island' }]) + '</script>';
    const responseWithIsland =
      '<div>wtVar_x = table.render(xoption);</div>' +
      '<table id="g1" lay-filter="f1"></table>' +
      island +
      '$$SearchPanel$$';

    const layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl('Temp635h', '<div>panel</div>');
    const ajax = makeAjaxSuccess(responseWithIsland, {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());
    const dispatchSpy = jest.spyOn(ff, '_dispatchIslandWhenReady');

    ff.OpenDialog2('/some/search/url', 'w635h', 'Title', 500, 400, '#Temp635h');

    const content = layui.layer.open.mock.calls[0][0].content;
    // ff.SafeHtml's real FORBID_TAGS: ['script','style'] strips the island
    // before it ever reaches `content` — it is inert-and-dropped, exactly
    // like today, not newly dispatched by #635.
    expect(content).not.toContain('wtm-dialog-init');
    expect(dispatchSpy).not.toHaveBeenCalled();
    expect(layui.layer.alert).not.toHaveBeenCalled();
  });

  test('7b. a bare <script> in the server response is NOT newly executed by the #635 change', () => {
    const realDomPurify = loadRealDomPurify();

    window.__635srv = 0;
    const responseWithScript =
      '<div>wtVar_x = table.render(xoption);</div>' +
      '<table id="g1" lay-filter="f1"></table>' +
      '<script>window.__635srv = 1;</script>' +
      '$$SearchPanel$$';

    const layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl('Temp635i', '<div>panel</div>');
    const ajax = makeAjaxSuccess(responseWithScript, {});
    const { ff } = loadFreshFf(layui, ajax, realDomPurify, makeSelectorJqueryFactory());

    ff.OpenDialog2('/some/search/url', 'w635i', 'Title', 500, 400, '#Temp635i');

    // SafeHtml strips the bare <script> from the server response (unchanged,
    // pre-existing #332 behavior); #635 never adds a new execution path for
    // server-response script content — it only ever touches the LOCAL
    // #Temp{Id} template text.
    expect(window.__635srv).toBe(0);
    const content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__635srv');

    delete window.__635srv;
  });
});
