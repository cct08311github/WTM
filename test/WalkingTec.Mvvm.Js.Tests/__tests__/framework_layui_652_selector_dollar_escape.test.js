// Tests for Issue #652 (SECURITY, stored XSS — dialog trust boundary, the
// #651 follow-up): every field TagHelper that can render inside a
// <wt:selector> search panel HtmlEncodes its model-derived plaintext (an
// option label, a stored value, a tree node title, a tag) before writing it
// into SelectorTagHelper's search-panel `content`. WebUtility.HtmlEncode
// escapes '<', '>', '&', '"', ''' but NOT '$' — so a stored value containing
// the literal text "$$script$$window.__pwned=1$$#script$$" previously
// survived HtmlEncode into `content` unchanged. It is not itself a real
// <script> element, so SelectorTagHelper's pre-existing tokenize replaces
// never touched it — it reached framework_layui.js's ff.OpenDialog2 as
// plain text inside the `#Temp{Id}` template, where OpenDialog2's
// $$script$$/$$#script$$ un-tokenize replace is GLOBAL over the whole
// composed template (not scoped to the sentinels SelectorTagHelper itself
// placed) and rehydrated the plaintext into a live <script> the instant the
// selector dialog opened.
//
// The fix (SelectorTagHelper.cs): escape every literal '$' in `content` to
// the Private-Use placeholder U+E000 IMMEDIATELY BEFORE the two existing
// tokenize replaces. After that escape, the ONLY '$' sequences the emitted
// template can carry are the $$dialoginit$$/$$script$$ tokens
// SelectorTagHelper itself places afterward — a forged sentinel can no
// longer be built from model data. This file is the JS-side half: it
// hand-crafts the EXACT tokenized fixture text the fixed C# TagHelper now
// emits (matching this repo's established convention — see
// framework_layui_651_island_sentinel_escape.test.js's header for why
// hand-crafting the already-tokenized fixture, rather than re-running the
// C# TagHelper, is the right split here), drives the REAL ff.OpenDialog2,
// and proves:
//   1. the escaped (fixed) plaintext payload never executes,
//   2. a real developer <script> containing a legitimate '$' (e.g. jQuery)
//      still executes correctly after the placeholder round-trips back to
//      '$',
//   3. WITHOUT the escape (the pre-#652 shape), the IDENTICAL payload DOES
//      execute — proving this harness actually detects the vulnerability it
//      exists to guard against (mutation-verify, mirroring #651's JS-layer
//      mutation-verify), and
//   4. the #627 kill-switch (DisableLegacyScriptRehydration) path also gets
//      its placeholder restored on the surviving (non-script) markup, with
//      no leftover U+E000 characters and no execution.

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
// Harness — mirrors framework_layui_651_island_sentinel_escape.test.js /
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
  $.ajax = jest.fn((opts) => {
    const request = { getResponseHeader: () => null };
    opts.success(OPEN_DIALOG2_RESPONSE_HTML, 'success', request);
  });
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

