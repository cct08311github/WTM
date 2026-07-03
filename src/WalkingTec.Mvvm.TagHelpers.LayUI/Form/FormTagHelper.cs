using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:form", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.NormalOrSelfClosing)]
    public class FormTagHelper : BaseElementTag
    {
        protected const string REQUIRED_ATTR_NAME = "vm";
        public const string FORM_ID_PREFIX = "wtForm_";

        private string _id = null;
        public new string Id
        {
            get
            {
                if (string.IsNullOrEmpty(_id))
                {
                    if (Vm?.Model is IBaseVM)
                    {
                        var vm = Vm.Model as IBaseVM;
                        _id = $"{FORM_ID_PREFIX}{vm.UniqueId}";
                    }
                    else if(Vm?.Model is BaseSearcher)
                    {
                        var vm = Vm.Model as BaseSearcher;
                        _id = $"{FORM_ID_PREFIX}{vm.UniqueId}";
                    }
                }
                return _id;
            }
            set
            {
                _id = value;
            }
        }

        /// <summary>
        /// 提交表单的Url
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// 表单绑定的Vm
        /// </summary>
        public ModelExpression Vm { get; set; }

        /// <summary>
        /// 提交前调用的js
        /// </summary>
        public string BeforeSubmit { get; set; }

        /// <summary>
        /// 使用传统表单提交方式提交，而不使用 AJAX 提交
        /// 比如登陆页面，提交后校验成功会跳转其他页面，而不是返会 PartialView
        /// </summary>
        public bool OldPost { get; set; }

        /// <summary>
        /// 设置表单内控件前面label的长度，默认为80
        /// </summary>
        public int? LabelWidth { get; set; }

        // Issue #561 (#470-B slice 2): System.Text.Json's default encoder
        // (JavaScriptEncoder.Default) escapes '<', '>', and '&', making the JSON
        // payload safe to embed inside a <script> block without risk of </script>
        // injection — same pattern as DialogInitTagHelper / DateTimeTagHelper's
        // laydate island.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var novm = true;
            BaseVM baseVM = null;
            BaseSearcher baseSearcher = null;
            if (Vm?.Model is  BaseVM)
            {
                novm = false;
                baseVM = Vm?.Model as BaseVM;
            }
            if(Vm?.Model is BaseSearcher )
            {
                novm = false;
                baseSearcher = Vm?.Model as BaseSearcher;
            }
            if (novm)
            {
                output.TagName = "div";
                output.TagMode = TagMode.StartTagAndEndTag;
                output.Content.SetContent("VM is not set, please check if <wt:form> set the vm field");
                return;
            }
            output.TagName = "form";
            output.Attributes.SetAttribute("id", Id);
            output.Attributes.SetAttribute("class", "layui-form");
            if (!(this is SearchPanelTagHelper))
            {
                output.Attributes.SetAttribute("style", "margin:10px");
                //添加items以便子项可以使用
                if (context.Items.ContainsKey("formid") == false)
                {
                    context.Items.Add("formid", Id);
                }
            }
            output.Attributes.SetAttribute("method", "post");
            output.Attributes.SetAttribute("lay-filter", Id);
            if (LabelWidth != null)
            {
                if (context.Items.ContainsKey("formlabelwidth") == false)
                {
                    context.Items.Add("formlabelwidth", LabelWidth.Value);
                }
            }
            if (context.Items.ContainsKey("model") == false)
            {
                if(baseVM != null)
                {
                    context.Items.Add("model", baseVM);
                }
                else if(baseSearcher != null)
                {
                    context.Items.Add("model", baseSearcher);
                }
            }

            if (string.IsNullOrEmpty(Url) == false)
            {
                output.Attributes.SetAttribute("action", Url);
            }
            else
            {
                //设置提交地址，如果不指定Url，默认为当前页面。如果绑定Vm为BatchVM，将提交地址由BatchXXX变为DoBatchXXX。
                //因为框架默认的Batch本身是一个Post方法，无法使用同名方法处理提交后的工作
                if (Vm.Model is IBaseBatchVM<BaseVM>)
                {
                    output.Attributes.SetAttribute("action",Regex.Replace(baseVM?.CurrentUrl,"/Batch", "/DoBatch", RegexOptions.IgnoreCase) ?? "#");
                }
                else
                {
                    output.Attributes.SetAttribute("action", baseVM?.CurrentUrl ?? baseSearcher?.Wtm?.BaseUrl ?? "#");
                }
            }
            var encodedView = System.Net.WebUtility.HtmlEncode(baseVM?.CurrentView ?? "");
            output.PostContent.AppendHtml($"<input type='hidden' name='FromView' value='{encodedView}' />");

            // Issue #561 (#470-B slice 2): initForm + bindSubmit as a single eval-free
            // JSON action island, replacing the legacy inline ff.RenderForm('{Id}') /
            // layui.form.on('submit(...)') + BeforeSubmit-gate scripts this used to
            // open with. See DialogInitTagHelper for the established
            // </script>-safe-encoding island pattern reused here.
            //
            // initForm reproduces ff.RenderForm(Id) exactly: layui.form.render(null, Id),
            // filtered by the <form lay-filter="{Id}"> attribute set above (NOT the
            // "{Id}filter" identifier used by bindSubmit below — that belongs to the
            // submit BUTTON's lay-filter, a separate identifier by design).
            var islandActions = new List<FormIslandAction>
            {
                new FormIslandAction { Type = "initForm", Filter = Id }
            };

            // Captured BEFORE the legacy "()"-call mutation below. bindSubmit resolves
            // action.beforeSubmit through a plain window[name] lookup (framework_layui.js
            // #558) — it must be exactly the developer's Razor-authored BeforeSubmit
            // identifier, never a JS-call-expression string and never any
            // field/request/user-supplied value.
            var rawBeforeSubmit = string.IsNullOrEmpty(BeforeSubmit) ? null : BeforeSubmit;

            // Only the standard AJAX-submit, non-SearchPanel path binds a
            // submit("{Id}filter") handler. OldPost forms use native form submission
            // (no AJAX intercept needed) and SearchPanel's OldPost click-handler
            // branch below binds on a button click (not a form submit event) and
            // also passes a searchervm argument bindSubmit does not model — neither
            // is expressible as bindSubmit, so both keep their existing inline
            // scripts unchanged.
            bool isAjaxSubmitForm = OldPost == false && !(this is SearchPanelTagHelper);

            // Issue #561 compat fix — 3-way decision on how the submit binding is
            // emitted for the AJAX-submit path. bindSubmit resolves
            // action.beforeSubmit through framework_layui.js's narrow window[name]
            // lookup (#558), which accepts ONLY a bare JS identifier
            // (/^[A-Za-z_$][\w$]*$/). A non-identifier BeforeSubmit (a call
            // expression like "obj.Check()" or a dotted "this.Validate") emitted into
            // the island would be SILENTLY skipped by that guard — dropping the
            // developer's submit gate and letting the form post ungated. Silently
            // dropping a gate is a "never silently change default behaviour"
            // red-line break, so for a non-identifier BeforeSubmit we do NOT migrate
            // the submit binding: we keep the LEGACY inline
            // layui.form.on('submit({Id}filter)') <script> (with the "()"-mutated
            // BeforeSubmit, evaluated as a live JS expression exactly as before), and
            // the island carries initForm only.
            //   - BeforeSubmit absent          -> island: initForm + bindSubmit(no beforeSubmit)
            //   - BeforeSubmit bare identifier -> island: initForm + bindSubmit(beforeSubmit:name)
            //   - BeforeSubmit non-identifier  -> island: initForm only; legacy inline submit <script> kept
            bool beforeSubmitPresent = !string.IsNullOrEmpty(BeforeSubmit);
            bool beforeSubmitIsIdentifier = beforeSubmitPresent &&
                Regex.IsMatch(BeforeSubmit, @"^[A-Za-z_$][\w$]*$");
            bool migrateSubmitToIsland = isAjaxSubmitForm && (!beforeSubmitPresent || beforeSubmitIsIdentifier);
            bool useLegacyInlineSubmit = isAjaxSubmitForm && beforeSubmitPresent && !beforeSubmitIsIdentifier;

            if (migrateSubmitToIsland)
            {
                islandActions.Add(new FormIslandAction
                {
                    Type = "bindSubmit",
                    Filter = $"{Id}filter",
                    BeforeSubmit = rawBeforeSubmit, // null when absent; a bare identifier otherwise (never a non-identifier expression)
                    FormId = Id,
                    DivId = baseVM?.ViewDivId
                });
            }

            var islandJson = JsonSerializer.Serialize(new FormIslandPayload { Actions = islandActions }, _islandJsonOptions);
            output.PostElement.AppendHtml(
                $"<script type=\"application/json\" class=\"wtm-dialog-init\">{islandJson}</script>");

            if(BeforeSubmit != null && BeforeSubmit.Contains("(") == false)
            {
                BeforeSubmit += "()";
            }

            // Issue #561 compat fallback: a non-identifier BeforeSubmit expression
            // cannot be honoured by the island's bindSubmit guard, so this form keeps
            // the exact legacy inline submit binding it had before #561 — the gate
            // ({BeforeSubmit} == false) still runs, evaluated as a live JS expression.
            if (useLegacyInlineSubmit)
            {
                output.PostElement.AppendHtml($@"
<script>
layui.use(['form'],function(){{
  layui.form.on('submit({Id}filter)', function(data){{
    if({BeforeSubmit ?? "true"} == false){{return false;}}
    ff.PostForm('', '{Id}', '{baseVM?.ViewDivId}')
    return false;
  }});
}})
</script>
");
            }

            // 使用传统表单提交方式提交，而不使用 AJAX 提交
            // 比如登陆页面，提交后校验成功会跳转其他页面，而不是返会 PartialView
            //
            // What remains here is the auto-validate handler that drives the hidden
            // #{Id}hidesubmit button (used by SubmitButtonTagHelper's custom-click
            // flow to pre-validate before manually posting). There is no
            // DispatchAction action type that models a per-form global validation
            // flag (var {Id}validate), so this stays a residual inline <script> — the
            // last blocker to fully retiring the eval fallback for a plain dialog.
            // Needed for BOTH the island-migrated and the legacy-inline submit paths.
            if (isAjaxSubmitForm)
            {
                output.PostElement.AppendHtml($@"
<script>
var {Id}validate = false;
layui.use(['form'],function(){{
  layui.form.on('submit({Id}filterAuto)', function(data){{
  {Id}validate = true;
  return false;
  }});
}})
</script>
");
                output.PostContent.AppendHtml($@"
<button class=""layui-hide"" id=""{Id}hidesubmit""  type=""submit"" lay-filter=""{Id}filterAuto"" lay-submit></button>
");

            }

            //如果是 SearchPanel，并且指定了 OldPost，则提交整个表单，而不是只刷新 Grid 数据
            if (OldPost == true && this is SearchPanelTagHelper search)
            {
//                string addhidden = $"var form = $('#{search.Id}');";
//                foreach (var item in search.GridId.Split(','))
//                {
//                    addhidden += $@"
//    for(let f in {item}defaultfilter.where){{
//        form.append(""<input type='hidden' name='Searcher.""+f+""' value='""+{item}defaultfilter.where[f]+""'/>"");
//    }}
//";
//                }
                output.PostElement.AppendHtml($@"
<script>
$('#{search.SearchBtnId}').on('click', function () {{
    if({BeforeSubmit ?? "true"} == false){{return false;}}
    ff.PostForm('', '{Id}', '{baseVM?.ViewDivId ?? baseSearcher?.ViewDivId}','{Vm?.Name}')
    return false;
  }});
</script>
");

            }

            //输出后台返回的错误信息
            if (baseVM?.MSD?.Count > 0)
            {
                output.PostElement.AppendHtml("<script>");
                string firstkey = null;
                foreach (var key in baseVM.MSD.Keys)
                {
                    bool haserror = false;
                    foreach (var error in baseVM.MSD[key])
                    {
                        haserror = true;
                        if (firstkey == null)
                        {
                            firstkey = key;
                        }
                        // TLU-SEC-004: use proper HtmlEncode; the original code used bogus &lg;/&rg;
                        // pseudo-entities that are not valid HTML and do not prevent injection.
                        string temperr = WebUtility.HtmlEncode(error.ErrorMessage);
                        output.PostElement.AppendHtml($@"
$(""#{Id}"").find(""button[type=submit]:first"").parent().prepend(""<div class='layui-input-block' style='text-align:left'><label style='color:red'>{temperr}</label></div>"");
");
                    }
                    if (haserror == true)
                    {
                        output.PostElement.AppendHtml($@"$(""#{Utils.GetIdByName(baseVM.GetType().Name + "." + key)}"").addClass('layui-form-danger');");
                    }
                }
                if (firstkey != null)
                {
                    output.PostElement.AppendHtml($@"$(""#{Utils.GetIdByName(baseVM.GetType().Name + "." + firstkey)}"").focus();");
                }
                output.PostElement.AppendHtml("</script>");
            }
            base.Process(context, output);
        }
    }

    // Issue #561 (#470-B slice 2): DTOs for FormTagHelper's wrapped {"actions":[...]}
    // JSON island — mirrors DialogInitPayload/DialogInitAction's shape (see
    // DialogInitTagHelper) but carries the extra fields the 'bindSubmit'
    // DispatchAction case needs (beforeSubmit/formId/url/divId). Not part of the
    // public API surface.
    internal class FormIslandPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("actions")]
        public List<FormIslandAction> Actions { get; set; } = new();
    }

    internal class FormIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("filter")]
        public string Filter { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("formType")]
        public string FormType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("beforeSubmit")]
        public string BeforeSubmit { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("formId")]
        public string FormId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("divId")]
        public string DivId { get; set; }
    }
}
