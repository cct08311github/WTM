/**
 * Tests for wtmColVis — column visibility persistence (issue #639).
 *
 * Pure-function tests (_storageKey, _collectHidden) run against the global
 * wtmColVis loaded by setup.js.
 *
 * Integration tests (init + save) each get a fresh VM context so the
 * module-level _restored closure resets between tests.
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

// ─── VM context factory ────────────────────────────────────────────────────────

/**
 * Create an isolated VM context for wtmColVis integration tests.
 *
 * @param {object} opts
 * @param {object} opts.storage       Initial localStorage contents ({ key: jsonString })
 * @param {object} opts.tableOptions  window[tableId + 'option'] values ({ tableId: option })
 * @param {object} opts.domCalls      Spy accumulator for DOM calls
 */
function makeEnv({ storage = {}, tableOptions = {}, domCalls = {} } = {}) {
    const store = Object.assign({}, storage);

    const localStorageMock = {
        getItem:    (k)    => (store[k] !== undefined ? store[k] : null),
        setItem:    (k, v) => { store[k] = v; },
        removeItem: (k)    => { delete store[k]; },
    };

    // Minimal jQuery mock that supports:
    //   $('#tableId + .layui-table-view').find('[data-key="..."]').addClass('layui-hide')
    const addClassCalls = [];
    function jqMock(sel) {
        return {
            find: function (innerSel) {
                return {
                    addClass: function (cls) {
                        addClassCalls.push({ sel, innerSel, cls });
                    },
                };
            },
            attr:  function () { return ''; },
            attr:  function () { return undefined; },
        };
    }
    jqMock.ajax   = function () {};
    jqMock.cookie = function () {};
    jqMock.fn     = {};

    const formOnCalls = [];
    const layuiMock = {
        use: function (mods, cb) { if (cb) cb(); },
        form: {
            render: function () {},
            on: function (evt, handler) {
                formOnCalls.push({ evt, handler });
            },
        },
        table: {
            reload:  function () {},
            resize:  function () {},
        },
        layer: { msg: function () {}, alert: function () {} },
        element: { tabChange: function () {} },
        each: function (obj, fn) {
            if (Array.isArray(obj)) {
                obj.forEach(function (item, i) { fn(i, item); });
            } else {
                Object.keys(obj || {}).forEach(function (k) { fn(k, obj[k]); });
            }
        },
    };

    const ctx = vm.createContext({
        window: {},
        $: jqMock,
        console,
        localStorage: localStorageMock,
        layui: layuiMock,
        setTimeout: global.setTimeout.bind(global),
        clearTimeout: global.clearTimeout.bind(global),
        DONOTUSE_TABLAYID: undefined,
        DONOTUSE_COOKIEPRE: '',
        DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;

    // Inject table options into the VM context window
    Object.keys(tableOptions).forEach(function (tableId) {
        ctx[tableId + 'option'] = tableOptions[tableId];
    });

    new vm.Script(SRC).runInContext(ctx);

    return {
        cv:            ctx.wtmColVis,
        store,
        addClassCalls,
        formOnCalls,
        ctx,
    };
}

// ─── Pure function tests (use global wtmColVis from setup.js) ─────────────────

describe('wtmColVis._storageKey', () => {
    test('returns prefixed key for table id', () => {
        expect(wtmColVis._storageKey('myGrid')).toBe('wtm_col_vis_myGrid');
    });

    test('handles table id with underscores', () => {
        expect(wtmColVis._storageKey('my_grid_123')).toBe('wtm_col_vis_my_grid_123');
    });
});

describe('wtmColVis._collectHidden', () => {
    test('returns empty array when no cols are hidden', () => {
        const cols = [[
            { field: 'Name',   hide: false },
            { field: 'Amount', hide: false },
        ]];
        expect(wtmColVis._collectHidden(cols)).toEqual([]);
    });

    test('returns field names for hidden cols', () => {
        const cols = [[
            { field: 'Name',   hide: false },
            { field: 'Region', hide: true  },
            { field: 'Amount', hide: false },
        ]];
        expect(wtmColVis._collectHidden(cols)).toEqual(['Region']);
    });

    test('collects multiple hidden fields', () => {
        const cols = [[
            { field: 'A', hide: true  },
            { field: 'B', hide: false },
            { field: 'C', hide: true  },
        ]];
        expect(wtmColVis._collectHidden(cols)).toEqual(['A', 'C']);
    });

    test('handles multi-row cols (layered headers)', () => {
        const cols = [
            [{ field: 'Name',   hide: true  }],
            [{ field: 'Amount', hide: false }, { field: 'Region', hide: true }],
        ];
        expect(wtmColVis._collectHidden(cols)).toEqual(['Name', 'Region']);
    });

    test('skips cols without a field (e.g. checkbox, toolbar cols)', () => {
        const cols = [[
            { type: 'checkbox', hide: true },   // no field
            { field: 'Amount',  hide: true },
        ]];
        expect(wtmColVis._collectHidden(cols)).toEqual(['Amount']);
    });

    test('returns empty array for null input', () => {
        expect(wtmColVis._collectHidden(null)).toEqual([]);
    });

    test('returns empty array for empty cols array', () => {
        expect(wtmColVis._collectHidden([])).toEqual([]);
    });
});

// ─── Integration tests (isolated VM contexts) ─────────────────────────────────

describe('wtmColVis.init — no saved state', () => {
    test('does nothing when localStorage has no entry', () => {
        const cols = [[{ field: 'Name', hide: false }]];
        const env = makeEnv({ tableOptions: { myGrid: { index: 0, cols } } });
        env.cv.init('myGrid');
        // col.hide should stay false
        expect(cols[0][0].hide).toBe(false);
        expect(env.addClassCalls).toHaveLength(0);
    });

    test('does nothing when saved list is empty', () => {
        const cols = [[{ field: 'Name', hide: false }]];
        const env = makeEnv({
            storage: { 'wtm_col_vis_myGrid': '[]' },
            tableOptions: { myGrid: { index: 0, cols } },
        });
        env.cv.init('myGrid');
        expect(cols[0][0].hide).toBe(false);
    });
});

describe('wtmColVis.init — restores saved state', () => {
    test('sets col.hide true for saved field', () => {
        const cols = [[
            { field: 'Name',   hide: false },
            { field: 'Region', hide: false },
        ]];
        const env = makeEnv({
            storage: { 'wtm_col_vis_myGrid': '["Region"]' },
            tableOptions: { myGrid: { index: 0, cols } },
        });
        env.cv.init('myGrid');
        expect(cols[0][0].hide).toBe(false);   // Name unchanged
        expect(cols[0][1].hide).toBe(true);    // Region hidden
    });

    test('sets col.hide for multiple saved fields', () => {
        const cols = [[
            { field: 'A', hide: false },
            { field: 'B', hide: false },
            { field: 'C', hide: false },
        ]];
        const env = makeEnv({
            storage: { 'wtm_col_vis_tbl': '["A","C"]' },
            tableOptions: { tbl: { index: 2, cols } },
        });
        env.cv.init('tbl');
        expect(cols[0][0].hide).toBe(true);
        expect(cols[0][1].hide).toBe(false);
        expect(cols[0][2].hide).toBe(true);
    });

    test('calls addClass(layui-hide) for hidden cols', () => {
        const cols = [[{ field: 'Region', hide: false }]];
        const env = makeEnv({
            storage: { 'wtm_col_vis_g1': '["Region"]' },
            tableOptions: { g1: { index: 1, cols } },
        });
        env.cv.init('g1');
        expect(env.addClassCalls.length).toBeGreaterThan(0);
        expect(env.addClassCalls[0].cls).toBe('layui-hide');
        // data-key uses table index
        expect(env.addClassCalls[0].innerSel).toContain('1-0-0');
    });

    test('ignores saved field that no longer exists in cols', () => {
        const cols = [[{ field: 'Name', hide: false }]];
        const env = makeEnv({
            storage: { 'wtm_col_vis_g2': '["DeletedField"]' },
            tableOptions: { g2: { index: 0, cols } },
        });
        env.cv.init('g2');
        expect(cols[0][0].hide).toBe(false);
    });
});

describe('wtmColVis.init — idempotency guard', () => {
    test('restore runs only once even when init is called multiple times', () => {
        const cols = [[{ field: 'X', hide: false }]];
        const env = makeEnv({
            storage: { 'wtm_col_vis_g3': '["X"]' },
            tableOptions: { g3: { index: 0, cols } },
        });
        env.cv.init('g3');
        expect(cols[0][0].hide).toBe(true);

        // Simulate table reload — manually reset hide to false
        cols[0][0].hide = false;
        env.cv.init('g3');      // second call (done fires on every reload)
        // hide should NOT be set again by restore
        expect(cols[0][0].hide).toBe(false);
    });
});

describe('wtmColVis — storage key round-trip', () => {
    test('storage key matches between _storageKey helper and init', () => {
        const cols = [[{ field: 'Z', hide: false }]];
        const env = makeEnv({
            tableOptions: { rt: { index: 0, cols } },
        });
        // Manually write to the key that _storageKey would produce
        const key = env.cv._storageKey('rt');
        env.store[key] = JSON.stringify(['Z']);
        env.cv.init('rt');
        expect(cols[0][0].hide).toBe(true);
    });
});
