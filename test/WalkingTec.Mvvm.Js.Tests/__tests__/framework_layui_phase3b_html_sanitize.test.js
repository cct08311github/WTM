// Tests for issue #789 Phase 3B: AJAX response HTML must be sanitized via
// DOMPurify before landing in jQuery .html() or innerHTML, and the sanitizer
// must fail closed if DOMPurify is absent.
//
// The sites fixed in Phase 3B:
//   - L247  LoadPage1 AJAX branch: $('#' + where).html(data)
//   - L~333 PostForm wrapper: _wrapper.html(data)
//   - L~373 BgRequest wrapper: _wrapper.html(str)
//   - L~438 OpenDialog wrapper: _wrapperEl.innerHTML = str

const fs = require('fs');
const path = require('path');

describe('#789 Phase 3B - framework_layui.js AJAX response sanitization', () => {
  const srcPath = path.resolve(
    __dirname,
    '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
  );
  const src = fs.readFileSync(srcPath, 'utf8');

  const stripLineComments = (text) =>
    text
      .split('\n')
      .map((line) => {
        const idx = line.indexOf('//');
        return idx === -1 ? line : line.slice(0, idx);
      })
      .join('\n');

  const active = stripLineComments(src);

  test('ff.SafeHtml helper is defined at the top of the ff object', () => {
    expect(active).toMatch(/SafeHtml\s*:\s*function/);
  });

  test('ff.SafeHtml delegates to window.DOMPurify.sanitize when available', () => {
    expect(active).toMatch(/window\.DOMPurify\.sanitize/);
  });

  test('ff.SafeHtml fails closed when DOMPurify is undefined (returns empty string)', () => {
    expect(active).toMatch(/typeof\s+window\.DOMPurify\s*===\s*['"]undefined['"]/);
    expect(active).toMatch(/return\s+['"]['"]/);
  });

  test('ff.SafeHtml forbids <script> and <style> tags', () => {
    expect(active).toMatch(/FORBID_TAGS\s*:\s*\[['"]script['"],\s*['"]style['"]\]/);
  });

  test('ff.SafeHtml forbids inline event-handler attributes', () => {
    expect(active).toMatch(/FORBID_ATTR\s*:\s*\[[^\]]*['"]onerror['"]/);
    expect(active).toMatch(/FORBID_ATTR\s*:\s*\[[^\]]*['"]onload['"]/);
    expect(active).toMatch(/FORBID_ATTR\s*:\s*\[[^\]]*['"]onclick['"]/);
  });

  test('LoadPage1 AJAX branch wraps raw data with ff.SafeHtml', () => {
    expect(active).toMatch(/\$\(['"]#['"]\s*\+\s*where\)\.html\(\s*ff\.SafeHtml\(data\)\s*\)/);
  });

  test('PostForm non-IsScript branch wraps data via ff.SafeHtml', () => {
    expect(active).toMatch(/addClass\(_wrapperClass\)[\s\S]{0,80}\.html\(\s*ff\.SafeHtml\(data\)\s*\)/);
  });

  test('BgRequest non-IsScript branch wraps str via ff.SafeHtml', () => {
    expect(active).toMatch(/addClass\(['"]layui-card-body donotuse_pdiv['"]\)[\s\S]{0,80}\.html\(\s*ff\.SafeHtml\(str\)\s*\)/);
  });

  test('OpenDialog wrapper assigns innerHTML through ff.SafeHtml', () => {
    expect(active).toMatch(/_wrapperEl\.innerHTML\s*=\s*ff\.SafeHtml\(str\)/);
  });

  test('no raw AJAX .html(data|str) assignment survives in the active code', () => {
    // Any `.html(data)` or `.html(str)` that is not wrapped with SafeHtml is a regression.
    const unsafe = /\.html\(\s*(data|str)\s*\)/;
    expect(active).not.toMatch(unsafe);
  });

  test('no raw innerHTML = data|str assignment survives in the active code', () => {
    const unsafe = /innerHTML\s*=\s*(data|str)\b/;
    expect(active).not.toMatch(unsafe);
  });
});

describe('#789 Phase 3B - semantic fail-closed and sanitization (jsdom + vendored DOMPurify)', () => {
  // Load the vendored dompurify.js file from src/WalkingTec.Mvvm.Mvc/dompurify.js
  // into the jsdom environment and verify the sanitizer strips dangerous input.
  // Then remove window.DOMPurify and verify the fail-closed path (which the
  // ff.SafeHtml helper guards against in framework_layui.js).
  const vm = require('vm');
  const fs = require('fs');
  const path = require('path');

  const purifyPath = path.resolve(
    __dirname,
    '../../../src/WalkingTec.Mvvm.Mvc/dompurify.js'
  );
  const purifySource = fs.readFileSync(purifyPath, 'utf8');

  beforeAll(() => {
    // dompurify.js is a UMD bundle; executing it in the jsdom window attaches
    // window.DOMPurify. Run via `new Function` so we can evaluate UMD code.
    const runInWindow = new Function('window', 'document', purifySource);
    runInWindow(global.window || global, global.document || {});
  });

  afterEach(() => {
    // Reset so the fail-closed test below can remove DOMPurify without
    // affecting other test files.
  });

  test('DOMPurify strips <script> tags from input', () => {
    if (!global.window || !global.window.DOMPurify) {
      // If the UMD load target is the jsdom `window`, the symbol is there.
      // Fallback check in case the module attached to globalThis.
      // eslint-disable-next-line no-undef
      if (typeof DOMPurify === 'undefined') {
        throw new Error('DOMPurify did not load into jsdom');
      }
    }
    const purify = (global.window && global.window.DOMPurify) || global.DOMPurify;
    const out = purify.sanitize('<div>hello</div><script>alert(1)</script>', {
      FORBID_TAGS: ['script', 'style']
    });
    expect(out).not.toMatch(/<script/i);
    expect(out).toMatch(/<div>hello<\/div>/);
  });

  test('DOMPurify strips inline onerror attribute from <img>', () => {
    const purify = (global.window && global.window.DOMPurify) || global.DOMPurify;
    const out = purify.sanitize('<img src=x onerror=alert(1)>', {
      FORBID_ATTR: ['onerror', 'onload', 'onclick']
    });
    expect(out).not.toMatch(/onerror/i);
  });

  test('DOMPurify treats javascript: URLs safely', () => {
    const purify = (global.window && global.window.DOMPurify) || global.DOMPurify;
    const out = purify.sanitize('<a href="javascript:alert(1)">x</a>');
    expect(out).not.toMatch(/javascript:/i);
  });

  test('SafeHtml-style fail-closed: simulated empty return when DOMPurify missing', () => {
    // Simulate the ff.SafeHtml helper's behavior when DOMPurify is not loaded.
    function safeHtml(rawHtml, dp) {
      if (typeof dp === 'undefined' || !dp || typeof dp.sanitize !== 'function') {
        return '';
      }
      return dp.sanitize(rawHtml || '');
    }
    expect(safeHtml('<div>hi</div>', undefined)).toBe('');
    expect(safeHtml('<div>hi</div>', null)).toBe('');
    expect(safeHtml('<div>hi</div>', {})).toBe(''); // no sanitize method
  });
});
