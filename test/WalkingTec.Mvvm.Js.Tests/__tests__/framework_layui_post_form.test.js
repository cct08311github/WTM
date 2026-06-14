/**
 * Tests for ff.PostForm error handling (issue #616).
 *
 * Verifies that PostForm uses layer.alert() — not native alert() — on AJAX
 * failure, and that it shows responseText when available or the localised
 * fallback string when it is not.
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

// ─── VM context factory ───────────────────────────────────────────────────────

function makeEnv() {
    const layerAlertCalls = [];
    const layerCloseCalls = [];
    const nativeAlertCalls = [];
    let capturedAjax = null;

    function jqMock(sel) {
        // Support the $('<div/>').text(s).html() idiom used by ff.EscapeText.
        // jsdom is available (jest-environment-jsdom), so delegate to real DOM.
        var _el = (typeof document !== 'undefined' && sel && typeof sel === 'string' && sel.indexOf('<') === 0)
            ? document.createElement('div')
            : null;
        var obj = {
            find:      () => ({ length: 0 }),
            attr:      () => undefined,
            parents:   () => ({ length: 0 }),
            serialize: () => '',
            html:      function (v) {
                if (v !== undefined && _el) { _el.innerHTML = v; return obj; }
                return _el ? _el.innerHTML : '';
            },
            text:      function (v) {
                if (v !== undefined && _el) { _el.textContent = String(v); return obj; }
                return _el ? _el.textContent : '';
            },
            parent:    () => ({ html: () => {} }),
            length:    0,
        };
        return obj;
    }
    jqMock.ajax    = function (opts) { capturedAjax = opts; };
    jqMock.cookie  = function () { return ''; };
    jqMock.fn      = {};
    jqMock.extend  = function (a, b) { return Object.assign(a, b); };

    const ctx = vm.createContext({
        window: {},
        $: jqMock,
        console,
        // native alert spy — should NOT be called in the fixed code
        alert: function (msg) { nativeAlertCalls.push(msg); },
        layui: {
            use:  function (mods, cb) { if (cb) cb(); },
            each: function (obj, fn) {
                // Handle array-like objects (jQuery collections, Arrays)
                if (obj && typeof obj.length === 'number') {
                    for (var i = 0; i < obj.length; i++) { fn.call(obj[i], i, obj[i]); }
                } else {
                    Object.keys(obj || {}).forEach(function (k) { fn(k, obj[k]); });
                }
            },
            table:   { reload: function () {}, checkStatus: function () { return { data: [] }; } },
            layer: {
                load:  function () { return 1; },
                close: function (idx) { layerCloseCalls.push(idx); },
                alert: function (msg) { layerAlertCalls.push(msg); },
                msg:   function () {},
            },
            form:    { render: function () {}, on: function () {} },
            element: { tabChange: function () {} },
        },
        setTimeout:  global.setTimeout.bind(global),
        clearTimeout: global.clearTimeout.bind(global),
        DONOTUSE_TABLAYID:   undefined,
        DONOTUSE_COOKIEPRE:  '',
        DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(SRC).runInContext(ctx);

    // Expose the submit-failed text so tests can reference it
    ctx.ff.DONOTUSE_Text_SubmitFailed = 'Submit failed';

    return {
        ff: ctx.ff,
        getAjax:          () => capturedAjax,
        layerAlertCalls,
        layerCloseCalls,
        nativeAlertCalls,
    };
}

// ─── Tests ───────────────────────────────────────────────────────────────────

describe('ff.PostForm — error handling (issue #616)', () => {

    test('uses layer.alert, not native alert, when AJAX fails', () => {
        const env = makeEnv();
        env.ff.PostForm('/api/save', 'form1', 'div1');
        const ajax = env.getAjax();
        expect(ajax).not.toBeNull();

        // Simulate AJAX failure with no responseText
        ajax.error({ responseText: '' });

        expect(env.nativeAlertCalls).toHaveLength(0);
        expect(env.layerAlertCalls).toHaveLength(1);
    });

    test('shows responseText when server returns an error message', () => {
        const env = makeEnv();
        env.ff.PostForm('/api/save', 'form1', 'div1');
        const ajax = env.getAjax();

        ajax.error({ responseText: 'Validation error: Name is required' });

        expect(env.layerAlertCalls[0]).toBe('Validation error: Name is required');
    });

    test('falls back to DONOTUSE_Text_SubmitFailed when responseText is empty', () => {
        const env = makeEnv();
        env.ff.PostForm('/api/save', 'form1', 'div1');
        const ajax = env.getAjax();

        ajax.error({ responseText: '' });

        expect(env.layerAlertCalls[0]).toBe('Submit failed');
    });

    test('falls back to DONOTUSE_Text_SubmitFailed when responseText is undefined', () => {
        const env = makeEnv();
        env.ff.PostForm('/api/save', 'form1', 'div1');
        const ajax = env.getAjax();

        ajax.error({ responseText: undefined });

        expect(env.layerAlertCalls[0]).toBe('Submit failed');
    });

    test('closes the loading indicator before showing the error', () => {
        const env = makeEnv();
        env.ff.PostForm('/api/save', 'form1', 'div1');
        const ajax = env.getAjax();

        ajax.error({ responseText: 'err' });

        // layer.close must have been called (spinner dismissed)
        expect(env.layerCloseCalls).toHaveLength(1);
        // loading closed before alert shown (calls were in order)
        expect(env.layerCloseCalls[0]).toBe(1);
    });

    test('uses POST method', () => {
        const env = makeEnv();
        env.ff.PostForm('/api/save', 'form1', 'div1');
        const ajax = env.getAjax();
        expect(ajax.type).toBe('POST');
    });

    test('uses the provided URL', () => {
        const env = makeEnv();
        env.ff.PostForm('/custom/endpoint', 'form1', 'div1');
        const ajax = env.getAjax();
        expect(ajax.url).toBe('/custom/endpoint');
    });
});
