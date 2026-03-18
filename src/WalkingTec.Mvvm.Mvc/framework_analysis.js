/**
 * framework_analysis.js
 * WTM Analysis Mode v2 — 拖拉式 BI 面板控制器
 *
 * 安全原則：所有來自伺服器的欄位名稱與值均透過 textContent 或 DOM 方法設值，
 * 禁止直接拼入 HTML 字串，以防 XSS。
 */
(function (window) {
    'use strict';

    // ─── i18n ─────────────────────────────────────────────────────────────────
    var _LOCALES = {
        'zh-TW': {
            'err.dimRequired':   '至少需要選取 1 個維度進行分組',
            'err.msrRequired':   '至少需要選取 1 個度量指標',
            'err.dimMax':        '維度最多選 3 個',
            'err.msrMax':        '度量最多選 3 個',
            'scale.yi':          '億',
            'scale.baiwan':      '百萬',
            'scale.wan':         '萬',
            'func.sum':          '合計',
            'func.count':        '計數',
            'func.avg':          '平均',
            'func.max':          '最大',
            'func.min':          '最小',
            'err.unknown':       '發生未知錯誤，請稍後再試。',
            'date.selectLevel':  '選擇日期分組層級',
            'date.year':         '年',
            'date.quarter':      '季',
            'date.month':        '月',
            'date.day':          '日',
            'panel.dimLabel':    '維度：',
            'panel.msrLabel':    '度量：',
            'panel.loadError':   '載入欄位失敗：',
            'panel.title':       '\u{1F4CA} 分析欄位選擇器',
            'panel.dimZone':     '維度（最多 3 個）',
            'panel.dimPh':       '拖入維度欄位...',
            'panel.msrZone':     '度量（最多 3 個）',
            'panel.msrPh':       '拖入度量欄位...',
            'panel.poolLabel':   '可用欄位（拖拉至上方區域）',
            'btn.query':         '查詢',
            'btn.exportXlsx':    '匯出 Excel',
            'btn.exportCsv':     '匯出 CSV',
            'btn.chartExport':   '含圖表',
            'btn.pivot':         '樞紐模式',
            'btn.addFilter':     '+ 新增篩選條件',
            'btn.clearFilter':   '清除篩選',
            'btn.reQuery':       '重新查詢',
            'btn.reset':         '重置',
            'panel.resultTitle': '分析結果',
            'chart.bar':         '長條圖',
            'chart.line':        '折線圖',
            'chart.pie':         '圓餅圖',
            'chart.barStacked':  '堆疊長條圖',
            'chart.card':        '數字卡片',
            'chart.scatter':     '散點圖',
            'op.eq':             '等於',
            'op.neq':            '不等於',
            'op.gt':             '大於',
            'op.gte':            '大於等於',
            'op.lt':             '小於',
            'op.lte':            '小於等於',
            'op.contains':       '包含',
            'op.notContains':    '不包含',
            'filter.selectEmpty':'-- 請選擇 --',
            'filter.valuePh':    '篩選值\u2026',
            'filter.fieldPh':    '選擇欄位\u2026',
            'pivot.selectDim':   '請選擇一個樞紐(Pivot)維度',
            'pivot.notSelected': '樞紐(Pivot)維度必須是已勾選的維度之一',
            'query.progress':    '查詢中... {t}s',
            'query.cancelled':   '查詢已取消',
            'query.failed':      '查詢失敗：',
            'warn.dataExceeds':  '\u26a0 來源資料超過 50,000 筆，已截斷。聚合結果（合計、平均等）可能不準確。',
            'warn.truncated':    '結果已截斷，僅顯示前 10,000 列。',
            'result.empty':      '查無符合條件的資料，請調整篩選條件後重試。',
            'result.total':      '總計',
            'drill.all':         '全部',
            'drill.up':          '返回上層',
            'export.truncated':  '\u26a0\ufe0f 匯出資料已截斷，僅包含前 10,000 筆結果。完整資料請聯繫管理員。',
            'export.failed':     '匯出失敗：',
            'pool.searchPh':     '搜尋欄位...',
            'pool.noMatch':      '無符合欄位',
            'dep.missingTitle':  'Analysis 模組缺少必要前端依賴，部分功能將無法使用：',
            'dep.echarts':       'ECharts — 請在 _Layout.cshtml 中加入：',
            'dep.sortable':      'SortableJS — 請在 _Layout.cshtml 中加入：',
            'warn.truncatedFmt': '結果已截斷，僅顯示前 10,000 列（共 {n} 組）。'
        },
        'en-US': {
            'err.dimRequired':   'At least 1 dimension required for grouping',
            'err.msrRequired':   'At least 1 measure required',
            'err.dimMax':        'Maximum 3 dimensions allowed',
            'err.msrMax':        'Maximum 3 measures allowed',
            'scale.yi':          '100M',
            'scale.baiwan':      'M',
            'scale.wan':         '10K',
            'func.sum':          'Sum',
            'func.count':        'Count',
            'func.avg':          'Avg',
            'func.max':          'Max',
            'func.min':          'Min',
            'err.unknown':       'An unknown error occurred, please try again.',
            'date.selectLevel':  'Select date grouping level',
            'date.year':         'Year',
            'date.quarter':      'Quarter',
            'date.month':        'Month',
            'date.day':          'Day',
            'panel.dimLabel':    'Dimensions:',
            'panel.msrLabel':    'Measures:',
            'panel.loadError':   'Failed to load fields: ',
            'panel.title':       '\u{1F4CA} Analysis Field Selector',
            'panel.dimZone':     'Dimensions (max 3)',
            'panel.dimPh':       'Drag dimension fields here...',
            'panel.msrZone':     'Measures (max 3)',
            'panel.msrPh':       'Drag measure fields here...',
            'panel.poolLabel':   'Available Fields (drag to zones above)',
            'btn.query':         'Query',
            'btn.exportXlsx':    'Export Excel',
            'btn.exportCsv':     'Export CSV',
            'btn.chartExport':   'Include Chart',
            'btn.pivot':         'Pivot Mode',
            'btn.addFilter':     '+ Add Filter',
            'btn.clearFilter':   'Clear Filters',
            'btn.reQuery':       'Re-Query',
            'btn.reset':         'Reset',
            'panel.resultTitle': 'Analysis Results',
            'chart.bar':         'Bar Chart',
            'chart.line':        'Line Chart',
            'chart.pie':         'Pie Chart',
            'chart.barStacked':  'Stacked Bar',
            'chart.card':        'Card',
            'chart.scatter':     'Scatter',
            'op.eq':             'Equals',
            'op.neq':            'Not Equals',
            'op.gt':             'Greater than',
            'op.gte':            'Greater or equal',
            'op.lt':             'Less than',
            'op.lte':            'Less or equal',
            'op.contains':       'Contains',
            'op.notContains':    'Not Contains',
            'filter.selectEmpty':'-- Select --',
            'filter.valuePh':    'Filter value\u2026',
            'filter.fieldPh':    'Select field\u2026',
            'pivot.selectDim':   'Please select a Pivot dimension',
            'pivot.notSelected': 'Pivot dimension must be one of the selected dimensions',
            'query.progress':    'Querying... {t}s',
            'query.cancelled':   'Query cancelled',
            'query.failed':      'Query failed: ',
            'warn.dataExceeds':  '\u26a0 Source data exceeds 50,000 rows and was truncated. Aggregates (sum, avg, etc.) may be inaccurate.',
            'warn.truncated':    'Results truncated, showing first 10,000 rows.',
            'result.empty':      'No data found. Please adjust your filters and try again.',
            'result.total':      'Total',
            'drill.all':         'All',
            'drill.up':          'Go Up',
            'export.truncated':  '\u26a0\ufe0f Export truncated to first 10,000 rows. Contact admin for full data.',
            'export.failed':     'Export failed: ',
            'pool.searchPh':     'Search fields...',
            'pool.noMatch':      'No matching fields',
            'dep.missingTitle':  'Analysis module is missing required frontend dependencies, some features will not work:',
            'dep.echarts':       'ECharts — add to _Layout.cshtml:',
            'dep.sortable':      'SortableJS — add to _Layout.cshtml:',
            'warn.truncatedFmt': 'Results truncated, showing first 10,000 rows ({n} groups total).'
        }
    };
    (function () {
        var ext = (typeof window !== 'undefined' && window.WTM_ANALYSIS_I18N) || {};
        Object.keys(ext).forEach(function (loc) {
            if (!_LOCALES[loc]) _LOCALES[loc] = {};
            Object.assign(_LOCALES[loc], ext[loc]);
        });
    }());
    var _locale = (typeof window !== 'undefined' && window.WTM_ANALYSIS_LOCALE) || 'zh-TW';
    function _i18n(key) {
        var tbl = _LOCALES[_locale] || _LOCALES['zh-TW'];
        return tbl[key] !== undefined ? tbl[key] : (_LOCALES['zh-TW'][key] !== undefined ? _LOCALES['zh-TW'][key] : key);
    }


    // ─── 純函式（無副作用）──────────────────────────────────────────────────

    function detectChartType(dims, msrs) {
        if (dims.length === 0) return 'card';
        if (dims.some(function (d) { return d.isDate; })) return 'line';
        if (dims.length >= 2) return 'bar-stacked';
        return 'bar';
    }

    function validateSelection(dims, msrs) {
        var errors = [];
        if (dims.length === 0) errors.push(_i18n('err.dimRequired'));
        if (msrs.length === 0) errors.push(_i18n('err.msrRequired'));
        if (dims.length > 3) errors.push(_i18n('err.dimMax'));
        if (msrs.length > 3) errors.push(_i18n('err.msrMax'));
        return errors;
    }

    function buildDrillFilter(dimField, value) {
        return { field: dimField, operator: 'Eq', value: value };
    }

    function nextHierarchy(current) {
        var map = { Year: 'Quarter', Quarter: 'Month', Month: 'Day' };
        return map[current] || null;
    }

    function formatDateKey(key) {
        var s = String(key);
        if (s.length === 4) return s;
        if (s.length === 5) return s.substring(0, 4) + ' Q' + s.substring(4);
        if (s.length === 6) return s.substring(0, 4) + '-' + s.substring(4);
        if (s.length === 8) return s.substring(0, 4) + '-' + s.substring(4, 6) + '-' + s.substring(6);
        return s;
    }

    function computeScale(maxVal) {
        var abs = Math.abs(maxVal) || 0;
        if (abs >= 100000000) return { divisor: 100000000, unit: _i18n('scale.yi') };
        if (abs >= 1000000)   return { divisor: 1000000, unit: _i18n('scale.baiwan') };
        if (abs >= 10000)     return { divisor: 10000, unit: _i18n('scale.wan') };
        return { divisor: 1, unit: '' };
    }

    function scaleSeriesData(data, divisor) {
        if (divisor == null || !isFinite(divisor) || divisor <= 1) return data;
        return data.map(function (v) {
            return v == null ? null : v / divisor;
        });
    }

    function detectDualAxis(rows, measures) {
        if (!measures || measures.length !== 2) return false;
        if (!rows || rows.length === 0) return false;
        var key0 = measures[0].field + '_' + measures[0].func;
        var key1 = measures[1].field + '_' + measures[1].func;
        var max0 = 0, max1 = 0;
        for (var i = 0; i < rows.length; i++) {
            var raw0 = rows[i][key0];
            var raw1 = rows[i][key1];
            var v0 = (typeof raw0 === 'number' && isFinite(raw0)) ? Math.abs(raw0) : 0;
            var v1 = (typeof raw1 === 'number' && isFinite(raw1)) ? Math.abs(raw1) : 0;
            if (v0 > max0) max0 = v0;
            if (v1 > max1) max1 = v1;
        }
        if (max0 === 0 || max1 === 0) return false;
        var ratio = max0 > max1 ? max0 / max1 : max1 / max0;
        return ratio >= 10;
    }

    function clearChildren(el) {
        while (el.firstChild) el.removeChild(el.firstChild);
    }

    var _FUNC_FLAGS = [
        { value: 1, name: 'Count' },
        { value: 2, name: 'Sum' },
        { value: 4, name: 'Avg' },
        { value: 8, name: 'Max' },
        { value: 16, name: 'Min' }
    ];

    function parseFuncs(flags) {
        flags = flags | 0;
        return _FUNC_FLAGS
            .filter(function (f) { return (flags & f.value) !== 0; })
            .map(function (f) { return f.name; });
    }

    function _FUNC_LABEL_MAP_fn(f) { var m = { Sum: _i18n('func.sum'), Count: _i18n('func.count'), Avg: _i18n('func.avg'), Max: _i18n('func.max'), Min: _i18n('func.min') }; return m[f] || f; }

    function getFuncLabel(func) {
        return _FUNC_LABEL_MAP_fn(func);
    }

    function formatNumeric(val) {
        var n = Number(val);
        if (isNaN(n)) return String(val);
        return n.toLocaleString(_locale, { maximumFractionDigits: 2 });
    }

    function showMsg(msg) {
        if (window.layui && window.layui.layer) {
            window.layui.layer.msg(msg);
        } else if (window.layer) {
            window.layer.msg(msg);
        } else {
            console.warn('[wtmAnalysis]', msg);
        }
    }

    // Parse a server error into a human-friendly message.
    // Handles ProblemDetails JSON ({"title":"...","detail":"..."}) as well as plain strings.
    function parseFriendlyError(err) {
        var raw = (err && err.message != null) ? err.message : String(err);
        try {
            var pd = JSON.parse(raw);
            if (pd && (pd.detail || pd.title)) {
                return pd.detail || pd.title;
            }
        } catch (_) { /* not JSON — fall through */ }
        return raw || _i18n('err.unknown');
    }

    // ─── 狀態 ─────────────────────────────────────────────────────────────────
    var _state = {};

    function collectSearcherFormData(gridId) {
        if (typeof ff === 'undefined' || typeof ff.GetSearchFormData !== 'function') return undefined;
        var formId = gridId.replace(/^wtTable_/, 'wtForm_');
        var formEl = document.getElementById(formId);
        if (!formEl) return undefined;
        var data = ff.GetSearchFormData(formId, 'Searcher');
        if (!data || Object.keys(data).length === 0) return undefined;
        // Strip empty-string values — they cause DateTime? to deserialize
        // as DateTime.MinValue instead of null, silently filtering out all rows.
        var cleaned = {};
        var hasValue = false;
        Object.keys(data).forEach(function (k) {
            var v = data[k];
            if (v !== '' && v !== null && v !== undefined) {
                cleaned[k] = v;
                hasValue = true;
            }
        });
        return hasValue ? JSON.stringify(cleaned) : undefined;
    }

    // ─── SortableJS 參照 ──────────────────────────────────────────────────────
    var Sortable = (typeof window !== 'undefined' && window.Sortable) ||
                   (typeof global !== 'undefined' && global.Sortable) || null;

    // ─── 依賴檢查 ─────────────────────────────────────────────────────────────
    /**
     * 檢查 ECharts 和 SortableJS 是否已載入。
     * 若缺少任一依賴，在 panel 頂端插入可見的警告 div（每個 panel 只插入一次）。
     * @param {Element} panel  Analysis 面板根元素
     */
    function checkDependencies(panel) {
        var missing = [];
        if (typeof window.echarts === 'undefined') {
            missing.push(_i18n('dep.echarts') +
                '<script src="https://cdn.jsdelivr.net/npm/echarts@5"></script>');
        }
        if (!Sortable) {
            missing.push(_i18n('dep.sortable') +
                '<script src="https://cdn.jsdelivr.net/npm/sortablejs@1"></script>');
        }
        if (missing.length === 0) return;

        // 每個 panel 只顯示一次
        if (panel.querySelector && panel.querySelector('.wtm-analysis-dep-warn')) return;

        var warn = document.createElement('div');
        warn.className = 'wtm-analysis-dep-warn';
        warn.style.cssText =
            'background:#fff3cd;border:1px solid #ffc107;padding:8px 12px;' +
            'margin-bottom:8px;border-radius:4px;font-size:13px;line-height:1.5;';

        var title = document.createElement('strong');
        title.textContent = _i18n('dep.missingTitle');
        warn.appendChild(title);

        missing.forEach(function (dep) {
            var p = document.createElement('p');
            p.style.cssText = 'margin:4px 0 0 8px;';
            p.textContent = '• ' + dep;
            warn.appendChild(p);
        });

        if (typeof panel.insertBefore === 'function') {
            panel.insertBefore(warn, panel.firstChild);
        } else {
            panel.appendChild(warn);
        }
    }

    // ─── Pill 建立 ────────────────────────────────────────────────────────────

    function createPoolPill(gridId, field) {
        var pill = document.createElement('span');
        pill.className = 'analysis-pill analysis-pill--available';
        pill.dataset.fieldName = field.fieldName;
        pill.dataset.displayName = field.displayName;
        pill.dataset.kind = field.kind;
        pill.dataset.gridId = gridId;
        if (field.isDate) pill.dataset.isDate = 'true';
        if (field.allowedFuncs) pill.dataset.allowedFuncs = String(field.allowedFuncs);
        pill.textContent = field.displayName;
        return pill;
    }

    function createDropZonePill(gridId, field, kind) {
        var pill = document.createElement('span');
        pill.className = 'analysis-pill analysis-pill--' + (kind === 'Dimension' ? 'dim' : 'msr');
        pill.dataset.fieldName = field.fieldName;
        pill.dataset.displayName = field.displayName;
        pill.dataset.kind = kind;
        pill.dataset.gridId = gridId;
        if (field.isDate) pill.dataset.isDate = 'true';

        var nameSpan = document.createElement('span');
        nameSpan.textContent = field.displayName;
        pill.appendChild(nameSpan);

        if (kind === 'Dimension' && field.isDate) {
            var hSel = document.createElement('select');
            hSel.className = 'analysis-hierarchy-select';
            hSel.dataset.field = field.fieldName;
            hSel.title = _i18n('date.selectLevel');
            [
                { value: 'Year', text: _i18n('date.year') },
                { value: 'Quarter', text: _i18n('date.quarter') },
                { value: 'Month', text: _i18n('date.month') },
                { value: 'Day', text: _i18n('date.day') }
            ].forEach(function (h) {
                var opt = document.createElement('option');
                opt.value = h.value;
                opt.textContent = h.text;
                if (h.value === 'Month') opt.selected = true;
                hSel.appendChild(opt);
            });
            pill.appendChild(hSel);
        }

        if (kind === 'Dimension') {
            var pivotRadio = document.createElement('input');
            pivotRadio.type = 'radio';
            pivotRadio.name = 'pivot-dim-' + gridId;
            pivotRadio.value = field.fieldName;
            pivotRadio.className = 'analysis-pivot-radio analysis-pivot-dim-select';
            pivotRadio.dataset.gridId = gridId;
            pill.appendChild(pivotRadio);
        }

        if (kind === 'Measure') {
            var funcs = parseFuncs(field.allowedFuncs || 0);
            if (funcs.length <= 1) {
                pill.dataset.defaultFunc = funcs[0] || 'Sum';
            } else {
                var funcSel = document.createElement('select');
                funcSel.className = 'analysis-func-select';
                funcs.forEach(function (fn) {
                    var opt = document.createElement('option');
                    opt.value = fn;
                    opt.textContent = fn;
                    funcSel.appendChild(opt);
                });
                pill.appendChild(funcSel);
            }
        }

        var removeBtn = document.createElement('span');
        removeBtn.className = 'pill-remove';
        removeBtn.textContent = '\u2715';
        removeBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            removePillFromZone(gridId, pill);
        });
        pill.appendChild(removeBtn);

        return pill;
    }

    function removePillFromZone(gridId, pill) {
        var fieldName = pill.dataset.fieldName;
        if (pill.parentNode) pill.parentNode.removeChild(pill);
        markPoolPill(gridId, fieldName, false);
        updatePlaceholders(gridId);
        updateSummaryBar(gridId);
    }

    function markPoolPill(gridId, fieldName, used) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;
        var pool = panel.querySelector('.analysis-field-pool');
        if (!pool) return;
        var pills = pool.querySelectorAll('.analysis-pill');
        for (var i = 0; i < pills.length; i++) {
            if (pills[i].dataset.fieldName === fieldName) {
                if (used) {
                    pills[i].classList.remove('analysis-pill--available');
                    pills[i].classList.add('analysis-pill--used');
                } else {
                    pills[i].classList.remove('analysis-pill--used');
                    pills[i].classList.add('analysis-pill--available');
                }
            }
        }
    }

    // Filter field pool pills by a search term (case-insensitive substring on displayName).
    // Pills that don't match are hidden; all are shown when term is empty.
    // Returns the count of visible pills.
    function filterPoolPills(gridId, term) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return 0;
        var pool = panel.querySelector('.analysis-field-pool');
        if (!pool) return 0;
        var pills = pool.querySelectorAll('.analysis-pill');
        var lower = term ? term.toLowerCase() : '';
        var visible = 0;
        for (var i = 0; i < pills.length; i++) {
            var name = (pills[i].dataset.displayName || '').toLowerCase();
            var match = !lower || name.indexOf(lower) !== -1;
            pills[i].style.display = match ? '' : 'none';
            if (match) visible++;
        }
        // Show/hide the "no match" empty state element
        var noMatch = pool.querySelector('.analysis-pool-no-match');
        if (noMatch) noMatch.style.display = (visible === 0 && lower) ? '' : 'none';
        return visible;
    }

    function updatePlaceholders(gridId) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;
        ['dim', 'msr'].forEach(function (kind) {
            var zone = panel.querySelector('.analysis-dropzone--' + kind);
            if (!zone) return;
            var ph = zone.querySelector('.analysis-dropzone-placeholder');
            var pills = zone.querySelectorAll('.analysis-pill');
            if (ph) ph.style.display = pills.length > 0 ? 'none' : '';
        });
    }

    function updateSummaryBar(gridId) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;
        var bar = panel.querySelector('.analysis-summary-bar');
        if (!bar) return;

        var sel = collectSelection(gridId);
        clearChildren(bar);

        var st = _state[gridId];
        var fieldByName = {};
        if (st && st.fields) {
            st.fields.forEach(function (f) { fieldByName[f.fieldName] = f; });
        }

        if (sel.dims.length > 0) {
            var dimLabel = document.createElement('span');
            dimLabel.className = 'summary-label';
            dimLabel.textContent = _i18n('panel.dimLabel');
            bar.appendChild(dimLabel);
            sel.dims.forEach(function (d) {
                var meta = fieldByName[d];
                var tag = document.createElement('span');
                tag.textContent = '[' + ((meta && meta.displayName) || d) + ']';
                bar.appendChild(tag);
            });
        }
        if (sel.msrs.length > 0) {
            var msrLabel = document.createElement('span');
            msrLabel.className = 'summary-label';
            msrLabel.textContent = _i18n('panel.msrLabel');
            bar.appendChild(msrLabel);
            sel.msrs.forEach(function (m) {
                var meta = fieldByName[m.field];
                var tag = document.createElement('span');
                tag.textContent = '[' + ((meta && meta.displayName) || m.field) + ' ' + getFuncLabel(m.func) + ']';
                bar.appendChild(tag);
            });
        }
    }

    // ─── 摺疊邏輯 ──────────────────────────────────────────────────────────────

    function toggleCollapse(gridId, sectionClass, stateKey) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;
        var st = _state[gridId];
        if (!st) return;

        var body = panel.querySelector('.' + sectionClass);
        var toggle = body && body.previousElementSibling
            ? body.previousElementSibling.querySelector('.analysis-panel-toggle')
            : null;
        var summaryBar = panel.querySelector('.analysis-summary-bar');

        st[stateKey] = !st[stateKey];
        if (body) {
            if (st[stateKey]) {
                body.classList.add('collapsed');
            } else {
                body.classList.remove('collapsed');
            }
        }
        if (toggle) {
            if (st[stateKey]) {
                toggle.classList.add('collapsed');
            } else {
                toggle.classList.remove('collapsed');
            }
        }
        if (stateKey === 'collapsed' && summaryBar) {
            if (st.collapsed) {
                updateSummaryBar(gridId);
                summaryBar.classList.add('visible');
            } else {
                summaryBar.classList.remove('visible');
            }
        }
    }

    // ─── 面板渲染（v2 拖拉式）────────────────────────────────────────────────

    function toggle(gridId, listVmType) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;

        if (!_state[gridId]) {
            _state[gridId] = {
                visible: false, listVmType: listVmType, fields: null,
                drillStack: [], drillFilters: [],
                collapsed: false, resultCollapsed: false,
                pivotEnabled: false, pivotDim: null,
                dims: [], msrs: [], dimHierarchies: {},
                sortableInstances: {},
                lastResult: null, lastReq: null, lastDimFields: null, lastChartType: null,
                abortController: null
            };
        }

        var st = _state[gridId];
        if (!st.visible) {
            panel.style.display = 'block';
            st.visible = true;
            checkDependencies(panel);
            if (!st.fields) {
                loadMeta(gridId, listVmType, panel);
            }
        } else {
            panel.style.display = 'none';
            st.visible = false;
        }
    }

    function loadMeta(gridId, listVmType, panelEl) {
        fetch('/_analysis/meta?listVmType=' + encodeURIComponent(listVmType), {
            method: 'GET',
            headers: { 'Content-Type': 'application/json' }
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t || 'HTTP ' + res.status); });
            return res.json();
        })
        .then(function (fields) {
            _state[gridId].fields = fields;
            renderPanel(gridId, fields, panelEl);
        })
        .catch(function (err) {
            var msg = document.createElement('div');
            msg.className = 'layui-alert layui-alert-danger';
            msg.textContent = _i18n('panel.loadError') + parseFriendlyError(err);
            panelEl.appendChild(msg);
        });
    }

    function renderPanel(gridId, fields, panelEl) {
        clearChildren(panelEl);
        var st = _state[gridId];

        // ── Section 1: 欄位選擇器 ──
        var selectorSection = document.createElement('div');
        selectorSection.className = 'analysis-panel';

        var selectorHeader = document.createElement('div');
        selectorHeader.className = 'analysis-panel-header';
        selectorHeader.addEventListener('click', function () {
            toggleCollapse(gridId, 'analysis-panel-body', 'collapsed');
        });

        var selectorTitle = document.createElement('span');
        selectorTitle.className = 'analysis-panel-title';
        selectorTitle.textContent = _i18n('panel.title');

        var selectorToggle = document.createElement('span');
        selectorToggle.className = 'analysis-panel-toggle';
        selectorToggle.textContent = '\u25BC';

        selectorHeader.appendChild(selectorTitle);
        selectorHeader.appendChild(selectorToggle);
        selectorSection.appendChild(selectorHeader);

        var selectorBody = document.createElement('div');
        selectorBody.className = 'analysis-panel-body';

        // Drop zones row
        var dzRow = document.createElement('div');
        dzRow.className = 'analysis-dropzone-row';

        var dimGroup = document.createElement('div');
        dimGroup.className = 'analysis-dropzone-group';
        var dimLabel = document.createElement('div');
        dimLabel.className = 'analysis-dropzone-label analysis-dropzone-label--dim';
        dimLabel.textContent = _i18n('panel.dimZone');
        var dimZone = document.createElement('div');
        dimZone.className = 'analysis-dropzone analysis-dropzone--dim';
        dimZone.dataset.kind = 'Dimension';
        var dimPh = document.createElement('span');
        dimPh.className = 'analysis-dropzone-placeholder';
        dimPh.textContent = _i18n('panel.dimPh');
        dimZone.appendChild(dimPh);
        dimGroup.appendChild(dimLabel);
        dimGroup.appendChild(dimZone);

        var msrGroup = document.createElement('div');
        msrGroup.className = 'analysis-dropzone-group';
        var msrLabel = document.createElement('div');
        msrLabel.className = 'analysis-dropzone-label analysis-dropzone-label--msr';
        msrLabel.textContent = _i18n('panel.msrZone');
        var msrZone = document.createElement('div');
        msrZone.className = 'analysis-dropzone analysis-dropzone--msr';
        msrZone.dataset.kind = 'Measure';
        var msrPh = document.createElement('span');
        msrPh.className = 'analysis-dropzone-placeholder';
        msrPh.textContent = _i18n('panel.msrPh');
        msrZone.appendChild(msrPh);
        msrGroup.appendChild(msrLabel);
        msrGroup.appendChild(msrZone);

        dzRow.appendChild(dimGroup);
        dzRow.appendChild(msrGroup);
        selectorBody.appendChild(dzRow);

        // Field pool
        var poolLabel = document.createElement('div');
        poolLabel.style.cssText = 'font-size:12px;color:#999;margin-bottom:4px;';
        poolLabel.textContent = _i18n('panel.poolLabel');
        selectorBody.appendChild(poolLabel);

        // Search input for field pool
        var poolSearch = document.createElement('input');
        poolSearch.type = 'text';
        poolSearch.className = 'analysis-pool-search';
        poolSearch.placeholder = _i18n('pool.searchPh');
        if (poolSearch.setAttribute) poolSearch.setAttribute('aria-label', _i18n('pool.searchPh'));
        selectorBody.appendChild(poolSearch);

        var fieldPool = document.createElement('div');
        fieldPool.className = 'analysis-field-pool';
        fields.forEach(function (f) {
            fieldPool.appendChild(createPoolPill(gridId, f));
        });

        // Empty state message shown when search has no matches
        var noMatchEl = document.createElement('span');
        noMatchEl.className = 'analysis-pool-no-match';
        noMatchEl.style.display = 'none';
        noMatchEl.textContent = _i18n('pool.noMatch');
        fieldPool.appendChild(noMatchEl);

        poolSearch.addEventListener('input', function () {
            filterPoolPills(gridId, poolSearch.value);
        });

        selectorBody.appendChild(fieldPool);

        // Button row
        var btnRow = document.createElement('div');
        btnRow.className = 'analysis-btn-row';

        var queryBtn = document.createElement('button');
        queryBtn.type = 'button';
        queryBtn.className = 'layui-btn layui-btn-sm layui-btn-normal';
        queryBtn.textContent = _i18n('btn.query');
        queryBtn.addEventListener('click', function () { query(gridId); });

        var exportXlsxBtn = document.createElement('button');
        exportXlsxBtn.type = 'button';
        exportXlsxBtn.className = 'layui-btn layui-btn-sm';
        exportXlsxBtn.textContent = _i18n('btn.exportXlsx');
        exportXlsxBtn.addEventListener('click', function () { exportData(gridId, 'xlsx'); });

        var exportCsvBtn = document.createElement('button');
        exportCsvBtn.type = 'button';
        exportCsvBtn.className = 'layui-btn layui-btn-sm layui-btn-warm';
        exportCsvBtn.textContent = _i18n('btn.exportCsv');
        exportCsvBtn.addEventListener('click', function () { exportData(gridId, 'csv'); });

        var chartExportWrapper = document.createElement('label');
        chartExportWrapper.style.cssText = 'display:inline-flex;align-items:center;cursor:pointer;';
        var chartExportCb = document.createElement('input');
        chartExportCb.type = 'checkbox';
        chartExportCb.className = 'analysis-export-chart-cb';
        chartExportCb.dataset.gridId = gridId;
        chartExportCb.style.marginRight = '4px';
        var chartExportLabel = document.createElement('span');
        chartExportLabel.textContent = _i18n('btn.chartExport');
        chartExportWrapper.appendChild(chartExportCb);
        chartExportWrapper.appendChild(chartExportLabel);

        var pivotWrapper = document.createElement('label');
        pivotWrapper.style.cssText = 'display:inline-flex;align-items:center;cursor:pointer;';
        var pivotToggleCb = document.createElement('input');
        pivotToggleCb.type = 'checkbox';
        pivotToggleCb.className = 'analysis-pivot-toggle';
        pivotToggleCb.dataset.gridId = gridId;
        pivotToggleCb.style.marginRight = '4px';
        pivotToggleCb.addEventListener('change', function () {
            var checked = this.checked;
            st.pivotEnabled = checked;
            var radios = panelEl.querySelectorAll('.analysis-pivot-radio');
            for (var i = 0; i < radios.length; i++) {
                radios[i].style.display = checked ? 'inline-block' : 'none';
            }
            if (checked) {
                var anyChecked = panelEl.querySelector('.analysis-pivot-dim-select:checked');
                if (!anyChecked) {
                    var first = panelEl.querySelector('.analysis-pivot-dim-select');
                    if (first) first.checked = true;
                }
            }
        });
        var pivotText = document.createElement('span');
        pivotText.textContent = _i18n('btn.pivot');
        pivotText.style.fontWeight = 'bold';
        pivotWrapper.appendChild(pivotToggleCb);
        pivotWrapper.appendChild(pivotText);

        btnRow.appendChild(queryBtn);
        btnRow.appendChild(exportXlsxBtn);
        btnRow.appendChild(exportCsvBtn);
        btnRow.appendChild(chartExportWrapper);
        btnRow.appendChild(pivotWrapper);
        selectorBody.appendChild(btnRow);

        // ── FilterBar（Ad-hoc 篩選條件）──
        var filterSection = document.createElement('div');
        filterSection.className = 'analysis-filter-section';
        filterSection.style.cssText = 'margin-top:8px;padding:8px 0 0 0;border-top:1px solid #e8e8e8;';

        var filterBtnRow = document.createElement('div');
        filterBtnRow.style.cssText = 'display:flex;align-items:center;gap:6px;margin-bottom:6px;';

        var addFilterBtn = document.createElement('button');
        addFilterBtn.type = 'button';
        addFilterBtn.className = 'layui-btn layui-btn-xs layui-btn-warm analysis-add-filter-btn';
        addFilterBtn.dataset.gridId = gridId;
        addFilterBtn.textContent = _i18n('btn.addFilter');
        addFilterBtn.addEventListener('click', function () { addFilterRow(gridId); });
        filterBtnRow.appendChild(addFilterBtn);

        var clearFilterBtn = document.createElement('button');
        clearFilterBtn.type = 'button';
        clearFilterBtn.className = 'layui-btn layui-btn-xs layui-btn-primary analysis-clear-filter-btn';
        clearFilterBtn.dataset.gridId = gridId;
        clearFilterBtn.textContent = _i18n('btn.clearFilter');
        clearFilterBtn.addEventListener('click', function () {
            var list = filterSection.querySelector('.analysis-filter-list');
            if (list) clearChildren(list);
        });
        filterBtnRow.appendChild(clearFilterBtn);

        filterSection.appendChild(filterBtnRow);

        var filterList = document.createElement('div');
        filterList.className = 'analysis-filter-list';
        filterSection.appendChild(filterList);

        selectorBody.appendChild(filterSection);

        selectorSection.appendChild(selectorBody);

        // Summary bar (visible when collapsed)
        var summaryBar = document.createElement('div');
        summaryBar.className = 'analysis-summary-bar';
        var reQueryBtn = document.createElement('button');
        reQueryBtn.type = 'button';
        reQueryBtn.className = 'layui-btn layui-btn-xs layui-btn-normal';
        reQueryBtn.textContent = _i18n('btn.reQuery');
        reQueryBtn.addEventListener('click', function () { query(gridId); });
        summaryBar.appendChild(reQueryBtn);
        selectorSection.appendChild(summaryBar);

        panelEl.appendChild(selectorSection);

        // ── Section 2: 分析結果 ──
        var resultSection = document.createElement('div');
        resultSection.className = 'analysis-result-section';
        resultSection.style.display = 'none'; // hidden until first query

        var resultHeader = document.createElement('div');
        resultHeader.className = 'analysis-panel-header';
        resultHeader.addEventListener('click', function () {
            toggleCollapse(gridId, 'analysis-result-body', 'resultCollapsed');
        });
        var resultTitle = document.createElement('span');
        resultTitle.className = 'analysis-panel-title';
        resultTitle.textContent = _i18n('panel.resultTitle');
        var resultToggle = document.createElement('span');
        resultToggle.className = 'analysis-panel-toggle';
        resultToggle.textContent = '\u25BC';
        resultHeader.appendChild(resultTitle);
        resultHeader.appendChild(resultToggle);
        resultSection.appendChild(resultHeader);

        var resultBody = document.createElement('div');
        resultBody.className = 'analysis-result-body';

        var _CHART_TITLE_MAP = { bar: _i18n('chart.bar'), line: _i18n('chart.line'), pie: _i18n('chart.pie'), 'bar-stacked': _i18n('chart.barStacked'), card: _i18n('chart.card'), scatter: _i18n('chart.scatter') };
        var chartToggleRow = document.createElement('div');
        chartToggleRow.id = 'analysis-chart-toggle-' + gridId;
        chartToggleRow.className = 'analysis-chart-toggle-bar';
        chartToggleRow.style.display = 'none';
        ['bar', 'line', 'bar-stacked', 'pie', 'scatter', 'card'].forEach(function (ct) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'layui-btn layui-btn-xs';
            btn.dataset.chartType = ct;
            btn.textContent = ct;
            btn.title = _CHART_TITLE_MAP[ct] || ct;
            btn.addEventListener('click', function () {
                if (!st || !st.lastResult) return;
                var rd = document.getElementById('analysis-result-' + gridId);
                if (!rd) return;
                var oldChart = document.getElementById('analysis-chart-' + gridId);
                if (oldChart) {
                    if (window.echarts) {
                        var instance = window.echarts.getInstanceByDom(oldChart);
                        if (instance) instance.dispose();
                    }
                    if (oldChart.parentNode) oldChart.parentNode.removeChild(oldChart);
                }
                var oldCards = rd.querySelector('.analysis-cards');
                if (oldCards && oldCards.parentNode) oldCards.parentNode.removeChild(oldCards);
                renderChart(gridId, st.lastResult, st.lastReq, st.lastDimFields, rd, ct);
            });
            chartToggleRow.appendChild(btn);
        });
        resultBody.appendChild(chartToggleRow);

        var drillBar = document.createElement('div');
        drillBar.id = 'analysis-drill-bar-' + gridId;
        drillBar.className = 'analysis-drill-bar';
        drillBar.style.display = 'none';
        resultBody.appendChild(drillBar);

        var resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-' + gridId;
        resultBody.appendChild(resultDiv);

        resultSection.appendChild(resultBody);
        panelEl.appendChild(resultSection);

        // ── 初始化 SortableJS ──
        initSortable(gridId, fieldPool, dimZone, msrZone);
    }

    function initSortable(gridId, fieldPool, dimZone, msrZone) {
        if (!Sortable) return;
        var st = _state[gridId];
        if (!st) return;

        function findFieldMeta(fieldName) {
            return (st.fields || []).filter(function (f) { return f.fieldName === fieldName; })[0] || null;
        }

        function onDropToDimZone(evt) {
            var item = evt.item;
            var fieldName = item.dataset.fieldName;
            var field = findFieldMeta(fieldName);
            if (!field || field.kind !== 'Dimension' || dimZone.querySelectorAll('.analysis-pill--dim').length >= 3) {
                if (item.parentNode) item.parentNode.removeChild(item);
                return;
            }
            var newPill = createDropZonePill(gridId, field, 'Dimension');
            dimZone.replaceChild(newPill, item);
            markPoolPill(gridId, fieldName, true);
            updatePlaceholders(gridId);
            updateSummaryBar(gridId);
            if (st.pivotEnabled) {
                var radio = newPill.querySelector('.analysis-pivot-radio');
                if (radio) radio.style.display = 'inline-block';
            }
        }

        function onDropToMsrZone(evt) {
            var item = evt.item;
            var fieldName = item.dataset.fieldName;
            var field = findFieldMeta(fieldName);
            if (!field || field.kind !== 'Measure' || msrZone.querySelectorAll('.analysis-pill--msr').length >= 3) {
                if (item.parentNode) item.parentNode.removeChild(item);
                return;
            }
            var newPill = createDropZonePill(gridId, field, 'Measure');
            msrZone.replaceChild(newPill, item);
            markPoolPill(gridId, fieldName, true);
            updatePlaceholders(gridId);
            updateSummaryBar(gridId);
        }

        function onRemoveFromZone(evt) {
            var item = evt.item;
            var fieldName = item.dataset.fieldName;
            if (item.parentNode) item.parentNode.removeChild(item);
            markPoolPill(gridId, fieldName, false);
            updatePlaceholders(gridId);
            updateSummaryBar(gridId);
        }

        st.sortableInstances.pool = new Sortable(fieldPool, {
            group: { name: 'fields-' + gridId, pull: 'clone', put: false },
            sort: false,
            filter: '.analysis-pill--used',
            animation: 150
        });

        st.sortableInstances.dimZone = new Sortable(dimZone, {
            group: { name: 'dims-' + gridId, pull: true, put: ['fields-' + gridId] },
            animation: 150,
            onAdd: onDropToDimZone,
            onRemove: onRemoveFromZone
        });

        st.sortableInstances.msrZone = new Sortable(msrZone, {
            group: { name: 'msrs-' + gridId, pull: true, put: ['fields-' + gridId] },
            animation: 150,
            onAdd: onDropToMsrZone,
            onRemove: onRemoveFromZone
        });
    }

    // ─── collectSelection (v2: 從 drop zone 讀取) ──────────────────────────

    function collectSelection(gridId) {
        var dims = [];
        var msrs = [];
        var dimensionHierarchies = {};
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return { dims: dims, msrs: msrs, dimensionHierarchies: dimensionHierarchies };

        // Read from drop zones
        var dimZone = panel.querySelector('.analysis-dropzone--dim');
        var msrZone = panel.querySelector('.analysis-dropzone--msr');

        if (dimZone) {
            var dimPills = dimZone.querySelectorAll('.analysis-pill--dim');
            for (var i = 0; i < dimPills.length; i++) {
                var p = dimPills[i];
                dims.push(p.dataset.fieldName);
                if (p.dataset.isDate === 'true') {
                    var hSel = p.querySelector('.analysis-hierarchy-select');
                    if (hSel) dimensionHierarchies[p.dataset.fieldName] = hSel.value;
                }
            }
        }

        if (msrZone) {
            var msrPills = msrZone.querySelectorAll('.analysis-pill--msr');
            for (var j = 0; j < msrPills.length; j++) {
                var mp = msrPills[j];
                var funcSel = mp.querySelector('.analysis-func-select');
                var func = funcSel ? funcSel.value : (mp.dataset.defaultFunc || 'Sum');
                msrs.push({ field: mp.dataset.fieldName, func: func });
            }
        }

        // Fallback: v1 checkbox mode (backward compat for tests)
        if (dims.length === 0 && msrs.length === 0 && !dimZone && !msrZone) {
            var hasQsa = panel && typeof panel.querySelectorAll === 'function';
            var root = hasQsa ? panel : document;
            var selector = hasQsa ? '.analysis-field-cb:checked' : '.analysis-field-cb[data-grid-id="' + gridId + '"]:checked';
            root.querySelectorAll(selector).forEach(function (cb) {
                if (cb.dataset.kind === 'Dimension') {
                    dims.push(cb.dataset.fieldName);
                    if (cb.dataset.isDate === 'true') {
                        var hs = cb.parentNode && cb.parentNode.querySelector
                            ? cb.parentNode.querySelector('.analysis-hierarchy-select') : null;
                        if (hs) dimensionHierarchies[cb.dataset.fieldName] = hs.value;
                    }
                } else {
                    var sel = cb.nextElementSibling;
                    var fn = (sel && sel.tagName === 'SELECT') ? sel.value : (cb.dataset.defaultFunc || 'Sum');
                    msrs.push({ field: cb.dataset.fieldName, func: fn });
                }
            });
        }

        return { dims: dims, msrs: msrs, dimensionHierarchies: dimensionHierarchies };
    }


    // ─── Ad-hoc 篩選條件 ─────────────────────────────────────────────────────

    var _FILTER_OPS = [
        { value: 'Eq',          label: _i18n('op.eq') },
        { value: 'NotEq',       label: _i18n('op.neq') },
        { value: 'Gt',          label: _i18n('op.gt') },
        { value: 'Gte',         label: _i18n('op.gte') },
        { value: 'Lt',          label: _i18n('op.lt') },
        { value: 'Lte',         label: _i18n('op.lte') },
        { value: 'Contains',    label: _i18n('op.contains') },
        { value: 'NotContains', label: _i18n('op.notContains') },
        { value: 'In',          label: 'In' },
        { value: 'NotIn',       label: 'Not In' }
    ];

    /**
     * 從 filterBar 讀取所有有效篩選列，組裝成 [{field, op, value}]。
     * 欄位或值任一為空的列被跳過。
     */
    function collectFilters(gridId) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return [];
        var rows = panel.querySelectorAll('.analysis-filter-row');
        var filters = [];
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var fieldSel = row.querySelector('.analysis-filter-field');
            var opSel    = row.querySelector('.analysis-filter-op');
            var valInput = row.querySelector('.analysis-filter-value');
            var field = fieldSel ? fieldSel.value : '';
            var op    = opSel    ? opSel.value    : '';
            var value = valInput ? valInput.value  : '';
            if (!field || !value) continue;
            filters.push({ field: field, operator: op || 'Eq', value: value });
        }
        return filters;
    }

    /**
     * 根據欄位 metadata 建立合適的值輸入控件：
     *   - 枚舉（allowedValues 非空）→ <select>
     *   - 日期（isDate === true）     → <input type="text"> + placeholder
     *   - 其他                        → <input type="text">
     */
    function createValueInput(fieldMeta) {
        var el;
        if (fieldMeta && fieldMeta.allowedValues && fieldMeta.allowedValues.length > 0) {
            el = document.createElement('select');
            el.className = 'analysis-filter-value layui-input';
            el.style.cssText = 'width:140px;display:inline-block;';
            var emptyOpt = document.createElement('option');
            emptyOpt.value = '';
            emptyOpt.textContent = _i18n('filter.selectEmpty');
            el.appendChild(emptyOpt);
            fieldMeta.allowedValues.forEach(function (v) {
                var opt = document.createElement('option');
                opt.value = v;
                opt.textContent = v;
                el.appendChild(opt);
            });
        } else {
            el = document.createElement('input');
            el.type = 'text';
            el.className = 'analysis-filter-value layui-input';
            el.style.cssText = 'width:140px;display:inline-block;';
            if (fieldMeta && fieldMeta.isDate) {
                el.placeholder = 'yyyy-MM-dd';
                if (typeof laydate !== 'undefined') {
                    laydate.render({ elem: el });
                }
            } else {
                el.placeholder = _i18n('filter.valuePh');
            }
        }
        return el;
    }

    /**
     * 在 filterBar 新增一行篩選列。
     * 初始建立時使用預設 text input；欄位選取後動態替換為合適的控件。
     */
    function addFilterRow(gridId, fields) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;
        var filterList = panel.querySelector('.analysis-filter-list');
        if (!filterList) return;

        var st = _state[gridId];
        var metaFields = fields || (st && st.fields) || [];

        // 建立 fieldName → meta 的查找表
        var metaByName = {};
        metaFields.forEach(function (f) { metaByName[f.fieldName] = f; });

        var row = document.createElement('div');
        row.className = 'analysis-filter-row layui-inline';
        row.style.cssText = 'display:flex;align-items:center;gap:6px;margin-bottom:6px;';

        var fieldSel = document.createElement('select');
        fieldSel.className = 'analysis-filter-field';
        fieldSel.style.cssText = 'min-width:120px;';
        var emptyOpt = document.createElement('option');
        emptyOpt.value = '';
        emptyOpt.textContent = _i18n('filter.fieldPh');
        fieldSel.appendChild(emptyOpt);
        metaFields.forEach(function (f) {
            var opt = document.createElement('option');
            opt.value = f.fieldName;
            opt.textContent = f.displayName;
            fieldSel.appendChild(opt);
        });
        row.appendChild(fieldSel);

        var opSel = document.createElement('select');
        opSel.className = 'analysis-filter-op';
        opSel.style.cssText = 'min-width:100px;';
        _FILTER_OPS.forEach(function (o) {
            var opt = document.createElement('option');
            opt.value = o.value;
            opt.textContent = o.label;
            opSel.appendChild(opt);
        });
        row.appendChild(opSel);

        // 初始值控件：無欄位選取時使用預設 text input
        var valEl = createValueInput(null);
        row.appendChild(valEl);

        // 欄位選取後，根據新欄位的 meta 替換值控件
        fieldSel.addEventListener('change', function () {
            var selectedMeta = metaByName[fieldSel.value] || null;
            var newValEl = createValueInput(selectedMeta);
            row.replaceChild(newValEl, valEl);
            valEl = newValEl;
        });

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'layui-btn layui-btn-xs layui-btn-danger';
        removeBtn.textContent = '\u2715';
        removeBtn.addEventListener('click', function () {
            if (filterList.contains(row)) filterList.removeChild(row);
        });
        row.appendChild(removeBtn);

        filterList.appendChild(row);
    }

    /**
     * 在 meta 載入後更新已存在篩選列的欄位選單。
     */
    function updateFilterFieldOptions(gridId, fields) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;
        var selects = panel.querySelectorAll('.analysis-filter-field');
        for (var i = 0; i < selects.length; i++) {
            var fieldSel = selects[i];
            var currentVal = fieldSel.value;
            while (fieldSel.firstChild) fieldSel.removeChild(fieldSel.firstChild);
            var emptyOpt = document.createElement('option');
            emptyOpt.value = '';
            emptyOpt.textContent = _i18n('filter.fieldPh');
            fieldSel.appendChild(emptyOpt);
            fields.forEach(function (f) {
                var opt = document.createElement('option');
                opt.value = f.fieldName;
                opt.textContent = f.displayName;
                if (f.fieldName === currentVal) opt.selected = true;
                fieldSel.appendChild(opt);
            });
        }
    }

    // ─── 查詢 ────────────────────────────────────────────────────────────────

    function query(gridId) {
        var st = _state[gridId];
        if (!st) return;

        var sel = collectSelection(gridId);
        var dims = sel.dims;
        var msrs = sel.msrs;

        var errors = validateSelection(dims, msrs);
        if (errors.length > 0) {
            showMsg(errors.join('\n'));
            return;
        }

        var isPivot = false;
        var pivotDim = null;
        var pivotToggle = document.querySelector('.analysis-pivot-toggle[data-grid-id="' + gridId + '"]');
        if (pivotToggle && pivotToggle.checked) {
            isPivot = true;
            var pivotRadio = document.querySelector('.analysis-pivot-dim-select[data-grid-id="' + gridId + '"]:checked');
            if (pivotRadio) pivotDim = pivotRadio.value;
            if (!pivotDim) {
                showMsg(_i18n('pivot.selectDim'));
                return;
            }
            if (dims.indexOf(pivotDim) < 0) {
                showMsg(_i18n('pivot.notSelected'));
                return;
            }
        }

        var searcherJson = collectSearcherFormData(gridId);
        var req = {
            listVmType: st.listVmType,
            dimensions: dims,
            measures: msrs,
            filters: collectFilters(gridId),
            dimensionHierarchies: Object.keys(sel.dimensionHierarchies).length > 0
                ? sel.dimensionHierarchies : undefined,
            searcherFormData: searcherJson
        };
        if (isPivot) req.pivotDimension = pivotDim;

        // Show result section
        var panel = document.getElementById('analysis-panel-' + gridId);
        var resultSection = panel ? panel.querySelector('.analysis-result-section') : null;
        if (resultSection) resultSection.style.display = '';

        var resultDiv = document.getElementById('analysis-result-' + gridId);

        // 若有進行中的查詢，先取消
        if (st.abortController) {
            st.abortController.abort();
            st.abortController = null;
        }
        var ac = new AbortController();
        st.abortController = ac;

        // 顯示計時 loading 狀態
        if (resultDiv) resultDiv.textContent = _i18n('query.progress').replace('{t}', '0');
        var elapsed = 0;
        var timer = setInterval(function () {
            elapsed++;
            if (resultDiv) resultDiv.textContent = _i18n('query.progress').replace('{t}', elapsed);
        }, 1000);

        var endpoint = isPivot ? '/_analysis/pivot' : '/_analysis/query';
        fetch(endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req),
            signal: ac.signal
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t); });
            return res.json();
        })
        .then(function (result) {
            clearInterval(timer);
            st.abortController = null;
            if (!resultDiv) return;
            clearChildren(resultDiv);

            if (result.dataTruncated) {
                var dataWarn = document.createElement('div');
                dataWarn.className = 'layui-alert layui-alert-orange';
                dataWarn.textContent = result.dataTruncatedMessage || _i18n('warn.dataExceeds');
                resultDiv.appendChild(dataWarn);
            }
            if (result.truncated) {
                var warn = document.createElement('div');
                warn.className = 'layui-alert layui-alert-warm';
                var totalStr = result.totalCount ? result.totalCount.toLocaleString() : '';
                warn.textContent = totalStr
                    ? _i18n('warn.truncatedFmt').replace('{n}', totalStr)
                    : _i18n('warn.truncated');
                resultDiv.appendChild(warn);
            }

            var dimFields = (st.fields || []).filter(function (f) {
                return f.kind === 'Dimension' && dims.indexOf(f.fieldName) >= 0;
            });
            var dateDimSet = {};
            dimFields.forEach(function (f) { if (f.isDate) dateDimSet[f.fieldName] = true; });

            if (isPivot) {
                renderPivotTable(gridId, result, resultDiv, dateDimSet);
                renderPivotChart(gridId, result, req, resultDiv);
                var tRow = document.getElementById('analysis-chart-toggle-' + gridId);
                if (tRow) tRow.style.display = 'none';
            } else {
                renderTable(gridId, result, resultDiv, dateDimSet);
                renderChart(gridId, result, { dimensions: dims, measures: msrs }, dimFields, resultDiv);
                st.lastResult = result;
                st.lastReq = {
                    dimensions: dims, measures: msrs,
                    dimensionHierarchies: Object.keys(sel.dimensionHierarchies).length > 0
                        ? sel.dimensionHierarchies : undefined
                };
                st.lastDimFields = dimFields;
                st.drillStack = [];
                st.drillFilters = [];
                updateDrillBar(gridId);
                var toggleRow = document.getElementById('analysis-chart-toggle-' + gridId);
                if (toggleRow) toggleRow.style.display = 'flex';
            }

        })
        .catch(function (err) {
            clearInterval(timer);
            st.abortController = null;
            if (err.name === 'AbortError') {
                if (resultDiv) resultDiv.textContent = _i18n('query.cancelled');
                return;
            }
            if (resultDiv) resultDiv.textContent = _i18n('query.failed') + parseFriendlyError(err);
        });
    }

    // ─── 渲染函式（保留 v1 邏輯）──────────────────────────────────────────────

    function renderPivotTable(gridId, result, container, dateDims) {
        var wrapper = document.createElement('div');
        wrapper.style.overflowX = 'auto';
        var table = document.createElement('table');
        table.className = 'layui-table';
        table.style.marginTop = '10px';
        table.style.whiteSpace = 'nowrap';
        var thead = document.createElement('thead');
        var headerRow = document.createElement('tr');
        result.columns.forEach(function (col) {
            var th = document.createElement('th');
            th.textContent = col;
            headerRow.appendChild(th);
        });
        thead.appendChild(headerRow);
        table.appendChild(thead);
        var tbody = document.createElement('tbody');
        result.rows.forEach(function (row) {
            var tr = document.createElement('tr');
            result.columns.forEach(function (col) {
                var td = document.createElement('td');
                var val = row[col];
                td.textContent = (val !== null && val !== undefined)
                    ? (dateDims[col] ? formatDateKey(val) : String(val)) : '-';
                tr.appendChild(td);
            });
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        wrapper.appendChild(table);
        container.appendChild(wrapper);
    }

    function renderPivotChart(gridId, result, req, container) {
        if (typeof window.echarts === 'undefined') return;
        var chartDiv = document.createElement('div');
        chartDiv.id = 'analysis-chart-' + gridId;
        chartDiv.style.width = '100%';
        chartDiv.style.height = '350px';
        chartDiv.style.marginTop = '15px';
        container.appendChild(chartDiv);
        var chart = window.echarts.init(chartDiv);
        var firstRowDim = result.rowDimensions.length > 0 ? result.rowDimensions[0] : '';
        var categories = result.rows.map(function (r) {
            return firstRowDim ? String(r[firstRowDim] || '') : _i18n('result.total');
        });
        var series = [];
        var legendData = [];
        result.pivotValues.forEach(function (pv) {
            result.measureNames.forEach(function (m) {
                var key = pv + '_' + m;
                legendData.push(key);
                series.push({
                    name: key, type: 'bar', stack: m,
                    data: result.rows.map(function (r) { return r[key] || 0; })
                });
            });
        });
        chart.setOption({
            tooltip: { trigger: 'axis' },
            legend: { data: legendData },
            xAxis: { type: 'category', data: categories },
            yAxis: { type: 'value' },
            series: series
        });
    }

    function renderTable(gridId, result, container, dateDims) {
        dateDims = dateDims || {};
        if (!result.rows || result.rows.length === 0) {
            var emptyP = document.createElement('p');
            emptyP.className = 'analysis-empty-state';
            emptyP.textContent = _i18n('result.empty');
            container.appendChild(emptyP);
            return;
        }
        // Build column label map: col key → human-friendly header
        // and measure set: col key → true (for numeric formatting)
        var colLabelMap = {};
        var msrColSet = {};
        var st = _state[gridId];
        var fields = (st && st.fields) ? st.fields : [];
        var fieldByName = {};
        fields.forEach(function (f) { fieldByName[f.fieldName] = f; });
        result.columns.forEach(function (col) {
            // Measure columns are encoded as "FieldName_Func" (e.g. "Amount_Sum")
            var underIdx = col.lastIndexOf('_');
            if (underIdx > 0) {
                var fieldPart = col.substring(0, underIdx);
                var funcPart = col.substring(underIdx + 1);
                var meta = fieldByName[fieldPart];
                if (meta && meta.kind === 'Measure') {
                    var label = (meta.displayName || meta.title || fieldPart) + ' ' + getFuncLabel(funcPart);
                    colLabelMap[col] = label;
                    msrColSet[col] = true;
                    return;
                }
            }
            // Dimension column or unmatched: use displayName if available
            var dimMeta = fieldByName[col];
            colLabelMap[col] = (dimMeta && (dimMeta.displayName || dimMeta.title)) || col;
        });

        // ── Compute column totals for % share (measure columns only) ──────────
        var colTotals = {};
        result.columns.forEach(function (col) {
            if (!msrColSet[col]) return;
            var total = 0;
            result.rows.forEach(function (row) {
                var v = row[col];
                if (typeof v === 'number' && isFinite(v)) total += v;
            });
            colTotals[col] = total;
        });

        var table = document.createElement('table');
        table.className = 'layui-table';
        table.style.marginTop = '10px';
        var thead = document.createElement('thead');
        var headerRow = document.createElement('tr');
        result.columns.forEach(function (col) {
            var th = document.createElement('th');
            th.textContent = colLabelMap[col] || col;
            headerRow.appendChild(th);
            if (msrColSet[col]) {
                var thPct = document.createElement('th');
                thPct.textContent = (colLabelMap[col] || col) + ' %';
                thPct.className = 'analysis-pct-header';
                thPct.style.cssText = 'color:#999;font-weight:normal;font-size:12px;';
                headerRow.appendChild(thPct);
            }
        });
        thead.appendChild(headerRow);
        table.appendChild(thead);
        var tbody = document.createElement('tbody');
        result.rows.forEach(function (row) {
            var tr = document.createElement('tr');
            result.columns.forEach(function (col) {
                var td = document.createElement('td');
                var val = row[col];
                if (val !== null && val !== undefined) {
                    if (dateDims[col]) {
                        td.textContent = formatDateKey(val);
                    } else if (msrColSet[col] && typeof val === 'number') {
                        td.textContent = formatNumeric(val);
                    } else {
                        td.textContent = String(val);
                    }
                } else {
                    td.textContent = '-';
                }
                tr.appendChild(td);
                if (msrColSet[col]) {
                    var tdPct = document.createElement('td');
                    var total = colTotals[col];
                    if (total !== 0 && typeof val === 'number' && isFinite(val)) {
                        tdPct.textContent = (val / total * 100).toFixed(1) + '%';
                    } else {
                        tdPct.textContent = '-';
                    }
                    tdPct.className = 'analysis-pct-cell';
                    tdPct.style.cssText = 'color:#aaa;font-size:12px;';
                    tr.appendChild(tdPct);
                }
            });
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        container.appendChild(table);
    }

    // Highlights the active chart-type button in the toggle bar (#618).
    // Called from renderChart so both auto-detect and manual selection stay in sync.
    function syncChartToggleActive(gridId, chartType) {
        var row = document.getElementById('analysis-chart-toggle-' + gridId);
        if (!row) return;
        var buttons = row.querySelectorAll('button[data-chart-type]');
        for (var i = 0; i < buttons.length; i++) {
            var btn = buttons[i];
            if (btn.dataset.chartType === chartType) {
                if (btn.className.indexOf('layui-btn-primary') < 0) {
                    btn.className += ' layui-btn-primary';
                }
            } else {
                btn.className = btn.className.replace(/\s*layui-btn-primary\b/g, '').trim();
            }
        }
    }

    function renderChart(gridId, result, req, dimFields, container, forceChartType) {
        if (!result.rows || result.rows.length === 0) {
            var emptyP = document.createElement('p');
            emptyP.className = 'analysis-empty-state';
            emptyP.textContent = _i18n('result.empty');
            container.appendChild(emptyP);
            return;
        }
        var dimMeta = req.dimensions.map(function (d) {
            var f = (dimFields || []).filter(function (fd) { return fd.fieldName === d; })[0];
            return { fieldName: d, isDate: f ? f.isDate === true : false };
        });
        var chartType = forceChartType || detectChartType(dimMeta, req.measures);
        // Persist effective chart type so exportData honours the user's manual selection (#479)
        var st = _state[gridId];
        if (st) st.lastChartType = chartType;
        syncChartToggleActive(gridId, chartType);

        // Build measure key → display label map for human-friendly legends
        var fieldByName = {};
        if (st && st.fields) {
            st.fields.forEach(function (f) { fieldByName[f.fieldName] = f; });
        }
        var msrLabelMap = {};
        req.measures.forEach(function (m) {
            var key = m.field + '_' + m.func;
            var meta = fieldByName[m.field];
            msrLabelMap[key] = ((meta && meta.displayName) || m.field) + ' ' + getFuncLabel(m.func);
        });

        if (chartType === 'card') {
            var cardContainer = document.createElement('div');
            cardContainer.className = 'analysis-cards';
            var row = result.rows && result.rows.length > 0 ? result.rows[0] : {};
            req.measures.forEach(function (m) {
                var key = m.field + '_' + m.func;
                var val = row[key];
                var card = document.createElement('div');
                card.className = 'analysis-card-item';
                var label = document.createElement('div');
                label.style.cssText = 'font-size:13px;color:#666;margin-bottom:8px;';
                label.textContent = msrLabelMap[key] || key;
                var value = document.createElement('div');
                value.style.cssText = 'font-size:28px;font-weight:bold;color:#333;';
                value.textContent = val != null ? String(val) : '\u2014';
                card.appendChild(label);
                card.appendChild(value);
                cardContainer.appendChild(card);
            });
            container.appendChild(cardContainer);
            return;
        }

        if (typeof window.echarts === 'undefined') return;
        var chartDiv = document.createElement('div');
        chartDiv.id = 'analysis-chart-' + gridId;
        chartDiv.style.width = '100%';
        chartDiv.style.height = '350px';
        chartDiv.style.marginTop = '15px';
        container.appendChild(chartDiv);
        var chart = window.echarts.init(chartDiv);
        var firstDim = req.dimensions[0];
        var firstDimIsDate = dimMeta.length > 0 && dimMeta[0].isDate;
        var categories = result.rows.map(function (r) {
            var v = r[firstDim];
            return firstDimIsDate ? formatDateKey(v) : String(v || '');
        });

        if (chartType === 'pie') {
            var pieKey = req.measures[0].field + '_' + req.measures[0].func;
            chart.setOption({
                tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
                legend: { orient: 'vertical', left: 'left', data: categories },
                series: [{
                    name: msrLabelMap[pieKey] || pieKey, type: 'pie', radius: '55%', center: ['50%', '55%'],
                    data: categories.map(function (cat, i) {
                        return { name: cat, value: result.rows[i][pieKey] };
                    }),
                    emphasis: { itemStyle: { shadowBlur: 10, shadowOffsetX: 0, shadowColor: 'rgba(0,0,0,0.5)' } }
                }]
            });
        } else {
            var isDual = chartType !== 'bar-stacked' && detectDualAxis(result.rows, req.measures);
            var scales = [];
            if (isDual) {
                req.measures.forEach(function (m) {
                    var key = m.field + '_' + m.func;
                    var maxVal = 0;
                    result.rows.forEach(function (r) {
                        var v = Math.abs(r[key] || 0);
                        if (v > maxVal) maxVal = v;
                    });
                    scales.push(computeScale(maxVal));
                });
            }
            var series = req.measures.map(function (m, idx) {
                var key = m.field + '_' + m.func;
                var rawData = result.rows.map(function (r) { return r[key]; });
                var s = {
                    name: msrLabelMap[key] || key,
                    type: chartType === 'line' ? 'line' : 'bar',
                    stack: chartType === 'bar-stacked' ? 'total' : undefined,
                    data: isDual ? scaleSeriesData(rawData, scales[idx].divisor) : rawData
                };
                if (isDual) {
                    s.yAxisIndex = idx;
                    s.originalData = rawData;
                }
                return s;
            });

            var yAxisOption;
            if (isDual) {
                var key0 = req.measures[0].field + '_' + req.measures[0].func;
                var key1 = req.measures[1].field + '_' + req.measures[1].func;
                var name0 = msrLabelMap[key0] || key0;
                var name1 = msrLabelMap[key1] || key1;
                yAxisOption = [
                    { type: 'value', name: name0 + (scales[0].unit ? '（' + scales[0].unit + '）' : ''), position: 'left' },
                    { type: 'value', name: name1 + (scales[1].unit ? '（' + scales[1].unit + '）' : ''), position: 'right' }
                ];
            } else {
                yAxisOption = { type: 'value' };
            }

            var tooltipOption = { trigger: 'axis' };
            if (isDual) {
                tooltipOption.formatter = function (params) {
                    var lines = [params[0].axisValueLabel];
                    params.forEach(function (p) {
                        var s = series[p.seriesIndex];
                        var original = s.originalData ? s.originalData[p.dataIndex] : p.value;
                        var scale = scales[p.seriesIndex];
                        var label;
                        if (original == null) {
                            label = '-';
                        } else if (scale && scale.unit) {
                            var scaled = (original / scale.divisor).toFixed(2).replace(/\.?0+$/, '');
                            label = scaled + ' ' + scale.unit + '\uff08' + original + '\uff09';
                        } else {
                            label = original;
                        }
                        lines.push(p.marker + ' ' + p.seriesName + ': ' + label);
                    });
                    return lines.join('<br/>');
                };
            }

            chart.setOption({
                tooltip: tooltipOption,
                legend: { data: req.measures.map(function (m) { var k = m.field + '_' + m.func; return msrLabelMap[k] || k; }) },
                xAxis: { type: 'category', data: categories },
                yAxis: yAxisOption,
                series: series
            });
        }

        chart.on('click', function (params) {
            if (!firstDim) return;
            var rawRow = result.rows[params.dataIndex];
            if (!rawRow) return;
            drillDown(gridId, firstDim, rawRow[firstDim], firstDimIsDate);
        });
    }

    // ─── Drill-down ──────────────────────────────────────────────────────────

    function updateDrillBar(gridId) {
        var drillBar = document.getElementById('analysis-drill-bar-' + gridId);
        if (!drillBar) return;
        var st = _state[gridId];
        if (!st || st.drillStack.length === 0) {
            drillBar.style.display = 'none';
            clearChildren(drillBar);
            return;
        }
        drillBar.style.display = 'flex';
        clearChildren(drillBar);
        var pathSpan = document.createElement('span');
        pathSpan.textContent = _i18n('drill.all');
        st.drillStack.forEach(function (frame) {
            pathSpan.textContent += ' > ' + String(frame.label);
        });
        drillBar.appendChild(pathSpan);
        var backBtn = document.createElement('button');
        backBtn.type = 'button';
        backBtn.className = 'layui-btn layui-btn-xs layui-btn-primary';
        backBtn.style.marginLeft = '8px';
        backBtn.textContent = _i18n('drill.up');
        backBtn.addEventListener('click', function () { drillBack(gridId); });
        drillBar.appendChild(backBtn);
        var resetBtn = document.createElement('button');
        resetBtn.type = 'button';
        resetBtn.className = 'layui-btn layui-btn-xs layui-btn-danger';
        resetBtn.style.marginLeft = '4px';
        resetBtn.textContent = _i18n('btn.reset');
        resetBtn.addEventListener('click', function () { drillReset(gridId); });
        drillBar.appendChild(resetBtn);
    }

    function drillDown(gridId, dimField, value, isDate) {
        var st = _state[gridId];
        if (!st || !st.lastReq) return;
        var currentFilters = (st.drillFilters || []).slice();
        var currentHierarchies = {};
        if (st.lastReq.dimensionHierarchies) {
            Object.keys(st.lastReq.dimensionHierarchies).forEach(function (k) {
                currentHierarchies[k] = st.lastReq.dimensionHierarchies[k];
            });
        }
        // Guard: date dim already at deepest hierarchy level (Day) — cannot drill further
        if (isDate && currentHierarchies[dimField] && !nextHierarchy(currentHierarchies[dimField])) return;
        // Guard: non-date dim already has a filter for this field — cannot drill further
        if (!isDate && currentFilters.some(function (f) { return f.field === dimField; })) return;

        var label = isDate ? formatDateKey(value) : String(value);
        st.drillStack.push({ filters: currentFilters, dimensionHierarchies: currentHierarchies, label: label });
        var newFilters = currentFilters.concat([buildDrillFilter(dimField, value)]);
        st.drillFilters = newFilters;
        var newHierarchies = {};
        Object.keys(currentHierarchies).forEach(function (k) { newHierarchies[k] = currentHierarchies[k]; });
        if (isDate && newHierarchies[dimField]) {
            var next = nextHierarchy(newHierarchies[dimField]);
            if (next) newHierarchies[dimField] = next;
        }
        updateDrillBar(gridId);
        drillQuery(gridId, newFilters, newHierarchies);
    }

    function drillBack(gridId) {
        var st = _state[gridId];
        if (!st || st.drillStack.length === 0) return;
        var frame = st.drillStack.pop();
        st.drillFilters = frame.filters;
        updateDrillBar(gridId);
        drillQuery(gridId, frame.filters, frame.dimensionHierarchies);
    }

    function drillReset(gridId) {
        var st = _state[gridId];
        if (!st) return;
        st.drillStack = [];
        st.drillFilters = [];
        updateDrillBar(gridId);
        drillQuery(gridId, [], st.lastReq.dimensionHierarchies || {});
    }

    function drillQuery(gridId, filters, hierarchies) {
        var st = _state[gridId];
        if (!st) return;
        var searcherJson = collectSearcherFormData(gridId);
        var req = {
            listVmType: st.listVmType,
            dimensions: st.lastReq.dimensions,
            measures: st.lastReq.measures,
            filters: filters,
            dimensionHierarchies: Object.keys(hierarchies).length > 0 ? hierarchies : undefined,
            searcherFormData: searcherJson
        };
        var resultDiv = document.getElementById('analysis-result-' + gridId);

        // 若有進行中的查詢，先取消
        if (st.abortController) {
            st.abortController.abort();
            st.abortController = null;
        }
        var ac = new AbortController();
        st.abortController = ac;

        // 顯示計時 loading 狀態
        if (resultDiv) resultDiv.textContent = _i18n('query.progress').replace('{t}', '0');
        var elapsed = 0;
        var timer = setInterval(function () {
            elapsed++;
            if (resultDiv) resultDiv.textContent = _i18n('query.progress').replace('{t}', elapsed);
        }, 1000);

        fetch('/_analysis/query', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req),
            signal: ac.signal
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t); });
            return res.json();
        })
        .then(function (result) {
            clearInterval(timer);
            st.abortController = null;
            if (!resultDiv) return;
            clearChildren(resultDiv);
            if (result.dataTruncated) {
                var dataWarn = document.createElement('div');
                dataWarn.className = 'layui-alert layui-alert-orange';
                dataWarn.textContent = result.dataTruncatedMessage || _i18n('warn.dataExceeds');
                resultDiv.appendChild(dataWarn);
            }
            if (result.truncated) {
                var warn = document.createElement('div');
                warn.className = 'layui-alert layui-alert-warm';
                var totalStr = result.totalCount ? result.totalCount.toLocaleString() : '';
                warn.textContent = totalStr
                    ? _i18n('warn.truncatedFmt').replace('{n}', totalStr)
                    : _i18n('warn.truncated');
                resultDiv.appendChild(warn);
            }
            var dimFields = (st.fields || []).filter(function (f) {
                return f.kind === 'Dimension' && st.lastReq.dimensions.indexOf(f.fieldName) >= 0;
            });
            var dateDimSet = {};
            dimFields.forEach(function (f) { if (f.isDate) dateDimSet[f.fieldName] = true; });
            st.lastReq = {
                dimensions: st.lastReq.dimensions,
                measures: st.lastReq.measures,
                dimensionHierarchies: hierarchies
            };
            st.lastResult = result;
            st.lastDimFields = dimFields;
            renderTable(gridId, result, resultDiv, dateDimSet);
            renderChart(gridId, result, st.lastReq, dimFields, resultDiv);
        })
        .catch(function (err) {
            clearInterval(timer);
            st.abortController = null;
            if (err.name === 'AbortError') {
                if (resultDiv) resultDiv.textContent = _i18n('query.cancelled');
                return;
            }
            if (resultDiv) resultDiv.textContent = _i18n('query.failed') + parseFriendlyError(err);
        });
    }

    // ─── 匯出 ────────────────────────────────────────────────────────────────

    function exportData(gridId, format) {
        var st = _state[gridId];
        if (!st) return;
        var sel = collectSelection(gridId);
        var dims = sel.dims;
        var msrs = sel.msrs;
        var loaderId = null;
        if (window.layui && window.layui.layer) loaderId = window.layui.layer.load(2);
        function closeLoader() {
            if (loaderId !== null && window.layui && window.layui.layer) window.layui.layer.close(loaderId);
        }
        var exportHierarchies = Object.keys(sel.dimensionHierarchies).length > 0
            ? sel.dimensionHierarchies : undefined;
        var isPivot = false;
        var pivotDim = null;
        var pt = document.querySelector('.analysis-pivot-toggle[data-grid-id="' + gridId + '"]');
        if (pt && pt.checked) {
            isPivot = true;
            var pr = document.querySelector('.analysis-pivot-dim-select[data-grid-id="' + gridId + '"]:checked');
            if (pr) pivotDim = pr.value;
        }
        var searcherJson = collectSearcherFormData(gridId);
        var req = {
            listVmType: st.listVmType, dimensions: dims, measures: msrs,
            filters: collectFilters(gridId), dimensionHierarchies: exportHierarchies, searcherFormData: searcherJson
        };
        if (isPivot) req.pivotDimension = pivotDim;
        var chartCb = document.querySelector('.analysis-export-chart-cb[data-grid-id="' + gridId + '"]');
        var includeChart = chartCb && chartCb.checked ? 'true' : 'false';
        var dimMeta = dims.map(function (d) {
            return (st.fields || []).find(function (f) { return f.fieldName === d; }) || { isDate: false };
        });
        var currentChartType = (st && st.lastChartType) || detectChartType(dimMeta, msrs);
        var endpoint = isPivot ? '/_analysis/pivot/export' : '/_analysis/export';
        return fetch(endpoint + '?format=' + encodeURIComponent(format) + '&includeChart=' + includeChart + '&chartType=' + encodeURIComponent(currentChartType), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req)
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t || 'HTTP ' + res.status); });
            var truncated = res.headers.get('X-Analysis-Truncated') === 'true';
            return res.blob().then(function (blob) {
                return { blob: blob, truncated: truncated };
            });
        })
        .then(function (data) {
            closeLoader();
            var url = window.URL.createObjectURL(data.blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = 'analysis.' + format;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(url);
            if (data.truncated) {
                showMsg(_i18n('export.truncated'), 'warn');
            }
        })
        .catch(function (err) {
            closeLoader();
            showMsg(_i18n('export.failed') + parseFriendlyError(err));
        });
    }

    // ─── Exposed for testing ──────────────────────────────────────────────────
    function renderTableExposed(gridId, result, container, dateDims) {
        return renderTable(gridId, result, container, dateDims);
    }

    // ─── 公開 API ─────────────────────────────────────────────────────────────
    window.wtmAnalysis = {
        toggle: toggle,
        query: query,
        exportData: exportData,
        detectChartType: detectChartType,
        validateSelection: validateSelection,
        collectSelection: collectSelection,
        parseFuncs: parseFuncs,
        renderChart: renderChart,
        syncChartToggleActive: syncChartToggleActive,
        renderPivotTable: renderPivotTable,
        renderPivotChart: renderPivotChart,
        renderTable: renderTableExposed,
        formatDateKey: formatDateKey,
        buildDrillFilter: buildDrillFilter,
        nextHierarchy: nextHierarchy,
        drillDown: drillDown,
        drillBack: drillBack,
        drillReset: drillReset,
        collectSearcherFormData: collectSearcherFormData,
        computeScale: computeScale,
        scaleSeriesData: scaleSeriesData,
        detectDualAxis: detectDualAxis,
        createValueInput: createValueInput,
        addFilterRow: addFilterRow,
        collectFilters: collectFilters,
        updateFilterFieldOptions: updateFilterFieldOptions,
        parseFriendlyError: parseFriendlyError,
        checkDependencies: checkDependencies,
        filterPoolPills: filterPoolPills,
        setLocale: function (loc) { _locale = loc; },
        getLocale: function () { return _locale; },
        addLocale: function (loc, strings) {
            if (!_LOCALES[loc]) _LOCALES[loc] = {};
            Object.assign(_LOCALES[loc], strings);
        },
        _getState: function (gridId) { return _state[gridId]; }
    };

}(typeof window !== 'undefined' ? window : global));
