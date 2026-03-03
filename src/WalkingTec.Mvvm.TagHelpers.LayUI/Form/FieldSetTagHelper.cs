using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public enum FieldSetStyleEnum { Default, Simple }

    [HtmlTargetElement("wt:fieldset", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class FieldSetTagHelper : BaseElementTag
    {
        public string Title { get; set; }
        public FieldSetStyleEnum FieldSetStyle { get; set; }

        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "fieldset";
            if (FieldSetStyle == FieldSetStyleEnum.Default)
            {
                output.Attributes.SetAttribute("class", "layui-elem-field");
            }
            else
            {
                output.Attributes.SetAttribute("class", "layui-elem-field layui-field-title");
            }
            string content = $@"<legend>{Title}</legend>";
            var innerContent = (await output.GetChildContentAsync()).GetContent();
            if (string.IsNullOrEmpty(innerContent?.Trim()) == false)
            {
                content += $@"<div class=""layui-field-box"">{innerContent}</div>";
            }
            output.Content.SetHtmlContent(content);
            base.Process(context, output);
        }
    }
}
