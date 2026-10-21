using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// The pages that show a view model write the reference the generic data endpoints ask for: next to _DONOT_USE_VMNAME in the grid
    /// filter, in the selector filter and in the template link. The reference admits exactly the request the page makes.
    /// </summary>
    [TestClass]
    public class GenericEndpointReferenceTagHelperTests : LayUITagHelperTestBase
    {
        private static readonly string ListVmName = GenericEndpointGuard.GetViewModelName(typeof(LayuiSchoolListVM));
        private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

        private LayuiSchoolListVM NewListVm(bool protectedContext = true)
        {
            return new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: protectedContext ? _provider : null) };
        }

        private static string ReferenceIn(string text)
        {
            var m = Regex.Match(text, GenericEndpointGuard.ReferenceParameter + @"[""']?\s*[:=]\s*[""']?([A-Za-z0-9_\-%]+)");
            Assert.IsTrue(m.Success, "No reference found in: " + text);
            return Uri.UnescapeDataString(m.Groups[1].Value);
        }

        [TestMethod]
        public void Grid_WritesAReferenceThatAdmitsItsPagingAndExportRequests()
        {
            var listVm = NewListVm();

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/_Framework/GetPagingData" }, "wt:grid");

            var reference = ReferenceIn(html);
            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.Export, true, listVm.CurrentCS).Allowed);
            Assert.IsFalse(listVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.Update, false, field: "SchoolName").Allowed);
        }

        [TestMethod]
        public void Grid_ReferenceBelongsToTheUserWhoSawThePage()
        {
            var listVm = NewListVm();
            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/_Framework/GetPagingData" }, "wt:grid");
            var reference = ReferenceIn(html);
            var bob = MockWtmContext.CreateWtmContext(usercode: "bob", dataProtection: _provider);

            Assert.IsFalse(bob.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void Grid_WithoutDataProtection_WritesNoReference_AndStillRenders()
        {
            var listVm = NewListVm(protectedContext: false);

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html, "_DONOT_USE_VMNAME");
            AssertNotContains(html, GenericEndpointGuard.ReferenceParameter);
        }

        [TestMethod]
        public void Selector_WritesAReferenceForItsListViewModel()
        {
            var form = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: _provider) };
            var schoolVm = NewListVm();
            var helper = new SelectorTagHelper
            {
                Field = Field(form, x => x.SchoolId),
                ListVM = Self(schoolVm, "Selector"),
                TextBind = Field(new School(), x => x.SchoolName),
            };

            var html = Render(helper, "wt:selector", FormItems(form, "wtForm_x"));

            var reference = ReferenceIn(html);
            Assert.IsTrue(schoolVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.Selector, true, schoolVm.CurrentCS).Allowed);
            Assert.IsFalse(schoolVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.List, true, schoolVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void TemplateButton_LinkCarriesAReferenceForTheTemplate()
        {
            var vm = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: _provider) };

            var html = Render(new DownloadTemplateButtonTagHelper { Id = "dt1", Vm = Self(vm, "Vm") }, "wt:downloadTemplateButton");

            AssertContains(html, "/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME=", "LayuiTestVM", "&amp;" + GenericEndpointGuard.ReferenceParameter + "=");
            var name = GenericEndpointGuard.GetViewModelName(typeof(LayuiTestVM));
            var reference = ReferenceIn(html);
            Assert.IsTrue(vm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Template).Allowed);
        }
    }
}
