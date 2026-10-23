// Node-only test (no browser, no network) for the values framework_layui.js appends to the address of a grid action (#344).
// Usage: node grid-action-encoding.test.js <path to framework_layui.js>
'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const source = fs.readFileSync(process.argv[2], 'utf8');

// text with the characters that cut or change an address, quotes, a backslash and a bracket
const SPECIAL = `a&b=c#d+e%f g'h"i\\j]k<l>`;

function createEnv() {
    const sandbox = { console: { log() {}, error() {}, warn() {} } };
    sandbox.window = sandbox;
    sandbox.document = { body: { clientWidth: 1000 } };
    sandbox.location = { hash: '', href: '' };
    sandbox.alert = () => {};
    sandbox.layui = { layer: { alert: () => {} }, form: { render: () => {} }, use: () => {} };
    function $() { return { length: 0 }; }
    $.ajax = () => {};
    $.cookie = () => '';
    $.extend = (target, ...sources) => Object.assign(target, ...sources);
    sandbox.$ = $;
    sandbox.jQuery = $;
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox, { filename: 'framework_layui.js' });
    return sandbox.ff;
}

let failed = 0;
const tests = [];
function test(name, fn) { tests.push([name, fn]); }

// what the server reads out of an address part: decoded once
function serverValue(url, name) {
    const q = url.substring(url.indexOf('?') + 1);
    for (const part of q.split('&')) {
        const i = part.indexOf('=');
        if (decodeURIComponent(part.substring(0, i)) === name) return decodeURIComponent(part.substring(i + 1));
    }
    return undefined;
}

test('concatWhereStr: ordinary values give the address they always gave', () => {
    const ff = createEnv();
    assert.strictEqual(ff.concatWhereStr('/x?1=1', ['a', 'b'], { a: 1, b: 'two', c: 3 }), '/x?1=1&a=1&b=two');
    assert.strictEqual(ff.concatWhereStr('/x?1=1', ['id', 'g'], { id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301', g: 42 }),
        '/x?1=1&id=3f2504e0-4f89-11d3-9a0c-0305e82c3301&g=42');
    assert.strictEqual(ff.concatWhereStr('/x?1=1', ['a'], null), '/x?1=1');
    assert.strictEqual(ff.concatWhereStr('/x?1=1', null, { a: 1 }), '/x?1=1');
    assert.strictEqual(ff.concatWhereStr('/x?1=1', [], { a: 1 }), '/x?1=1');
    assert.strictEqual(ff.concatWhereStr(null, null, null), '');
    // missing and null values keep the text they had
    assert.strictEqual(ff.concatWhereStr('/x?1=1', ['a', 'b'], { b: null }), '/x?1=1&a=undefined&b=null');
});

test('concatWhereStr: the server reads a value with address characters whole', () => {
    const ff = createEnv();
    const url = ff.concatWhereStr('/x?1=1', ['Code', 'Other'], { Code: SPECIAL, Other: 'o' });
    assert.strictEqual(serverValue(url, 'Code'), SPECIAL);
    assert.strictEqual(serverValue(url, 'Other'), 'o');
    assert.strictEqual(url, '/x?1=1&Code=' + encodeURIComponent(SPECIAL) + '&Other=o');
    // the characters that cut or change an address are all encoded (the apostrophe is allowed in an address)
    assert.ok(!/[# "<>\\\]]/.test(url.substring('/x?1=1'.length)), 'unencoded character in the added part: ' + url);
    assert.strictEqual(url.substring('/x?1=1'.length).split('&').length, 3, 'a value with & must not add parameters: ' + url);
});

test('concatWhereStr: the names of the fields are written as they are', () => {
    const ff = createEnv();
    assert.strictEqual(ff.concatWhereStr('/x?1=1', ['Entity.Name'], { 'Entity.Name': 'n' }), '/x?1=1&Entity.Name=n');
});

test('concatWhereStr: with the page flag the value is encoded twice (ff.LoadPage decodes the address once)', () => {
    const ff = createEnv();
    const url = ff.concatWhereStr('/x?1=1', ['Code'], { Code: SPECIAL }, true);
    assert.strictEqual(url, '/x?1=1&Code=' + encodeURIComponent(encodeURIComponent(SPECIAL)));
    // after the decode of LoadPage the address holds the value once encoded: the server reads it whole
    assert.strictEqual(serverValue(decodeURIComponent(url), 'Code'), SPECIAL);
    // ordinary values do not change
    assert.strictEqual(ff.concatWhereStr('/x?1=1', ['a', 'b'], { a: 1, b: 'two' }, true), '/x?1=1&a=1&b=two');
});

for (const [name, fn] of tests) {
    try { fn(); console.log('ok   - ' + name); }
    catch (e) { failed++; console.log('FAIL - ' + name + '\n' + (e && e.stack || e)); }
}
console.log(`${tests.length - failed}/${tests.length} passed`);
process.exit(failed ? 1 : 0);
