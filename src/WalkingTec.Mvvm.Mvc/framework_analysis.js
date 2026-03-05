/**
 * framework_analysis.js
 * WTM Analysis Mode — 前端分析模式控制器
 *
 * 安全原則：所有來自伺服器的欄位名稱與值均透過 textContent 或 DOM 方法設值，
 * 禁止直接拼入 HTML 字串，以防 XSS。
 */
(function (window) {
    'use strict';

    /**
     * 判斷應使用哪種圖表類型（純函式，無副作用）
     * @param {Array} dims - 選取的維度陣列（每項 {fieldName, isDate}）
     * @param {Array} msrs - 選取的度量陣列
     * @returns {string} 'card'|'bar'|'bar-stacked'|'line'
     */
    function detectChartType(dims, msrs) {
        if (dims.length === 0) return 'card';
        if (dims.some(function (d) { return d.isDate; })) return 'line';
        if (dims.length >= 2) return 'bar-stacked';
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

    /** 清空 DOM 節點的所有子節點 */
    function clearChildren(el) {
        while (el.firstChild) {
            el.removeChild(el.firstChild);
        }
    }

    // ─── 狀態 ─────────────────────────────────────────────────────────────────
    var _state = {};  // { [gridId]: { visible, listVmType, fields } }

    /**
     * 切換分析模式顯示狀態
     */
    function toggle(gridId, listVmType) {
        var panel = document.getElementById('analysis-panel-' + gridId);
        if (!panel) return;

        if (!_state[gridId]) {
            _state[gridId] = { visible: false, listVmType: listVmType, fields: null };
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

        btnRow.appendChild(queryBtn);
        btnRow.appendChild(exportXlsxBtn);
        btnRow.appendChild(exportCsvBtn);
        body.appendChild(btnRow);

        var resultDiv = document.createElement('div');
        resultDiv.id = 'analysis-result-' + gridId;
        resultDiv.style.marginTop = '15px';
        body.appendChild(resultDiv);

        container.appendChild(body);
        panelEl.appendChild(container);
    }

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

    function createFieldSection(gridId, fields, kind, label) {
        var section = document.createElement('div');
        section.style.marginBottom = '8px';

        var title = document.createElement('strong');
        title.textContent = label + '：';
        section.appendChild(title);

        fields.filter(function (f) { return f.kind === kind; }).forEach(function (f) {
            var wrapper = document.createElement('label');
            wrapper.style.marginLeft = '12px';

            var cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.className = 'analysis-field-cb';
            cb.dataset.gridId = gridId;
            cb.dataset.kind = kind;
            cb.dataset.fieldName = f.fieldName;
            cb.dataset.displayName = f.displayName;
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

    /**
     * 收集選取的維度/度量（供 query 和 exportData 共用）
     * 若度量旁有 <select>（聚合函式選擇器），讀取其 value；否則讀 dataset.defaultFunc。
     * @param {string} gridId
     * @returns {{ dims: string[], msrs: Array<{field:string, func:string}> }}
     */
    function collectSelection(gridId) {
        var dims = [];
        var msrs = [];
        document.querySelectorAll('.analysis-field-cb[data-grid-id="' + gridId + '"]:checked')
            .forEach(function (cb) {
                if (cb.dataset.kind === 'Dimension') {
                    dims.push(cb.dataset.fieldName);
                } else {
                    var sel = cb.nextElementSibling;
                    var func = (sel && sel.tagName === 'SELECT')
                        ? sel.value
                        : (cb.dataset.defaultFunc || 'Sum');
                    msrs.push({ field: cb.dataset.fieldName, func: func });
                }
            });
        return { dims: dims, msrs: msrs };
    }

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

        var req = {
            listVmType: st.listVmType,
            dimensions: dims,
            measures: msrs,
            filters: []
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

            renderTable(gridId, result, resultDiv);

            var dimFields = (st.fields || []).filter(function (f) {
                return f.kind === 'Dimension' && dims.indexOf(f.fieldName) >= 0;
            });
            renderChart(gridId, result, { dimensions: dims, measures: msrs }, dimFields, resultDiv);
        })
        .catch(function (err) {
            if (resultDiv) resultDiv.textContent = '查詢失敗：' + err.message;
        });
    }

    /**
     * 渲染聚合結果表格（所有值用 textContent 設值，XSS 安全）
     */
    function renderTable(gridId, result, container) {
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
                td.textContent = (val !== null && val !== undefined) ? String(val) : '';
                tr.appendChild(td);
            });
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        container.appendChild(table);
    }

    /**
     * 渲染 ECharts 圖表（若 echarts 全域變數不存在則略過）
     */
    function renderChart(gridId, result, req, dimFields, container) {
        if (typeof window.echarts === 'undefined') return;

        var chartDiv = document.createElement('div');
        chartDiv.id = 'analysis-chart-' + gridId;
        chartDiv.style.width = '100%';
        chartDiv.style.height = '350px';
        chartDiv.style.marginTop = '15px';
        container.appendChild(chartDiv);

        var dimMeta = req.dimensions.map(function (d) {
            var f = (dimFields || []).filter(function (fd) { return fd.fieldName === d; })[0];
            return { fieldName: d, isDate: f ? f.isDate === true : false };
        });

        var chartType = detectChartType(dimMeta, req.measures);
        var chart = window.echarts.init(chartDiv);
        var firstDim = req.dimensions[0];
        var categories = result.rows.map(function (r) { return String(r[firstDim] || ''); });
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

        return fetch('/_analysis/export?format=' + encodeURIComponent(format), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                listVmType: st.listVmType,
                dimensions: dims,
                measures: msrs,
                filters: []
            })
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
        parseFuncs: parseFuncs
    };

}(typeof window !== 'undefined' ? window : global));
