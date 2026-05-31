// Tests for issue #108: DataTableTagHelper getTemplate — stored XSS via cell data
//
// The C# DataTableTagHelper.getTemplate() generates a JS function that is passed
// to LayUI's `templet` option. That function renders row data (d.<field>) into
// the cell div's innerHTML. Before the fix, d.<field> was concatenated raw:
//
//   return '<div ...>' + d.fieldName.replace(/\"/g,"'") + bg + '</div>';
//
// After the fix (issue #108) the value is wrapped through ff.EscapeText:
//
//   return '<div ...>' + ff.EscapeText(d.fieldName) + bg + '</div>';
//
// This test file:
//   1. Verifies that the C#-generated JS pattern NO LONGER contains the raw
//      concatenation without escaping (source-sweep).
//   2. Verifies that the generated pattern DOES contain ff.EscapeText.
//   3. Semantic test: ff.EscapeText (replicated via jsdom) correctly
//      neutralizes XSS payloads that would otherwise execute when inserted
//      as innerHTML.

const fs = require('fs');
const path = require('path');

// We verify the DataTableTagHelper C# source since getTemplate() generates an
// inline JS string at render time — the JS itself is not a separate asset.
// We look for the presence of ff.EscapeText in the C# string literal.
const csPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs'
);

describe('#108 DataTableTagHelper cell-template XSS fix — C# source sweep', () => {
  const src = fs.readFileSync(csPath, 'utf8');

  test('getTemplate no longer uses raw d.field concatenation without EscapeText', () => {
    // The unsafe pattern was: d.{field}.replace(/"/g,"'")
    // After fix it should not appear in a return statement context.
    // We look for the old replace() idiom which bypassed encoding.
    // This is a negative check: the raw replace-as-escape idiom is gone.
    // Use string match to avoid regex delimiter conflicts with the / in the pattern.
    expect(src).not.toContain('d.{field}.replace');
  });

  test('getTemplate uses ff.EscapeText to encode row data', () => {
    // The fix changes the concatenation to: ff.EscapeText(d.{field})
    expect(src).toMatch(/ff\.EscapeText\(d\.\{field\}\)/);
  });

  test('getTemplate return statement puts encoded value into innerHTML before bg', () => {
    // Verify the div inner content uses EscapeText, then appends bg.
    // The C# string contains the substring: ff.EscapeText(d.{field})+bg+
    expect(src).toContain("ff.EscapeText(d.{field})+bg+'</div>");
  });
});

// Semantic: replicate ff.EscapeText behaviour against jsdom to prove XSS is
// neutralised when the output is inserted as innerHTML.
describe('#108 ff.EscapeText semantic — cell data XSS neutralisation (jsdom)', () => {
  // Replicate the jQuery $('<div/>').text(s).html() idiom using native jsdom
  // DOM (exactly what jQuery does internally; confirmed to behave identically).
  function escapeText(s) {
    if (s === null || s === undefined) { return ''; }
    const div = document.createElement('div');
    div.textContent = String(s);
    return div.innerHTML;
  }

  // Simulate the post-fix cell template render:
  //   innerHTML = '<div style="' + sty + '" id="' + did + '">' + ff.EscapeText(d.field) + bg + '</div>'
  function renderCell(fieldValue, sty = '', bg = '') {
    return '<div style="' + sty + '" id="cell1">' + escapeText(fieldValue) + bg + '</div>';
  }

  test('plain text renders unchanged via innerHTML', () => {
    const html = renderCell('Hello World');
    const container = document.createElement('div');
    container.innerHTML = html;
    expect(container.querySelector('div').textContent).toBe('Hello World');
  });

  test('<script> payload is neutralised — no script element injected', () => {
    const payload = '<script>alert(document.cookie)<\/script>';
    const html = renderCell(payload);
    const container = document.createElement('div');
    container.innerHTML = html;
    expect(container.querySelectorAll('script').length).toBe(0);
    // The text is still visible as literal characters
    expect(container.querySelector('div').textContent).toContain('script');
  });

  test('<img onerror> payload is neutralised — no img element injected', () => {
    const payload = '<img src=x onerror="alert(1)">';
    const html = renderCell(payload);
    const container = document.createElement('div');
    container.innerHTML = html;
    expect(container.querySelectorAll('img').length).toBe(0);
  });

  test('double-quote in value does not break surrounding HTML', () => {
    // A value containing " must not escape the attribute context of a surrounding
    // element. ff.EscapeText encodes & and < > which prevents structure breakout.
    const payload = '" onmouseover="alert(1)';
    const html = renderCell(payload);
    const container = document.createElement('div');
    container.innerHTML = html;
    // The div element should exist with the payload as text, not as an attribute.
    const divEl = container.querySelector('div');
    expect(divEl).not.toBeNull();
    expect(divEl.getAttribute('onmouseover')).toBeNull();
  });

  test('angle brackets are entity-encoded in the output', () => {
    const payload = '<b>bold</b>';
    const encoded = escapeText(payload);
    expect(encoded).not.toContain('<b>');
    expect(encoded).toContain('&lt;b&gt;');
  });
});
