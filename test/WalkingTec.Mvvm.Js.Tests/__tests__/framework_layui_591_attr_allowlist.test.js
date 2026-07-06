// Tests for issue #591: ff.SafeHtml (DOMPurify) strips WTM's own custom
// lay-*/wtm-*/div-for attributes (and, per the review-round follow-up audit,
// non-prefixed framework marker attributes) from dialog partials.
//
// Root cause: DOMPurify's internal default ALLOWED_ATTR list only recognizes
// standard HTML attributes. Every dialog partial rendered via ff.OpenDialog
// (and every PostForm/BgRequest redraw) is run through ff.SafeHtml before
// insertion, so any non-standard framework attribute — lay-filter scoped
// re-render, lay-skin styling, lay-verify/lay-reqtext validation, wtm-*
// combo/tree/chain-change metadata, and non-prefixed markers like subpro
// (master-detail grid clear), IsSearchButton/oldpost/chartlink (search
// panel wiring), and ischart (dialog chart resize) — was silently dropped.
//
// Fix: an explicit ADD_ATTR allowlist (source-audited — see the #591 PR body
// for the full per-attribute emission-site inventory) restores exactly the
// attributes WTM TagHelpers emit, with no wildcard/regex hook and no
// weakening of the existing FORBID_TAGS/FORBID_ATTR blocklists.
//
// Review-round follow-up: the initial audit grepped only for lay-/wtm-/
// div-for prefixed tokens, which missed five non-prefixed attributes
// (subpro, IsSearchButton, oldpost, ischart, chartlink) emitted by
// DataTableTagHelper, SearchPanelTagHelper, and ChartTagHelper. A full-source
// re-audit (Attributes.Add/SetAttribute + raw HTML literals across all of
// TagHelpers.LayUI, cross-checked against getAttribute/.attr reads in
// framework_layui.js) added them here and to ADD_ATTR.

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

// The full audited allowlist that SafeHtml's ADD_ATTR must contain — kept in
// sync with the emission-site inventory in the PR body. Any change to this
// list in framework_layui.js requires a matching, deliberate change here.
const EXPECTED_ADD_ATTR = [
    'lay-filter', 'lay-verify', 'lay-reqtext', 'lay-skin', 'lay-text',
    'lay-submit', 'lay-accordion', 'lay-allowclose', 'lay-height',
    'lay-title', 'lay-ignore', 'lay-percent', 'lay-showpercent',
    'wtm-name', 'wtm-ctype', 'wtm-multi', 'wtm-linkto', 'wtm-cf',
    'wtm-turl', 'div-for',
    // Review-round follow-up: non-prefixed marker attributes missed by the
    // initial lay-/wtm-/div-for grep sweep (see file header comment).
    'subpro', 'issearchbutton', 'oldpost', 'ischart', 'chartlink',
    // Issue #601: CodeTagHelper's lay-encode (sibling of lay-height/lay-title
    // above) — the layui code module (both the 2.6.3 and 2.13.8 vendored
    // trees) reads lay-encode, never the bare `encode` CodeTagHelper used to
    // emit; #601 renamed the emission and added the matching ADD_ATTR entry.
    'lay-encode'
];

// Attributes that must NOT be allowlisted, even though they are real layui
// attribute names, because WTM markup that flows through ff.SafeHtml never
// needs them and they are name-resolved event/handler bindings.
const EXCLUDED_EVENT_ATTRS = ['lay-on', 'lay-event'];

// ─── Source-sweep tests ────────────────────────────────────────────────────

