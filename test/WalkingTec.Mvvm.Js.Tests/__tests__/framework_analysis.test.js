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

    test('單一非日期維度 + 單 measure → pie', () => {
        expect(wa.detectChartType([{ fieldName: 'Region', isDate: false }], [{ field: 'Amount', func: 'Sum' }])).toBe('pie');
    });

    test('單一非日期維度 + 多 measure → bar', () => {
        expect(wa.detectChartType([{ fieldName: 'Region', isDate: false }], [{ field: 'A' }, { field: 'B' }])).toBe('bar');
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
        getElementById: jest.fn((id) => {
            var el = domNodes[id] || null;
            if (!el && id && id.startsWith('analysis-panel-')) {
                el = { style: { display: 'none' }, children: [], appendChild: jest.fn(), removeChild: jest.fn() };
            }
            if (el) {
                var originalQsa = el.querySelectorAll ? el.querySelectorAll.bind(el) : function() { return []; };
                el.querySelectorAll = function(sel) {
                    if (sel && sel.indexOf('.analysis-field-cb') >= 0) return mockDocument.querySelectorAll();
                    return originalQsa(sel);
                };
            }
            return el;
        }),
        querySelectorAll: jest.fn(() => []),
        querySelector: jest.fn(() => null),
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
        const { wa, mockDocument } = makeEnv();
        mockDocument.getElementById.mockReturnValue(null);
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
                querySelector: jest.fn(() => null),
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
                querySelector: jest.fn(() => null),
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
                querySelector: jest.fn(() => null),
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
                    on: jest.fn(),
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

    test('forceChartType=pie renders pie series with name/value data', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container, 'pie');
        expect(capturedOptions.length).toBe(1);
        expect(capturedOptions[0].series[0].type).toBe('pie');
        expect(capturedOptions[0].series[0].data[0]).toEqual({ name: 'North', value: 100 });
        // pie should not have xAxis/yAxis
        expect(capturedOptions[0].xAxis).toBeUndefined();
        expect(capturedOptions[0].yAxis).toBeUndefined();
    });

    test('forceChartType=card renders HTML cards, not ECharts', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        const cardResult = {
            columns: ['Amount_Sum', 'Qty_Count'],
            rows: [{ Amount_Sum: 12345, Qty_Count: 99 }],
        };
        const cardReq = { dimensions: [], measures: [{ field: 'Amount', func: 'Sum' }, { field: 'Qty', func: 'Count' }] };
        wa.renderChart('g1', cardResult, cardReq, [], container, 'card');
        // card should NOT call echarts (no setOption)
        expect(capturedOptions.length).toBe(0);
        // should append a card container div
        const appended = container.children;
        expect(appended.length).toBeGreaterThanOrEqual(1);
        const cardDiv = appended[appended.length - 1];
        expect(cardDiv.className).toContain('analysis-cards');
    });

    test('no forceChartType uses detectChartType result', () => {
        const { wa, capturedOptions } = makeChartEnv();
        const container = makeContainer();
        // sampleDimFields has isDate:false, single dim, single measure → detectChartType returns 'pie'
        wa.renderChart('g1', sampleResult, sampleReq, sampleDimFields, container);
        expect(capturedOptions[0].series[0].type).toBe('pie');
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
    _idSpy = jest.spyOn(document, 'getElementById').mockImplementation(function(id) {
        var el = idImpl ? idImpl(id) : null;
        if (el) {
            var originalQsa = el.querySelectorAll ? el.querySelectorAll.bind(el) : function() { return []; };
            el.querySelectorAll = function(sel) {
                if (sel && sel.indexOf('.analysis-field-cb') >= 0 && qsaImpl) return qsaImpl(sel);
                return originalQsa(sel);
            };
        }
        return el;
    });
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
    test('detectChartType: single non-date dim + single measure → pie', () => {
        expect(waReq.detectChartType([{ isDate: false }], [{ field: 'A' }])).toBe('pie');
    });
    test('detectChartType: single non-date dim + multi measure → bar', () => {
        expect(waReq.detectChartType([{ isDate: false }], [{ field: 'A' }, { field: 'B' }])).toBe('bar');
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
        global.echarts = { init: jest.fn(function() { return { setOption, on: jest.fn(), dispose: jest.fn() }; }) };
        waReq.renderChart('scovR2', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'bar');
        expect(global.echarts.init).toHaveBeenCalled();
        expect(setOption).toHaveBeenCalled();
    });

    test('forceChartType=line → series[0].type=line', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        waReq.renderChart('scovR3', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'line');
        expect(opts[0].series[0].type).toBe('line');
    });

    test('forceChartType=bar-stacked → stack=total', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        waReq.renderChart('scovR4', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'bar-stacked');
        expect(opts[0].series[0].stack).toBe('total');
    });

    test('date dim (no force) → detectChartType → line', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        const dateDim = [{ fieldName: 'Date', isDate: true }];
        const dateReq = { dimensions: ['Date'], measures: [{ field: 'Amount', func: 'Sum' }] };
        waReq.renderChart('scovR5', sampleResult, dateReq, dateDim, makeContainer());
        expect(opts[0].series[0].type).toBe('line');
    });

    test('2 dims (no force) → detectChartType → bar-stacked', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        const twoDimReq = { dimensions: ['R', 'C'], measures: [{ field: 'A', func: 'Sum' }] };
        const twoDimFields = [{ fieldName: 'R', isDate: false }, { fieldName: 'C', isDate: false }];
        waReq.renderChart('scovR6', { columns: ['R', 'C', 'A_Sum'], rows: [{ R: 'N', C: 'X', A_Sum: 10 }] }, twoDimReq, twoDimFields, makeContainer());
        expect(opts[0].series[0].stack).toBe('total');
    });

    test('no dims (card) → renders HTML cards, no echarts', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        const noDimReq = { dimensions: [], measures: [{ field: 'Amount', func: 'Sum' }] };
        const container = makeContainer();
        waReq.renderChart('scovR7', { columns: ['Amount_Sum'], rows: [{ Amount_Sum: 42 }] }, noDimReq, [], container);
        // card should NOT call echarts
        expect(opts.length).toBe(0);
        // should have appended a card container
        expect(container.querySelector('.analysis-cards')).not.toBeNull();
    });

    test('dim not in dimFields → isDate defaults false, single measure → pie type', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        const unknownReq = { dimensions: ['Unknown'], measures: [{ field: 'A', func: 'Sum' }] };
        waReq.renderChart('scovR8', { columns: ['Unknown', 'A_Sum'], rows: [{ Unknown: 'X', A_Sum: 1 }] }, unknownReq, [], makeContainer());
        expect(opts[0].series[0].type).toBe('pie');
    });

    test('forceChartType=pie → pie series with name/value data', () => {
        const opts = [];
        global.echarts = { init: jest.fn(function() { return { setOption: jest.fn(function(o) { opts.push(o); }), on: jest.fn(), dispose: jest.fn() }; }) };
        waReq.renderChart('scovR9', sampleResult, sampleReq, sampleDimFields, makeContainer(), 'pie');
        expect(opts[0].series[0].type).toBe('pie');
        expect(opts[0].series[0].data[0]).toEqual({ name: 'North', value: 100 });
        expect(opts[0].xAxis).toBeUndefined();
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
        // 4 fields (1 dim + 3 measures) + 1 pivot mode toggle + 1 含圖表 export checkbox = 6 checkboxes
        expect(checkboxes.length).toBe(6);
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
                return { setOption: jest.fn(function(o) { setOptionCalls.push(o); }), on: jest.fn(), dispose: jest.fn() };
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
        const drillBarToggle = document.createElement('div');
        drillBarToggle.id = 'analysis-drill-bar-' + gridId;
        spySetup(
            function(id) {
                if (id === 'analysis-panel-' + gridId) return panel;
                if (id === 'analysis-result-' + gridId) return resultDiv;
                if (id === 'analysis-chart-toggle-' + gridId) return toggleRow;
                if (id === 'analysis-drill-bar-' + gridId) return drillBarToggle;
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

// ─── formatDateKey ──────────────────────────────────────────────────────────
describe('wtmAnalysis.formatDateKey', () => {
    test('4 位 → Year 格式', () => {
        expect(wa.formatDateKey(2026)).toBe('2026');
        expect(wa.formatDateKey('2026')).toBe('2026');
    });

    test('5 位 → Quarter 格式', () => {
        expect(wa.formatDateKey(20261)).toBe('2026 Q1');
        expect(wa.formatDateKey(20263)).toBe('2026 Q3');
    });

    test('6 位 → Month 格式', () => {
        expect(wa.formatDateKey(202603)).toBe('2026-03');
        expect(wa.formatDateKey(202612)).toBe('2026-12');
    });

    test('8 位 → Day 格式', () => {
        expect(wa.formatDateKey(20260309)).toBe('2026-03-09');
        expect(wa.formatDateKey(20261231)).toBe('2026-12-31');
    });

    test('其他長度 → 原樣回傳', () => {
        expect(wa.formatDateKey(12)).toBe('12');
        expect(wa.formatDateKey(1234567)).toBe('1234567');
    });
});

// ─── Date hierarchy dropdown ────────────────────────────────────────────────
describe('Date hierarchy dropdown', () => {
    test('isDate 維度欄位旁渲染 hierarchy 下拉（createElement 追蹤）', async () => {
        const { wa, makePanel, mockFetch, mockDocument } = makeEnv();
        const panel = makePanel('analysis-panel-dateDim1');

        // 追蹤所有 createElement 產生的元素
        var createdElements = [];
        var origCreate = mockDocument.createElement;
        mockDocument.createElement = jest.fn(function (tag) {
            var el = origCreate(tag);
            createdElements.push(el);
            return el;
        });

        mockFetch.mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue([
                { fieldName: 'OrderDate', displayName: '訂單日期', kind: 'Dimension', isDate: true, allowedFuncs: [] },
                { fieldName: 'Region', displayName: '地區', kind: 'Dimension', isDate: false, allowedFuncs: [] },
                { fieldName: 'Amount', displayName: '金額', kind: 'Measure', allowedFuncs: ['Sum'] },
            ])
        });

        wa.toggle('dateDim1', 'TestVm');
        await new Promise(r => setTimeout(r, 40));

        // 從 createElement 紀錄中找 hierarchy select
        var selects = createdElements.filter(function (el) {
            return el.className === 'analysis-hierarchy-select';
        });

        // 只有 OrderDate 是 isDate，所以只有 1 個 hierarchy select
        expect(selects.length).toBe(1);
        expect(selects[0].dataset.field).toBe('OrderDate');
        // 應有 4 個 option（Year, Quarter, Month, Day）
        expect(selects[0].children.length).toBe(4);
    });

    test('isDate checkbox 有 data-is-date 屬性', async () => {
        const { wa, makePanel, mockFetch, mockDocument } = makeEnv();
        const panel = makePanel('analysis-panel-dateDim2');

        var createdElements = [];
        var origCreate = mockDocument.createElement;
        mockDocument.createElement = jest.fn(function (tag) {
            var el = origCreate(tag);
            createdElements.push(el);
            return el;
        });

        mockFetch.mockResolvedValue({
            ok: true,
            json: jest.fn().mockResolvedValue([
                { fieldName: 'OrderDate', displayName: '訂單日期', kind: 'Dimension', isDate: true, allowedFuncs: [] },
            ])
        });

        wa.toggle('dateDim2', 'TestVm');
        await new Promise(r => setTimeout(r, 40));

        var checkboxes = createdElements.filter(function (el) {
            return el.className === 'analysis-field-cb' && el.dataset.fieldName === 'OrderDate';
        });
        expect(checkboxes.length).toBe(1);
        expect(checkboxes[0].dataset.isDate).toBe('true');
    });
});

// ─── collectSelection with hierarchy ────────────────────────────────────────
describe('collectSelection with dimensionHierarchies', () => {
    test('日期維度的 hierarchy 被收集到 dimensionHierarchies', () => {
        const { wa, mockDocument } = makeEnv();

        // 模擬 panel
        const panel = {
            querySelectorAll: jest.fn(function () {
                return [dimCb];
            })
        };
        mockDocument.getElementById.mockReturnValue(panel);

        // 模擬 date dimension checkbox
        var hierarchySelect = { value: 'Quarter', className: 'analysis-hierarchy-select' };
        var dimCb = {
            dataset: { kind: 'Dimension', fieldName: 'OrderDate', isDate: 'true', gridId: 'g1' },
            parentNode: {
                querySelector: jest.fn(function (sel) {
                    if (sel === '.analysis-hierarchy-select') return hierarchySelect;
                    return null;
                })
            }
        };
        panel.querySelectorAll = jest.fn(function () { return [dimCb]; });

        var result = wa.collectSelection('g1');
        expect(result.dims).toEqual(['OrderDate']);
        expect(result.dimensionHierarchies).toEqual({ OrderDate: 'Quarter' });
    });

    test('非日期維度不加入 dimensionHierarchies', () => {
        const { wa, mockDocument } = makeEnv();

        var dimCb = {
            dataset: { kind: 'Dimension', fieldName: 'Region', gridId: 'g2' },
            parentNode: { querySelector: jest.fn(function () { return null; }) }
        };
        const panel = { querySelectorAll: jest.fn(function () { return [dimCb]; }) };
        mockDocument.getElementById.mockReturnValue(panel);

        var result = wa.collectSelection('g2');
        expect(result.dims).toEqual(['Region']);
        expect(result.dimensionHierarchies).toEqual({});
    });
});

// ─── buildDrillFilter ─────────────────────────────────────────────────────────
describe('wtmAnalysis.buildDrillFilter', () => {
    test('建構 Eq filter 物件', () => {
        expect(wa.buildDrillFilter('Region', '華東')).toEqual({
            field: 'Region', operator: 'Eq', value: '華東'
        });
    });

    test('數值型 value 保持原型別', () => {
        expect(wa.buildDrillFilter('Year', 2026)).toEqual({
            field: 'Year', operator: 'Eq', value: 2026
        });
    });
});

// ─── nextHierarchy ────────────────────────────────────────────────────────────
describe('wtmAnalysis.nextHierarchy', () => {
    test('Year → Quarter', () => {
        expect(wa.nextHierarchy('Year')).toBe('Quarter');
    });

    test('Quarter → Month', () => {
        expect(wa.nextHierarchy('Quarter')).toBe('Month');
    });

    test('Month → Day', () => {
        expect(wa.nextHierarchy('Month')).toBe('Day');
    });

    test('Day → null（已到最細層）', () => {
        expect(wa.nextHierarchy('Day')).toBeNull();
    });

    test('未知值 → null', () => {
        expect(wa.nextHierarchy('Unknown')).toBeNull();
    });
});

// ─── drill-down 互動 ─────────────────────────────────────────────────────────
describe('drill-down integration', () => {
    function makeDrillEnv() {
        const chartOnHandlers = {};
        const { wa, mockDocument, mockFetch, domNodes } = makeEnv({
            echarts: {
                init: jest.fn(() => ({
                    setOption: jest.fn(),
                    on: jest.fn((event, handler) => { chartOnHandlers[event] = handler; }),
                    dispose: jest.fn(),
                })),
            },
        });
        return { wa, mockDocument, mockFetch, domNodes, chartOnHandlers };
    }

    // Helper: create a mock node that supports clearChildren (firstChild-based loop)
    function mockNode(id, extraProps) {
        const node = {
            id: id,
            style: { display: 'none' },
            _children: [],
            get firstChild() { return this._children.length > 0 ? this._children[0] : null; },
            appendChild: jest.fn(function(c) { this._children.push(c); return c; }),
            removeChild: jest.fn(function(c) {
                var idx = this._children.indexOf(c);
                if (idx >= 0) this._children.splice(idx, 1);
            }),
            textContent: '',
            ...(extraProps || {}),
        };
        return node;
    }

    // Helper: setup a full drill test env with panel, resultDiv, drillBar, and state initialized
    function setupDrillState(suffix, env) {
        const { wa, mockDocument, mockFetch } = env;
        const panel = mockNode('analysis-panel-' + suffix);
        const resultDiv = mockNode('analysis-result-' + suffix);
        const drillBar = mockNode('analysis-drill-bar-' + suffix);

        mockDocument.getElementById.mockImplementation((id) => {
            if (id === 'analysis-panel-' + suffix) return panel;
            if (id === 'analysis-result-' + suffix) return resultDiv;
            if (id === 'analysis-drill-bar-' + suffix) return drillBar;
            return null;
        });

        mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });
        wa.toggle(suffix, 'TestVm');

        // Manually set lastReq so drillDown/drillBack/drillReset work
        var st = wa._getState(suffix);
        st.lastReq = {
            dimensions: ['Region'],
            measures: [{ field: 'Amount', func: 'Sum' }],
            dimensionHierarchies: undefined,
        };
        st.lastDimFields = [{ fieldName: 'Region', isDate: false }];
        st.drillFilters = [];

        return { panel, resultDiv, drillBar, st };
    }

    test('drillDown pushes to drillStack and calls fetch', () => {
        const env = makeDrillEnv();
        const { drillBar } = setupDrillState('dd1', env);

        env.mockFetch.mockClear();
        env.wa.drillDown('dd1', 'Region', '華東', false);

        expect(env.mockFetch).toHaveBeenCalledWith(
            '/_analysis/query',
            expect.objectContaining({ method: 'POST' })
        );
        // drillBar should be visible after drill-down
        expect(drillBar.style.display).toBe('block');
    });

    test('drillBack pops stack and re-queries', () => {
        const env = makeDrillEnv();
        setupDrillState('dd2', env);

        env.wa.drillDown('dd2', 'Region', '華東', false);
        env.mockFetch.mockClear();

        env.wa.drillBack('dd2');
        expect(env.mockFetch).toHaveBeenCalledWith(
            '/_analysis/query',
            expect.objectContaining({ method: 'POST' })
        );
    });

    test('drillReset clears stack and re-queries', () => {
        const env = makeDrillEnv();
        const { drillBar } = setupDrillState('dd3', env);

        env.wa.drillDown('dd3', 'Region', '華東', false);
        env.wa.drillDown('dd3', 'Region', '上海', false);
        env.mockFetch.mockClear();

        env.wa.drillReset('dd3');
        expect(drillBar.style.display).toBe('none');
        expect(env.mockFetch).toHaveBeenCalled();
    });

    test('日期 drill-down 自動降階 hierarchy', () => {
        const env = makeDrillEnv();
        setupDrillState('dd4', env);

        // Override with date dimension + hierarchy
        var st = env.wa._getState('dd4');
        st.lastReq.dimensions = ['OrderDate'];
        st.lastReq.dimensionHierarchies = { OrderDate: 'Year' };
        st.lastDimFields = [{ fieldName: 'OrderDate', isDate: true }];

        env.mockFetch.mockClear();
        env.wa.drillDown('dd4', 'OrderDate', 2026, true);

        const lastCall = env.mockFetch.mock.calls[0];
        const body = JSON.parse(lastCall[1].body);
        expect(body.filters).toEqual([
            { field: 'OrderDate', operator: 'Eq', value: 2026 }
        ]);
        // Hierarchy should have been downgraded from Year to Quarter
        expect(body.dimensionHierarchies).toEqual({ OrderDate: 'Quarter' });
    });

    test('ECharts click handler triggers drillDown', () => {
        const env = makeDrillEnv();
        const { resultDiv } = setupDrillState('dd5', env);

        const result = {
            columns: ['Region', 'Amount_Sum'],
            rows: [
                { Region: '華東', Amount_Sum: 100 },
                { Region: '華南', Amount_Sum: 200 },
            ],
        };
        const dimFields = [{ fieldName: 'Region', isDate: false }];
        const req = { dimensions: ['Region'], measures: [{ field: 'Amount', func: 'Sum' }] };

        env.wa.renderChart('dd5', result, req, dimFields, resultDiv);

        expect(env.chartOnHandlers['click']).toBeDefined();

        env.mockFetch.mockClear();
        env.chartOnHandlers['click']({ dataIndex: 0, name: '華東' });

        expect(env.mockFetch).toHaveBeenCalledWith(
            '/_analysis/query',
            expect.objectContaining({ method: 'POST' })
        );
    });

    test('drill path 顯示麵包屑', () => {
        const env = makeDrillEnv();
        const { drillBar } = setupDrillState('dd6', env);

        env.wa.drillDown('dd6', 'Region', '華東', false);

        expect(drillBar.style.display).toBe('block');
        // Should have path span + back button + reset button
        expect(drillBar._children.length).toBe(3);
        expect(drillBar._children[0].textContent).toBe('全部 > 華東');
    });

    test('drillBack 後堆疊為空則隱藏 drillBar', () => {
        const env = makeDrillEnv();
        const { drillBar } = setupDrillState('dd7', env);

        env.wa.drillDown('dd7', 'Region', '華東', false);
        expect(drillBar.style.display).toBe('block');

        env.wa.drillBack('dd7');
        expect(drillBar.style.display).toBe('none');
    });

    test('多層 drill-down 麵包屑疊加', () => {
        const env = makeDrillEnv();
        const { drillBar } = setupDrillState('dd8', env);

        env.wa.drillDown('dd8', 'Region', '華東', false);
        env.wa.drillDown('dd8', 'Region', '上海', false);

        expect(drillBar._children[0].textContent).toBe('全部 > 華東 > 上海');
    });

    test('drillDown 無 lastReq 時 silent return', () => {
        const env = makeDrillEnv();
        // Don't call setupDrillState — no state at all
        expect(() => env.wa.drillDown('nonexistent', 'X', 'Y', false)).not.toThrow();
    });
});

