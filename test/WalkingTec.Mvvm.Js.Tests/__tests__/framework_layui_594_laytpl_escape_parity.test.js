/**
 * @jest-environment node
 *
 * This file loads its own isolated jsdom windows (see helpers/layuiEngineLoader) rather than
 * relying on Jest's built-in `jsdom` testEnvironment: requiring the `jsdom` package from inside
 * a test running under Jest's jsdom environment throws `ReferenceError: TextEncoder is not
 * defined` (a known Jest/jsdom interaction -- Jest's jsdom sandbox doesn't expose TextEncoder/
 * TextDecoder, which jsdom's own `whatwg-url` dependency requires at load time). Running under
 * plain `node` avoids the conflict; DOM access below goes through the helper's `parseFirstElement`
 * (or the loaded engines' own `win.document`), not an ambient `document` global.
 */

// Tests for issue #594 regression 1: laytpl {{ }} HTML-escapes by default since layui 2.8
// (2.6.3 rendered it raw). The demo/layuiadmin menu + theme templates used to build a whole
// conditional attribute string inside a single interpolation, e.g.:
//
//   {{ hasChildren ? '' : 'lay-href="'+ url +'"' }}
//
// On 2.6.3 this was safe (raw output). On 2.13.8 the interpolated value gets HTML-escaped, so
// the literal `"` characters produced by the string concatenation turn into `&#34;`, corrupting
// the emitted attribute. The fix branches the whole opening tag with `{{# if(){ }}...{{# } }}`
// blocks instead, keeping quotes in the static template and interpolating bare values only.
//
// These tests load the REAL vendored laytpl engines from both trees (see helpers/layuiEngineLoader)
// so the assertions exercise the actual shipped compiler/escape behavior, not a reimplementation.

const fs = require('fs');
const path = require('path');
const { loadOldEngine, loadNextEngine, renderTpl, parseFirstElement, DEMO_WWWROOT } = require('./helpers/layuiEngineLoader');

const LAYOUT_CSHTML = path.resolve(
    DEMO_WWWROOT, '../Views/Home/Layout.cshtml'
);

describe('#594 engine fixtures — sanity/control', () => {
    test('old (2.6.3) engine loads and reports laytpl v1.2.0', () => {
        const win = loadOldEngine();
        expect(win.layui.laytpl.v).toBe('1.2.0');
    });

    test('next (2.13.8) engine loads and exposes a working laytpl', () => {
        const win = loadNextEngine();
        const out = renderTpl(win, 'hello {{ d.name }}', { name: 'world' });
        expect(out).toBe('hello world');
    });

    test('control: bare {{ d.x }} is raw on old engine but HTML-escapes quotes on next engine', () => {
        const data = { x: 'a"b' };
        const oldOut = renderTpl(loadOldEngine(), '{{ d.x }}', data);
        const nextOut = renderTpl(loadNextEngine(), '{{ d.x }}', data);

        expect(oldOut).toBe('a"b');           // 2.6.3: raw, no escaping
        expect(nextOut).toBe('a&#34;b');      // 2.13.8: HTML-escapes by default
    });
});

describe('#594 FIXED menu template — identical, correctly-quoted output on both trees', () => {
    // Mirrors the actual fix applied to Layout.cshtml's TPL_layout leaf-item branch
    // (and, structurally, theme.html's per-color branch): the whole opening tag is branched,
    // quotes live in the static template, and each {{ }} interpolates only a bare value.
    const FIXED_TPL = [
        '{{# if(d.hasChildren){ }}',
        '<a href="javascript:;" lay-tips="{{ d.title }}" lay-direction="2">',
        '{{# } else { }}',
        '<a href="javascript:;" lay-href="{{ d.url }}" lay-tips="{{ d.title }}" lay-direction="2">',
        '{{# } }}',
    ].join('');

    function render(win, data) {
        return renderTpl(win, FIXED_TPL, data);
    }

    test('leaf item (no children): lay-href is present and clean on both engines', () => {
        const data = { hasChildren: false, url: '/FTP/X/Index', title: 'FTP' };
        const oldOut = render(loadOldEngine(), data);
        const nextOut = render(loadNextEngine(), data);

        expect(oldOut).toBe(nextOut);
        expect(oldOut).toContain('lay-href="/FTP/X/Index"');
        expect(oldOut).not.toContain('&#34;');
        expect(oldOut).not.toContain('&quot;');

        // Parse and verify the attribute value itself is byte-clean (no embedded quote chars).
        const a = parseFirstElement(oldOut);
        expect(a.getAttribute('lay-href')).toBe('/FTP/X/Index');
    });

    test('parent item (has children): no lay-href emitted, tag structure identical on both engines', () => {
        const data = { hasChildren: true, url: '/should-not-appear', title: 'Parent' };
        const oldOut = render(loadOldEngine(), data);
        const nextOut = render(loadNextEngine(), data);

        expect(oldOut).toBe(nextOut);
        expect(oldOut).not.toContain('lay-href');
        expect(oldOut).toContain('lay-tips="Parent"');
    });

    test('title containing HTML-sensitive characters is still safely escaped (regression-free)', () => {
        // The fix must not reintroduce an XSS hole while closing the attribute-corruption bug:
        // {{ d.title }} is a plain interpolation and should still escape on the next engine.
        const data = { hasChildren: false, url: '/x', title: '<script>alert(1)</script>' };
        const nextOut = render(loadNextEngine(), data);
        expect(nextOut).not.toContain('<script>alert(1)</script>');
        // 2.13.8's escape() uses numeric character references (&#60; / &#62;), not named entities.
        expect(nextOut).toContain('&#60;script&#62;');
    });
});

