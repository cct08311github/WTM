// framework_workflow_designer_forms.js — WF-21.6
// Low-code workflow designer: per-NodeKind property panels, transitions table,
// fieldWhitelist editor, Condition merge-sync, client lint badges.
//
// Security rules (T-DSN-9):
//   - eval-free: no eval, no new Function, no Function constructor
//   - DOM: all mutations via createElement/textContent/addEventListener
//   - layer.open receives DOM node elements, never HTML strings
//   - layer.msg/alert never receive server-derived strings
//   - No inline event handlers in generated markup
//   - No CDN/network assets
//
// Design: IIFE, exposes window.WtmDesignerForms only.
// Dependencies: window.WtmDesignerCore (core module loaded first),
//               layui / $ available on window (consumer wwwroot).
// zh-CN UI strings inline; American-English code/comments.
//
// Critical invariant (design §2.4-3 / §0 verdict):
//   Form edits MERGE into existing node/transition objects.
//   Never rebuild/replace an entire node or transition object —
//   unknown fields and intact condition payloads are preserved verbatim.

'use strict';

(function () {

    // ─────────────────────────────────────────────────────────────────────────
    // Constants — NodeKind / enum values matching WorkflowGraphSchema.cs
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

    var NODE_KINDS = [NK_START, NK_END, NK_APPROVAL, NK_CONDITION, NK_CC, NK_ACK,
                      NK_JOIN, NK_PARALLEL, NK_INCLUSIVE];

    var APPROVE_MODES    = ['Sequential', 'All', 'Any'];
    var REJECT_GATES     = ['Immediate', 'AfterAll'];
    var REJECT_POLICIES  = ['TerminateInstance', 'ReturnToPrev', 'ReturnToNode', 'ReturnToInitiator'];
    var TIMER_ACTIONS    = ['Remind', 'AutoApprove', 'AutoReject', 'Escalate'];
    var CC_TRIGGERS      = ['OnSubmit', 'OnNode', 'OnComplete'];
    var ACK_MODES        = ['All', 'Any', 'Quorum'];
    var APPROVER_TYPES   = ['User', 'Role', 'ManagerChain', 'Initiator'];
    var FILTER_OPERATORS = ['Eq', 'NotEq', 'Gt', 'Gte', 'Lt', 'Lte',
                            'Contains', 'NotContains', 'In', 'NotIn'];
    var CLR_TYPES = [
        'System.String', 'System.Int32', 'System.Int64',
        'System.Decimal', 'System.Double', 'System.Boolean',
        'System.DateTime', 'System.Guid'
    ];

    var NODEKEY_RE = /^[A-Za-z0-9_\-\.]{1,64}$/;

    // Node kind human labels (zh-CN)
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

    // Approve mode labels
    var APPROVE_LABELS = {
        'Sequential': '串签（顺序审批）',
        'All':        '会签（全员审批）',
        'Any':        '或签（任一审批）'
    };

    // Reject gate labels
    var REJECT_GATE_LABELS = {
        'Immediate': '立即结束',
        'AfterAll':  '等所有人操作后'
    };

    // Reject policy labels
    var REJECT_POLICY_LABELS = {
        'TerminateInstance':  '终止流程',
        'ReturnToPrev':       '退回上一节点',
        'ReturnToNode':       '退回指定节点',
        'ReturnToInitiator':  '退回发起人'
    };

    // Timer action labels
    var TIMER_ACTION_LABELS = {
        'Remind':      '提醒',
        'AutoApprove': '自动通过',
        'AutoReject':  '自动拒绝',
        'Escalate':    '升级处理'
    };

    // CC trigger labels
    var CC_TRIGGER_LABELS = {
        'OnSubmit':   '提交时',
        'OnNode':     '节点激活时',
        'OnComplete': '完成时'
    };

    // Ack mode labels
    var ACK_MODE_LABELS = {
        'All':    '全员确认',
        'Any':    '任一确认',
        'Quorum': '定额确认'
    };

    // Approver type labels
    var APPROVER_TYPE_LABELS = {
        'User':         '指定用户',
        'Role':         '角色',
        'ManagerChain': '上级链',
        'Initiator':    '发起人（Wave-4）'
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 1. DOM helpers — all content via createElement + textContent
    // ─────────────────────────────────────────────────────────────────────────

    function el(tag, opts) {
        // opts: { cls, text, attrs, style }
        var e = document.createElement(tag);
        if (opts) {
            if (opts.cls)   { e.className = opts.cls; }
            if (opts.text !== undefined) { e.textContent = opts.text; }
            if (opts.attrs) {
                Object.keys(opts.attrs).forEach(function (k) {
                    e.setAttribute(k, opts.attrs[k]);
                });
            }
            if (opts.style) {
                Object.keys(opts.style).forEach(function (k) {
                    e.style[k] = opts.style[k];
                });
            }
        }
        return e;
    }

    function append(parent /*, children... */) {
        for (var i = 1; i < arguments.length; i++) {
            parent.appendChild(arguments[i]);
        }
        return parent;
    }

    // Build a label + input row for form fields
    function formRow(labelText, inputEl) {
        var row = el('div', { cls: 'layui-form-item' });
        var lbl = el('label', { cls: 'layui-form-label', text: labelText });
        var div = el('div', { cls: 'layui-input-block' });
        div.appendChild(inputEl);
        row.appendChild(lbl);
        row.appendChild(div);
        return row;
    }

    // Build a <select> element
    function buildSelect(id, options, selectedValue) {
        // options: array of { value, label }
        var sel = el('select', { cls: 'layui-input', attrs: { id: id } });
        options.forEach(function (opt) {
            var o = el('option', { text: opt.label, attrs: { value: opt.value } });
            if (opt.value === selectedValue) {
                o.setAttribute('selected', 'selected');
            }
            sel.appendChild(o);
        });
        return sel;
    }

    // Build a text input
    function buildInput(id, value, placeholder, type) {
        return el('input', {
            cls: 'layui-input',
            attrs: {
                id:          id || '',
                type:        type || 'text',
                value:       value !== null && value !== undefined ? String(value) : '',
                placeholder: placeholder || ''
            }
        });
    }

    // Build a checkbox
    function buildCheckbox(id, checked, labelText) {
        var wrap = el('span');
        var inp  = el('input', { attrs: {
            type: 'checkbox', id: id || '',
            title: labelText || ''
        }});
        if (checked) { inp.setAttribute('checked', 'checked'); }
        var lbl  = el('label', { text: ' ' + (labelText || ''), attrs: { 'for': id || '' } });
        wrap.appendChild(inp);
        wrap.appendChild(lbl);
        return { wrap: wrap, input: inp };
    }

    // NodeKey validation feedback
    function nodeKeyFeedback(input, feedbackEl) {
        var val = input.value.trim();
        if (!val) {
            feedbackEl.textContent = '节点键不能为空';
            feedbackEl.style.color = 'red';
        } else if (!NODEKEY_RE.test(val)) {
            feedbackEl.textContent = '节点键只允许字母、数字、下划线、横线、点（最长64位）';
            feedbackEl.style.color = 'red';
        } else {
            feedbackEl.textContent = '';
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Merge helpers — critical: these MERGE into existing objects/arrays
    //    (never rebuild/replace). Unknown fields are preserved.
    // ─────────────────────────────────────────────────────────────────────────

    // Merge scalar fields into a node object from a form values map.
    // Only keys present in the map are written; others left untouched.
    function mergeScalars(nodeObj, fieldMap) {
        Object.keys(fieldMap).forEach(function (k) {
            var v = fieldMap[k];
            // Only set defined non-null values; null means "remove the key"
            if (v === null) {
                delete nodeObj[k];
            } else {
                nodeObj[k] = v;
            }
        });
    }

    // Deep-merge an approver rule into a node.
    // Ensures nodeObj.approverRule exists (creates if absent); merges only
    // the fields present in ruleMap; unknown fields in the existing rule survive.
    function mergeApproverRule(nodeObj, ruleMap) {
        if (!nodeObj['approverRule'] || typeof nodeObj['approverRule'] !== 'object') {
            nodeObj['approverRule'] = {};
        }
        mergeScalars(nodeObj['approverRule'], ruleMap);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Per-NodeKind form panels
    //    Each builder returns { panelEl, collectValues }
    //    - panelEl: DOM element to pass to layer.open
    //    - collectValues(): returns { fieldMap, approverRule?, timeout?, cc?, ackQuorum? }
    //      caller merges these into the node using mergeScalars / mergeApproverRule
    // ─────────────────────────────────────────────────────────────────────────

    // ─── 3a. Start / End / Join panels (key + name only) ─────────────────────

    function buildSimplePanel(nodeObj) {
        // Used for Start, End, Join
        var panel = el('div', { cls: 'wtm-wf-panel', style: { minWidth: '400px', padding: '16px' } });

        var keyInput = buildInput('wf-node-key', nodeObj['nodeKey'] || '', '节点键（唯一标识）');
        var keyFeedback = el('span', { style: { fontSize: '12px' } });
        nodeKeyFeedback(keyInput, keyFeedback);
        keyInput.addEventListener('input', function () { nodeKeyFeedback(keyInput, keyFeedback); });

        var nameInput = buildInput('wf-node-name', nodeObj['name'] || '', '显示名称');

        append(panel,
            formRow('节点键', append(el('div'), keyInput, keyFeedback)),
            formRow('名称', nameInput)
        );

        function collectValues() {
            return {
                fieldMap: {
                    nodeKey: keyInput.value.trim(),
                    name:    nameInput.value.trim()
                }
            };
        }

        return { panelEl: panel, collectValues: collectValues };
    }

    // ─── 3b. Approval panel ───────────────────────────────────────────────────

    function buildApprovalPanel(nodeObj, allNodes) {
        var panel = el('div', { cls: 'wtm-wf-panel', style: { minWidth: '460px', padding: '16px' } });

        // --- nodeKey + name ---
        var keyInput = buildInput('wf-node-key', nodeObj['nodeKey'] || '', '节点键');
        var keyFeedback = el('span', { style: { fontSize: '12px' } });
        nodeKeyFeedback(keyInput, keyFeedback);
        keyInput.addEventListener('input', function () { nodeKeyFeedback(keyInput, keyFeedback); });

        var nameInput = buildInput('wf-node-name', nodeObj['name'] || '', '显示名称');

        // --- approveMode ---
        var approveOpts = APPROVE_MODES.map(function (m) {
            return { value: m, label: APPROVE_LABELS[m] || m };
        });
        var modeSelect = buildSelect('wf-approve-mode', approveOpts, nodeObj['approveMode'] || 'Sequential');

        // --- approvePercent (shown only for All mode) ---
        var curPct = nodeObj['approvePercent'];
        var pctInput = buildInput('wf-approve-pct',
            curPct !== undefined && curPct !== null ? String(curPct) : '',
            '例如 0.67（0<n≤1，空=全员）');
        var pctRow = formRow('审批比例', pctInput);

        function updatePctVisibility() {
            pctRow.style.display = modeSelect.value === 'All' ? '' : 'none';
        }
        updatePctVisibility();
        modeSelect.addEventListener('change', updatePctVisibility);

        // --- rejectGate (shown only for All mode) ---
        var gateOpts = REJECT_GATES.map(function (g) {
            return { value: g, label: REJECT_GATE_LABELS[g] || g };
        });
        var gateSelect = buildSelect('wf-reject-gate', gateOpts, nodeObj['rejectGate'] || 'Immediate');
        var gateRow = formRow('拒绝触发时机', gateSelect);

        function updateGateVisibility() {
            gateRow.style.display = modeSelect.value === 'All' ? '' : 'none';
        }
        updateGateVisibility();
        modeSelect.addEventListener('change', updateGateVisibility);

        // --- rejectPolicy ---
        var policyOpts = REJECT_POLICIES.map(function (p) {
            return { value: p, label: REJECT_POLICY_LABELS[p] || p };
        });
        var policySelect = buildSelect('wf-reject-policy', policyOpts, nodeObj['rejectPolicy'] || 'ReturnToInitiator');

        // --- ReturnToNode node picker ---
        var joinNodes = (allNodes || []).filter(function (n) {
            return n['kind'] === NK_APPROVAL || n['kind'] === NK_START;
        });
        var returnNodeOpts = [{ value: '', label: '-- 选择目标节点 --' }].concat(
            joinNodes.map(function (n) {
                return { value: n['nodeKey'] || '', label: (n['name'] || n['nodeKey'] || '') + ' (' + (n['nodeKey'] || '') + ')' };
            })
        );
        var returnNodeSelect = buildSelect('wf-return-node', returnNodeOpts,
            nodeObj['returnToNodeKey'] || '');
        var returnNodeRow = formRow('退回目标节点', returnNodeSelect);

        function updateReturnNodeVisibility() {
            returnNodeRow.style.display = policySelect.value === 'ReturnToNode' ? '' : 'none';
        }
        updateReturnNodeVisibility();
        policySelect.addEventListener('change', updateReturnNodeVisibility);

        // --- approverRule ---
        var existingRule = nodeObj['approverRule'] || {};
        var typeOpts = APPROVER_TYPES.map(function (t) {
            return { value: t, label: APPROVER_TYPE_LABELS[t] || t };
        });
        var ruleTypeSelect = buildSelect('wf-rule-type', typeOpts, existingRule['type'] || 'Role');

        var ruleValueInput = buildInput('wf-rule-value', existingRule['value'] || '', '角色代码或用户工号');
        var ruleValueRow = formRow('值', ruleValueInput);

        var maxLevelInput = buildInput('wf-rule-maxlevel',
            existingRule['maxLevel'] !== undefined && existingRule['maxLevel'] !== null ? String(existingRule['maxLevel']) : '',
            '最大层级（默认2）', 'number');
        var maxLevelRow = formRow('最大层级', maxLevelInput);

        function updateRuleFieldVisibility() {
            var t = ruleTypeSelect.value;
            ruleValueRow.style.display  = (t === 'User' || t === 'Role') ? '' : 'none';
            maxLevelRow.style.display   = (t === 'ManagerChain') ? '' : 'none';
        }
        updateRuleFieldVisibility();
        ruleTypeSelect.addEventListener('change', updateRuleFieldVisibility);

        // --- CC list (inline) ---
        var ccSection = el('div', { cls: 'wtm-wf-cc-section' });
        var ccTitle   = el('div', { text: '抄送（CC）', style: { fontWeight: 'bold', margin: '8px 0 4px' } });
        var ccList    = el('div', { cls: 'wtm-wf-cc-list' });
        var addCcBtn  = el('button', {
            cls: 'layui-btn layui-btn-xs',
            text: '+ 添加抄送',
            attrs: { type: 'button' }
        });

        var ccEntries = []; // track { triggerSel, ruleTypeSel, ruleValueInp, removeBtn, rowEl }

        var existingCc = nodeObj['cc'];
        if (Array.isArray(existingCc)) {
            existingCc.forEach(function (ccDef) {
                _addCcRow(ccDef);
            });
        }

        function _addCcRow(ccDef) {
            ccDef = ccDef || {};
            var rowEl = el('div', { cls: 'wtm-wf-cc-row', style: { display: 'flex', gap: '6px', marginBottom: '4px', alignItems: 'center' } });

            var trigOpts = CC_TRIGGERS.map(function (t) { return { value: t, label: CC_TRIGGER_LABELS[t] || t }; });
            var trigSel  = buildSelect('', trigOpts, (ccDef['trigger'] || 'OnSubmit'));
            trigSel.style.width = '120px';

            var ccRuleTypeOpts = APPROVER_TYPES.slice(0, 3).map(function (t) { return { value: t, label: APPROVER_TYPE_LABELS[t] || t }; });
            var ccRuleTypeSel  = buildSelect('', ccRuleTypeOpts, ((ccDef['rule'] && ccDef['rule']['type']) || 'Role'));
            ccRuleTypeSel.style.width = '100px';

            var ccRuleValInp = buildInput('', (ccDef['rule'] && ccDef['rule']['value']) || '', '角色/工号');
            ccRuleValInp.style.width = '120px';

            var removeBtn = el('button', {
                cls: 'layui-btn layui-btn-xs layui-btn-danger',
                text: '删除',
                attrs: { type: 'button' }
            });

            var entry = { triggerSel: trigSel, ruleTypeSel: ccRuleTypeSel, ruleValueInp: ccRuleValInp,
                          removeBtn: removeBtn, rowEl: rowEl };
            ccEntries.push(entry);

            removeBtn.addEventListener('click', function () {
                var idx = ccEntries.indexOf(entry);
                if (idx !== -1) { ccEntries.splice(idx, 1); }
                rowEl.parentNode && rowEl.parentNode.removeChild(rowEl);
            });

            append(rowEl, trigSel, ccRuleTypeSel, ccRuleValInp, removeBtn);
            ccList.appendChild(rowEl);
        }

        addCcBtn.addEventListener('click', function () { _addCcRow(null); });
        append(ccSection, ccTitle, ccList, addCcBtn);

        // --- Timeout sub-form ---
        var timeoutSection = _buildTimeoutSection(nodeObj['timeout'] || null);

        // --- Assemble ---
        append(panel,
            formRow('节点键', append(el('div'), keyInput, keyFeedback)),
            formRow('名称', nameInput),
            formRow('审批模式', modeSelect),
            pctRow,
            gateRow,
            formRow('拒绝策略', policySelect),
            returnNodeRow,
            el('hr'),
            el('div', { text: '审批人规则', style: { fontWeight: 'bold', margin: '8px 0 4px' } }),
            formRow('规则类型', ruleTypeSelect),
            ruleValueRow,
            maxLevelRow,
            el('hr'),
            ccSection,
            el('hr'),
            timeoutSection.el
        );

        function collectValues() {
            var pct = null;
            if (modeSelect.value === 'All') {
                var pctStr = pctInput.value.trim();
                if (pctStr) {
                    var pctNum = parseFloat(pctStr);
                    if (!isNaN(pctNum) && pctNum > 0 && pctNum <= 1) {
                        pct = pctNum;
                    }
                }
            }

            var maxLv = null;
            if (ruleTypeSelect.value === 'ManagerChain') {
                var lvStr = maxLevelInput.value.trim();
                if (lvStr) { maxLv = parseInt(lvStr, 10) || null; }
            }

            var ccArr = ccEntries.map(function (e) {
                return {
                    trigger: e.triggerSel.value,
                    rule: { type: e.ruleTypeSel.value, value: e.ruleValueInp.value.trim() || null }
                };
            });

            var ruleMap = {
                type: ruleTypeSelect.value
            };
            if (ruleTypeSelect.value === 'User' || ruleTypeSelect.value === 'Role') {
                ruleMap['value'] = ruleValueInput.value.trim() || null;
            } else {
                ruleMap['value'] = null;
            }
            if (ruleTypeSelect.value === 'ManagerChain') {
                ruleMap['maxLevel'] = maxLv;
            } else {
                ruleMap['maxLevel'] = null;
            }

            var fm = {
                nodeKey:      keyInput.value.trim(),
                name:         nameInput.value.trim(),
                approveMode:  modeSelect.value,
                rejectPolicy: policySelect.value
            };
            if (modeSelect.value === 'All') {
                fm['approvePercent'] = pct;
                fm['rejectGate']     = gateSelect.value;
            } else {
                fm['approvePercent'] = null;
                fm['rejectGate']     = null;
            }
            if (policySelect.value === 'ReturnToNode') {
                fm['returnToNodeKey'] = returnNodeSelect.value || null;
            } else {
                fm['returnToNodeKey'] = null;
            }

            return {
                fieldMap:     fm,
                approverRule: ruleMap,
                cc:           ccArr.length > 0 ? ccArr : null,
                timeout:      timeoutSection.collect()
            };
        }

        return { panelEl: panel, collectValues: collectValues };
    }

    // ─── 3c. Timeout sub-form builder ─────────────────────────────────────────

    function _buildTimeoutSection(existingTimeout) {
        var sec   = el('div');
        var title = el('div', { text: '超时设置', style: { fontWeight: 'bold', margin: '8px 0 4px' } });
        var body  = el('div');

        // Enable toggle
        var enabledCb = buildCheckbox('wf-timeout-enabled', !!existingTimeout, '启用超时');
        var enableRow = el('div', { style: { marginBottom: '8px' } });
        enableRow.appendChild(enabledCb.wrap);

        // Duration composer: days / hours / minutes
        var existDur = (existingTimeout && existingTimeout['duration']) || '';
        var durParsed = _parseIso8601Duration(existDur);

        var daysInput    = buildInput('wf-timeout-days',    durParsed.days || '',    '天', 'number');
        var hoursInput   = buildInput('wf-timeout-hours',   durParsed.hours || '',   '小时', 'number');
        var minutesInput = buildInput('wf-timeout-minutes', durParsed.minutes || '',  '分钟', 'number');
        daysInput.style.width = hoursInput.style.width = minutesInput.style.width = '70px';

        var durRow = el('div', { cls: 'layui-form-item' });
        var durLbl = el('label', { cls: 'layui-form-label', text: '时长' });
        var durBlock = el('div', { cls: 'layui-input-block' });
        var durWrap  = el('span', { style: { display: 'flex', gap: '4px', alignItems: 'center' } });
        append(durWrap,
            daysInput,    el('span', { text: '天' }),
            hoursInput,   el('span', { text: '小时' }),
            minutesInput, el('span', { text: '分钟' })
        );
        durBlock.appendChild(durWrap);
        append(durRow, durLbl, durBlock);

        // businessCalendar
        var bcCb = buildCheckbox('wf-timeout-bc',
            existingTimeout ? !!existingTimeout['businessCalendar'] : false,
            '仅工作日计时');
        var bcRow = el('div', { style: { marginBottom: '4px' } });
        bcRow.appendChild(bcCb.wrap);

        // action
        var actionOpts = TIMER_ACTIONS.map(function (a) { return { value: a, label: TIMER_ACTION_LABELS[a] || a }; });
        var actionSel  = buildSelect('wf-timeout-action', actionOpts,
            (existingTimeout && existingTimeout['action']) || 'Remind');

        // remindEveryHours (shown for Remind)
        var remindEveryInput = buildInput('wf-remind-every',
            (existingTimeout && existingTimeout['remindEveryHours']) ? String(existingTimeout['remindEveryHours']) : '',
            '每隔N小时再提醒', 'number');
        var remindEveryRow = formRow('提醒间隔（小时）', remindEveryInput);

        // maxReminders
        var maxRemindInput = buildInput('wf-max-remind',
            (existingTimeout && existingTimeout['maxReminders']) ? String(existingTimeout['maxReminders']) : '',
            '最多提醒次数', 'number');
        var maxRemindRow = formRow('最多提醒次数', maxRemindInput);

        // escalateTo (shown for Escalate)
        var escalateToInput = buildInput('wf-escalate-to',
            (existingTimeout && existingTimeout['escalateTo']) || '',
            '升级处理人工号（空=系统默认）');
        var escalateToRow = formRow('升级处理人', escalateToInput);

        function updateActionVisibility() {
            var act = actionSel.value;
            remindEveryRow.style.display = (act === 'Remind') ? '' : 'none';
            maxRemindRow.style.display   = (act === 'Remind') ? '' : 'none';
            escalateToRow.style.display  = (act === 'Escalate') ? '' : 'none';
        }
        updateActionVisibility();
        actionSel.addEventListener('change', updateActionVisibility);

        function updateEnabledVisibility() {
            body.style.display = enabledCb.input.checked ? '' : 'none';
        }
        updateEnabledVisibility();
        enabledCb.input.addEventListener('change', updateEnabledVisibility);

        append(body,
            durRow,
            bcRow,
            formRow('超时动作', actionSel),
            remindEveryRow,
            maxRemindRow,
            escalateToRow
        );
        append(sec, title, enableRow, body);

        function collect() {
            if (!enabledCb.input.checked) { return null; }

            var days    = parseInt(daysInput.value.trim(), 10) || 0;
            var hours   = parseInt(hoursInput.value.trim(), 10) || 0;
            var minutes = parseInt(minutesInput.value.trim(), 10) || 0;
            var duration = _composeIso8601Duration(days, hours, minutes);

            var result = {
                duration:         duration,
                businessCalendar: bcCb.input.checked,
                action:           actionSel.value
            };

            if (actionSel.value === 'Remind') {
                var re = parseInt(remindEveryInput.value.trim(), 10);
                if (!isNaN(re) && re > 0) { result['remindEveryHours'] = re; }
                var mr = parseInt(maxRemindInput.value.trim(), 10);
                if (!isNaN(mr) && mr > 0) { result['maxReminders'] = mr; }
            }
            if (actionSel.value === 'Escalate') {
                var et = escalateToInput.value.trim();
                if (et) { result['escalateTo'] = et; }
            }
            return result;
        }

        return { el: sec, collect: collect };
    }

    // ISO 8601 duration parse: "P2DT8H30M" → { days:2, hours:8, minutes:30 }
    function _parseIso8601Duration(s) {
        if (!s || typeof s !== 'string') { return {}; }
        var m = s.match(/^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?)?$/);
        if (!m) { return {}; }
        return {
            days:    m[1] ? parseInt(m[1], 10) : 0,
            hours:   m[2] ? parseInt(m[2], 10) : 0,
            minutes: m[3] ? parseInt(m[3], 10) : 0
        };
    }

    // ISO 8601 duration compose
    function _composeIso8601Duration(days, hours, minutes) {
        var s = 'P';
        if (days)    { s += days + 'D'; }
        if (hours || minutes) {
            s += 'T';
            if (hours)   { s += hours + 'H'; }
            if (minutes) { s += minutes + 'M'; }
        }
        if (s === 'P') { s = 'PT0M'; } // zero duration
        return s;
    }

    // ─── 3d. Condition panel ──────────────────────────────────────────────────

    function buildConditionPanel(nodeObj, allNodes, fieldWhitelist) {
        // fieldWhitelist: array of { field, clrType }
        var panel = el('div', { cls: 'wtm-wf-panel', style: { minWidth: '500px', padding: '16px' } });

        var keyInput = buildInput('wf-node-key', nodeObj['nodeKey'] || '', '节点键');
        var keyFeedback = el('span', { style: { fontSize: '12px' } });
        nodeKeyFeedback(keyInput, keyFeedback);
        keyInput.addEventListener('input', function () { nodeKeyFeedback(keyInput, keyFeedback); });

        var nameInput = buildInput('wf-node-name', nodeObj['name'] || '', '名称');

        // Default picker
        var otherNodes = (allNodes || []).filter(function (n) {
            return (n['nodeKey'] || '') !== (nodeObj['nodeKey'] || '');
        });
        var defaultOpts = [{ value: '', label: '-- 选择默认目标 --' }].concat(
            otherNodes.map(function (n) {
                return { value: n['nodeKey'] || '', label: (n['name'] || n['nodeKey'] || '') + ' (' + (n['nodeKey'] || '') + ')' };
            })
        );
        var defaultSel = buildSelect('wf-condition-default', defaultOpts, nodeObj['default'] || '');

        // Branches
        var branchesTitle = el('div', { text: '条件分支', style: { fontWeight: 'bold', margin: '8px 0 4px' } });
        var branchesList  = el('div', { cls: 'wtm-wf-branches' });
        var addBranchBtn  = el('button', {
            cls: 'layui-btn layui-btn-xs',
            text: '+ 添加分支',
            attrs: { type: 'button' }
        });

        var branchEntries = []; // { fieldSel, opSel, valueInp, targetSel, rowEl }

        // fieldOpts / opOpts MUST be initialized before _addBranchRow is defined and called
        var wlFields = (fieldWhitelist || []).map(function (f) { return f['field'] || ''; }).filter(Boolean);
        var fieldOpts = [{ value: '', label: '-- 选择字段 --' }].concat(
            wlFields.map(function (f) { return { value: f, label: f }; })
        );
        var opOpts = FILTER_OPERATORS.map(function (op) { return { value: op, label: op }; });

        var existingBranches = nodeObj['branches'];
        if (Array.isArray(existingBranches)) {
            existingBranches.forEach(function (b) { _addBranchRow(b); });
        }

        function _addBranchRow(branchDef) {
            branchDef = branchDef || {};
            var rule = branchDef['rule'] || {};
            var rowEl = el('div', { style: { display: 'flex', gap: '4px', marginBottom: '4px', alignItems: 'center' } });

            var fieldSel  = buildSelect('', fieldOpts, rule['field'] || '');
            fieldSel.style.width = '120px';
            var opSel     = buildSelect('', opOpts, rule['operator'] || 'Eq');
            opSel.style.width = '100px';
            var valueInp  = buildInput('', _ruleValueToString(rule['value']), '比较值');
            valueInp.style.width = '100px';
            var targetSel = buildSelect('', defaultOpts.slice(1), branchDef['target'] || '');
            targetSel.style.width = '120px';
            var removeBtn = el('button', {
                cls: 'layui-btn layui-btn-xs layui-btn-danger',
                text: '删除',
                attrs: { type: 'button' }
            });

            var entry = { fieldSel: fieldSel, opSel: opSel, valueInp: valueInp, targetSel: targetSel,
                          rowEl: rowEl, originalRule: rule };
            branchEntries.push(entry);

            removeBtn.addEventListener('click', function () {
                var idx = branchEntries.indexOf(entry);
                if (idx !== -1) { branchEntries.splice(idx, 1); }
                rowEl.parentNode && rowEl.parentNode.removeChild(rowEl);
            });

            append(rowEl, el('span', { text: '字段:' }), fieldSel,
                   el('span', { text: '运算符:' }), opSel,
                   el('span', { text: '值:' }), valueInp,
                   el('span', { text: '目标:' }), targetSel, removeBtn);
            branchesList.appendChild(rowEl);
        }

        addBranchBtn.addEventListener('click', function () { _addBranchRow(null); });

        append(panel,
            formRow('节点键', append(el('div'), keyInput, keyFeedback)),
            formRow('名称', nameInput),
            formRow('默认目标', defaultSel),
            el('hr'),
            branchesTitle, branchesList, addBranchBtn
        );

        function collectValues() {
            var branches = branchEntries.map(function (e) {
                return {
                    rule: {
                        field:    e.fieldSel.value || null,
                        operator: e.opSel.value || null,
                        value:    _parseRuleValue(e.valueInp.value)
                    },
                    target: e.targetSel.value
                };
            });
            return {
                fieldMap: {
                    nodeKey:   keyInput.value.trim(),
                    name:      nameInput.value.trim(),
                    'default': defaultSel.value || null,
                    branches:  branches.length > 0 ? branches : null
                }
            };
        }

        return { panelEl: panel, collectValues: collectValues };
    }

    // Rule value → string for display
    function _ruleValueToString(v) {
        if (v === null || v === undefined) { return ''; }
        if (Array.isArray(v)) { return v.join(','); }
        return String(v);
    }

    // Parse rule value string → JSON-compatible (array for In/NotIn)
    function _parseRuleValue(s) {
        if (!s || !s.trim()) { return null; }
        return s.trim();
    }

    // ─── 3e. Gateway panels (Parallel + Inclusive) ────────────────────────────

    function buildGatewayPanel(nodeObj, allNodes) {
        var panel = el('div', { cls: 'wtm-wf-panel', style: { minWidth: '400px', padding: '16px' } });

        var keyInput = buildInput('wf-node-key', nodeObj['nodeKey'] || '', '节点键');
        var keyFeedback = el('span', { style: { fontSize: '12px' } });
        nodeKeyFeedback(keyInput, keyFeedback);
        keyInput.addEventListener('input', function () { nodeKeyFeedback(keyInput, keyFeedback); });

        var nameInput = buildInput('wf-node-name', nodeObj['name'] || '', '名称');

        // joinNodeKey — dropdown filtered to Join nodes
        var joinNodes = (allNodes || []).filter(function (n) { return n['kind'] === NK_JOIN; });
        var joinOpts  = [{ value: '', label: '-- 选择汇聚节点 --' }].concat(
            joinNodes.map(function (n) {
                return { value: n['nodeKey'] || '', label: (n['name'] || n['nodeKey'] || '') + ' (' + (n['nodeKey'] || '') + ')' };
            })
        );
        var joinSel = buildSelect('wf-join-node-key', joinOpts, nodeObj['joinNodeKey'] || '');

        append(panel,
            formRow('节点键', append(el('div'), keyInput, keyFeedback)),
            formRow('名称', nameInput),
            formRow('汇聚节点', joinSel)
        );

        function collectValues() {
            return {
                fieldMap: {
                    nodeKey:     keyInput.value.trim(),
                    name:        nameInput.value.trim(),
                    joinNodeKey: joinSel.value || null
                }
            };
        }

        return { panelEl: panel, collectValues: collectValues };
    }

    // ─── 3f. Ack panel ────────────────────────────────────────────────────────

    function buildAckPanel(nodeObj) {
        var panel = el('div', { cls: 'wtm-wf-panel', style: { minWidth: '400px', padding: '16px' } });

        var keyInput = buildInput('wf-node-key', nodeObj['nodeKey'] || '', '节点键');
        var keyFeedback = el('span', { style: { fontSize: '12px' } });
        nodeKeyFeedback(keyInput, keyFeedback);
        keyInput.addEventListener('input', function () { nodeKeyFeedback(keyInput, keyFeedback); });

        var nameInput = buildInput('wf-node-name', nodeObj['name'] || '', '名称');

        var ackOpts = ACK_MODES.map(function (m) { return { value: m, label: ACK_MODE_LABELS[m] || m }; });
        var ackModeSel = buildSelect('wf-ack-mode', ackOpts, nodeObj['ackMode'] || 'All');

        // quorumCount: only relevant for Quorum mode
        var quorumInput = buildInput('wf-quorum-count',
            nodeObj['quorumCount'] !== undefined && nodeObj['quorumCount'] !== null ? String(nodeObj['quorumCount']) : '',
            '定额数量', 'number');
        var quorumRow = formRow('定额数量', quorumInput);

        function updateQuorumVisibility() {
            quorumRow.style.display = ackModeSel.value === 'Quorum' ? '' : 'none';
        }
        updateQuorumVisibility();
        ackModeSel.addEventListener('change', updateQuorumVisibility);

        append(panel,
            formRow('节点键', append(el('div'), keyInput, keyFeedback)),
            formRow('名称', nameInput),
            formRow('确认模式', ackModeSel),
            quorumRow
        );

        function collectValues() {
            var qc = null;
            if (ackModeSel.value === 'Quorum') {
                var qStr = quorumInput.value.trim();
                if (qStr) { qc = parseInt(qStr, 10) || null; }
            }
            return {
                fieldMap: {
                    nodeKey:      keyInput.value.trim(),
                    name:         nameInput.value.trim(),
                    ackMode:      ackModeSel.value,
                    quorumCount:  qc
                }
            };
        }

        return { panelEl: panel, collectValues: collectValues };
    }

    // ─── 3g. Cc node panel ───────────────────────────────────────────────────

    function buildCcNodePanel(nodeObj) {
        var panel = el('div', { cls: 'wtm-wf-panel', style: { minWidth: '420px', padding: '16px' } });

        var keyInput = buildInput('wf-node-key', nodeObj['nodeKey'] || '', '节点键');
        var keyFeedback = el('span', { style: { fontSize: '12px' } });
        nodeKeyFeedback(keyInput, keyFeedback);
        keyInput.addEventListener('input', function () { nodeKeyFeedback(keyInput, keyFeedback); });

        var nameInput = buildInput('wf-node-name', nodeObj['name'] || '', '名称');

        // trigger
        var trigOpts  = CC_TRIGGERS.map(function (t) { return { value: t, label: CC_TRIGGER_LABELS[t] || t }; });
        var trigSel   = buildSelect('wf-cc-trigger', trigOpts,
            (nodeObj['ccRuleDef'] && nodeObj['ccRuleDef']['trigger']) || nodeObj['trigger'] || 'OnNode');

        // rule (approver style)
        var existRule = (nodeObj['ccRuleDef'] && nodeObj['ccRuleDef']['rule']) || nodeObj['approverRule'] || {};
        var ccTypeOpts = APPROVER_TYPES.slice(0, 3).map(function (t) {
            return { value: t, label: APPROVER_TYPE_LABELS[t] || t };
        });
        var ccTypeSel  = buildSelect('wf-cc-rule-type', ccTypeOpts, existRule['type'] || 'Role');
        var ccValInp   = buildInput('wf-cc-rule-value', existRule['value'] || '', '角色代码或工号');

        append(panel,
            formRow('节点键', append(el('div'), keyInput, keyFeedback)),
            formRow('名称', nameInput),
            formRow('触发时机', trigSel),
            el('div', { text: '抄送规则', style: { fontWeight: 'bold', margin: '8px 0 4px' } }),
            formRow('规则类型', ccTypeSel),
            formRow('值', ccValInp)
        );

        function collectValues() {
            return {
                fieldMap: {
                    nodeKey:  keyInput.value.trim(),
                    name:     nameInput.value.trim(),
                    trigger:  trigSel.value,
                    ccRuleDef: {
                        trigger: trigSel.value,
                        rule: {
                            type:  ccTypeSel.value,
                            value: ccValInp.value.trim() || null
                        }
                    }
                }
            };
        }

        return { panelEl: panel, collectValues: collectValues };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Transitions table with per-edge condition editor
    // ─────────────────────────────────────────────────────────────────────────

    function buildTransitionsTable(transitions, allNodes, fieldWhitelist, onChanged) {
        // transitions: array of { from, to, condition?, ...unknownFields }
        // allNodes: array of node objects
        // onChanged: function() called when a transition is edited
        // Returns: { tableEl, collectTransitions }

        var container = el('div', { cls: 'wtm-wf-transitions' });
        var title     = el('div', { text: '流转关系', style: { fontWeight: 'bold', margin: '8px 0 4px' } });
        var tableEl   = el('table', { cls: 'layui-table', style: { width: '100%' } });

        var thead = el('thead');
        var headerRow = el('tr');
        ['来源节点', '目标节点', '条件（留空=无条件）', '操作'].forEach(function (h) {
            var th = el('th', { text: h });
            headerRow.appendChild(th);
        });
        thead.appendChild(headerRow);
        tableEl.appendChild(thead);

        var tbody = el('tbody');
        tableEl.appendChild(tbody);

        var addBtn = el('button', {
            cls: 'layui-btn layui-btn-xs',
            text: '+ 添加流转',
            attrs: { type: 'button' }
        });

        var entries = []; // { fromSel, toSel, condFieldSel, condOpSel, condValInp, rowEl, originalTrans }

        var nodeOpts = [{ value: '', label: '-- 选择节点 --' }].concat(
            (allNodes || []).map(function (n) {
                return { value: n['nodeKey'] || '', label: (n['name'] || n['nodeKey'] || '') + ' (' + (n['nodeKey'] || '') + ')' };
            })
        );

        var wlFields = (fieldWhitelist || []).map(function (f) { return f['field'] || ''; }).filter(Boolean);
        var condFieldOpts = [{ value: '', label: '（无条件）' }].concat(
            wlFields.map(function (f) { return { value: f, label: f }; })
        );
        var opOpts = FILTER_OPERATORS.map(function (op) { return { value: op, label: op }; });

        (transitions || []).forEach(function (t) { _addRow(t); });

        function _addRow(trans) {
            trans = trans || {};
            var cond = trans['condition'] || {};
            var rowEl = el('tr');

            var fromSel = buildSelect('', nodeOpts, trans['from'] || '');
            var toSel   = buildSelect('', nodeOpts, trans['to'] || '');

            // Condition: show only if the existing condition has a field (known flat rule)
            // Unknown/complex (and/or) conditions are shown as read-only summary
            var hasComplexCond = cond && (cond['and'] || cond['or']);
            var condFieldSel = null;
            var condOpSel    = null;
            var condValInp   = null;
            var condCell     = el('td');

            if (hasComplexCond) {
                // Read-only summary for complex conditions (§6: source-mode-only)
                condCell.appendChild(el('span', { text: '复杂条件（源码模式编辑）', style: { color: '#888', fontStyle: 'italic' } }));
                condFieldSel = null; condOpSel = null; condValInp = null;
            } else {
                condFieldSel = buildSelect('', condFieldOpts, (cond && cond['field']) || '');
                condFieldSel.style.width = '110px';
                condOpSel    = buildSelect('', opOpts, (cond && cond['operator']) || 'Eq');
                condOpSel.style.width = '80px';
                condValInp   = buildInput('', _ruleValueToString(cond && cond['value']), '值');
                condValInp.style.width = '90px';

                var condWrap = el('span', { style: { display: 'flex', gap: '4px', alignItems: 'center' } });
                append(condWrap, condFieldSel, condOpSel, condValInp);
                condCell.appendChild(condWrap);

                condFieldSel.addEventListener('change', function () { if (onChanged) { onChanged(); } });
                condOpSel.addEventListener('change',    function () { if (onChanged) { onChanged(); } });
                condValInp.addEventListener('input',    function () { if (onChanged) { onChanged(); } });
            }

            var removeBtn = el('button', {
                cls: 'layui-btn layui-btn-xs layui-btn-danger',
                text: '删除',
                attrs: { type: 'button' }
            });

            var entry = { fromSel: fromSel, toSel: toSel,
                          condFieldSel: condFieldSel, condOpSel: condOpSel, condValInp: condValInp,
                          rowEl: rowEl, originalTrans: trans, hasComplexCond: hasComplexCond };
            entries.push(entry);

            removeBtn.addEventListener('click', function () {
                var idx = entries.indexOf(entry);
                if (idx !== -1) { entries.splice(idx, 1); }
                rowEl.parentNode && rowEl.parentNode.removeChild(rowEl);
                if (onChanged) { onChanged(); }
            });

            fromSel.addEventListener('change', function () { if (onChanged) { onChanged(); } });
            toSel.addEventListener('change',   function () { if (onChanged) { onChanged(); } });

            var fromTd = el('td'); fromTd.appendChild(fromSel);
            var toTd   = el('td'); toTd.appendChild(toSel);
            var actTd  = el('td'); actTd.appendChild(removeBtn);

            append(rowEl, fromTd, toTd, condCell, actTd);
            tbody.appendChild(rowEl);
        }

        addBtn.addEventListener('click', function () {
            _addRow(null);
            if (onChanged) { onChanged(); }
        });

        append(container, title, tableEl, addBtn);

        // CRITICAL (T-DSN-15, §2.4-3 merge-not-regen):
        // Collect transitions as a MERGE over the original list.
        // For surviving (from,to) pairs: preserve ALL original fields (unknown + condition),
        // only UPDATE fields the user actually changed via the form.
        // For removed pairs: omit.
        // For added pairs: create new objects with only the user-provided fields.
        function collectTransitions() {
            return entries.map(function (e) {
                var from = e.fromSel.value;
                var to   = e.toSel.value;

                // Start from original object to preserve unknown fields
                var orig = e.originalTrans || {};
                var result = {};

                // Copy all original fields (preserves unknown fields verbatim)
                Object.keys(orig).forEach(function (k) { result[k] = orig[k]; });

                // Overwrite from/to with user selections
                result['from'] = from;
                result['to']   = to;

                // For complex conditions: preserve original condition verbatim
                if (e.hasComplexCond) {
                    // result['condition'] already copied from orig
                } else {
                    // Merge condition from form — but only if the user entered a field
                    if (e.condFieldSel && e.condFieldSel.value) {
                        // Preserve unknown fields inside the original condition if any
                        var origCond = orig['condition'] || {};
                        var newCond  = {};
                        Object.keys(origCond).forEach(function (k) { newCond[k] = origCond[k]; });
                        newCond['field']    = e.condFieldSel.value;
                        newCond['operator'] = e.condOpSel ? e.condOpSel.value : 'Eq';
                        newCond['value']    = e.condValInp ? _parseRuleValue(e.condValInp.value) : null;
                        result['condition'] = newCond;
                    } else {
                        // No field selected = unconditional transition; remove condition
                        delete result['condition'];
                    }
                }

                return result;
            });
        }

        return { tableEl: container, collectTransitions: collectTransitions };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. fieldWhitelist editor
    // ─────────────────────────────────────────────────────────────────────────

    function buildFieldWhitelistEditor(fieldWhitelist) {
        // fieldWhitelist: array of { field, clrType, allowedRoles? }
        var container = el('div', { cls: 'wtm-wf-fw-editor' });
        var title     = el('div', { text: '字段白名单', style: { fontWeight: 'bold', margin: '8px 0 4px' } });
        var tableEl   = el('table', { cls: 'layui-table', style: { width: '100%' } });

        var thead     = el('thead');
        var headerRow = el('tr');
        ['字段名', 'CLR类型', '允许角色（逗号分隔，留空=全部）', '操作'].forEach(function (h) {
            var th = el('th', { text: h });
            headerRow.appendChild(th);
        });
        thead.appendChild(headerRow);
        tableEl.appendChild(thead);

        var tbody  = el('tbody');
        tableEl.appendChild(tbody);

        var addBtn = el('button', {
            cls: 'layui-btn layui-btn-xs',
            text: '+ 添加字段',
            attrs: { type: 'button' }
        });

        var entries = []; // { fieldInp, clrTypeSel, rolesInp, rowEl }

        var clrOpts = CLR_TYPES.map(function (t) { return { value: t, label: t }; });

        (fieldWhitelist || []).forEach(function (fw) { _addFwRow(fw); });

        function _addFwRow(fw) {
            fw = fw || {};
            var rowEl = el('tr');

            var fieldInp   = buildInput('', fw['field'] || '', '字段名');
            fieldInp.style.width = '130px';
            var clrTypeSel = buildSelect('', clrOpts, fw['clrType'] || 'System.String');
            clrTypeSel.style.width = '160px';
            var roles      = (fw['allowedRoles'] || []).join(',');
            var rolesInp   = buildInput('', roles, '角色代码（逗号分隔）');
            rolesInp.style.width = '150px';

            var removeBtn  = el('button', {
                cls: 'layui-btn layui-btn-xs layui-btn-danger',
                text: '删除',
                attrs: { type: 'button' }
            });

            var entry = { fieldInp: fieldInp, clrTypeSel: clrTypeSel, rolesInp: rolesInp, rowEl: rowEl };
            entries.push(entry);

            removeBtn.addEventListener('click', function () {
                var idx = entries.indexOf(entry);
                if (idx !== -1) { entries.splice(idx, 1); }
                rowEl.parentNode && rowEl.parentNode.removeChild(rowEl);
            });

            var fieldTd = el('td'); fieldTd.appendChild(fieldInp);
            var typeTd  = el('td'); typeTd.appendChild(clrTypeSel);
            var rolesTd = el('td'); rolesTd.appendChild(rolesInp);
            var actTd   = el('td'); actTd.appendChild(removeBtn);

            append(rowEl, fieldTd, typeTd, rolesTd, actTd);
            tbody.appendChild(rowEl);
        }

        addBtn.addEventListener('click', function () { _addFwRow(null); });
        append(container, title, tableEl, addBtn);

        function collectFieldWhitelist() {
            return entries.map(function (e) {
                var rolesStr = e.rolesInp.value.trim();
                var roles = rolesStr ? rolesStr.split(',').map(function (s) { return s.trim(); }).filter(Boolean) : null;
                return {
                    field:        e.fieldInp.value.trim(),
                    clrType:      e.clrTypeSel.value,
                    allowedRoles: roles && roles.length > 0 ? roles : null
                };
            }).filter(function (fw) { return !!fw.field; });
        }

        return { editorEl: container, collectFieldWhitelist: collectFieldWhitelist };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Client lint — advisory, instant, never blocks save
    //    6 classes per design §5:
    //    L1: dangling transition (from/to references non-existent node)
    //    L2: Approval without approverRule
    //    L3: Condition without default
    //    L4: unreachable-from-Start (BFS)
    //    L5: gateway joinNodeKey not a Join
    //    L6: InclusiveGateway out-edge without condition
    // ─────────────────────────────────────────────────────────────────────────

    function runLint(graphTree) {
        // graphTree: the raw parsed tree from WtmJsonRaw.parse
        // Returns: array of { code, nodeKey?, message }

        var issues = [];

        if (!graphTree || typeof graphTree !== 'object') { return issues; }

        var nodes       = Array.isArray(graphTree['nodes'])       ? graphTree['nodes']       : [];
        var transitions = Array.isArray(graphTree['transitions']) ? graphTree['transitions'] : [];

        // Build nodeKey → node map
        var nodeMap = {};
        nodes.forEach(function (n) {
            var nk = _strVal(n['nodeKey']);
            if (nk) { nodeMap[nk] = n; }
        });

        var allNodeKeys = Object.keys(nodeMap);

        // L1: dangling transitions
        transitions.forEach(function (t) {
            var from = _strVal(t['from']);
            var to   = _strVal(t['to']);
            if (from && !nodeMap[from]) {
                issues.push({ code: 'L1', nodeKey: from, message: '流转来源节点不存在: ' + from });
            }
            if (to && !nodeMap[to]) {
                issues.push({ code: 'L1', nodeKey: to, message: '流转目标节点不存在: ' + to });
            }
        });

        // L2: Approval without approverRule
        nodes.forEach(function (n) {
            var kind = _strVal(n['kind']);
            if (kind === NK_APPROVAL) {
                var rule = n['approverRule'];
                if (!rule || typeof rule !== 'object' || !_strVal(rule['type'])) {
                    issues.push({ code: 'L2', nodeKey: _strVal(n['nodeKey']),
                                  message: '审批节点缺少审批人规则: ' + _strVal(n['nodeKey']) });
                }
            }
        });

        // L3: Condition without default
        nodes.forEach(function (n) {
            var kind = _strVal(n['kind']);
            if (kind === NK_CONDITION) {
                var def = _strVal(n['default']);
                if (!def) {
                    issues.push({ code: 'L3', nodeKey: _strVal(n['nodeKey']),
                                  message: '条件网关缺少默认分支: ' + _strVal(n['nodeKey']) });
                }
            }
        });

        // L4: unreachable-from-Start (BFS)
        var startNodes = nodes.filter(function (n) { return _strVal(n['kind']) === NK_START; });
        var reachable  = {};
        var queue      = [];
        startNodes.forEach(function (n) {
            var nk = _strVal(n['nodeKey']);
            if (nk && !reachable[nk]) { reachable[nk] = true; queue.push(nk); }
        });
        // Build adjacency
        var adj = {};
        transitions.forEach(function (t) {
            var from = _strVal(t['from']);
            var to   = _strVal(t['to']);
            if (from) {
                if (!adj[from]) { adj[from] = []; }
                adj[from].push(to);
            }
        });
        // Also follow Condition branches
        nodes.forEach(function (n) {
            var nk     = _strVal(n['nodeKey']);
            var kind   = _strVal(n['kind']);
            if (kind === NK_CONDITION) {
                var def = _strVal(n['default']);
                if (def) {
                    if (!adj[nk]) { adj[nk] = []; }
                    adj[nk].push(def);
                }
                var branches = n['branches'];
                if (Array.isArray(branches)) {
                    branches.forEach(function (b) {
                        var t2 = _strVal(b['target']);
                        if (t2) {
                            if (!adj[nk]) { adj[nk] = []; }
                            adj[nk].push(t2);
                        }
                    });
                }
            }
        });

        while (queue.length > 0) {
            var cur  = queue.shift();
            var nexts = adj[cur] || [];
            nexts.forEach(function (nxt) {
                if (nxt && !reachable[nxt]) {
                    reachable[nxt] = true;
                    queue.push(nxt);
                }
            });
        }

        allNodeKeys.forEach(function (nk) {
            if (!reachable[nk]) {
                issues.push({ code: 'L4', nodeKey: nk,
                              message: '节点无法从开始节点到达: ' + nk });
            }
        });

        // L5: gateway joinNodeKey not a Join node
        nodes.forEach(function (n) {
            var kind = _strVal(n['kind']);
            if (kind === NK_PARALLEL || kind === NK_INCLUSIVE) {
                var jnk = _strVal(n['joinNodeKey']);
                if (jnk) {
                    var jNode = nodeMap[jnk];
                    if (!jNode || _strVal(jNode['kind']) !== NK_JOIN) {
                        issues.push({ code: 'L5', nodeKey: _strVal(n['nodeKey']),
                                      message: '网关的汇聚节点不是 Join 类型: ' + jnk });
                    }
                }
            }
        });

        // L6: InclusiveGateway out-edge without condition
        nodes.forEach(function (n) {
            var kind = _strVal(n['kind']);
            if (kind === NK_INCLUSIVE) {
                var nk = _strVal(n['nodeKey']);
                var outEdges = transitions.filter(function (t) {
                    return _strVal(t['from']) === nk;
                });
                outEdges.forEach(function (t) {
                    var cond = t['condition'];
                    var hasField = cond && typeof cond === 'object' && _strVal(cond['field']);
                    if (!hasField) {
                        issues.push({ code: 'L6', nodeKey: nk,
                                      message: '包容网关出边缺少条件: ' + nk + ' → ' + _strVal(t['to']) });
                    }
                });
            }
        });

        return issues;
    }

    function _strVal(v) {
        if (v === null || v === undefined) { return ''; }
        // Handle RawNum (from the core codec) — its valueOf is numeric, but we want string
        if (typeof v === 'object' && v.raw !== undefined) { return String(v.raw); }
        return String(v);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. Lint panel renderer
    //    Renders lint issues into a container div using DOM methods only.
    //    layer.msg/alert are NOT used for server-derived strings.
    // ─────────────────────────────────────────────────────────────────────────

    function renderLintBadges(containerEl, issues, onFocusNodeKey) {
        // Clear previous content
        while (containerEl.firstChild) { containerEl.removeChild(containerEl.firstChild); }

        if (!issues || issues.length === 0) {
            containerEl.appendChild(el('span', { text: '✓ 无警告', style: { color: 'green' } }));
            return;
        }

        issues.forEach(function (issue) {
            var badge = el('div', { cls: 'wtm-wf-lint-badge', style: { marginBottom: '4px', cursor: 'pointer' } });
            var codeEl = el('span', {
                cls:   'layui-badge',
                text:  issue.code,
                style: { marginRight: '6px', background: '#ffaa00' }
            });
            var msgEl  = el('span', { text: issue.message });
            badge.appendChild(codeEl);
            badge.appendChild(msgEl);

            if (onFocusNodeKey && issue.nodeKey) {
                badge.addEventListener('click', function () {
                    onFocusNodeKey(issue.nodeKey);
                });
            }

            containerEl.appendChild(badge);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. Validate-response error strip
    //    Renders server validation errors (with optional nodeKey) into a strip.
    //    Uses textContent only — no server strings reach innerHTML or layer.alert.
    // ─────────────────────────────────────────────────────────────────────────

    function renderValidationErrors(containerEl, validateResponse, onFocusNodeKey) {
        while (containerEl.firstChild) { containerEl.removeChild(containerEl.firstChild); }

        if (!validateResponse) { return; }

        var isValid = validateResponse['isValid'];
        if (isValid) {
            containerEl.appendChild(el('span', { text: '服务端校验通过', style: { color: 'green' } }));
            return;
        }

        var errorCode = validateResponse['errorCode'] || '';
        var message   = validateResponse['message'] || '';
        var nodeKey   = validateResponse['nodeKey'] || '';

        var errEl = el('div', { cls: 'wtm-wf-lint-badge', style: { color: 'red', cursor: nodeKey ? 'pointer' : 'default' } });
        var codeEl = el('span', { text: '[' + errorCode + '] ', style: { fontWeight: 'bold' } });
        var msgEl  = el('span', { text: message });
        errEl.appendChild(codeEl);
        errEl.appendChild(msgEl);

        if (nodeKey && onFocusNodeKey) {
            errEl.addEventListener('click', function () { onFocusNodeKey(nodeKey); });
        }

        containerEl.appendChild(errEl);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9. openNodePanel — unified entry point to open a per-node form panel
    //    via layer.open type:1 with a DOM element.
    //    Merge-not-regen: on save, patches the tree node in-place.
    // ─────────────────────────────────────────────────────────────────────────

    function openNodePanel(opts) {
        // opts: { nodeObj, allNodes, fieldWhitelist, graphModel, onSaved, layui }
        var nodeObj        = opts.nodeObj || {};
        var allNodes       = opts.allNodes || [];
        var fieldWhitelist = opts.fieldWhitelist || [];
        var graphModel     = opts.graphModel;     // WtmDesignerCore.GraphModel
        var onSaved        = opts.onSaved;        // callback()
        var layui          = opts.layui || (typeof window !== 'undefined' && window.layui);

        var kind = _strVal(nodeObj['kind']);
        var panelResult;

        switch (kind) {
            case NK_START:
            case NK_END:
            case NK_JOIN:
                panelResult = buildSimplePanel(nodeObj);
                break;
            case NK_APPROVAL:
                panelResult = buildApprovalPanel(nodeObj, allNodes);
                break;
            case NK_CONDITION:
                panelResult = buildConditionPanel(nodeObj, allNodes, fieldWhitelist);
                break;
            case NK_PARALLEL:
            case NK_INCLUSIVE:
                panelResult = buildGatewayPanel(nodeObj, allNodes);
                break;
            case NK_ACK:
                panelResult = buildAckPanel(nodeObj);
                break;
            case NK_CC:
                panelResult = buildCcNodePanel(nodeObj);
                break;
            default:
                // Unknown kind — show source fallback
                var fallback = el('div', { style: { padding: '12px' } });
                fallback.appendChild(el('p', { text: '未知节点类型（' + kind + '），请使用源码模式编辑。' }));
                panelResult = { panelEl: fallback, collectValues: function () { return { fieldMap: {} }; } };
        }

        var saveBtn = el('button', {
            cls: 'layui-btn layui-btn-sm',
            text: '保存',
            attrs: { type: 'button' }
        });
        var cancelBtn = el('button', {
            cls: 'layui-btn layui-btn-sm layui-btn-primary',
            text: '取消',
            attrs: { type: 'button' }
        });

        var btnRow = el('div', { style: { marginTop: '12px', display: 'flex', gap: '8px' } });
        btnRow.appendChild(saveBtn);
        btnRow.appendChild(cancelBtn);
        panelResult.panelEl.appendChild(btnRow);

        var layerIndex = null;

        if (layui && layui.layer) {
            layerIndex = layui.layer.open({
                type:    1,
                title:   '编辑节点 — ' + (KIND_LABELS[kind] || kind),
                content: panelResult.panelEl,
                area:    ['520px', 'auto'],
                shadeClose: false
            });
        }

        function _close() {
            if (layui && layui.layer && layerIndex !== null) {
                layui.layer.close(layerIndex);
            }
        }

        cancelBtn.addEventListener('click', function () { _close(); });

        saveBtn.addEventListener('click', function () {
            var vals = panelResult.collectValues();
            // MERGE into nodeObj (not rebuild — preserves unknown fields)
            if (vals.fieldMap) {
                mergeScalars(nodeObj, vals.fieldMap);
            }
            if (vals.approverRule) {
                mergeApproverRule(nodeObj, vals.approverRule);
            }
            if (vals.cc !== undefined) {
                // Replace cc list (user explicitly built it)
                if (vals.cc === null) {
                    delete nodeObj['cc'];
                } else {
                    nodeObj['cc'] = vals.cc;
                }
            }
            if (vals.timeout !== undefined) {
                if (vals.timeout === null) {
                    delete nodeObj['timeout'];
                } else {
                    // Merge into existing timeout (preserves unknown fields)
                    if (!nodeObj['timeout'] || typeof nodeObj['timeout'] !== 'object') {
                        nodeObj['timeout'] = {};
                    }
                    mergeScalars(nodeObj['timeout'], vals.timeout);
                }
            }
            // Mark the graph dirty
            if (graphModel && graphModel.markDirty) { graphModel.markDirty(); }
            _close();
            if (onSaved) { onSaved(); }
        });

        return { close: _close, panelEl: panelResult.panelEl };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 10. Condition branch → transition merge-sync (T-DSN-15)
    //     When a Condition node's branches/default are edited, sync the
    //     transitions array IN-PLACE (merge-not-regen).
    //
    //     Rules:
    //       - For each surviving branch (from=condNodeKey, to=branch.target):
    //           if an existing transition (from,to) exists → UPDATE only 'to' if changed
    //           (condition payloads and unknown fields are preserved)
    //       - For removed branches: remove that transition by (from,to) key
    //       - For added branches: insert new transition {from:condNodeKey, to:branch.target}
    //       - Default is modeled as a no-condition transition too
    //       - Never rebuild the full out-edge list
    // ─────────────────────────────────────────────────────────────────────────

    function syncConditionTransitions(graphTree, condNodeKey, newBranches, newDefault) {
        // graphTree: the parsed tree (mutated in place)
        // condNodeKey: string — the condition node's key
        // newBranches: array of { rule, target }
        // newDefault: string — target for default branch (may be null)

        if (!graphTree || !condNodeKey) { return; }

        var transitions = graphTree['transitions'];
        if (!Array.isArray(transitions)) {
            graphTree['transitions'] = [];
            transitions = graphTree['transitions'];
        }

        // Build set of desired target nodeKeys from this Condition node
        var desiredTargets = {};
        if (Array.isArray(newBranches)) {
            newBranches.forEach(function (b) {
                var t = _strVal(b['target']);
                if (t) { desiredTargets[t] = true; }
            });
        }
        if (newDefault) { desiredTargets[newDefault] = true; }

        // Remove transitions that are outgoing from condNodeKey but NOT in desiredTargets
        var toRemove = [];
        transitions.forEach(function (t, idx) {
            if (_strVal(t['from']) === condNodeKey && !desiredTargets[_strVal(t['to'])]) {
                toRemove.push(idx);
            }
        });
        // Remove in reverse order to keep indices stable
        for (var i = toRemove.length - 1; i >= 0; i--) {
            transitions.splice(toRemove[i], 1);
        }

        // For each desired target: if not already present, add a new transition
        var existingTargets = {};
        transitions.forEach(function (t) {
            if (_strVal(t['from']) === condNodeKey) {
                existingTargets[_strVal(t['to'])] = true;
            }
        });

        Object.keys(desiredTargets).forEach(function (target) {
            if (!existingTargets[target]) {
                transitions.push({ from: condNodeKey, to: target });
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 11. Expose window.WtmDesignerForms
    // ─────────────────────────────────────────────────────────────────────────

    if (typeof window !== 'undefined') {
        window.WtmDesignerForms = {
            // Panel builders (return { panelEl, collectValues })
            buildSimplePanel:          buildSimplePanel,
            buildApprovalPanel:        buildApprovalPanel,
            buildConditionPanel:       buildConditionPanel,
            buildGatewayPanel:         buildGatewayPanel,
            buildAckPanel:             buildAckPanel,
            buildCcNodePanel:          buildCcNodePanel,

            // Composite builders
            buildTransitionsTable:     buildTransitionsTable,
            buildFieldWhitelistEditor: buildFieldWhitelistEditor,

            // Unified panel opener (uses layer.open)
            openNodePanel:             openNodePanel,

            // Condition branch → transition sync
            syncConditionTransitions:  syncConditionTransitions,

            // Lint
            runLint:                   runLint,
            renderLintBadges:          renderLintBadges,
            renderValidationErrors:    renderValidationErrors,

            // Merge helpers (exported for testing)
            mergeScalars:              mergeScalars,
            mergeApproverRule:         mergeApproverRule,

            // DOM helpers (exported for testing)
            _parseIso8601Duration:     _parseIso8601Duration,
            _composeIso8601Duration:   _composeIso8601Duration,

            // Constants (for external use/testing)
            NODE_KINDS:                NODE_KINDS,
            APPROVE_MODES:             APPROVE_MODES,
            FILTER_OPERATORS:          FILTER_OPERATORS,

            version: 'WF-21.6'
        };
    }

}());
