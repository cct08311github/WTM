using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:textbox", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class TextBoxTagHelper : BaseFieldTag
    {
        public string EmptyText { get; set; }

        public string SearchUrl { get; set; }

        public ModelExpression LinkField { get; set; }
        public string LinkId { get; set; }
        public string TriggerUrl { get; set; }


        /// <summary>
        /// 文本时触发的js函数，func(data)格式;
        /// <para>
        /// data得到文本;
        /// </para>
        /// </summary>
        public string ChangeFunc { get; set; }
        /// <summary>
        /// 文本修改后焦点离开时触发的js函数，func(data)格式;
        /// <para>
        /// data得到文本;
        /// </para>
        /// </summary>
        public string DoneFunc { get; set; }


        public bool IsPassword { get; set; }

        // Issue #601 (#470-F): identifier check for the ChangeFunc/DoneFunc
        // island migration below — the SAME identifier class framework_layui.js's
        // #558 bindSubmit / #601 bindInput resolver enforces (a bare JS
        // identifier, nothing else).
        //
        // ANCHOR NOTE (adversarial-review fix): the end anchor is `\z`, NOT `$`.
        // In .NET, `$` (even without RegexOptions.Multiline) matches at
        // end-of-string OR immediately before a single trailing '\n', whereas
        // JS `/.../.test()` with `$` matches ONLY the absolute end. Using `$`
        // here would classify a value like "myFunc\n" as an identifier
        // server-side (island emitted, inline attribute suppressed) while the
        // client-side JS resolver — /^[A-Za-z_$][\w$]*$/ — would REJECT it,
        // silently dropping the handler end-to-end. `\z` matches only the
        // absolute end-of-string in .NET, so both engines agree: "myFunc\n" is
        // a non-identifier here, falls back to the legacy inline attribute, and
        // the "never silently drop the developer's handler" invariant holds.
        // (`^` is fine as-is without Multiline; only the end anchor differs.)
        private static readonly Regex _identifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        // Issue #601: System.Text.Json's default encoder escapes '<', '>', and
        // '&', making the JSON payload safe to embed inside a <script> block
        // without risk of </script> injection — same pattern as
        // DateTimeTagHelper's _laydateJsonOptions (#556) and the other
        // #552/#556/#561/#571 islands.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            string placeHolder = EmptyText ?? "";
            string type = IsPassword ? "password":"text";
            output.TagName = "input";
            output.TagMode = TagMode.StartTagOnly;
            output.Attributes.Add("type", type);
            output.Attributes.Add("name", Field.Name);
            output.Attributes.Add("wtm-name", Field.Name);

            // Issue #601 (#470-F): ChangeFunc/DoneFunc used to always emit raw
            // oninput="fn(this.value)" / onchange="fn(this.value)" attributes.
            // ff.SafeHtml (DOMPurify) strips both from every dialog partial and
            // PostForm/BgRequest redraw ('onchange' is explicitly FORBID_ATTR'd
            // and neither is in the #591 ADD_ATTR allowlist — restoring either
            // would reopen the inline-event-handler XSS class #591 closed), so
            // ChangeFunc/DoneFunc silently died whenever a <wt:textbox> rendered
            // inside a dialog. Mirrors FormTagHelper's #561 BeforeSubmit 3-way
            // decision: FormatFuncName(..., appendparameter:false) already
            // truncates any explicit call-argument syntax down to the bare
            // function name (e.g. "foo(x)" -> "foo"), so the ONLY way the
            // resolved name is a non-identifier is a dotted/bracketed/otherwise
            // non-identifier developer literal (e.g. "obj.check") that never had
            // a "(" to truncate. A plain-identifier name migrates to a
            // 'bindInput' wtm-dialog-init JSON island — framework_layui.js
            // resolves it through the SAME #558 guarded window[name] lookup
            // bindSubmit's beforeSubmit uses (identifier regex + denylist +
            // own-property + typeof function; a failed lookup silently skips
            // just that one callback, never throws). A non-identifier name
            // keeps the EXACT legacy inline attribute — never silently dropping
            // the developer's handler, same compat guarantee #561 made.
            string changeFuncName = string.IsNullOrEmpty(ChangeFunc) ? null : FormatFuncName(ChangeFunc, false);
            string doneFuncName = string.IsNullOrEmpty(DoneFunc) ? null : FormatFuncName(DoneFunc, false);
            bool changeIsIdentifier = changeFuncName != null && _identifierRegex.IsMatch(changeFuncName);
            bool doneIsIdentifier = doneFuncName != null && _identifierRegex.IsMatch(doneFuncName);

            if (changeFuncName != null && !changeIsIdentifier)
            {
                output.Attributes.Add("oninput", $"{changeFuncName}(this.value)");
            }
            if (doneFuncName != null && !doneIsIdentifier)
            {
                output.Attributes.Add("onchange", $"{doneFuncName}(this.value)");
            }

            if (string.IsNullOrEmpty(Field?.Model?.ToString()) == false)
            {
                DefaultValue = null;
            }
            if (DefaultValue != null)
            {
                output.Attributes.Add("value", DefaultValue);
            }
            else
            {
                output.Attributes.Add("value", Field?.Model?.ToString());
            }
            output.Attributes.Add("placeholder", placeHolder);
            var maxLenVal = GetMaxLength();
            if (maxLenVal.HasValue)
            {
                output.Attributes.Add("maxlength", maxLenVal.Value.ToString());
            }
            output.Attributes.Add("class", "layui-input");
            if (string.IsNullOrEmpty(SearchUrl) == false)
            {
                output.Attributes.Add("autocomplete", "off");
            }
            if (LinkField != null || string.IsNullOrEmpty(LinkId) == false)
            {
                var linkto = "";
                if (string.IsNullOrEmpty(LinkId))
                {
                    linkto = Core.Utils.GetIdByName(LinkField.ModelExplorer.Container.ModelType.Name + "." + LinkField.Name);
                }
                else
                {
                    linkto = LinkId;
                }
                output.Attributes.Add("wtm-linkto", $"{linkto}");
            }

            // Issue #601: emit the bindInput island only when at least one of
            // ChangeFunc/DoneFunc actually migrated (both absent, or both
            // non-identifier and already emitted as legacy inline attributes
            // above, means no island at all).
            if ((changeFuncName != null && changeIsIdentifier) || (doneFuncName != null && doneIsIdentifier))
            {
                // Issue #578/#585 parity: the SAME ambient
                // context.Items["formid"] key FormTagHelper publishes for
                // descendant tag helpers, used by framework_layui.js's
                // containment gate (formEl.contains(el)) — not a new
                // resolution mechanism.
                var ownerFormId = context.Items.TryGetValue("formid", out var formIdObj)
                    ? formIdObj as string
                    : null;

                var action = new TextBoxBindInputIslandAction
                {
                    ElemId = Id,
                    ChangeFunc = changeIsIdentifier ? changeFuncName : null,
                    DoneFunc = doneIsIdentifier ? doneFuncName : null,
                    FormId = ownerFormId
                };
                var json = LayuiIslandJson.Serialize(action, _islandJsonOptions);
                output.PostElement.AppendHtml(
                    $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");
            }

            base.Process(context, output);
        }

        private int? GetMaxLength()
        {
            var validators = Field?.ModelExplorer?.Metadata?.ValidatorMetadata;
            if (validators == null) return null;
            foreach (var v in validators)
            {
                if (v is System.ComponentModel.DataAnnotations.StringLengthAttribute sl && sl.MaximumLength > 0)
                    return sl.MaximumLength;
            }
            foreach (var v in validators)
            {
                if (v is System.ComponentModel.DataAnnotations.MaxLengthAttribute ml && ml.Length > 0)
                    return ml.Length;
            }
            return null;
        }
    }

    // Issue #601 (#470-F): DTO for the bare (non-wrapped) bindInput JSON
    // island — {"type":"bindInput","elemId":"...","changeFunc":"...",
    // "doneFunc":"...","formId":"..."}. ff._normalizeIslandPayload
    // (framework_layui.js) wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects; not part of the public API surface.
    // changeFunc/doneFunc are ALWAYS compile-time, developer-authored Razor
    // literals (the ChangeFunc/DoneFunc TagHelper attributes) — never
    // field/request/model data — the same trust class as FormTagHelper's
    // beforeSubmit (#558/#561).
    internal class TextBoxBindInputIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "bindInput";

        [System.Text.Json.Serialization.JsonPropertyName("elemId")]
        public string ElemId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("changeFunc")]
        public string ChangeFunc { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("doneFunc")]
        public string DoneFunc { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("formId")]
        public string FormId { get; set; }
    }
}
