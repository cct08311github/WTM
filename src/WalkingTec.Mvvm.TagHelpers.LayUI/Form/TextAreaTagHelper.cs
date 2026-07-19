using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:textarea", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.NormalOrSelfClosing)]
    public class TextAreaTagHelper : BaseFieldTag
    {
        public string EmptyText { get; set; }

        /// <summary>
        /// When true, renders a "N/Max" character counter span below the textarea.
        /// Requires [StringLength] or [MaxLength] on the field. Opt-in; default false.
        /// </summary>
        public bool ShowCounter { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            string placeHolder = EmptyText ?? "";
            output.TagName = "textarea";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("placeholder", placeHolder);
            output.Attributes.Add("class", "layui-textarea");
            if (string.IsNullOrEmpty(Field?.Model?.ToString()) == false)
            {
                DefaultValue = null;
            }
            if (DefaultValue != null)
            {
                output.Content.SetContent(DefaultValue.ToString());
            }
            else
            {
                output.Content.SetContent(Field?.Model?.ToString());
            }

            var maxLenVal = GetMaxLength();
            if (maxLenVal.HasValue)
            {
                output.Attributes.Add("maxlength", maxLenVal.Value.ToString());
            }
            if (ShowCounter && maxLenVal.HasValue)
            {
                // Issue #753: Slice G shipped BEFORE WtmUIOptions.UseSelectIslandRender
                // existed and switched to the data-attribute + delegated-listener path
                // UNCONDITIONALLY (no legacy fallback branch at all), breaking the
                // flag-OFF byte-identical guarantee #470 Slice J/K/L/M established.
                // Gate on the SAME flag — flag-off restores the exact pre-Slice-G
                // per-widget inline <script> (no data-wtm-counter attribute).
                if (UIConfig.UseSelectIslandRender)
                {
                    // Issue #470 Slice G: data-attribute + document-level delegated
                    // 'input' handler (framework_layui.js) instead of a per-widget
                    // inline <script> calling wtmCounter.init(id, counterId, maxLen).
                    // No island needed for a one-liner — the delegated listener is
                    // registered exactly ONCE at script load and matches on
                    // data-wtm-counter regardless of when/how this textarea enters the
                    // DOM (full page, OpenDialog/OpenDialog2 fragment, SPA-tab
                    // framework, …), so it keeps working even when
                    // DisableLegacyScriptRehydration (#627) blocks a legacy inline
                    // <script>. maxLen is read from the textarea's own native
                    // `maxLength` DOM property at listener time — already reflecting
                    // the `maxlength` attribute set above — rather than threading a
                    // second, redundant value through the data attribute.
                    var rawCounterId = Id + "_counter";
                    var counterId = HtmlEncoder.Default.Encode(rawCounterId);
                    var currentLen = Field?.Model?.ToString()?.Length ?? 0;
                    output.Attributes.Add("data-wtm-counter", rawCounterId);
                    output.PostElement.AppendHtml(
                        $"<span id=\"{counterId}\" class=\"wtm-char-counter\" style=\"font-size:12px;color:#999;\">" +
                        $"{currentLen}/{maxLenVal.Value}</span>");
                }
                else
                {
                    // Issue #753: pre-Slice-G legacy span + inline <script> — byte-
                    // identical to base 947ecbc9 (the commit immediately before #470
                    // Slice G shipped).
                    var counterId = HtmlEncoder.Default.Encode(Id + "_counter");
                    var currentLen = Field?.Model?.ToString()?.Length ?? 0;
                    output.PostElement.AppendHtml(
                        $"<span id=\"{counterId}\" class=\"wtm-char-counter\" style=\"font-size:12px;color:#999;\">" +
                        $"{currentLen}/{maxLenVal.Value}</span>");
                    output.PostElement.AppendHtml(
                        $"<script>if(typeof wtmCounter!=='undefined'){{" +
                        $"wtmCounter.init('{JavaScriptEncoder.Default.Encode(Id)}'," +
                        $"'{counterId}',{maxLenVal.Value});}}</script>");
                }
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
}
