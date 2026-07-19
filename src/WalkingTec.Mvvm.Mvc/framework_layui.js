
/*eslint eqeqeq: ["error", "smart"]*/
var DONOTUSE_TABLAYID = undefined;

// ── Mobile responsive: allow horizontal scroll on narrow viewports ──────────
// LayUI sets overflow-x:hidden on .layui-table-box which silently truncates
// columns below 768px.  Inject a scoped override so grids are scrollable on
// mobile without affecting desktop layout.
if (typeof document !== 'undefined' && document.head) {
    var _wtmMobileStyle = document.createElement('style');
    _wtmMobileStyle.id = 'wtm-mobile-table-fix';
    _wtmMobileStyle.textContent =
        '@media (max-width:768px){' +
        '.layui-table-box{overflow-x:auto!important;-webkit-overflow-scrolling:touch}' +
        '}';
    document.head.appendChild(_wtmMobileStyle);
}
// #353: plain helper instead of prototype mutation
function removeByID(arr, id) {
    var index = -1;
    for (var i = 0; i < arr.length; i++) {
        if (arr[i].ID == id.ID) {
            index = i;
            break;
        }
    }
    if (index > -1) {
        arr.splice(index, 1);
    }
}
if (typeof window !== 'undefined') { window.removeByID = removeByID; }

// Issue #558 (#470-C): denylist of dangerous global names that the bindSubmit
// action's beforeSubmit resolver must REFUSE to look up, even if a name somehow
// passes the identifier-regex + own-property + typeof-function guard. This is
// defense-in-depth layered ON TOP of the compile-time-trusted-name invariant
// (beforeSubmit is always a developer-authored Razor literal, never request
// data — see the trust-boundary comment at the bindSubmit case). It exists so
// that even a future wiring mistake that let an attacker-influenced string
// reach beforeSubmit could not turn a plain global identifier like `eval`,
// `Function`, `setTimeout`, or `fetch` into an execution/exfiltration
// primitive: these are all own, callable properties of `window` whose names
// match the identifier regex, so the base guard alone would resolve them. Any
// beforeSubmit whose name is in this set is rejected — the gate is skipped and
// the form submit proceeds without a before-hook (never throws).
var WTM_BEFORESUBMIT_DENYLIST = (typeof Set !== 'undefined')
    ? new Set([
        'eval', 'Function', 'setTimeout', 'setInterval', 'setImmediate',
        'fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource',
        'open', 'postMessage', 'alert', 'confirm', 'prompt',
        'queueMicrotask', 'requestAnimationFrame', 'requestIdleCallback',
        'Worker', 'SharedWorker', 'importScripts', 'structuredClone',
        'Image', 'navigator', 'location', 'document', 'window', 'globalThis',
        'Reflect', 'Proxy'
      ])
    : null;

