using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public abstract class BaseFieldTag : BaseElementTag
    {
        private static readonly ConcurrentDictionary<System.Reflection.MemberInfo, FormFieldAttribute?> _formFieldAttrCache = new();

        /// <summary>
        /// Static UI options, initialized at startup via <see cref="SetUIOptions"/>.
        /// Defaults to LayUI-compatible values for zero-config backwards compatibility.
        /// </summary>
        private static WtmUIOptions _uiOptions = new WtmUIOptions();

        /// <summary>Set the global UI options. Called once during app startup.</summary>
        public static void SetUIOptions(WtmUIOptions options) => _uiOptions = options;

        /// <summary>Resolved UI options (always non-null).</summary>
        protected WtmUIOptions UIConfig => _uiOptions;

        // Issue #552 adversarial-review fix (P0, pre-existing XSS): ColorPickerTagHelper's
        // 'color'/'PredefinedColors' and SliderTagHelper's 'Theme' are persisted field DATA
        // (or a Razor-authored literal that can still originate from a DB-backed default),
        // spliced by layui.colorpicker/slider internally into a raw HTML string
        // (e.g. style="...'+color+'...") that is then parsed via jQuery's $(htmlString) —
        // an attribute/tag breakout in that string is a stored DOM-XSS, independent of
        // whatever wire-format escaping (JSON string escaping, JavaScriptEncoder) is
        // applied to get the value there. Escaping the value for JSON/JS-string-literal
        // syntax is NOT sufficient: once the browser JS-decodes the value back to the
        // original string, layui still concatenates that original string into HTML.
        //
        // The fix is to validate the DECODED value itself against a strict allowlist
        // BEFORE it is placed into either the JSON island opts or the legacy inline
        // <script> — on both paths — rather than relying on escaping alone.
        //
        // Grammar: only '#', digits, letters, '(', ')', ',', '.', '%', whitespace and '-'
        // are permitted. This covers every legitimate color token layui/CSS accepts:
        //   - #RGB / #RRGGBB / #RRGGBBAA hex
        //   - rgb(...) / rgba(...) / hsl(...) / hsla(...) functional notation
        //   - CSS named colors (e.g. "rebeccapurple")
        //   - decimals and percentages inside functional notation (e.g. "0.5", "50%")
        // and structurally EXCLUDES '<', '>', '"', '\'', ';', '{', '}', '`', '/' — the
        // characters needed to break out of an HTML attribute, a <script> block, or a
        // CSS statement — so a value that matches can never carry a tag/attribute/script
        // breakout, regardless of what layui does with it downstream.
        private static readonly Regex _safeColorTokenRegex =
            new(@"^[#0-9A-Za-z(),.%\s-]+$", RegexOptions.Compiled);

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="value"/> is safe to emit,
        /// unescaped-downstream, as a layui color/theme option (island opts or legacy
        /// inline &lt;script&gt;). See the remarks on <see cref="_safeColorTokenRegex"/>
        /// for the grammar and the vulnerability this closes (Issue #552 adversarial
        /// review). Null/empty/whitespace-only values are not safe (nothing to emit).
        /// </summary>
        protected static bool IsSafeColorToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && _safeColorTokenRegex.IsMatch(value);
        }

        protected const string REQUIRED_ATTR_NAME = "field";
        /// <summary>
        /// 绑定的字段 必填
        /// </summary>
        public ModelExpression Field { get; set; }
        public string ItemUrl { get; set; }

        public bool Disabled { get; set; }

        public string Name { get; set; }

        public string LabelText { get; set; }

        public int? LabelWidth { get; set; }

        public bool? Required { get; set; }
        public bool? HideLabel { get; set; }
        private string _id;
        public new string Id
        {
            get
            {
                if (string.IsNullOrEmpty(_id))
                {
                    string rv = string.Empty;
                    if(Field != null)
                    {
                        rv = Utils.GetIdByName(Field?.ModelExplorer.Container.ModelType.Name + "." + Field?.Name);
                    }
                    return  rv;
                }
                else
                {
                    return _id;
                }
            }
            set
            {
                _id = value;
            }
        }

        public string PaddingText { get; set; }

        public string DefaultValue { get; set; }


        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var preHtml = string.Empty;
            var postHtml = string.Empty;
            var requiredDot = string.Empty;
            var layfilter = string.Empty;
            if (output.Attributes.TryGetAttribute("id", out _) == false)
            {
                output.Attributes.SetAttribute("id", Id ?? string.Empty);
            }

            if (output.Attributes.TryGetAttribute("name", out _) == false)
            {
                output.Attributes.SetAttribute("name", string.IsNullOrEmpty(Name) ? Field?.Name : Name);
            }

            // ── [FormField] runtime defaults (STEP 0: before Disabled is evaluated) ──────
            // Resolve the [FormField] attribute early so that ReadonlyOnEdit can flip
            // Disabled=true before the readonly/disabled HTML attribute is emitted below.
            // This block is purely additive: absent attribute → zero change.
            var _formFieldPro = Field?.Metadata.ContainerType.GetSingleProperty(Field?.Metadata.PropertyName);
            FormFieldAttribute? _formFieldAttr = _formFieldPro == null ? null : _formFieldAttrCache.GetOrAdd(_formFieldPro, static mi => mi.GetCustomAttribute<FormFieldAttribute>());
            if (_formFieldAttr != null)
            {
                // ReadonlyOnEdit — flip Disabled only when not already set by the Razor author.
                if (_formFieldAttr.ReadonlyOnEdit && !Disabled)
                {
                    // Edit mode = the form VM is a BaseCRUDVM whose Entity already has a persisted key.
                    if (context.Items.TryGetValue("model", out var modelObj) &&
                        modelObj is IBaseCRUDVM<TopBasePoco> crudVm &&
                        crudVm.Entity?.HasID() == true)
                    {
                        Disabled = true;
                    }
                }
            }
            // ── end [FormField] pre-processing ───────────────────────────────────────────

                if (Disabled )
                {
                if (this is DateTimeTagHelper)
                {
                    output.Attributes.SetAttribute("disabled", string.Empty);
                }
                else
                {
                    output.Attributes.SetAttribute("readonly", string.Empty);
                }
                    output.Attributes.TryGetAttribute("class", out TagHelperAttribute oldclass);
                    output.Attributes.SetAttribute("class", UIConfig.DisabledClass + " " + (oldclass?.Value ?? string.Empty));
                }
            if (output.Attributes.ContainsName("lay-filter") == false && output.Attributes.ContainsName("id") == true)
            {
                layfilter = $"{output.Attributes["id"].Value}filter";
                output.Attributes.SetAttribute("lay-filter", layfilter);
            }
            else
            {
                layfilter = output.Attributes["lay-filter"].Value.ToString();
            }

            // #463: DisplayTagHelper legitimately renders static display-text without a field= binding;
            // exempt it here, mirroring the existing !(this is DisplayTagHelper) special-case on the
            // next guard below. All other field tags still require field=.
            if (Field == null && !(this is DisplayTagHelper))
            {
                throw new InvalidOperationException(
                    $"The 'field' attribute is required on <{GetType().Name.Replace("TagHelper", string.Empty).ToLower()}>. Ensure the field= attribute is set.");
            }
            if (!(this is DisplayTagHelper) && ((Field.Metadata.IsRequired && Field.Name.Contains("[-1]")==false) || Required == true))
            {
                requiredDot = UIConfig.RequiredMarkerHtml;
                output.Attributes.SetAttribute("aria-required", "true");
                if (!(this is UploadTagHelper || this is RadioTagHelper || this is CheckBoxTagHelper || this is MultiUploadTagHelper || this is ColorPickerTagHelper  || this is SliderTagHelper || this is TransferTagHelper)) // 上传组件自定义验证
                {
                    //richtextbox不需要进行必填验证
                    if (output.Attributes["isrich"] == null)
                    {
                        //combobox和tree用xmselect控件的验证
                        if (this is ComboBoxTagHelper combo || this is TreeTagHelper)
                        {
                            var script = $@"
<script>
    window['{this.Id}'].update({{
    layVerify:'required',
    layReqText:'{THProgram._localizer["Validate.{0}required", Field?.Metadata?.DisplayName ?? Field?.Metadata?.Name]}'
}});
</script>
";
                            output.PostElement.AppendHtml(script);
                        }
                        else
                        {
                            output.Attributes.Add("lay-verify", "required");
                            output.Attributes.Add("lay-reqText", $"{THProgram._localizer["Validate.{0}required", Field?.Metadata?.DisplayName ?? Field?.Metadata?.Name]}");
                        }
                    }
                }
            }

            if (LabelText == null)
            {
                // Reuse property info already resolved for [FormField] pre-processing above.
                var pro = _formFieldPro;
                if (pro != null)
                {
                    LabelText = pro.GetPropertyDisplayName();

                    // --- [FormField] Placeholder ---
                    // Apply only when the Razor author did not set an explicit EmptyText value.
                    // Subclasses (TextBoxTagHelper, TextAreaTagHelper, …) add the placeholder
                    // attribute as "" when EmptyText is null; we overwrite the empty/absent case
                    // so that an explicit EmptyText in Razor markup always wins.
                    if (_formFieldAttr?.Placeholder != null &&
                        output.Attributes.TryGetAttribute("placeholder", out var existingPlaceholder) &&
                        string.IsNullOrEmpty(existingPlaceholder?.Value?.ToString()))
                    {
                        output.Attributes.SetAttribute("placeholder", _formFieldAttr.Placeholder);
                    }
                }
                else
                {
                    LabelText = Field?.Metadata.DisplayName ?? Field?.Metadata.PropertyName;
                }
                if (LabelText == null)
                {
                    HideLabel = true;
                }
            }

            if (LabelWidth == null && context.Items.ContainsKey("formlabelwidth"))
            {
                LabelWidth = (int)context.Items["formlabelwidth"];
            }
            //如果不显示label则隐藏

            if (HideLabel != true)
            {
                string lb = "";
                if(LabelText != "") {
                    lb = $"{requiredDot}{LabelText}:";
                }
                string instyle = $"style=\"{(LabelWidth == null || string.IsNullOrEmpty(PaddingText) == false ? "" : "margin-left:" + (LabelWidth + UIConfig.LabelMarginOffset) + "px;")}";
                if(this is DisplayTagHelper)
                {
                    instyle += "width:unset;";
                }
                instyle += "\"";
                preHtml += $@"
<div {(this is DisplayTagHelper ? "style=\"margin-bottom:0px;\"" : "")} class=""layui-form-item layui-form"" lay-filter=""{layfilter}div"">
    <label for=""{Id}"" class=""layui-form-label"" {(LabelWidth == null ? "style='min-height:21px;'" : "style='min-height:21px;width:" + LabelWidth + "px'")}>{lb}</label>
    <div class=""{ (string.IsNullOrEmpty(PaddingText) ? "layui-input-block" : "layui-input-inline")}"" {instyle}>
";
            }
            else
            {
                preHtml += $@"
<div {(this is DisplayTagHelper ? "style=\"margin-bottom:0px;\"" : "")} class=""layui-form-item layui-form"" lay-filter=""{layfilter}div"">
    <div class=""{ (string.IsNullOrEmpty(PaddingText) ? "layui-input-block" : "layui-input-inline")}"" style=""{(this is DisplayTagHelper ? "margin-left:0px;width:unset;" : "margin-left:0px;")}"">
";
            }
            if (string.IsNullOrEmpty(PaddingText))
            {
                postHtml += $@"
    </div>
</div>
";
            }
            else
            {
                postHtml += $@"
    </div>
<div class=""layui-form-mid layui-word-aux"">{PaddingText}</div>
</div>
";

            }


            // ── Feature 1: EnableAutoVerify — lay-verify auto-projection ─────────────
            if (UIConfig.EnableAutoVerify && !(this is DisplayTagHelper) && _formFieldPro != null)
            {
                // Collect tokens already on the element (e.g. "required" set above)
                var existingVerify = output.Attributes.TryGetAttribute("lay-verify", out var lvAttr)
                    ? (lvAttr.Value?.ToString() ?? string.Empty)
                    : string.Empty;
                var tokens = new System.Collections.Generic.HashSet<string>(
                    existingVerify.Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);

                var propAttrs = _formFieldPro.GetCustomAttributes(inherit: true);

                foreach (var attr in propAttrs)
                {
                    if (attr is RequiredAttribute && !tokens.Contains("required"))
                        tokens.Add("required");
                    else if (attr is EmailAddressAttribute && !tokens.Contains("email"))
                        tokens.Add("email");
                    else if (attr is UrlAttribute && !tokens.Contains("url"))
                        tokens.Add("url");
                    else if (attr is PhoneAttribute && !tokens.Contains("phone"))
                    {
                        tokens.Add("phone");
                        var phoneErr = WebUtility.HtmlEncode("Invalid phone number");
                        var phoneScriptClean = "<script>" +
                            "if(typeof layui!=='undefined'){layui.use('form',function(){var f=layui.form;f.verify({phone:[/^[+]?[\\d\\s\\-().]{7,20}$/," +
                            "'" + phoneErr + "']});});}" +
                            "</script>";
                        output.PostElement.AppendHtml(phoneScriptClean);
                    }
                    else if (attr is RegularExpressionAttribute rxAttr && !string.IsNullOrEmpty(rxAttr.Pattern))
                    {
                        var ruleName = "wtmrx_" + Id;
                        if (!tokens.Contains(ruleName))
                        {
                            tokens.Add(ruleName);
                            var errMsg = WebUtility.HtmlEncode(
                                !string.IsNullOrEmpty(rxAttr.ErrorMessage) ? rxAttr.ErrorMessage : "Invalid format");
                            var escapedPattern = rxAttr.Pattern.Replace("\\", "\\\\").Replace("'", "\\'");
                            var rxScript = $"<script>" +
                                $"if(typeof layui!=='undefined'){{layui.use('form',function(){{var f=layui.form;" +
                                $"f.verify({{'{ruleName}':[/{escapedPattern}/,'{errMsg}']}});}});}}" +
                                $"</script>";
                            output.PostElement.AppendHtml(rxScript);
                        }
                    }
                    else if (attr is StringLengthAttribute slAttr)
                    {
                        if (slAttr.MaximumLength > 0)
                            output.Attributes.SetAttribute("maxlength", slAttr.MaximumLength.ToString());
                        if (slAttr.MinimumLength > 0)
                            output.Attributes.SetAttribute("minlength", slAttr.MinimumLength.ToString());
                    }
                    else if (attr is MaxLengthAttribute maxAttr && maxAttr.Length > 0)
                    {
                        output.Attributes.SetAttribute("maxlength", maxAttr.Length.ToString());
                    }
                    else if (attr is MinLengthAttribute minAttr && minAttr.Length > 0)
                    {
                        output.Attributes.SetAttribute("minlength", minAttr.Length.ToString());
                    }
                }

                // Numeric type → append "number" token
                var propType = _formFieldPro.PropertyType;
                var underlyingType = Nullable.GetUnderlyingType(propType) ?? propType;
                if (underlyingType == typeof(int) || underlyingType == typeof(long) ||
                    underlyingType == typeof(decimal) || underlyingType == typeof(double) ||
                    underlyingType == typeof(float) || underlyingType == typeof(uint) ||
                    underlyingType == typeof(short) || underlyingType == typeof(ushort) ||
                    underlyingType == typeof(ulong))
                {
                    if (!tokens.Contains("number"))
                        tokens.Add("number");
                }

                // Write updated lay-verify if we have tokens
                if (tokens.Count > 0)
                {
                    var verifyValue = string.Join(",", tokens);
                    if (output.Attributes.ContainsName("lay-verify"))
                        output.Attributes.SetAttribute("lay-verify", verifyValue);
                    else
                        output.Attributes.Add("lay-verify", verifyValue);
                }
            }
            // ── end Feature 1 ────────────────────────────────────────────────────────

            // ── Feature 2: EnableAria — ARIA wiring ──────────────────────────────────
            if (UIConfig.EnableAria)
            {
                // aria-label: when HideLabel=true, the visible label is not in the DOM.
                // Emit aria-label so screen readers can identify the field.
                if (HideLabel == true && !string.IsNullOrEmpty(LabelText))
                {
                    output.Attributes.SetAttribute("aria-label", WebUtility.HtmlEncode(LabelText));
                }

                // aria-describedby + hint id: give the hint element a stable id
                // and link the input to it so screen readers announce the hint.
                if (!string.IsNullOrEmpty(PaddingText))
                {
                    var hintId = Id + "_hint";
                    output.Attributes.SetAttribute("aria-describedby", hintId);
                    // Rebuild the hint div in postHtml to include the id attribute.
                    postHtml = postHtml.Replace(
                        $"<div class=\"layui-form-mid layui-word-aux\">{PaddingText}</div>",
                        $"<div class=\"layui-form-mid layui-word-aux\" id=\"{WebUtility.HtmlEncode(hintId)}\">{PaddingText}</div>");
                }

                // aria-invalid: default false; client JS (lay-verify) flips to true on validation failure.
                if (!output.Attributes.ContainsName("aria-invalid"))
                {
                    output.Attributes.SetAttribute("aria-invalid", "false");
                }
            }
            // ── end Feature 2 ────────────────────────────────────────────────────────

            output.PreElement.SetHtmlContent(preHtml + output.PreElement.GetContent());
            output.PostElement.AppendHtml(postHtml);
            base.Process(context, output);
        }

    }
}