describe('#594 OLD anti-pattern — reproduces the &#34; corruption on 2.13.8 (regression demo)', () => {
    // Reconstructed from git history (the pre-fix line) purely to demonstrate the regression;
    // this exact string no longer exists in the shipped template.
    const OLD_TPL = '<a href="javascript:;" {{ d.hasChildren ? \'\' : \'lay-href="\'+ d.url +\'"\' }} lay-tips="{{ d.title }}" lay-direction="2">';

    function render(win, data) {
        return renderTpl(win, OLD_TPL, data);
    }

    test('old engine (2.6.3): anti-pattern was safe — raw output, clean attribute', () => {
        const data = { hasChildren: false, url: '/FTP/X/Index', title: 'FTP' };
        const out = render(loadOldEngine(), data);

        expect(out).toContain('lay-href="/FTP/X/Index"');
        expect(out).not.toContain('&#34;');

        const a = parseFirstElement(out);
        expect(a.getAttribute('lay-href')).toBe('/FTP/X/Index');
    });

    test('next engine (2.13.8): anti-pattern corrupts the attribute with &#34;', () => {
        const data = { hasChildren: false, url: '/FTP/X/Index', title: 'FTP' };
        const out = render(loadNextEngine(), data);

        expect(out).toContain('&#34;');
        expect(out).not.toContain('lay-href="/FTP/X/Index"');
    });

    test('next engine (2.13.8): corrupted attribute value contains literal embedded quote characters', () => {
        // This is the actual failure mode described in #594: the browser's HTML tokenizer sees an
        // UNQUOTED attribute value (the char right after `=` is `&`, not `"`), decodes the `&#34;`
        // entities as part of that unquoted value, and the resulting attribute string ends up
        // containing literal `"` characters -- which is what breaks the hash-router navigation
        // when this value is later read via getAttribute() and pushed into location.hash.
        const data = { hasChildren: false, url: '/FTP/X/Index', title: 'FTP' };
        const out = render(loadNextEngine(), data);

        const a = parseFirstElement(out);
        const layHref = a.getAttribute('lay-href');

        expect(layHref).toBe('"/FTP/X/Index"'); // literal quote characters embedded — corrupted
        expect(layHref).not.toBe('/FTP/X/Index'); // proves it differs from the clean/expected value
    });
});

describe('#594 FIXED theme.html per-color template — identical output on both trees', () => {
    // Mirrors the theme.html fix: {{ index === themeColorIndex ? 'class="layui-this"' : '' }}
    // (whole-attribute-in-interpolation anti-pattern) branched into a whole-tag if/else instead.
    const FIXED_TPL = [
        '{{# if(d.index === d.themeColorIndex){ }}',
        '<li data-index="{{ d.index }}" class="layui-this" title="{{ d.alias }}">',
        '{{# } else { }}',
        '<li data-index="{{ d.index }}" title="{{ d.alias }}">',
        '{{# } }}',
    ].join('');

    test('selected color: class="layui-this" present, identical on both engines', () => {
        const data = { index: 0, themeColorIndex: 0, alias: 'default' };
        const oldOut = renderTpl(loadOldEngine(), FIXED_TPL, data);
        const nextOut = renderTpl(loadNextEngine(), FIXED_TPL, data);

        expect(oldOut).toBe(nextOut);
        expect(oldOut).toContain('class="layui-this"');
        expect(oldOut).not.toContain('&#34;');
        expect(oldOut).not.toContain('&quot;');
    });

    test('unselected color: no class attribute, identical on both engines', () => {
        const data = { index: 1, themeColorIndex: 0, alias: 'dark-blue' };
        const oldOut = renderTpl(loadOldEngine(), FIXED_TPL, data);
        const nextOut = renderTpl(loadNextEngine(), FIXED_TPL, data);

        expect(oldOut).toBe(nextOut);
        expect(oldOut).not.toContain('class=');
    });
});