window.ff = {
    DONOTUSE_Text_LoadFailed: "",
    DONOTUSE_Text_SubmitFailed: "",
    DONOTUSE_Text_PleaseSelect: "",
    DONOTUSE_Text_FailedLoadData: "",
    DONOTUSE_Text_ExportNoData: "",

    // Issue #789 Phase 3B: sanitize AJAX response HTML before inserting into
    // the DOM via jQuery .html() or innerHTML. Delegates to DOMPurify which
    // MUST be loaded before framework_layui.js (see _Layout.cshtml,
    // Login.cshtml script tag order). Fails closed — if DOMPurify is absent
    // the helper returns an empty string so attacker-controlled HTML is
    // dropped rather than rendered.
    //
    // Issue #591: DOMPurify's internal default allowlist only recognizes
    // standard HTML attributes, so it silently strips WTM's own non-standard
    // framework attributes from every dialog partial and PostForm/BgRequest
    // redraw that passes through here — breaking lay-filter scoped re-render,
    // lay-skin styling, form validation (lay-verify/lay-reqtext), combo/tree/
    // chain-change metadata (wtm-*), master-detail grid clearing (subpro),
    // search-panel wiring (IsSearchButton/oldpost/chartlink), and dialog
    // chart resize (ischart), etc. ADD_ATTR restores exactly the attributes
    // WTM TagHelpers are confirmed (by source audit) to emit into markup
    // that flows through ff.SafeHtml — see the #591 PR body for the full
    // emission-site inventory. Attribute VALUES are still run through
    // DOMPurify's normal value/serialization handling, and FORBID_TAGS/
    // FORBID_ATTR below are unchanged, so this does not weaken the
    // script/style/event-handler blocks.
    //
    // Issue #591 (review round): the initial audit swept source for the
    // `lay-`/`wtm-`/`div-for` prefixes only, which missed five non-prefixed
    // framework marker attributes (`subpro`, `IsSearchButton`/`issearchbutton`,
    // `oldpost`, `ischart`, `chartlink`). A follow-up full-source audit added
    // them below — same risk profile as the prefixed entries (plain markers/
    // flags read via getAttribute/.attr, never resolved into a handler name).
    //
    // INVARIANT: this list mirrors what WTM TagHelpers emit today — prefixed
    // AND non-prefixed. Any addition requires adversarial review across the
    // whole codebase, not just a lay-/wtm-/div-for grep. In particular, do
    // NOT add `lay-on` or `lay-event` (layui's name-resolved event-binding
    // attributes): the only current `lay-event` usage (grid row/toolbar
    // buttons) is produced client-side by layui.table's own templating
    // engine from AJAX JSON data and never passes through ff.SafeHtml, so
    // it is correctly left out and stays stripped by DOMPurify's default
    // allowlist. If a future TagHelper needs to emit `lay-event`/`lay-on`
    // into SafeHtml-sanitized markup, first prove that the value can never
    // be attacker-influenced (it would resolve/dispatch a handler by name).
    //
    // Issue #591 (review round) note on `wtm-cf`: its value is a JS function
    // NAME (ComboBoxTagHelper / CheckBoxTagHelper emit
    // FormatFuncName(ChangeFunc, ...)), which makes it shaped like the
    // name-resolved handler attributes excluded above. Verified inert today:
    // no sink reads `wtm-cf` off the DOM and invokes window[value](); in
    // legitimate markup the value is a compile-time, developer-authored
    // Razor literal (same trust class as bindSubmit's beforeSubmit), never
    // request/field data. If a future change wires a window[wtm-cf]()
    // invocation over combo/checkbox elements, it must get the same
    // adversarial review as lay-on/lay-event above.
    //
    // Issue #591 (review round) note on attribute VALUES: DOMPurify's
    // IS_ALLOWED_URI safety net can still drop an allowlisted attribute whose
    // value looks like an unknown URI scheme (`^[A-Za-z+.-]+:`) — e.g. a
    // localized lay-reqtext value like "Password: required" — even though
    // the attribute NAME is in ADD_ATTR. This is inherent DOMPurify behavior
    // (the same mechanism that strips `wtm-turl="javascript:...`) and is not
    // specific to this list.
    SafeHtml: function (rawHtml) {
        if (typeof window.DOMPurify === 'undefined' ||
            !window.DOMPurify ||
            typeof window.DOMPurify.sanitize !== 'function') {
            return '';
        }
        return window.DOMPurify.sanitize(rawHtml || '', {
            FORBID_TAGS: ['script', 'style'],
            FORBID_ATTR: ['onerror', 'onload', 'onclick', 'onmouseover', 'onfocus', 'onblur', 'onchange', 'onsubmit'],
            ADD_ATTR: [
                'lay-filter', 'lay-verify', 'lay-reqtext', 'lay-skin', 'lay-text',
                'lay-submit', 'lay-accordion', 'lay-allowclose', 'lay-height',
                'lay-title', 'lay-ignore', 'lay-percent', 'lay-showpercent',
                'wtm-name', 'wtm-ctype', 'wtm-multi', 'wtm-linkto', 'wtm-cf',
                'wtm-turl', 'div-for',
                // Issue #591 review round: non-prefixed framework marker/flag
                // attributes missed by the initial lay-/wtm-/div-for sweep.
                // subpro: DataTableTagHelper detail-grid clear marker (GetFormData).
                // issearchbutton: SearchPanelTagHelper search button marker
                //   (emitted as `IsSearchButton`; HTML lower-cases attribute
                //   names, so DOMPurify — and jQuery's attr selector, which is
                //   also case-insensitive for attribute names — see `issearchbutton`).
                // oldpost: SearchPanelTagHelper old-post-mode marker (RefreshGrid).
                // ischart: ChartTagHelper dialog-resize / redraw target marker.
                // chartlink: SearchPanelTagHelper chart-linked searcher marker (RefreshChart).
                'subpro', 'issearchbutton', 'oldpost', 'ischart', 'chartlink',
                // Issue #601: lay-encode is CodeTagHelper's sibling attribute to
                // the already-allowlisted lay-height/lay-title/lay-skin above —
                // it used to be emitted as the dead, non-`lay-`-prefixed `encode`
                // attribute (neither layui.code module — 2.6.3's
                // demo/.../layui/lay/modules/code.js nor 2.13.8's
                // layui-next bundle — ever reads bare `encode`; both read
                // `lay-encode` via `elem.attr("lay-"+"encode")`), so it was
                // ALREADY silently inert even outside dialogs. #601 renames the
                // emission to `lay-encode`, restoring the intended
                // escape-code-sample behavior; add it here so it also survives
                // SafeHtml in dialog partials, matching its siblings.
                'lay-encode'
            ]
        });
    },

    // Issue #805: escape a plain-text string into HTML-entity-encoded form
    // using the browser's own DOM text node. Used by DispatchAction to
    // enforce the WtmAction.Message / WtmAction.Title "plain text" contract
    // before passing into layui layer.alert (which parses HTML in msg/title).
    // Relies on jQuery which is always loaded before framework_layui.js.
    EscapeText: function (s) {
        if (s === null || s === undefined) { return ''; }
        return $('<div/>').text(String(s)).html();
    },

    EscapeAttr: function (s) { return ff.EscapeText(s).replace(/"/g, '&quot;').replace(/'/g, '&#39;'); },

    // Issue #332: DOM-safe input element builder for ChainChange and
    // LoadComboItems. Replaces string concatenation that allowed item.Value
    // and item.Text to break out of attribute contexts (stored XSS; server
    // half is #331). Attributes are set via setAttribute / property assignment,
    // never via innerHTML.
    _makeInput: function (type, name, value, title, checked, disabled) {
        var el = document.createElement('input');
        el.type = type;
        el.name = name;
        el.value = value !== undefined && value !== null ? String(value) : '';
        el.title = title !== undefined && title !== null ? String(title) : '';
        if (checked) { el.checked = true; }
        if (disabled) { el.disabled = true; }
        return el;
    },

    // Issue #556 (#470-B slice 1): normalizes a parsed .wtm-dialog-init island
    // payload into the {actions:[...]} shape ff.DispatchAction expects.
    // Supports two island shapes:
    //   1. The wrapped form {"actions":[{...}, ...]} emitted by
    //      <wt:dialog-init> (DialogInitTagHelper) — returned as-is.
    //   2. A bare single-action object {"type":"...", ...} emitted by
    //      lighter-weight islands such as DateTimeTagHelper's laydate
    //      island — wrapped into {actions:[parsed]}.
    // Returns null for anything else (malformed / unrecognized) so callers
    // can skip that island without affecting others.
    _normalizeIslandPayload: function (parsed) {
        if (!parsed || typeof parsed !== 'object') { return null; }
        if (Array.isArray(parsed.actions)) { return parsed; }
        if (parsed.type) { return { actions: [parsed] }; }
        return null;
    },

    // Issue #556 (#470-B slice 1, hardening): returns the set of layui modules
    // a normalized island payload needs before it can be dispatched without a
    // silent no-op. The 'laydate' / 'initForm' actions call layui.laydate.render
    // / layui.form.render, which no-op if the module hasn't finished its async
    // load yet — the timing race that made full-page date fields intermittently
    // fail to render. Actions with no module dependency (closeDialog, alert,
    // loadComboItems, …) contribute nothing, so a payload of only those routes
    // straight through with no deferral.
    // Issue #558 (#470-C): 'bindSubmit' calls layui.form.on, which requires the
    // 'form' module for the same reason — without this, a bindSubmit island
    // dispatched before layui's async 'form' module finishes loading would
    // silently fail to register the submit handler.
    // Issue #564 (#470-D): 'bindValidate' also calls layui.form.on (the
    // auto-validate submit(...) binding) and needs the same 'form' module
    // deferral. 'highlightErrors' is plain DOM manipulation — no layui module
    // dependency at all.
    // Issue #552 (#470-E): 'slider' / 'rate' / 'colorpicker' each call their
    // own layui.<mod>.render, which — same as laydate/form — no-ops if the
    // module hasn't finished its async load yet. Each needs its own module
    // deferral entry.
    // Issue #571: 'tagInput' is intentionally ABSENT from the map below — the
    // native tag/chip input has no layui module dependency at all (pure DOM
    // widget), so it never needs a layui.use(...) deferral and always
    // dispatches immediately, on both the page-ready and dialog paths.
    // Issue #601: 'bindInput' is likewise intentionally ABSENT — it is a plain
    // addEventListener bind (TextBoxTagHelper's ChangeFunc/DoneFunc), no layui
    // module involved at all, same rationale as 'tagInput' above.
    // Issue #632: 'fieldDefaults' is likewise intentionally ABSENT — it is a
    // plain window[] property write (CheckBoxTagHelper/RadioTagHelper's
    // back-compat default-selection global), no layui module and no DOM widget
    // involved at all, same rationale as 'tagInput'/'bindInput' above.
    // Issue #470 Slice G: 'ueditor' / 'layedit' each call their own layui
    // submodule ('ueditorconfig' / 'layedit'), same rationale as
    // slider/rate/colorpicker above. Their own DispatchAction case bodies
    // (_renderUEditorAction / _renderLayeditAction) ALSO wrap the render in
    // their own layui.use([...], cb) call (mirroring the legacy inline
    // <script>, which always did the same), so this entry is belt-and-
    // suspenders defense-in-depth — same intentionally-redundant pattern as
    // slider/rate/colorpicker (see the #576 comment above _renderSliderAction).
    // Issue #470 Slice J: 'renderSelect' is intentionally ABSENT — xm-select
    // is a plain <script src> global, not a layui.use(...) module (see
    // _renderSelectAction's own comment).
    // Issue #470 Slice K: 'renderTransfer' DOES need an entry — UNLIKE
    // xm-select, layui.transfer IS a layui.use(...) module (the legacy inline
    // <script> this island replaces always wrapped its call in
    // `layui.use(['transfer'], function(){ ... })`), so — same rationale as
    // laydate/slider/rate/colorpicker/ueditor/layedit above — dispatching
    // before the module has finished loading would silently no-op.
    // Issue #470 Slice L: 'upload'/'multiUpload' likewise need an entry —
    // layui.upload IS a layui.use(...) module (same rationale as
    // 'renderTransfer' above). 'uploadExisting' is intentionally ABSENT —
    // it is pure jQuery.ajax + DOM building with no layui module dependency
    // at all, same rationale as 'loadComboItems'.
    _islandModulesFor: function (payload) {
        var needed = { form: false, laydate: false, slider: false, rate: false, colorpicker: false, ueditorconfig: false, layedit: false, transfer: false, upload: false };
        if (payload && payload.actions) {
            for (var i = 0; i < payload.actions.length; i++) {
                var a = payload.actions[i];
                if (!a || !a.type) { continue; }
                if (a.type === 'laydate') {
                    needed.laydate = true;
                } else if (a.type === 'initForm') {
                    needed.form = true;
                    if (a.dates && a.dates.length) { needed.laydate = true; }
                } else if (a.type === 'bindSubmit') {
                    needed.form = true;
                } else if (a.type === 'bindValidate') {
                    needed.form = true;
                } else if (a.type === 'slider') {
                    needed.slider = true;
                } else if (a.type === 'rate') {
                    needed.rate = true;
                } else if (a.type === 'colorpicker') {
                    needed.colorpicker = true;
                } else if (a.type === 'ueditor') {
                    needed.ueditorconfig = true;
                } else if (a.type === 'layedit') {
                    needed.layedit = true;
                } else if (a.type === 'renderTransfer') {
                    needed.transfer = true;
                } else if (a.type === 'upload' || a.type === 'multiUpload') {
                    needed.upload = true;
                }
            }
        }
        var mods = [];
        if (needed.form) { mods.push('form'); }
        if (needed.laydate) { mods.push('laydate'); }
        if (needed.slider) { mods.push('slider'); }
        if (needed.rate) { mods.push('rate'); }
        if (needed.colorpicker) { mods.push('colorpicker'); }
        if (needed.ueditorconfig) { mods.push('ueditorconfig'); }
        if (needed.layedit) { mods.push('layedit'); }
        if (needed.transfer) { mods.push('transfer'); }
        if (needed.upload) { mods.push('upload'); }
        return mods;
    },

    // Issue #556 (#470-B slice 1, hardening): dispatch a normalized island
    // payload, GUARANTEEING any layui module it needs is loaded first. When a
    // module dependency exists and layui.use is available, the dispatch is
    // deferred into layui.use([...], cb) — layui runs the callback only once
    // the modules are loaded (immediately if already loaded), so laydate.render
    // / form.render can never silently no-op due to a not-yet-loaded module.
    // This is the invariant that kills the page-ready timing race: a full-page
    // island ALWAYS eventually renders. Payloads with no module dependency (or
    // when layui.use is unavailable) fall back to a direct synchronous dispatch,
    // preserving the original ordering for everything else.
    _dispatchIslandWhenReady: function (payload) {
        var mods = ff._islandModulesFor(payload);
        if (mods.length > 0 && typeof layui !== 'undefined' && typeof layui.use === 'function') {
            layui.use(mods, function () {
                try {
                    ff.DispatchAction(payload);
                } catch (e) {
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] deferred island dispatch failed:', e);
                    }
                }
            });
        } else {
            ff.DispatchAction(payload);
        }
    },

    // Issue #587: shared extraction helper — factors out the COLLECTION half
    // of ff.OpenDialog's #462/#470/#522 same-origin-partial pre-collection
    // (parse the raw HTML with a DETACHED DOMParser, then walk it for inline
    // <script> bodies and .wtm-dialog-init JSON islands, all BEFORE
    // ff.SafeHtml/DOMPurify strips <script> elements — including islands —
    // from the markup that actually gets inserted into the live document).
    // This is the SAME logic ff.OpenDialog has always run inline in its
    // $.ajax success handler; it is factored out here so a second call site
    // (ff.PostForm's validation-failure form-HTML redraw branch, added by
    // #587) can reuse it instead of duplicating it.
    // ff.OpenDialog's own inline extraction code is left COMPLETELY
    // UNCHANGED by this addition — its behavior (and the #462/#470/#522/#556
    // tests that pin its exact source shape) is unaffected; this helper
    // simply gives new call sites going forward a single place to call
    // instead of copy-pasting the DOMParser walk.
    // Returns { initScripts: string[], islandPayloads: object[] }. Never
    // throws: a malformed HTML document, a malformed individual island, or a
    // querySelectorAll failure is swallowed and simply yields fewer
    // collected entries (matching OpenDialog's own per-try/catch
    // granularity — one bad island must never drop the others).
    _collectInitFromHtml: function (html) {
        var initScripts = [];
        var pdoc = null;
        try {
            pdoc = new DOMParser().parseFromString(html, 'text/html');
            var nodes = pdoc.querySelectorAll('script');
            for (var ni = 0; ni < nodes.length; ni++) {
                var s = nodes[ni];
                var type = (s.getAttribute('type') || '').toLowerCase();
                var isJs = type === '' || type === 'text/javascript' || type === 'application/javascript' || type === 'module';
                // inline JS only — skip external src and non-JS data blocks (e.g. application/json)
                if (isJs && !s.src && s.textContent) {
                    initScripts.push(s.textContent);
                }
            }
        } catch (e) { /* malformed HTML → no init scripts; markup still rendered via SafeHtml */ }

        var islandPayloads = [];
        try {
            if (pdoc !== null) {
                var islandNodes = pdoc.querySelectorAll('script[type="application/json"].wtm-dialog-init');
                for (var ii = 0; ii < islandNodes.length; ii++) {
                    var islandNode = islandNodes[ii];
                    if (!islandNode || !islandNode.textContent) { continue; }
                    try {
                        var parsed = JSON.parse(islandNode.textContent);
                        var normalized = ff._normalizeIslandPayload(parsed);
                        if (normalized !== null) { islandPayloads.push(normalized); }
                    } catch (e) { /* malformed single island JSON → skip that island only */ }
                }
            }
        } catch (e) { /* querySelectorAll failure → no islands; legacy path unaffected */ }

        return { initScripts: initScripts, islandPayloads: islandPayloads };
    },

    // Issue #587: replay half of the shared helper above. Re-injects
    // previously-collected legacy inline <script> texts as real <script>
    // elements in original document order — the same technique as
    // ff.OpenDialog's #522 rehydration loop (native global scope +
    // execution order, so sibling scripts can share top-level vars) — and
    // then dispatches previously-collected .wtm-dialog-init island payloads
    // via ff._dispatchIslandWhenReady, scripts first and islands after,
    // matching ff.OpenDialog's ordering exactly (see the #576 comment on
    // that ordering invariant). Callers MUST invoke this only AFTER the
    // fragment the HTML came from has already been inserted into the live
    // document — script re-injection targets document.body directly and
    // does not itself insert `collected` anywhere.
    _replayInitFromHtml: function (collected) {
        if (!collected) { return; }
        var initScripts = collected.initScripts || [];
        // Issue #627: kill-switch check — extraction above (ff._collectInitFromHtml)
        // is completely unchanged; only this execution loop is gated. Island
        // payload dispatch below runs unchanged in both modes.
        if (ff._isLegacyRehydrationDisabled()) {
            if (initScripts.length > 0 && typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] ' + initScripts.length + ' legacy inline script(s) in fragment were NOT executed (DisableLegacyScriptRehydration). Migrate them to dialog-init islands. See #627/#470.');
            }
        } else {
            for (var si = 0; si < initScripts.length; si++) {
                var se = document.createElement('script');
                se.text = initScripts[si];
                document.body.appendChild(se);          // executes synchronously in global scope
                if (se.parentNode) { se.parentNode.removeChild(se); } // tidy up; effects persist
            }
        }
        var islandPayloads = collected.islandPayloads || [];
        for (var pi = 0; pi < islandPayloads.length; pi++) {
            try {
                ff._dispatchIslandWhenReady(islandPayloads[pi]);
            } catch (e) {
                if (typeof console !== 'undefined' && console.warn) {
                    console.warn('[WTM] init replay island dispatch failed:', e);
                }
            }
        }
    },

    // Issue #552 adversarial-review fix (module-load race, HIGH): shared render
    // body for the 'slider' DispatchAction case (below). Extracted so the
    // immediate path (layui.slider already loaded) and the deferred path
    // (layui.use(['slider'], cb) — see the 'slider' case) call the EXACT SAME
    // code, so the two paths can never drift out of sync. Reproduces what the
    // legacy inline <script> did (layui.use(['slider'], function(){ ... })),
    // which the original island migration lost: at the time this was written,
    // OpenDialog dispatched islands via a direct ff.DispatchAction call (NOT
    // ff._dispatchIslandWhenReady), so without this, a dialog whose only
    // special field is a callback-free <wt:slider> (no other
    // slider/rate/colorpicker/date usage on the page to have already
    // triggered layui's async module load) would silently render nothing on
    // first open. This function assumes layui.slider.render is already
    // available — the case below only calls it once that is true (either
    // immediately, or once the deferred layui.use callback fires).
    // Issue #576: OpenDialog's dialog-init dispatch loop now routes every
    // island (not just slider/rate/colorpicker) through
    // ff._dispatchIslandWhenReady generically, so this per-case guard is no
    // longer the only thing preventing the no-op — it is now redundant
    // belt-and-suspenders defense-in-depth. Left in place intentionally
    // (smaller diff; harmless double guard) rather than removed.
    //
    // Issue #470 Slice I: extended to also carry caller-supplied
    // ChangeFunc/OnTipsFunc callback NAMES (action.changeFn/action.onTipsFn),
    // resolved/wired here — NOT baked into action.opts server-side, because a
    // JSON island can only carry data, never a live function reference.
    //
    // TRUST BOUNDARY: action.changeFn/action.onTipsFn are ALWAYS compile-time,
    // developer-authored Razor literals (the ChangeFunc/OnTipsFunc TagHelper
    // attribute values) — NEVER field/request/model data, the same trust
    // class as bindSubmit's beforeSubmit (#558), bindInput's changeFunc/
    // doneFunc (#601), and laydate's readyFn/changeFn/doneFn (Slice H).
    // SliderTagHelper only ever emits these fields for a name that is already
    // a plain identifier; a dotted/call-expression name keeps the legacy
    // inline <script> instead (see SliderTagHelper.cs). Resolved through the
    // SAME ff._resolveGuardedWindowFn guard those callers use — identifier
    // regex + denylist + own-property + typeof function — so even a future
    // wiring mistake can't turn this into an eval-equivalent primitive. A
    // failed resolution silently skips JUST that one callback and never
    // throws, never evals.
    _renderSliderAction: function (action) {
        try {
            if (!action.opts || !action.opts.elem ||
                typeof layui === 'undefined' || !layui.slider ||
                typeof layui.slider.render !== 'function') { return; }
            var _slOpts = { elem: action.opts.elem };
            if (typeof action.opts.type === 'string') { _slOpts.type = action.opts.type; }
            if (typeof action.opts.min === 'number') { _slOpts.min = action.opts.min; }
            if (typeof action.opts.max === 'number') { _slOpts.max = action.opts.max; }
            if (action.opts.range === true) { _slOpts.range = true; }
            if (typeof action.opts.value === 'number' || Array.isArray(action.opts.value)) {
                _slOpts.value = action.opts.value;
            }
            if (typeof action.opts.step === 'number') { _slOpts.step = action.opts.step; }
            if (action.opts.disabled === true) { _slOpts.disabled = true; }
            if (typeof action.opts.showstep === 'boolean') { _slOpts.showstep = action.opts.showstep; }
            if (typeof action.opts.tips === 'boolean') { _slOpts.tips = action.opts.tips; }
            if (typeof action.opts.input === 'boolean') { _slOpts.input = action.opts.input; }
            if (typeof action.opts.height === 'number') { _slOpts.height = action.opts.height; }
            if (typeof action.opts.theme === 'string') { _slOpts.theme = action.opts.theme; }
            var _slFieldId0 = (typeof action.fieldId0 === 'string') ? action.fieldId0 : null;
            var _slFieldId1 = (typeof action.fieldId1 === 'string') ? action.fieldId1 : null;
            // Issue #578: containment gate mirrors #564's highlightErrors —
            // action.formId (absent on old/back-compat islands, in which case
            // the write proceeds unguarded exactly as before) is resolved via
            // document.getElementById, then each write target must satisfy
            // .contains(el) or the write-back is silently skipped. Closes the
            // id-spoofing gap a smuggled island (#462/#552 threat model) could
            // otherwise use to target a hidden input elsewhere on the page.
            var _slFormId = (typeof action.formId === 'string') ? action.formId : null;
            var _slFormEl = _slFormId ? document.getElementById(_slFormId) : null;
            var _slContained = function (el) {
                return !_slFormId || (_slFormEl != null && _slFormEl.contains(el));
            };
            var _slIsRange = _slOpts.range === true;
            // Issue #470 Slice I: resolved BEFORE render so both change/setTips
            // closures can reference them; _slSliderIns is assigned AFTER
            // render (same pattern as laydate's Slice H _ldDateIns) so the
            // closures — which fire only on later user interaction — always
            // see the live instance, never undefined.
            var _slSliderIns;
            var _slChangeFn = ff._resolveGuardedWindowFn(action.changeFn);
            var _slOnTipsFn = ff._resolveGuardedWindowFn(action.onTipsFn);
            _slOpts.change = function (value) {
                if (_slIsRange) {
                    if (_slFieldId0 && Array.isArray(value)) {
                        var _el0 = document.getElementById(_slFieldId0);
                        if (_el0 && _slContained(_el0)) { _el0.value = value[0]; }
                    }
                    if (_slFieldId1 && Array.isArray(value)) {
                        var _el1 = document.getElementById(_slFieldId1);
                        if (_el1 && _slContained(_el1)) { _el1.value = value[1]; }
                    }
                } else if (_slFieldId0) {
                    var _el = document.getElementById(_slFieldId0);
                    if (_el && _slContained(_el)) { _el.value = value; }
                }
                // Issue #470 Slice I: mirrors the legacy inline <script>'s
                // `change: function(value){defaultFunc(value,sliderIns); ChangeFunc(value,sliderIns)}`
                // — the built-in write-back above runs FIRST, then the
                // resolved caller callback (if any) runs with the same
                // (value, sliderIns) argument shape.
                if (_slChangeFn) { _slChangeFn(value, _slSliderIns); }
            };
            if (_slOnTipsFn) {
                // Mirrors the legacy inline <script>'s
                // `setTips: function(value){return OnTipsFunc(value,sliderIns);}`.
                _slOpts.setTips = function (value) { return _slOnTipsFn(value, _slSliderIns); };
            }
            _slSliderIns = layui.slider.render(_slOpts);
            // Mirrors the inline-script post-render style tweaks (cosmetic
            // only — never developer-supplied data, never eval'd).
            if (_slOpts.type === 'vertical') {
                if (_slOpts.input) {
                    var _slVIn = document.querySelector(_slOpts.elem + ' .layui-slider-input');
                    if (_slVIn) { _slVIn.style.cssText = 'right:unset;top:-40px'; }
                }
            } else {
                var _slContainer = document.querySelector(_slOpts.elem);
                if (_slContainer) { _slContainer.style.cssText = 'min-height: 18px;padding-top: 18px;'; }
                if (_slOpts.input) {
                    var _slDIn = document.querySelector(_slOpts.elem + ' .layui-slider-input');
                    if (_slDIn) { _slDIn.style.cssText = 'top:3px;'; }
                }
            }
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] slider action failed:', e);
            }
        }
    },

    // Issue #552 adversarial-review fix (module-load race, HIGH): shared render
    // body for the 'rate' DispatchAction case. Same rationale as
    // _renderSliderAction above.
    _renderRateAction: function (action) {
        try {
            if (!action.opts || !action.opts.elem ||
                typeof layui === 'undefined' || !layui.rate ||
                typeof layui.rate.render !== 'function') { return; }
            var _rtOpts = { elem: action.opts.elem };
            if (typeof action.opts.value === 'number') { _rtOpts.value = action.opts.value; }
            if (typeof action.opts.length === 'number') { _rtOpts.length = action.opts.length; }
            if (action.opts.half === true) { _rtOpts.half = true; }
            if (action.opts.readonly === true) { _rtOpts.readonly = true; }
            if (Array.isArray(action.opts.text)) { _rtOpts.text = action.opts.text; }
            var _rtValFieldId = (typeof action.valueFieldId === 'string') ? action.valueFieldId : null;
            // Issue #578: containment gate mirrors #564's highlightErrors —
            // see the slider case above for the full threat-model rationale.
            var _rtFormId = (typeof action.formId === 'string') ? action.formId : null;
            var _rtFormEl = _rtFormId ? document.getElementById(_rtFormId) : null;
            _rtOpts.choose = function (val) {
                if (_rtValFieldId) {
                    var _rtEl = document.getElementById(_rtValFieldId);
                    if (_rtEl && (!_rtFormId || (_rtFormEl != null && _rtFormEl.contains(_rtEl)))) {
                        _rtEl.value = val;
                    }
                }
            };
            layui.rate.render(_rtOpts);
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] rate action failed:', e);
            }
        }
    },

    // Issue #552 adversarial-review fix (module-load race, HIGH): shared render
    // body for the 'colorpicker' DispatchAction case. Same rationale as
    // _renderSliderAction above.
    //
    // Issue #470 Slice I: extended to also carry a caller-supplied ChangeFunc
    // callback NAME (action.changeFn), resolved/wired here — NOT baked into
    // action.opts server-side, because a JSON island can only carry data,
    // never a live function reference.
    //
    // TRUST BOUNDARY: action.changeFn is ALWAYS a compile-time,
    // developer-authored Razor literal (the ChangeFunc TagHelper attribute
    // value, run through FormatFuncName(ChangeFunc, false) server-side — see
    // ColorPicker.cs) — NEVER field/request/model data, the same trust class
    // as bindSubmit's beforeSubmit (#558), bindInput's changeFunc/doneFunc
    // (#601), and laydate's readyFn/changeFn/doneFn (Slice H).
    // ColorPickerTagHelper only ever emits this field for a bare name that is
    // already a plain identifier; a dotted/bracketed name keeps the legacy
    // inline <script> instead (see ColorPicker.cs). Resolved through the SAME
    // ff._resolveGuardedWindowFn guard those callers use — identifier regex +
    // denylist + own-property + typeof function — so even a future wiring
    // mistake can't turn this into an eval-equivalent primitive. A failed
    // resolution silently skips the callback and never throws, never evals.
    _renderColorpickerAction: function (action) {
        try {
            if (!action.opts || !action.opts.elem ||
                typeof layui === 'undefined' || !layui.colorpicker ||
                typeof layui.colorpicker.render !== 'function') { return; }
            var _cpOpts = { elem: action.opts.elem };
            if (typeof action.opts.color === 'string') { _cpOpts.color = action.opts.color; }
            if (typeof action.opts.alpha === 'boolean') { _cpOpts.alpha = action.opts.alpha; }
            if (typeof action.opts.format === 'string') { _cpOpts.format = action.opts.format; }
            if (typeof action.opts.predefine === 'boolean') { _cpOpts.predefine = action.opts.predefine; }
            if (Array.isArray(action.opts.colors)) { _cpOpts.colors = action.opts.colors; }
            var _cpValFieldId = (typeof action.valueFieldId === 'string') ? action.valueFieldId : null;
            // Issue #578: containment gate mirrors #564's highlightErrors —
            // see the slider case above for the full threat-model rationale.
            var _cpFormId = (typeof action.formId === 'string') ? action.formId : null;
            var _cpFormEl = _cpFormId ? document.getElementById(_cpFormId) : null;
            var _cpChangeFn = ff._resolveGuardedWindowFn(action.changeFn);
            _cpOpts.done = function (data) {
                if (_cpValFieldId) {
                    var _cpEl = document.getElementById(_cpValFieldId);
                    if (_cpEl && (!_cpFormId || (_cpFormEl != null && _cpFormEl.contains(_cpEl)))) {
                        _cpEl.value = data;
                    }
                }
                // Issue #470 Slice I: mirrors the legacy inline <script>'s
                // `done: function(data){ $('#Id').val(data); ChangeFunc(data); }`
                // — the built-in write-back above runs FIRST, then the
                // resolved caller callback (if any) runs with the single
                // "data" argument, matching FormatFuncName's legacy "(data)" call.
                if (_cpChangeFn) { _cpChangeFn(data); }
            };
            layui.colorpicker.render(_cpOpts);
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] colorpicker action failed:', e);
            }
        }
    },

    // Issue #470 Slice G: shared render body for the 'ueditor' DispatchAction
    // case. Mirrors the legacy inline <script> UEditorTagHelper emitted exactly:
    //   layui.use(['ueditorconfig'], function () {
    //     layui.ueditor.loadEditor(id).ready(function () { this.setContent(content); });
    //   });
    // Unlike _renderSliderAction/_renderRateAction/_renderColorpickerAction
    // (which are called EITHER immediately or via a layui.use(...) deferral
    // decided by their DispatchAction case), this function performs the
    // layui.use(...) call ITSELF — matching the legacy inline script, which
    // always called layui.use(['ueditorconfig'], ...) unconditionally too
    // (layui.use is idempotent/safe to call even once the module is already
    // loaded). No developer-facing callback attribute exists on
    // UEditorTagHelper, so this always safely reproduces the legacy behaviour
    // natively — no legacy-fallback branch needed (same rationale as
    // RateTagHelper, #552).
    _renderUEditorAction: function (action) {
        try {
            if (!action || !action.id || typeof layui === 'undefined' || typeof layui.use !== 'function') { return; }
            layui.use(['ueditorconfig'], function () {
                try {
                    if (!layui.ueditor || typeof layui.ueditor.loadEditor !== 'function') { return; }
                    layui.ueditor.loadEditor(action.id).ready(function () {
                        this.setContent(action.content || '');
                    });
                } catch (e2) {
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] ueditor action failed:', e2);
                    }
                }
            });
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] ueditor action failed:', e);
            }
        }
    },

    // Issue #470 Slice G: shared render body for the 'layedit' DispatchAction
    // case. Mirrors the legacy inline <script> RichTextBoxTagHelper emitted
    // exactly:
    //   layui.use('layedit', function(){
    //     var layedit = layui.layedit;
    //     layedit.set({ uploadImage: { url: uploadUrl } });
    //     var index = layedit.build(id[, {height:...}]);
    //     $('#'+id).attr('layeditindex', index);
    //   });
    // Same self-contained layui.use(...) pattern as _renderUEditorAction above
    // (the legacy inline script always called layui.use('layedit', ...)
    // unconditionally too). RichTextBoxTagHelper exposes no developer-facing
    // callback attribute, so this always safely migrates — no legacy-fallback
    // branch needed. Uses document.getElementById + setAttribute instead of
    // jQuery's .attr() — same DOM mutation, no jQuery dependency added here.
    _renderLayeditAction: function (action) {
        try {
            if (!action || !action.id || typeof layui === 'undefined' || typeof layui.use !== 'function') { return; }
            layui.use('layedit', function () {
                try {
                    var layedit = layui.layedit;
                    if (!layedit || typeof layedit.set !== 'function' || typeof layedit.build !== 'function') { return; }
                    layedit.set({ uploadImage: { url: action.uploadUrl || '' } });
                    var opts = (typeof action.height === 'number') ? { height: action.height } : undefined;
                    var index = layedit.build(action.id, opts);
                    var el = document.getElementById(action.id);
                    if (el) { el.setAttribute('layeditindex', index); }
                } catch (e2) {
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] layedit action failed:', e2);
                    }
                }
            });
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] layedit action failed:', e);
            }
        }
    },

    // Issue #470 Slice J: shared xmSelect item-render template — reproduces
    // the dropdown-item `template({item,sels,name,value})` xmSelect option
    // AND the multi-select `model.label.block.template` option verbatim.
    // Both are BYTE-IDENTICAL in the legacy inline <script> ComboBoxTagHelper
    // / TreeTagHelper emit (all 4 combinations: combo/tree × single/multi) —
    // both only ever read `item.icon`/`item.name`, ignoring the other
    // arguments xmSelect passes, so one function safely serves both call
    // sites. Kept as a plain top-level ff method (not a closure captured per
    // action) since it has no per-call state — mirrors how the legacy inline
    // <script> re-declared the identical function body at each call site.
    _selectItemTemplate: function (item, sels, name, value) {
        if (!item) { return ''; }
        if (item.icon !== undefined && item.icon != '' && item.icon != null) {
            return '<i class="' + item.icon + '"></i>' + item.name;
        }
        return item.name;
    },

    // Issue #470 Slice J: shared xmSelect single-select label template —
    // reproduces the `model.label.abc.template` xmSelect option the legacy
    // inline <script> emits for the `radio:true` (single-select) branch.
    // Unlike _selectItemTemplate above, this one reads `sels[0]` (the
    // currently-selected item), not `item` — a genuinely different function,
    // also byte-identical across the combo/tree call sites.
    _selectSingleLabelTemplate: function (item, sels) {
        var s0 = sels && sels[0];
        if (!s0) { return ''; }
        if (s0.icon !== undefined && s0.icon != '' && s0.icon != null) {
            return '<i class="' + s0.icon + '"></i>' + s0.name;
        }
        return s0.name;
    },

    // Issue #470 Slice J (#470-J): shared render body for the 'renderSelect'
    // DispatchAction case (below) — the opt-in (UseSelectIslandRender, default
    // OFF) eval-free island render for <wt:combobox>/<wt:tree>. Reproduces the
    // CURRENT legacy inline xmSelect.render(...) <script> EXACTLY: same cfg
    // shape, same remoteMethod/load closures (over action.remoteUrl/lazyUrl),
    // same icon+name templates (_selectItemTemplate/_selectSingleLabelTemplate
    // above), same on: handler wiring into ff.ChainChange, same
    // window[action.id] / window[action.id+'defaultvalues'] globals, same
    // chainInitial setTimeout(100) initial fire. xm-select is a plain
    // <script src> global (NOT a layui.use(...) module), so — unlike
    // slider/rate/colorpicker/ueditor/layedit — there is no module-load race
    // to guard against and no _islandModulesFor entry; this dispatches
    // synchronously, guarded only by its own existence check on `xmSelect`.
    //
    // TRUST BOUNDARY: action.changeFunc is ALWAYS a compile-time,
    // developer-authored Razor literal (the ComboBoxTagHelper/TreeTagHelper
    // ChangeFunc attribute value) — NEVER field/request/model data, the same
    // trust class as bindSubmit's beforeSubmit (#558) / laydate's
    // readyFn/changeFn/doneFn (#470 Slice H). The emitter (ComboBoxTagHelper/
    // TreeTagHelper) only ever emits this action when ChangeFunc is absent or
    // already a plain identifier; a non-identifier ChangeFunc keeps the legacy
    // inline <script> instead (see the TagHelpers' Process methods). Resolved
    // through the SAME ff._resolveGuardedWindowFn guard every other named-
    // callback action uses — identifier regex + denylist + own-property +
    // typeof function; a failed resolution silently no-ops that one callback,
    // never throws, never evals.
    _renderSelectAction: function (action) {
        try {
            if (!action || !action.id || !action.el) { return; }
            if (typeof xmSelect === 'undefined' || typeof xmSelect.render !== 'function') {
                if (typeof console !== 'undefined' && console.warn) {
                    console.warn('[WTM] renderSelect action skipped: xmSelect is not loaded (#470).');
                }
                return;
            }

            var cfg = {
                el: action.el,
                name: action.name,
                tips: action.tips,
                disabled: action.disabled === true,
                language: action.language === 'zn' ? 'zn' : 'en',
                autoRow: action.autoRow === true,
                filterable: action.filterable === true,
                template: ff._selectItemTemplate,
                height: (typeof action.height === 'string' && action.height) ? action.height : '400px',
                data: Array.isArray(action.items) ? action.items : []
            };

            // combo: remote search — mirrors ComboBoxTagHelper's remoteMethod
            // closure exactly (same ff.getComboItems(data.Data, []) call).
            if (action.widget === 'combo' && typeof action.remoteUrl === 'string' && action.remoteUrl) {
                cfg.remoteSearch = true;
                cfg.remoteMethod = function (val, cb) {
                    $.get(action.remoteUrl, { q: val }, function (data) {
                        cb(ff.getComboItems(data.Data, []));
                    });
                };
            }

            // tree: lazy load — mirrors TreeTagHelper's load closure exactly
            // (same ff.getTreeItems(data.Data, []) call).
            if (action.widget === 'tree' && typeof action.lazyUrl === 'string' && action.lazyUrl) {
                cfg.lazy = true;
                cfg.load = function (node, cb) {
                    $.get(action.lazyUrl, { id: node.value }, function (data) {
                        cb(ff.getTreeItems(data.Data, []));
                    });
                };
            }

            if (action.widget === 'tree') {
                cfg.tree = { strict: false, show: true, showFolderIcon: true, showLine: true, indent: 20 };
            }

            var showToolbar = action.showToolbar !== false;
            if (action.multiSelect === false) {
                cfg.radio = true;
                cfg.clickClose = true;
                cfg.model = { label: { type: 'abc', abc: { template: ff._selectSingleLabelTemplate } } };
                cfg.toolbar = { show: showToolbar, list: ['CLEAR'] };
            } else {
                cfg.toolbar = { show: true, list: ['ALL', 'REVERSE', 'CLEAR'] };
                cfg.model = { label: { block: { template: ff._selectItemTemplate } } };
            }

            var changeFn = ff._resolveGuardedWindowFn(action.changeFunc);
            var chainSelfEl = document.getElementById(action.id);
            cfg.on = function (data) {
                if (action.linkTo) {
                    var gate = true;
                    if (changeFn) { gate = changeFn(data); }
                    // #470 Slice J follow-up (2nd HIGH defect): LOOSE inequality
                    // (`!=`), matching the legacy inline render's gate EXACTLY
                    // (ComboBoxTagHelper.cs / TreeTagHelper.cs `on:` handler:
                    // `if ({ChangeFunc} != false)`). Strict `!==` would fire the
                    // chain for a ChangeFunc that returns 0, '', or '0' — every
                    // one of which is `== false` but not `=== false` — diverging
                    // from legacy, which blocks the chain for all three. Loose
                    // `!=` blocks the chain for 0/''/'0'/false, exactly like
                    // legacy, while still firing for null/undefined (JS: both
                    // `null != false` and `undefined != false` are true) —
                    // matching legacy's fallthrough for "no gate value returned".
                    if (gate != false) {
                        var u = action.triggerUrl || '';
                        if (u.indexOf('?') === -1) { u += '?t=' + new Date().getTime(); }
                        for (var i = 0; i < data.arr.length; i++) { u += '&id=' + data.arr[i].value; }
                        ff.ChainChange(u, chainSelfEl);
                    }
                } else if (changeFn) {
                    changeFn(data);
                }
            };

            window[action.id] = xmSelect.render(cfg);
            window[action.id + 'defaultvalues'] = Array.isArray(action.defaultValues) ? action.defaultValues : [];

            // #470 Slice J follow-up (2nd HIGH defect): apply required-field
            // validation as PART OF the island render, immediately after
            // xmSelect.render(...) — the exact call BaseFieldTag.cs's
            // (now island-path-skipped) inline <script> used to make:
            // window[id].update({layVerify, layReqText}). Populated
            // server-side (ComboBoxTagHelper/TreeTagHelper) ONLY when the
            // field IsFieldRequired() AND this island path was taken, so
            // this never double-applies alongside the (still-emitted, still
            // unguarded) inline script on the flag-OFF / non-identifier-
            // ChangeFunc fallback path. Runs synchronously right after
            // render — correct ordering, no race with window[id]'s own
            // (this line's) assignment above.
            if (action.layVerify) {
                window[action.id].update({ layVerify: action.layVerify, layReqText: action.layReqText });
            }

            // Reproduces the legacy inline render's initial-fire block EXACTLY
            // (ComboBoxTagHelper.cs / TreeTagHelper.cs, the `{Id}u`/`{Id}data`
            // setTimeout(100) branch): build the URL from action.triggerUrl,
            // append '?t=<timestamp>' only when the URL has no query string
            // yet, then append '&id=' for EACH of the field's own pre-selected
            // values (action.defaultValues, wired from selectVal/vals) so the
            // initial cascade request is scoped/filtered the same way the
            // legacy render produces it — never the bare triggerUrl.
            if (action.chainInitial && action.linkTo) {
                setTimeout(function () {
                    var initialUrl = action.triggerUrl || '';
                    if (initialUrl.indexOf('?') === -1) { initialUrl += '?t=' + new Date().getTime(); }
                    var initialVals = Array.isArray(action.defaultValues) ? action.defaultValues : [];
                    for (var j = 0; j < initialVals.length; j++) { initialUrl += '&id=' + initialVals[j]; }
                    ff.ChainChange(initialUrl, chainSelfEl, true);
                }, 100);
            }
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] renderSelect action failed:', e);
            }
        }
    },

    // Issue #470 Slice K (#470-K): shared render body for the 'renderTransfer'
    // DispatchAction case (below) — the opt-in (UseSelectIslandRender,
    // default OFF — the SAME flag #470 Slice J's 'renderSelect' island uses)
    // eval-free island render for <wt:transfer>. Reproduces the CURRENT
    // legacy inline `layui.use(['transfer'], function(){ ... transfer.render(
    // ...) })` <script> functionally: same defaultFunc write-back
    // (layui.transfer.getData(id) -> remove existing hidden inputs named
    // `name` inside the widget's #{id}div container -> append one fresh
    // hidden input per currently-selected value), same onchange wiring
    // (defaultFunc runs FIRST, then the resolved caller callback, same
    // (data,index,transferIns) argument shape), same init-default-value
    // hidden-input block, same Disabled post-render tweak (disable
    // checkbox/form-control descendants of the widget + a no-arg
    // transfer.render() call, matching the legacy script's own quirk).
    //
    // Issue #332 (deliberate, documented deviation from a literal
    // byte-for-byte port): the legacy inline <script> builds each hidden
    // input via raw HTML string concatenation
    // (`container.append('<input ... value="'+selectVals[i].value+'"/>')`),
    // the same unsafe pattern #332 already fixed for ff.LoadComboItems'
    // checkbox/radio branches. This island instead uses the SAME ff._makeInput
    // DOM-API builder those branches use — functionally identical (a hidden
    // input carrying the selected value under `name`), but immune to the
    // attribute-breakout class #332 closed. Reproducing the raw-concat
    // pattern verbatim in new code would knowingly reintroduce a
    // known-and-fixed vulnerability shape; using the codebase's own
    // established-safe primitive here is the correct call, not a functional
    // behavior change (a hidden input's `title` attribute, the only extra
    // thing _makeInput sets, has no visible or functional effect on a
    // type="hidden" element).
    //
    // Unlike xm-select (a plain <script src> global — see _renderSelectAction
    // above), layui.transfer IS a layui.use(...) module — see
    // _islandModulesFor's 'renderTransfer' entry — so this only ever
    // dispatches once that module is confirmed loaded (deferred via
    // ff._dispatchIslandWhenReady, mirroring laydate/slider/rate/colorpicker/
    // ueditor/layedit), matching the legacy inline script's own
    // layui.use(['transfer'], ...) wrap.
    //
    // TRUST BOUNDARY: action.changeFunc is ALWAYS a compile-time,
    // developer-authored Razor literal (the TransferTagHelper ChangeFunc
    // attribute value) — NEVER field/request/model data, the same trust
    // class as #470 Slice J's renderSelect changeFunc / bindSubmit's
    // beforeSubmit (#558). The emitter (TransferTagHelper.Process) only ever
    // emits this action when ChangeFunc is absent or already a plain
    // identifier; a non-identifier ChangeFunc keeps the legacy inline
    // <script> instead (see TransferTagHelper.cs). Resolved through the SAME
    // ff._resolveGuardedWindowFn guard every other named-callback action
    // uses — identifier regex + denylist + own-property + typeof function;
    // a failed resolution silently no-ops that one callback, never throws,
    // never evals.
    _renderTransferAction: function (action) {
        try {
            if (!action || !action.id || !action.el) { return; }
            if (typeof layui === 'undefined' || !layui.transfer ||
                typeof layui.transfer.render !== 'function') {
                if (typeof console !== 'undefined' && console.warn) {
                    console.warn('[WTM] renderTransfer action skipped: layui.transfer is not loaded (#470).');
                }
                return;
            }

            var name = action.name;
            var _id = action.id;
            var container = document.getElementById(_id + 'div');

            // Mirrors the legacy inline <script>'s
            // defaultFunc(data,index,transferIns) exactly (see the #332 note
            // above for the ff._makeInput deviation).
            function defaultFunc(data, index, transferIns) {
                var selectVals = layui.transfer.getData(_id);
                if (container) {
                    var inputs = container.querySelectorAll('input[name="' + name + '"]');
                    for (var i = 0; i < inputs.length; i++) {
                        if (inputs[i].parentNode) { inputs[i].parentNode.removeChild(inputs[i]); }
                    }
                    for (var j = 0; j < selectVals.length; j++) {
                        container.appendChild(ff._makeInput('hidden', name, selectVals[j].value));
                    }
                }
            }

            var defaultVal = Array.isArray(action.defaultValue) ? action.defaultValue : [];
            var changeFn = ff._resolveGuardedWindowFn(action.changeFunc);
            var transferIns;
            var opts = {
                elem: action.el,
                id: _id,
                title: Array.isArray(action.title) ? action.title : undefined,
                data: Array.isArray(action.data) ? action.data : [],
                text: { none: action.nonePlaceholder || '', searchNone: action.searchNonePlaceholder || '' },
                // Mirrors the legacy inline render's
                // `onchange: function(data,index){defaultFunc(data,index,transferIns);
                // ChangeFunc(data,index,transferIns);}` — the built-in
                // write-back above runs FIRST, then the resolved caller
                // callback (if any) runs with the same (data,index,
                // transferIns) argument shape.
                onchange: function (data, index) {
                    defaultFunc(data, index, transferIns);
                    if (changeFn) { changeFn(data, index, transferIns); }
                }
            };
            if (defaultVal.length > 0) { opts.value = defaultVal; }
            if (action.showSearch === true) { opts.showSearch = true; }
            if (typeof action.width === 'number') { opts.width = action.width; }
            if (typeof action.height === 'number') { opts.height = action.height; }

            transferIns = layui.transfer.render(opts);

            // Mirrors the legacy inline render's "init default value" block
            // exactly: append one hidden input per pre-selected value, so
            // the form submits the field's initial selection even before
            // the user interacts with the widget.
            if (defaultVal.length > 0 && container) {
                for (var k = 0; k < defaultVal.length; k++) {
                    container.appendChild(ff._makeInput('hidden', name, defaultVal[k]));
                }
            }

            // Mirrors the legacy inline render's Disabled post-render tweak
            // exactly: disable every checkbox/form-control descendant of the
            // widget's own container (`:checkbox` / `:input` in the legacy
            // jQuery selectors), then re-invoke transfer.render() with no
            // arguments — a layui quirk the legacy script also relied on.
            if (action.disabled === true) {
                var widgetEl = document.getElementById(_id);
                if (widgetEl) {
                    var checks = widgetEl.querySelectorAll('input[type="checkbox"]');
                    for (var m = 0; m < checks.length; m++) { checks[m].disabled = true; }
                    var formControls = widgetEl.querySelectorAll('input, select, textarea, button');
                    for (var n = 0; n < formControls.length; n++) { formControls[n].disabled = true; }
                }
                layui.transfer.render();
            }
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] renderTransfer action failed:', e);
            }
        }
    },

    // Issue #470 Slice L: shared render body for the 'upload' DispatchAction
    // case (below) — the opt-in (UseSelectIslandRender, default OFF — the
    // SAME flag #470 Slices J/K use) eval-free island render for
    // <wt:upload>. Reproduces the CURRENT legacy inline
    // `layui.use(['upload'], function(){ layui.upload.render(...) })`
    // <script> functionally: same xhr/progress wiring, same before/done/
    // error handlers, same ShowPreview branch (layer preview thumbnail +
    // delete icon, unless the icon/button was already produced by a prior
    // upload — legacy re-derives this from `res` every time, so does this).
    //
    // The legacy inline <script> also declared TWO fresh globals per widget
    // instance — window['{Id}DoDelete']/['{Id}DoPreview'] — and bound each
    // built element's click handler directly to them. This island instead
    // tags every clickable element with data-wtm-upload-action="delete"/
    // "preview" + data-wtm-upload-id/data-wtm-file-id, resolved by a SINGLE
    // document-level delegated click listener (registered once, near
    // ff.upload below) that calls ff.upload.doDelete/doPreview. This is a
    // genuine improvement, not just a refactor: the legacy per-widget
    // globals only existed once THAT widget's own <script> block had
    // executed (a window[Id]-shaped timing dependency, the same class #470
    // Slice J's follow-up fix had to work around for xmSelect's required-
    // validation wiring) — ff.upload's functions live on the always-
    // available `ff` namespace, loaded with this file before any widget
    // markup exists, so there is no such race to audit for here at all.
    //
    // layui.upload IS a layui.use(...) module (the legacy inline <script>
    // this island replaces always wrapped its call in
    // `layui.use(['upload'], function(){ ... })`), so — same rationale as
    // laydate/slider/rate/colorpicker/ueditor/layedit/transfer above —
    // _islandModulesFor's 'upload' entry defers dispatch until the module is
    // confirmed loaded.
    //
    // UploadTagHelper has no developer-facing callback attribute at all
    // (unlike ComboBox/Tree/Transfer's ChangeFunc) — so there is no
    // identifier-vs-non-identifier 3-way decision to make here; the ONLY
    // gate is UseSelectIslandRender itself (see UploadTagHelper.cs).
    _renderUploadAction: function (action) {
        try {
            if (!action || !action.id || !action.el) { return; }
            if (typeof layui === 'undefined' || !layui.upload ||
                typeof layui.upload.render !== 'function') {
                if (typeof console !== 'undefined' && console.warn) {
                    console.warn('[WTM] upload action skipped: layui.upload is not loaded (#470).');
                }
                return;
            }

            var id = action.id;
            var labelEl = document.getElementById(id + 'label');
            var hiddenEl = document.getElementById(id);
            var loadIndex = 0;
            var previewObj;

            // Mirrors the legacy inline <script>'s xhrOnProgress closure
            // exactly (the `xhr:`/`progress:` wiring that drives the
            // .layui-progress bar — a page-wide selector in the legacy
            // script too, not scoped per-widget; reproduced identically,
            // quirk and all).
            var xhrOnProgress = function (fn) {
                xhrOnProgress.onprogress = fn;
                return function () {
                    var xhr = $.ajaxSettings.xhr();
                    if (typeof xhrOnProgress.onprogress !== 'function') { return xhr; }
                    if (xhrOnProgress.onprogress && xhr.upload) {
                        xhr.upload.onprogress = xhrOnProgress.onprogress;
                    }
                    return xhr;
                };
            };

            var opts = {
                elem: action.el,
                url: action.url,
                size: action.size,
                accept: 'file',
                xhr: xhrOnProgress,
                progress: function (value) {
                    var bars = document.querySelectorAll('.layui-progress .layui-progress-bar');
                    for (var bi = 0; bi < bars.length; bi++) { bars[bi].style.width = value + '%'; }
                },
                before: function (obj) {
                    loadIndex = layui.layer.load(2);
                    previewObj = obj;
                },
                done: function (res) {
                    layui.layer.close(loadIndex);
                    if (!res || !res.Data || res.Data.Id === '') {
                        if (labelEl) { labelEl.innerHTML = ''; }
                        layui.layer.msg(action.uploadFailedText || '');
                        return;
                    }
                    if (labelEl) { labelEl.innerHTML = ''; }
                    if (hiddenEl) { hiddenEl.value = res.Data.Id; }
                    if (action.showPreview) {
                        if (previewObj && typeof previewObj.preview === 'function') {
                            previewObj.preview(function (idx, file, result) {
                                if (!labelEl) { return; }
                                var img = document.createElement('img');
                                img.src = result;
                                img.alt = file.name;
                                img.className = 'layui-upload-img';
                                img.width = action.previewWidth;
                                img.height = action.previewHeight;
                                img.id = id + 'preview';
                                img.style.cursor = 'pointer';
                                img.setAttribute('data-wtm-upload-action', 'preview');
                                img.setAttribute('data-wtm-upload-id', id);
                                img.setAttribute('data-wtm-file-id', res.Data.Id);
                                labelEl.appendChild(img);
                                var del = document.createElement('i');
                                del.className = 'layui-icon layui-icon-close';
                                del.id = id + 'del';
                                del.style.cssText = 'font-size: 20px;position:absolute;left:' +
                                    (action.previewWidth - 10) + 'px;top:-10px;color: #ff0000;';
                                del.setAttribute('data-wtm-upload-action', 'delete');
                                del.setAttribute('data-wtm-upload-id', id);
                                del.setAttribute('data-wtm-file-id', res.Data.Id);
                                labelEl.appendChild(del);
                            });
                        }
                    } else {
                        if (labelEl) {
                            var btn = document.createElement('button');
                            btn.className = 'layui-btn layui-btn-sm layui-btn-danger';
                            btn.type = 'button';
                            btn.id = id + 'del';
                            btn.style.color = 'white';
                            btn.textContent = (res.Data.Name || '') + '  ' + (action.deleteText || '');
                            btn.setAttribute('data-wtm-upload-action', 'delete');
                            btn.setAttribute('data-wtm-upload-id', id);
                            btn.setAttribute('data-wtm-file-id', res.Data.Id);
                            labelEl.appendChild(btn);
                        }
                        // Issue #753 (MEDIUM): do NOT reset the progress bar here — the
                        // legacy inline UploadTagHelper.cs only ever reset it inside the
                        // delete-button click handler (UploadTagHelper.cs ~lines 284-317),
                        // never unconditionally on upload completion. ff.upload.doDelete's
                        // single-mode branch below already resets it on click, mirroring
                        // the legacy {Id}DoDelete function exactly — resetting it again
                        // here (immediately after every successful upload, regardless of
                        // whether the user ever deletes) was extra behavior Slice L never
                        // had in the flag-OFF path.
                    }
                },
                error: function () {
                    layui.layer.close(loadIndex);
                }
            };
            if (action.exts) { opts.exts = action.exts; }

            layui.upload.render(opts);
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] upload action failed:', e);
            }
        }
    },

    // Issue #470 Slice L: shared render body for the 'multiUpload'
    // DispatchAction case (below) — the <wt:multiupload> sibling of
    // _renderUploadAction above. Reproduces the legacy inline
    // `{Id}selected` array + `{Id}SetValues()` write-back functionally via
    // ff.upload's per-id state registry (ff.upload._getState) instead of a
    // fresh window-scoped `{Id}selected` array per widget — see ff.upload
    // below for the full rationale (same eliminated global-timing
    // dependency as _renderUploadAction).
    _renderMultiUploadAction: function (action) {
        try {
            if (!action || !action.id || !action.el) { return; }
            if (typeof layui === 'undefined' || !layui.upload ||
                typeof layui.upload.render !== 'function') {
                if (typeof console !== 'undefined' && console.warn) {
                    console.warn('[WTM] multiUpload action skipped: layui.upload is not loaded (#470).');
                }
                return;
            }

            var id = action.id;
            var labelEl = document.getElementById(id + 'label');
            var state = ff.upload._getState(id);
            // Mirrors the legacy inline render's unconditional
            // `{Id}SetValues();` call immediately after `{Id}selected` is
            // seeded — reproduces the initial hidden-input write-back for
            // any pre-existing selected files before the user interacts
            // with the widget at all.
            ff.upload.setValues(id);
            var loadIndex = 0;

            var opts = {
                elem: action.el,
                url: action.url,
                size: action.size,
                accept: 'file',
                multiple: true,
                number: typeof action.number === 'number' ? action.number : 0,
                before: function () {
                    loadIndex = layui.layer.load(2);
                },
                done: function (res) {
                    layui.layer.close(loadIndex);
                    if (!res || !res.Data || res.Data.Id === '') {
                        layui.layer.msg(action.uploadFailedText || '');
                        return;
                    }
                    state.selected.push(res.Data.Id);
                    ff.upload.setValues(id);
                    if (labelEl) {
                        var label = document.createElement('label');
                        label.id = 'label' + res.Data.Id;
                        if (action.showPreview) {
                            var img = document.createElement('img');
                            img.alt = res.Data.Name || '';
                            img.setAttribute('layer-src',
                                '/_Framework/GetFile?id=' + res.Data.Id + '&_DONOT_USE_CS=' + state.cs);
                            img.src = '/_Framework/GetFile?id=' + res.Data.Id + '&stream=true&width=' +
                                action.previewWidth + '&height=' + action.previewHeight +
                                '&_DONOT_USE_CS=' + state.cs;
                            img.className = 'layui-upload-img';
                            img.width = action.previewWidth;
                            img.height = action.previewHeight;
                            img.id = 'preview' + res.Data.Id;
                            img.style.cssText = 'cursor:pointer;margin-bottom:5px';
                            img.setAttribute('data-wtm-upload-action', 'preview');
                            img.setAttribute('data-wtm-upload-id', id);
                            img.setAttribute('data-wtm-file-id', res.Data.Id);
                            label.appendChild(img);
                            var del = document.createElement('i');
                            del.className = 'layui-icon layui-icon-close';
                            del.id = 'del' + res.Data.Id;
                            del.style.cssText = 'font-size: 20px;position:relative;left:-10px;top:-27px;' +
                                'color: #ff0000;cursor: pointer;';
                            del.setAttribute('data-wtm-upload-action', 'delete');
                            del.setAttribute('data-wtm-upload-id', id);
                            del.setAttribute('data-wtm-file-id', res.Data.Id);
                            label.appendChild(del);
                        } else {
                            var btn = document.createElement('button');
                            btn.className = 'layui-btn layui-btn-sm layui-btn-danger';
                            btn.type = 'button';
                            btn.id = 'del' + res.Data.Id;
                            btn.style.color = 'white';
                            btn.style.marginLeft = '0px';
                            btn.textContent = (res.Data.Name || '') + '  ' + (action.deleteText || '');
                            btn.setAttribute('data-wtm-upload-action', 'delete');
                            btn.setAttribute('data-wtm-upload-id', id);
                            btn.setAttribute('data-wtm-file-id', res.Data.Id);
                            label.appendChild(btn);
                            label.appendChild(document.createElement('br'));
                        }
                        labelEl.appendChild(label);
                    }
                },
                error: function () {
                    layui.layer.close(loadIndex);
                }
            };
            if (action.exts) { opts.exts = action.exts; }

            layui.upload.render(opts);
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] multiUpload action failed:', e);
            }
        }
    },

    // Issue #470 Slice L: shared render body for the 'uploadExisting'
    // DispatchAction case (below) — the opt-in eval-free replacement for
    // UploadTagHelper's/MultiUploadTagHelper's "existing file" init
    // <script> (the block that fetches a stored file's display name via
    // /_Framework/GetFileName/<id> and builds the preview/delete markup for
    // a field that already has a value when the form first renders).
    // Reproduces the legacy $.ajax(...).success(...) markup building
    // functionally, but via safe DOM APIs (createElement/textContent/
    // setAttribute — see ff._buildUploadExistingEntry below) instead of raw
    // HTML string concatenation splicing the ajax-returned file NAME
    // directly into an HTML string. That raw-concat pattern is the SAME
    // #332-class attribute/tag-breakout risk #470 Slice K's ff._makeInput
    // deviation closed for Transfer's hidden inputs — applied here to a
    // value that is genuinely user-influenceable (a previously-uploaded
    // file's stored display name).
    //
    // No layui.use(...) module dependency at all (pure jQuery.ajax + DOM
    // building) — same rationale as 'loadComboItems' — so this dispatches
    // immediately; no _islandModulesFor entry needed.
    _renderUploadExistingAction: function (action) {
        try {
            if (!action || !action.id || !Array.isArray(action.files) || action.files.length === 0) { return; }
            var id = action.id;
            var labelEl = document.getElementById(id + 'label');
            if (!labelEl) { return; }
            for (var i = 0; i < action.files.length; i++) {
                (function (file) {
                    if (!file || !file.fileId || !file.getUrl) { return; }
                    $.ajax({
                        cache: false,
                        type: 'GET',
                        url: file.getUrl,
                        async: true,
                        success: function (data) {
                            ff._buildUploadExistingEntry(labelEl, id, file, data, action);
                        }
                    });
                })(action.files[i]);
            }
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] uploadExisting action failed:', e);
            }
        }
    },

    // Issue #470 Slice L: DOM-builder helper for _renderUploadExistingAction
    // above — factored out so it can be unit-tested directly against a
    // synchronous `data` value instead of only through the async $.ajax
    // mock. Mirrors the legacy inline <script>'s 4-branch matrix EXACTLY
    // (ShowPreview x Disabled): preview img (+ delete icon unless Disabled)
    // / disabled download link / delete button. Single-mode markup appends
    // straight into the widget's own #{id}label container; multi-mode
    // markup wraps each entry in its own <label id="label{fileId}">, the
    // same way the legacy MultiUpload inline script did (so
    // ff.upload.doDelete's multi-mode branch can find and remove it by that
    // id).
    _buildUploadExistingEntry: function (labelEl, id, file, name, action) {
        var mode = action.mode === 'multi' ? 'multi' : 'single';
        var wrap = mode === 'multi' ? document.createElement('label') : null;
        if (wrap) { wrap.id = 'label' + file.fileId; }
        var target = wrap || labelEl;
        var safeName = (name === undefined || name === null) ? '' : String(name);

        if (action.showPreview) {
            var img = document.createElement('img');
            img.src = file.pictureUrl || '';
            img.alt = safeName;
            img.className = 'layui-upload-img';
            img.width = action.previewWidth;
            img.height = action.previewHeight;
            img.id = mode === 'multi' ? 'preview' + file.fileId : id + 'preview';
            img.style.cursor = 'pointer';
            if (mode === 'multi') {
                img.setAttribute('layer-src', file.downloadUrl || '');
                img.style.marginBottom = '5px';
            }
            img.setAttribute('data-wtm-upload-action', 'preview');
            img.setAttribute('data-wtm-upload-id', id);
            img.setAttribute('data-wtm-file-id', file.fileId);
            target.appendChild(img);
            if (!action.disabled) {
                var del = document.createElement('i');
                del.className = 'layui-icon layui-icon-close';
                del.id = mode === 'multi' ? 'del' + file.fileId : id + 'del';
                del.style.cssText = mode === 'multi'
                    ? 'font-size: 20px;position:relative;left:-10px;top:-27px;color: #ff0000;cursor:pointer;margin-bottom:5px'
                    : ('font-size: 20px;position:absolute;left:' + (action.previewWidth - 10) + 'px;top:-10px;color: #ff0000;');
                del.setAttribute('data-wtm-upload-action', 'delete');
                del.setAttribute('data-wtm-upload-id', id);
                del.setAttribute('data-wtm-file-id', file.fileId);
                target.appendChild(del);
            }
        } else if (action.disabled) {
            var link = document.createElement('a');
            link.className = 'layui-btn layui-btn-primary layui-btn-xs';
            link.style.cssText = 'margin:9px 0;width:unset width:300px;';
            link.href = file.downloadUrl || '';
            link.textContent = safeName;
            target.appendChild(link);
        } else {
            var btn = document.createElement('button');
            btn.className = 'layui-btn layui-btn-sm layui-btn-danger';
            btn.type = 'button';
            btn.id = mode === 'multi' ? 'del' + file.fileId : id + 'del';
            btn.style.color = 'white';
            btn.textContent = safeName + '  ' + (action.deleteText || '');
            btn.setAttribute('data-wtm-upload-action', 'delete');
            btn.setAttribute('data-wtm-upload-id', id);
            btn.setAttribute('data-wtm-file-id', file.fileId);
            target.appendChild(btn);
            if (mode === 'multi') { target.appendChild(document.createElement('br')); }
        }
        if (wrap) { labelEl.appendChild(wrap); }
    },

    // Issue #571: native, dependency-free tag/chip input render body for the
    // 'tagInput' DispatchAction case (below). Unlike slider/rate/colorpicker
    // (#552), this widget has NO layui module dependency at all — it is built
    // entirely from plain DOM APIs, which is the whole point of the rewrite
    // (the old TagInputTagHelper targeted a layui.tagInput module that has
    // never shipped in any bundled layui tree — see TagInputTagHelper's XML
    // doc comment). Chips are built with createElement + textContent /
    // createTextNode ONLY — never innerHTML or string-concatenation with a
    // tag value, since tag values are user data (the #462/#552 stored-XSS
    // threat class: a tag value like '<img src=x onerror=alert(1)>' must
    // render as inert text, never markup). No eval, no Function constructor.
    _renderTagInputAction: function (action) {
        try {
            if (!action.opts || !action.opts.elem) { return; }
            var container = document.querySelector(action.opts.elem);
            if (!container) { return; }

            // Issue #578/#585: containment gate mirrors the #578 fix already
            // applied to _renderSliderAction/_renderRateAction/
            // _renderColorpickerAction — action.formId (absent on old/
            // back-compat islands, in which case rendering proceeds
            // unguarded exactly as before) is resolved via
            // document.getElementById, then BOTH the opts.elem container
            // (about to be destructively cleared/rebuilt below — unique to
            // tagInput among the island widgets, which otherwise only gated
            // their write-back callback) and the bound hidden value input
            // must satisfy formEl.contains(el) or the entire render is
            // silently skipped, BEFORE any clearing/writing happens. Closes
            // the id-spoofing gap a smuggled island (#462/#552 threat model)
            // could otherwise use — e.g.
            // {"type":"tagInput","opts":{"elem":"#anyContainer"},
            // "valueFieldId":"anyHiddenInput"} — to wipe an arbitrary
            // container and bind writes to an arbitrary hidden input
            // anywhere on the page.
            var _tiFormId = (typeof action.formId === 'string') ? action.formId : null;
            var _tiFormEl = _tiFormId ? document.getElementById(_tiFormId) : null;
            var _tiContained = function (el) {
                return !_tiFormId || (_tiFormEl != null && _tiFormEl.contains(el));
            };
            if (_tiFormId && !_tiContained(container)) { return; }

            var _tiValueFieldId = (typeof action.valueFieldId === 'string') ? action.valueFieldId : null;
            if (!_tiValueFieldId) { return; }
            // Prefer resolving the hidden value input scoped to the widget's
            // own container when it happens to live inside it; falls back to
            // a page-wide lookup for the layout TagInputTagHelper emits today
            // (the hidden input is a sibling of the container, not a child).
            var _tiHidden = container.querySelector('#' + _tiValueFieldId) || document.getElementById(_tiValueFieldId);
            if (!_tiHidden) { return; }
            if (_tiFormId && !_tiContained(_tiHidden)) { return; }

            var _tiSeparator = (typeof action.opts.separator === 'string' && action.opts.separator.length > 0)
                ? action.opts.separator : ',';
            var _tiPlaceholder = (typeof action.opts.placeholder === 'string') ? action.opts.placeholder : '';
            var _tiMax = (typeof action.opts.max === 'number' && action.opts.max > 0) ? action.opts.max : null;
            var _tiReadOnly = action.opts.readonly === true;
            var _tiDisabled = action.opts.disabled === true;
            var _tiInteractive = !_tiReadOnly && !_tiDisabled;

            // Idempotent — safe to invoke twice on the same container.
            while (container.firstChild) { container.removeChild(container.firstChild); }
            container.style.cssText = 'display:inline-flex;flex-wrap:wrap;align-items:center;gap:4px;' +
                'border:1px solid #e6e6e6;border-radius:2px;padding:4px 6px;min-height:20px;box-sizing:border-box;width:100%;';

            var _tiChips = document.createElement('span');
            _tiChips.className = 'wtm-taginput-chips';
            _tiChips.style.cssText = 'display:inline-flex;flex-wrap:wrap;gap:4px;';
            container.appendChild(_tiChips);

            var _tiTextInput = null;
            if (_tiInteractive) {
                _tiTextInput = document.createElement('input');
                _tiTextInput.type = 'text';
                _tiTextInput.className = 'wtm-taginput-entry';
                _tiTextInput.style.cssText = 'border:none;outline:none;flex:1;min-width:80px;';
                if (_tiPlaceholder) { _tiTextInput.placeholder = _tiPlaceholder; }
                container.appendChild(_tiTextInput);
            }

            // Issue #585 (B): trim each split piece — the legacy
            // BuildTagsJson implementation trimmed values before persisting
            // (so a server value like "a, b" rendered as clean "a"/"b"
            // chips); the native re-render must not regress and show
            // untrimmed leading/trailing whitespace.
            function _tiCurrentTags() {
                if (!_tiHidden.value) { return []; }
                return _tiHidden.value.split(_tiSeparator)
                    .map(function (s) { return s.replace(/^\s+|\s+$/g, ''); })
                    .filter(function (s) { return s.length > 0; });
            }

            function _tiWriteBack(tags) {
                _tiHidden.value = tags.join(_tiSeparator);
            }

            function _tiRenderChips() {
                while (_tiChips.firstChild) { _tiChips.removeChild(_tiChips.firstChild); }
                var tags = _tiCurrentTags();
                tags.forEach(function (tag, idx) {
                    var chip = document.createElement('span');
                    chip.className = 'wtm-taginput-chip';
                    chip.style.cssText = 'display:inline-flex;align-items:center;gap:3px;' +
                        'background:#f2f2f2;border-radius:2px;padding:2px 6px;';
                    // XSS-safe: textContent/createTextNode never parse the
                    // string as HTML — a payload like '<img src=x onerror=...>'
                    // is rendered as the literal, inert text of the chip.
                    chip.appendChild(document.createTextNode(tag));
                    if (_tiInteractive) {
                        var _tiClose = document.createElement('span');
                        _tiClose.className = 'wtm-taginput-chip-close';
                        _tiClose.style.cssText = 'cursor:pointer;color:#999;';
                        _tiClose.textContent = '×';
                        // Issue #585 (D): bind removal to mousedown (with
                        // preventDefault) rather than click. mousedown fires
                        // BEFORE the browser's default focus-shift blurs the
                        // entry input; blurring the entry input (see the
                        // 'blur' listener below) commits any pending typed
                        // text via _tiAddTag -> _tiRenderChips, which
                        // rebuilds every chip node from scratch — including
                        // the very × node the user is mid-click on. The
                        // subsequent click is then suppressed by the browser
                        // because its target was removed from the document
                        // between mousedown and mouseup, silently swallowing
                        // the removal. Handling removal on mousedown — and
                        // calling preventDefault() to stop the default blur
                        // outright — makes the removal atomic and immune to
                        // the race regardless of any pending entry text.
                        // Issue #585 review follow-up: 'click' only ever
                        // fires for the primary (left) button — auxiliary
                        // buttons dispatch 'auxclick' instead — but
                        // 'mousedown' fires for EVERY button. Without a
                        // guard, right-clicking the × (e.g. to open a
                        // context menu — preventDefault on mousedown does
                        // NOT suppress the separate 'contextmenu' event) or
                        // middle-clicking it (the Linux paste gesture) would
                        // also silently remove the tag, which 'click' never
                        // did. Bail out for any non-primary button before
                        // doing anything else. e.button is 0 for the
                        // primary button; treat a missing/non-numeric
                        // e.button (e.g. synthetic events dispatched by
                        // tests or programmatic .click() callers) as
                        // primary so existing callers keep working.
                        _tiClose.addEventListener('mousedown', function (e) {
                            if (e && typeof e.button === 'number' && e.button !== 0) { return; }
                            if (e && e.preventDefault) { e.preventDefault(); }
                            var t = _tiCurrentTags();
                            t.splice(idx, 1);
                            _tiWriteBack(t);
                            _tiRenderChips();
                        });
                        chip.appendChild(_tiClose);
                    }
                    _tiChips.appendChild(chip);
                });
            }

            // Issue #585 (B): split the raw input on the separator (handles
            // a pasted/typed multi-value string like "a,b,c", not just a
            // single value), trim each piece, drop empties, and enforce
            // _tiMax against the RESULTING total tag count — not just the
            // pre-add count. The previous guard (`tags.length >= _tiMax`)
            // counted a separator-bearing string as ONE tag against Max, so
            // e.g. max=5 with 4 existing tags let "a,b,c" through (4 < 5)
            // and produced 7 effective chips once _tiCurrentTags re-split
            // the written-back value. The whole batch is rejected (no
            // partial add) if it would push the total over max, matching
            // the previous single-tag reject-outright behavior. This also
            // sanitizes paste/blur uniformly with keydown, since both route
            // through this same function.
            function _tiAddTag(raw) {
                var pieces = (raw || '').split(_tiSeparator)
                    .map(function (s) { return s.replace(/^\s+|\s+$/g, ''); })
                    .filter(function (s) { return s.length > 0; });
                if (!pieces.length) { return; }
                var tags = _tiCurrentTags();
                if (_tiMax !== null && (tags.length + pieces.length) > _tiMax) { return; }
                _tiWriteBack(tags.concat(pieces));
                _tiRenderChips();
            }

            if (_tiTextInput) {
                _tiTextInput.addEventListener('keydown', function (e) {
                    if (e.key === 'Enter' || e.key === _tiSeparator) {
                        e.preventDefault();
                        _tiAddTag(_tiTextInput.value);
                        _tiTextInput.value = '';
                    }
                });
                _tiTextInput.addEventListener('blur', function () {
                    if (_tiTextInput.value) {
                        _tiAddTag(_tiTextInput.value);
                        _tiTextInput.value = '';
                    }
                });
            }

            _tiRenderChips();
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] tagInput action failed:', e);
            }
        }
    },

    // Issue #789 Phase 3C: CSP-safe JSON action dispatcher. The server returns
    // a WtmActionResult payload (X-WTM-Action: application/json header set) and
    // this function walks the whitelisted action types. Unknown action types
    // are logged and skipped — there is no dynamic-code-execution path here.
    //
    // Issue #805: 'alert' and 'message' branches escape action.message /
    // action.title through ff.EscapeText before handing off to ff.Alert /
    // ff.Msg. layui's layer.alert concatenates msg into innerHTML, which
    // would otherwise render attacker-controlled HTML (XSS).
    DispatchAction: function (payload) {
        if (!payload || !payload.actions || !payload.actions.length) {
            return;
        }
        var actions = payload.actions;
        for (var i = 0; i < actions.length; i++) {
            var action = actions[i];
            if (!action || !action.type) { continue; }
            switch (action.type) {
                case 'closeDialog':
                    if (typeof ff.CloseDialog === 'function') { ff.CloseDialog(); }
                    break;
                case 'alert':
                    if (typeof ff.Alert === 'function') {
                        // Issue #805: escape to enforce plain-text contract.
                        ff.Alert(
                            ff.EscapeText(action.message || ''),
                            ff.EscapeText(action.title || '')
                        );
                    }
                    break;
                case 'message':
                    if (typeof ff.Msg === 'function') {
                        // Issue #805: escape to enforce plain-text contract.
                        ff.Msg(
                            ff.EscapeText(action.message || ''),
                            ff.EscapeText(action.title || '')
                        );
                    }
                    break;
                case 'refreshGrid':
                    if (typeof ff.RefreshGrid === 'function') {
                        ff.RefreshGrid(action.winId || 'LAY_app_body', action.index || 0);
                    }
                    break;
                case 'refreshPage':
                    if (typeof layui !== 'undefined' && layui.index &&
                        typeof layui.index.render === 'function') {
                        layui.index.render();
                    }
                    break;
                case 'reload':
                    if (typeof location !== 'undefined' &&
                        typeof location.reload === 'function') {
                        location.reload();
                    }
                    break;
                case 'redirect':
                    // Issue #804: defense in depth. Server-side
                    // WtmActionResultExtension.Redirect throws on absolute
                    // URLs, but if a compromised downstream controller
                    // emits raw JSON bypassing the extension, reject
                    // absolute URLs here too.
                    // Issue #534: also reject a leading '/' followed by '\' —
                    // browsers normalize it to '/' ("/\evil.com" -> "//evil.com").
                    if (action.url) {
                        var _u = action.url;
                        if (/^\/(?:[^/\\]|$)/.test(_u)) {
                            location.href = _u;
                            return;
                        }
                        if (_u.charAt(0) === '#' || _u.charAt(0) === '?') {
                            location.href = _u;
                            return;
                        }
                        if (typeof console !== 'undefined' && console.warn) {
                            console.warn('[WTM] Redirect blocked: non-relative URL', _u);
                        }
                    }
                    break;
                // Issue #470: opt-in eval-free form initialisation via JSON island.
                // Calls layui.form.render() and laydate.render() — no dynamic code.
                case 'initForm':
                    try {
                        if (typeof layui !== 'undefined' && layui.form &&
                            typeof layui.form.render === 'function') {
                            layui.form.render(action.formType || null, action.filter || undefined);
                        }
                        if (action.dates && Array.isArray(action.dates) && action.dates.length > 0 &&
                            typeof layui !== 'undefined' && layui.laydate &&
                            typeof layui.laydate.render === 'function') {
                            for (var _di = 0; _di < action.dates.length; _di++) {
                                var _d = action.dates[_di];
                                if (_d && _d.elem) {
                                    // Issue #551: widen the static laydate option set beyond
                                    // elem/type/format to the full STATIC option set already
                                    // emitted by DateTimeTagHelper (range/min/max/zIndex/
                                    // showBottom/btns/calendar/lang/mark). Each option is only
                                    // added when present on the entry, so omitted options fall
                                    // back to laydate's own defaults — legacy
                                    // {elem,type,format}-only payloads render identically to
                                    // before. No callbacks (ready/change/done) are supported —
                                    // those remain an explicit out-of-scope blocker (#470).
                                    var _dOpts = { elem: _d.elem, type: _d.type || 'date', format: _d.format };
                                    if (_d.range !== undefined && _d.range !== null) { _dOpts.range = _d.range; }
                                    if (_d.min !== undefined && _d.min !== null) { _dOpts.min = _d.min; }
                                    if (_d.max !== undefined && _d.max !== null) { _dOpts.max = _d.max; }
                                    if (_d.zIndex !== undefined && _d.zIndex !== null) { _dOpts.zIndex = _d.zIndex; }
                                    if (_d.showBottom !== undefined && _d.showBottom !== null) { _dOpts.showBottom = _d.showBottom; }
                                    if (_d.btns !== undefined && _d.btns !== null) {
                                        _dOpts.btns = _d.btns;
                                    } else if (_d.confirmOnly) {
                                        _dOpts.btns = ['confirm'];
                                    }
                                    if (_d.calendar !== undefined && _d.calendar !== null) { _dOpts.calendar = _d.calendar; }
                                    if (_d.lang !== undefined && _d.lang !== null) { _dOpts.lang = _d.lang; }
                                    if (_d.mark !== undefined && _d.mark !== null) { _dOpts.mark = _d.mark; }
                                    layui.laydate.render(_dOpts);
                                }
                            }
                        }
                    } catch (e) {
                        if (typeof console !== 'undefined' && console.warn) {
                            console.warn('[WTM] initForm action failed:', e);
                        }
                    }
                    break;
                // Issue #551: thin JSON wrapper over the existing
                // ff.LoadComboItems(controltype, url, controlid, targetname, svals)
                // global — zero new capability, just a declarative re-expression of
                // a plain function call that server code already triggers today via
                // ChainChange/combo markup. Foundation for #552 (no server emitter
                // yet). Field names avoid the 'type' key (reserved for the action
                // discriminator above) — controlType/url/id/field/selectVal map
                // 1:1 onto ff.LoadComboItems's positional parameters.
                //
                // Issue #633 (#470-F): server emitter shipped — ComboBoxTagHelper /
                // CheckBoxTagHelper / RadioTagHelper / TransferTagHelper now emit this
                // action for their ItemUrl branch instead of an inline <script>. Also
                // additively wires the 7th positional arg (disabled) through: the
                // legacy inline script CheckBoxTagHelper emitted always passed an
                // explicit true/false 7th arg
                // (`ff.LoadComboItems('checkbox',url,id,field,vals,undefined,disabled)`)
                // so the freshly-fetched <input> elements come back with the right
                // disabled state. action.disabled is optional — combo/radio/transfer
                // never set it, so `action.disabled === true || action.disabled ===
                // false ? action.disabled : undefined` evaluates to `undefined` for
                // them, identical to the pre-#633 5-arg call (JS leaves an omitted
                // trailing arg `undefined` either way). cb (6th positional arg) has no
                // island caller yet (only the 'tree' controlType uses it) and stays
                // hard-coded `undefined`.
                case 'loadComboItems':
                    if (typeof ff.LoadComboItems === 'function' && action.url && action.id) {
                        ff.LoadComboItems(
                            action.controlType || undefined,
                            action.url,
                            action.id,
                            action.field || undefined,
                            action.selectVal || undefined,
                            undefined,
                            (action.disabled === true || action.disabled === false) ? action.disabled : undefined
                        );
                    }
                    break;
                // Issue #556 (#470-B slice 1): thin JSON wrapper over
                // layui.laydate.render(). The opts object is built entirely
                // server-side (DateTimeTagHelper) and passed straight through —
                // no remapping of opts itself.
                //
                // Issue #470 Slice H: extended to also carry caller-supplied
                // ready/change/done callback NAMES (action.readyFn/changeFn/
                // doneFn) and, for the two-hidden-input range mode, a built-in
                // start/end split (action.rangeStartId/rangeEndId/
                // rangeSplitStr). Both are resolved/wired here, NOT baked into
                // action.opts server-side, because a JSON island can only carry
                // data — never a live function reference.
                //
                // TRUST BOUNDARY: action.readyFn/changeFn/doneFn are ALWAYS
                // compile-time, developer-authored Razor literals (the
                // ReadyFunc/ChangeFunc/DoneFunc TagHelper attribute values) —
                // NEVER field/request/model data, the same trust class as
                // bindSubmit's beforeSubmit (#558) and bindInput's changeFunc/
                // doneFunc (#601). DateTimeTagHelper only ever emits this
                // action for a name that is already a plain identifier; a
                // dotted/call-expression name keeps the legacy inline <script>
                // instead (see DateTimeTagHelper.cs). Resolved through the SAME
                // ff._resolveGuardedWindowFn guard bindSubmit/bindInput use —
                // identifier regex + denylist + own-property + typeof function
                // — so even a future wiring mistake can't turn this into an
                // eval-equivalent primitive. A failed resolution silently skips
                // JUST that one callback and never throws, never evals.
                //
                // action.opts is mutated in place (rather than copied) before
                // being handed to layui.laydate.render — safe because it is a
                // freshly JSON.parse()'d object with no other holders, never
                // shared/re-dispatched, and this keeps the render call itself
                // (`layui.laydate.render(action.opts)`) byte-identical to the
                // pre-#470-Slice-H call site.
                case 'laydate':
                    if (action.opts && action.opts.elem &&
                        typeof layui !== 'undefined' && layui.laydate &&
                        typeof layui.laydate.render === 'function') {
                        try {
                            var _ldDateIns;
                            var _ldReadyFn = ff._resolveGuardedWindowFn(action.readyFn);
                            var _ldChangeFn = ff._resolveGuardedWindowFn(action.changeFn);
                            var _ldDoneFn = ff._resolveGuardedWindowFn(action.doneFn);
                            var _ldRangeStartEl = (typeof action.rangeStartId === 'string')
                                ? document.getElementById(action.rangeStartId) : null;
                            var _ldRangeEndEl = (typeof action.rangeEndId === 'string')
                                ? document.getElementById(action.rangeEndId) : null;
                            var _ldRangeSplitStr = (typeof action.rangeSplitStr === 'string')
                                ? action.rangeSplitStr : ' - ';

                            if (_ldReadyFn) {
                                action.opts.ready = function (value) { _ldReadyFn(value, _ldDateIns); };
                            }
                            if (_ldChangeFn) {
                                action.opts.change = function (value, date, endDate) {
                                    _ldChangeFn(value, date, endDate, _ldDateIns);
                                };
                            }
                            // Issue #470 Slice H: reproduces the legacy inline
                            // range <script>'s done: body exactly — the
                            // built-in start/end split runs FIRST, then (if
                            // present) the caller's DoneFn is chained AFTER it.
                            // When there is no range write-back, a plain
                            // doneFn (if any) is wired on its own.
                            if (_ldRangeStartEl && _ldRangeEndEl) {
                                action.opts.done = function (value, date, endDate) {
                                    _ldRangeStartEl.value = value.split(_ldRangeSplitStr)[0] || '';
                                    _ldRangeEndEl.value = value.split(_ldRangeSplitStr)[1] || '';
                                    if (_ldDoneFn) { _ldDoneFn(value, date, endDate, _ldDateIns); }
                                };
                            } else if (_ldDoneFn) {
                                action.opts.done = function (value, date, endDate) {
                                    _ldDoneFn(value, date, endDate, _ldDateIns);
                                };
                            }

                            _ldDateIns = layui.laydate.render(action.opts);
                        } catch (e) {
                            if (typeof console !== 'undefined' && console.warn) {
                                console.warn('[WTM] laydate action failed:', e);
                            }
                        }
                    }
                    break;
                // Issue #552 (#470-E): thin JSON wrapper over layui.slider.render().
                // Unlike the bare 'laydate' case above (which passes action.opts
                // straight through because DateTimeTagHelper only ever emits
                // already-known-safe static keys), this action rebuilds an
                // ALLOWLISTED opts object key-by-key with a typeof/Array.isArray
                // check on every value (see _renderSliderAction above). This is
                // deliberate defense in depth: any unexpected key (e.g. a
                // 'change'/'setTips' callback name smuggled into the island JSON)
                // is silently dropped because it is never copied, and any expected
                // key whose value isn't the right plain-data shape (e.g. a string
                // where a number is required) is dropped too — a function value or
                // "javascript:..." string can therefore never reach
                // layui.slider.render. Issue #470 Slice I: SliderTagHelper now also
                // emits this action when ChangeFunc/OnTipsFunc are set to a plain
                // identifier — carried as action.changeFn/action.onTipsFn (data,
                // not opts keys, so the allowlist copy above still can't smuggle a
                // function through opts itself) and resolved via
                // ff._resolveGuardedWindowFn in _renderSliderAction. A
                // non-identifier callback expression still routes to the legacy
                // inline <script> instead (see SliderTagHelper).
                //
                // Issue #552 adversarial-review fix (module-load race, HIGH): if
                // layui.slider hasn't finished its async load yet, defer via
                // layui.use(['slider'], cb) — exactly mirroring what the legacy
                // inline <script> did — instead of the previous permanent no-op.
                // This makes the "never silently no-ops due to a not-yet-loaded
                // module" guarantee (already true for the page-ready path via
                // _dispatchIslandWhenReady) hold for the OpenDialog dialog path
                // too. At the time this was written, OpenDialog dispatched
                // islands via a direct ff.DispatchAction call that bypassed
                // _dispatchIslandWhenReady (see the dialog-init dispatch loop in
                // OpenDialog — a separate, lower-risk latent race for the
                // laydate/initForm/bindSubmit/bindValidate action types, tracked
                // as a follow-up, not fixed here to keep this change scoped to
                // the three new action types). Issue #576 closed that follow-up:
                // OpenDialog now routes every dialog-init island through
                // ff._dispatchIslandWhenReady generically, so this per-case
                // guard is redundant belt-and-suspenders defense-in-depth now,
                // not the only guard. If layui ITSELF isn't loaded (not just the
                // submodule), there is no layui.use to defer through — same safe
                // break as before.
                case 'slider':
                    if (!action.opts || !action.opts.elem) { break; }
                    if (typeof layui === 'undefined') { break; }
                    if (layui.slider && typeof layui.slider.render === 'function') {
                        ff._renderSliderAction(action);
                    } else if (typeof layui.use === 'function') {
                        layui.use(['slider'], function () { ff._renderSliderAction(action); });
                    }
                    break;
                // Issue #552 (#470-E): thin JSON wrapper over layui.rate.render().
                // Same allowlist discipline as 'slider' above (see
                // _renderRateAction). RateTagHelper has no developer-facing
                // callback attribute at all, so it always emits this action — the
                // 'choose' write-back (persisting the picked value into the bound
                // hidden input) is the widget's own mandatory framework wiring,
                // reproduced natively in _renderRateAction.
                //
                // Issue #552 adversarial-review fix (module-load race, HIGH): same
                // layui.use(['rate'], cb) deferral as the 'slider' case above.
                case 'rate':
                    if (!action.opts || !action.opts.elem) { break; }
                    if (typeof layui === 'undefined') { break; }
                    if (layui.rate && typeof layui.rate.render === 'function') {
                        ff._renderRateAction(action);
                    } else if (typeof layui.use === 'function') {
                        layui.use(['rate'], function () { ff._renderRateAction(action); });
                    }
                    break;
                // Issue #552 (#470-E): thin JSON wrapper over layui.colorpicker.render().
                // Same allowlist discipline as 'slider'/'rate' above (see
                // _renderColorpickerAction). The 'done' write-back (persisting the
                // picked color into the bound hidden input) is mandatory framework
                // wiring reproduced natively. Issue #470 Slice I: ColorPickerTagHelper
                // now also emits this action when ChangeFunc resolves to a plain
                // identifier — carried as action.changeFn and resolved via
                // ff._resolveGuardedWindowFn in _renderColorpickerAction. A
                // non-identifier ChangeFunc still routes to the legacy inline
                // <script> instead.
                //
                // Issue #552 adversarial-review fix (module-load race, HIGH): same
                // layui.use(['colorpicker'], cb) deferral as the 'slider' case above.
                case 'colorpicker':
                    if (!action.opts || !action.opts.elem) { break; }
                    if (typeof layui === 'undefined') { break; }
                    if (layui.colorpicker && typeof layui.colorpicker.render === 'function') {
                        ff._renderColorpickerAction(action);
                    } else if (typeof layui.use === 'function') {
                        layui.use(['colorpicker'], function () { ff._renderColorpickerAction(action); });
                    }
                    break;
                // Issue #470 Slice G: thin JSON wrapper over
                // layui.ueditor.loadEditor(id).ready(...).setContent(...). See
                // _renderUEditorAction for the full rationale — that function
                // performs its own layui.use(['ueditorconfig'], cb) call
                // (mirroring the legacy inline <script> exactly), so this case
                // just delegates straight through.
                case 'ueditor':
                    ff._renderUEditorAction(action);
                    break;
                // Issue #470 Slice G: thin JSON wrapper over
                // layui.layedit.set/build(...). See _renderLayeditAction for the
                // full rationale — that function performs its own
                // layui.use('layedit', cb) call (mirroring the legacy inline
                // <script> exactly), so this case just delegates straight through.
                case 'layedit':
                    ff._renderLayeditAction(action);
                    break;
                // Issue #470 Slice J: thin JSON wrapper over xmSelect.render(...)
                // for <wt:combobox>/<wt:tree> — the opt-in (UseSelectIslandRender,
                // default OFF) eval-free island render. See _renderSelectAction
                // for the full rationale; that function performs its own
                // existence/guard checks (xmSelect loaded, action shape), so this
                // case just delegates straight through, same pattern as
                // 'ueditor'/'layedit' above.
                case 'renderSelect':
                    ff._renderSelectAction(action);
                    break;
                // Issue #470 Slice K: thin JSON wrapper over
                // layui.transfer.render() — the opt-in (UseSelectIslandRender,
                // default OFF) eval-free island render for <wt:transfer>. See
                // ff._renderTransferAction for the full rationale.
                case 'renderTransfer':
                    ff._renderTransferAction(action);
                    break;
                // Issue #470 Slice L: thin JSON wrappers over
                // layui.upload.render() — the opt-in (UseSelectIslandRender,
                // default OFF) eval-free island render for <wt:upload>/
                // <wt:multiupload>. See ff._renderUploadAction/
                // ff._renderMultiUploadAction for the full rationale; those
                // functions perform their own existence/guard checks (layui.
                // upload loaded, action shape), so these cases just delegate
                // straight through, same pattern as 'ueditor'/'layedit'/
                // 'renderTransfer' above.
                case 'upload':
                    ff._renderUploadAction(action);
                    break;
                case 'multiUpload':
                    ff._renderMultiUploadAction(action);
                    break;
                // Issue #470 Slice L: thin JSON wrapper over the "existing
                // file" init ajax + markup build for <wt:upload>/
                // <wt:multiupload>. See ff._renderUploadExistingAction for
                // the full rationale.
                case 'uploadExisting':
                    ff._renderUploadExistingAction(action);
                    break;
                // Issue #558 (#470-C): safe named-callback submit binding —
                // mechanism only (FormTagHelper does not emit this yet). Mirrors
                // the inline <script> FormTagHelper generates today:
                //   layui.form.on('submit('+filter+')', function(data){
                //     if(BeforeSubmit()==false){return false;}
                //     ff.PostForm(url, formId, divId); return false;
                //   });
                //
                // TRUST BOUNDARY — the invariant the future FormTagHelper wiring
                // MUST uphold: action.beforeSubmit and action.filter are ALWAYS
                // compile-time, developer-authored literals — beforeSubmit is the
                // `<wt:form BeforeSubmit="...">` Razor attribute value, filter is
                // the form's own generated lay-filter id. They are NEVER derived
                // from request / query / form-field / DB / tenant data. Every
                // guard below is defense-in-depth on top of that invariant; the
                // invariant itself is what makes the feature safe, and it must
                // not be weakened when the emitter is wired up.
                //
                // SECURITY: action.beforeSubmit is resolved through a narrow
                // window[name] lookup, never eval/new Function/string-to-code:
                //   1. name must match /^[A-Za-z_$][\w$]*$/ — a plain identifier
                //      only. Dotted ('a.b'), bracketed ('x[0]'), or otherwise
                //      non-identifier strings are rejected outright.
                //   2. name must NOT be in WTM_BEFORESUBMIT_DENYLIST — dangerous
                //      built-in globals (eval, Function, setTimeout, fetch, …)
                //      are own callable window properties whose names pass the
                //      identifier regex, so they are rejected explicitly.
                //   3. name must be an OWN property of window (via
                //      Object.prototype.hasOwnProperty), which blocks inherited
                //      Object.prototype members ('constructor', 'toString') and
                //      prototype-chain tricks ('__proto__') that would otherwise
                //      pass the identifier regex.
                //   4. window[name] must itself be a function.
                // action.filter is likewise validated against the same
                // identifier regex (layui lay-filter ids are always simple
                // identifiers) before it is interpolated into the submit event
                // selector — a non-identifier filter is a no-op (handler not
                // registered). Any failed check silently skips that step (submit
                // still proceeds without the before-hook) — never throws.
                case 'bindSubmit':
                    if (!action.filter || typeof action.filter !== 'string' ||
                        !/^[A-Za-z_$][\w$]*$/.test(action.filter)) { break; }
                    if (typeof layui === 'undefined' || !layui.form ||
                        typeof layui.form.on !== 'function') { break; }
                    // Issue #601 (#470-F): resolved through the shared
                    // ff._resolveGuardedWindowFn helper (extracted from this
                    // case's original inline checks) so TextBoxTagHelper's
                    // 'bindInput' action (ChangeFunc/DoneFunc) below can reuse
                    // the EXACT SAME guard instead of a second, possibly-
                    // drifting copy. Same four checks as before the extraction:
                    // identifier regex, denylist, own-property, typeof function.
                    var _bsBeforeFn = ff._resolveGuardedWindowFn(action.beforeSubmit);
                    layui.form.on(
                        'submit(' + action.filter + ')',
                        ff._makeBindSubmitHandler(
                            _bsBeforeFn,
                            action.formId || '',
                            action.url || '',
                            action.divId || ''
                        )
                    );
                    break;
                // Issue #564 (#470-D): safe auto-validate submit binding — mirrors
                // the inline <script> FormTagHelper generated today:
                //   var {Id}validate = false;
                //   layui.form.on('submit({Id}filterAuto)', function(data){
                //     {Id}validate = true; return false;
                //   });
                // This is the "trigger validation without actually submitting"
                // mechanism used by SubmitButtonTagHelper / LinkButtonTagHelper's
                // custom-click flow: they programmatically click the hidden
                // #{Id}hidesubmit button (lay-filter="{Id}filterAuto"), layui runs
                // its client-side validators, and — ONLY if validation passes —
                // fires this submit(...) handler, which flips the flag those
                // still-inline scripts poll afterwards.
                //
                // SECURITY: action.filter and action.formId are ALWAYS compile-time,
                // developer/framework-authored literals (Id and Id+"filterAuto" from
                // FormTagHelper.Id, itself derived from the VM's UniqueId) — never
                // request/form-field data. Both are still validated against a plain
                // identifier regex before use as defense-in-depth: action.filter is
                // interpolated into the submit(...) event selector (same pattern as
                // bindSubmit above), and action.formId + 'validate' becomes a dynamic
                // window[] property name.
                //
                // The flag is stored as window[formId + 'validate'] rather than
                // declared with a top-level `var` (which is how the legacy inline
                // <script> declared it). A `var` at the top level of a real <script>
                // element executes in global scope, so it becomes a `window`
                // property too — `window[name] = false` reproduces that exact
                // observable state, so SubmitButtonTagHelper / LinkButtonTagHelper's
                // own still-inline scripts (which read/write the bare identifier
                // "{formid}validate") keep working unchanged.
                case 'bindValidate':
                    if (!action.filter || typeof action.filter !== 'string' ||
                        !/^[A-Za-z_$][\w$]*$/.test(action.filter)) { break; }
                    if (!action.formId || typeof action.formId !== 'string' ||
                        !/^[A-Za-z_$][\w$]*$/.test(action.formId)) { break; }
                    if (typeof layui === 'undefined' || !layui.form ||
                        typeof layui.form.on !== 'function') { break; }
                    var _validateVarName = action.formId + 'validate';
                    window[_validateVarName] = false;
                    layui.form.on(
                        'submit(' + action.filter + ')',
                        ff._makeBindValidateHandler(_validateVarName)
                    );
                    break;
                // Issue #564 (#470-D): ModelState error highlight/focus — mirrors
                // the inline <script> FormTagHelper generated today: for every
                // error message, prepend a plain-text label into the form's first
                // submit button's parent container; add 'layui-form-danger' to
                // every field that had at least one error; focus the first
                // errored field.
                //
                // SECURITY: ModelState error messages can carry user/validation-
                // attribute-influenced content, so they are treated as untrusted.
                // Every message is inserted via .textContent — NEVER innerHTML,
                // insertAdjacentHTML, or string-built HTML — so it can never be
                // interpreted as markup (Issue #789 Phase 3B XSS-hardening pattern).
                // action.errors[].field is resolved via document.getElementById — a
                // literal id lookup, never built into a CSS-selector string, so it
                // cannot be used for selector injection — and the resolved element
                // must live inside the form (action.formId) or it is ignored, so a
                // field id can never reach outside its own dialog/island root.
                case 'highlightErrors':
                    if (!action.errors || !Array.isArray(action.errors) || action.errors.length === 0) { break; }
                    if (!action.formId || typeof action.formId !== 'string') { break; }
                    var _heFormEl = document.getElementById(action.formId);
                    if (!_heFormEl) { break; }
                    var _heSubmitBtn = _heFormEl.querySelector('button[type="submit"]');
                    var _heMsgContainer = _heSubmitBtn ? _heSubmitBtn.parentNode : null;
                    var _heFirstFieldId = null;
                    for (var _hei = 0; _hei < action.errors.length; _hei++) {
                        var _heErr = action.errors[_hei];
                        if (!_heErr) { continue; }
                        if (_heFirstFieldId === null && _heErr.field) { _heFirstFieldId = _heErr.field; }
                        if (_heMsgContainer) {
                            var _heMsg = (_heErr.message === undefined || _heErr.message === null) ? '' : String(_heErr.message);
                            var _heMsgDiv = document.createElement('div');
                            _heMsgDiv.className = 'layui-input-block';
                            _heMsgDiv.style.textAlign = 'left';
                            var _heLabel = document.createElement('label');
                            _heLabel.style.color = 'red';
                            // XSS-safe: textContent never parses the string as HTML.
                            _heLabel.textContent = _heMsg;
                            _heMsgDiv.appendChild(_heLabel);
                            _heMsgContainer.insertBefore(_heMsgDiv, _heMsgContainer.firstChild);
                        }
                        if (_heErr.field && typeof _heErr.field === 'string') {
                            var _heFieldEl = document.getElementById(_heErr.field);
                            if (_heFieldEl && _heFormEl.contains(_heFieldEl)) {
                                _heFieldEl.classList.add('layui-form-danger');
                            }
                        }
                    }
                    if (action.focusFirst && _heFirstFieldId) {
                        var _heFocusEl = document.getElementById(_heFirstFieldId);
                        if (_heFocusEl && _heFormEl.contains(_heFocusEl) &&
                            typeof _heFocusEl.focus === 'function') {
                            _heFocusEl.focus();
                        }
                    }
                    break;
                // Issue #571: thin dispatcher for the native, dependency-free
                // tag/chip input (replaces the non-functional layui.tagInput
                // module reference — see TagInputTagHelper's doc comment). No
                // layui module is required (pure DOM widget), so — unlike
                // slider/rate/colorpicker (#552) — there is no layui.use
                // deferral needed here; the render body itself
                // (_renderTagInputAction) is directly safe to call as soon as
                // the container element exists in the DOM. TagInputTagHelper
                // never had a developer-facing callback attribute, so — like
                // RateTagHelper — it unconditionally emits this action.
                case 'tagInput':
                    ff._renderTagInputAction(action);
                    break;
                // Issue #601 (#470-F): safe named-callback input/change binding
                // for <wt:textbox ChangeFunc="..." DoneFunc="..." />, mirroring
                // how #558's 'bindSubmit' migrated FormTagHelper's BeforeSubmit
                // off an inline <script>. TextBoxTagHelper used to emit raw
                // oninput="fn(this.value)" / onchange="fn(this.value)"
                // attributes — ff.SafeHtml's FORBID_ATTR strips 'onchange'
                // outright and DOMPurify's own default allowlist already drops
                // any other on* handler attribute (neither is in the #591
                // ADD_ATTR allowlist, and adding either back would reopen the
                // inline-event-handler XSS class #591 closed) — so
                // ChangeFunc/DoneFunc silently died on every dialog render.
                // No layui module is required (pure DOM addEventListener), so —
                // like 'tagInput' above — there is no layui.use deferral needed
                // here.
                //
                // TRUST BOUNDARY: action.changeFunc/action.doneFunc are ALWAYS
                // compile-time, developer-authored Razor literals (the
                // ChangeFunc/DoneFunc TagHelper attribute values) — NEVER
                // field/request/model data. TextBoxTagHelper only emits this
                // action for a name that is already a plain identifier; a
                // dotted/call-expression name keeps the exact legacy inline
                // attribute instead (see TextBoxTagHelper.cs). Resolved through
                // the SAME ff._resolveGuardedWindowFn guard 'bindSubmit' uses —
                // identifier regex + denylist + own-property + typeof function
                // — so even a future wiring mistake can't turn this into an
                // eval-equivalent primitive. A failed resolution silently skips
                // JUST that one callback and never throws.
                //
                // NOTE on `this`: the legacy inline attribute
                // (onchange="fn(this.value)") is a DOM0 event-handler-attribute
                // call, where `this` inside the handler body is bound to the
                // ELEMENT (DOM0 semantics — NOT window, despite the bare-call
                // syntax). addEventListener's listener is also invoked with
                // `this` === the element it is bound to, so calling
                // fn(el.value) here reproduces the exact same (fn, argument)
                // pair the legacy attribute produced — the only thing that
                // changes is how the LISTENER ITSELF is invoked, never what fn
                // receives. No known consumer relies on `this` inside fn (fn is
                // always called with a single positional value argument, never
                // .apply()'d against a `this`).
                //
                // Containment mirrors #578/#585: when action.formId is
                // present, the target element must resolve INSIDE that form
                // (formEl.contains(el)) or the whole bind is skipped. Element
                // resolution is BY ID (document.getElementById) — never a
                // page-wide CSS-selector query — and an absent formId simply
                // skips the check (back-compat with islands rendered outside a
                // <wt:form>).
                case 'bindInput':
                    if (!action.elemId || typeof action.elemId !== 'string') { break; }
                    var _biEl = document.getElementById(action.elemId);
                    if (!_biEl) { break; }
                    var _biFormId = (typeof action.formId === 'string') ? action.formId : null;
                    var _biFormEl = _biFormId ? document.getElementById(_biFormId) : null;
                    if (_biFormId && (!_biFormEl || !_biFormEl.contains(_biEl))) { break; }
                    var _biChangeFn = ff._resolveGuardedWindowFn(action.changeFunc);
                    var _biDoneFn = ff._resolveGuardedWindowFn(action.doneFunc);
                    if (_biChangeFn) {
                        _biEl.addEventListener('input', function () { _biChangeFn(_biEl.value); });
                    }
                    if (_biDoneFn) {
                        _biEl.addEventListener('change', function () { _biDoneFn(_biEl.value); });
                    }
                    break;
                // Issue #649 (second pre-release Codex adversarial review):
                // CheckBoxTagHelper.cs / RadioTagHelper.cs stopped emitting this
                // island. #646 had restored their synchronous inline
                // "{Id}defaultvalues = [...];" <script> write ALONGSIDE this
                // island; on a default full-page load the inline write ran first
                // (parse time, server values), app-authored JS could legitimately
                // mutate the resulting global afterward, and then — at
                // DOMContentLoaded — this case unconditionally overwrote it back
                // to the original server values, silently clobbering the app's
                // change. The fix removed the EMITTER (the island), not this
                // DispatchAction case: the case itself stays, as documented
                // back-compat infrastructure for hand-authored `wtm-dialog-init`
                // islands that still use this shape (its dispatch-mechanism
                // hardening below — id grammar, values coercion — has its own
                // direct test coverage), but no framework TagHelper output can
                // reach it anymore, so the #649 clobber cannot recur through any
                // framework-generated markup.
                //
                // Issue #632 (redesigned — data as markup, not script): BACK-COMPAT
                // ONLY replacement for CheckBoxTagHelper / RadioTagHelper's former
                // per-widget
                //   {Id}defaultvalues = [...];
                // inline <script>. The FRAMEWORK no longer reads this global for
                // those two control types at all — ff.ChainChange reads the
                // data-wtm-defaults HTML attribute instead (see
                // ff._readFieldDefaults) — so this action exists purely so
                // app-authored JS that still reads window[id + 'defaultvalues']
                // directly keeps working, and its dispatch timing (ordinary
                // island timing: DOMContentLoaded / dialog-fragment-insertion) is
                // safe precisely BECAUSE nothing the framework reads depends on
                // it anymore. This is the design the REJECTED fieldDefaults-
                // island-AS-AUTHORITATIVE approach could not claim: this island's
                // write is data consumed only by code OUTSIDE this file.
                //
                // TRUST BOUNDARY (deliberately narrower than bindSubmit/
                // bindValidate/bindInput's action.id/name grammar): this action
                // never RESOLVES-AND-CALLS a window[] property as a function — it
                // only WRITES action.values (JSON data, decoded by JSON.parse
                // before DispatchAction ever sees it — never code, no eval, no
                // Function constructor) behind a FIXED, non-configurable
                // 'defaultvalues' suffix. That fixed suffix — never a
                // caller-supplied path, dotted, or bracketed accessor — is what
                // guarantees the write can only ever land on
                // window[<action.id>+'defaultvalues'], never an arbitrary global
                // or a real prototype-chain property, even for identifier-shaped
                // ids like '__proto__' or 'constructor' (those become the
                // harmless property names "__proto__defaultvalues" /
                // "constructordefaultvalues", not the special __proto__/
                // constructor bindings). Given that, a strict ASCII identifier
                // grammar (/^[A-Za-z_$][\w$]*$/, the bindSubmit/bindValidate/
                // bindInput grammar) would do no additional security work here —
                // it would only reject legitimate ids: WTM's own
                // Utils.GetIdByName only strips '.'/'['/']'/'-', so ids derived
                // from non-ASCII model/property names (e.g. the ConsoleDemo's
                // 不要用中文模型名_View_模型名 fixture) are valid, real ids in
                // production. This case therefore validates only that action.id
                // is a non-empty string — matching the 'loadComboItems' case
                // above, which has always passed its own unvalidated `action.id`
                // straight through for the identical "write-only, id used as an
                // opaque handle" reason. action.values is coerced to [] when it
                // is not an array, so a malformed payload can never publish
                // something ff._readFieldDefaults' consumers wouldn't already
                // tolerate.
                //
                // A `u`-flag / \p{ID_Start}/\p{ID_Continue} Unicode identifier
                // regex was considered (and rejected) as a middle ground between
                // "strict ASCII grammar" and "no grammar": framework_layui.js
                // ships to the browser completely unbundled/untranspiled (no
                // babel/webpack step anywhere in this repo touches it) and is
                // otherwise ES5-flavored (plain `var`, no arrow functions, no
                // template literals), so a `u`-flag regex LITERAL would be a hard
                // SyntaxError — not a mere missing-feature no-op — on any engine
                // predating widespread Unicode property escape support,
                // breaking this file's ENTIRE parse (every action type, not just
                // this one) rather than just mis-handling one non-ASCII id. A
                // plain typeof/non-empty check carries none of that risk and, per
                // the write-only analysis above, loses no real security coverage.
                // #649: retained as a no-op; no server emitter after checkbox/radio
                // stopped islanding defaults.
                case 'fieldDefaults':
                    if (action.id && typeof action.id === 'string') {
                        window[action.id + 'defaultvalues'] = Array.isArray(action.values) ? action.values : [];
                    }
                    break;
                default:
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] Unknown WtmAction type:', action.type);
                    }
            }
        }
    },

    // Issue #627: opt-in kill-switch for FOUR legacy dynamic-script-execution
    // points in this file: this helper's dynamic eval-based fallback, the two
    // <script>-element re-injection loops in ff._replayInitFromHtml /
    // ff.OpenDialog's layer.open success callback, and ff.OpenDialog2's
    // $$script$$/$$#script$$ selector search-panel template rehydration
    // (review follow-up to the original #627 commit, which gated only the
    // first three).
    // Issue #722: ff.OpenDialog2 also now calls ff._replayInitFromHtml
    // directly (to replay Selector.cshtml's own top-level init script and
    // the wt:grid TagHelper's table.render call, collected from its ajax
    // response body) — this is a NEW CALL SITE of the existing point 2
    // (ff._replayInitFromHtml), not a new/fifth gated point: the gating
    // logic itself is unchanged, OpenDialog2 just reuses it the same way
    // ff.OpenDialog already does.
    // The common form-init paths (form init/submit/validate/error-highlight,
    // laydate, rate, taginput — plus slider/colorpicker in their
    // callback-free configurations) have been island-driven since
    // v10.13.12/13 (#470) and never touch these four points. But MANY
    // framework TagHelper configurations still ride legacy rehydration when
    // loaded inside an AJAX dialog/fragment — for example ComboBoxTagHelper's
    // xmSelect.render block (every combobox), DateTimeTagHelper's callback
    // and range branches, FormTagHelper's legacy inline submit <script> for
    // a non-identifier BeforeSubmit, and SelectorTagHelper's
    // $$script$$-tokenized search panel, among others
    // (transfer/ueditor/upload/checkbox-radio-defaults/grids; see
    // docs/csp-hardening.md for the audit method). So this is NOT purely an
    // "app-authored inline <script> in partials" concern. The kill-switch
    // blocks all of the above too, by design, with loud diagnostics: apps
    // whose AJAX-loaded content passes the audit can flip the switch to
    // unblock strict CSP (script-src without 'unsafe-inline').
    // Checked via EITHER mechanism:
    //   1. ff.DisableLegacyScriptRehydration === true (strict boolean check
    //      — a truthy string like 'true' does NOT count, only the literal
    //      boolean true, so a stray misconfiguration can't silently flip
    //      this on)
    //   2. <meta name="wtm-disable-legacy-script-rehydration" content="true">
    //      present in the document, with content exactly the string 'true'
    // Read LIVE on every call — never cached — because SPA layouts and test
    // harnesses may toggle either mechanism at runtime between calls. Both
    // mechanisms default to ABSENT, so this returns false unless an app
    // opts in: zero behaviour change for existing deployments (project red
    // line — see CLAUDE.md "never silently change default behaviour").
    _isLegacyRehydrationDisabled: function () {
        if (ff.DisableLegacyScriptRehydration === true) { return true; }
        if (typeof document !== 'undefined' && typeof document.querySelector === 'function') {
            var _meta = document.querySelector('meta[name="wtm-disable-legacy-script-rehydration"]');
            if (_meta && _meta.getAttribute('content') === 'true') { return true; }
        }
        return false;
    },

    // Issue #789 Phase 3C: centralized legacy fallback for the deprecated
    // IsScript response header. Every call site routes through this single
    // helper so the total number of eval( tokens in this file is 1 (down
    // from 18 before Phase 1), making the removal of this helper a one-line
    // change once downstream apps have finished migrating to FFResultJson.
    _legacyScriptEval: function (code) {
        // Issue #627: kill-switch check — when disabled, block execution
        // entirely instead of eval'ing. See ff._isLegacyRehydrationDisabled.
        if (ff._isLegacyRehydrationDisabled()) {
            if (typeof console !== 'undefined' && console.error) {
                console.error('[WTM] Legacy IsScript script-body response BLOCKED by DisableLegacyScriptRehydration. Migrate the server action to FFResultJson() (X-WTM-Action). See #627/#470.');
            }
            return;
        }
        if (typeof console !== 'undefined' && console.warn) {
            console.warn('[WTM] IsScript script-body response is deprecated. ' +
                         'Migrate server-side controllers to FFResultJson() ' +
                         '(X-WTM-Action header) to enable strict CSP. See #789 Phase 3C.');
        }
        // eslint-disable-next-line no-eval
        eval(code);
    },

    // Issue #789 Phase 3C: simple fire-and-dispatch helper for "click the
    // button to run a server action" pattern (used by LayuiUIService button
    // generation). Replaces the former inline eval(data) in the generated
    // onclick handler. Response handling matches PostForm/BgRequest: prefer
    // X-WTM-Action JSON over IsScript eval fallback.
    RunAction: function (url) {
        $.ajax({
            cache: false,
            type: 'GET',
            url: url,
            async: true,
            error: function () {
                if (typeof layui !== 'undefined' && layui.layer) {
                    layui.layer.alert(ff.DONOTUSE_Text_LoadFailed);
                }
            },
            success: function (data, textStatus, request) {
                var wtmAction = request.getResponseHeader('X-WTM-Action');
                if (wtmAction === 'application/json') {
                    try {
                        ff.DispatchAction(typeof data === 'string' ? JSON.parse(data) : data);
                    } catch (e) {
                        if (typeof console !== 'undefined' && console.error) {
                            console.error('[WTM] RunAction JSON parse failed:', e);
                        }
                    }
                } else if (request.getResponseHeader('IsScript') === 'true') {
                    ff._legacyScriptEval(data);
                }
            }
        });
    },

    SetCookie: function (name, value, allwindow) {
        try {
            var cookiePrefix = '', windowGuid = '';

            if ("undefined" !== typeof DONOTUSE_COOKIEPRE) {
                cookiePrefix = DONOTUSE_COOKIEPRE;
            }
            if ("undefined" !== typeof DONOTUSE_WINDOWGUID) {
                windowGuid = DONOTUSE_WINDOWGUID;
            }

            if (allwindow) {
                $.cookie(cookiePrefix + name, value);
            }
            else {
                $.cookie(cookiePrefix + windowGuid + name, value);
            }
        }
        catch (e) { }
    },

    GetCookie: function (name, allwindow) {
        try {
            var cookiePrefix = '', windowGuid = '';
            if ("undefined" !== typeof DONOTUSE_COOKIEPRE) {
                cookiePrefix = DONOTUSE_COOKIEPRE;
            }
            if ("undefined" !== typeof DONOTUSE_WINDOWGUID) {
                windowGuid = DONOTUSE_WINDOWGUID;
            }
            if (allwindow) {
                return $.cookie(cookiePrefix + name);
            }
            else {
                return $.cookie(cookiePrefix + windowGuid + name);

            }
        }
        catch (e) { }
    },

    GetSelections: function (gridId) {
        var checkStatus = layui.table.checkStatus(gridId);
        var data = checkStatus.data;
        var ids = [];
        if (data.length > 0) {
            for (var i = 0; i < data.length; i++) {
                ids.push(data[i].ID);
            }
        }
        return ids;
    },

    GetNonSelections: function (gridId) {
        var table = layui.table
            , nums = 0
            , ids = [] // 未选中id
            , data = table.cache[gridId] || [];
        //计算未选中个数
        layui.each(data, function (i, item) {
            if (item.constructor === Array) {
                return; //无效数据，或已删除的
            }
            if (!item[table.config.checkName]) {
                nums++;
                ids.push(item.ID);
            }
        });
        return ids;
    },

    GetSelectionData: function (gridId) {
        return layui.table.checkStatus(gridId).data;
    },

    GetNonSelectionData: function (gridId) {
        var table = layui.table
            , nums = 0 // 未选中个数
            , invalidNum = 0
            , arr = [] // 未选中数据
            , data = table.cache[gridId] || [];
        //计算未选中个数
        layui.each(data, function (i, item) {
            if (item.constructor === Array) {
                invalidNum++; //无效数据，或已删除的
                return;
            }
            if (!item[table.config.checkName]) {
                nums++;
                arr.push(table.clearCacheKey(item));
            }
        });
        return arr;
    },

    GetIsSelectAll: function (gridId) {
        return layui.table.checkStatus(gridId).isAll;
    },

    Alert: function (msg, title) {
        var layer = layui.layer;
        if (title != undefined) {
            layer.alert(msg, { title: title });
        }
        else {
            layer.alert(msg);
        }
    },

    Msg: function (msg, title) {
        var layer = layui.layer;
        if (title != undefined) {
            layer.msg(msg, { title: title });
        }
        else {
            layer.msg(msg);
        }
    },

    LoadPage: function (url, newwindow, title, para) {
        this.SetCookie("windowids", null);
        var layer = layui.layer;
        var index = layer.load(2);
        url = decodeURIComponent(url);
        var furl = url;
        var re = /(\/_framework\/outside\?url=)(.*?)$/ig;
        url = url.replace(re, function (match, p1, p2) {
            return p1 + encodeURIComponent(p2);
        });
        if (newwindow === true || para !== undefined) {
            var getpost = "GET";
            if (para !== undefined) {
                getpost = "Post";
            }
            var child = window.open("/Home/PIndex/#" + url);
            $(child.document).ready(function() {
            setTimeout(function() {
                    $(child.document).attr("title", title);
                }, 500);
            });
            layer.close(index);
        }
        else {
            layer.close(index);
            location.hash = url;
        }
    },


    LoadPage1: function (url, where) {
        url = url.toLowerCase();
        if (url.indexOf("http://") === 0 || url.indexOf("https://") === 0) {
            // Issue #789 Phase 3A: build iframe via DOM API so the url is set
            // through the .src setter (which safely serializes any quotes or
            // HTML metacharacters) instead of string-concat into innerHTML.
            var _iframe = document.createElement('iframe');
            _iframe.setAttribute('frameborder', 'no');
            _iframe.setAttribute('border', '0');
            _iframe.setAttribute('height', '100%');
            _iframe.src = url;
            $('#' + where).empty().append(_iframe);
            $('#' + where).css("overflow-y", "auto");
        }
        else {
            var layer = layui.layer, index = layer.load(2);
            $.ajax({
                url: decodeURIComponent(url),
                type: 'GET',
                success: function (data) {
                    // Issue #789 Phase 3B: sanitize raw AJAX response before writing HTML.
                    $('#' + where).html(ff.SafeHtml(data));
                    $('#' + where).css("overflow-y", "scroll");
                    layer.close(index);
                },
                error: function (xhr, status, error) {
                    layer.close(index);
                    layer.alert(ff.DONOTUSE_Text_LoadFailed);
                },
                complete: function () {
                    ff.SetCookie("windowids", null);
                }
            });
        }
    },

    GetPostData: function (formId) {
        var richtextbox = $("#" + formId + " textarea");
        for (var i = 0; i < richtextbox.length; i++) {
            var ra = richtextbox[i].attributes['layeditindex'];
            if (ra !== undefined && ra != null) {
                var rindex = ra.value;
                layui.layedit.sync(rindex);
            }
        }
        var combobox = $('#' + formId + ' :checkbox');

        var datastr = $('#' + formId).serialize();
        var checkboxes = $('#' + formId + ' :checkbox');
        for (i = 0; i < checkboxes.length; i++) {
            var ck = checkboxes[i];
            if (ck.checked === false && (ck.value === 'true' || ck.value === 'false')) {
                datastr += "&" + ck.name + "=false";
            }
        }
        return datastr;
    },

    RenderForm: function (formId) {
        var comboxs = $(".layui-form[lay-filter=" + formId + "] div[wtm-ctype='combo']");
            layui.use(['form'], function () {
                var form = layui.form.render(null, formId);
            });
    },

    // Issue #601 (#470-F): shared guarded window[name] resolver — extracted
    // from the #558 'bindSubmit' case's original inline checks (beforeSubmit)
    // so 'bindInput' (TextBoxTagHelper's ChangeFunc/DoneFunc, #601) can reuse
    // the EXACT SAME guard instead of a second, possibly-drifting copy. Both
    // callers share the SAME trust boundary: `name` is ALWAYS a compile-time,
    // developer-authored Razor literal (BeforeSubmit / ChangeFunc / DoneFunc
    // TagHelper attribute values) — NEVER request/field/model data. Every
    // check below is defense-in-depth on top of that invariant:
    //   1. name must match /^[A-Za-z_$][\w$]*$/ — a plain identifier only.
    //      Dotted ('a.b'), bracketed ('x[0]'), or otherwise non-identifier
    //      strings are rejected outright.
    //   2. name must NOT be in WTM_BEFORESUBMIT_DENYLIST — dangerous built-in
    //      globals (eval, Function, setTimeout, fetch, …) are own callable
    //      window properties whose names pass the identifier regex, so they
    //      are rejected explicitly.
    //   3. name must be an OWN property of window (via
    //      Object.prototype.hasOwnProperty), which blocks inherited
    //      Object.prototype members ('constructor', 'toString') and
    //      prototype-chain tricks ('__proto__') that would otherwise pass the
    //      identifier regex.
    //   4. window[name] must itself be a function.
    // Any failed check returns null — the caller silently skips that one
    // callback/gate and never throws.
    _resolveGuardedWindowFn: function (name) {
        if (name &&
            typeof name === 'string' &&
            /^[A-Za-z_$][\w$]*$/.test(name) &&
            !(WTM_BEFORESUBMIT_DENYLIST && WTM_BEFORESUBMIT_DENYLIST.has(name)) &&
            Object.prototype.hasOwnProperty.call(window, name) &&
            typeof window[name] === 'function') {
            return window[name];
        }
        return null;
    },

    // Issue #558 (#470-C): factory for the 'bindSubmit' DispatchAction case's
    // layui.form.on('submit(...)') callback. Returning a fresh closure per
    // call (rather than referencing DispatchAction's loop-scoped `action`
    // variable directly from an inline function) avoids the classic
    // var-in-a-loop capture bug: each invocation freezes its own
    // beforeFn/formId/url/divId, so multiple bindSubmit actions registered in
    // the same or different DispatchAction calls never share state, even
    // though their submit handlers fire asynchronously long after the
    // dispatch loop that created them has finished.
    // NOTE: the caller's `filter` MUST be the form's own unique lay-filter —
    // layui overwrites any handler that shares a (module, filter) key, so a
    // filter collision would silently bind the wrong form's submit handler.
    _makeBindSubmitHandler: function (beforeFn, formId, url, divId) {
        return function (data) {
            if (beforeFn && beforeFn(data) === false) { return false; }
            ff.PostForm(url, formId, divId);
            return false;
        };
    },

    // Issue #564 (#470-D): factory for the 'bindValidate' DispatchAction case's
    // layui.form.on('submit(...)') callback. Same var-in-a-loop-capture rationale
    // as _makeBindSubmitHandler above — each invocation freezes its own varName,
    // so multiple bindValidate actions (multiple forms/dialogs on the same page)
    // never share state even though their submit handlers fire asynchronously.
    _makeBindValidateHandler: function (varName) {
        return function (data) {
            window[varName] = true;
            return false;
        };
    },

    PostForm: function (url, formId, divid, searchervm) {
        var layer = layui.layer;
        var index = layer.load(2);
        if (url === undefined || url === "") {
            url = $("#" + formId).attr("action");
        }
        var d = null;
        if ($("#" + formId).find("a[IsSearchButton]").length>0) {
            d = ff.GetSearchFormData(formId, searchervm);
        }
        else {
            d = ff.GetFormData(formId)
        }
        $.ajax({
            cache: false,
            type: "POST",
            url: url,
            data: d,
            async: true,
            error: function (request) {
                layer.close(index);
                if (request.responseText !== undefined && request.responseText !== '') {
                    // Issue #332: wrap server error text with EscapeText to prevent
                    // XSS via HTML-injected responseText (layer.alert parses HTML).
                    layer.alert(ff.EscapeText(request.responseText));
                } else {
                    layer.alert(ff.DONOTUSE_Text_SubmitFailed);
                }
            },
            success: function (data, textStatus, request) {
                var wtmActionHdr = request.getResponseHeader('X-WTM-Action');
                if (wtmActionHdr === 'application/json') {
                    // Issue #789 Phase 3C: CSP-safe JSON action dispatch.
                    try {
                        ff.DispatchAction(typeof data === 'string' ? JSON.parse(data) : data);
                    } catch (e) {
                        if (typeof console !== 'undefined' && console.error) {
                            console.error('[WTM] PostForm X-WTM-Action parse failed:', e);
                        }
                    }
                    layer.close(index);
                    return;
                }
                if (request.getResponseHeader('IsScript') === 'true') {
                    ff._legacyScriptEval(data);
                }
                else {
                    // Issue #587: pre-collect legacy inline <script> bodies and
                    // .wtm-dialog-init JSON islands from the same-origin response
                    // HTML via a DETACHED DOMParser, BEFORE ff.SafeHtml/DOMPurify
                    // strips <script> elements from the markup inserted below —
                    // mirrors ff.OpenDialog's #462/#470/#522 extraction via the
                    // shared ff._collectInitFromHtml helper (see its comment for
                    // why OpenDialog's own inline extraction is left untouched).
                    // Without this, a validation-failure redraw of an islandized
                    // form (initForm/bindSubmit/bindValidate/laydate/slider/rate/
                    // colorpicker fields — islandized since #556/#558/#564/#552)
                    // silently lost all of that init: SafeHtml strips the
                    // island/script and nothing ever re-ran it (Gap 2, #587).
                    var _pfCollected = ff._collectInitFromHtml(data);

                    // Issue #789 Phase 3A: build wrapper via jQuery .attr() so the
                    // cookie-sourced id is set through setAttribute (safe) instead
                    // of being concatenated into an HTML string (breakable).
                    var inlayer = $("#" + formId).parents(".layui-layer-content");
                    var _wrapperClass = (inlayer !== undefined && inlayer.length > 0)
                        ? 'donotuse_pdiv'
                        : 'layui-card-body donotuse_pdiv';
                    var _wrapper = $('<div/>')
                        .attr('id', $.cookie("divid") || '')
                        .addClass(_wrapperClass)
                        .html(ff.SafeHtml(data));
                    $("#" + divid).parent().empty().append(_wrapper);

                    // Issue #587: now that the redrawn fragment is attached to the
                    // live document, replay the collected legacy scripts and
                    // dispatch the collected islands — same ordering (scripts,
                    // then islands) as ff.OpenDialog's layer.open success callback.
                    ff._replayInitFromHtml(_pfCollected);
                }
                layer.close(index);
            }
        });
    },

    BgRequest: function (url, para, divid) {
        var layer = layui.layer;
        var index = layer.load(2);
        var getpost = "GET";
        if (para !== undefined) {
            getpost = "Post";
        }
        $.ajax({
            cache: false,
            type: getpost,
            url: url,
            data: para,
            async: true,
            error: function (request) {
                layer.close(index);
                if (request.responseText !== undefined && request.responseText !== "") {
                    // Issue #332: wrap server error text with EscapeText to prevent
                    // XSS via HTML-injected responseText (layer.alert parses HTML).
                    layer.alert(ff.EscapeText(request.responseText));
                }
                else {
                    layer.alert(ff.DONOTUSE_Text_LoadFailed);
                }
            },
            success: function (str, textStatus, request) {
                layer.close(index);
                var wtmActionHdr = request.getResponseHeader('X-WTM-Action');
                if (wtmActionHdr === 'application/json') {
                    // Issue #789 Phase 3C: CSP-safe JSON action dispatch.
                    try {
                        ff.DispatchAction(typeof str === 'string' ? JSON.parse(str) : str);
                    } catch (e) {
                        if (typeof console !== 'undefined' && console.error) {
                            console.error('[WTM] BgRequest X-WTM-Action parse failed:', e);
                        }
                    }
                    return;
                }
                if (request.getResponseHeader('IsScript') === 'true') {
                    ff._legacyScriptEval(str);
                }
                else {
                    // Issue #789 Phase 3A: same cookie-to-id fix as PostForm above.
                    var _wrapper = $('<div/>')
                        .attr('id', $.cookie("divid") || '')
                        .addClass('layui-card-body donotuse_pdiv')
                        .html(ff.SafeHtml(str));
                    $("#" + divid).parent().empty().append(_wrapper);
                }
            }
        });

    },

    OpenDialog: function (url, windowid, title, width, height, para, maxed) {
        var layer = layui.layer;
        var index = layer.load(2);
        var wid = this.GetCookie("windowids");
        var owid = wid;
        if (wid === null || wid === '') {
            wid = windowid;
        }
        else {
            wid += "," + windowid;
        }
        this.SetCookie("windowids", wid);
        if ("undefined" !== typeof DONOTUSE_WINDOWGUID) {
            this.SetCookie("windowguid", DONOTUSE_WINDOWGUID, true);
        }
        var getpost = "GET";
        if (para !== undefined) {
            getpost = "Post";
        }

        $.ajax({
            cache: false,
            type: getpost,
            url: url,
            data: para,
            async: true,
            error: function (xhr) {
                layer.close(index);
                let location = xhr.getResponseHeader("Location");
                if (location) {
                    // Issue #332: validate Location header shape before redirect —
                    // reject absolute URLs, protocol-relative (//), javascript:, data:.
                    // Mirrors DispatchAction 'redirect' guard (Issue #804).
                    // Issue #534: also reject a leading '/' followed by '\' —
                    // browsers normalize it to '/' ("/\evil.com" -> "//evil.com").
                    var _loc = location;
                    if (/^\/(?:[^/\\]|$)/.test(_loc) ||
                        _loc.charAt(0) === '#' || _loc.charAt(0) === '?') {
                        window.location = _loc;
                        return false;
                    }
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] OpenDialog redirect blocked: non-relative Location header', _loc);
                    }
                    return false;
                }
                ff.SetCookie("windowids", owid);
                if (xhr.responseText !== undefined && xhr.responseText !== "") {
                    // Issue #332: wrap server error text with EscapeText to prevent
                    // XSS via HTML-injected responseText (layer.alert parses HTML).
                    layer.alert(ff.EscapeText(xhr.responseText));
                }
                else {
                    layer.alert(ff.DONOTUSE_Text_LoadFailed);
                }
            },
            success: function (str, textStatus, request) {
                layer.close(index);
                var max = true;
                var wtmActionHdr = request.getResponseHeader('X-WTM-Action');
                if (wtmActionHdr === 'application/json') {
                    // Issue #789 Phase 3C: CSP-safe JSON action dispatch.
                    ff.SetCookie("windowids", owid);
                    try {
                        ff.DispatchAction(typeof str === 'string' ? JSON.parse(str) : str);
                    } catch (e) {
                        if (typeof console !== 'undefined' && console.error) {
                            console.error('[WTM] OpenDialog X-WTM-Action parse failed:', e);
                        }
                    }
                    return;
                }
                if (request.getResponseHeader('IsScript') === 'true') {
                    ff.SetCookie("windowids", owid);
                    ff._legacyScriptEval(str);
                }
                else {
                    // Issue #462: extract trusted inline init scripts from the same-origin partial
                    // BEFORE DOMPurify strips them, then re-run after the dialog DOM is inserted.
                    // Use DOMParser (NOT regex) so only real <script> ELEMENTS are taken — a
                    // "<script>" string sitting inside an attribute value or text node is NOT a
                    // script element and must never be executed (regex would have eval'd it,
                    // widening XSS beyond pre-#789). DOMParser does not execute scripts itself.
                    // Markup is still sanitized via ff.SafeHtml below.
                    var _initScripts = [];
                    try {
                        var _pdoc = new DOMParser().parseFromString(str, 'text/html');
                        var _nodes = _pdoc.querySelectorAll('script');
                        for (var _ni = 0; _ni < _nodes.length; _ni++) {
                            var _s = _nodes[_ni];
                            var _type = (_s.getAttribute('type') || '').toLowerCase();
                            var _isJs = _type === '' || _type === 'text/javascript' || _type === 'application/javascript' || _type === 'module';
                            // inline JS only — skip external src and non-JS data blocks (e.g. application/json)
                            if (_isJs && !_s.src && _s.textContent) {
                                _initScripts.push(_s.textContent);
                            }
                        }
                    } catch (e) { /* malformed HTML → no init scripts; markup still rendered via SafeHtml */ }
                    // Issue #470: extract opt-in JSON action island(s) (<script type="application/json"
                    // class="wtm-dialog-init">) from the same-origin partial BEFORE SafeHtml strips
                    // the script elements. Each island payload is dispatched via ff.DispatchAction
                    // after the dialog DOM is inserted — zero eval, no dynamic code.
                    // Issue #556 (#470-B slice 1): querySelectorAll (not querySelector) — a partial
                    // can now carry more than one island (e.g. a <wt:dialog-init> form island plus
                    // one bare 'laydate' island per migrated date field). Each is parsed and
                    // normalized independently so one malformed island doesn't drop the others.
                    // No dialog/page-ready double-dispatch: these islands are read from _pdoc, a
                    // DETACHED DOMParser document, BEFORE ff.SafeHtml (DOMPurify, FORBID_TAGS:
                    // ['script']) strips ALL <script> elements — including these — from the markup
                    // that actually gets inserted into the live `document` below. The live
                    // `document` therefore never contains a dialog-origin island, so the page-ready
                    // consumer (ff._consumePageReadyIslands, which only scans the live `document`)
                    // can never see or re-dispatch one of these.
                    var _dialogInitPayloads = [];
                    try {
                        if (typeof _pdoc !== 'undefined') {
                            var _islandNodes = _pdoc.querySelectorAll('script[type="application/json"].wtm-dialog-init');
                            for (var _ii = 0; _ii < _islandNodes.length; _ii++) {
                                var _islandNode = _islandNodes[_ii];
                                if (!_islandNode || !_islandNode.textContent) { continue; }
                                try {
                                    var _parsed = JSON.parse(_islandNode.textContent);
                                    var _normalized = ff._normalizeIslandPayload(_parsed);
                                    if (_normalized !== null) { _dialogInitPayloads.push(_normalized); }
                                } catch (e) { /* malformed single island JSON → skip that island only */ }
                            }
                        }
                    } catch (e) { /* querySelectorAll failure → no islands; legacy path unaffected */ }
                    // Issue #789 Phase 3A: build wrapper via DOM API and serialize
                    // through outerHTML so the cookie-sourced id is safely escaped
                    // in the resulting markup that becomes layer.open({content}).
                    var _wrapperEl = document.createElement('div');
                    _wrapperEl.setAttribute('id', $.cookie("divid") || '');
                    _wrapperEl.className = 'donotuse_pdiv';
                    // Issue #789 Phase 3B: sanitize via DOMPurify before innerHTML.
                    _wrapperEl.innerHTML = ff.SafeHtml(str);
                    str = _wrapperEl.outerHTML;
                    var area = 'auto';
                    if (width > document.body.clientWidth) {
                        max = false;
                        maxed = true;
                    }
                    if (width !== undefined && width !== null && height !== undefined && height !== null) {
                        area = [width + 'px', height + 'px'];
                    }
                    if (width !== undefined && width !== null && (height === undefined || height === null)) {
                        area = width + 'px';
                    }
                    if (title === undefined || title === null || title === '') {
                        title = false;
                        max = false;
                    }
                    var oid = layer.open({
                        type: 1
                        , title: title
                        , area: area
                        , maxmin: max
                        , shade: 0.8
                        , btn: []
                        , id: windowid //设定一个id，防止重复弹出
                        , content: str
                        , success: function () {
                            // Issue #522: re-inject the extracted inline init scripts as real
                            // <script> elements in original document order. The browser runs
                            // them in native global scope + order (var sharing across sibling
                            // scripts), reproducing the original inline-<script> semantics that
                            // ordering/scope-dependent controls (xm-select render→update, etc.)
                            // require — while ff.SafeHtml/DOMPurify still sanitizes the dialog
                            // MARKUP (scripts were extracted before sanitization, markup is
                            // purified, scripts re-injected separately). Same trust boundary as
                            // #462: only real <script> ELEMENTS parsed by DOMParser are re-run;
                            // "<script>" text in attrs/text nodes is not.
                            // Previously used ff._legacyScriptEval per script (Issue #462), but
                            // per-script eval() runs in a local scope — top-level `var X` does
                            // NOT become a global, so sibling scripts (e.g. xmSelect.render →
                            // window[id].update) could not share vars across eval boundaries.
                            // Issue #627: kill-switch check — the extraction above (before
                            // ff.SafeHtml/DOMPurify sanitizes the dialog markup) is completely
                            // unchanged; only this execution loop is gated. Island dispatch
                            // below runs unchanged in both modes.
                            if (ff._isLegacyRehydrationDisabled()) {
                                if (_initScripts.length > 0 && typeof console !== 'undefined' && console.warn) {
                                    console.warn('[WTM] ' + _initScripts.length + ' legacy inline script(s) in dialog were NOT executed (DisableLegacyScriptRehydration). Migrate them to dialog-init islands. See #627/#470.');
                                }
                            } else {
                                for (var _si = 0; _si < _initScripts.length; _si++) {
                                    var _se = document.createElement('script');
                                    _se.text = _initScripts[_si];
                                    document.body.appendChild(_se);          // executes synchronously in global scope
                                    if (_se.parentNode) { _se.parentNode.removeChild(_se); } // tidy up; effects persist
                                }
                            }
                            // Issue #470: dispatch JSON action island(s) after legacy scripts so
                            // both paths are supported. No-op when no island was present.
                            // Issue #556 (#470-B slice 1): one dispatch per island collected above.
                            // Issue #576: route through ff._dispatchIslandWhenReady (NOT a bare
                            // ff.DispatchAction call) — mirrors ff._consumePageReadyIslands exactly.
                            // This closes the module-load race for the dialog path generically:
                            // initForm/bindSubmit/bindValidate/laydate/loadComboItems (none of
                            // which had a per-case layui.use guard, unlike the #552
                            // slider/rate/colorpicker cases) could previously no-op silently if
                            // dispatched from a freshly-opened dialog before their layui submodule
                            // finished loading. Ordering: this loop runs strictly after the
                            // _initScripts rehydration loop above has fully completed (plain
                            // synchronous JS, no yield between the two loops), so legacy inline
                            // scripts always run before ANY island dispatch here — including one
                            // deferred into layui.use — preserving the pre-#576 relative order
                            // between legacy-script rehydration and island init.
                            for (var _pi = 0; _pi < _dialogInitPayloads.length; _pi++) {
                                try {
                                    ff._dispatchIslandWhenReady(_dialogInitPayloads[_pi]);
                                } catch (e) {
                                    if (typeof console !== 'undefined' && console.warn) {
                                        console.warn('[WTM] dialog-init island dispatch failed:', e);
                                    }
                                }
                            }
                        }
                        , resizing: function (layero) {
                            ff.triggerResize();
                          $(layero).find("div[ischart = '1']").each(
                                function (index) {
                                    var _chart = window[$(this).attr('id') + 'Chart']; if (_chart && typeof _chart.resize === 'function') _chart.resize();
                                }
                            );
                        }
                        , full: function (layero) {
                            ff.triggerResize();
                            $(layero).find("div[ischart = '1']").each(
                                function (index) {
                                    var _chart = window[$(this).attr('id') + 'Chart']; if (_chart && typeof _chart.resize === 'function') _chart.resize();
                                }
                            );
                        }
                        , restore : function (layero) {
                            ff.triggerResize();
                          $(layero).find("div[ischart = '1']").each(
                                function (index) {
                                    var _chart = window[$(this).attr('id') + 'Chart']; if (_chart && typeof _chart.resize === 'function') _chart.resize();
                                }
                            );
                        }
                        , end: function () {
                            if (ff.GetCookie("windowids") === wid) {
                                ff.SetCookie("windowids", owid);
                            }
                        }
                    });
                    if (maxed === true) {
                        layer.full(oid);
                        ff.triggerResize();
                  }
                }
            }
        });
    },

    OpenDialog2: function (url, windowid, title, width, height, tempId, para) {
        var layer = layui.layer;
        var index = layer.load(2);
        var wid = this.GetCookie("windowids");
        var owid = wid;
        if (wid === null || wid === '') {
            wid = windowid;
        }
        else {
            wid += "," + windowid;
        }
        this.SetCookie("windowids", wid);
        this.SetCookie("windowguid", DONOTUSE_WINDOWGUID, true);
        var getpost = "GET";
        if (para !== undefined) {
            getpost = "Post";
        }

        $.ajax({
            cache: false,
            type: getpost,
            url: url,
            data: para,
            async: true,
            error: function (request) {
                layer.close(index);
                ff.SetCookie("windowids", owid);
                if (request.responseText !== undefined && request.responseText !== "") {
                    // Issue #332: wrap server error text with EscapeText to prevent
                    // XSS via HTML-injected responseText (layer.alert parses HTML).
                    layer.alert(ff.EscapeText(request.responseText));
                }
                else {
                    layer.alert(ff.DONOTUSE_Text_LoadFailed);
                }
            },
            success: function (str) {
                var regGridVar = /wtVar_(.*)\s{0,}=\s{0,}table.render\([a-zA-Z0-9_]{1,}option\)/im;
                // Issue #722: Selector.cshtml renders its OWN top-level inline
                // <script> block (submitSelect/gridCheckedFunc) plus the
                // wt:grid TagHelper's own inline table.render(...) init
                // script, directly in this response body — same shape as any
                // other same-origin partial ff.OpenDialog handles. Collect
                // them from the RAW `str` BEFORE ff.SafeHtml/DOMPurify strips
                // all <script> elements below, using the SAME shared
                // extraction helper ff.OpenDialog uses (ff._collectInitFromHtml,
                // factored out by #587) — no new parsing logic, no new eval.
                // Previously nothing collected these, so DOMPurify silently
                // dropped them with no restoration path: the dialog opened
                // but the grid never called table.render, so GetPagingData
                // never fired and the picker stayed empty. Replay happens via
                // ff._replayInitFromHtml in the layer.open `success` callback
                // below, once the sanitized content is actually in the live
                // DOM — that helper is the same one that gates legacy script
                // execution behind the #627 kill-switch
                // (ff._isLegacyRehydrationDisabled), so this fix inherits the
                // exact same kill-switch semantics ff.OpenDialog has.
                //
                // Issue #635 (#470 prerequisite) UPDATE: the helper also
                // collects .wtm-dialog-init JSON islands, which this
                // framework-owned view does not currently emit (only the
                // Form/ field TagHelpers and <wt:dialog-init> emit them, and
                // none appear in Selector.cshtml today) — so island
                // collection is a harmless no-op here, not new island
                // support. If a future change adds an island-emitting
                // TagHelper to this view, it is now dispatched correctly
                // instead of being unreachable, which is strictly safer than
                // the prior stripped-and-dropped fate.
                var _selectorInitCollected = ff._collectInitFromHtml(str);
                // Issue #332: sanitize the server response via DOMPurify BEFORE
                // extracting the grid-id or inserting into the DOM (stored XSS guard).
                // The $$script$$/$$#script$$ escape tokens in the tempId template are
                // rehydrated AFTER sanitization (they live in local DOM, not the server
                // response, so they are trusted content).
                var safeStr = ff.SafeHtml(str);
                if ($(tempId).length > 0 && regGridVar.test(str)) {
                    // Issue #332: replace brittle regex grid-id extraction with safe
                    // DOM query. Parse a throwaway element, find the table with a
                    // lay-filter attribute, and read its id.
                    var _tmpDiv = document.createElement('div');
                    _tmpDiv.innerHTML = safeStr;
                    var _gridTable = _tmpDiv.querySelector('table[lay-filter]');
                    var gridId = _gridTable ? _gridTable.id : null;
                    var gridVar = gridId ? ('wtVar_' + regGridVar.exec(str)[1]) : null;
                    if (gridId) {
                        var template = $(tempId)[0].innerHTML;
                        // Issue #635 (#470 prerequisite): rehydrate wtm-dialog-init JSON
                        // island tokens UNCONDITIONALLY — independent of, and BEFORE, the
                        // kill-switch branch below. These tokens are DATA (JSON.parse +
                        // whitelist dispatch via ff._dispatchIslandWhenReady in the
                        // layer.open success callback further down), never eval'd code, so
                        // the #627 kill-switch's "block WTM-driven dynamic script
                        // execution" contract does not apply to them — the kill-switch's
                        // whole point is that an islandified selector panel becomes
                        // CSP-clean, not dead. SelectorTagHelper.cs tokenizes each island's
                        // own open+close tag as ONE matched pair specifically so this
                        // restore is unambiguous and independent of the legacy
                        // $$script$$/$$#script$$ handling below.
                        template = template
                            .replace(/[$]{2}dialoginit[$]{2}/img, '<script type="application/json" class="wtm-dialog-init">')
                            .replace(/[$]{2}#dialoginit[$]{2}/img, '<\/script>');
                        // Issue #627 review follow-up: this is the FOURTH gated legacy
                        // dynamic-script-execution point in this file (see
                        // ff._isLegacyRehydrationDisabled) — the original #627 commit only
                        // gated the other three. The $$script$$/$$#script$$ tokens here
                        // live in the page-local #Temp{Id} template markup (SelectorTagHelper
                        // escapes its child <script> to these tokens before emitting the
                        // template) — local developer-authored DOM, not server response data
                        // (see the #332 comment above: `str`/`safeStr` are the sanitized
                        // server response and are untouched by this branch). Gating is
                        // execution-only: extraction/reading of the surrounding search-panel
                        // markup is unaffected in both modes; only whether the token pair
                        // becomes a live <script> element differs. Contract: the kill-switch
                        // means zero WTM-driven dynamic script execution across dialog/
                        // fragment flows, so this token pair must be blocked here too.
                        if (ff._isLegacyRehydrationDisabled()) {
                            // Strip the token-delimited segments entirely — rather than
                            // rehydrating them into a live <script>, or leaving the literal
                            // "$$script$$"/"$$#script$$" text visible in the rendered
                            // dialog — so nothing here can execute or leak. The rest of the
                            // search-panel template (grid/table markup) still renders.
                            var _scriptSegmentRe = /[$]{2}script[$]{2}[\s\S]*?[$]{2}#script[$]{2}/img;
                            var _scriptSegments = template.match(_scriptSegmentRe) || [];
                            if (_scriptSegments.length > 0) {
                                template = template.replace(_scriptSegmentRe, '');
                                if (typeof console !== 'undefined' && console.warn) {
                                    console.warn('[WTM] ' + _scriptSegments.length + ' legacy inline script(s) in the selector search-panel template were NOT executed (DisableLegacyScriptRehydration). See #627/#470.');
                                }
                            }
                        } else {
                            template = template.replace(/[$]{2}script[$]{2}/img, "<script>").replace(/[$]{2}#script[$]{2}/img, "<\/script>");
                        }
                        // Issue #652 (SECURITY): restore model '$' that SelectorTagHelper escaped to the
                        // U+E000 placeholder (see its DollarEscapePlaceholder comment). Runs AFTER both the
                        // $$dialoginit$$ and $$script$$ un-tokenize steps above, so every sentinel WE place
                        // at this level is already a real tag again — the remaining U+E000 chars are escaped
                        // model dollars. Because this restore is the LAST transform on the template at this
                        // level, a plaintext "$$script$$…" re-formed by it stays INERT text (no later
                        // un-tokenize turns it into a live <script>): safe for a SINGLE selector level.
                        // split/join (not .replace) so a restored '$' is never treated as a $$/$& pattern.
                        // KNOWN LIMITATION (#655, kill-switch-mitigated): a <wt:selector> nested inside
                        // another selector's <wt:searchpanel> folds its own #Temp template into the outer
                        // content; this global restore reverses the inner escape, re-arming a sentinel the
                        // inner selector's own OpenDialog2 pass then executes. Unused/exotic composition
                        // (0 demos); the #627 kill-switch strips those segments at every level. The real fix
                        // is the #470 string-sentinel retirement — do NOT chase depth with regex here.
                        template = template.split('').join('$');
                        //get old gridid
                        try {
                            var oldgridid = /table[.]reload\('(.*)',\s{0,}{/img.exec(template)[1];
                            //替换gridId
                            template = template.replace(new RegExp(oldgridid, "gim"), gridId);
                        }
                        catch (e) { }
                        // Issue #652: insert the restored template via a REPLACER FUNCTION, not a
                        // replacement string — String.prototype.replace special-cases $$/$&/$`/$'/$n
                        // in a string 2nd arg, which would re-corrupt the '$' we just restored above
                        // (e.g. a "$$10$$" price label -> "$10$", or "$&" -> the matched marker text).
                        // A function return value is inserted literally, making the round-trip lossless.
                        safeStr = safeStr.replace('$$SearchPanel$$', function () { return template; });
                    }
                }
                str = safeStr;
                layer.close(index);
                var area = 'auto';
                if (width !== undefined && width !== null && height !== undefined && height !== null) {
                    area = [width + 'px', height + 'px'];
                }
                if (width !== undefined && width !== null && (height === undefined || height === null)) {
                    area = width + 'px';
                }
                var max = true;
                if (title === undefined || title === null || title === '') {
                    title = false;
                    max = false;
                }
                if (width > document.body.clientWidth) {
                    max = false;
                }
                var oid = layer.open({
                    type: 1
                    , title: title
                    , area: area
                    , maxmin: max
                    , btn: []
                    , shade: 0.8
                    , id: windowid //设定一个id，防止重复弹出
                    , content: str
                    , success: function (layero) {
                        // Issue #722: replay Selector.cshtml's own top-level inline
                        // scripts (submitSelect/gridCheckedFunc) and the wt:grid
                        // TagHelper's table.render(...) init script collected from the
                        // RAW response above, now that the sanitized content is actually
                        // in the live DOM. ff._replayInitFromHtml re-injects them as real
                        // <script> elements in original document order (native global
                        // scope, same technique as ff.OpenDialog's #522 rehydration loop)
                        // and is gated by the same #627 kill-switch
                        // (ff._isLegacyRehydrationDisabled) ff.OpenDialog uses — no new
                        // eval, no new execution path. Must run BEFORE
                        // ff.ConsumeIslandsIn below (scripts-before-islands ordering,
                        // matching the #576 invariant): the grid's own table.render call
                        // needs to run before anything reacts to the grid being present.
                        ff._replayInitFromHtml(_selectorInitCollected);
                        // Issue #635 (#470 prerequisite): dispatch any wtm-dialog-init
                        // island(s) that just became part of the live DOM as this layer's
                        // content — e.g. a callback-free <wt:datetime> field inside the
                        // selector search-panel template rehydrated above.
                        // ff.ConsumeIslandsIn is scoped to layero's own subtree — never the
                        // whole document (see its #587 comment) — and claims each island
                        // (data-wtm-dispatched="1") before scheduling its dispatch, so this
                        // can never double-dispatch. Ordering: layer.open's own content
                        // insertion (the mechanism that turns a rehydrated bare
                        // <script>...</script> segment into a running side effect) and the
                        // ff._replayInitFromHtml call above have already completed by the
                        // time this line runs, so a legacy $$script$$ segment (when the
                        // kill-switch is OFF) always runs before this island dispatch.
                        ff.ConsumeIslandsIn(layero);
                    }
                    , end: function () {
                        ff.SetCookie("windowids", owid);
                    }
                });
                if (width > document.body.clientWidth) {
                    layer.full(oid);
                }

            }
        });
    },

    CloseDialog: function () {
        var layer = layui.layer;
        var wid = this.GetCookie("windowids");
        if (wid !== null && wid !== '') {
            var wids = wid.split(",");
            var windowid = wids.pop();
            var index = $('#' + windowid).parent('.layui-layer').attr("times");
            layer.close(index);
            this.SetCookie("windowids", wids.join());
        }
        else {
            if (layui.setter == undefined || layui.setter.pageTabs == undefined || window.location.href.toLocaleLowerCase().indexOf("/home/pindex/")>-1) {
                window.close();
            }
            else if (layui.setter.pageTabs === false || $('.layadmin-tabsbody-item').length === 0) {
                $('#LAY_app_body').html('');
            }
            else {
                layui.admin.closeThisTabs();
            }
        }
    },

    ResizeChart: function (id) {
        if (layui == undefined || layui.admin == undefined) {
            return;
        }
        if (id === undefined || id === null || id === '') {
            layui.use(['admin'], function () {
                layui.admin.resize(function () {
                    {
                       $("div[ischart = '1']").each(
                            function (index) {
                                var _chart = window[$(this).attr('id') + 'Chart']; if (_chart && typeof _chart.resize === 'function') _chart.resize();
                            }
                        );
                    }
                });
            }
            );
        }
        else {
            layui.use(['admin'], function () {
                layui.admin.resize(function () {
                    {
                        $("#"+id).find("div[ischart = '1']").each(
                            function (index) {
                               var _chart = window[$(this).attr('id') + 'Chart']; if (_chart && typeof _chart.resize === 'function') _chart.resize();
                            }
                        );
                    }
                });
            }
            );
        }
    },

    // Issue #632 (redesigned — data as markup, not script): authoritative source
    // for ff.ChainChange's default-selection lookups. `target` is the CHAINED
    // control's own wrapping div (BaseFieldTag gives it `id="{Id}"`, the same
    // element ChainChange already resolved as `target` via
    // `$('#'+formid).find('#'+linkto.value)`), so `target.attr('data-wtm-defaults')`
    // is read directly off it — no extra DOM lookup, no timing dependency on
    // DOMContentLoaded/island dispatch at all, since the attribute is written by
    // the server into the initial HTML and is therefore present at HTML-parse
    // time, before any script on the page (including this one) has even started
    // running. CheckBoxTagHelper/RadioTagHelper emit this attribute (#632); it is
    // the ONLY authoritative source for those two control types now — the
    // legacy window[comboid+"defaultvalues"] global is published purely for
    // BACK-COMPAT app-authored JS and is never read by this function for a
    // migrated control type.
    // ComboBoxTagHelper/TreeTagHelper are NOT migrated by #632 (out of scope —
    // see the #632 commit body) and still only publish the legacy global
    // synchronously, at inline-<script> parse time (unchanged, unraced). Falling
    // back to that global when the attribute is absent therefore preserves their
    // existing behavior byte-for-byte: the fallback branch is live code for
    // combo/tree and effectively unreachable dead code for checkbox/radio (whose
    // attribute — even an empty-array "[]" — is always a non-empty attribute
    // string once the TagHelper has run).
    // Issue #638: whichever source resolves, this ALWAYS returns an array, never
    // undefined/null — callers can safely call .indexOf on the result with no
    // separate guard (this subsumes the standalone `if (df == undefined...)`
    // checks #638 added directly in the checkbox/radio branches below).
    _readFieldDefaults: function (target, comboid) {
        var raw = target && typeof target.attr === 'function' ? target.attr('data-wtm-defaults') : undefined;
        if (raw !== undefined && raw !== null && raw !== '') {
            try {
                var parsed = JSON.parse(raw);
                if (Array.isArray(parsed)) { return parsed; }
            } catch (e) { /* malformed attribute → fall through to the legacy global */ }
        }
        var legacy = window[comboid + "defaultvalues"];
        if (legacy == undefined || legacy == null) { return []; }
        return legacy;
    },

    // Issue #645: order-independent race guard between ff.ChainChange and
    // ff.LoadComboItems. Since #633 islandified item-url loads to fire at
    // DOMContentLoaded, while a chained field's default-value cascade
    // (ComboBoxTagHelper's `on:` handler / its edit-page setTimeout) fires
    // its own $.get independently, two unrelated network round trips can
    // both resolve against the SAME target element with no
    // generation/cancellation guard — whichever $.get lands last silently
    // wins, even when that means the stale unfiltered item-url list
    // clobbers the correct filtered chain result (or vice versa).
    // The fix: the instant ChainChange applies items to `target` — in any
    // of the tree/transfer/combo/checkbox/radio branches below, regardless
    // of usedefaultvalue, since a user-driven refresh must claim authority
    // too — it stamps `data-wtm-chain-applied` on that element. ChainChange's
    // `target` (`$('#'+formid).find('#'+linkto.value)`) and
    // ff.LoadComboItems' own `target` (`$('#'+controlid)`) resolve to the
    // SAME DOM node whenever a field is simultaneously a chain target and
    // has its own item-url (linkto.value === controlid === the field's own
    // wrapping `#{Id}` div — see ComboBoxTagHelper.cs). Chain is
    // authoritative: once claimed, ff.LoadComboItems' still-in-flight (or
    // future) item-url apply is a no-op — order-independent of which $.get
    // actually resolves first (see ff.LoadComboItems' matching check).
    // Fields that are never a chain target (no source's wtm-linkto ever
    // points at them) never get this attribute set, so the marker check in
    // ff.LoadComboItems is inert for them — byte-identical to pre-#645
    // behavior for the plain item-url-only and plain-chain-only cases.
    ChainChange: function (url, self, usedefaultvalue) {
        var form = layui.form;
        var linkto = self.attributes["wtm-linkto"];
        if (linkto == undefined) {
            return;
        }
        var formid = self.closest("form").id
        var target = $('#' + formid).find('#' + linkto.value);
        if (target.length == 0) {
            return;
        }
        var controltype = target.attr("wtm-ctype");
        var targetfilter = target.attr("lay-filter");
        var targetname = target.attr("wtm-name");
        var ismulticombo = target.attr("wtm-combo") != undefined;
        var targetid = target.attr("id");
        var comboid = targetid;

        if (controltype == undefined) {
            controltype = "";
        }
        if (targetfilter == undefined) {
            targetfilter = "";
        }
        targetfilter += "div";
        //clear
        // Issue #470 Slice J: existence-guard the combo/tree clear step —
        // mirrors ff.LoadComboItems' combo/tree existence guard (#633/#645).
        // window[comboid] is undefined until the widget's render call has
        // actually run — for a legacy inline <script> render, blocked by the
        // #627 kill-switch, or for an island-rendered widget (#470 Slice J
        // opt-in UseSelectIslandRender) whose 'renderSelect' island dispatch
        // (DOMContentLoaded-deferred) simply hasn't fired yet. Without this
        // guard, window[comboid].update(...) throws an uncaught TypeError
        // instead of degrading with a diagnostic — pure defense, no behavior
        // change for any currently-working (already-rendered) config.
        switch (controltype) {
            case "combo":
                if (!window[comboid] || typeof window[comboid].update !== 'function') {
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] ChainChange: widget "' + comboid + '" was never rendered — skipping clear (#470).');
                    }
                } else {
                    window[comboid].update({ data: [] });
                }
                break;
            case "checkbox":
                target.html('');
                form.render('checkbox', targetfilter);
                break;
            case "radio":
                target.html('');
                form.render('radio', targetfilter);
                break;
            case "tree":
                if (!window[comboid] || typeof window[comboid].update !== 'function') {
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] ChainChange: widget "' + comboid + '" was never rendered — skipping clear (#470).');
                    }
                } else {
                    window[comboid].update({ data: [] });
                }
                break;
            case "transfer":
                layui.transfer.reload(targetid, {
                    data: []
                });
            default:
        }
        if (url != "") {
            $.get(url, {}, function (data, status) {
                if (status === "success") {
                    var i = 0;
                    var item = null;

                    if (controltype === "tree") {
                        var df = [];
                        if (usedefaultvalue == true) {
                            df = ff._readFieldDefaults(target, comboid);
                        }
                        // Issue #470 Slice J: existence-guard the apply step — see
                        // the comment above the clear-step switch for the full
                        // rationale (same invariant as ff.LoadComboItems' guard).
                        if (!window[comboid] || typeof window[comboid].update !== 'function') {
                            if (typeof console !== 'undefined' && console.warn) {
                                console.warn('[WTM] ChainChange: widget "' + comboid + '" was never rendered — items were fetched but could not be applied (#470).');
                            }
                        } else {
                            window[comboid].update({ data: ff.getTreeItems(data.Data,df) });
                            // Issue #645: claim the target — see the comment above ff.ChainChange.
                            target.attr('data-wtm-chain-applied', '1');
                        }
                    }
                    if (controltype === "transfer") {
                        layui.transfer.reload(targetid, {
                            data: ff.getTransferItems(data.Data)
                        });
                        // Issue #645: claim the target — see the comment above ff.ChainChange.
                        target.attr('data-wtm-chain-applied', '1');
                    }

                    if (controltype === "combo") {
                        var df = [];
                        if (usedefaultvalue == true) {
                            df = ff._readFieldDefaults(target, comboid);
                      }
                        // Issue #470 Slice J: existence-guard the apply step — see
                        // the comment above the clear-step switch for the full
                        // rationale (same invariant as ff.LoadComboItems' guard).
                        if (!window[comboid] || typeof window[comboid].update !== 'function') {
                            if (typeof console !== 'undefined' && console.warn) {
                                console.warn('[WTM] ChainChange: widget "' + comboid + '" was never rendered — items were fetched but could not be applied (#470).');
                            }
                        } else {
                            window[comboid].update({ data: ff.getComboItems(data.Data, df, usedefaultvalue) });
                            // Issue #645: claim the target — see the comment above ff.ChainChange.
                            target.attr('data-wtm-chain-applied', '1');
                        }
                    }
                    if (controltype === "checkbox") {
                        for (i = 0; i < data.Data.length; i++) {
                            item = data.Data[i];
                            if (usedefaultvalue == true) {
                                // Issue #632: ff._readFieldDefaults always returns an
                                // array (never undefined/null — see its own comment
                                // for the #638 guarantee this subsumes), so
                                // df.indexOf below is always safe.
                                var df = ff._readFieldDefaults(target, comboid);
                                // Issue #332: use ff._makeInput (DOM API) instead of HTML
                                // string concat to safely set name/value/title attributes.
                                target.append(ff._makeInput('checkbox', targetname, item.Value, item.Text, df.indexOf(item.Value) > -1, false));
                           }
                            else {
                                target.append(ff._makeInput('checkbox', targetname, item.Value, item.Text, item.Selected === true, false));
                            }
                        }
                        form.render('checkbox', targetfilter);
                        // Issue #645: claim the target — see the comment above ff.ChainChange.
                        target.attr('data-wtm-chain-applied', '1');
                    }
                    if (controltype === "radio") {
                        for (i = 0; i < data.Data.length; i++) {
                            item = data.Data[i];
                            if (usedefaultvalue == true) {
                                // Issue #632/#638: see the checkbox branch above.
                                var df = ff._readFieldDefaults(target, comboid);
                                // Issue #332: use ff._makeInput (DOM API) instead of HTML
                                // string concat to safely set name/value/title attributes.
                                target.append(ff._makeInput('radio', targetname, item.Value, item.Text, df.indexOf(item.Value) > -1, false));
                           }
                            else {
                                target.append(ff._makeInput('radio', targetname, item.Value, item.Text, item.Selected === true, false));
                            }
                        }
                        form.render('radio', targetfilter);
                        // Issue #645: claim the target — see the comment above ff.ChainChange.
                        target.attr('data-wtm-chain-applied', '1');
                    }

                }
                else {
                    // Issue #332: layer is not in scope here; use layui.layer.
                    layui.layer.alert(ff.DONOTUSE_Text_FailedLoadData);
                }
            });
        }

        ff.ChainChange("", target[0], "");
    },

    LoadComboItems: function (controltype,url, controlid, targetname,svals, cb,disabled) {
        var target = $("#" + controlid);
        var targetfilter = target.attr("lay-filter");
        var ismulticombo = target.attr("wtm-combo") != undefined;
        if (svals == undefined || svals == null) {
            svals = [];
        }
       $.get(url, {}, function (data, status) {
           if (status === "success") {
               // Issue #645: ff.ChainChange (a chain link targeting this SAME
               // element — see the comment above ff.ChainChange for the DOM-
               // node-identity argument) may have already applied a filtered
               // result to `target` while this item-url fetch was still in
               // flight, or may still be about to (the two $.get round trips
               // race with no ordering guarantee). Once claimed, chain is
               // authoritative — this apply must yield rather than clobber it
               // with the stale/unfiltered item-url list. This is expected,
               // not an error, so it's a quiet debug note, not a warning.
               if (target.attr('data-wtm-chain-applied')) {
                   if (typeof console !== 'undefined' && console.debug) {
                       console.debug('[WTM] LoadComboItems: widget "' + controlid + '" already claimed by ff.ChainChange — skipping stale item-url apply (#645).');
                   }
                   return;
               }
               var i = 0;
               var item = null;
               if (controltype === "tree") {
                   var da = ff.getTreeItems(data.Data, svals);
                   // Issue #470 (Slice G follow-up): same invariant as the combo
                   // branch below — TreeTagHelper's ItemUrl path now routes
                   // through this SAME island dispatch, but the widget's own
                   // render call (xmSelect.render(...), TreeTagHelper) is still
                   // a bare inline <script> that the #627 kill-switch can block,
                   // leaving window[controlid] unset. Degrade with a diagnostic
                   // instead of an uncaught throw when the widget was never
                   // rendered; leave the normal (rendered) path byte-identical
                   // to before.
                   if (!window[controlid] || typeof window[controlid].update !== 'function') {
                       if (typeof console !== 'undefined' && console.warn) {
                           console.warn('[WTM] LoadComboItems: widget "' + controlid + '" was never rendered — its inline render script did not run. If DisableLegacyScriptRehydration is enabled (#627), this widget still emits a legacy inline render script and is not yet islandified (#470 hard blocker). Items were fetched but could not be applied.');
                       }
                   } else {
                       window[controlid].update({ data: da });
                       if (cb !== undefined && cb != null) {
                           cb();
                       }
                   }
               }
               if (controltype == "transfer") {
                   // Issue #633 (review follow-up) / #627 / #470: dialog-init islands
                   // (this LoadComboItems call) dispatch unconditionally under the
                   // DisableLegacyScriptRehydration kill-switch — islands are data, not
                   // code. The widget's own render call (`layui.transfer.render(...)`,
                   // TransferTagHelper) is still a bare inline <script>, which the
                   // kill-switch DOES block, so a partially-islandified widget can reach
                   // here with no rendered instance. layui.transfer keeps its rendered-
                   // instance registry (`r.that`, keyed by id) in a private closure with
                   // no public API to probe it ahead of time — probing would just
                   // reproduce the same throw reload() itself would raise — so detection
                   // here is a try/catch around the call rather than a pre-check.
                   try {
                       layui.transfer.reload(controlid, {
                           data: ff.getTransferItems(data.Data, svals)
                       });
                   } catch (e) {
                       if (typeof console !== 'undefined' && console.warn) {
                           console.warn('[WTM] LoadComboItems: widget "' + controlid + '" was never rendered — its inline render script did not run. If DisableLegacyScriptRehydration is enabled (#627), this widget still emits a legacy inline render script and is not yet islandified (#470 hard blocker). Items were fetched but could not be applied.');
                       }
                   }
               }
               if (controltype === "combo") {
                   var da = ff.getComboItems(data.Data, svals,undefined,disabled);
                   // Issue #633 (review follow-up) / #627 / #470: same invariant as the
                   // transfer branch above — xmSelect.render(...) (ComboBoxTagHelper)
                   // assigns window[Id] from a bare inline <script> that the kill-switch
                   // blocks, while this LoadComboItems island dispatch always runs.
                   // Degrade with a diagnostic instead of an uncaught throw when the
                   // widget was never rendered; leave the normal (rendered) path
                   // byte-identical to before.
                   if (!window[controlid] || typeof window[controlid].update !== 'function') {
                       if (typeof console !== 'undefined' && console.warn) {
                           console.warn('[WTM] LoadComboItems: widget "' + controlid + '" was never rendered — its inline render script did not run. If DisableLegacyScriptRehydration is enabled (#627), this widget still emits a legacy inline render script and is not yet islandified (#470 hard blocker). Items were fetched but could not be applied.');
                       }
                   } else {
                       window[controlid].update({ data: da });
                   }
               }
               if (controltype === "checkbox") {
                   target[0].innerHTML = "";
                   for (i = 0; i < data.Data.length; i++) {
                       item = data.Data[i];
                       var isChecked = item.Selected === true || svals.indexOf(item.Value) > -1;
                       var isDisabled = disabled == true;
                       // Issue #332: use ff._makeInput (DOM API) instead of HTML
                       // string concat to safely set name/value/title attributes.
                       target.append(ff._makeInput('checkbox', targetname, item.Value, item.Text, isChecked, isDisabled));
                   }
                   layui.form.render('checkbox', targetfilter + "div");
               }
               if (controltype === "radio") {
                   for (i = 0; i < data.Data.length; i++) {
                       item = data.Data[i];
                       var isChecked = item.Selected === true || svals.indexOf(item.Value) > -1;
                       // Issue #332: use ff._makeInput (DOM API) instead of HTML
                       // string concat to safely set name/value/title attributes.
                       target.append(ff._makeInput('radio', targetname, item.Value, item.Text, isChecked, false));
                   }
                   layui.form.render('radio', targetfilter + "div");
               }

           }

            else {
                // Issue #332: layer is not in scope here; use layui.layer.
                layui.layer.alert(ff.DONOTUSE_Text_FailedLoadData);
            }
        });

    },

    GetFormArray: function (formId) {
        var searchForm = $('#' + formId), filter = [], fieldElem = searchForm.find('input,select,textarea');
        layui.each(fieldElem, function (_, item) {
            if (!item.name) return;
            if (/^checkbox|radio$/.test(item.type) && !item.checked) return;
            if (item.value !== null && item.value !== "")
                filter.push({ name: item.name, value: item.value });
        });
        return filter;
    },

    GetFormData: function (formId) {
        var richtextbox = $("#" + formId + " textarea");
        for (var i = 0; i < richtextbox.length; i++) {
            var ra = richtextbox[i].attributes['layeditindex'];
            if (ra !== undefined && ra != null) {
                var rindex = ra.value;
                layui.layedit.sync(rindex);
            }
        }
        var searchForm = $('#' + formId), filter = {}, filterback = {}, fieldElem = searchForm.find('input,select,textarea');

        var tables = $('#' + formId + ' table[id]');
        for (var i = 0; i < tables.length; i++) {
            var tableid = tables[i].id;
            var loaddata = layui.table.cache[tableid];
            if (loaddata == undefined || loaddata.length == 0) {
                try {
                    var subpro = tables[i].attributes["subpro"].value;
                    if (subpro != undefined && subpro != "") {
                        filter[subpro + ".length"] = "0";
                    }
                }
                catch { }
            }
        }

        var xselect = searchForm.find("div[wtm-ctype='tree'],div[wtm-ctype='combo']");
        layui.each(xselect, function (_, item) {
            var val = window[item.id].getValue('value');
                fieldElem = fieldElem.filter(function (index) {
                   return this.name != item.attributes["wtm-name"].value;
                })
                $.each(val, function (i, v) {
                    fieldElem.push({ name: item.attributes["wtm-name"].value, value: v });
                });
            if (val.length == 0) {
                var ismulti = '';
                try { ismulti = item.attributes["wtm-multi"].value } catch { }
                fieldElem.push({ name: item.attributes["wtm-name"].value, value: ismulti=='true'?'':null });
            }
        });

        var check = {};
        layui.each(fieldElem, function (_, item) {
            if (!item.name) return;
            if (/^checkbox$/.test(item.type) && !item.checked) {
                if (item.value === "true") {
                    filter[item.name] = false;
                }
                return;
            }
            if (/^radio$/.test(item.type) && !item.checked) {
                return;
            }
            var itemname = item.name;
            if (/_DONOTUSE_(.*?)\[(.*?)\]\.(.*?)$/.test(itemname)) {
                var name1 = RegExp.$1;
                var number = RegExp.$2;
                var name2 = RegExp.$3;
                if (filterback.hasOwnProperty(name1) == false && filter.hasOwnProperty(name1 + "[" + number + "]." + name2) == false) {
                    filterback[name1] = 1;
                }
                return;
            }
            if (/_DONOTUSE_(.*?)$/.test(itemname)) {
                var name1 = RegExp.$1;
              if (filterback.hasOwnProperty(name1) == false && filter.hasOwnProperty(name1) == false) {
                    filterback[name1] = 1;
                }
                return;
            }
            var issub = false;
            if (/(.*?)\[(.*?)\]\.(.*?)$/.test(itemname)) {
                var name1 = RegExp.$1;
                var number = RegExp.$2;
                var name2 = RegExp.$3;
                if (number == "-1") {
                    var checkname = itemname;
                    if (check.hasOwnProperty(checkname) == false) {
                        check[checkname] = 0;
                    }
                    if (filterback.hasOwnProperty(name1) == true) {
                        filterback[name1] = undefined;
                    }
                    if (filterback.hasOwnProperty(itemname) == true) {
                        filterback[itemname] = undefined;
                    }
                    var newname = itemname;
                    newname = name1 + "[" + check[checkname] + "]." + name2;
                    filter[newname] = item.value;
                    check[checkname] = check[checkname] + 1;
                    issub = true;
                }
            }
            if (issub == false) {
                if (filter.hasOwnProperty(itemname)) {
                    var temp = filter[itemname];
                    if (!(temp instanceof Array))
                        temp = [temp];
                    temp.push(item.value);
                    filter[itemname] = temp;
                }
                else {
                    filter[itemname] = item.value;
                    if (filterback.hasOwnProperty(itemname) == true && item.value != '') {
                        filterback[itemname] = undefined;
                    }
                }
            }
        });

        for (item in filterback) {
            if (filterback[item] !== undefined) {
                filter[item] = undefined;
                filter[item + ".length"] = "0";
            }
        }
        return filter;
    },

    GetSearchFormData: function (formId, listvm) {
        var data = ff.GetFormData(formId, listvm);
        for (var attr in data) {
            if (attr.startsWith(listvm + ".")) {
                data[attr.replace(listvm + ".", "")] = data[attr];
                delete data[attr];
            }
        }
        var tc = $("#" + formId).closest("div[wtm-ctype='tc']")
        if (tc.length > 0) {
            var obj = window[tc[0].id + "selected"];
            if (obj !== undefined && obj !== null) {
                for (var item in obj) {
                    if (listvm == "") {
                        data["Searcher."+item] = obj[item];
                    }
                    else {
                        data[item] = obj[item];
                    }
                }
            }
        }
        return data;
    },

DownloadExcelOrPdf: function (url, formId, defaultcondition, ids) {
    var formData = ff.GetSearchFormData(formId);
    if (defaultcondition == null) {
        defaultcondition = {};
    }
    var tempwhere = {};
    for (let item in defaultcondition) {
        if (formData["Searcher." + item]) {
        } else {
            tempwhere[item] = defaultcondition[item];
        }
    }
    $.extend(tempwhere, formData);
    for (let item in tempwhere) {
        if (item.startsWith("Searcher.") == false) {
            tempwhere["Searcher." + item] = tempwhere[item];
        }
    }
    if (ids !== undefined && ids !== null) {
        tempwhere["Ids"] = ids;
    }
    var postData = $.param(tempwhere, true);
    var layer = layui.layer;
    var loadIndex = layer.load(2);
    $.ajax({
        url: url,
        type: "POST",
        data: postData,
        xhrFields: { responseType: "blob" },
        success: function (blob, status, xhr) {
            layer.close(loadIndex);
            var disposition = xhr.getResponseHeader("Content-Disposition") || "";
            var filenameMatch = disposition.match(/filename\*?=['"]?(?:UTF-8'')?([^;'"\s]+)/i);
            var filename = filenameMatch ? decodeURIComponent(filenameMatch[1]) : "export";
            var objUrl = URL.createObjectURL(blob);
            var a = document.createElement("a");
            a.href = objUrl;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(objUrl);
            var exp = new Date(new Date().getTime() + 30000).toUTCString();
            document.cookie = "DONOTUSEDOWNLOADING=0; path=/; expires=" + exp;
        },
        error: function (xhr) {
            layer.close(loadIndex);
            if (xhr.status === 422) {
                var reader = new FileReader();
                reader.onload = function () {
                    try {
                        var err = JSON.parse(reader.result);
                        layer.alert(err.message || ff.DONOTUSE_Text_ExportNoData,
                            { icon: 0, title: false, btn: ["OK"] });
                    } catch (e) {
                        layer.alert(ff.DONOTUSE_Text_ExportNoData,
                            { icon: 0, title: false, btn: ["OK"] });
                    }
                };
                reader.readAsText(xhr.response);
            } else {
                layer.alert(ff.DONOTUSE_Text_LoadFailed,
                    { icon: 2, title: false, btn: ["OK"] });
            }
        }
    });
},

    Download: function (url, ids) {
        // Issue #332: build POST form via DOM API to avoid HTML injection through
        // url and ids parameters. Mirrors DownloadExcelOrPdf DOM anchor pattern.
        var form = document.createElement('form');
        form.method = 'POST';
        form.action = url;
        if (ids !== undefined && ids !== null) {
            for (var i = 0; i < ids.length; i++) {
                var inp = document.createElement('input');
                inp.type = 'hidden';
                inp.name = 'Ids';
                inp.value = ids[i] !== undefined && ids[i] !== null ? String(ids[i]) : '';
                form.appendChild(inp);
            }
        }
        document.body.appendChild(form);
        form.submit();
        document.body.removeChild(form);
    },

    RefreshChart: function (chartid,chartpre) {
        var postdata = '';

        var searcher = $('form[chartlink*="' + chartid + '"]');
        if (searcher !== undefined && searcher.length > 0) {
            if (chartpre) {
                postdata = ff.GetSearchFormData(searcher[0].id, chartpre);
            }
            else {
                postdata = ff.GetSearchFormData(searcher[0].id, "Searcher");
            }
        }
            $.ajax({
                cache: false,
                type: 'POST',
                url: window[chartid + "ChartUrl"],
                data: postdata,
                async: true,
                success: function (data, textStatus, request) {
                    if (data.series != undefined) {
                        data.series = data.series.replace(/"type":"charttype"/g, window[chartid + 'ChartType']);
                    }
                    (function () {
                        var _chart = window[chartid + 'Chart'];
                        if (_chart && typeof _chart.setOption === 'function') {
                            // Issue #332: JSONfns.parse (which executes function literals in
                            // JSON) replaced with safe JSON.parse for the default path.
                            // Function-typed series remain possible via the explicit opt-in
                            // registry: set window[chartid + 'ChartSeriesParser'] to a
                            // trusted function before calling RefreshChart. See CHANGELOG.md.
                            var _seriesParser = typeof window[chartid + 'ChartSeriesParser'] === 'function'
                                ? window[chartid + 'ChartSeriesParser']
                                : JSON.parse;
                            _chart.setOption({dataset: JSON.parse(data.dataset), series: _seriesParser(data.series)}, {replaceMerge: 'series'});
                        }
                    })();
                    if (window[chartid + 'ChartLegend'] == 'true') {
                        (function () {
                            var _chart = window[chartid + 'Chart'];
                            if (_chart && typeof _chart.setOption === 'function') {
                                _chart.setOption({legend: JSON.parse(data.legend)});
                            }
                        })();
                    }
                }
            });
    },

    /**
     * RefreshGrid
     * @param {string} dialogid the dialogid
     * @param {number} index the grid index
     */
    RefreshGrid: function (dialogid, index) {
        if (index === undefined) {
            index = 0;
        }
        var tab = "";
        if (layui.setter.pageTabs === true && dialogid == "LAY_app_body") {
            tab = " .layadmin-tabsbody-item.layui-show";
        }
        var tables = $('#' + dialogid + tab + ' table[id]');
        var searchBtns = $('#' + dialogid + tab + ' form a[IsSearchButton]');
        if (searchBtns.length > index) {
            var sb = $('#' + searchBtns[index].id);
            var form = sb.parents("form");
            if (form.attr("oldpost") == 'True') {
                sb.trigger("click");
            }
            else {
                sb.trigger("myclick", true);
            }
        }
        else {
            if (tables.length > index) {
                layui.table.reload(tables[index].id);
            }
        }
    },

    AddGridRow: function (gridid, option, data) {
        var loaddata = layui.table.cache[gridid];
        for (val in data) {
            if (val === "ID") {
                data[val] = ff.guid();
            }
        }
        var re = /(<input .*?)\s*\/>(.*?)/ig;
        var re2 = /(<select .*?)\s*>(.*?<\/select>)/ig;
        var re3 = /(.*?)<input hidden name='(.*?)\.id' .*?\/>(.*?)/ig;
        for (val in data) {
            if (typeof (data[val]) == 'string') {
                data[val] = data[val].replace(/\[\d+\]/ig, "[" + loaddata.length + "]");
                data[val] = data[val].replace(/_\d+_/ig, "_" + loaddata.length + "_");
                data[val] = data[val].replace(re, "$1 onchange=\"ff.gridcellchange(this,'" + gridid + "'," + loaddata.length + ",'" + val + "',0)\" />$2");
                data[val] = data[val].replace(re2, "$1 onchange=\"ff.gridcellchange(this,'" + gridid + "'," + loaddata.length + ",'" + val + "',1)\" >$2");
                data[val] = data[val].replace(re3, "$1 <input hidden name=\"$2.id\" value='" + data["ID"] + "'/> $3");
            }
        }
        loaddata.push(data);
        option.url = null;
        option.data = loaddata;
        option.limit = 9999;
        layui.table.render(option);
    },

    SetGridCellDate: function (id,dt) {
        layui.use('laydate', function () {
            var laydate = layui.laydate;
            laydate.render({
                elem: '#' + id
                , type: dt
                , show: true
                , closeStop: '#' + id
                , done: function (value, date, endDate) {
                    document.getElementById(id).value = value;
                    document.getElementById(id).onchange();
                }
            });
        });
    },

    LoadLocalData: function (gridid, option, datas, isnormaltable) {
        // Issue #490: The $$script$$/$$#script$$ placeholder reversal was removed.
        // The server-side EscapeLocalDataJson helper now encodes '<' / '>' / '&' as
        // < / > / & Unicode escapes inside the JSON, which the JS
        // engine decodes automatically.  Cell HTML is therefore delivered correctly
        // without any client-side string surgery here.
        var re = /(<input .*?)\s*\/>/ig;
        var re2 = /(<select .*?)\s*>(.*?<\/select>)/ig;
        for (var i = 0; i < datas.length; i++) {
            var data = datas[i];
            for (val in data) {
                if (typeof (data[val]) == 'string') {
                    if (isnormaltable === false) {
                        data[val] = data[val].replace(re, "$1 onchange=\"ff.gridcellchange(this,'" + gridid + "'," + i + ",'" + val + "',0)\" />");
                        data[val] = data[val].replace(re2, "$1 onchange=\"ff.gridcellchange(this,'" + gridid + "'," + i + ",'" + val + "',1)\" >$2");
                    }
                }
            }
        }
        option.url = null;
        option.data = datas;
        option.limit = 9999;
        layui.table.render(option);
    },

    RemoveGridRow: function (gridid, option, index) {
        var loaddata = layui.table.cache[gridid];
        loaddata.splice(index - 1, 1);
        for (var i = 0; i < loaddata.length; i++) {
            for (val in loaddata[i]) {
                if (typeof (loaddata[i][val]) == 'string') {
                    loaddata[i][val] = loaddata[i][val].replace(/\[\d+\]/ig, "[" + i + "]");
                    loaddata[i][val] = loaddata[i][val].replace(/_\d+_/ig, "_" + i + "_");
                    if (/<input .*?\s*\/>.*?/.test(loaddata[i][val])) {
                        loaddata[i][val] = loaddata[i][val].replace(/onchange=\".*?\"/ig, "onchange=\"ff.gridcellchange(this,'" + gridid + "'," + i + ",'" + val + "',0)\"");
                    }
                    if (/<select .*?\s*>.*?<\/select>/.test(loaddata[i][val])) {
                        loaddata[i][val] = loaddata[i][val].replace(/onchange=\".*?\"/ig, "onchange=\"ff.gridcellchange(this,'" + gridid + "'," + i + ",'" + val + "',1)\"");
                    }
                }
            }
        }
        option.url = null;
        option.data = loaddata;
        option.limit = 9999;
        layui.table.render(option);
    },

    gridcellchange: function (ele, gridid, row, col, celltype) {
        var loaddata = layui.table.cache[gridid];
        if (celltype === 0) {
            loaddata[row][col] = loaddata[row][col].replace(/value\s*=\s*'.*?'/i, "value='" + ele.value + "'");
        }
        if (celltype === 1) {
            loaddata[row][col] = loaddata[row][col].replace(/(<option .*?) selected\s*>/ig, "$1>");
            var re = new RegExp("(<option\\s*value\\s*=\\s*[\"']" + ele.value + "[\"'])\\s*>", "ig");
            loaddata[row][col] = loaddata[row][col].replace(re, "$1 selected>");
        }

    },

    clearSelector: function (id) {
        $("#" + id).val("");
        $("#" + id + "_Display").val("");
        var vals = $('#' + id + '_Container input[type=hidden]');
        for (var i = 0; i < vals.length; i++) {
            vals[i].remove();
        }
    },

    setSelectorPara: function (id, obj) {
        window[id + "filter"] = obj;
    },

    guid: function () {
        function s4() {
            return Math.floor((1 + Math.random()) * 0x10000)
                .toString(16)
                .substring(1);
        }
        return s4() + s4() + '-' + s4() + '-' + s4() + '-' + s4() + '-' + s4() + s4() + s4();
    },

    concatWhereStr: function (tempUrl, whereStr, data) {
        if (tempUrl == null) tempUrl = "";
        if (data == null) return tempUrl;
        if (whereStr != null && whereStr.length > 0) {
            for (var i = 0; i < whereStr.length; i++) {
                tempUrl = tempUrl + '&' + whereStr[i] + '=' + data[whereStr[i]];
            }
        }
        return tempUrl;
    },

    triggerResize: function () {
        setTimeout(function () {
            {
                if (typeof (Event) === 'function') {
                    {
                        window.dispatchEvent(new Event('resize'));
                    }
                } else {
                    {
                        var evt = window.document.createEvent('UIEvents');
                        evt.initUIEvent('resize', true, false, window, 0);
                        window.dispatchEvent(evt);
                    }
                }
            }
        }, 100);
    },

    getTreeChecked: function (items) {
        var rv = [];
        for (var i = 0; i < items.length; i++) {
            if (items[i].children == null || items[i].children.length == 0) {
                rv.push(items[i].id);
            }
            else {
                rv = rv.concat(this.getTreeChecked(items[i].children));
            }
        }
        return rv;
    },

    getTreeItems: function (data,svals) {
        var rv = [];
        if (svals == undefined || svals == null) {
            svals = [];
        }

        for (var i = 0; i < data.length; i++) {
            var item = {};
            item.value = data[i].Value;
            item.name = data[i].Text;
            item.disabled = data[i].Disabled;
            item.selected = data[i].Selected || svals.indexOf(data[i].Value) > -1;
            item.icon = data[i].Icon;

            if (data[i].Children != null && data[i].Children.length > 0) {
                item.children = this.getTreeItems(data[i].Children, svals);
            }
            rv.push(item);
        }
        return rv;
    },

    getComboItems: function (data, svals, useDefaultvalue,disabled) {
        var rv = [];
        if (svals == undefined || svals == null) {
            svals = [];
        }
        if (data != null) {
            for (var i = 0; i < data.length; i++) {
                var item = {};
                item.value = data[i].Value;
                item.name = data[i].Text;
                item.disabled = disabled!=undefined?disabled: data[i].Disabled;
                item.selected = useDefaultvalue == true ? svals.indexOf(data[i].Value) > -1 : (data[i].Selected || svals.indexOf(data[i].Value) > -1);
                item.icon = data[i].Icon;
                if (data[i].Children != null && data[i].Children.length > 0) {
                    item.children = this.getTreeItems(data[i].Children, svals);
                }
                rv.push(item);
            }
        }
        return rv;
    },

    getTransferItems: function (data, svals) {
        var rv = [];
        if (svals == undefined || svals == null) {
            svals = [];
        }

        for (var i = 0; i < data.length; i++) {
            var item = {};
            item.value = data[i].Value;
            item.title = data[i].Text;
            item.disabled = data[i].Disabled;
            //item.checked = data[i].Selected || svals.indexOf(data[i].Value) > -1;
            rv.push(item);
        }
        return rv;
    },


    changeComboIcon: function (data) {
        for (var i = 0; i < data.elem.length; i++) {
            if (data.elem[i].value === data.value) {
                var value = data.value
                    , iconFont = $(data.elem[i]).attr('icon')
                    , comboTitle = data.othis.children('.layui-select-title')
                    , icon = comboTitle.children('._wtm-combo-icon')
                    , comboInput = comboTitle.children('input');
                if (icon.length !== 0) {
                    icon.remove();
                    $(comboInput).removeAttr("style");
                }
                if (iconFont !== undefined && iconFont !== null) {
                    icon = $('<i class="_wtm-combo-icon ' + iconFont + '"></i>');

                    comboTitle.prepend(icon);
                    $(comboInput).css({ "padding-left": "30px" });
                }
                break;
            }
        }
    },

    resetForm: function (formId) {

        $("#" + formId).find('input[type=text],select').each(function () {
            $(this).val('');
        });

        var hidAreas = [' input[wtm-tag=wtmselector]'];
        // 多选下拉框
        var multiCombos = $('#' + formId + ' div[wtm-ctype=combo]').add($('#' + formId + ' div[wtm-ctype=tree]'));
        if (multiCombos && multiCombos.length > 0) {
            multiCombos.each(function () {
                let name = $(this).attr('id');
                window[name].setValue([]);
            }
            );
        }
        for (var i = 0; i < hidAreas.length; i++) {
            var hiddenAreas = $('#' + formId + hidAreas[i]);
            if (hiddenAreas && hiddenAreas.length > 0) {
                for (var j = 0; j < hiddenAreas.length; j++) {
                    hiddenAreas[j].remove();
                }
            }
        }
    },

    refreshcombobox: function (select, arr) {
        layui.formSelects.on({
            layFilter: select.layFilter, left: '', right: '', separator: ',', arr: arr,
            url: select.url, self: select.self, targetname: select.targetname, linkto: select.linkto, cf: select.cf,
            selectFunc: select.selectFunc
        });
    }
};

// Issue #470 Slice L: ff.upload — shared, id-parameterized namespace
// replacing the per-widget window['{Id}DoDelete']/['{Id}DoPreview']/
// ['{Id}SetValues'] globals UploadTagHelper/MultiUploadTagHelper's legacy
// inline <script> declared. Consumed by ff._renderUploadAction/
// ff._renderMultiUploadAction/ff._buildUploadExistingEntry (above) AND by
// the delegated click listener below.
//
// Per-instance data (connection string, field name, upload mode, the
// multi-select's currently-selected file ids) is carried via data-wtm-*
// attributes on the widget's own hidden `#{id}` input — UploadTagHelper.cs/
// MultiUploadTagHelper.cs emit these ONLY when UseSelectIslandRender is ON
// (see those files), present in the DOM at HTML-PARSE time (same technique
// as ComboBoxTagHelper's data-wtm-defaults, #470 Slice J), independent of
// the 'upload' layui module's own async load timing or the render island's
// dispatch timing. ff.upload._getState lazily reads and caches those
// attributes into a per-id in-memory registry on first access — whichever
// caller asks first (a delegated click on an existing-file preview/delete
// button, or the render island itself once its module has loaded) seeds
// the SAME shared state object, so mutations (e.g. a MultiUpload delete
// removing an id from `selected`) are visible to every subsequent caller
// regardless of call order. This is what makes the existing-file delegated
// handlers work "regardless of render timing": they never depend on the
// 'upload' module or the render island having dispatched yet.
window.ff.upload = {
    _state: {},

    _getState: function (id) {
        if (ff.upload._state[id]) { return ff.upload._state[id]; }
        var el = document.getElementById(id);
        var state = {
            mode: (el && el.getAttribute('data-wtm-upload-mode')) || 'single',
            cs: (el && el.getAttribute('data-wtm-cs')) || '',
            fieldName: (el && el.getAttribute('data-wtm-field-name')) || '',
            selected: []
        };
        if (el) {
            var raw = el.getAttribute('data-wtm-selected');
            if (raw) {
                try {
                    var parsed = JSON.parse(raw);
                    if (Array.isArray(parsed)) { state.selected = parsed; }
                } catch (e) { /* malformed attribute -> empty selected, never throws */ }
            }
        }
        ff.upload._state[id] = state;
        return state;
    },

    // Mirrors the legacy inline <script>'s {Id}DoDelete(fileid) exactly, for
    // both modes. Single mode: append a 'DeletedFileIds' hidden input (via
    // ff._makeInput — the SAME DOM-safe builder #470 Slice K used for
    // Transfer's hidden inputs, replacing the legacy's raw HTML string
    // concatenation of `fileid`), clear the label, clear the hidden value,
    // reset the (page-wide, same as legacy) progress bar. Multi mode: ajax
    // DELETE round trip, then remove the '#label'+fileid element, filter
    // `fileid` out of the shared selected-ids state, and re-run setValues.
    doDelete: function (id, fileid) {
        var state = ff.upload._getState(id);
        if (state.mode === 'multi') {
            $.ajax({
                type: 'get',
                url: '/api/_file/DeletedFile/' + fileid,
                success: function () {
                    var lbl = document.getElementById('label' + fileid);
                    if (lbl && lbl.parentNode) { lbl.parentNode.removeChild(lbl); }
                    state.selected = state.selected.filter(function (item) { return item != fileid; });
                    ff.upload.setValues(id);
                },
                error: function () {
                    if (typeof console !== 'undefined' && console.log) { console.log('failed'); }
                }
            });
        } else {
            var el = document.getElementById(id);
            var form = (el && typeof el.closest === 'function') ? el.closest('form') : null;
            if (form) {
                var delInput = ff._makeInput('hidden', 'DeletedFileIds', fileid);
                delInput.id = 'DeletedFileIds';
                form.appendChild(delInput);
            }
            var label = document.getElementById(id + 'label');
            if (label) { label.innerHTML = ''; }
            if (el) { el.value = ''; }
            var bars = document.querySelectorAll('.layui-progress .layui-progress-bar');
            for (var i = 0; i < bars.length; i++) { bars[i].style.width = '0%'; }
        }
    },

    // Mirrors the legacy inline <script>'s {Id}DoPreview(fileid) exactly,
    // for both modes. Single mode: open a single-photo layer.photos view
    // built from the connection-string-scoped GetFile URL (state.cs, read
    // from data-wtm-cs). Multi mode: open the container-wide layer.photos
    // gallery keyed by '#{id}label' (layui reads each descendant's
    // layer-src attribute itself) — `fileid` is accepted for a uniform
    // (id, fileid) call signature but unused here, matching the legacy
    // {Id}DoPreview() being called with an (ignored) argument in the
    // MultiUpload render/existing-file click bindings.
    doPreview: function (id, fileid) {
        if (typeof layui === 'undefined' || !layui.layer || typeof layui.layer.photos !== 'function') { return; }
        var state = ff.upload._getState(id);
        if (state.mode === 'multi') {
            layui.layer.photos({ photos: '#' + id + 'label', anim: 5 });
        } else {
            layui.layer.photos({
                photos: { data: [{ src: '/_Framework/GetFile/' + fileid + '?_DONOT_USE_CS=' + state.cs }] },
                anim: 5
            });
        }
    },

    // Mirrors the legacy inline <script>'s {Id}SetValues() exactly: remove
    // every previously-appended hidden input marked with the dynamic
    // `{id}hidden="{id}"` attribute (the SAME dynamic-attribute-name trick
    // the legacy jQuery selector used), then append one fresh hidden input
    // per currently-selected file id (via ff._makeInput, same #332-class
    // hardening as doDelete above), and set the visible `#{id}` hidden
    // field to '1'/'' depending on whether anything is selected. Multi mode
    // only — called from ff._renderMultiUploadAction's initial seed, its
    // upload-done callback, and ff.upload.doDelete's multi-mode branch.
    setValues: function (id) {
        var state = ff.upload._getState(id);
        var el = document.getElementById(id);
        if (!el) { return; }
        var form = typeof el.closest === 'function' ? el.closest('form') : null;
        if (!form) { return; }
        var stale = form.querySelectorAll('[' + id + 'hidden="' + id + '"]');
        for (var si = 0; si < stale.length; si++) {
            if (stale[si].parentNode) { stale[si].parentNode.removeChild(stale[si]); }
        }
        var count = 0;
        for (count = 0; count < state.selected.length; count++) {
            var name = state.fieldName + '[' + count + '].FileId';
            var input = ff._makeInput('hidden', name, state.selected[count]);
            input.setAttribute(id + 'hidden', id);
            form.appendChild(input);
        }
        el.value = count > 0 ? '1' : '';
    }
};

// Issue #470 Slice L: single document-level delegated click listener for
// ff.upload's doDelete/doPreview — mirrors the #470 Slice G data-wtm-counter
// delegation pattern above exactly (registered once, unconditionally; a
// complete no-op on any page/click that never has the attribute, so this
// contributes zero behavior change when UseSelectIslandRender is OFF, since
// the TagHelpers then never emit data-wtm-upload-action at all). Handles
// BOTH the freshly-uploaded-file markup ff._renderUploadAction/
// ff._renderMultiUploadAction build and the existing-file markup
// ff._buildUploadExistingEntry builds — one mechanism, not two — so it works
// identically regardless of which of those built the clicked element, and
// regardless of whether the click happens before or after the 'upload'
// layui module (or the existing-file ajax round trip) has resolved.
if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
    document.addEventListener('click', function (e) {
        var target = e && e.target;
        if (!target || typeof target.getAttribute !== 'function') { return; }
        var act = target.getAttribute('data-wtm-upload-action');
        if (!act) { return; }
        var id = target.getAttribute('data-wtm-upload-id');
        if (!id) { return; }
        var fileId = target.getAttribute('data-wtm-file-id');
        if (act === 'delete') { ff.upload.doDelete(id, fileId); }
        else if (act === 'preview') { ff.upload.doPreview(id, fileId); }
    });
}

// Issue #470 Slice M: shared dispatch map for the delegated data-wtm-click
// listener below. Each entry receives the clicked element (already resolved
// via closest('[data-wtm-click]')) and invokes exactly ONE fixed framework
// action (ff.OpenDialog / ff.RunAction / ff.BgRequest / ff.LoadPage /
// layui.layer.photos / ff.SetGridCellDate / a guarded developer callback) —
// no eval, no new Function, no per-button generated function. Mirrors the
// #470 Slice L upload delegated listener above and DispatchAction's
// whitelist philosophy: the data-wtm-click value and its accompanying
// data-wtm-* attributes are compile-time, developer-authored output of
// LayuiUIService.Make*/MakeDateTime (or, for 'scriptCall', a bare no-arg
// identifier extracted from a developer-authored 'script' string at render
// time) — never request/user data. Registered once, unconditionally; a
// complete no-op on any page/click that never carries data-wtm-click, so
// this contributes zero behavior change when UseSelectIslandRender is OFF
// (LayuiUIService then never emits data-wtm-click at all).
window.ff._buttonAction = {
    openDialog: function (el) {
        var url = el.getAttribute('data-wtm-url') || '';
        var winid = el.getAttribute('data-wtm-winid') || '';
        var title = el.getAttribute('data-wtm-title') || '';
        var widthAttr = el.getAttribute('data-wtm-width');
        var heightAttr = el.getAttribute('data-wtm-height');
        var width = (widthAttr === null || widthAttr === '') ? undefined : Number(widthAttr);
        var height = (heightAttr === null || heightAttr === '') ? undefined : Number(heightAttr);
        var max = el.getAttribute('data-wtm-max') === 'true';
        ff.OpenDialog(url, winid, title, width, height, undefined, max);
    },
    runAction: function (el) {
        ff.RunAction(el.getAttribute('data-wtm-url') || '');
    },
    bgRequest: function (el) {
        ff.BgRequest(el.getAttribute('data-wtm-url') || '', undefined, el.getAttribute('data-wtm-divid') || '');
    },
    loadPage: function (el) {
        var newwindow = el.getAttribute('data-wtm-newwindow') === 'true';
        ff.LoadPage(el.getAttribute('data-wtm-url') || '', newwindow, el.getAttribute('data-wtm-title') || '');
    },
    view: function (el) {
        if (typeof layui === 'undefined' || !layui.layer || typeof layui.layer.photos !== 'function') { return; }
        layui.layer.photos({ photos: { data: [{ src: el.getAttribute('data-wtm-url') || '' }] }, anim: 5 });
    },
    dateClick: function (el) {
        var id = el.getAttribute('data-wtm-date-id') || el.id || '';
        var dt = el.getAttribute('data-wtm-date-type') || '';
        ff.SetGridCellDate(id, dt);
    },
    // MakeScriptButton's developer 'script' is the one exception in this
    // dispatch map that is NOT a fixed framework call — only reached when the
    // server already reduced 'script' to a bare no-arg identifier call (see
    // LayuiUIService.MakeScriptButton), and even then resolved through the
    // SAME guarded ff._resolveGuardedWindowFn(name) every other named-callback
    // action in this file uses (identifier regex + denylist + own-property +
    // typeof-function checks) before ever being invoked.
    scriptCall: function (el) {
        var fn = ff._resolveGuardedWindowFn(el.getAttribute('data-wtm-fn'));
        if (fn) { fn(); }
    }
};

if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
    document.addEventListener('click', function (e) {
        var target = e && e.target;
        if (!target || typeof target.closest !== 'function') { return; }
        var el = target.closest('[data-wtm-click]');
        if (!el) { return; }
        var handler = ff._buttonAction[el.getAttribute('data-wtm-click')];
        if (typeof handler === 'function') {
            handler(el);
        }
    });
}

// Issue #470 Slice M: SubmitButtonTagHelper's f_{Id}Click handshake, expressed
// as a fixed, reusable framework function instead of a generated per-button
// <script>function f_{Id}Click(){...}</script>. Replays the EXACT SAME
// {formid}validate / #{formid}hidesubmit / ff.PostForm sequence the legacy
// generated function used, reading it off data-wtm-submit-* attributes
// (compile-time, developer-authored Razor literals — never request/field
// data) carried on the button element.
//
// Called as `ff._submitButtonClick('{Id}')` — a server-known STRING ID
// LITERAL, never `this`. BaseButtonTag.Process's ConfirmTxt handling wraps
// Click inside a NEW nested `function(index){ ... }` passed as layer.confirm's
// 3rd argument whenever ConfirmTxt is also set; inside that plain nested
// function `this` is NOT the clicked element, so resolving by `this` would
// silently short-circuit the handshake below in that (very common,
// confirm-before-submit) case. Resolving via document.getElementById works
// identically regardless of call context. A DOM element is still accepted
// (and preferred, skipping the lookup) for callers/tests that already hold
// one.
//
// checkFnName resolution failure (missing/denylisted/not-a-function) falls
// back to `check = true` (submit proceeds without the gate) rather than
// throwing — this mirrors the SAME "unresolved guarded callback silently
// skips that one gate, never blocks" precedent the 'bindSubmit' DispatchAction
// case's beforeSubmit resolution already established (see the comment above
// that case), not a new failure mode introduced here.
window.ff._submitButtonClick = function (elOrId) {
    var el = (typeof elOrId === 'string') ? document.getElementById(elOrId) : elOrId;
    if (!el || typeof el.getAttribute !== 'function') { return false; }
    var checkFnName = el.getAttribute('data-wtm-submit-checkfn');
    var check = true;
    if (checkFnName) {
        var fn = ff._resolveGuardedWindowFn(checkFnName);
        check = fn ? fn() : true;
    }
    // Intentionally loose (`==`) — NOT a typo. SubmitButtonTagHelper's legacy
    // generated f_{Id}Click() used `check == undefined || check == false`
    // (see SubmitButtonTagHelper.cs), so falsy-but-not-strictly-false check
    // results (0, "", "0", null) must ALSO block submission here to be the
    // "EXACT SAME" handshake this function claims to reproduce. Do not
    // "fix" this to `===` — that would silently change gating semantics for
    // any developer checkFn that returns one of those values.
    if (check == undefined || check == false) { return false; }
    var formid = el.getAttribute('data-wtm-submit-formid') || '';
    var divid = el.getAttribute('data-wtm-submit-divid') || '';
    try {
        window[formid + 'validate'] = false;
        $('#' + formid + 'hidesubmit').trigger('click');
    } catch (e) {
        window[formid + 'validate'] = true;
    }
    if (window[formid + 'validate'] === true) {
        ff.PostForm('', formid, divid);
    }
    return false;
};

// ─── Header Column Filter ─────────────────────────────────────────────────────
var wtmHeaderFilter = (function () {
    'use strict';
    var _filters = {};   // { gridId: { field: value } }
    var _debounce = {};  // debounce timers per gridId

    function _ensureStyles() {
        if (document.getElementById('wtm-hf-styles')) return;
        var css = [
            '.wtm-hf-row td { background: #f5f5f5; }',
            '.wtm-hf-cell { padding: 2px 4px !important; vertical-align: middle !important; }',
            '.wtm-hf-input {',
            '  display: block; width: 100%; height: 24px;',
            '  border: 1px solid #d2d2d2; border-radius: 3px;',
            '  padding: 0 5px; font-size: 12px; outline: none;',
            '  box-sizing: border-box; background: #fff; color: #333;',
            '}',
            '.wtm-hf-input:focus { border-color: #1e9fff; box-shadow: 0 0 0 2px rgba(30,159,255,.12); }',
            '.wtm-hf-input::placeholder { color: #bbb; font-size: 11px; }'
        ].join('\n');
        var el = document.createElement('style');
        el.id = 'wtm-hf-styles';
        el.textContent = css;
        document.head.appendChild(el);
    }

    // Called before table.render() — initialise state only
    function init(gridId) {
        if (!_filters[gridId]) _filters[gridId] = {};
        _ensureStyles();
    }

    // Called in done callback — inject/re-inject filter row and restore state
    function refresh(gridId) {
        var $view = $('#' + gridId + ' + .layui-table-view');
        if (!$view.length) return;
        _injectRow(gridId, $view);
        _bindEvents(gridId, $view);
        // Restore any previously entered filter values
        var filters = _filters[gridId] || {};
        Object.keys(filters).forEach(function (field) {
            if (filters[field]) {
                $view.find('.wtm-hf-input[data-field="' + field + '"]').val(filters[field]);
            }
        });
        _applyFilters(gridId, $view);
    }

    function _injectRow(gridId, $view) {
        $view.find('.wtm-hf-row').remove();
        // Only target the main header (direct child of layui-table-box).
        // Fixed-column headers (.layui-table-fixed) are position:absolute with
        // z-index:101 — injecting a wrong-width row there covers the data area.
        var $mainHeader = $view.find('.layui-table-box > .layui-table-header');
        if (!$mainHeader.length) return;
        var $headerTr = $mainHeader.find('thead tr:last-child');
        if (!$headerTr.length) return;

        var cells = [];
        $headerTr.find('th').each(function () {
            var field = $(this).data('field');
            if (field && typeof field === 'string') {
                cells.push(
                    '<td class="wtm-hf-cell">' +
                    '<div class="layui-table-cell" style="padding:0 2px;">' +
                    '<input class="wtm-hf-input" data-field="' + field + '" placeholder="\uD83D\uDD0D" />' +
                    '</div></td>'
                );
            } else {
                cells.push('<td class="wtm-hf-cell"><div class="layui-table-cell"></div></td>');
            }
        });

        if (cells.length) {
            $mainHeader.find('thead').append(
                $('<tr class="wtm-hf-row">' + cells.join('') + '</tr>')
            );
        }
    }

    function _bindEvents(gridId, $view) {
        $view.off('input.wtmhf').on('input.wtmhf', '.wtm-hf-input', function () {
            var field = $(this).attr('data-field');
            var val = $(this).val();
            if (!_filters[gridId]) _filters[gridId] = {};
            _filters[gridId][field] = val;
            clearTimeout(_debounce[gridId]);
            _debounce[gridId] = setTimeout(function () {
                _applyFilters(gridId, $view);
            }, 150);
        });
    }

    function _applyFilters(gridId, $view) {
        var filters = _filters[gridId] || {};
        // Build active filter map (non-empty values only)
        var active = {};
        Object.keys(filters).forEach(function (k) {
            var v = (filters[k] || '').trim();
            if (v) active[k] = v.toLowerCase();
        });

        // No active filters — restore all rows that may have been hidden by a previous filter (#511)
        if (Object.keys(active).length === 0) {
            $view.find('.layui-table-main tbody tr').show();
            $view.find('.layui-table-fixed .layui-table-body tbody tr').show();
            $view.find('.layui-table-fixed-r .layui-table-body tbody tr').show();
            return;
        }

        var $mainTbody = $view.find('.layui-table-main tbody');
        var visibility = [];

        $mainTbody.find('tr').each(function (i) {
            var $row = $(this);
            var show = true;
            if (Object.keys(active).length > 0) {
                var keys = Object.keys(active);
                for (var ki = 0; ki < keys.length; ki++) {
                    var field = keys[ki];
                    var $cell = $row.find('td[data-field="' + field + '"]');
                    var text = $cell.find('.layui-table-cell').text().toLowerCase();
                    if (text.indexOf(active[field]) < 0) { show = false; break; }
                }
            }
            visibility.push(show);
            $row.toggle(show);
        });

        // Sync fixed-left and fixed-right column rows by index
        $view.find('.layui-table-fixed .layui-table-body tbody tr').each(function (i) {
            $(this).toggle(visibility[i] !== false);
        });
        $view.find('.layui-table-fixed-r .layui-table-body tbody tr').each(function (i) {
            $(this).toggle(visibility[i] !== false);
        });
    }

    // #353: teardown — clear per-grid filter state and debounce timers
    function destroy(gridId) {
        if (_debounce[gridId]) {
            clearTimeout(_debounce[gridId]);
            delete _debounce[gridId];
        }
        delete _filters[gridId];
    }

    return { init: init, refresh: refresh, destroy: destroy };
}());
window.wtmHeaderFilter = wtmHeaderFilter;

/**
 * wtmColVis — persists LayUI grid column visibility to localStorage (issue #639).
 *
 * Storage key per table: 'wtm_col_vis_{tableId}'  (array of hidden field names).
 *
 * Called from the table's done callback:
 *   wtmColVis.init('myGridId');
 *
 * When the user opens the "筛选列" panel and toggles a column, the new state
 * is written to localStorage automatically.
 */
var wtmColVis = (function () {
    var _indexToId  = {};   // { layuiTableIndex (string) : tableId }
    var _restored   = {};   // { tableId: true }  — guard so restore runs once per load
    var _registered = false;

    function _storageKey(tableId) {
        return 'wtm_col_vis_' + tableId;
    }

    /**
     * Collect field names whose hide flag is currently true.
     * Pure function — no side-effects, safe to unit-test.
     * @param {Array<Array>} cols  option.cols from a LayUI table options object
     * @returns {string[]}
     */
    function _collectHidden(cols) {
        var hidden = [];
        if (!cols) return hidden;
        for (var i1 = 0; i1 < cols.length; i1++) {
            var row = cols[i1];
            for (var i2 = 0; i2 < row.length; i2++) {
                var col = row[i2];
                if (col && col.field && col.hide) {
                    hidden.push(col.field);
                }
            }
        }
        return hidden;
    }

    function _save(tableId) {
        var option = window[tableId + 'option'];
        if (!option || !option.cols) return;
        var hidden = _collectHidden(option.cols);
        try {
            localStorage.setItem(_storageKey(tableId), JSON.stringify(hidden));
        } catch (e) { /* storage quota exceeded — ignore */ }
    }

    // Register a single document-level handler for the column-filter checkboxes.
    // Runs lazily on the first init() call so layui is guaranteed to be loaded.
    function _registerOnce() {
        if (_registered) return;
        _registered = true;
        layui.use(['form'], function () {
            layui.form.on('checkbox(LAY_TABLE_TOOL_COLS)', function (data) {
                // data-key format: "{tableIndex}-{row}-{col}"
                var key = $(data.elem).attr('data-key') || '';
                var idx = key.split('-')[0];
                var tid = _indexToId[idx];
                if (!tid) return;
                // Run after LayUI's own handler updates col.hide
                setTimeout(function () { _save(tid); }, 0);
            });
        });
    }

    /**
     * Register a rendered table and restore its saved column visibility.
     * Safe to call on every table done() — restores only once per page load.
     * @param {string} tableId
     */
    function init(tableId) {
        _registerOnce();

        // Map LayUI's internal table index to our tableId.
        // option.index is set by LayUI during table.render().
        var option = window[tableId + 'option'];
        if (option && option.index !== undefined) {
            _indexToId[String(option.index)] = tableId;
        }

        // Restore only once per page load (done() fires on every reload)
        if (_restored[tableId]) return;
        _restored[tableId] = true;

        var saved;
        try {
            var raw = localStorage.getItem(_storageKey(tableId));
            saved = raw ? JSON.parse(raw) : null;
        } catch (e) { saved = null; }
        if (!saved || !saved.length) return;
        if (!option || !option.cols) return;

        // Apply saved hidden columns: update col.hide flags + DOM classes
        for (var i1 = 0; i1 < option.cols.length; i1++) {
            var row = option.cols[i1];
            for (var i2 = 0; i2 < row.length; i2++) {
                var col = row[i2];
                if (col && col.field && saved.indexOf(col.field) !== -1) {
                    col.hide = true;
                    $('#' + tableId + ' + .layui-table-view')
                        .find('[data-key="' + option.index + '-' + i1 + '-' + i2 + '"]')
                        .addClass('layui-hide');
                }
            }
        }
        try { layui.table.resize(tableId); } catch (e) { /* older LayUI versions */ }
    }

    return {
        init          : init,
        _collectHidden: _collectHidden,   // exposed for unit tests
        _storageKey   : _storageKey,      // exposed for unit tests
    };
}());
window.wtmColVis = wtmColVis;

/**
 * wtmPermFilter — client-side search filter for the role-permission tree table.
 *
 * The permission tree is rendered as a flat LayUI table where each row's
 * PageName cell carries leading &nbsp; entities to indicate depth
 * (4 &nbsp; per level).  filterTree() is a pure function so it can be
 * unit-tested without a DOM; apply() wires it to a rendered LayUI table.
 */
var wtmPermFilter = (function () {

    /**
     * Compute row visibility for a permission-tree table.
     *
     * @param {Array<{text: string, depth: number}>} rows
     *   Parallel to the rendered table rows.  text is the stripped page name
     *   (no &nbsp;, no HTML tags, trimmed).  depth is the tree level (0 = root).
     * @param {string} query  Case-insensitive substring to match.
     * @returns {boolean[]}  Parallel to rows — true means the row should show.
     */
    function filterTree(rows, query) {
        if (!query) {
            return rows.map(function () { return true; });
        }
        var q = query.toLowerCase();

        // Step 1: mark rows whose own text matches the query.
        var vis = rows.map(function (r) {
            return r.text.toLowerCase().indexOf(q) !== -1;
        });

        // Step 2: for every matching row, walk backwards and show each ancestor
        // (a row with strictly smaller depth that appears before it in the list).
        for (var i = 0; i < rows.length; i++) {
            if (!vis[i]) { continue; }
            var depth = rows[i].depth;
            for (var j = i - 1; j >= 0 && depth > 0; j--) {
                if (rows[j].depth < depth) {
                    vis[j] = true;
                    depth = rows[j].depth;
                }
            }
        }

        return vis;
    }

    /**
     * Apply a search filter to a rendered LayUI permission-tree table.
     *
     * @param {jQuery} $container  The .layui-table-view element wrapping the grid.
     * @param {string} query       Search string (empty string clears the filter).
     */
    function apply($container, query) {
        var $rows = $container.find('.layui-table-main tbody tr');
        var rowData = [];
        $rows.each(function () {
            var html = $(this).find('td').first().find('.layui-table-cell').html() || '';
            // &nbsp; entities in innerHTML — 4 per depth level.
            var nbspCount = (html.match(/&nbsp;/g) || []).length;
            var depth = Math.floor(nbspCount / 4);
            var tempDiv = document.createElement('div');
            tempDiv.innerHTML = html.replace(/&nbsp;/g, ' ');
            var text = (tempDiv.textContent || tempDiv.innerText || '').trim();
            rowData.push({ text: text, depth: depth });
        });

        var visible = filterTree(rowData, (query || '').trim());
        $rows.each(function (i) { $(this).toggle(visible[i]); });
    }

    return { filterTree: filterTree, apply: apply };
}());
window.wtmPermFilter = wtmPermFilter;

var wtmTheme = (function () {
    var STORAGE_KEY = 'wtm_theme_class';

    function _currentClass() {
        return localStorage.getItem(STORAGE_KEY) || '';
    }

    function _apply(cls) {
        var prev = localStorage.getItem(STORAGE_KEY);
        if (prev) { document.body.classList.remove(prev); }
        if (cls) {
            document.body.classList.add(cls);
            localStorage.setItem(STORAGE_KEY, cls);
        } else {
            localStorage.removeItem(STORAGE_KEY);
        }
    }

    function toggle(cls) {
        if (document.body.classList.contains(cls)) {
            _apply('');
        } else {
            _apply(cls);
        }
    }

    function init(defaultCls) {
        var stored = _currentClass();
        var cls = stored || defaultCls || '';
        if (cls) { document.body.classList.add(cls); }
    }

    return {
        init:           init,
        toggle:         toggle,
        _apply:         _apply,
        _currentClass:  _currentClass,
        _storageKey:    function () { return STORAGE_KEY; },
    };
}());
window.wtmTheme = wtmTheme;

var wtmCounter = (function () {
    function init(fieldId, counterId, maxLen) {
        var field = document.getElementById(fieldId);
        var counter = document.getElementById(counterId);
        if (!field || !counter) return;
        function update() {
            var len = (field.value || '').length;
            counter.textContent = len + '/' + maxLen;
        }
        field.addEventListener('input', update);
        update();
    }
    return { init: init };
}());
window.wtmCounter = wtmCounter;

// Issue #470 Slice G: delegated wtmCounter wiring — TextAreaTagHelper
// (ShowCounter) now emits a `data-wtm-counter="<counterId>"` attribute on the
// textarea itself instead of a per-widget inline <script> calling
// wtmCounter.init(id, counterId, maxLen). A SINGLE document-level delegated
// 'input' listener (registered once, here) replaces per-widget
// addEventListener('input', ...) wiring: because the listener is bound to
// `document` itself (never the widget), it needs no re-scan/re-init when a
// counter-enabled textarea is inserted later (OpenDialog/OpenDialog2
// fragment, SPA-tab framework, …), unlike the wtm-dialog-init island
// consumers above — this is what "no island needed for a one-liner" buys.
// Keeps the counter working even when DisableLegacyScriptRehydration (#627)
// blocks the legacy inline <script> path. Reads maxLen from the textarea's
// own native `maxLength` DOM property (mirroring the `maxlength` HTML
// attribute TextAreaTagHelper already emits alongside data-wtm-counter)
// instead of threading a redundant second value through a data attribute —
// identical to the value wtmCounter.init's caller always passed. The
// original wtmCounter.init/update functions above are left byte-identical
// for back-compat with any external caller.
if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
    document.addEventListener('input', function (e) {
        var field = e && e.target;
        if (!field || typeof field.getAttribute !== 'function') { return; }
        var counterId = field.getAttribute('data-wtm-counter');
        if (!counterId) { return; }
        var counter = document.getElementById(counterId);
        if (!counter) { return; }
        var maxLen = field.maxLength;
        var len = (field.value || '').length;
        counter.textContent = len + '/' + maxLen;
    });
}