// Same shape as #651's mock: layer.open ACTUALLY inserts `content` into the
// live jsdom document and executes any JS-typed <script> found in it, THEN
// calls `success` — required because the injection this test guards against
// is only observable through a REAL DOM parse of the fully
// tokenized/rehydrated/restored string, not a string-level assertion alone.
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
        // SAFETY NOTE (test-only, mirrors #651/#635's identical mock
        // verbatim): `opts.content` is ALWAYS a hard-coded string this SAME
        // test file's own fixtures build a few lines above each call site —
        // never external/attacker-controlled input. This innerHTML
        // assignment IS the thing under test (jsdom parsing the fully
        // tokenized/rehydrated/restored template exactly as a real browser
        // would), not a production code path and not a real sanitization
        // boundary in itself.
        container.innerHTML = opts.content;
        document.body.appendChild(container);
        var scripts = container.querySelectorAll('script');
        for (var i = 0; i < scripts.length; i++) {
          var s = scripts[i];
          var type = (s.getAttribute('type') || '').toLowerCase();
          var isJs = type === '' || type === 'text/javascript' || type === 'application/javascript';
          if (isJs) {
            // SAFETY NOTE (test-only, mirrors #651/#635's identical mock):
            // stands in for jQuery's own domManip/DOMEval script-execution
            // step. `s.textContent` here is always text that originated
            // from this test file's own fixtures (never external/attacker
            // input) — deliberately exercising the EXACT mechanism the
            // #652 vulnerability abuses is the whole point of these tests.
            //
            // Per-<script> try/catch is REQUIRED for fidelity, not laxity:
            // a browser (and jQuery's domManip) compiles and runs each
            // <script> element INDEPENDENTLY, so a parse/reference error in
            // one is a non-fatal, isolated error that does NOT prevent a
            // later, injected <script> from executing. Without this
            // isolation the mock would ABORT on a broken first script and
            // mask the injection the mutation test must observe.
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

// Faithful #TempA fixture for the NESTED-selector tests: SelectorTagHelper
// emits a selector's search-panel template as a REAL
// <script type="text/template" id="Temp{Id}"> element, whose body is INERT
// raw text (the HTML spec treats script content as raw text — jsdom follows
// this, so a nested "<script id=TempB>" inside it is NOT a live DOM node and
// document.getElementById('TempB') resolves nothing until this template's
// body is inserted into a real dialog). Using a plain <div> here would let
// jsdom parse that nested TempB into a live element prematurely (the #651
// tests document this getElementById pitfall), masking the nesting flow — so
// these tests use a genuine text/template script to model production exactly.
function makeScriptTemplateEl(id, bodyText) {
  var el = document.createElement('script');
  el.type = 'text/template';
  el.id = id;
  el.innerHTML = bodyText; // raw script text; jsdom does not parse it as DOM
  document.body.appendChild(el);
  return el;
}

const OPEN_DIALOG2_RESPONSE_HTML =
  '<div>wtVar_x = table.render(xoption);</div>' +
  '<table id="g1" lay-filter="f1"></table>' +
  '$$SearchPanel$$';

// ---------------------------------------------------------------------------
// #652 fixture builders
// ---------------------------------------------------------------------------

// The Private-Use placeholder SelectorTagHelper.cs escapes '$' to
// (DollarEscapePlaceholder). Kept as a literal U+E000 char here — not
// derived from anything — so this test independently pins the exact wire
// shape the C# emitter and the JS restore must agree on byte-for-byte.
const PLACEHOLDER = '';

// The exact #652 report shape: a stored label/value containing a literal
// sentinel sequence that is NOT wrapped in a real <script> element — plain
// text a field TagHelper's HtmlEncode left untouched (HtmlEncode does not
// touch '$').
const MALICIOUS_TEXT = '$$script$$window.__pwned=1$$#script$$';

// What the FIXED C# TagHelper now emits for that plaintext: every '$'
// escaped to the placeholder, 1:1 (mirrors
// SelectorTagHelper.cs's `content.Replace("$", DollarEscapePlaceholder)`).
function fixedEscapedText(text) {
  return text.split('$').join(PLACEHOLDER);
}

describe('#652 ff.OpenDialog2 — model-derived plaintext sentinel-collision XSS is neutralized', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwned;
  });

  test('1. FIXED (\\uE000-escaped) plaintext label: no script executes, and the label renders as inert text (kill-switch OFF, default)', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    var escaped = fixedEscapedText(MALICIOUS_TEXT);
    // Sanity: the escaped fixture carries ZERO literal '$' — exactly what
    // SelectorTagHelper.cs's content.Replace("$", DollarEscapePlaceholder)
    // guarantees before its two tokenize replaces run.
    expect(escaped.indexOf('$')).toBe(-1);
    makeOpenDialog2TempEl(
      'Temp652a',
      '<div id="Label652a">' + escaped + '</div><div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w652a', 'Title', 500, 400, '#Temp652a');

    // No attacker script ever ran.
    expect(window.__pwned).toBeUndefined();

    var content = layui.layer.open.mock.calls[0][0].content;
    // No live breakout shape anywhere in the composed content.
    expect(content).not.toContain('<script>window.__pwned');
    expect(content).not.toContain('window.__pwned=1</script>');

    // The label rendered as INERT TEXT (no live <script> element was formed
    // from it) — read it back off the rehydrated DOM (the jsdom mock above
    // already parsed `content` and ran any real <script> it found; if the
    // malicious text had become live, window.__pwned would be set above).
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var labelEl = scratch.querySelector('#Label652a');
    expect(labelEl).not.toBeNull();
    // The placeholder was restored back to '$' (round-trip), and the text
    // carries no live <script> tag — it is plain text content of the div.
    expect(labelEl.querySelector('script')).toBeNull();
    expect(labelEl.textContent.indexOf(PLACEHOLDER)).toBe(-1);
    // Tightened (requires the $$SearchPanel$$ replacer-FUNCTION fix): the
    // neutralized label must survive as EXACTLY the inert original text — no
    // '$$'->'$' collapse or '$&'-style mangling from the template being fed
    // as a replacement STRING. Against the old string-replacement line the
    // '$$' pairs here would each collapse to a single '$'
    // ("$script$window.__pwned=1$#script$"), failing this assertion.
    expect(labelEl.textContent).toBe('$$script$$window.__pwned=1$$#script$$');
  });

  test('2. FIXED legit script with jQuery \'$\': the script executes after OpenDialog2, and the restored \'$\' is the REAL jQuery mock (proves \\uE000 -> \'$\' round-trip, not a broken/undefined identifier)', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    // The developer-authored <script> body legitimately contains jQuery's
    // '$'. SelectorTagHelper.cs escapes it to the placeholder BEFORE
    // tokenizing the surrounding <script>/</script> to $$script$$/$$#script$$
    // — this is exactly what the fixed C# emitter produces.
    var scriptBody = 'window.__ok=(function(){return ' + PLACEHOLDER + ' ? 1 : 2;})()';
    makeOpenDialog2TempEl(
      'Temp652b',
      '$$script$$' + scriptBody + '$$#script$$<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w652b', 'Title', 500, 400, '#Temp652b');

    // If the placeholder were NOT restored to '$', the executed script body
    // would reference the raw U+E000 character as a bare identifier — not a
    // valid JS identifier start — causing a SyntaxError that the mock's
    // per-script try/catch swallows, leaving window.__ok undefined. Getting
    // 1 back proves '$' was restored to the REAL jQuery mock (a function,
    // hence truthy) before this script executed.
    expect(window.__ok).toBe(1);
  });

  test("3. FIXED: a legit '$'-heavy searcher label round-trips LOSSLESSLY (requires the $$SearchPanel$$ replacer-function fix; FAILS against the old string-replacement line)", () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    // An ordinary, entirely benign searcher label that just happens to be
    // '$'-heavy in every way String.prototype.replace's string-arg special
    // patterns would mangle: '$$' pairs (would collapse to '$'), '$&' (would
    // re-insert the matched '$$SearchPanel$$' marker), a lone '$5', and a
    // trailing '$'. Post-#652 the C# emitter escapes every '$' to the
    // placeholder, so the fixture is the fully-escaped form.
    var label = 'Save $$10$$ on $5 items (deal $&)';
    var escaped = fixedEscapedText(label);
    expect(escaped.indexOf('$')).toBe(-1); // sanity: fully escaped, as C# emits
    makeOpenDialog2TempEl(
      'Temp652e',
      '<span id="Label652e">' + escaped + '</span><div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w652e', 'Title', 500, 400, '#Temp652e');

    var content = layui.layer.open.mock.calls[0][0].content;
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var labelEl = scratch.querySelector('#Label652e');
    expect(labelEl).not.toBeNull();
    // Lossless: the rendered text equals the EXACT original label — no
    // '$$'->'$' collapse, no '$&' marker re-insertion, no leftover
    // placeholder. Against the old `.replace('$$SearchPanel$$', template)`
    // string form this would be corrupted to
    // "Save $10$ on $5 items (deal $$SearchPanel$$)".
    expect(labelEl.textContent).toBe('Save $$10$$ on $5 items (deal $&)');
    expect(content.indexOf(PLACEHOLDER)).toBe(-1);
  });

  test('4. mutation-verify: WITHOUT the \\uE000 escape (the pre-#652 vulnerable shape), the IDENTICAL plaintext payload breaks out and DOES execute — proves this harness actually detects the vulnerability', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    // Pre-fix shape: the raw plaintext label with LITERAL '$' characters —
    // exactly what WebUtility.HtmlEncode left behind before the #652 fix
    // (HtmlEncode never touches '$').
    makeOpenDialog2TempEl(
      'Temp652c',
      '<div id="Label652c">' + MALICIOUS_TEXT + '</div><div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w652c', 'Title', 500, 400, '#Temp652c');

    // OpenDialog2's blanket $$script$$/$$#script$$ un-tokenize replace
    // rehydrated the plaintext into a live <script> that executed.
    expect(window.__pwned).toBe(1);
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).toContain('<script>window.__pwned=1</script>');
  });
});

