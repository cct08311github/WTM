// Tests for issue #482: EscapeAttr — attribute-safe encoding for grid Image/Progress templates.
//
// ff.EscapeText encodes & < > but NOT double-quote (") or single-quote (').
// Grid Image (src="...") and Progress (lay-percent="...") place the encoded value
// inside double-quoted HTML attributes. A value like   x" onerror="alert(1)
// would break out of the attribute and inject an event handler — stored XSS.
//
// The fix adds ff.EscapeAttr which delegates to ff.EscapeText then additionally
// encodes " → &quot; and ' → &#39;, closing the quote-breakout vector.
//
// Test strategy (mirrors the pattern used throughout this test suite):
//   - Source-text checks: inspect framework_layui.js source for the EscapeAttr declaration
//     and its two .replace() calls (no jQuery runtime required).
//   - Semantic / behavioral tests: replicate the EscapeAttr logic using native jsdom DOM
//     (same idiom as the "ff.EscapeText semantic behavior" describe block in
//     framework_layui_alert_escape_805.test.js), then verify the XSS vector is closed.
//   - The global.ff object is NOT called at runtime here: the test-harness jQuery mock
//     (`jqueryMock`) does not implement `.text()/.html()`, so calling ff.EscapeAttr()
//     directly would exercise the mock rather than the real encoding logic.

const fs   = require('fs');
const path = require('path');

const src = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

// ── Local helper: replicates the two-step EscapeAttr logic using jsdom ──────
// Step 1: mimic jQuery $('<div/>').text(s).html() via native DOM (EscapeText)
// Step 2: additionally encode " → &quot; and ' → &#39; (the EscapeAttr delta)
function escapeAttrLocal(s) {
    if (s === null || s === undefined) { return ''; }
    var div = document.createElement('div');
    div.textContent = String(s);
    return div.innerHTML
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

describe('#482 ff.EscapeAttr — source-level declaration checks', () => {
    test('ff.EscapeAttr is declared in framework_layui.js', () => {
        expect(src).toMatch(/EscapeAttr\s*:\s*function/);
    });

    test('EscapeAttr encodes double-quote via .replace(/"/g, \'&quot;\')', () => {
        expect(src).toMatch(/EscapeAttr[\s\S]{0,200}?replace\(\/"/);
    });

    test('EscapeAttr encodes single-quote via .replace(/\'/g, \'&#39;\')', () => {
        expect(src).toMatch(/EscapeAttr[\s\S]{0,200}?replace\(\/'/);
    });

    test('EscapeAttr delegates to ff.EscapeText for base HTML encoding', () => {
        expect(src).toMatch(/EscapeAttr[\s\S]{0,200}?ff\.EscapeText\(s\)/);
    });
});

describe('#482 ff.EscapeAttr — semantic behavior (local jsdom replication)', () => {
    test('encodes double-quote to &quot;', () => {
        const out = escapeAttrLocal('x" onerror="alert(1)');
        expect(out).not.toContain('"');
        expect(out).toContain('&quot;');
    });

    test('encodes single-quote to &#39;', () => {
        const out = escapeAttrLocal("it's");
        expect(out).not.toContain("'");
        expect(out).toContain('&#39;');
    });

    test('encodes < and > (inherits from EscapeText step)', () => {
        const out = escapeAttrLocal('<b>');
        expect(out).not.toContain('<');
        expect(out).not.toContain('>');
        expect(out).toContain('&lt;');
        expect(out).toContain('&gt;');
    });

    test('encodes & (inherits from EscapeText step)', () => {
        const out = escapeAttrLocal('a&b');
        expect(out).toContain('&amp;');
    });

    test('returns empty string for null', () => {
        expect(escapeAttrLocal(null)).toBe('');
    });

    test('returns empty string for undefined', () => {
        expect(escapeAttrLocal(undefined)).toBe('');
    });

    test('leaves plain text unchanged', () => {
        expect(escapeAttrLocal('hello world')).toBe('hello world');
    });
});

describe('#482 Image template behavioral test — quote-breakout XSS blocked', () => {
    // Simulate what the C#-generated template does for the Image column:
    //   (d.Photo ? '<img src="' + ff.EscapeAttr(d.Photo) + '" style="width:32px;..."/>' : '')
    // We build this string using the local replication helper, parse it as innerHTML,
    // and assert no onerror attribute was injected.

    function renderImageCell(fieldValue) {
        var encoded = escapeAttrLocal(fieldValue);
        return '<img src="' + encoded + '" style="width:32px;height:32px;object-fit:cover;"/>';
    }

    test('XSS payload x" onerror="alert(1) does not produce onerror attribute on img', () => {
        const payload = 'x" onerror="alert(1)';
        const html = renderImageCell(payload);

        // Parse as HTML — innerHTML intentionally used here to prove the XSS vector is closed:
        // this is the attack simulation, not a production code path.
        const container = document.createElement('div');
        container.innerHTML = html; // eslint-disable-line no-unsanitized/property
        const img = container.querySelector('img');

        expect(img).not.toBeNull();
        // Critical assertion: no onerror attribute was injected by the browser parser
        expect(img.getAttribute('onerror')).toBeNull();
        // The src attribute should contain the literal &quot; string (HTML entity in attribute)
        expect(img.outerHTML).toContain('&quot;');
    });

    test('normal image URL renders correctly', () => {
        const url = '/uploads/photo.jpg';
        const html = renderImageCell(url);

        const container = document.createElement('div');
        container.innerHTML = html; // eslint-disable-line no-unsanitized/property
        const img = container.querySelector('img');

        expect(img).not.toBeNull();
        expect(img.getAttribute('src')).toBe('/uploads/photo.jpg');
        expect(img.getAttribute('onerror')).toBeNull();
    });
});
