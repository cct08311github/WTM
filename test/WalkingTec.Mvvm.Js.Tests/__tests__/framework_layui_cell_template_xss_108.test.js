// Tests for issue #108: DataTableTagHelper getTemplate — stored XSS via cell data
//
// The C# DataTableTagHelper.getTemplate() generates a JS function that is passed
// to LayUI's `templet` option. That function renders row data (d.<field>) into
// the cell div's innerHTML. Before the fix, d.<field> was concatenated raw:
//
//   return '<div ...>' + d.fieldName.replace(/\"/g,"'") + bg + '</div>';
//
// After the fix (issue #108) user/database values are wrapped through ff.EscapeText:
//
//   return '<div ...>' + ff.EscapeText(d.fieldName) + bg + '</div>';
//
// After the grid-001 fix (issue #195) getTemplate accepts a hasFormat bool:
//   - hasFormat=false (plain data column): uses ff.EscapeText — #108 XSS guard preserved.
//   - hasFormat=true  (format/button column): renders raw HTML produced by framework Make*
//     methods (which themselves HtmlEncode user-supplied text per TLU-SEC-004), so
//     ff.EscapeText is NOT applied here (applying it would double-encode and break buttons).
//
// The C# implements this with a local variable:
//   var cellExpr = hasFormat ? $"d.{field}" : $"ff.EscapeText(d.{field})";
//   return $"...'+{cellExpr}+bg+'</div>'...";
//
// This test file:
//   1. Verifies that the C#-generated JS pattern NO LONGER contains the raw
//      concatenation without escaping (source-sweep).
//   2. Verifies that the non-format (user data) path STILL uses ff.EscapeText —
//      the #108 stored-XSS guard is preserved.
//   3. Verifies that the format path uses the raw field expression (no EscapeText),
//      and confirms that the framework Make* methods produce HtmlEncoded output
//      (so user-supplied text inside button labels cannot inject script).
//   4. Semantic test: ff.EscapeText (replicated via jsdom) correctly
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

  test('getTemplate uses ff.EscapeText to encode row data (#108 guard preserved)', () => {
    // The non-format path still uses ff.EscapeText: $"ff.EscapeText(d.{field})"
    // This is the C# string literal that becomes the JS expression for plain
    // data columns — the core #108 XSS guard must never be removed.
    expect(src).toMatch(/ff\.EscapeText\(d\.\{field\}\)/);
  });

  test('non-format (user data) path uses ff.EscapeText as cellExpr — #108 guard intact', () => {
    // grid-001 fix (issue #195): getTemplate now uses a cellExpr local variable.
    // For hasFormat=false the C# assigns: $"ff.EscapeText(d.{field})"
    // Verify that string literal is present in the ternary's false branch,
    // proving the non-format branch still routes user cell data through ff.EscapeText.
    //
    // The C# reads: var cellExpr = hasFormat ? $"d.{field}" : $"ff.EscapeText(d.{field})";
    // The template then interpolates {cellExpr} into the JS return string.
    expect(src).toContain('ff.EscapeText(d.{field})');
    // And the template return uses {cellExpr} followed by +bg+'</div>' ensuring
    // the cell expression (escape or raw) feeds directly into the innerHTML concat.
    expect(src).toContain("{cellExpr}+bg+'</div>'");
  });

  test('format (button/HTML) path uses raw field expression — no EscapeText applied', () => {
    // grid-001 fix: for hasFormat=true the C# assigns: $"d.{field}"
    // The raw field value contains framework-generated HTML (Make* output) and must
    // NOT be escaped; verify the conditional assignment is present in source.
    //
    // The C# ternary is written as:
    //   var cellExpr = hasFormat
    //       ? $"d.{field}"
    //       : $"ff.EscapeText(d.{field})";
    //
    // Assert each required piece individually (the ternary spans lines with CRLF):
    expect(src).toContain('var cellExpr = hasFormat');
    // hasFormat=true branch: bare field reference (no EscapeText)
    expect(src).toContain('? $"d.{field}"');
    // hasFormat=false branch: wrapped in EscapeText
    expect(src).toContain(': $"ff.EscapeText(d.{field})"');
  });

  test('framework Make* methods HtmlEncode user-supplied button text (TLU-SEC-004)', () => {
    // For the hasFormat=true path to be safe, the framework HTML must itself encode
    // any user-supplied text embedded in buttons/links. Verify that WebUtility.HtmlEncode
    // is applied to button name/text in the row-button builder — this is TLU-SEC-004.
    // If this assertion fails it means user data can reach raw HTML through format columns.
    expect(src).toMatch(/WebUtility\.HtmlEncode\(item\.Name\)/);
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

  // Simulate the post-fix cell template render for a non-format (user data) column:
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
