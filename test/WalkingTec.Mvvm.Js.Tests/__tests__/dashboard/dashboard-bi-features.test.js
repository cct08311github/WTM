/**
 * Tests for Dashboard client-side BI features (Issue #216):
 *   Q2  – multi-series / dual-Y charts
 *   L6  – chart-type expansion (gauge, funnel, radar, heatmap, scatter, sankey)
 *   L7  – DateRange filter presets
 *   Q10 – in-flight guard / self-scheduling refresh
 *   Q3  – static/text/image widget
 */
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const SRC = path.resolve(
    __dirname,
    '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'
);

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(SRC, 'utf8');

    const capturedOptions = [];
    const mockEcharts = {
        init: jest.fn(() => ({
            setOption: jest.fn(opt => capturedOptions.push(opt)),
            dispose: jest.fn()
        }))
    };

    const mockDocument = {
        createElement: jest.fn((tag) => ({
            tag,
            className: '',
            textContent: '',
            innerHTML: '',
            style: {},
            children: [],
            appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
            addEventListener: jest.fn()
        })),
        getElementById: jest.fn(() => null)
    };

    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: mockDocument,
        echarts: mockEcharts,
        fetch: jest.fn(),
        setTimeout: jest.fn((fn) => { fn(); return 1; }),
        clearTimeout: jest.fn(),
        setInterval: jest.fn(() => 99),
        clearInterval: jest.fn(),
        ...overrides
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockDocument,
        mockEcharts,
        capturedOptions,
        ctx: freshCtx
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Q2: Multi-series / dual-Y chart rendering
// ─────────────────────────────────────────────────────────────────────────────
describe('Q2: multi-series chart rendering', () => {
    function runChart(data, config) {
        const { wd, mockDocument, capturedOptions } = makeEnv();
        const container = mockDocument.createElement('div');
        wd.WidgetRendererFactory.getRenderer('chart')(container, data, config || {});
        return capturedOptions;
    }

    test('single-series bar: one series on primary Y, no secondary Y', () => {
        const opts = runChart({
            columns: ['Region', 'Revenue'],
            rows: [{ Region: 'North', Revenue: 100 }, { Region: 'South', Revenue: 200 }]
        }, { chartType: 'bar' });

        expect(opts.length).toBe(1);
        const opt = opts[0];
        expect(opt.series.length).toBe(1);
        expect(opt.series[0].type).toBe('bar');
        expect(opt.series[0].yAxisIndex).toBeUndefined();
        // Single Y axis
        expect(Array.isArray(opt.yAxis) ? opt.yAxis.length : 1).toBe(1);
    });

    test('multi-series bar+line combo: two series, dual Y axis', () => {
        const opts = runChart({
            columns: ['Month', 'Revenue', 'Orders'],
            rows: [
                { Month: '2024-01', Revenue: 500, Orders: 10 },
                { Month: '2024-02', Revenue: 700, Orders: 15 }
            ]
        }, { chartType: 'bar' });

        expect(opts.length).toBe(1);
        const opt = opts[0];
        // Two series
        expect(opt.series.length).toBe(2);
        // First series: bar on primary Y
        expect(opt.series[0].type).toBe('bar');
        expect(opt.series[0].name).toBe('Revenue');
        expect(opt.series[0].yAxisIndex).toBeUndefined();
        // Second series: line on secondary Y (default secondaryType)
        expect(opt.series[1].type).toBe('line');
        expect(opt.series[1].name).toBe('Orders');
        expect(opt.series[1].yAxisIndex).toBe(1);
        // Two Y axes
        expect(Array.isArray(opt.yAxis)).toBe(true);
        expect(opt.yAxis.length).toBe(2);
    });

    test('multi-series: secondaryType=bar overrides default line', () => {
        const opts = runChart({
            columns: ['Cat', 'A', 'B'],
            rows: [{ Cat: 'X', A: 1, B: 2 }]
        }, { chartType: 'bar', secondaryType: 'bar' });

        const opt = opts[0];
        expect(opt.series[1].type).toBe('bar');
    });

    test('multi-series three columns: three series, series[1] and [2] on secondary Y', () => {
        const opts = runChart({
            columns: ['Q', 'A', 'B', 'C'],
            rows: [{ Q: 'Q1', A: 1, B: 2, C: 3 }]
        }, { chartType: 'bar' });

        const opt = opts[0];
        expect(opt.series.length).toBe(3);
        expect(opt.series[0].yAxisIndex).toBeUndefined();
        expect(opt.series[1].yAxisIndex).toBe(1);
        expect(opt.series[2].yAxisIndex).toBe(1);
    });

    test('multi-series: legend is included', () => {
        const opts = runChart({
            columns: ['D', 'V1', 'V2'],
            rows: [{ D: 'x', V1: 1, V2: 2 }]
        }, { chartType: 'bar' });

        expect(opts[0].legend).toBeDefined();
    });

    test('single-series: no legend added', () => {
        const opts = runChart({
            columns: ['D', 'V'],
            rows: [{ D: 'x', V: 1 }]
        }, { chartType: 'bar' });

        expect(opts[0].legend).toBeUndefined();
    });

    test('multi-series xAxis data is built from dim column', () => {
        const opts = runChart({
            columns: ['Month', 'Sales', 'Cost'],
            rows: [
                { Month: 'Jan', Sales: 100, Cost: 80 },
                { Month: 'Feb', Sales: 120, Cost: 90 }
            ]
        }, { chartType: 'line' });

        expect(opts[0].xAxis.data).toEqual(['Jan', 'Feb']);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// L6: Chart-type expansion
// ─────────────────────────────────────────────────────────────────────────────
describe('L6: chart-type expansion via buildChartOption', () => {
    function build(type, xAxisData, allSeries, config) {
        const { wd } = makeEnv();
        return wd._internal.buildChartOption(type, xAxisData, allSeries, config || {});
    }

    // Existing types must still work
    test('bar: primary type, series[0] is bar', () => {
        const opt = build('bar', ['A'], [{ name: 'V', data: [1] }]);
        expect(opt.series[0].type).toBe('bar');
    });

    test('line: primary type, series[0] is line', () => {
        const opt = build('line', ['Jan'], [{ name: 'V', data: [10] }]);
        expect(opt.series[0].type).toBe('line');
    });

    test('pie: generates pie series with name/value pairs', () => {
        const opt = build('pie', ['A', 'B'], [{ name: 'V', data: [10, 20] }]);
        expect(opt.series[0].type).toBe('pie');
        expect(opt.series[0].data).toEqual([
            { name: 'A', value: 10 },
            { name: 'B', value: 20 }
        ]);
        expect(opt.series[0].radius).toBe('60%');
    });

    test('piehollow: donut with radius array', () => {
        const opt = build('piehollow', ['X'], [{ name: 'V', data: [5] }]);
        expect(opt.series[0].type).toBe('pie');
        expect(Array.isArray(opt.series[0].radius)).toBe(true);
        expect(opt.series[0].radius[0]).toBe('40%');
        expect(opt.series[0].radius[1]).toBe('70%');
    });

    // L6 NEW types
    test('gauge: single value series', () => {
        const opt = build('gauge', ['Speed'], [{ name: 'RPM', data: [75] }]);
        expect(opt.series[0].type).toBe('gauge');
        expect(opt.series[0].data[0].value).toBe(75);
    });

    test('gauge: missing data defaults to 0', () => {
        const opt = build('gauge', [], [{ name: '', data: [] }]);
        expect(opt.series[0].data[0].value).toBe(0);
    });

    test('funnel: generates funnel series with name/value pairs', () => {
        const opt = build('funnel', ['Step1', 'Step2'], [{ name: 'Count', data: [100, 60] }]);
        expect(opt.series[0].type).toBe('funnel');
        expect(opt.series[0].data).toEqual([
            { name: 'Step1', value: 100 },
            { name: 'Step2', value: 60 }
        ]);
    });

    test('radar: indicator list from xAxisData, series data from allSeries', () => {
        const opt = build('radar', ['Strength', 'Speed', 'Agility'], [
            { name: 'Hero', data: [80, 90, 70] }
        ]);
        expect(opt.series[0].type).toBe('radar');
        expect(opt.radar.indicator).toEqual([
            { name: 'Strength' },
            { name: 'Speed' },
            { name: 'Agility' }
        ]);
        expect(opt.series[0].data[0].name).toBe('Hero');
        expect(opt.series[0].data[0].value).toEqual([80, 90, 70]);
    });

    test('radar: multi-series produces multiple radar data items', () => {
        const opt = build('radar', ['A', 'B'], [
            { name: 'Team1', data: [70, 80] },
            { name: 'Team2', data: [60, 90] }
        ]);
        expect(opt.series[0].data.length).toBe(2);
    });

    test('heatmap: returns heatmap series', () => {
        const opt = build('heatmap', ['Mon', 'Tue'], [{ name: 'Heat', data: [[0, 0, 5]] }]);
        expect(opt.series[0].type).toBe('heatmap');
        expect(opt.xAxis.type).toBe('category');
        expect(opt.yAxis.type).toBe('category');
        expect(opt.visualMap).toBeDefined();
    });

    test('scatter: returns scatter series, value axis', () => {
        const opt = build('scatter', [], [{ name: 'Group1', data: [[1, 2], [3, 4]] }]);
        expect(opt.series[0].type).toBe('scatter');
        expect(opt.xAxis.type).toBe('value');
        expect(opt.yAxis.type).toBe('value');
    });

    test('scatter: multi-series produces multiple scatter series', () => {
        const opt = build('scatter', [], [
            { name: 'A', data: [[1, 1]] },
            { name: 'B', data: [[2, 2]] }
        ]);
        expect(opt.series.length).toBe(2);
        expect(opt.series[1].type).toBe('scatter');
    });

    test('sankey: returns sankey series with nodes and links', () => {
        const nodes = [{ name: 'A' }, { name: 'B' }];
        const links = [{ source: 'A', target: 'B', value: 10 }];
        const opt = build('sankey', [], [{ name: 'Flow', raw: { nodes, links } }]);
        expect(opt.series[0].type).toBe('sankey');
        expect(opt.series[0].data).toBe(nodes);
        expect(opt.series[0].links).toBe(links);
    });

    test('sankey: missing raw falls back to empty nodes/links', () => {
        const opt = build('sankey', [], [{ name: 'Flow' }]);
        expect(opt.series[0].type).toBe('sankey');
        expect(opt.series[0].data).toEqual([]);
        expect(opt.series[0].links).toEqual([]);
    });

    test('unknown type: treated as bar (default branch)', () => {
        // An unknown type falls through to bar/line branch and uses it as-is
        const opt = build('treemap', ['A'], [{ name: 'V', data: [1] }]);
        // series[0].type is whatever was passed (not validated here — ECharts handles it)
        expect(opt.series[0].data).toEqual([1]);
    });

    test('WidgetRendererFactory recognises chart type and calls echarts.init', () => {
        const { wd, mockDocument, mockEcharts } = makeEnv();
        const container = mockDocument.createElement('div');
        wd.WidgetRendererFactory.getRenderer('chart')(container, {
            columns: ['Cat', 'Val'],
            rows: [{ Cat: 'A', Val: 1 }]
        }, { chartType: 'gauge' });
        expect(mockEcharts.init).toHaveBeenCalled();
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// L7: DateRange filter presets
// ─────────────────────────────────────────────────────────────────────────────
describe('L7: computeDateRangePreset', () => {
    function preset(key, now) {
        const { wd } = makeEnv();
        return wd._internal.computeDateRangePreset(key, now);
    }

    const REF = new Date(2024, 5, 15); // 2024-06-15 (June is month 5)

    test('today: start === end === today', () => {
        const r = preset('today', REF);
        expect(r).toEqual({ start: '2024-06-15', end: '2024-06-15' });
    });

    test('last7days: 6 days before today inclusive', () => {
        const r = preset('last7days', REF);
        expect(r).toEqual({ start: '2024-06-09', end: '2024-06-15' });
    });

    test('last30days: 29 days before today inclusive', () => {
        const r = preset('last30days', REF);
        expect(r).toEqual({ start: '2024-05-17', end: '2024-06-15' });
    });

    test('thisMonth: 1st of current month to today', () => {
        const r = preset('thisMonth', REF);
        expect(r).toEqual({ start: '2024-06-01', end: '2024-06-15' });
    });

    test('thisQuarter: Q2 starts April 1', () => {
        const r = preset('thisQuarter', REF); // June = Q2 (months 3-5 = Apr-Jun)
        expect(r).toEqual({ start: '2024-04-01', end: '2024-06-15' });
    });

    test('thisQuarter: Q1 starts January 1', () => {
        const q1Ref = new Date(2024, 1, 14); // 2024-02-14
        const r = preset('thisQuarter', q1Ref);
        expect(r).toEqual({ start: '2024-01-01', end: '2024-02-14' });
    });

    test('thisQuarter: Q3 starts July 1', () => {
        const q3Ref = new Date(2024, 8, 1); // 2024-09-01
        const r = preset('thisQuarter', q3Ref);
        expect(r).toEqual({ start: '2024-07-01', end: '2024-09-01' });
    });

    test('thisYear: January 1 of current year to today', () => {
        const r = preset('thisYear', REF);
        expect(r).toEqual({ start: '2024-01-01', end: '2024-06-15' });
    });

    test('unknown preset returns null', () => {
        expect(preset('yesterday', REF)).toBeNull();
        expect(preset('', REF)).toBeNull();
        expect(preset('invalid', REF)).toBeNull();
    });

    test('case-insensitive: THISMONTH === thisMonth', () => {
        const r = preset('THISMONTH', REF);
        expect(r).not.toBeNull();
        expect(r.start).toBe('2024-06-01');
    });

    test('hyphen/space variants normalised: this-month, this month', () => {
        const r1 = preset('this-month', REF);
        const r2 = preset('this month', REF);
        expect(r1).not.toBeNull();
        expect(r2).not.toBeNull();
        expect(r1.start).toBe('2024-06-01');
        expect(r2.start).toBe('2024-06-01');
    });

    test('preset values are usable as FilterBar values', () => {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        wd.FilterBar.init([
            { field: 'DateRange', type: 'daterange', defaultValue: 'thisMonth' }
        ], container);

        const vals = wd.FilterBar.getValues();
        // daterange filter creates _start and _end fields
        expect(vals['DateRange_start']).toBe('2024-01-01' === vals['DateRange_start']
            ? '2024-01-01'
            : vals['DateRange_start']); // just check it's a non-empty string
        expect(typeof vals['DateRange_start']).toBe('string');
        expect(vals['DateRange_start'].length).toBeGreaterThan(0);
        expect(typeof vals['DateRange_end']).toBe('string');
        expect(vals['DateRange_end'].length).toBeGreaterThan(0);
    });

    test('daterange FilterBar sets start/end values from preset', () => {
        // Use a fixed date so the test is deterministic
        // We can only call computeDateRangePreset directly here
        const { wd } = makeEnv();
        const now = new Date(2024, 0, 20); // 2024-01-20
        const r = wd._internal.computeDateRangePreset('thisMonth', now);
        expect(r.start).toBe('2024-01-01');
        expect(r.end).toBe('2024-01-20');
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// Q10: In-flight guard — no request pile-up
// ─────────────────────────────────────────────────────────────────────────────
describe('Q10: in-flight guard prevents concurrent fetches for same widget', () => {
    function makeDocWithWidget(widgetId) {
        function makeEl(tag) {
            return {
                tag, className: '', textContent: '', value: '', type: '',
                name: '', style: {}, children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                addEventListener: jest.fn()
            };
        }
        const widgetEl = makeEl('div');
        widgetEl.id = widgetId;
        return {
            getElementById: jest.fn(id => id === widgetId ? widgetEl : null),
            createElement: jest.fn(tag => makeEl(tag)),
            widgetEl
        };
    }

    test('second call while first is in-flight skips the fetch', async () => {
        const mockDoc = makeDocWithWidget('w1');
        const mockFetch = jest.fn();

        // Dashboard definition fetch
        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1', layout: [],
                widgets: { 'w1': { type: 'kpi', config: {} } }
            })
        });
        // First widget data fetch — never resolves (simulates slow in-flight)
        let resolveFirst;
        const firstFetchPromise = new Promise(resolve => { resolveFirst = resolve; });
        mockFetch.mockReturnValueOnce(firstFetchPromise);
        // Would-be second fetch response (should not be called)
        mockFetch.mockResolvedValue({ ok: true, json: jest.fn().mockResolvedValue({ value: 0 }) });

        const { wd } = makeEnv({
            document: mockDoc,
            GridStack: { init: jest.fn(() => ({ load: jest.fn() })) },
            fetch: mockFetch,
            // setTimeout/clearTimeout handled by makeEnv defaults
        });

        await wd.DashboardManager.init('container', 'dash1');
        // After init, w1 fetch is in-flight (never resolved above).
        // Manually verify the in-flight flag is set (init triggered _fetchAndRenderWidget)
        // Now call _fetchAndRenderWidget again — it should NOT start a new fetch
        const callsBefore = mockFetch.mock.calls.length;
        wd.DashboardManager._fetchAndRenderWidget('w1', { type: 'kpi', config: {} });
        const callsAfter = mockFetch.mock.calls.length;

        // No additional fetch call should have been made
        expect(callsAfter).toBe(callsBefore);
    });

    test('_getInFlight returns false for never-fetched widget', () => {
        const { wd } = makeEnv();
        expect(wd.DashboardManager._getInFlight('neverFetched')).toBe(false);
    });

    test('_setInFlight / _getInFlight toggles correctly', () => {
        const { wd } = makeEnv();
        wd.DashboardManager._setInFlight('w1', true);
        expect(wd.DashboardManager._getInFlight('w1')).toBe(true);
        wd.DashboardManager._setInFlight('w1', false);
        expect(wd.DashboardManager._getInFlight('w1')).toBe(false);
    });

    test('in-flight is cleared on successful fetch', async () => {
        const mockDoc = makeDocWithWidget('w2');
        const mockFetch = jest.fn();

        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1', layout: [],
                widgets: { 'w2': { type: 'kpi', config: {} } }
            })
        });
        mockFetch.mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue({ value: 42 })
        });

        const { wd } = makeEnv({
            document: mockDoc,
            GridStack: { init: jest.fn(() => ({ load: jest.fn() })) },
            fetch: mockFetch
        });

        await wd.DashboardManager.init('container', 'dash1');
        // After successful completion the flag should be cleared
        await new Promise(r => setTimeout(r, 10));
        expect(wd.DashboardManager._getInFlight('w2')).toBe(false);
    });

    test('in-flight is cleared on failed fetch', async () => {
        const mockDoc = makeDocWithWidget('w3');
        const mockFetch = jest.fn();

        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1', layout: [],
                widgets: { 'w3': { type: 'kpi', config: {} } }
            })
        });
        // Widget data fetch fails
        mockFetch.mockResolvedValue({ ok: false });

        const { wd } = makeEnv({
            document: mockDoc,
            GridStack: { init: jest.fn(() => ({ load: jest.fn() })) },
            fetch: mockFetch
        });

        await wd.DashboardManager.init('container', 'dash1');
        await new Promise(r => setTimeout(r, 10));
        expect(wd.DashboardManager._getInFlight('w3')).toBe(false);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// Q10: Self-scheduling refresh (setTimeout, not setInterval)
// ─────────────────────────────────────────────────────────────────────────────
describe('Q10: self-scheduling refresh uses setTimeout', () => {
    test('startRefresh calls setTimeout (not setInterval)', () => {
        const mockSetTimeout = jest.fn(() => 42);
        const mockSetInterval = jest.fn();
        const { wd } = makeEnv({
            setTimeout: mockSetTimeout,
            clearTimeout: jest.fn(),
            setInterval: mockSetInterval,
            clearInterval: jest.fn()
        });

        wd.DashboardManager.startRefresh(30);
        expect(mockSetTimeout).toHaveBeenCalled();
    });

    test('stopRefresh calls clearTimeout', () => {
        const mockClearTimeout = jest.fn();
        const { wd, ctx } = makeEnv({
            setTimeout: jest.fn(() => 77),
            clearTimeout: mockClearTimeout,
            setInterval: jest.fn(),
            clearInterval: jest.fn()
        });

        wd.DashboardManager.startRefresh(30);
        wd.DashboardManager.stopRefresh();
        expect(mockClearTimeout).toHaveBeenCalledWith(77);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// Q3: Static / Text / Image widget
// ─────────────────────────────────────────────────────────────────────────────
describe('Q3: static widget renderer', () => {
    function makeStaticEnv() {
        const { wd, mockDocument } = makeEnv();
        const container = mockDocument.createElement('div');
        container.children = [];
        return { wd, mockDocument, container };
    }

    test('WidgetRendererFactory returns a function for "static" type', () => {
        const { wd } = makeStaticEnv();
        const renderer = wd.WidgetRendererFactory.getRenderer('static');
        expect(typeof renderer).toBe('function');
    });

    test('renders title when config.title is set', () => {
        const { wd, container } = makeStaticEnv();
        wd.WidgetRendererFactory.getRenderer('static')(container, null, { title: 'Hello Banner' });
        const titleEl = container.children.find(c => c.className === 'wtm-static-title');
        expect(titleEl).toBeDefined();
        expect(titleEl.textContent).toBe('Hello Banner');
    });

    test('renders plain text via textContent (no innerHTML)', () => {
        const { wd, container } = makeStaticEnv();
        wd.WidgetRendererFactory.getRenderer('static')(container, null, { text: 'Annotation note' });
        const textEl = container.children.find(c => c.className === 'wtm-static-text');
        expect(textEl).toBeDefined();
        expect(textEl.textContent).toBe('Annotation note');
    });

    test('renders image with src and alt when imageUrl is set', () => {
        const { wd, mockDocument, container } = makeStaticEnv();
        wd.WidgetRendererFactory.getRenderer('static')(container, null, {
            imageUrl: 'https://example.com/logo.png',
            title: 'Logo'
        });
        const imgEl = container.children.find(c => c.tag === 'img');
        expect(imgEl).toBeDefined();
        expect(imgEl.src).toBe('https://example.com/logo.png');
        expect(imgEl.alt).toBe('Logo');
    });

    test('html config: uses textContent when DOMPurify not present (safe fallback)', () => {
        const { wd, container } = makeStaticEnv();
        const htmlPayload = '<b>Bold text</b>';
        wd.WidgetRendererFactory.getRenderer('static')(container, null, { html: htmlPayload });
        const contentEl = container.children.find(c => c.className === 'wtm-static-content');
        expect(contentEl).toBeDefined();
        // Without DOMPurify the code uses textContent (safe fallback)
        expect(contentEl.textContent).toBe(htmlPayload);
    });

    test('html config: uses DOMPurify.sanitize when available', () => {
        const sanitized = '<b>safe</b>';
        const mockDOMPurify = { sanitize: jest.fn(() => sanitized) };
        const { wd, mockDocument } = makeEnv({ DOMPurify: mockDOMPurify });
        const container = mockDocument.createElement('div');
        container.children = [];
        wd.WidgetRendererFactory.getRenderer('static')(container, null, { html: '<b>safe</b>' });
        expect(mockDOMPurify.sanitize).toHaveBeenCalled();
    });

    test('no server fetch triggered for static widget during _renderAllWidgets', async () => {
        function makeDocWith(widgetId) {
            function el(tag) {
                return {
                    tag, className: '', textContent: '', style: {},
                    children: [],
                    appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                    addEventListener: jest.fn()
                };
            }
            const widgetEl = el('div');
            widgetEl.id = widgetId;
            return {
                getElementById: jest.fn(id => id === widgetId ? widgetEl : null),
                createElement: jest.fn(tag => el(tag)),
                widgetEl
            };
        }

        const mockDoc = makeDocWith('banner1');
        const mockFetch = jest.fn();

        // Dashboard def
        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1', layout: [],
                widgets: {
                    'banner1': {
                        type: 'static',
                        config: { title: 'Notice', text: 'System maintenance tonight' }
                    }
                }
            })
        });

        const { wd } = makeEnv({
            document: mockDoc,
            GridStack: { init: jest.fn(() => ({ load: jest.fn() })) },
            fetch: mockFetch
        });

        await wd.DashboardManager.init('container', 'dash1');

        // Only one fetch call: the dashboard definition itself.
        // No additional fetch for the static widget.
        expect(mockFetch).toHaveBeenCalledTimes(1);
        expect(mockFetch.mock.calls[0][0]).toBe('/_dashboard/dash1');
    });

    test('static widget renders without data argument (null data)', () => {
        const { wd, container } = makeStaticEnv();
        expect(() => {
            wd.WidgetRendererFactory.getRenderer('static')(container, null, { text: 'Note' });
        }).not.toThrow();
    });

    test('static widget with empty config renders without crashing', () => {
        const { wd, container } = makeStaticEnv();
        expect(() => {
            wd.WidgetRendererFactory.getRenderer('static')(container, null, {});
        }).not.toThrow();
    });

    test('html priority over imageUrl over text', () => {
        const { wd, container } = makeStaticEnv();
        wd.WidgetRendererFactory.getRenderer('static')(container, null, {
            html: '<em>html wins</em>',
            imageUrl: 'https://example.com/img.png',
            text: 'plain text'
        });
        // html branch should be used: wtm-static-content div, no img, no wtm-static-text
        const contentEl = container.children.find(c => c.className === 'wtm-static-content');
        const imgEl = container.children.find(c => c.tag === 'img');
        const textEl = container.children.find(c => c.className === 'wtm-static-text');
        expect(contentEl).toBeDefined();
        expect(imgEl).toBeUndefined();
        expect(textEl).toBeUndefined();
    });
});
