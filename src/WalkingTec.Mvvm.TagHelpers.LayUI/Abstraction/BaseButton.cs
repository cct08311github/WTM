using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public enum ButtonSizeEnum { Big, Normal, Small, Mini }
    public enum ButtonThemeEnum { Primary, Normal, Warm, Danger, Disabled }
    public abstract class BaseButtonTag : BaseElementTag
    {
        // Issue #470 Slice M: SubmitButtonTagHelper (and any future BaseButtonTag
        // subclass) gets WtmUIOptions.UseSelectIslandRender via the inherited
        // UIConfig accessor without a second startup wiring call.
        // Issue #470 Slice N1 polish: the accessor now lives once on
        // BaseElementTag (this class's base type); the duplicate declaration
        // that used to shadow it here (CS0108) was removed.

        // Issue #784 (#470 residual): identifier check for the generic
        // (non-SubmitButton) delegated click island below — the SAME bare
        // no-arg-call convention #470 Slice M's SubmitButtonTagHelper
        // established (_bareCallRegex there), duplicated here rather than
        // shared because SubmitButtonTagHelper's own copy has a documented,
        // deliberately-separate rationale (see that file's comment) and this
        // one must be independently reviewable.
        private static readonly Regex _clickBareCallRegex = new(@"^[A-Za-z_$][\w$]*\(\)$", RegexOptions.Compiled);

        // Issue #784 REVIEW FIX (CRITICAL, security): subclass-to-base
        // "I already fully wired my own delegated dispatch attributes"
        // signaling MUST NOT be derived from output.Attributes — that
        // collection begins PRE-POPULATED by the Razor TagHelper runtime
        // with every literal HTML attribute present on the SOURCE markup tag
        // (the same reason BaseElementTag/BaseFieldTag use
        // output.Attributes.ContainsName("id") to detect developer-supplied
        // pass-through attributes). A literal `data-wtm-click="..."`
        // attribute on a <wt:button>/<wt:linkbutton>/etc. tag — written by a
        // developer for any unrelated reason — would therefore satisfy the
        // OLD `output.Attributes.ContainsName("data-wtm-click")` check
        // regardless of WtmUIOptions.UseSelectIslandRender, silently
        // skipping the legacy click wrapper even with the flag OFF (breaking
        // HARD INVARIANT (1), the #754 byte-identical-when-off guarantee)
        // and, even with the flag ON, silently mis-wiring a button that never
        // went through SubmitButtonTagHelper's own island-safe branch.
        // context.Items is scoped to the current tag-processing pipeline and
        // can NEVER be populated from raw HTML source — only from TagHelper
        // C# code — so it is a collision-proof channel no developer-authored
        // attribute can ever spoof. SubmitButtonTagHelper sets this key
        // immediately before calling base.Process; nothing else may set it.
        internal const string SubclassDelegatedClickItemKey = "__wtm784SubclassDelegatedClick";

        /// <summary>
        /// 按钮尺寸,默认为Normal
        /// </summary>
        public ButtonSizeEnum? Size { get; set; }

        /// <summary>
        /// 按钮风格,默认为Normal
        /// </summary>
        public ButtonThemeEnum? Theme { get; set; }

        /// <summary>
        /// 按钮图标
        /// 图标字符串格式参考 http://www.layui.com/doc/element/icon.html
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// 是否圆角
        /// </summary>
        public bool IsRound { get; set; }

        /// <summary>
        /// 按钮文字
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 点击事件调用的js方法，如doclick()
        /// </summary>
        public string Click { get; set; }

        public bool Disabled { get; set; }

        public string ConfirmTxt { get; set; }


        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            if (string.IsNullOrEmpty(Id))
            {
                Id = Guid.NewGuid().ToNoSplitString();
            }
            if (output.TagName != "a")
            {
                output.TagName = "button";
                output.TagMode = TagMode.StartTagAndEndTag;
                var btnclass = "layui-btn";
                if (Size != null && Size != ButtonSizeEnum.Normal)
                {
                    switch (Size)
                    {
                        case ButtonSizeEnum.Big:
                            btnclass += " layui-btn-lg";
                            break;
                        case ButtonSizeEnum.Small:
                            btnclass += " layui-btn-sm";
                            break;
                        case ButtonSizeEnum.Mini:
                            btnclass += " layui-btn-xs";
                            break;
                        default:
                            break;
                    }
                }
                if (Disabled == true)
                {
                    Theme = ButtonThemeEnum.Disabled;
                    output.Attributes.SetAttribute(new TagHelperAttribute("disabled"));
                }
                if (Theme != null)
                {
                    btnclass += " layui-btn-" + Theme.Value.ToString().ToLower();
                }
                output.Attributes.SetAttribute("class", btnclass);
                if (string.IsNullOrEmpty(Icon) == false)
                {
                    output.Content.SetHtmlContent($@"<i class=""{Icon}""></i> {Text ?? ""}");
                }
                else
                {
                    output.Content.SetHtmlContent(Text ?? string.Empty);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(Icon) == false)
                {
                    output.Attributes.SetAttribute("class", "shortcut");
                    output.Content.SetHtmlContent($"<div><i class=\"{Icon}\"></i></div>{Text ?? ""}");
                }
                else
                {
                    output.Content.SetHtmlContent(Text ?? string.Empty);
                }
            }
            string submitButtonUrl = "";
            if (this is SubmitButtonTagHelper sbt)
            {
                if (string.IsNullOrEmpty(sbt.SubmitUrl) == false && context.Items.ContainsKey("formid") == true)
                {
                    submitButtonUrl = $"$('#{context.Items["formid"]}').attr('action','{sbt.SubmitUrl}');";
                }
            }

            // Issue #784 (#470 residual): opt-in (WtmUIOptions.UseSelectIslandRender,
            // default OFF) delegated data-wtm-click dispatch instead of the
            // generated per-button `$('#{Id}').on('click',function(){...});`
            // wrapper <script> below. Early-return-style branching so the
            // flag-OFF path (the final `else` below) is byte-identical to
            // pre-#784 (the #754 gate).
            //
            // context.Items[SubclassDelegatedClickItemKey] presence signals a
            // SUBCLASS (currently only SubmitButtonTagHelper's own
            // island-safe branch, see SubmitButtonTagHelper.cs) has ALREADY
            // fully wired its own delegated dispatch attributes
            // (data-wtm-submit-formid/divid/checkfn) before calling
            // base.Process — this class only needs to layer the SHARED
            // ConfirmTxt/SubmitUrl data on top and skip the wrapper entirely;
            // it must NOT re-derive island-safety from Click itself (Click at
            // this point may hold framework-internal placeholder text, not a
            // developer-authored expression). Deliberately NOT derived from
            // output.Attributes (see SubclassDelegatedClickItemKey's own
            // comment — that collection is spoofable by literal source
            // markup); the flag is ALSO re-checked here, mirroring every
            // other #470/#784 gate, even though SubmitButtonTagHelper only
            // ever sets the Items key while the flag is already ON.
            bool subclassDelegatedClick = UIConfig.UseSelectIslandRender
                && context.Items.ContainsKey(SubclassDelegatedClickItemKey);

            if (subclassDelegatedClick)
            {
                if (this is SubmitButtonTagHelper sbt2 && string.IsNullOrEmpty(sbt2.SubmitUrl) == false && context.Items.ContainsKey("formid") == true)
                {
                    output.Attributes.SetAttribute("data-wtm-submit-url", WebUtility.HtmlEncode(sbt2.SubmitUrl));
                }
                if (string.IsNullOrEmpty(ConfirmTxt) == false)
                {
                    output.Attributes.SetAttribute("data-wtm-confirm", WebUtility.HtmlEncode(ConfirmTxt));
                    output.Attributes.SetAttribute("data-wtm-confirm-title", WebUtility.HtmlEncode(THProgram._localizer["Sys.Info"]));
                }
            }
            else if (UIConfig.UseSelectIslandRender && (this is SubmitButtonTagHelper) == false)
            {
                // Issue #784: generic (non-SubmitButton) delegated click path
                // — a bare no-arg developer function call (same
                // _bareCallRegex convention #470 Slice M established for
                // SubmitButtonTagHelper's Click) is safe to reproduce via
                // ff._buttonAction.button, resolved through the SAME guarded
                // ff._resolveGuardedWindowFn every other #470 slice's named
                // callback uses. An empty/no-op Click needs no wiring at all.
                // Anything else (compound expressions, calls with arguments,
                // dotted framework calls like ff.OpenDialog(...) —
                // LinkButtonTagHelper/CloseButtonTagHelper always set Click
                // this way) is NOT representable without eval and keeps the
                // exact legacy inline <script> below, loudly deprecated via
                // console.warn — mirroring the non-identifier fallback every
                // other #470 slice already established.
                var trimmedClick = (Click ?? "").Trim();
                bool hasClickText = trimmedClick.Length > 0;
                bool bareCallMatch = hasClickText && _clickBareCallRegex.IsMatch(trimmedClick);
                bool islandSafe = Disabled || !hasClickText || bareCallMatch;

                if (islandSafe)
                {
                    // Mirrors the legacy gate exactly: ConfirmTxt is only ever
                    // wired when Click is ALSO present and the button is not
                    // Disabled (see the `else` legacy branch below) — an
                    // empty Click ignores ConfirmTxt entirely in legacy too.
                    if (!Disabled && hasClickText && bareCallMatch)
                    {
                        output.Attributes.SetAttribute("data-wtm-click", "button");
                        var fnName = trimmedClick[..^2];
                        output.Attributes.SetAttribute("data-wtm-clickfn", WebUtility.HtmlEncode(fnName));
                        if (!string.IsNullOrEmpty(ConfirmTxt))
                        {
                            output.Attributes.SetAttribute("data-wtm-confirm", WebUtility.HtmlEncode(ConfirmTxt));
                            output.Attributes.SetAttribute("data-wtm-confirm-title", WebUtility.HtmlEncode(THProgram._localizer["Sys.Info"]));
                        }
                    }
                    // else: nothing to wire (no Click, or Disabled) — a no-op
                    // click handler in legacy; contributes zero behavior
                    // either way, so nothing is emitted at all here.
                }
                else
                {
                    var warn = $"console.warn('[WTM] button #{JavaScriptEncoder.Default.Encode(Id ?? "")}: Click \\'{JavaScriptEncoder.Default.Encode(Click)}\\' is not a bare no-arg function call — UseSelectIslandRender is ON but island render was skipped for this button; keeping the legacy inline script. See #470/#784.');\n";
                    string onclickFallback = null;
                    if (!string.IsNullOrEmpty(ConfirmTxt))
                    {
                        Click = $"layer.confirm('{ConfirmTxt}', {{icon: 3, title:'{THProgram._localizer["Sys.Info"]}'}}, function(index){{ {Click};layer.close(index); }})";
                    }
                    onclickFallback = Click + ";return false;";

                    output.PostElement.AppendHtml($@"
<script>
{warn}  $('#{Id}').on('click',function(){{
    {submitButtonUrl}
    {onclickFallback}
}});
</script>
");
                }
            }
            else
            {
                // Flag OFF, OR SubmitButtonTagHelper's own non-delegated
                // fallback (a compound Click expression that SubmitButtonTagHelper
                // itself already decided isn't island-safe and already warned
                // about — see SubmitButtonTagHelper.cs) — EXACT legacy
                // wrapper, byte-identical to pre-#784.
                string onclick = null;
                if (string.IsNullOrEmpty(Click) == false && Disabled == false)
                {
                    if (!string.IsNullOrEmpty(ConfirmTxt))
                    {
                        Click = $"layer.confirm('{ConfirmTxt}', {{icon: 3, title:'{THProgram._localizer["Sys.Info"]}'}}, function(index){{ {Click};layer.close(index); }})";
                    }
                    onclick = Click + ";return false;";
                }

                output.PostElement.AppendHtml($@"
<script>
  $('#{Id}').on('click',function(){{
    {submitButtonUrl}
    {onclick}
}});
</script>
");
            }

            base.Process(context, output);
        }

    }
}
