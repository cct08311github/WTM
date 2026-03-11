const fs = require('fs');
const path = require('path');
const vm = require('vm');

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );
    const mockDocument = {
        createElement: jest.fn((tag) => ({
            tag,
            className: '',
            textContent: '',
            value: '',
            type: '',
            name: '',
            style: {},
            children: [],
            appendChild: jest.fn(function (c) { this.children.push(c); return c; }),
            addEventListener: jest.fn()
        }))
    };
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: mockDocument,
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockDocument,
    };
}

describe('WtmDashboard.FilterBar', () => {
    test('init renders controls from filter config', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const filters = [
            { field: 'Region', type: 'select', options: ['North', 'South'] },
            { field: 'Year', type: 'text' }
        ];

        wd.FilterBar.init(filters, container);

        // Should have created elements for each filter
        expect(container.children.length).toBeGreaterThanOrEqual(2);
    });

    test('getValues returns current filter values', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const filters = [
            { field: 'Region', type: 'text', defaultValue: 'North' },
            { field: 'Year', type: 'text', defaultValue: '2025' }
        ];

        wd.FilterBar.init(filters, container);

        const values = wd.FilterBar.getValues();
        expect(values.Region).toBe('North');
        expect(values.Year).toBe('2025');
    });

    test('onChange registers callback that receives values', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const filters = [
            { field: 'Region', type: 'text', defaultValue: 'North' }
        ];

        wd.FilterBar.init(filters, container);

        const callback = jest.fn();
        wd.FilterBar.onChange(callback);

        // Simulate value change
        wd.FilterBar.setValue('Region', 'South');

        expect(callback).toHaveBeenCalledTimes(1);
        expect(callback).toHaveBeenCalledWith({ Region: 'South' });
    });
});
