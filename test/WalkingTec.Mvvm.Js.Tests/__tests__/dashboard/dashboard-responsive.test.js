const fs = require('fs');
const path = require('path');
const vm = require('vm');

function makeEnv(overrides = {}) {
    const src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.Mvc/framework_dashboard.js'),
        'utf8'
    );
    const mockGridStack = {
        save: jest.fn(() => []),
        load: jest.fn(),
        removeAll: jest.fn(),
        enable: jest.fn(),
        disable: jest.fn(),
        addWidget: jest.fn()
    };
    const fetchResponses = [];
    const mockFetch = jest.fn(() => {
        const resp = fetchResponses.shift() || { ok: true, json: () => Promise.resolve({}) };
        return Promise.resolve(resp);
    });
    const mockDocument = {
        createElement: jest.fn((tag) => ({
            tag,
            className: '',
            textContent: '',
            style: {},
            children: [],
            appendChild: jest.fn(function (c) { this.children.push(c); return c; })
        })),
        getElementById: jest.fn((id) => {
            const el = {
                id,
                className: '',
                textContent: '',
                style: {},
                _attrs: {},
                setAttribute: jest.fn(function (k, v) { this._attrs[k] = v; }),
                removeAttribute: jest.fn(function (k) { delete this._attrs[k]; }),
                appendChild: jest.fn()
            };
            return el;
        })
    };
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: mockDocument,
        GridStack: {
            init: jest.fn(() => mockGridStack)
        },
        fetch: mockFetch,
        setInterval: jest.fn(),
        clearInterval: jest.fn(),
        innerWidth: 1400,   // default: lg
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockGridStack,
        mockFetch,
        fetchResponses,
        mockDocument,
        ctx: freshCtx
    };
}

// ---------------------------------------------------------------------------
// detectBreakpoint
// ---------------------------------------------------------------------------
describe('detectBreakpoint', () => {
    test('returns lg when innerWidth >= 1200', () => {
        const { wd } = makeEnv({ innerWidth: 1400 });
        expect(wd._internal.detectBreakpoint()).toBe('lg');
    });

    test('returns md when 992 <= innerWidth < 1200', () => {
        const { wd } = makeEnv({ innerWidth: 1000 });
        expect(wd._internal.detectBreakpoint()).toBe('md');
    });

    test('returns sm when 768 <= innerWidth < 992', () => {
        const { wd } = makeEnv({ innerWidth: 800 });
        expect(wd._internal.detectBreakpoint()).toBe('sm');
    });

    test('returns xs when innerWidth < 768', () => {
        const { wd } = makeEnv({ innerWidth: 375 });
        expect(wd._internal.detectBreakpoint()).toBe('xs');
    });

    test('returns lg when innerWidth is exactly 1200', () => {
        const { wd } = makeEnv({ innerWidth: 1200 });
        expect(wd._internal.detectBreakpoint()).toBe('lg');
    });

    test('returns sm when innerWidth is exactly 768', () => {
        const { wd } = makeEnv({ innerWidth: 768 });
        expect(wd._internal.detectBreakpoint()).toBe('sm');
    });
});

