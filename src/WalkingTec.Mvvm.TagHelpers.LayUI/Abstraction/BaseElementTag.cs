using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public abstract class BaseElementTag : TagHelper
    {
        // Issue #470 Slice N1: same shared WtmUIOptionsHolder BaseFieldTag.UIConfig /
        // BaseButtonTag.UIConfig read — gives TreeContainerTagHelper/ChartTagHelper
        // (and any future BaseElementTag subclass that doesn't go through
        // BaseFieldTag/BaseButtonTag) access to WtmUIOptions.UseSelectIslandRender
        // without a second startup wiring call. Purely additive — no existing
        // BaseElementTag consumer reads this, so it changes nothing for them.
        protected WtmUIOptions UIConfig => WtmUIOptionsHolder.Options;

        // Issue #784 (#470 residual): identifier check for the checkbox/switch/
        // radio ChangeFunc and TextBox ChangeFunc island migrations below — the
        // SAME identifier class every other #470 slice's guarded
        // ff._resolveGuardedWindowFn resolver enforces: a bare JS identifier,
        // nothing else. Uses `\z` (not `$`) as the end anchor for the same
        // reason as SliderTagHelper's/TextBoxTagHelper's #470 Slice H/I
        // _identifierRegex — keeps .NET's and JS's differing `$` semantics in
        // agreement.
        private static readonly Regex _changeFuncIdentifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        // Issue #784 (#470 residual): island DTOs omit null members, matching
        // every other #470 slice's _islandJsonOptions convention.
        private static readonly JsonSerializerOptions _formChangeIslandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public int? Colspan { get; set; }
        public string Id { get; set; }

        public int? Height { get; set; }

        public int? Width { get; set; }

        public string Class { get; set; }

        public string Style { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var preHtml = string.Empty;
            var postHtml = string.Empty;
            if (output.Attributes.ContainsName("id") == false && string.IsNullOrEmpty(Id) == false)
            {
                output.Attributes.SetAttribute("id", Id);
            }
            if (output.Attributes.ContainsName("lay-filter") == false && output.Attributes.ContainsName("id") == true)
            {
                output.Attributes.SetAttribute("lay-filter", $"{output.Attributes["id"].Value}filter");
            }
            if (string.IsNullOrEmpty(Class) == false)
            {
                output.Attributes.SetAttribute("class", Class);
            }
            if (Style == null)
            {
                Style = "";
            }
            if (Width.HasValue)
            {
                Style += $" width:{Width}px;";
            }
            if (Height.HasValue)
            {
                Style += $" min-height:{Height}px;";
            }
            if (string.IsNullOrEmpty(Style) == false)
            {
                if (this is TreeTagHelper )
                {
                    Style += " overflow:auto;";
                }
                TagHelperAttribute prestyle = null;
                if(output.Attributes.TryGetAttribute("style", out prestyle))
                {
                    string s = prestyle.Value.ToString();
                    if(s.EndsWith(";") == false)
                    {
                        s += ";";
                    }
                    Style = s+Style;
                }
                
                output.Attributes.SetAttribute("style",  Style);
            }

            if (context.Items.ContainsKey("ipr"))
            {
                int? ipr = (int?)context.Items["ipr"];
                if (ipr > 0)
                {
                    int col = 12 / ipr.Value;
                    if (Colspan != null)
                    {
                        col *= Colspan.Value;
                    }

                    // Build responsive column class starting with md breakpoint
                    var colClass = $"layui-col-md{col}";

                    if (context.Items.ContainsKey("ipr_sm"))
                    {
                        int? iprSm = (int?)context.Items["ipr_sm"];
                        if (iprSm > 0)
                        {
                            int colSm = 12 / iprSm.Value;
                            if (Colspan != null) colSm *= Colspan.Value;
                            colClass = $"layui-col-sm{colSm} " + colClass;
                        }
                    }

                    if (context.Items.ContainsKey("ipr_xs"))
                    {
                        int? iprXs = (int?)context.Items["ipr_xs"];
                        if (iprXs > 0)
                        {
                            int colXs = 12 / iprXs.Value;
                            if (Colspan != null) colXs *= Colspan.Value;
                            colClass = $"layui-col-xs{colXs} " + colClass;
                        }
                    }

                    preHtml = $@"
<div class=""{colClass}"">
" + preHtml;
                    postHtml += @"
</div>
";
                    output.PreElement.SetHtmlContent(preHtml + output.PreElement.GetContent());
                    output.PostElement.AppendHtml(postHtml);
                }
                if(this is CardTagHelper || this is FormTagHelper || this is ContainerTagHelper || this is TreeContainerTagHelper || this is SearchPanelTagHelper)
                {
                    context.Items.Remove("ipr");
                    context.Items.Remove("ipr_xs");
                    context.Items.Remove("ipr_sm");
                }
            }
            //输出事件
            switch (this)
            {
                case ComboBoxTagHelper item:
                    if(item.MultiSelect == true)
                    {
                        break;
                    }
//                    if (item.LinkField != null || item.LinkId != null)
//                    {
//                        if (!string.IsNullOrEmpty(item.TriggerUrl))
//                        {
//                            output.PostElement.AppendHtml($@"
//<script>
//layui.use(['form'],function(){{
//  var form = layui.form;
//  form.on('select({output.Attributes["lay-filter"].Value})', function(data){{
//    {FormatFuncName(item.ChangeFunc)};
//    ff.ChainChange('{item.TriggerUrl}/'+data.value,data.elem)
//    ff.changeComboIcon(data);
//  }});
//}})
//</script>
//");
//                        }
//                    }
//                    else
//                    {
//                        output.PostElement.AppendHtml($@"
//<script>
//layui.use(['form'],function(){{
//  var form = layui.form;
//  form.on('select({output.Attributes["lay-filter"].Value})', function(data){{
//    {FormatFuncName(item.ChangeFunc)};
//    ff.changeComboIcon(data);
//  }});
//}})
//</script>
//");
//                    }
                    break;
                case CheckBoxTagHelper item:
                    if (string.IsNullOrEmpty(item.ChangeFunc) == false)
                    {
                        output.PostContent.SetHtmlContent(output.PostContent.GetContent().Replace("type=\"checkbox\" ", $"type=\"checkbox\" lay-filter=\"{output.Attributes["lay-filter"].Value}\""));
                        EmitFormChangeWiring(output, "checkbox", output.Attributes["lay-filter"].Value.ToString(), item.ChangeFunc);
                    }
                    break;
                case SwitchTagHelper item:
                    if (string.IsNullOrEmpty(item.ChangeFunc) == false)
                    {
                        EmitFormChangeWiring(output, "switch", output.Attributes["lay-filter"].Value.ToString(), item.ChangeFunc);
                    }
                    break;
                case RadioTagHelper item:
                    if (string.IsNullOrEmpty(item.ChangeFunc) == false)
                    {
                        output.PostContent.SetHtmlContent(output.PostContent.GetContent().Replace("type=\"radio\" ", $"type=\"radio\" lay-filter=\"{output.Attributes["lay-filter"].Value}\""));
                        EmitFormChangeWiring(output, "radio", output.Attributes["lay-filter"].Value.ToString(), item.ChangeFunc);
                    }
                    break;
                case TextBoxTagHelper item:
                    if (string.IsNullOrEmpty(item.SearchUrl) == false)
                    {
                        EmitAutocompleteWiring(output, item.Id, item.SearchUrl, item.TriggerUrl, item.ChangeFunc);
                    }
                    break;
            }

            //如果是submitbutton，则在button前面加入一个区域用来定位输出后台返回的错误
            //if (output.TagName == "button" && output.Attributes.TryGetAttribute("lay-submit", out TagHelperAttribute ta) == true)
            //{
            //    output.PreElement.SetHtmlContent($"<p id='{Id}errorholder'></p>" + output.PreElement.GetContent());
            //}
        }

        public string FormatFuncName(string funcname,bool appendparameter = true)
        {
            if (funcname == null)
            {
                return null;
            }
            var rv = funcname;
            var ind = rv.IndexOf("(");
            if (ind > 0)
            {
                rv = rv.Substring(0, ind);
            }
            if (appendparameter == true)
            {
                rv += "(data)";
            }
            return rv;
        }

        // Issue #999 part (B): FormatFuncName above truncates at the funcname's
        // FIRST "(" and appends "(data)" — for a bare identifier ("myFunc")
        // that is harmless, but for a function literal ("function(v){...}")
        // the slice point is a syntactic accident, not a grammar decision: the
        // text before the "(" is "function", which IS emitted, unmodified,
        // followed by "(data)" — "function(data)" — a hard JS SyntaxError
        // (confirmed with Acornima; see InvocationHelper999BTests.cs).
        //
        // Part (B)'s first design tried to teach FormatFuncName to keep the
        // whole expression when the text before the first "(" is not a plain
        // identifier. Cross-vendor review killed it: "function" itself
        // matches the identifier regex every 3-way island decision in this
        // project uses (^[A-Za-z_$][\w$]*\z) — it is a JS KEYWORD, not an
        // identifier, and that regex has no keyword awareness. The fix would
        // have shipped and changed nothing.
        //
        // FormatFuncInvocation takes the opposite approach: no classification
        // at all. It keeps the caller's expression completely unmodified and
        // wraps it in a grouping operator before appending the call —
        // (expr)(args) — which is valid JavaScript for every shape this
        // project's *Func attributes accept:
        //   - a bare identifier:  (myFunc)(data)   === myFunc(data)
        //   - a dotted reference: (a.b.c)(data)    — the grouping operator
        //     does not strip the Reference a MemberExpression produces, so
        //     `this` stays bound to `a.b`, exactly as `a.b.c(data)` would.
        //   - a function literal, arrow function, or factory-call expression:
        //     the parens make the expression unambiguous regardless of
        //     statement-vs-expression position, so the "function declaration
        //     at statement position" ambiguity (the bug #999 exists to fix)
        //     never arises.
        //
        // Deliberately static, and deliberately NOT implemented in terms of
        // FormatFuncName (or vice versa): the two methods must never share a
        // code path. FormatFuncName's 11 other call sites feed a DECISION (is
        // this a plain identifier?) or an HTML data attribute — never
        // executable JS — and must keep doing exactly that, unchanged. This
        // method's callers feed EXECUTABLE JS and must never also classify
        // it. Collapsing "the string I classify" and "the string I execute"
        // into one value is the root cause #999 exists to fix, not a detail
        // of it — so the two paths stay textually separate here too.
        //
        // Issue #1034: the paren-wrap this method has always emitted —
        // "(expr)(args)" — is valid JS for every shape described above EXCEPT
        // one: it ends an optional chain's short-circuit. "handlers?.onChange"
        // wrapped becomes "(handlers?.onChange)(data)" — the grouping
        // operator forces the ChainExpression to produce its VALUE (undefined,
        // when `handlers` is nullish) before the call happens, so a call that
        // used to short-circuit to a harmless no-op now throws TypeError and
        // aborts the callback. The only way to restore that short-circuit is
        // to emit the call INSIDE the chain — "handlers?.onChange(data)" — but
        // that is only safe for the narrow set of values that are PROVABLY a
        // plain member-access chain; doing it unconditionally (or by a
        // loose textual test like Contains("?.")) is exactly the
        // guess-JS-grammar-from-a-string approach part (B) above exists to
        // eliminate — e.g. an arrow body "(v)=>a?.b" contains "?." but must
        // never be direct-appended. _narrowOptionalChainRegex is a CLOSED
        // language classifier: every string it accepts is provably a legal
        // ES2020 OptionalMemberExpression chain (ASCII identifier atoms
        // joined by "." or "?.", at least one "?.", non-keyword head), so
        // direct-appending the call is a spec-guaranteed-safe transform for
        // every value it matches, and every value it rejects falls back to
        // this method's existing, unchanged, byte-identical wrap output —
        // fail-closed, never fail-open.
        //
        // The end anchor is `\z`, NOT `$` — same repo-wide reason as
        // TextBoxTagHelper.cs:43-55's ANCHOR NOTE (also BaseElementTag.cs's
        // own _changeFuncIdentifierRegex above): .NET's `$` matches at
        // end-of-string OR immediately before a single trailing '\n', while
        // JS has no such trailing-newline exception. Using `$` here would let
        // a value like "a?.b\n" classify as a safe chain server-side while
        // being emitted with the literal trailing newline still attached —
        // client-side JS would then see "a?.b\n(data)", parsed as ASI
        // inserting a statement break, i.e. NOT the call this method thinks
        // it emitted. `\z` matches only the true end of the .NET string, so
        // "a?.b\n" is correctly rejected here and falls back to the wrap.
        //
        // Deliberately ASCII-only atoms (NOT \w — .NET's \w includes Unicode
        // letter categories with no corresponding guarantee on the JS engine
        // parsing the emitted output): a value like "中?.b" is rejected here,
        // falls back to the existing wrap, and is no worse than today.
        private static readonly Regex _narrowOptionalChainRegex =
            new(@"^[A-Za-z_$][A-Za-z0-9_$]*(?:\??\.[A-Za-z_$][A-Za-z0-9_$]*)+\z", RegexOptions.Compiled);

        // Denylist is hygiene, not a correctness requirement: even if
        // "function?.call" slipped past the regex above, BOTH the direct and
        // wrapped forms are equally a SyntaxError for a bare `function`
        // keyword head, so admitting it would not make anything worse. It is
        // kept anyway so "classifier accepts the value" implies "the emitted
        // direct form is always valid JS" with no keyword-shaped exception.
        private static readonly HashSet<string> _jsReservedHeads = new(StringComparer.Ordinal)
        {
            "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "function",
            "if", "implements", "import", "in", "instanceof", "interface", "let", "new", "null",
            "package", "private", "protected", "public", "return", "static", "super", "switch", "this",
            "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield",
        };

        // Issue #1034 T-cls note: this is a 36-entry classification regression
        // matrix (6 true-hits + 30 false-fallbacks; see
        // OptionalChainInvocation1034Tests.cs), run only against the .NET
        // implementation below — it pins THIS regex/denylist's behaviour, it
        // does not (and cannot, from a .NET-only test) prove equivalence with
        // any JS-engine regex. The underlying node transcript this classifier
        // was designed against is recorded in the design doc / PR notes, not
        // re-executed by this suite.
        private static bool IsNarrowOptionalChain(string s)
        {
            // Fast reject: every value in today's real corpus lacks "?." and
            // is rejected here in O(n) with no regex engine invocation at
            // all — this is cheaper than the per-render regex this same
            // class already runs elsewhere (see _changeFuncIdentifierRegex's
            // use above), not a new cost center.
            if (!s.Contains("?."))
            {
                return false;
            }

            if (!_narrowOptionalChainRegex.IsMatch(s))
            {
                return false;
            }

            int cut = s.IndexOfAny(new[] { '.', '?' });
            return !_jsReservedHeads.Contains(s[..cut]);
        }

        // funcExpression is null/empty-safe, mirroring FormatFuncName's null
        // passthrough: several callers (e.g. EmitAutocompleteWiring below,
        // TreeTagHelper/ComboBoxTagHelper's `on:function(data){...}` handler)
        // can legitimately reach this with an unset *Func, and the legacy
        // inline <script> must keep emitting nothing for that slot, not the
        // nonsensical "()(data);".
        public static string FormatFuncInvocation(string funcExpression, string args = "data")
        {
            if (string.IsNullOrEmpty(funcExpression))
            {
                return null;
            }

            // Issue #1034 T-chain mutant note: this whole `if` block (both the
            // condition and its body) can be deleted on its own and the file
            // still compiles — control simply falls through to the
            // byte-identical wrap `return` below for narrow-chain values too.
            // OptionalChainInvocation1034Tests.cs's T-chain assertions are
            // pinned to exactly that deletion: every T-chain test goes red
            // the moment this block is gone, because the emitted text goes
            // back to the wrapped "(expr)(args)" shape for every site.
            if (IsNarrowOptionalChain(funcExpression))
            {
                return $"{funcExpression}({args})";
            }

            return $"({funcExpression})({args})";
        }

        // Issue #784 (#470 residual): shared checkbox/switch/radio ChangeFunc ->
        // layui.form.on(...) wiring — used by the CheckBoxTagHelper/
        // SwitchTagHelper/RadioTagHelper cases above (identical shape apart
        // from the layui form event "kind" name: checkbox/switch/radio). Flag
        // OFF keeps the EXACT legacy inline <script> below, byte-identical to
        // pre-#784 (the #754 gate); flag ON with a plain-identifier ChangeFunc
        // migrates to the eval-free 'formChange' JSON island
        // (ff._renderFormChangeAction, framework_layui.js), resolved
        // client-side through the SAME guarded ff._resolveGuardedWindowFn
        // every other #470 slice's named callback uses. A non-identifier
        // ChangeFunc (flag ON) keeps the legacy inline <script>, loudly
        // deprecated via console.warn — mirroring every other #470 slice's
        // 3-way decision.
        //
        // TRUST BOUNDARY: changeFunc is ALWAYS a compile-time,
        // developer-authored Razor literal (the ChangeFunc TagHelper
        // attribute value) — NEVER field/request/model data, the same trust
        // class as bindSubmit's beforeSubmit (#558) / Slice I's
        // Slider/ColorPicker ChangeFunc.
        private void EmitFormChangeWiring(TagHelperOutput output, string kind, string filter, string changeFunc)
        {
            var changeFuncName = FormatFuncName(changeFunc, false);
            bool isIdentifier = changeFuncName != null && _changeFuncIdentifierRegex.IsMatch(changeFuncName);
            bool useIsland = UIConfig.UseSelectIslandRender && isIdentifier;

            if (useIsland)
            {
                var action = new FormChangeIslandAction { Kind = kind, Filter = filter, ChangeFunc = changeFuncName };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _formChangeIslandJsonOptions)}</script>
");
            }
            else
            {
                // Issue #784: only warn when the flag is actually ON and island
                // render was skipped for a genuine non-identifier ChangeFunc —
                // when the flag is OFF (or ChangeFunc is empty — unreachable
                // here, callers already guard on IsNullOrEmpty) this must
                // contribute ZERO characters to stay byte-identical to the
                // pre-#784 emission (the #754 gate).
                var warn = (UIConfig.UseSelectIslandRender && !isIdentifier)
                    ? $"console.warn('[WTM] {kind} ChangeFunc \\'{JavaScriptEncoder.Default.Encode(changeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470/#784.');\n"
                    : "";
                output.PostElement.AppendHtml($@"
<script>
{warn}layui.use(['form'],function(){{
  var form = layui.form;
  form.on('{kind}({filter})', function(data){{
    {FormatFuncInvocation(changeFunc)};
  }});
}})
</script>
");
            }
        }

        // Issue #784 (#470 residual): shared TextBoxTagHelper SearchUrl/
        // TriggerUrl -> layui.autocomplete.render(...) wiring. Flag OFF keeps
        // the EXACT legacy inline <script> below (either TriggerUrl variant),
        // byte-identical to pre-#784; flag ON with an empty or
        // plain-identifier ChangeFunc migrates to the eval-free
        // 'autocomplete' JSON island (ff._renderAutocompleteAction,
        // framework_layui.js). A non-identifier ChangeFunc (flag ON) keeps
        // the legacy inline <script>, loudly deprecated via console.warn.
        //
        // TRUST BOUNDARY: same class as EmitFormChangeWiring above — ChangeFunc
        // is ALWAYS a compile-time, developer-authored Razor literal, never
        // field/request/model data.
        private void EmitAutocompleteWiring(TagHelperOutput output, string id, string searchUrl, string triggerUrl, string changeFunc)
        {
            var changeFuncName = FormatFuncName(changeFunc, false);
            // A null changeFuncName (ChangeFunc never set) is trivially
            // island-safe — there is nothing to resolve, matching legacy's
            // FormatFuncName(null) => null => empty interpolation => no-op.
            bool isIdentifier = changeFuncName == null || _changeFuncIdentifierRegex.IsMatch(changeFuncName);
            bool useIsland = UIConfig.UseSelectIslandRender && isIdentifier;

            if (useIsland)
            {
                var action = new AutocompleteIslandAction
                {
                    Id = id,
                    Url = searchUrl,
                    TriggerUrl = string.IsNullOrEmpty(triggerUrl) ? null : triggerUrl,
                    ChangeFunc = changeFuncName
                };
                output.PostElement.AppendHtml($@"
<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(action, _formChangeIslandJsonOptions)}</script>
");
            }
            else
            {
                var warn = (UIConfig.UseSelectIslandRender && changeFuncName != null && !isIdentifier)
                    ? $"console.warn('[WTM] TextBox #{JavaScriptEncoder.Default.Encode(id ?? "")} ChangeFunc \\'{JavaScriptEncoder.Default.Encode(changeFunc)}\\' is not a plain identifier — UseSelectIslandRender is ON but island render was skipped for this field; keeping the legacy inline script. See #470/#784.');\n"
                    : "";
                if (!string.IsNullOrEmpty(triggerUrl))
                {
                    output.PostElement.AppendHtml($@"
<script>
{warn}layui.use(['autocomplete'],function(){{
  layui.autocomplete.render({{
    elem: $('#{id}')[0],
    url: '{searchUrl}',
    cache: false,
    template_val: '{{{{d.Value}}}}',
    template_txt: '{{{{d.Text}}}}',
    onselect: function (data) {{
      $('#{id}').val(data.Value);
     {FormatFuncInvocation(changeFunc)};
     ff.ChainChange('{triggerUrl}/'+data.Value, data.elem);
    }}
  }});
}})
</script>
");
                }
                else
                {
                    output.PostElement.AppendHtml($@"
<script>
{warn}layui.use(['autocomplete'],function(){{
  layui.autocomplete.render({{
    elem: $('#{id}')[0],
    url: '{searchUrl}',
    cache: false,
    template_val: '{{{{d.Value}}}}',
    template_txt: '{{{{d.Text}}}}',
    onselect: function (data) {{
      $('#{id}').val(data.Value);
     {FormatFuncInvocation(changeFunc)};
    }}
  }});
}})
</script>
");

                }
            }
        }
    }

    // Issue #784 (#470 residual): DTO for the bare 'formChange' JSON island —
    // checkbox/switch/radio ChangeFunc -> layui.form.on(kind(filter), ...)
    // wiring. kind is always one of "checkbox"/"switch"/"radio" (server-picked
    // from a closed C# switch in BaseElementTag.Process — never request/field
    // data). ff._normalizeIslandPayload (framework_layui.js) wraps this into
    // the {actions:[...]} shape ff.DispatchAction expects; not part of the
    // public API surface.
    internal sealed class FormChangeIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "formChange";

        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        [JsonPropertyName("filter")]
        public string Filter { get; set; }

        // Compile-time, developer-authored Razor literal (ChangeFunc
        // attribute value) — NEVER field/request/model data. Only ever
        // populated when it is already a plain identifier (see
        // EmitFormChangeWiring above); a non-identifier value keeps the
        // legacy inline <script> instead and this DTO is never constructed.
        [JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }
    }

    // Issue #784 (#470 residual): DTO for the bare 'autocomplete' JSON island
    // — TextBoxTagHelper's SearchUrl/TriggerUrl ->
    // layui.autocomplete.render(...) wiring.
    internal sealed class AutocompleteIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "autocomplete";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("triggerUrl")]
        public string TriggerUrl { get; set; }

        [JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }
    }
}
