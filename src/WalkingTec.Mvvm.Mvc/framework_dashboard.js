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

    // --- API 導出 ------------------------------------------------------------
    var api = {
        EventBus: EventBus,
        GridManager: GridManager
    };

    if (typeof global.window !== "undefined") {
        global.window.WtmDashboard = api;
    }
    
    // 將結果賦值給全域物件（為了解決某些環境下 global 未指向 window 的問題）
    global.WtmDashboard = api;

    return api;
})(typeof window !== "undefined" ? window : this);