// Issue #556 (#470-B slice 1): idempotent page-ready consumer for
// .wtm-dialog-init islands present in the MAIN document at initial page
// load. This is what lets eval-free JSON-island initialisation (laydate,
// initForm, loadComboItems, ...) work for full-page (non-dialog) forms too,
// not just ff.OpenDialog partials.
//
// No dialog/page-ready double-dispatch: see the matching comment in
// ff.OpenDialog above — dialog-origin islands are parsed from a detached
// DOMParser document BEFORE ff.SafeHtml (DOMPurify, FORBID_TAGS: ['script'])
// strips ALL <script> elements from the markup that is actually inserted
// into the live `document`. A dialog-origin island is therefore NEVER
// present in `document` for this consumer to find.
//
// Idempotency: every island this consumer touches is CLAIMED synchronously
// (marked data-wtm-dispatched="1") before its dispatch is scheduled, and the
// query excludes already-claimed nodes — so calling _consumePageReadyIslands()
// more than once (or a stray double DOMContentLoaded) can never schedule the
// same island twice.
//
// Timing race — FIXED (#556 hardening): claiming synchronously is safe here
// precisely BECAUSE the dispatch is routed through ff._dispatchIslandWhenReady,
// which defers laydate/form actions into layui.use([...], cb) so the render
// runs only once the module has loaded. The dispatch therefore cannot silently
// no-op on a not-yet-loaded module, so a claimed-but-unrendered island (the old
// "date field never renders on a full-page form" bug) is no longer possible —
// the render ALWAYS eventually happens. (The claim marks that the island has
// been *consumed*, i.e. its dispatch is guaranteed scheduled — not that the
// async render has already completed.)
window.ff._consumePageReadyIslands = function () {
    if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') { return; }
    var nodes = document.querySelectorAll('script[type="application/json"].wtm-dialog-init:not([data-wtm-dispatched])');
    for (var _ni = 0; _ni < nodes.length; _ni++) {
        var node = nodes[_ni];
        node.setAttribute('data-wtm-dispatched', '1');
        if (!node.textContent) { continue; }
        try {
            var parsed = JSON.parse(node.textContent);
            var normalized = ff._normalizeIslandPayload(parsed);
            if (normalized !== null) { ff._dispatchIslandWhenReady(normalized); }
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] page-ready island dispatch failed:', e);
            }
        }
    }
};

