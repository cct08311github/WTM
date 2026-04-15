// Tests for issue #789 Phase 2: remaining non-FFResult eval() sites must be gone.
//
// Phase 2A (framework_layui.js global-variable access):
//   - eval(comboid + "defaultvalues") x4
//   - eval(tc[0].id + "selected") x1
//   - eval(id + "filter = obj;") x1 (assignment)
//
// Phase 2B (TagHelper server-templated eval): verified via grep of the generated
// C# source files. These files emit JavaScript at Razor render time; the eval
// wrappers are gone after Phase 2B, so the rendered output cannot contain them.

const fs = require('fs');
const path = require('path');

describe('#789 Phase 2A — framework_layui.js global-variable eval removal', () => {
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

  test('no combo defaultvalues eval survives', () => {
    const bad = /eval\s*\(\s*comboid\s*\+\s*["\']defaultvalues["\']\)/;
    expect(active).not.toMatch(bad);
  });

  test('no tc selected eval survives', () => {
    const bad = /eval\s*\(\s*tc\[0\]\.id\s*\+\s*["\']selected["\']\)/;
    expect(active).not.toMatch(bad);
  });

  test('no selector filter-assignment eval survives', () => {
    const bad = /eval\s*\(\s*id\s*\+\s*["\']filter\s*=/;
    expect(active).not.toMatch(bad);
  });

  test('new safe pattern is in place for combo/tc/selector globals', () => {
    expect(active).toMatch(/window\[\s*comboid\s*\+\s*["\']defaultvalues["\']\]/);
    expect(active).toMatch(/window\[\s*tc\[0\]\.id\s*\+\s*["\']selected["\']\]/);
    expect(active).toMatch(/window\[\s*id\s*\+\s*["\']filter["\']\]\s*=/);
  });

  test('total remaining eval( sites in framework_layui.js is exactly 3 (IsScript, Phase 2C scope)', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(3);
  });
});

describe('#789 Phase 2B — TagHelper server-templated eval removal', () => {
  const taghelperDir = path.resolve(
    __dirname,
    '../../../src/WalkingTec.Mvvm.TagHelpers.LayUI'
  );

  const readCs = (relative) =>
    fs.readFileSync(path.join(taghelperDir, relative), 'utf8');

  test('TreeTagHelper.cs no longer emits an eval(...) wrapper around ChangeFunc', () => {
    const src = readCs('TreeTagHelper.cs');
    // strip single-line C# comments
    const active = src
      .split('\n')
      .map((l) => (l.indexOf('//') === -1 ? l : l.slice(0, l.indexOf('//'))))
      .join('\n');
    const bad = new RegExp(String.raw`\beval\s*\(\s*""\s*\{\(`);
    expect(active).not.toMatch(bad);
  });

  test('ComboBoxTagHelper.cs no longer emits an eval(...) wrapper around ChangeFunc', () => {
    const src = readCs('Form/ComboBoxTagHelper.cs');
    const active = src
      .split('\n')
      .map((l) => (l.indexOf('//') === -1 ? l : l.slice(0, l.indexOf('//'))))
      .join('\n');
    const bad = new RegExp(String.raw`\beval\s*\(\s*""\s*\{\(`);
    expect(active).not.toMatch(bad);
  });

  test('SubmitButtonTagHelper.cs no longer emits an eval(...) wrapper around Click', () => {
    const src = readCs('Form/SubmitButtonTagHelper.cs');
    const active = src
      .split('\n')
      .map((l) => (l.indexOf('//') === -1 ? l : l.slice(0, l.indexOf('//'))))
      .join('\n');
    const bad = new RegExp(String.raw`var\s+check\s*=\s*eval\s*\(`);
    expect(active).not.toMatch(bad);
  });
});