// ---------------------------------------------------------------------------
// applyBreakpointToLayout
// ---------------------------------------------------------------------------
describe('applyBreakpointToLayout', () => {
    const baseLayout = [
        { id: 'w1', x: 0, y: 0, w: 6, h: 2 },
        { id: 'w2', x: 6, y: 0, w: 6, h: 2 }
    ];

    test('returns empty array for null or empty layout', () => {
        const { wd } = makeEnv();
        expect(wd._internal.applyBreakpointToLayout(null, 'lg')).toEqual([]);
        expect(wd._internal.applyBreakpointToLayout([], 'lg')).toEqual([]);
    });

    test('lg uses base values when no breakpoints defined', () => {
        const { wd } = makeEnv();
        const result = wd._internal.applyBreakpointToLayout(baseLayout, 'lg');
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 6, h: 2 });
        expect(result[1]).toEqual({ id: 'w2', x: 6, y: 0, w: 6, h: 2 });
    });

    test('xs auto-stacks to single column when no xs breakpoint defined', () => {
        const { wd } = makeEnv();
        const result = wd._internal.applyBreakpointToLayout(baseLayout, 'xs');
        // w1: h=2, so occupies y[0..1]; w2 must start at y=2 (not y=1) to avoid overlap
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 12, h: 2 });
        expect(result[1]).toEqual({ id: 'w2', x: 0, y: 2, w: 12, h: 2 });
    });

    test('xs uses explicit xs breakpoint when provided', () => {
        const { wd } = makeEnv();
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 6, h: 2, breakpoints: { xs: { x: 0, y: 0, w: 12, h: 4 } } }
        ];
        const result = wd._internal.applyBreakpointToLayout(layout, 'xs');
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 12, h: 4 });
    });

    test('md uses md breakpoint override when provided', () => {
        const { wd } = makeEnv();
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 6, h: 2, breakpoints: { md: { x: 0, y: 0, w: 8, h: 3 } } }
        ];
        const result = wd._internal.applyBreakpointToLayout(layout, 'md');
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 8, h: 3 });
    });

    test('sm falls back to base values when only md breakpoint is defined', () => {
        const { wd } = makeEnv();
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 6, h: 2, breakpoints: { md: { x: 0, y: 0, w: 8, h: 3 } } }
        ];
        const result = wd._internal.applyBreakpointToLayout(layout, 'sm');
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 6, h: 2 });
    });

    test('xs preserves h from base when auto-stacking', () => {
        const { wd } = makeEnv();
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 4, h: 3 },
            { id: 'w2', x: 4, y: 0, w: 4, h: 5 }
        ];
        const result = wd._internal.applyBreakpointToLayout(layout, 'xs');
        expect(result[0].h).toBe(3);
        expect(result[1].h).toBe(5);
    });

    test('xs auto-stacking uses accumulated height to prevent overlap', () => {
        // w1 h=3: occupies y[0..2]; w2 must start at y=3
        // w2 h=5: occupies y[3..7]; w3 must start at y=8
        const { wd } = makeEnv();
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 4, h: 3 },
            { id: 'w2', x: 4, y: 0, w: 4, h: 5 },
            { id: 'w3', x: 8, y: 0, w: 4, h: 2 }
        ];
        const result = wd._internal.applyBreakpointToLayout(layout, 'xs');
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 12, h: 3 });
        expect(result[1]).toEqual({ id: 'w2', x: 0, y: 3, w: 12, h: 5 });
        expect(result[2]).toEqual({ id: 'w3', x: 0, y: 8, w: 12, h: 2 });
    });

    test('xs auto-stacking handles missing h (defaults to 1)', () => {
        const { wd } = makeEnv();
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 6 },       // no h → defaults to 1
            { id: 'w2', x: 6, y: 0, w: 6, h: 2 }
        ];
        const result = wd._internal.applyBreakpointToLayout(layout, 'xs');
        expect(result[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 12, h: 1 });
        expect(result[1]).toEqual({ id: 'w2', x: 0, y: 1, w: 12, h: 2 });
    });
});

// ---------------------------------------------------------------------------
// GridManager.setViewportMode / getCurrentBreakpoint
// ---------------------------------------------------------------------------
describe('GridManager viewport mode', () => {
    test('getViewportMode returns null by default', () => {
        const { wd } = makeEnv();
        expect(wd.GridManager.getViewportMode()).toBeNull();
    });

    test('setViewportMode stores the mode', () => {
        const { wd } = makeEnv();
        wd.GridManager.setViewportMode('sm');
        expect(wd.GridManager.getViewportMode()).toBe('sm');
    });

    test('getCurrentBreakpoint returns viewport mode when set', () => {
        const { wd } = makeEnv({ innerWidth: 1400 }); // would be lg
        wd.GridManager.setViewportMode('xs');
        expect(wd.GridManager.getCurrentBreakpoint()).toBe('xs');
    });

    test('getCurrentBreakpoint falls back to detected breakpoint when mode is null', () => {
        const { wd } = makeEnv({ innerWidth: 800 }); // sm
        wd.GridManager.setViewportMode(null);
        expect(wd.GridManager.getCurrentBreakpoint()).toBe('sm');
    });

    test('setViewportMode ignores invalid values', () => {
        const { wd } = makeEnv();
        wd.GridManager.setViewportMode('xxl'); // invalid
        expect(wd.GridManager.getViewportMode()).toBeNull();
    });

    test('loadLayoutForBreakpoint calls grid.load with resolved layout', () => {
        const { wd, mockGridStack } = makeEnv();
        wd.GridManager.init('.grid-stack', {});
        const layout = [
            { id: 'w1', x: 0, y: 0, w: 6, h: 2 },
            { id: 'w2', x: 6, y: 0, w: 6, h: 2 }
        ];
        wd.GridManager.loadLayoutForBreakpoint(layout, 'xs');
        // w1 h=2 → w2 must start at y=2
        expect(mockGridStack.load).toHaveBeenCalledWith([
            { id: 'w1', x: 0, y: 0, w: 12, h: 2 },
            { id: 'w2', x: 0, y: 2, w: 12, h: 2 }
        ]);
    });

    test('loadLayoutForBreakpoint uses detected breakpoint when bp is null', () => {
        const { wd, mockGridStack } = makeEnv({ innerWidth: 1400 }); // lg
        wd.GridManager.init('.grid-stack', {});
        const layout = [{ id: 'w1', x: 0, y: 0, w: 6, h: 2 }];
        wd.GridManager.loadLayoutForBreakpoint(layout, null);
        expect(mockGridStack.load).toHaveBeenCalledWith([
            { id: 'w1', x: 0, y: 0, w: 6, h: 2 }
        ]);
    });
});

