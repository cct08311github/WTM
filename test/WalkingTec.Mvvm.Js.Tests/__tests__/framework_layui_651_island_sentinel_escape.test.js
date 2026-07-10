// Tests for Issue #651 (stored XSS, dialog trust boundary — cross-vendor
// reviewed reproduction): ff.OpenDialog2 (the <wt:selector> search-panel
// dialog opener, framework_layui.js) restores a wtm-dialog-init island's own
// open/close tag AND any bare <script> tag with GLOBAL regex replaces over
// the WHOLE composed search-panel template:
//   template
//     .replace(/[$]{2}dialoginit[$]{2}/img, '<script type="application/json" class="wtm-dialog-init">')
//     .replace(/[$]{2}#dialoginit[$]{2}/img, '</script>')
//     // ...and, further down (kill-switch OFF path):
//     .replace(/[$]{2}script[$]{2}/img, '<script>')
//     .replace(/[$]{2}#script[$]{2}/img, '</script>')
// These replaces are NOT scoped to the sentinel occurrences SelectorTagHelper
// itself placed — they match ANY occurrence of the literal token text
// anywhere in the template string. A wtm-dialog-init island's JSON body
// (ComboBoxTagHelper/CheckBoxTagHelper/RadioTagHelper/TransferTagHelper's
// loadComboItemsAction, or CheckBox/RadioTagHelper's data-wtm-defaults
// attribute) carries MODEL-DERIVED data (selectVal, url, the defaults array).
// _islandJsonOptions' default JavaScriptEncoder escapes '<', '>', '&' (safe
// against a raw </script> breakout) but NOT '$' — so, before the #651 fix, a
// stored value containing the literal sentinel sequence
//   "$$#dialoginit$$$$script$$window.__pwned=1$$#script$$"
// survived JSON serialization intact and, once the composed template reached
// ff.OpenDialog2, was rehydrated exactly like a real sentinel:
//   ...selectVal:["</script><script>window.__pwned=1</script>"]...
// — breaking out of the island and executing attacker-controlled script the
// instant the selector dialog opens.
//
// The fix (LayuiIslandJson.Serialize, src/WalkingTec.Mvvm.TagHelpers.LayUI/
// Form/ComboBoxTagHelper.cs, exercised by
// test/WalkingTec.Mvvm.Core.Test/TagHelpers/IslandSentinelEscape651Tests.cs
// on the C# side): after JsonSerializer.Serialize, every '$' is replaced with
// its JSON Unicode escape sequence ($) — a valid, reversible transform
// since '$' only ever appears inside JSON STRING VALUES in these payloads.
// This file is the JS-side half: it hand-crafts the EXACT tokenized fixture
// text the fixed C# TagHelpers now emit (matching this repo's established
// convention — see framework_layui_635_opendialog2_island_dispatch.test.js's
// header for why hand-crafting the already-tokenized fixture, rather than
// re-running the C# TagHelper, is the right split here), drives the REAL
// ff.OpenDialog2, and proves:
//   1. no attacker script ever executes,
//   2. the island/attribute round-trips (JSON.parse) to the EXACT original
//      malicious-looking string, so ff.LoadComboItems receives it as inert
//      DATA, and
//   3. WITHOUT the $ escape (the pre-#651 shape), the identical payload
//      DOES execute — proving this test harness actually detects the
//      vulnerability it exists to guard against (mutation-verify at the JS
//      layer, mirroring the C#-side mutation-verify run for the fix itself).

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
// Harness — mirrors framework_layui_633_opendialog2_selector_panel.test.js /
// framework_layui_635_opendialog2_island_dispatch.test.js exactly.
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
  // ff.LoadComboItems's inner request — never expected to actually be
  // reached in the injection scenarios below (the payload lives in
  // selectVal, not url), but wired up so a false-negative "it just crashed
  // before reaching the assertion" can't masquerade as a pass.
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

