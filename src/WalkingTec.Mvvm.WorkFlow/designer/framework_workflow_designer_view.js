// framework_workflow_designer_view.js — WF-21.7
// Low-code workflow designer: read-only SVG auto-layout graph view,
// version-history drawer, publish confirm/conflict flows,
// ProcessDefinitionListVM dead-link repoint.
//
// Security rules (T-DSN-9):
//   - eval-free: no eval, no new Function, no Function constructor
//   - SVG: all elements via createElementNS + textContent only
//   - No foreignObject; numeric-only attribute composition
//   - DOM: all mutations via createElement/createElementNS/textContent/addEventListener
//   - layer.msg/alert never receive server-derived strings
//   - No inline event handlers in generated markup
//   - No CDN/network assets
//
// Design: IIFE, exposes window.WtmDesignerView only.
// Dependencies: window.WtmDesignerCore (core module loaded first),
//               layui / $ available on window (consumer wwwroot).
// zh-CN UI strings inline; American-English code/comments.

'use strict';

(function () {

    // ─────────────────────────────────────────────────────────────────────────
    // Constants — matching WorkflowGraphSchema.cs node kinds
    // ─────────────────────────────────────────────────────────────────────────

    var NK_START     = 'Start';
    var NK_END       = 'End';
    var NK_APPROVAL  = 'Approval';
    var NK_CONDITION = 'Condition';
    var NK_CC        = 'Cc';
    var NK_ACK       = 'Ack';
    var NK_JOIN      = 'Join';
    var NK_PARALLEL  = 'ParallelGateway';
    var NK_INCLUSIVE = 'InclusiveGateway';

    var KIND_LABELS = {
        'Start':           '开始',
        'End':             '结束',
        'Approval':        '审批',
        'Condition':       '条件网关',
        'Cc':              '抄送',
        'Ack':             '确认',
        'Join':            '汇聚',
        'ParallelGateway': '并行网关',
        'InclusiveGateway':'包容网关'
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Layout constants (all numeric — no dynamic string composition)
    // ─────────────────────────────────────────────────────────────────────────

    var NODE_W      = 140;  // node box width (px)
    var NODE_H      = 50;   // node box height (px)
    var RANK_GAP    = 120;  // horizontal gap between ranks
    var NODE_V_GAP  = 20;   // vertical gap between nodes in same rank
    var PADDING     = 40;   // canvas padding

    // Diamond (gateway) half-size
    var DIAMOND     = 24;

    // SVG namespace
    var SVG_NS      = 'http://www.w3.org/2000/svg';

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Layout engine — BFS rank-from-Start, deterministic ordinal tie-break
    //    Pure function: (nodes[], transitions[]) → layoutMap {nodeKey→{x,y,w,h}}
    //
    //    Algorithm:
    //      1. Find Start node (kind === 'Start').  Fall back to first node ordinal.
    //      2. BFS from Start, assigning rank = shortest-path depth in the transition
    //         DAG.  Cycles are tolerated (already-ranked nodes are not re-visited).
    //      3. Within each rank, nodes appear in stable ordinal order (insertion
    //         order in the nodes array, then alphabetically by key as tiebreak).
    //      4. Disconnected nodes are appended after the BFS frontier, in ordinal order.
    //      5. x = PADDING + rank * (NODE_W + RANK_GAP)
    //         y = PADDING + nodeIndex * (NODE_H + NODE_V_GAP)
    // ─────────────────────────────────────────────────────────────────────────

    function _computeLayout(nodes, transitions) {
        if (!Array.isArray(nodes) || nodes.length === 0) { return {}; }
        if (!Array.isArray(transitions)) { transitions = []; }

        // Index nodes by key; track ordinal for stable ordering.
        var nodeByKey = {};
        var ordinalByKey = {};
        for (var ni = 0; ni < nodes.length; ni++) {
            var n = nodes[ni];
            var k = _strVal(n['nodeKey'] || n['key']);
            if (k) {
                nodeByKey[k] = n;
                ordinalByKey[k] = ni;
            }
        }

        // Build adjacency list (out-edges) from transitions.
        var outEdges = {};
        for (var ti = 0; ti < transitions.length; ti++) {
            var tr = transitions[ti];
            var fromKey = _strVal(tr['from']);
            var toKey   = _strVal(tr['to']);
            if (!fromKey || !toKey) { continue; }
            if (!outEdges[fromKey]) { outEdges[fromKey] = []; }
            outEdges[fromKey].push(toKey);
        }

        // Find Start node key.
        var startKey = null;
        for (var si = 0; si < nodes.length; si++) {
            var kind = _strVal(nodes[si]['kind'] || nodes[si]['nodeKind']);
            if (kind === NK_START) {
                startKey = _strVal(nodes[si]['nodeKey'] || nodes[si]['key']);
                break;
            }
        }
        // Fall back to first node in ordinal order.
        if (!startKey && nodes.length > 0) {
            startKey = _strVal(nodes[0]['nodeKey'] || nodes[0]['key']);
        }

        // BFS rank assignment.
        var rankOf = {};
        var queue = [];
        if (startKey && nodeByKey[startKey]) {
            rankOf[startKey] = 0;
            queue.push(startKey);
        }
        var head = 0;
        while (head < queue.length) {
            var cur = queue[head++];
            var edges = outEdges[cur] || [];
            // Sort edges by ordinal of target for determinism.
            var sortedEdges = edges.slice().sort(function (a, b) {
                return (ordinalByKey[a] !== undefined ? ordinalByKey[a] : 9999) -
                       (ordinalByKey[b] !== undefined ? ordinalByKey[b] : 9999);
            });
            for (var ei = 0; ei < sortedEdges.length; ei++) {
                var next = sortedEdges[ei];
                if (nodeByKey[next] && rankOf[next] === undefined) {
                    rankOf[next] = rankOf[cur] + 1;
                    queue.push(next);
                }
            }
        }

        // Assign disconnected nodes after BFS frontier (ordinal order).
        var maxRank = 0;
        var keys = Object.keys(rankOf);
        for (var ri = 0; ri < keys.length; ri++) {
            if (rankOf[keys[ri]] > maxRank) { maxRank = rankOf[keys[ri]]; }
        }
        var allKeys = Object.keys(nodeByKey);
        allKeys.sort(function (a, b) {
            return (ordinalByKey[a] || 0) - (ordinalByKey[b] || 0);
        });
        var disconnectRank = maxRank + 1;
        for (var di = 0; di < allKeys.length; di++) {
            var dk = allKeys[di];
            if (rankOf[dk] === undefined) {
                rankOf[dk] = disconnectRank++;
            }
        }

        // Group keys by rank; within each rank sort by ordinal then alphabetically.
        var rankGroups = {};
        var allRankedKeys = Object.keys(rankOf);
        for (var gi = 0; gi < allRankedKeys.length; gi++) {
            var gk = allRankedKeys[gi];
            var gr = rankOf[gk];
            if (!rankGroups[gr]) { rankGroups[gr] = []; }
            rankGroups[gr].push(gk);
        }
        var rankNums = Object.keys(rankGroups);
        rankNums.sort(function (a, b) { return Number(a) - Number(b); });
        for (var rni = 0; rni < rankNums.length; rni++) {
            rankGroups[rankNums[rni]].sort(function (a, b) {
                var oa = ordinalByKey[a] !== undefined ? ordinalByKey[a] : 9999;
                var ob = ordinalByKey[b] !== undefined ? ordinalByKey[b] : 9999;
                if (oa !== ob) { return oa - ob; }
                return a < b ? -1 : (a > b ? 1 : 0);
            });
        }

        // Assign pixel coordinates.
        var layout = {};
        for (var lri = 0; lri < rankNums.length; lri++) {
            var lRank  = Number(rankNums[lri]);
            var lGroup = rankGroups[rankNums[lri]];
            var lX     = PADDING + lRank * (NODE_W + RANK_GAP);
            for (var lni = 0; lni < lGroup.length; lni++) {
                var lKey  = lGroup[lni];
                var lY    = PADDING + lni * (NODE_H + NODE_V_GAP);
                var nKind = _strVal((nodeByKey[lKey] || {})['kind'] || (nodeByKey[lKey] || {})['nodeKind']);
                var isGate = (nKind === NK_CONDITION || nKind === NK_PARALLEL || nKind === NK_INCLUSIVE);
                layout[lKey] = {
                    x:        lX,
                    y:        lY,
                    w:        isGate ? DIAMOND * 2 : NODE_W,
                    h:        isGate ? DIAMOND * 2 : NODE_H,
                    rank:     lRank,
                    rankIdx:  lni,
                    isGate:   isGate,
                    kind:     nKind
                };
            }
        }
        return layout;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. SVG renderer — createElementNS + textContent only; no foreignObject;
    //    all numeric attributes composed from numbers, never from data strings.
    //
    //    Produces a self-contained <svg> element ready to append to the DOM.
    //    Returns null when the graph has no nodes.
    // ─────────────────────────────────────────────────────────────────────────

    function renderSvg(graphTree) {
        if (!graphTree || typeof graphTree !== 'object') { return null; }

        var nodes       = Array.isArray(graphTree['nodes'])       ? graphTree['nodes']       : [];
        var transitions = Array.isArray(graphTree['transitions']) ? graphTree['transitions'] : [];

        if (nodes.length === 0) {
            // Return placeholder.
            var placeholder = document.createElementNS(SVG_NS, 'svg');
            _setAttrs(placeholder, { width: 200, height: 60 });
            var txt = document.createElementNS(SVG_NS, 'text');
            _setAttrs(txt, { x: 20, y: 35, fill: '#999', 'font-size': 14 });
            txt.textContent = '（无节点）';
            placeholder.appendChild(txt);
            return placeholder;
        }

        var layout = _computeLayout(nodes, transitions);

        // Compute SVG canvas size.
        var maxX = 0;
        var maxY = 0;
        var lKeys = Object.keys(layout);
        for (var lki = 0; lki < lKeys.length; lki++) {
            var lb = layout[lKeys[lki]];
            var rx = lb.x + lb.w + PADDING;
            var ry = lb.y + lb.h + PADDING;
            if (rx > maxX) { maxX = rx; }
            if (ry > maxY) { maxY = ry; }
        }

        var svgEl = document.createElementNS(SVG_NS, 'svg');
        _setAttrs(svgEl, {
            width:   maxX,
            height:  maxY,
            viewBox: '0 0 ' + maxX + ' ' + maxY
        });
        svgEl.style.display    = 'block';
        svgEl.style.background = '#fafafa';
        svgEl.style.border     = '1px solid #e6e6e6';

        // ── Defs: arrowhead marker ──
        var defs   = document.createElementNS(SVG_NS, 'defs');
        var marker = document.createElementNS(SVG_NS, 'marker');
        _setAttrs(marker, {
            id:           'wfd-arrow',
            markerWidth:  8,
            markerHeight: 8,
            refX:         6,
            refY:         3,
            orient:       'auto',
            markerUnits:  'strokeWidth'
        });
        var arrowPath = document.createElementNS(SVG_NS, 'path');
        // d attribute is purely numeric coords — no data involved.
        arrowPath.setAttribute('d', 'M0,0 L0,6 L8,3 z');
        arrowPath.setAttribute('fill', '#888');
        marker.appendChild(arrowPath);
        defs.appendChild(marker);
        svgEl.appendChild(defs);

        // ── Edges (drawn behind nodes) ──
        var edgeGroup = document.createElementNS(SVG_NS, 'g');
        edgeGroup.setAttribute('class', 'wfd-edges');
        for (var ti = 0; ti < transitions.length; ti++) {
            var tr     = transitions[ti];
            var fromK  = _strVal(tr['from']);
            var toK    = _strVal(tr['to']);
            var fromLb = layout[fromK];
            var toLb   = layout[toK];
            if (!fromLb || !toLb) { continue; }

            var x1 = fromLb.x + fromLb.w;
            var y1 = fromLb.y + Math.round(fromLb.h / 2);
            var x2 = toLb.x;
            var y2 = toLb.y + Math.round(toLb.h / 2);

            var line = document.createElementNS(SVG_NS, 'line');
            _setAttrs(line, {
                x1: x1, y1: y1,
                x2: x2, y2: y2,
                stroke:             '#888',
                'stroke-width':     1.5,
                'marker-end':       'url(#wfd-arrow)'
            });
            edgeGroup.appendChild(line);
        }
        svgEl.appendChild(edgeGroup);

        // ── Nodes ──
        var nodeGroup = document.createElementNS(SVG_NS, 'g');
        nodeGroup.setAttribute('class', 'wfd-nodes');
        for (var ni = 0; ni < nodes.length; ni++) {
            var node    = nodes[ni];
            var nKey    = _strVal(node['nodeKey'] || node['key']);
            var nKind   = _strVal(node['kind']    || node['nodeKind']);
            var nName   = _strVal(node['name']    || '');
            var lb      = layout[nKey];
            if (!lb) { continue; }

            var g = document.createElementNS(SVG_NS, 'g');
            g.setAttribute('class', 'wfd-node');

            if (lb.isGate) {
                // Diamond shape for gateways.
                _appendDiamond(g, lb, nKind);
            } else if (nKind === NK_START || nKind === NK_END) {
                // Rounded rect (circle-like) for Start/End.
                _appendRoundedRect(g, lb, nKind);
            } else {
                // Standard rectangle for Approval, Cc, Ack, Join.
                _appendRect(g, lb, nKind);
            }

            // Node label (name) — always textContent, never innerHTML.
            var labelTxt = nName || nKey || KIND_LABELS[nKind] || nKind;
            _appendNodeLabel(g, lb, labelTxt);

            // Kind badge (small).
            _appendKindBadge(g, lb, nKind);

            nodeGroup.appendChild(g);
        }
        svgEl.appendChild(nodeGroup);

        return svgEl;
    }

    // ─── Shape helpers (numeric attrs only) ───────────────────────────────────

    function _appendRect(g, lb, nKind) {
        var fill   = _nodeFill(nKind);
        var stroke = _nodeStroke(nKind);
        var rect   = document.createElementNS(SVG_NS, 'rect');
        _setAttrs(rect, {
            x:      lb.x,
            y:      lb.y,
            width:  lb.w,
            height: lb.h,
            rx:     4,
            ry:     4,
            fill:   fill,
            stroke: stroke,
            'stroke-width': 1.5
        });
        g.appendChild(rect);
    }

    function _appendRoundedRect(g, lb, nKind) {
        var fill   = _nodeFill(nKind);
        var stroke = _nodeStroke(nKind);
        var r      = Math.round(lb.h / 2);
        var rect   = document.createElementNS(SVG_NS, 'rect');
        _setAttrs(rect, {
            x:      lb.x,
            y:      lb.y,
            width:  lb.w,
            height: lb.h,
            rx:     r,
            ry:     r,
            fill:   fill,
            stroke: stroke,
            'stroke-width': 1.5
        });
        g.appendChild(rect);
    }

    function _appendDiamond(g, lb, nKind) {
        // Diamond: composed of 4 numeric coords only (no user data in path).
        var cx     = lb.x + Math.round(lb.w / 2);
        var cy     = lb.y + Math.round(lb.h / 2);
        var hw     = Math.round(lb.w / 2);
        var hh     = Math.round(lb.h / 2);
        var fill   = _nodeFill(nKind);
        var stroke = _nodeStroke(nKind);
        // Build path d from numbers only.
        var d = 'M' + cx + ',' + (cy - hh) +
                ' L' + (cx + hw) + ',' + cy +
                ' L' + cx + ',' + (cy + hh) +
                ' L' + (cx - hw) + ',' + cy + ' Z';
        var poly = document.createElementNS(SVG_NS, 'path');
        poly.setAttribute('d', d);
        poly.setAttribute('fill', fill);
        poly.setAttribute('stroke', stroke);
        poly.setAttribute('stroke-width', '1.5');
        g.appendChild(poly);
    }

    function _appendNodeLabel(g, lb, labelText) {
        // Truncate label to fit — all numeric composition.
        var cx     = lb.x + Math.round(lb.w / 2);
        var cy     = lb.y + Math.round(lb.h / 2) + 5;
        var txt    = document.createElementNS(SVG_NS, 'text');
        _setAttrs(txt, {
            x:              cx,
            y:              cy,
            'text-anchor':  'middle',
            fill:           '#333',
            'font-size':    12,
            'font-family':  'sans-serif'
        });
        // textContent only — never innerHTML (T-DSN-9).
        // Truncate if too long (> 10 chars).
        var display = labelText.length > 10 ? labelText.slice(0, 9) + '…' : labelText;
        txt.textContent = display;
        g.appendChild(txt);
    }

    function _appendKindBadge(g, lb, nKind) {
        // Small kind label below/inside node.
        var label = KIND_LABELS[nKind];
        if (!label) { return; }
        var cx  = lb.x + Math.round(lb.w / 2);
        var by  = lb.y + lb.h - 4;
        var txt = document.createElementNS(SVG_NS, 'text');
        _setAttrs(txt, {
            x:             cx,
            y:             by,
            'text-anchor': 'middle',
            fill:          '#888',
            'font-size':   9,
            'font-family': 'sans-serif'
        });
        txt.textContent = label;
        g.appendChild(txt);
    }

    function _nodeFill(kind) {
        switch (kind) {
            case NK_START:     return '#d9f7be';
            case NK_END:       return '#ffccc7';
            case NK_APPROVAL:  return '#e6f7ff';
            case NK_CONDITION: return '#fffbe6';
            case NK_CC:        return '#f9f0ff';
            case NK_ACK:       return '#e6fffb';
            case NK_JOIN:      return '#fff7e6';
            case NK_PARALLEL:  return '#f0f5ff';
            case NK_INCLUSIVE: return '#fcffe6';
            default:           return '#f5f5f5';
        }
    }

    function _nodeStroke(kind) {
        switch (kind) {
            case NK_START:     return '#52c41a';
            case NK_END:       return '#ff4d4f';
            case NK_APPROVAL:  return '#1890ff';
            case NK_CONDITION: return '#faad14';
            case NK_CC:        return '#722ed1';
            case NK_ACK:       return '#13c2c2';
            case NK_JOIN:      return '#fa8c16';
            case NK_PARALLEL:  return '#2f54eb';
            case NK_INCLUSIVE: return '#a0d911';
            default:           return '#bfbfbf';
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. SVG view panel — mounts into #wfd-svg-view
    //    Called by the shell when the user switches to the SVG tab.
    //    Accepts a raw graph JSON string or a parsed tree.
    // ─────────────────────────────────────────────────────────────────────────

    var SvgView = (function () {
        var _container = null;

        function init(containerEl) {
            _container = containerEl;
        }

        function render(graphTreeOrJson) {
            if (!_container) { return; }
            // Clear.
            while (_container.firstChild) {
                _container.removeChild(_container.firstChild);
            }

            var tree = graphTreeOrJson;
            if (typeof tree === 'string') {
                try {
                    // Use WtmDesignerCore codec if available (preserves RawNum); fall back to JSON.parse.
                    var Core = (typeof window !== 'undefined' && window.WtmDesignerCore) ?
                        window.WtmDesignerCore.WtmJsonRaw : null;
                    tree = Core ? Core.parse(tree) : JSON.parse(tree);
                } catch (e) {
                    var errTxt = document.createElement('p');
                    errTxt.style.color = '#ff4d4f';
                    errTxt.textContent = 'JSON 解析错误: ' + e.message;
                    _container.appendChild(errTxt);
                    return;
                }
            }

            var svgEl = renderSvg(tree);
            if (svgEl) {
                _container.appendChild(svgEl);
            } else {
                var empty = document.createElement('p');
                empty.style.color = '#999';
                empty.textContent = '（无法渲染图形）';
                _container.appendChild(empty);
            }
        }

        function clear() {
            if (_container) {
                while (_container.firstChild) {
                    _container.removeChild(_container.firstChild);
                }
            }
        }

        return { init: init, render: render, clear: clear };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Version history drawer
    //    Renders a version list panel inside a given container element.
    //    Each row shows: VersionNo, ContentHash prefix, SchemaVersion,
    //    PublishedAt, PublishedBy, IsCurrent.
    //    Includes a "载入为草稿" (load-as-draft) action.
    // ─────────────────────────────────────────────────────────────────────────

    var VersionHistoryPanel = (function () {

        // Build a version-history table from an array of version DTOs.
        // versionDtos: [{versionNo, contentHash, schemaVersion, publishedAt, publishedBy, isCurrent}]
        // onLoadDraft: function(versionId) — callback when user clicks 载入为草稿
        // Returns a DOM element (never innerHTML).
        function build(versionDtos, onLoadDraft) {
            var wrapper = document.createElement('div');
            wrapper.style.maxWidth  = '700px';

            if (!Array.isArray(versionDtos) || versionDtos.length === 0) {
                var empty = document.createElement('p');
                empty.style.color = '#999';
                empty.textContent = '暂无发布版本';
                wrapper.appendChild(empty);
                return wrapper;
            }

            var table = document.createElement('table');
            table.style.width       = '100%';
            table.style.borderCollapse = 'collapse';
            table.style.fontSize    = '13px';

            // Header row — all static strings (no user data).
            var thead = document.createElement('thead');
            var headRow = document.createElement('tr');
            var headers = ['版本', 'Hash前缀', 'Schema版本', '发布时间', '发布人', '状态', '操作'];
            for (var hi = 0; hi < headers.length; hi++) {
                var th = document.createElement('th');
                th.style.padding    = '6px 8px';
                th.style.borderBottom = '2px solid #e6e6e6';
                th.style.textAlign  = 'left';
                th.style.background = '#fafafa';
                th.textContent = headers[hi];
                headRow.appendChild(th);
            }
            thead.appendChild(headRow);
            table.appendChild(thead);

            var tbody = document.createElement('tbody');
            for (var vi = 0; vi < versionDtos.length; vi++) {
                var v   = versionDtos[vi];
                var tr  = document.createElement('tr');
                tr.style.borderBottom = '1px solid #f0f0f0';
                if (v.isCurrent) {
                    tr.style.background = '#f6ffed';
                }

                // Version No
                _appendTd(tr, 'v' + (v.versionNo || '?'));
                // Hash prefix (first 8 chars) — displayed as text, never trusted as code
                var hash = typeof v.contentHash === 'string' ? v.contentHash.slice(0, 8) : '—';
                _appendTd(tr, hash);
                // Schema version
                _appendTd(tr, String(v.schemaVersion || '1'));
                // Published at
                _appendTd(tr, _formatTs(v.publishedAt));
                // Published by — user-controlled value: textContent only (T-DSN-9)
                _appendTd(tr, typeof v.publishedBy === 'string' ? v.publishedBy : '—');
                // Status
                var statusTd = document.createElement('td');
                statusTd.style.padding = '6px 8px';
                var badge = document.createElement('span');
                badge.style.padding    = '2px 6px';
                badge.style.borderRadius = '3px';
                badge.style.fontSize   = '11px';
                if (v.isCurrent) {
                    badge.style.background = '#f6ffed';
                    badge.style.color      = '#52c41a';
                    badge.style.border     = '1px solid #b7eb8f';
                    badge.textContent = '当前版本';
                } else {
                    badge.style.background = '#f5f5f5';
                    badge.style.color      = '#666';
                    badge.style.border     = '1px solid #d9d9d9';
                    badge.textContent = '历史';
                }
                statusTd.appendChild(badge);
                tr.appendChild(statusTd);

                // Action — 载入为草稿
                var actionTd = document.createElement('td');
                actionTd.style.padding = '6px 8px';
                var btn = document.createElement('button');
                btn.style.fontSize     = '12px';
                btn.style.cursor       = 'pointer';
                btn.style.padding      = '2px 8px';
                btn.style.border       = '1px solid #d9d9d9';
                btn.style.borderRadius = '3px';
                btn.style.background   = '#fff';
                btn.textContent = '载入为草稿';
                // Closure: capture versionId.
                (function (vId) {
                    btn.addEventListener('click', function () {
                        if (typeof onLoadDraft === 'function') {
                            onLoadDraft(vId);
                        }
                    });
                }(v.versionId));
                actionTd.appendChild(btn);
                tr.appendChild(actionTd);

                tbody.appendChild(tr);
            }
            table.appendChild(tbody);
            wrapper.appendChild(table);
            return wrapper;
        }

        function _appendTd(tr, text) {
            var td = document.createElement('td');
            td.style.padding = '6px 8px';
            td.textContent   = String(text); // textContent only (T-DSN-9)
            tr.appendChild(td);
        }

        function _formatTs(ts) {
            if (!ts) { return '—'; }
            try {
                // Parse as Date; output is purely formatted numbers — no user data flows here.
                var d = new Date(ts);
                if (isNaN(d.getTime())) { return String(ts); }
                var Y  = d.getFullYear();
                var Mo = _pad(d.getMonth() + 1);
                var D  = _pad(d.getDate());
                var H  = _pad(d.getHours());
                var Mi = _pad(d.getMinutes());
                return Y + '-' + Mo + '-' + D + ' ' + H + ':' + Mi;
            } catch (e) {
                return '—';
            }
        }

        function _pad(n) { return n < 10 ? '0' + n : '' + n; }

        return { build: build };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 5. Publish confirm/conflict flows
    //    Utilities used by the shell to surface outcome toasts.
    //
    //    IMPORTANT: layer.msg / layer.alert NEVER receive server-derived strings
    //    (T-DSN-9).  All human-readable messages here are hardcoded zh-CN literals.
    //    Server error codes are matched by value and mapped to literals.
    // ─────────────────────────────────────────────────────────────────────────

    var PublishFlow = (function () {

        // Map closed server outcome codes to zh-CN literals (T-DSN-9).
        var OUTCOME_MSGS = {
            'Published':           '已发布新版本',
            'IdempotentNoOp':      '内容未变化，未产生新版本',
            'BaseVersionChanged':  '该流程在你编辑期间已被他人发布新版本，请重新加载',
            'ValidationFailed':    '发布失败：流程图验证不通过',
            'SchemaVersionUnsupported': '发布失败：不支持的 schema 版本',
            'DefinitionNotFound':  '发布失败：流程定义不存在'
        };

        // Show publish outcome toast (layer.msg with fixed literal — T-DSN-9).
        function showOutcomeToast(outcomeCode) {
            var msg = OUTCOME_MSGS[outcomeCode] || '发布操作已完成';
            if (typeof window !== 'undefined' && window.layui && window.layui.layer) {
                window.layui.layer.msg(msg);
            }
        }

        // Build confirm dialog content for publish.
        // confirmData: { currentVersionNo, draftLastSavedBy, currentActor }
        // Returns a DOM element (no innerHTML).
        function buildConfirmPanel(confirmData) {
            var panel = document.createElement('div');
            panel.style.padding = '16px';

            var heading = document.createElement('p');
            heading.style.fontWeight = '600';
            heading.style.marginBottom = '8px';
            var fromVer = confirmData && confirmData.currentVersionNo != null ?
                'v' + confirmData.currentVersionNo : '(新)';
            heading.textContent = '确认发布 ' + fromVer + ' → 下一版本？';
            panel.appendChild(heading);

            // Attribution warning — show when draft was last saved by someone other than actor.
            if (confirmData &&
                confirmData.draftLastSavedBy &&
                confirmData.currentActor &&
                confirmData.draftLastSavedBy !== confirmData.currentActor) {
                var warn = document.createElement('p');
                warn.style.color       = '#fa8c16';
                warn.style.fontSize    = '12px';
                warn.style.marginTop   = '4px';
                warn.style.marginBottom = '0';
                // draftLastSavedBy is user-authored content: textContent only (T-DSN-9).
                var by = document.createElement('span');
                by.textContent = '草稿最后由 ';
                var who = document.createElement('strong');
                who.textContent = confirmData.draftLastSavedBy;
                var suffix = document.createTextNode(' 编辑，与当前操作人不同');
                warn.appendChild(by);
                warn.appendChild(who);
                warn.appendChild(suffix);
                panel.appendChild(warn);
            }

            return panel;
        }

        // Build conflict toast content for 409 BaseVersionChanged.
        // Returns a DOM element (no innerHTML).
        function buildConflictPanel() {
            var panel = document.createElement('div');
            panel.style.padding = '12px';

            var msg = document.createElement('p');
            msg.style.color    = '#ff4d4f';
            msg.style.margin   = '0 0 8px 0';
            msg.textContent    = '该流程在你编辑期间已被他人发布新版本。';
            panel.appendChild(msg);

            var hint = document.createElement('p');
            hint.style.color   = '#666';
            hint.style.margin  = '0';
            hint.style.fontSize = '12px';
            hint.textContent   = '请重新加载最新版本，在源码模式中重新应用你的修改。';
            panel.appendChild(hint);

            return panel;
        }

        return {
            showOutcomeToast:  showOutcomeToast,
            buildConfirmPanel: buildConfirmPanel,
            buildConflictPanel: buildConflictPanel,
            OUTCOME_MSGS:      OUTCOME_MSGS
        };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Version list integration — UI shell helpers for the definition list panel
    //    and per-definition version-history drawer (T-DSN-18: versioning UX).
    // ─────────────────────────────────────────────────────────────────────────

    var VersionListUi = (function () {

        // Build definition list rows from paged head DTOs.
        // defs: [{code, name, category, isEnabled, currentVersionNo, hasDraft}]
        // onSelect: function(code) — open definition for editing
        // Returns a DOM element (table).
        function buildDefinitionTable(defs, onSelect) {
            var wrapper = document.createElement('div');

            if (!Array.isArray(defs) || defs.length === 0) {
                var empty = document.createElement('p');
                empty.style.color = '#999';
                empty.textContent = '暂无流程定义';
                wrapper.appendChild(empty);
                return wrapper;
            }

            var table = document.createElement('table');
            table.style.width          = '100%';
            table.style.borderCollapse = 'collapse';
            table.style.fontSize       = '13px';

            var thead = document.createElement('thead');
            var headRow = document.createElement('tr');
            var headers = ['流程代码', '流程名称', '类别', '启用', '当前版本', '草稿', '操作'];
            for (var hi = 0; hi < headers.length; hi++) {
                var th = document.createElement('th');
                th.style.padding      = '6px 8px';
                th.style.borderBottom = '2px solid #e6e6e6';
                th.style.textAlign    = 'left';
                th.style.background   = '#fafafa';
                th.textContent = headers[hi];
                headRow.appendChild(th);
            }
            thead.appendChild(headRow);
            table.appendChild(thead);

            var tbody = document.createElement('tbody');
            for (var di = 0; di < defs.length; di++) {
                var def = defs[di];
                var tr  = document.createElement('tr');
                tr.style.borderBottom = '1px solid #f0f0f0';

                // Code — user-authored: textContent only (T-DSN-9)
                _appendTd(tr, typeof def.code === 'string' ? def.code : '—');
                // Name
                _appendTd(tr, typeof def.name === 'string' ? def.name : '—');
                // Category
                _appendTd(tr, typeof def.category === 'string' ? def.category : '—');
                // IsEnabled
                _appendTd(tr, def.isEnabled ? '是' : '否');
                // CurrentVersionNo
                _appendTd(tr, def.currentVersionNo != null ? 'v' + def.currentVersionNo : '—');
                // HasDraft
                _appendTd(tr, def.hasDraft ? '有草稿' : '—');

                // Action
                var actionTd = document.createElement('td');
                actionTd.style.padding = '6px 8px';
                var btn = document.createElement('button');
                btn.style.fontSize     = '12px';
                btn.style.cursor       = 'pointer';
                btn.style.padding      = '2px 8px';
                btn.style.border       = '1px solid #1890ff';
                btn.style.borderRadius = '3px';
                btn.style.background   = '#e6f7ff';
                btn.style.color        = '#1890ff';
                btn.textContent = '打开设计器';
                (function (defCode) {
                    btn.addEventListener('click', function () {
                        if (typeof onSelect === 'function') {
                            onSelect(defCode);
                        }
                    });
                }(def.code));
                actionTd.appendChild(btn);
                tr.appendChild(actionTd);

                tbody.appendChild(tr);
            }
            table.appendChild(tbody);
            wrapper.appendChild(table);
            return wrapper;
        }

        function _appendTd(tr, text) {
            var td = document.createElement('td');
            td.style.padding = '6px 8px';
            td.textContent   = String(text);
            tr.appendChild(td);
        }

        return { buildDefinitionTable: buildDefinitionTable };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 7. Utility helpers (shared, pure)
    // ─────────────────────────────────────────────────────────────────────────

    function _strVal(v) {
        if (v === null || v === undefined) { return ''; }
        if (typeof v === 'string') { return v; }
        // RawNum or object with .raw
        if (typeof v === 'object' && typeof v.raw === 'string') { return v.raw; }
        return String(v);
    }

    function _setAttrs(el, attrs) {
        var keys = Object.keys(attrs);
        for (var i = 0; i < keys.length; i++) {
            el.setAttribute(keys[i], String(attrs[keys[i]]));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. Expose window.WtmDesignerView
    // ─────────────────────────────────────────────────────────────────────────

    if (typeof window !== 'undefined') {
        window.WtmDesignerView = {
            // Layout engine (pure function; deterministic; exported for jest tests)
            computeLayout:       _computeLayout,

            // SVG renderer
            renderSvg:           renderSvg,

            // SVG panel (DOM-aware)
            SvgView:             SvgView,

            // Version history panel builder
            VersionHistoryPanel: VersionHistoryPanel,

            // Publish flow helpers
            PublishFlow:         PublishFlow,

            // Version list UI
            VersionListUi:       VersionListUi,

            // Version tag for diagnostics
            version:             'WF-21.7'
        };
    }

}());