// ─── renderPivotTable ─────────────────────────────────────────────────────────
describe('wtmAnalysis.renderPivotTable', () => {
    function makePivotResult() {
        return {
            rowDimensions: ['Region'],
            pivotValues: ['2026-Q1', '2026-Q2'],
            measureNames: ['Amount_Sum'],
            columns: ['Region', '2026-Q1_Amount_Sum', '2026-Q2_Amount_Sum'],
            rows: [
                { Region: '華東', '2026-Q1_Amount_Sum': 100, '2026-Q2_Amount_Sum': 200 },
                { Region: '華南', '2026-Q1_Amount_Sum': null, '2026-Q2_Amount_Sum': 150 },
            ],
        };
    }

    test('動態表頭生成 — columns 全部渲染為 th', () => {
        const { wa, mockDocument } = makeEnv();
        const createdElements = [];
        mockDocument.createElement.mockImplementation((tag) => {
            const el = {
                tag, style: {}, className: '', textContent: '', id: '',
                dataset: {}, children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                removeChild: jest.fn(), addEventListener: jest.fn(),
            };
            createdElements.push(el);
            return el;
        });

        const container = { appendChild: jest.fn() };
        wa.renderPivotTable('pv1', makePivotResult(), container, {});

        var ths = createdElements.filter(function(e) { return e.tag === 'th'; });
        expect(ths.length).toBe(3);
        expect(ths[0].textContent).toBe('Region');
        expect(ths[1].textContent).toBe('2026-Q1_Amount_Sum');
        expect(ths[2].textContent).toBe('2026-Q2_Amount_Sum');
    });

    test('缺值（null）顯示 "-"', () => {
        const { wa, mockDocument } = makeEnv();
        const createdElements = [];
        mockDocument.createElement.mockImplementation((tag) => {
            const el = {
                tag, style: {}, className: '', textContent: '', id: '',
                dataset: {}, children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                removeChild: jest.fn(), addEventListener: jest.fn(),
            };
            createdElements.push(el);
            return el;
        });

        const container = { appendChild: jest.fn() };
        wa.renderPivotTable('pv2', makePivotResult(), container, {});

        var tds = createdElements.filter(function(e) { return e.tag === 'td'; });
        // Row 2 (華南): Region=華南, Q1=null→"-", Q2=150
        expect(tds[3].textContent).toBe('華南');
        expect(tds[4].textContent).toBe('-');
        expect(tds[5].textContent).toBe('150');
    });

    test('date dimension key 在 dateDims 中會被 formatDateKey 格式化', () => {
        const { wa, mockDocument } = makeEnv();
        const createdElements = [];
        mockDocument.createElement.mockImplementation((tag) => {
            const el = {
                tag, style: {}, className: '', textContent: '', id: '',
                dataset: {}, children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                removeChild: jest.fn(), addEventListener: jest.fn(),
            };
            createdElements.push(el);
            return el;
        });

        var result = {
            rowDimensions: ['OrderDate'],
            pivotValues: ['East'],
            measureNames: ['Amount_Sum'],
            columns: ['OrderDate', 'East_Amount_Sum'],
            rows: [{ OrderDate: 202603, 'East_Amount_Sum': 100 }],
        };
        var container = { appendChild: jest.fn() };
        wa.renderPivotTable('pv3', result, container, { OrderDate: true });

        var tds = createdElements.filter(function(e) { return e.tag === 'td'; });
        expect(tds[0].textContent).toBe('2026-03');
    });

    test('水平捲動 wrapper 使用 overflowX: auto', () => {
        const { wa, mockDocument } = makeEnv();
        const createdElements = [];
        mockDocument.createElement.mockImplementation((tag) => {
            const el = {
                tag, style: {}, className: '', textContent: '', id: '',
                dataset: {}, children: [],
                appendChild: jest.fn(function(c) { this.children.push(c); return c; }),
                removeChild: jest.fn(), addEventListener: jest.fn(),
            };
            createdElements.push(el);
            return el;
        });

        var container = { appendChild: jest.fn() };
        wa.renderPivotTable('pv4', makePivotResult(), container, {});

        var wrapper = createdElements.find(function(e) { return e.tag === 'div'; });
        expect(wrapper.style.overflowX).toBe('auto');
    });
});

