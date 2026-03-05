/**
 * Tests for pure functions in framework_analysis.js
 * (detectChartType, validateSelection)
 *
 * framework_analysis.js 透過 vm.Script 載入，寫入 global.wtmAnalysis。
 */

const fs = require('fs');
const path = require('path');
const vm = require('vm');

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
