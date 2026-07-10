// Issue #651 (island-JSON sentinel class — completeness guard): a source-sweep
// that FAILS if any LayUI TagHelper serializes model-derived data into a
// wtm-dialog-init island (or with the island options objects) WITHOUT routing
// it through LayuiIslandJson.Serialize — the '$'-escaping wrapper that makes the
// serialized JSON collision-free against SelectorTagHelper's
// $$dialoginit$$/$$#dialoginit$$/$$script$$/$$#script$$ tokenization (see
// framework_layui_651_island_sentinel_escape.test.js for the behavioural proof).
//
// Why a guard: #635 made ff.OpenDialog2 dispatch EVERY wtm-dialog-init island on
// the selector-panel path, so any island emitter is exposed to the sentinel
// collision when used as a selector searcher with model-derived data. A future
// island emitter that calls JsonSerializer.Serialize(action, _islandJsonOptions)
// directly would silently reintroduce the stored-XSS. This sweep makes that a
// build-breaking test, modelled on the existing framework_layui.js eval(-count
// source sweeps and the #646 CheckBox/Radio C#-source sweep.
//
// The escape itself is proven correct once, in LayuiIslandJson (default-encoder
// options that already escape <>&'" + a blanket .Replace("$","\\u0024")); the
// guard's job is only to prove NOTHING bypasses it.

'use strict';

const fs = require('fs');
const path = require('path');

const LAYUI_SRC_DIR = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.TagHelpers.LayUI'
);

