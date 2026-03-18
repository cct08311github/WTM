/**
 * Tests for the mobile-table CSS injection added to framework_layui.js
 * for issue #640 (layui-table-box overflow-x truncation on ≤768px viewports).
 *
 * framework_layui.js injects a <style id="wtm-mobile-table-fix"> into
 * document.head when it loads.  These tests verify:
 *   - the element is injected exactly once
 *   - it contains the correct media query and CSS rule
 *   - the injection is safely skipped when document is unavailable (Node/VM)
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

// ─── Helper: run framework_layui.js with a given document ────────────────────

function makeCtxWithDoc(doc) {
    var ctx = vm.createContext({
        window:              {},
        document:            doc,
        $: Object.assign(function () { return {}; }, { cookie: function () {}, ajax: function () {} }),
        console:             console,
        setTimeout:          global.setTimeout.bind(global),
        clearTimeout:        global.clearTimeout.bind(global),
        DONOTUSE_TABLAYID:   undefined,
        DONOTUSE_COOKIEPRE:  '',
        DONOTUSE_WINDOWGUID: '',
        layui: {
            use:     function () {},
            table:   { reload: function () {} },
            layer:   { msg: function () {}, alert: function () {} },
            form:    { render: function () {} },
            element: { tabChange: function () {} },
        },
    });
    ctx.window = ctx;
    new vm.Script(SRC).runInContext(ctx);
    return ctx;
}

// ─── Tests ───────────────────────────────────────────────────────────────────

describe('mobile table fix — CSS injection (issue #640)', () => {

    test('injects a <style> with id wtm-mobile-table-fix into document.head', () => {
        // Use a fresh isolated document so this test does not bleed into others.
        var doc = document.implementation.createHTMLDocument('test');
        makeCtxWithDoc(doc);

        var el = doc.getElementById('wtm-mobile-table-fix');
        expect(el).not.toBeNull();
        expect(el.tagName.toLowerCase()).toBe('style');
    });

    test('injected style contains the 768px media query', () => {
        var doc = document.implementation.createHTMLDocument('test');
        makeCtxWithDoc(doc);

        var content = doc.getElementById('wtm-mobile-table-fix').textContent;
        expect(content).toContain('@media (max-width:768px)');
    });

    test('injected style sets overflow-x:auto with !important on .layui-table-box', () => {
        var doc = document.implementation.createHTMLDocument('test');
        makeCtxWithDoc(doc);

        var content = doc.getElementById('wtm-mobile-table-fix').textContent;
        expect(content).toContain('.layui-table-box');
        expect(content).toContain('overflow-x:auto!important');
    });

    test('injected style includes -webkit-overflow-scrolling:touch for iOS', () => {
        var doc = document.implementation.createHTMLDocument('test');
        makeCtxWithDoc(doc);

        var content = doc.getElementById('wtm-mobile-table-fix').textContent;
        expect(content).toContain('-webkit-overflow-scrolling:touch');
    });

    test('injects exactly one style element (idempotency guard via id)', () => {
        var doc = document.implementation.createHTMLDocument('test');
        makeCtxWithDoc(doc);

        var elements = doc.querySelectorAll('#wtm-mobile-table-fix');
        expect(elements.length).toBe(1);
    });

    test('does not throw when document is absent (Node / VM environment)', () => {
        // Simulate running in a Node VM that has no document global at all.
        var ctx = vm.createContext({
            window:              {},
            // no document property
            $: Object.assign(function () { return {}; }, { cookie: function () {}, ajax: function () {} }),
            console:             console,
            setTimeout:          global.setTimeout.bind(global),
            clearTimeout:        global.clearTimeout.bind(global),
            DONOTUSE_TABLAYID:   undefined,
            DONOTUSE_COOKIEPRE:  '',
            DONOTUSE_WINDOWGUID: '',
            layui: {
                use:     function () {},
                table:   { reload: function () {} },
                layer:   { msg: function () {}, alert: function () {} },
                form:    { render: function () {} },
                element: { tabChange: function () {} },
            },
        });
        ctx.window = ctx;

        expect(() => new vm.Script(SRC).runInContext(ctx)).not.toThrow();
    });

});