// Same shape as #633/#635's mock: layer.open ACTUALLY inserts `content` into
// the live jsdom document and executes any JS-typed <script> found in it
// (application/json islands are left untouched, exactly as a real browser
// would), THEN calls `success` — required because the injection this test
// guards against is only observable through a REAL DOM parse of the fully
// tokenized/rehydrated string, not a string-level assertion alone.
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
        // SAFETY NOTE (test-only, mirrors #633/#635's identical mock
        // verbatim): `opts.content` is ALWAYS a hard-coded string this SAME
        // test file's own fixtures build a few lines above each call site —
        // never external/attacker-controlled input, never anything read
        // from a network response, user input, or file. This innerHTML
        // assignment IS the thing under test (jsdom parsing the fully
        // tokenized/rehydrated template exactly as a real browser would),
        // not a production code path and not a real sanitization boundary
        // in itself — the real boundary (ff.SafeHtml/DOMPurify) runs inside
        // ff.OpenDialog2 itself, before this mock ever sees `content`.
        container.innerHTML = opts.content;
        document.body.appendChild(container);
        var scripts = container.querySelectorAll('script');
        for (var i = 0; i < scripts.length; i++) {
          var s = scripts[i];
          var type = (s.getAttribute('type') || '').toLowerCase();
          var isJs = type === '' || type === 'text/javascript' || type === 'application/javascript';
          if (isJs) {
            // SAFETY NOTE (test-only, mirrors #633/#635's identical mock):
            // stands in for jQuery's own domManip/DOMEval script-execution
            // step (real jQuery .append()/.html() executes recognized-JS-type
            // <script> elements found in an inserted HTML string, unlike a
            // raw element.innerHTML assignment, which the HTML spec marks
            // "already started" so it never executes). `s.textContent` here
            // is always text that originated from this test file's own
            // fixtures (never external/attacker input) — deliberately
            // exercising the EXACT mechanism the #651 vulnerability abuses
            // is the whole point of these tests (see test 3/8's mutation-verify,
            // which assert this line DOES run attacker-shaped script when the
            // fix's escape is absent, and the fixed tests assert it does NOT).
            //
            // Per-<script> try/catch is REQUIRED for fidelity, not laxity: a
            // browser (and jQuery's domManip) compiles and runs each <script>
            // element INDEPENDENTLY, so a parse error in one (e.g. the truncated
            // `{Id}defaultvalues = ["` left when an injected </script> breaks out
            // of the inline assignment) is a non-fatal, isolated error that does
            // NOT prevent the very next, injected `<script>window.__pwned2=1<...`
            // from executing. Without this isolation the mock would ABORT on the
            // broken first script and mask the injection the mutation test must
            // observe — i.e. it would make the vulnerable case falsely look safe.
            try {
              (new Function(s.textContent))();
            } catch (scriptErr) {
              /* isolated per-script compile/runtime error — browser-faithful */
            }
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

// ---------------------------------------------------------------------------
// #651 fixture builders
// ---------------------------------------------------------------------------

// The exact malicious value from the #651 report: an attempt to break out of
// a wtm-dialog-init island and execute arbitrary script the instant the
// composed template is rehydrated/inserted.
const XSS_PAYLOAD = '$$#dialoginit$$$$script$$window.__pwned=1$$#script$$';

// Mirrors LayuiIslandJson.Serialize (ComboBoxTagHelper.cs): JSON.stringify
// followed by escaping every '$' to its JSON Unicode escape. This is what
// the FIXED C# TagHelpers now emit as an island's JSON body.
function fixedIslandJson(action) {
  return JSON.stringify(action).replace(/\$/g, '\\u0024');
}

// The PRE-#651 shape: plain JsonSerializer.Serialize output with no '$'
// escaping — used ONLY by the mutation-verify tests below, to prove this
// harness actually distinguishes fixed from vulnerable output.
function vulnerableIslandJson(action) {
  return JSON.stringify(action);
}

describe('#651 ff.OpenDialog2 — wtm-dialog-init island sentinel-collision XSS is neutralized', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwned;
  });

  test('1. FIXED (\\u0024-escaped) payload: no script executes, composed content carries no live breakout, and the island round-trips to the exact original malicious string (kill-switch OFF, default)', () => {
    var comboAjaxData = { Data: [] };
    var $ = makeJQueryMock(comboAjaxData);
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var action = {
      type: 'loadComboItems',
      controlType: 'combo',
      url: '/Home/GetComboItems',
      id: 'DialogCombo651a',
      field: 'ComboField',
      selectVal: [XSS_PAYLOAD]
    };
    makeOpenDialog2TempEl(
      'Temp651a',
      '$$dialoginit$$' + fixedIslandJson(action) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var loadComboItemsSpy = jest.spyOn(ff, 'LoadComboItems');

    ff.OpenDialog2('/some/search/url', 'w651a', 'Title', 500, 400, '#Temp651a');

    // 1. No attacker script ever ran.
    expect(window.__pwned).toBeUndefined();

    // 2. The composed content contains no live breakout shape.
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__pwned');
    expect(content).not.toContain('window.__pwned=1</script>');

    // 3. ff.LoadComboItems received the EXACT original malicious-looking
    // string, as inert data — proving the escape round-trips through
    // JSON.parse (island rehydration -> ff.DispatchAction -> this call).
    expect(loadComboItemsSpy).toHaveBeenCalledWith(
      'combo', '/Home/GetComboItems', 'DialogCombo651a', 'ComboField',
      [XSS_PAYLOAD], undefined, undefined
    );
  });

  test('2. FIXED (\\u0024-escaped) payload: same guarantees hold with the #627 kill-switch ON (DisableLegacyScriptRehydration)', () => {
    var comboAjaxData = { Data: [] };
    var $ = makeJQueryMock(comboAjaxData);
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var action = {
      type: 'loadComboItems',
      controlType: 'combo',
      url: '/Home/GetComboItems',
      id: 'DialogCombo651b',
      field: 'ComboField',
      selectVal: [XSS_PAYLOAD]
    };
    makeOpenDialog2TempEl(
      'Temp651b',
      '$$dialoginit$$' + fixedIslandJson(action) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var loadComboItemsSpy = jest.spyOn(ff, 'LoadComboItems');

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/some/search/url', 'w651b', 'Title', 500, 400, '#Temp651b');

    expect(window.__pwned).toBeUndefined();
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__pwned');
    expect(loadComboItemsSpy).toHaveBeenCalledWith(
      'combo', '/Home/GetComboItems', 'DialogCombo651b', 'ComboField',
      [XSS_PAYLOAD], undefined, undefined
    );
  });

  test('3. mutation-verify: WITHOUT the \\u0024 escape (the pre-#651 vulnerable shape), the IDENTICAL payload breaks out of the island and DOES execute — proves this harness actually detects the vulnerability', () => {
    var comboAjaxData = { Data: [] };
    var $ = makeJQueryMock(comboAjaxData);
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var action = {
      type: 'loadComboItems',
      controlType: 'combo',
      url: '/Home/GetComboItems',
      id: 'DialogCombo651c',
      field: 'ComboField',
      selectVal: [XSS_PAYLOAD]
    };
    makeOpenDialog2TempEl(
      'Temp651c',
      // Pre-fix shape: the raw _islandJsonOptions-only serialization (no
      // $ escaping) — this is exactly what ComboBoxTagHelper emitted
      // before the #651 fix.
      '$$dialoginit$$' + vulnerableIslandJson(action) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w651c', 'Title', 500, 400, '#Temp651c');

    // The injected script actually ran: window.__pwned was set to 1 by the
    // attacker-controlled <script> that broke out of the island.
    expect(window.__pwned).toBe(1);
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<script>window.__pwned=1</script>');
  });
});

describe('#651 data-wtm-defaults attribute — sentinel collision in a checkbox/radio default-selection attribute is neutralized', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
  });

  // The data-wtm-defaults attribute is not itself a dispatched island (it is
  // read lazily by ff.ChainChange/_readFieldDefaults, never automatically on
  // dialog open) — so the #651 invariant under test here is the one the C#
  // fix actually promises: OpenDialog2's blanket $$...$$ regex replaces must
  // have ZERO effect on the attribute's serialized text. This is proven
  // directly against the REAL rehydration output (not a re-implementation)
  // by driving ff.OpenDialog2 and reading the attribute back off the live,
  // inserted DOM node.

  function fixedDefaultsJson(values) {
    return JSON.stringify(values).replace(/\$/g, '\\u0024');
  }

  function vulnerableDefaultsJson(values) {
    return JSON.stringify(values);
  }

  test('4. FIXED (\\u0024-escaped) data-wtm-defaults survives OpenDialog2 rehydration byte-for-byte and round-trips to the exact original value', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var escaped = fixedDefaultsJson([XSS_PAYLOAD]);
    // Built via setAttribute (not string concatenation) so the surrounding
    // HTML quoting is produced by jsdom's own serializer, exactly like
    // ASP.NET Core's TagHelperOutput.Attributes pipeline would produce it —
    // this test is only about the '$' characters inside the value, not
    // quote-encoding (already a separately-covered, pre-existing guarantee).
    // SAFETY NOTE (test-only): builds a detached, throwaway fixture element
    // from a hard-coded literal string (no interpolated/external data) —
    // used purely to get jsdom's own attribute-serialization behavior for
    // the `setAttribute` call below, matching how ASP.NET Core's
    // TagHelperOutput.Attributes pipeline would serialize the real markup.
    var hostMarkup = document.createElement('div');
    hostMarkup.innerHTML = '<div id="AttrHost651a" wtm-ctype="checkbox"></div>';
    hostMarkup.firstElementChild.setAttribute('data-wtm-defaults', escaped);
    makeOpenDialog2TempEl('Temp651d', hostMarkup.innerHTML + '<div>panel</div>');

    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    ff.OpenDialog2('/some/search/url', 'w651d', 'Title', 500, 400, '#Temp651d');

    var content = layui.layer.open.mock.calls[0][0].content;
    // No live breakout shape anywhere in the composed content.
    expect(content).not.toContain('<script>window.');
    expect(content).not.toContain('</script><script>');

    // Read the attribute back off the REHYDRATED copy of the markup — i.e.
    // re-parse the EXACT `content` string ff.OpenDialog2 handed to
    // layer.open, into a detached scratch element, NEVER via
    // document.getElementById. The live document also still contains the
    // ORIGINAL #Temp651d template node (framework_layui.js deliberately
    // never removes it — it is the reusable template SOURCE for repeated
    // dialog opens), which carries an id="AttrHost651a" element of its own;
    // document.getElementById would silently resolve to THAT untouched
    // template copy (document order puts it first) instead of the
    // rehydrated one this test needs to inspect, defeating the assertion.
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var hostEl = scratch.querySelector('#AttrHost651a');
    expect(hostEl).not.toBeNull();
    var raw = hostEl.getAttribute('data-wtm-defaults');
    expect(JSON.parse(raw)).toEqual([XSS_PAYLOAD]);
  });

  test("5. mutation-verify: WITHOUT the \\u0024 escape, the data-wtm-defaults attribute text IS altered by OpenDialog2's global sentinel replace — the raw payload no longer round-trips, proving the collision is real for this attribute too", () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var unescaped = vulnerableDefaultsJson([XSS_PAYLOAD]);
    var hostMarkup = document.createElement('div');
    hostMarkup.innerHTML = '<div id="AttrHost651b" wtm-ctype="checkbox"></div>';
    hostMarkup.firstElementChild.setAttribute('data-wtm-defaults', unescaped);
    makeOpenDialog2TempEl('Temp651e', hostMarkup.innerHTML + '<div>panel</div>');

    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    ff.OpenDialog2('/some/search/url', 'w651e', 'Title', 500, 400, '#Temp651e');

    // Same re-parse-the-composed-`content`-into-a-scratch-element approach as
    // test 4 above, and for the SAME reason: document.getElementById would
    // silently resolve to the untouched #Temp651e template copy rather than
    // the rehydrated (here: corrupted) one this test needs to inspect.
    var content = layui.layer.open.mock.calls[0][0].content;
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var hostEl = scratch.querySelector('#AttrHost651b');
    expect(hostEl).not.toBeNull();
    var raw = hostEl.getAttribute('data-wtm-defaults');
    // The un-escaped payload's embedded $$#dialoginit$$/$$script$$/$$#script$$
    // sequences were rewritten by OpenDialog2's blanket regex replace — the
    // attribute text can no longer be parsed back to the original value
    // (either JSON.parse throws on the corrupted text, or — if it happens to
    // still parse — the value differs from what was stored), which is
    // exactly the collision the #651 fix eliminates.
    var corrupted;
    try {
      corrupted = JSON.parse(raw);
    } catch (e) {
      corrupted = undefined;
    }
    expect(corrupted).not.toEqual([XSS_PAYLOAD]);
  });
});

