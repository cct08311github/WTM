# CSP Hardening Guide — the Dialog-Flow Strict-CSP Recipe

> Added in 10.14.3 (#627, a slice of the #470 epic). Rewritten for #470 Slice Q
> (2026-07, the G→O island campaign's deprecation & docs endgame — design authority:
> Gitea issue #470 comment 16357 "Completion plan: Slice G → Q") once Slices
> G/H/I/J/K/L/M/N1/N2/O1/O2/O3 had all shipped (v10.16.0 + follow-on). Explains how far
> a WTM app can tighten `Content-Security-Policy` today, exactly which knobs unlock each
> level, and — just as important — which framework configurations are **still** not
> eligible for the strictest levels, and why.

## Recommended target: Level 2 or Level 3

As of the #470 G→O campaign, the **entire form-widget + grid + dialog-init family**
supports eval-free, island-driven rendering as an opt-in. For any app whose dialogs
don't hit one of the honest residual blockers in the table below, **Level 2 (kill-switch
flipped) or Level 3 (strict `script-src`) is now the recommended target**, not an
aspirational one. Two switches get you there:

```csharp
// Program.cs / Startup — opt in to island rendering for the whole form/grid/dialog family
services.Configure<WtmUIOptions>(o => o.UseSelectIslandRender = true);
```

```html
<!-- _Layout.cshtml <head> — opt in to the #627 kill-switch -->
<meta name="wtm-disable-legacy-script-rehydration" content="true">
```

Both default OFF; both are additive opt-ins with zero effect on apps that don't set
them. Read the rest of this guide before flipping either in production — Level 1's audit
step still matters, and the residual-blocker table tells you up front whether your
dialogs contain something that will fall back to (safely warned, never silently broken)
legacy rendering.

## Background: what is and is not eval-free today

The `framework_layui.js` eval-removal effort (originally tracked as #789 on this repo's
pre-Gitea-cutover GitHub tracker, now defunct, phases 1–3D; continued locally as
#470/#552/#556/#558/#561/#564/#576/#587, then the #470 Slice G→O campaign) stands at:

- **The common form-init paths are JSON-island-driven unconditionally** (v10.13.12+,
  predates and is independent of `UseSelectIslandRender`): `<wt:form>`'s AJAX submit
  binding (`bindSubmit`), the auto-validate handshake (`bindValidate`), and
  ModelState error highlighting (`highlightErrors`) always travel as
  `<script type="application/json">` islands consumed by the `ff.DispatchAction`
  whitelist — `JSON.parse`, never code execution. laydate (non-range, callback-free),
  rate, and taginput likewise predate the flag and are always island-driven.
- **Exactly one `eval(` remains** in `framework_layui.js`: `ff._legacyScriptEval` (line
  ~3663), the back-compat fallback for the deprecated `IsScript` response-header
  protocol. **Deprecated** — see "Deprecating the legacy paths" below.
- **Four dynamic execution points remain, all gated by the #627 kill-switch**
  (`ff._isLegacyRehydrationDisabled()`, exactly 4 call sites verified by grep):
  `ff._legacyScriptEval` itself, `ff.OpenDialog`'s inline-`<script>` rehydration (#522),
  `ff._replayInitFromHtml`'s SPA-fragment/PostForm replay (#587), and
  `ff.OpenDialog2`'s Selector search-panel `$$script$$` template rehydration (#332).
- **The entire form-widget + grid + dialog-init family is now island-capable behind
  `WtmUIOptions.UseSelectIslandRender=true`** (default **OFF** — byte-identical to
  pre-#470-G when unset). Coverage by slice, all reusing the SAME flag and the SAME
  "guarded bare-identifier or legacy+`console.warn`" 3-way decision for any
  developer-authored callback:

  | Slice | Covers | Island mechanism | Falls back to legacy when |
  |---|---|---|---|
  | **G** | `<wt:tree>` `item-url`, `<wt:ueditor>`, `<wt:textarea ShowCounter>` | `loadComboItems` island reuse; `ueditor` action; `data-wtm-counter` attr (no script at all) | never (all callback-free) |
  | **H** | `<wt:datetime>` (laydate), incl. the **range** configuration | island (`ready`/`change`/`done` + native range write-back) | any non-identifier callback |
  | **I** | `<wt:slider>`, `<wt:colorpicker>` | island (`bindChange`/`autocomplete`/`verify` actions) | non-identifier `change-func`/`on-tips-func` |
  | **J** | `<wt:combobox>`, `<wt:tree>` **render** (the `xmSelect.render` hard blocker) | `renderSelect` island | non-identifier `ChangeFunc` |
  | **K** | `<wt:transfer>` | `renderTransfer` island | non-identifier `ChangeFunc` |
  | **L** | `<wt:upload>`, `<wt:multiupload>` | island (`ff.upload` namespace: `DoDelete`/`DoPreview`/`SetValues`) | non-identifier callback |
  | **M** | Grid-cell/dialog-action buttons (`LayuiUIService.MakeDialogButton`/`MakeButton`/`MakeViewButton`/`MakeDateTime`/`MakeScriptButton`); `<wt:submitbutton>`'s **click body** | `data-wtm-click` delegated dispatch (zero inline script for `Make*`); fixed `ff._submitButtonClick(id)` call for SubmitButton (still wrapped in one inline `<script>` — see residual table) | non-bare-call `Click`/`script` expression |
  | **N1** | `<wt:treecontainer>`, `<wt:chart>` | island | non-identifier `ClickFunc` (treecontainer) |
  | **N2** | `<wt:searchpanel>` (AJAX/non-`OldPost` mode) | `searchPanelInit` island + delegated `click`/`myclick` | `OldPost=true` or `IsInSelector=true` |
  | **O1** | `<wt:grid>` render core (`table.render`, templet registry replacing the `_raw_`-function-string injection) | `renderGrid` island | `IsInSelector`, `EnableAnalysis`, non-identifier `DoneFunc`/`CheckedFunc`/`GridAction.OnClickFunc` |
  | **O2** | Grid toolbar + row-action buttons (retires the `wtToolBarFunc_{Id}` dispatcher and both laytpl `<script type="text/html">` templates) | `gridActions[]` + `toolbarHtml` descriptors on the same island | same gate as O1 (grid-level, not per-button) |
  | **O3** | `UseLocalData`, grid-cell `onchange` delegation, the `SearcherExpanded` fold script | `localData` island field; `data-wtm-cellchange*` delegation; `foldPanel` island action | same gate as O1 |

  `UseLocalData` was itself a legacy-forcing condition through O1/O2 and was lifted in
  O3 — a local-data grid is now island-eligible under the same O1 gate as every other
  grid.

## Honest residual blockers (verified by grep against the current tree, not carried
forward from the pre-Slice-Q version of this doc)

Even with `UseSelectIslandRender=true` **and** the kill-switch flipped, the following
still emit — or can still execute — inline script. Each row states *why*, and whether
it is expected to close.

| Blocker | Why it's still legacy | Status |
|---|---|---|
| `<wt:checkbox>` / `<wt:radio>` `{Id}defaultvalues` global write | Intentionally **not** islandifiable — #632 tried an island, #646 and #649 (two separate pre-release adversarial reviews) both found it broke the synchronous parse-time back-compat contract app code relies on. The inline write is now the *sole* publisher, unconditionally, regardless of the flag. The CSP-safe read path is the `data-wtm-defaults` attribute (`ff.ChainChange` already reads only that). | **Permanent by design** — will never be islandified; deprecation is "use `data-wtm-defaults`", not "wait for a future slice". |
| `<wt:selector>` `$$script$$`-tokenized search-panel template (`<script type="text/template" id="Temp{Id}">`) | Slice P (sentinel retirement — replace the string-token sentinel with a native `<template>` element) has **not shipped**. The sentinel itself is inert markup (`type="text/template"` never executes), but `ff.OpenDialog2` rehydrates its `$$script$$` segments back into a live `<script>` on dialog open — one of the kill-switch's 4 gated points. | Tracked: #655, gated on the same BMS staging-validation gate as #567 Phase 2 (flipping `UseSelectIslandRender` to default-**on** in selector panels). |
| Grid `IsInSelector=true` | O1's `DetermineGridIslandDecision` forces these to legacy — smaller blast radius, same containment reasoning as the selector sentinel above. | Tracked: #655 (same gate as the row above — Slice P lifts both together). |
| Grid `EnableAnalysis=true` | The inline analysis-toggle button and `framework_analysis.js`/`sortable.min.js` `<script src>` includes are only emitted on the legacy path; O1's decision function forces these grids to legacy permanently ("out of scope through O3" per `DataTableTagHelper.Island.cs`'s own class doc). | Out of scope for the grid campaign; would need a dedicated slice. |
| `<wt:checkbox>`/`<wt:switch>`/`<wt:radio>` `ChangeFunc` → `layui.form.on(...)` change-event binding; `<wt:textbox SearchUrl>` autocomplete render | `Abstraction/BaseElementTag.cs`'s `Process()` switch emits these as unconditional inline `<script>` — **no `UseSelectIslandRender` check exists in this file at all**. Distinct from the widgets' own *render* islandification (J/K did the render half; this change-event wiring was never touched by any slice). | **CLOSED for this table's premise (corrected 2026-07-28, #835).** Identified in the Slice Q sweep, then fixed by #784 (`463807636`), which gates these emitters behind `UseSelectIslandRender`. This table lists blockers that survive *even with the flag on* — these no longer do, so the row is retained only for history. They do still emit inline script at the flag's **default `false`**, but that is the legacy path this whole document is about, not a residual blocker. |
| `<wt:button click="...">` / `<wt:submitbutton>` click-wiring `<script>` wrapper | `Abstraction/BaseButton.cs`'s `BaseButtonTag.Process()` always emits `$('#{Id}').on('click', function(){...});` inside an inline `<script>` tag, with no flag check in that file. Slice M's `SubmitButtonTagHelper` flag-ON path only swaps the closure *body* to a fixed `ff._submitButtonClick(id)` call when `Click` is island-safe — the wrapping `<script>` element itself is unconditional. Contrast with `LayuiUIService.Make*` (Slice M, same commit) which built genuinely zero-script `data-wtm-click` anchors for C#-generated buttons — that gap is why grid-cell/dialog-action buttons are CSP-clean today but hand-authored `<wt:button>`/`<wt:submitbutton>` markup is not. | **CLOSED for this table's premise (corrected 2026-07-28, #835)** — fixed by #784 (`463807636`); see the `ChangeFunc` row above for the full correction. **Caveat that remains open:** #784's replacement handler calls only `preventDefault()`, while the jQuery `return false` it replaced did `preventDefault()` **and** `stopPropagation()`, and the delegated listener sits at `document` in the bubble phase (`framework_layui.js:6480`, no `{capture:true}`) — by then every ancestor handler has already run. The commit message's "matches legacy `return false`" claim is **retracted**; see #835. |
| `<wt:tab>` / `<wt:panel>` wiring | `TabTagHelper`/`PanelTagHelper` always emit a fixed-shape inline `<script>` (tab-selection + chart-resize; collapse-resize respectively). Nominally listed under Slice N's original scope ("searchPanel + tab/panel/treeContainer/chart wiring", 2026-07-12 plan) but only TreeContainer/Chart (N1) and SearchPanel (N2) actually shipped. | **CLOSED for this table's premise (corrected 2026-07-28, #835)** — dropped silently from N's delivered scope, then fixed by #784 (`463807636`); see the `ChangeFunc` row above for the full correction. |
| `EnableAutoVerify`'s `[Phone]`/`[RegularExpression]` `layui.form.verify()` registration | `Abstraction/BaseFieldTag.cs` — gated behind the **separate**, narrower `WtmUIOptions.EnableAutoVerify` opt-in (default off); when that flag is on, it always emits inline script regardless of `UseSelectIslandRender`. No island alternative exists yet. | Low priority — already double-opt-in; noted for completeness, not a hard blocker for the mainstream recipe. |
| Non-identifier developer callbacks (any widget: `ChangeFunc`/`DoneFunc`/`CheckedFunc`/`OnClickFunc`/`BeforeSubmit`/`Click`/`script`/…) | By design — an arbitrary call/dotted/compound expression cannot be safely re-expressed as JSON data for `ff.DispatchAction` to run, so every slice keeps the exact legacy inline script for that one field/button/grid and emits a `console.warn` naming the offending attribute. | **Inherent, not a bug.** Rewrite the callback as a bare no-arg (or single-arg, widget-dependent) named function to become island-eligible. |
| `<wt:form>` with non-identifier `BeforeSubmit`; `<wt:searchpanel OldPost="true">`'s submit-click handler | The AJAX-submit *island* (`bindSubmit`) only fires for an absent or bare-identifier `BeforeSubmit` — a non-identifier expression keeps the legacy `layui.form.on('submit(...)')` script so the submit gate still runs, per the "never silently change default behaviour" red line (#561). `OldPost=true` forms post natively — no JS binding is needed or emitted at all beyond the one inline click handler. | By design; `BeforeSubmit` closes the same way as any other non-identifier callback above. `OldPost` is inherent (native post, not a gap). |
| The single `eval(` — `ff._legacyScriptEval` (`IsScript` response-header protocol) | Back-compat for controllers still returning the deprecated script-body response shape. | **Deprecated.** See below — migrate to `FFResultJson()`; removal stays gated on downstream `IsScript` usage reaching zero (tracked on the #789/#807 futures epics). |
| `ff.OpenDialog` / `ff._replayInitFromHtml` / `ff.OpenDialog2` rehydration of AJAX-loaded partials | These exist so a partial/dialog response can still carry a hand-authored inline `<script>` (app code, not framework-generated) and have it execute after DOMPurify sanitization. Closing this permanently would require every app-authored dialog script to migrate off inline `<script>` too — outside the framework's control. | Close it **per-app** via the #627 kill-switch once your own dialog partials are script-free (Level 2 below). The framework side stays available indefinitely for apps that have not opted in. |

## The kill-switch (opt-in, default OFF)

Two equivalent mechanisms; either one disables all four legacy execution points.
Default is OFF — behaviour is byte-identical to previous releases until you opt in.

**Markup-only (preferred — works under the strictest CSP, no inline script needed):**

```html
<!-- in your _Layout.cshtml <head> -->
<meta name="wtm-disable-legacy-script-rehydration" content="true">
```

**JS property (for programmatic/per-page control):**

```js
ff.DisableLegacyScriptRehydration = true; // strict boolean — 'true' string does NOT count
```

The flag is read live on every dialog open / fragment replay / IsScript response, so it
can be toggled at runtime (useful in SPA shells, staging audits, and tests). When ON:

| Legacy path | Behaviour when disabled |
|---|---|
| `ff._legacyScriptEval` (`IsScript` responses) | **Not executed.** `console.error` names the blocked path and points to `FFResultJson()` migration. |
| `ff.OpenDialog` inline-script rehydration | **Not executed.** One `console.warn` with the count of skipped scripts. Dialog-init islands dispatch normally. |
| `ff._replayInitFromHtml` (SPA fragment / PostForm redraw) | Same as above. |
| `ff.OpenDialog2` Selector search-panel `$$script$$` rehydration | **Not executed.** Script segments are stripped from the template (non-script search-panel markup still renders); one counted `console.warn`. |

Script **extraction** from dialog markup is unchanged — it happens before
`ff.SafeHtml`/DOMPurify sanitization and is part of the markup-safety pipeline. Only
**execution** is gated. The diagnostics are deliberately loud: any warning in staging
means something still rides a legacy path — your own partial, a developer callback that
isn't a bare identifier, or one of the residual blockers in the table above.

## The graduated recipe

### Level 0 — shipped default (already eval-free)

`app.UseWtmContentSecurityPolicy()` emits, by default:

```
script-src 'self' 'unsafe-inline'
```

`'unsafe-eval'` is **already absent** — the framework baseline needs no eval. If you serve
this default and nothing breaks, you are at level 0. (See `WtmCspOptions` for every
directive; `Mode = WtmCspMode.ReportOnly` gives a non-blocking trial run, and `ReportUri`
collects violation reports.)

### Level 1 — audit: is your app eligible for the recommended target?

Two things to check before flipping `UseSelectIslandRender` + the kill-switch in
production:

1. **Do your dialogs/pages hit a residual blocker?** Walk the table above. A grid with
   `EnableAnalysis` or `IsInSelector`, a `<wt:selector>`, checkbox/radio defaultvalues,
   or a non-identifier developer callback anywhere will fall back to the exact legacy
   script for *that one configuration* — loudly (`console.warn`), never silently. That
   is usually fine (most pages mix a handful of legacy-only widgets with many
   island-eligible ones); it only blocks Level 3 (see below) for the specific
   page/dialog that contains it.
2. **Your own hand-authored inline `<script>` blocks** in anything loaded via
   `ff.OpenDialog` / `ff.OpenDialog2` / SPA fragments. Migrate them to
   `<wt:dialog-init>` JSON islands (or the `ff.DispatchAction` action vocabulary); for
   genuinely custom logic, move it into an external `.js` file invoked via a
   whitelisted island action or a delegated event handler.

How to audit in practice:

- **Turn on `UseSelectIslandRender` in staging** and watch the browser console: every
  `[WTM] … UseSelectIslandRender is ON but island render was skipped … See #470 Slice
  {G..O}.` warning pinpoints a straggler by TagHelper Id/attribute name, not just a
  widget class.
- **Watch for** `[WTM] IsScript script-body response is deprecated` — each occurrence is
  a controller action still on the legacy script-body protocol; migrate it to
  `FFResultJson()`.
- **Enable the kill-switch in staging** and exercise every dialog-heavy flow: each
  `[WTM] … NOT executed (DisableLegacyScriptRehydration)` warning pinpoints a straggler
  with the count of skipped scripts. Silence across full regression coverage = eligible
  for Level 2.

### Level 2 — flip the kill-switch in production (recommended target)

After a clean level-1 audit. Add the `<meta>` tag to your layout, and set
`UseSelectIslandRender = true` in `WtmUIOptions`. Rollback is deleting one line / one
config flip each — no server restart beyond the config reload, no schema change. From
this point the dialog/fragment flow performs **zero** `eval` and **zero** dynamic script
injection across all four gated paths, deterministically (instead of relying on the
browser's CSP blocking with noisy violation reports) — for every widget configuration
that isn't in the residual-blocker table above.

### Level 3 — tighten `script-src` toward `'self'` (recommended target for eligible pages)

With the kill-switch on and `UseSelectIslandRender` on, most pages no longer need
`'unsafe-inline'`. What *still* needs it, page by page:

- **Any of the residual blockers above**, if present on that page — checkbox/radio
  defaultvalues, a selector, an `EnableAnalysis`/`IsInSelector` grid, a non-identifier
  callback, `<wt:tab>`/`<wt:panel>`/`<wt:button>` (see #784), or `EnableAutoVerify`
  Phone/RegularExpression rules.
- **Your own layout bootstrap** — e.g. the demo's:

```html
<script>
  var DONOTUSE_IGNOREHASH = false;
  var DONOTUSE_COOKIEPRE = '@ViewData["DONOTUSE_COOKIEPRE"]';
  var DONOTUSE_WINDOWGUID = '@Guid.NewGuid().ToString().Replace("-", "")';
  layui.config({ base: '/layuiadmin/', version: '@DateTime.Now.Ticks' });
</script>
```

This block contains **per-request dynamic values**, so a static `'sha256-…'` hash cannot
cover it. Your options, in increasing order of effort:

- **Stay at `'self' 'unsafe-inline'`** (level 2). Already a real win: the *dynamic*
  injection surface is gone; `'unsafe-inline'` only covers scripts present in the initial
  server-rendered HTML, which your Razor layer controls, and — once
  `UseSelectIslandRender` is on — that set is now small and enumerable via the
  residual-blocker table above instead of "most of the widget catalog".
- **Externalize the bootstrap**: move the values into `<meta>`/`data-*` attributes
  (markup, not script — e.g. `<meta name="wtm-cookie-pre" content="@ViewData[...]">`)
  and read them from a small app-owned external `.js` file; generate the per-window GUID
  client-side. Then set `ScriptSrc = "'self'"` in `WtmCspOptions`. Any remaining *static*
  inline block can be allowed by adding its `'sha256-…'` hash to `ScriptSrc` (hashes work
  with the static policy string; browsers print the expected hash in the violation
  message).
- **Nonce-based CSP**: not currently provided by `WtmCspMiddleware` (a per-request nonce
  needs middleware↔Razor cooperation) — tracked separately under #807 (nonce/report-to
  futures epic, on this repo's pre-Gitea-cutover GitHub tracker, now defunct — see
  § Reference below). Do not hand-roll a static "nonce": a fixed value defeats the
  mechanism.

Recommended rollout for level 3: switch `Mode = WtmCspMode.ReportOnly` with the tightened
`ScriptSrc`, run staging + a production canary until the violation reports are quiet, then
`Enforce`.

## Deprecating the legacy paths

`UseSelectIslandRender=false` (the default) and the kill-switch OFF (the default) remain
fully supported — this is a **documentation-level deprecation**, not a behaviour change.
Nothing here alters a default, logs a startup warning, or nags at runtime; the framework
stays silent unless you opt in and then hit a residual blocker (in which case you get a
targeted `console.warn`, not a generic nag).

- **Prefer `UseSelectIslandRender=true`** for new projects and during planned migrations
  of existing ones. It is additive: flipping it on only changes rendering for the
  configurations in the slice-coverage table above; every residual blocker keeps working
  exactly as before, with a warning instead of a silent gap.
- **Avoid the `IsScript` response-header protocol** in new controller code — return
  `FFResultJson()` instead. `IsScript` is what keeps `ff._legacyScriptEval` (the codebase's
  one remaining `eval(`) alive; it is not removed yet because downstream apps still use
  it, and removing it would be a breaking change requiring a major-version migration path,
  not a docs update. Track migration progress via the `console.error` the kill-switch
  emits when it blocks an `IsScript` response.
- **`WtmUIOptions.UseSelectIslandRender`**'s XML doc (`Core/ConfigOptions/WtmUIOptions.cs`)
  now points here for the current, full scope of what the flag covers — it originally
  documented only Slice J's combobox/tree behaviour and has been kept up to date as later
  slices reused the same flag.
- **Kill-switch default-ON** is explicitly **out of scope** for this deprecation pass — it
  is a next-major-version topic (a real behaviour change: it stops re-executing
  app-authored inline scripts in AJAX partials by default), not something a docs slice can
  schedule.

### Honest limits (updated)

- **Partially-islandified widgets degrade, they do not break.** A configuration this doc
  lists as "falls back to legacy" (a non-identifier callback, an `EnableAnalysis` grid, a
  selector, …) keeps working exactly as it did before `UseSelectIslandRender` existed —
  the framework detects the condition and emits one actionable `console.warn` naming the
  TagHelper/attribute instead of throwing or silently degrading.
- **The residual-blocker table above is the current, exhaustive list** — it replaces the
  older "indicative, not exhaustive" hard-blocker inventory from before Slice Q, which
  predates the G→O campaign and listed combobox/tree/transfer/ueditor/upload/grid/
  selector/checkbox/radio as uniformly still-legacy. That was accurate at the time; it is
  stale now. Do not audit by widget class alone regardless — the staging-flip procedure in
  level 1 is still the authoritative check, since new app code or a future framework
  change could add to the table above before this doc is updated again.
- `style-src` keeps `'unsafe-inline'`: layui and several TagHelpers set inline styles.
  Tightening style-src is out of scope here.
- Third-party or legacy pages you cannot audit (hand-written admin partials, vendored
  widgets with inline `onclick=`) each need individual migration; CSP `'self'` will
  refuse inline event handlers too.
- The `_legacyScriptEval` helper itself stays in the codebase indefinitely as back-compat
  for apps that have **not** opted in — the kill-switch changes your app's runtime
  behaviour, not the framework's compatibility guarantee.

## Related: `Layui:Asset=legacy` vendored-tree XSS (#776)

A separate, narrower issue from the CSP campaign above, but worth knowing if you are
deciding which vendored layui tree to serve: the **default** vendored tree
(`Layui:Asset` absent or any value other than `"legacy"`, selecting `/layui-next`,
layui 2.13.8) is not affected. Setting `Layui:Asset=legacy` (selecting the older,
deprecated `/layui`, layui 2.6.3 tree) historically was — that tree's `table.js` built
each grid cell's `<td data-content="...">` attribute (an internal tooltip/truncation
feature) directly from the raw field value, with **no double-quote escaping**. A cell
value containing a literal `"` followed by markup could break out of the attribute and
inject real DOM, entirely independent of WTM's own `ff.EscapeText` cell-templet guard
(#108), which never runs on this code path — it affects grids regardless of templet
configuration.

The vulnerable construction physically existed in **two** on-disk locations: the
monolithic `layui.js` bundle (which the Demo and Vue3Demo vendored trees ship with the
"table" module inlined — and which `_Layout.cshtml` actually loads in production, so
this is the one that mattered at runtime for those trees) and the standalone
`lay/modules/table.js` module file (fetched on demand by trees, like BlazorDemo, that
ship `layui.js` as a thin loader instead of a bundle). Both needed the identical
one-line fix — patching only the standalone module file would have left the bundled
trees exploitable. The vendored copies in this repo now carry an upstream-parity fix in
every such location (escapes via `layui.util.escape`, matching what 2.13.8 already
did). Two things to know if you use `legacy`:

- A **future re-vendor** of the 2.6.3 tree (e.g. pulling a fresh copy from upstream to
  pick up an unrelated fix) could silently reintroduce the unpatched construction in
  either location — `legacy` is a real downstream rollback path back to vulnerable
  code, not just a historical footnote.
- This is one more reason `Layui:Asset=legacy` is deprecated (see
  `LayuiAssets.ResolveLayuiBase`'s XML doc); prefer the default `/layui-next`. As
  defense in depth, a strict `script-src` (level 3 above, no `'unsafe-inline'`) would
  also block the injected inline event handler / `<script>` this class of bug produces
  — another reason to work toward the graduated recipe above regardless of which layui
  tree you serve.

WTM emits one `LogWarning` at startup (`UseWtmContext`) when `Layui:Asset=legacy` is
detected, naming #776.

## Reference

| Knob | Where | Default |
|---|---|---|
| `WtmUIOptions.UseSelectIslandRender` | `services.Configure<WtmUIOptions>(...)` | `false` (OFF) — recommended `true` for the strict-CSP target |
| `<meta name="wtm-disable-legacy-script-rehydration" content="true">` | app layout markup | absent (OFF) — recommended present for the strict-CSP target |
| `ff.DisableLegacyScriptRehydration` | JS, strict `=== true` | `undefined` (OFF) |
| `WtmCspOptions.ScriptSrc` | `UseWtmContentSecurityPolicy(...)` | `'self' 'unsafe-inline'` |
| `WtmCspOptions.Mode` | same | `Enforce` (`ReportOnly` for trials, `Disabled` for kill) |
| `WtmCspOptions.ReportUri` | same | `null` |

Related: `docs/wtm-developer-manual.md` § security middleware; issue #470 (epic — the
G→O island slices and the residual-blocker inventory above both live there), #789 (eval
removal epic) and #807 (nonce/report-to futures, unsafe-inline removal epic) — both on
this repo's pre-Gitea-cutover GitHub tracker, now defunct — #627 (this kill-switch),
#655/#567 (Slice P / selector-default-on gate), and #784 (residual non-flag-gated
emitters found during the Slice Q sweep: `BaseElementTag`'s change-event bindings,
`BaseButtonTag`'s click-wiring wrapper, `<wt:tab>`/`<wt:panel>`).
