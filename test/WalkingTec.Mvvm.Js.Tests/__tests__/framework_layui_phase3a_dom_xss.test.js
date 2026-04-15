// Tests for issue #789 Phase 3A: DOM XSS via cookie-to-id concatenation and
// iframe URL concatenation in framework_layui.js must be gone.
//
// The sites fixed in Phase 3A:
//   - L230 LoadPage1 iframe: url concatenated into <iframe src='...'> HTML string
//   - L317-321 PostForm wrapper div: $.cookie("divid") concatenated into <div id='...'>
//   - L356-358 BgRequest wrapper div: same cookie pattern
//   - L414 OpenDialog wrapper div: same cookie pattern passed to layer.open content
//
// These tests regex-sweep the JS source to ensure the unsafe patterns are gone
// and the new DOM-API-based safe pattern is in place.

const fs = require('fs');
const path = require('path');

describe('#789 Phase 3A — cookie/id and iframe url DOM XSS removal', () => {
  const srcPath = path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js');
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

  test('no cookie value concatenated into <div id> HTML string', () => {
    // Matches patterns like:
    //   "<div id='" + $.cookie("divid") + "'
    //   '<div id="' + $.cookie("divid") + '"
    const bad = /["\']<div[^<>]*id=["\']["\']?\s*\+\s*\$\.cookie\(/;
    expect(active).not.toMatch(bad);
  });

  test('no url concatenated into <iframe src> HTML string', () => {
    // Matches patterns like:
    //   "<iframe ... src='" + url + "'
    const bad = /["\']<iframe[^<>]*src=["\']["\']?\s*\+\s*url\s*\+/;
    expect(active).not.toMatch(bad);
  });

  test('new safe pattern: document.createElement(\'iframe\') with .src setter', () => {
    expect(active).toMatch(/document\.createElement\(\s*["\']iframe["\']\s*\)/);
    expect(active).toMatch(/_iframe\.src\s*=\s*url/);
  });

  test('new safe pattern: jQuery $(\'<div/>\').attr(\'id\', $.cookie(...))', () => {
    // PostForm and BgRequest use jQuery builder form
    const matches =
      active.match(/\$\(\s*["\']<div\/?>\s*["\']\s*\)\s*[\n\s]*\.attr\(\s*["\']id["\']\s*,\s*\$\.cookie\(/g) || [];
    expect(matches.length).toBeGreaterThanOrEqual(2);
  });

  test('new safe pattern: setAttribute(\'id\', $.cookie(...)) for OpenDialog wrapper', () => {
    expect(active).toMatch(/setAttribute\(\s*["\']id["\']\s*,\s*\$\.cookie\(/);
  });
});

describe('#789 Phase 3A — safe attribute escaping semantics (jsdom)', () => {
  // These tests exercise the jsdom DOM API directly to verify that the fix
  // pattern — document.createElement + setAttribute + outerHTML — safely
  // escapes attacker-controlled values so they cannot break out of an id
  // attribute.

  test('setAttribute serializes single-quote break-out safely', () => {
    const el = document.createElement('div');
    const attackerCookie = "' onmouseover='alert(1)";
    el.setAttribute('id', attackerCookie);
    el.innerHTML = 'inner content';

    // Round-trip through outerHTML and reparse
    const wrapper = document.createElement('div');
    wrapper.innerHTML = el.outerHTML;
    const roundTripped = wrapper.firstChild;

    // The id is preserved literally, not interpreted as extra attributes.
    expect(roundTripped.id).toBe(attackerCookie);
    expect(roundTripped.hasAttribute('onmouseover')).toBe(false);
  });

  test('setAttribute serializes double-quote break-out safely', () => {
    const el = document.createElement('div');
    const attackerCookie = '"><script>alert(1)</script><div id="';
    el.setAttribute('id', attackerCookie);

    // Same round trip: the malicious script tag is escaped inside the id
    // attribute value, never parsed as an element.
    const wrapper = document.createElement('div');
    wrapper.innerHTML = el.outerHTML;
    expect(wrapper.querySelector('script')).toBeNull();
  });

  test('iframe.src setter rejects javascript: by surrounding guard', () => {
    // The code already guards the iframe branch with
    //   if (url.indexOf("http://") === 0 || url.indexOf("https://") === 0)
    // This test locks that guard in place as a regression signal.
    const srcPath = require('path').resolve(
      __dirname,
      '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
    );
    const src = require('fs').readFileSync(srcPath, 'utf8');
    expect(src).toMatch(/url\.indexOf\(["\']http:\/\/["\']\)\s*===\s*0/);
    expect(src).toMatch(/url\.indexOf\(["\']https:\/\/["\']\)\s*===\s*0/);
  });
});
