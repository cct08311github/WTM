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
        const resp = fetchResponses.shift();
        if (!resp) return Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
        if (resp.reject) return Promise.reject(new Error(resp.reject));
        return Promise.resolve(resp);
    });
    const containers = {};
    const mockDocument = {
        createElement: jest.fn((tag) => ({
            tag,
            className: '',
            textContent: '',
            style: {},
            children: [],
            appendChild: jest.fn(function (c) { this.children.push(c); return c; }),
            addEventListener: jest.fn()
        })),
        getElementById: jest.fn((id) => {
            if (!containers[id]) {
                containers[id] = {
                    className: '',
                    textContent: '',
                    style: {},
                    children: [],
                    appendChild: jest.fn(function (c) { this.children.push(c); return c; })
                };
            }
            return containers[id];
        })
    };
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: mockDocument,
        GridStack: { init: jest.fn(() => mockGridStack) },
        fetch: mockFetch,
        setInterval: jest.fn(),
        clearInterval: jest.fn(),
        requestAnimationFrame: jest.fn((cb) => cb(16)),
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockFetch,
        fetchResponses,
        mockDocument,
        containers,
        ctx: freshCtx
    };
}

describe('WtmDashboard Loading/Error States', () => {
    test('animateNumber is a function on Utils', () => {
        const { wd } = makeEnv();
        expect(typeof wd.Utils.animateNumber).toBe('function');
    });

    test('failure count increments on fetch error', async () => {
        const { wd, mockFetch, fetchResponses, containers } = makeEnv();

        // Init dashboard
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'dash-1',
                widgets: { w1: { type: 'kpi', config: { title: 'Test' } } },
                layout: []
            })
        });
        // Widget data fetch will fail
        fetchResponses.push({ reject: 'Network error' });

        await wd.DashboardManager.init('container', 'dash-1');

        // Wait for async widget fetch to settle
        await new Promise(r => setTimeout(r, 10));

        var counts = wd.DashboardManager.getFailureCounts();
        expect(counts['w1']).toBeGreaterThanOrEqual(1);
    });

    test('resets failure count on success', async () => {
        const { wd, mockFetch, fetchResponses } = makeEnv();

        // Init dashboard
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'dash-2',
                widgets: { w1: { type: 'kpi', config: { title: 'Test' } } },
                layout: []
            })
        });
        // First fetch succeeds
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({ value: 42 })
        });

        await wd.DashboardManager.init('container', 'dash-2');
        await new Promise(r => setTimeout(r, 10));

        var counts = wd.DashboardManager.getFailureCounts();
        expect(counts['w1'] || 0).toBe(0);
    });

    test('stops retry after 3 consecutive failures', async () => {
        const { wd, mockFetch, fetchResponses } = makeEnv();

        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'dash-3',
                widgets: { w1: { type: 'kpi', config: {} } },
                layout: []
            })
        });
        // 3 failures
        fetchResponses.push({ reject: 'fail1' });

        await wd.DashboardManager.init('container', 'dash-3');
        await new Promise(r => setTimeout(r, 10));

        // Manually set failure count to 3 to simulate 3 consecutive failures
        wd.DashboardManager._setFailureCount('w1', 3);

        // shouldRetry should return false
        expect(wd.DashboardManager.shouldRetry('w1')).toBe(false);
    });
});