describe('#594 source-consistency sweep — fix applied uniformly across all demo variants', () => {
    const DEMO_ROOT = path.resolve(DEMO_WWWROOT, '../..');
    const VARIANTS = [
        'WalkingTec.Mvvm.Demo',
        'WalkingTec.Mvvm.VueDemo',
        'WalkingTec.Mvvm.Vue3Demo',
        'WalkingTec.Mvvm.ReactDemo',
        'WalkingTec.Mvvm.BlazorDemo/WalkingTec.Mvvm.BlazorDemo',
    ];

    // The issue's sweep regex (\{\{[^}]*'[^']*=") is intentionally loose -- meant for a manual
    // pass with "eyeball the near-misses" (it also flags safe, static-quoted interpolations like
    // `data-name="{{ item.name || '' }}"`, where the quotes belong to the JS `''` empty-string
    // literal, not to an attribute being built). For an automated assertion we tighten it to what
    // actually distinguishes the anti-pattern: inside {{ }}, a string literal that itself starts
    // with an attribute-name-shaped token followed by `="` (e.g. 'lay-href="', 'class="'). This
    // catches `'lay-href="'+url+'"'` and `'class="layui-this"'` while leaving `''`/`item.name || ''`
    // alone. [^}\n] / [^'\n] additionally keep the match from crossing newlines or {{ }} boundaries
    // (character classes match \n by default; an unbounded version spans clean into unrelated
    // static HTML later in the file).
    const ANTI_PATTERN = /\{\{[^}\n]*'[a-zA-Z][\w-]*="/;

    test.each(VARIANTS)('%s: theme.html no longer contains the whole-attribute-in-interpolation anti-pattern', (variant) => {
        const themePath = path.join(DEMO_ROOT, variant, 'wwwroot/layuiadmin/views/system/theme.html');
        const src = fs.readFileSync(themePath, 'utf8');
        expect(src).not.toMatch(ANTI_PATTERN);
        expect(src).toMatch(/\{\{#\s*if\(index === themeColorIndex\)/);
    });

    test('WalkingTec.Mvvm.Demo: Layout.cshtml no longer contains the whole-attribute-in-interpolation anti-pattern', () => {
        const src = fs.readFileSync(LAYOUT_CSHTML, 'utf8');
        // Near-miss lines like `data-name="{{ item.name || '' }}"` are expected to remain (quotes
        // are static / belong to the JS `''` literal, not to an attribute being built) and must
        // NOT be flagged by the tightened anti-pattern regex above.
        expect(src).toContain('data-name="{{ item.name || \'\' }}"');
        expect(src).not.toMatch(ANTI_PATTERN);
        expect(src).toMatch(/\{\{#\s*if\(hasChildren\)\{ \}\}/);
        expect(src).toMatch(/\{\{#\s*if\(item3\.iframe\)\{ \}\}/);
    });

    test('WalkingTec.Mvvm.Demo: laytpl-compiled comment explaining the fix lives outside the <script> template range (Razor @* *@)', () => {
        const src = fs.readFileSync(LAYOUT_CSHTML, 'utf8');
        const scriptStart = src.indexOf('id="TPL_layout">');
        const scriptEnd = src.indexOf('</script>', scriptStart);
        expect(scriptStart).toBeGreaterThan(-1);
        expect(scriptEnd).toBeGreaterThan(scriptStart);
        const templateBody = src.slice(scriptStart, scriptEnd);
        // The explanatory WTM #594 comment must not appear inside the laytpl-compiled range --
        // laytpl compiles HTML comments too, and literal {{ / }} tokens inside a comment there
        // would blow up the whole template.
        expect(templateBody).not.toContain('WTM #594');
    });
});
