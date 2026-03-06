/**
 * Tests for pure functions in framework_analysis.js
 * (detectChartType, validateSelection)
 *
 * framework_analysis.js 透過 vm.Script 載入，寫入 global.wtmAnalysis。
 */

const fs = require('fs');
const path = require('path');
const vm = require('vm');

// ─── Force Jest/V8 instrumentation via require() ──────────────────────────────
// With coverageProvider:'v8', Jest tracks coverage only for functions called in
// the V8 context where they were compiled. Files loaded via vm.Script run in a
// separate V8 context and are invisible to coverage. We require() the source
// here so V8 compiles it in the main context; then we capture a reference to
// the require()'d wtmAnalysis BEFORE the vm.Script tests overwrite global.wtmAnalysis.
// The `waReq` variable below is used in the coverage describe blocks at the bottom.
//
// IMPORTANT: In Jest's jsdom env, `document` in require()'d modules resolves to
// the jsdom window.document — NOT global.document. Use jest.spyOn(document, ...)
// to mock document methods. `fetch`, `alert`, `layui`, `echarts`, `URL` CAN be
// replaced via global.xxx (they are not non-configurable jsdom properties).
global.window = global;
global.fetch = jest.fn(function () {
    return Promise.resolve({ ok: false, text: function () { return Promise.resolve(''); } });
});
global.alert = jest.fn();
global.URL = { createObjectURL: jest.fn(function () { return 'blob:x'; }), revokeObjectURL: jest.fn() };

// Load via require() → V8 compiles under main context → coverage tracked
jest.resetModules();
require('../../../src/WalkingTec.Mvvm.Mvc/framework_analysis.js');
// Capture the require()'d version before vm.Script tests overwrite global.wtmAnalysis
const waReq = global.wtmAnalysis;
let consoleErrorSpy;

beforeAll(() => {
    if (global.window && global.window.HTMLAnchorElement) {
        jest.spyOn(global.window.HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    }
});

afterAll(() => {
    if (global.window && global.window.HTMLAnchorElement &&
        global.window.HTMLAnchorElement.prototype.click &&
        global.window.HTMLAnchorElement.prototype.click.mockRestore) {
        global.window.HTMLAnchorElement.prototype.click.mockRestore();
    }
});

beforeEach(() => {
    consoleErrorSpy = jest.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
    expect(consoleErrorSpy).not.toHaveBeenCalled();
    consoleErrorSpy.mockRestore();
});

// ─── 載入 framework_analysis.js ──────────────────────────────────────────────
const src = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_analysis.js'),
    'utf8'
);

// 使用 vm.createContext，window=global → window.wtmAnalysis = global.wtmAnalysis
const ctx = vm.createContext({
    window: global,
    global: global,
    document: { getElementById: jest.fn(), querySelectorAll: jest.fn(() => []) },
    fetch: jest.fn(),
    alert: jest.fn(),
    console: console,
});
new vm.Script(src).runInContext(ctx);

const wa = global.wtmAnalysis;

// ─── detectChartType ──────────────────────────────────────────────────────────
describe('wtmAnalysis.detectChartType', () => {
    test('無維度 → card', () => {
        expect(wa.detectChartType([], [{ field: 'Amount' }])).toBe('card');
    });

    test('日期維度 → line', () => {
        expect(wa.detectChartType([{ fieldName: 'Date', isDate: true }], [{}])).toBe('line');
    });

    test('單一非日期維度 → bar', () => {
        expect(wa.detectChartType([{ fieldName: 'Region', isDate: false }], [{}])).toBe('bar');
    });

    test('兩個維度 → bar-stacked', () => {
        expect(wa.detectChartType([{ isDate: false }, { isDate: false }], [{}])).toBe('bar-stacked');
    });

    test('三個維度 → bar-stacked', () => {
        expect(wa.detectChartType([{}, {}, {}], [{}])).toBe('bar-stacked');
    });
});

// ─── 每個 DOM 測試需獨立 context（_state 為 module-level） ──────────────────
function makeEnv(overrides) {
    const domNodes = {};
    const mockDocument = {
        getElementById: jest.fn((id) => domNodes[id] || null),
        querySelectorAll: jest.fn(() => []),
        createElement: jest.fn((tag) => ({
            tag,
            style: {},
            className: '',
            textContent: '',
            id: '',
            dataset: {},
            children: [],
            appendChild: jest.fn(function(child) { this.children.push(child); return child; }),
            removeChild: jest.fn(),
            addEventListener: jest.fn(),
            click: jest.fn(),
        })),
        body: {
            appendChild: jest.fn(),
            removeChild: jest.fn(),
        },
    };

    function makePanel(id) {
        const node = {
            id,
            style: { display: 'none' },
            children: [],
            appendChild: jest.fn(function(c) { this.children.push(c); }),
            firstChild: null,
            removeChild: jest.fn(),
        };
        domNodes[id] = node;
        return node;
    }

    const mockFetch = jest.fn();
    const mockAlert = jest.fn();

    const freshSrc = fs.readFileSync(
        path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_analysis.js'),
        'utf8'
    );
    const freshCtx = vm.createContext({
        window: {},
        global: {},
        document: mockDocument,
        fetch: mockFetch,
        alert: mockAlert,
        console,
        URL: { createObjectURL: jest.fn(() => 'blob://test'), revokeObjectURL: jest.fn() },
        ...overrides,
    });
    // wire window = freshCtx.window for wtmAnalysis registration
    freshCtx.window = freshCtx;
    new vm.Script(freshSrc).runInContext(freshCtx);

    return {
        wa: freshCtx.wtmAnalysis,
        mockDocument,
        mockFetch,
        mockAlert,
        makePanel,
        domNodes,
    };
}

// ─── toggle ──────────────────────────────────────────────────────────────────
describe('wtmAnalysis.toggle', () => {
    test('panel 元素不存在 → silent return，不拋例外', () => {
        const { wa } = makeEnv();
        expect(() => wa.toggle('grid1', 'MyVm')).not.toThrow();
    });

    test('panel 存在 → 第一次 toggle 顯示 panel 並呼叫 loadMeta', () => {
        const { wa, makePanel, mockFetch } = makeEnv();
        const panel = makePanel('analysis-panel-grid1');
        mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        wa.toggle('grid1', 'MyVm');

        expect(panel.style.display).toBe('block');
        expect(mockFetch).toHaveBeenCalledWith(
            expect.stringContaining('/_analysis/meta'),
            expect.any(Object)
        );
    });

    test('連續兩次 toggle → 第二次隱藏 panel', () => {
        const { wa, makePanel, mockFetch } = makeEnv();
        const panel = makePanel('analysis-panel-grid2');
        mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        wa.toggle('grid2', 'MyVm');
        wa.toggle('grid2', 'MyVm');

        expect(panel.style.display).toBe('none');
    });
});

