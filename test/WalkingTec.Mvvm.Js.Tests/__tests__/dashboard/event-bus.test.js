const fs = require('fs');
const path = require('path');
const vm = require('vm');

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard
    };
}

describe('WtmDashboard.EventBus', () => {
    test('on and emit delivers to handler', () => {
        const { wd } = makeEnv();
        const handler = jest.fn();
        
        wd.EventBus.on('test-event', 'widget1', handler);
        wd.EventBus.emit('test-event', { payload: 123 });
        
        expect(handler).toHaveBeenCalledTimes(1);
        expect(handler).toHaveBeenCalledWith({ payload: 123 });
    });

    test('off removes handler', () => {
        const { wd } = makeEnv();
        const handler = jest.fn();
        
        wd.EventBus.on('test-event', 'widget1', handler);
        wd.EventBus.off('test-event', 'widget1');
        wd.EventBus.emit('test-event', { payload: 123 });
        
        expect(handler).not.toHaveBeenCalled();
    });

    test('emit to unregistered event does not throw', () => {
        const { wd } = makeEnv();
        expect(() => {
            wd.EventBus.emit('unknown-event', { a: 1 });
        }).not.toThrow();
    });

    test('multiple handlers on same event', () => {
        const { wd } = makeEnv();
        const handler1 = jest.fn();
        const handler2 = jest.fn();
        
        wd.EventBus.on('test-event', 'widget1', handler1);
        wd.EventBus.on('test-event', 'widget2', handler2);
        
        wd.EventBus.emit('test-event', 'data');
        
        expect(handler1).toHaveBeenCalledWith('data');
        expect(handler2).toHaveBeenCalledWith('data');
        
        wd.EventBus.off('test-event', 'widget1');
        wd.EventBus.emit('test-event', 'data2');
        
        expect(handler1).toHaveBeenCalledTimes(1);
        expect(handler2).toHaveBeenCalledTimes(2);
        expect(handler2).toHaveBeenCalledWith('data2');
    });
});