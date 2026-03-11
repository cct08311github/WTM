/**
 * Tests for WtmDashboard.GridManager
 */
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const src = fs.readFileSync(
    path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
    'utf8'
);

function makeGridStub() {
    var _items = [];
    return {
        save: function () { return _items.slice(); },
        load: function (items) { _items = items; },
        removeAll: function () { _items = []; },
        enableMove: jest.fn(),
        enableResize: jest.fn(),
        addWidget: function (opts) { _items.push(opts); return opts; }
    };
}

function makeEnv() {
    var stub = makeGridStub();
    var ctx = vm.createContext({
        console: console,
        GridStack: {
            init: function () { return stub; }
        }
    });
    new vm.Script(src).runInContext(ctx);
    return { wd: ctx.WtmDashboard, stub: stub };
}

describe('GridManager', function () {
    test('saveLayout returns gridstack format', function () {
        var env = makeEnv();
        env.wd.GridManager.init('.grid-stack');
        env.stub.load([{ id: 'w1', x: 0, y: 0, w: 3, h: 1 }]);
        var layout = env.wd.GridManager.saveLayout();
        expect(layout).toEqual([{ id: 'w1', x: 0, y: 0, w: 3, h: 1 }]);
    });

    test('isEditMode defaults to false', function () {
        var env = makeEnv();
        env.wd.GridManager.init('.grid-stack');
        expect(env.wd.GridManager.isEditMode()).toBe(false);
    });
});
