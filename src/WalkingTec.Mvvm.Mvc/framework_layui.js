
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
    SafeHtml: function (rawHtml) {
        if (typeof window.DOMPurify === 'undefined' ||
            !window.DOMPurify ||
            typeof window.DOMPurify.sanitize !== 'function') {
            return '';
        }
        return window.DOMPurify.sanitize(rawHtml || '', {
            FORBID_TAGS: ['script', 'style'],
            FORBID_ATTR: ['onerror', 'onload', 'onclick', 'onmouseover', 'onfocus', 'onblur', 'onchange', 'onsubmit']
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
                    // Issue #470: extract opt-in JSON action island (<script type="application/json"
                    // class="wtm-dialog-init">) from the same-origin partial BEFORE SafeHtml strips
                    // the script elements. The island payload is dispatched via ff.DispatchAction
                    // after the dialog DOM is inserted — zero eval, no dynamic code.
                    var _dialogInitPayload = null;
                    try {
                        if (typeof _pdoc !== 'undefined') {
                            var _islandNode = _pdoc.querySelector('script[type="application/json"].wtm-dialog-init');
                            if (_islandNode && _islandNode.textContent) {
                                _dialogInitPayload = JSON.parse(_islandNode.textContent);
                            }
                        }
                    } catch (e) { /* malformed island JSON → skip; legacy path unaffected */ }
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
                            // Issue #470: dispatch JSON action island after legacy scripts so
                            // both paths are supported. No-op when no island was present.
                            if (_dialogInitPayload !== null) {
                                try {
                                    ff.DispatchAction(_dialogInitPayload);
                                } catch (e) {
                                    if (typeof console !== 'undefined' && console.warn) {
                                        console.warn('[WTM] initForm island dispatch failed:', e);
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

$.ajax({
    url: '/_framework/GetScriptLanguage',
    type: 'GET',
    success: function (data) {
        for (val in data) {
            ff[val] = data[val];
        }
    }
});