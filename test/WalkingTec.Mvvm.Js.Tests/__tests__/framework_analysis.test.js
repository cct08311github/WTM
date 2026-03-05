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
