/**
 * WalkingTec.Mvvm Dashboard Designer Module
 *
 * Provides a no-code drag-drop designer that lets users build DashboardDefinition
 * payloads without editing JSON.  Produces the SAME DashboardDefinition /
 * WidgetDefinition schema consumed by the runtime (framework_dashboard.js).
 *
 * Architecture overview
 * ─────────────────────
 * DesignerModel   — in-memory draft of the DashboardDefinition being built
 * DesignerPanel   — widget config panel (form helpers, live-preview trigger)
 * DesignerApi     — public surface: init, addWidget, updateWidget, removeWidget,
 *                   save, loadDataSources, previewWidget
 *
 * Dependencies (runtime):
 *   WtmDashboard (framework_dashboard.js) — reuses EventBus, WidgetRendererFactory,
 *                                            GridManager, DashboardEditor
 *   layui                                 — layer, form, element (tab)
 */
(function (global) {
    "use strict";

    // ─── Supported chart types (10 types matching runtime renderChart) ────────
    var CHART_TYPES = [
        { value: 'bar',        label: '長條圖 Bar' },
        { value: 'line',       label: '折線圖 Line' },
        { value: 'pie',        label: '圓餅圖 Pie' },
        { value: 'piehollow',  label: '甜甜圈 Pie Hollow' },
        { value: 'gauge',      label: '儀表盤 Gauge' },
        { value: 'funnel',     label: '漏斗圖 Funnel' },
        { value: 'radar',      label: '雷達圖 Radar' },
        { value: 'heatmap',    label: '熱力圖 Heatmap' },
        { value: 'scatter',    label: '散佈圖 Scatter' },
        { value: 'sankey',     label: 'Sankey 流向圖' }
    ];

    // Widget types offered in the designer
    var WIDGET_TYPES = [
        { value: 'kpi',     label: 'KPI 指標' },
        { value: 'chart',   label: '圖表 Chart' },
        { value: 'table',   label: '表格 Table' },
        { value: 'progress',label: '進度條 Progress' },
        { value: 'list',    label: '清單 List' },
        { value: 'embed',   label: '嵌入 Embed' },
        { value: 'static',  label: '靜態內容 Static' }
    ];

    // DateRange presets (mirrors computeDateRangePreset in runtime)
    var DATE_PRESETS = [
        { value: '',            label: '(自訂)' },
        { value: 'today',       label: '今日' },
        { value: 'last7days',   label: '過去 7 天' },
        { value: 'last30days',  label: '過去 30 天' },
        { value: 'thisMonth',   label: '本月' },
        { value: 'thisQuarter', label: '本季' },
        { value: 'thisYear',    label: '今年' }
    ];

    var FILTER_OPS = ['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'contains', 'notcontains', 'in', 'notin'];

    // ─── Unique ID helper ─────────────────────────────────────────────────────
    function genId() {
        return 'w_' + Date.now() + '_' + Math.floor(Math.random() * 1000);
    }

    // ─── DesignerModel ────────────────────────────────────────────────────────
    /**
     * Holds the draft DashboardDefinition while the user is designing.
     * All mutations go through this object so DesignerApi can read a clean copy.
     */
    var DesignerModel = {
        _def: null,

        /** Initialise or replace the draft from a DashboardDefinition object. */
        load: function (def) {
            this._def = JSON.parse(JSON.stringify(def || {
                schemaVersion: 1,
                id: '',
                title: '',
                owner: '',
                tenantId: null,
                sharing: { mode: 'private', roles: null },
                refreshInterval: 60,
                layout: [],
                widgets: {},
                filters: null,
                links: null
            }));
            return this;
        },

        /** Return a deep copy of the current draft (safe to serialise). */
        snapshot: function () {
            return JSON.parse(JSON.stringify(this._def));
        },

        setTitle: function (title) { this._def.title = title; },
        setRefreshInterval: function (n) { this._def.refreshInterval = n; },
        setSharing: function (mode, roles) {
            this._def.sharing = { mode: mode, roles: roles || null };
        },

        /** Add or replace a widget. Returns the widgetId. */
        upsertWidget: function (widgetId, widgetDef) {
            if (!widgetId) widgetId = genId();
            if (!this._def.widgets) this._def.widgets = {};
            this._def.widgets[widgetId] = widgetDef;
            return widgetId;
        },

        removeWidget: function (widgetId) {
            if (this._def.widgets) {
                delete this._def.widgets[widgetId];
            }
            if (this._def.layout) {
                this._def.layout = this._def.layout.filter(function (li) {
                    return li.id !== widgetId;
                });
            }
        },

        getWidget: function (widgetId) {
            return (this._def.widgets && this._def.widgets[widgetId]) || null;
        },

        getWidgets: function () {
            return this._def.widgets || {};
        },

        updateLayout: function (layout) {
            this._def.layout = layout || [];
        },

        addDashboardFilter: function (filterDef) {
            if (!this._def.filters) this._def.filters = [];
            this._def.filters.push(filterDef);
        },

        removeDashboardFilter: function (filterId) {
            if (!this._def.filters) return;
            this._def.filters = this._def.filters.filter(function (f) { return f.id !== filterId; });
        },

        getDef: function () { return this._def; }
    };

    // ─── DesignerPanel ────────────────────────────────────────────────────────
    /**
     * Builds and manages the widget config panel (LayUI form-in-layer).
     * Calls back into DesignerApi when the user commits changes.
     */
    var DesignerPanel = {
        _layerId: null,
        _availableSources: [],

        setAvailableSources: function (sources) {
            this._availableSources = sources || [];
        },

        // ── Build option HTML helpers ──────────────────────────────────────
        _buildOptions: function (items, selected) {
            return items.map(function (item) {
                var sel = item.value === selected ? ' selected' : '';
                return '<option value="' + _esc(item.value) + '"' + sel + '>' + _esc(item.label) + '</option>';
            }).join('');
        },

        // ── Open panel for adding a new widget ────────────────────────────
        openAdd: function (onConfirm) {
            this._openForm(null, null, onConfirm);
        },

        // ── Open panel for editing an existing widget ─────────────────────
        openEdit: function (widgetId, widgetDef, onConfirm) {
            this._openForm(widgetId, widgetDef, onConfirm);
        },

        _openForm: function (widgetId, existingDef, onConfirm) {
            var self = this;
            var def = existingDef || {};
            var cfg = def.config || {};
            var src = def.source || {};
            var isEdit = !!widgetId;

            // ── Main widget form HTML ──────────────────────────────────────
            var html = '<form class="layui-form" style="padding:16px 24px 0;">' +

                // Basic
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">標題</label>' +
                  '<div class="layui-input-block">' +
                    '<input type="text" id="dsd-title" name="title" class="layui-input" value="' + _esc(def.title || '') + '" placeholder="元件標題">' +
                  '</div></div>' +

                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">元件類型</label>' +
                  '<div class="layui-input-block">' +
                    '<select id="dsd-type" lay-filter="dsd-type">' + self._buildOptions(WIDGET_TYPES, def.type || 'kpi') + '</select>' +
                  '</div></div>' +

                // Width (grid cols 1-12)
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">寬度 (欄數)</label>' +
                  '<div class="layui-input-block">' +
                    '<input type="number" id="dsd-width" name="width" class="layui-input" value="' + (parseInt(cfg.gridW, 10) || 4) + '" min="1" max="12">' +
                  '</div></div>' +

                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">高度 (列數)</label>' +
                  '<div class="layui-input-block">' +
                    '<input type="number" id="dsd-height" name="height" class="layui-input" value="' + (parseInt(cfg.gridH, 10) || 3) + '" min="1" max="20">' +
                  '</div></div>' +

                // Data source
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">資料來源</label>' +
                  '<div class="layui-input-block">' +
                    '<select id="dsd-source-kind" lay-filter="dsd-source-kind">' +
                      '<option value="">（無）</option>' +
                      '<option value="analysis"' + (src.kind === 'analysis' ? ' selected' : '') + '>Analysis VM</option>' +
                      '<option value="rest"' + (src.kind === 'rest' ? ' selected' : '') + '>REST API</option>' +
                    '</select>' +
                  '</div></div>' +

                // Analysis VM selector (shown when kind=analysis)
                '<div id="dsd-analysis-block" style="display:none;">' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">Analysis VM</label>' +
                    '<div class="layui-input-block">' +
                      '<select id="dsd-vm-select" lay-filter="dsd-vm-select">' +
                        '<option value="">（選擇 VM）</option>' +
                        self._buildVmOptions(src.listVmType || '') +
                      '</select>' +
                    '</div></div>' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">維度欄位</label>' +
                    '<div class="layui-input-block">' +
                      '<input type="text" id="dsd-dimensions" name="dimensions" class="layui-input" value="' + _esc(self._serializeDimensions(src.dimensions)) + '" placeholder="逗號分隔欄位名，如 year,region">' +
                    '</div></div>' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">指標欄位</label>' +
                    '<div class="layui-input-block">' +
                      '<input type="text" id="dsd-measures" name="measures" class="layui-input" value="' + _esc(self._serializeMeasures(src.measures)) + '" placeholder="逗號分隔，如 revenue:Sum,count:Count">' +
                    '</div></div>' +
                '</div>' +

                // REST URL (shown when kind=rest)
                '<div id="dsd-rest-block" style="display:none;">' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">REST URL</label>' +
                    '<div class="layui-input-block">' +
                      '<input type="text" id="dsd-rest-url" name="restUrl" class="layui-input" value="' + _esc((src.restOptions && src.restOptions.url) || '') + '" placeholder="https://…">' +
                    '</div></div>' +
                '</div>' +

                // Chart type (shown for chart widgets)
                '<div id="dsd-chart-block" style="display:none;">' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">圖表類型</label>' +
                    '<div class="layui-input-block">' +
                      '<select id="dsd-chart-type">' +
                        '<option value="">(自動推斷)</option>' +
                        self._buildOptions(CHART_TYPES, cfg.chartType || '') +
                      '</select>' +
                    '</div></div>' +
                '</div>' +

                // KPI format (shown for kpi widgets)
                '<div id="dsd-kpi-block" style="display:none;">' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">格式</label>' +
                    '<div class="layui-input-block">' +
                      '<select id="dsd-kpi-format">' +
                        '<option value="">(數字)</option>' +
                        '<option value="currency"' + (cfg.format === 'currency' ? ' selected' : '') + '>貨幣</option>' +
                        '<option value="percent"' + (cfg.format === 'percent' ? ' selected' : '') + '>百分比</option>' +
                      '</select>' +
                    '</div></div>' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">前置符號</label>' +
                    '<div class="layui-input-block">' +
                      '<input type="text" id="dsd-kpi-prefix" class="layui-input" value="' + _esc(cfg.prefix || '') + '" placeholder="如 $, NT$">' +
                    '</div></div>' +
                '</div>' +

                // Embed URL (shown for embed widgets)
                '<div id="dsd-embed-block" style="display:none;">' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">嵌入 URL</label>' +
                    '<div class="layui-input-block">' +
                      '<input type="text" id="dsd-embed-url" class="layui-input" value="' + _esc(cfg.url || '') + '" placeholder="https://…">' +
                    '</div></div>' +
                '</div>' +

                // Static content (shown for static widgets)
                '<div id="dsd-static-block" style="display:none;">' +
                  '<div class="layui-form-item">' +
                    '<label class="layui-form-label">文字內容</label>' +
                    '<div class="layui-input-block">' +
                      '<textarea id="dsd-static-text" class="layui-textarea" placeholder="靜態顯示文字">' + _esc(cfg.text || '') + '</textarea>' +
                    '</div></div>' +
                '</div>' +

                // Thresholds
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">閾值警示</label>' +
                  '<div class="layui-input-block">' +
                    '<div id="dsd-thresholds-container">' + self._buildThresholdsHtml(cfg.thresholds) + '</div>' +
                    '<button type="button" class="layui-btn layui-btn-sm layui-btn-primary" id="dsd-add-threshold">+ 新增閾值</button>' +
                  '</div></div>' +

                // Drill-down links
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">Drill-Down</label>' +
                  '<div class="layui-input-block">' +
                    '<div id="dsd-drilldown-container">' + self._buildDrillDownHtml(def.drillDown) + '</div>' +
                    '<button type="button" class="layui-btn layui-btn-sm layui-btn-primary" id="dsd-add-drilldown">+ 新增 Drill-Down</button>' +
                  '</div></div>' +

                // Filters (widget-level)
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">篩選條件</label>' +
                  '<div class="layui-input-block">' +
                    '<div id="dsd-filters-container">' + self._buildFiltersHtml(src.filters) + '</div>' +
                    '<button type="button" class="layui-btn layui-btn-sm layui-btn-primary" id="dsd-add-filter">+ 新增篩選</button>' +
                  '</div></div>' +

                // DateRange preset
                '<div class="layui-form-item">' +
                  '<label class="layui-form-label">日期範圍預設</label>' +
                  '<div class="layui-input-block">' +
                    '<select id="dsd-date-preset">' +
                      self._buildOptions(DATE_PRESETS, cfg.datePreset || '') +
                    '</select>' +
                  '</div></div>' +

                // Live preview
                '<div class="layui-form-item" style="border-top:1px solid #eee;padding-top:10px;">' +
                  '<label class="layui-form-label">即時預覽</label>' +
                  '<div class="layui-input-block">' +
                    '<button type="button" class="layui-btn layui-btn-sm" id="dsd-btn-preview">預覽</button>' +
                    '<div id="dsd-preview-container" style="min-height:160px;border:1px dashed #ccc;margin-top:8px;padding:8px;"></div>' +
                  '</div></div>' +

                '<div class="layui-form-item" style="text-align:right;padding-bottom:8px;">' +
                  '<button type="button" class="layui-btn" id="dsd-btn-confirm">' + (isEdit ? '儲存' : '新增') + '</button>' +
                  '<button type="button" class="layui-btn layui-btn-primary" id="dsd-btn-cancel">取消</button>' +
                '</div>' +
                '</form>';

            self._layerId = layui.layer.open({
                type: 1,
                title: isEdit ? '編輯元件' : '新增元件',
                area: ['600px', '90vh'],
                scrollbar: true,
                content: html,
                success: function () {
                    self._wireFormEvents(widgetId, onConfirm);
                },
                end: function () { self._layerId = null; }
            });
        },

        _buildVmOptions: function (selectedVm) {
            return this._availableSources
                .filter(function (s) { return s.kind === 'analysis'; })
                .map(function (s) {
                    var sel = s.name === selectedVm ? ' selected' : '';
                    return '<option value="' + _esc(s.name) + '"' + sel + '>' + _esc(s.name) + '</option>';
                }).join('');
        },

        _buildThresholdsHtml: function (thresholds) {
            if (!thresholds || !thresholds.length) return '';
            return thresholds.map(function (t, i) {
                return '<div class="dsd-threshold-row" data-idx="' + i + '">' +
                    '<input type="number" class="layui-input dsd-th-value" style="width:90px;display:inline-block;" placeholder="值" value="' + _esc(String(t.value || 0)) + '">' +
                    '<input type="color" class="dsd-th-color" style="width:40px;display:inline-block;" value="' + _esc(t.color || '#ff0000') + '">' +
                    '<input type="text" class="layui-input dsd-th-label" style="width:90px;display:inline-block;" placeholder="標籤" value="' + _esc(t.label || '') + '">' +
                    '<button type="button" class="layui-btn layui-btn-xs layui-btn-danger dsd-remove-threshold">刪除</button>' +
                    '</div>';
            }).join('');
        },

        _buildDrillDownHtml: function (links) {
            if (!links || !links.length) return '';
            return links.map(function (dl, i) {
                return '<div class="dsd-dd-row" data-idx="' + i + '">' +
                    '<input type="text" class="layui-input dsd-dd-srcfield" style="width:110px;display:inline-block;" placeholder="sourceField" value="' + _esc(dl.sourceField || '') + '">' +
                    '<input type="text" class="layui-input dsd-dd-targetfilter" style="width:110px;display:inline-block;" placeholder="targetFilterId" value="' + _esc(dl.targetFilterId || '') + '">' +
                    '<input type="text" class="layui-input dsd-dd-targetwidget" style="width:110px;display:inline-block;" placeholder="targetWidgetId(選)" value="' + _esc(dl.targetWidgetId || '') + '">' +
                    '<button type="button" class="layui-btn layui-btn-xs layui-btn-danger dsd-remove-dd">刪除</button>' +
                    '</div>';
            }).join('');
        },

        _buildFiltersHtml: function (filters) {
            if (!filters || !filters.length) return '';
            var opOptions = FILTER_OPS.map(function (op) {
                return '<option value="' + op + '">' + op + '</option>';
            }).join('');
            return filters.map(function (f, i) {
                return '<div class="dsd-filter-row" data-idx="' + i + '">' +
                    '<input type="text" class="layui-input dsd-f-field" style="width:100px;display:inline-block;" placeholder="field" value="' + _esc(f.field || '') + '">' +
                    '<select class="dsd-f-op" style="width:90px;display:inline-block;">' + opOptions + '</select>' +
                    '<input type="text" class="layui-input dsd-f-value" style="width:100px;display:inline-block;" placeholder="value" value="' + _esc(f.value || '') + '">' +
                    '<button type="button" class="layui-btn layui-btn-xs layui-btn-danger dsd-remove-filter">刪除</button>' +
                    '</div>';
            }).join('');
        },

        _serializeDimensions: function (dims) {
            if (!dims || !dims.length) return '';
            return dims.map(function (d) { return d.field; }).join(',');
        },

        _serializeMeasures: function (msrs) {
            if (!msrs || !msrs.length) return '';
            return msrs.map(function (m) { return m.field + ':' + m.func; }).join(',');
        },

        _wireFormEvents: function (existingWidgetId, onConfirm) {
            var self = this;

            // ── Toggle source-kind blocks ──────────────────────────────────
            function updateSourceBlock() {
                var kind = _val('dsd-source-kind');
                _display('dsd-analysis-block', kind === 'analysis');
                _display('dsd-rest-block', kind === 'rest');
                if (layui && layui.form) layui.form.render('select');
            }

            // ── Toggle widget-type blocks ──────────────────────────────────
            function updateTypeBlock() {
                var type = _val('dsd-type');
                _display('dsd-chart-block', type === 'chart');
                _display('dsd-kpi-block', type === 'kpi');
                _display('dsd-embed-block', type === 'embed');
                _display('dsd-static-block', type === 'static');
                if (layui && layui.form) layui.form.render('select');
            }

            layui.form.on('select(dsd-source-kind)', updateSourceBlock);
            layui.form.on('select(dsd-type)', updateTypeBlock);

            // Initial visibility
            updateSourceBlock();
            updateTypeBlock();
            layui.form.render();

            // ── Dynamic row additions ──────────────────────────────────────
            _on('dsd-add-threshold', 'click', function () {
                var container = document.getElementById('dsd-thresholds-container');
                if (!container) return;
                var newRow = document.createElement('div');
                newRow.className = 'dsd-threshold-row';
                // Number input for threshold value
                var numInp = document.createElement('input');
                numInp.type = 'number';
                numInp.className = 'layui-input dsd-th-value';
                numInp.style.cssText = 'width:90px;display:inline-block;';
                numInp.placeholder = '值';
                numInp.value = '0';
                // Colour picker
                var colInp = document.createElement('input');
                colInp.type = 'color';
                colInp.className = 'dsd-th-color';
                colInp.style.cssText = 'width:40px;display:inline-block;';
                colInp.value = '#ff0000';
                // Label input
                var lblInp = document.createElement('input');
                lblInp.type = 'text';
                lblInp.className = 'layui-input dsd-th-label';
                lblInp.style.cssText = 'width:90px;display:inline-block;';
                lblInp.placeholder = '標籤';
                // Delete button
                var delBtn = _makeDangerBtn('刪除', 'dsd-remove-threshold');
                newRow.appendChild(numInp);
                newRow.appendChild(colInp);
                newRow.appendChild(lblInp);
                newRow.appendChild(delBtn);
                container.appendChild(newRow);
            });

            _on('dsd-add-drilldown', 'click', function () {
                var container = document.getElementById('dsd-drilldown-container');
                if (!container) return;
                var newRow = document.createElement('div');
                newRow.className = 'dsd-dd-row';
                var sf = _makeInput('dsd-dd-srcfield', 'sourceField', '');
                sf.style.width = '110px';
                var tf = _makeInput('dsd-dd-targetfilter', 'targetFilterId', '');
                tf.style.width = '110px';
                var tw = _makeInput('dsd-dd-targetwidget', 'targetWidgetId(選)', '');
                tw.style.width = '110px';
                var delBtn = _makeDangerBtn('刪除', 'dsd-remove-dd');
                newRow.appendChild(sf);
                newRow.appendChild(tf);
                newRow.appendChild(tw);
                newRow.appendChild(delBtn);
                container.appendChild(newRow);
            });

            _on('dsd-add-filter', 'click', function () {
                var container = document.getElementById('dsd-filters-container');
                if (!container) return;
                var newRow = document.createElement('div');
                newRow.className = 'dsd-filter-row';
                var fieldInp = _makeInput('dsd-f-field', 'field', '');
                var opSel = _makeSelect('dsd-f-op',
                    FILTER_OPS.map(function (op) { return { value: op, label: op }; }),
                    'eq');
                var valInp = _makeInput('dsd-f-value', 'value', '');
                var delBtn = _makeDangerBtn('刪除', 'dsd-remove-filter');
                newRow.appendChild(fieldInp);
                newRow.appendChild(opSel);
                newRow.appendChild(valInp);
                newRow.appendChild(delBtn);
                container.appendChild(newRow);
            });

            // Delegate delete buttons on dynamic rows
            var containers = ['dsd-thresholds-container', 'dsd-drilldown-container', 'dsd-filters-container'];
            containers.forEach(function (cid) {
                var el = document.getElementById(cid);
                if (!el) return;
                el.addEventListener('click', function (e) {
                    var target = e.target;
                    if (target && (
                        target.classList.contains('dsd-remove-threshold') ||
                        target.classList.contains('dsd-remove-dd') ||
                        target.classList.contains('dsd-remove-filter')
                    )) {
                        var row = target.parentNode;
                        if (row && row.parentNode) row.parentNode.removeChild(row);
                    }
                });
            });

            // ── Live preview ───────────────────────────────────────────────
            _on('dsd-btn-preview', 'click', function () {
                var widgetDef = self._readForm();
                var previewContainer = document.getElementById('dsd-preview-container');
                if (!previewContainer) return;

                if (widgetDef.type === 'static') {
                    // Static: render immediately, no server call
                    if (global.WtmDashboard && global.WtmDashboard.WidgetRendererFactory) {
                        var renderer = global.WtmDashboard.WidgetRendererFactory.getRenderer('static');
                        if (renderer) { renderer(previewContainer, null, widgetDef.config || {}); }
                    }
                    return;
                }

                // For analysis/rest source widgets call the designer preview endpoint
                DesignerApi.previewWidgetInContainer(widgetDef, previewContainer);
            });

            // ── Confirm ────────────────────────────────────────────────────
            _on('dsd-btn-confirm', 'click', function () {
                var widgetDef = self._readForm();
                var gridW = parseInt(_val('dsd-width'), 10) || 4;
                var gridH = parseInt(_val('dsd-height'), 10) || 3;

                if (!widgetDef.type) {
                    layui.layer.msg('請選擇元件類型');
                    return;
                }

                var wid = existingWidgetId || genId();
                if (typeof onConfirm === 'function') {
                    onConfirm(wid, widgetDef, gridW, gridH);
                }
                if (self._layerId != null) {
                    layui.layer.close(self._layerId);
                }
            });

            _on('dsd-btn-cancel', 'click', function () {
                if (self._layerId != null) {
                    layui.layer.close(self._layerId);
                }
            });
        },

        /** Read the current form state into a WidgetDefinition-compatible object. */
        _readForm: function () {
            var type = _val('dsd-type');
            var sourceKind = _val('dsd-source-kind');

            var source = {};
            if (sourceKind === 'analysis') {
                source = {
                    kind: 'analysis',
                    listVmType: _val('dsd-vm-select'),
                    dimensions: _parseDimensions(_val('dsd-dimensions')),
                    measures: _parseMeasures(_val('dsd-measures')),
                    filters: _readFilterRows()
                };
            } else if (sourceKind === 'rest') {
                source = {
                    kind: 'rest',
                    restOptions: { url: _val('dsd-rest-url') }
                };
            }

            var config = {};
            if (type === 'chart') {
                config.chartType = _val('dsd-chart-type') || undefined;
            }
            if (type === 'kpi') {
                config.format = _val('dsd-kpi-format') || undefined;
                config.prefix = _val('dsd-kpi-prefix') || undefined;
            }
            if (type === 'embed') {
                config.url = _val('dsd-embed-url');
            }
            if (type === 'static') {
                config.text = _val('dsd-static-text');
            }
            var datePreset = _val('dsd-date-preset');
            if (datePreset) config.datePreset = datePreset;

            config.thresholds = _readThresholdRows();

            return {
                type: type,
                title: _val('dsd-title'),
                source: source,
                config: config,
                drillDown: _readDrillDownRows()
            };
        },

        close: function () {
            if (this._layerId != null) {
                layui.layer.close(this._layerId);
                this._layerId = null;
            }
        }
    };

    // ─── Row-reader helpers ───────────────────────────────────────────────────
    function _readThresholdRows() {
        var rows = document.querySelectorAll('.dsd-threshold-row');
        var result = [];
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var val = parseFloat(row.querySelector('.dsd-th-value').value);
            if (isNaN(val)) continue;
            result.push({
                value: val,
                color: row.querySelector('.dsd-th-color').value || '#ff0000',
                label: row.querySelector('.dsd-th-label').value || undefined
            });
        }
        return result.length ? result : undefined;
    }

    function _readDrillDownRows() {
        var rows = document.querySelectorAll('.dsd-dd-row');
        var result = [];
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var sf = row.querySelector('.dsd-dd-srcfield').value.trim();
            var tf = row.querySelector('.dsd-dd-targetfilter').value.trim();
            if (!sf || !tf) continue;
            var tw = row.querySelector('.dsd-dd-targetwidget').value.trim();
            result.push({ sourceField: sf, targetFilterId: tf, targetWidgetId: tw || null });
        }
        return result.length ? result : undefined;
    }

    function _readFilterRows() {
        var rows = document.querySelectorAll('.dsd-filter-row');
        var result = [];
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var field = row.querySelector('.dsd-f-field').value.trim();
            if (!field) continue;
            result.push({
                field: field,
                op: row.querySelector('.dsd-f-op').value || 'eq',
                value: row.querySelector('.dsd-f-value').value.trim()
            });
        }
        return result.length ? result : null;
    }

    function _parseDimensions(str) {
        if (!str || !str.trim()) return null;
        return str.split(',').map(function (s) { return { field: s.trim() }; }).filter(function (d) { return !!d.field; });
    }

    function _parseMeasures(str) {
        if (!str || !str.trim()) return null;
        return str.split(',').map(function (s) {
            var parts = s.trim().split(':');
            return { field: parts[0].trim(), func: (parts[1] || 'Sum').trim() };
        }).filter(function (m) { return !!m.field; });
    }

    // ─── DOM helpers ──────────────────────────────────────────────────────────
    function _val(id) {
        var el = document.getElementById(id);
        return el ? el.value : '';
    }
    function _on(id, event, fn) {
        var el = document.getElementById(id);
        if (el) el.addEventListener(event, fn);
    }
    function _display(id, show) {
        var el = document.getElementById(id);
        if (el) el.style.display = show ? '' : 'none';
    }
    /** HTML-attribute-safe escaping — used only in trusted template strings where
     *  DOM construction is not practical (e.g. LayUI layer content string).
     *  All user-visible text uses textContent whenever a real DOM node is available. */
    function _esc(str) {
        return String(str || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    /** Build a labelled text-input DOM node. */
    function _makeInput(cls, placeholder, value) {
        var inp = document.createElement('input');
        inp.type = 'text';
        inp.className = 'layui-input ' + cls;
        inp.style.cssText = 'width:100px;display:inline-block;';
        inp.placeholder = placeholder;
        inp.value = value || '';
        return inp;
    }

    /** Build a <select> DOM node populated from an array of {value, label} pairs. */
    function _makeSelect(cls, items, selected) {
        var sel = document.createElement('select');
        sel.className = cls;
        sel.style.cssText = 'width:90px;display:inline-block;';
        items.forEach(function (item) {
            var opt = document.createElement('option');
            opt.value = item.value;
            opt.textContent = item.label;
            if (item.value === selected) opt.selected = true;
            sel.appendChild(opt);
        });
        return sel;
    }

    /** Build a small danger button. */
    function _makeDangerBtn(label, cls) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'layui-btn layui-btn-xs layui-btn-danger ' + (cls || '');
        btn.textContent = label;
        return btn;
    }

    // ─── DesignerApi (public surface) ─────────────────────────────────────────
    var _dashboardId = null;     // id being edited (null = new)
    var _containerId = null;     // GridStack container id
    var _previewBaseUrl = '/_dashboard-designer/preview';

    var DesignerApi = {
        /**
         * Initialise the designer.
         * @param {string} gridContainerId  DOM id of the GridStack container
         * @param {string|null} dashboardId  Existing dashboard id to load, or null for new
         */
        init: function (gridContainerId, dashboardId) {
            _containerId = gridContainerId;
            _dashboardId = dashboardId || null;

            // Load data sources first so the panel has them ready
            return DesignerApi.loadDataSources().then(function (sources) {
                DesignerPanel.setAvailableSources(sources);

                if (!_dashboardId) {
                    // New dashboard — start with an empty model
                    DesignerModel.load(null);
                    _initGrid({});
                    return null;
                }

                // Load existing dashboard
                return global.fetch('/_dashboard/' + encodeURIComponent(_dashboardId))
                    .then(function (r) {
                        if (!r.ok) throw new Error('Failed to load dashboard');
                        return r.json();
                    })
                    .then(function (def) {
                        DesignerModel.load(def);
                        _initGrid(def);
                        return def;
                    });
            });
        },

        /** Load available Analysis VMs + REST data sources from the server. */
        loadDataSources: function () {
            return global.fetch('/_dashboard/datasources')
                .then(function (r) { return r.ok ? r.json() : []; })
                .catch(function () { return []; });
        },

        /** Open the add-widget panel. */
        addWidget: function () {
            DesignerPanel.openAdd(function (widgetId, widgetDef, gridW, gridH) {
                var id = DesignerModel.upsertWidget(widgetId, widgetDef);
                var grid = global.WtmDashboard && global.WtmDashboard.GridManager && global.WtmDashboard.GridManager.getGrid();
                if (grid) {
                    // Pass the DOM node directly so event listeners (edit/delete
                    // buttons) are preserved — no innerHTML needed.
                    var cellNode = _makeWidgetNode(id, widgetDef);
                    grid.addWidget({ id: id, w: gridW, h: gridH, el: cellNode });
                }
            });
        },

        /** Open the edit-widget panel for an existing widget. */
        editWidget: function (widgetId) {
            var def = DesignerModel.getWidget(widgetId);
            if (!def) return;
            DesignerPanel.openEdit(widgetId, def, function (wid, widgetDef) {
                DesignerModel.upsertWidget(wid, widgetDef);
                // Update title in the GridStack cell header if present
                var titleEl = document.querySelector('#' + wid + '-header .dsd-widget-title');
                if (titleEl) titleEl.textContent = widgetDef.title || '';
            });
        },

        /** Remove a widget from the model and the grid. */
        removeWidget: function (widgetId) {
            DesignerModel.removeWidget(widgetId);
            if (global.WtmDashboard && global.WtmDashboard.GridManager) {
                var grid = global.WtmDashboard.GridManager.getGrid();
                if (grid) {
                    var nodeEl = document.getElementById(widgetId);
                    if (nodeEl) {
                        var gridItem = nodeEl.closest('.grid-stack-item');
                        if (gridItem) grid.removeWidget(gridItem);
                    }
                }
            }
        },

        /**
         * Preview a widget definition by calling the server preview endpoint.
         * Renders the result using the existing framework_dashboard.js renderers.
         * @param {object} widgetDef  WidgetDefinition-compatible object
         * @param {HTMLElement} container  DOM element to render into
         */
        previewWidgetInContainer: function (widgetDef, container) {
            if (!container) return Promise.resolve();
            container.textContent = '載入預覽…';

            return global.fetch(_previewBaseUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(widgetDef)
            })
            .then(function (r) {
                if (!r.ok) throw new Error('Preview failed: ' + r.status);
                return r.json();
            })
            .then(function (data) {
                container.textContent = '';
                if (!global.WtmDashboard) return;
                var renderer = global.WtmDashboard.WidgetRendererFactory.getRenderer(widgetDef.type);
                if (renderer) {
                    renderer(container, data, widgetDef.config || {});
                } else {
                    container.textContent = '(無法預覽此元件類型)';
                }
            })
            .catch(function (e) {
                container.textContent = '預覽失敗。';
                if (console && console.warn) console.warn('Preview error', e);
            });
        },

        /**
         * Save the current designer state to the server.
         * Creates a new dashboard (POST) or updates existing one (PUT).
         */
        save: function (dashboardMeta) {
            // Sync layout from GridStack
            if (global.WtmDashboard && global.WtmDashboard.GridManager) {
                var layout = global.WtmDashboard.GridManager.saveLayout();
                DesignerModel.updateLayout(layout);
            }
            if (dashboardMeta) {
                if (dashboardMeta.title != null) DesignerModel.setTitle(dashboardMeta.title);
                if (dashboardMeta.refreshInterval != null) DesignerModel.setRefreshInterval(dashboardMeta.refreshInterval);
                if (dashboardMeta.sharing) DesignerModel.setSharing(dashboardMeta.sharing.mode, dashboardMeta.sharing.roles);
            }

            var def = DesignerModel.snapshot();

            if (!_dashboardId) {
                // Create
                return global.fetch('/_dashboard/', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(def)
                })
                .then(function (r) {
                    if (!r.ok) return r.text().then(function (t) { throw new Error(t); });
                    return r.json();
                })
                .then(function (newId) {
                    _dashboardId = newId;
                    DesignerModel.getDef().id = newId;
                    return newId;
                });
            } else {
                // Update
                def.id = _dashboardId;
                return global.fetch('/_dashboard/' + encodeURIComponent(_dashboardId), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(def)
                })
                .then(function (r) {
                    if (!r.ok) return r.text().then(function (t) { throw new Error(t); });
                    return _dashboardId;
                });
            }
        },

        /** Read-only access to the current draft (for tests / host page). */
        getModel: function () { return DesignerModel; },

        /** Expose constants for tests */
        _constants: {
            CHART_TYPES: CHART_TYPES,
            WIDGET_TYPES: WIDGET_TYPES,
            DATE_PRESETS: DATE_PRESETS,
            FILTER_OPS: FILTER_OPS
        }
    };

    // ─── GridStack initialisation helper ─────────────────────────────────────
    function _initGrid(def) {
        if (!global.WtmDashboard || !global.WtmDashboard.GridManager) return;
        if (!global.GridStack) return;

        var grid = global.WtmDashboard.GridManager.init('#' + _containerId, {
            cellHeight: 80,
            margin: 8,
            column: 12
        });
        global.WtmDashboard.GridManager.setEditMode(true);

        if (def && def.layout && def.widgets) {
            // Load existing widgets onto the grid using DOM nodes (no innerHTML).
            for (var i = 0; i < def.layout.length; i++) {
                var li = def.layout[i];
                var wDef = def.widgets && def.widgets[li.id];
                if (!wDef) continue;
                if (grid) {
                    grid.addWidget({
                        id: li.id,
                        x: li.x, y: li.y,
                        w: li.w, h: li.h,
                        el: _makeWidgetNode(li.id, wDef)
                    });
                }
            }
        }
    }

    /**
     * Build the inner DOM node for a GridStack widget cell.
     * Uses DOM methods + addEventListener — no inline onclick or innerHTML.
     * Returns an HTMLElement whose outerHTML GridStack can use as content,
     * OR the element itself when GridStack accepts DOM nodes.
     *
     * Security: widgetId comes from genId() (timestamp+random) and is never
     * user-supplied text. Even so, we use textContent / setAttribute throughout
     * rather than string concatenation.
     */
    function _makeWidgetNode(widgetId, widgetDef) {
        var wrapper = document.createElement('div');
        wrapper.className = 'dsd-widget-cell';

        // Header bar
        var header = document.createElement('div');
        header.id = widgetId + '-header';
        header.className = 'dsd-widget-header';

        var titleSpan = document.createElement('span');
        titleSpan.className = 'dsd-widget-title';
        titleSpan.textContent = (widgetDef && widgetDef.title) || widgetId;

        var typeBadge = document.createElement('span');
        typeBadge.className = 'dsd-widget-type-badge';
        typeBadge.textContent = (widgetDef && widgetDef.type) || '';

        var editBtn = document.createElement('button');
        editBtn.type = 'button';
        editBtn.className = 'layui-btn layui-btn-xs';
        editBtn.textContent = '編輯';
        // Capture widgetId in closure — no inline handler
        (function (wid) {
            editBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                DesignerApi.editWidget(wid);
            });
        }(widgetId));

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'layui-btn layui-btn-xs layui-btn-danger';
        removeBtn.textContent = '刪除';
        (function (wid) {
            removeBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                DesignerApi.removeWidget(wid);
            });
        }(widgetId));

        header.appendChild(titleSpan);
        header.appendChild(typeBadge);
        header.appendChild(editBtn);
        header.appendChild(removeBtn);

        // Preview area (reuses runtime renderers after a save/preview)
        var previewArea = document.createElement('div');
        previewArea.id = widgetId;
        previewArea.className = 'dsd-widget-preview-area';
        previewArea.style.cssText = 'height:calc(100% - 36px);overflow:hidden;';

        wrapper.appendChild(header);
        wrapper.appendChild(previewArea);
        return wrapper;
    }

    /**
     * Returns an HTML string representation of the widget cell.
     * Kept as a thin wrapper so callers that need a string (e.g. GridStack
     * content property when it doesn't accept DOM nodes) still work.
     * All values go through textContent — the outerHTML is built by the browser
     * DOM serialiser, not by string concatenation, so attribute-injection is
     * impossible regardless of what widgetId/title contain.
     */
    function _makeWidgetHtml(widgetId, widgetDef) {
        // Build via DOM to avoid any string-concatenation XSS risk,
        // then serialise with outerHTML (browser-escaped).
        var node = _makeWidgetNode(widgetId, widgetDef);
        // Event listeners won't survive outerHTML serialisation, but the
        // Designer re-attaches them via delegated event handling on the grid.
        return node.outerHTML;
    }

    // ─── Public export ────────────────────────────────────────────────────────
    var exportApi = {
        DesignerModel: DesignerModel,
        DesignerPanel: DesignerPanel,
        DesignerApi: DesignerApi,
        // Helpers exposed for tests
        _helpers: {
            genId: genId,
            _parseDimensions: _parseDimensions,
            _parseMeasures: _parseMeasures,
            _readThresholdRows: _readThresholdRows,
            _readDrillDownRows: _readDrillDownRows,
            _readFilterRows: _readFilterRows,
            _esc: _esc,
            _makeWidgetNode: _makeWidgetNode,
            _makeInput: _makeInput,
            _makeSelect: _makeSelect,
            _makeDangerBtn: _makeDangerBtn,
            CHART_TYPES: CHART_TYPES,
            WIDGET_TYPES: WIDGET_TYPES,
            DATE_PRESETS: DATE_PRESETS,
            FILTER_OPS: FILTER_OPS
        }
    };

    if (typeof global.window !== 'undefined') {
        global.window.WtmDesigner = DesignerApi;
        global.window.WtmDashboardDesigner = exportApi;
    }
    global.WtmDesigner = DesignerApi;
    global.WtmDashboardDesigner = exportApi;

}(typeof window !== 'undefined' ? window : (typeof global !== 'undefined' ? global : this)));