// ─── renderPivotChart ─────────────────────────────────────────────────────────
describe('wtmAnalysis.renderPivotChart', () => {
    test('Pivot chart 建立 stacked bar series（pivotValue × measure）', () => {
        const capturedOptions = [];
        const { wa } = makeEnv({
            echarts: {
                init: jest.fn(() => ({
                    setOption: jest.fn((opt) => { capturedOptions.push(opt); }),
                    on: jest.fn(),
                    dispose: jest.fn(),
                })),
            },
        });

        var result = {
            rowDimensions: ['Region'],
            pivotValues: ['Q1', 'Q2'],
            measureNames: ['Amount_Sum'],
            columns: ['Region', 'Q1_Amount_Sum', 'Q2_Amount_Sum'],
            rows: [
                { Region: '華東', 'Q1_Amount_Sum': 100, 'Q2_Amount_Sum': 200 },
                { Region: '華南', 'Q1_Amount_Sum': 50, 'Q2_Amount_Sum': 150 },
            ],
        };
        var container = { appendChild: jest.fn() };

        wa.renderPivotChart('pc1', result, {}, container);

        expect(capturedOptions.length).toBe(1);
        var opt = capturedOptions[0];
        expect(opt.xAxis.data).toEqual(['華東', '華南']);
        expect(opt.series.length).toBe(2);
        expect(opt.series[0].name).toBe('Q1_Amount_Sum');
        expect(opt.series[0].type).toBe('bar');
        expect(opt.series[0].stack).toBe('Amount_Sum');
        expect(opt.series[0].data).toEqual([100, 50]);
        expect(opt.series[1].name).toBe('Q2_Amount_Sum');
        expect(opt.series[1].data).toEqual([200, 150]);
    });

    test('無 echarts 時不拋錯', () => {
        const { wa } = makeEnv();
        var container = { appendChild: jest.fn() };
        expect(() => wa.renderPivotChart('pc2', {
            rowDimensions: [], pivotValues: [], measureNames: [],
            columns: [], rows: []
        }, {}, container)).not.toThrow();
    });

    test('rowDimensions 為空時 X 軸顯示 "總計"', () => {
        const capturedOptions = [];
        const { wa } = makeEnv({
            echarts: {
                init: jest.fn(() => ({
                    setOption: jest.fn((opt) => { capturedOptions.push(opt); }),
                    on: jest.fn(),
                    dispose: jest.fn(),
                })),
            },
        });
        var result = {
            rowDimensions: [],
            pivotValues: ['A'],
            measureNames: ['M_Sum'],
            columns: ['A_M_Sum'],
            rows: [{ 'A_M_Sum': 42 }],
        };
        wa.renderPivotChart('pc3', result, {}, { appendChild: jest.fn() });
        expect(capturedOptions[0].xAxis.data).toEqual(['總計']);
    });
});