// ---------------------------------------------------------------------------
// DashboardEditor.previewViewport
// ---------------------------------------------------------------------------
describe('DashboardEditor.previewViewport', () => {
    async function initWithDashboard(wd, mockFetch, fetchResponses) {
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'dash-r1',
                layout: [
                    { id: 'w1', x: 0, y: 0, w: 6, h: 2 },
                    { id: 'w2', x: 6, y: 0, w: 6, h: 2 }
                ],
                widgets: {},
                refreshInterval: 0
            })
        });
        await wd.DashboardManager.init('container', 'dash-r1');
    }

    test('previewViewport xs sets data-viewport attribute on container', async () => {
        const { wd, mockFetch, fetchResponses, mockDocument } = makeEnv();
        wd.GridManager.init('.grid-stack', {});
        await initWithDashboard(wd, mockFetch, fetchResponses);

        wd.DashboardEditor.previewViewport('xs');

        const el = mockDocument.getElementById.mock.results.find(
            r => r.value && r.value.id === 'container'
        );
        expect(el).toBeDefined();
        expect(el.value.setAttribute).toHaveBeenCalledWith('data-viewport', 'xs');
    });

    test('previewViewport null removes data-viewport attribute', async () => {
        const { wd, mockFetch, fetchResponses, mockDocument } = makeEnv();
        wd.GridManager.init('.grid-stack', {});
        await initWithDashboard(wd, mockFetch, fetchResponses);

        wd.DashboardEditor.previewViewport('sm');
        wd.DashboardEditor.previewViewport(null);

        const calls = mockDocument.getElementById.mock.results.filter(
            r => r.value && r.value.id === 'container'
        );
        const lastEl = calls[calls.length - 1].value;
        expect(lastEl.removeAttribute).toHaveBeenCalledWith('data-viewport');
    });

    test('previewViewport updates GridManager viewport mode', async () => {
        const { wd, mockFetch, fetchResponses } = makeEnv();
        wd.GridManager.init('.grid-stack', {});
        await initWithDashboard(wd, mockFetch, fetchResponses);

        wd.DashboardEditor.previewViewport('md');
        expect(wd.GridManager.getViewportMode()).toBe('md');
    });

    test('previewViewport xs reloads grid with single-column layout', async () => {
        const { wd, mockFetch, fetchResponses, mockGridStack } = makeEnv();
        wd.GridManager.init('.grid-stack', {});
        await initWithDashboard(wd, mockFetch, fetchResponses);

        wd.DashboardEditor.previewViewport('xs');

        const loadCalls = mockGridStack.load.mock.calls;
        const lastCall = loadCalls[loadCalls.length - 1][0];
        // Both widgets should be full-width
        expect(lastCall[0].w).toBe(12);
        expect(lastCall[0].x).toBe(0);
        expect(lastCall[1].w).toBe(12);
        expect(lastCall[1].x).toBe(0);
    });

    test('previewViewport lg restores base layout', async () => {
        const { wd, mockFetch, fetchResponses, mockGridStack } = makeEnv();
        wd.GridManager.init('.grid-stack', {});
        await initWithDashboard(wd, mockFetch, fetchResponses);

        wd.DashboardEditor.previewViewport('xs');
        wd.DashboardEditor.previewViewport('lg');

        const loadCalls = mockGridStack.load.mock.calls;
        const lastCall = loadCalls[loadCalls.length - 1][0];
        expect(lastCall[0]).toEqual({ id: 'w1', x: 0, y: 0, w: 6, h: 2 });
        expect(lastCall[1]).toEqual({ id: 'w2', x: 6, y: 0, w: 6, h: 2 });
    });
});