// 產生假的已勾選 checkbox mock（供 query/exportData 使用）
function fakeCheckedCbs(gridId) {
    return [
        { dataset: { gridId, kind: 'Dimension', fieldName: 'Region',  displayName: '地區' } },
        { dataset: { gridId, kind: 'Measure',   fieldName: 'Amount',  displayName: '金額', defaultFunc: 'Sum' } },
    ];
}

// ─── query ────────────────────────────────────────────────────────────────────
describe('wtmAnalysis.query', () => {
    test('fetch 失敗 → resultDiv 顯示錯誤訊息', async () => {
        const { wa, makePanel, mockFetch, mockDocument } = makeEnv();
        const panel = makePanel('analysis-panel-grid3');
        const resultDiv = { id: 'analysis-result-grid3', textContent: '', children: [],
            appendChild: jest.fn(), firstChild: null, removeChild: jest.fn() };
        mockDocument.getElementById.mockImplementation((id) => {
            if (id === 'analysis-panel-grid3') return panel;
            if (id === 'analysis-result-grid3') return resultDiv;
            return null;
        });
        // querySelectorAll 回傳有效的 dim+msr checkboxes，讓 validateSelection 通過
        mockDocument.querySelectorAll.mockReturnValue(fakeCheckedCbs('grid3'));
        mockFetch
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') }) // loadMeta
            .mockRejectedValueOnce(new Error('網路錯誤'));                                   // query

        wa.toggle('grid3', 'MyVm');
        wa.query('grid3');

        await new Promise(r => setTimeout(r, 50));

        expect(resultDiv.textContent).toMatch(/查詢失敗/);
    });

    test('result.truncated=true → 顯示截斷警告', async () => {
        const { wa, makePanel, mockFetch, mockDocument } = makeEnv();
        const panel = makePanel('analysis-panel-grid4');
        const appended = [];
        const resultDiv = {
            id: 'analysis-result-grid4', textContent: '', children: [],
            appendChild: jest.fn((c) => appended.push(c)),
            firstChild: null, removeChild: jest.fn(),
        };
        // 同時回傳 panel 和 resultDiv，避免 toggle 找不到 panel 而 early return
        mockDocument.getElementById.mockImplementation((id) => {
            if (id === 'analysis-panel-grid4') return panel;
            if (id === 'analysis-result-grid4') return resultDiv;
            return null;
        });
        mockDocument.querySelectorAll.mockReturnValue(fakeCheckedCbs('grid4'));
        mockFetch
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') }) // loadMeta
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: true, totalCount: 15000,
                    columns: ['Region'], rows: [{ Region: '華東' }]
                })
            });

        wa.toggle('grid4', 'MyVm');
        wa.query('grid4');

        await new Promise(r => setTimeout(r, 50));

        const warningNode = appended.find(c => c.className && c.className.includes('alert-warm'));
        expect(warningNode).toBeDefined();
        expect(warningNode.textContent).toMatch(/截斷/);
    });
});

// ─── exportData ────────────────────────────────────────────────────────────────
describe('wtmAnalysis.exportData', () => {
    test('server 回 400 → alert 顯示 server error body，非 "HTTP 400"', async () => {
        const { wa, makePanel, mockFetch, mockAlert, mockDocument } = makeEnv();
        makePanel('analysis-panel-grid5');
        mockDocument.querySelectorAll.mockReturnValue(fakeCheckedCbs('grid5'));
        mockFetch
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') }) // loadMeta
            .mockResolvedValueOnce({
                ok: false,
                text: jest.fn().mockResolvedValue('最多選取 3 個維度。')
            });

        wa.toggle('grid5', 'MyVm');
        wa.exportData('grid5', 'xlsx');

        await new Promise(r => setTimeout(r, 50));

        expect(mockAlert).toHaveBeenCalledWith(expect.stringContaining('最多選取 3 個維度。'));
        expect(mockAlert).not.toHaveBeenCalledWith(expect.stringContaining('HTTP 400'));
    });
});

// ─── renderTable XSS 安全 ─────────────────────────────────────────────────────
describe('wtmAnalysis.renderTable XSS safety via query', () => {
    test('含 HTML 的欄位值透過 textContent 設值，不產生 innerHTML', async () => {
        const { wa, makePanel, mockFetch, mockDocument } = makeEnv();
        const panel = makePanel('analysis-panel-grid6');
        const createdElements = [];
        mockDocument.createElement.mockImplementation((tag) => {
            const el = {
                tag, style: {}, className: '', textContent: '', id: '', dataset: {},
                type: '', children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                removeChild: jest.fn(),
                addEventListener: jest.fn(),
                click: jest.fn(),
                href: '', download: '',
            };
            createdElements.push(el);
            return el;
        });
        const resultDiv = {
            id: 'analysis-result-grid6', textContent: '', children: [],
            appendChild: jest.fn(function(c) { this.children.push(c); }),
            firstChild: null, removeChild: jest.fn(),
        };
        mockDocument.getElementById.mockImplementation((id) => {
            if (id === 'analysis-panel-grid6') return panel;
            if (id === 'analysis-result-grid6') return resultDiv;
            return null;
        });
        mockDocument.querySelectorAll.mockReturnValue(fakeCheckedCbs('grid6'));
        const xssPayload = '<script>alert("xss")</script>';
        mockFetch
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: false, totalCount: 1,
                    columns: ['Region'],
                    rows: [{ Region: xssPayload }]
                })
            });

        wa.toggle('grid6', 'MyVm');
        wa.query('grid6');

        await new Promise(r => setTimeout(r, 50));

        // 所有 td 元素的 textContent 應為原始字串，不應有任何 innerHTML 設值
        const tds = createdElements.filter(e => e.tag === 'td');
        expect(tds.length).toBeGreaterThan(0);
        const xssTd = tds.find(td => td.textContent === xssPayload);
        expect(xssTd).toBeDefined();
        // 確認沒有元素設置過 innerHTML（屬性不存在 = 安全）
        tds.forEach(td => expect(td.innerHTML).toBeUndefined());
    });
});

// ─── validateSelection ────────────────────────────────────────────────────────
describe('wtmAnalysis.validateSelection', () => {
    test('空維度空度量 → 一個錯誤', () => {
        expect(wa.validateSelection([], [])).toHaveLength(1);
        expect(wa.validateSelection([], [])[0]).toMatch(/至少選擇/);
    });

    test('4 個維度 → 錯誤訊息包含「維度最多選 3 個」', () => {
        const result = wa.validateSelection([1, 2, 3, 4], [1]);
        expect(result).toContain('維度最多選 3 個');
    });

    test('4 個度量 → 錯誤訊息包含「度量最多選 3 個」', () => {
        const result = wa.validateSelection([1], [1, 2, 3, 4]);
        expect(result).toContain('度量最多選 3 個');
    });

    test('合法選取 → 無錯誤', () => {
        expect(wa.validateSelection(['Region', 'Category'], [{ field: 'Amount' }])).toHaveLength(0);
    });

    test('剛好 3 維度 3 度量 → 無錯誤', () => {
        expect(wa.validateSelection([1, 2, 3], [1, 2, 3])).toHaveLength(0);
    });

    test('1 dim 0 msrs → valid (returns empty errors)', () => {
        expect(wa.validateSelection(['Region'], [])).toHaveLength(0);
    });
});

