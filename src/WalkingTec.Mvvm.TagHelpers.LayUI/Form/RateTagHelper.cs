#nullable enable
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Renders a Layui star-rating widget (layui.rate) bound to a numeric field.
    /// Opt-in: use &lt;wt:rate field="..." /&gt; in your Razor views.
    /// </summary>
    [HtmlTargetElement("wt:rate", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class RateTagHelper : BaseFieldTag
    {
        /// <summary>Number of stars (default 5).</summary>
        public int Length { get; set; } = 5;

        /// <summary>Whether to allow half-star selection (default false).</summary>
        public bool Half { get; set; }

        /// <summary>Whether the rating is read-only (default false).</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Optional text displayed beside the stars.</summary>
        public string? Text { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            var id = Id;
            output.Attributes.SetAttribute("id", id);
            output.Attributes.Add("class", "wtm-rate-placeholder");

            var currentVal = 0;
            if (Field?.Model != null && int.TryParse(Field.Model.ToString(), out var parsed))
            {
                currentVal = parsed;
            }

            var safeId = HtmlEncoder.Default.Encode(id);
            var safeName = HtmlEncoder.Default.Encode(string.IsNullOrEmpty(Name) ? (Field?.Name ?? "") : Name);
            var safeJsId = JavaScriptEncoder.Default.Encode(id);
            var textAttr = string.IsNullOrEmpty(Text) ? "" : $",text:['{JavaScriptEncoder.Default.Encode(Text)}']";
            var readOnlyAttr = ReadOnly ? ",readonly:true" : "";
            var halfAttr = Half ? ",half:true" : "";

            output.PostElement.AppendHtml(
                $"<input type=\"hidden\" id=\"{safeId}_val\" name=\"{safeName}\" value=\"{HtmlEncoder.Default.Encode(currentVal.ToString())}\" />");
            output.PostElement.AppendHtml($@"
<script>
layui.use(['rate'], function(){{
  var rate = layui.rate;
  rate.render({{
    elem: '#{safeJsId}'
    ,value: {currentVal}
    ,length: {Length}
    {halfAttr}
    {readOnlyAttr}
    {textAttr}
    ,choose: function(val){{
      document.getElementById('{safeJsId}_val').value = val;
    }}
  }});
}});
</script>");

            base.Process(context, output);
        }
    }
}
