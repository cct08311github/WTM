/**
 * framework_analysis.js
 * WTM Analysis Mode v2 — 拖拉式 BI 面板控制器
 *
 * 安全原則：所有來自伺服器的欄位名稱與值均透過 textContent 或 DOM 方法設值，
 * 禁止直接拼入 HTML 字串，以防 XSS。
 */
(function (window) {
    'use strict';

    // ─── 純函式（無副作用）──────────────────────────────────────────────────

    function detectChartType(dims, msrs) {
        if (dims.length === 0) return 'card';
        if (dims.some(function (d) { return d.isDate; })) return 'line';
        if (dims.length >= 2) return 'bar-stacked';
        if (dims.length === 1 && msrs.length === 1) return 'pie';
        return 'bar';
    }

    function validateSelection(dims, msrs) {
        var errors = [];
        if (dims.length > 3) errors.push('維度最多選 3 個');
        if (msrs.length > 3) errors.push('度量最多選 3 個');
        if (dims.length === 0 && msrs.length === 0) errors.push('請至少選擇一個維度或度量');
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

    // ─── 狀態 ─────────────────────────────────────────────────────────────────
    var _state = {};

    function collectSearcherFormData(gridId) {
        if (typeof ff === 'undefined' || typeof ff.GetSearchFormData !== 'function') return undefined;
        var formId = gridId.replace(/^wtTable_/, 'wtForm_');
        var formEl = document.getElementById(formId);
        if (!formEl) return undefined;
        var data = ff.GetSearchFormData(formId, 'Searcher');
        if (!data || Object.keys(data).length === 0) return undefined;
        return JSON.stringify(data);
    }

    // ─── SortableJS 參照 ──────────────────────────────────────────────────────
    var Sortable = (typeof window !== 'undefined' && window.Sortable) ||
                   (typeof global !== 'undefined' && global.Sortable) || null;

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

        if (sel.dims.length > 0) {
            var dimLabel = document.createElement('span');
            dimLabel.className = 'summary-label';
            dimLabel.textContent = '維度：';
            bar.appendChild(dimLabel);
            sel.dims.forEach(function (d) {
                var tag = document.createElement('span');
                tag.textContent = '[' + d + ']';
                bar.appendChild(tag);
            });
        }
        if (sel.msrs.length > 0) {
            var msrLabel = document.createElement('span');
            msrLabel.className = 'summary-label';
            msrLabel.textContent = '度量：';
            bar.appendChild(msrLabel);
            sel.msrs.forEach(function (m) {
                var tag = document.createElement('span');
                tag.textContent = '[' + m.field + ' ' + m.func + ']';
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
                lastResult: null, lastReq: null, lastDimFields: null
            };
        }

        var st = _state[gridId];
        if (!st.visible) {
            panel.style.display = 'block';
            st.visible = true;
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
            msg.textContent = '載入欄位失敗：' + err.message;
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
        selectorTitle.textContent = '\u{1F4CA} 分析欄位選擇器';

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
        dimLabel.textContent = '維度（最多 3 個）';
        var dimZone = document.createElement('div');
        dimZone.className = 'analysis-dropzone analysis-dropzone--dim';
        dimZone.dataset.kind = 'Dimension';
        var dimPh = document.createElement('span');
        dimPh.className = 'analysis-dropzone-placeholder';
        dimPh.textContent = '拖入維度欄位...';
        dimZone.appendChild(dimPh);
        dimGroup.appendChild(dimLabel);
        dimGroup.appendChild(dimZone);

        var msrGroup = document.createElement('div');
        msrGroup.className = 'analysis-dropzone-group';
        var msrLabel = document.createElement('div');
        msrLabel.className = 'analysis-dropzone-label analysis-dropzone-label--msr';
        msrLabel.textContent = '度量（最多 3 個）';
        var msrZone = document.createElement('div');
        msrZone.className = 'analysis-dropzone analysis-dropzone--msr';
        msrZone.dataset.kind = 'Measure';
        var msrPh = document.createElement('span');
        msrPh.className = 'analysis-dropzone-placeholder';
        msrPh.textContent = '拖入度量欄位...';
        msrZone.appendChild(msrPh);
        msrGroup.appendChild(msrLabel);
        msrGroup.appendChild(msrZone);

        dzRow.appendChild(dimGroup);
        dzRow.appendChild(msrGroup);
        selectorBody.appendChild(dzRow);

        // Field pool
        var poolLabel = document.createElement('div');
        poolLabel.style.cssText = 'font-size:12px;color:#999;margin-bottom:4px;';
        poolLabel.textContent = '可用欄位（拖拉至上方區域）';
        selectorBody.appendChild(poolLabel);

        var fieldPool = document.createElement('div');
        fieldPool.className = 'analysis-field-pool';
        fields.forEach(function (f) {
            fieldPool.appendChild(createPoolPill(gridId, f));
        });
        selectorBody.appendChild(fieldPool);

        // Button row
        var btnRow = document.createElement('div');
        btnRow.className = 'analysis-btn-row';

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

        var chartExportWrapper = document.createElement('label');
        chartExportWrapper.style.cssText = 'display:inline-flex;align-items:center;cursor:pointer;';
        var chartExportCb = document.createElement('input');
        chartExportCb.type = 'checkbox';
        chartExportCb.className = 'analysis-export-chart-cb';
        chartExportCb.dataset.gridId = gridId;
        chartExportCb.style.marginRight = '4px';
        var chartExportLabel = document.createElement('span');
        chartExportLabel.textContent = '含圖表';
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
        pivotText.textContent = '樞紐模式';
        pivotText.style.fontWeight = 'bold';
        pivotWrapper.appendChild(pivotToggleCb);
        pivotWrapper.appendChild(pivotText);

        btnRow.appendChild(queryBtn);
        btnRow.appendChild(exportXlsxBtn);
        btnRow.appendChild(exportCsvBtn);
        btnRow.appendChild(chartExportWrapper);
        btnRow.appendChild(pivotWrapper);
        selectorBody.appendChild(btnRow);

        selectorSection.appendChild(selectorBody);

        // Summary bar (visible when collapsed)
        var summaryBar = document.createElement('div');
        summaryBar.className = 'analysis-summary-bar';
        var reQueryBtn = document.createElement('button');
        reQueryBtn.type = 'button';
        reQueryBtn.className = 'layui-btn layui-btn-xs layui-btn-normal';
        reQueryBtn.textContent = '重新查詢';
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
        resultTitle.textContent = '分析結果';
        var resultToggle = document.createElement('span');
        resultToggle.className = 'analysis-panel-toggle';
        resultToggle.textContent = '\u25BC';
        resultHeader.appendChild(resultTitle);
        resultHeader.appendChild(resultToggle);
        resultSection.appendChild(resultHeader);

        var resultBody = document.createElement('div');
        resultBody.className = 'analysis-result-body';

        var chartToggleRow = document.createElement('div');
        chartToggleRow.id = 'analysis-chart-toggle-' + gridId;
        chartToggleRow.className = 'analysis-chart-toggle-bar';
        chartToggleRow.style.display = 'none';
        ['bar', 'line', 'bar-stacked', 'pie', 'card'].forEach(function (ct) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'layui-btn layui-btn-xs';
            btn.textContent = ct;
            btn.addEventListener('click', function () {
                if (!st || !st.lastResult) return;
                var rd = document.getElementById('analysis-result-' + gridId);
                if (!rd) return;
                var oldChart = document.getElementById('analysis-chart-' + gridId);
                if (oldChart && oldChart.parentNode) oldChart.parentNode.removeChild(oldChart);
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
            if (!field || field.kind !== 'Dimension' || dimZone.querySelectorAll('.analysis-pill--dim').length > 3) {
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
            if (!field || field.kind !== 'Measure' || msrZone.querySelectorAll('.analysis-pill--msr').length > 3) {
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

    // ─── 查詢 ────────────────────────────────────────────────────────────────

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
        if (isPivot) req.pivotDimension = pivotDim;

        // Show result section
        var panel = document.getElementById('analysis-panel-' + gridId);
        var resultSection = panel ? panel.querySelector('.analysis-result-section') : null;
        if (resultSection) resultSection.style.display = '';

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

            // Auto-collapse field selector after query
            if (!st.collapsed) {
                toggleCollapse(gridId, 'analysis-panel-body', 'collapsed');
            }
        })
        .catch(function (err) {
            if (resultDiv) resultDiv.textContent = '查詢失敗：' + err.message;
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
            return firstRowDim ? String(r[firstRowDim] || '') : '總計';
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
                td.textContent = (val !== null && val !== undefined)
                    ? (dateDims[col] ? formatDateKey(val) : String(val)) : '';
                tr.appendChild(td);
            });
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        container.appendChild(table);
    }

    function renderChart(gridId, result, req, dimFields, container, forceChartType) {
        var dimMeta = req.dimensions.map(function (d) {
            var f = (dimFields || []).filter(function (fd) { return fd.fieldName === d; })[0];
            return { fieldName: d, isDate: f ? f.isDate === true : false };
        });
        var chartType = forceChartType || detectChartType(dimMeta, req.measures);

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
                label.textContent = key;
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
                    name: pieKey, type: 'pie', radius: '55%', center: ['50%', '55%'],
                    data: categories.map(function (cat, i) {
                        return { name: cat, value: result.rows[i][pieKey] };
                    }),
                    emphasis: { itemStyle: { shadowBlur: 10, shadowOffsetX: 0, shadowColor: 'rgba(0,0,0,0.5)' } }
                }]
            });
        } else {
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
            filters: [], dimensionHierarchies: exportHierarchies, searcherFormData: searcherJson
        };
        if (isPivot) req.pivotDimension = pivotDim;
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
        _getState: function (gridId) { return _state[gridId]; }
    };

}(typeof window !== 'undefined' ? window : global));
