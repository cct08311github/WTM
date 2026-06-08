'use strict';
/**
 * Tests for framework_dashboard_designer.js
 *
 * Coverage:
 *  1. DesignerModel — adding a widget produces a valid WidgetDefinition shape
 *  2. DesignerModel — upsert, remove, snapshot
 *  3. _helpers — parseDimensions / parseMeasures
 *  4. _helpers — _esc XSS safety
 *  5. _helpers — _makeInput / _makeSelect / _makeDangerBtn (DOM, no innerHTML)
 *  6. _helpers — _makeWidgetNode (no inline onclick, uses addEventListener)
 *  7. chart-type / threshold / filter / drilldown form → config round-trip
 *  8. FILTER_OPS allowlist is exhaustive
 *  9. DATE_PRESETS keys match the runtime computeDateRangePreset keys
 * 10. live-preview calls /_dashboard-designer/preview with POST
 * 11. DesignerApi.save calls POST (new) or PUT (existing) on the right endpoint
 * 12. DesignerApi.loadDataSources calls /_dashboard/datasources
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

// ── Shared helpers ────────────────────────────────────────────────────────────

function loadDesigner(overrides = {}) {
    const designerSrc = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard_designer.js'),
        'utf8'
    );
    // Designer depends on framework_dashboard.js for WtmDashboard
    const dashboardSrc = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );

    const fetchResponses = [];
    const mockFetch = jest.fn(() => {
        const resp = fetchResponses.shift() || { ok: true, json: () => Promise.resolve({}) };
        return Promise.resolve(resp);
    });

    const mockGridStack = {
        save: jest.fn(() => []),
        load: jest.fn(),
        removeAll: jest.fn(),
        enable: jest.fn(),
        disable: jest.fn(),
        addWidget: jest.fn()
    };

    // Minimal DOM stubs — enough for the designer helpers that need document.getElementById
    // and querySelector, but NOT a full jsdom tree (tests run with testEnvironment: jsdom
    // so the real document is available for the _makeWidgetNode tests).
    const ctx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: global.document,       // use jsdom's real document
        layui: {
            layer: {
                open: jest.fn(() => 1),
                close: jest.fn(),
                msg: jest.fn()
            },
            form: { on: jest.fn(), render: jest.fn() }
        },
        GridStack: { init: jest.fn(() => mockGridStack) },
        fetch: mockFetch,
        setTimeout: jest.fn(),
        clearTimeout: jest.fn(),
        history: { replaceState: jest.fn() },
        ...overrides
    });
    ctx.window = ctx;

    new vm.Script(dashboardSrc).runInContext(ctx);
    new vm.Script(designerSrc).runInContext(ctx);

    return { ctx, mockFetch, fetchResponses, mockGridStack };
}

// ── 1. DesignerModel — addWidget produces valid WidgetDefinition shape ─────────

describe('DesignerModel.upsertWidget', () => {
    test('adding a widget stores a WidgetDefinition-compatible object', () => {
        const { ctx } = loadDesigner();
        const model = ctx.WtmDashboardDesigner.DesignerModel;
        model.load(null);

        const widgetDef = {
            type: 'chart',
            title: 'Revenue',
            source: { kind: 'analysis', listVmType: 'Foo.SalesVM', dimensions: [{ field: 'year' }], measures: [{ field: 'revenue', func: 'Sum' }] },
            config: { chartType: 'bar' },
            drillDown: null
        };

        const wid = model.upsertWidget('w_test_1', widgetDef);

        expect(wid).toBe('w_test_1');
        const stored = model.getWidget('w_test_1');
        expect(stored).not.toBeNull();
        expect(stored.type).toBe('chart');
        expect(stored.title).toBe('Revenue');
        expect(stored.source.kind).toBe('analysis');
        expect(stored.source.listVmType).toBe('Foo.SalesVM');
        expect(stored.source.dimensions[0].field).toBe('year');
        expect(stored.source.measures[0].func).toBe('Sum');
        expect(stored.config.chartType).toBe('bar');
    });

    test('auto-generates widgetId when not supplied', () => {
        const { ctx } = loadDesigner();
        const model = ctx.WtmDashboardDesigner.DesignerModel;
        model.load(null);
        const wid = model.upsertWidget(null, { type: 'kpi', title: 'KPI', source: {}, config: {} });
        expect(typeof wid).toBe('string');
        expect(wid.startsWith('w_')).toBe(true);
    });
});

// ── 2. DesignerModel — remove, snapshot ──────────────────────────────────────

describe('DesignerModel', () => {
    test('removeWidget deletes the widget from the model', () => {
        const { ctx } = loadDesigner();
        const model = ctx.WtmDashboardDesigner.DesignerModel;
        model.load(null);
        model.upsertWidget('w1', { type: 'kpi', title: 'K', source: {}, config: {} });
        model.removeWidget('w1');
        expect(model.getWidget('w1')).toBeNull();
    });

    test('snapshot returns a deep copy — mutations do not affect the model', () => {
        const { ctx } = loadDesigner();
        const model = ctx.WtmDashboardDesigner.DesignerModel;
        model.load({ id: 'dash1', title: 'T', widgets: {}, layout: [] });
        const snap = model.snapshot();
        snap.title = 'MUTATED';
        expect(model.getDef().title).toBe('T');
    });

    test('setTitle / setRefreshInterval / setSharing update the draft', () => {
        const { ctx } = loadDesigner();
        const model = ctx.WtmDashboardDesigner.DesignerModel;
        model.load(null);
        model.setTitle('My DB');
        model.setRefreshInterval(120);
        model.setSharing('roles', ['admin']);
        const def = model.getDef();
        expect(def.title).toBe('My DB');
        expect(def.refreshInterval).toBe(120);
        expect(def.sharing.mode).toBe('roles');
        expect(def.sharing.roles).toEqual(['admin']);
    });
});

// ── 3. _helpers — parseDimensions / parseMeasures ────────────────────────────

describe('_helpers._parseDimensions', () => {
    test('parses comma-separated fields into DimensionConfig array', () => {
        const { ctx } = loadDesigner();
        const parse = ctx.WtmDashboardDesigner._helpers._parseDimensions;
        const result = parse('year,region');
        expect(result).toHaveLength(2);
        expect(result[0]).toEqual({ field: 'year' });
        expect(result[1]).toEqual({ field: 'region' });
    });

    test('returns null for empty string', () => {
        const { ctx } = loadDesigner();
        const parse = ctx.WtmDashboardDesigner._helpers._parseDimensions;
        expect(parse('')).toBeNull();
        expect(parse(null)).toBeNull();
    });
});

describe('_helpers._parseMeasures', () => {
    test('parses "field:func" pairs into MeasureConfig array', () => {
        const { ctx } = loadDesigner();
        const parse = ctx.WtmDashboardDesigner._helpers._parseMeasures;
        const result = parse('revenue:Sum,count:Count');
        expect(result).toHaveLength(2);
        expect(result[0]).toEqual({ field: 'revenue', func: 'Sum' });
        expect(result[1]).toEqual({ field: 'count', func: 'Count' });
    });

    test('defaults func to Sum when not specified', () => {
        const { ctx } = loadDesigner();
        const parse = ctx.WtmDashboardDesigner._helpers._parseMeasures;
        const result = parse('amount');
        expect(result[0].func).toBe('Sum');
    });
});

// ── 4. _helpers — _esc XSS safety ────────────────────────────────────────────

describe('_helpers._esc', () => {
    test('escapes HTML special characters', () => {
        const { ctx } = loadDesigner();
        const esc = ctx.WtmDashboardDesigner._helpers._esc;
        expect(esc('<script>alert(1)</script>')).toBe('&lt;script&gt;alert(1)&lt;/script&gt;');
        expect(esc('"hello"')).toBe('&quot;hello&quot;');
        expect(esc('a & b')).toBe('a &amp; b');
    });

    test('handles null/undefined safely', () => {
        const { ctx } = loadDesigner();
        const esc = ctx.WtmDashboardDesigner._helpers._esc;
        expect(esc(null)).toBe('');
        expect(esc(undefined)).toBe('');
    });
});

// ── 5. _helpers — safe DOM builder functions ──────────────────────────────────

describe('_helpers DOM builders', () => {
    test('_makeInput creates an input element (no innerHTML)', () => {
        const { ctx } = loadDesigner();
        const makeInput = ctx.WtmDashboardDesigner._helpers._makeInput;
        const inp = makeInput('my-class', 'placeholder text', 'initial value');
        // jsdom will have created a real HTMLInputElement
        expect(inp.tagName.toLowerCase()).toBe('input');
        expect(inp.placeholder).toBe('placeholder text');
        expect(inp.value).toBe('initial value');
        expect(inp.className).toContain('my-class');
    });

    test('_makeSelect creates a select with correct options (no innerHTML)', () => {
        const { ctx } = loadDesigner();
        const makeSelect = ctx.WtmDashboardDesigner._helpers._makeSelect;
        const items = [{ value: 'a', label: 'Alpha' }, { value: 'b', label: 'Beta' }];
        const sel = makeSelect('sel-class', items, 'b');
        expect(sel.tagName.toLowerCase()).toBe('select');
        expect(sel.options.length).toBe(2);
        expect(sel.options[1].selected).toBe(true);
        expect(sel.options[0].textContent).toBe('Alpha');
    });

    test('_makeDangerBtn creates a button with correct text (no innerHTML)', () => {
        const { ctx } = loadDesigner();
        const makeBtn = ctx.WtmDashboardDesigner._helpers._makeDangerBtn;
        const btn = makeBtn('刪除', 'my-extra-cls');
        expect(btn.tagName.toLowerCase()).toBe('button');
        expect(btn.textContent).toBe('刪除');
        expect(btn.className).toContain('layui-btn-danger');
        expect(btn.className).toContain('my-extra-cls');
    });
});

// ── 6. _helpers — _makeWidgetNode: no inline onclick, uses addEventListener ───

describe('_helpers._makeWidgetNode', () => {
    test('produces a DOM node with title and type badge via textContent', () => {
        const { ctx } = loadDesigner();
        const makeNode = ctx.WtmDashboardDesigner._helpers._makeWidgetNode;
        const node = makeNode('w_test', { type: 'chart', title: 'My Chart' });
        expect(node.querySelector('.dsd-widget-title').textContent).toBe('My Chart');
        expect(node.querySelector('.dsd-widget-type-badge').textContent).toBe('chart');
    });

    test('edit and delete buttons have no inline onclick attribute', () => {
        const { ctx } = loadDesigner();
        const makeNode = ctx.WtmDashboardDesigner._helpers._makeWidgetNode;
        const node = makeNode('w_test', { type: 'kpi', title: 'KPI' });
        const buttons = node.querySelectorAll('button');
        buttons.forEach(function (btn) {
            // getAttribute returns null or '' for missing attribute
            expect(btn.getAttribute('onclick')).toBeNull();
        });
    });

    test('XSS in title is rendered safely via textContent', () => {
        const { ctx } = loadDesigner();
        const makeNode = ctx.WtmDashboardDesigner._helpers._makeWidgetNode;
        const node = makeNode('w_test', { type: 'kpi', title: '<img src=x onerror=alert(1)>' });
        const titleEl = node.querySelector('.dsd-widget-title');
        // textContent should show the raw string — not execute it
        expect(titleEl.textContent).toBe('<img src=x onerror=alert(1)>');
        // The DOM should NOT contain an <img> child from the title
        expect(node.querySelector('img')).toBeNull();
    });
});

// ── 7. Config round-trip: form reads → WidgetDefinition fields ────────────────
// DesignerPanel._readForm is private; we test the constituent parsers + constant
// coverage through the public _helpers surface.

describe('Form → config round-trip (parser helpers)', () => {
    test('threshold row reader returns undefined when no DOM rows present', () => {
        const { ctx } = loadDesigner();
        // No .dsd-threshold-row nodes in the test document
        const thresholds = ctx.WtmDashboardDesigner._helpers._readThresholdRows();
        expect(thresholds).toBeUndefined();
    });

    test('drilldown row reader returns undefined when no DOM rows present', () => {
        const { ctx } = loadDesigner();
        const links = ctx.WtmDashboardDesigner._helpers._readDrillDownRows();
        expect(links).toBeUndefined();
    });

    test('filter row reader returns null when no DOM rows present', () => {
        const { ctx } = loadDesigner();
        const filters = ctx.WtmDashboardDesigner._helpers._readFilterRows();
        expect(filters).toBeNull();
    });

    test('CHART_TYPES contains exactly 10 distinct type values', () => {
        const { ctx } = loadDesigner();
        const types = ctx.WtmDashboardDesigner._helpers.CHART_TYPES;
        expect(types).toHaveLength(10);
        const values = types.map(function (t) { return t.value; });
        const unique = new Set(values);
        expect(unique.size).toBe(10);
    });

    test('WIDGET_TYPES list matches expected types', () => {
        const { ctx } = loadDesigner();
        const types = ctx.WtmDashboardDesigner._helpers.WIDGET_TYPES.map(function (t) { return t.value; });
        expect(types).toEqual(expect.arrayContaining(['kpi', 'chart', 'table', 'progress', 'list', 'embed', 'static']));
    });
});

// ── 8. FILTER_OPS allowlist ───────────────────────────────────────────────────

describe('FILTER_OPS', () => {
    test('contains all 10 expected operator strings', () => {
        const { ctx } = loadDesigner();
        const ops = ctx.WtmDashboardDesigner._helpers.FILTER_OPS;
        const expected = ['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'contains', 'notcontains', 'in', 'notin'];
        expected.forEach(function (op) {
            expect(ops).toContain(op);
        });
        expect(ops).toHaveLength(10);
    });
});

// ── 9. DATE_PRESETS match runtime keys ────────────────────────────────────────

describe('DATE_PRESETS', () => {
    test('preset keys match the runtime computeDateRangePreset expectations', () => {
        const { ctx } = loadDesigner();
        const presets = ctx.WtmDashboardDesigner._helpers.DATE_PRESETS;
        const nonEmpty = presets.filter(function (p) { return p.value !== ''; });
        const runtimeKeys = ['today', 'last7days', 'last30days', 'thisMonth', 'thisQuarter', 'thisYear'];
        // Case-insensitive match: runtime normalises with toLowerCase + strip separators
        nonEmpty.forEach(function (p) {
            const normalised = p.value.toLowerCase().replace(/[\s_\-]/g, '');
            const runtimeNorm = runtimeKeys.map(function (k) { return k.toLowerCase().replace(/[\s_\-]/g, ''); });
            expect(runtimeNorm).toContain(normalised);
        });
    });
});

// ── 10. live-preview calls POST /_dashboard-designer/preview ─────────────────

describe('DesignerApi.previewWidgetInContainer', () => {
    test('calls POST /_dashboard-designer/preview with widget def JSON body', async () => {
        const { ctx, mockFetch, fetchResponses } = loadDesigner();

        // Stub for datasources (called by init)
        fetchResponses.push({ ok: true, json: () => Promise.resolve([]) });

        const widgetDef = { type: 'chart', title: 'T', source: { kind: 'analysis' }, config: { chartType: 'bar' } };

        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({ columns: ['year'], rows: [{ year: '2024', revenue: 100 }] })
        });

        const container = document.createElement('div');
        await ctx.WtmDesigner.previewWidgetInContainer(widgetDef, container);

        // Find the preview fetch call
        const previewCall = mockFetch.mock.calls.find(function (c) {
            return c[0].includes('/_dashboard-designer/preview');
        });
        expect(previewCall).toBeDefined();
        expect(previewCall[1].method).toBe('POST');
        expect(previewCall[1].headers['Content-Type']).toBe('application/json');
        const body = JSON.parse(previewCall[1].body);
        expect(body.type).toBe('chart');
    });

    test('sets error text in container when preview call fails', async () => {
        const { ctx, fetchResponses } = loadDesigner();
        // Do NOT push a datasources stub here — init() is never called in this test,
        // so the first (and only) fetch is the preview POST.
        fetchResponses.push({ ok: false, status: 500 });                    // preview fails

        const container = document.createElement('div');
        await ctx.WtmDesigner.previewWidgetInContainer(
            { type: 'kpi', title: 'K', source: {}, config: {} }, container);

        expect(container.textContent).toContain('預覽失敗');
    });
});

// ── 11. DesignerApi.save — POST for new, PUT for existing ─────────────────────

describe('DesignerApi.save', () => {
    test('calls POST /_dashboard/ when no dashboardId is set (new dashboard)', async () => {
        const { ctx, mockFetch, fetchResponses } = loadDesigner();

        // datasources fetch
        fetchResponses.push({ ok: true, json: () => Promise.resolve([]) });
        // init (new, no fetch for definition)
        await ctx.WtmDesigner.init('dsd-grid', null);

        // save POST
        fetchResponses.push({ ok: true, json: () => Promise.resolve('new-id-123') });

        const newId = await ctx.WtmDesigner.save({ title: 'New DB', refreshInterval: 60, sharing: { mode: 'private' } });

        expect(newId).toBe('new-id-123');
        const saveCall = mockFetch.mock.calls.find(function (c) {
            return c[1] && c[1].method === 'POST';
        });
        expect(saveCall).toBeDefined();
        expect(saveCall[0]).toContain('/_dashboard/');
        const body = JSON.parse(saveCall[1].body);
        expect(body.title).toBe('New DB');
    });

    test('calls PUT /_dashboard/{id} when editing an existing dashboard', async () => {
        const { ctx, mockFetch, fetchResponses } = loadDesigner();

        // datasources
        fetchResponses.push({ ok: true, json: () => Promise.resolve([]) });
        // GET existing definition
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'existing-dash', title: 'Old', widgets: {}, layout: [],
                refreshInterval: 60, sharing: { mode: 'private' }
            })
        });
        await ctx.WtmDesigner.init('dsd-grid', 'existing-dash');

        // save PUT
        fetchResponses.push({ ok: true, json: () => Promise.resolve('existing-dash') });

        await ctx.WtmDesigner.save({ title: 'Updated', refreshInterval: 30, sharing: { mode: 'public' } });

        const putCall = mockFetch.mock.calls.find(function (c) {
            return c[1] && c[1].method === 'PUT';
        });
        expect(putCall).toBeDefined();
        expect(putCall[0]).toContain('existing-dash');
        const body = JSON.parse(putCall[1].body);
        expect(body.title).toBe('Updated');
        expect(body.id).toBe('existing-dash');
    });
});

// ── 12. DesignerApi.loadDataSources calls /_dashboard/datasources ─────────────

describe('DesignerApi.loadDataSources', () => {
    test('fetches from /_dashboard/datasources and returns the array', async () => {
        const { ctx, mockFetch, fetchResponses } = loadDesigner();
        const mockSources = [
            { name: 'Foo.SalesVM', kind: 'analysis', dimensions: [{ field: 'year' }], measures: [] },
            { name: 'RestSource', kind: 'rest' }
        ];
        fetchResponses.push({ ok: true, json: () => Promise.resolve(mockSources) });

        const sources = await ctx.WtmDesigner.loadDataSources();

        expect(sources).toEqual(mockSources);
        const fetchedUrl = mockFetch.mock.calls[0][0];
        expect(fetchedUrl).toContain('/_dashboard/datasources');
    });

    test('returns empty array when fetch fails', async () => {
        const { ctx, fetchResponses } = loadDesigner();
        fetchResponses.push({ ok: false, status: 500 });

        const sources = await ctx.WtmDesigner.loadDataSources();
        expect(Array.isArray(sources)).toBe(true);
        expect(sources.length).toBe(0);
    });
});

// ── 13. Tenant scoping is preserved through DesignerApi.save ─────────────────
// (tenantId is set server-side; the designer just passes whatever the GET returned)

describe('DesignerApi.save tenant scoping', () => {
    test('snapshot preserves tenantId from the loaded definition', async () => {
        const { ctx, mockFetch, fetchResponses } = loadDesigner();

        fetchResponses.push({ ok: true, json: () => Promise.resolve([]) }); // datasources
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'td-1', title: 'T', tenantId: 'tenant-abc',
                widgets: {}, layout: [], refreshInterval: 60,
                sharing: { mode: 'private' }
            })
        });
        await ctx.WtmDesigner.init('dsd-grid', 'td-1');

        fetchResponses.push({ ok: true, json: () => Promise.resolve('td-1') });
        await ctx.WtmDesigner.save({ title: 'T', refreshInterval: 60, sharing: { mode: 'private' } });

        const putCall = mockFetch.mock.calls.find(function (c) { return c[1] && c[1].method === 'PUT'; });
        expect(putCall).toBeDefined();
        const body = JSON.parse(putCall[1].body);
        // tenantId must be forwarded — server will re-assert it, but client must not strip it
        expect(body.tenantId).toBe('tenant-abc');
    });
});
