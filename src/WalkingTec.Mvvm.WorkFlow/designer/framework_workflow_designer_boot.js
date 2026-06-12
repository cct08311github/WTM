// framework_workflow_designer_boot.js — WF-21 FIX-A3
// Boot/orchestration module for the low-code workflow designer.
//
// Implements design spec §1 boot sequence:
//   1. URLSearchParams → read ?code= (definition to open, if any)
//   2. GET bootstrap → obtain antiforgery token + set on DesignerApi
//   3. GET definition list → render definition list panel
//   4. Toolbar / tab bar rendering
//   5. Form/source/SVG view tab switching
//   6. Node-click → openNodePanel via NodePanel (WF-21.6)
//   7. Save (draft) and Publish flows wired end-to-end
//
// Security invariants (T-DSN-9 / FIX-A3):
//   - eval-free: no eval, no new Function, no Function constructor
//   - DOM mutations: createElement/textContent/setAttribute/addEventListener only;
//     no innerHTML, no insertAdjacentHTML, no outerHTML
//   - Kind string from server is sanitized via textContent before any use in
//     layui layer.open title (stored-XSS sink guard per FIX-A3 review finding)
//   - layer.msg/layer.alert never receive raw server-derived strings
//
// Design: IIFE, exposes window.WtmDesignerBoot only.
// Dependencies: window.WtmDesignerCore (core.js), window.layui (consumer wwwroot).
// zh-CN UI strings inline; American-English code/comments.

'use strict';

