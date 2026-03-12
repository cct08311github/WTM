# Analysis Mode v2 Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the checkbox-based Analysis Mode UI with a drag-and-drop BI panel that coexists with the data grid.

**Architecture:** Frontend-only rewrite of `framework_analysis.js` + new CSS + SortableJS library. Backend APIs unchanged. TagHelper emits new DOM structure. Three independently collapsible sections: search panel, field selector, chart results — all above the data grid.

**Tech Stack:** Vanilla JS (IIFE pattern), SortableJS v1.15.6, ECharts (existing), LayUI (existing), CSS3 transitions.

**Spec:** `docs/superpowers/specs/2026-03-12-analysis-mode-v2-design.md` (committed on `feat/issue-229-chart-enhancements` branch)

**Prerequisites:**
- PR #254 (search filter integration) must be merged to `dotnet8` before starting. If not yet merged, merge it first or cherry-pick `collectSearcherFormData` changes.

**Existing files to understand:**
- `src/WalkingTec.Mvvm.Mvc/framework_analysis.js` (1030 lines, current analysis UI)
- `src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs:532-537,684-694` (analysis button + panel injection)
- `test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js` (2072 lines, 137 tests)
- `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj:39-42` (EmbeddedResource declarations)

---

## Chunk 1: Infrastructure — SortableJS + CSS + TagHelper

### Task 0: Create GitHub Issue

- [ ] **Step 1: Create issue**

```bash
gh issue create --repo cct08311github/WalkingTec.Mvvm \
  --title "feat(analysis): drag-and-drop BI panel (Analysis Mode v2)" \
  --body "Replace checkbox-based Analysis Mode UI with drag-and-drop BI panel. See spec: docs/superpowers/specs/2026-03-12-analysis-mode-v2-design.md" \
  --label "enhancement"
```

