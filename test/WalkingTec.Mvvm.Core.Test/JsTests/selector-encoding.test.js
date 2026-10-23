// Node-only test (no browser, no network) for the markup the selector client code builds from row and selection data:
//  - the hidden inputs the selector dialog adds (the inline script of src/WalkingTec.Mvvm.Mvc/Views/_Framework/Selector.cshtml)
//  - the hidden inputs of the forms framework_layui.js posts for downloads and exports (ids of the selected rows)
// Usage: node selector-encoding.test.js <path to framework_layui.js> <path to Selector.cshtml>
'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');

const source = fs.readFileSync(process.argv[2], 'utf8');
const cshtml = fs.readFileSync(process.argv[3], 'utf8');

const SPECIAL = `a&b<c>"d'e</b><i id="m1">x</i>`;

// ---- framework_layui.js in a sandbox with a tiny jQuery stand-in: every markup string handed to $() is recorded ----------------
function createEnv() {
    const created = [];       // markup strings given to $('<...>')
    const forms = [];
    const sandbox = { console: { log() {}, error() {}, warn() {} } };
    sandbox.window = sandbox;
    sandbox.document = { body: { clientWidth: 1000 } };
    sandbox.location = { hash: '', href: '' };
    sandbox.alert = () => {};
    sandbox.layui = { layer: { alert: () => {} }, form: { render: () => {} }, use: () => {} };
    const htmlSet = [];
    function $(arg) {
        if (typeof arg === 'string' && arg.charAt(0) === '#') {
            return { append() {}, length: 0, closest: () => ({ length: 0 }), html(h) { htmlSet.push(h); return this; }, css() { return this; } };
        }
        if (typeof arg === 'string' && arg.charAt(0) === '<') {
            created.push(arg);
            const el = { markup: arg, children: [], append(c) { el.children.push(c); return el; }, submit() { forms.push(el); }, remove() {} };
            return el;
        }
        return { append() {}, length: 0, closest: () => ({ length: 0 }) };
    }
    $.ajax = () => {};
    $.cookie = () => '';
    $.extend = Object.assign;
    sandbox.$ = $;
    sandbox.jQuery = $;
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox, { filename: 'framework_layui.js' });
    sandbox.ff.GetSearchFormData = () => ({});
    return { sandbox, ff: sandbox.ff, created, forms, htmlSet };
}

