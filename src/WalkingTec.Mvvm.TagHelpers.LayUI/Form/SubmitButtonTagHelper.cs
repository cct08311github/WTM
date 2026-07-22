using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:submitbutton", TagStructure = TagStructure.WithoutEndTag)]
    public class SubmitButtonTagHelper : BaseButtonTag
    {
        public string SubmitUrl { get; set; }

        // Issue #470 Slice M: matches a bare no-arg function-call expression only
        // (e.g. "myCheck()"). Deliberately STRICTER than the FormatFuncName +
        // identifier-regex convention Slices J/K/L use for ChangeFunc-style
        // callbacks (which truncate at the first '(' and accept whatever
        // precedes it, discarding the rest). SubmitButtonTagHelper's
        // Click/ConfirmTxt-driven 'innerclick' expression GATES form
        // submission, so silently truncating a compound expression like
        // "a() && b()" down to "a()" would silently change validation-gating
        // semantics — a correctness/security concern, not just a dropped
        // callback arg. Only a SELF-CONTAINED bare no-arg call is treated as
        // island-safe; bare variable references, compound expressions, and
        // calls with arguments all keep the exact legacy inline <script> path.
        private static readonly Regex _bareCallRegex = new(@"^[A-Za-z_$][\w$]*\(\)$", RegexOptions.Compiled);

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            string innerclick = Click;
            string formid = "";
            BaseVM vm = null;
            if (context.Items.ContainsKey("model") == true)
            {
                vm = context.Items["model"] as BaseVM;
            }
            output.Attributes.SetAttribute("type", "submit");
            output.Attributes.SetAttribute(new TagHelperAttribute("lay-submit"));
            if (context.Items.ContainsKey("formid"))
            {
                if(string.IsNullOrEmpty(this.Id) == true){
                    this.Id = Guid.NewGuid().ToString().Replace("-","");
                }
                output.Attributes.SetAttribute("lay-filter", context.Items["formid"] + "filter");
                formid = context.Items["formid"].ToString();
            }
            if (string.IsNullOrEmpty(Text))
            {
                Text = THProgram._localizer["Sys.Submit"];
            }
            if (string.IsNullOrEmpty(Click) == false || string.IsNullOrEmpty(ConfirmTxt) == false)
            {
                // Issue #470 Slice M FIX2: the formid-context Id fallback above
                // (lines 44-46) only runs when context.Items carries "formid".
                // BaseButtonTag.Process's OWN Id fallback (base.cs) does not run
                // until base.Process(context, output) below — i.e. AFTER this
                // whole block, including the JavaScriptEncoder.Encode(this.Id)
                // call a few lines down. A SubmitButtonTagHelper rendered
                // without an explicit Id AND outside a "formid"-bearing context
                // (e.g. not nested in wt:form) would therefore still have a null
                // Id here. That's harmless for the flag-OFF legacy path below
                // (string interpolation/concatenation of a null Id just yields
                // an empty segment), but the flag-ON island path encodes Id via
                // JavaScriptEncoder.Default.Encode, which throws
                // ArgumentNullException on null — an unhandled exception during
                // Razor rendering. Guard here, scoped to UseSelectIslandRender
                // only, so the flag-OFF default path stays byte-identical.
                if (UIConfig.UseSelectIslandRender && string.IsNullOrEmpty(this.Id))
                {
                    this.Id = Guid.NewGuid().ToString().Replace("-", "");
                }
                output.Attributes.SetAttribute("lay-filter", "f_"+this.Id + "filter");

                // Issue #470 Slice M: opt-in (WtmUIOptions.UseSelectIslandRender,
                // default OFF — the SAME flag #470 Slices J/K/L use) delegated
                // data-wtm-submit-* dispatch instead of a generated per-button
                // <script>function f_{Id}Click(){...}</script>. Early-return-style
                // branch so the flag-OFF path below is completely untouched
                // (byte-identical to pre-Slice-M).
                if (UIConfig.UseSelectIslandRender)
                {
                    var trimmedClick = (innerclick ?? "").Trim();
                    var bareCallMatch = trimmedClick.Length > 0 && _bareCallRegex.IsMatch(trimmedClick);
                    bool islandSafe = string.IsNullOrEmpty(innerclick) || bareCallMatch;

                    if (islandSafe)
                    {
                        // Issue #470 Slice M / Issue #784 (#470 residual, completes
                        // Slice M): ff._submitButtonClick(id) is a FIXED framework
                        // function (framework_layui.js) that replays the EXACT SAME
                        // {formid}validate / #{formid}hidesubmit / ff.PostForm
                        // handshake the legacy generated function used, reading it
                        // off data-wtm-submit-* attributes (compile-time,
                        // developer-authored Razor literals — never request/field
                        // data) instead of a per-button generated closure.
                        //
                        // #784 completes the gate: rather than assigning Click to a
                        // string BaseButtonTag.Process would still wrap in its OWN
                        // unconditional `$('#{Id}').on('click',function(){...})`
                        // <script> (leaving that wrapper itself non-eval-free), this
                        // sets data-wtm-click="submit" — BaseButtonTag.Process
                        // recognizes the attribute and skips its wrapper entirely,
                        // letting the document-level delegated listener
                        // (ff._buttonAction.submit) drive the SAME
                        // ff._submitButtonClick(el) handshake, resolved by DOM
                        // element (never `this`/an id string built from developer
                        // text) — same this-binding-safety rationale as before (see
                        // the ConfirmTxt tests): BaseButtonTag.Process's ConfirmTxt
                        // handling wraps clicks in a nested function(index){...}
                        // where `this` is not the clicked button, so resolving by
                        // element/id must never depend on it.
                        // Issue #784 REVIEW FIX (CRITICAL, security): signal
                        // "I already wired my own delegated dispatch" to
                        // BaseButtonTag.Process via context.Items, NOT via the
                        // presence of the data-wtm-click OUTPUT attribute
                        // itself — output.Attributes begins pre-populated
                        // from raw source markup and a developer-authored
                        // `data-wtm-click="..."` attribute on the tag would
                        // otherwise spoof this signal regardless of flag
                        // state. See BaseButtonTag.SubclassDelegatedClickItemKey's
                        // own comment for the full rationale.
                        context.Items[BaseButtonTag.SubclassDelegatedClickItemKey] = true;
                        output.Attributes.SetAttribute("data-wtm-click", "submit");
                        output.Attributes.SetAttribute("data-wtm-submit-formid", WebUtility.HtmlEncode(formid));
                        output.Attributes.SetAttribute("data-wtm-submit-divid", WebUtility.HtmlEncode(vm?.ViewDivId ?? ""));
                        if (bareCallMatch)
                        {
                            var checkFnName = trimmedClick[..^2];
                            output.Attributes.SetAttribute("data-wtm-submit-checkfn", WebUtility.HtmlEncode(checkFnName));
                        }
                    }
                    else
                    {
                        Click = $"f_{this.Id}Click();";
                        var warn = $"console.warn('[WTM] SubmitButtonTagHelper #{Id}: Click \\'{JavaScriptEncoder.Default.Encode(innerclick)}\\' is not a bare no-arg function call — UseSelectIslandRender is ON but island render was skipped for this button; keeping the legacy inline script. See #470 Slice M.');\n";
                        output.PostElement.AppendHtml($@"
<script>
{warn}function f_{this.Id}Click(){{
    var check = {(string.IsNullOrEmpty(innerclick) ? "true" : innerclick)};
    if(check == undefined || check == false){{return false;}}
    try{{
        {formid}validate = false;
        $('#{formid}hidesubmit').trigger('click');
    }}
    catch(e){{ {formid}validate = true;}}
    if({formid}validate == true){{
    ff.PostForm('', '{formid}', '{vm?.ViewDivId}')
    }}
    return false;
}}
</script>
");
                    }
                }
                else
                {
                    Click = $"f_{this.Id}Click();";
                    output.PostElement.AppendHtml($@"
<script>
function f_{this.Id}Click(){{
    var check = {(string.IsNullOrEmpty(innerclick) ? "true" : innerclick)};
    if(check == undefined || check == false){{return false;}}
    try{{
        {formid}validate = false;
        $('#{formid}hidesubmit').trigger('click');
    }}
    catch(e){{ {formid}validate = true;}}
    if({formid}validate == true){{
    ff.PostForm('', '{formid}', '{vm?.ViewDivId}')
    }}
    return false;
}}
</script>
");
                }
            }
            base.Process(context, output);
        }
    }
}