// ---------------------------------------------------------------------------
// #651 follow-up: the checkbox/radio DEFAULT-VALUE inline <script> path.
//
// CheckBox/RadioTagHelper always emit an inline
//   <script> {Id}defaultvalues = [...]; </script>
// carrying the field's default selection. When that field is a searcher inside
// a <wt:selector> panel, SelectorTagHelper tokenizes that inline <script> to
// $$script$$...$$#script$$ — so an unescaped $$#script$$ inside the serialized
// default VALUE breaks out of the inline script exactly as it would out of an
// island. These tests drive the REAL ff.OpenDialog2 against a hand-crafted
// tokenized template of that inline write (fixed = the C# $-escaped shape,
// vulnerable = the pre-fix raw shape) and prove no injected script executes.
//
// Global-scope note: the layer.open mock executes a rehydrated inline <script>
// via `new Function(body)()` — a non-strict function whose bare
// `{Id}defaultvalues = [...]` assignment lands on the test realm's global
// object (jsdom's `window`), and an injected `window.__pwned2 = 1` lands there
// too. So the assertions read `window.<Id>defaultvalues` / `window.__pwned2`.
// ---------------------------------------------------------------------------
describe('#651 checkbox/radio inline defaultvalues <script> — sentinel collision is neutralized', () => {
  // The #651 report payload, re-parameterised so a successful injection sets a
  // DISTINCT global (__pwned2) from the island tests above.
  var INLINE_PAYLOAD = '$$#script$$$$script$$window.__pwned2=1$$#script$$';

  // What the FIXED C# emitter now produces for `{Id}defaultvalues = <json>` —
  // JSON.stringify then escape every '$' to $ (mirrors
  // LayuiIslandJson.Serialize). The whole inline <script> is then tokenized by
  // SelectorTagHelper to $$script$$...$$#script$$.
  function fixedInlineDefaultsTemplate(id, values) {
    var json = JSON.stringify(values).replace(/\$/g, '\\u0024');
    return '$$script$$\n ' + id + 'defaultvalues = ' + json + ';\n$$#script$$';
  }

  // The PRE-#651 shape: raw JSON.stringify with no '$' escaping.
  function vulnerableInlineDefaultsTemplate(id, values) {
    return '$$script$$\n ' + id + 'defaultvalues = ' + JSON.stringify(values) + ';\n$$#script$$';
  }

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwned2;
    delete window.Chk651adefaultvalues;
    delete window.Chk651bdefaultvalues;
    delete window.Chk651cdefaultvalues;
  });

  test('6. FIXED inline defaults (kill-switch OFF): no injected script runs and the global round-trips to the exact original payload', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl(
      'Temp651inlA',
      fixedInlineDefaultsTemplate('Chk651a', [INLINE_PAYLOAD]) + '<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w651inlA', 'Title', 500, 400, '#Temp651inlA');

    // No injected script executed.
    expect(window.__pwned2).toBeUndefined();
    // Composed content carries no live breakout shape.
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__pwned2');
    expect(content).not.toContain('window.__pwned2=1</script>');
    // The inline assignment ran as inert data: the global equals the ORIGINAL
    // payload (JS decoded $ back to '$').
    expect(window.Chk651adefaultvalues).toEqual([INLINE_PAYLOAD]);
  });

  test('7. FIXED inline defaults (kill-switch ON): the whole $$script$$ block is stripped, so nothing runs and no injection occurs', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl(
      'Temp651inlB',
      fixedInlineDefaultsTemplate('Chk651b', [INLINE_PAYLOAD]) + '<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/some/search/url', 'w651inlB', 'Title', 500, 400, '#Temp651inlB');

    expect(window.__pwned2).toBeUndefined();
    // Stripped, not executed — the global was never set either.
    expect(window.Chk651bdefaultvalues).toBeUndefined();
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__pwned2');
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('legacy inline script(s) in the selector search-panel template were NOT executed')
    );
  });

  test('8. mutation-verify: the PRE-#651 (unescaped) inline defaults DOES break out and execute the injected script (kill-switch OFF) — proves this harness detects the collision for the inline path too', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl(
      'Temp651inlC',
      vulnerableInlineDefaultsTemplate('Chk651c', [INLINE_PAYLOAD]) + '<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w651inlC', 'Title', 500, 400, '#Temp651inlC');

    // The embedded $$#script$$$$script$$window.__pwned2=1$$#script$$ was
    // globally rehydrated into </script><script>window.__pwned2=1</script>,
    // breaking out of the inline assignment and executing.
    expect(window.__pwned2).toBe(1);
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<script>window.__pwned2=1</script>');
  });
});

