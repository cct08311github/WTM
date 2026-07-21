# CSP Hardening Guide — the Dialog-Flow Strict-CSP Recipe

> Added in 10.14.3 (#627, a slice of the #470 epic). Explains how far a WTM app can
> tighten `Content-Security-Policy` today, exactly which knob unlocks each level, and —
> just as important — which framework widgets are **not yet** ready for the strictest
> levels.

## Background: what is and is not eval-free today

The `framework_layui.js` eval-removal effort (originally tracked as #789 on this repo's
pre-Gitea-cutover GitHub tracker, now defunct, phases 1–3D; continued locally as
#470/#552/#556/#558/#561/#564/#576/#587) ended with:

- **The common form-init paths are JSON-island-driven** (v10.13.12+): form init/submit/
  validate/error-highlight, laydate (non-range, callback-free), rate, taginput, and the
  callback-free slider/colorpicker configurations travel as
  `<script type="application/json">` islands consumed by the `ff.DispatchAction`
  whitelist — `JSON.parse`, never code execution.
- **Exactly one `eval(` remains** in `framework_layui.js`: `ff._legacyScriptEval`, the
  back-compat fallback for the deprecated `IsScript` response-header protocol.
- **Three dynamic `<script>`-injection paths remain** for AJAX-loaded content:
  `ff.OpenDialog` (#522) and `ff._replayInitFromHtml` (#587) re-run inline `<script>`
  extracted from partials; `ff.OpenDialog2` rehydrates the Selector search-panel's
  `$$script$$` template tokens (#332).
- **Many framework TagHelper configurations still emit executable inline `<script>`**
  (tracked under the #470 hard blockers — xmSelect's global-`var` sharing needs a
  dedicated declarative action). The list below is **indicative, not exhaustive** — the
  authoritative audit is the grep + staging-flip procedure in level 1:
  - *Unconditional*: `<wt:combobox>` / `<wt:tree>` (the `xmSelect.render` block — every
    combobox), `<wt:transfer>` (`layui.transfer.render`), `<wt:ueditor>`,
    `<wt:upload>` / `<wt:multiupload>` (per-widget `DoDelete`/`DoPreview` helpers),
    `<wt:grid>`/data tables rendered inside AJAX-loaded partials,
    `<wt:selector>` (the whole `$$script$$`-tokenized search-panel template), the
    SearchPanel `OldPost` click handler, and `<wt:checkbox>` / `<wt:radio>`. The
    field's default selection travels as a `data-wtm-defaults` HTML attribute
    (race-free; `ff.ChainChange` reads only this, never a script), and the legacy
    `{Id}defaultvalues` global is *also* published via an unconditional inline
    `<script>` write, for app-authored JS that reads it synchronously right after the
    widget markup (#646: an earlier redesign (#632) made a `wtm-dialog-init` JSON
    island — type `fieldDefaults` — the *only* publisher of that global; on a full
    page the island is consumed at DOMContentLoaded, which silently broke that
    parse-time back-compat guarantee). checkbox/radio therefore rejoined this
    unconditional inline-script list in #646.
    **#649 correction:** #646's fix kept the island alongside the restored inline
    write, which reintroduced a *different* bug — on a default full-page load the
    island's DOMContentLoaded dispatch unconditionally re-published the *original
    server* values, silently clobbering any mutation app-authored JS made to the
    global in between. checkbox/radio no longer emit the `fieldDefaults` island at
    all; the inline write is now the **sole** publisher of the global. Consequence:
    under the #627 kill-switch (strict CSP), the browser blocks this inline script
    and, since #649, nothing else republishes the global for these two widgets — an
    app that needs the value under strict CSP must read the `data-wtm-defaults`
    attribute instead (exactly what `ff.ChainChange` already does). The global is
    therefore no longer CSP-clean-capable via a deferred island; it is inline-only,
    synchronous-or-absent.
    (An earlier #632 draft made the island the authoritative source for
    `ff.ChainChange` itself; review found a real dispatch-ordering race — see the
    #632 commit message. That part of #632 stands; only the app-facing global's
    publisher changed.)
  - *Conditional*: `item-url` on combobox/transfer/checkbox/radio (`ff.LoadComboItems`);
    `<wt:datetime>` **callback and range** branches (the range configuration emits the
    inline script even with zero callbacks); `<wt:slider>` / `<wt:colorpicker>` when a
    `change-func`/`on-tips-func` callback is set; `<wt:form>` when `BeforeSubmit` is not
    a plain identifier.

The four legacy execution points exist for BOTH app-authored inline scripts **and** the
framework widget configurations above. That distinction drives the honest audit in
level 1 below.

**Scope note:** the kill-switch gates the framework's *dynamic* execution paths — i.e.
scripts inside **AJAX-loaded** dialogs and fragments. Inline scripts in a normal
full-page load execute natively by the browser and are unaffected by the switch (they
become relevant only at level 3, where the CSP itself starts blocking them).

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
means something still rides a legacy path — your own partial *or* one of the framework
widget configurations listed above.

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

### Level 1 — audit: is your AJAX-loaded content script-free?

The kill-switch only pays off if nothing in your dialogs/fragments depends on the legacy
paths. Two categories to audit:

1. **Framework widgets that still emit inline scripts.** If any AJAX-loaded dialog or
   fragment contains a widget configuration from the inventory above (combobox, tree,
   transfer, ueditor, upload, checkbox/radio, embedded grids, selector, range or
   callback datetimes, callback sliders/colorpickers, non-identifier `BeforeSubmit`
   forms — **non-exhaustively**), that content **requires** the legacy rehydration today
   and will break (loudly) with the switch on. These are #470 hard-blocker territory —
   most CRUD apps with comboboxes in create/edit dialogs are **not yet eligible** and
   should stay at level 0/1 until those widgets get island-driven variants. Because the
   list is not exhaustive, do not audit by widget list alone: the staging flip in step 3
   below is the authoritative check.
2. **Your own inline `<script>` blocks** in anything loaded via `ff.OpenDialog` /
   `ff.OpenDialog2` / SPA fragments. Migrate them to `<wt:dialog-init>` JSON islands (or
   the `ff.DispatchAction` action vocabulary); for genuinely custom logic, move it into
   an external `.js` file invoked via a whitelisted island action or a delegated event
   handler.

How to audit in practice:

- **Grep your views** for the widget list above inside dialog partials, and for
  hand-written `<script>` in AJAX-loaded content.
- **Watch the browser console in staging** for
  `[WTM] IsScript script-body response is deprecated` — each occurrence is a controller
  action still on the legacy script-body protocol; migrate it to `FFResultJson()`.
- **Enable the kill-switch in staging** and exercise every dialog-heavy flow: each
  `[WTM] … NOT executed (DisableLegacyScriptRehydration)` warning pinpoints a straggler
  with the count of skipped scripts. Silence across full regression coverage = eligible.

### Level 2 — flip the kill-switch in production

Only after a clean level-1 audit. Add the `<meta>` tag to your layout. Rollback is
deleting one line — no server restart, no config change. From this point the dialog/
fragment flow performs **zero** `eval` and **zero** dynamic script injection across all
four gated paths, deterministically (instead of relying on the browser's CSP blocking
with noisy violation reports).

### Level 3 — tighten `script-src` toward `'self'`

With the kill-switch on, the framework no longer needs `'unsafe-inline'` for the dialog
flow. What *usually still needs it*:

- **Framework widgets on full-page loads.** The same widget list from level 1 emits
  inline `<script>` into normal page loads too, where the browser executes it natively.
  Dropping `'unsafe-inline'` blocks those — so level 3 additionally requires that your
  *pages* (not just dialogs) avoid the still-script-based widget configurations.
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
  server-rendered HTML, which your Razor layer controls.
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

### Honest limits

- **Partially-islandified widgets degrade, they do not work.** Islandification lands one
  widget concern at a time. A widget whose *data-loading* half is an island but whose
  *render* half is still a legacy inline `<script>` (today: `<wt:combobox>`/`<wt:tree>`'s
  `xmSelect.render`, `<wt:transfer>`'s `transfer.render`) behaves asymmetrically with the
  kill-switch ON: the island still dispatches, the render script does not. The framework
  detects this and emits one actionable `console.warn` naming the widget instead of
  throwing — items are fetched but cannot be applied. That warning means "this widget is
  not yet eligible", not "your page is broken beyond this point". It disappears when the
  widget's render half is islandified (tracked under #470).
- **Most WTM apps cannot reach level 2/3 yet** if their dialogs use comboboxes/selectors —
  that is a framework limitation (the #470 xmSelect hard blocker), not an app defect.
  The switch still has value there as a *staging audit tool*: flip it in a test
  environment to enumerate exactly what would break.
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
| `<meta name="wtm-disable-legacy-script-rehydration" content="true">` | app layout markup | absent (OFF) |
| `ff.DisableLegacyScriptRehydration` | JS, strict `=== true` | `undefined` (OFF) |
| `WtmCspOptions.ScriptSrc` | `UseWtmContentSecurityPolicy(...)` | `'self' 'unsafe-inline'` |
| `WtmCspOptions.Mode` | same | `Enforce` (`ReportOnly` for trials, `Disabled` for kill) |
| `WtmCspOptions.ReportUri` | same | `null` |

Related: `docs/wtm-developer-manual.md` § security middleware; issues #470 (epic — the
xmSelect/widget islandification hard blockers live there), #789 (eval removal epic) and
#807 (nonce/report-to futures, unsafe-inline removal epic) — both on this repo's
pre-Gitea-cutover GitHub tracker, now defunct — and #627 (this kill-switch).
