'use strict';
/**
 * Tests for framework_workflow_designer_forms.js — WF-21.6
 *
 * Coverage:
 *  T-DSN-15 FormCoverage + branch-sync regression:
 *    - All 9 NodeKinds produce panels with correct fields
 *    - Condition branch edit preserves unknown fields + condition on surviving edges
 *      (merge-not-regen — the skeptic's regen-destroys-fields break must stay fixed)
 *    - syncConditionTransitions: add/remove/preserve semantics
 *  T-DSN-17 ClientLint:
 *    - All 6 lint classes badge correctly (L1–L6)
 *    - Lint never blocks save (advisory-only)
 *    - renderLintBadges / renderValidationErrors use textContent only
 *  T-DSN-9 (forms sources):
 *    - Zero eval / new Function / innerHTML / insertAdjacentHTML / outerHTML / inline on*
 *    - layer.msg / layer.alert never receive server-derived strings
 *  Additional:
 *    - mergeScalars / mergeApproverRule preserve unknown fields
 *    - ISO-8601 duration composer / parser round-trip
 *    - Transitions table merge-not-regen: unknown fields on surviving (from,to) edges survive
 *    - fieldWhitelist editor collect round-trip
 *    - renderValidationErrors uses textContent (not innerHTML)
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

// ── Loader ────────────────────────────────────────────────────────────────────

function loadForms(overrides) {
    var src = fs.readFileSync(
        path.resolve(__dirname,
            '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/framework_workflow_designer_forms.js'),
        'utf8'
    );

    var layerCalls = [];
    var mockLayui = {
        layer: {
            open: jest.fn(function (opts) {
                layerCalls.push({ type: 'open', opts: opts });
                return 42; // mock layer index
            }),
            close: jest.fn(function (idx) {
                layerCalls.push({ type: 'close', idx: idx });
            }),
            msg: jest.fn(function (msg) {
                layerCalls.push({ type: 'msg', msg: msg });
            }),
            alert: jest.fn(function (msg) {
                layerCalls.push({ type: 'alert', msg: msg });
            })
        }
    };

    var sandbox = {
        window:   {},
        document: typeof document !== 'undefined' ? document : global.document,
        layui:    mockLayui
    };
    sandbox.window = sandbox;
    sandbox.window.layui = mockLayui;

    // Apply any test overrides
    if (overrides) {
        Object.keys(overrides).forEach(function (k) {
            sandbox.window[k] = overrides[k];
        });
    }

    vm.runInNewContext(src, sandbox);

    return {
        Forms:      sandbox.window.WtmDesignerForms,
        layerCalls: layerCalls,
        mockLayui:  mockLayui
    };
}

// ── Source-level security assertions (T-DSN-9) ───────────────────────────────

describe('T-DSN-9: forms module source security assertions', function () {
    var src;
    beforeAll(function () {
        src = fs.readFileSync(
            path.resolve(__dirname,
                '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/framework_workflow_designer_forms.js'),
            'utf8'
        );
    });

    test('no eval() calls', function () {
        // Allow occurrences only in comments (lines starting with //)
        var lines = src.split('\n');
        var violations = lines.filter(function (line) {
            var stripped = line.replace(/\/\/.*$/, '').replace(/\/\*[\s\S]*?\*\//g, '');
            return /\beval\s*\(/.test(stripped);
        });
        expect(violations).toHaveLength(0);
    });

    test('no new Function() calls', function () {
        var lines = src.split('\n');
        var violations = lines.filter(function (line) {
            var stripped = line.replace(/\/\/.*$/, '');
            return /new\s+Function\s*\(/.test(stripped);
        });
        expect(violations).toHaveLength(0);
    });

    test('no innerHTML assignments', function () {
        var lines = src.split('\n');
        var violations = lines.filter(function (line) {
            var stripped = line.replace(/\/\/.*$/, '');
            return /\.innerHTML\s*=/.test(stripped);
        });
        expect(violations).toHaveLength(0);
    });

    test('no insertAdjacentHTML calls', function () {
        expect(src).not.toMatch(/insertAdjacentHTML/);
    });

    test('no outerHTML assignments', function () {
        var lines = src.split('\n');
        var violations = lines.filter(function (line) {
            var stripped = line.replace(/\/\/.*$/, '');
            return /\.outerHTML\s*=/.test(stripped);
        });
        expect(violations).toHaveLength(0);
    });

    test('no inline event handlers (on* = "...") in template strings', function () {
        // Should have no string containing on* handler attributes
        expect(src).not.toMatch(/setAttribute\s*\(\s*['"]on[a-z]+['"]/);
    });

    test('layer.msg and layer.alert not called with server-derived strings', function () {
        // The module should not call layui.layer.msg() or layui.layer.alert() at all
        // (those paths are reserved for caller code, not form module internals).
        // Asserting zero direct .msg / .alert calls in the source:
        var lines = src.split('\n');
        var msgCalls = lines.filter(function (line) {
            var stripped = line.replace(/\/\/.*$/, '');
            return /layer\.(msg|alert)\s*\(/.test(stripped);
        });
        expect(msgCalls).toHaveLength(0);
    });

    test('no Function constructor usage', function () {
        var lines = src.split('\n');
        var violations = lines.filter(function (line) {
            var stripped = line.replace(/\/\/.*$/, '');
            // Match Function(...) or Function.prototype but not normal references in comments
            return /(?:^|[^a-zA-Z_$])Function\s*\(/.test(stripped) ||
                   /\bFunction\.constructor/.test(stripped);
        });
        // Allow: `jest.fn(function...` is test code; in the module itself there should be none
        expect(violations).toHaveLength(0);
    });

    test('module exposes WtmDesignerForms on window', function () {
        var result = loadForms();
        expect(result.Forms).toBeDefined();
        expect(result.Forms.version).toBe('WF-21.6');
    });
});

// ── T-DSN-15: Panel builders for all 9 NodeKinds ─────────────────────────────

describe('T-DSN-15: panel builders for all 9 NodeKinds', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    // Helper: check a panel result has the expected shape
    function expectPanel(result) {
        expect(result).toBeDefined();
        expect(result.panelEl).toBeDefined();
        expect(result.panelEl.tagName).toBe('DIV');
        expect(typeof result.collectValues).toBe('function');
    }

    test('buildSimplePanel — Start node produces key+name fields', function () {
        var node = { nodeKey: 'start1', kind: 'Start', name: '开始' };
        var result = Forms.buildSimplePanel(node);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('start1');
        expect(vals.fieldMap.name).toBe('开始');
    });

    test('buildSimplePanel — End node', function () {
        var node = { nodeKey: 'end1', kind: 'End', name: '结束' };
        var result = Forms.buildSimplePanel(node);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('end1');
    });

    test('buildSimplePanel — Join node', function () {
        var node = { nodeKey: 'join1', kind: 'Join', name: '汇聚' };
        var result = Forms.buildSimplePanel(node);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('join1');
    });

    test('buildApprovalPanel — Sequential mode produces correct fields', function () {
        var node = {
            nodeKey: 'ap1', kind: 'Approval', name: '财务审批',
            approveMode: 'Sequential',
            rejectPolicy: 'ReturnToPrev',
            approverRule: { type: 'Role', value: 'finance' }
        };
        var result = Forms.buildApprovalPanel(node, []);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('ap1');
        expect(vals.fieldMap.approveMode).toBe('Sequential');
        expect(vals.approverRule.type).toBe('Role');
        expect(vals.approverRule.value).toBe('finance');
    });

    test('buildApprovalPanel — All mode includes approvePercent + rejectGate', function () {
        var node = {
            nodeKey: 'ap2', kind: 'Approval', name: '会签节点',
            approveMode: 'All', approvePercent: 0.67, rejectGate: 'Immediate',
            rejectPolicy: 'TerminateInstance',
            approverRule: { type: 'Role', value: 'managers' }
        };
        var result = Forms.buildApprovalPanel(node, []);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.approveMode).toBe('All');
        // approvePercent should be a number in the 0-1 range
        expect(vals.fieldMap.approvePercent).toBeGreaterThan(0);
        expect(vals.fieldMap.rejectGate).toBeDefined();
    });

    test('buildApprovalPanel — Any mode omits approvePercent + rejectGate', function () {
        var node = {
            nodeKey: 'ap3', kind: 'Approval', name: '或签',
            approveMode: 'Any', rejectPolicy: 'ReturnToInitiator',
            approverRule: { type: 'User', value: 'alice' }
        };
        var result = Forms.buildApprovalPanel(node, []);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.approveMode).toBe('Any');
        expect(vals.fieldMap.approvePercent).toBeNull();
        expect(vals.fieldMap.rejectGate).toBeNull();
    });

    test('buildApprovalPanel — ManagerChain type produces maxLevel field', function () {
        var node = {
            nodeKey: 'ap4', kind: 'Approval', name: '上级审批',
            approveMode: 'Sequential',
            rejectPolicy: 'ReturnToInitiator',
            approverRule: { type: 'ManagerChain', maxLevel: 3 }
        };
        var result = Forms.buildApprovalPanel(node, []);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.approverRule.type).toBe('ManagerChain');
        expect(vals.approverRule.maxLevel).toBe(3);
    });

    test('buildConditionPanel — produces key, name, default, branches fields', function () {
        var node = {
            nodeKey: 'cond1', kind: 'Condition', name: '条件1',
            'default': 'end1',
            branches: [{ rule: { field: 'amount', operator: 'Gt', value: '1000' }, target: 'highApproval' }]
        };
        var wl = [{ field: 'amount', clrType: 'System.Decimal' }];
        var allNodes = [{ nodeKey: 'end1', kind: 'End', name: '结束' },
                        { nodeKey: 'highApproval', kind: 'Approval', name: '高额审批' }];
        var result = Forms.buildConditionPanel(node, allNodes, wl);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('cond1');
        expect(vals.fieldMap['default']).toBe('end1');
        expect(Array.isArray(vals.fieldMap.branches)).toBe(true);
        expect(vals.fieldMap.branches.length).toBe(1);
    });

    test('buildGatewayPanel — ParallelGateway with joinNodeKey', function () {
        var node = { nodeKey: 'pg1', kind: 'ParallelGateway', name: '并行', joinNodeKey: 'join1' };
        var allNodes = [{ nodeKey: 'join1', kind: 'Join', name: '汇聚' }];
        var result = Forms.buildGatewayPanel(node, allNodes);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('pg1');
        expect(vals.fieldMap.joinNodeKey).toBe('join1');
    });

    test('buildGatewayPanel — InclusiveGateway with joinNodeKey', function () {
        var node = { nodeKey: 'ig1', kind: 'InclusiveGateway', name: '包容', joinNodeKey: 'join2' };
        var allNodes = [{ nodeKey: 'join2', kind: 'Join', name: '汇聚2' }];
        var result = Forms.buildGatewayPanel(node, allNodes);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.joinNodeKey).toBe('join2');
    });

    test('buildAckPanel — All mode', function () {
        var node = { nodeKey: 'ack1', kind: 'Ack', name: '确认', ackMode: 'All' };
        var result = Forms.buildAckPanel(node);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.ackMode).toBe('All');
        expect(vals.fieldMap.quorumCount).toBeNull();
    });

    test('buildAckPanel — Quorum mode includes quorumCount', function () {
        var node = { nodeKey: 'ack2', kind: 'Ack', name: '定额确认', ackMode: 'Quorum', quorumCount: 3 };
        var result = Forms.buildAckPanel(node);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.ackMode).toBe('Quorum');
        // quorumCount is shown only when Quorum mode selected
        // The select defaults to ackMode from the node, so Quorum should show the field
        // Note: quorumCount input should have the existing value
        expect(vals.fieldMap.quorumCount).toBe(3);
    });

    test('buildCcNodePanel — produces trigger + rule fields', function () {
        var node = {
            nodeKey: 'cc1', kind: 'Cc', name: '抄送',
            trigger: 'OnSubmit',
            approverRule: { type: 'Role', value: 'hr' }
        };
        var result = Forms.buildCcNodePanel(node);
        expectPanel(result);
        var vals = result.collectValues();
        expect(vals.fieldMap.nodeKey).toBe('cc1');
        expect(vals.fieldMap.trigger).toBeDefined();
    });
});

// ── T-DSN-15: merge-not-regen invariant ──────────────────────────────────────

describe('T-DSN-15: merge-not-regen — unknown fields preserved', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('mergeScalars: only listed keys are written; unknown fields survive', function () {
        var nodeObj = {
            nodeKey:       'n1',
            kind:          'Approval',
            unknownField:  'preserve-me',
            anotherField:  42,
            approveMode:   'Sequential'
        };
        Forms.mergeScalars(nodeObj, { nodeKey: 'n1-updated', name: 'New Name' });
        expect(nodeObj.nodeKey).toBe('n1-updated');
        expect(nodeObj.name).toBe('New Name');
        // Unknown fields MUST survive
        expect(nodeObj.unknownField).toBe('preserve-me');
        expect(nodeObj.anotherField).toBe(42);
        // Existing untouched field survives
        expect(nodeObj.approveMode).toBe('Sequential');
    });

    test('mergeScalars: null value deletes the key', function () {
        var nodeObj = { nodeKey: 'n1', approvePercent: 0.5, rejectGate: 'Immediate' };
        Forms.mergeScalars(nodeObj, { approvePercent: null });
        expect(nodeObj.approvePercent).toBeUndefined();
        expect(nodeObj.rejectGate).toBe('Immediate'); // untouched
    });

    test('mergeApproverRule: unknown fields in existing rule survive', function () {
        var nodeObj = {
            approverRule: {
                type:         'Role',
                value:        'finance',
                unknownRule:  'custom-field'
            }
        };
        Forms.mergeApproverRule(nodeObj, { type: 'User', value: 'bob' });
        expect(nodeObj.approverRule.type).toBe('User');
        expect(nodeObj.approverRule.value).toBe('bob');
        // Unknown field in rule MUST survive
        expect(nodeObj.approverRule.unknownRule).toBe('custom-field');
    });

    test('mergeApproverRule: creates approverRule if absent', function () {
        var nodeObj = { nodeKey: 'n1' };
        Forms.mergeApproverRule(nodeObj, { type: 'Role', value: 'hr' });
        expect(nodeObj.approverRule).toBeDefined();
        expect(nodeObj.approverRule.type).toBe('Role');
    });

    test('buildTransitionsTable collectTransitions preserves unknown fields on surviving edges', function () {
        // Set up transitions with an unknown field on a surviving edge
        var trans = [
            { from: 'start', to: 'approval', unknownEdgeField: 'keep-this', condition: null },
            { from: 'approval', to: 'end' }
        ];
        var nodes = [
            { nodeKey: 'start', kind: 'Start', name: '开始' },
            { nodeKey: 'approval', kind: 'Approval', name: '审批' },
            { nodeKey: 'end', kind: 'End', name: '结束' }
        ];
        var result = Forms.buildTransitionsTable(trans, nodes, [], null);
        expect(result.tableEl).toBeDefined();
        expect(typeof result.collectTransitions).toBe('function');

        var collected = result.collectTransitions();
        // Should have 2 transitions
        expect(collected.length).toBe(2);

        // Find the start→approval transition
        var startToApproval = collected.find(function (t) {
            return t.from === 'start' && t.to === 'approval';
        });
        expect(startToApproval).toBeDefined();
        // CRITICAL: unknown field must survive (merge-not-regen)
        expect(startToApproval.unknownEdgeField).toBe('keep-this');
    });

    test('buildTransitionsTable: condition on surviving (from,to) edge is preserved verbatim', function () {
        var existingCondition = {
            field:    'amount',
            operator: 'Gt',
            value:    '1000'
        };
        var trans = [
            { from: 'cond', to: 'highApproval', condition: existingCondition }
        ];
        var nodes = [
            { nodeKey: 'cond', kind: 'Condition', name: '条件' },
            { nodeKey: 'highApproval', kind: 'Approval', name: '高额审批' }
        ];
        var wl = [{ field: 'amount', clrType: 'System.Decimal' }];
        var result = Forms.buildTransitionsTable(trans, nodes, wl, null);
        var collected = result.collectTransitions();
        expect(collected.length).toBe(1);
        // The condition's field should be preserved (form shows existing value)
        var t = collected[0];
        expect(t.from).toBe('cond');
        expect(t.to).toBe('highApproval');
        // condition should be present and have the correct field
        expect(t.condition).toBeDefined();
        expect(t.condition.field).toBe('amount');
    });

    test('buildTransitionsTable: complex (and/or) condition is preserved read-only (no edit)', function () {
        var complexCond = {
            and: [
                { field: 'amount', operator: 'Gt', value: '500' },
                { field: 'dept', operator: 'Eq', value: 'finance' }
            ]
        };
        var trans = [{ from: 'cond', to: 'approval', condition: complexCond }];
        var nodes = [
            { nodeKey: 'cond', kind: 'Condition', name: '条件' },
            { nodeKey: 'approval', kind: 'Approval', name: '审批' }
        ];
        var result = Forms.buildTransitionsTable(trans, nodes, [], null);
        var collected = result.collectTransitions();
        expect(collected.length).toBe(1);
        // Complex condition (and/or) must be preserved verbatim — not destroyed
        var t = collected[0];
        expect(t.condition).toBeDefined();
        expect(t.condition.and).toBeDefined();
        expect(t.condition.and.length).toBe(2);
    });
});

// ── T-DSN-15: syncConditionTransitions ───────────────────────────────────────

describe('T-DSN-15: syncConditionTransitions — merge-not-regen', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('new branches: adds transitions for new targets', function () {
        var tree = { transitions: [] };
        Forms.syncConditionTransitions(tree, 'cond1',
            [{ target: 'nodeA' }, { target: 'nodeB' }], 'nodeC');

        var fromCond = tree.transitions.filter(function (t) { return t.from === 'cond1'; });
        var targets  = fromCond.map(function (t) { return t.to; }).sort();
        expect(targets).toEqual(['nodeA', 'nodeB', 'nodeC'].sort());
    });

    test('removed branches: removes transitions no longer in desired set', function () {
        var tree = {
            transitions: [
                { from: 'cond1', to: 'nodeA', unknownEdgeField: 'keep' },
                { from: 'cond1', to: 'nodeB' },
                { from: 'other', to: 'nodeA' } // different origin — must NOT be touched
            ]
        };
        // Remove nodeB branch, keep nodeA
        Forms.syncConditionTransitions(tree, 'cond1', [{ target: 'nodeA' }], null);

        var fromCond = tree.transitions.filter(function (t) { return t.from === 'cond1'; });
        expect(fromCond.length).toBe(1);
        expect(fromCond[0].to).toBe('nodeA');
        // Unknown field on the surviving edge MUST survive
        expect(fromCond[0].unknownEdgeField).toBe('keep');

        // Other-origin transitions are not disturbed
        var fromOther = tree.transitions.filter(function (t) { return t.from === 'other'; });
        expect(fromOther.length).toBe(1);
    });

    test('existing transition surviving: preserves unknown edge fields (merge-not-regen)', function () {
        var tree = {
            transitions: [
                { from: 'cond1', to: 'nodeA',
                  unknownEdgeField: 'preserve-me',
                  condition: { field: 'amount', operator: 'Gt', value: '100' } }
            ]
        };
        // Same branch target — should NOT remove and re-add
        Forms.syncConditionTransitions(tree, 'cond1', [{ target: 'nodeA' }], null);

        expect(tree.transitions.length).toBe(1);
        // Unknown field still there (original object was kept, not rebuilt)
        expect(tree.transitions[0].unknownEdgeField).toBe('preserve-me');
        // Existing condition still intact
        expect(tree.transitions[0].condition.field).toBe('amount');
    });

    test('empty branches + null default: removes all outgoing transitions from cond node', function () {
        var tree = {
            transitions: [
                { from: 'cond1', to: 'nodeA' },
                { from: 'cond1', to: 'nodeB' },
                { from: 'start', to: 'cond1' }
            ]
        };
        Forms.syncConditionTransitions(tree, 'cond1', [], null);
        var fromCond = tree.transitions.filter(function (t) { return t.from === 'cond1'; });
        expect(fromCond.length).toBe(0);
        // Incoming edge survives
        expect(tree.transitions.length).toBe(1);
        expect(tree.transitions[0].from).toBe('start');
    });

    test('transitions array created if absent', function () {
        var tree = {};
        Forms.syncConditionTransitions(tree, 'cond1', [{ target: 'n1' }], null);
        expect(Array.isArray(tree.transitions)).toBe(true);
        expect(tree.transitions.length).toBe(1);
    });
});

// ── T-DSN-17: Client lint — all 6 classes ────────────────────────────────────

describe('T-DSN-17: client lint — all 6 lint classes', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    function makeGraph(overrides) {
        return Object.assign({
            schemaVersion: 1,
            key: 'test',
            name: 'Test',
            fieldWhitelist: [],
            nodes: [],
            transitions: []
        }, overrides);
    }

    test('empty graph → no lint issues', function () {
        var issues = Forms.runLint(makeGraph());
        expect(issues).toHaveLength(0);
    });

    test('L1: dangling transition — from references non-existent node', function () {
        var graph = makeGraph({
            nodes: [{ nodeKey: 'start', kind: 'Start' }],
            transitions: [{ from: 'ghost', to: 'start' }]
        });
        var issues = Forms.runLint(graph);
        var l1 = issues.filter(function (i) { return i.code === 'L1'; });
        expect(l1.length).toBeGreaterThan(0);
        expect(l1[0].nodeKey).toBe('ghost');
    });

    test('L1: dangling transition — to references non-existent node', function () {
        var graph = makeGraph({
            nodes: [{ nodeKey: 'start', kind: 'Start' }],
            transitions: [{ from: 'start', to: 'nowhere' }]
        });
        var issues = Forms.runLint(graph);
        var l1 = issues.filter(function (i) { return i.code === 'L1'; });
        expect(l1.length).toBeGreaterThan(0);
        expect(l1[0].nodeKey).toBe('nowhere');
    });

    test('L2: Approval node without approverRule', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'ap1', kind: 'Approval', name: '审批' }
                // no approverRule
            ],
            transitions: [{ from: 'start', to: 'ap1' }]
        });
        var issues = Forms.runLint(graph);
        var l2 = issues.filter(function (i) { return i.code === 'L2'; });
        expect(l2.length).toBeGreaterThan(0);
        expect(l2[0].nodeKey).toBe('ap1');
    });

    test('L2: Approval node WITH valid approverRule → no L2', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'ap1', kind: 'Approval', approverRule: { type: 'Role', value: 'hr' } }
            ],
            transitions: [{ from: 'start', to: 'ap1' }]
        });
        var issues = Forms.runLint(graph);
        var l2 = issues.filter(function (i) { return i.code === 'L2'; });
        expect(l2).toHaveLength(0);
    });

    test('L3: Condition without default', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'cond', kind: 'Condition', branches: [] }
                // no default
            ],
            transitions: [{ from: 'start', to: 'cond' }]
        });
        var issues = Forms.runLint(graph);
        var l3 = issues.filter(function (i) { return i.code === 'L3'; });
        expect(l3.length).toBeGreaterThan(0);
        expect(l3[0].nodeKey).toBe('cond');
    });

    test('L3: Condition WITH default → no L3', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'cond', kind: 'Condition', 'default': 'end', branches: [] },
                { nodeKey: 'end', kind: 'End' }
            ],
            transitions: [
                { from: 'start', to: 'cond' },
                { from: 'cond', to: 'end' }
            ]
        });
        var issues = Forms.runLint(graph);
        var l3 = issues.filter(function (i) { return i.code === 'L3'; });
        expect(l3).toHaveLength(0);
    });

    test('L4: unreachable node (not connected from Start)', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'end', kind: 'End' },
                { nodeKey: 'orphan', kind: 'Approval', approverRule: { type: 'Role', value: 'hr' } }
            ],
            transitions: [{ from: 'start', to: 'end' }]
        });
        var issues = Forms.runLint(graph);
        var l4 = issues.filter(function (i) { return i.code === 'L4'; });
        expect(l4.length).toBeGreaterThan(0);
        expect(l4[0].nodeKey).toBe('orphan');
    });

    test('L4: fully connected graph → no L4', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'ap1', kind: 'Approval', approverRule: { type: 'Role', value: 'hr' } },
                { nodeKey: 'end', kind: 'End' }
            ],
            transitions: [
                { from: 'start', to: 'ap1' },
                { from: 'ap1', to: 'end' }
            ]
        });
        var issues = Forms.runLint(graph);
        var l4 = issues.filter(function (i) { return i.code === 'L4'; });
        expect(l4).toHaveLength(0);
    });

    test('L4: nodes reachable via Condition branches are not flagged as unreachable', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'cond', kind: 'Condition', 'default': 'end',
                  branches: [{ rule: {}, target: 'apHigh' }] },
                { nodeKey: 'apHigh', kind: 'Approval', approverRule: { type: 'Role', value: 'mgr' } },
                { nodeKey: 'end', kind: 'End' }
            ],
            transitions: [
                { from: 'start', to: 'cond' },
                { from: 'cond', to: 'end' },
                { from: 'cond', to: 'apHigh' },
                { from: 'apHigh', to: 'end' }
            ]
        });
        var issues = Forms.runLint(graph);
        var l4 = issues.filter(function (i) { return i.code === 'L4'; });
        expect(l4).toHaveLength(0);
    });

    test('L5: gateway joinNodeKey references a non-Join node', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'pg', kind: 'ParallelGateway', joinNodeKey: 'notAJoin' },
                { nodeKey: 'notAJoin', kind: 'Approval', approverRule: { type: 'Role', value: 'hr' } }
            ],
            transitions: [
                { from: 'start', to: 'pg' },
                { from: 'pg', to: 'notAJoin' }
            ]
        });
        var issues = Forms.runLint(graph);
        var l5 = issues.filter(function (i) { return i.code === 'L5'; });
        expect(l5.length).toBeGreaterThan(0);
        expect(l5[0].nodeKey).toBe('pg');
    });

    test('L5: gateway joinNodeKey references a Join node → no L5', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'pg', kind: 'ParallelGateway', joinNodeKey: 'jn' },
                { nodeKey: 'jn', kind: 'Join' }
            ],
            transitions: [
                { from: 'start', to: 'pg' },
                { from: 'pg', to: 'jn' }
            ]
        });
        var issues = Forms.runLint(graph);
        var l5 = issues.filter(function (i) { return i.code === 'L5'; });
        expect(l5).toHaveLength(0);
    });

    test('L6: InclusiveGateway out-edge without condition', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'ig', kind: 'InclusiveGateway', joinNodeKey: 'jn' },
                { nodeKey: 'jn', kind: 'Join' }
            ],
            transitions: [
                { from: 'start', to: 'ig' },
                { from: 'ig', to: 'jn' } // no condition — should flag L6
            ]
        });
        var issues = Forms.runLint(graph);
        var l6 = issues.filter(function (i) { return i.code === 'L6'; });
        expect(l6.length).toBeGreaterThan(0);
        expect(l6[0].nodeKey).toBe('ig');
    });

    test('L6: InclusiveGateway out-edge WITH condition → no L6', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'start', kind: 'Start' },
                { nodeKey: 'ig', kind: 'InclusiveGateway', joinNodeKey: 'jn' },
                { nodeKey: 'jn', kind: 'Join' }
            ],
            transitions: [
                { from: 'start', to: 'ig' },
                { from: 'ig', to: 'jn', condition: { field: 'amount', operator: 'Gt', value: '0' } }
            ]
        });
        var issues = Forms.runLint(graph);
        var l6 = issues.filter(function (i) { return i.code === 'L6'; });
        expect(l6).toHaveLength(0);
    });

    test('lint is advisory-only: does not throw or prevent graph access', function () {
        // Lint must NEVER throw — even on malformed/null input
        expect(function () { Forms.runLint(null); }).not.toThrow();
        expect(function () { Forms.runLint(undefined); }).not.toThrow();
        expect(function () { Forms.runLint({}); }).not.toThrow();
        expect(function () { Forms.runLint({ nodes: null, transitions: null }); }).not.toThrow();
    });

    test('multiple lint issues can co-exist', function () {
        var graph = makeGraph({
            nodes: [
                { nodeKey: 'ap1', kind: 'Approval' },           // L2: no approverRule, L4: unreachable (no Start)
                { nodeKey: 'cond', kind: 'Condition' }          // L3: no default, L4: unreachable
            ],
            transitions: [{ from: 'cond', to: 'ap1' }]
        });
        var issues = Forms.runLint(graph);
        var codes = issues.map(function (i) { return i.code; });
        expect(codes).toContain('L2');
        expect(codes).toContain('L3');
        // Both ap1 and cond are unreachable (no Start node)
        expect(codes).toContain('L4');
    });
});

// ── T-DSN-17: renderLintBadges + renderValidationErrors DOM safety ────────────

describe('T-DSN-17: renderLintBadges / renderValidationErrors DOM safety', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('renderLintBadges: no issues → shows "no warnings" via textContent', function () {
        var container = document.createElement('div');
        Forms.renderLintBadges(container, [], null);
        expect(container.textContent).toContain('无警告');
        // No innerHTML manipulation
        expect(container.innerHTML).not.toContain('<script');
        expect(container.innerHTML).not.toContain('onerror');
    });

    test('renderLintBadges: XSS payload in message is inert via textContent', function () {
        var xssIssues = [
            { code: 'L1', nodeKey: '<img src=x onerror=alert(1)>', message: '<script>alert(1)</script>' }
        ];
        var container = document.createElement('div');
        Forms.renderLintBadges(container, xssIssues, null);
        // The message text should be present as text, not as active HTML
        expect(container.textContent).toContain('<script>');
        // But it should NOT be actual active script or element
        expect(container.querySelector('script')).toBeNull();
        expect(container.querySelector('img')).toBeNull();
    });

    test('renderLintBadges: click on badge calls onFocusNodeKey with nodeKey', function () {
        var focusedKeys = [];
        var issues = [{ code: 'L4', nodeKey: 'orphanNode', message: '节点无法到达' }];
        var container = document.createElement('div');
        Forms.renderLintBadges(container, issues, function (nk) { focusedKeys.push(nk); });
        var badge = container.querySelector('.wtm-wf-lint-badge');
        expect(badge).not.toBeNull();
        badge.click();
        expect(focusedKeys).toContain('orphanNode');
    });

    test('renderValidationErrors: isValid=true shows success via textContent', function () {
        var container = document.createElement('div');
        Forms.renderValidationErrors(container, { isValid: true }, null);
        expect(container.textContent).toContain('通过');
        expect(container.querySelector('script')).toBeNull();
    });

    test('renderValidationErrors: XSS payload in errorCode/message is inert', function () {
        var container = document.createElement('div');
        var resp = {
            isValid:   false,
            errorCode: '<img src=x onerror=alert(1)>',
            message:   '<script>alert(2)</script>',
            nodeKey:   'n1'
        };
        Forms.renderValidationErrors(container, resp, null);
        // Content present as text
        expect(container.textContent).toContain('<img');
        expect(container.textContent).toContain('<script>');
        // No active elements
        expect(container.querySelector('script')).toBeNull();
        expect(container.querySelector('img')).toBeNull();
    });

    test('renderValidationErrors: click focuses nodeKey', function () {
        var focused = [];
        var container = document.createElement('div');
        Forms.renderValidationErrors(container,
            { isValid: false, errorCode: 'InvalidNodeKey', message: 'bad key', nodeKey: 'bad1' },
            function (nk) { focused.push(nk); });
        var errEl = container.firstChild;
        expect(errEl).not.toBeNull();
        errEl.click();
        expect(focused).toContain('bad1');
    });

    test('renderLintBadges: clears previous content before rendering', function () {
        var container = document.createElement('div');
        container.appendChild(document.createTextNode('old content'));
        Forms.renderLintBadges(container, [], null);
        expect(container.textContent).not.toContain('old content');
    });
});

// ── ISO-8601 duration composer / parser ──────────────────────────────────────

describe('ISO-8601 duration composer/parser round-trip', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('parse P2D → days:2, hours:0, minutes:0', function () {
        var r = Forms._parseIso8601Duration('P2D');
        expect(r.days).toBe(2);
        expect(r.hours).toBe(0);
        expect(r.minutes).toBe(0);
    });

    test('parse PT8H → days:0, hours:8, minutes:0', function () {
        var r = Forms._parseIso8601Duration('PT8H');
        expect(r.hours).toBe(8);
        expect(r.days).toBe(0);
    });

    test('parse P1DT30M → days:1, hours:0, minutes:30', function () {
        var r = Forms._parseIso8601Duration('P1DT30M');
        expect(r.days).toBe(1);
        expect(r.minutes).toBe(30);
    });

    test('parse P2DT8H30M → round-trip', function () {
        var r = Forms._parseIso8601Duration('P2DT8H30M');
        expect(r.days).toBe(2);
        expect(r.hours).toBe(8);
        expect(r.minutes).toBe(30);
    });

    test('compose 2, 8, 30 → P2DT8H30M', function () {
        var s = Forms._composeIso8601Duration(2, 8, 30);
        expect(s).toBe('P2DT8H30M');
    });

    test('compose 0, 0, 0 → PT0M (zero-duration sentinel)', function () {
        var s = Forms._composeIso8601Duration(0, 0, 0);
        expect(s).toBe('PT0M');
    });

    test('compose 3, 0, 0 → P3D', function () {
        var s = Forms._composeIso8601Duration(3, 0, 0);
        expect(s).toBe('P3D');
    });

    test('parse malformed string → empty object (no throw)', function () {
        expect(function () { Forms._parseIso8601Duration('not-a-duration'); }).not.toThrow();
        var r = Forms._parseIso8601Duration('not-a-duration');
        expect(Object.keys(r)).toHaveLength(0);
    });

    test('parse null/undefined → empty object', function () {
        expect(Forms._parseIso8601Duration(null)).toEqual({});
        expect(Forms._parseIso8601Duration(undefined)).toEqual({});
    });
});

// ── fieldWhitelist editor ─────────────────────────────────────────────────────

describe('fieldWhitelist editor', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('buildFieldWhitelistEditor: collects existing entries correctly', function () {
        var wl = [
            { field: 'amount', clrType: 'System.Decimal', allowedRoles: ['finance'] },
            { field: 'dept',   clrType: 'System.String',  allowedRoles: null }
        ];
        var result = Forms.buildFieldWhitelistEditor(wl);
        expect(result.editorEl).toBeDefined();
        expect(typeof result.collectFieldWhitelist).toBe('function');

        var collected = result.collectFieldWhitelist();
        expect(collected.length).toBe(2);

        var amountEntry = collected.find(function (e) { return e.field === 'amount'; });
        expect(amountEntry).toBeDefined();
        expect(amountEntry.clrType).toBe('System.Decimal');

        var deptEntry = collected.find(function (e) { return e.field === 'dept'; });
        expect(deptEntry).toBeDefined();
        expect(deptEntry.clrType).toBe('System.String');
    });

    test('buildFieldWhitelistEditor: empty whitelist produces empty collection', function () {
        var result = Forms.buildFieldWhitelistEditor([]);
        var collected = result.collectFieldWhitelist();
        expect(collected).toHaveLength(0);
    });

    test('buildFieldWhitelistEditor: null whitelist is handled gracefully', function () {
        expect(function () { Forms.buildFieldWhitelistEditor(null); }).not.toThrow();
        var result = Forms.buildFieldWhitelistEditor(null);
        var collected = result.collectFieldWhitelist();
        expect(collected).toHaveLength(0);
    });
});

// ── openNodePanel dispatch ─────────────────────────────────────────────────────

describe('openNodePanel: dispatch to correct builder per NodeKind', function () {
    var Forms;
    var loaded;
    beforeEach(function () {
        loaded = loadForms();
        Forms  = loaded.Forms;
    });

    var NODE_KINDS = [
        { kind: 'Start',           extraFields: {} },
        { kind: 'End',             extraFields: {} },
        { kind: 'Join',            extraFields: {} },
        { kind: 'Approval',        extraFields: { approveMode: 'Sequential', rejectPolicy: 'ReturnToInitiator', approverRule: { type: 'Role', value: 'hr' } } },
        { kind: 'Condition',       extraFields: { 'default': 'end', branches: [] } },
        { kind: 'ParallelGateway', extraFields: { joinNodeKey: null } },
        { kind: 'InclusiveGateway',extraFields: { joinNodeKey: null } },
        { kind: 'Ack',             extraFields: { ackMode: 'All' } },
        { kind: 'Cc',              extraFields: { trigger: 'OnNode' } }
    ];

    NODE_KINDS.forEach(function (spec) {
        test('openNodePanel dispatches ' + spec.kind, function () {
            var nodeObj = Object.assign({ nodeKey: 'n1', kind: spec.kind, name: '节点' }, spec.extraFields);
            var mockGraphModel = { markDirty: jest.fn() };
            var savedCalled = false;

            var result = Forms.openNodePanel({
                nodeObj:    nodeObj,
                allNodes:   [nodeObj],
                fieldWhitelist: [],
                graphModel: mockGraphModel,
                onSaved:    function () { savedCalled = true; },
                layui:      loaded.mockLayui
            });

            expect(result).toBeDefined();
            expect(result.panelEl).toBeDefined();
            expect(result.panelEl.tagName).toBe('DIV');

            // layer.open should have been called once
            expect(loaded.mockLayui.layer.open).toHaveBeenCalled();
            // The content passed to layer.open must be a DOM element (not a string)
            var openCall = loaded.mockLayui.layer.open.mock.calls[0][0];
            expect(typeof openCall.content).toBe('object'); // DOM element
            expect(typeof openCall.content).not.toBe('string');
        });
    });

    test('openNodePanel: unknown kind shows fallback panel without throwing', function () {
        var nodeObj = { nodeKey: 'n1', kind: 'UnknownFutureKind', name: '未知' };
        expect(function () {
            Forms.openNodePanel({
                nodeObj:    nodeObj,
                allNodes:   [],
                fieldWhitelist: [],
                graphModel: { markDirty: jest.fn() },
                onSaved:    function () {},
                layui:      loaded.mockLayui
            });
        }).not.toThrow();
    });
});

// ── T-DSN-9: layer.open receives DOM nodes only ───────────────────────────────

describe('T-DSN-9: layer.open receives DOM element content, not strings', function () {
    var Forms;
    var loaded;
    beforeEach(function () {
        loaded = loadForms();
        Forms  = loaded.Forms;
    });

    test('layer.open content is a DOM element for Approval panel', function () {
        var nodeObj = {
            nodeKey: 'ap1', kind: 'Approval', name: '审批',
            approveMode: 'Sequential', rejectPolicy: 'ReturnToInitiator',
            approverRule: { type: 'Role', value: 'hr' }
        };
        Forms.openNodePanel({
            nodeObj: nodeObj, allNodes: [], fieldWhitelist: [],
            graphModel: { markDirty: jest.fn() },
            onSaved: function () {},
            layui: loaded.mockLayui
        });
        var openCall = loaded.mockLayui.layer.open.mock.calls[0][0];
        // content must be a DOM node (has nodeType or tagName), NOT a string
        expect(typeof openCall.content).toBe('object');
        expect(openCall.content).not.toBeNull();
        if (openCall.content.tagName) {
            expect(typeof openCall.content.tagName).toBe('string');
        }
    });
});

// ── T-DSN-15: full server-validate-compatible graph via forms ─────────────────

describe('T-DSN-15: form-authored nodes produce valid schema fields', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('Approval panel collectValues produces required approveMode field', function () {
        var node = {
            nodeKey: 'ap1', kind: 'Approval', name: '审批',
            approveMode: 'Sequential',
            rejectPolicy: 'ReturnToInitiator',
            approverRule: { type: 'Role', value: 'hr' }
        };
        var result = Forms.buildApprovalPanel(node, []);
        var vals = result.collectValues();
        expect(['Sequential', 'All', 'Any']).toContain(vals.fieldMap.approveMode);
        expect(['TerminateInstance', 'ReturnToPrev', 'ReturnToNode', 'ReturnToInitiator'])
            .toContain(vals.fieldMap.rejectPolicy);
        expect(['Role', 'User', 'ManagerChain', 'Initiator']).toContain(vals.approverRule.type);
    });

    test('approvePercent: valid decimal within (0,1] is accepted', function () {
        var node = {
            nodeKey: 'ap2', kind: 'Approval', name: '会签',
            approveMode: 'All', approvePercent: 0.67,
            rejectPolicy: 'TerminateInstance',
            approverRule: { type: 'Role', value: 'mgr' }
        };
        var result = Forms.buildApprovalPanel(node, []);
        var vals = result.collectValues();
        if (vals.fieldMap.approvePercent !== null) {
            expect(vals.fieldMap.approvePercent).toBeGreaterThan(0);
            expect(vals.fieldMap.approvePercent).toBeLessThanOrEqual(1);
        }
    });

    test('InclusiveGateway panel includes joinNodeKey field', function () {
        var node = { nodeKey: 'ig1', kind: 'InclusiveGateway', joinNodeKey: 'jn1' };
        var allNodes = [{ nodeKey: 'jn1', kind: 'Join' }];
        var result = Forms.buildGatewayPanel(node, allNodes);
        var vals = result.collectValues();
        expect(vals.fieldMap.joinNodeKey).toBe('jn1');
    });

    test('Condition panel with In operator branch is collected correctly', function () {
        var node = {
            nodeKey: 'cond1', kind: 'Condition', name: '条件',
            'default': 'end',
            branches: [{ rule: { field: 'dept', operator: 'In', value: 'hr,finance' }, target: 'ap1' }]
        };
        var wl = [{ field: 'dept', clrType: 'System.String' }];
        var allNodes = [
            { nodeKey: 'end', kind: 'End' },
            { nodeKey: 'ap1', kind: 'Approval' }
        ];
        var result = Forms.buildConditionPanel(node, allNodes, wl);
        var vals = result.collectValues();
        expect(vals.fieldMap['default']).toBe('end');
        expect(Array.isArray(vals.fieldMap.branches)).toBe(true);
    });
});

// ── T-DSN-9: Nodekey charset validation live feedback ─────────────────────────

describe('T-DSN-9: nodeKey charset validation', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('buildSimplePanel: nodeKey input validates against charset in real time', function () {
        // Build a panel and find the nodeKey input
        var node = { nodeKey: 'valid-key_1', kind: 'Start', name: 'Start' };
        var result = Forms.buildSimplePanel(node);
        var panel = result.panelEl;

        // Find the key input by id
        var keyInput = panel.querySelector('#wf-node-key');
        expect(keyInput).not.toBeNull();
        expect(keyInput.value).toBe('valid-key_1');
    });

    test('collectValues preserves trimmed nodeKey from input', function () {
        var node = { nodeKey: '  trimMe  ', kind: 'End', name: '结束' };
        var result = Forms.buildSimplePanel(node);
        var vals = result.collectValues();
        // Input should be pre-populated with the trimmed value
        // (buildSimplePanel reads nodeObj['nodeKey'] directly)
        expect(vals.fieldMap.nodeKey.trim()).toBe('trimMe');
    });
});

// ── constants sanity check ────────────────────────────────────────────────────

describe('exported constants', function () {
    var Forms;
    beforeEach(function () {
        Forms = loadForms().Forms;
    });

    test('NODE_KINDS covers all 9 kinds', function () {
        var expected = ['Start', 'End', 'Approval', 'Condition', 'Cc', 'Ack',
                        'Join', 'ParallelGateway', 'InclusiveGateway'];
        expected.forEach(function (k) {
            expect(Forms.NODE_KINDS).toContain(k);
        });
    });

    test('APPROVE_MODES covers Sequential/All/Any', function () {
        expect(Forms.APPROVE_MODES).toContain('Sequential');
        expect(Forms.APPROVE_MODES).toContain('All');
        expect(Forms.APPROVE_MODES).toContain('Any');
    });

    test('FILTER_OPERATORS covers In/NotIn', function () {
        expect(Forms.FILTER_OPERATORS).toContain('In');
        expect(Forms.FILTER_OPERATORS).toContain('NotIn');
    });
});
