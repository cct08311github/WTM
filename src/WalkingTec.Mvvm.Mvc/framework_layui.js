
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
                'subpro', 'issearchbutton', 'oldpost', 'ischart', 'chartlink'
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
    _islandModulesFor: function (payload) {
        var needed = { form: false, laydate: false, slider: false, rate: false, colorpicker: false };
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
                }
            }
        }
        var mods = [];
        if (needed.form) { mods.push('form'); }
        if (needed.laydate) { mods.push('laydate'); }
        if (needed.slider) { mods.push('slider'); }
        if (needed.rate) { mods.push('rate'); }
        if (needed.colorpicker) { mods.push('colorpicker'); }
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
        for (var si = 0; si < initScripts.length; si++) {
            var se = document.createElement('script');
            se.text = initScripts[si];
            document.body.appendChild(se);          // executes synchronously in global scope
            if (se.parentNode) { se.parentNode.removeChild(se); } // tidy up; effects persist
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
            };
            layui.slider.render(_slOpts);
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
            _cpOpts.done = function (data) {
                if (_cpValFieldId) {
                    var _cpEl = document.getElementById(_cpValFieldId);
                    if (_cpEl && (!_cpFormId || (_cpFormEl != null && _cpFormEl.contains(_cpEl)))) {
                        _cpEl.value = data;
                    }
                }
            };
            layui.colorpicker.render(_cpOpts);
        } catch (e) {
            if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] colorpicker action failed:', e);
            }
        }
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
                case 'loadComboItems':
                    if (typeof ff.LoadComboItems === 'function' && action.url && action.id) {
                        ff.LoadComboItems(
                            action.controlType || undefined,
                            action.url,
                            action.id,
                            action.field || undefined,
                            action.selectVal || undefined
                        );
                    }
                    break;
                // Issue #556 (#470-B slice 1): thin JSON wrapper over
                // layui.laydate.render(). The opts object is built entirely
                // server-side (DateTimeTagHelper) and passed straight through —
                // no remapping, no callbacks (ready/change/done can't be
                // JSON-expressed, so callback-bearing date fields keep emitting
                // the legacy inline <script> instead of this action).
                case 'laydate':
                    if (action.opts && action.opts.elem &&
                        typeof layui !== 'undefined' && layui.laydate &&
                        typeof layui.laydate.render === 'function') {
                        try {
                            layui.laydate.render(action.opts || {});
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
                // layui.slider.render. SliderTagHelper only emits this action when
                // ChangeFunc/OnTipsFunc are both empty; either callback routes to
                // the legacy inline <script> instead (see SliderTagHelper).
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
                // wiring reproduced natively. ColorPickerTagHelper only emits this
                // action when ChangeFunc is empty; a non-empty ChangeFunc routes to
                // the legacy inline <script> instead (an arbitrary developer
                // callback name can't be safely JSON-expressed).
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
                    var _bsBeforeFn = null;
                    if (action.beforeSubmit &&
                        typeof action.beforeSubmit === 'string' &&
                        /^[A-Za-z_$][\w$]*$/.test(action.beforeSubmit) &&
                        !(WTM_BEFORESUBMIT_DENYLIST && WTM_BEFORESUBMIT_DENYLIST.has(action.beforeSubmit)) &&
                        Object.prototype.hasOwnProperty.call(window, action.beforeSubmit) &&
                        typeof window[action.beforeSubmit] === 'function') {
                        _bsBeforeFn = window[action.beforeSubmit];
                    }
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
                default:
                    if (typeof console !== 'undefined' && console.warn) {
                        console.warn('[WTM] Unknown WtmAction type:', action.type);
                    }
            }
        }
    },

    // Issue #789 Phase 3C: centralized legacy fallback for the deprecated
    // IsScript response header. Every call site routes through this single
    // helper so the total number of eval( tokens in this file is 1 (down
    // from 18 before Phase 1), making the removal of this helper a one-line
    // change once downstream apps have finished migrating to FFResultJson.
    _legacyScriptEval: function (code) {
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
                            for (var _si = 0; _si < _initScripts.length; _si++) {
                                var _se = document.createElement('script');
                                _se.text = _initScripts[_si];
                                document.body.appendChild(_se);          // executes synchronously in global scope
                                if (_se.parentNode) { _se.parentNode.removeChild(_se); } // tidy up; effects persist
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
                        template = template.replace(/[$]{2}script[$]{2}/img, "<script>").replace(/[$]{2}#script[$]{2}/img, "<\/script>");
                        //get old gridid
                        try {
                            var oldgridid = /table[.]reload\('(.*)',\s{0,}{/img.exec(template)[1];
                            //替换gridId
                            template = template.replace(new RegExp(oldgridid, "gim"), gridId);
                        }
                        catch (e) { }
                        safeStr = safeStr.replace('$$SearchPanel$$', template);
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
        switch (controltype) {
            case "combo":
                window[comboid].update({ data: [] });
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
                window[comboid].update({ data: [] });
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
                            df = window[comboid + "defaultvalues"];
                        }
                       window[comboid].update({ data: ff.getTreeItems(data.Data,df) });
                    }
                    if (controltype === "transfer") {
                        layui.transfer.reload(targetid, {
                            data: ff.getTransferItems(data.Data)
                        });
                    }

                    if (controltype === "combo") {
                        var df = [];
                        if (usedefaultvalue == true) {
                            df = window[comboid + "defaultvalues"]; 
                      }
                        window[comboid].update({ data: ff.getComboItems(data.Data, df, usedefaultvalue) });
                    }
                    if (controltype === "checkbox") {
                        for (i = 0; i < data.Data.length; i++) {
                            item = data.Data[i];
                            if (usedefaultvalue == true) {
                                var df = [];
                                df = window[comboid + "defaultvalues"];
                                // Issue #332: use ff._makeInput (DOM API) instead of HTML
                                // string concat to safely set name/value/title attributes.
                                target.append(ff._makeInput('checkbox', targetname, item.Value, item.Text, df.indexOf(item.Value) > -1, false));
                           }
                            else {
                                target.append(ff._makeInput('checkbox', targetname, item.Value, item.Text, item.Selected === true, false));
                            }
                        }
                        form.render('checkbox', targetfilter);
                    }
                    if (controltype === "radio") {
                        for (i = 0; i < data.Data.length; i++) {
                            item = data.Data[i];
                            if (usedefaultvalue == true) {
                                var df = [];
                                df = window[comboid + "defaultvalues"];
                                // Issue #332: use ff._makeInput (DOM API) instead of HTML
                                // string concat to safely set name/value/title attributes.
                                target.append(ff._makeInput('radio', targetname, item.Value, item.Text, df.indexOf(item.Value) > -1, false));
                           }
                            else {
                                target.append(ff._makeInput('radio', targetname, item.Value, item.Text, item.Selected === true, false));
                            }
                        }
                        form.render('radio', targetfilter);
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
               var i = 0;
               var item = null;
               if (controltype === "tree") {
                   var da = ff.getTreeItems(data.Data, svals);
                   window[controlid].update({ data: da });
                   if (cb !== undefined && cb != null) {
                       cb();
                   }
               }
               if (controltype == "transfer") {
                   layui.transfer.reload(controlid, {
                       data: ff.getTransferItems(data.Data, svals)
                   });
               }
               if (controltype === "combo") {
                   var da = ff.getComboItems(data.Data, svals,undefined,disabled);
                    window[controlid].update({ data: da });
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