// Tests for Issue #649 (second pre-release Codex adversarial review): #646
// restored CheckBoxTagHelper.cs / RadioTagHelper.cs's synchronous inline
//   {Id}defaultvalues = [...];
// <script> write, but KEPT #632's back-compat-only 'fieldDefaults'
// wtm-dialog-init JSON island alongside it. On a default full-page load both
// ended up publishing window[id + 'defaultvalues']:
//   1. the inline <script> runs first, at HTML-parse time, setting the
//      global to the SERVER values;
//   2. app-authored JS running after the widget markup may legitimately
//      read AND MUTATE that array;
//   3. at DOMContentLoaded, the island's 'fieldDefaults' DispatchAction case
//      unconditionally RE-ASSIGNED the global back to the ORIGINAL server
//      values — silently clobbering step 2's mutation.
// #649's fix removes the island emission (step 3's publisher) entirely from
// both TagHelpers; the inline write (step 1) is now the SOLE publisher, so
// there is nothing left to run at DOMContentLoaded that could ever overwrite
// an app's mutation. See CheckBoxTagHelper.cs / RadioTagHelper.cs and
// framework_layui.js's 'fieldDefaults' DispatchAction case (retained as
// documented, unreachable-from-framework-markup back-compat infrastructure)
// for the source-side fix and rationale.
//
// This file proves the fix end-to-end against the REAL DispatchAction
// mechanism:
//   1. simulates the widget's own restored inline write (#646) setting the
//      global to the server values — the OBSERVABLE EFFECT of that
//      synchronous, parse-time <script> execution (that execution itself,
//      and its exact synchronous timing, is already proven directly against
//      real <script> elements by
//      framework_layui_646_sync_defaultvalues.test.js Part 2; this file's
//      job is the DispatchAction-side clobber, not the script-timing side);
//   2. simulates app-authored JS mutating that global afterward;
//   3. builds the widget's post-#649 markup (data-wtm-defaults attribute,
//      NO fieldDefaults island) into a REAL, shared jsdom document;
//   4. runs the REAL ff._consumePageReadyIslands() — the exact function a
//      real page runs at DOMContentLoaded — over that document;
//   5. asserts the app's mutation from step 2 survived untouched.
//
// A freshly vm-loaded `ff` is used, with `window` bound SELF-REFERENTIALLY
// to the vm context itself (ctx.window = ctx) — the same convention
// framework_layui_632_fielddefaults_markup.test.js's loadFreshFfWithDom and
// framework_layui_587_scoped_consumer.test.js's loadFreshFf both use. This
// is REQUIRED, not stylistic: ff._consumePageReadyIslands (and everything it
// calls — ff._normalizeIslandPayload, ff._dispatchIslandWhenReady,
// ff.DispatchAction, and the 'fieldDefaults' case's own
// window[action.id+'defaultvalues'] write) all reference `ff`/`window` as
// BARE identifiers internally. A bare identifier inside a script run via
// vm.Script#runInContext resolves against THAT script's own vm context
// object, never the caller's scope — so unless `window` (and therefore
// `ff`, hung off `window.ff`) is a property of the context object ITSELF,
// those bare references throw "ff is not defined" / "window is not
// defined", silently swallowed by DispatchAction's surrounding try/catch as
// a console.warn (verified empirically: the setup.js-provided GLOBAL `ff`
// — whose vm context passes `window: global`, NOT `window: <the context
// object itself>` — hits exactly this failure mode the moment any of its
// methods cross-reference `ff` internally; every existing test in this
// suite that needs REAL dispatched effects therefore loads its own
// self-referential `ff` instance rather than relying on the global one).
// Because real <script>-tag execution always runs in the DOCUMENT's true
// owner realm and can never be redirected into an isolated vm context, this
// file simulates the inline write / app mutation as direct property
// assignments on the SAME ctx the dispatch runs in (ctx[id+'defaultvalues']
// = ...) — mirroring framework_layui_632_fielddefaults_markup.test.js's own
// direct ff._readFieldDefaults unit-coverage tests, which use the identical
// "assign the global directly, then exercise the real function" technique.

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

// Mirrors framework_layui_632_fielddefaults_markup.test.js's
// loadFreshFfWithDom / framework_layui_587_scoped_consumer.test.js's
// loadFreshFf: self-referential window (bare `ff`/`window` cross-references
// inside the loaded module resolve correctly), shared REAL `document` (DOM
// queries see whatever this test builds).
function loadFreshFfWithDom(layui) {
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jqueryMock,
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return ctx;
}

// Builds the markup CheckBoxTagHelper.cs / RadioTagHelper.cs actually emit
// post-#649: a div carrying data-wtm-defaults, with NO fieldDefaults
// wtm-dialog-init island anywhere alongside it.
function buildWidgetDom(id) {
  const div = document.createElement('div');
  div.id = id;
  div.setAttribute('wtm-ctype', 'checkbox');
  div.setAttribute('wtm-name', id);
  div.setAttribute('data-wtm-defaults', '["server"]');
  document.body.appendChild(div);
  return div;
}

describe('#649 clobber regression — post-fix widget markup never lets a deferred island overwrite an app mutation', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  test('no fieldDefaults island in the widget markup: a real ff._consumePageReadyIslands() pass never overwrites a mutation app-authored JS made after the inline write', () => {
    const layui = { form: { render: jest.fn() }, transfer: { reload: jest.fn() }, layer: { alert: jest.fn() } };
    const ctx = loadFreshFfWithDom(layui);
    const id = 'Chk649Clobber';

    // (1) The widget's own markup — no fieldDefaults island (the #649 fix).
    buildWidgetDom(id);

    // (2) The OBSERVABLE EFFECT of the widget's own restored inline
    // "{Id}defaultvalues = [...];" write (#646): the global holds the
    // SERVER values. (The write's real synchronous <script>-execution
    // timing is proven directly by
    // framework_layui_646_sync_defaultvalues.test.js Part 2.)
    ctx[id + 'defaultvalues'] = ['server'];
    expect(ctx[id + 'defaultvalues']).toEqual(['server']);

    // (3) App-authored JS runs after the widget markup and mutates the
    // global — the exact scenario #649 found being silently clobbered.
    ctx[id + 'defaultvalues'].push('app');
    expect(ctx[id + 'defaultvalues']).toEqual(['server', 'app']);

    // (4) Run the REAL page-ready island consumption pass (the
    // DOMContentLoaded moment) over the live, shared document. No
    // fieldDefaults island exists anywhere in it, so there is nothing to
    // dispatch and the app's mutation must survive untouched.
    ctx.ff._consumePageReadyIslands();

    expect(ctx[id + 'defaultvalues']).toEqual(['server', 'app']);
  });
});
