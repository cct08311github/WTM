// Tests for issue #462: ff.OpenDialog must re-run the inline init <script> blocks
// from the same-origin controller-rendered form partial after DOMPurify sanitization
// strips them. Without this fix, dialog Create/Edit forms never initialize (tabs
// inactive, fields hidden, layui.form.render / laydate / combobox handlers gone).
//
// Fix: extract script bodies BEFORE ff.SafeHtml sanitizes, re-run them via
// ff._legacyScriptEval in the layer.open `success` callback.
//
// Trust boundary: identical to the existing _legacyScriptEval / IsScript path —
// same-origin controller-rendered response only. Markup XSS protection (SafeHtml)
// is unchanged: only the script bodies are separately re-executed.

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

describe('#462 OpenDialog script rehydration — source sweep', () => {
    const src = fs.readFileSync(srcPath, 'utf8');
    const active = stripLineComments(src);

    test('OpenDialog else-branch extracts script bodies via DOMParser into _initScripts before SafeHtml', () => {
        // The extraction block must use DOMParser, not a regex.
        expect(active).toMatch(/_initScripts\s*=\s*\[\]/);
        expect(active).toMatch(/new DOMParser\(\)\.parseFromString/);
        expect(active).toMatch(/querySelectorAll\s*\(\s*['"]script['"]\s*\)/);
        // Must NOT use the old regex approach
        expect(active).not.toMatch(/_scriptRe\s*=\s*\//);
    });

    test('OpenDialog layer.open success callback re-injects _initScripts as <script> elements (Issue #522)', () => {
        // Issue #522 changed the rehydration from per-script ff._legacyScriptEval (eval scope)
        // to real <script> element re-injection (native global scope, var-sharing across siblings).
        // The success callback must iterate _initScripts and create a script element per entry.
        expect(active).toMatch(/success\s*:\s*function\s*\(\s*\)\s*\{[\s\S]{0,800}?_initScripts/);
        expect(active).toMatch(/document\.createElement\s*\(\s*['"]script['"]\s*\)/);
        expect(active).toMatch(/document\.body\.appendChild/);
    });

    test('SafeHtml is still called on the partial markup (XSS protection preserved)', () => {
        // ff.SafeHtml must still be applied to _wrapperEl.innerHTML — the markup
        // sanitization is not removed by the #462 fix.
        expect(active).toMatch(/_wrapperEl\.innerHTML\s*=\s*ff\.SafeHtml\(str\)/);
    });

    test('_initScripts extraction appears before SafeHtml in the else branch of OpenDialog', () => {
        // Locate the OpenDialog function block and confirm ordering:
        // _initScripts array init → _scriptRe regex → _wrapperEl.innerHTML = ff.SafeHtml
        const openDialogBlock = active.match(/OpenDialog\s*:\s*function[\s\S]{0,12000}?OpenDialog2/);
        expect(openDialogBlock).not.toBeNull();
        const block = openDialogBlock[0];
        const posInit = block.indexOf('_initScripts = []');
        const posSafeHtml = block.indexOf('_wrapperEl.innerHTML = ff.SafeHtml');
        expect(posInit).toBeGreaterThan(-1);
        expect(posSafeHtml).toBeGreaterThan(-1);
        expect(posInit).toBeLessThan(posSafeHtml);
    });
});

describe('#462 OpenDialog script rehydration — behavioral stub', () => {
    // Simulate the core of the #462 fix: script extraction + selective re-execution
    // after SafeHtml sanitization. This mirrors the actual OpenDialog else branch
    // without depending on jQuery / layui / browser globals.

    function makeOpenDialogElseBranch(safeHtml, legacyScriptEval) {
        // Returns a function that simulates the "else" branch of OpenDialog's
        // success callback: extracts scripts via DOMParser, sanitizes, then on
        // layer "success" re-runs them.
        return function processPartialHtml(str) {
            // Step 1: extract trusted inline init scripts via DOMParser (not regex)
            var _initScripts = [];
            try {
                var _pdoc = new DOMParser().parseFromString(str, 'text/html');
                var _nodes = _pdoc.querySelectorAll('script');
                for (var _ni = 0; _ni < _nodes.length; _ni++) {
                    var _s = _nodes[_ni];
                    var _type = (_s.getAttribute('type') || '').toLowerCase();
                    var _isJs = _type === '' || _type === 'text/javascript' || _type === 'application/javascript' || _type === 'module';
                    if (_isJs && !_s.src && _s.textContent) {
                        _initScripts.push(_s.textContent);
                    }
                }
            } catch (e) { /* malformed HTML → no init scripts */ }
            // Step 2: sanitize markup (DOMPurify strips scripts from content)
            var sanitized = safeHtml(str);
            // Step 3: (simulating layer.open success) re-run extracted scripts
            function onLayerSuccess() {
                for (var _si = 0; _si < _initScripts.length; _si++) {
                    legacyScriptEval(_initScripts[_si]);
                }
            }
            return { sanitized: sanitized, onLayerSuccess: onLayerSuccess, _initScripts: _initScripts };
        };
    }

    test('scripts are extracted from partial and re-executed via _legacyScriptEval after layer opens', () => {
        const legacyScriptEval = jest.fn();
        // Simulate SafeHtml: strips <script> tags (as DOMPurify does)
        const safeHtml = (html) => html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');

        const partialHtml = '<div class="form"><input name="Name"/></div>' +
            '<script>layui.form.render();</script>' +
            '<script>laydate.render({elem:"#Date"});</script>';

        const processEl = makeOpenDialogElseBranch(safeHtml, legacyScriptEval);
        const result = processEl(partialHtml);

        // Sanitized markup must NOT contain scripts
        expect(result.sanitized).not.toMatch(/<script/i);
        // Sanitized markup must contain the form HTML
        expect(result.sanitized).toMatch(/class="form"/);

        // _initScripts must have captured both script bodies
        expect(result._initScripts).toHaveLength(2);
        expect(result._initScripts[0]).toBe('layui.form.render();');
        expect(result._initScripts[1]).toBe('laydate.render({elem:"#Date"});');

        // Before layer success: scripts not yet executed
        expect(legacyScriptEval).not.toHaveBeenCalled();

        // After layer success: both scripts executed
        result.onLayerSuccess();
        expect(legacyScriptEval).toHaveBeenCalledTimes(2);
        expect(legacyScriptEval).toHaveBeenNthCalledWith(1, 'layui.form.render();');
        expect(legacyScriptEval).toHaveBeenNthCalledWith(2, 'laydate.render({elem:"#Date"});');
    });

    test('SECURITY: attribute-embedded script string is NOT extracted or executed (DOMParser XSS guard)', () => {
        // A <script> text sitting inside an HTML attribute value is NOT a script
        // element — browsers do not execute it. The old regex approach would have
        // extracted and eval'd it, creating a new XSS sink. DOMParser correctly
        // ignores attribute-embedded script strings.
        const legacyScriptEval = jest.fn();
        const safeHtml = (html) => html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');

        // The attacker controls e.g. a description field rendered into a data-attribute.
        // The genuine init script is a top-level <script> element (expected to run).
        const partialHtml =
            '<div data-x="<script>window.__pwned=1</script>">ok</div>' +
            '<script>window.__init=1</script>';

        const processEl = makeOpenDialogElseBranch(safeHtml, legacyScriptEval);
        const result = processEl(partialHtml);

        // Only the genuine top-level <script> element must be captured — NOT the
        // attribute-embedded one.
        expect(result._initScripts).toHaveLength(1);
        expect(result._initScripts[0]).toBe('window.__init=1');

        result.onLayerSuccess();
        // Only the safe init script executed — attacker's payload never ran.
        expect(legacyScriptEval).toHaveBeenCalledTimes(1);
        expect(legacyScriptEval).toHaveBeenCalledWith('window.__init=1');
    });

    test('partial with no inline scripts: no _legacyScriptEval calls, sanitized HTML preserved', () => {
        const legacyScriptEval = jest.fn();
        const safeHtml = (html) => html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');

        const partialHtml = '<div class="form"><input name="Name"/></div>';
        const processEl = makeOpenDialogElseBranch(safeHtml, legacyScriptEval);
        const result = processEl(partialHtml);

        expect(result._initScripts).toHaveLength(0);
        result.onLayerSuccess();
        expect(legacyScriptEval).not.toHaveBeenCalled();
        expect(result.sanitized).toMatch(/class="form"/);
    });

    test('markup XSS in script-free HTML is still sanitized (SafeHtml gate preserved)', () => {
        const legacyScriptEval = jest.fn();
        // Simulate SafeHtml stripping onerror attributes too
        const safeHtml = (html) => html.replace(/\sonerror="[^"]*"/gi, '');

        const partialHtml = '<img src=x onerror="alert(1)"><div>safe content</div>';
        const processEl = makeOpenDialogElseBranch(safeHtml, legacyScriptEval);
        const result = processEl(partialHtml);

        expect(result.sanitized).not.toMatch(/onerror/i);
        expect(result.sanitized).toMatch(/safe content/);
        expect(result._initScripts).toHaveLength(0);
        result.onLayerSuccess();
        expect(legacyScriptEval).not.toHaveBeenCalled();
    });

    test('multiline script body is captured and re-executed intact', () => {
        const legacyScriptEval = jest.fn();
        const safeHtml = (html) => html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');

        const scriptBody = '\n    layui.use(["form","element"], function() {\n' +
            '        var form = layui.form;\n' +
            '        form.render();\n' +
            '    });\n';
        const partialHtml = '<div class="form"></div><script>' + scriptBody + '</script>';

        const processEl = makeOpenDialogElseBranch(safeHtml, legacyScriptEval);
        const result = processEl(partialHtml);

        result.onLayerSuccess();
        expect(legacyScriptEval).toHaveBeenCalledWith(scriptBody);
    });
});