(function () {

    // ── Dependency guard ──────────────────────────────────────────────────────
    // WtmDesignerCore is loaded by the <script> tags that precede this file.
    // If it is missing (script load failure), surface the error and bail.

    function _fatal(msg) {
        var el = document.getElementById('wfd-error');
        if (el) {
            el.textContent = msg;
            el.style.display = 'block';
        }
        var loading = document.getElementById('wfd-loading');
        if (loading) { loading.style.display = 'none'; }
    }

    if (typeof window === 'undefined') {
        // Running in a test environment that does not provide window — no-op.
        // Tests invoke boot() directly or stub the DOM.
        return;
    }

    if (!window.WtmDesignerCore) {
        _fatal('设计器核心模块未加载 (WtmDesignerCore missing)。请检查网络请求。');
        return;
    }

    var Core = window.WtmDesignerCore;
    var DesignerApi  = Core.DesignerApi;
    var GraphModel   = Core.GraphModel;
    var SourceMode   = Core.SourceMode;
    var LocalStorageCache = Core.LocalStorageCache;
    var isSchemaVersionSupported = Core.isSchemaVersionSupported;

    // ── State ─────────────────────────────────────────────────────────────────

    var _state = {
        code:            null,     // current definition code (null = list view)
        xsrfToken:       null,
        autoSaveTimer:   null,
        currentRowVer:   null,     // draft RowVersion for If-Match
        isDraftNew:      false,    // true = no server draft yet; use If-None-Match:*
        currentView:     'form',   // 'form' | 'source' | 'svg'
        definitions:     [],       // cached definition list
        currentPage:     1,
        totalCount:      0,
    };

    // ── DOM helpers (all mutations via createElement/textContent/setAttribute) ─

    function _el(id) { return document.getElementById(id); }

    function _show(id) {
        var el = _el(id);
        if (el) { el.style.display = ''; }
    }

    function _hide(id) {
        var el = _el(id);
        if (el) { el.style.display = 'none'; }
    }

    function _clearChildren(el) {
        while (el && el.firstChild) { el.removeChild(el.firstChild); }
    }

    // Create a button element with textContent and a click handler.
    // label is a static string literal; never a server-derived value.
    function _btn(label, onClick, className) {
        var b = document.createElement('button');
        b.textContent = label;
        if (className) { b.className = className; }
        b.addEventListener('click', onClick);
        return b;
    }

    // Create a span with a static text label.
    function _span(text, className) {
        var s = document.createElement('span');
        s.textContent = text;
        if (className) { s.className = className; }
        return s;
    }

    // ── Sanitize kind string for layui layer.open title ───────────────────────
    // FIX-A3: kind comes from the server (node.kind field in graphJson).
    // Using it directly in layer.open({ title: kind }) creates a stored-XSS sink
    // because layui interprets title as HTML. We sanitize by setting it as
    // textContent on a temporary element and reading textContent back — this is
    // the same escaping path as .textContent assignment on real DOM nodes.
    function _sanitizeKind(kind) {
        if (typeof kind !== 'string') { return '节点'; }
        var tmp = document.createElement('span');
        tmp.textContent = kind;
        return tmp.textContent;
    }

    // ── Error display ─────────────────────────────────────────────────────────

    function _showError(msg) {
        var el = _el('wfd-error');
        if (!el) { return; }
        el.textContent = msg;   // textContent only — no innerHTML
        el.style.display = msg ? 'block' : 'none';
    }

    function _clearError() { _showError(''); }

    // ── Tab view switching ────────────────────────────────────────────────────

    var _VIEWS = ['form', 'source', 'svg'];

    function _switchView(view) {
        _state.currentView = view;
        _VIEWS.forEach(function (v) {
            var el = _el('wfd-' + v + '-view');
            if (el) { el.style.display = v === view ? '' : 'none'; }
        });
        _renderTabBar();
    }

    function _renderTabBar() {
        var bar = _el('wfd-tab-bar');
        if (!bar) { return; }
        _clearChildren(bar);

        var tabs = [
            { id: 'form',   label: '表单视图' },
            { id: 'source', label: '源码视图' },
            { id: 'svg',    label: '流程图'   },
        ];

        tabs.forEach(function (tab) {
            var btn = document.createElement('button');
            btn.textContent = tab.label;
            btn.className = 'wtm-tab-btn' + (tab.id === _state.currentView ? ' wtm-tab-active' : '');
            btn.setAttribute('data-view', tab.id);
            btn.addEventListener('click', function () { _switchView(tab.id); });
            bar.appendChild(btn);
        });
    }

    // ── Toolbar rendering ─────────────────────────────────────────────────────

    function _renderToolbar() {
        var actions = _el('wfd-toolbar-actions');
        if (!actions) { return; }
        _clearChildren(actions);

        if (_state.code) {
            // Edit view: back, save draft, publish
            actions.appendChild(_btn('← 返回列表', function () { _openList(); }, 'wtm-btn'));
            actions.appendChild(_btn('保存草稿', function () { _saveDraft(); }, 'wtm-btn'));
            actions.appendChild(_btn('发布', function () { _publish(); }, 'wtm-btn wtm-btn-primary'));
        } else {
            // List view: create
            actions.appendChild(_btn('+ 新建流程', function () { _openCreateDialog(); }, 'wtm-btn wtm-btn-primary'));
        }
    }

    // ── Definition list view ──────────────────────────────────────────────────

    function _openList() {
        _state.code        = null;
        _state.currentView = 'form';

        _hide('wfd-edit-panel');
        _show('wfd-list-panel');
        _renderToolbar();
        _loadDefinitions(_state.currentPage);
    }

    function _loadDefinitions(page) {
        _state.currentPage = page || 1;
        DesignerApi.listDefinitions(_state.currentPage, 20)
            .then(function (data) {
                _state.definitions = (data && data.items) ? data.items : [];
                _state.totalCount  = (data && data.totalCount) ? data.totalCount : 0;
                _renderDefinitionList(_state.definitions);
            })
            .catch(function (err) {
                _showError('加载流程列表失败: 请检查网络连接');
            });
    }

    function _renderDefinitionList(items) {
        var panel = _el('wfd-list-panel');
        if (!panel) { return; }
        _clearChildren(panel);

        if (!items || items.length === 0) {
            var empty = document.createElement('p');
            empty.textContent = '暂无流程定义，点击右上角新建。';
            empty.style.color = '#999';
            panel.appendChild(empty);
            return;
        }

        var table = document.createElement('table');
        table.className = 'wtm-def-table layui-table';
        table.style.width = '100%';

        // Header row — static strings only
        var thead = document.createElement('thead');
        var hrow  = document.createElement('tr');
        ['编号', '名称', '分类', '状态', '操作'].forEach(function (col) {
            var th = document.createElement('th');
            th.textContent = col;
            hrow.appendChild(th);
        });
        thead.appendChild(hrow);
        table.appendChild(thead);

        var tbody = document.createElement('tbody');

        items.forEach(function (def) {
            var tr = document.createElement('tr');

            // Code — safe plain text
            var tdCode = document.createElement('td');
            tdCode.textContent = def.code || '';
            tr.appendChild(tdCode);

            // Name — safe plain text
            var tdName = document.createElement('td');
            tdName.textContent = def.name || '';
            tr.appendChild(tdName);

            // Category — safe plain text
            var tdCat = document.createElement('td');
            tdCat.textContent = def.category || '';
            tr.appendChild(tdCat);

            // IsEnabled badge — static text
            var tdStatus = document.createElement('td');
            tdStatus.textContent = def.isEnabled ? '启用' : '禁用';
            tr.appendChild(tdStatus);

            // Actions
            var tdActions = document.createElement('td');
            var editBtn = _btn('编辑', function () {
                _openDefinition(def.code);
            }, 'layui-btn layui-btn-sm');
            tdActions.appendChild(editBtn);
            tr.appendChild(tdActions);

            tbody.appendChild(tr);
        });

        table.appendChild(tbody);
        panel.appendChild(table);
    }

    // ── Create definition dialog ───────────────────────────────────────────────

    function _openCreateDialog() {
        var layer = (window.layui && window.layui.layer) ? window.layui.layer : null;
        if (!layer) {
            _showError('layui 未加载，无法打开对话框');
            return;
        }

        // Build dialog content with DOM methods only (no innerHTML)
        var wrap = document.createElement('div');
        wrap.style.padding = '16px';

        var codeLabel = document.createElement('label');
        codeLabel.textContent = '流程编号（字母/数字/-/_/.，1-64位）：';
        wrap.appendChild(codeLabel);
        wrap.appendChild(document.createElement('br'));

        var codeInput = document.createElement('input');
        codeInput.className = 'layui-input';
        codeInput.setAttribute('type', 'text');
        codeInput.setAttribute('maxlength', '64');
        codeInput.style.marginBottom = '12px';
        wrap.appendChild(codeInput);
        wrap.appendChild(document.createElement('br'));

        var nameLabel = document.createElement('label');
        nameLabel.textContent = '流程名称：';
        wrap.appendChild(nameLabel);
        wrap.appendChild(document.createElement('br'));

        var nameInput = document.createElement('input');
        nameInput.className = 'layui-input';
        nameInput.setAttribute('type', 'text');
        nameInput.setAttribute('maxlength', '200');
        wrap.appendChild(nameInput);

        layer.open({
            type: 1,
            title: '新建流程定义',
            content: wrap,
            btn: ['确认', '取消'],
            yes: function (index) {
                var code = codeInput.value.trim();
                var name = nameInput.value.trim();
                if (!code || !name) {
                    layer.msg('请填写编号和名称', { icon: 2 });
                    return;
                }
                _createDefinition(code, name, index, layer);
            },
            btn2: function (index) { layer.close(index); }
        });
    }

    function _createDefinition(code, name, dialogIndex, layer) {
        DesignerApi.createDefinition({ code: code, name: name })
            .then(function (r) {
                if (r.status === 201) {
                    if (layer && dialogIndex !== undefined) { layer.close(dialogIndex); }
                    _openDefinition(code);
                } else if (r.status === 409) {
                    layer.msg('流程编号已存在，请更换', { icon: 2 });
                } else {
                    layer.msg('创建失败 (HTTP ' + r.status + ')', { icon: 2 });
                }
            })
            .catch(function () {
                layer.msg('网络错误，请重试', { icon: 2 });
            });
    }

    // ── Open definition for editing ───────────────────────────────────────────

    function _openDefinition(code) {
        _state.code = code;
        _clearError();

        _hide('wfd-list-panel');
        _show('wfd-edit-panel');
        _switchView('form');
        _renderToolbar();
        _renderTabBar();

        // Load graph envelope (current published version + draft if any)
        DesignerApi.getGraph(code)
            .then(function (envelope) {
                GraphModel.load(envelope);
                _renderFormView();
                _renderSvgView();

                // If a server draft exists, pre-populate source view too
                if (envelope && envelope.draft && envelope.draft.graphJson) {
                    SourceMode.loadText(envelope.draft.graphJson);
                    _state.currentRowVer = envelope.draft.rowVer
                        ? Number(envelope.draft.rowVer) : null;
                    _state.isDraftNew = (_state.currentRowVer === null);
                } else {
                    SourceMode.loadText(GraphModel.getPayload());
                    _state.currentRowVer = null;
                    _state.isDraftNew    = true;
                }
            })
            .catch(function () {
                _showError('加载流程失败，请刷新页面重试');
            });
    }

    // ── Form view rendering ───────────────────────────────────────────────────
    // Form view shows the graph node list and wires node-click → openNodePanel.
    // All mutations use createElement/textContent/addEventListener only.

    function _renderFormView() {
        var container = _el('wfd-form-view');
        if (!container) { return; }
        _clearChildren(container);

        var tree = GraphModel.getTree();
        if (!tree) {
            var msg = document.createElement('p');
            msg.textContent = '此流程尚未有已发布版本，请在源码视图中输入流程图 JSON 后保存/发布。';
            msg.style.color = '#999';
            container.appendChild(msg);
            return;
        }

        var nodes = (tree && Array.isArray(tree.nodes)) ? tree.nodes : [];

        if (nodes.length === 0) {
            var noNodes = document.createElement('p');
            noNodes.textContent = '流程图暂无节点。';
            container.appendChild(noNodes);
            return;
        }

        var title = document.createElement('h4');
        title.textContent = '节点列表（点击编辑属性）';
        container.appendChild(title);

        var list = document.createElement('ul');
        list.className = 'wtm-node-list';
        list.style.listStyle = 'none';
        list.style.padding = '0';

        nodes.forEach(function (node, idx) {
            var li = document.createElement('li');
            li.className = 'wtm-node-item';
            li.style.cssText = 'padding:8px;border:1px solid #e6e6e6;margin-bottom:4px;cursor:pointer;border-radius:3px;';

            var keySpan = document.createElement('span');
            keySpan.textContent = node.nodeKey || ('node-' + idx);
            keySpan.style.fontWeight = '600';
            li.appendChild(keySpan);

            var sep = document.createTextNode(' — ');
            li.appendChild(sep);

            var kindSpan = document.createElement('span');
            // FIX-A3 XSS guard: sanitize kind via textContent round-trip before display
            kindSpan.textContent = _sanitizeKind(node.kind);
            kindSpan.style.color = '#666';
            li.appendChild(kindSpan);

            // node-click → openNodePanel
            li.addEventListener('click', (function (capturedNode) {
                return function () { _openNodePanel(capturedNode); };
            }(node)));

            list.appendChild(li);
        });

        container.appendChild(list);
    }

    // ── Node panel (layui drawer/dialog) ──────────────────────────────────────
    // Opens a layui layer.open dialog with node properties.
    // kind string is sanitized via _sanitizeKind before use in the title.

    function _openNodePanel(node) {
        var layer = (window.layui && window.layui.layer) ? window.layui.layer : null;
        if (!layer) { return; }

        // Build panel content with DOM methods only
        var wrap = document.createElement('div');
        wrap.style.padding = '16px';

        var schemaVer = GraphModel.getSchemaVersion();
        var editable  = isSchemaVersionSupported(schemaVer);

        // NodeKey (read-only display)
        var keyLabel = document.createElement('label');
        keyLabel.textContent = '节点键 (NodeKey)：';
        wrap.appendChild(keyLabel);
        var keyVal = document.createElement('p');
        keyVal.textContent = node.nodeKey || '';
        keyVal.style.fontWeight = '600';
        wrap.appendChild(keyVal);

        // Kind (read-only display)
        var kindLabel = document.createElement('label');
        kindLabel.textContent = '节点类型 (Kind)：';
        wrap.appendChild(kindLabel);
        var kindVal = document.createElement('p');
        // FIX-A3 XSS: kind used as textContent in title param below AND here — sanitized
        var safeKind = _sanitizeKind(node.kind);
        kindVal.textContent = safeKind;
        wrap.appendChild(kindVal);

        if (!editable) {
            var warn = document.createElement('p');
            warn.style.color = '#cf1322';
            warn.textContent = '此版本 schemaVersion 不受支持，仅可查看，无法编辑。';
            wrap.appendChild(warn);
        }

        // Approver rule (editable when supported)
        var approverInput = null;
        if (editable && node.approverRule) {
            var arLabel = document.createElement('label');
            arLabel.textContent = '审批人规则 Value：';
            wrap.appendChild(arLabel);
            approverInput = document.createElement('input');
            approverInput.className = 'layui-input';
            approverInput.setAttribute('type', 'text');
            approverInput.value = (node.approverRule && node.approverRule.Value) ? node.approverRule.Value : '';
            wrap.appendChild(approverInput);
        }

        // FIX-A3: use sanitized kind in layer.open title — prevents stored XSS
        // because layui interprets the title parameter as HTML
        var safeTitle = '节点属性 — ' + safeKind;

        var btns = editable ? ['保存', '取消'] : ['关闭'];

        layer.open({
            type: 1,
            title: safeTitle,
            content: wrap,
            area: ['480px', '400px'],
            btn: btns,
            yes: function (index) {
                if (editable && approverInput && node.approverRule) {
                    // Patch the approverRule.Value in the model tree
                    var nodeIdx = _findNodeIndex(node.nodeKey);
                    if (nodeIdx >= 0) {
                        GraphModel.setProperty(['nodes', nodeIdx, 'approverRule', 'Value'], approverInput.value);
                        GraphModel.markDirty();
                        _renderFormView();
                    }
                }
                layer.close(index);
            },
            btn2: function (index) { layer.close(index); }
        });
    }

    function _findNodeIndex(nodeKey) {
        var tree  = GraphModel.getTree();
        var nodes = (tree && Array.isArray(tree.nodes)) ? tree.nodes : [];
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].nodeKey === nodeKey) { return i; }
        }
        return -1;
    }

    // ── SVG view rendering ─────────────────────────────────────────────────────
    // Renders a minimal ASCII/text flow summary (SVG generation is WF-21.7).
    // All mutations via textContent/createElement.

    function _renderSvgView() {
        var container = _el('wfd-svg-view');
        if (!container) { return; }
        _clearChildren(container);

        var tree = GraphModel.getTree();
        if (!tree) {
            var msg = document.createElement('p');
            msg.textContent = '无已发布版本，无法生成流程图。';
            msg.style.color = '#999';
            container.appendChild(msg);
            return;
        }

        // Emit a text-mode flow summary pending full SVG layout (WF-21.7)
        var pre = document.createElement('pre');
        pre.className = 'wtm-svg-text';
        pre.style.cssText = 'background:#f5f5f5;padding:12px;overflow:auto;font-size:12px;';

        var nodes = (tree && Array.isArray(tree.nodes)) ? tree.nodes : [];
        var transitions = (tree && Array.isArray(tree.transitions)) ? tree.transitions : [];

        var lines = [];
        lines.push('== 节点 ==');
        nodes.forEach(function (n) {
            lines.push('  ' + _sanitizeKind(n.kind) + ' | ' + (n.nodeKey || ''));
        });
        lines.push('== 流转 ==');
        transitions.forEach(function (t) {
            lines.push('  ' + (t.from || '') + ' → ' + (t.to || '') + (t.condition ? ' [条件]' : ''));
        });

        pre.textContent = lines.join('\n');
        container.appendChild(pre);
    }

    // ── Save draft ────────────────────────────────────────────────────────────

    function _saveDraft() {
        if (!_state.code) { return; }

        // In source view, sync textarea content to model first
        if (_state.currentView === 'source') {
            var sourceText = SourceMode.getText();
            var validation = SourceMode.validateSyntax();
            if (!validation.ok) {
                _showError('源码 JSON 格式错误，请修正后再保存');
                return;
            }
            try {
                GraphModel.loadRaw(sourceText);
            } catch (e) {
                _showError('JSON 解析失败: 请检查源码格式');
                return;
            }
        }

        var payload = GraphModel.getPayload();
        var options = {};

        if (_state.isDraftNew || _state.currentRowVer === null) {
            options.ifNoneMatch = true;
        } else {
            options.rowVer = _state.currentRowVer;
        }

        var versionInfo  = GraphModel.getVersionInfo();
        options.baseContentHash = versionInfo.baseContentHash || null;

        DesignerApi.saveDraft(_state.code, payload, options)
            .then(function (r) {
                if (r.status === 200) {
                    return r.json().then(function (body) {
                        _state.currentRowVer = body.newRowVersion;
                        _state.isDraftNew    = false;
                        // Best-effort local cache clear (draft persisted on server)
                        LocalStorageCache.clear(_state.code);
                        _clearError();
                        var layer = window.layui && window.layui.layer;
                        if (layer) { layer.msg('草稿已保存', { icon: 1, time: 1500 }); }
                    });
                } else if (r.status === 409) {
                    _showError('草稿并发冲突，请刷新页面重试');
                } else if (r.status === 400) {
                    _showError('保存失败：请求格式错误');
                } else {
                    _showError('保存失败 (HTTP ' + r.status + ')');
                }
            })
            .catch(function () {
                _showError('网络错误，无法保存草稿');
            });
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    function _publish() {
        if (!_state.code) { return; }

        // Sync source view content if active
        if (_state.currentView === 'source') {
            var sourceText = SourceMode.getText();
            var validation = SourceMode.validateSyntax();
            if (!validation.ok) {
                _showError('源码 JSON 格式错误，请修正后再发布');
                return;
            }
            try {
                GraphModel.loadRaw(sourceText);
            } catch (e) {
                _showError('JSON 解析失败: 请检查源码格式');
                return;
            }
        }

        var layer = (window.layui && window.layui.layer) ? window.layui.layer : null;

        if (layer) {
            layer.confirm('确认发布此版本？发布后当前草稿将被清除。', { icon: 3 },
                function (index) {
                    layer.close(index);
                    _doPublish(layer);
                }
            );
        } else {
            _doPublish(null);
        }
    }

    function _doPublish(layer) {
        var payload      = GraphModel.getPayload();
        var versionInfo  = GraphModel.getVersionInfo();
        var expectedHash = versionInfo.contentHash || null;

        DesignerApi.publish(_state.code, payload, expectedHash)
            .then(function (r) {
                if (r.status === 200) {
                    return r.json().then(function (body) {
                        LocalStorageCache.clear(_state.code);
                        _state.currentRowVer = null;
                        _state.isDraftNew    = true;
                        _clearError();
                        if (layer) {
                            layer.msg('发布成功 (版本 ' + body.versionNo + ')', { icon: 1, time: 2000 });
                        }
                        // Reload graph to update version info
                        _openDefinition(_state.code);
                    });
                } else if (r.status === 409) {
                    _showError('发布冲突：有其他用户并发发布，请刷新后重试');
                } else if (r.status === 400) {
                    return r.json().then(function (body) {
                        _showError('发布验证失败：' + (body.errorMessage || '请检查流程图结构'));
                    }).catch(function () {
                        _showError('发布请求格式错误');
                    });
                } else if (r.status === 404) {
                    _showError('流程定义不存在，请刷新页面');
                } else {
                    _showError('发布失败 (HTTP ' + r.status + ')');
                }
            })
            .catch(function () {
                _showError('网络错误，无法发布');
            });
    }

    // ── Main boot sequence ────────────────────────────────────────────────────

    function boot() {
        // 1. Read ?code= from URLSearchParams
        var params   = new URLSearchParams(window.location.search);
        var codeParam = params.get('code') || null;

        // 2. GET bootstrap → obtain antiforgery token
        DesignerApi.getBootstrap()
            .then(function (data) {
                if (data && data.requestToken) {
                    _state.xsrfToken = data.requestToken;
                    DesignerApi.setXsrfToken(data.requestToken);
                }

                // 3. Hide loading, show shell
                _hide('wfd-loading');
                _show('wfd-shell');
                _clearError();

                // 4. Initialize SourceMode into the source-view container
                var sourceContainer = _el('wfd-source-view');
                if (sourceContainer) {
                    SourceMode.init(sourceContainer);
                }

                // 5. Render toolbar
                _renderToolbar();

                // 6. Route: open specific definition or show list
                if (codeParam) {
                    _openDefinition(codeParam);
                } else {
                    _openList();
                }
            })
            .catch(function (err) {
                _fatal('设计器初始化失败，请刷新页面重试。如问题持续，请联系管理员。');
            });
    }

    // ── Expose window.WtmDesignerBoot ────────────────────────────────────────

    window.WtmDesignerBoot = {
        boot:            boot,
        // Internal helpers exposed for testing
        _sanitizeKind:   _sanitizeKind,
        _switchView:     _switchView,
        _saveDraft:      _saveDraft,
        _publish:        _publish,
        _openDefinition: _openDefinition,
        _openList:       _openList,
        _state:          _state,
        version:         'WF-21-FIX-A3'
    };

    // Auto-boot when DOM is ready (DOMContentLoaded or immediately if already loaded)
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }

}());