describe('#652 ff.OpenDialog2 — kill-switch ON (DisableLegacyScriptRehydration) path also restores the placeholder safely', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwned;
    delete window.__killswitchScriptRan;
  });

  test('5. kill-switch ON: a real legacy <script> segment is stripped (unexecuted), AND the surviving grid/table markup has its \\uE000 restored to \'$\' with no leftover placeholder characters', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    // A genuine developer <script> (tokenized the normal way) — under the
    // kill-switch this whole segment must be stripped, never executed.
    var legacyScript = '$$script$$window.__killswitchScriptRan=1;$$#script$$';
    // Surviving (non-script) grid/table markup carrying an escaped '$' —
    // e.g. a price label rendered by a searcher field inside the panel.
    var priceLabel = '<div id="Price652">Price: ' + PLACEHOLDER + '5</div>';
    makeOpenDialog2TempEl(
      'Temp652d',
      legacyScript + priceLabel + '<div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;
    ff.OpenDialog2('/some/search/url', 'w652d', 'Title', 500, 400, '#Temp652d');

    // The legacy script segment was stripped, not executed.
    expect(window.__killswitchScriptRan).toBeUndefined();
    var content = layui.layer.open.mock.calls[0][0].content;
    expect(content).not.toContain('<script>window.__killswitchScriptRan');
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('legacy inline script(s) in the selector search-panel template were NOT executed')
    );

    // The surviving price label's placeholder was restored to a real '$',
    // and no U+E000 character leaks into the final composed content.
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var priceEl = scratch.querySelector('#Price652');
    expect(priceEl).not.toBeNull();
    expect(priceEl.textContent).toBe('Price: $5');
    expect(content.indexOf(PLACEHOLDER)).toBe(-1);
  });
});