// ─── Pivot mode in query ──────────────────────────────────────────────────────
describe('Pivot mode in query()', () => {
    test('pivot toggle on → 查詢 /_analysis/pivot 並傳送 pivotDimension', async () => {
        const { wa, makePanel, mockFetch, mockDocument } = makeEnv();
        var panel = makePanel('analysis-panel-pvq1');
        var resultDiv = {
            id: 'analysis-result-pvq1', textContent: '', children: [],
            appendChild: jest.fn(), firstChild: null, removeChild: jest.fn(),
        };
        mockDocument.getElementById.mockImplementation((id) => {
            if (id === 'analysis-panel-pvq1') return panel;
            if (id === 'analysis-result-pvq1') return resultDiv;
            return null;
        });
        mockDocument.querySelectorAll.mockReturnValue([
            { dataset: { kind: 'Dimension', fieldName: 'Region', gridId: 'pvq1' } },
            { dataset: { kind: 'Dimension', fieldName: 'Quarter', gridId: 'pvq1' } },
            { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'pvq1', defaultFunc: 'Sum' } },
        ]);
        mockDocument.querySelector.mockImplementation((sel) => {
            if (sel.indexOf('pivot-toggle') >= 0) return { checked: true };
            if (sel.indexOf('pivot-dim-select') >= 0) return { value: 'Region' };
            return null;
        });

        mockFetch
            .mockResolvedValueOnce({ ok: false, text: jest.fn().mockResolvedValue('err') })
            .mockResolvedValueOnce({
                ok: true,
                json: jest.fn().mockResolvedValue({
                    rowDimensions: ['Quarter'],
                    pivotValues: ['華東', '華南'],
                    measureNames: ['Amount_Sum'],
                    columns: ['Quarter', '華東_Amount_Sum', '華南_Amount_Sum'],
                    rows: [{ Quarter: '2026 Q1', '華東_Amount_Sum': 100, '華南_Amount_Sum': 200 }],
                })
            });

        wa.toggle('pvq1', 'TestVm');
        wa.query('pvq1');
        await new Promise(r => setTimeout(r, 50));

        var queryCall = mockFetch.mock.calls.find(c => c[0] === '/_analysis/pivot');
        expect(queryCall).toBeDefined();
        var body = JSON.parse(queryCall[1].body);
        expect(body.pivotDimension).toBe('Region');
        expect(body.dimensions).toEqual(['Region', 'Quarter']);
    });

    test('pivot toggle on 但未選 pivot 維度 → alert 錯誤', () => {
        const { wa, makePanel, mockFetch, mockDocument, mockAlert } = makeEnv();
        makePanel('analysis-panel-pvq2');
        mockDocument.querySelectorAll.mockReturnValue([
            { dataset: { kind: 'Dimension', fieldName: 'Region', gridId: 'pvq2' } },
            { dataset: { kind: 'Measure', fieldName: 'Amount', gridId: 'pvq2', defaultFunc: 'Sum' } },
        ]);
        mockDocument.querySelector.mockImplementation((sel) => {
            if (sel.indexOf('pivot-toggle') >= 0) return { checked: true };
            if (sel.indexOf('pivot-dim-select') >= 0) return null;
            return null;
        });
        mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        wa.toggle('pvq2', 'TestVm');
        wa.query('pvq2');

        expect(mockAlert).toHaveBeenCalledWith('請選擇一個樞紐(Pivot)維度');
    });
});