// ─── collectSelection ─────────────────────────────────────────────────────────
describe('wtmAnalysis.collectSelection', () => {
    test('returns empty dims/msrs when no checkboxes checked', () => {
        const { wa, mockDocument } = makeEnv();
        mockDocument.querySelectorAll.mockReturnValue([]);
        const result = wa.collectSelection('grid1');
        expect(result.dims).toEqual([]);
        expect(result.msrs).toEqual([]);
    });

    test('checked Dimension checkbox added to dims', () => {
        const { wa, mockDocument } = makeEnv();
        const cb = { dataset: { kind: 'Dimension', fieldName: 'Region', gridId: 'grid1' }, nextElementSibling: null };
        mockDocument.querySelectorAll.mockReturnValue([cb]);
        const result = wa.collectSelection('grid1');
        expect(result.dims).toEqual(['Region']);
        expect(result.msrs).toEqual([]);
    });

    test('checked Measure with no select uses dataset.defaultFunc', () => {
        const { wa, mockDocument } = makeEnv();
        const cb = { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'grid1', defaultFunc: 'Max' }, nextElementSibling: null };
        mockDocument.querySelectorAll.mockReturnValue([cb]);
        const result = wa.collectSelection('grid1');
        expect(result.msrs).toEqual([{ field: 'Amount', func: 'Max' }]);
    });

    test('checked Measure falls back to Sum when no defaultFunc and no select', () => {
        const { wa, mockDocument } = makeEnv();
        const cb = { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'grid1' }, nextElementSibling: null };
        mockDocument.querySelectorAll.mockReturnValue([cb]);
        const result = wa.collectSelection('grid1');
        expect(result.msrs).toEqual([{ field: 'Amount', func: 'Sum' }]);
    });

    test('checked Measure uses select.value when nextElementSibling is SELECT', () => {
        const { wa, mockDocument } = makeEnv();
        const sel = { tagName: 'SELECT', value: 'Avg' };
        const cb = { dataset: { kind: 'Measure', fieldName: 'Revenue', gridId: 'grid1' }, nextElementSibling: sel };
        mockDocument.querySelectorAll.mockReturnValue([cb]);
        const result = wa.collectSelection('grid1');
        expect(result.msrs).toEqual([{ field: 'Revenue', func: 'Avg' }]);
    });

    test('mixed dimension and measure', () => {
        const { wa, mockDocument } = makeEnv();
        const cb1 = { dataset: { kind: 'Dimension', fieldName: 'Region', gridId: 'grid1' }, nextElementSibling: null };
        const cb2 = { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'grid1', defaultFunc: 'Sum' }, nextElementSibling: null };
        mockDocument.querySelectorAll.mockReturnValue([cb1, cb2]);
        const result = wa.collectSelection('grid1');
        expect(result.dims).toEqual(['Region']);
        expect(result.msrs).toEqual([{ field: 'Amount', func: 'Sum' }]);
    });

    test('nextElementSibling that is not SELECT is ignored', () => {
        const { wa, mockDocument } = makeEnv();
        const span = { tagName: 'SPAN', value: 'ignored' };
        const cb = { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'grid1', defaultFunc: 'Min' }, nextElementSibling: span };
        mockDocument.querySelectorAll.mockReturnValue([cb]);
        const result = wa.collectSelection('grid1');
        expect(result.msrs).toEqual([{ field: 'Amount', func: 'Min' }]);
    });
});

// ─── exportData loading state ─────────────────────────────────────────────────
describe('exportData — layui loading state', () => {
    test('layer.load(2) called before fetch, layer.close called on success', async () => {
        const exportCallOrder = [];
        const layerLoad = jest.fn(() => { exportCallOrder.push('load'); return 55; });
        const layerClose = jest.fn(() => { exportCallOrder.push('close'); });
        // Use a two-call fetch: first for loadMeta (toggle), second for exportData
        const fetchMock = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') }) // loadMeta
            .mockImplementationOnce(() => { exportCallOrder.push('fetch'); return Promise.resolve({ ok: true, blob: () => Promise.resolve(new Blob()) }); }); // exportData
        const { wa } = makeEnv({
            fetch: fetchMock,
            layui: { layer: { load: layerLoad, close: layerClose } },
            URL: { createObjectURL: jest.fn(() => 'blob:url'), revokeObjectURL: jest.fn() },
            document: {
                getElementById: jest.fn((id) => id === 'analysis-panel-gridX' ? { style: {}, appendChild: jest.fn(), removeChild: jest.fn(), firstChild: null } : null),
                querySelectorAll: jest.fn(() => []),
                createElement: jest.fn((tag) => ({ tag, style: {}, href: '', download: '', click: jest.fn(), children: [], textContent: '', className: '', appendChild: jest.fn(function(c){ this.children.push(c); }), removeChild: jest.fn() })),
                createTextNode: jest.fn((t) => t),
                body: { appendChild: jest.fn(), removeChild: jest.fn() },
            },
        });
        wa.toggle('gridX', 'MyVm');
        // Wait for loadMeta fetch to settle before exportData so toggle doesn't pollute callOrder
        await new Promise(r => setTimeout(r, 10));
        await wa.exportData('gridX', 'xlsx');

        expect(layerLoad).toHaveBeenCalledWith(2);
        expect(layerClose).toHaveBeenCalledWith(55);
        // load must be called before fetch (within exportData's own operations)
        expect(exportCallOrder[0]).toBe('load');
        expect(exportCallOrder[1]).toBe('fetch');
        expect(exportCallOrder[2]).toBe('close');
    });

    test('layer.close called on fetch error', async () => {
        const layerLoad = jest.fn().mockReturnValue(77);
        const layerClose = jest.fn();
        const alertMock = jest.fn();
        const { wa } = makeEnv({
            fetch: jest.fn().mockRejectedValue(new Error('network down')),
            layui: { layer: { load: layerLoad, close: layerClose } },
            alert: alertMock,
            document: {
                getElementById: jest.fn((id) => id === 'analysis-panel-gridX' ? { style: {}, appendChild: jest.fn(), removeChild: jest.fn(), firstChild: null } : null),
                querySelectorAll: jest.fn(() => []),
                createElement: jest.fn((tag) => ({ tag, style: {}, children: [], textContent: '', className: '', appendChild: jest.fn(function(c){ this.children.push(c); }), removeChild: jest.fn() })),
                createTextNode: jest.fn((t) => t),
                body: { appendChild: jest.fn(), removeChild: jest.fn() },
            },
        });
        wa.toggle('gridX', 'MyVm');
        await wa.exportData('gridX', 'xlsx');

        expect(layerLoad).toHaveBeenCalledWith(2);
        expect(layerClose).toHaveBeenCalledWith(77);
        expect(alertMock).toHaveBeenCalledWith(expect.stringContaining('network down'));
    });

    test('no crash when layui is undefined', async () => {
        const { wa } = makeEnv({
            fetch: jest.fn()
                .mockResolvedValueOnce({ ok: false, text: async () => 'meta error' })
                .mockResolvedValueOnce({ ok: true, blob: async () => new Blob(['data']) }),
            URL: { createObjectURL: jest.fn(() => 'blob:url'), revokeObjectURL: jest.fn() },
            document: {
                getElementById: jest.fn((id) => id === 'analysis-panel-gridX' ? { style: {}, appendChild: jest.fn(), removeChild: jest.fn(), firstChild: null } : null),
                querySelectorAll: jest.fn(() => []),
                createElement: jest.fn((tag) => ({ tag, style: {}, href: '', download: '', click: jest.fn(), children: [], textContent: '', className: '', appendChild: jest.fn(function(c){ this.children.push(c); }), removeChild: jest.fn() })),
                createTextNode: jest.fn((t) => t),
                body: { appendChild: jest.fn(), removeChild: jest.fn() },
            },
            // no layui property — simulates page without layui loaded
        });
        wa.toggle('gridX', 'MyVm');
        // Should complete without throwing (layui absent is gracefully handled)
        await wa.exportData('gridX', 'xlsx');
    });
});

