#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #470: opt-in, eval-free JSON action island for dialog form initialization.
    /// Emits a &lt;script type="application/json" class="wtm-dialog-init"&gt; element that
    /// ff.OpenDialog reads after the dialog DOM is inserted and dispatches via ff.DispatchAction.
    /// No eval(), no inline scripts — all initialization goes through the whitelisted action types.
    ///
    /// Usage (inside a form partial):
    ///   &lt;wt:dialog-init form-filter="myFormFilter" /&gt;
    ///
    /// With date fields:
    ///   &lt;wt:dialog-init form-filter="myFormFilter"
    ///                  dates='[{"elem":"#BirthDate","type":"date","format":"yyyy-MM-dd"}]' /&gt;
    /// </summary>
    [HtmlTargetElement("wt:dialog-init", TagStructure = TagStructure.WithoutEndTag)]
    public class DialogInitTagHelper : TagHelper
    {
        // System.Text.Json's default encoder (JavaScriptEncoder.Default) escapes
        // '<', '>', and '&' as <, >, &, making the JSON payload
        // safe to embed inside a <script> block without risk of </script> injection.
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Required. The layui filter name for <c>form.render()</c>.</summary>
        public string FormFilter { get; set; } = string.Empty;

        /// <summary>
        /// Optional form type passed to layui.form.render() as the first argument
        /// (e.g. "select", "checkbox", "radio"). Null / empty renders all types.
        /// </summary>
        public string? FormType { get; set; }

        /// <summary>
        /// Optional date-picker configurations. Each entry is passed to layui.laydate.render().
        /// Serialized as the 'dates' array in the action payload.
        /// </summary>
        public List<DialogDateConfig>? Dates { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            // Suppress the <wt:dialog-init> element itself; emit a raw <script> instead.
            output.TagName = null;

            var action = new DialogInitAction
            {
                Type = "initForm",
                Filter = string.IsNullOrEmpty(FormFilter) ? null : FormFilter,
                FormType = string.IsNullOrEmpty(FormType) ? null : FormType,
                Dates = (Dates != null && Dates.Count > 0) ? Dates : null
            };

            var payload = new DialogInitPayload
            {
                Actions = new List<DialogInitAction> { action }
            };

            var json = LayuiIslandJson.Serialize(payload, _jsonOptions);

            // The emitted island is picked up by ff.OpenDialog's DOMParser extraction
            // (Issue #470) and dispatched via ff.DispatchAction. The type="application/json"
            // attribute ensures the existing _initScripts extraction loop skips this element
            // (it filters for JS types only), so there is no double-execution.
            output.Content.SetHtmlContent(
                $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");
        }
    }

    /// <summary>Date-picker configuration for a single field.</summary>
    public class DialogDateConfig
    {
        /// <summary>Required. The CSS selector or DOM element ID for the date input (e.g. <c>#BirthDate</c>).</summary>
        [System.Text.Json.Serialization.JsonPropertyName("elem")]
        public string Elem { get; set; } = string.Empty;

        /// <summary>Optional. The laydate type (e.g. <c>"date"</c>, <c>"datetime"</c>, <c>"time"</c>, <c>"year"</c>, <c>"month"</c>). Defaults to <c>"date"</c> when omitted.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>Optional. The display format string (e.g. <c>"yyyy-MM-dd"</c>). Uses laydate's default format when omitted.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("format")]
        public string? Format { get; set; }
    }

    // Internal DTO types — not part of the public API surface.

    internal class DialogInitPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("actions")]
        public List<DialogInitAction> Actions { get; set; } = new();
    }

    internal class DialogInitAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("filter")]
        public string? Filter { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("formType")]
        public string? FormType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("dates")]
        public List<DialogDateConfig>? Dates { get; set; }
    }
}