// ---------------------------------------------------------------------------
// #651 completeness: a NON-loadComboItems island emitter (TagInput) used as a
// selector searcher. #635 dispatches EVERY wtm-dialog-init island on the
// selector path, so a taginput island whose model/config-derived opts
// (placeholder/separator) carry a sentinel is exposed to the same collision.
// These drive the REAL ff.OpenDialog2 against a hand-crafted taginput island
// (fixed = the C# $-escaped shape via LayuiIslandJson.Serialize(action,
// _islandJsonOptions); vulnerable = the pre-fix raw shape).
// ---------------------------------------------------------------------------
describe('#651 TagInput island (a non-loadComboItems island emitter) in a selector panel — sentinel collision neutralized', () => {
  // Break out of the <script type="application/json"> island wrapper: close it
  // ($$#dialoginit$$ → </script>), then open+run an injected script.
  var TAGINPUT_PAYLOAD = '$$#dialoginit$$$$script$$window.__pwned3=1$$#script$$';

  function tagInputAction(placeholder) {
    return {
      type: 'tagInput',
      opts: { elem: '#Tin651', separator: ',', placeholder: placeholder },
      valueFieldId: 'Tin651_val',
      formId: null
    };
  }

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwned3;
  });

  test('9. FIXED taginput island (kill-switch OFF): no injected script runs, and the island round-trips to the exact original placeholder', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl(
      'Temp651tinA',
      '$$dialoginit$$' + fixedIslandJson(tagInputAction(TAGINPUT_PAYLOAD)) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w651tinA', 'Title', 500, 400, '#Temp651tinA');

    expect(window.__pwned3).toBeUndefined();
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__pwned3');
    expect(content).not.toContain('window.__pwned3=1</script>');
    // The island node's JSON parses back to the exact original placeholder
    // (inert data). Read it off the rehydrated copy in `content`, never
    // document.getElementById (which would resolve the untouched template).
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var island = scratch.querySelector('script[type="application/json"].wtm-dialog-init');
    expect(island).not.toBeNull();
    expect(JSON.parse(island.textContent).opts.placeholder).toBe(TAGINPUT_PAYLOAD);
  });

  test('10. FIXED taginput island (kill-switch ON): still no injection', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl(
      'Temp651tinB',
      '$$dialoginit$$' + fixedIslandJson(tagInputAction(TAGINPUT_PAYLOAD)) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/some/search/url', 'w651tinB', 'Title', 500, 400, '#Temp651tinB');

    expect(window.__pwned3).toBeUndefined();
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__pwned3');
  });

  test('11. mutation-verify: the PRE-#651 (unescaped) taginput island DOES break out and execute (kill-switch OFF)', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    makeOpenDialog2TempEl(
      'Temp651tinC',
      '$$dialoginit$$' + vulnerableIslandJson(tagInputAction(TAGINPUT_PAYLOAD)) + '$$#dialoginit$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w651tinC', 'Title', 500, 400, '#Temp651tinC');

    // The placeholder's embedded $$#dialoginit$$ closed the application/json
    // island early and $$script$$window.__pwned3=1$$#script$$ became a live
    // <script> that executed.
    expect(window.__pwned3).toBe(1);
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<script>window.__pwned3=1</script>');
  });
});

// ---------------------------------------------------------------------------
// eval( invariant — untouched by this fix (pure data-serialization change,
// no framework_layui.js edits at all).
// ---------------------------------------------------------------------------
describe('#651 — framework_layui.js eval( invariant unchanged', () => {
  const stripLineComments = (text) =>
    text
      .split('\n')
      .map((line) => {
        const idx = line.indexOf('//');
        return idx === -1 ? line : line.slice(0, idx);
      })
      .join('\n');
  const active = stripLineComments(src);

  test('active-code eval( count is still exactly 1', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('raw eval( token count in the file is unchanged (4: 1 real call + 3 comment mentions)', () => {
    const matches = src.match(/eval\(/g) || [];
    expect(matches).toHaveLength(4);
  });
});