// ─── parseFuncs ───────────────────────────────────────────────────────────────
describe('wtmAnalysis.parseFuncs', () => {
    test('flags=0 returns empty array', () => {
        expect(wa.parseFuncs(0)).toEqual([]);
    });
    test('flags=1 returns [Count]', () => {
        expect(wa.parseFuncs(1)).toEqual(['Count']);
    });
    test('flags=2 returns [Sum]', () => {
        expect(wa.parseFuncs(2)).toEqual(['Sum']);
    });
    test('flags=4 returns [Avg]', () => {
        expect(wa.parseFuncs(4)).toEqual(['Avg']);
    });
    test('flags=8 returns [Max]', () => {
        expect(wa.parseFuncs(8)).toEqual(['Max']);
    });
    test('flags=16 returns [Min]', () => {
        expect(wa.parseFuncs(16)).toEqual(['Min']);
    });
    test('flags=6 returns [Sum, Avg]', () => {
        expect(wa.parseFuncs(6)).toEqual(['Sum', 'Avg']);
    });
    test('flags=30 returns [Sum, Avg, Max, Min]', () => {
        expect(wa.parseFuncs(30)).toEqual(['Sum', 'Avg', 'Max', 'Min']);
    });
    test('flags=31 returns all 5 funcs in order', () => {
        expect(wa.parseFuncs(31)).toEqual(['Count', 'Sum', 'Avg', 'Max', 'Min']);
    });
    test('flags=NaN returns empty array (bitwise & NaN = 0)', () => {
        expect(wa.parseFuncs(NaN)).toEqual([]);
    });
    test('flags=undefined treated as 0, returns empty', () => {
        expect(wa.parseFuncs(undefined)).toEqual([]);
    });
});

// ─── renderChart — forceChartType ─────────────────────────────────────────────
describe('renderChart — forceChartType parameter', () => {
    function makeChartEnv(forcedType) {
        const capturedOptions = [];
        const { wa } = makeEnv({
            echarts: {
                init: jest.fn(() => ({
                    setOption: jest.fn((opt) => { capturedOptions.push(opt); }),
                    dispose: jest.fn(),
                })),
            },
        });
        return { wa, capturedOptions };
    }

    function makeContainer() {
        const children = [];
        return {
            appendChild: jest.fn(function(c) { children.push(c); }),
            children,
            style: {},
            id: '',
        };
    }

    const sampleResult = {
        columns: ['Region', 'Amount_Sum'],
        rows: [{ Region: 'North', Amount_Sum: 100 }],
    };
    const sampleReq = { dimensions: ['Region'], measures: [{ field: 'Amount', func: 'Sum' }] };
    const sampleDimFields = [{ fieldName: 'Region', isDate: false }];

    test('forceChartType=line overrides detectChartType (which would return bar)', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container, 'line');
        expect(capturedOptions.length).toBe(1);
        expect(capturedOptions[0].series[0].type).toBe('line');
    });

    test('forceChartType=bar uses bar type', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container, 'bar');
        expect(capturedOptions[0].series[0].type).toBe('bar');
        expect(capturedOptions[0].series[0].stack).toBeUndefined();
    });

    test('forceChartType=bar-stacked sets stack=total', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container, 'bar-stacked');
        expect(capturedOptions[0].series[0].type).toBe('bar');
        expect(capturedOptions[0].series[0].stack).toBe('total');
    });

    test('no forceChartType uses detectChartType result', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        // sampleDimFields has isDate:false, single dim → detectChartType returns 'bar'
        wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container);
        expect(capturedOptions[0].series[0].type).toBe('bar');
    });

    test('renderChart skips when echarts not available', () => {
        const { wa } = makeEnv({ /* no echarts */ });
        const container = makeContainer();
        // should not throw
        expect(() => wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container, 'bar')).not.toThrow();
    });
});

// ─── Coverage-oriented tests using require()'d module (waReq) ─────────────────
// These tests call waReq.* functions directly so V8 coverage tracks them.
// The vm.Script tests above provide state isolation; these provide line coverage.
//
// KEY: Use jest.spyOn(document, 'getElementById') etc. because the free-variable
// `document` in the require()'d module resolves to jsdom window.document.
// Replacing global.document does NOT affect it. But `fetch`, `alert`, `layui`,
// `echarts`, `URL` can be set/replaced via global.xxx (they are writable globals).

// Helper: active spies and originals to restore in afterEach
let _idSpy = null;
let _qsaSpy = null;

function spySetup(idImpl, qsaImpl) {
    if (_idSpy) { _idSpy.mockRestore(); }
    if (_qsaSpy) { _qsaSpy.mockRestore(); }
    _idSpy = jest.spyOn(document, 'getElementById').mockImplementation(idImpl || function() { return null; });
    _qsaSpy = jest.spyOn(document, 'querySelectorAll').mockImplementation(qsaImpl || function() { return []; });
}

function spyTeardown() {
    if (_idSpy) { _idSpy.mockRestore(); _idSpy = null; }
    if (_qsaSpy) { _qsaSpy.mockRestore(); _qsaSpy = null; }
}

