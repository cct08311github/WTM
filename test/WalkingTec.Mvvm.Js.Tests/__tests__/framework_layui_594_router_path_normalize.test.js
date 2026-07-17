/**
 * @jest-environment node
 *
 * See framework_layui_594_laytpl_escape_parity.test.js for why this file loads its own isolated
 * jsdom windows (helpers/layuiEngineLoader) and runs under plain `node` rather than Jest's
 * built-in jsdom testEnvironment.
 */

// Tests for issue #594 regression 2: layui 2.13.8's router().path carries a LEADING EMPTY
// ELEMENT that 2.6.3 never produced. Hash "#/FTP/X/Index" gives ["FTP","X","Index"] on 2.6.3 but
// ["","FTP","X","Index"] on 2.13.8 (root cause: the 2.6.3 regex strips "#/" as one unit before
// splitting, while 2.13.8 strips only "#", leaving the leading "/" to produce an empty first
// split segment). layuiadmin's index.js/pindex.js then do `view.render(path.join("/"))`, so the
// un-normalized 2.13.8 shape yields "//FTP/..." -- which the browser's HTML/URL machinery parses
// as a *protocol-relative* URL, sending the first real segment out as a hostname
// (GET http://ftp/... -> ERR_NAME_NOT_RESOLVED). The fix normalizes at each consumption site:
//
//   if(""===path[0]){path.shift();}
//
// -- a no-op on 2.6.3 (whose path never has a leading empty element for a normal route).

const fs = require('fs');
const path = require('path');
const { loadOldEngine, loadNextEngine, DEMO_WWWROOT } = require('./helpers/layuiEngineLoader');

/** The exact normalize snippet landed at every #594 consumption site (single shift, no loop). */
function normalize(pathArr) {
    if ('' === pathArr[0]) { pathArr.shift(); }
    return pathArr;
}

/**
 * Mirrors the FULL pattern actually deployed at index.js's/pindex.js's two sites: the #594
 * normalize shift, immediately followed by the pre-existing `o.length||(o=[""])` guard that
 * resets to a single-entry root path if the shift emptied the array. Tested together because the
 * two lines interact: on 2.6.3, a hash of "#/" yields path [""], which the shift alone would turn
 * into [] -- but the very next (pre-existing, unrelated to this fix) line restores it to [""],
 * matching what 2.13.8's shift-then-non-empty path already produces.
 */
function normalizeWithRootGuard(pathArr) {
    let out = pathArr;
    if ('' === out[0]) { out.shift(); }
    if (!out.length) { out = ['']; }
    return out;
}

