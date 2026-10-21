// Node-only test (no browser, no network) for the server script channel in framework_layui.js.
// Usage: node script-channel.test.js <path to framework_layui.js>
'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');

const file = process.argv[2];
const source = fs.readFileSync(file, 'utf8');

function createEnv() {
    const calls = { ajax: [], warn: [], layerClose: [], html: [] };
    const sandbox = { console: { log() {}, error() {}, warn: (...a) => calls.warn.push(a.join(' ')) } };
    sandbox.window = sandbox;
    sandbox.document = { body: { clientWidth: 1000 } };
    sandbox.location = { hash: '', href: '' };
    sandbox.alert = () => {};
    sandbox.layui = {
        layer: {
            load: () => 7,
            close: (i) => calls.layerClose.push(i),
            alert: () => {},
            open: () => 1,
        },
        use: () => {},
    };
    function $(sel) {
        return {
            attr: () => '/post', find: () => ({ length: 0 }), parents: () => ({ length: 0 }),
            parent: () => ({ html: (h) => calls.html.push(h) }),
        };
    }
    $.ajax = (opt) => calls.ajax.push(opt);
    $.cookie = () => 'divid1';
    sandbox.$ = $;
    sandbox.jQuery = $;
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox, { filename: 'framework_layui.js' });
    return { sandbox, ff: sandbox.ff, calls };
}

function response(isScript) {
    return { getResponseHeader: (n) => (n === 'IsScript' && isScript ? 'true' : null) };
}

const tests = [];
function test(name, fn) { tests.push([name, fn]); }

test('defaults: StrictScriptChannel is false and ExecServerScript exists', () => {
    const { ff } = createEnv();
    assert.strictEqual(ff.StrictScriptChannel, false);
    assert.strictEqual(typeof ff.ExecServerScript, 'function');
});

test('ExecServerScript runs standard commands against ff', () => {
    const { ff, sandbox } = createEnv();
    const seen = [];
    ff.Alert = (m, t) => seen.push(['Alert', m, t]);
    ff.RefreshGrid = (w, i) => seen.push(['RefreshGrid', w, i]);
    ff.ExecServerScript("ff.Alert('done','Info');ff.RefreshGrid('LAY_app_body',0);");
    assert.deepStrictEqual(seen, [['Alert', 'done', 'Info'], ['RefreshGrid', 'LAY_app_body', 0]]);
    ff.ExecServerScript("window.__x = 1;");
    assert.strictEqual(sandbox.__x, 1);
});

test('StrictScriptChannel=true: nothing runs, console.warn is called', () => {
    const { ff, sandbox, calls } = createEnv();
    ff.StrictScriptChannel = true;
    ff.ExecServerScript('window.__ran = true;');
    assert.strictEqual(sandbox.__ran, undefined);
    assert.strictEqual(calls.warn.length, 1);
});

for (const [entry, invoke, url] of [
    ['PostForm', (ff) => { ff.GetFormData = () => ''; ff.PostForm('/post', 'f1', 'd1'); }, '/post'],
    ['BgRequest', (ff) => ff.BgRequest('/bg', undefined, 'd1'), '/bg'],
    ['OpenDialog', (ff) => ff.OpenDialog('/dlg', 'w1', 'title', 300, 200), '/dlg'],
]) {
    test(`${entry}: IsScript=true goes through ExecServerScript and standard commands run`, () => {
        const { ff, calls } = createEnv();
        const execd = [];
        const alerts = [];
        const orig = ff.ExecServerScript;
        ff.ExecServerScript = function (t) { execd.push(t); return orig.call(ff, t); };
        ff.Alert = (m, t) => alerts.push([m, t]);
        invoke(ff);
        const req = calls.ajax.filter((a) => a.url === url);
        assert.strictEqual(req.length, 1);
        const text = "ff.Alert('ok','Info');";
        req[0].success(text, 'success', response(true));
        assert.deepStrictEqual(execd, [text]);
        assert.deepStrictEqual(alerts, [['ok', 'Info']]);
    });

    test(`${entry}: IsScript=false takes the HTML path and does not execute`, () => {
        const { ff, sandbox, calls } = createEnv();
        const execd = [];
        ff.ExecServerScript = (t) => execd.push(t);
        invoke(ff);
        let threw = null;
        try { calls.ajax.filter((a) => a.url === url)[0].success('window.__ran=1;', 'success', response(false)); } catch (e) { threw = e; }
        assert.deepStrictEqual(execd, []);
        assert.strictEqual(sandbox.__ran, undefined);
        if (entry !== 'OpenDialog') {
            assert.ok(calls.html.length >= 1, 'html should be rendered');
            assert.ok(String(calls.html[0]).includes('window.__ran=1;'));
        }
        void threw; // OpenDialog HTML path needs the full layui UI, only the no-exec contract matters here
    });

    test(`${entry}: StrictScriptChannel=true does not execute and warns`, () => {
        const { ff, sandbox, calls } = createEnv();
        ff.StrictScriptChannel = true;
        invoke(ff);
        calls.ajax.filter((a) => a.url === url)[0].success('window.__ran=1;', 'success', response(true));
        assert.strictEqual(sandbox.__ran, undefined);
        assert.strictEqual(calls.warn.length, 1);
    });
}

let failed = 0;
for (const [name, fn] of tests) {
    try { fn(); console.log('ok   - ' + name); }
    catch (e) { failed++; console.log('FAIL - ' + name + '\n' + (e && e.stack)); }
}
console.log(`${tests.length - failed}/${tests.length} passed`);
process.exit(failed ? 1 : 0);