// ─── [cov] Pure functions ─────────────────────────────────────────────────────
describe('[cov] waReq pure functions — detectChartType / validateSelection / parseFuncs', () => {
    test('detectChartType: no dims → card', () => {
        expect(waReq.detectChartType([], [{}])).toBe('card');
    });
    test('detectChartType: date dim → line', () => {
        expect(waReq.detectChartType([{ isDate: true }], [{}])).toBe('line');
    });
    test('detectChartType: single non-date dim → bar', () => {
        expect(waReq.detectChartType([{ isDate: false }], [{}])).toBe('bar');
    });
    test('detectChartType: 2 dims → bar-stacked', () => {
        expect(waReq.detectChartType([{}, {}], [{}])).toBe('bar-stacked');
    });

    test('validateSelection: empty → error', () => {
        expect(waReq.validateSelection([], [])).toHaveLength(1);
    });
    test('validateSelection: too many dims → error', () => {
        expect(waReq.validateSelection([1,2,3,4], [1])).toContain('維度最多選 3 個');
    });
    test('validateSelection: too many msrs → error', () => {
        expect(waReq.validateSelection([1], [1,2,3,4])).toContain('度量最多選 3 個');
    });
    test('validateSelection: valid → no errors', () => {
        expect(waReq.validateSelection(['R'], [{ field: 'A' }])).toHaveLength(0);
    });

    test('parseFuncs(0) → []', () => { expect(waReq.parseFuncs(0)).toEqual([]); });
    test('parseFuncs(1) → [Count]', () => { expect(waReq.parseFuncs(1)).toEqual(['Count']); });
    test('parseFuncs(2) → [Sum]', () => { expect(waReq.parseFuncs(2)).toEqual(['Sum']); });
    test('parseFuncs(4) → [Avg]', () => { expect(waReq.parseFuncs(4)).toEqual(['Avg']); });
    test('parseFuncs(8) → [Max]', () => { expect(waReq.parseFuncs(8)).toEqual(['Max']); });
    test('parseFuncs(16) → [Min]', () => { expect(waReq.parseFuncs(16)).toEqual(['Min']); });
    test('parseFuncs(31) → all 5', () => {
        expect(waReq.parseFuncs(31)).toEqual(['Count', 'Sum', 'Avg', 'Max', 'Min']);
    });
    test('parseFuncs(-1) → all 5 (all bits set in int32)', () => {
        expect(waReq.parseFuncs(-1)).toEqual(['Count', 'Sum', 'Avg', 'Max', 'Min']);
    });
    test('parseFuncs(NaN) → []', () => { expect(waReq.parseFuncs(NaN)).toEqual([]); });
    test('parseFuncs(undefined) → []', () => { expect(waReq.parseFuncs(undefined)).toEqual([]); });
});

// ─── [cov] collectSelection ────────────────────────────────────────────────────
describe('[cov] waReq.collectSelection via jest.spyOn', () => {
    afterEach(spyTeardown);

    test('no checked checkboxes → empty dims/msrs', () => {
        spySetup(null, function() { return []; });
        const result = waReq.collectSelection('scg1');
        expect(result.dims).toEqual([]);
        expect(result.msrs).toEqual([]);
    });
    test('Dimension checkbox → added to dims', () => {
        const cb = { dataset: { kind: 'Dimension', fieldName: 'Region' }, nextElementSibling: null };
        spySetup(null, function() { return [cb]; });
        const result = waReq.collectSelection('scg1');
        expect(result.dims).toEqual(['Region']);
    });
    test('Measure with defaultFunc → uses defaultFunc', () => {
        const cb = { dataset: { kind: 'Measure', fieldName: 'Amt', defaultFunc: 'Max' }, nextElementSibling: null };
        spySetup(null, function() { return [cb]; });
        const result = waReq.collectSelection('scg1');
        expect(result.msrs).toEqual([{ field: 'Amt', func: 'Max' }]);
    });
    test('Measure with no defaultFunc → falls back to Sum', () => {
        const cb = { dataset: { kind: 'Measure', fieldName: 'Amt' }, nextElementSibling: null };
        spySetup(null, function() { return [cb]; });
        const result = waReq.collectSelection('scg1');
        expect(result.msrs).toEqual([{ field: 'Amt', func: 'Sum' }]);
    });
    test('Measure with SELECT sibling → uses select.value', () => {
        const sel = { tagName: 'SELECT', value: 'Avg' };
        const cb = { dataset: { kind: 'Measure', fieldName: 'Rev' }, nextElementSibling: sel };
        spySetup(null, function() { return [cb]; });
        const result = waReq.collectSelection('scg1');
        expect(result.msrs).toEqual([{ field: 'Rev', func: 'Avg' }]);
    });
    test('Measure with non-SELECT sibling → uses defaultFunc', () => {
        const span = { tagName: 'SPAN', value: 'ignore' };
        const cb = { dataset: { kind: 'Measure', fieldName: 'Rev', defaultFunc: 'Min' }, nextElementSibling: span };
        spySetup(null, function() { return [cb]; });
        const result = waReq.collectSelection('scg1');
        expect(result.msrs).toEqual([{ field: 'Rev', func: 'Min' }]);
    });
});

// ─── [cov] toggle ─────────────────────────────────────────────────────────────
describe('[cov] waReq.toggle — state machine branches', () => {
    let _fetchOrig;
    beforeEach(() => { _fetchOrig = global.fetch; });
    afterEach(() => { spyTeardown(); global.fetch = _fetchOrig; });

    test('panel not found → silent return', () => {
        spySetup(function() { return null; });
        expect(() => waReq.toggle('scovMiss', 'VM')).not.toThrow();
    });

    test('panel found → first toggle shows panel + calls fetch for meta', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovG';
        global.fetch = jest.fn().mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });
        spySetup(function(id) { return id === 'analysis-panel-scovG' ? panel : null; });
        waReq.toggle('scovG', 'MyVm');
        expect(panel.style.display).toBe('block');
        expect(global.fetch).toHaveBeenCalledWith(expect.stringContaining('/_analysis/meta'), expect.any(Object));
        await new Promise(r => setTimeout(r, 20));
    });

    test('second toggle hides panel', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovH';
        global.fetch = jest.fn().mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });
        spySetup(function(id) { return id === 'analysis-panel-scovH' ? panel : null; });
        waReq.toggle('scovH', 'Vm2');
        waReq.toggle('scovH', 'Vm2');
        expect(panel.style.display).toBe('none');
    });

    test('toggle with cached fields skips loadMeta on second open', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovI';
        const fetchMock = jest.fn().mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue([
                { kind: 'Dimension', fieldName: 'X', displayName: 'X', isDate: false, allowedFuncs: 0 }
            ])
        });
        global.fetch = fetchMock;
        spySetup(function(id) { return id === 'analysis-panel-scovI' ? panel : null; });
        waReq.toggle('scovI', 'VmI');         // open → loadMeta
        await new Promise(r => setTimeout(r, 40));
        waReq.toggle('scovI', 'VmI');         // close
        const callsBefore = fetchMock.mock.calls.length;
        waReq.toggle('scovI', 'VmI');         // open again → fields cached → no fetch
        await new Promise(r => setTimeout(r, 10));
        expect(fetchMock.mock.calls.length).toBe(callsBefore);
    });
});

