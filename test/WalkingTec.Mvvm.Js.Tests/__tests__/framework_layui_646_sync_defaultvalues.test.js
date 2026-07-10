// Tests for Issue #646 (Codex adversarial review, pre-10.14.4 release gate):
// #632 replaced CheckBoxTagHelper.cs / RadioTagHelper.cs's per-widget
//   {Id}defaultvalues = [...];
// inline <script> — which executes at HTML-PARSE TIME, the instant the
// browser's parser reaches it, synchronously making
// window[id + 'defaultvalues'] readable by any script that runs after it —
// with ONLY the back-compat-only 'fieldDefaults' wtm-dialog-init JSON
// island, consumed at DOMContentLoaded (ff._consumePageReadyIslands, see
// framework_layui_632_fielddefaults_markup.test.js). #632 explicitly kept
// the global "for app-authored JS back-compat", but that back-compat was
// incomplete: app code reading the global from an inline <script>
// immediately after the widget's own markup — a common integration
// pattern — saw `undefined` until DOMContentLoaded, a real regression vs.
// the pre-#632 synchronous guarantee.
//
// The fix restores the inline <script>{Id}defaultvalues=...} write,
// unconditionally, in PostElement, alongside the data-wtm-defaults attribute
// AND the fieldDefaults island (see CheckBoxTagHelper.cs / RadioTagHelper.cs
// Process() comments for the full C#-side rationale, including why the
// TagHelper — server-side — cannot conditionally omit it based on the
// client-only #627 kill-switch).
//
// This file has two parts:
//   1. A SOURCE SWEEP against the actual .cs files — this is what makes the
//      suite a real regression lock: it fails against #632-as-committed
//      (island-only, no inline write) and passes once the inline write is
//      restored, without needing to invoke C# from Jest.
//   2. A BEHAVIORAL proof, at the JS/DOM level, that the restored write
//      makes window[id + 'defaultvalues'] readable SYNCHRONOUSLY — an
//      app-authored script executed immediately after the widget's own
//      script sees the global already defined, with NO DOMContentLoaded and
//      NO framework code (framework_layui.js is never loaded in this file)
//      involved at all.
//
// Parse-time simulation technique: mirrors the established #627/#332
// OpenDialog rehydration convention (framework_layui_627_legacy_killswitch
// .test.js) for simulating real <script> execution order without
// DOMContentLoaded. This suite's jest-jsdom environment is configured with
// runScripts:'dangerously' (@jest/environment-jsdom-abstract), and
// framework_layui.js's OWN legacy script rehydration uses the identical
//   var se = document.createElement('script'); se.text = code;
//   document.body.appendChild(se);
// idiom (see ff._replayInitFromHtml / ff.OpenDialog in framework_layui.js).
// createElement+appendChild-inserted <script> elements execute synchronously
// and immediately upon insertion (per the HTML5 "prepare a script"
// algorithm) — unlike scripts inserted via innerHTML/document.write, whose
// "already started" flag makes them permanently inert. Appending two such
// scripts back-to-back therefore runs them in the same strict, synchronous,
// no-async-boundary order real parser-encountered <script> tags do, which is
// exactly the guarantee under test.

'use strict';

const fs = require('fs');
const path = require('path');

const checkBoxPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/CheckBoxTagHelper.cs'
);
const radioPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/RadioTagHelper.cs'
);
const checkBoxSrc = fs.readFileSync(checkBoxPath, 'utf8');
const radioSrc = fs.readFileSync(radioPath, 'utf8');

const stripLineComments = (text) =>
  text
    .split('\n')
    .map((line) => {
      const idx = line.indexOf('//');
      return idx === -1 ? line : line.slice(0, idx);
    })
    .join('\n');

const checkBoxActive = stripLineComments(checkBoxSrc);
const radioActive = stripLineComments(radioSrc);

// The exact literal CheckBoxTagHelper.cs / RadioTagHelper.cs must emit, in
// REAL code (not a comment), for the restored inline write — a plain
// JsonSerializer.Serialize(values) call with no options object, matching the
// pre-#632/#638 emission byte-for-byte (spaces around '=', trailing ';').
// The many surrounding comments in both files intentionally use the
// no-spaces "{Id}defaultvalues=...}"" shorthand precisely so they can never
// accidentally satisfy this pattern.
const INLINE_WRITE_PATTERN = /\{Id\}defaultvalues = \{JsonSerializer\.Serialize\(values\)\};/;

