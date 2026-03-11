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
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        console: console,
        document: {
            createElement: jest.fn((tag) => ({
                tag,
                className: '',
                textContent: '',
                style: {},
                children: [],
                appendChild: jest.fn(function (c) { this.children.push(c); return c; })
            })),
            getElementById: jest.fn(() => null)
        },
        GridStack: {
            init: jest.fn(() => mockGridStack)
        },
        fetch: mockFetch,
        setInterval: jest.fn(),
        clearInterval: jest.fn(),
        ...overrides,
    });
    freshCtx.window = freshCtx;
    new vm.Script(src).runInContext(freshCtx);

    return {
        wd: freshCtx.WtmDashboard,
        mockGridStack,
        mockFetch,
        fetchResponses,
        ctx: freshCtx
    };
}

describe('WtmDashboard.DashboardEditor', () => {
    test('toggleEditMode flips GridManager edit state', () => {
        const { wd } = makeEnv();
        wd.GridManager.init('.grid-stack', {});

        expect(wd.GridManager.isEditMode()).toBe(false);

        wd.DashboardEditor.toggleEditMode();
        expect(wd.GridManager.isEditMode()).toBe(true);

        wd.DashboardEditor.toggleEditMode();
        expect(wd.GridManager.isEditMode()).toBe(false);
    });

    test('toggleEditMode pauses refresh when entering edit mode', () => {
        const { wd, ctx } = makeEnv();
        wd.GridManager.init('.grid-stack', {});

        // Simulate an active refresh timer
        const stopSpy = jest.spyOn(wd.DashboardManager, 'stopRefresh');

        wd.DashboardEditor.toggleEditMode(); // enter edit mode
        expect(stopSpy).toHaveBeenCalled();
    });

    test('saveDashboard sends PUT with layout and widgets', async () => {
        const { wd, mockGridStack, mockFetch, fetchResponses } = makeEnv();
        wd.GridManager.init('.grid-stack', {});

        // Set up a current dashboard via DashboardManager internal state
        // We simulate by calling init with a mock fetch response
        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'dash-1',
                name: 'Test',
                widgets: {},
                layout: []
            })
        });
        await wd.DashboardManager.init('container', 'dash-1');

        mockGridStack.save.mockReturnValue([{ x: 0, y: 0, w: 3, h: 2, id: 'w1' }]);

        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({ success: true })
        });

        await wd.DashboardEditor.saveDashboard();

        // The last fetch call should be PUT
        const lastCall = mockFetch.mock.calls[mockFetch.mock.calls.length - 1];
        expect(lastCall[0]).toContain('/_dashboard/dash-1');
        expect(lastCall[1].method).toBe('PUT');

        const body = JSON.parse(lastCall[1].body);
        expect(body.layout).toEqual([{ x: 0, y: 0, w: 3, h: 2, id: 'w1' }]);
    });

    test('deleteDashboard sends DELETE request', async () => {
        const { wd, mockFetch, fetchResponses } = makeEnv();
        wd.GridManager.init('.grid-stack', {});

        fetchResponses.push({
            ok: true,
            json: () => Promise.resolve({
                id: 'dash-2',
                name: 'ToDelete',
                widgets: {},
                layout: []
            })
        });
        await wd.DashboardManager.init('container', 'dash-2');

        fetchResponses.push({ ok: true, json: () => Promise.resolve({}) });

        await wd.DashboardEditor.deleteDashboard();

        const lastCall = mockFetch.mock.calls[mockFetch.mock.calls.length - 1];
        expect(lastCall[0]).toContain('/_dashboard/dash-2');
        expect(lastCall[1].method).toBe('DELETE');
    });
});