// ═══════════════════════════════════════════════════════════════════════════════
// v2 Drag-Drop Panel Tests (RED phase — functions not yet implemented)
// ═══════════════════════════════════════════════════════════════════════════════

// ─── buildPillHtml ────────────────────────────────────────────────────────────
describe('buildPillHtml (v2)', function () {
    test('dimension pill has correct class and data attributes', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'Region',
            title: '地區',
            kind: 'Dimension',
            isDate: false,
        }, 'grid1');

        expect(html).toContain('analysis-pill');
        expect(html).toContain('data-field="Region"');
        expect(html).toContain('data-kind="Dimension"');
        expect(html).toContain('地區');
    });

    test('measure pill includes aggregate function select', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'Amount',
            title: '金額',
            kind: 'Measure',
            isDate: false,
            defaultFunc: 'Sum',
        }, 'grid1');

        expect(html).toContain('<select');
        expect(html).toContain('Sum');
        expect(html).toContain('Avg');
        expect(html).toContain('Count');
        expect(html).toContain('Max');
        expect(html).toContain('Min');
    });

    test('date dimension pill includes hierarchy select with Year/Quarter/Month/Day options', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'OrderDate',
            title: '訂單日期',
            kind: 'Dimension',
            isDate: true,
        }, 'grid1');

        expect(html).toContain('<select');
        expect(html).toContain('Year');
        expect(html).toContain('Quarter');
        expect(html).toContain('Month');
        expect(html).toContain('Day');
    });

    test('pill includes remove button with ✕', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'Region',
            title: '地區',
            kind: 'Dimension',
            isDate: false,
        }, 'grid1');

        expect(html).toContain('✕');
    });

    test('XSS: field name with HTML special chars is escaped', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: '<script>alert(1)</script>',
            title: 'Safe Title',
            kind: 'Dimension',
            isDate: false,
        }, 'grid1');

        expect(html).not.toContain('<script>');
        expect(html).toContain('&lt;script&gt;');
    });

    test('XSS: field title with HTML special chars is escaped', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'SafeName',
            title: '"><img src=x onerror=alert(1)>',
            kind: 'Dimension',
            isDate: false,
        }, 'grid1');

        expect(html).not.toContain('onerror');
        expect(html).toContain('&quot;');
    });
});

