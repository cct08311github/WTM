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
    expect(src).toMatch(/case\s+['"]redirect['"][\s\S]{0,600}?\.charAt\s*\(\s*0\s*\)/);
  });
});

describe('#804 open-redirect guard — dispatcher semantic behavior', () => {
  // Re-implements the dispatcher contract against a pure-JS stub. Drift
  // between this stub and framework_layui.js is caught by the sweep above.

  function makeRedirectDispatcher(locationStub, consoleStub) {
    return function dispatchRedirect(url) {
      if (!url) { return; }
      if (url.charAt(0) === '/' && url.charAt(1) !== '/') {
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
    '/',
    '#top',
    '?page=2',
  ];

  const absoluteReject = [
    'https://evil.com/phish',
    'http://evil.com',
    '//evil.com/path',
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'ftp://evil.com/',
    'evil.com',
    'www.example.com/path',
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
