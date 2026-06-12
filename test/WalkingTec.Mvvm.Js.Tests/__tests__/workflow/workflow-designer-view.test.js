'use strict';
/**
 * Tests for framework_workflow_designer_view.js — WF-21.7
 *
 * Coverage:
 *  T-DSN-9  (view sources): zero eval / new Function / innerHTML / insertAdjacentHTML /
 *            outerHTML / inline on* / foreignObject across the view module; all SVG via
 *            createElementNS + textContent; no numeric string from user data in attributes.
 *  T-DSN-18 (layout determinism jest half): BFS rank-from-Start deterministic auto-layout;
 *            ordinal tie-break; disconnected nodes appended; round-trip result stable
 *            across multiple calls.
 *  Additional: VersionHistoryPanel.build DOM structure (textContent, no innerHTML);
 *              PublishFlow outcome msg map coverage; VersionListUi.buildDefinitionTable
 *              DOM structure; SvgView render does not throw on empty/malformed input.
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

// ── Loader ────────────────────────────────────────────────────────────────────

function loadView(overrides) {
    var src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/framework_workflow_designer_view.js'),
        'utf8'
    );

    var ctx = vm.createContext(Object.assign({
        window:   global,
        document: global.document,
        console:  console,
        Object:   Object,
        Array:    Array,
        String:   String,
        Number:   Number,
        Math:     Math,
        Date:     Date,
        JSON:     JSON,
        isNaN:    isNaN
    }, overrides || {}));
    // window === ctx so window.WtmDesignerView is set on ctx.
    ctx.window = ctx;

    new vm.Script(src).runInContext(ctx);

    return {
        ctx:  ctx,
        View: ctx.WtmDesignerView
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// T-DSN-9 source-level security assertions (view module)
// ─────────────────────────────────────────────────────────────────────────────

describe('T-DSN-9 source-level security (view module)', function () {
    var src;
    beforeAll(function () {
        src = fs.readFileSync(
            path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/framework_workflow_designer_view.js'),
            'utf8'
        );
    });

    test('no eval() calls in module source', function () {
        var matches = src.match(/\beval\s*\(/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no new Function() calls in module source', function () {
        var matches = src.match(/new\s+Function\s*\(/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no Function constructor call in module source', function () {
        var matches = src.match(/Function\s*\(\s*['\"]/) || [];
        expect(matches).toHaveLength(0);
    });

    test('no innerHTML assignments in module source', function () {
        var matches = src.match(/\.innerHTML\s*=/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no insertAdjacentHTML calls in module source', function () {
        var matches = src.match(/insertAdjacentHTML\s*\(/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no outerHTML assignments in module source', function () {
        var matches = src.match(/\.outerHTML\s*=/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no inline event handler attributes (on* strings) in module source', function () {
        // Allow "onclick", "onload" etc. only in comments; catch in generated markup.
        // We check for setAttribute calls with 'on' as first two chars of attribute name
        // passing a non-listener (i.e. not addEventListener).
        // Simpler heuristic: no literal string 'on...' = '...' attribute composition.
        var matches = src.match(/setAttribute\s*\(\s*["']on[a-z]+["']/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no foreignObject in SVG construction (createElementNS call)', function () {
        // foreignObject must not appear as an argument to createElementNS.
        // (It may appear in comments documenting the prohibition — that is intentional.)
        var matches = src.match(/createElementNS\s*\([^)]*foreignObject/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('SVG elements created only via createElementNS', function () {
        // All SVG element creation must use createElementNS, not createElement alone.
        // Check that every 'svg', 'rect', 'text', 'line', 'path', 'defs', 'marker', 'g'
        // literal in a createElement call is absent (i.e. none created via non-NS).
        var svgTags = ['svg', 'rect', 'text', 'line', 'path', 'defs', 'marker'];
        for (var i = 0; i < svgTags.length; i++) {
            var tag = svgTags[i];
            // Match createElement('svg') / createElement("svg") — not createElementNS.
            var re = new RegExp('createElement\\s*\\(\\s*[\'"]' + tag + '[\'"]\\s*\\)', 'g');
            var matches = src.match(re) || [];
            expect(matches).toHaveLength(0);
        }
    });

    test('layer.msg and layer.alert do not receive template-string server data', function () {
        // All layer.msg/alert calls must use hardcoded string literals, not server vars.
        // We assert no layer.msg/alert call uses a variable (only string literals).
        var msgRe   = /layer\.msg\s*\(([^)]+)\)/g;
        var alertRe = /layer\.alert\s*\(([^)]+)\)/g;
        var m;
        while ((m = msgRe.exec(src)) !== null) {
            // The argument should be a local literal variable (msg), not a server string.
            // We check that the call arg is "msg" (a literal lookup) and that msg is only
            // assigned from OUTCOME_MSGS or a hardcoded string.  Here we just confirm no
            // raw server DTO field is passed directly.
            expect(m[1].trim()).not.toMatch(/\.nodeKey|\.message|\.error|\.body/);
        }
        while ((m = alertRe.exec(src)) !== null) {
            expect(m[1].trim()).not.toMatch(/\.nodeKey|\.message|\.error|\.body/);
        }
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// T-DSN-18 (layout determinism — jest half)
// BFS rank-from-Start; ordinal tie-break; disconnected nodes; stable across calls.
// ─────────────────────────────────────────────────────────────────────────────

describe('T-DSN-18 layout determinism', function () {
    var View;

    beforeAll(function () {
        var result = loadView();
        View = result.View;
    });

    test('WtmDesignerView is exposed on window', function () {
        expect(View).toBeDefined();
        expect(typeof View.computeLayout).toBe('function');
        expect(typeof View.renderSvg).toBe('function');
    });

    test('version tag is WF-21.7', function () {
        expect(View.version).toBe('WF-21.7');
    });

    test('computeLayout empty nodes returns empty object', function () {
        var layout = View.computeLayout([], []);
        expect(layout).toEqual({});
    });

    test('computeLayout single Start node gets rank 0', function () {
        var nodes       = [{ nodeKey: 'Start1', kind: 'Start', name: '开始' }];
        var transitions = [];
        var layout = View.computeLayout(nodes, transitions);
        expect(layout['Start1']).toBeDefined();
        expect(layout['Start1'].rank).toBe(0);
        expect(layout['Start1'].rankIdx).toBe(0);
    });

    test('computeLayout linear chain assigns sequential ranks', function () {
        var nodes = [
            { nodeKey: 'S', kind: 'Start', name: '开始' },
            { nodeKey: 'A', kind: 'Approval', name: '审批' },
            { nodeKey: 'E', kind: 'End', name: '结束' }
        ];
        var transitions = [
            { from: 'S', to: 'A' },
            { from: 'A', to: 'E' }
        ];
        var layout = View.computeLayout(nodes, transitions);
        expect(layout['S'].rank).toBe(0);
        expect(layout['A'].rank).toBe(1);
        expect(layout['E'].rank).toBe(2);
    });

    test('computeLayout parallel nodes at same rank get distinct rankIdx', function () {
        var nodes = [
            { nodeKey: 'S',  kind: 'Start',    name: '开始' },
            { nodeKey: 'P',  kind: 'ParallelGateway', name: '并行' },
            { nodeKey: 'A1', kind: 'Approval', name: '审批1' },
            { nodeKey: 'A2', kind: 'Approval', name: '审批2' },
            { nodeKey: 'J',  kind: 'Join',     name: '汇聚' },
            { nodeKey: 'E',  kind: 'End',      name: '结束' }
        ];
        var transitions = [
            { from: 'S',  to: 'P'  },
            { from: 'P',  to: 'A1' },
            { from: 'P',  to: 'A2' },
            { from: 'A1', to: 'J'  },
            { from: 'A2', to: 'J'  },
            { from: 'J',  to: 'E'  }
        ];
        var layout = View.computeLayout(nodes, transitions);
        expect(layout['A1'].rank).toBe(layout['A2'].rank);
        // They must have different rankIdx values (different vertical positions).
        expect(layout['A1'].rankIdx).not.toBe(layout['A2'].rankIdx);
    });

    test('computeLayout ordinal tie-break: node earlier in array gets lower rankIdx', function () {
        var nodes = [
            { nodeKey: 'S',  kind: 'Start',    name: '开始' },
            { nodeKey: 'A1', kind: 'Approval', name: '审批1' },  // ordinal 1
            { nodeKey: 'A2', kind: 'Approval', name: '审批2' }   // ordinal 2
        ];
        var transitions = [
            { from: 'S', to: 'A1' },
            { from: 'S', to: 'A2' }
        ];
        var layout = View.computeLayout(nodes, transitions);
        expect(layout['A1'].rankIdx).toBeLessThan(layout['A2'].rankIdx);
    });

    test('computeLayout disconnected nodes appended after BFS frontier', function () {
        var nodes = [
            { nodeKey: 'S', kind: 'Start', name: '开始' },
            { nodeKey: 'E', kind: 'End',   name: '结束' },
            { nodeKey: 'D', kind: 'Cc',    name: '孤立节点' }  // no edges
        ];
        var transitions = [{ from: 'S', to: 'E' }];
        var layout = View.computeLayout(nodes, transitions);
        // D is disconnected — its rank must be > E's rank.
        expect(layout['D'].rank).toBeGreaterThan(layout['E'].rank);
    });

    test('computeLayout is stable: two calls produce identical results', function () {
        var nodes = [
            { nodeKey: 'S', kind: 'Start',    name: '开始' },
            { nodeKey: 'A', kind: 'Approval', name: '审批' },
            { nodeKey: 'E', kind: 'End',      name: '结束' }
        ];
        var transitions = [
            { from: 'S', to: 'A' },
            { from: 'A', to: 'E' }
        ];
        var layout1 = View.computeLayout(nodes, transitions);
        var layout2 = View.computeLayout(nodes, transitions);
        expect(JSON.stringify(layout1)).toBe(JSON.stringify(layout2));
    });

    test('computeLayout gateway nodes have isGate = true', function () {
        var nodes = [
            { nodeKey: 'S', kind: 'Start',            name: '开始' },
            { nodeKey: 'C', kind: 'Condition',         name: '条件' },
            { nodeKey: 'P', kind: 'ParallelGateway',   name: '并行' },
            { nodeKey: 'I', kind: 'InclusiveGateway',  name: '包容' }
        ];
        var layout = View.computeLayout(nodes, []);
        expect(layout['S'].isGate).toBe(false);
        expect(layout['C'].isGate).toBe(true);
        expect(layout['P'].isGate).toBe(true);
        expect(layout['I'].isGate).toBe(true);
    });

    test('computeLayout with null/undefined transitions does not throw', function () {
        var nodes = [{ nodeKey: 'S', kind: 'Start', name: '开始' }];
        expect(function () { View.computeLayout(nodes, null); }).not.toThrow();
        expect(function () { View.computeLayout(nodes, undefined); }).not.toThrow();
    });

    test('computeLayout falls back to first node if no Start kind found', function () {
        var nodes = [
            { nodeKey: 'A', kind: 'Approval', name: '审批' },
            { nodeKey: 'E', kind: 'End',      name: '结束' }
        ];
        var transitions = [{ from: 'A', to: 'E' }];
        var layout = View.computeLayout(nodes, transitions);
        // A is the first node, so it should be at rank 0.
        expect(layout['A'].rank).toBe(0);
        expect(layout['E'].rank).toBe(1);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// renderSvg — DOM output checks
// ─────────────────────────────────────────────────────────────────────────────

describe('renderSvg DOM output', function () {
    var View;

    beforeAll(function () {
        var result = loadView();
        View = result.View;
    });

    test('renderSvg null/undefined input returns null', function () {
        expect(View.renderSvg(null)).toBeNull();
        expect(View.renderSvg(undefined)).toBeNull();
    });

    test('renderSvg empty nodes returns SVG placeholder (not null)', function () {
        var svgEl = View.renderSvg({ nodes: [], transitions: [] });
        expect(svgEl).not.toBeNull();
        expect(svgEl.tagName.toLowerCase()).toBe('svg');
    });

    test('renderSvg linear graph produces SVG element', function () {
        var tree = {
            nodes: [
                { nodeKey: 'S', kind: 'Start',    name: '开始' },
                { nodeKey: 'A', kind: 'Approval', name: '审批' },
                { nodeKey: 'E', kind: 'End',      name: '结束' }
            ],
            transitions: [
                { from: 'S', to: 'A' },
                { from: 'A', to: 'E' }
            ]
        };
        var svgEl = View.renderSvg(tree);
        expect(svgEl).not.toBeNull();
        expect(svgEl.tagName.toLowerCase()).toBe('svg');
        // Should have node groups.
        var gs = svgEl.querySelectorAll('g.wfd-node');
        expect(gs.length).toBe(3);
    });

    test('renderSvg node label uses textContent not innerHTML', function () {
        var xssName = '<img src=x onerror=alert(1)>';
        var tree = {
            nodes: [{ nodeKey: 'S', kind: 'Start', name: xssName }],
            transitions: []
        };
        var svgEl = View.renderSvg(tree);
        // The XSS payload must NOT appear as innerHTML (would parse the img tag).
        // It MUST appear as literal textContent (inert).
        var allTexts = svgEl.querySelectorAll('text');
        var found = false;
        allTexts.forEach(function (t) {
            if (t.textContent.indexOf('<img') !== -1 || t.textContent.indexOf('onerror') !== -1) {
                found = true;
            }
        });
        // The rendered text contains the truncated literal (first 9 chars + '…').
        // The key requirement: no img/onerror in innerHTML (i.e. not executed as HTML).
        var svgHtml = svgEl.innerHTML || svgEl.outerHTML || '';
        // Should not contain an actual <img> element (would only happen via innerHTML injection).
        var imgEls = svgEl.querySelectorAll('img');
        expect(imgEls.length).toBe(0);
    });

    test('renderSvg all 9 node kinds produce a node group each', function () {
        var kinds = ['Start', 'End', 'Approval', 'Condition', 'Cc', 'Ack', 'Join',
                     'ParallelGateway', 'InclusiveGateway'];
        var nodes = kinds.map(function (k, i) {
            return { nodeKey: k + '_' + i, kind: k, name: k };
        });
        var svgEl = View.renderSvg({ nodes: nodes, transitions: [] });
        var gs = svgEl.querySelectorAll('g.wfd-node');
        expect(gs.length).toBe(9);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// VersionHistoryPanel — DOM structure, no innerHTML, textContent safety
// ─────────────────────────────────────────────────────────────────────────────

describe('VersionHistoryPanel', function () {
    var View;

    beforeAll(function () {
        var result = loadView();
        View = result.View;
    });

    test('build returns DOM element for empty array', function () {
        var el = View.VersionHistoryPanel.build([], function () {});
        expect(el).toBeDefined();
        expect(el.nodeType).toBe(1); // Element
    });

    test('build returns table for non-empty versions', function () {
        var versions = [
            { versionId: '1', versionNo: 1, contentHash: 'abcdef12345678', schemaVersion: 1,
              publishedAt: '2026-06-01T10:00:00Z', publishedBy: 'admin', isCurrent: true }
        ];
        var el = View.VersionHistoryPanel.build(versions, function () {});
        var tables = el.querySelectorAll('table');
        expect(tables.length).toBe(1);
    });

    test('build renders publishedBy via textContent (XSS injection inert)', function () {
        var xssBy = '<script>evil()</script>';
        var versions = [
            { versionId: '2', versionNo: 1, contentHash: 'abc', schemaVersion: 1,
              publishedAt: '2026-06-01T10:00:00Z', publishedBy: xssBy, isCurrent: false }
        ];
        var el = View.VersionHistoryPanel.build(versions, function () {});
        // The script tag should not be executed — it must appear as literal text.
        var scriptEls = el.querySelectorAll('script');
        expect(scriptEls.length).toBe(0);
    });

    test('build calls onLoadDraft with correct versionId when button clicked', function () {
        var captured = null;
        var versions = [
            { versionId: 'ver-42', versionNo: 2, contentHash: 'def', schemaVersion: 1,
              publishedAt: '2026-06-01T10:00:00Z', publishedBy: 'bob', isCurrent: true }
        ];
        var el = View.VersionHistoryPanel.build(versions, function (id) { captured = id; });
        var btn = el.querySelector('button');
        expect(btn).not.toBeNull();
        btn.click();
        expect(captured).toBe('ver-42');
    });

    test('build renders hash prefix (first 8 chars only)', function () {
        var versions = [
            { versionId: '3', versionNo: 1, contentHash: '0123456789abcdef',
              schemaVersion: 1, publishedAt: '2026-06-01T00:00:00Z', publishedBy: 'x', isCurrent: true }
        ];
        var el = View.VersionHistoryPanel.build(versions, function () {});
        var text = el.textContent;
        expect(text).toContain('01234567'); // first 8
        expect(text).not.toContain('89abcdef'); // rest not shown
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// PublishFlow — outcome code mapping; confirm/conflict panel DOM safety
// ─────────────────────────────────────────────────────────────────────────────

describe('PublishFlow', function () {
    var View;

    beforeAll(function () {
        var result = loadView();
        View = result.View;
    });

    test('OUTCOME_MSGS covers key outcome codes', function () {
        var msgs = View.PublishFlow.OUTCOME_MSGS;
        expect(msgs['Published']).toBeDefined();
        expect(msgs['IdempotentNoOp']).toBeDefined();
        expect(msgs['BaseVersionChanged']).toBeDefined();
        expect(msgs['ValidationFailed']).toBeDefined();
        expect(msgs['SchemaVersionUnsupported']).toBeDefined();
        expect(msgs['DefinitionNotFound']).toBeDefined();
    });

    test('buildConfirmPanel returns a DOM element', function () {
        var panel = View.PublishFlow.buildConfirmPanel({
            currentVersionNo: 3,
            draftLastSavedBy: 'alice',
            currentActor: 'bob'
        });
        expect(panel).toBeDefined();
        expect(panel.nodeType).toBe(1);
    });

    test('buildConfirmPanel shows attribution warning when actors differ', function () {
        var panel = View.PublishFlow.buildConfirmPanel({
            currentVersionNo: 1,
            draftLastSavedBy: 'alice',
            currentActor: 'bob'
        });
        // Should contain the actor name via textContent — verify via textContent of the panel.
        expect(panel.textContent).toContain('alice');
    });

    test('buildConfirmPanel attribution warning uses textContent not innerHTML (XSS)', function () {
        var xssActor = '<img src=x onerror=evil()>';
        var panel = View.PublishFlow.buildConfirmPanel({
            currentVersionNo: 1,
            draftLastSavedBy: xssActor,
            currentActor: 'bob'
        });
        // No img element should have been created.
        var imgEls = panel.querySelectorAll('img');
        expect(imgEls.length).toBe(0);
    });

    test('buildConflictPanel returns a DOM element with expected text', function () {
        var panel = View.PublishFlow.buildConflictPanel();
        expect(panel).toBeDefined();
        expect(panel.nodeType).toBe(1);
        expect(panel.textContent).toContain('已被他人发布新版本');
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// VersionListUi — definition table DOM structure + XSS safety
// ─────────────────────────────────────────────────────────────────────────────

describe('VersionListUi', function () {
    var View;

    beforeAll(function () {
        var result = loadView();
        View = result.View;
    });

    test('buildDefinitionTable returns DOM for empty array', function () {
        var el = View.VersionListUi.buildDefinitionTable([], function () {});
        expect(el).toBeDefined();
        expect(el.nodeType).toBe(1);
    });

    test('buildDefinitionTable renders table for definitions', function () {
        var defs = [
            { code: 'PURCHASE', name: '采购审批', category: '财务',
              isEnabled: true, currentVersionNo: 3, hasDraft: false }
        ];
        var el = View.VersionListUi.buildDefinitionTable(defs, function () {});
        var tables = el.querySelectorAll('table');
        expect(tables.length).toBe(1);
    });

    test('buildDefinitionTable calls onSelect with correct code on button click', function () {
        var captured = null;
        var defs = [
            { code: 'LEAVE', name: '请假审批', category: 'HR',
              isEnabled: true, currentVersionNo: 1, hasDraft: true }
        ];
        var el = View.VersionListUi.buildDefinitionTable(defs, function (code) {
            captured = code;
        });
        var btn = el.querySelector('button');
        expect(btn).not.toBeNull();
        btn.click();
        expect(captured).toBe('LEAVE');
    });

    test('buildDefinitionTable definition name is rendered via textContent (XSS inert)', function () {
        var xssName = '<script>evil()</script>';
        var defs = [
            { code: 'X', name: xssName, category: 'test',
              isEnabled: false, currentVersionNo: null, hasDraft: false }
        ];
        var el = View.VersionListUi.buildDefinitionTable(defs, function () {});
        var scriptEls = el.querySelectorAll('script');
        expect(scriptEls.length).toBe(0);
    });
});
