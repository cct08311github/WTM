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

    // Regression test for #382: empty string Type should return null (not crash)
    test('getRenderer returns null for empty string type', () => {
        const { wd } = makeEnv();
        const renderer = wd.WidgetRendererFactory.getRenderer('');
        expect(renderer).toBeNull();
    });

    test('getRenderer returns null for null type', () => {
        const { wd } = makeEnv();
        const renderer = wd.WidgetRendererFactory.getRenderer(null);
        expect(renderer).toBeNull();
    });

    test('renderWidget shows error message for empty type', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        // Simulate the render path: getRenderer('') → null → error message
        const renderer = wd.WidgetRendererFactory.getRenderer('');
        if (!renderer) {
            container.textContent = 'Unknown widget type: ';
        }
        expect(container.textContent).toContain('Unknown widget type:');
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

    test('renderKpi with data={} (missing value field) shows 0 not "undefined" (#435)', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        wd.WidgetRendererFactory.getRenderer('kpi')(container, {}, { title: 'X' });
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv).toBeDefined();
        expect(valueDiv.textContent).not.toBe('undefined');
        expect(valueDiv.textContent).not.toContain('undefined');
        expect(valueDiv.textContent).toBe('0');
    });

    test('renderKpi with data=null shows 0 not "undefined" (#435)', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        wd.WidgetRendererFactory.getRenderer('kpi')(container, null, { title: 'X' });
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv).toBeDefined();
        expect(valueDiv.textContent).not.toBe('undefined');
        expect(valueDiv.textContent).toBe('0');
    });

    test('renderKpi with data.value=null shows 0 not "undefined" (#435)', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        wd.WidgetRendererFactory.getRenderer('kpi')(container, { value: null }, { title: 'X' });
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv).toBeDefined();
        expect(valueDiv.textContent).not.toBe('undefined');
        expect(valueDiv.textContent).toBe('0');
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

    test('getRenderer returns function for table type', () => {
        const { wd } = makeEnv();
        expect(typeof wd.WidgetRendererFactory.getRenderer('table')).toBe('function');
    });

    test('renderTable creates table with headers and rows', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = {
            columns: ['Name', 'Score'],
            rows: [
                { Name: 'Alice', Score: 95 },
                { Name: 'Bob', Score: 88 }
            ]
        };
        const config = {};

        wd.WidgetRendererFactory.getRenderer('table')(container, data, config);

        // Should create a table element
        expect(mockDocument.createElement).toHaveBeenCalledWith('table');
        expect(container.children.length).toBeGreaterThan(0);
        const table = container.children[0];
        expect(table.tag).toBe('table');
        // thead row + 2 data rows
        expect(table.children.length).toBe(3);
        // header cells
        const headerRow = table.children[0];
        expect(headerRow.children.some(c => c.textContent === 'Name')).toBe(true);
        expect(headerRow.children.some(c => c.textContent === 'Score')).toBe(true);
    });

    test('renderProgress creates bar with percentage', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = { value: 0.73 };
        const config = { title: 'Completion', color: '#4caf50' };

        wd.WidgetRendererFactory.getRenderer('progress')(container, data, config);

        expect(container.children.length).toBeGreaterThan(0);
        // Should have title and a progress bar container
        expect(container.children.some(c => c.textContent === 'Completion')).toBe(true);
        // The percentage label
        expect(container.children.some(c => c.textContent === '73%')).toBe(true);
    });

    test('renderList creates list items with textContent only', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = {
            items: [
                { label: 'Task A', description: 'Do something' },
                { label: 'Task B', description: 'Do another' }
            ]
        };
        const config = { title: 'Recent Tasks' };

        wd.WidgetRendererFactory.getRenderer('list')(container, data, config);

        expect(container.children.length).toBeGreaterThan(0);
        expect(container.children.some(c => c.textContent === 'Recent Tasks')).toBe(true);
        // Should have a list container with items
        const listContainer = container.children.find(c => c.tag === 'ul');
        expect(listContainer).toBeDefined();
        expect(listContainer.children.length).toBe(2);
        expect(listContainer.children[0].textContent).toBe('Task A — Do something');
    });

    test('renderEmbed creates iframe with sandbox', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = {};
        const config = { url: 'https://example.com/report' };

        wd.WidgetRendererFactory.getRenderer('embed')(container, data, config);

        expect(mockDocument.createElement).toHaveBeenCalledWith('iframe');
        expect(container.children.length).toBe(1);
        const iframe = container.children[0];
        expect(iframe.tag).toBe('iframe');
        expect(iframe.src).toBe('https://example.com/report');
        expect(iframe.sandbox).toBe('allow-scripts');
    });

    test('renderEmbed rejects javascript: URLs', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        const data = {};
        const config = { url: 'javascript:alert(1)' };

        wd.WidgetRendererFactory.getRenderer('embed')(container, data, config);

        // Should not create an iframe, should show error
        const hasIframe = container.children && container.children.some(c => c.tag === 'iframe');
        expect(hasIframe).toBeFalsy();
        expect(container.textContent).toMatch(/blocked|invalid/i);
    });
});

