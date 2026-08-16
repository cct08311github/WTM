#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    // Issue #470 Slice O1: opt-in (WtmUIOptions.UseSelectIslandRender, default OFF —
    // the SAME flag Slices J/K/L/M/N1 use) eval-free 'renderGrid' JSON island render
    // for <wt:grid>/DataTableTagHelper. Design authority: internal infrastructure issue #470 comment
    // 18118 ("Slice O design brief"). This file is intentionally split from
    // DataTableTagHelper.cs (which stays completely UNTOUCHED except for the
    // `partial` keyword and the single branch point in Process()) so the flag-OFF
    // byte-identity guard (DataTableByteIdentityTests) can never be disturbed by
    // island-only edits — every method here is either net-new or an early-return
    // gated on the flag, never a modification of existing legacy code.
    //
    // Containment (brief §2 point 3/5, invariant 5): the whole grid falls back to
    // the EXACT legacy BuildTableOptionsScript path (+ a console.warn naming the
    // reason) when any of: IsInSelector (Selector.cshtml grids stay legacy in O1 —
    // smaller blast radius, matches the brief's "recommended" containment),
    // EnableAnalysis (inline onclick toggle button — out of scope through O3),
    // or a developer-authored DoneFunc/CheckedFunc/GridAction.OnClickFunc that is
    // not a bare JS identifier (an arbitrary call/dotted expression can't be
    // safely JSON-expressed — same 3-way decision Slices J/K/L/N1 already use).
    // UseLocalData was ALSO in this list through O1/O2 ("localData island
    // deferred to Slice O3") — Slice O3 (below) lifts that containment; see
    // DetermineGridIslandDecision's own comment.
    //
    // GridActions toolbar/row-button (brief §2 O1 point 4, invariant 7): even when
    // a grid islandifies, the wtToolBarFunc_{Id} inline dispatcher + the two laytpl
    // <script type="text/html"> templates are STILL emitted verbatim (character-for-
    // character identical to the legacy chunk) — O1 only islandifies the table
    // RENDER core; toolbar/row-button descriptor islandification is O2 scope.
    //
    // Issue #470 Slice O2 (this file, continued): completes the toolbar/row-button
    // islandification O1 deferred. For island-eligible grids ONLY (containment
    // unchanged from O1 — DetermineGridIslandDecision is not touched by O2), the
    // legacy wtToolBarFunc_{Id} inline dispatcher AND both laytpl
    // <script type="text/html"> templates are now RETIRED, replaced by:
    //   - a `gridActions[]` descriptor array on the renderGrid island (dispatch
    //     info for every GridAction leaf, keyed by its `event` = Area+Controller+
    //     Action+QueryString — the SAME string legacy used as the `lay-event`/
    //     `case` key) — named `gridActions`, NOT `actions`, to avoid colliding
    //     with ff._normalizeIslandPayload's `Array.isArray(parsed.actions)`
    //     batch-shape check (see the Actions property's own comment below for
    //     the full incident writeup — a regression that shipped once already);
    //   - a server-rendered `toolbarHtml` string (the toolbar buttons, with
    //     data-wtm-click/data-wtm-grid/data-wtm-event attributes replacing inline
    //     onclick — Slice M's ff._buttonAction infra) assigned DIRECTLY to layui's
    //     `toolbar` option as literal HTML (never a `<script type="text/html">`
    //     selector — see BuildIslandActions's doc for why this is not just a
    //     cosmetic difference);
    //   - the row-action column's `toolbar: '#{ToolBarId}'` selector replaced by a
    //     `templet: {tpl:'actionCol'}` descriptor — ff.gridTemplets.actionCol
    //     (framework_layui.js) rebuilds each row's action anchors client-side,
    //     reproducing the retired laytpl `{{# if(d.{visibleField}) }}` conditional
    //     exactly (see AddIslandActionDescriptor's row-descriptor comment).
    // Real improvement (brief §2 O2, worth calling out): today, a
    // <script type="text/html"> template rendered INTO a dialog (e.g. a grid whose
    // markup arrives via ff.OpenDialog) is silently stripped by DOMPurify —
    // _collectInitFromHtml (framework_layui.js) only replays JS `<script>` blocks,
    // never non-JS-typed ones — so a dialog-hosted grid's toolbar/row buttons are
    // ALREADY dead code today. Since O2 never emits either laytpl block for island
    // grids, this failure mode cannot occur on the island path: dialog-hosted
    // island-toolbar buttons WORK where their legacy equivalent silently didn't.
    //
    // Issue #470 Slice O3 (this file, continued — LAST grid slice): completes the
    // grid islandification campaign. Design authority: comment 18118 §2 O3.
    //   1. UseLocalData containment LIFTED — DetermineGridIslandDecision no
    //      longer forces UseLocalData grids to legacy. BuildRenderGridAction
    //      populates a `localData` field (a data island field — an OBJECT-typed
    //      RenderGridIslandAction member holding the JSON array, never a bare
    //      top-level array and never named `actions`/`gridActions` — the O2
    //      incident's collision guard, see RenderGridIslandAction.Actions'
    //      comment) carrying the SAME payload the legacy path serializes via
    //      ListVM.GetDataJson(). EscapeLocalDataJson (DataTableTagHelper.cs)
    //      stays legacy-path-only: LayuiIslandJson.Serialize's underlying
    //      JsonSerializer already HTML-encodes '<'/'>'/'&' by default (no
    //      Encoder override in _jsonOptions), so a `</script>` substring inside
    //      row data can never break out of the island's own
    //      `<script type="application/json">` element — the #490 hardening
    //      applies transparently to the island path without needing its own copy.
    //   2. Grid-cell delegation — ff.AddGridRow/ff.LoadLocalData/ff.RemoveGridRow
    //      (framework_layui.js) now stamp `data-wtm-cellchange*` attributes
    //      instead of a literal `onchange="ff.gridcellchange(...)"` attribute
    //      when (and ONLY when) `ff.grid.isIsland(gridId)` reports true — that
    //      helper feature-detects island rendering from the `data-wtm-grid-id`
    //      attribute this Process() override already stamps on island-eligible
    //      `<table>` elements (unconditionally, since O1) — so a flag-OFF page's
    //      cell markup is emitted by the EXACT SAME code path, unchanged. The
    //      `layui.table.cache[gridid]` mutation + `[n]`/`_n_` index-rewrite
    //      semantics inside those three functions are NOT touched — only the
    //      wiring mechanism (attribute name / dispatch route) changes. See each
    //      function's own comment in framework_layui.js for the byte-for-byte
    //      parity argument.
    //   3. SearcherExpanded fold — BuildTableIslandScript's own copy of the
    //      trailing fold `<script>` (an island-only duplicate O1/O2 left as raw
    //      script text, unlike the rest of the island path) is replaced by a
    //      dedicated `foldPanel` island action, dispatched via
    //      ff._renderFoldPanelAction. The LEGACY BuildTableOptionsScript's own
    //      copy (DataTableTagHelper.cs) is untouched — flag-OFF byte-identity is
    //      unaffected either way, since this method is only ever reached on the
    //      island (flag-ON) path.
    //   4. ff.grid namespace (framework_layui.js) — `state(gridId)` centralizes
    //      reads of the four legacy compat globals for INTERNAL callers; the
    //      globals' WRITES (window[gridId+'option'/'defaultfilter'/'filterback'/
    //      'url'], written by both the legacy inline script and
    //      ff._renderGridAction) are UNCHANGED — Selector.cshtml's
    //      gridCheckedFunc and third-party user code read them directly and must
    //      keep working indefinitely (invariant 4).
    public partial class DataTableTagHelper
    {
        // Same identifier class ff._resolveGuardedWindowFn (framework_layui.js) and
        // every other #470 slice's containment gate enforces: a bare JS identifier,
        // nothing else. Mirrors ComboBoxTagHelper's/TreeContainerTagHelper's
        // _identifierRegex exactly, including the `\z` (not `$`) end anchor.
        private static readonly Regex _islandIdentifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        /// <summary>
        /// Result of the flag-ON containment analysis: whether this grid instance is
        /// eligible for island render, and — when the global flag is ON but this
        /// grid still fell back — the human-readable reason (surfaced via
        /// console.warn so the fallback is visible during migration, Slice J
        /// precedent).
        /// </summary>
        private readonly struct GridIslandDecision(bool useIsland, string? flagOnFallbackReason)
        {
            public bool UseIsland { get; } = useIsland;
            public string? FlagOnFallbackReason { get; } = flagOnFallbackReason;
        }

        private GridIslandDecision DetermineGridIslandDecision(List<GridAction>? actionCol)
        {
            if (!WtmUIOptionsHolder.Options.UseSelectIslandRender)
            {
                return new GridIslandDecision(false, null);
            }
            if (IsInSelector)
            {
                return new GridIslandDecision(false, "IsInSelector grids stay legacy in O1 (Selector.cshtml / #655 blast-radius containment)");
            }
            // Issue #470 Slice O3: the UseLocalData containment O1/O2 held (brief
            // §2 O1 point 3(b), invariant "localData island deferred to Slice O3")
            // is LIFTED here — UseLocalData grids now island-ify via the
            // `localData` field BuildRenderGridAction populates below, with
            // ff._renderGridAction consuming it through the SAME ff.LoadLocalData
            // function the legacy path calls (see that method's own #470 Slice O3
            // comment for the cache-mutation/cell-markup details). EnableAnalysis
            // and IsInSelector remain OUT of O3 scope — untouched above/below.
            if (EnableAnalysis)
            {
                return new GridIslandDecision(false, "EnableAnalysis (inline analysis-toggle button is legacy until Slice O2/O3)");
            }
            if (!string.IsNullOrEmpty(DoneFunc) && !_islandIdentifierRegex.IsMatch(DoneFunc))
            {
                return new GridIslandDecision(false, $"DoneFunc '{DoneFunc}' is not a plain identifier");
            }
            if (!string.IsNullOrEmpty(CheckedFunc) && !_islandIdentifierRegex.IsMatch(CheckedFunc))
            {
                return new GridIslandDecision(false, $"CheckedFunc '{CheckedFunc}' is not a plain identifier");
            }
            var badOnClick = FindNonIdentifierOnClickFunc(actionCol);
            if (badOnClick != null)
            {
                return new GridIslandDecision(false, $"GridAction.OnClickFunc '{badOnClick}' is not a plain identifier");
            }
            return new GridIslandDecision(true, null);
        }

        private static string? FindNonIdentifierOnClickFunc(IEnumerable<GridAction>? actions)
        {
            if (actions == null)
            {
                return null;
            }
            foreach (var action in actions)
            {
                if (!string.IsNullOrEmpty(action.OnClickFunc) && !_islandIdentifierRegex.IsMatch(action.OnClickFunc))
                {
                    return action.OnClickFunc;
                }
                var nested = FindNonIdentifierOnClickFunc(action.SubActions);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Builds the flag-ON island render output: the legacy toolbar-dispatcher
        /// script + laytpl templates (invariant 7 / brief §2 O1 point 4, verbatim),
        /// the <c>renderGrid</c> JSON island (invariant 8: via
        /// <see cref="LayuiIslandJson"/>), and the same trailing blocks
        /// (DetailGridPrix hidden input / button-group script / SearcherExpanded
        /// fold) <c>BuildTableOptionsScript</c> emits — duplicated rather than
        /// shared so the legacy method can stay 100% untouched for the
        /// byte-identity harness.
        /// </summary>
        private void BuildTableIslandScript(
            TagHelperOutput output,
            string vmQualifiedName,
            int maxDepth,
            List<string> aggregateFields,
            Dictionary<string, object> where,
            int lefttoolbarmergin,
            bool page)
        {
            var action = BuildRenderGridAction(vmQualifiedName, maxDepth, aggregateFields, where, lefttoolbarmergin, page);

            // Issue #470 Slice O2: the legacy wtToolBarFunc_{Id} inline dispatcher +
            // the two laytpl <script type="text/html"> templates (O1's accepted
            // interim, invariant 7) are RETIRED for island grids — see the class
            // doc's O2 section for the full rationale (incl. the DOMPurify-dialog
            // fix). Toolbar/row-button dispatch is now driven entirely by the
            // `gridActions`/`toolbarHtml` fields on the renderGrid island below
            // (ff._gridToolDispatch / ff.gridTemplets.actionCol, framework_layui.js).
            output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _jsonOptions)}</script>
");

            // Trailing blocks — copied verbatim from BuildTableOptionsScript's own
            // trailing-block emission (DetailGridPrix / SearcherExpanded).
            // EnableAnalysis's own trailing block is intentionally OMITTED here: that
            // condition forces the whole grid to the legacy path (see
            // DetermineGridIslandDecision), so this method is never reached with
            // EnableAnalysis == true. The button-group wiring script is likewise
            // omitted (O1 kept it per-grid; O2 retires it) — replaced by ONE
            // always-registered, `[data-wtm-btngroup]`-scoped delegated binding in
            // framework_layui.js. The island button markup deliberately does NOT
            // carry the legacy "downpanel" class (see that binding's comment and
            // AddIslandActionDescriptor's button-group branch, above) so a
            // coexisting legacy-fallback grid's own unscoped `.downpanel` per-render
            // script has nothing of the island's to match.
            output.PostElement.AppendHtml($@"
{(string.IsNullOrEmpty(ListVM.DetailGridPrix) ? string.Empty : $"<input type=\"hidden\" name=\"{Vm.Name}.DetailGridPrix\" value=\"{ListVM.DetailGridPrix}\"/>")}
");

            // Issue #470 Slice O3: replaces the island-only inline fold <script>
            // (O1/O2 duplicated BuildTableOptionsScript's own copy verbatim here,
            // one of the few remaining raw-<script> emissions on the island path)
            // with a `foldPanel` island action — ff._renderFoldPanelAction
            // (framework_layui.js) reproduces the SAME layui.element.fold(filter,
            // fold) call. `fold` mirrors the legacy `foldBool` computation exactly
            // (SearcherExpanded==true -> expanded -> fold:false).
            if (SearcherExpanded.HasValue)
            {
                var foldAction = new FoldPanelIslandAction
                {
                    SearchPanelId = SearchPanelId,
                    Fold = !SearcherExpanded.Value,
                };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(foldAction, _jsonOptions)}</script>
");
            }
        }

        private RenderGridIslandAction BuildRenderGridAction(
            string vmQualifiedName,
            int maxDepth,
            List<string> aggregateFields,
            Dictionary<string, object> where,
            int lefttoolbarmergin,
            bool page)
        {
            // Issue #470 Slice O2: island-native toolbar/row-button descriptor build
            // — see BuildIslandActions's own doc for the full rationale. Replaces O1's
            // dependency on the legacy toolBarBtnStrBuilder (still computed by
            // Process()'s unconditional BuildToolbarButtons() call for the LEGACY
            // path, but no longer consulted here).
            var (islandActions, toolBarHtmlRaw, actionMsgs) = BuildIslandActions(vmQualifiedName);

            // Mirrors Process()'s own `toolbardef` gate exactly (:510) — a grid gets a
            // toolbar element when it has row/toolbar buttons OR any of the three
            // built-in defaultToolbar icons (filter/print/client-export).
            bool hasToolbar = toolBarHtmlRaw.Length > 0 || NeedShowFilter == true || NeedShowPrint == true || EnableClientExport;

            var defaultToolbar = new List<string>(3);
            if (NeedShowFilter == true) defaultToolbar.Add("filter");
            if (NeedShowPrint == true) defaultToolbar.Add("print");
            if (EnableClientExport) defaultToolbar.Add("exports");

            string? heightMode = null;
            int? heightValue = null;
            if (Height.HasValue)
            {
                if (Height.Value >= 0)
                {
                    heightMode = "fixed";
                    heightValue = Height.Value;
                }
                else
                {
                    heightMode = "full";
                    heightValue = Height.Value;
                }
            }

            GridPageOptions? pageOptions = null;
            if (page)
            {
                pageOptions = new GridPageOptions
                {
                    Rpptext = THProgram._localizer["Sys.RecordsPerPage"],
                    Totaltext = THProgram._localizer["Sys.Total"],
                    Recordtext = THProgram._localizer["Sys.Record"],
                    Gototext = THProgram._localizer["Sys.Goto"],
                    Pagetext = THProgram._localizer["Sys.Page"],
                    Oktext = THProgram._localizer["Sys.GotoButtonText"],
                };
            }

            // Issue #470 Slice O3: island-native replacement for the legacy
            // `ff.LoadLocalData("{Id}",{Id}option,{EscapeLocalDataJson(...)},...)`
            // inline call (BuildTableOptionsScript :821-ish) — the SAME
            // ListVM.GetDataJson() payload, parsed into a JsonElement so it is
            // carried as structured JSON on the island object (never a raw JS-text
            // string, never EscapeLocalDataJson — that helper stays legacy-path
            // only; LayuiIslandJson.Serialize's underlying JsonSerializer already
            // HTML-encodes '<'/'>'/'&' by default, so a `</script>` substring in
            // row data can't break out of the island's own <script> element, the
            // SAME #490 protection the legacy path achieves a different way — see
            // this file's class doc). Named `localData`: an OBJECT-typed member
            // holding a nested JSON array — never a bare top-level array, never
            // `actions`/`gridActions` — so it cannot collide with
            // ff._normalizeIslandPayload's `Array.isArray(parsed.actions)`
            // batch-shape probe (the O2 incident this campaign learned from).
            JsonElement? localData = null;
            if (UseLocalData)
            {
                using var localDataDoc = JsonDocument.Parse(ListVM!.GetDataJson());
                localData = localDataDoc.RootElement.Clone();
            }

            var action = new RenderGridIslandAction
            {
                GridId = Id,
                TableJsVar = TableJSVar,
                Elem = "#" + Id,
                Id = Id,
                Text = new GridTextOptions { None = THProgram._localizer["Sys.NoData"] },
                // Non-null only when !IsInSelector, mirroring BuildTableOptionsScript's
                // own `IsInSelector==false? ... : ""` conditional (:721). IsInSelector
                // forces the whole grid to the legacy path in O1 (see
                // DetermineGridIslandDecision), so this is always non-null on the
                // island path today — kept conditional for schema/forward-compat
                // correctness once O2/O3 lift the IsInSelector containment.
                Request = IsInSelector ? null : new GridRequestOptions(),
                // Issue #470 Slice O2: the `#{ToolBarId}2` laytpl-<script>-selector
                // form (O1) is retired for island grids — Toolbar is intentionally
                // left null (schema kept for forward/backward compat; nothing sets
                // it anymore on the island path). ToolbarHtml (below) carries the
                // SAME button markup as a literal HTML string, assigned directly to
                // layui's `toolbar` option client-side — this is NOT cosmetic: a
                // literal string never touches a `<script type="text/html">` DOM
                // element, so it cannot be stripped by DOMPurify in dialog contexts
                // (see the class doc's O2 section).
                ToolbarHtml = hasToolbar
                    ? $@"<div id=""{Id}buttons"" style=""text-align:right;margin-right:{lefttoolbarmergin}px"">{toolBarHtmlRaw}</div>"
                    : null,
                // Always the (possibly EMPTY) list — never coerced to null. Mirrors
                // BuildDefaultToolbar, which ALWAYS emits `,defaultToolbar: [...]`
                // (never omits it), explicitly disabling layui's own built-in
                // filter/print/exports icons when the list is empty. Omitting this
                // field when the grid still has a toolbar (custom buttons only, no
                // built-in icons wanted) would let layui fall back to ITS OWN
                // default icon set instead of showing none — a real behavior
                // regression the flag-OFF harness can't catch (island-only field).
                DefaultToolbar = defaultToolbar,
                TotalRow = NeedShowTotal,
                // Pre-serialized with the SAME default (no-options) JsonSerializer call
                // BuildTableOptionsScript uses at :726 (`JsonSerializer.Serialize(where)`)
                // — preserves exact value semantics (e.g. enum-as-number vs
                // enum-as-camelCase-string) regardless of the outer DTO's own
                // _jsonOptions (which carries a JsonStringEnumConverter that would
                // otherwise re-shape any un-attributed enum inside `where`).
                Where = where.Count == 0 ? (JsonElement?)null : JsonSerializer.SerializeToElement(where),
                Method = Method == null ? "post" : Method.Value.ToString().ToLower(),
                Loading = (Loading ?? true) == false ? (bool?)false : null,
                Page = pageOptions,
                // Mirrors BuildTableOptionsScript's own ternary exactly
                // (`page ? Limit : (UseLocalData ? entityCount : 0)`) — UseLocalData
                // always implies page==false (Process() forces ListVM.NeedPage=false
                // before `page` is read), so this and the legacy formula can never
                // disagree. The array length is read off the JSON already built
                // above (localData) rather than re-querying
                // ListVM.GetEntityList().Count() a second time — provably the same
                // count (GetDataJson() emits exactly one JSON object per entity) and
                // avoids a redundant DB round-trip. ff.LoadLocalData overwrites this
                // to 9999 client-side regardless, so the exact value is cosmetic —
                // reproduced for fixture/schema fidelity only.
                Limit = page ? Limit!.Value : (UseLocalData ? localData!.Value.GetArrayLength() : 0),
                Limits = page && Limits != null && Limits.Length > 0 ? Limits : null,
                Width = Width,
                HeightMode = heightMode,
                HeightValue = heightValue,
                Cols = BuildColumnDescriptors(maxDepth),
                Skin = Skin.HasValue ? Skin.Value.ToString().ToLower() : null,
                Even = (Even.HasValue && !Even.Value) ? (bool?)false : null,
                Size = Size.HasValue ? Size.Value.ToString().ToLower() : null,
                Url = Url,
                ExportFileName = ExportFileName ?? Id,
                EnableClientExport = EnableClientExport,
                IsInSelector = IsInSelector,
                DetailGridPrix = string.IsNullOrEmpty(ListVM.DetailGridPrix) ? null : ListVM.DetailGridPrix,
                SearchPanelId = SearchPanelId,
                FieldPre = fieldPre,
                AutoSearch = AutoSearch,
                MobileLayout = page,
                Done = new GridDoneOptions
                {
                    HeightAuto = !Height.HasValue,
                    MaxDepth = maxDepth,
                    LineHeight = LineHeight,
                    MultiLine = MultiLine,
                    EnableHeaderFilter = EnableHeaderFilter,
                    AggregateFields = aggregateFields.Count > 0 ? aggregateFields : null,
                    TitleError = THProgram._localizer["Sys.Error"],
                    TitleColumnFilter = THProgram._localizer["Sys.ColumnFilter"],
                    TitlePrint = THProgram._localizer["Sys.Print"],
                    // Guaranteed to already be a bare identifier (or null) — a
                    // non-identifier value forces the legacy path before this method
                    // is ever reached (see DetermineGridIslandDecision).
                    DoneFn = string.IsNullOrEmpty(DoneFunc) ? null : DoneFunc,
                    CheckedFn = string.IsNullOrEmpty(CheckedFunc) ? null : CheckedFunc,
                },
                Actions = islandActions.Count > 0 ? islandActions : null,
                ActionMsgs = actionMsgs,
                LocalData = localData,
            };
            return action;
        }

        /// <summary>
        /// Issue #470 Slice O2: island-native replacement for the legacy
        /// <c>BuildToolbarButtons</c>/<c>AddSubButton</c> pair (DataTableTagHelper.cs)
        /// — walks the SAME <c>ListVM.GetGridActions()</c> tree, but instead of
        /// building laytpl-template text (rowBtnStrBuilder) and a
        /// <c>switch(layEvent)</c> JS-text case body (gridBtnEventStrBuilder), it
        /// produces a flat <see cref="GridActionIslandDescriptor"/> list (one entry
        /// per dispatchable leaf action, keyed by its <c>event</c> string — the SAME
        /// value legacy used as the <c>lay-event</c> attribute / switch case label)
        /// plus a SERVER-RENDERED toolbar HTML string.
        ///
        /// Toolbar buttons remain server-rendered markup (mirrors AddSubButton's own
        /// HTML-building almost verbatim — only the onclick attribute is replaced by
        /// data-wtm-click/data-wtm-grid/data-wtm-event, Slice M's ff._buttonAction
        /// infra) — deliberately NOT rebuilt from descriptors client-side, unlike the
        /// row-action column (see AddIslandActionDescriptor's comment on why row
        /// buttons MUST be client-built instead). The resulting string is assigned
        /// directly to layui's <c>toolbar</c> option as literal HTML by
        /// ff._renderGridAction, never routed through a
        /// <c>&lt;script type="text/html"&gt;</c> element — this is what makes the
        /// toolbar immune to the DOMPurify dialog-stripping failure mode documented
        /// in the class doc's O2 section.
        /// </summary>
        private (List<GridActionIslandDescriptor> Actions, StringBuilder ToolbarHtmlRaw, GridActionMsgs? ActionMsgs) BuildIslandActions(string vmQualifiedName)
        {
            var actionCol = ListVM?.GetGridActions();
            var actions = new List<GridActionIslandDescriptor>();
            var toolBarHtmlBuilder = new StringBuilder();

            if (actionCol != null && actionCol.Count > 0)
            {
                var vm = Vm.Model as BaseVM;
                foreach (var item in actionCol)
                {
                    AddIslandActionDescriptor(vmQualifiedName, toolBarHtmlBuilder, actions, vm, item);
                }
            }

            // Issue #470 Slice O2: EnableAnalysis's toolbar button
            // (BuildToolbarButtons, DataTableTagHelper.cs) is intentionally NOT
            // reproduced here — EnableAnalysis forces the whole grid to the legacy
            // path (DetermineGridIslandDecision, invariant 2 unchanged by O2), so
            // this method is never reached with EnableAnalysis == true. The
            // ff._buttonAction.analysisToggle handler (framework_layui.js) exists
            // regardless, ready for a future slice that lifts the EnableAnalysis
            // containment — see that handler's own comment.

            var actionMsgs = actions.Count == 0
                ? null
                : new GridActionMsgs
                {
                    SelectOneRow = THProgram._localizer["Sys.SelectOneRow"],
                    SelectOneRowMax = THProgram._localizer["Sys.SelectOneRowMax"],
                    SelectOneRowMin = THProgram._localizer["Sys.SelectOneRowMin"],
                    InfoTitle = THProgram._localizer["Sys.Info"],
                };

            return (actions, toolBarHtmlBuilder, actionMsgs);
        }

        /// <summary>
        /// Issue #470 Slice O2: per-<see cref="GridAction"/> descriptor/markup build
        /// — the island-native sibling of AddSubButton (DataTableTagHelper.cs
        /// ~1038-1289), reproducing its EXACT structure (auth gate, ShowInRow-first
        /// ordering, HideOnToolBar/ActionsGroup branching, SubActions recursion,
        /// ParameterType-driven dispatch fields, the `_Framework/GetExportExcel`
        /// URL-suffix special case, the `IsExport`-OR-that-same-special-case
        /// `export` flag) so <c>ff._gridToolDispatch</c> (framework_layui.js) is a
        /// byte-for-byte behavioral mirror of AddSubButton's generated JS, just
        /// data-driven instead of text-generated.
        ///
        /// Row-action-column visibility (invariant 5, brief §2 O2): a ShowInRow
        /// action gets ONE descriptor added to <paramref name="actions"/> regardless
        /// of whether it ALSO renders on the toolbar (mirrors AddSubButton's own
        /// ordering — the ShowInRow block runs unconditionally, before the
        /// HideOnToolBar/group branch) — <c>ff.gridTemplets.actionCol</c> filters
        /// <c>actions</c> by <c>showInRow</c> at row-build time, reproducing the
        /// retired laytpl <c>{{# if(d.{visibleField} == true || ... ) }}</c>
        /// conditional with the SAME three-way string/bool comparison. Row buttons
        /// MUST be rebuilt client-side per row (never server-pre-rendered like the
        /// toolbar) because the visibility check and the RemoveRow row-index both
        /// depend on per-row data (<c>d</c>) that only exists once layui renders
        /// each row.
        /// </summary>
        private void AddIslandActionDescriptor(
            string vmQualifiedName,
            StringBuilder toolBarHtmlBuilder,
            List<GridActionIslandDescriptor> actions,
            BaseVM? vm,
            GridAction item,
            bool isSub = false)
        {
            if (!(string.IsNullOrEmpty(item.Url) || vm?.Wtm?.IsUrlPublic(item.Url) == true || vm?.Wtm?.IsAccessable(item.Url) == true ||
                  item.ParameterType == GridActionParameterTypesEnum.AddRow ||
                  item.ParameterType == GridActionParameterTypesEnum.RemoveRow))
            {
                return;
            }

            var eventKey = item.Area + item.ControllerName + item.ActionName + item.QueryString;
            var isRemoveRow = item.ParameterType == GridActionParameterTypesEnum.RemoveRow;

            GridActionIslandDescriptor? rowDescriptor = null;
            if (item.ShowInRow)
            {
                rowDescriptor = new GridActionIslandDescriptor
                {
                    Event = eventKey,
                    Name = item.Name,
                    ButtonClass = item.ButtonClass,
                    ShowInRow = true,
                    RemoveRow = isRemoveRow,
                    // BindVisiableColName is meaningless for RemoveRow (legacy never
                    // wraps the RemoveRow anchor in a laytpl conditional — it is
                    // always shown; only non-RemoveRow row buttons respect it).
                    VisibleField = isRemoveRow ? null : item.BindVisiableColName,
                };
            }

            if (!item.HideOnToolBar)
            {
                if (item.ActionName?.Equals("ActionsGroup") == true && item.SubActions != null && item.SubActions.Count > 0)
                {
                    var subBarBtnStrList = new StringBuilder();
                    foreach (var subItem in item.SubActions)
                    {
                        var subBarBtnStr = new StringBuilder();
                        AddIslandActionDescriptor(vmQualifiedName, subBarBtnStr, actions, vm, subItem, true);
                        if (subBarBtnStr.Length > 0)
                        {
                            subBarBtnStrList.AppendFormat("<dd style=\"padding: 0 0px;margin-bottom:1px;line-height: initial;\">{0}</dd>", subBarBtnStr.ToString());
                        }
                    }
                    if (subBarBtnStrList.Length == 0)
                    {
                        if (rowDescriptor != null) { actions.Add(rowDescriptor); }
                        return;
                    }
                    // Issue #470 Slice O2 review polish: the island button-group
                    // trigger deliberately does NOT carry the legacy "downpanel"
                    // class. That class has zero CSS footprint (it exists purely as
                    // a JS behavior marker, both here and in the legacy
                    // AddSubButton/BuildTableOptionsScript emission — grep the repo,
                    // there is no ".downpanel" stylesheet rule anywhere), so dropping
                    // it costs nothing visually. It is dropped because keeping it was
                    // a ONE-DIRECTIONAL hazard: a coexisting legacy-fallback grid on
                    // the SAME page (e.g. EnableAnalysis, which always takes the
                    // legacy branch per DetermineGridIslandDecision) emits its own
                    // per-render `$(".downpanel").on(...)` script (BuildTableOptionsScript's
                    // hasButtonGroup branch) with an UNSCOPED class selector — it
                    // would happily bind onto this island button too if it still
                    // carried that class, shadowing/racing framework_layui.js's own
                    // document-level `[data-wtm-btngroup]`-scoped delegated handler
                    // depending on setTimeout/DOM-mutation timing. data-wtm-btngroup="1"
                    // (island-only marker) is now the SOLE selector surface for the
                    // open/close binding on both sides — see that binding's comment
                    // in framework_layui.js.
                    toolBarHtmlBuilder.Append($@"<button type=""button"" class=""layui-btn {(string.IsNullOrEmpty(item.ButtonClass) ? "" : $"{item.ButtonClass}")} layui-btn-sm layui-unselect layui-form-select"" data-wtm-btngroup=""1"" style=""z-index:9999;"" id=""btn_{item.ButtonId}"">
                                 <div class=""layui-select-title"" style=""padding-right:20px;"">
                                        {WebUtility.HtmlEncode(item.Name)}
                                 <i class=""layui-edge""></i>
                                 </div>
                                 <dl class=""layui-anim layui-anim-upbit"" style=""top: initial;padding:1px 0px 0px 0px;"" >
                                    {subBarBtnStrList}
                                 </dl>
                                 </button>");
                    if (rowDescriptor != null) { actions.Add(rowDescriptor); }
                    // Group containers never get their own dispatch descriptor —
                    // matches AddSubButton's own early `return;` after building the
                    // dropdown (only leaf SubActions, recursed above, get one).
                    return;
                }

                var icon = $@"<i class=""{item.IconCls}""></i>";
                var substyle = "style=\"" + (isSub ? "width: 100%;" : "") + "\"";
                toolBarHtmlBuilder.Append($@"<a href=""javascript:void(0)"" data-wtm-click=""toolbarButton"" data-wtm-grid=""{Id}"" data-wtm-event=""{WebUtility.HtmlEncode(eventKey)}"" class=""layui-btn {(string.IsNullOrEmpty(item.ButtonClass) ? "" : $"{item.ButtonClass}")} layui-btn-sm"" {substyle}>{icon}{WebUtility.HtmlEncode(item.Name)}</a>");
            }

            var url = item.Url;
            if (item.ControllerName == "_Framework" && item.ActionName == "GetExportExcel")
            {
                url = $"{url}&_DONOT_USE_VMNAME={vmQualifiedName}";
            }

            var descriptor = rowDescriptor ?? new GridActionIslandDescriptor
            {
                Event = eventKey,
                Name = item.Name,
                ButtonClass = item.ButtonClass,
                RemoveRow = isRemoveRow,
            };
            descriptor.ParamType = item.ParameterType;
            descriptor.Url = url;
            descriptor.WhereStr = item.whereStr;
            descriptor.IconCls = item.IconCls;

            if (item.ParameterType == GridActionParameterTypesEnum.AddRow)
            {
                // Issue #470 Slice O2 (brief §2 O2): island JSON escaping replaces
                // the legacy ScriptBlockRegex strip (LayUiRegexes.cs) — parsing into
                // a JsonElement and letting LayuiIslandJson.Serialize's default
                // JavaScriptEncoder re-emit it HTML-encodes any embedded `<`/`>`
                // (e.g. from a stray `<script>` in a formatted cell value) as
                // `<`/`>`, which is STRICTLY safer than the old strip (no
                // silent content loss, and — unlike the strip — it also protects
                // against the enclosing `<script type="application/json">` island
                // itself being closed early by a literal `</script>` substring).
                using var doc = JsonDocument.Parse(ListVM!.GetSingleDataJson(null!, false));
                descriptor.AddRowJson = doc.RootElement.Clone();
            }
            else if (!isRemoveRow)
            {
                if (string.IsNullOrEmpty(item.OnClickFunc))
                {
                    descriptor.Download = item.IsDownload;
                    descriptor.ShowDialog = item.ShowDialog;
                    descriptor.Redirect = item.IsRedirect;
                    descriptor.ForcePost = item.ForcePost;
                    descriptor.Max = item.Max;
                    descriptor.DialogWidth = item.DialogWidth;
                    descriptor.DialogHeight = item.DialogHeight;
                    descriptor.DialogTitle = item.DialogTitle;
                    descriptor.Export = (item.Area == string.Empty && item.ControllerName == "_Framework" && item.ActionName == "GetExportExcel") || item.IsExport;
                    if (item.ShowDialog && !item.IsRedirect)
                    {
                        // Fixed at RENDER time, matching AddSubButton's own
                        // Guid.NewGuid().ToNoSplitString() call — a NEW guid per
                        // dispatch (i.e. regenerated on every click) would break
                        // ff.OpenDialog's window-id bookkeeping (SetCookie
                        // "windowids" tracks open dialogs by this id across the
                        // dialog's whole lifetime, not just the opening click).
                        descriptor.DialogGuid = Guid.NewGuid().ToNoSplitString();
                    }
                }
                else
                {
                    // Guaranteed a bare identifier — a non-identifier OnClickFunc
                    // forces the whole grid to the legacy path before this method is
                    // ever reached (DetermineGridIslandDecision / FindNonIdentifierOnClickFunc).
                    descriptor.OnClickFn = item.OnClickFunc;
                }

                if (!string.IsNullOrEmpty(item.PromptMessage))
                {
                    descriptor.Prompt = item.PromptMessage;
                }
            }

            // Reached only by the HideOnToolBar==true (row-only) and the
            // non-group-toolbar-button paths — the ActionsGroup branch above
            // always returns before this point, adding `rowDescriptor` (if any)
            // itself. Add exactly once, whether `descriptor` is the reused
            // `rowDescriptor` (ShowInRow merged with dispatch fields) or a fresh
            // dispatch-only descriptor (ShowInRow == false).
            actions.Add(descriptor);
        }

        /// <summary>
        /// Descriptor mirror of <see cref="BuildColumns"/> — walks the SAME
        /// (idempotently cached) <c>ListVM.GetHeaders()</c> tree, but instead of
        /// baking each column's <c>Templet</c> into a raw JS function string
        /// (getTemplate/GetRichTemplate), it captures the STRUCTURED descriptor data
        /// those functions were built from. <c>ff.gridTemplets</c>
        /// (framework_layui.js) rebuilds the equivalent function client-side from
        /// this descriptor — see the class doc for the templet-registry rationale.
        /// Does NOT touch <c>NeedShowTotal</c> beyond a same-value re-OR (the field
        /// is already correctly set by the earlier, unconditional
        /// <c>BuildColumns()</c> call every Process() path makes).
        /// </summary>
        private List<List<LayuiColumnDescriptor>> BuildColumnDescriptors(int maxDepth)
        {
            // ListVM is guaranteed non-null here — Process() throws before either
            // Build*Script path is reached if it is null (same invariant BuildColumns()
            // relies on for its own ListVM?.GetHeaders() call).
            var rawCols = ListVM!.GetHeaders();
            List<List<LayuiColumnDescriptor>> layuiCols = [];

            List<LayuiColumnDescriptor> tempCols = [];
            layuiCols.Add(tempCols);
            if (!HiddenCheckbox)
            {
                var checkboxHeader = new LayuiColumnDescriptor
                {
                    Type = LayuiColumnTypeEnum.Checkbox,
                    LAY_CHECKED = CheckedAll,
                    Rowspan = maxDepth,
                    Fixed = GridColumnFixedEnum.Left,
                    UnResize = true,
                };
                if (LineHeight != null)
                {
                    checkboxHeader.Style = $"height:{LineHeight}px";
                }
                tempCols.Add(checkboxHeader);
            }
            if (!HiddenGridIndex)
            {
                var gridIndex = new LayuiColumnDescriptor
                {
                    Type = LayuiColumnTypeEnum.Numbers,
                    Rowspan = maxDepth,
                    Fixed = GridColumnFixedEnum.Left,
                    UnResize = true,
                };
                if (LineHeight != null)
                {
                    gridIndex.Style = $"height:{LineHeight}px";
                }
                tempCols.Add(gridIndex);
            }

            List<IGridColumn<TopBasePoco>> nextCols = [];
            generateColHeaderDescriptors(rawCols, nextCols, tempCols, maxDepth, 0);
            if (nextCols.Count > 0)
            {
                CalcChildColDescriptors(layuiCols, nextCols, maxDepth, 1);
            }

            if (layuiCols.Count > 0 && layuiCols[0].Count > 0)
            {
                layuiCols[0][0].TotalRowText = ListVM?.TotalText;
            }

            return layuiCols;
        }

        private void CalcChildColDescriptors(List<List<LayuiColumnDescriptor>> layuiCols, List<IGridColumn<TopBasePoco>> rawCols, int maxDepth, int depth)
        {
            List<LayuiColumnDescriptor> tempCols = [];
            layuiCols.Add(tempCols);

            List<IGridColumn<TopBasePoco>> nextCols = [];
            generateColHeaderDescriptors(rawCols, nextCols, tempCols, maxDepth, depth);

            if (nextCols.Count > 0)
            {
                CalcChildColDescriptors(layuiCols, nextCols, maxDepth, depth + 1);
            }
        }

        private void generateColHeaderDescriptors(
            IEnumerable<IGridColumn<TopBasePoco>> rawCols,
            List<IGridColumn<TopBasePoco>> nextCols,
            List<LayuiColumnDescriptor> tempCols,
            int maxDepth, int depth)
        {
            var leftCols = new List<IGridColumn<TopBasePoco>>();
            var midCols = new List<IGridColumn<TopBasePoco>>();
            var rightCols = new List<IGridColumn<TopBasePoco>>();
            foreach (var col in rawCols)
            {
                if (col.Fixed == GridColumnFixedEnum.Left) leftCols.Add(col);
                else if (col.Fixed == GridColumnFixedEnum.Right) rightCols.Add(col);
                else midCols.Add(col);
            }
            generateColHeaderCoreDescriptors(leftCols, nextCols, tempCols, maxDepth, depth);
            generateColHeaderCoreDescriptors(midCols, nextCols, tempCols, maxDepth, depth);
            generateColHeaderCoreDescriptors(rightCols, nextCols, tempCols, maxDepth, depth);
        }

        private void generateColHeaderCoreDescriptors(
            IEnumerable<IGridColumn<TopBasePoco>> rawCols,
            List<IGridColumn<TopBasePoco>> nextCols,
            List<LayuiColumnDescriptor> tempCols,
            int maxDepth, int depth)
        {
            string random = System.Guid.NewGuid().ToString().Replace("-", "");

            foreach (var item in rawCols)
            {
                var resolvedFixed = item.Fixed;
                if (item.Field != null && _fixedLeftFieldSet?.Contains(item.Field) == true)
                    resolvedFixed = GridColumnFixedEnum.Left;
                else if (item.Field != null && _fixedRightFieldSet?.Contains(item.Field) == true)
                    resolvedFixed = GridColumnFixedEnum.Right;

                var tempCol = new LayuiColumnDescriptor
                {
                    Title = item.Title,
                    Field = item.Field,
                    Width = item.Width,
                    Sort = item.Sort,
                    Fixed = resolvedFixed,
                    Align = item.Align,
                    Event = item.Event,
                    UnResize = item.UnResize,
                    Hide = item.Hide,
                    ShowTotal = item.ShowTotal
                };

                if (DisableColumnResize && tempCol.Type == null && tempCol.UnResize != false)
                    tempCol.UnResize = true;

                if (LineHeight != null && item.Fixed.HasValue)
                {
                    tempCol.Style = $"height:{LineHeight}px";
                }

                if ((string.IsNullOrEmpty(ListVM.DetailGridPrix) == true && string.IsNullOrEmpty(item.Field) == false) || item.Field == "BatchError")
                {
                    if (item.RichColumnType != GridRichColumnTypeEnum.Default)
                    {
                        tempCol.Templet = BuildRichTempletDescriptor(item.Field!, item.RichColumnType,
                            item.CurrencyFormat, item.TagColor, item.ImageSize, item.CurrencyCodeField);
                    }
                    else
                    {
                        var isBoolColumn = item.FieldType == typeof(bool) || item.FieldType == typeof(bool?);
                        var hasFormat = item.HasFormat() || isBoolColumn;
                        tempCol.Templet = new GridTempletDescriptor
                        {
                            Tpl = isBoolColumn ? "bool" : "plain",
                            Field = item.Field,
                            Random = random,
                            HasFormat = hasFormat,
                            EncodeFormat = item.EncodeFormat,
                        };
                    }
                }

                switch (item.ColumnType)
                {
                    case GridColumnTypeEnum.Space:
                        tempCol.Type = LayuiColumnTypeEnum.Space;
                        break;
                    case GridColumnTypeEnum.Action:
                        // Issue #470 Slice O2: the `#{ToolBarId}` laytpl-<script>-
                        // selector form (O1) is retired — 'actionCol' is a MARKER
                        // descriptor only (no extra fields needed here): ff._renderGridAction
                        // special-cases this tpl name at column-rebuild time, passing it
                        // the grid's gridId + the SAME action.gridActions list (filtered to
                        // showInRow entries) that also drives ff._gridToolDispatch — a
                        // single source of truth instead of duplicating the row-action
                        // list onto every column descriptor.
                        tempCol.Templet = new GridTempletDescriptor { Tpl = "actionCol" };
                        break;
                }
                if (item.Children != null && item.Children.Any())
                {
                    tempCol.Colspan = item.ChildrenLength;
                }
                if (maxDepth > 1 && (item.Children == null || !item.Children.Any()))
                {
                    if (maxDepth - depth > 1)
                    {
                        tempCol.Rowspan = maxDepth - depth;
                    }
                }
                tempCols.Add(tempCol);
                if (item.Children != null && item.Children.Any())
                    nextCols.AddRange(item.Children);
            }
        }

        private static GridTempletDescriptor BuildRichTempletDescriptor(
            string field,
            GridRichColumnTypeEnum richType,
            string? currencyFormat,
            string? tagColor,
            int? imageSize,
            string? currencyCodeField)
        {
            return richType switch
            {
                GridRichColumnTypeEnum.Progress => new GridTempletDescriptor { Tpl = "progress", Field = field },
                GridRichColumnTypeEnum.Tag => new GridTempletDescriptor { Tpl = "tag", Field = field, TagColor = tagColor },
                GridRichColumnTypeEnum.Image => new GridTempletDescriptor { Tpl = "image", Field = field, ImageSize = imageSize },
                GridRichColumnTypeEnum.Currency => string.IsNullOrEmpty(currencyCodeField)
                    ? new GridTempletDescriptor { Tpl = "currency", Field = field, CurrencyFormat = currencyFormat }
                    : new GridTempletDescriptor { Tpl = "currencyRow", Field = field, CurrencyCodeField = currencyCodeField },
                _ => new GridTempletDescriptor { Tpl = "plain", Field = field, HasFormat = false },
            };
        }
    }

    // ── Island DTOs ──────────────────────────────────────────────────────────
    // All 'renderGrid' payload shapes. ff._renderGridAction (framework_layui.js)
    // is the sole consumer; ff._normalizeIslandPayload wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects. Not part of the public
    // API surface — internal, same pattern as every other #470 slice's *IslandAction
    // DTOs (RenderSelectIslandAction, RenderTransferIslandAction, etc.).

    internal sealed class RenderGridIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "renderGrid";

        [JsonPropertyName("gridId")]
        public string? GridId { get; set; }

        [JsonPropertyName("tableJsVar")]
        public string? TableJsVar { get; set; }

        [JsonPropertyName("elem")]
        public string? Elem { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("text")]
        public GridTextOptions? Text { get; set; }

        [JsonPropertyName("request")]
        public GridRequestOptions? Request { get; set; }

        [JsonPropertyName("toolbar")]
        public string? Toolbar { get; set; }

        [JsonPropertyName("defaultToolbar")]
        public List<string>? DefaultToolbar { get; set; }

        [JsonPropertyName("totalRow")]
        public bool TotalRow { get; set; }

        [JsonPropertyName("where")]
        public JsonElement? Where { get; set; }

        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("loading")]
        public bool? Loading { get; set; }

        [JsonPropertyName("page")]
        public GridPageOptions? Page { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("limits")]
        public int[]? Limits { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("heightMode")]
        public string? HeightMode { get; set; }

        [JsonPropertyName("heightValue")]
        public int? HeightValue { get; set; }

        [JsonPropertyName("cols")]
        public List<List<LayuiColumnDescriptor>>? Cols { get; set; }

        [JsonPropertyName("skin")]
        public string? Skin { get; set; }

        [JsonPropertyName("even")]
        public bool? Even { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("exportFileName")]
        public string? ExportFileName { get; set; }

        [JsonPropertyName("enableClientExport")]
        public bool EnableClientExport { get; set; }

        [JsonPropertyName("isInSelector")]
        public bool IsInSelector { get; set; }

        [JsonPropertyName("detailGridPrix")]
        public string? DetailGridPrix { get; set; }

        [JsonPropertyName("searchPanelId")]
        public string? SearchPanelId { get; set; }

        [JsonPropertyName("fieldPre")]
        public string? FieldPre { get; set; }

        [JsonPropertyName("autoSearch")]
        public bool AutoSearch { get; set; }

        [JsonPropertyName("mobileLayout")]
        public bool MobileLayout { get; set; }

        [JsonPropertyName("done")]
        public GridDoneOptions? Done { get; set; }

        // Issue #470 Slice O2 additions ─────────────────────────────────────

        /// <summary>
        /// Server-rendered toolbar button markup (mirrors AddSubButton's own HTML
        /// build, onclick replaced by data-wtm-click/data-wtm-grid/data-wtm-event).
        /// <c>ff._renderGridAction</c> assigns this DIRECTLY to layui's
        /// <c>toolbar</c> option as a literal HTML string (wrapped in one extra
        /// <c>&lt;div&gt;</c> so jQuery's <c>.html()</c> unwrap semantics land on
        /// the SAME <c>&lt;div id="{gridId}buttons"&gt;</c> the legacy
        /// <c>&lt;script type="text/html"&gt;</c> selector form produced) — never a
        /// selector, never a <c>&lt;script type="text/html"&gt;</c> DOM element, so
        /// it cannot be stripped by DOMPurify in dialog contexts (class doc's O2
        /// section). Deliberately NOT rebuilt from <see cref="Actions"/>
        /// client-side, unlike the row-action column: the toolbar's structure
        /// (button-group nesting, icon markup, HideOnToolBar filtering) is exactly
        /// what AddSubButton already computes server-side, so re-deriving it in JS
        /// would duplicate that logic for no benefit — only the ROW column needs a
        /// client builder, because row content depends on per-row data that does
        /// not exist until layui renders each row.
        /// </summary>
        [JsonPropertyName("toolbarHtml")]
        public string? ToolbarHtml { get; set; }

        // Issue #470 Slice O2 CRITICAL FIX (review-caught regression): this field
        // was originally serialized as "actions" — but ff._normalizeIslandPayload
        // (framework_layui.js) checks `Array.isArray(parsed.actions)` BEFORE it
        // checks `parsed.type`, to recognize the pre-existing BATCH island shape
        // {"actions":[{type:...},...]} emitted by DialogInitTagHelper/
        // FormTagHelper. Since renderGrid's OWN descriptor array is ALSO named
        // `actions`, the entire renderGrid payload was misclassified as an
        // already-batched payload and passed through unwrapped — DispatchAction
        // then iterated the descriptor list looking for a `.type` field none of
        // them have, so `case 'renderGrid'` was NEVER reached and the grid never
        // rendered. Renamed to "gridActions" to eliminate the collision (never
        // rename back to "actions" without also re-verifying
        // ff._normalizeIslandPayload's shape detection). Every JS reader of this
        // field (ff._renderGridAction, ff._buildGridActionRegistry) must read
        // `action.gridActions`, not `action.actions`.
        [JsonPropertyName("gridActions")]
        public List<GridActionIslandDescriptor>? Actions { get; set; }

        [JsonPropertyName("actionMsgs")]
        public GridActionMsgs? ActionMsgs { get; set; }

        // Issue #470 Slice O3: the UseLocalData payload — same shape/content as
        // legacy's ListVM.GetDataJson() output, parsed into structured JSON.
        // Deliberately named `localData` (an OBJECT-typed member holding a
        // nested JSON array), NOT `actions`/`gridActions` — see the
        // <see cref="Actions"/> comment above for the full O2-collision incident
        // this naming choice avoids repeating. ff._renderGridAction hands this
        // straight to ff.LoadLocalData(gridId, opt, action.localData,
        // isNormalTable) — the SAME framework function the legacy inline
        // `ff.LoadLocalData(...)` call invokes, so cache-mutation/index-rewrite
        // behavior is byte-for-byte shared between the two paths, not
        // reimplemented. Null for every non-UseLocalData grid (the overwhelming
        // majority) — DefaultIgnoreCondition.WhenWritingNull (_jsonOptions) omits
        // the property entirely rather than serializing `"localData":null`.
        [JsonPropertyName("localData")]
        public JsonElement? LocalData { get; set; }
    }

    internal sealed class GridTextOptions
    {
        [JsonPropertyName("none")]
        public string? None { get; set; }
    }

    internal sealed class GridRequestOptions
    {
        [JsonPropertyName("pageName")]
        public string PageName { get; set; } = "Page";

        [JsonPropertyName("limitName")]
        public string LimitName { get; set; } = "Limit";
    }

    internal sealed class GridPageOptions
    {
        [JsonPropertyName("rpptext")]
        public string? Rpptext { get; set; }

        [JsonPropertyName("totaltext")]
        public string? Totaltext { get; set; }

        [JsonPropertyName("recordtext")]
        public string? Recordtext { get; set; }

        [JsonPropertyName("gototext")]
        public string? Gototext { get; set; }

        [JsonPropertyName("pagetext")]
        public string? Pagetext { get; set; }

        [JsonPropertyName("oktext")]
        public string? Oktext { get; set; }
    }

    internal sealed class GridDoneOptions
    {
        [JsonPropertyName("heightAuto")]
        public bool HeightAuto { get; set; }

        [JsonPropertyName("maxDepth")]
        public int MaxDepth { get; set; }

        [JsonPropertyName("lineHeight")]
        public int? LineHeight { get; set; }

        [JsonPropertyName("multiLine")]
        public bool MultiLine { get; set; }

        [JsonPropertyName("enableHeaderFilter")]
        public bool EnableHeaderFilter { get; set; }

        [JsonPropertyName("aggregateFields")]
        public List<string>? AggregateFields { get; set; }

        [JsonPropertyName("titleError")]
        public string? TitleError { get; set; }

        [JsonPropertyName("titleColumnFilter")]
        public string? TitleColumnFilter { get; set; }

        [JsonPropertyName("titlePrint")]
        public string? TitlePrint { get; set; }

        [JsonPropertyName("doneFn")]
        public string? DoneFn { get; set; }

        [JsonPropertyName("checkedFn")]
        public string? CheckedFn { get; set; }
    }

    internal sealed class LayuiColumnDescriptor
    {
        [JsonPropertyName("LAY_CHECKED")]
        public bool? LAY_CHECKED { get; set; }

        [JsonPropertyName("toolbar")]
        public string? Toolbar { get; set; }

        [JsonPropertyName("type")]
        public LayuiColumnTypeEnum? Type { get; set; }

        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("colspan")]
        public int? Colspan { get; set; }

        [JsonPropertyName("rowspan")]
        public int? Rowspan { get; set; }

        [JsonPropertyName("sort")]
        public bool? Sort { get; set; }

        [JsonPropertyName("fixed")]
        public GridColumnFixedEnum? Fixed { get; set; }

        [JsonPropertyName("align")]
        public GridColumnAlignEnum? Align { get; set; }

        [JsonPropertyName("unresize")]
        public bool? UnResize { get; set; }

        [JsonPropertyName("hide")]
        public bool? Hide { get; set; }

        [JsonPropertyName("style")]
        public string? Style { get; set; }

        [JsonPropertyName("totalRow")]
        public bool? ShowTotal { get; set; }

        [JsonPropertyName("totalRowText")]
        public string? TotalRowText { get; set; }

        [JsonPropertyName("templet")]
        public GridTempletDescriptor? Templet { get; set; }
    }

    /// <summary>
    /// Client-registered templet descriptor (brief §2 O1 point 1 / §3): the
    /// framework-internal templet registry replacement for a raw JS function
    /// string. <c>tpl</c> selects the <c>ff.gridTemplets[tpl]</c> builder
    /// (framework_layui.js) — 'plain' | 'bool' | 'progress' | 'tag' | 'image' |
    /// 'currency' | 'currencyRow' | 'actionCol'. Issue #470 Slice O2: Action
    /// columns (GridColumnTypeEnum.Action) now DO get a templet descriptor —
    /// <c>{tpl:'actionCol'}</c> (see <c>generateColHeaderCoreDescriptors</c>'s
    /// Action case) — retiring the legacy <c>toolbar: '#{ToolBarId}'</c> column
    /// property for island grids. No extra fields are carried on the descriptor
    /// itself for 'actionCol': <c>ff._renderGridAction</c> special-cases it at
    /// column-rebuild time, supplying the gridId + the SAME
    /// <see cref="RenderGridIslandAction.Actions"/> list (filtered to
    /// <c>showInRow</c> entries) that also drives <c>ff._gridToolDispatch</c>.
    /// </summary>
    internal sealed class GridTempletDescriptor
    {
        [JsonPropertyName("tpl")]
        public string? Tpl { get; set; }

        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("random")]
        public string? Random { get; set; }

        [JsonPropertyName("hasFormat")]
        public bool? HasFormat { get; set; }

        [JsonPropertyName("encodeFormat")]
        public bool? EncodeFormat { get; set; }

        [JsonPropertyName("tagColor")]
        public string? TagColor { get; set; }

        [JsonPropertyName("imageSize")]
        public int? ImageSize { get; set; }

        [JsonPropertyName("currencyFormat")]
        public string? CurrencyFormat { get; set; }

        [JsonPropertyName("currencyCodeField")]
        public string? CurrencyCodeField { get; set; }
    }

    /// <summary>
    /// Issue #470 Slice O2: one flat entry per dispatchable <see cref="GridAction"/>
    /// leaf, keyed by <c>event</c> (= Area+ControllerName+ActionName+QueryString —
    /// the SAME string legacy used as the <c>lay-event</c> attribute / switch case
    /// label). Serves DOUBLE duty, matching the #470 comment-18118 design brief §2
    /// O2's unified descriptor shape:
    ///   - tool DISPATCH (<c>ff._gridToolDispatch</c>, framework_layui.js) — every
    ///     field except <see cref="ShowInRow"/>/<see cref="VisibleField"/>/
    ///     <see cref="IconCls"/> reproduces one branch of AddSubButton's decision
    ///     tree (DataTableTagHelper.cs ~1130-1289);
    ///   - ROW rendering (<c>ff.gridTemplets.actionCol</c>) — entries with
    ///     <see cref="ShowInRow"/> true are filtered out (in declaration order) and
    ///     rebuilt into per-row anchor HTML, using <see cref="Name"/>/
    ///     <see cref="ButtonClass"/>/<see cref="VisibleField"/>/<see cref="RemoveRow"/>.
    /// A pure ActionsGroup container (brief's <c>group:[…]</c>) never gets an entry
    /// of its own — only its SubActions do, added by the SAME recursive
    /// AddIslandActionDescriptor call that walks the group — so this list is
    /// intentionally flat, not tree-shaped; the group's own toolbar markup (the
    /// dropdown `&lt;button&gt;`/`&lt;dl&gt;`) is server-rendered directly into
    /// <see cref="RenderGridIslandAction.ToolbarHtml"/> instead (see that field's
    /// doc for why the toolbar is not client-rebuilt from descriptors the way the
    /// row-action column is).
    ///
    /// <see cref="Redirect"/> is a TOP-LEVEL field (not nested under a `dialog`
    /// object, despite the brief's illustrative `dialog:{w,h,title,max,redirect}`
    /// grouping) because AddSubButton branches on <c>IsRedirect</c> both INSIDE and
    /// OUTSIDE the <c>ShowDialog</c> branch (with a different <c>newwindow</c> flag
    /// each time — see <c>ff._gridToolDispatch</c>'s <c>run()</c> closure) — a
    /// nested shape would obscure that the same flag governs both branches.
    /// </summary>
    internal sealed class GridActionIslandDescriptor
    {
        [JsonPropertyName("event")]
        public string? Event { get; set; }

        [JsonPropertyName("paramType")]
        public GridActionParameterTypesEnum ParamType { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("whereStr")]
        public string[]? WhereStr { get; set; }

        [JsonPropertyName("download")]
        public bool Download { get; set; }

        [JsonPropertyName("export")]
        public bool Export { get; set; }

        [JsonPropertyName("showDialog")]
        public bool ShowDialog { get; set; }

        [JsonPropertyName("dialogWidth")]
        public int? DialogWidth { get; set; }

        [JsonPropertyName("dialogHeight")]
        public int? DialogHeight { get; set; }

        [JsonPropertyName("dialogTitle")]
        public string? DialogTitle { get; set; }

        [JsonPropertyName("dialogGuid")]
        public string? DialogGuid { get; set; }

        [JsonPropertyName("max")]
        public bool Max { get; set; }

        [JsonPropertyName("redirect")]
        public bool Redirect { get; set; }

        [JsonPropertyName("forcePost")]
        public bool ForcePost { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("onClickFn")]
        public string? OnClickFn { get; set; }

        [JsonPropertyName("addRowJson")]
        public JsonElement? AddRowJson { get; set; }

        [JsonPropertyName("removeRow")]
        public bool RemoveRow { get; set; }

        [JsonPropertyName("visibleField")]
        public string? VisibleField { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("class")]
        public string? ButtonClass { get; set; }

        [JsonPropertyName("iconCls")]
        public string? IconCls { get; set; }

        [JsonPropertyName("showInRow")]
        public bool ShowInRow { get; set; }
    }

    /// <summary>
    /// Issue #470 Slice O2: the localized strings <c>ff._gridToolDispatch</c>'s
    /// selection guards / PromptMessage confirm need — hoisted ONCE onto the grid
    /// (rather than repeated per <see cref="GridActionIslandDescriptor"/>) since
    /// they never vary across a single grid's actions. Mirrors AddSubButton's own
    /// per-ParameterType localizer lookups (<c>Sys.SelectOneRow</c>/
    /// <c>Sys.SelectOneRowMax</c>/<c>Sys.SelectOneRowMin</c>) plus the PromptMessage
    /// confirm dialog's title (<c>Sys.Info</c>).
    /// </summary>
    internal sealed class GridActionMsgs
    {
        [JsonPropertyName("selectOneRow")]
        public string? SelectOneRow { get; set; }

        [JsonPropertyName("selectOneRowMax")]
        public string? SelectOneRowMax { get; set; }

        [JsonPropertyName("selectOneRowMin")]
        public string? SelectOneRowMin { get; set; }

        [JsonPropertyName("infoTitle")]
        public string? InfoTitle { get; set; }
    }

    /// <summary>
    /// Issue #470 Slice O3: standalone island action replacing the SearcherExpanded
    /// fold <c>&lt;script&gt;</c> BuildTableIslandScript previously duplicated
    /// verbatim from BuildTableOptionsScript (DataTableTagHelper.cs) — a real,
    /// separate <c>&lt;script type="application/json" class="wtm-dialog-init"&gt;</c>
    /// element (not a field nested on <see cref="RenderGridIslandAction"/>), since
    /// each such element is dispatched independently by
    /// <c>ff._consumePageReadyIslands</c> and the brief explicitly calls for "a
    /// foldPanel island action (ff.DispatchAction case)". <c>ff._renderFoldPanelAction</c>
    /// (framework_layui.js) reproduces <c>layui.element.fold(filter, fold)</c>
    /// exactly, reading the <c>lay-filter</c> attribute off
    /// <c>#{searchPanelId} .layui-collapse</c> at dispatch time (unchanged from
    /// legacy — the filter value isn't known until the SearchPanel markup exists).
    /// </summary>
    internal sealed class FoldPanelIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "foldPanel";

        [JsonPropertyName("searchPanelId")]
        public string? SearchPanelId { get; set; }

        [JsonPropertyName("fold")]
        public bool Fold { get; set; }
    }
}
