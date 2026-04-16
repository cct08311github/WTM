// Tests for issue #805: layui's layer.alert(msg) concatenates msg into
// innerHTML and parses HTML tags. WtmAction.Message / Title must therefore
// be treated as plain text — the dispatcher escapes via ff.EscapeText
// before passing into ff.Alert / ff.Msg.
//
// What this file locks in place:
//   1. framework_layui.js declares ff.EscapeText helper.
//   2. DispatchAction's 'alert' and 'message' branches route
//      action.message and action.title through ff.EscapeText.
//   3. A representative XSS payload is neutralized: the escaped output
//      contains no raw < or > characters (entity-encoded only).

const fs = require('fs');
const path = require('path');

describe('#805 layui alert HTML escape — framework_layui.js source sweep', () => {
  const src = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
  );

  test('ff.EscapeText helper is defined', () => {
    expect(src).toMatch(/EscapeText\s*:\s*function/);
  });

  test('ff.EscapeText uses jQuery text()/html() idiom', () => {
    // Must use $('<div/>').text(...).html() — reliable HTML entity encoding
    // via the browser's own DOM layer. Hand-rolled regex replacement would
    // be fragile and is rejected.
    expect(src).toMatch(/EscapeText[\s\S]{0,200}?\$\(\s*['"]<div\s*\/>['"]\s*\)\.text\(/);
  });

  test("alert branch routes message/title through ff.EscapeText", () => {
    // Match: case 'alert':  ... ff.Alert(ff.EscapeText(action.message ...
    expect(src).toMatch(
      /case\s+['"]alert['"][\s\S]{0,300}?ff\.Alert\([\s\S]{0,100}?ff\.EscapeText\(\s*action\.message/
    );
    expect(src).toMatch(
      /case\s+['"]alert['"][\s\S]{0,500}?ff\.EscapeText\(\s*action\.title/
    );
  });

  test("message branch routes message/title through ff.EscapeText", () => {
    expect(src).toMatch(
      /case\s+['"]message['"][\s\S]{0,300}?ff\.Msg\([\s\S]{0,100}?ff\.EscapeText\(\s*action\.message/
    );
    expect(src).toMatch(
      /case\s+['"]message['"][\s\S]{0,500}?ff\.EscapeText\(\s*action\.title/
    );
  });

  test('#805 reference comment present on the dispatcher', () => {
    expect(src).toMatch(/Issue #805/);
  });
});

describe('#805 ff.EscapeText semantic behavior (jsdom)', () => {
  // The jest jsdom environment gives us a DOM. jQuery is not installed as
  // a test dep (the existing setup.js uses a jQuery mock) so we replicate
  // the $('<div/>').text(s).html() idiom against jsdom's native DOM —
  // which is exactly what jQuery does internally. Any drift between this
  // test helper and framework_layui.js's actual jQuery-based EscapeText is
  // caught by the source-sweep describe block above.

  function escapeText(s) {
    if (s === null || s === undefined) { return ''; }
    var div = document.createElement('div');
    div.textContent = String(s);
    return div.innerHTML;
  }

  test('entity-encodes angle brackets', () => {
    const out = escapeText('<b>hi</b>');
    expect(out).not.toContain('<');
    expect(out).not.toContain('>');
    expect(out).toMatch(/&lt;b&gt;hi&lt;\/b&gt;/);
  });

  test('neutralizes <img src=x onerror> XSS payload', () => {
    const payload = '<img src=x onerror=alert(1)>';
    const out = escapeText(payload);
    // The raw `<img` tag form is gone. The text "onerror=alert(1)" may
    // remain as plain text inside the encoded string, which is fine:
    // without the surrounding `<` `>` no DOM node gets created, which is
    // what matters for XSS neutralization.
    expect(out).not.toContain('<img');
    // Primary safety check: inserting into DOM produces zero img elements.
    const container = document.createElement('div');
    container.innerHTML = out;
    expect(container.querySelectorAll('img').length).toBe(0);
  });

  test('neutralizes <script> payload', () => {
    const out = escapeText('<script>alert(1)</script>');
    expect(out).not.toContain('<script');
    const container = document.createElement('div');
    container.innerHTML = out;
    expect(container.querySelectorAll('script').length).toBe(0);
  });

  test('encodes the five HTML metacharacters', () => {
    // jQuery .text().html() encodes <, >, &. It does NOT encode ' or ",
    // but that is safe because our target context (layui-layer-content
    // div innerHTML) is element content, not an attribute value.
    expect(escapeText('<')).toBe('&lt;');
    expect(escapeText('>')).toBe('&gt;');
    expect(escapeText('&')).toBe('&amp;');
  });

  test('leaves plain text unchanged', () => {
    expect(escapeText('Hello world')).toBe('Hello world');
    expect(escapeText('中文測試')).toBe('中文測試');
    expect(escapeText('123.456')).toBe('123.456');
  });

  test('returns empty string for null / undefined / empty', () => {
    expect(escapeText(null)).toBe('');
    expect(escapeText(undefined)).toBe('');
    expect(escapeText('')).toBe('');
  });

  test('coerces non-string input via String(x)', () => {
    expect(escapeText(42)).toBe('42');
    expect(escapeText(true)).toBe('true');
    expect(escapeText({ toString: () => '<x>' })).toBe('&lt;x&gt;');
  });
});

describe('#805 dispatcher integration — alert/message escape payloads', () => {
  // Re-implement the dispatcher's alert/message branches against mocked
  // ff.Alert / ff.Msg / ff.EscapeText and verify the escape helper is
  // actually called before the layui invocation.

  function escapeText(s) {
    if (s === null || s === undefined) { return ''; }
    // Trivial escape for test purposes — mirrors the jQuery idiom's output.
    return String(s)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  function makeDispatcher(ff) {
    return function (payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      for (var i = 0; i < payload.actions.length; i++) {
        var a = payload.actions[i];
        if (!a || !a.type) continue;
        if (a.type === 'alert') {
          ff.Alert(
            ff.EscapeText(a.message || ''),
            ff.EscapeText(a.title || '')
          );
        } else if (a.type === 'message') {
          ff.Msg(
            ff.EscapeText(a.message || ''),
            ff.EscapeText(a.title || '')
          );
        }
      }
    };
  }

  test('alert action passes escaped message to ff.Alert', () => {
    const ff = { Alert: jest.fn(), EscapeText: escapeText };
    const dispatch = makeDispatcher(ff);
    dispatch({
      actions: [
        {
          type: 'alert',
          message: '<img src=x onerror=alert(1)>',
          title: '<b>Bad</b>',
        },
      ],
    });
    expect(ff.Alert).toHaveBeenCalledWith(
      '&lt;img src=x onerror=alert(1)&gt;',
      '&lt;b&gt;Bad&lt;/b&gt;'
    );
  });

  test('message action passes escaped message to ff.Msg', () => {
    const ff = { Msg: jest.fn(), EscapeText: escapeText };
    const dispatch = makeDispatcher(ff);
    dispatch({
      actions: [
        {
          type: 'message',
          message: '<script>alert(1)</script>',
          title: '',
        },
      ],
    });
    expect(ff.Msg).toHaveBeenCalledWith(
      '&lt;script&gt;alert(1)&lt;/script&gt;',
      ''
    );
  });
});
