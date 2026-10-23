// Node-only test (no browser, no network) for the values framework_layui.js writes into markup and selectors when a combo box icon is
// shown and when a tree container node is clicked or preselected (#326).
// Usage: node linkage-encoding.test.js <path to framework_layui.js>
'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');

const source = fs.readFileSync(process.argv[2], 'utf8');

// text with the five characters that need encoding, a backslash and a selector delimiter
const SPECIAL = `a&b<c>"d'e\\f]g`;

// a tiny jQuery stand-in: every markup string handed to $('<...>') is recorded; plain objects become wrappers with attr()
function createEnv() {
    const created = [];
    const sandbox = { console: { log() {}, error() {}, warn() {} } };
    sandbox.window = sandbox;
    sandbox.document = { body: { clientWidth: 1000 } };
    sandbox.location = { hash: '', href: '' };
    sandbox.alert = () => {};
    sandbox.layui = { layer: { alert: () => {} }, form: { render: () => {} }, use: () => {} };
    function $(arg) {
        if (typeof arg === 'string' && arg.charAt(0) === '<') {
            created.push(arg);
            return { markup: arg };
        }
        if (arg && typeof arg === 'object') {
            return { attr: (n) => (arg.attrs ? arg.attrs[n] : undefined), css() { return this; }, removeAttr() {}, length: 1 };
        }
        return { length: 0 };
    }
    $.ajax = () => {};
    $.cookie = () => '';
    $.extend = (target, ...sources) => Object.assign(target, ...sources);
    sandbox.$ = $;
    sandbox.jQuery = $;
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox, { filename: 'framework_layui.js' });
    return { sandbox, ff: sandbox.ff, created };
}

// a combo box select event for the option that has the given icon attribute
function comboEvent(icon) {
    const prepended = [];
    // data.othis.children('.layui-select-title') -> comboTitle; comboTitle.children('._wtm-combo-icon') -> none; comboTitle.children('input') -> input
    const comboTitle = {
        children: (sel) => (sel === '._wtm-combo-icon' ? { length: 0, remove() {} } : {}),
        prepend: (x) => prepended.push(x),
    };
    const option = { value: 'v1', attrs: { icon } };
    return { event: { elem: [option], value: 'v1', othis: { children: () => comboTitle } }, prepended };
}

let failed = 0;
const tests = [];
function test(name, fn) { tests.push([name, fn]); }

test('changeComboIcon: an ordinary icon class gives the markup it always gave', () => {
    const e = createEnv();
    const c = comboEvent('layui-icon-user');
    e.ff.changeComboIcon(c.event);
    assert.deepStrictEqual(e.created, ['<i class="_wtm-combo-icon layui-icon-user"></i>']);
    assert.strictEqual(c.prepended.length, 1);
});

test('changeComboIcon: an option without an icon adds no icon element', () => {
    const e = createEnv();
    const c = comboEvent(undefined);
    e.ff.changeComboIcon(c.event);
    assert.deepStrictEqual(e.created, []);
});

test('changeComboIcon: the icon value stays inside the class attribute', () => {
    const e = createEnv();
    const c = comboEvent(`x" data-a="1' ${SPECIAL}`);
    e.ff.changeComboIcon(c.event);
    assert.strictEqual(e.created.length, 1);
    const m = /^<i class="([^"]*)"><\/i>$/.exec(e.created[0]);
    assert.ok(m, 'not a single <i> element with one class attribute: ' + e.created[0]);
    assert.ok(!/[<>']/.test(m[1]), 'unencoded character in the attribute value: ' + m[1]);
    assert.strictEqual(m[1], '_wtm-combo-icon ' + e.ff.EncodeHtml(`x" data-a="1' ${SPECIAL}`));
});

// ---- tree container (layui tree) ---------------------------------------------------------------------------------------------------

test('treeNodeData: a click gets the original id of the node, read back from the element; the data of layui is not changed', () => {
    const { ff } = createEnv();
    const node = { id: ff.EncodeHtml(SPECIAL), level: 1, title: 'T' };
    const elem = { attr: (n) => (n === 'data-id' ? SPECIAL : undefined) };
    const data = { elem, state: 'open', data: node };
    const out = ff.treeNodeData(data);
    assert.strictEqual(out.data.id, SPECIAL);
    assert.strictEqual(out.data.level, 1);
    assert.strictEqual(out.data.title, 'T');
    assert.strictEqual(out.state, 'open');
    assert.strictEqual(out.elem, elem);
    // the node object of layui keeps the id it was given
    assert.strictEqual(node.id, ff.EncodeHtml(SPECIAL));
    assert.strictEqual(data.data, node);
});

test('treeNodeData: an ordinary id is the same id; data without an element is returned as it is', () => {
    const { ff } = createEnv();
    const withElem = ff.treeNodeData({ elem: { attr: () => 'n1' }, data: { id: 'n1' } });
    assert.strictEqual(withElem.data.id, 'n1');
    const noElem = { data: { id: 'n1' } };
    assert.strictEqual(ff.treeNodeData(noElem), noElem);
    const noAttr = { elem: { attr: () => undefined }, data: { id: 'n1' } };
    assert.strictEqual(ff.treeNodeData(noAttr), noAttr);
});

// nodes stand in for the elements layui writes: each has the data-id attribute as the browser reads it
function fakeTree(ids) {
    const nodes = ids.map((id) => ({ attrs: { 'data-id': id } }));
    const root = { find: (sel) => { assert.strictEqual(sel, '.layui-tree-set'); return { filter: (fn) => nodes.filter((n) => fn.call(n)) }; } };
    return { root, nodes };
}

test('findTreeNode: finds the node whose id was handed to layui, whatever characters the id has', () => {
    const { ff } = createEnv();
    const { root, nodes } = fakeTree(['n1', SPECIAL, `x'] , div[data-id='y`]);
    assert.deepStrictEqual(ff.findTreeNode(root, { data: { id: 'n1' } }), [nodes[0]]);
    assert.deepStrictEqual(ff.findTreeNode(root, { data: { id: ff.EncodeHtml(SPECIAL) } }), [nodes[1]]);
    assert.deepStrictEqual(ff.findTreeNode(root, { data: { id: ff.EncodeHtml(`x'] , div[data-id='y`) } }), [nodes[2]]);
    assert.deepStrictEqual(ff.findTreeNode(root, { data: { id: 'missing' } }), []);
});

for (const [name, fn] of tests) {
    try { fn(); console.log('ok   - ' + name); }
    catch (e) { failed++; console.log('FAIL - ' + name + '\n' + (e && e.stack || e)); }
}
console.log(`${tests.length - failed}/${tests.length} passed`);
process.exit(failed ? 1 : 0);
