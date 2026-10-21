// Node-only test (no browser, no network) for the checkbox/radio option markup built in framework_layui.js (#262).
// Usage: node option-encoding.test.js <path to framework_layui.js>
'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');

const file = process.argv[2];
const source = fs.readFileSync(file, 'utf8');

// Creates the sandbox with a tiny jQuery/layui stand-in. Every markup string handed to target.append is recorded.
function createEnv(items) {
    const appended = [];
    const sandbox = { console: { log() {}, error() {}, warn() {} } };
    sandbox.window = sandbox;
    sandbox.document = { body: { clientWidth: 1000 } };
    sandbox.location = { hash: '', href: '' };
    sandbox.alert = () => {};
    const form = { render: () => {} };
    sandbox.layui = { layer: { alert: () => {} }, form, use: () => {} };
    const attrs = { 'wtm-ctype': 'checkbox', 'lay-filter': 'flt', 'wtm-name': 'Entity.Field', id: 'tgt' };
    const target = {
        0: { attributes: {}, innerHTML: '' },
        length: 1,
        attr: (n) => attrs[n],
        html: () => {},
        append: (s) => { appended.push(s); },
        find: () => target,
    };
    function $(sel) {
        if (sel === '#tgt') return target;
        if (sel === '#f1') return target;
        return target;
    }
    $.ajax = () => {};
    $.cookie = () => 'd';
    $.get = (url, _data, cb) => cb({ Data: items }, 'success');
    sandbox.$ = $;
    sandbox.jQuery = $;
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox, { filename: 'framework_layui.js' });
    return { sandbox, ff: sandbox.ff, attrs, appended };
}

const ordinary = [
    { Value: '1', Text: 'Alpha', Selected: false },
    { Value: '2', Text: 'Beta item', Selected: true },
    { Value: '3', Text: '中文選項', Selected: false },
];

// scenario name -> function producing the list of appended markup strings for the given items
const scenarios = {
    'LoadComboItems checkbox': (items) => {
        const e = createEnv(items); e.attrs['wtm-ctype'] = 'checkbox';
        e.ff.LoadComboItems('checkbox', '/u', 'tgt', 'Entity.Field', [], undefined, false);
        return e.appended;
    },
    'LoadComboItems checkbox selected+disabled': (items) => {
        const e = createEnv(items);
        e.ff.LoadComboItems('checkbox', '/u', 'tgt', 'Entity.Field', ['3'], undefined, true);
        return e.appended;
    },
    'LoadComboItems radio': (items) => {
        const e = createEnv(items);
        e.ff.LoadComboItems('radio', '/u', 'tgt', 'Entity.Field', ['3'], undefined, false);
        return e.appended;
    },
    'ChainChange checkbox': (items) => {
        const e = createEnv(items); e.attrs['wtm-ctype'] = 'checkbox';
        e.ff.ChainChange('/u', chainSelf(), false);
        return e.appended;
    },
    'ChainChange checkbox default values': (items) => {
        const e = createEnv(items); e.attrs['wtm-ctype'] = 'checkbox';
        e.sandbox['tgtdefaultvalues'] = ['3'];
        e.ff.ChainChange('/u', chainSelf(), true);
        return e.appended;
    },
    'ChainChange radio': (items) => {
        const e = createEnv(items); e.attrs['wtm-ctype'] = 'radio';
        e.ff.ChainChange('/u', chainSelf(), false);
        return e.appended;
    },
    'ChainChange radio default values': (items) => {
        const e = createEnv(items); e.attrs['wtm-ctype'] = 'radio';
        e.sandbox['tgtdefaultvalues'] = ['3', '2'];
        e.ff.ChainChange('/u', chainSelf(), true);
        return e.appended;
    },
};

function chainSelf() {
    return { attributes: { 'wtm-linkto': { value: 'tgt' } }, closest: () => ({ id: 'f1' }) };
}

// Expected markup for ordinary data, captured from the implementation before the encoding change.
const GOLDEN = {
    "LoadComboItems checkbox": [
        "<input type='checkbox'  name = 'Entity.Field' value = '1' title = 'Alpha'/>",
        "<input type='checkbox'  name = 'Entity.Field' value = '2' title = 'Beta item' checked />",
        "<input type='checkbox'  name = 'Entity.Field' value = '3' title = '中文選項'/>"
    ],
    "LoadComboItems checkbox selected+disabled": [
        "<input type='checkbox'  name = 'Entity.Field' value = '1' title = 'Alpha' disabled />",
        "<input type='checkbox'  name = 'Entity.Field' value = '2' title = 'Beta item' checked  disabled />",
        "<input type='checkbox'  name = 'Entity.Field' value = '3' title = '中文選項' checked  disabled />"
    ],
    "LoadComboItems radio": [
        "<input type='radio' name = 'Entity.Field' value = '1' title = 'Alpha'  />",
        "<input type='radio'  name = 'Entity.Field' value = '2' title = 'Beta item' checked />",
        "<input type='radio'  name = 'Entity.Field' value = '3' title = '中文選項' checked />"
    ],
    "ChainChange checkbox": [
        "<input type='checkbox' name = 'Entity.Field' value = '1' title = 'Alpha'  />",
        "<input type='checkbox'  name = 'Entity.Field' value = '2' title = 'Beta item' checked />",
        "<input type='checkbox' name = 'Entity.Field' value = '3' title = '中文選項'  />"
    ],
    "ChainChange checkbox default values": [
        "<input type='checkbox' name = 'Entity.Field' value = '1' title = 'Alpha'  />",
        "<input type='checkbox' name = 'Entity.Field' value = '2' title = 'Beta item'  />",
        "<input type='checkbox'  name = 'Entity.Field' value = '3' title = '中文選項' checked />"
    ],
    "ChainChange radio": [
        "<input type='radio' name = 'Entity.Field' value = '1' title = 'Alpha'  />",
        "<input type='radio'  name = 'Entity.Field' value = '2' title = 'Beta item' checked />",
        "<input type='radio' name = 'Entity.Field' value = '3' title = '中文選項'  />"
    ],
    "ChainChange radio default values": [
        "<input type='radio' name = 'Entity.Field' value = '1' title = 'Alpha'  />",
        "<input type='radio'  name = 'Entity.Field' value = '2' title = 'Beta item' checked />",
        "<input type='radio'  name = 'Entity.Field' value = '3' title = '中文選項' checked />"
    ]
};