// ---------------------------------------------------------------------------
// #652 nested-selector composition — KNOWN, TRACKED residual (#655), NOT a fix
// this suite claims. When <wt:selector> B is nested inside outer <wt:selector>
// A's <wt:searchpanel>, B's whole output (B's #TempB text/template AND B's
// click-handler <script>) folds into A's search-panel `content`. The U+E000
// escape (SelectorTagHelper) + the OpenDialog2 restore are a matched pair only
// WITHIN ONE selector level; on the DEFAULT (kill-switch OFF) path, opening A
// runs a GLOBAL restore that reverses B's inner escape, re-arming a $$script$$
// sentinel from an attacker-stored label inside TempB, which B's OWN later
// ff.OpenDialog2('#TempB') pass then rehydrates into a live <script>.
//
// Per the repo owner's decision, this default-path residual is accepted as a
// KNOWN LIMITATION tracked in issue #655: nesting a selector inside another
// selector's search panel is an unused/exotic composition (0 demos), and the
// real remedy is the #470 string-sentinel-scheme retirement — not a regex that
// chases nesting depth (which itself carried a dev-`text/template` regression
// and a ReDoS concern). This suite therefore covers (a) the SINGLE-LEVEL fix,
// which IS the actual #652 (tests 1-5 + the $$dialoginit$$ forgery test), and
// (b) the #655 MITIGATION below: the #627 kill-switch strips legacy script
// segments at EVERY level, so even the nested re-armed sentinel never executes.
// We deliberately do NOT assert the default-path XSS fires (no live-vuln test).
describe('#652/#655 ff.OpenDialog2 — NESTED selector kill-switch-ON mitigation', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwned;
  });

  // Builds outer selector A's #TempA text/template body EXACTLY as
  // SelectorTagHelper emits it for a nested B.
  function buildTempABody() {
    // B already escaped the malicious label's '$' to U+E000 -> TempB body is
    // all-U+E000 ($$ pairs become PLACEHOLDER-PLACEHOLDER), fully inert.
    var innerMaliciousAllPua = MALICIOUS_TEXT.split('$').join(PLACEHOLDER);
    // B's click-handler <script>, tokenized by A to $$script$$/$$#script$$,
    // with its jQuery '$' escaped by A to U+E000.
    var bHandler =
      '$$script$$var Bfilter={};' + PLACEHOLDER +
      "('#B_Select').on('click',function(){ff.OpenDialog2('/some/url','wB',null,500,400,'#TempB');});" +
      '$$#script$$';
    // B's own #TempB text/template: attribute-bearing OPEN tag stays literal;
    // its </script> was tokenized by A to $$#script$$.
    var tempBSegment =
      '<script type="text/template" id="TempB">' + innerMaliciousAllPua + '$$#script$$';
    return bHandler + tempBSegment;
  }

  test('NESTED selector (kill-switch ON): legacy script segments are STRIPPED at every level, so a nested re-armed sentinel never executes (#655 mitigation)', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    // #TempA is a REAL text/template script (see makeScriptTemplateEl) so its
    // nested TempB is inert raw text — getElementById('TempB') resolves nothing
    // until A's dialog content is composed and inserted below.
    makeScriptTemplateEl('TempA', buildTempABody());
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());
    var warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    ff.DisableLegacyScriptRehydration = true;

    // ── Stage 1: open outer selector A ──────────────────────────────────────
    // The kill-switch strips B's $$script$$ handler segment (warn fires). The
    // global restore still runs on the survivors — re-arming the sentinel INSIDE
    // TempB's inert text/template body — but that body is not executed here.
    ff.OpenDialog2('/some/url', 'wA', 'A', 500, 400, '#TempA');
    expect(window.__pwned).toBeUndefined();

    // The nested #TempB is now a live DOM node in A's inserted content.
    var tempB = document.getElementById('TempB');
    expect(tempB).not.toBeNull();

    // ── Stage 2: drill into inner selector B ────────────────────────────────
    // #TempB's body carries the re-armed $$script$$...$$#script$$ sentinel, but
    // the kill-switch STRIPS it here too (warn fires again) rather than
    // rehydrating it into a live <script> — so nothing executes at any level.
    ff.OpenDialog2('/some/url', 'wB', 'B', 500, 400, '#TempB');

    // #655 mitigation guarantee: no attacker script ran at EITHER level.
    expect(window.__pwned).toBeUndefined();

    // The strip warning fired (proving the kill-switch actually neutralized the
    // segments rather than the flow silently short-circuiting).
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('legacy inline script(s) in the selector search-panel template were NOT executed')
    );
  });
});