// ─── collectDropZoneData ──────────────────────────────────────────────────────
describe('collectDropZoneData (v2)', function () {
    test('reads dimension pills from drop zone', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        // Set up mock DOM with dimension pills in drop zone
        var dimPill = {
            dataset: { field: 'Region', kind: 'Dimension' },
            querySelector: jest.fn(function () { return null; }),
        };
        var dimZone = {
            id: 'dim-dropzone-g1',
            children: [dimPill],
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [dimPill];
                return [];
            }),
        };
        env.domNodes['dim-dropzone-g1'] = dimZone;

        var result = _test.collectDropZoneData('g1');
        expect(result.dims).toContain('Region');
        expect(result.dims.length).toBe(1);
    });

    test('reads measure pills with selected aggregate function', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var msrSelect = { value: 'Avg' };
        var msrPill = {
            dataset: { field: 'Amount', kind: 'Measure' },
            querySelector: jest.fn(function (sel) {
                if (sel.indexOf('select') >= 0) return msrSelect;
                return null;
            }),
        };
        var msrZone = {
            id: 'msr-dropzone-g1',
            children: [msrPill],
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [msrPill];
                return [];
            }),
        };
        env.domNodes['msr-dropzone-g1'] = msrZone;

        var result = _test.collectDropZoneData('g1');
        expect(result.msrs).toEqual(
            expect.arrayContaining([
                expect.objectContaining({ field: 'Amount', func: 'Avg' }),
            ])
        );
    });

    test('reads dimHierarchies from date dimension selects', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var hierSelect = { value: 'Quarter' };
        var datePill = {
            dataset: { field: 'OrderDate', kind: 'Dimension', isDate: 'true' },
            querySelector: jest.fn(function (sel) {
                if (sel.indexOf('select') >= 0) return hierSelect;
                return null;
            }),
        };
        var dimZone = {
            id: 'dim-dropzone-g2',
            children: [datePill],
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [datePill];
                return [];
            }),
        };
        env.domNodes['dim-dropzone-g2'] = dimZone;

        var result = _test.collectDropZoneData('g2');
        expect(result.dimHierarchies).toBeDefined();
        expect(result.dimHierarchies['OrderDate']).toBe('Quarter');
    });

    test('reads pivotDim when pivot mode enabled and radio checked', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.mockDocument.querySelector.mockImplementation(function (sel) {
            if (sel.indexOf('pivot-toggle') >= 0) return { checked: true };
            if (sel.indexOf('pivot-dim-select') >= 0) return { value: 'Category' };
            return null;
        });

        var result = _test.collectDropZoneData('g3');
        expect(result.pivotDim).toBe('Category');
    });

    test('returns empty arrays and null pivotDim when drop zones are empty', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var result = _test.collectDropZoneData('empty1');
        expect(result.dims).toEqual([]);
        expect(result.msrs).toEqual([]);
        expect(result.pivotDim).toBeNull();
    });
});

// ─── buildSummaryBar ──────────────────────────────────────────────────────────
describe('buildSummaryBar (v2)', function () {
    test('shows dimension and measure counts in Chinese', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildSummaryBar({
            dims: ['Region', 'Category'],
            msrs: [{ field: 'Amount', func: 'Sum' }],
        });

        // Should contain counts like "2 個維度" and "1 個度量"
        expect(html).toContain('2');
        expect(html).toContain('維度');
        expect(html).toContain('1');
        expect(html).toContain('度量');
    });

    test('shows field names as mini pill text', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildSummaryBar({
            dims: ['Region'],
            msrs: [{ field: 'Amount', func: 'Sum' }],
        });

        expect(html).toContain('Region');
        expect(html).toContain('Amount');
    });
});

// ─── escapeHtml ───────────────────────────────────────────────────────────────
describe('escapeHtml (v2)', function () {
    test('escapes < > & " characters', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var result = _test.escapeHtml('<div class="a">&b</div>');
        expect(result).toBe('&lt;div class=&quot;a&quot;&gt;&amp;b&lt;/div&gt;');
    });

    test('returns empty string for empty input', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        expect(_test.escapeHtml('')).toBe('');
    });

    test('passes through safe strings unchanged', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        expect(_test.escapeHtml('Hello World 123')).toBe('Hello World 123');
    });
});

// ─── SortableJS lifecycle ─────────────────────────────────────────────────────
describe('SortableJS lifecycle (v2)', function () {
    test('sortableInstances array exists in state after toggle', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-sort1');
        env.mockFetch.mockResolvedValue({ ok: true, json: jest.fn().mockResolvedValue({ fields: [] }) });

        env.wa.toggle('sort1', 'TestVm');

        // After toggle on, state should have sortableInstances array
        var state = _test.getState && _test.getState('sort1');
        if (state) {
            expect(Array.isArray(state.sortableInstances)).toBe(true);
        } else {
            // Fallback: just check _test exposes getState
            expect(_test.getState).toBeDefined();
        }
    });

    test('toggle off clears sortableInstances from state', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-sort2');
        env.mockFetch.mockResolvedValue({ ok: true, json: jest.fn().mockResolvedValue({ fields: [] }) });

        env.wa.toggle('sort2', 'TestVm');
        env.wa.toggle('sort2', 'TestVm'); // toggle off

        var state = _test.getState && _test.getState('sort2');
        if (state) {
            expect(!state.sortableInstances || state.sortableInstances.length === 0).toBe(true);
        } else {
            expect(_test.getState).toBeDefined();
        }
    });
});

