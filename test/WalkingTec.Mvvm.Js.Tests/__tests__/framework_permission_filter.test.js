/**
 * Tests for wtmPermFilter.filterTree — the pure permission-tree row-visibility
 * function added to framework_layui.js for issue #610.
 *
 * filterTree(rows, query) takes:
 *   rows  – [{ text: string, depth: number }]  (parallel to rendered table rows)
 *   query – case-insensitive substring to match (empty = show all)
 * and returns a boolean[] parallel to rows.
 *
 * Ancestor rows of any match are also shown so the tree hierarchy stays readable.
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

/** Minimal context sufficient to run framework_layui.js and expose wtmPermFilter. */
function makeCtx() {
    var ctx = {
        window: {},
        global: {},
        $: Object.assign(function () { return {}; }, { cookie: function () {}, ajax: function () {} }),
        console: console,
        setTimeout: global.setTimeout.bind(global),
        clearTimeout: global.clearTimeout.bind(global),
        DONOTUSE_TABLAYID: undefined,
        DONOTUSE_COOKIEPRE: '',
        DONOTUSE_WINDOWGUID: '',
        layui: {
            use:     function () {},
            table:   { reload: function () {} },
            layer:   { msg: function () {}, alert: function () {} },
            form:    { render: function () {} },
            element: { tabChange: function () {} },
        },
    };
    ctx.window = ctx;   // window self-reference used by the file
    vm.createContext(ctx);
    new vm.Script(SRC).runInContext(ctx);
    return ctx;
}

// ─── Helpers ─────────────────────────────────────────────────────────────────

/** Shorthand: build a row descriptor. */
function r(text, depth) { return { text: text, depth: depth }; }

/** Run filterTree from a fresh VM context. */
function ft(rows, query) {
    return makeCtx().wtmPermFilter.filterTree(rows, query);
}

// ─── Tests ───────────────────────────────────────────────────────────────────

describe('wtmPermFilter.filterTree', () => {

    test('wtmPermFilter is exposed on window', () => {
        var ctx = makeCtx();
        expect(typeof ctx.wtmPermFilter).toBe('object');
        expect(typeof ctx.wtmPermFilter.filterTree).toBe('function');
        expect(typeof ctx.wtmPermFilter.apply).toBe('function');
    });

    test('empty query shows all rows', () => {
        var rows = [r('System', 0), r('Users', 1), r('Roles', 1)];
        expect(ft(rows, '')).toEqual([true, true, true]);
    });

    test('null / undefined query shows all rows', () => {
        var rows = [r('System', 0), r('Users', 1)];
        expect(ft(rows, null)).toEqual([true, true]);
        expect(ft(rows, undefined)).toEqual([true, true]);
    });

    test('query with no matches hides all rows', () => {
        var rows = [r('System', 0), r('Users', 1), r('Roles', 1)];
        expect(ft(rows, 'zzz')).toEqual([false, false, false]);
    });

    test('single root-level match shows only that row', () => {
        var rows = [r('System', 0), r('Reports', 0), r('Users', 0)];
        expect(ft(rows, 'Reports')).toEqual([false, true, false]);
    });

    test('leaf match shows leaf and all ancestors', () => {
        // Tree:
        //   System (0)
        //     User Mgmt (1)
        //       Add User (2)   ← match
        //       Edit User (2)
        var rows = [r('System', 0), r('User Mgmt', 1), r('Add User', 2), r('Edit User', 2)];
        expect(ft(rows, 'Add User')).toEqual([true, true, true, false]);
    });

    test('depth-2 match traces ancestors through depth-1 then depth-0', () => {
        var rows = [
            r('Root A', 0),
            r('Root B', 0),
            r('Sub B1', 1),
            r('Leaf B1a', 2),  // ← match
        ];
        expect(ft(rows, 'Leaf B1a')).toEqual([false, true, true, true]);
    });

    test('multiple matches at different levels show all matched + ancestors', () => {
        // Tree:
        //   Admin (0)
        //     Roles (1)       ← match "role"
        //       Add Role (2)  ← match "role"
        //     Users (1)
        //   Reports (0)
        var rows = [
            r('Admin', 0),
            r('Roles', 1),
            r('Add Role', 2),
            r('Users', 1),
            r('Reports', 0),
        ];
        var result = ft(rows, 'role');
        // Admin (ancestor of Roles and Add Role), Roles, Add Role visible; Users and Reports hidden
        expect(result).toEqual([true, true, true, false, false]);
    });

    test('match in second subtree does not show ancestors from first subtree', () => {
        var rows = [
            r('Section A', 0),
            r('Page A1', 1),
            r('Section B', 0),
            r('Page B1', 1),  // ← match
        ];
        expect(ft(rows, 'B1')).toEqual([false, false, true, true]);
    });

    test('case-insensitive matching', () => {
        var rows = [r('System Settings', 0), r('User Management', 1)];
        expect(ft(rows, 'SYSTEM')).toEqual([true, false]);
        expect(ft(rows, 'system settings')).toEqual([true, false]);
        expect(ft(rows, 'MANAGEMENT')).toEqual([true, true]);
    });

    test('partial substring match', () => {
        var rows = [r('User Management', 0), r('Role Administration', 0)];
        expect(ft(rows, 'manage')).toEqual([true, false]);
        expect(ft(rows, 'admin')).toEqual([false, true]);
    });

    test('empty rows array returns empty result', () => {
        expect(ft([], 'anything')).toEqual([]);
    });

    test('single row match', () => {
        expect(ft([r('Only Row', 0)], 'Only')).toEqual([true]);
        expect(ft([r('Only Row', 0)], 'Missing')).toEqual([false]);
    });

    test('ancestor shown only once even if multiple descendants match', () => {
        var rows = [
            r('Parent', 0),
            r('Child A', 1),  // ← match
            r('Child B', 1),  // ← match
        ];
        // Parent should be visible (ancestor of both matches) — no duplication issue
        expect(ft(rows, 'Child')).toEqual([true, true, true]);
    });

    test('depth-0 rows are never hidden because of ancestor logic (no parent to show)', () => {
        var rows = [r('Root', 0)];
        expect(ft(rows, 'Root')).toEqual([true]);
    });

    test('whitespace-only query treated as empty (no trim needed by caller)', () => {
        // filterTree itself does NOT trim — the apply() wrapper trims before calling.
        // Verify that a space does not incorrectly match all rows.
        var rows = [r('System', 0), r('Users', 1)];
        // ' ' is not empty string so it does an indexOf check; 'system'.indexOf(' ') = -1
        expect(ft(rows, ' ')).toEqual([false, false]);
    });

});