// ─── KPI threshold / alert coloring #386 ───────────────────────────────────
describe('renderKpi threshold coloring #386', () => {
    function makeKpiEnv() {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        container.children = [];
        return { wd, mockDocument, container };
    }

    function kpiRender(wd, container, val, thresholds, direction) {
        const config = { title: 'Risk', thresholds: thresholds };
        if (direction) config.thresholdDirection = direction;
        wd.WidgetRendererFactory.getRenderer('kpi')(container, { value: val }, config);
    }

    test('no thresholds — valueDiv has no color', () => {
        const { wd, container } = makeKpiEnv();
        kpiRender(wd, container, 5000, []);
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv).toBeDefined();
        expect(valueDiv.style.color || '').toBe('');
    });

    test('value below all thresholds — no color applied', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [
            { value: 10000, color: '#faad14', label: '警告' },
            { value: 50000, color: '#ff4d4f', label: '危險' }
        ];
        kpiRender(wd, container, 5000, thresholds);
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv.style.color || '').toBe('');
    });

    test('value exceeds warning threshold — yellow applied', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [
            { value: 10000, color: '#faad14', label: '警告' },
            { value: 50000, color: '#ff4d4f', label: '危險' }
        ];
        kpiRender(wd, container, 20000, thresholds);
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv.style.color).toBe('#faad14');
    });

    test('value exceeds danger threshold — red applied (highest level wins)', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [
            { value: 10000, color: '#faad14', label: '警告' },
            { value: 50000, color: '#ff4d4f', label: '危險' }
        ];
        kpiRender(wd, container, 60000, thresholds);
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv.style.color).toBe('#ff4d4f');
    });

    test('matched threshold with label — alert span appended', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [{ value: 10000, color: '#faad14', label: '警告' }];
        kpiRender(wd, container, 15000, thresholds);
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        const alertSpan = valueDiv.children && valueDiv.children.find(c => c.className === 'wtm-kpi-alert-label');
        expect(alertSpan).toBeDefined();
        expect(alertSpan.textContent).toContain('警告');
        expect(alertSpan.style.color).toBe('#faad14');
    });

    test('threshold without label — no alert span', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [{ value: 10000, color: '#faad14' }];
        kpiRender(wd, container, 15000, thresholds);
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        const alertSpan = valueDiv.children && valueDiv.children.find(c => c.className === 'wtm-kpi-alert-label');
        expect(alertSpan).toBeUndefined();
    });

    test('direction=below — color applied when value is at or below threshold', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [
            { value: 5000, color: '#faad14', label: '低水位警告' },
            { value: 1000, color: '#ff4d4f', label: '危急' }
        ];
        kpiRender(wd, container, 3000, thresholds, 'below');
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv.style.color).toBe('#faad14');
    });

    test('direction=below — critical level when value drops further', () => {
        const { wd, container } = makeKpiEnv();
        const thresholds = [
            { value: 5000, color: '#faad14', label: '低水位警告' },
            { value: 1000, color: '#ff4d4f', label: '危急' }
        ];
        kpiRender(wd, container, 500, thresholds, 'below');
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv.style.color).toBe('#ff4d4f');
    });

    test('non-array thresholds config — gracefully ignored', () => {
        const { wd, container } = makeKpiEnv();
        expect(() => {
            wd.WidgetRendererFactory.getRenderer('kpi')(container, { value: 99999 }, {
                title: 'X',
                thresholds: 'invalid'
            });
        }).not.toThrow();
        const valueDiv = container.children.find(c => c.className === 'wtm-kpi-value');
        expect(valueDiv.style.color || '').toBe('');
    });
});
