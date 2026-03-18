/**
 * Regression tests for wtmHeaderFilter._applyFilters
 * #511: clearing all filter inputs must restore previously-hidden rows.
 *
 * Each test runs in a fresh VM context so module-level _filters closure resets.
 */

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

/**
 * Build a test environment with a minimal jQuery mock and isolated VM context.
 *
 * rows: [{ fields: { fieldName: 'text' }, visible: true }, ...]
 *
 * Supports the exact jQuery call chains used by _applyFilters:
 *   $view.find('.layui-table-main tbody').find('tr').each(fn)
 *   $row.find('td[data-field="X"]').find('.layui-table-cell').text()
 *   $row.toggle(show)
 *   $view.find('.layui-table-main tbody tr').show()          ← #511 fix path
 *   $view.find('.layui-table-fixed ...').show() / .each(fn)
 */
function makeEnv(rows) {
    function makeRowEl(row) {
        return {
            _row: row,
            find: function (sel) {
                var m = sel.match(/data-field="([^"]+)"/);
                if (m) {
                    var fieldVal = row.fields[m[1]] !== undefined ? String(row.fields[m[1]]) : '';
                    return {
                        find: function (inner) {
                            return inner === '.layui-table-cell'
                                ? { text: function () { return fieldVal; }, length: 1 }
                                : { text: function () { return ''; }, length: 0 };
                        },
                        text: function () { return fieldVal; },
                        length: 1,
                    };
                }
                return { find: function () { return { text: function () { return ''; } }; }, length: 0 };
            },
            toggle: function (show) { row.visible = show; },
        };
    }

    var rowEls = rows.map(makeRowEl);

    function makeCollection(els) {
        return {
            find: function () { return makeCollection(els); },
            each: function (fn) { els.forEach(function (el, i) { fn.call(el, i, el); }); return makeCollection(els); },
            toggle: function (show) { els.forEach(function (el) { el._row.visible = show; }); return makeCollection(els); },
            show: function () { els.forEach(function (el) { el._row.visible = true; }); return makeCollection(els); },
            length: els.length,
        };
    }

    // $view.find() dispatch table
    var $viewFindMap = {
        '.layui-table-main tbody': {
            find: function (sel) {
                if (sel !== 'tr') return makeCollection([]);
                return {
                    each: function (fn) { rowEls.forEach(function (el, i) { fn.call(el, i, el); }); },
                    show: function () { rows.forEach(function (r) { r.visible = true; }); },
                    length: rowEls.length,
                };
            },
            show: function () { rows.forEach(function (r) { r.visible = true; }); },
        },
        '.layui-table-main tbody tr': {
            show: function () { rows.forEach(function (r) { r.visible = true; }); },
            each: function (fn) { rowEls.forEach(function (el, i) { fn.call(el, i, el); }); },
            length: rowEls.length,
        },
        '.layui-table-fixed .layui-table-body tbody tr': makeCollection(rowEls),
        '.layui-table-fixed-r .layui-table-body tbody tr': makeCollection(rowEls),
        // _injectRow / _bindEvents no-ops
        '.wtm-hf-row': { remove: function () {} },
        '.layui-table-box > .layui-table-header': {
            length: 0,
            find: function () { return { length: 0, find: function () { return { length: 0, each: function () {}, append: function () {} }; } }; },
        },
    };

    var $view = {
        find: function (sel) { return $viewFindMap[sel] || makeCollection([]); },
        off: function () { return $view; },
        on: function () { return $view; },
        length: 1,
    };

    function jqMock(arg) {
        // $(this) inside .each() — wrap the row element
        if (arg && typeof arg === 'object' && arg._row) {
            return { find: arg.find.bind(arg), toggle: arg.toggle.bind(arg), val: function () { return ''; }, length: 1 };
        }
        // '#gridId + .layui-table-view' → return $view
        if (typeof arg === 'string' && arg.indexOf('.layui-table-view') >= 0) return $view;
        // other selectors
        if (typeof arg === 'string') return $view.find(arg);
        return { cookie: function () {} };
    }
    jqMock.cookie = function () {};
    jqMock.ajax = function () {};

    var ctx = vm.createContext({
        window: {},
        global: {},
        $: jqMock,
        console: console,
        setTimeout: global.setTimeout.bind(global),
        clearTimeout: global.clearTimeout.bind(global),
        DONOTUSE_TABLAYID: undefined,
        DONOTUSE_COOKIEPRE: '',
        DONOTUSE_WINDOWGUID: '',
        layui: {
            use: function () {},
            table: { reload: function () {} },
            layer: { msg: function () {}, alert: function () {} },
            form: { render: function () {} },
            element: { tabChange: function () {} },
        },
    });
    ctx.window = ctx;
    new vm.Script(SRC).runInContext(ctx);

    return { hf: ctx.wtmHeaderFilter, $view: $view, rows: rows };
}

// ─── Tests ───────────────────────────────────────────────────────────────────

describe('wtmHeaderFilter._applyFilters via refresh', () => {

    test('rows stay visible when no filters active', () => {
        var rows = [
            { fields: { Name: 'Alice' }, visible: true },
            { fields: { Name: 'Bob' },   visible: true },
        ];
        var env = makeEnv(rows);
        env.hf.refresh('g1');
        expect(rows[0].visible).toBe(true);
        expect(rows[1].visible).toBe(true);
    });

    test('regression #511: clearing filters after filter-applied state restores all rows', () => {
        // Simulate rows left hidden from a previous filter pass.
        // refresh() with empty _filters must restore them (the #511 fix).
        var rows = [
            { fields: { Name: 'Alice' }, visible: false },
            { fields: { Name: 'Bob' },   visible: false },
        ];
        var env = makeEnv(rows);
        env.hf.refresh('g1');
        expect(rows[0].visible).toBe(true);
        expect(rows[1].visible).toBe(true);
    });

});