// ─── [cov] loadMeta HTTP error ────────────────────────────────────────────────
describe('[cov] waReq.loadMeta — HTTP error branches', () => {
    let _fetchOrig;
    beforeEach(() => { _fetchOrig = global.fetch; });
    afterEach(() => { spyTeardown(); global.fetch = _fetchOrig; });

    test('loadMeta 4xx with body → appends error div to panel', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovJ';
        global.fetch = jest.fn().mockResolvedValue({
            ok: false,
            text: jest.fn().mockResolvedValue('Forbidden')
        });
        spySetup(function(id) { return id === 'analysis-panel-scovJ' ? panel : null; });
        waReq.toggle('scovJ', 'VmJ');
        await new Promise(r => setTimeout(r, 40));
        expect(panel.textContent).toMatch(/Forbidden/);
    });

    test('loadMeta 5xx with empty body → uses "HTTP N" fallback', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovK';
        global.fetch = jest.fn().mockResolvedValue({
            ok: false,
            status: 503,
            text: jest.fn().mockResolvedValue('')
        });
        spySetup(function(id) { return id === 'analysis-panel-scovK' ? panel : null; });
        waReq.toggle('scovK', 'VmK');
        await new Promise(r => setTimeout(r, 40));
        expect(panel.textContent).toMatch(/HTTP 503/);
    });
});

// ─── [cov] query branches ─────────────────────────────────────────────────────
describe('[cov] waReq.query — branches', () => {
    let _fetchOrig, _alertOrig;
    beforeEach(() => { _fetchOrig = global.fetch; _alertOrig = global.alert; });
    afterEach(() => { spyTeardown(); global.fetch = _fetchOrig; global.alert = _alertOrig; });

    test('query with no _state → silent return', () => {
        expect(() => waReq.query('scovQNone_never_used_grid')).not.toThrow();
    });

    test('query with empty selection → alert validation error', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovQ1';
        global.fetch = jest.fn().mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });
        const alertMock = jest.fn();
        global.alert = alertMock;
        spySetup(function(id) { return id === 'analysis-panel-scovQ1' ? panel : null; }, function() { return []; });
        waReq.toggle('scovQ1', 'VmQ');
        await new Promise(r => setTimeout(r, 20));
        waReq.query('scovQ1');
        expect(alertMock).toHaveBeenCalled();
    });

    test('query success, non-truncated → renders table', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovQ2';
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-scovQ2';
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: false, totalCount: 1,
                    columns: ['Region', 'Amount_Sum'],
                    rows: [{ Region: 'East', Amount_Sum: 500 }]
                })
            });
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'Region', gridId: 'scovQ2' }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'scovQ2', defaultFunc: 'Sum' }, nextElementSibling: null };
        spySetup(
            function(id) {
                if (id === 'analysis-panel-scovQ2') return panel;
                if (id === 'analysis-result-scovQ2') return resultDiv;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle('scovQ2', 'VmQ2');
        await new Promise(r => setTimeout(r, 20));
        waReq.query('scovQ2');
        await new Promise(r => setTimeout(r, 40));
        const table = resultDiv.querySelector('table');
        expect(table).toBeTruthy();
    });

    test('query truncated=true → shows truncation warning', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovQ3';
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-scovQ3';
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: true, totalCount: 20000,
                    columns: ['R'], rows: [{ R: 'x' }]
                })
            });
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'R', gridId: 'scovQ3' }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'A', gridId: 'scovQ3', defaultFunc: 'Sum' }, nextElementSibling: null };
        spySetup(
            function(id) {
                if (id === 'analysis-panel-scovQ3') return panel;
                if (id === 'analysis-result-scovQ3') return resultDiv;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle('scovQ3', 'VmQ3');
        await new Promise(r => setTimeout(r, 20));
        waReq.query('scovQ3');
        await new Promise(r => setTimeout(r, 40));
        expect(resultDiv.innerHTML).toMatch(/截斷/);
    });

    test('query toggleRow → sets display=block after success', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovQ4';
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-scovQ4';
        const toggleRow = document.createElement('div');
        toggleRow.id = 'analysis-chart-toggle-scovQ4';
        toggleRow.style.display = 'none';
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: false, totalCount: 1,
                    columns: ['R'], rows: [{ R: 'x' }]
                })
            });
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'R', gridId: 'scovQ4' }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'A', gridId: 'scovQ4', defaultFunc: 'Sum' }, nextElementSibling: null };
        spySetup(
            function(id) {
                if (id === 'analysis-panel-scovQ4') return panel;
                if (id === 'analysis-result-scovQ4') return resultDiv;
                if (id === 'analysis-chart-toggle-scovQ4') return toggleRow;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle('scovQ4', 'VmQ4');
        await new Promise(r => setTimeout(r, 20));
        waReq.query('scovQ4');
        await new Promise(r => setTimeout(r, 40));
        expect(toggleRow.style.display).toBe('block');
    });

    test('renderTable with null/undefined cells → empty string, no crash', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovQ5';
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-scovQ5';
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: false, totalCount: 1,
                    columns: ['A', 'B', 'C'],
                    rows: [{ A: null, B: undefined, C: 0 }]
                })
            });
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'A', gridId: 'scovQ5' }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'B', gridId: 'scovQ5', defaultFunc: 'Sum' }, nextElementSibling: null };
        spySetup(
            function(id) {
                if (id === 'analysis-panel-scovQ5') return panel;
                if (id === 'analysis-result-scovQ5') return resultDiv;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle('scovQ5', 'VmQ5');
        await new Promise(r => setTimeout(r, 20));
        waReq.query('scovQ5');
        await new Promise(r => setTimeout(r, 40));
        // C=0 should appear; null/undefined cells → empty, no crash
        expect(resultDiv.innerHTML).toMatch(/0/);
    });

    test('query fetch error → resultDiv shows error message', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovQ6';
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-scovQ6';
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockRejectedValueOnce(new Error('Network down'));
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'R', gridId: 'scovQ6' }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'A', gridId: 'scovQ6', defaultFunc: 'Sum' }, nextElementSibling: null };
        spySetup(
            function(id) {
                if (id === 'analysis-panel-scovQ6') return panel;
                if (id === 'analysis-result-scovQ6') return resultDiv;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle('scovQ6', 'VmQ6');
        await new Promise(r => setTimeout(r, 20));
        waReq.query('scovQ6');
        await new Promise(r => setTimeout(r, 40));
        expect(resultDiv.textContent).toMatch(/查詢失敗/);
    });
});

