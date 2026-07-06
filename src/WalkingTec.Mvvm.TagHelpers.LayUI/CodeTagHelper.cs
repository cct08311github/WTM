using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{

    [HtmlTargetElement("wt:code", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class CodeTagHelper : BaseElementTag
    {
        public new int? Height { get; set; }

        public string Title { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "pre";
            output.Attributes.SetAttribute("class", "layui-code");
            // Issue #601: the vendored layui.code module reads `lay-encode`
            // (prefixed), never the bare `encode` this previously emitted — see
            // demo/.../layui/lay/modules/code.js's
            // `c.attr("lay-encode")||e.encode` (2.6.3 tree) and the 2.13.8
            // layui-next bundle's `attr("lay-"+"encode")` option loop. The bare
            // `encode` attribute was therefore ALREADY silently inert on both
            // vendored trees, even on a full (non-dialog) page render — this
            // rename restores the intended escape-code-sample behavior. It also
            // now survives ff.SafeHtml in dialog partials via the matching
            // ADD_ATTR entry added alongside CodeTagHelper's other lay-*
            // siblings (lay-height/lay-title/lay-skin) in framework_layui.js.
            output.Attributes.SetAttribute("lay-encode", true);
            if (Height.HasValue)
            {
                output.Attributes.SetAttribute("lay-height", $"{Height}px");
            }
            if (string.IsNullOrEmpty(Title) == false)
            {
                output.Attributes.SetAttribute("lay-title", Title);
            }
            base.Process(context, output);
        }
    }
}
