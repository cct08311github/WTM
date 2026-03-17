/**
 * WalkingTec.Mvvm Dashboard Module
 */
(function (global) {
    "use strict";

    // --- EventBus -----------------------------------------------------------
    var _handlers = {};

    var EventBus = {
        /**
         * 註冊事件監聽
         * @param {string} event 事件名稱
         * @param {string} widgetId 註冊的 widget ID (用來支援 widget 級別的 off)
         * @param {function} handler 處理函式
         */
        on: function (event, widgetId, handler) {
            var key = event + ':' + widgetId;
            if (!_handlers[event]) {
                _handlers[event] = [];
            }
            _handlers[event].push({
                key: key,
                handler: handler
            });
        },

        /**
         * 移除特定 widget 的事件監聽
         * @param {string} event 事件名稱
         * @param {string} widgetId 註冊的 widget ID
         */
        off: function (event, widgetId) {
            if (!_handlers[event]) return;
            var key = event + ':' + widgetId;
            var newHandlers = [];
            for (var i = 0; i < _handlers[event].length; i++) {
                if (_handlers[event][i].key !== key) {
                    newHandlers.push(_handlers[event][i]);
                }
            }
            _handlers[event] = newHandlers;
        },

        /**
         * 觸發事件
         * @param {string} event 事件名稱
         * @param {any} data 傳遞的資料
         */
        emit: function (event, data) {
            if (!_handlers[event]) return;
            for (var i = 0; i < _handlers[event].length; i++) {
                try {
                    _handlers[event][i].handler(data);
                } catch (e) {
                    if (console && console.error) {
                        console.error('EventBus error in handler for ' + event, e);
                    }
                }
            }
        }
    };

    // --- Responsive Breakpoints ---------------------------------------------
    /**
     * Breakpoint definitions (matches CSS media queries in framework_dashboard.css).
     * Keys: 'lg' | 'md' | 'sm' | 'xs'
     */
    var BREAKPOINTS = {
        lg: 1200,
        md: 992,
        sm: 768,
        xs: 0
    };

    /**
     * Detect the current viewport breakpoint key.
     * @returns {'lg'|'md'|'sm'|'xs'}
     */
    function detectBreakpoint() {
        var w = (global.innerWidth != null) ? global.innerWidth : 9999;
        if (w >= BREAKPOINTS.lg) return 'lg';
        if (w >= BREAKPOINTS.md) return 'md';
        if (w >= BREAKPOINTS.sm) return 'sm';
        return 'xs';
    }

    /**
     * Given a LayoutItem array and a target breakpoint, return a transformed
     * array where each item's x/y/w/h are resolved for that breakpoint.
     * For 'xs', items without an explicit xs breakpoint default to w=12, x=0
     * (single-column stacking).
     * @param {Array} layout  Original LayoutItem array
     * @param {string} bp     Target breakpoint key
     * @returns {Array}
     */
    function applyBreakpointToLayout(layout, bp) {
        if (!layout || !layout.length) return layout || [];
        var result = [];
        var yOffset = 0;  // accumulated y for xs auto-stacking
        for (var idx = 0; idx < layout.length; idx++) {
            var item = layout[idx];
            var bpData = item.breakpoints && item.breakpoints[bp];
            if (bpData) {
                result.push({
                    id: item.id,
                    x: bpData.x,
                    y: bpData.y,
                    w: bpData.w,
                    h: bpData.h
                });
                // For xs explicit breakpoints, don't advance yOffset (layout is fully user-controlled)
                continue;
            }
            // xs without explicit override → single-column stacking
            if (bp === 'xs') {
                var h = item.h || 1;
                result.push({
                    id: item.id,
                    x: 0,
                    y: yOffset,   // accumulate y to avoid overlap
                    w: 12,
                    h: h
                });
                yOffset += h;
                continue;
            }
            // other breakpoints without override → use base values
            result.push({
                id: item.id,
                x: item.x,
                y: item.y,
                w: item.w,
                h: item.h
            });
        }
        return result;
    }

    // --- GridManager --------------------------------------------------------
    var _grid = null;
    var _isEditMode = false;
    /** Current viewport preview mode: null means "auto-detect from window" */
    var _viewportPreview = null;

    var GridManager = {
        /**
         * 初始化 GridStack
         * @param {string} selector 容器選擇器
         * @param {object} options GridStack 選項
         */
        init: function (selector, options) {
            options = options || {};
            // 預設為不可編輯
            options.staticGrid = !_isEditMode;
            if (global.GridStack) {
                _grid = global.GridStack.init(options, selector);
            }
            return _grid;
        },

        /**
         * 取得當前網格物件
         */
        getGrid: function () {
            return _grid;
        },

        /**
         * 儲存佈局
         * @returns {Array} GridStack 佈局陣列
         */
        saveLayout: function () {
            if (!_grid) return [];
            return _grid.save();
        },

        /**
         * 載入佈局
         * @param {Array} layout GridStack 佈局陣列
         */
        loadLayout: function (layout) {
            if (!_grid) return;
            _grid.load(layout);
        },

        /**
         * 載入佈局並套用指定斷點覆蓋
         * @param {Array} layout  原始 LayoutItem 陣列（含 breakpoints 欄位）
         * @param {string} bp     目標斷點 ('lg'|'md'|'sm'|'xs'|null)
         *                        null = 根據目前視窗寬度自動判斷
         */
        loadLayoutForBreakpoint: function (layout, bp) {
            if (!_grid) return;
            var targetBp = bp || detectBreakpoint();
            var resolved = applyBreakpointToLayout(layout, targetBp);
            _grid.load(resolved);
        },

        /**
         * 取得目前生效的斷點（考慮預覽模式）
         * @returns {'lg'|'md'|'sm'|'xs'}
         */
        getCurrentBreakpoint: function () {
            return _viewportPreview || detectBreakpoint();
        },

        /**
         * 設定視窗預覽模式（編輯器用）
         * @param {string|null} mode 'lg'|'md'|'sm'|'xs'|null (null = 自動)
         */
        setViewportMode: function (mode) {
            var valid = ['lg', 'md', 'sm', 'xs', null];
            if (valid.indexOf(mode) === -1) return;
            _viewportPreview = mode;
        },

        /**
         * 取得目前的視窗預覽模式
         * @returns {string|null}
         */
        getViewportMode: function () {
            return _viewportPreview;
        },

        /**
         * 檢查是否為編輯模式
         * @returns {boolean}
         */
        isEditMode: function () {
            return _isEditMode;
        },

        /**
         * 設定編輯模式
         * @param {boolean} mode 是否為編輯模式
         */
        setEditMode: function (mode) {
            _isEditMode = !!mode;
            if (_grid) {
                if (_isEditMode) {
                    _grid.enable();
                } else {
                    _grid.disable();
                }
            }
        }
    };

    // --- Utils --------------------------------------------------------------
    var Utils = {
        formatValue: function(value, format, prefix) {
            if (value == null || isNaN(value)) return value;
            var num = Number(value);
            
            if (format === 'percent') {
                return (num * 100).toFixed(1).replace(/\.0$/, '') + '%';
            }
            
            var formatted = num.toLocaleString();
            if (format === 'currency') {
                // simple abbreviation logic
                if (Math.abs(num) >= 1000000) {
                    formatted = (num / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
                } else if (Math.abs(num) >= 1000) {
                    formatted = (num / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
                }
                return (prefix || '') + formatted;
            }
            
            return formatted;
        },
        calculateTrend: function(current, previous) {
            if (!previous) return 0;
            return ((current - previous) / previous) * 100;
        },
        animateNumber: function(element, from, to, duration) {
            if (!global.requestAnimationFrame) {
                element.textContent = String(to);
                return;
            }
            var start = null;
            var diff = to - from;
            duration = duration || 500;
            function step(ts) {
                if (!start) start = ts;
                var progress = Math.min((ts - start) / duration, 1);
                var current = from + diff * progress;
                element.textContent = String(Math.round(current));
                if (progress < 1) {
                    global.requestAnimationFrame(step);
                }
            }
            global.requestAnimationFrame(step);
        }
    };

    // --- Widget Renderers ---------------------------------------------------
    function renderKpi(container, data, config) {
        container.innerHTML = ''; // reset
        
        var titleDiv = document.createElement('div');
        titleDiv.className = 'wtm-kpi-title';
        titleDiv.textContent = config.title || '';
        
        var valueDiv = document.createElement('div');
        valueDiv.className = 'wtm-kpi-value';
        var val = data ? data.value : 0;
        valueDiv.textContent = Utils.formatValue(val, config.format, config.prefix);

        // ── Threshold / alert coloring ─────────────────────────────────────
        // config.thresholds: [{ value: Number, color: String, label?: String }]
        // Supports ascending (warn/danger) and descending (below-safe) modes.
        // config.thresholdDirection: 'above' (default) | 'below'
        var thresholds = Array.isArray(config.thresholds) ? config.thresholds : [];
        if (thresholds.length > 0) {
            var direction = config.thresholdDirection === 'below' ? 'below' : 'above';
            // Sort descending by value so we match the highest triggered level first
            var sorted = thresholds.slice().sort(function (a, b) {
                return direction === 'above' ? b.value - a.value : a.value - b.value;
            });
            var matched = null;
            for (var t = 0; t < sorted.length; t++) {
                var th = sorted[t];
                if ((direction === 'above' && val >= th.value) ||
                    (direction === 'below' && val <= th.value)) {
                    matched = th;
                    break;
                }
            }
            if (matched) {
                valueDiv.style.color = matched.color;
                if (matched.label) {
                    var alertSpan = document.createElement('span');
                    alertSpan.className = 'wtm-kpi-alert-label';
                    alertSpan.textContent = ' ' + matched.label;
                    alertSpan.style.color = matched.color;
                    alertSpan.style.fontSize = '0.7em';
                    valueDiv.appendChild(alertSpan);
                }
            }
        }
        
        container.appendChild(titleDiv);
        container.appendChild(valueDiv);
        
        if (data && typeof data.previousValue !== 'undefined') {
            var trend = Utils.calculateTrend(val, data.previousValue);
            var trendDiv = document.createElement('div');
            trendDiv.className = 'wtm-kpi-trend ' + (trend >= 0 ? 'up' : 'down');
            var arrow = trend >= 0 ? '▲' : '▼';
            trendDiv.textContent = arrow + ' ' + Math.abs(trend).toFixed(2) + '%';
            container.appendChild(trendDiv);
        }
    }

    /**
     * Infers the most appropriate ECharts series type when config.chartType is absent.
     *
     * Rules (applied in order):
     *  1. Time dimension (column name contains year/month/quarter/week/day/date keywords
     *     or follows yyyy-MM / yyyyMM / yyyyQn patterns) → 'line'
     *  2. Single dimension + single measure + ≤ 8 distinct category values → 'pie'
     *  3. Everything else → 'bar'
     */
    function inferChartType(config, data) {
        if (config && config.chartType) return config.chartType;
        if (!data || !data.columns || !data.rows) return 'bar';

        var dimField = data.columns[0];
        var rowCount = data.rows.length;

        // Rule 1: time-like dimension name
        var timeKeywords = /year|month|quarter|week|day|date|年|月|季|週|日/i;
        var timePattern = /^\d{4}[-/]?\d{0,2}$|^Q[1-4]$/i;
        if (dimField && timeKeywords.test(dimField)) return 'line';
        if (rowCount > 0) {
            var sample = String(data.rows[0][dimField] || '');
            if (timePattern.test(sample.trim())) return 'line';
        }

        // Rule 2: few categories → pie
        if (data.columns.length === 2 && rowCount > 0 && rowCount <= 8) return 'pie';

        return 'bar';
    }

    function renderChart(container, data, config) {
        if (!global.echarts) return;
        var chart = global.echarts.init(container);

        // Simple fallback
        if (!data || !data.columns || !data.rows) return;

        var xAxisData = [];
        var seriesData = [];
        var dimField = data.columns[0];
        var msrField = data.columns[1];

        for (var i = 0; i < data.rows.length; i++) {
            xAxisData.push(data.rows[i][dimField]);
            seriesData.push(data.rows[i][msrField]);
        }

        var resolvedType = inferChartType(config, data);

        var option = {
            xAxis: {
                type: 'category',
                data: xAxisData
            },
            yAxis: {
                type: 'value'
            },
            series: [{
                data: seriesData,
                type: resolvedType
            }]
        };

        chart.setOption(option);
    }

    function renderTable(container, data, config) {
        container.innerHTML = '';
        if (!data || !data.columns || !data.rows) return;

        var table = document.createElement('table');
        table.className = 'wtm-widget-table';

        // header row
        var headerRow = document.createElement('tr');
        for (var c = 0; c < data.columns.length; c++) {
            var th = document.createElement('th');
            th.textContent = data.columns[c];
            headerRow.appendChild(th);
        }
        table.appendChild(headerRow);

        // data rows
        for (var i = 0; i < data.rows.length; i++) {
            var tr = document.createElement('tr');
            for (var j = 0; j < data.columns.length; j++) {
                var td = document.createElement('td');
                var val = data.rows[i][data.columns[j]];
                td.textContent = val != null ? String(val) : '';
                tr.appendChild(td);
            }
            table.appendChild(tr);
        }

        container.appendChild(table);
    }

    function renderProgress(container, data, config) {
        container.innerHTML = '';
        var value = (data && data.value != null) ? Number(data.value) : 0;
        var pct = Math.max(0, Math.min(1, value));

        if (config.title) {
            var titleDiv = document.createElement('div');
            titleDiv.className = 'wtm-progress-title';
            titleDiv.textContent = config.title;
            container.appendChild(titleDiv);
        }

        var track = document.createElement('div');
        track.className = 'wtm-progress-track';

        var bar = document.createElement('div');
        bar.className = 'wtm-progress-bar';
        bar.style.width = (pct * 100) + '%';
        if (config.color) {
            bar.style.backgroundColor = config.color;
        }
        track.appendChild(bar);
        container.appendChild(track);

        var label = document.createElement('div');
        label.className = 'wtm-progress-label';
        label.textContent = Math.round(pct * 100) + '%';
        container.appendChild(label);
    }

    function renderList(container, data, config) {
        container.innerHTML = '';

        if (config.title) {
            var titleDiv = document.createElement('div');
            titleDiv.className = 'wtm-list-title';
            titleDiv.textContent = config.title;
            container.appendChild(titleDiv);
        }

        if (!data || !data.items) return;

        var ul = document.createElement('ul');
        ul.className = 'wtm-list';

        for (var i = 0; i < data.items.length; i++) {
            var item = data.items[i];
            var li = document.createElement('li');
            li.className = 'wtm-list-item';
            // Security: all dynamic text via textContent only
            var text = item.label || '';
            if (item.description) {
                text += ' \u2014 ' + item.description;
            }
            li.textContent = text;

            if (item.url && config.clickAction === 'navigate') {
                li.dataset = li.dataset || {};
                li.dataset.href = item.url;
            }
            ul.appendChild(li);
        }

        container.appendChild(ul);
    }

    function renderEmbed(container, data, config) {
        container.innerHTML = '';
        var url = config.url || '';

        // Security: block dangerous URL schemes
        var lower = url.toLowerCase().replace(/\s/g, '');
        if (lower.indexOf('javascript:') === 0 || lower.indexOf('data:') === 0 || lower.indexOf('vbscript:') === 0) {
            container.textContent = 'Blocked: invalid URL scheme';
            return;
        }

        var iframe = document.createElement('iframe');
        iframe.className = 'wtm-embed-iframe';
        iframe.src = url;
        iframe.sandbox = 'allow-scripts'; // no allow-same-origin
        iframe.style.width = '100%';
        iframe.style.height = '100%';
        iframe.style.border = 'none';
        container.appendChild(iframe);
    }

    var _renderers = {
        kpi: renderKpi,
        chart: renderChart,
        table: renderTable,
        progress: renderProgress,
        list: renderList,
        embed: renderEmbed
    };

    var WidgetRendererFactory = {
        getRenderer: function(type) { 
            return _renderers[type] || null; 
        }
    };

    // --- DashboardManager ---------------------------------------------------
    var _currentDashboard = null;
    var _refreshTimer = null;
    var _containerId = null;
    var _failureCounts = {};
    var MAX_RETRIES = 3;

    var DashboardManager = {
        init: function(containerId, dashboardId) {
            _containerId = containerId;
            return this._loadDashboard(dashboardId).then(function(def) {
                _currentDashboard = def;
                GridManager.init('#' + containerId, {});
                if (def.layout) {
                    GridManager.loadLayout(def.layout);
                }
                
                DashboardManager._renderAllWidgets(def);
                
                if (def.links) {
                    DashboardManager._registerLinks(def.links);
                }
                
                if (def.refreshInterval && def.refreshInterval > 0) {
                    DashboardManager.startRefresh(def.refreshInterval);
                }
                return def;
            }).catch(function(e) {
                if (console && console.error) console.error('Dashboard init failed', e);
            });
        },
        
        _loadDashboard: function(id) {
            return global.fetch('/_dashboard/' + id)
                .then(function(res) {
                    if (!res.ok) throw new Error('Network response was not ok');
                    return res.json();
                });
        },
        
        _renderAllWidgets: function(def) {
            if (!def.widgets) return;
            for (var widgetId in def.widgets) {
                if (Object.prototype.hasOwnProperty.call(def.widgets, widgetId)) {
                    this._fetchAndRenderWidget(widgetId, def.widgets[widgetId]);
                }
            }
        },
        
        _fetchAndRenderWidget: function(widgetId, widgetDef) {
            var container = document.getElementById(widgetId);
            if (!container) return;

            // Skip if max retries exceeded
            if ((_failureCounts[widgetId] || 0) >= MAX_RETRIES) return;

            // Show loading state
            container.className = 'wtm-widget-loading';
            container.textContent = 'Loading...';

            var url = '/_dashboard/' + _currentDashboard.id + '/widget/' + widgetId + '/data';

            global.fetch(url)
                .then(function(res) {
                    if (!res.ok) throw new Error('Widget data fetch failed');
                    return res.json();
                })
                .then(function(data) {
                    container.className = '';
                    _failureCounts[widgetId] = 0;
                    var renderer = WidgetRendererFactory.getRenderer(widgetDef.type);
                    if (renderer) {
                        renderer(container, data, widgetDef.config || {});
                    } else {
                        container.textContent = 'Unknown widget type: ' + widgetDef.type;
                    }
                })
                .catch(function(e) {
                    _failureCounts[widgetId] = (_failureCounts[widgetId] || 0) + 1;
                    container.className = 'wtm-widget-error';
                    container.textContent = 'Error loading widget data.';
                    if (console && console.error) console.error(e);
                });
        },
        
        _registerLinks: function(links) {
            for (var i = 0; i < links.length; i++) {
                var link = links[i];
                EventBus.on(link.event, link.sourceWidget, function(payload) {
                    // Logic to handle link action e.g., filter target widget
                    // This will be expanded later
                });
            }
        },
        
        startRefresh: function(intervalSec) {
            this.stopRefresh();
            _refreshTimer = setInterval(function() {
                if (_currentDashboard) {
                    DashboardManager._renderAllWidgets(_currentDashboard);
                }
            }, intervalSec * 1000);
        },
        
        stopRefresh: function() {
            if (_refreshTimer) {
                clearInterval(_refreshTimer);
                _refreshTimer = null;
            }
        },

        getFailureCounts: function() {
            return _failureCounts;
        },

        shouldRetry: function(widgetId) {
            return (_failureCounts[widgetId] || 0) < MAX_RETRIES;
        },

        _setFailureCount: function(widgetId, count) {
            _failureCounts[widgetId] = count;
        }
    };

    // --- DashboardEditor ----------------------------------------------------
    var DashboardEditor = {
        toggleEditMode: function() {
            var newMode = !GridManager.isEditMode();
            GridManager.setEditMode(newMode);
            if (newMode) {
                DashboardManager.stopRefresh();
            } else if (_currentDashboard && _currentDashboard.refreshInterval > 0) {
                DashboardManager.startRefresh(_currentDashboard.refreshInterval);
            }
        },

        /**
         * 在編輯模式下預覽指定視窗尺寸的佈局。
         * 會更新容器的 data-viewport 屬性（供 CSS 切換框線樣式），
         * 並以斷點解析後的 layout 重新載入 GridStack。
         * @param {string|null} mode 'lg'|'md'|'sm'|'xs'|null (null = 恢復自動)
         */
        previewViewport: function(mode) {
            GridManager.setViewportMode(mode);
            if (_containerId) {
                var container = (global.document && global.document.getElementById)
                    ? global.document.getElementById(_containerId)
                    : null;
                if (container) {
                    if (mode) {
                        container.setAttribute('data-viewport', mode);
                    } else {
                        container.removeAttribute('data-viewport');
                    }
                }
            }
            if (_currentDashboard && _currentDashboard.layout) {
                GridManager.loadLayoutForBreakpoint(_currentDashboard.layout, mode);
            }
        },

        saveDashboard: function() {
            if (!_currentDashboard) return Promise.resolve();
            var layout = GridManager.saveLayout();
            var body = {
                id: _currentDashboard.id,
                title: _currentDashboard.title,
                refreshInterval: _currentDashboard.refreshInterval || 60,
                sharing: _currentDashboard.sharing || { mode: 'private' },
                filters: _currentDashboard.filters || [],
                links: _currentDashboard.links || [],
                layout: layout,
                widgets: _currentDashboard.widgets || {}
            };
            return global.fetch('/_dashboard/' + _currentDashboard.id, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            }).then(function(res) {
                if (!res.ok) throw new Error('Save failed');
                return res.json();
            });
        },

        deleteDashboard: function() {
            if (!_currentDashboard) return Promise.resolve();
            return global.fetch('/_dashboard/' + _currentDashboard.id, {
                method: 'DELETE'
            }).then(function(res) {
                if (!res.ok) throw new Error('Delete failed');
                return res.json();
            });
        },

        addWidget: function(widgetId, widgetDef, gridOpts) {
            if (!_currentDashboard) return;
            if (!_currentDashboard.widgets) _currentDashboard.widgets = {};
            _currentDashboard.widgets[widgetId] = widgetDef;
            if (_grid) {
                _grid.addWidget(gridOpts || { id: widgetId, w: 4, h: 3 });
            }
        },

        removeWidget: function(widgetId) {
            if (!_currentDashboard || !_currentDashboard.widgets) return;
            delete _currentDashboard.widgets[widgetId];
            if (_currentDashboard.layout) {
                _currentDashboard.layout = _currentDashboard.layout.filter(function(item) {
                    return item.id !== widgetId;
                });
            }
        }
    };

    // --- FilterBar ----------------------------------------------------------
    var _filterValues = {};
    var _filterChangeCallbacks = [];
    var _filterDefs = [];

    var FilterBar = {
        init: function(filters, container) {
            _filterDefs = filters || [];
            _filterValues = {};
            _filterChangeCallbacks = [];

            for (var i = 0; i < _filterDefs.length; i++) {
                var f = _filterDefs[i];
                _filterValues[f.field] = f.defaultValue || '';

                var wrapper = document.createElement('div');
                wrapper.className = 'wtm-filter-item';

                var label = document.createElement('label');
                label.textContent = f.field;
                wrapper.appendChild(label);

                if (f.type === 'select' && f.options) {
                    var sel = document.createElement('select');
                    sel.name = f.field;
                    for (var j = 0; j < f.options.length; j++) {
                        var opt = document.createElement('option');
                        opt.value = f.options[j];
                        opt.textContent = f.options[j];
                        sel.appendChild(opt);
                    }
                    if (f.defaultValue) sel.value = f.defaultValue;
                    (function(fieldName) {
                        sel.addEventListener('change', function() {
                            FilterBar.setValue(fieldName, sel.value);
                        });
                    })(f.field);
                    wrapper.appendChild(sel);
                } else {
                    var input = document.createElement('input');
                    input.type = 'text';
                    input.name = f.field;
                    if (f.defaultValue) input.value = f.defaultValue;
                    (function(fieldName) {
                        input.addEventListener('input', function() {
                            FilterBar.setValue(fieldName, input.value);
                        });
                    })(f.field);
                    wrapper.appendChild(input);
                }

                container.appendChild(wrapper);
            }
        },

        getValues: function() {
            var copy = {};
            for (var k in _filterValues) {
                if (Object.prototype.hasOwnProperty.call(_filterValues, k)) {
                    copy[k] = _filterValues[k];
                }
            }
            return copy;
        },

        setValue: function(field, value) {
            _filterValues[field] = value;
            var vals = FilterBar.getValues();
            for (var i = 0; i < _filterChangeCallbacks.length; i++) {
                try {
                    _filterChangeCallbacks[i](vals);
                } catch (e) {
                    if (console && console.error) console.error('FilterBar onChange error', e);
                }
            }
        },

        onChange: function(callback) {
            if (typeof callback === 'function') {
                _filterChangeCallbacks.push(callback);
            }
        }
    };

    // --- API 導出 ------------------------------------------------------------
    var api = {
        EventBus: EventBus,
        GridManager: GridManager,
        Utils: Utils,
        WidgetRendererFactory: WidgetRendererFactory,
        DashboardManager: DashboardManager,
        DashboardEditor: DashboardEditor,
        FilterBar: FilterBar,
        // Responsive helpers (also exposed for testing)
        _internal: {
            detectBreakpoint: detectBreakpoint,
            applyBreakpointToLayout: applyBreakpointToLayout,
            BREAKPOINTS: BREAKPOINTS,
            inferChartType: inferChartType
        }
    };

    if (typeof global.window !== "undefined") {
        global.window.WtmDashboard = api;
    }
    
    // 將結果賦值給全域物件（為了解決某些環境下 global 未指向 window 的問題）
    global.WtmDashboard = api;

    return api;
})(typeof window !== "undefined" ? window : this);