if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', ff._consumePageReadyIslands);
    } else {
        ff._consumePageReadyIslands();
    }
}

// Issue #587: scoped island consumer for DOM subtrees inserted by code paths
// that neither of the two existing consumers cover:
//   - ff._consumePageReadyIslands (above) only ever runs ONCE, against the
//     live `document`, at DOMContentLoaded (or immediately if the document
//     has already finished loading). It never re-scans anything inserted
//     into the page LATER.
//   - ff.OpenDialog's detached-DOMParser pre-collection (see the #462/#470/
//     #522 comments in OpenDialog's $.ajax success handler) reads islands
//     from a DETACHED parse of the same-origin partial BEFORE ff.SafeHtml/
//     DOMPurify strips <script> elements from what is actually inserted —
//     it is specific to that one call site's response-handling shape.
//
// Two real DOM-insertion paths bypass BOTH of the above:
//   GAP 1 — a SPA-tab framework (e.g. layuiadmin lib/view.js) that inserts
//   ajax'd HTML fragments into the LIVE document via jQuery .html() well
//   after the page's initial DOMContentLoaded: islands inside the fragment
//   are never `.wtm-dialog-init`-scanned by anything, so laydate/initForm/
//   bindSubmit/etc. never initialize for a field that only exists inside a
//   SPA tab's ajax'd content.
//   GAP 2 — ff.PostForm's validation-failure form-HTML redraw branch, fixed
//   directly in this same #587 change via the ff._collectInitFromHtml /
//   ff._replayInitFromHtml pair above (that branch doesn't need this
//   function — it never inserts an unstripped island into the live
//   document in the first place, so there's nothing left here to scan).
//
// ff.ConsumeIslandsIn(rootEl) closes GAP 1 (and any future similar one):
// callers invoke it immediately after inserting a fragment into the LIVE
// document, passing the just-inserted root element (its jQuery wrapper is
// also accepted and unwrapped via [0]). It scans rootEl's own subtree —
// INCLUDING rootEl itself, if rootEl is itself a matching island node — for
// un-claimed `.wtm-dialog-init` islands and dispatches each via
// ff._dispatchIslandWhenReady, using the exact same claim-then-dispatch
// idempotency mechanic as ff._consumePageReadyIslands (mark
// data-wtm-dispatched="1" BEFORE scheduling the dispatch, and exclude
// already-claimed nodes from the query), just scoped to rootEl instead of
// the whole document.
//
// rootEl contract — never throws:
//   - a DOM Element with a working querySelectorAll → scanned.
//   - a jQuery-wrapped element (has a truthy `.jquery` property) → unwrapped
//     via rootEl[0] first (an empty jQuery collection unwraps to
//     `undefined`, which then hits the no-op branch below).
//   - null / undefined / anything without a querySelectorAll function, or
//     an element not currently connected to the document (`isConnected ===
//     false`) → silent no-op. A detached rootEl is deliberately a no-op:
//     this consumer's whole contract is "islands that just became part of
//     the live page", and every action a dispatched island can run
//     (layui.*.render, form field write-backs by element id, …) assumes a
//     connected element to measure or attach to — exactly like every other
//     island-dispatch call site, which only ever runs against the live
//     document.
//
// SCOPED-ONLY BY DESIGN — this function intentionally has NO document-wide
// fallback path, and must never grow one. Some downstream SafeHtml shims
// (see the #587 issue's BMS-canary notes) can leave an OpenDialog-dispatched
// island unmarked (missing data-wtm-dispatched) inside dialog DOM after
// insertion. If ConsumeIslandsIn ever rescanned the whole document instead
// of the caller's own just-inserted rootEl, it would re-dispatch that
// already-handled island — double bindSubmit registration, double form
// submission. Scoping to rootEl means a caller can only ever re-claim
// islands within the subtree it itself just inserted, never something
// dispatched earlier by an unrelated code path elsewhere in the page.
window.ff.ConsumeIslandsIn = function (rootEl) {
    if (rootEl && rootEl.jquery) { rootEl = rootEl[0]; } // unwrap a jQuery object
    if (!rootEl || typeof rootEl.querySelectorAll !== 'function') { return; }
    if (rootEl.isConnected === false) { return; } // detached subtree → no-op

    var SELECTOR = 'script[type="application/json"].wtm-dialog-init:not([data-wtm-dispatched])';
    var nodes = [];
    try {
        if (typeof rootEl.matches === 'function' && rootEl.matches(SELECTOR)) {
            nodes.push(rootEl);
        }
    } catch (e) { /* matches() unsupported/failed on this node → skip the root-self check only */ }
    var scoped = rootEl.querySelectorAll(SELECTOR);
    for (var _si = 0; _si < scoped.length; _si++) { nodes.push(scoped[_si]); }

    for (var _ni = 0; _ni < nodes.length; _ni++) {
        var node = nodes[_ni];
        node.setAttribute('data-wtm-dispatched', '1');
        if (!node.textContent) { continue; }
        try {
            var parsed = JSON.parse(node.textContent);
            var normalized = ff._normalizeIslandPayload(parsed);
            if (normalized !== null) { ff._dispatchIslandWhenReady(normalized); }
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] ConsumeIslandsIn dispatch failed:', e);
            }
        }
    }
};

$.ajax({
    url: '/_framework/GetScriptLanguage',
    type: 'GET',
    success: function (data) {
        for (val in data) {
            ff[val] = data[val];
        }
    }
});