# #565 — TagHelper &harr; LayUI browser regression suite

Phase 1 of the LayUI modernization roadmap (#567). This suite is the
**hard gate** that must stay green before any bundled-layui version bump
(Phase 2, #566): every assertion here renders the exact markup / init-call
shape a WTM TagHelper emits today, drives it through the real, unmodified
`src/WalkingTec.Mvvm.Mvc/framework_layui.js` and the real vendored layui
(currently 2.6.3, `demo/WalkingTec.Mvvm.Demo/wwwroot/layui/layui.js`), and
asserts — programmatically, not just visually — that the widget actually
initialized. A layui upgrade that breaks any of these assertions is a real
regression; this run is the baseline #566 will diff against.

No `src/` or `demo/` files are touched by this suite — test infrastructure
only.

## What each harness section proves

All 14 sections live in one combined harness page,
[`565-taghelper-layui-regression.html`](./565-taghelper-layui-regression.html),
in clearly separated `<section>`-style `<div class="box">` blocks:

| # | Widget | TagHelper | What's proven |
|---|--------|-----------|----------------|
| 1 | `form.render` | `FormTagHelper` | The `{"actions":[{"type":"initForm",...}]}` JSON island (eval-free path, #470/#561) gets auto-dispatched on `DOMContentLoaded` and `layui.form.render()` actually skins the form (a `lay-skin="switch"` checkbox grows its `.layui-form-switch` sibling). |
| 2 | `laydate` (single date) | `DateTimeTagHelper` | The bare `{"type":"laydate","opts":{...}}` island (#556) gets normalized + dispatched and `laydate.render()` stamps the input with a `lay-key` attribute. |
| 3 | `laydate` (date range) | `DateTimeTagHelper` | Same as above with `range:true` in `opts`. |
| 4 | `layer` / `OpenDialog` | (dialog-open flow) | `ff.OpenDialog()` — the real function — is invoked with `$.ajax` stubbed at the network boundary only (returns a canned same-origin partial). DOMParser island extraction, `ff.SafeHtml`/DOMPurify sanitization, and `layer.open()` all run for real; asserts `.layui-layer` appears. |
| 5 | `table`/grid | `DataTableTagHelper` | `table.render({elem, cols, data})` — the same local-`data` code path `DataTableTagHelper` itself uses when `UseLocalData=true` (no live backend needed) — asserts `.layui-table-view` is built next to the `<table>` element. |
| 6 | ComboBox | `ComboBoxTagHelper` | The div (`wtm-ctype="combo"`) + inline `xmSelect.render({el,...})` script it emits; asserts the `<xm-select>` custom element (xm-select.js's actual mount marker — not a `.xm-select` CSS class) is built. |
| 7 | Tree | `TreeTagHelper` | Same `xmSelect.render()` call but with `tree:{show:true}` + parent/child (`pid`) data, matching `TreeTagHelper`'s tree-mode xmSelect config; same `<xm-select>` assertion. |
| 8 | checkbox / radio + `ff.LoadComboItems` | `CheckBoxTagHelper` / `RadioTagHelper` | Reproduces `BaseFieldTag`'s real wrapper shape (`<div class="layui-form-item layui-form" lay-filter="{Id}filterdiv">`) so `ff.LoadComboItems`'s `layui.form.render(type, targetFilter+"div")` call actually targets the right container. `$.get` is stubbed at the network boundary; asserts every appended input gets skinned (`.layui-form-checkbox` / `.layui-form-radio`). |
| 9 | transfer | `TransferTagHelper` | `transfer.render({elem, title, data})`; asserts `.layui-transfer-box` is built. |
| 10 | upload (render only) | `UploadTagHelper` | `layui.upload.render({elem, url, auto:false})` — **no real file I/O attempted**, per #565 scope; asserts `upload.render()` returns a live instance (the widget mounted / click-handler bound). |
| 11 | slider | `SliderTagHelper` | `slider.render({elem, value, step})`; asserts `.layui-slider` is built. |
| 12 | rate | `RateTagHelper` | `rate.render({elem, value, length, choose})`; asserts `.layui-rate` is built. |
| 13 | colorPicker | `ColorPickerTagHelper` | `colorpicker.render({elem, color, format, done})`; asserts `.layui-colorpicker` is built. |
| 14 | tagInput | `TagInputTagHelper` | **Documented KNOWN GAP, not a suite bug.** `TagInputTagHelper`'s own doc comment says it targets "Layui 2.8+ tagInput widget" — no `tagInput` module file exists anywhere under the vendored layui 2.6.3 trees (`demo/**/wwwroot/layui/`), so `layui.use(['tagInput'], cb)` never resolves. Flagged `knownGap:true` so it is surfaced (not silently skipped) without blocking the gate. Once #566 bumps layui to a version that ships `tagInput`, this assertion should start passing and can be promoted out of `knownGap`. |

Every section reports one `{ name, pass, detail, knownGap? }` entry into
`window.__regressionResults`; `window.__regressionDone` flips to `true` once
all 14 have reported.

## How to run

### Headless (Playwright) — the automated gate

```bash
cd test/regression
npm install          # installs @playwright/test only — see "dependency footprint" below
npx playwright test
```

This boots a zero-dependency Node static file server
([`static-server.mjs`](../../regression/static-server.mjs)) rooted at the
repo root (`playwright.config.mjs`'s `webServer` block starts/stops it
automatically), navigates to the harness page, waits for
`window.__regressionDone === true`, and asserts every non-`knownGap` entry
has `pass === true`. `knownGap` entries (currently just `tagInput`) are
logged for visibility but do not fail the run.

### Manual — open it in a real browser

```bash
# from the repo root
python3 -m http.server 8000
open http://localhost:8000/test/manual/regression/565-taghelper-layui-regression.html
```

`file://` does not work — the harness's relative `<script src>` loads and
some browsers' same-origin rules require a real HTTP origin (same
requirement as `test/manual/561-formtaghelper-island.html` and
`test/manual/470b-laydate-island.html`). The page renders a live,
color-coded PASS/FAIL/GAP summary banner at the top (sticky, updates as each
section reports) so a human can eyeball the result without opening
DevTools — each entry also shows the exact detail message the headless
runner asserts on.

## Dependency footprint

This suite adds exactly one new dependency: **`@playwright/test`**
(`test/regression/package.json`, devDependency only, scoped to that
directory — it does not touch the solution's NuGet packages or the
top-level JS test setup under `test/WalkingTec.Mvvm.Js.Tests/`). Playwright's
Chromium browser binary was already present in this environment's local
cache (`~/Library/Caches/ms-playwright`, revision 1228) and matched the
installed `@playwright/test` version exactly, so no browser download was
required for this run — a clean environment would need
`npx playwright install chromium` (or the full `npx playwright install`) once
after `npm install`.

## Gate statement

**This suite is the Phase-2 (#566) gate.** Before bumping the bundled layui
version, run `npx playwright test` from `test/regression/` against the new
layui build (swap the vendored `demo/WalkingTec.Mvvm.Demo/wwwroot/layui/`
tree, or point the harness's relative paths at a build under test) and
confirm every non-`knownGap` assertion still passes. If the `tagInput`
`knownGap` entry starts passing under the new layui version, promote it out
of `knownGap` in the harness (drop the `true` argument to `pushResult`) as
part of that same PR — that is a real capability gain, not a false negative.