// ─── multi-grid isolation ─────────────────────────────────────────────────────
describe('multi-grid isolation (v2)', function () {
    test('two grids have independent state objects', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-iso1');
        env.makePanel('analysis-panel-iso2');
        env.mockFetch.mockResolvedValue({ ok: true, json: jest.fn().mockResolvedValue({ fields: [] }) });

        env.wa.toggle('iso1', 'VmA');
        env.wa.toggle('iso2', 'VmB');

        var state1 = _test.getState && _test.getState('iso1');
        var state2 = _test.getState && _test.getState('iso2');

        if (state1 && state2) {
            expect(state1).not.toBe(state2);
            expect(state1.listVmType).toBe('VmA');
            expect(state2.listVmType).toBe('VmB');
        } else {
            expect(_test.getState).toBeDefined();
        }
    });

    test('collapsing one grid panel does not affect another', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-col1');
        env.makePanel('analysis-panel-col2');
        env.mockFetch.mockResolvedValue({ ok: true, json: jest.fn().mockResolvedValue({ fields: [] }) });

        env.wa.toggle('col1', 'VmA');
        env.wa.toggle('col2', 'VmB');

        // Collapse grid 1 only
        if (_test.collapsePanel) {
            _test.collapsePanel('col1');
        }

        var state1 = _test.getState && _test.getState('col1');
        var state2 = _test.getState && _test.getState('col2');

        if (state1 && state2) {
            expect(state1.collapsed).toBe(true);
            expect(state2.collapsed).toBeFalsy();
        } else {
            expect(_test.getState).toBeDefined();
        }
    });
});

// ─── drop zone validation ─────────────────────────────────────────────────────
describe('drop zone validation (v2)', function () {
    test('max 3 dimensions enforced (via validateSelection)', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        // Attempt to validate with 4 dimensions
        var result = _test.validateSelection
            ? _test.validateSelection({
                dims: ['A', 'B', 'C', 'D'],
                msrs: [{ field: 'Amount', func: 'Sum' }],
            })
            : null;

        if (result !== null) {
            // validateSelection should return an error message or false
            expect(result.valid === false || typeof result === 'string').toBe(true);
        } else {
            expect(_test.validateSelection).toBeDefined();
        }
    });

    test('max 3 measures enforced (via validateSelection)', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var result = _test.validateSelection
            ? _test.validateSelection({
                dims: ['Region'],
                msrs: [
                    { field: 'A', func: 'Sum' },
                    { field: 'B', func: 'Sum' },
                    { field: 'C', func: 'Sum' },
                    { field: 'D', func: 'Sum' },
                ],
            })
            : null;

        if (result !== null) {
            expect(result.valid === false || typeof result === 'string').toBe(true);
        } else {
            expect(_test.validateSelection).toBeDefined();
        }
    });
});

// ═══════════════════════════════════════════════════════════════════════════════
// Drag-Drop Lifecycle Interaction Tests
// Note: SortableJS is not available in Node — tests focus on state management
// and DOM class manipulation that can be verified without real drag events.
// ═══════════════════════════════════════════════════════════════════════════════

// ─── Pill drag-to-dim-zone: field marked as used ──────────────────────────────
describe('drag-drop: dimension zone pill state (v2)', function () {
    test('pill HTML for dimension has analysis-pill class and not --used initially', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'Region',
            title: '地區',
            kind: 'Dimension',
            isDate: false,
        }, 'grid1');

        // Pill in drop zone starts as analysis-pill (no --used on the drop-zone pill itself)
        expect(html).toContain('analysis-pill');
        expect(html).toContain('data-field="Region"');
        expect(html).toContain('data-kind="Dimension"');
        // The --used class would be applied to the source pill in the field list by SortableJS;
        // we verify the drop-zone pill HTML does not pre-apply --used
        expect(html).not.toContain('analysis-pill--used');
    });

    test('collectDropZoneData reads a pill added to dim drop zone', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        // Simulate a pill that has been dropped into the dimension zone
        var droppedPill = {
            dataset: { field: 'Region', kind: 'Dimension' },
            querySelector: jest.fn(function () { return null; }),
        };
        var dimZone = {
            id: 'dim-dropzone-ddlc1',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [droppedPill];
                return [];
            }),
        };
        env.domNodes['dim-dropzone-ddlc1'] = dimZone;

        var result = _test.collectDropZoneData('ddlc1');
        // After a pill is dropped, collectDropZoneData reflects it in dims
        expect(result.dims).toContain('Region');
        expect(result.msrs).toEqual([]);
    });
});

// ─── Pill drag-to-measure-zone: func select appears ──────────────────────────
describe('drag-drop: measure zone pill has func select (v2)', function () {
    test('measure pill HTML contains a <select> element for aggregate function', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'Amount',
            title: '金額',
            kind: 'Measure',
            isDate: false,
            defaultFunc: 'Sum',
        }, 'grid1');

        // Measure pill in drop zone must include a <select> for func selection
        expect(html).toContain('<select');
        expect(html).toContain('analysis-pill__select');
        // Verify options present
        expect(html).toContain('Sum');
        expect(html).toContain('Avg');
        expect(html).toContain('Count');
    });

    test('collectDropZoneData reads measure pill with selected func from <select>', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var funcSelect = { value: 'Max' };
        var msrPill = {
            dataset: { field: 'Revenue', kind: 'Measure' },
            querySelector: jest.fn(function (sel) {
                if (sel.indexOf('select') >= 0) return funcSelect;
                return null;
            }),
        };
        var msrZone = {
            id: 'msr-dropzone-ddlc2',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [msrPill];
                return [];
            }),
        };
        env.domNodes['msr-dropzone-ddlc2'] = msrZone;

        var result = _test.collectDropZoneData('ddlc2');
        // func should be read from the select element value
        expect(result.msrs).toEqual(
            expect.arrayContaining([
                expect.objectContaining({ field: 'Revenue', func: 'Max' }),
            ])
        );
    });
});

// ─── Pill remove (✕ click): field restored in source list ────────────────────
describe('drag-drop: pill remove restores source field (v2)', function () {
    test('pill HTML includes a remove button with ✕', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var html = _test.buildPillHtml({
            fieldName: 'Region',
            title: '地區',
            kind: 'Dimension',
            isDate: false,
        }, 'grid1');

        expect(html).toContain('analysis-pill__remove');
        expect(html).toContain('\u2715'); // ✕
    });

    test('after removing pill from drop zone, collectDropZoneData returns empty dims', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        // Simulate empty drop zone after pill removal
        var dimZone = {
            id: 'dim-dropzone-ddlc3',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return []; // empty after removal
                return [];
            }),
        };
        env.domNodes['dim-dropzone-ddlc3'] = dimZone;

        var result = _test.collectDropZoneData('ddlc3');
        expect(result.dims).toEqual([]);
    });
});

