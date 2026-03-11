const fs = require('fs');
const path = require('path');
const vm = require('vm');

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );
    const mockGridStack = {
        save: jest.fn(),
        load: jest.fn(),
        removeAll: jest.fn(),
        enable: jest.fn(),
        disable: jest.fn(),
        addWidget: jest.fn()
    };
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        GridStack: {
            init: jest.fn(() => mockGridStack)
        },
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockGridStack,
        GridStackInit: freshCtx.GridStack.init
    };
}

describe('WtmDashboard.GridManager', () => {
    test('saveLayout returns gridstack format', () => {
        const { wd, mockGridStack, GridStackInit } = makeEnv();
        
        wd.GridManager.init('.grid-stack', {});
        
        expect(GridStackInit).toHaveBeenCalled();
        
        mockGridStack.save.mockReturnValue([{ x: 0, y: 0, w: 2, h: 2, id: 'w1' }]);
        
        const layout = wd.GridManager.saveLayout();
        expect(layout).toEqual([{ x: 0, y: 0, w: 2, h: 2, id: 'w1' }]);
    });

    test('isEditMode defaults to false and can be set', () => {
        const { wd, mockGridStack } = makeEnv();
        
        wd.GridManager.init('.grid-stack', {});
        
        expect(wd.GridManager.isEditMode()).toBe(false);
        
        wd.GridManager.setEditMode(true);
        expect(wd.GridManager.isEditMode()).toBe(true);
        expect(mockGridStack.enable).toHaveBeenCalled();
        
        wd.GridManager.setEditMode(false);
        expect(wd.GridManager.isEditMode()).toBe(false);
        expect(mockGridStack.disable).toHaveBeenCalled();
    });
});