// ---------------------------------------------------------------------------
// #652 (LOW, defense-in-depth): the blanket '$'-escape ALSO neutralizes the
// OTHER forgeable sentinel class — a plaintext
// $$dialoginit$${...json...}$$#dialoginit$$ label. ff.OpenDialog2 globally
// turns $$dialoginit$$/$$#dialoginit$$ into a live wtm-dialog-init island that
// gets JSON.parsed and dispatched — so a stored label carrying that literal
// token text would forge an island just like the $$script$$ case. This test
// pins that the escape covers dialoginit too: it MUST fail if the escape were
// ever narrowed to $$script$$-only.
describe('#652 ff.OpenDialog2 — plaintext $$dialoginit$$ island forgery is also neutralized', () => {
  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
    delete window.__pwnedIsland;
  });

  test('FIXED (\\uE000-escaped) plaintext $$dialoginit$$ label: no live island is dispatched, and it renders as inert text (kill-switch OFF)', () => {
    var $ = makeJQueryMock({ Data: [] });
    var layui = makeOpenDialog2LayuiWithDomInsertion();
    // A stored label forging a wtm-dialog-init island: if rehydrated live, its
    // JSON would be parsed + dispatched by ff.ConsumeIslandsIn. As plaintext,
    // SelectorTagHelper escapes every '$' to U+E000 -> the fixture is the fully
    // escaped form (matching what the C# emitter produces).
    var maliciousIslandLabel =
      '$$dialoginit$${"actions":[{"type":"eval","code":"window.__pwnedIsland=1"}]}$$#dialoginit$$';
    var escaped = maliciousIslandLabel.split('$').join(PLACEHOLDER);
    expect(escaped.indexOf('$')).toBe(-1); // sanity: fully escaped, as C# emits
    makeOpenDialog2TempEl(
      'Temp652isl',
      '<span id="Label652isl">' + escaped + '</span><div>panel</div>'
    );
    var { ff } = loadFreshFf(layui, $, makePassthroughDomPurify());

    ff.OpenDialog2('/some/search/url', 'w652isl', 'Title', 500, 400, '#Temp652isl');

    // No island was dispatched from the forged label (nothing executed).
    expect(window.__pwnedIsland).toBeUndefined();

    var content = layui.layer.open.mock.calls[0][0].content;
    // The forged island never became a live wtm-dialog-init <script> element.
    expect(content).not.toContain('class="wtm-dialog-init"');
    expect(content).not.toContain('<script type="application/json"');

    // The label rendered as INERT TEXT: placeholder restored to '$', exact
    // original string preserved, no live island node inside it.
    var scratch = document.createElement('div');
    scratch.innerHTML = content;
    var labelEl = scratch.querySelector('#Label652isl');
    expect(labelEl).not.toBeNull();
    expect(labelEl.querySelector('script')).toBeNull();
    expect(labelEl.textContent).toBe(maliciousIslandLabel);
    expect(content.indexOf(PLACEHOLDER)).toBe(-1);
  });
});