// ─── [cov] exportData branches ────────────────────────────────────────────────
describe('[cov] waReq.exportData — branches', () => {
    let _fetchOrig, _alertOrig, _URLOrig, _layuiOrig;
    beforeEach(() => {
        _fetchOrig = global.fetch;
        _alertOrig = global.alert;
        _URLOrig = global.URL;
        _layuiOrig = global.layui;
    });
    afterEach(() => {
        spyTeardown();
        global.fetch = _fetchOrig;
        global.alert = _alertOrig;
        global.URL = _URLOrig;
        global.layui = _layuiOrig;
    });

    test('exportData with no _state → silent return', () => {
        expect(() => waReq.exportData('scovENone_never_used_grid', 'xlsx')).not.toThrow();
    });

    test('exportData success → blob downloaded (createObjectURL + revokeObjectURL called)', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovE1';
        global.layui = undefined; // disable layui path to avoid setup.js layui.layer.load missing
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({ ok: true, blob: jest.fn().mockResolvedValue(new Blob(['x'])) });
        global.URL = { createObjectURL: jest.fn().mockReturnValue('blob:e1'), revokeObjectURL: jest.fn() };
        spySetup(function(id) { return id === 'analysis-panel-scovE1' ? panel : null; });
        waReq.toggle('scovE1', 'VmE1');
        await new Promise(r => setTimeout(r, 20));
        await waReq.exportData('scovE1', 'xlsx');
        expect(global.URL.createObjectURL).toHaveBeenCalled();
        expect(global.URL.revokeObjectURL).toHaveBeenCalled();
    });

    test('exportData 4xx with body → alert with server message', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovE2';
        global.layui = undefined; // disable layui path
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('Dimension limit exceeded') });
        const alertMock = jest.fn();
        global.alert = alertMock;
        spySetup(function(id) { return id === 'analysis-panel-scovE2' ? panel : null; });
        waReq.toggle('scovE2', 'VmE2');
        await new Promise(r => setTimeout(r, 20));
        await waReq.exportData('scovE2', 'xlsx');
        expect(alertMock).toHaveBeenCalledWith(expect.stringContaining('Dimension limit exceeded'));
    });

    test('exportData 5xx with empty body → alert with HTTP status', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovE3';
        global.layui = undefined; // disable layui path
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({ ok: false, status: 500, text: jest.fn().mockResolvedValue('') });
        const alertMock = jest.fn();
        global.alert = alertMock;
        spySetup(function(id) { return id === 'analysis-panel-scovE3' ? panel : null; });
        waReq.toggle('scovE3', 'VmE3');
        await new Promise(r => setTimeout(r, 20));
        await waReq.exportData('scovE3', 'xlsx');
        expect(alertMock).toHaveBeenCalledWith(expect.stringContaining('HTTP 500'));
    });

    test('exportData with layui → layer.load before fetch, layer.close on success', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovE4';
        const layerLoad = jest.fn().mockReturnValue(42);
        const layerClose = jest.fn();
        global.layui = { layer: { load: layerLoad, close: layerClose } };
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({ ok: true, blob: jest.fn().mockResolvedValue(new Blob(['y'])) });
        global.URL = { createObjectURL: jest.fn().mockReturnValue('blob:e4'), revokeObjectURL: jest.fn() };
        spySetup(function(id) { return id === 'analysis-panel-scovE4' ? panel : null; });
        waReq.toggle('scovE4', 'VmE4');
        await new Promise(r => setTimeout(r, 20));
        await waReq.exportData('scovE4', 'xlsx');
        expect(layerLoad).toHaveBeenCalledWith(2);
        expect(layerClose).toHaveBeenCalledWith(42);
    });

    test('exportData with layui → layer.close on fetch error', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovE5';
        const layerLoad = jest.fn().mockReturnValue(99);
        const layerClose = jest.fn();
        const alertMock = jest.fn();
        global.layui = { layer: { load: layerLoad, close: layerClose } };
        global.alert = alertMock;
        global.fetch = jest.fn()
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockRejectedValueOnce(new Error('net error'));
        spySetup(function(id) { return id === 'analysis-panel-scovE5' ? panel : null; });
        waReq.toggle('scovE5', 'VmE5');
        await new Promise(r => setTimeout(r, 20));
        await waReq.exportData('scovE5', 'xlsx');
        expect(layerClose).toHaveBeenCalledWith(99);
        expect(alertMock).toHaveBeenCalledWith(expect.stringContaining('net error'));
    });
});

// ─── [cov] renderChart branches ───────────────────────────────────────────────
describe('[cov] waReq.renderChart — all branches', () => {
    let _echartsOrig;
    beforeEach(() => { _echartsOrig = global.echarts; });
    afterEach(() => { global.echarts = _echartsOrig; });

    const sampleResult = {
        columns: ['Region', 'Amount_Sum'],
        rows: [{ Region: 'North', Amount_Sum: 100 }],
    };
    const sampleReq = { dimensions: ['Region'], measures: [{ field: 'Amount', func: 'Sum' }] };
    const sampleDimFields = [{ fieldName: 'Region', isDate: false }];

    function makeContainer() { return document.createElement('div'); }

    test('renderChart skips when window.echarts is undefined', () => {
        global.echarts = undefined;
        expect(() => waReq.renderChart('scovR1', sampleResult, sampleReq, sampleDimFields, makeContainer())).not.toThrow();
    });

    test('renderChart with echarts → calls init and setOption', () => {
        const setOption = jest.fn();
        global.echarts = { init: jest.fn(function() { return { setOption, dispose: jest.fn() }; }) };
        waReq.renderChart('scovR2', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'bar');
        expect(global.echarts.init).toHaveBeenCalled();
        expect(setOption).toHaveBeenCalled();
    });

    test('forceChartType=line → series[0].type=line', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), dispose: jest.fn() }; }) };
        waReq.renderChart('scovR3', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'line');
        expect(opts[0].series[0].type).toBe('line');
    });

    test('forceChartType=bar-stacked → stack=total', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), dispose: jest.fn() }; }) };
        waReq.renderChart('scovR4', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'bar-stacked');
        expect(opts[0].series[0].stack).toBe('total');
    });

    test('date dim (no force) → detectChartType → line', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), dispose: jest.fn() }; }) };
        const dateDim = [{ fieldName: 'Date', isDate: true }];
        const dateReq = { dimensions: ['Date'], measures: [{ field: 'Amount', func: 'Sum' }] };
        waReq.renderChart('scovR5', sampleResult, dateReq, dateDim, makeContainer());
        expect(opts[0].series[0].type).toBe('line');
    });

    test('2 dims (no force) → detectChartType → bar-stacked', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), dispose: jest.fn() }; }) };
        const twoDimReq = { dimensions: ['R', 'C'], measures: [{ field: 'A', func: 'Sum' }] };
        const twoDimFields = [{ fieldName: 'R', isDate: false }, { fieldName: 'C', isDate: false }];
        waReq.renderChart('scovR6', { columns: ['R', 'C', 'A_Sum'], rows: [{ R: 'N', C: 'X', A_Sum: 10 }] }, twoDimReq, twoDimFields, makeContainer());
        expect(opts[0].series[0].stack).toBe('total');
    });

    test('no dims (card) → type=bar, no stack', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), dispose: jest.fn() }; }) };
        const noDimReq = { dimensions: [], measures: [{ field: 'Amount', func: 'Sum' }] };
        waReq.renderChart('scovR7', { columns: ['Amount_Sum'], rows: [{ Amount_Sum: 42 }] }, noDimReq, [], makeContainer());
        expect(opts[0].series[0].type).toBe('bar');
        expect(opts[0].series[0].stack).toBeUndefined();
    });

    test('dim not in dimFields → isDate defaults false → bar type', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), dispose: jest.fn() }; }) };
        const unknownReq = { dimensions: ['Unknown'], measures: [{ field: 'A', func: 'Sum' }] };
        waReq.renderChart('scovR8', { columns: ['Unknown', 'A_Sum'], rows: [{ Unknown: 'X', A_Sum: 1 }] }, unknownReq, [], makeContainer());
        expect(opts[0].series[0].type).toBe('bar');
    });
});

