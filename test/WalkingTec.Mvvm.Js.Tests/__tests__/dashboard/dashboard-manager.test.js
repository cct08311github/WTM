const fs = require('fs');
const path = require('path');
const vm = require('vm');

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );
    const mockDocument = {
        getElementById: jest.fn((id) => {
            return {
                id,
                textContent: '',
                style: {},
                appendChild: jest.fn()
            };
        }),
        createElement: jest.fn((tag) => {
            return { tag, className: '', textContent: '', appendChild: jest.fn() };
        })
    };
    
    const mockGridStack = {
        init: jest.fn(() => ({
            load: jest.fn()
        }))
    };

    const mockFetch = jest.fn();

    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: mockDocument,
        GridStack: mockGridStack,
        fetch: mockFetch,
        setInterval: jest.fn(() => 999),
        clearInterval: jest.fn(),
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockDocument,
        mockGridStack,
        mockFetch,
        ctx: freshCtx
    };
}

describe('WtmDashboard.DashboardManager', () => {
    test('init fetches dashboard, initializes grid, and renders widgets', async () => {
        const { wd, mockFetch, mockGridStack } = makeEnv();
        
        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1',
                layout: [],
                widgets: {
                    'w1': { type: 'kpi' }
                }
            })
        });
        
        // Mock fetch for widget data
        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({ value: 100 })
        });
        
        await wd.DashboardManager.init('container', 'dash1');
        
        expect(mockFetch).toHaveBeenCalledWith('/_dashboard/dash1');
        expect(mockGridStack.init).toHaveBeenCalled();
        expect(mockFetch).toHaveBeenCalledWith('/_dashboard/dash1/widget/w1/data');
    });

    test('startRefresh sets interval', () => {
        const { wd, ctx } = makeEnv();
        wd.DashboardManager.startRefresh(60);
        expect(ctx.setInterval).toHaveBeenCalledWith(expect.any(Function), 60000);
    });

    test('stopRefresh clears interval', () => {
        const { wd, ctx } = makeEnv();
        wd.DashboardManager.startRefresh(60);
        wd.DashboardManager.stopRefresh();
        expect(ctx.clearInterval).toHaveBeenCalledWith(999);
    });

    // ── Q1: FilterBar values are forwarded in widget data fetch URL ───────

    // Helper: make a minimal document mock whose createElement returns elements
    // with addEventListener so FilterBar.init does not throw.
    function makeDocWithWidget(widgetId) {
        function makeEl(tag) {
            return {
                tag,
                className: '',
                textContent: '',
                value: '',
                type: '',
                name: '',
                style: {},
                children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                addEventListener: jest.fn()
            };
        }
        const widgetContainer = makeEl('div');
        widgetContainer.id = widgetId;
        return {
            getElementById: jest.fn((id) => id === widgetId ? widgetContainer : null),
            createElement: jest.fn((tag) => makeEl(tag)),
            widgetContainer
        };
    }

    test('Q1: widget data request URL includes active FilterBar values as query params', async () => {
        const mockDocW = makeDocWithWidget('w1');
        const mockGridStack = { init: jest.fn(() => ({ load: jest.fn() })) };
        const mockFetch = jest.fn();

        const { wd } = makeEnv({
            document: mockDocW,
            GridStack: mockGridStack,
            fetch: mockFetch
        });

        // Step 1: load dashboard definition
        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1',
                layout: [],
                widgets: { 'w1': { type: 'kpi' } }
            })
        });
        // Step 2: widget data response
        mockFetch.mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue({ value: 42 })
        });

        // Prime the FilterBar with a value before init
        wd.FilterBar.init([
            { field: 'Region', type: 'text', defaultValue: 'North' }
        ], mockDocW.createElement('div'));

        await wd.DashboardManager.init('container', 'dash1');

        // The widget data fetch URL must include the filter value
        const calls = mockFetch.mock.calls.map(c => c[0]);
        const widgetDataCall = calls.find(url => url.includes('/widget/w1/data'));
        expect(widgetDataCall).toBeDefined();
        expect(widgetDataCall).toContain('Region=North');
    });

    test('Q1: FilterBar change triggers a new widget data fetch with updated filter value', async () => {
        const mockDocW = makeDocWithWidget('w1');
        const mockGridStack = { init: jest.fn(() => ({ load: jest.fn() })) };
        const mockFetch = jest.fn();

        const { wd } = makeEnv({
            document: mockDocW,
            GridStack: mockGridStack,
            fetch: mockFetch
        });

        // Load dashboard first, then widget data
        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1',
                layout: [],
                widgets: { 'w1': { type: 'kpi' } }
            })
        });
        mockFetch.mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue({ value: 0 })
        });

        wd.FilterBar.init([
            { field: 'Year', type: 'text', defaultValue: '2024' }
        ], mockDocW.createElement('div'));

        await wd.DashboardManager.init('container', 'dash1');

        // Clear previous fetch calls
        const callsBefore = mockFetch.mock.calls.length;

        // Change the filter value (triggers onChange → _renderAllWidgets → _fetchAndRenderWidget)
        wd.FilterBar.setValue('Year', '2025');

        // Wait for async fetch
        await new Promise(r => setTimeout(r, 10));

        const newCalls = mockFetch.mock.calls.slice(callsBefore).map(c => c[0]);
        const refetchCall = newCalls.find(url => url.includes('/widget/w1/data'));
        expect(refetchCall).toBeDefined();
        expect(refetchCall).toContain('Year=2025');
    });

    test('Q1: empty FilterBar values are omitted from widget data URL', async () => {
        const mockDocW = makeDocWithWidget('w1');
        const mockGridStack = { init: jest.fn(() => ({ load: jest.fn() })) };
        const mockFetch = jest.fn();

        const { wd } = makeEnv({
            document: mockDocW,
            GridStack: mockGridStack,
            fetch: mockFetch
        });

        mockFetch.mockResolvedValueOnce({
            ok: true,
            json: jest.fn().mockResolvedValue({
                id: 'dash1',
                layout: [],
                widgets: { 'w1': { type: 'kpi' } }
            })
        });
        mockFetch.mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue({ value: 1 })
        });

        // FilterBar with empty default values — nothing should appear in URL
        wd.FilterBar.init([
            { field: 'Region', type: 'text', defaultValue: '' }
        ], mockDocW.createElement('div'));

        await wd.DashboardManager.init('container', 'dash1');

        const calls = mockFetch.mock.calls.map(c => c[0]);
        const widgetDataCall = calls.find(url => url.includes('/widget/w1/data'));
        expect(widgetDataCall).toBeDefined();
        // No query string appended for empty values
        expect(widgetDataCall).not.toContain('?');
        expect(widgetDataCall).toBe('/_dashboard/dash1/widget/w1/data');
    });
});