// Strict parser for the markup these functions are allowed to produce: exactly one <input .../> element with
// quoted attributes. Anything else (extra element, extra attribute, broken quoting) makes the parse fail.
function decode(s) {
    return s.replace(/&(amp|lt|gt|quot|#39);/g, (_, n) => ({ amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'" }[n]));
}
function parseInput(html) {
    const m = /^<input((?:\s+[\w-]+\s*=\s*(?:'[^']*'|"[^"]*")|\s+[\w-]+)*)\s*\/>$/.exec(html);
    assert.ok(m, 'markup is not a single well-formed <input /> element: ' + html);
    const attrs = {};
    const re = /\s+([\w-]+)(?:\s*=\s*(?:'([^']*)'|"([^"]*)"))?/g;
    let a;
    while ((a = re.exec(m[1])) !== null) {
        assert.ok(!(a[1] in attrs), 'duplicate attribute ' + a[1]);
        attrs[a[1]] = a[2] !== undefined ? decode(a[2]) : a[3] !== undefined ? decode(a[3]) : '';
    }
    return attrs;
}

const tests = [];
function test(name, fn) { tests.push([name, fn]); }

test('EncodeHtml: encodes the five characters, null/undefined become empty, other types become strings', () => {
    const { ff } = createEnv([]);
    assert.strictEqual(ff.EncodeHtml(`&<>"'`), '&amp;&lt;&gt;&quot;&#39;');
    assert.strictEqual(ff.EncodeHtml(null), '');
    assert.strictEqual(ff.EncodeHtml(undefined), '');
    assert.strictEqual(ff.EncodeHtml(12), '12');
    assert.strictEqual(ff.EncodeHtml(false), 'false');
    assert.strictEqual(ff.EncodeHtml('plain text'), 'plain text');
    assert.strictEqual(ff.EncodeHtml('中文'), '中文');
});

for (const name of Object.keys(scenarios)) {
    test(`ordinary data: ${name} markup is identical to the pre-change output`, () => {
        assert.deepStrictEqual(scenarios[name](ordinary), GOLDEN[name]);
    });
}

const special = [
    { Value: `a'b"c<d>e&f`, Text: `T'1"<b>&amp;`, Selected: true },
    { Value: `x' onclick='alert(1)`, Text: `"><script>1</script>`, Selected: false },
    { Value: `v3`, Text: `</label><input name=evil value=1>`, Selected: false },
];

for (const name of Object.keys(scenarios)) {
    test(`special characters: ${name} yields one well-formed input per item with original values`, () => {
        const html = scenarios[name](special);
        assert.strictEqual(html.length, special.length);
        html.forEach((h, i) => {
            const attrs = parseInput(h);
            assert.strictEqual(attrs.value, special[i].Value);
            assert.strictEqual(attrs.title, special[i].Text);
            assert.strictEqual(attrs.name, 'Entity.Field');
            const extra = Object.keys(attrs).filter((k) => !['type', 'name', 'value', 'title', 'checked', 'disabled'].includes(k));
            assert.deepStrictEqual(extra, []);
        });
    });
}

test('special characters: name attribute is encoded too', () => {
    const e = createEnv(ordinary);
    e.ff.LoadComboItems('checkbox', '/u', 'tgt', `n'a"me`, [], undefined, false);
    e.appended.forEach((h) => assert.strictEqual(parseInput(h).name, `n'a"me`));
});

if (process.env.PRINT_GOLDEN) {
    const out = {};
    for (const name of Object.keys(scenarios)) out[name] = scenarios[name](ordinary);
    console.log(JSON.stringify(out, null, 4));
    process.exit(0);
}

let failed = 0;
for (const [name, fn] of tests) {
    try { fn(); console.log('ok   - ' + name); }
    catch (e) { failed++; console.log('FAIL - ' + name + '\n' + (e && e.stack || e)); }
}
console.log(`${tests.length - failed}/${tests.length} passed`);
process.exit(failed ? 1 : 0);
