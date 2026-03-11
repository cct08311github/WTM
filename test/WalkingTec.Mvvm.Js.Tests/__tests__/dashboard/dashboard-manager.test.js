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
});