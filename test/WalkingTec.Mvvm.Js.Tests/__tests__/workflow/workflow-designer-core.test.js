'use strict';
/**
 * Tests for framework_workflow_designer_core.js — WF-21.5
 *
 * Coverage:
 *  T-DSN-3  (client half): RawNum lossless preservation — int64, lexical forms,
 *            deep nesting; RawNum unforgeable from JSON data; dirty-save preserves
 *            untouched number literals byte-exact.
 *  T-DSN-9  (core sources): zero eval / new Function / innerHTML / insertAdjacentHTML /
 *            outerHTML / inline on* across the module; designer.html zero inline scripts.
 *  T-DSN-12 (lockout): schemaVersion != 1 detected; isSchemaVersionSupported returns
 *            false for unsupported versions; GraphModel exposes getSchemaVersion correctly.
 *
 * Additional coverage:
 *  - WtmJsonRaw.parse round-trip (objects, arrays, strings, booleans, null)
 *  - WtmJsonRaw.stringify (RawNum emitted verbatim, composite structures)
 *  - WtmJsonRaw.readNum / writeNum helpers
 *  - GraphModel dirty tracking + byte-passthrough (T-DSN-1 client analog)
 *  - GraphModel setProperty / getProperty
 *  - GraphModel load with draft (draft marks dirty)
 *  - DesignerApi: mutating calls include X-WTM-WF-XSRF header
 *  - DesignerApi: saveDraft sets If-Match / If-None-Match headers correctly
 *  - DesignerApi: publish sets X-WTM-Expected-Hash header
 *  - LocalStorageCache: save / load / clear round-trip
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

// ── Loader ────────────────────────────────────────────────────────────────────

function loadCore(overrides) {
    var src = fs.readFileSync(
        path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/framework_workflow_designer_core.js'),
        'utf8'
    );

    // Minimal localStorage stub
    var _store = {};
    var mockLocalStorage = {
        getItem:    function (k) { return Object.prototype.hasOwnProperty.call(_store, k) ? _store[k] : null; },
        setItem:    function (k, v) { _store[k] = String(v); },
        removeItem: function (k) { delete _store[k]; },
        _store:     _store
    };

    var fetchCalls = [];
    var mockFetch = jest.fn(function (url, opts) {
        fetchCalls.push({ url: url, opts: opts });
        return Promise.resolve({ ok: true, json: function () { return Promise.resolve({}); } });
    });

    var ctx = vm.createContext(Object.assign({
        window:       {},
        console:      console,
        document:     global.document,
        localStorage: mockLocalStorage,
        fetch:        mockFetch,
        URLSearchParams: URLSearchParams,
        Date:         Date,
        Promise:      Promise,
        Object:       Object,
        Array:        Array,
        String:       String,
        Number:       Number,
        JSON:         JSON,
        Math:         Math,
        encodeURIComponent: encodeURIComponent
    }, overrides || {}));
    ctx.window = ctx;

    new vm.Script(src).runInContext(ctx);

    return {
        ctx:            ctx,
        core:           ctx.WtmDesignerCore,
        fetchCalls:     fetchCalls,
        mockFetch:      mockFetch,
        localStorage:   mockLocalStorage
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// T-DSN-9 (core sources): source-level security assertions
// ─────────────────────────────────────────────────────────────────────────────

describe('T-DSN-9 source-level security (core module)', function () {
    var src;
    beforeAll(function () {
        src = fs.readFileSync(
            path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/framework_workflow_designer_core.js'),
            'utf8'
        );
    });

    test('no eval() calls in module source', function () {
        // Match eval( but not "eval-free" in comments
        var matches = src.match(/\beval\s*\(/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no new Function() in module source', function () {
        var matches = src.match(/new\s+Function\s*\(/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no Function() constructor in module source', function () {
        // Direct Function("...") call
        var matches = src.match(/\bFunction\s*\(["'`]/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no innerHTML assignments in module source', function () {
        var matches = src.match(/\.innerHTML\s*=/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no insertAdjacentHTML in module source', function () {
        var matches = src.match(/insertAdjacentHTML/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no outerHTML assignments in module source', function () {
        var matches = src.match(/\.outerHTML\s*=/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no inline event handler attributes (on*=) in module source', function () {
        // Should not generate on* attribute strings like onclick=, onfocus=, etc.
        var matches = src.match(/setAttribute\s*\(\s*["']on\w+["']/g) || [];
        expect(matches).toHaveLength(0);
    });

    test('no string-based setTimeout (string eval)', function () {
        // setTimeout("...") is an eval vector
        var matches = src.match(/setTimeout\s*\(\s*["'`]/g) || [];
        expect(matches).toHaveLength(0);
    });
});

describe('T-DSN-9 source-level security (designer.html)', function () {
    var html;
    beforeAll(function () {
        html = fs.readFileSync(
            path.resolve(__dirname, '../../../../src/WalkingTec.Mvvm.WorkFlow/designer/designer.html'),
            'utf8'
        );
    });

    test('designer.html contains zero inline <script> blocks', function () {
        // <script> tags without a src attribute are inline
        var inlineScripts = html.match(/<script(?![^>]*\bsrc\s*=)[^>]*>[\s\S]*?<\/script>/gi) || [];
        expect(inlineScripts).toHaveLength(0);
    });

    test('designer.html has no inline on* event handlers', function () {
        var matches = html.match(/\bon\w+\s*=/g) || [];
        expect(matches).toHaveLength(0);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// RawNum class
// ─────────────────────────────────────────────────────────────────────────────

describe('RawNum', function () {
    var RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        RawNum = core.RawNum;
    });

    test('stores raw string verbatim', function () {
        var rn = new RawNum('9007199254740993');
        expect(rn.raw).toBe('9007199254740993');
    });

    test('toString returns raw literal', function () {
        expect(new RawNum('1E2').toString()).toBe('1E2');
        expect(new RawNum('0.50').toString()).toBe('0.50');
        expect(new RawNum('-0').toString()).toBe('-0');
    });

    test('valueOf converts to JS number', function () {
        expect(new RawNum('42').valueOf()).toBe(42);
    });

    test('instanceof check works', function () {
        var rn = new RawNum('1');
        expect(rn instanceof RawNum).toBe(true);
    });

    test('throws TypeError if raw is not a string', function () {
        // Use message match instead of constructor to avoid cross-vm TypeError mismatch.
        expect(function () { return new RawNum(42); }).toThrowError('must be a string');
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// T-DSN-3 (client half): WtmJsonRaw lossless codec
// ─────────────────────────────────────────────────────────────────────────────

describe('T-DSN-3: WtmJsonRaw.parse — int64 preservation', function () {
    var WtmJsonRaw, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        WtmJsonRaw = core.WtmJsonRaw;
        RawNum = core.RawNum;
    });

    test('parses int64 9007199254740993 as RawNum (not truncated JS double)', function () {
        // JSON.parse("9007199254740993") === 9007199254740992 — lossy!
        // WtmJsonRaw must preserve the exact literal.
        var tree = WtmJsonRaw.parse('{"xExternalRef":9007199254740993}');
        expect(tree.xExternalRef instanceof RawNum).toBe(true);
        expect(tree.xExternalRef.raw).toBe('9007199254740993');
    });

    test('int64 round-trips via stringify byte-exact', function () {
        var input = '{"xExternalRef":9007199254740993}';
        var tree = WtmJsonRaw.parse(input);
        var output = WtmJsonRaw.stringify(tree);
        expect(output).toBe('{"xExternalRef":9007199254740993}');
    });

    test('parses 0.50 as RawNum with raw "0.50"', function () {
        var tree = WtmJsonRaw.parse('{"v":0.50}');
        expect(tree.v instanceof RawNum).toBe(true);
        expect(tree.v.raw).toBe('0.50');
    });

    test('0.50 round-trips via stringify as "0.50" (not "0.5")', function () {
        var input = '{"v":0.50}';
        expect(WtmJsonRaw.stringify(WtmJsonRaw.parse(input))).toBe('{"v":0.50}');
    });

    test('parses 1E2 as RawNum with raw "1E2"', function () {
        var tree = WtmJsonRaw.parse('{"n":1E2}');
        expect(tree.n instanceof RawNum).toBe(true);
        expect(tree.n.raw).toBe('1E2');
    });

    test('1E2 round-trips via stringify as "1E2" (not "100")', function () {
        expect(WtmJsonRaw.stringify(WtmJsonRaw.parse('{"n":1E2}'))).toBe('{"n":1E2}');
    });

    test('parses -0 as RawNum with raw "-0"', function () {
        var tree = WtmJsonRaw.parse('{"z":-0}');
        expect(tree.z instanceof RawNum).toBe(true);
        expect(tree.z.raw).toBe('-0');
    });

    test('-0 round-trips via stringify as "-0"', function () {
        expect(WtmJsonRaw.stringify(WtmJsonRaw.parse('{"z":-0}'))).toBe('{"z":-0}');
    });

    test('deeply nested number preserved', function () {
        var input = '{"a":{"b":{"c":9007199254740993}}}';
        var tree = WtmJsonRaw.parse(input);
        expect(tree.a.b.c instanceof RawNum).toBe(true);
        expect(tree.a.b.c.raw).toBe('9007199254740993');
        expect(WtmJsonRaw.stringify(tree)).toBe(input);
    });

    test('number inside array preserved', function () {
        var input = '[9007199254740993,0.50,1E2]';
        var tree = WtmJsonRaw.parse(input);
        expect(tree[0] instanceof RawNum).toBe(true);
        expect(tree[0].raw).toBe('9007199254740993');
        expect(tree[1].raw).toBe('0.50');
        expect(tree[2].raw).toBe('1E2');
        expect(WtmJsonRaw.stringify(tree)).toBe(input);
    });
});

describe('T-DSN-3: RawNum unforgeable from JSON data', function () {
    var WtmJsonRaw, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        WtmJsonRaw = core.WtmJsonRaw;
        RawNum = core.RawNum;
    });

    test('plain object with __raw property is not a RawNum', function () {
        // A data object {"__raw":"9007199254740993"} must remain a plain object.
        // RawNum can only be minted by the parse() function for actual number tokens.
        var tree = WtmJsonRaw.parse('{"__raw":"9007199254740993"}');
        expect(tree instanceof RawNum).toBe(false);
        expect(typeof tree).toBe('object');
        expect(typeof tree['__raw']).toBe('string');
    });

    test('plain string values in JSON are decoded as strings, not RawNum', function () {
        var tree = WtmJsonRaw.parse('{"name":"test"}');
        expect(typeof tree.name).toBe('string');
        expect(tree.name instanceof RawNum).toBe(false);
    });

    test('boolean and null are decoded as native values', function () {
        var tree = WtmJsonRaw.parse('{"a":true,"b":false,"c":null}');
        expect(tree.a).toBe(true);
        expect(tree.b).toBe(false);
        expect(tree.c).toBe(null);
    });
});

describe('WtmJsonRaw.parse — basic types and structures', function () {
    var WtmJsonRaw, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        WtmJsonRaw = core.WtmJsonRaw;
        RawNum = core.RawNum;
    });

    test('parses empty object', function () {
        expect(WtmJsonRaw.parse('{}')).toEqual({});
    });

    test('parses empty array', function () {
        expect(WtmJsonRaw.parse('[]')).toEqual([]);
    });

    test('parses string values', function () {
        var tree = WtmJsonRaw.parse('{"s":"hello world"}');
        expect(tree.s).toBe('hello world');
    });

    test('parses escape sequences in strings', function () {
        var tree = WtmJsonRaw.parse('{"s":"line1\\nline2"}');
        expect(tree.s).toBe('line1\nline2');
    });

    test('parses unicode escapes in strings', function () {
        var tree = WtmJsonRaw.parse('{"s":"\\u4e2d\\u6587"}');
        expect(tree.s).toBe('中文');
    });

    test('parses nested objects', function () {
        var tree = WtmJsonRaw.parse('{"a":{"b":{"c":"deep"}}}');
        expect(tree.a.b.c).toBe('deep');
    });

    test('parses arrays of mixed types', function () {
        var tree = WtmJsonRaw.parse('[1,"two",true,null,{"x":5}]');
        expect(tree[0] instanceof RawNum).toBe(true);
        expect(tree[0].raw).toBe('1');
        expect(tree[1]).toBe('two');
        expect(tree[2]).toBe(true);
        expect(tree[3]).toBe(null);
        expect(tree[4].x instanceof RawNum).toBe(true);
    });

    test('throws on malformed JSON', function () {
        expect(function () { WtmJsonRaw.parse('{bad}'); }).toThrow();
    });

    test('throws on trailing content', function () {
        expect(function () { WtmJsonRaw.parse('{}{}'); }).toThrow();
    });

    test('throws on non-string input', function () {
        // Use message match instead of constructor to avoid cross-vm TypeError mismatch.
        expect(function () { WtmJsonRaw.parse(null); }).toThrowError('must be a string');
    });
});

describe('WtmJsonRaw.stringify', function () {
    var WtmJsonRaw, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        WtmJsonRaw = core.WtmJsonRaw;
        RawNum = core.RawNum;
    });

    test('emits RawNum raw literal verbatim', function () {
        expect(WtmJsonRaw.stringify(new RawNum('9007199254740993'))).toBe('9007199254740993');
    });

    test('emits null as "null"', function () {
        expect(WtmJsonRaw.stringify(null)).toBe('null');
    });

    test('emits boolean true as "true"', function () {
        expect(WtmJsonRaw.stringify(true)).toBe('true');
    });

    test('emits boolean false as "false"', function () {
        expect(WtmJsonRaw.stringify(false)).toBe('false');
    });

    test('emits string via JSON.stringify (normalized escapes)', function () {
        expect(WtmJsonRaw.stringify('hello')).toBe('"hello"');
    });

    test('emits object with RawNum values verbatim', function () {
        var obj = { a: new RawNum('0.50'), b: new RawNum('1E2') };
        var result = WtmJsonRaw.stringify(obj);
        expect(result).toBe('{"a":0.50,"b":1E2}');
    });

    test('emits array with RawNum values verbatim', function () {
        var arr = [new RawNum('9007199254740993'), new RawNum('-0')];
        expect(WtmJsonRaw.stringify(arr)).toBe('[9007199254740993,-0]');
    });

    test('unknown fields in parsed tree survive stringify', function () {
        var input = '{"schemaVersion":1,"unknownField":"preserved","nodes":[]}';
        var tree = WtmJsonRaw.parse(input);
        var output = WtmJsonRaw.stringify(tree);
        var reparsed = JSON.parse(output);
        expect(reparsed.unknownField).toBe('preserved');
        expect(reparsed.schemaVersion).toBe(1);
    });

    test('unknown nested objects survive stringify', function () {
        var input = '{"a":{"unknownNested":{"deepKey":42}}}';
        var tree = WtmJsonRaw.parse(input);
        var output = WtmJsonRaw.stringify(tree);
        var reparsed = JSON.parse(output);
        expect(reparsed.a.unknownNested.deepKey).toBe(42);
    });
});

describe('WtmJsonRaw.readNum / writeNum helpers', function () {
    var WtmJsonRaw, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        WtmJsonRaw = core.WtmJsonRaw;
        RawNum = core.RawNum;
    });

    test('readNum extracts JS number from RawNum', function () {
        expect(WtmJsonRaw.readNum(new RawNum('42'))).toBe(42);
        expect(WtmJsonRaw.readNum(new RawNum('0.50'))).toBeCloseTo(0.5);
    });

    test('readNum returns NaN for non-RawNum/non-number', function () {
        expect(WtmJsonRaw.readNum('42')).toBeNaN();
        expect(WtmJsonRaw.readNum(null)).toBeNaN();
    });

    test('readNum accepts plain JS number', function () {
        expect(WtmJsonRaw.readNum(7)).toBe(7);
    });

    test('writeNum stores RawNum with exact text for valid number', function () {
        var obj = {};
        var ok = WtmJsonRaw.writeNum(obj, 'x', '0.75');
        expect(ok).toBe(true);
        expect(obj.x instanceof RawNum).toBe(true);
        expect(obj.x.raw).toBe('0.75');
    });

    test('writeNum rejects invalid number text', function () {
        var obj = {};
        expect(WtmJsonRaw.writeNum(obj, 'x', 'abc')).toBe(false);
        expect(WtmJsonRaw.writeNum(obj, 'x', '')).toBe(false);
        expect(WtmJsonRaw.writeNum(obj, 'x', 'NaN')).toBe(false);
        expect(WtmJsonRaw.writeNum(obj, 'x', 'Infinity')).toBe(false);
        expect(Object.prototype.hasOwnProperty.call(obj, 'x')).toBe(false);
    });

    test('writeNum handles negative numbers', function () {
        var obj = {};
        expect(WtmJsonRaw.writeNum(obj, 'x', '-1')).toBe(true);
        expect(obj.x.raw).toBe('-1');
    });

    test('writeNum accepts RFC 8259 exponent notation (e and E)', function () {
        // writeNum regex /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+\-]?\d+)?$/ accepts both.
        var obj1 = {};
        expect(WtmJsonRaw.writeNum(obj1, 'n', '1e2')).toBe(true);
        expect(obj1.n.raw).toBe('1e2');

        var obj2 = {};
        expect(WtmJsonRaw.writeNum(obj2, 'n', '1E2')).toBe(true);
        expect(obj2.n.raw).toBe('1E2');

        var obj3 = {};
        expect(WtmJsonRaw.writeNum(obj3, 'n', '2.5e-10')).toBe(true);
        expect(obj3.n.raw).toBe('2.5e-10');
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// T-DSN-3 (dirty-save preserves untouched literals) + T-DSN-1 (byte-passthrough)
// ─────────────────────────────────────────────────────────────────────────────

describe('GraphModel — dirty tracking and byte-passthrough (T-DSN-1 client analog)', function () {
    var GraphModel, WtmJsonRaw, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        GraphModel = core.GraphModel;
        WtmJsonRaw = core.WtmJsonRaw;
        RawNum = core.RawNum;
    });

    test('not dirty after load — getPayload returns original bytes verbatim', function () {
        var originalJson = '{"schemaVersion":1,"nodes":[],"transitions":[]}';
        GraphModel.load({ graphJson: originalJson, contentHash: 'abc123' });
        expect(GraphModel.isDirty()).toBe(false);
        // Byte-identical passthrough is the ContentHash-stability keystone.
        expect(GraphModel.getPayload()).toBe(originalJson);
    });

    test('loading envelope with no draft leaves isDirty false', function () {
        GraphModel.load({ graphJson: '{"schemaVersion":1}', contentHash: 'h1' });
        expect(GraphModel.isDirty()).toBe(false);
    });

    test('loading envelope with draft marks isDirty true', function () {
        GraphModel.load({
            graphJson:  '{"schemaVersion":1,"published":true}',
            contentHash: 'h1',
            draft: {
                graphJson:       '{"schemaVersion":1,"published":false}',
                rowVer:          '42',
                lastSavedBy:     'alice',
                baseContentHash: 'h1'
            }
        });
        expect(GraphModel.isDirty()).toBe(true);
    });

    test('getPayload after draft load returns draft graphJson', function () {
        var draftJson = '{"schemaVersion":1,"draft":true}';
        GraphModel.load({
            graphJson:  '{"schemaVersion":1}',
            contentHash: 'h1',
            draft: { graphJson: draftJson, rowVer: '1' }
        });
        // Draft loads as dirty; getPayload uses stringify(tree) on dirty.
        // The result should contain draft content.
        var payload = GraphModel.getPayload();
        expect(JSON.parse(payload).draft).toBe(true);
    });

    test('markDirty makes getPayload use stringify', function () {
        var original = '{"schemaVersion":1,"x":9007199254740993}';
        GraphModel.load({ graphJson: original, contentHash: 'h' });
        GraphModel.markDirty();
        expect(GraphModel.isDirty()).toBe(true);
        // Payload is now the stringified tree — int64 still preserved via RawNum.
        var payload = GraphModel.getPayload();
        expect(payload).toContain('9007199254740993');
    });

    test('int64 in original bytes preserved through dirty stringify', function () {
        var original = '{"schemaVersion":1,"refId":9007199254740993}';
        GraphModel.load({ graphJson: original, contentHash: 'h' });
        // Simulate a field edit
        GraphModel.setProperty(['schemaVersion'], new RawNum('1'));
        expect(GraphModel.isDirty()).toBe(true);
        var payload = GraphModel.getPayload();
        // refId must still be exactly 9007199254740993 (not 9007199254740992)
        expect(payload).toContain('9007199254740993');
    });

    test('0.50 preserved as "0.50" through dirty stringify (T-DSN-3)', function () {
        var original = '{"schemaVersion":1,"approvePercent":0.50}';
        GraphModel.load({ graphJson: original, contentHash: 'h' });
        GraphModel.markDirty();
        var payload = GraphModel.getPayload();
        expect(payload).toContain('0.50');
    });

    test('1E2 preserved as "1E2" through dirty stringify (T-DSN-3)', function () {
        var original = '{"schemaVersion":1,"limit":1E2}';
        GraphModel.load({ graphJson: original, contentHash: 'h' });
        GraphModel.markDirty();
        var payload = GraphModel.getPayload();
        expect(payload).toContain('1E2');
    });

    test('loadRaw marks dirty', function () {
        GraphModel.loadRaw('{"schemaVersion":1}');
        expect(GraphModel.isDirty()).toBe(true);
    });

    test('getSchemaVersion returns numeric version from tree', function () {
        GraphModel.load({ graphJson: '{"schemaVersion":1,"nodes":[]}', contentHash: 'h' });
        expect(GraphModel.getSchemaVersion()).toBe(1);
    });

    test('getVersionInfo returns correct fields', function () {
        GraphModel.load({
            graphJson: '{"schemaVersion":1}',
            versionId: 'vid-1',
            versionNo: 3,
            contentHash: 'hash-3'
        });
        var info = GraphModel.getVersionInfo();
        expect(info.versionId).toBe('vid-1');
        expect(info.versionNo).toBe(3);
        expect(info.contentHash).toBe('hash-3');
        expect(info.baseContentHash).toBe('hash-3'); // equals contentHash when no draft
    });

    test('getDraft returns null when no draft in envelope', function () {
        GraphModel.load({ graphJson: '{"schemaVersion":1}', contentHash: 'h' });
        expect(GraphModel.getDraft()).toBeNull();
    });

    test('getDraft returns draft metadata when draft exists', function () {
        GraphModel.load({
            graphJson:  '{"schemaVersion":1}',
            contentHash: 'h1',
            draft: { graphJson: '{}', rowVer: '7', lastSavedBy: 'bob', baseContentHash: 'h1' }
        });
        var draft = GraphModel.getDraft();
        expect(draft).not.toBeNull();
        expect(draft.rowVer).toBe('7');
        expect(draft.lastSavedBy).toBe('bob');
    });
});

describe('GraphModel.setProperty / getProperty', function () {
    var GraphModel, RawNum;
    beforeEach(function () {
        var { core } = loadCore();
        GraphModel = core.GraphModel;
        RawNum = core.RawNum;
    });

    test('setProperty marks dirty and updates tree', function () {
        GraphModel.load({ graphJson: '{"schemaVersion":1,"name":"old"}', contentHash: 'h' });
        expect(GraphModel.isDirty()).toBe(false);
        GraphModel.setProperty(['name'], 'new');
        expect(GraphModel.isDirty()).toBe(true);
        expect(GraphModel.getProperty(['name'])).toBe('new');
    });

    test('setProperty at nested path', function () {
        var json = '{"schemaVersion":1,"a":{"b":"original"}}';
        GraphModel.load({ graphJson: json, contentHash: 'h' });
        GraphModel.setProperty(['a', 'b'], 'updated');
        expect(GraphModel.getProperty(['a', 'b'])).toBe('updated');
    });

    test('getProperty returns undefined for missing path', function () {
        GraphModel.load({ graphJson: '{"schemaVersion":1}', contentHash: 'h' });
        expect(GraphModel.getProperty(['missing', 'path'])).toBeUndefined();
    });

    test('setProperty into unknown field preserves unknown siblings', function () {
        var json = '{"schemaVersion":1,"name":"x","unknownField":"kept"}';
        GraphModel.load({ graphJson: json, contentHash: 'h' });
        GraphModel.setProperty(['name'], 'y');
        var payload = GraphModel.getPayload();
        var reparsed = JSON.parse(payload);
        expect(reparsed.unknownField).toBe('kept');
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// T-DSN-12: SchemaVersion lockout
// ─────────────────────────────────────────────────────────────────────────────

describe('T-DSN-12: schema version guard', function () {
    var core;
    beforeEach(function () {
        core = loadCore().core;
    });

    test('isSchemaVersionSupported returns true for version 1', function () {
        expect(core.isSchemaVersionSupported(1)).toBe(true);
    });

    test('isSchemaVersionSupported returns false for version 2', function () {
        expect(core.isSchemaVersionSupported(2)).toBe(false);
    });

    test('isSchemaVersionSupported returns false for version 0', function () {
        expect(core.isSchemaVersionSupported(0)).toBe(false);
    });

    test('isSchemaVersionSupported returns false for null', function () {
        expect(core.isSchemaVersionSupported(null)).toBe(false);
    });

    test('SUPPORTED_SCHEMA_VERSION constant is 1', function () {
        expect(core.SUPPORTED_SCHEMA_VERSION).toBe(1);
    });

    test('GraphModel.getSchemaVersion returns 2 for schemaVersion:2 graph', function () {
        core.GraphModel.load({ graphJson: '{"schemaVersion":2,"nodes":[]}', contentHash: 'h' });
        expect(core.GraphModel.getSchemaVersion()).toBe(2);
        expect(core.isSchemaVersionSupported(core.GraphModel.getSchemaVersion())).toBe(false);
    });

    test('GraphModel.getSchemaVersion returns null when no schemaVersion field', function () {
        core.GraphModel.load({ graphJson: '{"nodes":[]}', contentHash: 'h' });
        expect(core.GraphModel.getSchemaVersion()).toBeNull();
        expect(core.isSchemaVersionSupported(null)).toBe(false);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// DesignerApi — XSRF header and request shaping
// ─────────────────────────────────────────────────────────────────────────────

describe('DesignerApi antiforgery header', function () {
    var DesignerApi, fetchCalls, mockFetch;
    beforeEach(function () {
        var loaded = loadCore();
        DesignerApi = loaded.core.DesignerApi;
        fetchCalls  = loaded.fetchCalls;
        mockFetch   = loaded.mockFetch;
    });

    test('mutating calls include X-WTM-WF-XSRF header after setXsrfToken', function () {
        DesignerApi.setXsrfToken('tok-abc');
        DesignerApi.publish('CODE1', '{}', null);
        var call = fetchCalls[0];
        expect(call.opts.headers['X-WTM-WF-XSRF']).toBe('tok-abc');
    });

    test('createDefinition includes X-WTM-WF-XSRF header', function () {
        DesignerApi.setXsrfToken('tok-xyz');
        DesignerApi.createDefinition({ code: 'T', name: 'Test', category: 'Cat' });
        expect(fetchCalls[0].opts.headers['X-WTM-WF-XSRF']).toBe('tok-xyz');
    });

    test('saveDraft includes X-WTM-WF-XSRF header', function () {
        DesignerApi.setXsrfToken('tok-draft');
        DesignerApi.saveDraft('CODE1', '{}', { ifNoneMatch: true });
        expect(fetchCalls[0].opts.headers['X-WTM-WF-XSRF']).toBe('tok-draft');
    });

    test('deleteDraft includes X-WTM-WF-XSRF header', function () {
        DesignerApi.setXsrfToken('tok-del');
        DesignerApi.deleteDraft('CODE1');
        expect(fetchCalls[0].opts.headers['X-WTM-WF-XSRF']).toBe('tok-del');
    });

    test('reading calls (getGraph) do not require XSRF', function () {
        DesignerApi.setXsrfToken('tok');
        DesignerApi.getGraph('CODE1');
        // GET does not include XSRF (reading headers are empty)
        expect(fetchCalls[0].opts.headers['X-WTM-WF-XSRF']).toBeUndefined();
    });

    test('getBootstrap uses GET method', function () {
        DesignerApi.getBootstrap();
        expect(fetchCalls[0].opts.method).toBe('GET');
    });
});

describe('DesignerApi.saveDraft — If-Match / If-None-Match headers', function () {
    var DesignerApi, fetchCalls;
    beforeEach(function () {
        var loaded = loadCore();
        DesignerApi = loaded.core.DesignerApi;
        fetchCalls  = loaded.fetchCalls;
        DesignerApi.setXsrfToken('t');
    });

    test('ifNoneMatch=true sets If-None-Match: *', function () {
        DesignerApi.saveDraft('C', '{}', { ifNoneMatch: true });
        expect(fetchCalls[0].opts.headers['If-None-Match']).toBe('*');
        expect(fetchCalls[0].opts.headers['If-Match']).toBeUndefined();
    });

    test('rowVer sets If-Match with quoted value', function () {
        DesignerApi.saveDraft('C', '{}', { rowVer: '5' });
        expect(fetchCalls[0].opts.headers['If-Match']).toBe('"5"');
        expect(fetchCalls[0].opts.headers['If-None-Match']).toBeUndefined();
    });

    test('no options sets neither If-Match nor If-None-Match', function () {
        DesignerApi.saveDraft('C', '{}', {});
        expect(fetchCalls[0].opts.headers['If-Match']).toBeUndefined();
        expect(fetchCalls[0].opts.headers['If-None-Match']).toBeUndefined();
    });

    test('baseContentHash sent as X-WTM-WF-Base-Hash header (FIX-B1)', function () {
        DesignerApi.saveDraft('C', '{}', { ifNoneMatch: true, baseContentHash: 'abc123' });
        // FIX-B1: base hash travels as a header, NOT as a query parameter.
        expect(fetchCalls[0].opts.headers['X-WTM-WF-Base-Hash']).toBe('abc123');
        expect(fetchCalls[0].url).not.toContain('base=');
    });
});

describe('DesignerApi.publish — X-WTM-WF-Expected-Hash header (FIX-B1)', function () {
    var DesignerApi, fetchCalls;
    beforeEach(function () {
        var loaded = loadCore();
        DesignerApi = loaded.core.DesignerApi;
        fetchCalls  = loaded.fetchCalls;
        DesignerApi.setXsrfToken('t');
    });

    test('publish includes X-WTM-WF-Expected-Hash when expectedHash provided (FIX-B1)', function () {
        DesignerApi.publish('CODE1', '{}', 'hash-v4');
        // FIX-B1: header must be X-WTM-WF-Expected-Hash (with WF namespace), not X-WTM-Expected-Hash.
        expect(fetchCalls[0].opts.headers['X-WTM-WF-Expected-Hash']).toBe('hash-v4');
        expect(fetchCalls[0].opts.headers['X-WTM-Expected-Hash']).toBeUndefined();
    });

    test('publish omits X-WTM-WF-Expected-Hash when expectedHash is null (FIX-B1)', function () {
        DesignerApi.publish('CODE1', '{}', null);
        expect(fetchCalls[0].opts.headers['X-WTM-WF-Expected-Hash']).toBeUndefined();
    });

    test('publish uses POST method', function () {
        DesignerApi.publish('CODE1', '{}', null);
        expect(fetchCalls[0].opts.method).toBe('POST');
    });

    test('publish sends raw graph string as body', function () {
        var rawGraph = '{"schemaVersion":1,"nodes":[]}';
        DesignerApi.publish('CODE1', rawGraph, null);
        expect(fetchCalls[0].opts.body).toBe(rawGraph);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// LocalStorageCache
// ─────────────────────────────────────────────────────────────────────────────

describe('LocalStorageCache', function () {
    var LocalStorageCache, ls;
    beforeEach(function () {
        var loaded = loadCore();
        LocalStorageCache = loaded.core.LocalStorageCache;
        ls = loaded.localStorage;
    });

    test('save stores data; load returns { ts, json }', function () {
        LocalStorageCache.save('PROC1', '{"schemaVersion":1}');
        var entry = LocalStorageCache.load('PROC1');
        expect(entry).not.toBeNull();
        expect(entry.json).toBe('{"schemaVersion":1}');
        expect(typeof entry.ts).toBe('number');
    });

    test('load returns null for unknown code', function () {
        expect(LocalStorageCache.load('NO_SUCH_CODE')).toBeNull();
    });

    test('clear removes the entry', function () {
        LocalStorageCache.save('PROC2', '{"schemaVersion":1}');
        LocalStorageCache.clear('PROC2');
        expect(LocalStorageCache.load('PROC2')).toBeNull();
    });

    test('save is keyed per code (different codes independent)', function () {
        LocalStorageCache.save('A', '{"x":1}');
        LocalStorageCache.save('B', '{"x":2}');
        expect(LocalStorageCache.load('A').json).toBe('{"x":1}');
        expect(LocalStorageCache.load('B').json).toBe('{"x":2}');
    });

    test('save with quota error is silently ignored (no throw)', function () {
        // Simulate unavailable localStorage
        var loaded = loadCore({
            localStorage: {
                setItem: function () { throw new Error('QuotaExceededError'); },
                getItem: function () { return null; },
                removeItem: function () {}
            }
        });
        // Must not throw
        expect(function () {
            loaded.core.LocalStorageCache.save('X', '{}');
        }).not.toThrow();
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// WtmDesignerCore module shape
// ─────────────────────────────────────────────────────────────────────────────

describe('WtmDesignerCore module shape', function () {
    var core;
    beforeEach(function () {
        core = loadCore().core;
    });

    test('exposes RawNum class', function () {
        expect(typeof core.RawNum).toBe('function');
    });

    test('exposes WtmJsonRaw with parse/stringify/readNum/writeNum', function () {
        expect(typeof core.WtmJsonRaw.parse).toBe('function');
        expect(typeof core.WtmJsonRaw.stringify).toBe('function');
        expect(typeof core.WtmJsonRaw.readNum).toBe('function');
        expect(typeof core.WtmJsonRaw.writeNum).toBe('function');
    });

    test('exposes GraphModel with all required methods', function () {
        var gm = core.GraphModel;
        expect(typeof gm.load).toBe('function');
        expect(typeof gm.loadRaw).toBe('function');
        expect(typeof gm.getTree).toBe('function');
        expect(typeof gm.getSchemaVersion).toBe('function');
        expect(typeof gm.isDirty).toBe('function');
        expect(typeof gm.markDirty).toBe('function');
        expect(typeof gm.getDraft).toBe('function');
        expect(typeof gm.getVersionInfo).toBe('function');
        expect(typeof gm.getPayload).toBe('function');
        expect(typeof gm.setProperty).toBe('function');
        expect(typeof gm.getProperty).toBe('function');
    });

    test('exposes DesignerApi with all required methods', function () {
        var api = core.DesignerApi;
        expect(typeof api.setXsrfToken).toBe('function');
        expect(typeof api.getBootstrap).toBe('function');
        expect(typeof api.listDefinitions).toBe('function');
        expect(typeof api.createDefinition).toBe('function');
        expect(typeof api.getGraph).toBe('function');
        expect(typeof api.saveDraft).toBe('function');
        expect(typeof api.deleteDraft).toBe('function');
        expect(typeof api.publish).toBe('function');
        expect(typeof api.validate).toBe('function');
    });

    test('exposes LocalStorageCache', function () {
        expect(typeof core.LocalStorageCache.save).toBe('function');
        expect(typeof core.LocalStorageCache.load).toBe('function');
        expect(typeof core.LocalStorageCache.clear).toBe('function');
    });

    test('exposes isSchemaVersionSupported and SUPPORTED_SCHEMA_VERSION', function () {
        expect(typeof core.isSchemaVersionSupported).toBe('function');
        expect(core.SUPPORTED_SCHEMA_VERSION).toBe(1);
    });

    test('version tag is WF-21.5', function () {
        expect(core.version).toBe('WF-21.5');
    });

    // ── FIX-B1: WfHeaders exported and frozen ──────────────────────────────────
    test('exposes WfHeaders with correct header name literals (FIX-B1 cross-layer contract)', function () {
        var h = core.WfHeaders;
        expect(h).toBeDefined();
        // Byte-identical with DesignerHeaderNames.cs — change here iff C# changes.
        expect(h.Xsrf).toBe('X-WTM-WF-XSRF');
        expect(h.ExpectedHash).toBe('X-WTM-WF-Expected-Hash');
        expect(h.BaseHash).toBe('X-WTM-WF-Base-Hash');
    });

    test('WfHeaders is frozen (immutable after definition)', function () {
        var h = core.WfHeaders;
        expect(Object.isFrozen(h)).toBe(true);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// FIX-B1: Cross-layer header contract — WfHeaders vs DesignerApi call shapes
// ─────────────────────────────────────────────────────────────────────────────

describe('FIX-B1 cross-layer contract: WfHeaders drives actual request headers', function () {
    var core, DesignerApi, WfHeaders, fetchCalls;
    beforeEach(function () {
        var loaded = loadCore();
        core        = loaded.core;
        DesignerApi = loaded.core.DesignerApi;
        WfHeaders   = loaded.core.WfHeaders;
        fetchCalls  = loaded.fetchCalls;
        DesignerApi.setXsrfToken('tok');
    });

    test('saveDraft sends WfHeaders.BaseHash as header key when baseContentHash provided', function () {
        DesignerApi.saveDraft('C', '{}', { ifNoneMatch: true, baseContentHash: 'h1' });
        // The actual header key must exactly match the WfHeaders.BaseHash constant.
        expect(fetchCalls[0].opts.headers[WfHeaders.BaseHash]).toBe('h1');
    });

    test('publish sends WfHeaders.ExpectedHash as header key when expectedHash provided', function () {
        DesignerApi.publish('CODE1', '{}', 'h2');
        expect(fetchCalls[0].opts.headers[WfHeaders.ExpectedHash]).toBe('h2');
    });

    test('mutating calls send WfHeaders.Xsrf as header key', function () {
        DesignerApi.saveDraft('C', '{}', { ifNoneMatch: true });
        expect(fetchCalls[0].opts.headers[WfHeaders.Xsrf]).toBe('tok');
    });

    test('WfHeaders literal values are stable strings matching server constants', function () {
        // These must be kept byte-identical with DesignerHeaderNames.cs in the .NET project.
        // Update BOTH files if either literal changes.
        expect(WfHeaders.Xsrf).toBe('X-WTM-WF-XSRF');
        expect(WfHeaders.ExpectedHash).toBe('X-WTM-WF-Expected-Hash');
        expect(WfHeaders.BaseHash).toBe('X-WTM-WF-Base-Hash');
    });
});
