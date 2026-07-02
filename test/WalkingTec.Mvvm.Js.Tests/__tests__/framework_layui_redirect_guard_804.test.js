// Tests for issue #804: open-redirect defense in depth. Server-side
// WtmActionResultExtension.Redirect already throws on absolute URLs,
// but in case a compromised/custom controller emits raw JSON bypassing
// the extension, the client dispatcher must also reject non-relative URLs.
//
// What this file locks in place:
//   1. framework_layui.js redirect case validates URL shape before
//      assigning to location.href.
//   2. Absolute, protocol-relative, javascript:, data: URLs are all
//      rejected with a console.warn.
//   3. Issue #534: a leading "/" followed by a backslash (e.g. "/\evil.com")
//      is also rejected. Browsers normalize "\" to "/" for special schemes,
//      so the original charAt(1) !== '/' check let this bypass the guard
//      and navigate to "//evil.com" (open redirect).

const fs = require('fs');
const path = require('path');

describe('#804 open-redirect guard — framework_layui.js source sweep', () => {
  const src = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
  );

  test("redirect case is not a bare location.href = action.url", () => {
    // Before the fix the pattern was literally `location.href = action.url`.
    // After #804 it must be gated behind a URL-shape check.
    const bare = /case\s+['"]redirect['"][\s\S]{0,120}?if\s*\(\s*action\.url\s*\)\s*\{\s*location\.href\s*=\s*action\.url\s*;\s*\}/;
    expect(src).not.toMatch(bare);
  });

  test('redirect case references issue #804 in a comment', () => {
    expect(src).toMatch(/Issue #804/);
  });

  test("redirect case checks charAt for shape validation", () => {
    // The guard reads *.charAt(0) to classify relative vs absolute without
    // running new URL() (which would accept far too much). The code may
    // alias action.url to a local variable, so match charAt(0) generically.
    // Window widened from 600 to 900 chars in #534 to accommodate the
    // added backslash-bypass comment ahead of the guard.
    expect(src).toMatch(/case\s+['"]redirect['"][\s\S]{0,900}?\.charAt\s*\(\s*0\s*\)/);
  });

  test('redirect case references issue #534 in a comment (backslash bypass fix)', () => {
    expect(src).toMatch(/Issue #534/);
  });

  test('redirect case guard rejects a backslash as the second character', () => {
    // The old vulnerable check was `charAt(0) === '/' && charAt(1) !== '/'`,
    // which let "/\evil.com" through. The fixed guard uses a regex whose
    // character class excludes BOTH '/' and '\' as the second character.
    const fixedPattern = String.raw`/^\/(?:[^/\\]|$)/`;
    expect(src.indexOf(fixedPattern)).toBeGreaterThan(-1);
  });

  test('redirect case no longer contains the vulnerable charAt(1) !== \'/\' pattern', () => {
    const vulnerable = /charAt\s*\(\s*1\s*\)\s*!==\s*['"]\/['"]/;
    expect(src).not.toMatch(vulnerable);
  });
});

describe('#804 open-redirect guard — dispatcher semantic behavior', () => {
  // Re-implements the dispatcher contract against a pure-JS stub. Drift
  // between this stub and framework_layui.js is caught by the sweep above.

  function makeRedirectDispatcher(locationStub, consoleStub) {
    return function dispatchRedirect(url) {
      if (!url) { return; }
      // Issue #534: /^\/(?:[^/\\]|$)/ requires exactly one leading '/'
      // followed by end-of-string or a character that is neither '/'
      // (protocol-relative) nor '\' (backslash bypass — browsers normalize
      // it to '/' for special schemes, turning "/\evil.com" into
      // "//evil.com").
      if (/^\/(?:[^/\\]|$)/.test(url)) {
        locationStub.href = url;
        return;
      }
      if (url.charAt(0) === '#' || url.charAt(0) === '?') {
        locationStub.href = url;
        return;
      }
      if (consoleStub && consoleStub.warn) {
        consoleStub.warn('[WTM] Redirect blocked: non-relative URL', url);
      }
    };
  }

  const relativeAccept = [
    '/admin/home',
    '/api/v1/users',
    '/Home/Index',
    '/',
    '#top',
    '?page=2',
  ];

  const absoluteReject = [
    'https://evil.com/phish',
    'http://evil.com',
    '//evil.com',
    '//evil.com/path',
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'ftp://evil.com/',
    'evil.com',
    'www.example.com/path',
    // Issue #534: backslash-as-second-character bypass variants. Browsers
    // normalize a leading "\" run to "/" for special schemes, so these all
    // resolve to protocol-relative navigation to evil.com.
    String.raw`/\evil.com`,
    String.raw`/\\evil.com`,
    String.raw`\/evil.com`,
  ];

  test.each(relativeAccept)('accepts relative URL: %s', (url) => {
    const locationStub = { href: '' };
    const consoleStub = { warn: jest.fn() };
    const dispatch = makeRedirectDispatcher(locationStub, consoleStub);
    dispatch(url);
    expect(locationStub.href).toBe(url);
    expect(consoleStub.warn).not.toHaveBeenCalled();
  });

  test.each(absoluteReject)('rejects absolute URL: %s', (url) => {
    const locationStub = { href: '' };
    const consoleStub = { warn: jest.fn() };
    const dispatch = makeRedirectDispatcher(locationStub, consoleStub);
    dispatch(url);
    expect(locationStub.href).toBe('');
    expect(consoleStub.warn).toHaveBeenCalledWith(
      '[WTM] Redirect blocked: non-relative URL',
      url
    );
  });

  test('empty / null / undefined url is a silent no-op', () => {
    const locationStub = { href: '' };
    const consoleStub = { warn: jest.fn() };
    const dispatch = makeRedirectDispatcher(locationStub, consoleStub);
    dispatch('');
    dispatch(null);
    dispatch(undefined);
    expect(locationStub.href).toBe('');
    expect(consoleStub.warn).not.toHaveBeenCalled();
  });
});
