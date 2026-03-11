const fs = require('fs');
const path = require('path');
const vm = require('vm');

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );
    const mockDocument = {
        createElement: jest.fn((tag) => {
            return {
                tag,
                className: '',
                textContent: '',
                style: {},
                appendChild: jest.fn(function (c) { this.children = this.children || []; this.children.push(c); return c; })
            };
        })
    };
    
    const capturedOptions = [];
    const mockEcharts = {
        init: jest.fn(() => ({
            setOption: jest.fn(opt => capturedOptions.push(opt)),
            dispose: jest.fn()
        }))
    };

    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: mockDocument,
        echarts: mockEcharts,
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockDocument,
        mockEcharts,
        capturedOptions
    };
}

describe('WtmDashboard Pure Functions', () => {
    test('formatValue handles currency', () => {
        const { wd } = makeEnv();
        expect(wd.Utils.formatValue(1200000, 'currency', '$')).toBe('$1.2M');
        expect(wd.Utils.formatValue(500, 'currency', '¥')).toBe('¥500');
    });

    test('formatValue handles percent', () => {
        const { wd } = makeEnv();
        expect(wd.Utils.formatValue(0.784, 'percent')).toBe('78.4%');
        expect(wd.Utils.formatValue(1, 'percent')).toBe('100%');
    });

    test('calculateTrend computes percentage', () => {
        const { wd } = makeEnv();
        expect(wd.Utils.calculateTrend(1200000, 1070000)).toBeCloseTo(12.1495, 2);
        expect(wd.Utils.calculateTrend(100, 0)).toBe(0);
    });
});

describe('WtmDashboard.WidgetRendererFactory', () => {
    test('getRenderer returns function for kpi type', () => {
        const { wd } = makeEnv();
        const renderer = wd.WidgetRendererFactory.getRenderer('kpi');
        expect(typeof renderer).toBe('function');
    });

    test('getRenderer returns function for chart type', () => {
        const { wd } = makeEnv();
        const renderer = wd.WidgetRendererFactory.getRenderer('chart');
        expect(typeof renderer).toBe('function');
    });

    test('getRenderer returns null for unknown type', () => {
        const { wd } = makeEnv();
        const renderer = wd.WidgetRendererFactory.getRenderer('unknown');
        expect(renderer).toBeNull();
    });

    test('renderKpi creates DOM with value', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = { value: 12345, previousValue: 10000 };
        const config = { title: 'Sales', format: 'currency', prefix: '$' };
        
        wd.WidgetRendererFactory.getRenderer('kpi')(container, data, config);
        
        expect(container.children.length).toBeGreaterThan(0);
        // title, value, trend
        expect(container.children.some(c => c.textContent === 'Sales')).toBe(true);
        expect(container.children.some(c => c.textContent === '$12.3K')).toBe(true);
        expect(container.children.some(c => c.textContent.indexOf('23.45%') >= 0)).toBe(true);
    });

    test('renderChart calls echarts.init', () => {
        const { wd, mockDocument, mockEcharts, capturedOptions } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = {
            columns: ['Region', 'Amount'],
            rows: [ { Region: 'North', Amount: 100 } ]
        };
        const config = { chartType: 'bar' };
        
        wd.WidgetRendererFactory.getRenderer('chart')(container, data, config);
        
        expect(mockEcharts.init).toHaveBeenCalledWith(container);
        expect(capturedOptions.length).toBe(1);
        expect(capturedOptions[0].xAxis.type).toBe('category');
        expect(capturedOptions[0].series[0].type).toBe('bar');
    });
});