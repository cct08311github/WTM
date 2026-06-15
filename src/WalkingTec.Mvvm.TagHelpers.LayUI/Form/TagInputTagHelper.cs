#nullable enable
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Renders a Layui 2.8+ tagInput widget bound to a delimited string field.
    /// The underlying value is stored as a comma-separated hidden input.
    /// Opt-in: use &lt;wt:taginput field="..." /&gt; in your Razor views.
    /// </summary>
    [HtmlTargetElement("wt:taginput", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class TagInputTagHelper : BaseFieldTag
    {
        /// <summary>
        /// Delimiter used to join/split tag values (default ",").
        /// </summary>
        public string Delimiter { get; set; } = ",";

        /// <summary>
        /// Placeholder text shown when no tags are entered.
        /// </summary>
        public string? EmptyText { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            var id = Id;
            output.Attributes.SetAttribute("id", id);
            output.Attributes.Add("class", "wtm-taginput-placeholder");

            var currentVal = Field?.Model?.ToString() ?? "";
            var safeId = HtmlEncoder.Default.Encode(id);
            var safeName = HtmlEncoder.Default.Encode(string.IsNullOrEmpty(Name) ? (Field?.Name ?? "") : Name);
            var safeJsId = JavaScriptEncoder.Default.Encode(id);
            var safeDelimiter = JavaScriptEncoder.Default.Encode(Delimiter);
            var safeJsPlaceholder = JavaScriptEncoder.Default.Encode(EmptyText ?? "");

            var initialTagsJson = BuildTagsJson(currentVal, Delimiter);

            output.PostElement.AppendHtml(
                $"<input type=\"hidden\" id=\"{safeId}_val\" name=\"{safeName}\" value=\"{HtmlEncoder.Default.Encode(currentVal)}\" />");

            output.PostElement.AppendHtml($@"
<script>
layui.use(['tagInput'], function(){{
  var tagInput = layui.tagInput;
  tagInput.render({{
    elem: '#{safeJsId}'
    {(string.IsNullOrEmpty(EmptyText) ? "" : $",placeholder: '{safeJsPlaceholder}'")}
    ,tagInitData: {initialTagsJson}
    ,change: function(tagData){{
      document.getElementById('{safeJsId}_val').value = tagData.map(function(t){{return t.value;}}).join('{safeDelimiter}');
    }}
  }});
}});
</script>");

            base.Process(context, output);
        }

        private static string BuildTagsJson(string value, string delimiter)
        {
            if (string.IsNullOrEmpty(value)) return "[]";
            var parts = value.Split(new[] { delimiter }, System.StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder("[");
            foreach (var part in parts)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"');
                sb.Append(JavaScriptEncoder.Default.Encode(part.Trim()));
                sb.Append('"');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