// ─── [cov] renderPanel — createFieldSection allowedFuncs branches ─────────────
describe('[cov] waReq.renderPanel — createFieldSection allowedFuncs branches', () => {
    let _fetchOrig;
    beforeEach(() => { _fetchOrig = global.fetch; });
    afterEach(() => { spyTeardown(); global.fetch = _fetchOrig; });

    test('single-func measure → defaultFunc set; multi-func → select element; no-func → no extra UI', async () => {
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-scovP1';
        global.fetch = jest.fn().mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue([
                { kind: 'Dimension', fieldName: 'Region', displayName: '地區', isDate: false, allowedFuncs: 0 },
                { kind: 'Measure',   fieldName: 'Cnt',    displayName: '計數', allowedFuncs: 1 },  // Count only
                { kind: 'Measure',   fieldName: 'Amt',    displayName: '金額', allowedFuncs: 6 },  // Sum+Avg → select
                { kind: 'Measure',   fieldName: 'Qty',    displayName: '數量', allowedFuncs: 0 },  // no flags
            ])
        });
        spySetup(function(id) { return id === 'analysis-panel-scovP1' ? panel : null; });
        waReq.toggle('scovP1', 'VmP1');
        await new Promise(r => setTimeout(r, 50));
        // Panel got renderPanel output appended — verify checkboxes and select
        const checkboxes = panel.querySelectorAll('input[type="checkbox"]');
        const selects = panel.querySelectorAll('select');
        expect(checkboxes.length).toBe(4);
        expect(selects.length).toBe(1);
    });
});

// ─── [cov] dimFields filter (line 309) + chart toggle handler (lines 148-154) ──
describe('[cov] query with real meta — dimFields filter + chart toggle click', () => {
    let _fetchOrig, _layuiOrig, _echartsOrig;
    beforeEach(() => {
        _fetchOrig = global.fetch;
        _layuiOrig = global.layui;
        _echartsOrig = global.echarts;
    });
    afterEach(() => {
        spyTeardown();
        global.fetch = _fetchOrig;
        global.layui = _layuiOrig;
        global.echarts = _echartsOrig;
    });

    test('query with loaded meta → dimFields filter callback executes (line 309 covered)', async () => {
        // This test uses a unique gridId so _state is fresh
        const gridId = 'scovLine309';
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-' + gridId;
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-' + gridId;
        global.layui = undefined;
        global.echarts = undefined;
        // First fetch: loadMeta success with a Dimension field
        // Second fetch: query success
        global.fetch = jest.fn()
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue([
                    { kind: 'Dimension', fieldName: 'Region', displayName: '地區', isDate: false, allowedFuncs: 0 },
                    { kind: 'Measure',   fieldName: 'Amount', displayName: '金額', allowedFuncs: 2 }
                ])
            })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: false, totalCount: 1,
                    columns: ['Region', 'Amount_Sum'],
                    rows: [{ Region: 'East', Amount_Sum: 100 }]
                })
            });
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'Region', gridId }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'Amount', gridId, defaultFunc: 'Sum' }, nextElementSibling: null };
        spySetup(
            function(id) {
                if (id === 'analysis-panel-' + gridId) return panel;
                if (id === 'analysis-result-' + gridId) return resultDiv;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle(gridId, 'VmLine309');
        await new Promise(r => setTimeout(r, 40));  // loadMeta completes
        waReq.query(gridId);
        await new Promise(r => setTimeout(r, 40));  // query completes
        // Verify table rendered — dimFields filter executed internally
        const table = resultDiv.querySelector('table');
        expect(table).toBeTruthy();
    });

    test('chart toggle button click handler (lines 148-154) — triggers re-render', async () => {
        // This test requires: 1) loadMeta success, 2) query success (sets lastResult),
        // 3) a click on one of the chart toggle buttons created by renderPanel
        const gridId = 'scovLine148';
        const panel = document.createElement('div');
        panel.id = 'analysis-panel-' + gridId;
        const resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-' + gridId;
        global.layui = undefined;
        // echarts needed to verify chart rendering path in button click handler
        const setOptionCalls = [];
        global.echarts = {
            init: jest.fn(function() {
                return { setOption: jest.fn(function(o) { setOptionCalls.push(o); }), dispose: jest.fn() };
            })
        };
        global.fetch = jest.fn()
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue([
                    { kind: 'Dimension', fieldName: 'Region', displayName: '地區', isDate: false, allowedFuncs: 0 },
                    { kind: 'Measure',   fieldName: 'Amt',    displayName: '金額', allowedFuncs: 2 }
                ])
            })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    truncated: false, totalCount: 1,
                    columns: ['Region', 'Amount_Sum'],
                    rows: [{ Region: 'East', Amount_Sum: 100 }]
                })
            });
        const dimCb = { dataset: { kind: 'Dimension', fieldName: 'Region', gridId }, nextElementSibling: null };
        const msrCb = { dataset: { kind: 'Measure', fieldName: 'Amt', gridId, defaultFunc: 'Sum' }, nextElementSibling: null };
        const toggleRow = document.createElement('div');
        toggleRow.id = 'analysis-chart-toggle-' + gridId;
        spySetup(
            function(id) {
                if (id === 'analysis-panel-' + gridId) return panel;
                if (id === 'analysis-result-' + gridId) return resultDiv;
                if (id === 'analysis-chart-toggle-' + gridId) return toggleRow;
                return null;
            },
            function() { return [dimCb, msrCb]; }
        );
        waReq.toggle(gridId, 'VmLine148');
        await new Promise(r => setTimeout(r, 40));  // loadMeta → renderPanel (buttons created in panel)
        waReq.query(gridId);
        await new Promise(r => setTimeout(r, 40));  // query → lastResult stored

        // Find the chart toggle buttons rendered by renderPanel inside `panel`
        // They are <button> elements in the chart-toggle row (div#analysis-chart-toggle-...)
        const toggleButtons = panel.querySelectorAll('button');
        // Filter for the ones in the chartToggleRow (their textContent is one of 'bar','line',etc.)
        const chartTypeBtn = Array.from(toggleButtons).find(function(b) {
            return b.textContent === 'line';
        });
        expect(chartTypeBtn).toBeDefined();
        // Click the button — this fires lines 148-154
        chartTypeBtn.click();
        // After click, renderChart should have been called (echarts.init called again)
        expect(global.echarts.init.mock.calls.length).toBe(2);
    });
});
