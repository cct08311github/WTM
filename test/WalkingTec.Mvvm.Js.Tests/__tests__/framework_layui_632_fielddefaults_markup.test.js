// Tests for Issue #632 (redesigned — "data as markup, not script"):
// CheckBoxTagHelper/RadioTagHelper's per-widget
//   {Id}defaultvalues = [...];
// inline <script> was originally replaced by TWO things:
//   1. A data-wtm-defaults="[...]" HTML attribute on the control's own div —
//      the AUTHORITATIVE source ff.ChainChange now reads via the new
//      ff._readFieldDefaults(target, comboid) helper (framework_layui.js).
//   2. A back-compat-only 'fieldDefaults' wtm-dialog-init JSON island that
//      publishes window[id + 'defaultvalues'] for app-authored JS — the
//      FRAMEWORK itself never reads this island's output.
//
// REJECTED design (documented so the regression this file guards against is
// explicit): an earlier draft made the fieldDefaults ISLAND itself the
// authoritative source. Review proved a real race: the island write happens
// at DOMContentLoaded (ff._consumePageReadyIslands), while the only consumer
// (ff.ChainChange) can be triggered by a SOURCE combo/tree widget's own
// inline script via setTimeout(fn, 100) anchored at that source widget's
// PARSE point — on a large form, the 100ms timer can fire before the whole
// document finishes parsing (DOMContentLoaded), so the reader would run
// before the writer. This file's "race regression" block proves the
// ACCEPTED design does not have that problem: the attribute is present at
// HTML-parse time, before any script (including this one) has even started
// running, so there is no dispatch to race in the first place.
//
// Issue #649 UPDATE (second pre-release Codex adversarial review): #646
// restored the legacy inline "{Id}defaultvalues = [...];" write ALONGSIDE
// item 2 above (kept for a different back-compat reason — synchronous,
// parse-time global publication). Keeping BOTH publishers turned out to be a
// bug of its own: on a default full-page load, the island's DOMContentLoaded
// dispatch unconditionally re-published the ORIGINAL server values over
// whatever an app had mutated the global to in between — see
// framework_layui_649_clobber_regression.test.js. CheckBoxTagHelper.cs /
// RadioTagHelper.cs no longer emit item 2 at all; the tests in THIS file that
// exercise the 'fieldDefaults' DispatchAction case below do so against
// HAND-CRAFTED island fixtures — they prove the dispatch mechanism itself
// still works correctly (id validation, values coercion, Unicode ids) for the
// case where an island of this shape exists (e.g. app-authored), NOT that any
// framework TagHelper still emits one. The case is retained in
// framework_layui.js as documented, unreachable-from-framework-markup
// back-compat infrastructure — see its own comment for the full #649
// rationale.

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
describe('#632 (redesigned) — source sweep', () => {
  test('ff._readFieldDefaults exists and reads data-wtm-defaults first', () => {
    expect(active).toMatch(/_readFieldDefaults\s*:\s*function\s*\(\s*target\s*,\s*comboid\s*\)/);
    const block = active.match(/_readFieldDefaults\s*:\s*function[\s\S]{0,900}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/target\.attr\(\s*['"]data-wtm-defaults['"]\s*\)/);
    expect(block[0]).toMatch(/JSON\.parse\(\s*raw\s*\)/);
    // Legacy-global fallback (serves un-migrated combo/tree — #632 is scoped
    // to CheckBoxTagHelper/RadioTagHelper only).
    expect(block[0]).toMatch(/window\[\s*comboid\s*\+\s*['"]defaultvalues['"]\s*\]/);
    // #638 guarantee: always returns an array.
    expect(block[0]).toMatch(/return\s*\[\]/);
  });

  test('ChainChange checkbox/radio/combo/tree read sites all call ff._readFieldDefaults', () => {
    const chainChangeBlock = active.match(/ChainChange\s*:\s*function[\s\S]*?\n {4}\},/);
    expect(chainChangeBlock).not.toBeNull();
    const occurrences = (chainChangeBlock[0].match(/ff\._readFieldDefaults\(/g) || []).length;
    expect(occurrences).toBe(4);
    // The direct window[] read must be gone from ChainChange's body.
    expect(chainChangeBlock[0]).not.toMatch(/window\[\s*comboid\s*\+\s*['"]defaultvalues['"]\s*\]/);
  });

  test('DispatchAction switch contains a fieldDefaults case', () => {
    expect(active).toMatch(/case\s+['"]fieldDefaults['"]/);
  });

  function fieldDefaultsBlock() {
    const block = active.match(/case\s+['"]fieldDefaults['"]:[\s\S]{0,600}?\n\s*default:/);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('fieldDefaults case writes window[action.id + "defaultvalues"] behind the fixed suffix', () => {
    const block = fieldDefaultsBlock();
    expect(block).toMatch(/window\[\s*action\.id\s*\+\s*['"]defaultvalues['"]\s*\]\s*=/);
  });

  test('fieldDefaults case validates action.id with a plain type+non-empty check, NOT the bindSubmit ASCII identifier grammar', () => {
    const block = fieldDefaultsBlock();
    expect(block).toMatch(/action\.id\s*&&\s*typeof\s+action\.id\s*===\s*['"]string['"]/);
    // eslint-disable-next-line no-useless-escape
    expect(block).not.toMatch(/\/\^\[A-Za-z_\$\]\[\\w\$\]\*\$\//);
  });

  test('fieldDefaults case coerces non-array action.values to []', () => {
    const block = fieldDefaultsBlock();
    expect(block).toMatch(/Array\.isArray\(\s*action\.values\s*\)/);
  });

  test('_islandModulesFor does not gain a module entry for fieldDefaults (no layui dependency)', () => {
    // Issue #470 Slice L: bumped the capture cap (2000 -> 2500) to fit the
    // 'upload'/'multiUpload' additions to _islandModulesFor — same rationale
    // as #470 Slice K's own bump of this class of hardcoded regex cap.
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,2500}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).not.toMatch(/a\.type\s*===\s*['"]fieldDefaults['"]/);
  });

  test('bindSubmit/bindValidate/bindInput window[name] guards are untouched (still the strict ASCII grammar)', () => {
    // Issue #632 must not weaken the FUNCTION-resolving guard — only add a
    // separate, narrower check for the DATA-writing fieldDefaults action.
    const guard = active.match(/_resolveGuardedWindowFn\s*:\s*function[\s\S]{0,600}?\n\s*\},/);
    expect(guard).not.toBeNull();
    // eslint-disable-next-line no-useless-escape
    expect(guard[0]).toMatch(/\/\^\[A-Za-z_\$\]\[\\w\$\]\*\$\//);
  });

  test('active-code eval( count is still exactly 1 after #632 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('raw eval( token count is still exactly 4 after #632 changes', () => {
    const matches = src.match(/eval\(/g) || [];
    expect(matches).toHaveLength(4);
  });
});

// ---------------------------------------------------------------------------
// ff._readFieldDefaults — direct unit coverage (no DOM/vm needed: works on
// any object exposing .attr(), matching jQuery's contract)
// ---------------------------------------------------------------------------
function loadFreshFf(windowObj, extra) {
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext(Object.assign({
    window: windowObj,
    document: (extra && extra.document) || { querySelectorAll: function () { return []; }, readyState: 'complete' },
    console,
    $: jqueryMock,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  }, extra || {}));
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx;
}

describe('#632 ff._readFieldDefaults — direct unit coverage', () => {
  test('attribute present and valid JSON array -> parsed array wins (authoritative)', () => {
    const ctx = loadFreshFf({});
    ctx.MyFielddefaultvalues = ['legacy-should-be-ignored'];
    const target = { attr: function (name) { return name === 'data-wtm-defaults' ? '["a","b"]' : undefined; } };
    expect(ctx.ff._readFieldDefaults(target, 'MyField')).toEqual(['a', 'b']);
  });

  test('attribute absent, legacy global present -> legacy global wins (combo/tree fallback, unmigrated by #632)', () => {
    const ctx = loadFreshFf({});
    ctx.MyFielddefaultvalues = ['x', 'y'];
    const target = { attr: function () { return undefined; } };
    expect(ctx.ff._readFieldDefaults(target, 'MyField')).toEqual(['x', 'y']);
  });

  test('both absent -> [] (subsumes the #638 null guard)', () => {
    const ctx = loadFreshFf({});
    const target = { attr: function () { return undefined; } };
    expect(ctx.ff._readFieldDefaults(target, 'MyField')).toEqual([]);
    expect(function () { ctx.ff._readFieldDefaults(target, 'MyField').indexOf('x'); }).not.toThrow();
  });

  test('malformed attribute JSON -> falls through to legacy global, never throws', () => {
    const ctx = loadFreshFf({});
    ctx.MyFielddefaultvalues = ['fallback'];
    const target = { attr: function () { return '{not valid json'; } };
    expect(function () { ctx.ff._readFieldDefaults(target, 'MyField'); }).not.toThrow();
    expect(ctx.ff._readFieldDefaults(target, 'MyField')).toEqual(['fallback']);
  });

  test('attribute present but not a JSON array (e.g. an object) -> falls through to legacy/[]', () => {
    const ctx = loadFreshFf({});
    const target = { attr: function () { return '{"not":"an array"}'; } };
    expect(ctx.ff._readFieldDefaults(target, 'MyField')).toEqual([]);
  });

  test('no target / target.attr not a function -> [] , never throws', () => {
    const ctx = loadFreshFf({});
    expect(function () { ctx.ff._readFieldDefaults(null, 'MyField'); }).not.toThrow();
    expect(ctx.ff._readFieldDefaults(null, 'MyField')).toEqual([]);
    expect(ctx.ff._readFieldDefaults({}, 'MyField')).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// Real ff.ChainChange end-to-end — proves the framework never needs the
// window[]defaultvalues global for checkbox/radio anymore
// ---------------------------------------------------------------------------
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

// Builds the markup CheckBoxTagHelper/RadioTagHelper actually emit for a
// ChainChange TARGET: a div carrying id/wtm-ctype/wtm-name/lay-filter AND
// data-wtm-defaults — optionally followed by the back-compat fieldDefaults
// island <script>, matching real emitted output byte-for-byte in shape
// (islandHtml is omitted entirely in the "race regression" tests below, to
// prove the attribute needs no island at all).
function buildChainDom(controltype, defaultsJson, islandHtml) {
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
  if (defaultsJson !== undefined) {
    target.setAttribute('data-wtm-defaults', defaultsJson);
  }
  form.appendChild(source);
  form.appendChild(target);
  if (islandHtml) {
    const wrapper = document.createElement('div');
    wrapper.innerHTML = islandHtml;
    form.appendChild(wrapper.firstChild);
  }
  document.body.appendChild(form);
  return { form, source, target };
}

function loadFreshFfWithDom(layui, $) {
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

describe.each(['checkbox', 'radio'])(
  '#632 ff.ChainChange(usedefaultvalue=true) — %s applies markup-sourced defaults with the legacy global NEVER set',
  (controltype) => {
    afterEach(() => { document.body.innerHTML = ''; });

    test('correct defaults applied purely from data-wtm-defaults; window[id+"defaultvalues"] stays undefined throughout', () => {
      const ajaxData = { Data: [
        { Value: 'a', Text: 'Alpha', Selected: false },
        { Value: 'b', Text: 'Beta', Selected: false }
      ] };
      const $ = makeJQueryMock(ajaxData);
      const formRender = jest.fn();
      const layui = { form: { render: formRender }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
      // NOTE: no fieldDefaults island in this DOM at all — proves ChainChange
      // needs no island, dispatched or not.
      const { source } = buildChainDom(controltype, '["b"]');
      const ctx = loadFreshFfWithDom(layui, $);

      expect(ctx.TargetFielddefaultvalues).toBeUndefined();

      ctx.ff.ChainChange('/Home/GetItems', source, true);

      expect(ctx.TargetFielddefaultvalues).toBeUndefined(); // still never touched

      const target = document.getElementById('TargetField');
      const inputs = target.querySelectorAll('input[type="' + controltype + '"]');
      expect(inputs.length).toBe(2);
      expect(inputs[0].checked).toBe(false); // 'a' not in ["b"]
      expect(inputs[1].checked).toBe(true);  // 'b' in ["b"]
      expect(formRender).toHaveBeenCalledWith(controltype, 'TargetFieldfilterdiv');
    });
  }
);

// ---------------------------------------------------------------------------
// Race regression — the scenario the REJECTED island-as-authoritative design
// would fail. See the file-header comment for the full explanation.
// ---------------------------------------------------------------------------
describe.each(['checkbox', 'radio'])(
  '#632 race regression — %s: reader runs with NO island ever having existed/dispatched',
  (controltype) => {
    afterEach(() => { document.body.innerHTML = ''; });

    test('defaults still apply correctly (this is the exact case the rejected design could not guarantee)', () => {
      const ajaxData = { Data: [{ Value: 'x', Text: 'X', Selected: false }] };
      const $ = makeJQueryMock(ajaxData);
      const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
      // Build the DOM with ONLY the attribute (no fieldDefaults island markup
      // present at all — simulates "before/without any island dispatch",
      // which is the reachable state the rejected design raced on: a large
      // form where DOMContentLoaded (island dispatch time) has not yet fired
      // when a chained SOURCE widget's own setTimeout(..., 100) already
      // triggered ChainChange).
      const { source } = buildChainDom(controltype, '["x"]', null);
      const ctx = loadFreshFfWithDom(layui, $);

      expect(function () {
        ctx.ff.ChainChange('/Home/GetItems', source, true);
      }).not.toThrow();

      const target = document.getElementById('TargetField');
      const input = target.querySelector('input[type="' + controltype + '"]');
      expect(input.checked).toBe(true); // correct default applied, no island needed
    });
  }
);

// ---------------------------------------------------------------------------
// Full-page ordering + back-compat global publication
// ---------------------------------------------------------------------------
describe('#632 full-page ordering — attribute readable before island dispatch; back-compat global still ends up published', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('attribute is synchronously readable the instant the DOM exists, before ff is even loaded (i.e. before any island machinery exists)', () => {
    // No vm / no ff at all yet — proves the datum itself has no script
    // dependency: it is plain, parsed HTML.
    const { target } = buildChainDom('checkbox', '["a","b"]', null);
    expect(target.getAttribute('data-wtm-defaults')).toBe('["a","b"]');
  });

  // Issue #649: no CheckBoxTagHelper/RadioTagHelper output can produce this
  // island anymore (see the file-header update above) — this fixture is
  // HAND-CRAFTED to prove the DispatchAction case itself is still functional,
  // documented back-compat infrastructure (framework_layui.js retains it as a
  // no-op no longer reachable from any framework-generated markup), not that
  // any emitter still uses it.
  test('the fieldDefaults island (when present, e.g. hand-authored) still dispatches at ordinary page-ready time and publishes the back-compat global', () => {
    const islandHtml = '<script type="application/json" class="wtm-dialog-init">'
      + JSON.stringify({ type: 'fieldDefaults', id: 'TargetField', values: ['a', 'b'] })
      + '</script>';
    buildChainDom('checkbox', '["a","b"]', islandHtml);
    // document.readyState in jsdom's default test document is 'complete', so
    // ff._consumePageReadyIslands runs synchronously as soon as ff loads —
    // exactly like a real already-loaded page. No setTimeout / manual trigger
    // needed for this to prove the back-compat path still works end-to-end.
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const $ = makeJQueryMock({ Data: [] });
    const ctx = loadFreshFfWithDom(layui, $);

    expect(ctx.TargetFielddefaultvalues).toEqual(['a', 'b']);
  });
});

// ---------------------------------------------------------------------------
// Unicode id round-trip — the #632 finding: Utils.GetIdByName only strips
// '.'/'['/']'/'-', so ids derived from non-ASCII model/property names are
// valid, real ids in production (e.g. the ConsoleDemo's
// 不要用中文模型名_View_模型名 fixture). A strict ASCII identifier grammar
// (the bindSubmit/bindValidate/bindInput one) would silently reject these —
// this design uses a plain type+non-empty check instead (see the source
// sweep above), so bracket-notation window[] writes work regardless of the
// id's character set.
// ---------------------------------------------------------------------------
describe('#632 Unicode id round-trip — fieldDefaults DispatchAction', () => {
  test('a Chinese id (matching the ConsoleDemo TagHelper-derived id shape) round-trips through DispatchAction', () => {
    const ctx = loadFreshFf({});
    const chineseId = '不要用中文模型名_View_模型名';

    ctx.ff.DispatchAction({
      actions: [{ type: 'fieldDefaults', id: chineseId, values: ['Admin', 'Guest'] }]
    });

    expect(ctx[chineseId + 'defaultvalues']).toEqual(['Admin', 'Guest']);
  });

  test('an ASCII id still works identically (no regression for the common case)', () => {
    const ctx = loadFreshFf({});
    ctx.ff.DispatchAction({
      actions: [{ type: 'fieldDefaults', id: 'MyCheckbox', values: ['a'] }]
    });
    expect(ctx.MyCheckboxdefaultvalues).toEqual(['a']);
  });

  test('a rejected-by-the-OLD-ASCII-grammar id like "__proto__" only ever produces the harmless derived property name, never touches the real prototype', () => {
    const ctx = loadFreshFf({});
    ctx.ff.DispatchAction({
      actions: [{ type: 'fieldDefaults', id: '__proto__', values: ['a'] }]
    });
    expect(ctx['__proto__defaultvalues']).toEqual(['a']);
    // The real Object.prototype is untouched — this is a plain own property
    // on the window-equivalent object, not prototype pollution.
    expect(Object.prototype.hasOwnProperty.call(ctx, '__proto__defaultvalues')).toBe(true);
    expect({}.polluted).toBeUndefined();
  });

  test('non-array values are coerced to [] rather than publishing garbage', () => {
    const ctx = loadFreshFf({});
    ctx.ff.DispatchAction({
      actions: [{ type: 'fieldDefaults', id: 'Weird', values: 'not-an-array' }]
    });
    expect(ctx.Weirddefaultvalues).toEqual([]);
  });

  test('missing/empty id performs no write at all', () => {
    const ctx = loadFreshFf({});
    ctx.ff.DispatchAction({ actions: [{ type: 'fieldDefaults', id: '', values: ['a'] }] });
    expect(ctx['defaultvalues']).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// DOMPurify / ff.SafeHtml preserves data-wtm-defaults through dialog-fragment
// sanitization (the attribute must survive OpenDialog's markup pipeline the
// same way it survives full-page parsing).
// ---------------------------------------------------------------------------
describe('#632 ff.SafeHtml preserves data-wtm-defaults (jsdom + vendored DOMPurify)', () => {
  const purifyPath = path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/dompurify.js');
  const purifySource = fs.readFileSync(purifyPath, 'utf8');

  beforeAll(() => {
    const runInWindow = new Function('window', 'document', purifySource);
    runInWindow(global.window || global, global.document || {});
  });

  afterEach(() => { document.body.innerHTML = ''; });

  test('data-wtm-defaults survives ff.SafeHtml sanitization of a dialog partial', () => {
    const html = '<div id="chk1" wtm-ctype="checkbox" wtm-name="Roles" data-wtm-defaults="[&quot;Admin&quot;,&quot;User&quot;]"><input type="checkbox" name="Roles" value="Admin"/></div>';
    const out = ff.SafeHtml(html);
    expect(out).toMatch(/data-wtm-defaults="\[&quot;Admin&quot;,&quot;User&quot;\]"/);
  });

  // Review fix (false-assurance test): the prior version of this test — "a
  // hostile data-wtm-defaults value cannot break out of the attribute" —
  // used the fixture `data-wtm-defaults="x"><script>alert(1)</script>"`,
  // whose hostile payload is a CHILD ELEMENT, not the attribute value ("x" is
  // benign). It only ever passed because DOMPurify's FORBID_TAGS strips
  // <script> — a property already pinned, with the near-identical fixture
  // `<form lay-filter="x"><script>alert(1)</script>"></form>`, by
  // framework_layui_591_attr_allowlist.test.js's "attribute-value escape
  // attempt (unescaped quote breakout) stays inert" test. That coverage is
  // redundant here, so it is dropped rather than kept under a misleading
  // name; the named property (a hostile ATTRIBUTE VALUE stays inert) is
  // exercised for real below.
  //
  // WHERE THE GUARANTEE ACTUALLY LIVES: CheckBoxTagHelper.cs / RadioTagHelper.cs
  // emit this attribute via `output.Attributes.Add("data-wtm-defaults",
  // JsonSerializer.Serialize(values, ...))` — a plain string Add(), never raw
  // HtmlContent — so System.Text.Json's default HTML-safe encoder (escapes
  // '<'/'>'/'&') AND ASP.NET Core's TagHelperOutput.Attributes pipeline
  // (HTML-attribute-encodes '"' as '&quot;', etc.) both run BEFORE the markup
  // ever reaches this JS file. That two-layer C#-SIDE encoding — not
  // DOMPurify — is what makes a value containing '"'/'>'/'<' unable to break
  // out of the attribute. Empirical mutation-verify (see PR/commit body for
  // the full pass/fail record):
  //   - Stubbing ff.SafeHtml to a pure passthrough (DOMPurify bypassed
  //     entirely) on the CORRECTLY-ENCODED fixture below: assertions still
  //     PASS — proves DOMPurify plays no part in the no-breakout guarantee
  //     for a value that arrived already safely encoded.
  //   - Feeding the SAME payload with the C#-side encoding step removed
  //     (naive concatenation) through the REAL ff.SafeHtml/DOMPurify: an
  //     actual <img> element materializes (assertion FAILS) — DOMPurify
  //     cannot "un-parse" a breakout that already happened in the HTML
  //     parser before FORBID_TAGS/FORBID_ATTR ever see the resulting tree.
  // So this test honestly pins what ff.SafeHtml/DOMPurify DOES contribute:
  // given an already-correctly-encoded value, its parse+reserialize round
  // trip does not corrupt it back into live markup (no mutation-XSS), and
  // its FORBID_TAGS/FORBID_ATTR blocklists stay intact as defense-in-depth.
  test('a hostile data-wtm-defaults ATTRIBUTE VALUE, correctly HTML-attribute-encoded (matching the real C# emit pipeline), stays inert through ff.SafeHtml: no element/attribute breakout, and the value round-trips as plain data', () => {
    // The shape ff._readFieldDefaults expects: a JSON array whose one string
    // element IS the would-be breakout payload — contains '"', '>', '<', and
    // 'onerror='.
    const rawJsonArrayValue = '["\\"><img src=x onerror=alert(1)>"]';

    // HTML-attribute-encode it the way ASP.NET Core's HtmlEncoder does when
    // TagHelperOutput.Attributes serializes a plain string Add() value —
    // simulates the real C# emit pipeline's output byte-for-byte in effect.
    const htmlAttrEncode = (s) => s
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
    const html = '<div id="chk2" wtm-ctype="checkbox" data-wtm-defaults="'
      + htmlAttrEncode(rawJsonArrayValue) + '"></div>';

    const out = ff.SafeHtml(html);

    // Parse the SANITIZED OUTPUT into a real DOM — exactly what the
    // framework does next (`$('#'+where).html(ff.SafeHtml(data))`). A
    // string/regex check on `out` would be unreliable here: DOMPurify's
    // serializer has no need to (and does not) re-escape '<'/'>' inside an
    // already double-quoted attribute value, so the literal substrings
    // "<img" and "onerror=" legitimately appear INSIDE the safe, quoted
    // attribute text of `out` even in this fully-safe case — a regex
    // assertion like `not.toMatch(/<img/)` would misreport that as a
    // breakout. Only a real parse (getAttribute) tells the two apart.
    const wrapper = document.createElement('div');
    wrapper.innerHTML = out;

    expect(wrapper.querySelectorAll('img').length).toBe(0);
    expect(wrapper.querySelectorAll('[onerror]').length).toBe(0);

    const div = wrapper.querySelector('div');
    expect(div).not.toBeNull();
    expect(div.getAttribute('data-wtm-defaults')).not.toBeNull();
    // Round-trips byte-for-byte as inert text — the hostile characters never
    // escaped their role as attribute-value content.
    expect(div.getAttribute('data-wtm-defaults')).toBe(rawJsonArrayValue);

    // ff._readFieldDefaults must see this purely as DATA: a one-element
    // array containing the payload as a plain string, never executing
    // anything, and .indexOf must work on both the array and the string
    // without throwing (proves it is plain data, not something exotic).
    const target = { attr: (name) => div.getAttribute(name) };
    const defaults = ff._readFieldDefaults(target, 'chk2');
    expect(Array.isArray(defaults)).toBe(true);
    expect(defaults).toEqual(['"><img src=x onerror=alert(1)>']);
    expect(() => defaults.indexOf('anything')).not.toThrow();
    expect(typeof defaults[0]).toBe('string');
    expect(() => defaults[0].indexOf('onerror')).not.toThrow();
    expect(defaults[0].indexOf('onerror')).toBeGreaterThan(-1);
  });
});
