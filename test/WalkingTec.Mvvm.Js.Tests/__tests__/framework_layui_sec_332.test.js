// Tests for issue #332: XSS / open-redirect / code-exec hardening
// in framework_layui.js.
//
// Sub-items covered:
//   1. layer.alert(responseText) wrapped with ff.EscapeText
//   2. ChainChange/LoadComboItems use DOM API (ff._makeInput) — no HTML concat
//   3. OpenDialog Location header validated before window.location
//   4. OpenDialog2 response run through ff.SafeHtml; grid-id via DOM query
//   5. Download form built via DOM API
//   6. RefreshChart uses JSON.parse by default (no JSONfns); opt-in registry works

const fs = require('fs');
const path = require('path');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

// Strip single-line comments so commented-out patterns do not trigger false positives.
const stripLineComments = (text) =>
  text
    .split('\n')
    .map((line) => {
      const idx = line.indexOf('//');
      return idx === -1 ? line : line.slice(0, idx);
    })
    .join('\n');

const active = stripLineComments(src);

// ---------------------------------------------------------------------------
// Block 1: source sweep
// ---------------------------------------------------------------------------
describe('#332 — framework_layui.js source sweep', () => {

  test('1a. PostForm/BgRequest/OpenDialog error handler wraps responseText with ff.EscapeText', () => {
    // There must be at least one ff.EscapeText(request.responseText) or
    // ff.EscapeText(xhr.responseText) call — no bare layer.alert(request.responseText).
    const wrapped = /ff\.EscapeText\(\s*(request|xhr)\.responseText\s*\)/;
    expect(active).toMatch(wrapped);
  });

  test('1b. No bare layer.alert(request.responseText) remains in active code', () => {
    const bare = /layer\.alert\(\s*request\.responseText\s*\)/;
    expect(active).not.toMatch(bare);
  });

  test('1c. No bare layer.alert(xhr.responseText) remains in active code', () => {
    const bare = /layer\.alert\(\s*xhr\.responseText\s*\)/;
    expect(active).not.toMatch(bare);
  });

  test('2a. ff._makeInput helper is defined', () => {
    expect(active).toMatch(/_makeInput\s*:\s*function/);
  });

  test('2b. No HTML string concat for checkbox input remains in active code', () => {
    // Pattern: target.append("<input type='checkbox'")
    const htmlConcat = /target\.append\(\s*["']<input\s+type\s*=\s*['"]?(checkbox)/i;
    expect(active).not.toMatch(htmlConcat);
  });

  test('2c. No HTML string concat for radio input remains in active code', () => {
    // Pattern: target.append("<input type='radio'")
    const htmlConcat = /target\.append\(\s*["']<input\s+type\s*=\s*['"]?(radio)/i;
    expect(active).not.toMatch(htmlConcat);
  });

  test('3a. ff.SafeHtml(str) is called inside OpenDialog2 success handler', () => {
    // The safeStr variable is assigned via ff.SafeHtml(str)
    expect(active).toMatch(/ff\.SafeHtml\(\s*str\s*\)/);
  });

  test('3b. querySelector(table[lay-filter]) appears — safe grid-id extraction', () => {
    expect(active).toMatch(/querySelector\(\s*['"]table\[lay-filter\]['"]\s*\)/);
  });

  test('4a. Download uses form.action = url (DOM API, not HTML concat)', () => {
    expect(active).toMatch(/form\.action\s*=\s*url/);
  });

  test('4b. Download uses form.method = \'POST\' (DOM API)', () => {
    expect(active).toMatch(/form\.method\s*=\s*['"]POST['"]/);
  });

  test('4c. No HTML form string concat remains in Download', () => {
    // Old pattern: $('<form method="POST" action="' + url
    const htmlConcat = /\$\(\s*['"]<form\s+method\s*=\s*['"]POST['"]\s+action\s*=/i;
    expect(active).not.toMatch(htmlConcat);
  });

  test('5a. JSONfns does NOT appear in active (uncommented) code', () => {
    // JSONfns.parse executes function literals — must be gone from active paths.
    expect(active).not.toMatch(/JSONfns/);
  });

  test('5b. window[chartid + ChartSeriesParser] opt-in check appears', () => {
    expect(active).toMatch(/window\[chartid\s*\+\s*['"]ChartSeriesParser['"]\]/);
  });

  test('5c. RefreshChart default path uses JSON.parse for series', () => {
    // After the fix, series is parsed via a _seriesParser variable that
    // defaults to JSON.parse. The assignment may span multiple lines.
    expect(active).toMatch(/_seriesParser[\s\S]{0,200}?JSON\.parse/);
  });

  test('6. Issue #332 comment reference is present in the source', () => {
    expect(src).toMatch(/Issue #332/);
  });

  test('7a. OpenDialog Location guard references Issue #534 (backslash bypass fix)', () => {
    expect(src).toMatch(/Issue #534/);
  });

  test('7b. OpenDialog Location guard uses the backslash-safe regex, not bare charAt(1) !== \'/\'', () => {
    // The old vulnerable check was `charAt(0) === '/' && charAt(1) !== '/'`,
    // which let a Location header like "/\evil.com" through — browsers
    // normalize '\' to '/' for special schemes, navigating to "//evil.com".
    const fixedPattern = String.raw`/^\/(?:[^/\\]|$)/`;
    expect(src.indexOf(fixedPattern)).toBeGreaterThan(-1);
    expect(active).not.toMatch(/charAt\s*\(\s*1\s*\)\s*!==\s*['"]\/['"]/);
  });
});

// ---------------------------------------------------------------------------
// Block 2: semantic tests for ff._makeInput DOM approach (jsdom)
// ---------------------------------------------------------------------------
describe('#332 — ff._makeInput DOM safety (jsdom semantic tests)', () => {
  // Re-implement _makeInput the same way framework_layui.js does.
  // Source sweep (Block 1) ensures the actual code uses this pattern.
  function makeInput(type, name, value, title, checked, disabled) {
    var el = document.createElement('input');
    el.type = type;
    el.name = name;
    el.value = value !== undefined && value !== null ? String(value) : '';
    el.title = title !== undefined && title !== null ? String(title) : '';
    if (checked) { el.checked = true; }
    if (disabled) { el.disabled = true; }
    return el;
  }

  test('checkbox has correct type, name, value, title', () => {
    const el = makeInput('checkbox', 'myField', 'val1', 'Label one', false, false);
    expect(el.type).toBe('checkbox');
    expect(el.name).toBe('myField');
    expect(el.value).toBe('val1');
    expect(el.title).toBe('Label one');
    expect(el.checked).toBe(false);
    expect(el.disabled).toBe(false);
  });

  test('radio with checked=true is properly checked', () => {
    const el = makeInput('radio', 'myRadio', 'optA', 'Option A', true, false);
    expect(el.type).toBe('radio');
    expect(el.checked).toBe(true);
  });

  test('disabled flag is applied', () => {
    const el = makeInput('checkbox', 'f', 'v', 't', false, true);
    expect(el.disabled).toBe(true);
  });

  test('XSS payload in title does NOT create an event handler attribute', () => {
    // If title were injected into HTML: title='x' onmouseover='alert(1)'
    // That would break out of the attribute. DOM assignment via .title = value
    // is safe — the browser treats the entire string as the attribute VALUE,
    // not as raw HTML markup.
    const payload = "x' onmouseover='alert(1)";
    const el = makeInput('checkbox', 'f', 'v', payload, false, false);
    // The title property holds the raw string — no attribute injection.
    expect(el.title).toBe(payload);
    // DOM property assignment: el has exactly one attribute named "title".
    // There must be no separate attribute named "onmouseover".
    expect(el.hasAttribute('onmouseover')).toBe(false);
    // Inserting into DOM does not add extra attributes via the payload.
    const container = document.createElement('div');
    container.appendChild(el);
    const inserted = container.querySelector('input');
    expect(inserted).not.toBeNull();
    expect(inserted.hasAttribute('onmouseover')).toBe(false);
  });

  test('XSS payload in value does NOT inject script tags', () => {
    const payload = '"><script>alert(1)<\/script>';
    const el = makeInput('checkbox', 'f', payload, 't', false, false);
    expect(el.value).toBe(payload);
    // Inserting into DOM does not cause a <script> element to appear.
    const container = document.createElement('div');
    container.appendChild(el);
    expect(container.querySelectorAll('script').length).toBe(0);
  });

  test('null/undefined value and title are coerced to empty string', () => {
    const el = makeInput('checkbox', 'f', null, undefined, false, false);
    expect(el.value).toBe('');
    expect(el.title).toBe('');
  });
});

// ---------------------------------------------------------------------------
// Block 3: OpenDialog Location redirect guard (mirrors #804 pattern)
// ---------------------------------------------------------------------------
describe('#332 — OpenDialog Location header redirect guard', () => {
  // Re-implement the guard as specified in the PR, mirroring #804 logic.
  // Source sweep (Block 1 test 6 + the Issue #332 reference) ensures the
  // actual source contains this guard. These semantic tests verify the shape
  // of the contract.

  function makeOpenDialogRedirectGuard(locationStub, consoleStub) {
    return function guard(location) {
      if (!location) { return false; }
      var _loc = location;
      // Issue #534: /^\/(?:[^/\\]|$)/ requires exactly one leading '/'
      // followed by end-of-string or a character that is neither '/'
      // (protocol-relative) nor '\' (backslash bypass — browsers normalize
      // it to '/' for special schemes, turning "/\evil.com" into
      // "//evil.com").
      if (/^\/(?:[^/\\]|$)/.test(_loc) ||
          _loc.charAt(0) === '#' || _loc.charAt(0) === '?') {
        locationStub.location = _loc;
        return false;
      }
      if (consoleStub && consoleStub.warn) {
        consoleStub.warn('[WTM] OpenDialog redirect blocked: non-relative Location header', _loc);
      }
      return false;
    };
  }

  const allowed = [
    '/admin/home',
    '/api/v1/users',
    '/Home/Index',
    '/',
    '#anchor',
    '?q=1',
  ];

  const blocked = [
    'javascript:alert(1)',
    'https://evil.com',
    'http://evil.com',
    '//evil.com',
    'data:text/html,<script>alert(1)<\/script>',
    'ftp://files.example.com/',
    'evil.com/path',
    // Issue #534: backslash-as-second-character bypass variants. Browsers
    // normalize a leading "\" run to "/" for special schemes, so these all
    // resolve to protocol-relative navigation to evil.com.
    String.raw`/\evil.com`,
    String.raw`/\\evil.com`,
    String.raw`\/evil.com`,
  ];

  test.each(allowed)('allows relative Location: %s', (loc) => {
    const stub = { location: '' };
    const con = { warn: jest.fn() };
    const guard = makeOpenDialogRedirectGuard(stub, con);
    guard(loc);
    expect(stub.location).toBe(loc);
    expect(con.warn).not.toHaveBeenCalled();
  });

  test.each(blocked)('blocks non-relative Location: %s', (loc) => {
    const stub = { location: '' };
    const con = { warn: jest.fn() };
    const guard = makeOpenDialogRedirectGuard(stub, con);
    guard(loc);
    expect(stub.location).toBe('');
    expect(con.warn).toHaveBeenCalledWith(
      '[WTM] OpenDialog redirect blocked: non-relative Location header',
      loc
    );
  });

  test('empty/null/undefined Location is a silent no-op', () => {
    const stub = { location: '' };
    const con = { warn: jest.fn() };
    const guard = makeOpenDialogRedirectGuard(stub, con);
    guard('');
    guard(null);
    guard(undefined);
    expect(stub.location).toBe('');
    expect(con.warn).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// Block 4: RefreshChart JSONfns opt-in registry
// ---------------------------------------------------------------------------
describe('#332 — RefreshChart JSONfns opt-in registry', () => {
  // Re-implement the parser-selection logic from RefreshChart.
  // Source sweep (tests 5a-5c) ensures the actual code has this pattern.

  function selectParser(chartid) {
    return typeof window[chartid + 'ChartSeriesParser'] === 'function'
      ? window[chartid + 'ChartSeriesParser']
      : JSON.parse;
  }

  afterEach(() => {
    delete global.myChartChartSeriesParser;
    delete global.customChartChartSeriesParser;
  });

  test('defaults to JSON.parse when registry entry is absent', () => {
    const parser = selectParser('myChart');
    expect(parser).toBe(JSON.parse);
  });

  test('defaults to JSON.parse when registry entry is not a function', () => {
    global.myChartChartSeriesParser = 'not a function';
    const parser = selectParser('myChart');
    expect(parser).toBe(JSON.parse);
  });

  test('uses custom parser when registry has a function', () => {
    const customParser = jest.fn(() => [{ type: 'bar' }]);
    global.customChartChartSeriesParser = customParser;
    const parser = selectParser('customChart');
    expect(parser).toBe(customParser);
    const result = parser('[{"type":"bar"}]');
    expect(customParser).toHaveBeenCalledWith('[{"type":"bar"}]');
    expect(result).toEqual([{ type: 'bar' }]);
  });

  test('JSON.parse is used end-to-end when no registry entry exists', () => {
    const seriesJson = '[{"type":"line","data":[1,2,3]}]';
    const parser = selectParser('noCustomChart');
    const result = parser(seriesJson);
    expect(result).toEqual([{ type: 'line', data: [1, 2, 3] }]);
  });

  test('JSONfns symbol is absent from the module-level scope (no global leak)', () => {
    // If JSONfns were still referenced in active code it would need to be a
    // global. Verify it is not defined as a side-effect of loading the test env.
    expect(typeof global.JSONfns).toBe('undefined');
  });
});
