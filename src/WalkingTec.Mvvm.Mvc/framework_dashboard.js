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
        var val = (data && data.value != null) ? data.value : 0;
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

    // --- L6: ECharts option builders for extended chart types ----------------
    /**
     * Build an ECharts option object for the given resolved chart type.
     *
     * Supported types: bar, line, pie, piehollow, gauge, funnel, radar,
     *                  heatmap, scatter, sankey.
     *
     * Q2 multi-series: when allSeries has more than one element and the
     * resolved type is bar or line, each series beyond the first is rendered
     * with `secondaryType` (default: 'line') on a secondary Y axis (right).
     * Single-column results render exactly as before.
     *
     * @param {string} resolvedType  Lower-case chart type string
     * @param {Array}  xAxisData    Category labels from the dimension column
     * @param {Array}  allSeries    [{name, data}] — one element per measure column
     * @param {object} config       Widget config object from the dashboard def
     * @returns {object} ECharts setOption-compatible option object
     */
    function buildChartOption(resolvedType, xAxisData, allSeries, config) {
        var cfg = config || {};

        // ── Pie / PieHollow ───────────────────────────────────────────────────
        if (resolvedType === 'pie' || resolvedType === 'piehollow') {
            var pieData = [];
            var firstSeries = allSeries[0] || { name: '', data: [] };
            for (var pi = 0; pi < xAxisData.length; pi++) {
                pieData.push({ name: xAxisData[pi], value: firstSeries.data[pi] });
            }
            var pieRadius = resolvedType === 'piehollow' ? ['40%', '70%'] : '60%';
            return {
                tooltip: { trigger: 'item' },
                legend: { type: 'scroll', orient: 'horizontal' },
                series: [{ type: 'pie', radius: pieRadius, data: pieData }]
            };
        }

        // ── Funnel ────────────────────────────────────────────────────────────
        if (resolvedType === 'funnel') {
            var funnelData = [];
            var funnelSrc = allSeries[0] || { name: '', data: [] };
            for (var fi = 0; fi < xAxisData.length; fi++) {
                funnelData.push({ name: xAxisData[fi], value: funnelSrc.data[fi] });
            }
            return {
                tooltip: { trigger: 'item' },
                series: [{ type: 'funnel', data: funnelData }]
            };
        }

        // ── Gauge ─────────────────────────────────────────────────────────────
        if (resolvedType === 'gauge') {
            var gaugeVal = (allSeries[0] && allSeries[0].data && allSeries[0].data[0] != null)
                ? Number(allSeries[0].data[0]) : 0;
            return {
                series: [{
                    type: 'gauge',
                    data: [{ value: gaugeVal, name: (allSeries[0] && allSeries[0].name) || '' }]
                }]
            };
        }

        // ── Radar ─────────────────────────────────────────────────────────────
        if (resolvedType === 'radar') {
            var radarIndicators = xAxisData.map(function(name) { return { name: name }; });
            var radarSeriesData = allSeries.map(function(s) {
                return { name: s.name, value: s.data };
            });
            return {
                tooltip: {},
                radar: { indicator: radarIndicators },
                series: [{ type: 'radar', data: radarSeriesData }]
            };
        }

        // ── Heatmap ───────────────────────────────────────────────────────────
        if (resolvedType === 'heatmap') {
            var heatSeries = allSeries[0] || { data: [] };
            return {
                tooltip: {},
                xAxis: { type: 'category', data: xAxisData },
                yAxis: { type: 'category' },
                visualMap: { calculable: true },
                series: [{ type: 'heatmap', data: heatSeries.data }]
            };
        }

        // ── Scatter ───────────────────────────────────────────────────────────
        if (resolvedType === 'scatter') {
            var scatterSeriesArr = allSeries.map(function(s) {
                return { type: 'scatter', name: s.name, data: s.data };
            });
            return {
                tooltip: { trigger: 'item' },
                xAxis: { type: 'value' },
                yAxis: { type: 'value' },
                series: scatterSeriesArr
            };
        }

        // ── Sankey ────────────────────────────────────────────────────────────
        if (resolvedType === 'sankey') {
            // Expects allSeries[0].raw = { nodes: [...], links: [...] }
            var sankeyRaw = (allSeries[0] && allSeries[0].raw) || { nodes: [], links: [] };
            return {
                tooltip: { trigger: 'item' },
                series: [{
                    type: 'sankey',
                    data: sankeyRaw.nodes,
                    links: sankeyRaw.links,
                    emphasis: { focus: 'adjacency' }
                }]
            };
        }

        // ── Bar / Line (default, with Q2 multi-series + dual-Y) ───────────────
        // For multi-series data (columns[1..n]): series[0] goes on the primary
        // (left) Y axis with `resolvedType`; series[1..n] go on the secondary
        // (right) Y axis using `cfg.secondaryType` (default 'line').
        var yAxes = [{ type: 'value' }];
        var hasSecondary = allSeries.length > 1;
        if (hasSecondary) {
            yAxes.push({ type: 'value', splitLine: { show: false } });
        }

        var secondaryType = cfg.secondaryType || 'line';
        var seriesArr = allSeries.map(function(s, idx) {
            var sType = idx === 0 ? resolvedType : secondaryType;
            var serObj = { name: s.name, type: sType, data: s.data };
            if (hasSecondary && idx > 0) {
                serObj.yAxisIndex = 1;
            }
            return serObj;
        });

        var option = {
            tooltip: { trigger: 'axis' },
            xAxis: { type: 'category', data: xAxisData },
            yAxis: yAxes,
            series: seriesArr
        };
        if (hasSecondary) {
            option.legend = {};
        }
        return option;
    }

    function renderChart(container, data, config) {
        if (!global.echarts) return;
        var chart = global.echarts.init(container);

        // Simple fallback
        if (!data || !data.columns || !data.rows) return;

        var xAxisData = [];
        var dimField = data.columns[0];

        for (var i = 0; i < data.rows.length; i++) {
            xAxisData.push(data.rows[i][dimField]);
        }

        // Q2: Collect all measure columns (columns[1..n]) as separate series.
        // Single-column result → one series (fully backward-compatible).
        var allSeries = [];
        for (var ci = 1; ci < data.columns.length; ci++) {
            var msrField = data.columns[ci];
            var seriesData = [];
            for (var ri = 0; ri < data.rows.length; ri++) {
                seriesData.push(data.rows[ri][msrField]);
            }
            allSeries.push({ name: msrField, data: seriesData });
        }

        // Nothing to render if there are no measure columns
        if (allSeries.length === 0) return;

        var resolvedType = inferChartType(config, data);
        var option = buildChartOption(resolvedType, xAxisData, allSeries, config);
        chart.setOption(option);

        // Cross-widget drill-down: emit widgetClicked when user clicks a chart data point.
        // Payload: { widgetId, field, value, rowData }
        var widgetId = container.id;
        if (widgetId) {
            chart.on('click', function(params) {
                var clickedField = dimField;
                var clickedValue = params.name != null ? params.name : params.value;
                EventBus.emit('widgetClicked', {
                    widgetId: widgetId,
                    field: clickedField,
                    value: clickedValue,
                    rowData: params.data != null ? params.data : {}
                });
            });
        }
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

        // data rows — each row is clickable for cross-widget drill-down.
        var widgetId = container.id;
        for (var i = 0; i < data.rows.length; i++) {
            (function(rowData) {
                var tr = document.createElement('tr');
                tr.style.cursor = 'pointer';
                for (var j = 0; j < data.columns.length; j++) {
                    var td = document.createElement('td');
                    var val = rowData[data.columns[j]];
                    td.textContent = val != null ? String(val) : '';
                    tr.appendChild(td);
                }
                // Cross-widget drill-down: clicking a table row emits widgetClicked
                // for every column in the row so that any matching DrillDownLink can
                // pick up the correct source field.
                if (widgetId) {
                    tr.addEventListener('click', function() {
                        for (var k = 0; k < data.columns.length; k++) {
                            var colName = data.columns[k];
                            EventBus.emit('widgetClicked', {
                                widgetId: widgetId,
                                field: colName,
                                value: rowData[colName],
                                rowData: rowData
                            });
                        }
                    });
                }
                table.appendChild(tr);
            })(data.rows[i]);
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
                text += ' — ' + item.description;
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

    // --- Q3: Static / Text / Image widget ------------------------------------
    /**
     * Renders static content client-side with NO server fetch.
     * config.html   — arbitrary HTML markup (rendered via innerHTML after DOMPurify
     *                 if available, otherwise escaped as text for safety)
     * config.text   — plain-text content (uses textContent)
     * config.imageUrl — image URL rendered as <img>
     * config.title  — optional heading
     *
     * Priority: html > imageUrl > text
     * Security note: config.html is sanitized with DOMPurify when available.
     * Without DOMPurify the raw HTML is intentionally NOT injected; text
     * fallback is used so the widget is always safe even without a sanitizer.
     */
    function renderStatic(container, data, config) {
        container.innerHTML = '';
        var cfg = config || {};

        if (cfg.title) {
            var titleDiv = document.createElement('div');
            titleDiv.className = 'wtm-static-title';
            titleDiv.textContent = cfg.title;
            container.appendChild(titleDiv);
        }

        if (cfg.html) {
            var contentDiv = document.createElement('div');
            contentDiv.className = 'wtm-static-content';
            // Use DOMPurify if loaded; otherwise fall back to textContent for safety
            if (global.DOMPurify && typeof global.DOMPurify.sanitize === 'function') {
                contentDiv.innerHTML = global.DOMPurify.sanitize(cfg.html);
            } else {
                contentDiv.textContent = cfg.html;
            }
            container.appendChild(contentDiv);
        } else if (cfg.imageUrl) {
            var img = document.createElement('img');
            img.className = 'wtm-static-image';
            img.src = cfg.imageUrl;
            img.alt = cfg.title || '';
            img.style.maxWidth = '100%';
            container.appendChild(img);
        } else if (cfg.text) {
            var textDiv = document.createElement('div');
            textDiv.className = 'wtm-static-text';
            textDiv.textContent = cfg.text;
            container.appendChild(textDiv);
        }
    }

    // --- L7: DateRange presets -----------------------------------------------
    /**
     * Compute [startDate, endDate] strings (YYYY-MM-DD) for a named preset.
     *
     * Supported presets:
     *   today      — current day
     *   last7days  — past 7 calendar days (inclusive of today)
     *   last30days — past 30 calendar days (inclusive of today)
     *   thisMonth  — 1st day of current month → today
     *   thisQuarter — 1st day of current quarter → today
     *   thisYear   — 1st Jan of current year → today
     *
     * Returns null for unknown presets.
     *
     * @param {string} preset  Preset key (case-insensitive)
     * @param {Date}   [now]   Override current date (for testing)
     * @returns {{ start: string, end: string }|null}
     */
    function computeDateRangePreset(preset, now) {
        var d = now || new Date();
        var key = String(preset).toLowerCase().replace(/[\s_\-]/g, '');

        function pad(n) { return n < 10 ? '0' + n : String(n); }
        function fmt(dt) {
            return dt.getFullYear() + '-' + pad(dt.getMonth() + 1) + '-' + pad(dt.getDate());
        }
        function addDays(dt, n) {
            var r = new Date(dt.getTime());
            r.setDate(r.getDate() + n);
            return r;
        }

        var today = new Date(d.getFullYear(), d.getMonth(), d.getDate());
        var endStr = fmt(today);

        if (key === 'today') {
            return { start: endStr, end: endStr };
        }
        if (key === 'last7days') {
            return { start: fmt(addDays(today, -6)), end: endStr };
        }
        if (key === 'last30days') {
            return { start: fmt(addDays(today, -29)), end: endStr };
        }
        if (key === 'thismonth') {
            var monthStart = new Date(today.getFullYear(), today.getMonth(), 1);
            return { start: fmt(monthStart), end: endStr };
        }
        if (key === 'thisquarter') {
            var q = Math.floor(today.getMonth() / 3);
            var qStart = new Date(today.getFullYear(), q * 3, 1);
            return { start: fmt(qStart), end: endStr };
        }
        if (key === 'thisyear') {
            var yearStart = new Date(today.getFullYear(), 0, 1);
            return { start: fmt(yearStart), end: endStr };
        }
        return null;
    }

    var _renderers = {
        kpi: renderKpi,
        chart: renderChart,
        table: renderTable,
        progress: renderProgress,
        list: renderList,
        embed: renderEmbed,
        static: renderStatic
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
    // Q10: per-widget in-flight guard
    var _widgetInFlight = {};
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

                // Cross-widget drill-down: wire up DrillDownLink entries from widgets.
                if (def.widgets) {
                    DashboardManager._registerDrillDownLinks(def.widgets);
                }

                // Q1: When any filter changes, re-fetch all widgets so the new
                // filter values are forwarded to the data sources.
                FilterBar.onChange(function() {
                    DashboardManager._renderAllWidgets(def);
                });

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
                    // Q3: static widgets have no server fetch — render directly
                    var wDef = def.widgets[widgetId];
                    if (wDef.type === 'static') {
                        var staticContainer = document.getElementById(widgetId);
                        if (staticContainer) {
                            renderStatic(staticContainer, null, wDef.config || {});
                        }
                        continue;
                    }
                    this._fetchAndRenderWidget(widgetId, wDef);
                }
            }
        },

        _fetchAndRenderWidget: function(widgetId, widgetDef) {
            var container = document.getElementById(widgetId);
            if (!container) return;

            // Skip if max retries exceeded
            if ((_failureCounts[widgetId] || 0) >= MAX_RETRIES) return;

            // Q10: In-flight guard — skip if a fetch is already running for
            // this widget. The next scheduled tick will run after completion.
            if (_widgetInFlight[widgetId]) return;
            _widgetInFlight[widgetId] = true;

            // Show loading state
            container.className = 'wtm-widget-loading';
            container.textContent = 'Loading...';

            // Q1: Wire FilterBar values into the widget data request.
            // FilterBar.getValues() returns the current filter state as a plain
            // key→value map.  Append each non-empty value as a query-string
            // parameter so the server can forward them to the data source.
            var baseUrl = '/_dashboard/' + _currentDashboard.id + '/widget/' + widgetId + '/data';
            var filterValues = FilterBar.getValues();
            var params = [];
            for (var k in filterValues) {
                if (Object.prototype.hasOwnProperty.call(filterValues, k)) {
                    var v = filterValues[k];
                    if (v !== null && v !== undefined && String(v).length > 0) {
                        params.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
                    }
                }
            }
            var url = params.length > 0 ? baseUrl + '?' + params.join('&') : baseUrl;

            // Capture widgetId in closure for the async callbacks
            (function(wId, wDef) {
                global.fetch(url)
                    .then(function(res) {
                        if (!res.ok) throw new Error('Widget data fetch failed');
                        return res.json();
                    })
                    .then(function(data) {
                        _widgetInFlight[wId] = false;
                        container.className = '';
                        _failureCounts[wId] = 0;
                        var renderer = WidgetRendererFactory.getRenderer(wDef.type);
                        if (renderer) {
                            renderer(container, data, wDef.config || {});
                        } else {
                            container.textContent = 'Unknown widget type: ' + wDef.type;
                        }
                    })
                    .catch(function(e) {
                        _widgetInFlight[wId] = false;
                        _failureCounts[wId] = (_failureCounts[wId] || 0) + 1;
                        container.className = 'wtm-widget-error';
                        container.textContent = 'Error loading widget data.';
                        if (console && console.error) console.error(e);
                    });
            })(widgetId, widgetDef);
        },

        _registerLinks: function(links) {
            for (var i = 0; i < links.length; i++) {
                var link = links[i];
                EventBus.on(link.event, link.sourceWidget, function(payload) {
                    // Legacy WidgetLink action handling (future expansion placeholder).
                });
            }
        },

        /**
         * Cross-widget drill-down: register DrillDownLink entries from widget definitions.
         *
         * For each widget that has a non-empty `drillDown` array, listen for
         * `widgetClicked` events from that widget.  When the clicked field matches
         * a DrillDownLink's sourceField:
         *   - If targetWidgetId is set: set the filter value via FilterBar and
         *     re-fetch only that one target widget.
         *   - Otherwise: set the value via FilterBar (which broadcasts onChange to
         *     all widgets automatically).
         *
         * @param {object} widgets  Map of widgetId → widgetDef from the dashboard definition.
         */
        _registerDrillDownLinks: function(widgets) {
            if (!widgets) return;
            var self = this;
            for (var sourceWidgetId in widgets) {
                if (!Object.prototype.hasOwnProperty.call(widgets, sourceWidgetId)) continue;
                var wDef = widgets[sourceWidgetId];
                var links = wDef.drillDown;
                if (!links || !links.length) continue;

                // Capture the sourceWidgetId and its links in a closure.
                (function(srcId, drillLinks) {
                    EventBus.on('widgetClicked', srcId, function(payload) {
                        if (!payload || payload.widgetId !== srcId) return;
                        for (var li = 0; li < drillLinks.length; li++) {
                            var dl = drillLinks[li];
                            if (dl.sourceField && dl.sourceField !== payload.field) continue;
                            // Apply the filter value.
                            FilterBar.setValue(dl.targetFilterId, String(payload.value != null ? payload.value : ''));
                            // If a specific target widget is named, re-fetch it directly.
                            // FilterBar.setValue already triggers onChange (which re-fetches
                            // all widgets) when targetWidgetId is absent.
                            if (dl.targetWidgetId && _currentDashboard) {
                                var targetDef = _currentDashboard.widgets && _currentDashboard.widgets[dl.targetWidgetId];
                                if (targetDef) {
                                    self._fetchAndRenderWidget(dl.targetWidgetId, targetDef);
                                }
                            }
                        }
                    });
                })(sourceWidgetId, links);
            }
        },

        // Q10: Self-scheduling refresh — schedule next tick only after
        // current widget fetches complete, preventing request pile-up.
        startRefresh: function(intervalSec) {
            this.stopRefresh();
            var ms = intervalSec * 1000;
            (function scheduleNext() {
                _refreshTimer = global.setTimeout(function() {
                    if (_currentDashboard) {
                        DashboardManager._renderAllWidgets(_currentDashboard);
                    }
                    scheduleNext();
                }, ms);
            })();
        },

        stopRefresh: function() {
            if (_refreshTimer) {
                global.clearTimeout(_refreshTimer);
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
        },

        // Q10: Expose in-flight state for testing
        _getInFlight: function(widgetId) {
            return !!_widgetInFlight[widgetId];
        },

        _setInFlight: function(widgetId, val) {
            _widgetInFlight[widgetId] = !!val;
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
                } else if (f.type === 'daterange') {
                    // L7: DateRange filter — renders a preset dropdown + optional
                    // start/end text inputs. Preset changes compute [start, end]
                    // and set two filter fields: `<field>_start` and `<field>_end`.
                    var drSel = document.createElement('select');
                    drSel.name = f.field + '_preset';
                    var presets = [
                        { key: 'today',       label: '今天' },
                        { key: 'last7days',   label: '近7天' },
                        { key: 'last30days',  label: '近30天' },
                        { key: 'thisMonth',   label: '本月' },
                        { key: 'thisQuarter', label: '本季' },
                        { key: 'thisYear',    label: '本年' },
                        { key: 'custom',      label: '自訂' }
                    ];
                    for (var p = 0; p < presets.length; p++) {
                        var pOpt = document.createElement('option');
                        pOpt.value = presets[p].key;
                        pOpt.textContent = presets[p].label;
                        drSel.appendChild(pOpt);
                    }
                    // Custom range inputs (shown only for 'custom' preset)
                    var startInput = document.createElement('input');
                    startInput.type = 'text';
                    startInput.name = f.field + '_start';
                    startInput.placeholder = 'YYYY-MM-DD';
                    var endInput = document.createElement('input');
                    endInput.type = 'text';
                    endInput.name = f.field + '_end';
                    endInput.placeholder = 'YYYY-MM-DD';

                    // Initialize filter values from default preset
                    var initPreset = (f.defaultValue && f.defaultValue !== 'custom') ? f.defaultValue : 'thisMonth';
                    var initRange = computeDateRangePreset(initPreset);
                    if (initRange) {
                        _filterValues[f.field + '_start'] = initRange.start;
                        _filterValues[f.field + '_end'] = initRange.end;
                    }

                    (function(fieldName, drSelEl, startEl, endEl) {
                        drSelEl.addEventListener('change', function() {
                            var selected = drSelEl.value;
                            if (selected === 'custom') {
                                // For custom range, wait for user to fill start/end inputs
                                FilterBar.setValue(fieldName + '_start', startEl.value || '');
                                FilterBar.setValue(fieldName + '_end', endEl.value || '');
                            } else {
                                var range = computeDateRangePreset(selected);
                                if (range) {
                                    startEl.value = range.start;
                                    endEl.value = range.end;
                                    FilterBar.setValue(fieldName + '_start', range.start);
                                    FilterBar.setValue(fieldName + '_end', range.end);
                                }
                            }
                        });
                        startEl.addEventListener('input', function() {
                            FilterBar.setValue(fieldName + '_start', startEl.value);
                        });
                        endEl.addEventListener('input', function() {
                            FilterBar.setValue(fieldName + '_end', endEl.value);
                        });
                    })(f.field, drSel, startInput, endInput);

                    wrapper.appendChild(drSel);
                    wrapper.appendChild(startInput);
                    wrapper.appendChild(endInput);
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
            inferChartType: inferChartType,
            buildChartOption: buildChartOption,
            computeDateRangePreset: computeDateRangePreset
        }
    };

    // Expose _registerDrillDownLinks on DashboardManager for testing.
    // (Already defined as a method of DashboardManager; listed here for clarity.)
    // Note: _registerDrillDownLinks is already accessible via api.DashboardManager.

    if (typeof global.window !== "undefined") {
        global.window.WtmDashboard = api;
    }

    // 將結果賦值給全域物件（為了解決某些環境下 global 未指向 window 的問題）
    global.WtmDashboard = api;

    return api;
})(typeof window !== "undefined" ? window : this);
