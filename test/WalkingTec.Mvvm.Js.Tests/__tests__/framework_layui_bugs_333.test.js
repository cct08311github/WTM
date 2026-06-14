// Regression tests for Issue #333 — JS defects in framework_layui.js
//
// Bug 10: clearSelector used literal "id" string instead of id parameter variable
// Bug 11: GetNonSelections referenced undeclared invalidNum (ReferenceError in strict mode)
//
// This file uses the source-sweep approach: reads the JS file as text
// and verifies the fix is in place, mirroring the pattern used in
// framework_layui_sec_332.test.js.

const fs = require('fs');
const path = require('path');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

// Strip single-line comments to avoid false positives from commented-out code.
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
// Bug 10: clearSelector — must use `id` variable, not literal "id" string
// ---------------------------------------------------------------------------
describe('#333 Bug 10 — clearSelector uses id variable not literal', () => {
  test('clearSelector does NOT contain the literal string "id" as the container prefix', () => {
    // The buggy pattern: '#' + "id" + '_Container'
    // After the fix it must be: '#' + id + '_Container'
    const buggyPattern = /'#'\s*\+\s*"id"\s*\+\s*'_Container/;
    expect(active).not.toMatch(buggyPattern);
  });

  test('clearSelector uses the id parameter variable for the container selector', () => {
    // The fixed pattern: '#' + id + '_Container  (id as a bare variable, not a quoted string)
    // We look for the clearSelector function and check it references id + '_Container
    const fixedPattern = /clearSelector\s*:\s*function\s*\(id\)[\s\S]*?'#'\s*\+\s*id\s*\+\s*'_Container/;
    expect(active).toMatch(fixedPattern);
  });
});

// ---------------------------------------------------------------------------
// Bug 11: GetNonSelections — must NOT reference undeclared `invalidNum`
// ---------------------------------------------------------------------------
describe('#333 Bug 11 — GetNonSelections has no stray invalidNum reference', () => {
  test('GetNonSelections function body does not reference invalidNum', () => {
    // Extract the GetNonSelections function body (stop before next function).
    // We look for `GetNonSelections` and capture everything until the next top-level key.
    const fnMatch = active.match(/GetNonSelections\s*:\s*function[\s\S]*?(?=,\s*\n\s*\w+\s*:|$)/);
    expect(fnMatch).not.toBeNull();

    const fnBody = fnMatch[0];
    // After the fix, invalidNum should NOT appear in GetNonSelections
    expect(fnBody).not.toMatch(/invalidNum/);
  });

  test('GetNonSelections uses nums counter for accumulation (not invalidNum)', () => {
    // Verify the fix used the correct local variable name
    const fnMatch = active.match(/GetNonSelections\s*:\s*function[\s\S]*?(?=,\s*\n\s*\w+\s*:|$)/);
    expect(fnMatch).not.toBeNull();
    const fnBody = fnMatch[0];
    expect(fnBody).toMatch(/nums\+\+/);
  });

  test('GetNonSelectionData function (with declared invalidNum) is still present and valid', () => {
    // This sibling function correctly declares and uses invalidNum — must be untouched.
    // Note: the variable is declared via comma continuation (e.g., "var table = ...\n, invalidNum = 0")
    // so we match the reference form actually present in the source.
    const fnMatch = active.match(/GetNonSelectionData\s*:\s*function[\s\S]*?(?=,\s*\n\s*\w+\s*:|$)/);
    expect(fnMatch).not.toBeNull();

    const fnBody = fnMatch[0];
    // invalidNum is declared and used here — should still be present
    expect(fnBody).toMatch(/invalidNum/);
    expect(fnBody).toMatch(/invalidNum\+\+/);
  });
});

// ---------------------------------------------------------------------------
// Runtime smoke test via the global ff object loaded in setup.js
// ---------------------------------------------------------------------------
describe('#333 runtime smoke — ff functions exist', () => {
  test('ff.clearSelector is defined', () => {
    expect(typeof ff.clearSelector).toBe('function');
  });

  test('ff.GetNonSelections is defined', () => {
    expect(typeof ff.GetNonSelections).toBe('function');
  });
});
