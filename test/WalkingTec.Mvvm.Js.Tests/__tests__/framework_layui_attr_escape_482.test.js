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
// What this file locks in:
//   1. ff.EscapeAttr exists on the ff object.
//   2. EscapeAttr encodes double-quote to &quot;.
//   3. EscapeAttr encodes single-quote to &#39;.
//   4. EscapeAttr still encodes < > & (inherits from EscapeText).
//   5. Behavioral: a simulated image src attribute render with the XSS payload
//      produces NO onerror attribute on the parsed img element.

describe('#482 ff.EscapeAttr — attribute-safe encoding', () => {
    test('ff.EscapeAttr is defined on the ff object', () => {
        expect(typeof global.ff.EscapeAttr).toBe('function');
    });

    test('encodes double-quote to &quot;', () => {
        const out = global.ff.EscapeAttr('x" onerror="alert(1)');
        expect(out).not.toContain('"');
        expect(out).toContain('&quot;');
    });

    test('encodes single-quote to &#39;', () => {
        const out = global.ff.EscapeAttr("it's");
        expect(out).not.toContain("'");
        expect(out).toContain('&#39;');
    });

    test('encodes < and > (inherits from EscapeText)', () => {
        const out = global.ff.EscapeAttr('<b>');
        expect(out).not.toContain('<');
        expect(out).not.toContain('>');
        expect(out).toContain('&lt;');
        expect(out).toContain('&gt;');
    });

    test('encodes & (inherits from EscapeText)', () => {
        const out = global.ff.EscapeAttr('a&b');
        expect(out).toContain('&amp;');
    });

    test('returns empty string for null', () => {
        expect(global.ff.EscapeAttr(null)).toBe('');
    });

    test('returns empty string for undefined', () => {
        expect(global.ff.EscapeAttr(undefined)).toBe('');
    });

    test('leaves plain text unchanged', () => {
        expect(global.ff.EscapeAttr('hello world')).toBe('hello world');
    });
});

describe('#482 Image template behavioral test — quote-breakout XSS blocked', () => {
    // Simulate what the C#-generated template does for the Image column:
    //   (d.Photo ? '<img src="' + ff.EscapeAttr(d.Photo) + '" style="width:32px;..."/>' : '')
    // We build this string, parse it as innerHTML, and assert no onerror attribute exists.

    function renderImageCell(fieldValue) {
        var encoded = global.ff.EscapeAttr(fieldValue);
        return '<img src="' + encoded + '" style="width:32px;height:32px;object-fit:cover;"/>';
    }

    test('XSS payload x" onerror="alert(1) does not produce onerror attribute on img', () => {
        const payload = 'x" onerror="alert(1)';
        const html = renderImageCell(payload);

        // Parse as HTML
        const container = document.createElement('div');
        container.innerHTML = html;
        const img = container.querySelector('img');

        expect(img).not.toBeNull();
        // The critical assertion: no onerror attribute was injected
        expect(img.getAttribute('onerror')).toBeNull();
        // The src should contain the encoded quote, not the raw breakout
        expect(img.getAttribute('src')).toContain('&quot;');
    });

    test('normal image URL renders correctly', () => {
        const url = '/uploads/photo.jpg';
        const html = renderImageCell(url);

        const container = document.createElement('div');
        container.innerHTML = html;
        const img = container.querySelector('img');

        expect(img).not.toBeNull();
        expect(img.getAttribute('src')).toBe('/uploads/photo.jpg');
        expect(img.getAttribute('onerror')).toBeNull();
    });
});