describe('#591 source sweep — ADD_ATTR allowlist wired into ff.SafeHtml', () => {
    const src = fs.readFileSync(srcPath, 'utf8');
    const active = stripLineComments(src);

    test('ff.SafeHtml still forbids <script>/<style> (FORBID_TAGS unchanged)', () => {
        expect(active).toMatch(/FORBID_TAGS\s*:\s*\[['"]script['"],\s*['"]style['"]\]/);
    });

    test('ff.SafeHtml still forbids inline event-handler attributes (FORBID_ATTR unchanged)', () => {
        expect(active).toMatch(/FORBID_ATTR\s*:\s*\[[^\]]*['"]onerror['"]/);
        expect(active).toMatch(/FORBID_ATTR\s*:\s*\[[^\]]*['"]onload['"]/);
        expect(active).toMatch(/FORBID_ATTR\s*:\s*\[[^\]]*['"]onclick['"]/);
    });

    test('ff.SafeHtml config carries an ADD_ATTR allowlist', () => {
        expect(active).toMatch(/ADD_ATTR\s*:\s*\[/);
    });

    test('ADD_ATTR contains every audited attribute exactly once', () => {
        const match = active.match(/ADD_ATTR\s*:\s*\[([^\]]*)\]/);
        expect(match).not.toBeNull();
        const listed = match[1]
            .split(',')
            .map((s) => s.trim().replace(/^['"]|['"]$/g, ''))
            .filter((s) => s.length > 0);

        expect(listed.sort()).toEqual([...EXPECTED_ADD_ATTR].sort());
    });

    test('ADD_ATTR does NOT contain lay-on or lay-event (name-resolved event binding)', () => {
        const match = active.match(/ADD_ATTR\s*:\s*\[([^\]]*)\]/);
        const listContent = match[1];
        for (const attr of EXCLUDED_EVENT_ATTRS) {
            expect(listContent).not.toMatch(new RegExp(`['"]${attr}['"]`));
        }
    });

    test('#591 is referenced near the ADD_ATTR config (review trail)', () => {
        // Must reference the issue in a comment near SafeHtml.
        const safeHtmlBlock = src.slice(
            src.indexOf('SafeHtml:') - 2000 > 0 ? src.indexOf('SafeHtml:') - 2000 : 0,
            src.indexOf('SafeHtml:') + 500
        );
        expect(safeHtmlBlock).toMatch(/#591/);
    });

    test('no ALLOW_UNKNOWN_PROTOCOLS / wildcard attribute hooks were introduced', () => {
        expect(active).not.toMatch(/ALLOW_UNKNOWN_PROTOCOLS/);
        expect(active).not.toMatch(/uponSanitizeAttribute/);
        expect(active).not.toMatch(/ADD_ATTR\s*:\s*\[\s*\/.*\/\s*\]/); // no regex entries
    });

    test('eval() count stays at exactly 1 (only ff._legacyScriptEval)', () => {
        const evalMatches = active.match(/\beval\s*\(/g) || [];
        expect(evalMatches.length).toBe(1);
    });
});

// ─── Semantic tests (real DOMPurify + real ff.SafeHtml) ───────────────────

describe('#591 semantic — ff.SafeHtml preserves framework attributes (jsdom + vendored DOMPurify)', () => {
    const purifyPath = path.resolve(
        __dirname,
        '../../../src/WalkingTec.Mvvm.Mvc/dompurify.js'
    );
    const purifySource = fs.readFileSync(purifyPath, 'utf8');

    beforeAll(() => {
        // dompurify.js is a UMD bundle; executing it against the jsdom window
        // attaches window.DOMPurify, which ff.SafeHtml (already loaded onto
        // the same global by setup.js) delegates to.
        const runInWindow = new Function('window', 'document', purifySource);
        runInWindow(global.window || global, global.document || {});
    });

    test('sanity: DOMPurify loaded and ff.SafeHtml is wired up', () => {
        expect(typeof ff.SafeHtml).toBe('function');
        expect(typeof (global.window || global).DOMPurify.sanitize).toBe('function');
    });

    test('#591 PoC: lay-filter and lay-skin both survive sanitize', () => {
        const out = ff.SafeHtml('<form lay-filter="x"><input lay-skin="switch"></form>');
        expect(out).toMatch(/lay-filter="x"/);
        expect(out).toMatch(/lay-skin="switch"/);
    });

    test.each(EXPECTED_ADD_ATTR)('allowlisted attribute %s survives SafeHtml', (attr) => {
        const html = `<div ${attr}="v"></div>`;
        const out = ff.SafeHtml(html);
        expect(out).toMatch(new RegExp(`${attr}="v"`));
    });

    test('onclick/onerror event-handler attributes are still stripped', () => {
        const out = ff.SafeHtml('<img src="x" onerror="alert(1)"><div onclick="alert(2)">hi</div>');
        expect(out).not.toMatch(/onerror/i);
        expect(out).not.toMatch(/onclick/i);
    });

    test('<script> and <style> tags are still stripped', () => {
        const out = ff.SafeHtml('<script>alert(1)</script><style>body{color:red}</style><div>ok</div>');
        expect(out).not.toMatch(/<script/i);
        expect(out).not.toMatch(/<style/i);
        expect(out).toMatch(/<div>ok<\/div>/);
    });

    test('lay-on is still stripped (name-resolved event binding, excluded by design)', () => {
        const out = ff.SafeHtml('<button lay-on="click">go</button>');
        expect(out).not.toMatch(/lay-on/);
    });

    test('lay-event is still stripped (name-resolved event binding, excluded by design)', () => {
        const out = ff.SafeHtml('<a class="layui-btn" lay-event="edit">edit</a>');
        expect(out).not.toMatch(/lay-event/);
    });

    test('attribute-value escape attempt (unescaped quote breakout) stays inert', () => {
        // Simulates a server bug where a field value reached an attribute
        // without proper escaping. The HTML parser (not DOMPurify's allow/deny
        // logic) closes the attribute/tag at the first literal quote — any
        // injected <script> becomes a sibling element and is stripped by
        // FORBID_TAGS regardless of ADD_ATTR.
        const out = ff.SafeHtml('<form lay-filter="x"><script>alert(1)</script>"></form>');
        expect(out).not.toMatch(/<script/i);
        expect(out).not.toMatch(/alert\(1\)/);
        expect(out).toMatch(/lay-filter="x"/);
    });

    test('attribute-value escape attempt (javascript: scheme in a custom attribute) stays inert', () => {
        // DOMPurify's own IS_ALLOWED_URI safety net rejects values that look
        // like a script-ish protocol even on non-URI attributes when the
        // value doesn't parse as a normal token.
        const out = ff.SafeHtml('<div wtm-turl="javascript:alert(1)"></div>');
        expect(out).not.toMatch(/javascript:/i);
    });

    test('realistic <wt:form>-shaped dialog partial fragment round-trips with framework attributes intact', () => {
        // Mirrors what FormTagHelper + SwitchTagHelper + BaseFieldTag actually
        // emit into a dialog partial: a form with lay-filter, a lay-skin
        // switch, and a required text input with lay-verify/lay-reqtext.
        const fragment = `
<form lay-filter="myformfilter" class="layui-form">
  <div class="layui-form-item">
    <label class="layui-form-label">Name</label>
    <div class="layui-input-block">
      <input type="text" name="Name" wtm-name="Name" lay-verify="required" lay-reqtext="Name is required" class="layui-input" />
    </div>
  </div>
  <div class="layui-form-item">
    <label class="layui-form-label">Enabled</label>
    <div class="layui-input-block">
      <input type="checkbox" name="Enabled" lay-skin="switch" lay-text="Yes|No" />
    </div>
  </div>
  <button type="submit" lay-submit lay-filter="myformfilterbtn">Submit</button>
</form>`;

        const out = ff.SafeHtml(fragment);

        expect(out).toMatch(/lay-filter="myformfilter"/);
        expect(out).toMatch(/wtm-name="Name"/);
        expect(out).toMatch(/lay-verify="required"/);
        expect(out).toMatch(/lay-reqtext="Name is required"/);
        expect(out).toMatch(/lay-skin="switch"/);
        expect(out).toMatch(/lay-text="Yes\|No"/);
        expect(out).toMatch(/lay-submit/);
        expect(out).toMatch(/lay-filter="myformfilterbtn"/);
        expect(out).not.toMatch(/<script/i);
    });

    // ─── Review-round: non-prefixed marker attributes (subpro, IsSearchButton,
    // oldpost, ischart, chartlink) — each mirrors the exact shape the real
    // TagHelper emits, not just the generic test.each `<div attr="v">` case.

    test('#591 review round: subpro (DataTableTagHelper detail-grid marker) survives on <table>', () => {
        // DataTableTagHelper.cs:444 -- output.Attributes.Add("subpro", prefix)
        // on the <table> element; read by GetFormData (framework_layui.js)
        // via tables[i].attributes["subpro"].value.
        const out = ff.SafeHtml('<table id="grid1" subpro="Children"></table>');
        expect(out).toMatch(/subpro="Children"/);
    });

    test('#591 review round: issearchbutton (SearchPanelTagHelper search button) survives as boolean attribute', () => {
        // SearchPanelTagHelper.cs:206 -- raw literal `<a ... IsSearchButton>`
        // (no value). HTML lower-cases attribute names, so DOMPurify and
        // jQuery's `a[IsSearchButton]` selector both see `issearchbutton`.
        const out = ff.SafeHtml('<a href="javascript:void(0)" IsSearchButton>Search</a>');
        expect(out).toMatch(/issearchbutton/i);
    });

    test('#591 review round: oldpost (SearchPanelTagHelper old-post marker) survives on <form>', () => {
        // SearchPanelTagHelper.cs:170 -- output.Attributes.Add("oldpost", true);
        // read by RefreshGrid: form.attr("oldpost") == 'True'.
        const out = ff.SafeHtml('<form class="layui-form" oldpost="True"></form>');
        expect(out).toMatch(/oldpost="True"/);
    });

    test('#591 review round: ischart (ChartTagHelper dialog-resize marker) survives on <div>', () => {
        // ChartTagHelper.cs:94 -- output.Attributes.Add("ischart", "1");
        // read via $(layero).find("div[ischart = '1']") in dialog
        // resize/full/restore callbacks and ResizeChart.
        const out = ff.SafeHtml('<div id="chart1" ischart="1"></div>');
        expect(out).toMatch(/ischart="1"/);
    });

    test('#591 review round: chartlink (SearchPanelTagHelper chart-linked searcher marker) survives on <form>', () => {
        // SearchPanelTagHelper.cs:236 -- output.Attributes.SetAttribute("chartlink", ChartId);
        // read by RefreshChart: $('form[chartlink*="' + chartid + '"]').
        const out = ff.SafeHtml('<form class="layui-form" chartlink="chart1"></form>');
        expect(out).toMatch(/chartlink="chart1"/);
    });
});
