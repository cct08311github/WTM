/**
 * Tests for WtmDashboard.EventBus
 */
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const src = fs.readFileSync(
    path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
    'utf8'
);

function makeEnv() {
    var ctx = vm.createContext({
        console: console,
        GridStack: undefined
    });
    new vm.Script(src).runInContext(ctx);
    return ctx.WtmDashboard;
}

describe('EventBus', function () {
    test('on and emit delivers to handler', function () {
        var wd = makeEnv();
        var received = null;
        wd.EventBus.on('click:w1', function (data) { received = data; });
        wd.EventBus.emit('click:w1', { value: 42 });
        expect(received).toEqual({ value: 42 });
    });

    test('off removes handler', function () {
        var wd = makeEnv();
        var count = 0;
        var handler = function () { count++; };
        wd.EventBus.on('test', handler);
        wd.EventBus.emit('test');
        expect(count).toBe(1);
        wd.EventBus.off('test', handler);
        wd.EventBus.emit('test');
        expect(count).toBe(1);
    });

    test('emit to unregistered event does not throw', function () {
        var wd = makeEnv();
        expect(function () {
            wd.EventBus.emit('nonexistent', { x: 1 });
        }).not.toThrow();
    });

    test('multiple handlers on same event', function () {
        var wd = makeEnv();
        var results = [];
        wd.EventBus.on('multi', function () { results.push('a'); });
        wd.EventBus.on('multi', function () { results.push('b'); });
        wd.EventBus.emit('multi');
        expect(results).toEqual(['a', 'b']);
    });
});
