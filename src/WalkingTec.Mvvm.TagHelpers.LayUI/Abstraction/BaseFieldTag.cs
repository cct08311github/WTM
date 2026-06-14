using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Reflection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public abstract class BaseFieldTag : BaseElementTag
    {
        /// <summary>
        /// Static UI options, initialized at startup via <see cref="SetUIOptions"/>.
        /// Defaults to LayUI-compatible values for zero-config backwards compatibility.
        /// </summary>
        private static WtmUIOptions _uiOptions = new WtmUIOptions();

        /// <summary>Set the global UI options. Called once during app startup.</summary>
        public static void SetUIOptions(WtmUIOptions options) => _uiOptions = options;

        /// <summary>Resolved UI options (always non-null).</summary>
        protected WtmUIOptions UIConfig => _uiOptions;

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
            FormFieldAttribute? _formFieldAttr = _formFieldPro?.GetCustomAttribute<FormFieldAttribute>();
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

            if (Field == null)
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


            output.PreElement.SetHtmlContent(preHtml + output.PreElement.GetContent());
            output.PostElement.AppendHtml(postHtml);
            base.Process(context, output);
        }

    }
}