describe('#594 simulated shapes — the two documented cases join identically after normalize', () => {
    test('2.6.3-style path (no leading empty) is a no-op', () => {
        const p = ['FTP', 'X', 'Index'];
        expect(normalize(p)).toEqual(['FTP', 'X', 'Index']);
    });

    test('2.13.8-style path (leading empty element) collapses to the same shape', () => {
        const p = ['', 'FTP', 'X', 'Index'];
        expect(normalize(p)).toEqual(['FTP', 'X', 'Index']);
    });

    test('both shapes join to the identical, non-protocol-relative path string', () => {
        const oldStyle = normalize(['FTP', 'X', 'Index']);
        const nextStyle = normalize(['', 'FTP', 'X', 'Index']);
        expect(oldStyle.join('/')).toBe('FTP/X/Index');
        expect(nextStyle.join('/')).toBe('FTP/X/Index');
        expect(oldStyle.join('/')).toBe(nextStyle.join('/'));
        // The actual bug symptom: a leading "//" makes the browser treat the joined string as a
        // protocol-relative URL. Confirms the fix removes it entirely (not just shrinks it).
        expect(oldStyle.join('/')).not.toMatch(/^\/\//);
        expect(nextStyle.join('/')).not.toMatch(/^\/\//);
    });
});

describe('#594 real router() output — regression exists, and normalize erases the divergence', () => {
    // Loads the REAL vendored router() implementations (not a reimplementation) from both trees.
    const oldLayui = loadOldEngine().layui;
    const nextLayui = loadNextEngine().layui;

    test('regression demonstration: real router().path diverges for a normal nested route', () => {
        const hash = '#/FTP/X/Index';
        const oldPath = oldLayui.router(hash).path;
        const nextPath = nextLayui.router(hash).path;

        expect(oldPath).toEqual(['FTP', 'X', 'Index']);
        expect(nextPath).toEqual(['', 'FTP', 'X', 'Index']); // the regression: leading empty element
        expect(oldPath).not.toEqual(nextPath);
    });

    test('fix: normalizing both real router() outputs produces an identical, sane join', () => {
        const hash = '#/FTP/X/Index';
        const oldPath = normalize(oldLayui.router(hash).path);
        const nextPath = normalize(nextLayui.router(hash).path);

        expect(oldPath).toEqual(['FTP', 'X', 'Index']);
        expect(nextPath).toEqual(['FTP', 'X', 'Index']);
        expect(oldPath.join('/')).toBe(nextPath.join('/'));
        expect(nextPath.join('/')).toBe('FTP/X/Index');
    });

    test('deeper nested route also normalizes identically', () => {
        const hash = '#/Sys/Role/Index';
        const oldPath = normalize(oldLayui.router(hash).path);
        const nextPath = normalize(nextLayui.router(hash).path);
        expect(oldPath.join('/')).toBe('Sys/Role/Index');
        expect(nextPath.join('/')).toBe('Sys/Role/Index');
    });
});

describe('#594 pathological hashes stay sane after normalize (no throw, no protocol-relative join)', () => {
    const oldLayui = loadOldEngine().layui;
    const nextLayui = loadNextEngine().layui;

    test('empty hash: both engines already agree ([]), normalize is a safe no-op', () => {
        const oldPath = oldLayui.router('').path;
        const nextPath = nextLayui.router('').path;
        expect(oldPath).toEqual([]);
        expect(nextPath).toEqual([]);

        expect(() => normalize(oldPath)).not.toThrow();
        expect(() => normalize(nextPath)).not.toThrow();
        expect(normalize(oldPath).join('/')).toBe('');
        expect(normalize(nextPath).join('/')).toBe('');
    });

    test('"#/" (root route): real per-engine shapes differ, but the deployed shift+root-guard combo used at index.js/pindex.js converges them', () => {
        const hash = '#/';
        const oldPath = oldLayui.router(hash).path;
        const nextPath = nextLayui.router(hash).path;

        // Ground truth (see probe): old engine gives [""], next engine gives ["",""].
        expect(oldPath).toEqual(['']);
        expect(nextPath).toEqual(['', '']);

        const oldNormalized = normalizeWithRootGuard(oldPath.slice());
        const nextNormalized = normalizeWithRootGuard(nextPath.slice());
        expect(oldNormalized).toEqual(['']);
        expect(nextNormalized).toEqual(['']);
        expect(oldNormalized.join('/')).toBe(nextNormalized.join('/'));
    });

    test('"#//x" (malformed double-slash hash): normalize alone does not fully equalize the two engines, but neither produces a dangerous leading "//" join', () => {
        const hash = '#//x';
        const oldPath = oldLayui.router(hash).path;
        const nextPath = nextLayui.router(hash).path;

        // Ground truth: old engine's stricter `#/` prefix regex only ever produces one leading
        // empty element for this input; 2.13.8's looser `#` regex produces two. A single `shift()`
        // (as literally deployed -- not a `while` loop) removes only one, so the two engines are
        // NOT required to end up byte-identical for this genuinely malformed input; what matters
        // is that the result is still safe (no throw, no protocol-relative "//" when joined).
        expect(oldPath).toEqual(['', 'x']);
        expect(nextPath).toEqual(['', '', 'x']);

        expect(() => normalize(oldPath)).not.toThrow();
        expect(() => normalize(nextPath)).not.toThrow();

        const oldJoined = oldPath.join('/');
        const nextJoined = nextPath.join('/');
        expect(oldJoined).not.toMatch(/^\/\//);
        expect(nextJoined).not.toMatch(/^\/\//);
        expect(oldJoined).toBe('x');
        expect(nextJoined).toBe('/x');
    });
});

describe('#594 admin.js hash(side) style — shift-only (no root guard), used only for menu-highlight comparisons', () => {
    // admin.js's `hash(side)` handler normalizes with just the shift (no `o.length||(o=[""])`
    // follow-up) since it only ever compares `path[0]`/`path[1]`/`path[2]` against menu item
    // names/jumps for CSS highlighting -- a cosmetic concern, not a navigation-breaking one.
    const oldLayui = loadOldEngine().layui;
    const nextLayui = loadNextEngine().layui;

    test('normal nested route: path[0]/[1]/[2] comparisons agree across engines after the shift', () => {
        const hash = '#/Sys/Role/Index';
        const oldPath = normalize(oldLayui.router(hash).path);
        const nextPath = normalize(nextLayui.router(hash).path);

        expect(oldPath[0]).toBe('Sys');
        expect(nextPath[0]).toBe('Sys');
        expect(oldPath[1]).toBe('Role');
        expect(nextPath[1]).toBe('Role');
        expect(oldPath[2]).toBe('Index');
        expect(nextPath[2]).toBe('Index');
    });
});

describe('#594 source-consistency sweep — normalize applied at all 5 documented sites, in every demo variant', () => {
    const DEMO_ROOT = path.resolve(DEMO_WWWROOT, '../..');
    // #679 retired VueDemo (Vue2) and ReactDemo (webpack4); only the surviving
    // demo variants are swept here. ("5 documented sites" above refers to the
    // 2+2+1 normalize call sites across index.js/pindex.js/lib/admin.js per
    // variant, not the variant count.)
    const VARIANTS = [
        'WalkingTec.Mvvm.Demo',
        'WalkingTec.Mvvm.Vue3Demo',
        'WalkingTec.Mvvm.BlazorDemo/WalkingTec.Mvvm.BlazorDemo',
    ];

    function markerCount(src) {
        return (src.match(/WTM #594/g) || []).length;
    }

    test.each(VARIANTS)('%s: index.js has exactly 2 normalize sites', (variant) => {
        const src = fs.readFileSync(path.join(DEMO_ROOT, variant, 'wwwroot/layuiadmin/index.js'), 'utf8');
        expect(markerCount(src)).toBe(2);
        expect(src).toMatch(/""===\w+\[0\]&&\w+\.shift\(\)/);
    });

    test.each(VARIANTS)('%s: pindex.js has exactly 2 normalize sites', (variant) => {
        const src = fs.readFileSync(path.join(DEMO_ROOT, variant, 'wwwroot/layuiadmin/pindex.js'), 'utf8');
        expect(markerCount(src)).toBe(2);
        expect(src).toMatch(/""===\w+\[0\]&&\w+\.shift\(\)/);
    });

    test.each(VARIANTS)('%s: lib/admin.js has exactly 1 normalize site (hash(side))', (variant) => {
        const src = fs.readFileSync(path.join(DEMO_ROOT, variant, 'wwwroot/layuiadmin/lib/admin.js'), 'utf8');
        expect(markerCount(src)).toBe(1);
        expect(src).toMatch(/hash\(side\)/);
        expect(src).toMatch(/""===\w+\[0\]&&\w+\.shift\(\)/);
    });
});