// the inputs a form was built from, as [name, value] pairs after parsing the quoted attributes like a browser does
function decode(s) {
    return s.replace(/&(amp|lt|gt|quot|#39);/g, (_, n) => ({ amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'" }[n]));
}
function parseHidden(markup) {
    const m = /^<input type=(["'])hidden\1 name=(["'])([^"']*?)\2 value=(["'])(.*)\4>$/s.exec(markup);
    assert.ok(m, 'not a single well formed hidden input: ' + markup);
    const quote = m[4];
    assert.ok(!m[5].includes(quote) && !/[<>]/.test(m[5]), 'the value ends its attribute or tag: ' + markup);
    return [decode(m[3]), decode(m[5])];
}

let failed = 0;
function test(name, fn) {
    try { fn(); console.log('ok   - ' + name); }
    catch (e) { failed++; console.log('FAIL - ' + name + '\n' + (e && e.stack || e)); }
}

// ---- the addresses written into attributes ---------------------------------------------------------------------------------------------
const HOSTILE_URL = `/x?a=1&b="><b>1</b>'`;
function attributeOf(markup, name) {
    const m = new RegExp(name + '=(["\'])(.*?)\\1', 's').exec(markup);
    assert.ok(m, 'no ' + name + ' attribute in ' + markup);
    return m[2];
}

test('Download and DownloadExcelOrPdf: an ordinary address gives the markup it always gave', () => {
    const e = createEnv();
    e.ff.Download('/Gadget/Download', ['1']);
    e.ff.DownloadExcelOrPdf('/Gadget/Export', 'f1', {}, ['1']);
    assert.strictEqual(e.created[0], '<form method="POST" action="/Gadget/Download">');
    assert.ok(e.created.includes('<form method="POST" action="/Gadget/Export">'));
});

test('Download and DownloadExcelOrPdf: an address cannot end the attribute of the form', () => {
    const e = createEnv();
    e.ff.Download(HOSTILE_URL, []);
    e.ff.DownloadExcelOrPdf(HOSTILE_URL, 'f1', {}, []);
    for (const markup of e.created.filter(x => x.startsWith('<form'))) {
        const action = attributeOf(markup, 'action');
        assert.ok(!/["'<>]/.test(action), 'raw special characters in the action: ' + markup);
        assert.strictEqual(decode(action), HOSTILE_URL);
    }
});

test('LoadPage1: an ordinary external address gives the markup it always gave, a hostile one stays inside the attribute', () => {
    const e = createEnv();
    e.ff.LoadPage1('https://example.com/a/b', 'w');
    assert.deepStrictEqual(e.htmlSet, ["<iframe frameborder='no' border='0' height='100%' src='https://example.com/a/b'></iframe>"]);
    const h = createEnv();
    h.ff.LoadPage1("https://example.com/a'data-a='x\"><b>1</b>", 'w');
    const m = /^<iframe frameborder='no' border='0' height='100%' src='([^']*)'><\/iframe>$/.exec(h.htmlSet[0]);
    assert.ok(m, 'the address ended its attribute: ' + h.htmlSet[0]);
    assert.ok(!/[<>"]/.test(m[1]), 'raw special characters in the src: ' + h.htmlSet[0]);
});

// ---- the hidden inputs of the downloads and exports ------------------------------------------------------------------------------
test('Download: ordinary ids give the markup they always gave', () => {
    const e = createEnv();
    e.ff.Download('/Gadget/Download', ['1', 'a-b-c', '中文']);
    assert.deepStrictEqual(e.created.slice(1), [
        '<input type="hidden" name="Ids" value="1">',
        '<input type="hidden" name="Ids" value="a-b-c">',
        '<input type="hidden" name="Ids" value="中文">',
    ]);
});

test('Download: an id with special characters is shown as text and posted unchanged', () => {
    const e = createEnv();
    e.ff.Download('/Gadget/Download', [SPECIAL]);
    assert.strictEqual(e.created.length, 2);
    assert.deepStrictEqual(parseHidden(e.created[1]), ['Ids', SPECIAL]);
});

test('DownloadExcelOrPdf: ordinary values and ids give the markup they always gave', () => {
    const e = createEnv();
    e.ff.DownloadExcelOrPdf('/Gadget/Export', 'f1', { Name: 'abc', Tags: ['x', 'y'] }, ['1', '2']);
    const inputs = e.created.filter(x => x.startsWith('<input'));
    assert.ok(inputs.includes('<input type="hidden" name="Name" value="abc">'), inputs.join('\n'));
    assert.ok(inputs.includes('<input type="hidden" name="Tags[0]" value="x">'));
    assert.ok(inputs.includes('<input type="hidden" name="Tags[1]" value="y">'));
    assert.ok(inputs.includes('<input type="hidden" name="Ids" value="1">'));
    assert.ok(inputs.includes('<input type="hidden" name="Ids" value="2">'));
});

test('DownloadExcelOrPdf: special characters in values, names and ids are posted unchanged', () => {
    const e = createEnv();
    e.ff.DownloadExcelOrPdf('/Gadget/Export', 'f1', { ['N' + SPECIAL]: SPECIAL, List: [SPECIAL] }, [SPECIAL]);
    const pairs = e.created.filter(x => x.startsWith('<input')).map(parseHidden);
    const has = (n, v) => pairs.some(p => p[0] === n && p[1] === v);
    assert.ok(has('N' + SPECIAL, SPECIAL), JSON.stringify(pairs));
    assert.ok(has('List[0]', SPECIAL));
    assert.ok(has('Ids', SPECIAL));
});

// ---- the hidden inputs of the selector dialog (inline script of Selector.cshtml) ----------------------------------------------------
// The two statements that push a hidden input are taken from the view itself and run with the Razor parts replaced by their ordinary
// output, so a change of the view is tested, not a copy of it.
function viewStatements() {
    const found = cshtml.split(/\r?\n/).filter(l => /hiddenStrArr\.push\(/.test(l));
    assert.strictEqual(found.length, 2, 'expected two hiddenStrArr.push statements in Selector.cshtml');
    return found.map(l => l.replace(/@Html\.Raw\(SelectorScript\.HtmlAttributeInJsString\(ViewBag\.FieldName\)\)/g, 'Entity.Items')
        .replace(/@Html\.Raw\(SelectorScript\.JsString\(ViewBag\.ValName\)\)/g, 'ID')
        .replace(/@Html\.Raw\(SelectorScript\.JsString\(ViewBag\.TextName\)\)/g, 'Name'));
}
function runView(rows) {
    const e = createEnv();
    const hiddenStrArr = [];
    const selected = rows;
    const ctx = vm.createContext({ ff: e.ff, hiddenStrArr, selected });
    const [withRows, empty] = viewStatements();
    for (let i = 0; i < rows.length; i++) { ctx.i = i; vm.runInContext(withRows, ctx); }
    vm.runInContext(empty, ctx);
    return hiddenStrArr;
}

test('Selector dialog: ordinary values give the markup they always gave', () => {
    const out = runView([{ ID: '9d298e06-d818-422b-b9d0-86dce4c7e665', Name: 'Alpha' }, { ID: '2', Name: '中文' }]);
    assert.deepStrictEqual(out, [
        "<input type='hidden' wtm-tag='wtmselector' name='Entity.Items' value='9d298e06-d818-422b-b9d0-86dce4c7e665' />",
        "<input type='hidden' wtm-tag='wtmselector' name='Entity.Items' value='2' />",
        "<input type='hidden' wtm-tag='wtmselector' name='Entity.Items' value='' />",
    ]);
});

test('Selector dialog: a value with special characters is shown as text and posted unchanged', () => {
    const out = runView([{ ID: SPECIAL, Name: SPECIAL }]);
    const m = /^<input type='hidden' wtm-tag='wtmselector' name='Entity.Items' value='(.*)' \/>$/s.exec(out[0]);
    assert.ok(m, out[0]);
    assert.ok(!/['<>"]/.test(m[1]), 'raw special characters in the attribute: ' + m[1]);
    assert.strictEqual(decode(m[1]), SPECIAL);
});

test('Selector dialog: numbers are written as before', () => {
    const out = runView([{ ID: 5, Name: 'x' }]);
    assert.strictEqual(out[0], "<input type='hidden' wtm-tag='wtmselector' name='Entity.Items' value='5' />");
});

if (failed > 0) { console.log(failed + ' test(s) failed'); process.exit(1); }
console.log('all selector encoding tests passed');