// Recursively collect every .cs file under the TagHelper project, skipping
// build output (bin/obj) so we only sweep real source.
function collectCsFiles(dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'bin' || entry.name === 'obj') continue;
      out.push(...collectCsFiles(path.join(dir, entry.name)));
    } else if (entry.isFile() && entry.name.endsWith('.cs')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

// Strip // line comments (not string-aware, but sufficient here: the patterns
// we assert on never legitimately appear inside a string literal in these
// files, and stripping comments prevents the many explanatory comments that
// mention "JsonSerializer.Serialize" / "_islandJsonOptions" from tripping the
// sweep) — same technique as the framework_layui.js eval(-sweep tests.
function stripLineComments(text) {
  return text
    .split('\n')
    .map((line) => {
      const idx = line.indexOf('//');
      return idx === -1 ? line : line.slice(0, idx);
    })
    .join('\n');
}

const csFiles = collectCsFiles(LAYUI_SRC_DIR);
const sources = csFiles.map((file) => ({
  file,
  rel: path.relative(LAYUI_SRC_DIR, file),
  active: stripLineComments(fs.readFileSync(file, 'utf8')),
}));

describe('#651 island-serialize completeness guard — every island/attribute serialization routes through LayuiIslandJson', () => {
  test('sanity: the sweep actually found the LayUI TagHelper sources', () => {
    expect(sources.length).toBeGreaterThan(20);
    // The helper must exist and carry the '$'-escape on BOTH overloads.
    const combo = sources.find((s) => s.rel.endsWith('ComboBoxTagHelper.cs'));
    expect(combo).toBeDefined();
    expect(combo.active).toMatch(/internal static class LayuiIslandJson/);
    const escapes = combo.active.match(/\.Replace\("\$",\s*"\\\\u0024"\)/g) || [];
    expect(escapes.length).toBe(2); // options overload + default overload
  });

  // Guard 1 — the exact coordinator spec: NO raw JsonSerializer.Serialize(...)
  // may pass _islandJsonOptions or _laydateJsonOptions. Those island options
  // objects may ONLY be handed to LayuiIslandJson.Serialize(...).
  test('Guard 1: no raw JsonSerializer.Serialize(...) passes _islandJsonOptions / _laydateJsonOptions', () => {
    const offenders = [];
    // Match a JsonSerializer.Serialize( ... _islandJsonOptions | _laydateJsonOptions )
    // that is NOT LayuiIslandJson.Serialize (the negative lookbehind excludes the helper).
    const re = /(?<!LayuiIsland)JsonSerializer\.Serialize\s*\([^;]*?_(?:island|laydate)JsonOptions/g;
    for (const s of sources) {
      if (re.test(s.active)) offenders.push(s.rel);
      re.lastIndex = 0;
    }
    expect(offenders).toEqual([]);
  });

  // Guard 2 — future-proof beyond the named options: in every file that EMITS a
  // wtm-dialog-init island, the only raw JsonSerializer.Serialize( calls allowed
  // are LayuiIslandJson's own two helper overloads (whose sole argument is the
  // generic `value`). Any other raw serialize in an island-emitting file is an
  // un-routed island payload — exactly the reintroduction this guard blocks
  // (e.g. DialogInitTagHelper feeds its island via _jsonOptions, a name Guard 1
  // wouldn't catch; this guard does).
  test('Guard 2: every wtm-dialog-init-emitting file routes all serialization through LayuiIslandJson (only the helper keeps a raw JsonSerializer.Serialize)', () => {
    const offenders = [];
    for (const s of sources) {
      if (!s.active.includes('wtm-dialog-init')) continue;
      // Find every raw JsonSerializer.Serialize( occurrence (not LayuiIslandJson.Serialize).
      const rawCalls = s.active.match(/(?<!LayuiIsland)JsonSerializer\.Serialize\s*\(\s*[A-Za-z_][A-Za-z0-9_]*/g) || [];
      for (const call of rawCalls) {
        // The LayuiIslandJson helper's two overloads are the ONLY permitted raw
        // calls, and both take the generic parameter named `value`.
        if (/JsonSerializer\.Serialize\s*\(\s*value$/.test(call)) continue;
        offenders.push(`${s.rel}: ${call.trim()}`);
      }
    }
    expect(offenders).toEqual([]);
  });

  // Guard 3 — the CATCH-ALL regression net (closes the Guards 1/2 blind spot):
  // Guard 1 keys on the island OPTIONS identifiers and Guard 2 only inspects
  // wtm-dialog-init-emitting files, so NEITHER covers the DEFAULT-overload,
  // non-island inline emitters this fix also routes — TreeTagHelper,
  // TreeContainerTagHelper, MultiUploadTagHelper (and the Selector nested-filter
  // / DateTime mark: sites). A revert of any of those to a raw
  // JsonSerializer.Serialize(...) uses default options and emits no
  // wtm-dialog-init literal, so it would slip past 1 and 2. This guard sweeps
  // EVERY LayUI TagHelper source and requires that every raw
  // JsonSerializer.Serialize( call is EITHER the LayuiIslandJson helper's own
  // overloads OR on this explicit, documented allowlist. Anything else — a
  // Tree/TreeContainer/MultiUpload revert, or a brand-new file — fails here.
  //
  // Note: `JsonSerializer.Serialize` and `LayuiIslandJson.Serialize` are
  // distinct strings (the latter is `...IslandJson.Serialize`, never
  // `JsonSerializer.Serialize`), so a routed call never matches the sweep
  // below — only genuinely-raw System.Text.Json calls do.
  const RAW_SERIALIZE_ALLOWLIST = [
    // The LayuiIslandJson helper's own two overloads (the $-escape wrapper
    // itself) — both take the generic parameter named `value`.
    {
      rel: 'Form/ComboBoxTagHelper.cs',
      arg: 'value',
      reason: 'LayuiIslandJson.Serialize overloads — the $-escaping wrapper itself',
    },
    // DataTableTagHelper renders into the RESULTS GRID, which is built from the
    // SERVER RESPONSE (ff.SafeHtml/DOMPurify-sanitised) and spliced into a slot
    // that is NOT the tokenized #Temp{Id} searcher template — so it is never
    // subject to the $$…$$ sentinel rehydration. Its `cols` serialize also
    // relies on custom "_raw_" marker post-processing that $-escaping would
    // corrupt. Intentionally NOT routed; documented here.
    {
      rel: 'DataTableTagHelper.cs',
      arg: 'where',
      reason: 'grid where-clause — server-response results grid, not the tokenized #Temp{Id} template',
    },
    {
      rel: 'DataTableTagHelper.cs',
      arg: 'layuiCols',
      reason: 'grid columns — server-response grid + custom "_raw_" marker logic that $-escaping would corrupt',
    },
    {
      rel: 'DataTableTagHelper.cs',
      arg: 'item.whereStr',
      reason: 'grid toolbar where-string — server-response grid, not the tokenized template',
    },
  ];

  const normRel = (p) => p.replace(/\\/g, '/');
  const relMatches = (fileRel, allowRel) =>
    normRel(fileRel) === allowRel || normRel(fileRel).endsWith('/' + allowRel);

  // Every raw call's captured first-argument token, across all sources.
  function rawSerializeCalls() {
    const calls = [];
    const re = /JsonSerializer\.Serialize\s*\(\s*([^,)\s]+)/g;
    for (const s of sources) {
      let m;
      re.lastIndex = 0;
      while ((m = re.exec(s.active)) !== null) {
        calls.push({ rel: s.rel, arg: m[1] });
      }
    }
    return calls;
  }

  test('Guard 3 (catch-all): every raw JsonSerializer.Serialize outside LayuiIslandJson is on the documented allowlist', () => {
    const offenders = [];
    for (const call of rawSerializeCalls()) {
      const allowed = RAW_SERIALIZE_ALLOWLIST.some(
        (e) => relMatches(call.rel, e.rel) && e.arg === call.arg
      );
      if (!allowed) {
        offenders.push(`${normRel(call.rel)}: JsonSerializer.Serialize(${call.arg}`);
      }
    }
    // A non-empty list means a serialization that can reach the tokenized
    // selector template is NOT $-escaped (e.g. a Tree/TreeContainer/MultiUpload
    // revert) — a reintroduced #651 stored-XSS. Route it through
    // LayuiIslandJson.Serialize, or (if provably out of the tokenized template)
    // add it to RAW_SERIALIZE_ALLOWLIST above with a documented reason.
    expect(offenders).toEqual([]);
  });

  test('Guard 3 hygiene: the allowlist has no stale entries (every entry matches a real raw call)', () => {
    const calls = rawSerializeCalls();
    const stale = RAW_SERIALIZE_ALLOWLIST.filter(
      (e) => !calls.some((c) => relMatches(c.rel, e.rel) && c.arg === e.arg)
    ).map((e) => `${e.rel}: JsonSerializer.Serialize(${e.arg}  [reason: ${e.reason}]`);
    // A stale entry means the underlying call was routed/removed but the
    // allowlist exemption lingers — prune it so the allowlist stays an accurate
    // ledger of what is deliberately NOT $-escaped.
    expect(stale).toEqual([]);
  });

  // Positive coverage: each known island emitter must actually reference
  // LayuiIslandJson.Serialize — proves the sweep sees them and that routing is
  // present, not merely that raw calls are absent (a file could have neither).
  test('every known island emitter references LayuiIslandJson.Serialize', () => {
    const emitters = [
      'Form/ComboBoxTagHelper.cs',
      'Form/CheckBoxTagHelper.cs',
      'Form/RadioTagHelper.cs',
      'Form/TransferTagHelper.cs',
      'Form/TagInputTagHelper.cs',
      'Form/TextBoxTagHelper.cs',
      'Form/SliderTagHelper.cs',
      'Form/RateTagHelper.cs',
      'Form/ColorPicker.cs',
      'Form/FormTagHelper.cs',
      'Form/DateTimeTagHelper.cs',
      'DialogInitTagHelper.cs',
    ];
    const missing = [];
    for (const rel of emitters) {
      const s = sources.find((x) => x.rel === rel || x.rel.endsWith('/' + rel) || x.rel.endsWith('\\' + rel));
      if (!s) {
        missing.push(`${rel} (file not found by sweep)`);
        continue;
      }
      if (!s.active.includes('LayuiIslandJson.Serialize')) {
        missing.push(`${rel} (no LayuiIslandJson.Serialize reference)`);
      }
    }
    expect(missing).toEqual([]);
  });
});
