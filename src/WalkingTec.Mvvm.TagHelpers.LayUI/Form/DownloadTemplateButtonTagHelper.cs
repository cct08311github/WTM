using System;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WalkingTec.Mvvm.Core;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:downloadTemplateButton", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class DownloadTemplateButtonTagHelper : BaseButtonTag
    {
        protected const string REQUIRED_ATTR_NAME = "vm";
        public ModelExpression Vm { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var vmQualifiedName = Vm.Model.GetType().AssemblyQualifiedName;
            vmQualifiedName = vmQualifiedName.Substring(0, vmQualifiedName.LastIndexOf(", Version="));

            output.TagName = "a";
            output.TagMode = TagMode.StartTagAndEndTag;

            var href = $"/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME={vmQualifiedName}";
            var vmReference = (Vm.Model as BaseVM)?.Wtm?.CreateGenericEndpointReference(Vm.Model.GetType(), GenericEndpointPurpose.Template);
            if (vmReference != null)
            {
                href += $"&{GenericEndpointGuard.ReferenceParameter}={Uri.EscapeDataString(vmReference)}";
            }
            output.Attributes.SetAttribute("href", href);
            Text = THProgram._localizer["Sys.DownloadTemplate"];
            base.Process(context, output);
        }

    }
}