// ─── Collapse/expand panel toggle ─────────────────────────────────────────────
describe('collapse/expand panel toggle (v2)', function () {
    test('collapsePanel sets state.collapsed = true', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-cp1');
        env.mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        env.wa.toggle('cp1', 'TestVm');

        var st = _test.getState('cp1');
        expect(st).toBeDefined();
        expect(st.collapsed).toBe(false);

        _test.collapsePanel('cp1');
        expect(st.collapsed).toBe(true);
    });

    test('expandPanel sets state.collapsed = false after collapse', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-cp2');
        env.mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        env.wa.toggle('cp2', 'TestVm');

        _test.collapsePanel('cp2');
        var st = _test.getState('cp2');
        expect(st.collapsed).toBe(true);

        _test.expandPanel('cp2');
        expect(st.collapsed).toBe(false);
    });

    test('collapsePanel on unknown gridId does not throw', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        expect(function () { _test.collapsePanel('nonexistent_cp'); }).not.toThrow();
    });
});

// ─── Collapse/expand result block toggle ─────────────────────────────────────
describe('collapse/expand result block toggle (v2)', function () {
    test('collapseResult sets state.resultCollapsed = true', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-cr1');
        env.mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        env.wa.toggle('cr1', 'TestVm');

        var st = _test.getState('cr1');
        expect(st.resultCollapsed).toBe(false);

        _test.collapseResult('cr1');
        expect(st.resultCollapsed).toBe(true);
    });

    test('expandResult sets state.resultCollapsed = false after collapse', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-cr2');
        env.mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        env.wa.toggle('cr2', 'TestVm');

        _test.collapseResult('cr2');
        var st = _test.getState('cr2');
        expect(st.resultCollapsed).toBe(true);

        _test.expandResult('cr2');
        expect(st.resultCollapsed).toBe(false);
    });

    test('panel collapse and result collapse are independent state fields', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        env.makePanel('analysis-panel-cr3');
        env.mockFetch.mockResolvedValue({ ok: false, text: jest.fn().mockResolvedValue('err') });

        env.wa.toggle('cr3', 'TestVm');

        _test.collapsePanel('cr3');
        var st = _test.getState('cr3');
        // Panel collapsed but result should still be expanded
        expect(st.collapsed).toBe(true);
        expect(st.resultCollapsed).toBe(false);

        _test.collapseResult('cr3');
        // Both collapsed now
        expect(st.collapsed).toBe(true);
        expect(st.resultCollapsed).toBe(true);

        _test.expandPanel('cr3');
        // Panel expanded but result still collapsed
        expect(st.collapsed).toBe(false);
        expect(st.resultCollapsed).toBe(true);
    });
});

// ─── Export uses drop zone data, not old checkboxes ──────────────────────────
describe('export reads from drop zones (v2)', function () {
    test('collectDropZoneData is used instead of querySelectorAll checkboxes', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        // Set up drop zones with data
        var dimPill = {
            dataset: { field: 'Region', kind: 'Dimension' },
            querySelector: jest.fn(function () { return null; }),
        };
        var dimZone = {
            id: 'dim-dropzone-exp1',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [dimPill];
                return [];
            }),
        };
        var funcSelect = { value: 'Sum' };
        var msrPill = {
            dataset: { field: 'Amount', kind: 'Measure' },
            querySelector: jest.fn(function (sel) {
                if (sel.indexOf('select') >= 0) return funcSelect;
                return null;
            }),
        };
        var msrZone = {
            id: 'msr-dropzone-exp1',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [msrPill];
                return [];
            }),
        };
        env.domNodes['dim-dropzone-exp1'] = dimZone;
        env.domNodes['msr-dropzone-exp1'] = msrZone;

        // collectDropZoneData returns data from drop zones (not checkboxes)
        var result = _test.collectDropZoneData('exp1');
        expect(result.dims).toEqual(['Region']);
        expect(result.msrs).toEqual(
            expect.arrayContaining([
                expect.objectContaining({ field: 'Amount', func: 'Sum' }),
            ])
        );
        // Should not have called mockDocument.querySelectorAll for checkboxes
        // (drop zone approach reads from pill DOM elements, not .analysis-field-cb inputs)
        var cbCalls = env.mockDocument.querySelectorAll.mock.calls.filter(function (c) {
            return c[0] && c[0].indexOf('analysis-field-cb') >= 0;
        });
        expect(cbCalls.length).toBe(0);
    });

    test('collectDropZoneData returns both dims and msrs together for export payload', function () {
        var env = makeEnv();
        if (!env.wa._test) { expect(env.wa._test).toBeDefined(); return; }
        var _test = env.wa._test;

        var dimPill1 = { dataset: { field: 'Region', kind: 'Dimension' }, querySelector: jest.fn(function() { return null; }) };
        var dimPill2 = { dataset: { field: 'Category', kind: 'Dimension' }, querySelector: jest.fn(function() { return null; }) };
        var dimZone = {
            id: 'dim-dropzone-exp2',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [dimPill1, dimPill2];
                return [];
            }),
        };
        var s1 = { value: 'Avg' };
        var s2 = { value: 'Count' };
        var msrPill1 = { dataset: { field: 'Sales', kind: 'Measure' }, querySelector: jest.fn(function(sel) { return sel.indexOf('select') >= 0 ? s1 : null; }) };
        var msrPill2 = { dataset: { field: 'Qty', kind: 'Measure' }, querySelector: jest.fn(function(sel) { return sel.indexOf('select') >= 0 ? s2 : null; }) };
        var msrZone = {
            id: 'msr-dropzone-exp2',
            querySelectorAll: jest.fn(function (sel) {
                if (sel.indexOf('analysis-pill') >= 0) return [msrPill1, msrPill2];
                return [];
            }),
        };
        env.domNodes['dim-dropzone-exp2'] = dimZone;
        env.domNodes['msr-dropzone-exp2'] = msrZone;

        var result = _test.collectDropZoneData('exp2');
        expect(result.dims).toEqual(['Region', 'Category']);
        expect(result.msrs).toHaveLength(2);
        expect(result.msrs[0]).toEqual({ field: 'Sales', func: 'Avg' });
        expect(result.msrs[1]).toEqual({ field: 'Qty', func: 'Count' });
    });
});
