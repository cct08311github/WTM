// Tests for issue #522: ff.OpenDialog rehydration must re-inject extracted inline
// <script> elements as REAL <script> DOM elements (not eval them per-script) so
// the browser executes them in native global scope with correct ordering, enabling
// cross-script var sharing (e.g. xmSelect.render() in script A, window[id].update()
// in script B — script B depends on the global var set by script A).
//
// Root cause: scaffolded ComboBox (xmSelect) forms emit two <script> blocks:
//   Script A (ComboBoxTagHelper):  var {Id} = xmSelect.render({...})
//   Script B (BaseFieldTag):       window['{Id}'].update({layVerify:'required',...})
// Per-script eval() puts Script A's `var {Id}` in eval's local scope — NOT window —
// so Script B's window['{Id}'] is undefined → TypeError: undefined.update is not a function.
// Re-injecting as real <script> elements runs both in global scope in order, matching
// the original native inline-<script> semantics.
//
// XSS: does NOT widen the trust boundary vs #462 — the same DOMParser-extracted script
// bodies are re-run; markup is still sanitized by ff.SafeHtml/DOMPurify first. Only
// execution semantics change (element injection vs eval).

'use strict';

const fs = require('fs');
const path = require('path');

const srcPath = path.resolve(
    __dirname,
    '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);

const stripLineComments = (text) =>
    text
        .split('\n')
        .map((line) => {
            const idx = line.indexOf('//');
            return idx === -1 ? line : line.slice(0, idx);
        })
        .join('\n');

// ─── Source-sweep tests ────────────────────────────────────────────────────

describe('#522 OpenDialog rehydration — source sweep', () => {
    const src = fs.readFileSync(srcPath, 'utf8');
    const active = stripLineComments(src);

    test('success callback re-injects _initScripts via createElement("script") not eval', () => {
        // Must create real <script> elements — not use _legacyScriptEval for init scripts.
        expect(active).toMatch(/document\.createElement\s*\(\s*['"]script['"]\s*\)/);
        expect(active).toMatch(/document\.body\.appendChild/);
        // The rehydration loop must reference _initScripts
        expect(active).toMatch(/_initScripts/);
    });

    test('_legacyScriptEval is still present for IsScript header branch (untouched)', () => {
        // ff._legacyScriptEval must still exist — it is used by IsScript header
        // response paths (PostForm, BgRequest, OpenDialog IsScript branch).
        expect(active).toMatch(/_legacyScriptEval\s*:\s*function/);
        // IsScript branch of OpenDialog must still call _legacyScriptEval
        expect(active).toMatch(/IsScript.*true[\s\S]{0,200}?_legacyScriptEval/);
    });

    test('eval() count is 1 (only inside _legacyScriptEval — not added by #522 fix)', () => {
        // The total number of bare eval( tokens must remain exactly 1:
        // only the one inside ff._legacyScriptEval. The #522 fix must NOT add new eval calls.
        const evalMatches = active.match(/\beval\s*\(/g) || [];
        expect(evalMatches.length).toBe(1);
    });

    test('SafeHtml still applied to dialog markup (DOMPurify gate preserved)', () => {
        expect(active).toMatch(/_wrapperEl\.innerHTML\s*=\s*ff\.SafeHtml\(str\)/);
    });

    test('_initScripts extraction still uses DOMParser not regex (#462 boundary preserved)', () => {
        expect(active).toMatch(/new DOMParser\(\)\.parseFromString/);
        expect(active).toMatch(/querySelectorAll\s*\(\s*['"]script['"]\s*\)/);
        expect(active).not.toMatch(/_scriptRe\s*=\s*\//);
    });

    test('re-injected <script> element is removed from DOM after execution (tidying)', () => {
        // Each injected script element must be removed after it runs so the DOM
        // does not accumulate stale inline script tags.
        expect(active).toMatch(/removeChild/);
    });
});

// ─── Behavioral tests — mechanism verification ─────────────────────────────

describe('#522 OpenDialog rehydration — script element injection mechanism', () => {
    // Simulate the rehydration loop from the fixed OpenDialog success callback:
    // create a real <script> element per extracted body, append to document.body,
    // then remove. The jsdom test environment has runScripts:'dangerously' (see
    // @jest/environment-jsdom-abstract), so injected <script> elements execute.

    function rehydrateViaElements(scriptBodies) {
        // Mirrors the fixed OpenDialog success callback loop exactly.
        for (var _si = 0; _si < scriptBodies.length; _si++) {
            var _se = document.createElement('script');
            _se.text = scriptBodies[_si];
            document.body.appendChild(_se);
            if (_se.parentNode) { _se.parentNode.removeChild(_se); }
        }
    }

    beforeEach(() => {
        // Reset globals written by previous tests. Use assignment rather than
        // delete because var-declared globals from injected <script> elements are
        // non-configurable on window (native var semantics — the very behaviour
        // this fix relies on), so delete throws a TypeError in strict mode.
        window.__522_renderCalled = undefined;
        window.__522_updateCalled = undefined;
        window.__522_xmInst = undefined;
        window.__522_multiA = undefined;
        window.__522_multiB = undefined;
        window.__522_multiC = undefined;
    });

    test('Script A global var is visible to script B (xmSelect render→update pattern)', () => {
        // Script A: xmSelect.render() returns an instance stored in a top-level var.
        // Script B: accesses that var via window[id] to call .update().
        // With per-script eval() Script A's var is local to eval scope → window[id] is
        // undefined in Script B → TypeError. With real <script> elements, Script A's
        // top-level var IS on window → Script B succeeds.
        var scriptA = [
            'var __522_xmInst = { update: function(opts) { window.__522_updateCalled = opts; } };',
            'window.__522_renderCalled = true;'
        ].join('\n');

        var scriptB = 'window.__522_xmInst.update({ layVerify: "required" });';

        rehydrateViaElements([scriptA, scriptB]);

        expect(window.__522_renderCalled).toBe(true);
        expect(window.__522_updateCalled).toEqual({ layVerify: 'required' });
    });

    test('scripts execute in document order — A before B', () => {
        // Ordering matters: Script B must run after Script A has set up globals.
        var log = [];
        window.__522_log = log;

        var scriptA = 'window.__522_log.push("A");';
        var scriptB = 'window.__522_log.push("B");';
        var scriptC = 'window.__522_log.push("C");';

        rehydrateViaElements([scriptA, scriptB, scriptC]);

        expect(log).toEqual(['A', 'B', 'C']);

        delete window.__522_log;
    });

    test('each script runs in global scope — top-level var becomes window property', () => {
        // Core of the #522 fix: a top-level var declared inside a real inline <script>
        // is added to window. This is what eval() fails to reproduce (eval scope ≠ global).
        var scriptA = 'var __522_multiA = 42;';
        var scriptB = 'var __522_multiB = window.__522_multiA + 1;';
        var scriptC = 'window.__522_multiC = window.__522_multiB * 2;';

        rehydrateViaElements([scriptA, scriptB, scriptC]);

        expect(window.__522_multiA).toBe(42);
        expect(window.__522_multiB).toBe(43);
        expect(window.__522_multiC).toBe(86);
    });

    test('injected <script> elements are removed from DOM after execution', () => {
        var beforeCount = document.body.querySelectorAll('script').length;

        rehydrateViaElements([
            'window.__522_tidyCheck = true;',
            'window.__522_tidyCheck2 = true;'
        ]);

        var afterCount = document.body.querySelectorAll('script').length;
        // No net increase — injected elements were removed.
        expect(afterCount).toBe(beforeCount);
        // But the script effects persist.
        expect(window.__522_tidyCheck).toBe(true);
        expect(window.__522_tidyCheck2).toBe(true);

        delete window.__522_tidyCheck;
        delete window.__522_tidyCheck2;
    });

    test('empty _initScripts array: no script elements created, no errors', () => {
        var beforeCount = document.body.querySelectorAll('script').length;
        expect(() => rehydrateViaElements([])).not.toThrow();
        expect(document.body.querySelectorAll('script').length).toBe(beforeCount);
    });
});

// ─── Security: trust boundary not widened vs #462 ─────────────────────────

describe('#522 XSS trust boundary — same as #462', () => {
    // The fix re-runs the SAME script bodies that #462 extracted via DOMParser.
    // The DOMParser extraction already ensures only real <script> ELEMENTS are
    // included — "<script>" text inside attributes/text nodes is NOT captured.
    // This test confirms the extraction gate (DOMParser) remains in place.

    function extractScriptBodies(html) {
        // Mirrors the DOMParser extraction block in OpenDialog (unchanged by #522).
        var initScripts = [];
        try {
            var pdoc = new DOMParser().parseFromString(html, 'text/html');
            var nodes = pdoc.querySelectorAll('script');
            for (var ni = 0; ni < nodes.length; ni++) {
                var s = nodes[ni];
                var type = (s.getAttribute('type') || '').toLowerCase();
                var isJs = type === '' || type === 'text/javascript' ||
                           type === 'application/javascript' || type === 'module';
                if (isJs && !s.src && s.textContent) {
                    initScripts.push(s.textContent);
                }
            }
        } catch (e) { /* malformed → no scripts */ }
        return initScripts;
    }

    test('attribute-embedded script string is NOT extracted (DOMParser gate)', () => {
        // Attacker controls a data attribute — contains "<script>" text but is NOT
        // a script element. DOMParser correctly does not treat it as executable.
        var html =
            '<div data-x="<script>window.__pwned=1<\/script>">safe</div>' +
            '<script>window.__522_safeInit=1;<\/script>';

        var bodies = extractScriptBodies(html);

        // Only the real top-level <script> element — not the attribute-embedded one.
        expect(bodies).toHaveLength(1);
        expect(bodies[0]).toBe('window.__522_safeInit=1;');
    });

    test('markup XSS is still prevented by ff.SafeHtml (DOMPurify gate) — scripts extracted separately', () => {
        // SafeHtml strips <script> from markup AND sanitizes event handlers.
        // The extraction happens BEFORE SafeHtml (same as #462), so both gates apply.
        var html =
            '<img src=x onerror="window.__522_onerror=1">' +
            '<script>window.__522_legit=1;<\/script>';

        // Step 1: extract before SafeHtml
        var bodies = extractScriptBodies(html);
        expect(bodies).toHaveLength(1);
        expect(bodies[0]).toBe('window.__522_legit=1;');

        // Step 2: SafeHtml sanitizes the markup (simulate DOMPurify stripping onerror + scripts)
        var safeHtml = function(h) {
            return h
                .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
                .replace(/\sonerror="[^"]*"/gi, '');
        };
        var sanitized = safeHtml(html);
        expect(sanitized).not.toMatch(/onerror/i);
        expect(sanitized).not.toMatch(/<script/i);
        // The legit init script body was captured before sanitization — not lost.
        expect(bodies[0]).toBe('window.__522_legit=1;');
    });
});
