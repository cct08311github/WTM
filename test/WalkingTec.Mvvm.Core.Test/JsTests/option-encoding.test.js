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
    $.extend = (target, ...sources) => Object.assign(target, ...sources);
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
// one level of the entities the framework writes (what EncodeHtmlOnce decodes before it encodes)
function decodeOnce(s) {
    return s.replace(/&(amp|lt|gt|quot|apos|#39);/g, (_, n) => ({ amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", '#39': "'" }[n]));
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
            // layui reads the title attribute and writes it to the page as HTML: what it writes shows the text of the item
            assert.strictEqual(decode(attrs.title), decodeOnce(special[i].Text));
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

// ---- display text of the widgets (#279) ------------------------------------------------------------------------------

test('EncodeHtmlOnce: plain text is unchanged, markup characters are encoded, one level of entities is decoded first', () => {
    const { ff } = createEnv([]);
    assert.strictEqual(ff.EncodeHtmlOnce('plain text 中文 100%'), 'plain text 中文 100%');
    assert.strictEqual(ff.EncodeHtmlOnce(null), '');
    assert.strictEqual(ff.EncodeHtmlOnce(undefined), '');
    assert.strictEqual(ff.EncodeHtmlOnce(12), '12');
    // raw text
    assert.strictEqual(ff.EncodeHtmlOnce(`a<b>"c" 'd' & e`), 'a&lt;b&gt;&quot;c&quot; &#39;d&#39; &amp; e');
    // text that was HTML-encoded when it was saved shows as the text it stands for (not encoded twice)
    assert.strictEqual(ff.EncodeHtmlOnce('Tom &amp; Jerry &lt;b&gt; &#39;x&#39; &quot;y&quot; &#60;i&#62;'), 'Tom &amp; Jerry &lt;b&gt; &#39;x&#39; &quot;y&quot; &lt;i&gt;');
    // only one level is decoded: typed entity text stays what was typed
    assert.strictEqual(ff.EncodeHtmlOnce('&amp;lt;'), '&amp;lt;');
    // incomplete or unknown references are text
    assert.strictEqual(ff.EncodeHtmlOnce('R&D &unknown; &#xZZ; &#0; &#1114112;'), 'R&amp;D &amp;unknown; &amp;#xZZ; &amp;#0; &amp;#1114112;');
});

test('EncodeHtmlOnce: the result never contains an unencoded markup character, whatever the input mixes', () => {
    const { ff } = createEnv([]);
    const inputs = [`<i id="x">&lt;b&gt;`, `&#60;script&#62;1&#60;/script&#62;`, `&lt;&amp;lt;<`, `'"&#x3c;&#X3E;`, `&amp;#60;`];
    for (const input of inputs) {
        const out = ff.EncodeHtmlOnce(input);
        assert.ok(!/[<>"']/.test(out), 'unencoded character in: ' + out);
        assert.ok(!/&(?!(amp|lt|gt|quot|#39);)/.test(out), 'entity the encoder did not write in: ' + out);
    }
});

test('EncodeTitle: encoded for the attribute and for the HTML that layui writes from it; plain text unchanged', () => {
    const { ff } = createEnv([]);
    assert.strictEqual(ff.EncodeTitle('Plain item 中文'), 'Plain item 中文');
    const title = ff.EncodeTitle(special[1].Text);
    // the attribute value, as the browser reads it, is HTML that shows the text and holds no markup character
    const attributeValue = decode(title);
    assert.ok(!/[<>"']/.test(attributeValue), attributeValue);
    assert.strictEqual(decode(attributeValue), special[1].Text);
});

test('getTransferItems keeps the text as it is; encodeTransferItems encodes value and title for the widget (reload paths)', () => {
    const { ff } = createEnv([]);
    const items = ff.getTransferItems([{ Value: `v"1`, Text: `<b>"x"</b>`, Disabled: true }]);
    assert.deepStrictEqual(plain(items), [{ value: `v"1`, title: `<b>"x"</b>`, disabled: true }]);
    const [item] = ff.encodeTransferItems(items);
    assert.strictEqual(item.value, 'v&quot;1');
    assert.ok(!/[<>"']/.test(decode(item.title)));
    assert.strictEqual(decode(decode(item.title)), `<b>"x"</b>`);
    assert.strictEqual(item.disabled, true);
});

test('encodeTransferItems: ordinary items are unchanged, other members are kept, the input is not modified', () => {
    const { ff } = createEnv([]);
    const input = [{ value: 'p1', title: 'First item', disabled: false, checked: true }, { value: `a'b`, title: `<i>`, disabled: false, checked: false }];
    const copy = JSON.parse(JSON.stringify(input));
    const out = ff.encodeTransferItems(input);
    assert.deepStrictEqual(input, copy);
    assert.deepStrictEqual(plain(out[0]), input[0]);
    assert.strictEqual(out[1].value, 'a&#39;b');
    assert.strictEqual(out[1].checked, false);
    assert.ok(!/[<>"']/.test(decode(out[1].title)));
});

// objects made inside the sandbox have another prototype; compare plain copies
function plain(x) { return JSON.parse(JSON.stringify(x)); }

function gridEnv(cell) {
    const e = createEnv([]);
    e.sandbox.layui.table = { cache: { g: [{ Name: cell }] } };
    return e;
}

test('gridcellchange (text cell): the new value is encoded, so text with quotes survives; ordinary text is written as before', () => {
    const e = gridEnv(`<input type='text' name='R[0].Name' value='old' />`);
    e.ff.gridcellchange({ value: 'plain & simple' }, 'g', 0, 'Name', 0);
    assert.strictEqual(e.sandbox.layui.table.cache.g[0].Name, `<input type='text' name='R[0].Name' value='plain &amp; simple' />`);
    e.ff.gridcellchange({ value: `it's "x" <b> $& $1` }, 'g', 0, 'Name', 0);
    const cell = e.sandbox.layui.table.cache.g[0].Name;
    assert.strictEqual(cell, `<input type='text' name='R[0].Name' value='it&#39;s &quot;x&quot; &lt;b&gt; $&amp; $1' />`);
    // the cell still holds exactly one input element with one single quoted value
    assert.ok(/^<input type='text' name='R\[0\]\.Name' value='[^']*' \/>$/.test(cell));
});

test('gridcellchange (select cell): the option of the value is selected, also when the value needs encoding', () => {
    const e = gridEnv(`<select name='R[0].Kind'><option value='a'>A</option><option value='x&#39;y&amp;z' selected>X</option></select>`);
    e.ff.gridcellchange({ value: 'a' }, 'g', 0, 'Name', 1);
    assert.strictEqual(e.sandbox.layui.table.cache.g[0].Name, `<select name='R[0].Kind'><option value='a' selected>A</option><option value='x&#39;y&amp;z'>X</option></select>`);
    e.ff.gridcellchange({ value: `x'y&z` }, 'g', 0, 'Name', 1);
    assert.strictEqual(e.sandbox.layui.table.cache.g[0].Name, `<select name='R[0].Kind'><option value='a'>A</option><option value='x&#39;y&amp;z' selected>X</option></select>`);
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