// ---------------------------------------------------------------------------
// Part 1: source sweep (the actual regression lock)
// ---------------------------------------------------------------------------
describe('#646 source sweep — CheckBoxTagHelper.cs / RadioTagHelper.cs restore the inline defaultvalues write', () => {
  test('CheckBoxTagHelper.cs emits the inline {Id}defaultvalues write in real code', () => {
    // FAILS against #632-as-committed (380732b5a): that revision's
    // PostElement emission contains only the fieldDefaults island, so this
    // exact code pattern is absent (it survives only inside the file's
    // historical comments, which stripLineComments removes).
    expect(checkBoxActive).toMatch(INLINE_WRITE_PATTERN);
  });

  test('RadioTagHelper.cs emits the inline {Id}defaultvalues write in real code', () => {
    expect(radioActive).toMatch(INLINE_WRITE_PATTERN);
  });

  test('RadioTagHelper.cs emits the write exactly once in code, outside the listItems loop (#638 placement preserved)', () => {
    const codeMatches = radioActive.match(
      /\{Id\}defaultvalues = \{JsonSerializer\.Serialize\(values\)\};/g
    ) || [];
    expect(codeMatches).toHaveLength(1);
  });

  test('both TagHelpers still emit the data-wtm-defaults attribute (framework-race-free ff.ChainChange path, untouched by #646)', () => {
    expect(checkBoxActive).toMatch(/"data-wtm-defaults"/);
    expect(radioActive).toMatch(/"data-wtm-defaults"/);
  });

  test('both TagHelpers still emit the fieldDefaults wtm-dialog-init island (kept: it is how the global still publishes once the #627 kill-switch blocks the inline script)', () => {
    expect(checkBoxActive).toMatch(/FieldDefaultsIslandAction/);
    expect(checkBoxActive).toMatch(/wtm-dialog-init/);
    expect(radioActive).toMatch(/FieldDefaultsIslandAction/);
    expect(radioActive).toMatch(/wtm-dialog-init/);
  });
});

// ---------------------------------------------------------------------------
// Part 2: behavioral proof — the restored write is readable SYNCHRONOUSLY
// ---------------------------------------------------------------------------
function appendScript(code) {
  const script = document.createElement('script');
  script.text = code;
  document.body.appendChild(script);
}

// Reconstructs the inline write's actual JS statement shape (System.Text
// .Json's default encoder produces a plain, double-quoted JSON array
// literal — safe to embed as-is; JSON.stringify here stands in for it,
// matching the same escaping semantics for the ASCII fixture values used
// below).
function widgetInlineDefaultsStatement(id, values) {
  return id + 'defaultvalues = ' + JSON.stringify(values) + ';';
}

describe('#646 — restored inline {Id}defaultvalues write is readable SYNCHRONOUSLY at parse time', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  test('an app-authored script immediately after the widget markup sees the global already defined — no DOMContentLoaded, no framework code loaded at all', () => {
    const id = 'chk646sync';
    const values = ['Admin', 'User'];

    // (1) The widget's own inline write — the exact statement
    // CheckBoxTagHelper.cs's restored PostElement emission produces (proven
    // present, in real code, by the source sweep above).
    appendScript(widgetInlineDefaultsStatement(id, values));

    // (2) App-authored JS that runs IMMEDIATELY after — no DOMContentLoaded,
    // no setTimeout, no microtask boundary — reading the global and
    // recording what it observed for the assertion below. This is the exact
    // scenario #646 found broken under #632 (island-only): under that
    // shape, this read would see `undefined`, because only the island
    // (deferred to DOMContentLoaded) ever publishes the global.
    appendScript(
      'window.__wtm646Observed = (typeof ' + id + 'defaultvalues !== "undefined") ? ' +
        id + 'defaultvalues : undefined;'
    );

    expect(window.__wtm646Observed).toBeDefined();
    expect(window.__wtm646Observed).toEqual(values);

    delete window.__wtm646Observed;
    delete window[id + 'defaultvalues'];
  });

  test('works identically for the RadioTagHelper indentation variant (same statement shape, different PostElement leading whitespace — insignificant to JS)', () => {
    const id = 'radio646sync';
    const values = ['Guest'];

    // RadioTagHelper.cs's restored line is indented under a nested <script>
    // block ("         {Id}defaultvalues = ...") — leading whitespace has
    // no semantic effect on the statement itself.
    appendScript('         ' + widgetInlineDefaultsStatement(id, values));
    appendScript(
      'window.__wtm646ObservedRadio = (typeof ' + id + 'defaultvalues !== "undefined") ? ' +
        id + 'defaultvalues : undefined;'
    );

    expect(window.__wtm646ObservedRadio).toEqual(values);

    delete window.__wtm646ObservedRadio;
    delete window[id + 'defaultvalues'];
  });

  test('contrast: an island-only publication (the #632-as-committed shape) leaves the global undefined for a script that runs immediately after — this is the exact bug #646 fixes', () => {
    const id = 'chk646islandonly';

    // A `type="application/json"` script is never executed by the browser
    // as script — that is the entire point of the island design (JSON.parse
    // via ff.DispatchAction, never eval). Appending one here, with no
    // accompanying bare <script>{id}defaultvalues=...} write, reproduces the
    // #632-as-committed PostElement shape exactly. Only DOMContentLoaded
    // consumption (ff._consumePageReadyIslands, deliberately never invoked
    // in this file) would go on to publish the global — nothing here does.
    const island = document.createElement('script');
    island.type = 'application/json';
    island.className = 'wtm-dialog-init';
    island.text = JSON.stringify({ type: 'fieldDefaults', id, values: ['Admin'] });
    document.body.appendChild(island);

    appendScript(
      'window.__wtm646ObservedIslandOnly = (typeof ' + id + 'defaultvalues !== "undefined") ? ' +
        id + 'defaultvalues : undefined;'
    );

    expect(window.__wtm646ObservedIslandOnly).toBeUndefined();

    delete window.__wtm646ObservedIslandOnly;
  });
});
