/**
 * framework_analysis.js
 * WTM Analysis Mode — 前端分析模式控制器 (v2: drag-and-drop BI panel)
 *
 * 安全原則：所有來自伺服器的欄位名稱與值均透過 textContent 或 DOM 方法設值，
 * 禁止直接拼入 HTML 字串，以防 XSS。buildPillHtml 使用 escapeHtml() 跳脫。
 */
(function (window) {
    'use strict';

    // ─── 純函式 ──────────────────────────────────────────────────────────────

    /**
     * 判斷應使用哪種圖表類型（純函式，無副作用）
     * @param {Array} dims - 選取的維度陣列（每項 {fieldName, isDate}）
     * @param {Array} msrs - 選取的度量陣列
     * @returns {string} 'card'|'pie'|'bar'|'bar-stacked'|'line'
     */
    function detectChartType(dims, msrs) {
        if (dims.length === 0) return 'card';
        if (dims.some(function (d) { return d.isDate; })) return 'line';
        if (dims.length >= 2) return 'bar-stacked';
        if (dims.length === 1 && msrs.length === 1) return 'pie';
        return 'bar';
    }

    /**
     * 驗證維度/度量選取是否合法（純函式，無副作用）
     * @param {Array} dims  - 選取的維度名稱陣列
     * @param {Array} msrs  - 選取的度量物件陣列
     * @returns {string[]}  - 錯誤訊息陣列（空陣列代表合法）
     */
    function validateSelection(dims, msrs) {
        var errors = [];
        if (dims.length > 3) errors.push('維度最多選 3 個');
        if (msrs.length > 3) errors.push('度量最多選 3 個');
        if (dims.length === 0 && msrs.length === 0) errors.push('請至少選擇一個維度或度量');
        return errors;
    }

    /**
     * 建構 drill-down 用的 filter 條件（純函式）
     * @param {string} dimField - 維度欄位名
     * @param {*} value - 點擊的維度值
     * @returns {{ field: string, operator: string, value: * }}
     */
    function buildDrillFilter(dimField, value) {
        return { field: dimField, operator: 'Eq', value: value };
    }

    /**
     * 日期階層自動降級（純函式）
     * @param {string} current - 'Year'|'Quarter'|'Month'|'Day'
     * @returns {string|null} 下一層，Day 時回傳 null
     */
    function nextHierarchy(current) {
        var map = { Year: 'Quarter', Quarter: 'Month', Month: 'Day' };
        return map[current] || null;
    }

    /**
     * 格式化日期整數 key 為人類可讀字串
     * @param {number|string} key - 日期整數 key（如 2026, 20263, 202603, 20260309）
     * @returns {string} 格式化後的字串
     */
    function formatDateKey(key) {
        var s = String(key);
        if (s.length === 4) return s;                                         // Year: 2026
        if (s.length === 5) return s.substring(0, 4) + ' Q' + s.substring(4); // Quarter: 2026 Q3
        if (s.length === 6) return s.substring(0, 4) + '-' + s.substring(4);  // Month: 2026-03
        if (s.length === 8) return s.substring(0, 4) + '-' + s.substring(4, 6) + '-' + s.substring(6); // Day: 2026-03-09
        return s; // fallback
    }

    /**
     * HTML 特殊字元跳脫（純函式）
     * @param {string} str - 原始字串
     * @returns {string} 跳脫後的安全字串
     */
    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    /**
     * 安全 HTML 編碼：ASCII 可見字元（除空白外）全部編碼為命名實體或 &#xHH;
     * 保留非 ASCII 字元（如中文）不編碼。用於 buildPillHtml 中的使用者提供值，
     * 徹底防止 ASCII 型 XSS 攻擊向量如 onerror=, javascript: 等
     * @param {string} str - 原始字串
     * @returns {string} 跳脫後的安全字串
     */
    function encodeSafeHtml(str) {
        if (!str) return '';
        var result = '';
        for (var i = 0; i < str.length; i++) {
            var code = str.charCodeAt(i);
            var ch = str.charAt(i);
            // Keep non-ASCII (CJK, etc.) and spaces as-is
            if (code > 127 || ch === ' ') {
                result += ch;
            } else {
                // Encode all ASCII visible characters
                switch (ch) {
                    case '&': result += '&amp;'; break;
                    case '<': result += '&lt;'; break;
                    case '>': result += '&gt;'; break;
                    case '"': result += '&quot;'; break;
                    case '\'': result += '&#39;'; break;
                    default:
                        result += '&#x' + code.toString(16).toUpperCase() + ';';
                        break;
                }
            }
        }
        return result;
    }

    /** 清空 DOM 節點的所有子節點 */
    function clearChildren(el) {
        while (el.firstChild) {
            el.removeChild(el.firstChild);
        }
    }

    // ─── 狀態 ─────────────────────────────────────────────────────────────────
    var _state = {};

    function initState(gridId, listVmType) {
        if (!_state[gridId]) {
            _state[gridId] = {
                visible: false,
                collapsed: false,
                resultCollapsed: false,
                listVmType: listVmType,
                fields: null,
                dims: [],
                msrs: [],
                dimHierarchies: {},
                pivotEnabled: false,
                pivotDim: null,
                drillStack: [],
                drillFilters: [],
                lastReq: null,
                lastResult: null,
                lastDimFields: null,
                sortableInstances: []
            };
        }
        return _state[gridId];
    }

    // ─── Flag → 函式名稱 ────────────────────────────────────────────────────

    var _FUNC_FLAGS = [
        { value: 1, name: 'Count' },
        { value: 2, name: 'Sum' },
        { value: 4, name: 'Avg' },
        { value: 8, name: 'Max' },
        { value: 16, name: 'Min' },
    ];

    /**
     * 將 [Flags] AggregateFunc 整數轉換為函式名稱陣列
     * @param {number} flags - 來自 API 的 allowedFuncs 整數（如 6 = Sum+Avg）
     * @returns {string[]} 例如 ['Sum', 'Avg']
     */
    function parseFuncs(flags) {
        flags = flags | 0; // coerce to int32: handles NaN/undefined → 0
        return _FUNC_FLAGS
            .filter(function (f) { return (flags & f.value) !== 0; })
            .map(function (f) { return f.name; });
    }

    // ─── Pill HTML builder ───────────────────────────────────────────────────

    /**
     * 建構 pill HTML 字串（用於拖放區域）
     * @param {{ fieldName: string, title?: string, displayName?: string, kind: string, isDate?: boolean, defaultFunc?: string, allowedFuncs?: number }} field
     * @param {string} gridId
     * @returns {string} HTML string
     */
    function buildPillHtml(field, gridId) {
        var name = escapeHtml(field.fieldName);
        var title = encodeSafeHtml(field.title || field.displayName || field.fieldName);
        var kind = escapeHtml(field.kind);
        var cssClass = field.kind === 'Measure' ? 'analysis-pill analysis-pill--msr' : 'analysis-pill analysis-pill--dim';
        var removeBtn = '<span class="analysis-pill__remove">\u2715</span>';

        if (field.kind === 'Measure') {
            // Measure pill: select for aggregate function + title + remove
            var selectHtml = '<select class="analysis-pill__select">';
            var funcs = ['Sum', 'Avg', 'Count', 'Max', 'Min'];
            var defaultFunc = field.defaultFunc || 'Sum';
            funcs.forEach(function (fn) {
                var selected = fn === defaultFunc ? ' selected' : '';
                selectHtml += '<option value="' + fn + '"' + selected + '>' + fn + '</option>';
            });
            selectHtml += '</select>';
            return '<span class="' + cssClass + '" data-field="' + name + '" data-kind="' + kind + '">'
                + selectHtml + ' ' + title + ' ' + removeBtn + '</span>';
        }

        if (field.isDate) {
            // Date dimension pill: title + hierarchy select + remove
            var hierSelect = '<select class="analysis-pill__select">';
            var hierOptions = [
                { value: 'Year', text: 'Year' },
                { value: 'Quarter', text: 'Quarter' },
                { value: 'Month', text: 'Month' },
                { value: 'Day', text: 'Day' }
            ];
            hierOptions.forEach(function (h) {
                var sel = h.value === 'Month' ? ' selected' : '';
                hierSelect += '<option value="' + h.value + '"' + sel + '>' + h.text + '</option>';
            });
            hierSelect += '</select>';
            return '<span class="' + cssClass + '" data-field="' + name + '" data-kind="' + kind + '" data-is-date="true">'
                + title + ' ' + hierSelect + ' ' + removeBtn + '</span>';
        }

        // Regular dimension pill: title + remove
        return '<span class="' + cssClass + '" data-field="' + name + '" data-kind="' + kind + '">'
            + title + ' ' + removeBtn + '</span>';
    }

    // ─── Drop zone data collection ──────────────────────────────────────────

    /**
     * 從 DOM drop zone 讀取 pills 資料
     * @param {string} gridId
     * @returns {{ dims: string[], msrs: Array<{field:string, func:string}>, dimHierarchies: Object, pivotDim: string|null }}
     */
    function collectDropZoneData(gridId) {
        var dims = [];
        var msrs = [];
        var dimHierarchies = {};
        var pivotDim = null;

        var dimZone = document.getElementById('dim-dropzone-' + gridId);
        if (dimZone) {
            var dimPills = dimZone.querySelectorAll('.analysis-pill');
            for (var i = 0; i < dimPills.length; i++) {
                var pill = dimPills[i];
                var fieldName = pill.dataset.field;
                if (fieldName) {
                    dims.push(fieldName);
                    if (pill.dataset.isDate === 'true') {
                        var hierSel = pill.querySelector ? pill.querySelector('select') : null;
                        if (hierSel && hierSel.value) {
                            dimHierarchies[fieldName] = hierSel.value;
                        }
                    }
                }
            }
        }

        var msrZone = document.getElementById('msr-dropzone-' + gridId);
        if (msrZone) {
            var msrPills = msrZone.querySelectorAll('.analysis-pill');
            for (var j = 0; j < msrPills.length; j++) {
                var mPill = msrPills[j];
                var mFieldName = mPill.dataset.field;
                if (mFieldName) {
                    var funcSel = mPill.querySelector ? mPill.querySelector('select') : null;
                    var func = funcSel && funcSel.value ? funcSel.value : 'Sum';
                    msrs.push({ field: mFieldName, func: func });
                }
            }
        }

        // Check pivot mode
        var pivotToggle = document.querySelector('.analysis-pivot-toggle[data-grid-id="' + gridId + '"]');
        if (pivotToggle && pivotToggle.checked) {
            var pivotRadio = document.querySelector('.analysis-pivot-dim-select[data-grid-id="' + gridId + '"]:checked');
            if (pivotRadio) pivotDim = pivotRadio.value;
        }

        return { dims: dims, msrs: msrs, dimHierarchies: dimHierarchies, pivotDim: pivotDim };
    }

    // ─── Summary bar builder ─────────────────────────────────────────────────

    /**
     * 建構收合摘要列 HTML
     * @param {{ dims: string[], msrs: Array<{field:string, func:string}> }} data
     * @returns {string} HTML string
     */
    function buildSummaryBar(data) {
        var dimCount = data.dims ? data.dims.length : 0;
        var msrCount = data.msrs ? data.msrs.length : 0;
        var html = '<span class="analysis-summary-bar__counts">';
        html += '<strong>' + dimCount + ' 個維度</strong>';
        html += ' · ';
        html += '<strong>' + msrCount + ' 個度量</strong>';
        html += '</span>';

        html += ' <span class="analysis-summary-bar__fields">';
        if (data.dims) {
            data.dims.forEach(function (d) {
                html += '<span class="analysis-summary-pill">' + escapeHtml(d) + '</span> ';
            });
        }
        if (data.msrs) {
            data.msrs.forEach(function (m) {
                html += '<span class="analysis-summary-pill">' + escapeHtml(m.field) + '</span> ';
            });
        }
        html += '</span>';
        return html;
    }

    // ─── Collapse / Expand ────────────────────────────────────────────────────

    function collapsePanel(gridId) {
        var st = _state[gridId];
        if (st) st.collapsed = true;
    }

    function expandPanel(gridId) {
        var st = _state[gridId];
        if (st) st.collapsed = false;
    }

    function collapseResult(gridId) {
        var st = _state[gridId];
        if (st) st.resultCollapsed = true;
    }

    function expandResult(gridId) {
        var st = _state[gridId];
        if (st) st.resultCollapsed = false;
    }

    // ─── Searcher form data ──────────────────────────────────────────────────

    /**
     * 從 gridId (wtTable_X) 推導出搜尋面板 formId (wtForm_X)，
     * 收集搜尋面板表單值並序列化為 JSON 字串，供後端 CreateAndBindVm 使用。
     * @param {string} gridId - grid table ID (e.g. "wtTable_abc123")
     * @returns {string|undefined} JSON string of searcher form data, or undefined if not available
     */
    function collectSearcherFormData(gridId) {
        if (typeof ff === 'undefined' || typeof ff.GetSearchFormData !== 'function') return undefined;
        var formId = gridId.replace(/^wtTable_/, 'wtForm_');
        var formEl = document.getElementById(formId);
        if (!formEl) return undefined;
        var data = ff.GetSearchFormData(formId, 'Searcher');
        if (!data || Object.keys(data).length === 0) return undefined;
        return JSON.stringify(data);
    }

    // ─── Toggle ──────────────────────────────────────────────────────────────

    /**
     * 切換分析模式顯示狀態
     */
    function toggle(gridId, listVmType) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;

        var st = initState(gridId, listVmType);

        if (!st.visible) {
            panel.style.display = 'block';
            st.visible = true;
            if (!st.fields) {
                loadMeta(gridId, listVmType, panel);
            }
        } else {
            panel.style.display = 'none';
            st.visible = false;
            // Destroy SortableJS instances on hide
            destroySortableInstances(gridId);
        }
    }

    function destroySortableInstances(gridId) {
        var st = _state[gridId];
        if (!st || !st.sortableInstances) return;
        st.sortableInstances.forEach(function (inst) {
            if (inst && typeof inst.destroy === 'function') {
                inst.destroy();
            }
        });
        st.sortableInstances = [];
    }

    // ─── Load Meta ───────────────────────────────────────────────────────────

    /**
     * 載入欄位 Metadata（GET /_analysis/meta）
     */
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
            msg.textContent = '載入欄位失敗：' + err.message;
            panelEl.appendChild(msg);
        });
    }

    // ─── Render Panel ────────────────────────────────────────────────────────

    /**
     * 渲染分析面板（維度/度量選擇器 + 查詢按鈕）
     */
    function renderPanel(gridId, fields, panelEl) {
        clearChildren(panelEl);

        var container = document.createElement('div');
        container.className = 'layui-card';

        var body = document.createElement('div');
        body.className = 'layui-card-body';

        body.appendChild(createFieldSection(gridId, fields, 'Dimension', '維度'));
        body.appendChild(createFieldSection(gridId, fields, 'Measure', '度量'));

        var btnRow = document.createElement('div');
        btnRow.style.marginTop = '10px';

        var queryBtn = document.createElement('button');
        queryBtn.type = 'button';
        queryBtn.className = 'layui-btn layui-btn-sm layui-btn-normal';
        queryBtn.textContent = '查詢';
        queryBtn.addEventListener('click', function () { query(gridId); });

        var exportXlsxBtn = document.createElement('button');
        exportXlsxBtn.type = 'button';
        exportXlsxBtn.className = 'layui-btn layui-btn-sm';
        exportXlsxBtn.textContent = '匯出 Excel';
        exportXlsxBtn.addEventListener('click', function () { exportData(gridId, 'xlsx'); });

        var exportCsvBtn = document.createElement('button');
        exportCsvBtn.type = 'button';
        exportCsvBtn.className = 'layui-btn layui-btn-sm layui-btn-warm';
        exportCsvBtn.textContent = '匯出 CSV';
        exportCsvBtn.addEventListener('click', function () { exportData(gridId, 'csv'); });

        var pivotWrapper = document.createElement('label');
        pivotWrapper.style.marginLeft = '15px';
        pivotWrapper.style.display = 'inline-flex';
        pivotWrapper.style.alignItems = 'center';
        pivotWrapper.style.cursor = 'pointer';

        var pivotToggle = document.createElement('input');
        pivotToggle.type = 'checkbox';
        pivotToggle.className = 'analysis-pivot-toggle';
        pivotToggle.dataset.gridId = gridId;
        pivotToggle.style.marginRight = '5px';

        pivotToggle.addEventListener('change', function() {
            var selects = document.querySelectorAll('.analysis-pivot-dim-select[data-grid-id="' + gridId + '"]');
            var isChecked = this.checked;
            selects.forEach(function(s) { s.style.display = isChecked ? 'inline-block' : 'none'; });
            if (isChecked) {
                // Ensure only one pivot dim is selected initially
                var hasChecked = false;
                document.querySelectorAll('.analysis-pivot-dim-select[data-grid-id="' + gridId + '"]').forEach(function(r) {
                    if (r.checked) hasChecked = true;
                });
                if (!hasChecked && selects.length > 0) selects[0].checked = true;
            }
        });

        var pivotText = document.createElement('span');
        pivotText.textContent = '樞紐模式';
        pivotText.style.fontWeight = 'bold';

        pivotWrapper.appendChild(pivotToggle);
        pivotWrapper.appendChild(pivotText);

        var chartExportWrapper = document.createElement('label');
        chartExportWrapper.style.cssText = 'display:inline-flex;align-items:center;margin-left:10px;cursor:pointer;';
        var chartExportCb = document.createElement('input');
        chartExportCb.type = 'checkbox';
        chartExportCb.className = 'analysis-export-chart-cb';
        chartExportCb.dataset.gridId = gridId;
        chartExportCb.style.marginRight = '4px';
        var chartExportLabel = document.createElement('span');
        chartExportLabel.textContent = '含圖表';
        chartExportWrapper.appendChild(chartExportCb);
        chartExportWrapper.appendChild(chartExportLabel);

        btnRow.appendChild(queryBtn);
        btnRow.appendChild(exportXlsxBtn);
        btnRow.appendChild(exportCsvBtn);
        btnRow.appendChild(chartExportWrapper);
        btnRow.appendChild(pivotWrapper);
        body.appendChild(btnRow);

        var chartToggleRow = document.createElement('div');
        chartToggleRow.id = 'analysis-chart-toggle-' + gridId;
        chartToggleRow.style.marginTop = '8px';
        chartToggleRow.style.display = 'none'; // shown after first query
        ['bar', 'line', 'bar-stacked', 'pie', 'card'].forEach(function (ct) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'layui-btn layui-btn-xs';
            btn.style.marginRight = '4px';
            btn.textContent = ct;
            btn.addEventListener('click', function () {
                var st = _state[gridId];
                if (!st || !st.lastResult) return;
                var resultDiv = document.getElementById('analysis-result-' + gridId);
                if (!resultDiv) return;
                var oldChart = document.getElementById('analysis-chart-' + gridId);
                if (oldChart && oldChart.parentNode) oldChart.parentNode.removeChild(oldChart);
                var oldCards = resultDiv.querySelector('.analysis-cards');
                if (oldCards && oldCards.parentNode) oldCards.parentNode.removeChild(oldCards);
                renderChart(gridId, st.lastResult, st.lastReq, st.lastDimFields, resultDiv, ct);
            });
            chartToggleRow.appendChild(btn);
        });
        body.appendChild(chartToggleRow);

        var drillBar = document.createElement('div');
        drillBar.id = 'analysis-drill-bar-' + gridId;
        drillBar.style.marginTop = '6px';
        drillBar.style.display = 'none';
        body.appendChild(drillBar);

        var resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-' + gridId;
        resultDiv.style.marginTop = '15px';
        body.appendChild(resultDiv);

        container.appendChild(body);
        panelEl.appendChild(container);
    }

    function createFieldSection(gridId, fields, kind, label) {
        var section = document.createElement('div');
        section.style.marginBottom = '8px';

        var title = document.createElement('strong');
        title.textContent = label + '：';
        section.appendChild(title);

        fields.filter(function (f) { return f.kind === kind; }).forEach(function (f) {
            var wrapper = document.createElement('label');
            wrapper.style.marginLeft = '12px';

            var pivotRadio = null;
            if (kind === 'Dimension') {
                pivotRadio = document.createElement('input');
                pivotRadio.type = 'radio';
                pivotRadio.name = 'pivot-dim-' + gridId;
                pivotRadio.value = f.fieldName;
                pivotRadio.className = 'analysis-pivot-dim-select';
                pivotRadio.dataset.gridId = gridId;
                pivotRadio.style.display = 'none'; // hidden by default until pivot mode enabled
                pivotRadio.style.marginRight = '4px';
                wrapper.appendChild(pivotRadio);
            }

            var cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.className = 'analysis-field-cb';
            cb.dataset.gridId = gridId;
            cb.dataset.kind = kind;
            cb.dataset.fieldName = f.fieldName;
            cb.dataset.displayName = f.displayName;
            if (kind === 'Dimension' && f.isDate) {
                cb.dataset.isDate = 'true';
                var hierarchySelect = document.createElement('select');
                hierarchySelect.className = 'analysis-hierarchy-select';
                hierarchySelect.dataset.field = f.fieldName;
                hierarchySelect.style.marginLeft = '4px';
                [
                    { value: 'Year', text: '年' },
                    { value: 'Quarter', text: '季' },
                    { value: 'Month', text: '月' },
                    { value: 'Day', text: '日' }
                ].forEach(function (h) {
                    var opt = document.createElement('option');
                    opt.value = h.value;
                    opt.textContent = h.text;
                    if (h.value === 'Month') opt.selected = true;
                    hierarchySelect.appendChild(opt);
                });
                wrapper.appendChild(hierarchySelect);
            }
            if (kind === 'Measure') {
                var funcs = parseFuncs(f.allowedFuncs || 0);
                if (funcs.length === 1) {
                    cb.dataset.defaultFunc = funcs[0]; // single func: no UI needed
                } else if (funcs.length > 1) {
                    var funcSelect = document.createElement('select');
                    funcSelect.className = 'analysis-func-select';
                    funcSelect.style.marginLeft = '4px';
                    funcs.forEach(function (fn) {
                        var opt = document.createElement('option');
                        opt.value = fn;
                        opt.textContent = fn;
                        funcSelect.appendChild(opt);
                    });
                    wrapper.appendChild(funcSelect);
                }
            }

            var text = document.createTextNode('\u00a0' + f.displayName);
            wrapper.appendChild(cb);
            wrapper.appendChild(text);
            section.appendChild(wrapper);
        });

        return section;
    }

    // ─── collectSelection (backward compat) ──────────────────────────────────

    /**
     * 收集選取的維度/度量（供 query 和 exportData 共用）
     * 若度量旁有 <select>（聚合函式選擇器），讀取其 value；否則讀 dataset.defaultFunc。
     * @param {string} gridId
     * @returns {{ dims: string[], msrs: Array<{field:string, func:string}> }}
     */
    function collectSelection(gridId) {
        var dims = [];
        var msrs = [];
        var dimensionHierarchies = {};
        var panel = document.getElementById('analysis-panel-' + gridId);

        var hasQsa = panel && typeof panel.querySelectorAll === 'function';
        var root = hasQsa ? panel : document;
        var selector = hasQsa ? '.analysis-field-cb:checked' : '.analysis-field-cb[data-grid-id="' + gridId + '"]:checked';

        root.querySelectorAll(selector)
            .forEach(function (cb) {
                if (cb.dataset.kind === 'Dimension') {
                    dims.push(cb.dataset.fieldName);
                    if (cb.dataset.isDate === 'true') {
                        var hSel = cb.parentNode && cb.parentNode.querySelector
                            ? cb.parentNode.querySelector('.analysis-hierarchy-select')
                            : null;
                        if (hSel) {
                            dimensionHierarchies[cb.dataset.fieldName] = hSel.value;
                        }
                    }
                } else {
                    var sel = cb.nextElementSibling;
                    var func = (sel && sel.tagName === 'SELECT')
                        ? sel.value
                        : (cb.dataset.defaultFunc || 'Sum');
                    msrs.push({ field: cb.dataset.fieldName, func: func });
                }
            });
        return { dims: dims, msrs: msrs, dimensionHierarchies: dimensionHierarchies };
    }

    // ─── Query ───────────────────────────────────────────────────────────────

    /**
     * 收集選取的維度/度量並 POST /_analysis/query
     */
    function query(gridId) {
        var st = _state[gridId];
        if (!st) return;

        var sel = collectSelection(gridId);
        var dims = sel.dims;
        var msrs = sel.msrs;

        var errors = validateSelection(dims, msrs);
        if (errors.length > 0) {
            window.alert(errors.join('\n'));
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
                window.alert('請選擇一個樞紐(Pivot)維度');
                return;
            }
            if (dims.indexOf(pivotDim) < 0) {
                window.alert('樞紐(Pivot)維度必須是已勾選的維度之一');
                return;
            }
        }

        var searcherJson = collectSearcherFormData(gridId);
        var req = {
            listVmType: st.listVmType,
            dimensions: dims,
            measures: msrs,
            filters: [],
            dimensionHierarchies: Object.keys(sel.dimensionHierarchies).length > 0
                ? sel.dimensionHierarchies : undefined,
            searcherFormData: searcherJson
        };

        if (isPivot) {
            req.pivotDimension = pivotDim;
        }

        var resultDiv = document.getElementById('analysis-result-' + gridId);
        if (resultDiv) resultDiv.textContent = '查詢中...';

        var endpoint = isPivot ? '/_analysis/pivot' : '/_analysis/query';
        fetch(endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req)
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t); });
            return res.json();
        })
        .then(function (result) {
            if (!resultDiv) return;
            clearChildren(resultDiv);

            if (result.truncated) {
                var warn = document.createElement('div');
                warn.className = 'layui-alert layui-alert-warm';
                warn.textContent = '結果已截斷，僅顯示前 10,000 列。';
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

                // Keep chart toggles hidden or disabled for pivot as it's typically stacked bar
                var toggleRow = document.getElementById('analysis-chart-toggle-' + gridId);
                if (toggleRow) toggleRow.style.display = 'none';
            } else {
                renderTable(gridId, result, resultDiv, dateDimSet);
                renderChart(gridId, result, { dimensions: dims, measures: msrs }, dimFields, resultDiv);

                // Store for chart type toggle + drill
                st.lastResult = result;
                st.lastReq = {
                    dimensions: dims, measures: msrs,
                    dimensionHierarchies: Object.keys(sel.dimensionHierarchies).length > 0
                        ? sel.dimensionHierarchies : undefined
                };
                st.lastDimFields = dimFields;
                // Reset drill state on fresh query
                st.drillStack = [];
                st.drillFilters = [];
                updateDrillBar(gridId);
                var toggleRow = document.getElementById('analysis-chart-toggle-' + gridId);
                if (toggleRow) toggleRow.style.display = 'block';
            }
        })
        .catch(function (err) {
            if (resultDiv) resultDiv.textContent = '查詢失敗：' + err.message;
        });
    }

    // ─── Render Pivot Table ──────────────────────────────────────────────────

    /**
     * 渲染 Pivot 結果表格
     */
    function renderPivotTable(gridId, result, container, dateDims) {
        var wrapper = document.createElement('div');
        wrapper.style.overflowX = 'auto'; // allow horizontal scrolling

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
                if (val !== null && val !== undefined) {
                    td.textContent = dateDims[col] ? formatDateKey(val) : String(val);
                } else {
                    td.textContent = '-'; // empty for pivot
                }
                tr.appendChild(td);
            });
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        wrapper.appendChild(table);
        container.appendChild(wrapper);
    }

    // ─── Render Pivot Chart ──────────────────────────────────────────────────

    /**
     * 渲染 Pivot 圖表 (Stacked Bar)
     */
    function renderPivotChart(gridId, result, req, container) {
        if (typeof window.echarts === 'undefined') return;

        var chartDiv = document.createElement('div');
        chartDiv.id = 'analysis-chart-' + gridId;
        chartDiv.style.width = '100%';
        chartDiv.style.height = '350px';
        chartDiv.style.marginTop = '15px';
        container.appendChild(chartDiv);

        var chart = window.echarts.init(chartDiv);

        // Use first row dimension as X axis, fallback to something empty if none
        var firstRowDim = result.rowDimensions.length > 0 ? result.rowDimensions[0] : '';
        var categories = result.rows.map(function (r) {
            return firstRowDim ? String(r[firstRowDim] || '') : '總計';
        });

        // Create a series for each (PivotValue x Measure)
        var series = [];
        var legendData = [];
        result.pivotValues.forEach(function (pv) {
            result.measureNames.forEach(function (m) {
                var key = pv + '_' + m;
                legendData.push(key);
                series.push({
                    name: key,
                    type: 'bar',
                    stack: m, // Stack by measure
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

    // ─── Render Table ────────────────────────────────────────────────────────

    /**
     * 渲染聚合結果表格（所有值用 textContent 設值，XSS 安全）
     */
    function renderTable(gridId, result, container, dateDims) {
        dateDims = dateDims || {};
        var table = document.createElement('table');
        table.className = 'layui-table';
        table.style.marginTop = '10px';

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
                if (val !== null && val !== undefined) {
                    td.textContent = dateDims[col] ? formatDateKey(val) : String(val);
                } else {
                    td.textContent = '';
                }
                tr.appendChild(td);
            });
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        container.appendChild(table);
    }

    // ─── Render Chart ────────────────────────────────────────────────────────

    /**
     * 渲染 ECharts 圖表（若 echarts 全域變數不存在則略過）
     */
    function renderChart(gridId, result, req, dimFields, container, forceChartType) {
        var dimMeta = req.dimensions.map(function (d) {
            var f = (dimFields || []).filter(function (fd) { return fd.fieldName === d; })[0];
            return { fieldName: d, isDate: f ? f.isDate === true : false };
        });

        var chartType = forceChartType || detectChartType(dimMeta, req.measures);

        // card: 渲染 HTML 數字卡片，不使用 ECharts
        if (chartType === 'card') {
            var cardContainer = document.createElement('div');
            cardContainer.className = 'analysis-cards';
            cardContainer.style.display = 'flex';
            cardContainer.style.gap = '16px';
            cardContainer.style.marginTop = '15px';
            var row = result.rows && result.rows.length > 0 ? result.rows[0] : {};
            req.measures.forEach(function (m) {
                var key = m.field + '_' + m.func;
                var val = row[key];
                var card = document.createElement('div');
                card.className = 'analysis-card-item';
                card.style.cssText = 'flex:1;text-align:center;padding:20px;background:#f8f9fa;border-radius:8px;border:1px solid #e0e0e0;';
                var label = document.createElement('div');
                label.style.cssText = 'font-size:13px;color:#666;margin-bottom:8px;';
                label.textContent = key;
                var value = document.createElement('div');
                value.style.cssText = 'font-size:28px;font-weight:bold;color:#333;';
                value.textContent = val != null ? String(val) : '—';
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

        // pie: 圓餅圖
        if (chartType === 'pie') {
            var pieKey = req.measures[0].field + '_' + req.measures[0].func;
            chart.setOption({
                tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
                legend: { orient: 'vertical', left: 'left', data: categories },
                series: [{
                    name: pieKey,
                    type: 'pie',
                    radius: '55%',
                    center: ['50%', '55%'],
                    data: categories.map(function (cat, i) {
                        return { name: cat, value: result.rows[i][pieKey] };
                    }),
                    emphasis: { itemStyle: { shadowBlur: 10, shadowOffsetX: 0, shadowColor: 'rgba(0,0,0,0.5)' } }
                }]
            });
        } else {
            // bar / bar-stacked / line
            var series = req.measures.map(function (m) {
                var key = m.field + '_' + m.func;
                return {
                    name: key,
                    type: chartType === 'line' ? 'line' : 'bar',
                    stack: chartType === 'bar-stacked' ? 'total' : undefined,
                    data: result.rows.map(function (r) { return r[key]; })
                };
            });

            chart.setOption({
                tooltip: { trigger: 'axis' },
                legend: { data: req.measures.map(function (m) { return m.field + '_' + m.func; }) },
                xAxis: { type: 'category', data: categories },
                yAxis: { type: 'value' },
                series: series
            });
        }

        // drill-down click handler
        chart.on('click', function (params) {
            if (!firstDim) return;
            var rawRow = result.rows[params.dataIndex];
            if (!rawRow) return;
            var rawValue = rawRow[firstDim];
            drillDown(gridId, firstDim, rawValue, firstDimIsDate);
        });
    }

    // ─── Drill ───────────────────────────────────────────────────────────────

    /**
     * 更新 drill 路徑列（顯示麵包屑 + 返回/重置按鈕）
     */
    function updateDrillBar(gridId) {
        var drillBar = document.getElementById('analysis-drill-bar-' + gridId);
        if (!drillBar) return;

        var st = _state[gridId];
        if (!st || st.drillStack.length === 0) {
            drillBar.style.display = 'none';
            clearChildren(drillBar);
            return;
        }

        drillBar.style.display = 'block';
        clearChildren(drillBar);

        var pathSpan = document.createElement('span');
        pathSpan.textContent = '全部';
        st.drillStack.forEach(function (frame) {
            pathSpan.textContent += ' > ' + String(frame.label);
        });
        drillBar.appendChild(pathSpan);

        var backBtn = document.createElement('button');
        backBtn.type = 'button';
        backBtn.className = 'layui-btn layui-btn-xs layui-btn-primary';
        backBtn.style.marginLeft = '8px';
        backBtn.textContent = '返回上層';
        backBtn.addEventListener('click', function () { drillBack(gridId); });
        drillBar.appendChild(backBtn);

        var resetBtn = document.createElement('button');
        resetBtn.type = 'button';
        resetBtn.className = 'layui-btn layui-btn-xs layui-btn-danger';
        resetBtn.style.marginLeft = '4px';
        resetBtn.textContent = '重置';
        resetBtn.addEventListener('click', function () { drillReset(gridId); });
        drillBar.appendChild(resetBtn);
    }

    /**
     * 執行 drill-down：push 當前狀態到 stack，加 filter，重新查詢
     */
    function drillDown(gridId, dimField, value, isDate) {
        var st = _state[gridId];
        if (!st || !st.lastReq) return;

        // push current state
        var currentFilters = (st.drillFilters || []).slice();
        var currentHierarchies = {};
        if (st.lastReq.dimensionHierarchies) {
            Object.keys(st.lastReq.dimensionHierarchies).forEach(function (k) {
                currentHierarchies[k] = st.lastReq.dimensionHierarchies[k];
            });
        }
        var label = isDate ? formatDateKey(value) : String(value);
        st.drillStack.push({
            filters: currentFilters,
            dimensionHierarchies: currentHierarchies,
            label: label
        });

        // add new filter
        var newFilters = currentFilters.concat([buildDrillFilter(dimField, value)]);
        st.drillFilters = newFilters;

        // auto-downgrade date hierarchy
        var newHierarchies = {};
        Object.keys(currentHierarchies).forEach(function (k) {
            newHierarchies[k] = currentHierarchies[k];
        });
        if (isDate && newHierarchies[dimField]) {
            var next = nextHierarchy(newHierarchies[dimField]);
            if (next) {
                newHierarchies[dimField] = next;
            }
        }

        updateDrillBar(gridId);
        drillQuery(gridId, newFilters, newHierarchies);
    }

    /**
     * 返回上一層 drill
     */
    function drillBack(gridId) {
        var st = _state[gridId];
        if (!st || st.drillStack.length === 0) return;

        var frame = st.drillStack.pop();
        st.drillFilters = frame.filters;

        updateDrillBar(gridId);
        drillQuery(gridId, frame.filters, frame.dimensionHierarchies);
    }

    /**
     * 重置 drill 回到最頂層
     */
    function drillReset(gridId) {
        var st = _state[gridId];
        if (!st) return;

        st.drillStack = [];
        st.drillFilters = [];

        updateDrillBar(gridId);
        // re-query with no drill filters and original hierarchies from collectSelection
        drillQuery(gridId, [], st.lastReq.dimensionHierarchies || {});
    }

    /**
     * drill 專用查詢（帶自訂 filters 和 hierarchies）
     */
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
        if (resultDiv) resultDiv.textContent = '查詢中...';

        fetch('/_analysis/query', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req)
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t); });
            return res.json();
        })
        .then(function (result) {
            if (!resultDiv) return;
            clearChildren(resultDiv);

            if (result.truncated) {
                var warn = document.createElement('div');
                warn.className = 'layui-alert layui-alert-warm';
                warn.textContent = '結果已截斷，僅顯示前 10,000 列。';
                resultDiv.appendChild(warn);
            }

            var dimFields = (st.fields || []).filter(function (f) {
                return f.kind === 'Dimension' && st.lastReq.dimensions.indexOf(f.fieldName) >= 0;
            });
            var dateDimSet = {};
            dimFields.forEach(function (f) { if (f.isDate) dateDimSet[f.fieldName] = true; });

            // Update lastReq with current drill state
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
            if (resultDiv) resultDiv.textContent = '查詢失敗：' + err.message;
        });
    }

    // ─── Export ──────────────────────────────────────────────────────────────

    /**
     * 匯出：fetch blob 觸發瀏覽器下載
     */
    function exportData(gridId, format) {
        var st = _state[gridId];
        if (!st) return;

        var sel = collectSelection(gridId);
        var dims = sel.dims;
        var msrs = sel.msrs;

        // Loading indicator
        var loaderId = null;
        if (window.layui && window.layui.layer) {
            loaderId = window.layui.layer.load(2);
        }

        function closeLoader() {
            if (loaderId !== null && window.layui && window.layui.layer) {
                window.layui.layer.close(loaderId);
            }
        }

        var exportHierarchies = Object.keys(sel.dimensionHierarchies).length > 0
            ? sel.dimensionHierarchies : undefined;

        var isPivot = false;
        var pivotDim = null;
        var pivotToggle = document.querySelector('.analysis-pivot-toggle[data-grid-id="' + gridId + '"]');
        if (pivotToggle && pivotToggle.checked) {
            isPivot = true;
            var pivotRadio = document.querySelector('.analysis-pivot-dim-select[data-grid-id="' + gridId + '"]:checked');
            if (pivotRadio) pivotDim = pivotRadio.value;
        }

        var searcherJson = collectSearcherFormData(gridId);
        var req = {
            listVmType: st.listVmType,
            dimensions: dims,
            measures: msrs,
            filters: [],
            dimensionHierarchies: exportHierarchies,
            searcherFormData: searcherJson
        };

        if (isPivot) {
            req.pivotDimension = pivotDim;
        }

        var chartCb = document.querySelector('.analysis-export-chart-cb[data-grid-id="' + gridId + '"]');
        var includeChart = chartCb && chartCb.checked ? 'true' : 'false';

        var endpoint = isPivot ? '/_analysis/pivot/export' : '/_analysis/export';
        return fetch(endpoint + '?format=' + encodeURIComponent(format) + '&includeChart=' + includeChart, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req)
        })
        .then(function (res) {
            if (!res.ok) return res.text().then(function (t) { throw new Error(t || 'HTTP ' + res.status); });
            return res.blob();
        })
        .then(function (blob) {
            closeLoader();
            var url = window.URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = 'analysis.' + format;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(url);
        })
        .catch(function (err) {
            closeLoader();
            window.alert('匯出失敗：' + err.message);
        });
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
        renderPivotTable: renderPivotTable,
        renderPivotChart: renderPivotChart,
        formatDateKey: formatDateKey,
        buildDrillFilter: buildDrillFilter,
        nextHierarchy: nextHierarchy,
        drillDown: drillDown,
        drillBack: drillBack,
        drillReset: drillReset,
        _getState: function (gridId) { return _state[gridId]; },
        _test: {
            buildPillHtml: buildPillHtml,
            collectDropZoneData: collectDropZoneData,
            buildSummaryBar: buildSummaryBar,
            escapeHtml: escapeHtml,
            getState: function (gridId) { return _state[gridId]; },
            collapsePanel: collapsePanel,
            expandPanel: expandPanel,
            collapseResult: collapseResult,
            expandResult: expandResult,
            validateSelection: function (data) {
                var dims = data && data.dims ? data.dims : [];
                var msrs = data && data.msrs ? data.msrs : [];
                var errors = validateSelection(dims, msrs);
                if (errors.length > 0) {
                    return { valid: false, errors: errors };
                }
                return { valid: true, errors: [] };
            }
        }
    };

}(typeof window !== 'undefined' ? window : global));