Record the issue number (e.g., #260) — use it for branch name and PR references throughout.

- [ ] **Step 2: Create feature branch**

```bash
git checkout dotnet8
git pull origin dotnet8
git checkout -b feat/issue-XXX-analysis-v2
```

Replace `XXX` with the actual issue number.

### Task 1: Add SortableJS as EmbeddedResource

**Files:**
- Create: `src/WalkingTec.Mvvm.Mvc/sortable.min.js`
- Modify: `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj:39-42`

- [ ] **Step 1: Download SortableJS v1.15.6**

```bash
curl -L -o src/WalkingTec.Mvvm.Mvc/sortable.min.js \
  https://cdn.jsdelivr.net/npm/sortablejs@1.15.6/Sortable.min.js
# Verify SHA-256 — record the hash in the commit message for audit trail
shasum -a 256 src/WalkingTec.Mvvm.Mvc/sortable.min.js
# Verify file size is ~10KB gzip (~33KB raw) — a significantly different size indicates wrong file
wc -c src/WalkingTec.Mvvm.Mvc/sortable.min.js
```

- [ ] **Step 2: Add to .csproj as EmbeddedResource**

In `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj`, after the `framework_dashboard.css` line, add:

```xml
    <EmbeddedResource Include="sortable.min.js" />
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/sortable.min.js src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
git commit -m "chore: add SortableJS v1.15.6 as EmbeddedResource"
```

### Task 2: Create `framework_analysis.css`

**Files:**
- Create: `src/WalkingTec.Mvvm.Mvc/framework_analysis.css`
- Modify: `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj`

- [ ] **Step 1: Write CSS file**

Create `src/WalkingTec.Mvvm.Mvc/framework_analysis.css` with all the styles for the analysis panel. All classes prefixed with `analysis-` to avoid LayUI conflicts.

Full CSS content:

```css
/* === Panel container === */
.analysis-panel {
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  padding: 10px 12px;
  background: #fafafa;
}
.analysis-panel--collapsed .analysis-panel-body {
  max-height: 0;
  opacity: 0;
  overflow: hidden;
  padding: 0;
}

/* === Summary bar (visible when collapsed) === */
.analysis-summary-bar {
  display: none;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
  font-size: 13px;
  color: #666;
}
.analysis-panel--collapsed .analysis-summary-bar { display: flex; }

/* === Drop zone row === */
.analysis-dropzone-row {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  margin-bottom: 8px;
}
.analysis-dropzone {
  flex: 1;
  min-height: 38px;
  border: 2px dashed #ccc;
  border-radius: 6px;
  padding: 6px 8px;
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
  transition: border-color 0.15s, background 0.15s;
}
.analysis-dropzone--dim { background: #e8f5e9; border-color: #4caf50; }
.analysis-dropzone--msr { background: #e3f2fd; border-color: #2196f3; }
.analysis-dropzone--highlight { border-color: #ff9800; background: #fff3e0; }

/* === Pills === */
.analysis-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  border-radius: 12px;
  padding: 3px 10px;
  font-size: 12px;
  cursor: grab;
  user-select: none;
  white-space: nowrap;
}
.analysis-pill--dim { background: #4caf50; color: #fff; }
.analysis-pill--msr { background: #2196f3; color: #fff; }
.analysis-pill--used {
  background: #e8e8e8;
  color: #aaa;
  text-decoration: line-through;
  cursor: default;
  pointer-events: none;
}
.analysis-pill__remove {
  opacity: 0.7;
  cursor: pointer;
  margin-left: 2px;
  font-size: 11px;
}
.analysis-pill__remove:hover { opacity: 1; }
.analysis-pill__select {
  background: transparent;
  border: none;
  color: inherit;
  font-size: 11px;
  padding: 0 2px;
  cursor: pointer;
}

/* === Field list === */
.analysis-field-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-bottom: 8px;
  min-height: 30px;
}

/* === Result block === */
.analysis-result {
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  background: #fff;
}
.analysis-result__header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-bottom: 1px solid #eee;
  font-size: 13px;
}
.analysis-result__chart { padding: 8px 12px; min-height: 300px; }
.analysis-result__table { padding: 0 12px 8px; overflow-x: auto; }
.analysis-result--collapsed .analysis-result-body {
  max-height: 0;
  opacity: 0;
  overflow: hidden;
}

/* === Collapse button === */
.analysis-collapse-btn {
  background: none;
  border: 1px solid #ddd;
  border-radius: 4px;
  padding: 2px 8px;
  font-size: 12px;
  cursor: pointer;
  color: #666;
}
.analysis-collapse-btn:hover { background: #f0f0f0; }

/* === Transitions === */
.analysis-panel-body,
.analysis-result-body {
  max-height: 2000px;
  opacity: 1;
  transition: max-height 250ms ease-out, opacity 250ms ease-out;
}

/* === SortableJS drag states === */
.sortable-ghost { opacity: 0.4; }
.sortable-chosen { box-shadow: 0 2px 8px rgba(0,0,0,0.15); }
.sortable-drag { opacity: 0.9; }

/* === Button row === */
.analysis-btn-row {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-top: 6px;
}
```

- [ ] **Step 2: Add to .csproj**

```xml
    <EmbeddedResource Include="framework_analysis.css" />
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release
```

- [ ] **Step 4: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/framework_analysis.css src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
git commit -m "feat(analysis): add analysis panel CSS with pill/dropzone styles"
```

### Task 3: Update DataTableTagHelper DOM structure

**Files:**
- Modify: `src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs:532-537,684-694`

- [ ] **Step 1: Update analysis panel HTML emission (lines 684-694)**

Replace the current block:
```csharp
if (EnableAnalysis)
{
    output.PostElement.AppendHtml(
        $@"<div id=""analysis-panel-{Id}"" style=""display:none;margin-top:10px;""></div>");
    if (!context.Items.ContainsKey("analysis_js_loaded"))
    {
        context.Items["analysis_js_loaded"] = true;
        output.PostElement.AppendHtml(
            @"<script src=""/_js/framework_analysis.js""></script>");
    }
}
```

With new structure that emits three containers (panel, result, scripts):
```csharp
if (EnableAnalysis)
{
    // Field selector panel (collapsible)
    output.PostElement.AppendHtml(
        $@"<div id=""analysis-panel-{Id}"" class=""analysis-panel"" style=""display:none;margin-top:10px;""></div>");
    // Result block (independent, collapsible)
    output.PostElement.AppendHtml(
        $@"<div id=""analysis-result-block-{Id}"" class=""analysis-result"" style=""display:none;margin-top:6px;""></div>");
    if (!context.Items.ContainsKey("analysis_js_loaded"))
    {
        context.Items["analysis_js_loaded"] = true;
        output.PostElement.AppendHtml(
            @"<link rel=""stylesheet"" href=""/_js/framework_analysis.css"" />");
        output.PostElement.AppendHtml(
            @"<script src=""/_js/sortable.min.js""></script>");
        output.PostElement.AppendHtml(
            @"<script src=""/_js/framework_analysis.js""></script>");
    }
}
```

- [ ] **Step 2: Build solution**

```bash
dotnet build WalkingTec.Mvvm.sln -c Release
```

- [ ] **Step 3: Update TagHelper tests for new HTML output**

Existing `DataTableTagHelperAnalysisTests` only test the dedup flag (`analysis_js_loaded`). Add assertions verifying the new HTML output includes:
- `analysis-result-block-{Id}` div
- `<link ... framework_analysis.css />`
- `<script src="/_js/sortable.min.js">`

Update existing assertions that check old HTML structure (single div, no CSS link).

- [ ] **Step 4: Run TagHelper tests**

```bash
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~EnableAnalysis" -c Release -v normal
```
Expected: All tests pass with updated assertions.

- [ ] **Step 5: Commit**

```bash
git add src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs test/WalkingTec.Mvvm.Core.Test/
git commit -m "feat(analysis): emit new DOM structure with CSS/SortableJS refs"
```

---

## Chunk 2: Core JS — State, Drag-Drop Panel, Collection

### Task 4: Write comprehensive tests for new functions (RED phase)

Write all tests before any implementation. This is the RED phase of TDD.

**Files:**
- Modify: `test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js`

**`makeEnv()` integration:** All new tests access internal functions via the existing `makeEnv()` pattern. The rewritten JS will expose new functions through `_test` on the `wtmAnalysis` object:
```javascript
const { wa } = makeEnv(); // wa = window.wtmAnalysis
const _test = wa._test;   // { buildPillHtml, collectDropZoneData, buildSummaryBar, escapeHtml, ... }
```

- [ ] **Step 1: Write tests for `buildPillHtml`**

```javascript
describe('buildPillHtml', function () {
  var _test;
  beforeEach(function () {
    var env = makeEnv();
    _test = env.wa._test;
  });

  test('dimension pill has correct class and data attributes', function () {
    var field = { fieldName: 'Region', title: '地區', isDimension: true };
    var html = _test.buildPillHtml(field, 'dim');
    expect(html).toContain('class="analysis-pill analysis-pill--dim"');
    expect(html).toContain('data-field-name="Region"');
    expect(html).toContain('data-kind="dim"');
    expect(html).toContain('地區');
  });

  test('measure pill includes aggregate function select', function () {
    var field = { fieldName: 'Amount', title: '金額', isMeasure: true, allowedFuncs: ['Sum', 'Avg', 'Count'] };
    var html = _test.buildPillHtml(field, 'msr');
    expect(html).toContain('analysis-pill--msr');
    expect(html).toContain('<select');
    expect(html).toContain('Sum');
    expect(html).toContain('Avg');
  });

  test('date dimension pill includes hierarchy select', function () {
    var field = { fieldName: 'OrderDate', title: '日期', isDimension: true, isDate: true };
    var html = _test.buildPillHtml(field, 'dim');
    expect(html).toContain('<select');
    expect(html).toContain('Year');
    expect(html).toContain('Month');
  });

  test('pill includes remove button', function () {
    var field = { fieldName: 'Region', title: '地區', isDimension: true };
    var html = _test.buildPillHtml(field, 'dim');
    expect(html).toContain('analysis-pill__remove');
    expect(html).toContain('✕');
  });

  test('XSS: field name with HTML chars is escaped', function () {
    var field = { fieldName: '<script>alert(1)</script>', title: '<b>xss</b>', isDimension: true };
    var html = _test.buildPillHtml(field, 'dim');
    expect(html).not.toContain('<script>');
    expect(html).not.toContain('<b>');
    expect(html).toContain('&lt;');
  });
});
```

- [ ] **Step 2: Write tests for `collectDropZoneData`**

```javascript
describe('collectDropZoneData', function () {
  test('reads dimension pills from drop zone', function () {
    // Setup: create mock drop zone DOM with 2 dim pills
    var data = _test.collectDropZoneData('testGrid');
    expect(data.dims).toHaveLength(2);
    expect(data.dims[0]).toHaveProperty('fieldName');
  });

  test('reads measure pills with selected aggregate function', function () {
    // Setup: create mock drop zone with 1 msr pill, select set to 'Avg'
    var data = _test.collectDropZoneData('testGrid');
    expect(data.msrs[0].func).toBe('Avg');
  });

  test('reads dimHierarchies from date dimension selects', function () {
    // Setup: date dim pill with hierarchy select set to 'Month'
    var data = _test.collectDropZoneData('testGrid');
    expect(data.dimHierarchies).toEqual({ OrderDate: 'Month' });
  });

  test('reads pivotDim when pivot mode enabled', function () {
    // Setup: pivot enabled, one dim pill has radio checked
    var data = _test.collectDropZoneData('testGrid');
    expect(data.pivotDim).toBe('Region');
  });

  test('returns empty arrays when drop zones are empty', function () {
    var data = _test.collectDropZoneData('emptyGrid');
    expect(data.dims).toEqual([]);
    expect(data.msrs).toEqual([]);
    expect(data.dimHierarchies).toEqual({});
    expect(data.pivotDim).toBeNull();
  });
});
```

- [ ] **Step 3: Write tests for `buildSummaryBar`**

```javascript
describe('buildSummaryBar', function () {
  test('shows dimension and measure count', function () {
    var bar = _test.buildSummaryBar('testGrid');
    expect(bar).toContain('2 維度');
    expect(bar).toContain('1 指標');
  });

  test('shows field names as mini pills', function () {
    var bar = _test.buildSummaryBar('testGrid');
    expect(bar).toContain('地區');
    expect(bar).toContain('金額');
  });
});
```

- [ ] **Step 4: Write tests for SortableJS lifecycle**

```javascript
describe('SortableJS lifecycle', function () {
  test('destroy is called on existing instances before re-init', function () {
    // Setup: _state[gridId].sortableInstances populated with mock destroy()
    // Call renderPanel again
    // Assert: destroy() was called on all previous instances
  });

  test('sortableInstances are stored in state after renderPanel', function () {
    // After renderPanel, _state[gridId].sortableInstances should have 3 entries
    // (fieldList, dimZone, msrZone)
  });

  test('toggle off destroys sortable instances', function () {
    // Call toggle to hide → sortableInstances should be destroyed and cleared
  });
});
```

- [ ] **Step 5: Write tests for multi-grid isolation**

```javascript
describe('multi-grid isolation', function () {
  test('two grids have independent state', function () {
    // Init two grids: gridA, gridB
    // Add dim to gridA
    // Assert gridB dims unchanged
  });

  test('collapsing one grid does not affect another', function () {
    // Collapse gridA panel
    // Assert gridB panel still expanded
  });

  test('query on gridA does not affect gridB result block', function () {
    // Query gridA
    // Assert gridB result block untouched
  });
});
```

- [ ] **Step 6: Write tests for drop zone validation**

```javascript
describe('drop zone validation', function () {
  test('max 3 dimensions enforced', function () {
    // Add 3 dims, attempt 4th
    // Assert: 4th rejected, layer.msg called
  });

  test('max 3 measures enforced', function () {
    // Add 3 msrs, attempt 4th
    // Assert: 4th rejected
  });

  test('removing a pill re-enables adding', function () {
    // Add 3 dims, remove 1, add 1 more
    // Assert: succeeds
  });
});
```

- [ ] **Step 7: Run all new tests to verify they FAIL**

```bash
cd test/WalkingTec.Mvvm.Js.Tests && npm test -- --testPathPattern="framework_analysis" 2>&1 | tail -30
```
Expected: New tests FAIL (functions don't exist yet), existing pure function tests still PASS.

- [ ] **Step 8: Commit test-only changes**

```bash
git add test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js
git commit -m "test(analysis): add comprehensive tests for v2 drag-drop panel (RED)"
```

### Task 5: Implement `framework_analysis.js` rewrite (GREEN phase)

This is the GREEN phase — write minimal implementation to make all tests pass.

**Files:**
- Modify: `src/WalkingTec.Mvvm.Mvc/framework_analysis.js` (full rewrite)

- [ ] **Step 1: Rewrite framework_analysis.js**

Rewrite the entire file preserving:
- IIFE pattern `(function(window) { ... })(window);`
- `global.wtmAnalysis = { toggle, _test: { ... } }` public API
- All pure functions: `detectChartType`, `validateSelection`, `buildDrillFilter`, `autoDowngradeHierarchy`, `parseFuncs`, `formatDateKey`, `clearChildren`
- `collectSearcherFormData` (PR #254)

New/changed sections:
1. **State structure** — per spec's `_state[gridId]` definition
2. **`toggle(gridId, listVmType)`** — show/hide panel, load meta if needed (same API, new DOM). **On hide: destroy SortableJS instances** via `_state[gridId].sortableInstances.forEach(s => s.destroy()); _state[gridId].sortableInstances = [];`
3. **`loadMeta(gridId, listVmType, panelEl)`** — fetch `/_analysis/meta`, then call `renderPanel`
4. **`renderPanel(gridId, fields, panelEl)`** — completely new:
   - **First: destroy any existing SortableJS instances** (`_state[gridId].sortableInstances.forEach(s => s.destroy())`)
   - Creates field pill list (`.analysis-field-list`)
   - Creates dimension drop zone (`.analysis-dropzone--dim`)
   - Creates measure drop zone (`.analysis-dropzone--msr`)
   - Initializes SortableJS on all three (field list = `pull: 'clone'`, drop zones = `put: true`)
   - **Stores instances**: `_state[gridId].sortableInstances = [fieldListSortable, dimSortable, msrSortable]`
   - Creates button row (query, Excel, CSV, pivot toggle, chart export toggle)
   - Creates collapse/expand toggle
5. **`buildPillHtml(field, kind)`** — returns HTML string for a pill element. **Must escape field name/title** via `escapeHtml()` helper to prevent XSS:
   - For dimensions: optional date hierarchy `<select>` if `isDate`
   - For measures: aggregate function `<select>` based on `allowedFuncs`
   - ✕ remove button
   - `data-field-name`, `data-kind` attributes
6. **`escapeHtml(str)`** — new helper, escapes `<>&"'` for safe DOM insertion
7. **`onDrop(evt, gridId)`** — SortableJS `onAdd` handler:
   - Updates `_state[gridId].dims` / `_state[gridId].msrs`
   - Marks source pill as `.analysis-pill--used`
   - Validates max 3 dims / 3 msrs
8. **`onRemove(evt, gridId)`** — SortableJS `onRemove` handler:
   - Restores source pill from `.analysis-pill--used`
   - Updates state
9. **`collectDropZoneData(gridId)`** — reads pills from drop zones, returns `{ dims, msrs, dimHierarchies, pivotDim }` (includes pivot dimension when pivot mode is active)
10. **`query(gridId)`** — POST `/_analysis/query` with data from `collectDropZoneData` + `collectSearcherFormData`
    - On success: render result to `#analysis-result-block-{gridId}` (independent block)
    - Auto-collapse field selector to summary bar
11. **`renderResultBlock(gridId, result, req, dimFields)`** — renders into the independent result block:
    - Header with chart type buttons + drill breadcrumb
    - Chart (reuse existing `renderChart` logic)
    - Aggregated table (reuse existing `renderTable` logic)
    - Collapse/expand toggle
12. **`buildSummaryBar(gridId)`** — creates the collapsed summary showing selected dims/msrs as mini pills
13. **`collapsePanel(gridId)` / `expandPanel(gridId)`** — toggle field selector
14. **`collapseResult(gridId)` / `expandResult(gridId)`** — toggle result block
15. **`drillQuery`**, **`drillDown`**, **`drillBack`**, **`drillReset`** — same logic, adapted to new result block DOM
16. **`exportData(gridId, format)`** — same logic, uses `collectDropZoneData` instead of old `collectSelection`
17. **Pivot mode** — same checkbox + radio logic, adapted to pill UI; `collectDropZoneData` reads `pivotDim` from radio state

- [ ] **Step 2: Run all tests**

```bash
cd test/WalkingTec.Mvvm.Js.Tests && npm test -- --testPathPattern="framework_analysis"
```
Expected: ALL tests pass (old pure functions + new Task 4 tests).

- [ ] **Step 3: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/framework_analysis.js
git commit -m "feat(analysis): rewrite to drag-and-drop BI panel with SortableJS (GREEN)"
```

---

## Chunk 3: Integration Testing + Polish

### Task 6: Update existing DOM/interaction tests

**Files:**
- Modify: `test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js`

- [ ] **Step 1: Update DOM test setup**

Existing tests use `makeEnv()` to create a VM context with mock DOM. Update:
- Mock `Sortable` global (SortableJS creates `new Sortable(el, options)`)
- Update DOM structure expectations (new class names, new elements)
- Update `toggle()` tests to check new panel structure
- Update `query()` tests to verify `collectDropZoneData` integration
- Keep all chart rendering tests (they operate on result data, not DOM)

- [ ] **Step 2: Add interaction tests for drag-drop lifecycle**

New tests for:
- Pill drag from field list to dimension zone → field marked as used
- Pill drag from field list to measure zone → func select appears
- Pill remove (✕ click) → field restored in list
- Summary bar shows correct pills after collapse
- Collapse/expand panel toggle
- Collapse/expand result block toggle
- Pivot mode: radio appears on dim pills when toggle checked
- Export uses drop zone data (not old checkboxes)

Note: max dims/msrs validation and multi-grid isolation tests were already added in Task 4.

- [ ] **Step 3: Run full test suite**

```bash
# JS tests
cd test/WalkingTec.Mvvm.Js.Tests && npm test

# .NET Analysis tests
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~Analysis" -c Release -v normal

# Full build
dotnet build WalkingTec.Mvvm.sln -c Release
```
Expected: ALL pass.

- [ ] **Step 4: Commit**

```bash
git add test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js
git commit -m "test(analysis): update existing DOM tests for drag-and-drop panel v2"
```

### Task 7: Manual smoke test with demo

- [ ] **Step 1: Start demo**

```bash
cd demo/WalkingTec.Mvvm.Demo
$HOME/.dotnet/dotnet run --urls "http://0.0.0.0:52838"
```

- [ ] **Step 2: Test checklist**

Open http://localhost:52838, login admin/000000:

1. Navigate to Order list page
2. Click "分析模式" button → field selector panel appears below search, grid still visible
3. Drag "地區" pill to dimension drop zone → pill turns green, source greys out
4. Drag "金額" to measure drop zone → pill turns blue with Sum select
5. Click "查詢" → chart + table appear in independent result block, field selector collapses to summary bar
6. Click "▼ 展開" on summary bar → field selector expands
7. Click "▲ 收合" on result block → chart hides
8. Test with search filter: set Status=Completed → click search → click 查詢 in analysis → verify chart only shows completed orders
9. Test drill-down: click a bar in chart → drill breadcrumb appears
10. Test export: click Excel → file downloads
11. Test pivot mode: toggle on → radio buttons appear on dimension pills

- [ ] **Step 3: Fix any issues found in smoke test**

- [ ] **Step 4: Final commit (only if changes were made)**

```bash
# Stage only the specific files that were modified during smoke test fixes
git add src/WalkingTec.Mvvm.Mvc/framework_analysis.js src/WalkingTec.Mvvm.Mvc/framework_analysis.css test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js
git commit -m "fix(analysis): polish from smoke testing"
```

### Task 8: Push and open PR

- [ ] **Step 1: Push branch**

Replace `XXX` with the actual issue number from Task 0.

```bash
git push -u origin feat/issue-XXX-analysis-v2
```

- [ ] **Step 2: Create PR**

Replace `XXX` with the actual issue number from Task 0.

```bash
gh pr create --base dotnet8 --title "feat(analysis): drag-and-drop BI panel (Analysis Mode v2)" --body "$(cat <<'EOF'
## Summary
- Replace checkbox UI with drag-and-drop field pills (SortableJS)
- Analysis panel, chart results, and data grid coexist (not toggle)
- Three independently collapsible sections
- Field selector auto-collapses to summary bar after query

Closes #XXX

## Changes
- Rewrite `framework_analysis.js` (~1000 lines)
- New `framework_analysis.css`
- Add `sortable.min.js` (v1.15.6, MIT, 10KB gzip)
- Update `DataTableTagHelper.cs` DOM emission
- Update JS tests (new: pill, drop zone, lifecycle, multi-grid, XSS)

## Test plan
- [ ] All JS analysis tests pass (old + new)
- [ ] All .NET analysis tests pass
- [ ] Manual: drag-drop, collapse, search filter, drill, export, pivot

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3: Link PR to issue**

Add PR URL as a comment on the issue:
```bash
gh issue comment XXX --repo cct08311github/WalkingTec.Mvvm --body "PR: <PR_URL>"
```
