/**
 * Tests for buildPivotColLabel() — issue #646
 *
 * Pivot column keys are encoded as "{pivotValue}_{fieldName}_{func}".
 * buildPivotColLabel() converts them to "{pivotValue} ({displayName} {funcLabel})"
 * so that table headers and chart legends are human-friendly.
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_analysis.js'),
    'utf8'
);

// ─── Helper: load framework_analysis.js in a minimal context ─────────────────

function loadWa(overrides) {
    const ctx = vm.createContext(Object.assign({
        window: {},
        document: {
            createElement: () => ({ style: {}, appendChild: jest.fn(), children: [] }),
            querySelector: () => null,
            querySelectorAll: () => [],
        },
        fetch: jest.fn(),
        console,
        setInterval: global.setInterval.bind(global),
        clearInterval: global.clearInterval.bind(global),
        AbortController: global.AbortController,
        URL: { createObjectURL: jest.fn(), revokeObjectURL: jest.fn() },
        alert: jest.fn(),
    }, overrides));
    ctx.window = ctx;
    new vm.Script(SRC).runInContext(ctx);
    return ctx.wtmAnalysis;
}

// ─── Fixture data ─────────────────────────────────────────────────────────────

const PIVOT_VALUES   = ['男', '女'];
const MEASURE_NAMES  = ['RecordCount_Sum', 'Score_Avg'];
const FIELD_BY_NAME  = {
    RecordCount: { fieldName: 'RecordCount', displayName: '學生數', kind: 'Measure' },
    Score:       { fieldName: 'Score',       displayName: '分數',   kind: 'Measure' },
    Gender:      { fieldName: 'Gender',      displayName: '性别',   kind: 'Dimension' },
    Region:      { fieldName: 'Region',      displayName: '地區',   kind: 'Dimension' },
};

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('buildPivotColLabel — pivot column header formatting (issue #646)', () => {
    let wa;
    beforeAll(() => { wa = loadWa(); });

    // ── Basic formatting ─────────────────────────────────────────────────────

    test('formats pivot column as "pivotValue (displayName funcLabel)"', () => {
        const label = wa.buildPivotColLabel(
            '男_RecordCount_Sum', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        expect(label).toBe('男 (學生數 合計)');
    });

    test('formats second pivot value correctly', () => {
        const label = wa.buildPivotColLabel(
            '女_RecordCount_Sum', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        expect(label).toBe('女 (學生數 合計)');
    });

    test('formats Avg function with correct localized label', () => {
        const label = wa.buildPivotColLabel(
            '男_Score_Avg', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        expect(label).toBe('男 (分數 平均)');
    });

    test('formats female pivot + avg measure', () => {
        const label = wa.buildPivotColLabel(
            '女_Score_Avg', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        expect(label).toBe('女 (分數 平均)');
    });

    // ── Row dimension columns ────────────────────────────────────────────────

    test('returns displayName for row dimension column', () => {
        const label = wa.buildPivotColLabel(
            'Gender', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        expect(label).toBe('性别');
    });

    test('returns displayName for a different dimension column', () => {
        const label = wa.buildPivotColLabel(
            'Region', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        expect(label).toBe('地區');
    });

    // ── Fallback behaviour ───────────────────────────────────────────────────

    test('falls back to raw col name when fieldByName has no entry', () => {
        const label = wa.buildPivotColLabel(
            'UnknownCol', PIVOT_VALUES, MEASURE_NAMES, {}
        );
        expect(label).toBe('UnknownCol');
    });

    test('falls back to raw fieldPart when meta is missing for measure', () => {
        // Column matches pivot format but field not in fieldByName
        const label = wa.buildPivotColLabel(
            '男_Score_Avg', PIVOT_VALUES, MEASURE_NAMES, {}
        );
        expect(label).toBe('男 (Score 平均)');
    });

    test('uses title as fallback when displayName is absent', () => {
        const fieldByNameWithTitle = {
            RecordCount: { fieldName: 'RecordCount', title: '學生人數', kind: 'Measure' },
        };
        const label = wa.buildPivotColLabel(
            '男_RecordCount_Sum', PIVOT_VALUES, MEASURE_NAMES, fieldByNameWithTitle
        );
        expect(label).toBe('男 (學生人數 合計)');
    });

    // ── Edge cases ───────────────────────────────────────────────────────────

    test('does not confuse partial pivot value prefix', () => {
        // "男性" is NOT in PIVOT_VALUES, so the column should be treated as dimension
        const label = wa.buildPivotColLabel(
            '男性', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        // Not a pivot column, no dim meta → raw fallback
        expect(label).toBe('男性');
    });

    test('handles empty pivotValues list gracefully', () => {
        const label = wa.buildPivotColLabel(
            '男_RecordCount_Sum', [], MEASURE_NAMES, FIELD_BY_NAME
        );
        // With no pivot values, treated as a dimension column → raw fallback
        expect(label).toBe('男_RecordCount_Sum');
    });

    test('handles empty measureNames list gracefully', () => {
        // Column starts with pivot prefix but no measure match → raw fallback
        const label = wa.buildPivotColLabel(
            '男_RecordCount_Sum', PIVOT_VALUES, [], FIELD_BY_NAME
        );
        expect(label).toBe('男_RecordCount_Sum');
    });

    test('returned label does NOT contain raw underscore-encoded key format', () => {
        const label = wa.buildPivotColLabel(
            '男_RecordCount_Sum', PIVOT_VALUES, MEASURE_NAMES, FIELD_BY_NAME
        );
        // Must not look like "男_RecordCount_Sum"
        expect(label).not.toMatch(/_RecordCount_Sum/);
    });
});
