var WtmDashboard = (function () {
    'use strict';

    // ─── EventBus ──────────────────────────────────────────────────────────────
    var _handlers = {};

    var EventBus = {
        on: function (event, handler) {
            if (!_handlers[event]) {
                _handlers[event] = [];
            }
            _handlers[event].push(handler);
        },
        off: function (event, handler) {
            if (!_handlers[event]) return;
            var idx = _handlers[event].indexOf(handler);
            if (idx !== -1) {
                _handlers[event].splice(idx, 1);
            }
        },
        emit: function (event, data) {
            if (!_handlers[event]) return;
            for (var i = 0; i < _handlers[event].length; i++) {
                _handlers[event][i](data);
            }
        },
        _reset: function () {
            _handlers = {};
        }
    };

    // ─── GridManager ───────────────────────────────────────────────────────────
    var _grid = null;
    var _editMode = false;

    var GridManager = {
        init: function (selector, options) {
            if (typeof GridStack === 'undefined') {
                return null;
            }
            var opts = {
                column: 12,
                cellHeight: 80,
                animate: true,
                disableResize: true,
                disableDrag: true
            };
            if (options) {
                for (var key in options) {
                    if (options.hasOwnProperty(key)) {
                        opts[key] = options[key];
                    }
                }
            }
            _grid = GridStack.init(opts, selector);
            _editMode = false;
            return _grid;
        },
        saveLayout: function () {
            if (!_grid) return [];
            return _grid.save();
        },
        loadLayout: function (items) {
            if (!_grid) return;
            _grid.removeAll();
            _grid.load(items);
        },
        isEditMode: function () {
            return _editMode;
        },
        setEditMode: function (enabled) {
            _editMode = enabled;
            if (!_grid) return;
            if (enabled) {
                _grid.enableMove(true);
                _grid.enableResize(true);
            } else {
                _grid.enableMove(false);
                _grid.enableResize(false);
            }
        },
        addWidget: function (opts) {
            if (!_grid) return null;
            return _grid.addWidget(opts);
        },
        getGrid: function () {
            return _grid;
        },
        _reset: function () {
            _grid = null;
            _editMode = false;
        }
    };

    // ─── Public API ────────────────────────────────────────────────────────────
    return {
        EventBus: EventBus,
        GridManager: GridManager
    };
})();
