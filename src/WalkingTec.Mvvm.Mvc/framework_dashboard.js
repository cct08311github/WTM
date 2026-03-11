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

    // --- GridManager --------------------------------------------------------
    var _grid = null;
    var _isEditMode = false;

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
                type: config.chartType || 'bar'
            }]
        };
        
        chart.setOption(option);
    }

    var _renderers = {
        kpi: renderKpi,
        chart: renderChart
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
            if (!container) return; // Grid item might not exist yet if layout wasn't set up perfectly, but assume it exists in grid
            
            // Show loading
            container.textContent = 'Loading...';
            
            var url = '/_dashboard/' + _currentDashboard.id + '/widget/' + widgetId + '/data';
            
            global.fetch(url)
                .then(function(res) {
                    if (!res.ok) throw new Error('Widget data fetch failed');
                    return res.json();
                })
                .then(function(data) {
                    var renderer = WidgetRendererFactory.getRenderer(widgetDef.type);
                    if (renderer) {
                        renderer(container, data, widgetDef.config || {});
                    } else {
                        container.textContent = 'Unknown widget type: ' + widgetDef.type;
                    }
                })
                .catch(function(e) {
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
                    wrapper.appendChild(sel);
                } else {
                    var input = document.createElement('input');
                    input.type = 'text';
                    input.name = f.field;
                    if (f.defaultValue) input.value = f.defaultValue;
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
        FilterBar: FilterBar
    };

    if (typeof global.window !== "undefined") {
        global.window.WtmDashboard = api;
    }
    
    // 將結果賦值給全域物件（為了解決某些環境下 global 未指向 window 的問題）
    global.WtmDashboard = api;

    return api;
})(typeof window !== "undefined" ? window : this);